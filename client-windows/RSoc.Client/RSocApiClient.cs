using System.Net.Http.Json;
using System.Text.Json;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Cliente del plano de control de RSoc (HTTP/JSON contra RSocServer). Lo usan tanto el
/// agente (registro + heartbeat + sondeo de sesiones) como el controlador (login + lista +
/// abrir sesión). Es agnóstico de plataforma: lo comparten el cliente Windows y, en Fase 2,
/// el de Android.
/// </summary>
public sealed class RSocApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? _apiToken;

    /// <summary>Alta/refresco del dispositivo (lado agente).</summary>
    public async Task RegisterAsync(string deviceId, string alias, string connectionPassword,
        string publicKey, CancellationToken ct = default)
    {
        var req = new RegisterRequest(deviceId, alias,
            HashPassword(connectionPassword), publicKey);
        var resp = await http.PostAsJsonAsync("/api/devices/register", req, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Keep-alive del dispositivo. Devuelve false si el servidor ya no lo conoce.</summary>
    public async Task<bool> HeartbeatAsync(string deviceId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/api/devices/{deviceId}/heartbeat", null, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Login del API de gestión; guarda el bearer para las llamadas autenticadas.</summary>
    public async Task LoginAsync(string user, string password, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/login", new LoginRequest(user, password), Json, ct);
        resp.EnsureSuccessStatusCode();
        _apiToken = (await resp.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!.ApiToken;
        http.DefaultRequestHeaders.Authorization = new("Bearer", _apiToken);
    }

    /// <summary>Lista de dispositivos online (requiere login previo).</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var list = await http.GetFromJsonAsync<List<DeviceInfo>>("/api/devices", Json, ct);
        return list ?? [];
    }

    /// <summary>Abre sesión contra un dispositivo destino (lado controlador). Requiere login.</summary>
    public async Task<SessionTicket> CreateSessionAsync(string fromDeviceId, string toDeviceId,
        string connectionPassword, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(fromDeviceId, toDeviceId, connectionPassword), Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionTicket>(Json, ct))!;
    }

    /// <summary>El agente sondea su billete de sesión pendiente. Null si no hay ninguno.</summary>
    public async Task<SessionTicket?> GetPendingSessionAsync(string deviceId, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/sessions/pending/{deviceId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionTicket>(Json, ct);
    }

    // --- Autoactualización ---

    /// <summary>Consulta si hay actualización para la plataforma/versión dadas. Null si está deshabilitada.</summary>
    public async Task<UpdateManifest?> CheckUpdateAsync(string platform, string version, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/api/update/check?platform={platform}&version={Uri.EscapeDataString(version)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<UpdateManifest>(Json, ct);
    }

    /// <summary>
    /// Descarga el artefacto de actualización a <paramref name="destPath"/>. Devuelve true si se
    /// descargó; false si el servidor pide reintentar más tarde (503, sin ranura de rollout).
    /// </summary>
    public async Task<bool> DownloadUpdateAsync(string platform, string destPath, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync($"/api/update/download?platform={platform}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) return false;
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
        return true;
    }

    private static string HashPassword(string password) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password)));
}
