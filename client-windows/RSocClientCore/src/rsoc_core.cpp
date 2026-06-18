// RSocClientCore — implementación del núcleo nativo del cliente Windows.
//   · Captura de pantalla con DXGI Desktop Duplication (Direct3D 11).
//   · Inyección de teclado/ratón con SendInput.
// Ver include/rsoc_core.h para el contrato C.

#define WIN32_LEAN_AND_MEAN
#include "rsoc_core.h" // RSOC_CORE_EXPORTS lo define la línea de comandos (/DRSOC_CORE_EXPORTS)

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <new>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")

namespace {

template <typename T>
void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

// Rectángulo (coordenadas de escritorio) del monitor que se está capturando ahora mismo.
// Lo fija rsoc_capture_create y lo usa rsoc_input_inject para mapear el ratón al monitor
// correcto sobre el escritorio virtual. Ancho 0 = no fijado todavía (se usa el primario).
RECT g_activeMonitor = {0, 0, 0, 0};

struct CaptureContext {
    ID3D11Device*             device   = nullptr;
    ID3D11DeviceContext*      context  = nullptr;
    IDXGIOutputDuplication*   dupl     = nullptr;
    ID3D11Texture2D*          staging  = nullptr; // textura de lectura por CPU
    int                       width    = 0;
    int                       height   = 0;
    bool                      mapped   = false;
};

// (Re)crea la textura de staging (USAGE_STAGING, lectura por CPU) si cambia el tamaño.
bool EnsureStaging(CaptureContext* c, const D3D11_TEXTURE2D_DESC& src) {
    if (c->staging && c->width == (int)src.Width && c->height == (int)src.Height)
        return true;

    SafeRelease(c->staging);
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = src.Width;
    d.Height = src.Height;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = src.Format;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_STAGING;
    d.BindFlags = 0;
    d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    d.MiscFlags = 0;

    if (FAILED(c->device->CreateTexture2D(&d, nullptr, &c->staging)))
        return false;
    c->width = (int)src.Width;
    c->height = (int)src.Height;
    return true;
}

} // namespace

// --- Captura ---

extern "C" RSOC_API int32_t rsoc_capture_monitor_count(void) {
    IDXGIFactory1* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&factory)))
        return 0;
    IDXGIAdapter1* adapter = nullptr;
    int32_t count = 0;
    if (SUCCEEDED(factory->EnumAdapters1(0, &adapter))) {
        IDXGIOutput* out = nullptr;
        for (UINT i = 0; adapter->EnumOutputs(i, &out) != DXGI_ERROR_NOT_FOUND; ++i) {
            SafeRelease(out);
            ++count;
        }
        SafeRelease(adapter);
    }
    SafeRelease(factory);
    return count;
}

extern "C" RSOC_API void* rsoc_capture_create(int32_t monitor_index) {
    auto* c = new (std::nothrow) CaptureContext();
    if (!c) return nullptr;

    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION,
        &c->device, &fl, &c->context);
    if (FAILED(hr)) { delete c; return nullptr; }

    IDXGIDevice* dxgiDevice = nullptr;
    IDXGIAdapter* adapter = nullptr;
    IDXGIOutput* output = nullptr;
    IDXGIOutput1* output1 = nullptr;

    if (monitor_index < 0) monitor_index = 0;

    bool ok = false;
    if (SUCCEEDED(c->device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice)) &&
        SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) &&
        SUCCEEDED(adapter->EnumOutputs((UINT)monitor_index, &output)) &&
        SUCCEEDED(output->QueryInterface(__uuidof(IDXGIOutput1), (void**)&output1)) &&
        SUCCEEDED(output1->DuplicateOutput(c->device, &c->dupl))) {
        // Guarda el rectángulo del monitor (coords de escritorio) para el mapeo del ratón.
        DXGI_OUTPUT_DESC od = {};
        if (SUCCEEDED(output->GetDesc(&od)))
            g_activeMonitor = od.DesktopCoordinates;
        ok = true;
    }

    SafeRelease(output1);
    SafeRelease(output);
    SafeRelease(adapter);
    SafeRelease(dxgiDevice);

    if (!ok) {
        SafeRelease(c->dupl);
        SafeRelease(c->context);
        SafeRelease(c->device);
        delete c;
        return nullptr;
    }
    return c;
}

