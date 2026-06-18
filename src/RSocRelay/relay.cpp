// RSocRelay — reenviador de tráfico de sesión para RSoc.
//
// Servidor TCP puro y sin estado de negocio: empareja las dos conexiones que presentan el
// mismo token de 16 bytes (emitido por RSocServer) y reenvía los bytes en ambos sentidos.
// No interpreta el contenido de la sesión (que va cifrado extremo a extremo entre clientes).
//
// Handshake (debe coincidir con RSoc.Protocol/RelayProtocol.cs):
//   [0..3] "RSOC"  [4] version=1  [5] role(0/1)  [6..21] token(16)
//
// Uso:  RSocRelay.exe [puerto]      (por defecto 21117)
//
// Compilar (desde un entorno con vcvars64 cargado):
//   cl /EHsc /O2 /std:c++17 relay.cpp ws2_32.lib /Fe:RSocRelay.exe

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#pragma comment(lib, "ws2_32.lib")

namespace {

constexpr uint8_t kMagic[4]   = {'R', 'S', 'O', 'C'};
constexpr uint8_t kVersion    = 1;
constexpr int     kTokenSize  = 16;
constexpr int     kHandshake  = 4 + 1 + 1 + kTokenSize; // 22
constexpr int     kDefaultPort = 21117;

constexpr size_t  kMaxLogBytes = 10 * 1024 * 1024; // 10 MB por fichero
constexpr int     kMaxLogFiles = 10;               // máximo 10 ficheros por log

// --- Logging rotativo (10 MB x 10 ficheros) ---
//
// Un fichero base "<nombre>.log" y hasta 9 rotados "<nombre>.log.1".."<nombre>.log.9".
// Al superar el tamaño, se desplazan (.9 se borra) y se abre uno nuevo. Thread-safe.
class RollingLog {
public:
    RollingLog(std::string base, size_t maxBytes, int maxFiles)
        : base_(std::move(base)), maxBytes_(maxBytes), maxFiles_(maxFiles) {
        std::error_code ec;
        size_ = std::filesystem::exists(base_, ec) ? (size_t)std::filesystem::file_size(base_, ec) : 0;
        out_.open(base_, std::ios::app | std::ios::binary);
    }

    void line(const std::string& msg) {
        std::lock_guard<std::mutex> lk(mu_);
        if (!out_.is_open()) return;
        std::string s = stamp() + " " + msg + "\n";
        if (size_ + s.size() > maxBytes_ && size_ > 0) roll();
        out_.write(s.data(), (std::streamsize)s.size());
        out_.flush();
        size_ += s.size();
    }

private:
    void roll() {
        out_.close();
        std::error_code ec;
        std::filesystem::remove(base_ + "." + std::to_string(maxFiles_ - 1), ec);
        for (int i = maxFiles_ - 2; i >= 1; --i)
            std::filesystem::rename(base_ + "." + std::to_string(i),
                                    base_ + "." + std::to_string(i + 1), ec);
        std::filesystem::rename(base_, base_ + ".1", ec);
        out_.open(base_, std::ios::trunc | std::ios::binary);
        size_ = 0;
    }

    static std::string stamp() {
        SYSTEMTIME st; GetLocalTime(&st);
        char buf[32];
        snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d.%03d",
                 st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
        return buf;
    }

