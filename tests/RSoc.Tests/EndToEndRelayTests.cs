// =====================================================================================
//  RSoc — Suite de tests: EndToEndRelayTests
// -------------------------------------------------------------------------------------
//  QUÉ SE PRUEBA
//    Integración de extremo a extremo de la Fase 1 (servidores), sin intervención manual:
//      · RSocServer (ASP.NET, en proceso vía WebApplicationFactory)
//      · RSocRelay  (proceso nativo C++ real, compilado con packaging/build-relay.ps1)
//
//  COBERTURA
//    1. Registro de dispositivo + keep-alive y aparición en la lista de online.
//    2. Login del API de gestión y autorización por bearer.
//    3. Señalización de sesión: validación de la contraseña de conexión y emisión de un
//       token de relay coherente para ambos extremos (controlador y agente).
//    4. Plano de datos: emparejado por token en RSocRelay y reenvío de bytes en ambos
//       sentidos (controlador -> agente y agente -> controlador).
//
//  REQUISITO PREVIO
//    RSocRelay.exe compilado en src/RSocRelay/bin (lo construye packaging/build-relay.ps1).
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RSoc.Protocol;
using RSocServer;
using Xunit;

namespace RSoc.Tests;

/// <summary>
/// Test de extremo a extremo de la Fase 1: arranca RSocServer (en proceso) y RSocRelay
/// (proceso nativo real), simula un agente y un controlador, y comprueba que:
///   1. el agente se registra y aparece en la lista de dispositivos online,
///   2. el controlador abre sesión validando la contraseña de conexión,
///   3. ambos extremos se emparejan en el relay con el token emitido,
///   4. los bytes fluyen en ambos sentidos a través del relay.
/// Todo sin intervención manual.
/// </summary>
public sealed class EndToEndRelayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ApiUser = "admin";
    private const string ApiPass = "admin";
    private const string AgentId = "AGENT-001";
    private const string ControllerId = "CTRL-001";
    private const string ConnPassword = "Remoto2024!";

    /// <summary>
    /// Prueba el camino feliz completo: un agente se registra y queda online, un controlador
    /// se autentica, abre sesión validando la contraseña de conexión, ambos extremos se
    /// emparejan en RSocRelay con el token emitido y se comprueba que los bytes fluyen en los
    /// dos sentidos a través del relay. Es la prueba viva de que señalización + relay encajan.
    /// </summary>
    [Fact]
    public async Task Session_is_brokered_and_bytes_flow_through_relay()
    {
        int relayPort = GetFreeTcpPort();
        using var relay = StartRelay(relayPort);
        await WaitForTcpAsync(relayPort, TimeSpan.FromSeconds(10));

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                // Fuente añadida después de la config del propio servidor (incl. rsoc-server-config.json),
                // de modo que estos valores de test ganan.
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Relay:Host"] = "127.0.0.1",
                    ["Relay:Port"] = relayPort.ToString(),
                    ["Api:User"] = ApiUser,
                    ["Api:Password"] = ApiPass,
                }));
            });

        using var http = factory.CreateClient();

        // --- 1. El agente se registra y manda heartbeat ---
        var register = new RegisterRequest(
            AgentId, "Equipo de pruebas",
            RegistryStore.HashPassword(ConnPassword), "pubkey-demo");
        var regResp = await http.PostAsJsonAsync("/api/devices/register", register, Json);
        Assert.Equal(HttpStatusCode.NoContent, regResp.StatusCode);

        var hb = await http.PostAsync($"/api/devices/{AgentId}/heartbeat", null);
        Assert.Equal(HttpStatusCode.NoContent, hb.StatusCode);

        // --- 2. Login del API y lista de dispositivos ---
        var loginResp = await http.PostAsJsonAsync("/api/login", new LoginRequest(ApiUser, ApiPass), Json);
        loginResp.EnsureSuccessStatusCode();
        var apiToken = (await loginResp.Content.ReadFromJsonAsync<LoginResponse>(Json))!.ApiToken;
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiToken);

        var devices = await http.GetFromJsonAsync<List<DeviceInfo>>("/api/devices", Json);
        Assert.Contains(devices!, d => d.DeviceId == AgentId && d.Online);

        // --- 3. El controlador abre sesión contra el agente ---
        var createResp = await http.PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(ControllerId, AgentId, ConnPassword), Json);
        createResp.EnsureSuccessStatusCode();
        var ctrlTicket = (await createResp.Content.ReadFromJsonAsync<SessionTicket>(Json))!;
        Assert.Equal(RelayProtocol.Role.Controller.ToString(), ctrlTicket.Role);

        // El agente recoge su billete pendiente (mismo token de relay).
        var agentTicket = await http.GetFromJsonAsync<SessionTicket>($"/api/sessions/pending/{AgentId}", Json);
        Assert.NotNull(agentTicket);
        Assert.Equal(RelayProtocol.Role.Agent.ToString(), agentTicket!.Role);
        Assert.Equal(ctrlTicket.RelayTokenHex, agentTicket.RelayTokenHex);
        Assert.Equal(ctrlTicket.SessionId, agentTicket.SessionId);

        // --- 4. Ambos extremos se emparejan en el relay y se intercambian bytes ---
        byte[] token = Convert.FromHexString(ctrlTicket.RelayTokenHex);

        using var agent = await ConnectAndHandshakeAsync(
            agentTicket.RelayHost, agentTicket.RelayPort, RelayProtocol.Role.Agent, token);
        using var controller = await ConnectAndHandshakeAsync(
            ctrlTicket.RelayHost, ctrlTicket.RelayPort, RelayProtocol.Role.Controller, token);

        var agentStream = agent.GetStream();
        var ctrlStream = controller.GetStream();

        // Controlador -> Agente
        byte[] toAgent = Encoding.UTF8.GetBytes("ping-desde-controlador");
        await ctrlStream.WriteAsync(toAgent);
        await ctrlStream.FlushAsync();
        Assert.Equal(toAgent, await ReadExactlyAsync(agentStream, toAgent.Length));

        // Agente -> Controlador
        byte[] toCtrl = Encoding.UTF8.GetBytes("pong-desde-agente");
        await agentStream.WriteAsync(toCtrl);
        await agentStream.FlushAsync();
        Assert.Equal(toCtrl, await ReadExactlyAsync(ctrlStream, toCtrl.Length));
    }

    // --- helpers ---

    private static async Task<TcpClient> ConnectAndHandshakeAsync(
        string host, int port, RelayProtocol.Role role, byte[] token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var hs = RelayProtocol.BuildHandshake(role, token);
        await client.GetStream().WriteAsync(hs);
        await client.GetStream().FlushAsync();
        return client;
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buf = new byte[count];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(buf, cts.Token);
        return buf;
    }

    private static Process StartRelay(int port)
    {
        // ruta a RSocRelay.exe compilado (packaging/build-relay.ps1)
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(baseDir);
        var exe = Path.Combine(repoRoot, "src", "RSocRelay", "bin", "RSocRelay.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"No existe {exe}. Compílalo antes con packaging/build-relay.ps1.", exe);

        var psi = new ProcessStartInfo(exe, port.ToString())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi)!;
        return p;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RSoc.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repo (RSoc.slnx).");
    }

    private static int GetFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitForTcpAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"RSocRelay no aceptó conexiones en el puerto {port} a tiempo.");
    }
}