extern "C" RSOC_API int32_t rsoc_capture_grab(void* handle, RSocFrame* out_frame, int32_t timeout_ms) {
    auto* c = static_cast<CaptureContext*>(handle);
    if (!c || !c->dupl || !out_frame) return -1;
    if (c->mapped) { c->context->Unmap(c->staging, 0); c->mapped = false; }

    DXGI_OUTDUPL_FRAME_INFO info = {};
    IDXGIResource* res = nullptr;
    HRESULT hr = c->dupl->AcquireNextFrame((UINT)timeout_ms, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) return 0;
    if (FAILED(hr)) { SafeRelease(res); return -1; }

    ID3D11Texture2D* tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex))) {
        SafeRelease(res);
        c->dupl->ReleaseFrame();
        return -1;
    }

    D3D11_TEXTURE2D_DESC desc = {};
    tex->GetDesc(&desc);

    int rc = -1;
    if (EnsureStaging(c, desc)) {
        c->context->CopyResource(c->staging, tex);
        SafeRelease(tex);
        c->dupl->ReleaseFrame(); // ya tenemos copia en staging

        D3D11_MAPPED_SUBRESOURCE map = {};
        if (SUCCEEDED(c->context->Map(c->staging, 0, D3D11_MAP_READ, 0, &map))) {
            c->mapped = true;
            LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
            out_frame->data = static_cast<const uint8_t*>(map.pData);
            out_frame->width = c->width;
            out_frame->height = c->height;
            out_frame->stride = (int32_t)map.RowPitch;
            out_frame->timestamp_qpc = (uint64_t)qpc.QuadPart;
            rc = 1;
        }
    } else {
        SafeRelease(tex);
        c->dupl->ReleaseFrame();
    }
    SafeRelease(res);
    return rc;
}

extern "C" RSOC_API void rsoc_capture_release_frame(void* handle) {
    auto* c = static_cast<CaptureContext*>(handle);
    if (c && c->mapped) { c->context->Unmap(c->staging, 0); c->mapped = false; }
}

extern "C" RSOC_API void rsoc_capture_destroy(void* handle) {
    auto* c = static_cast<CaptureContext*>(handle);
    if (!c) return;
    if (c->mapped && c->staging) c->context->Unmap(c->staging, 0);
    SafeRelease(c->staging);
    SafeRelease(c->dupl);
    SafeRelease(c->context);
    SafeRelease(c->device);
    delete c;
}

// --- Inyección de entrada ---

extern "C" RSOC_API void rsoc_input_inject(const RSocInputEvent* ev) {
    if (!ev) return;
    INPUT in = {};

    switch (ev->kind) {
    case RSOC_INPUT_MOUSE_MOVE: {
        // ev->x,y vienen normalizados 0..65535 RELATIVOS al monitor que se está capturando.
        // Se traducen a píxel dentro de ese monitor y de ahí a coordenadas normalizadas del
        // ESCRITORIO VIRTUAL (MOUSEEVENTF_VIRTUALDESK), para que el cursor caiga en el monitor
        // correcto aunque no sea el primario. Si aún no hay monitor fijado, cae al primario.
        in.type = INPUT_MOUSE;
        const RECT& m = g_activeMonitor;
        if (m.right > m.left && m.bottom > m.top) {
            int px = m.left + (int)((int64_t)ev->x * (m.right - m.left) / 65535);
            int py = m.top  + (int)((int64_t)ev->y * (m.bottom - m.top) / 65535);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            in.mi.dx = (LONG)((int64_t)(px - vx) * 65535 / (vw > 1 ? vw - 1 : 1));
            in.mi.dy = (LONG)((int64_t)(py - vy) * 65535 / (vh > 1 ? vh - 1 : 1));
            in.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        } else {
            in.mi.dx = ev->x;
            in.mi.dy = ev->y;
            in.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        }
        break;
    }

    case RSOC_INPUT_MOUSE_BUTTON: {
        in.type = INPUT_MOUSE;
        DWORD down = 0, up = 0;
        switch (ev->code) {
        case 0: down = MOUSEEVENTF_LEFTDOWN;   up = MOUSEEVENTF_LEFTUP;   break;
        case 1: down = MOUSEEVENTF_RIGHTDOWN;  up = MOUSEEVENTF_RIGHTUP;  break;
        case 2: down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
        default: return;
        }
        in.mi.dwFlags = ev->down ? down : up;
        break;
    }

    case RSOC_INPUT_WHEEL:
        in.type = INPUT_MOUSE;
        in.mi.dwFlags = MOUSEEVENTF_WHEEL;
        in.mi.mouseData = (DWORD)ev->wheel;
        break;

    case RSOC_INPUT_KEY:
        in.type = INPUT_KEYBOARD;
        in.ki.wVk = (WORD)ev->code;
        in.ki.dwFlags = ev->down ? 0 : KEYEVENTF_KEYUP;
        break;

    default:
        return;
    }

    SendInput(1, &in, sizeof(INPUT));
}