    std::string   base_;
    size_t        maxBytes_;
    int           maxFiles_;
    std::ofstream out_;
    size_t        size_ = 0;
    std::mutex    mu_;
};

std::filesystem::path g_logDir;            // carpeta logs\ junto al exe
std::unique_ptr<RollingLog> g_log;         // log general del relay
std::atomic<uint64_t> g_pairCounter{0};    // desempata ficheros por par

void logMain(const std::string& msg) { if (g_log) g_log->line(msg); }

// Dirección "ip:puerto" del extremo remoto de un socket (para el log).
std::string peerName(SOCKET s) {
    sockaddr_in addr{};
    int len = sizeof(addr);
    if (getpeername(s, reinterpret_cast<sockaddr*>(&addr), &len) != 0) return "?:?";
    char ip[INET_ADDRSTRLEN] = {0};
    inet_ntop(AF_INET, &addr.sin_addr, ip, sizeof(ip));
    return std::string(ip) + ":" + std::to_string(ntohs(addr.sin_port));
}

// token (hex) -> socket que espera pareja
std::map<std::string, SOCKET> g_waiting;
std::mutex                    g_mutex;

std::string toHex(const uint8_t* p, int n) {
    static const char* d = "0123456789abcdef";
    std::string s;
    s.reserve(n * 2);
    for (int i = 0; i < n; ++i) {
        s.push_back(d[p[i] >> 4]);
        s.push_back(d[p[i] & 0xF]);
    }
    return s;
}

// Lee exactamente n bytes o falla.
bool recvAll(SOCKET s, uint8_t* buf, int n) {
    int got = 0;
    while (got < n) {
        int r = recv(s, reinterpret_cast<char*>(buf) + got, n - got, 0);
        if (r <= 0) return false;
        got += r;
    }
    return true;
}

// Une dos sockets emparejados: un hilo por sentido. El primer sentido en terminar derriba
// ambos sockets (shutdown) para desbloquear el contrario; el cierre final es único.
// `plog` (puede ser null) registra el detalle del tráfico de ESTE par.
void bridge(SOCKET a, SOCKET b, RollingLog* plog) {
    auto guard = std::make_shared<std::atomic<int>>(0);
    auto half = [guard, plog](SOCKET src, SOCKET dst, const char* dir) {
        char buf[65536];
        uint64_t total = 0;
        for (;;) {
            int r = recv(src, buf, sizeof(buf), 0);
            if (r <= 0) break;
            int sent = 0;
            while (sent < r) {
                int w = send(dst, buf + sent, r - sent, 0);
                if (w <= 0) { r = -1; break; }
                sent += w;
            }
            if (r < 0) {
                if (plog) plog->line(std::string(dir) + " error de envio tras " + std::to_string(total) + " bytes");
                break;
            }
            total += (uint64_t)r;
            if (plog) plog->line(std::string(dir) + " " + std::to_string(r) +
                                 " bytes (acumulado " + std::to_string(total) + ")");
        }
        if (plog) plog->line(std::string(dir) + " sentido cerrado, total " + std::to_string(total) + " bytes");
        // El primero en terminar derriba ambos sockets.
        if (guard->fetch_add(1) == 0) {
            shutdown(src, SD_BOTH);
            shutdown(dst, SD_BOTH);
        }
    };
    std::thread t1(half, a, b, "A->B");
    std::thread t2(half, b, a, "B->A");
    t1.join();
    t2.join();
    closesocket(a);
    closesocket(b);
    if (plog) plog->line("par cerrado");
}

void handleConnection(SOCKET sock) {
    std::string who = peerName(sock);
    uint8_t hs[kHandshake];
    if (!recvAll(sock, hs, kHandshake)) {
        logMain("handshake incompleto desde " + who + ", descartado");
        closesocket(sock); return;
    }
    if (memcmp(hs, kMagic, 4) != 0 || hs[4] != kVersion) {
        logMain("handshake invalido desde " + who + ", descartado");
        closesocket(sock); return;
    }

    std::string token = toHex(hs + 6, kTokenSize);

    SOCKET peer = INVALID_SOCKET;
    {
        std::lock_guard<std::mutex> lk(g_mutex);
        auto it = g_waiting.find(token);
        if (it == g_waiting.end()) {
            // Primer extremo: queda aparcado esperando pareja.
            g_waiting[token] = sock;
            logMain("token " + token + ": primer extremo " + who + " esperando pareja");
            return; // este hilo termina; el socket vive en el mapa
        }
        peer = it->second;
        g_waiting.erase(it);
    }

    // Segundo extremo: emparejar y reenviar. Un fichero de log propio para este par.
    std::string peerWho = peerName(peer);
    uint64_t pairId = g_pairCounter.fetch_add(1);
    logMain("token " + token + ": emparejado " + peerWho + " (A) <-> " + who + " (B), par #" +
            std::to_string(pairId));

    std::unique_ptr<RollingLog> plog;
    try {
        auto path = (g_logDir / ("par_" + token + ".log")).string();
        plog = std::make_unique<RollingLog>(path, kMaxLogBytes, kMaxLogFiles);
        plog->line("=== par #" + std::to_string(pairId) + " token " + token +
                   " : A=" + peerWho + " B=" + who + " ===");
    } catch (...) { plog.reset(); }

    bridge(peer, sock, plog.get());
    logMain("token " + token + ": par #" + std::to_string(pairId) + " finalizado");
}

} // namespace

// Carpeta logs\ junto al ejecutable (no al CWD, para que funcione como servicio).
std::filesystem::path exeLogDir() {
    char path[MAX_PATH] = {0};
    DWORD n = GetModuleFileNameA(nullptr, path, MAX_PATH);
    std::filesystem::path exe = (n > 0) ? std::filesystem::path(path) : std::filesystem::path(".");
    return exe.parent_path() / "logs";
}

int main(int argc, char** argv) {
    int port = (argc > 1) ? atoi(argv[1]) : kDefaultPort;

    // Logging: carpeta logs\ junto al exe y log general rotativo.
    std::error_code ec;
    g_logDir = exeLogDir();
    std::filesystem::create_directories(g_logDir, ec);
    g_log = std::make_unique<RollingLog>((g_logDir / "RSocRelay.log").string(), kMaxLogBytes, kMaxLogFiles);
    logMain("=== RSocRelay arrancando, puerto " + std::to_string(port) + " ===");

    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "RSocRelay: WSAStartup fallo\n");
        logMain("WSAStartup fallo");
        return 1;
    }

    SOCKET listenSock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listenSock == INVALID_SOCKET) { fprintf(stderr, "RSocRelay: socket fallo\n"); return 1; }

    // SIN SO_REUSEADDR a propósito: en Windows permitiría que una segunda instancia se
    // enlazara al mismo puerto y robara parte de las conexiones (los dos extremos de una
    // sesión acabarían en relays distintos y no se emparejarían). Sin él, una segunda
    // instancia falla al bind y sale, evitando ese reparto.
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(static_cast<u_short>(port));

    if (bind(listenSock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        fprintf(stderr, "RSocRelay: bind fallo en puerto %d (%d)\n", port, WSAGetLastError());
        logMain("bind fallo en puerto " + std::to_string(port) + " (" + std::to_string(WSAGetLastError()) + ")");
        return 1;
    }
    if (listen(listenSock, SOMAXCONN) == SOCKET_ERROR) {
        fprintf(stderr, "RSocRelay: listen fallo\n");
        logMain("listen fallo");
        return 1;
    }

    fprintf(stderr, "RSocRelay escuchando en TCP %d\n", port);
    fflush(stderr);
    logMain("escuchando en TCP " + std::to_string(port));

    for (;;) {
        SOCKET client = accept(listenSock, nullptr, nullptr);
        if (client == INVALID_SOCKET) continue;
        std::thread(handleConnection, client).detach();
    }
}
