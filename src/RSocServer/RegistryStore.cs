using System.Collections.Concurrent;
using System.Security.Cryptography;
using RSoc.Protocol;

namespace RSocServer;

/// <summary>
/// Estado en memoria de RSocServer: dispositivos registrados (con keep-alive), sesiones
/// señalizadas pendientes de recoger por el agente, y tokens del API de gestión.
///
/// Es deliberadamente simple (un solo proceso, sin BD) para la Fase 1. La persistencia real
/// (SQLite/SQL Server) se conecta detrás de esta misma interfaz más adelante.
/// </summary>
public sealed class RegistryStore(TimeProvider clock, IConfiguration config)
{
    private sealed record DeviceRecord(
        string DeviceId,
        string Alias,
        string ConnectionPasswordHash,
        string PublicKey,
        DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new();
    private readonly ConcurrentDictionary<string, SessionTicket> _pendingByDevice = new();
    private readonly ConcurrentDictionary<string, byte> _apiTokens = new();

    public TimeSpan PeerTimeout { get; } =
        TimeSpan.FromSeconds(config.GetValue("Server:PeerTimeoutSecs", 300));

    private string RelayHost => config["Relay:Host"] ?? "127.0.0.1";
    private int RelayPort => config.GetValue("Relay:Port", 21117);

    // --- API de gestión ---

    public string? Login(LoginRequest req)
    {
        var user = config["Api:User"];
        var pass = config["Api:Password"];
        // Sin credenciales configuradas no se permite ningún login (no hay usuario/clave por defecto).
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return null;
        if (req.User != user || req.Password != pass) return null;

        var token = RandomToken(24);
        _apiTokens[token] = 1;
        return token;
    }

    public bool IsApiTokenValid(string? token) =>
        !string.IsNullOrEmpty(token) && _apiTokens.ContainsKey(token);

    // --- Dispositivos ---

    public void Register(RegisterRequest req)
    {
        _devices[req.DeviceId] = new DeviceRecord(
            req.DeviceId, req.Alias, req.ConnectionPasswordHash, req.PublicKey, clock.GetUtcNow());
    }

    public bool Heartbeat(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var d)) return false;
        _devices[deviceId] = d with { LastSeen = clock.GetUtcNow() };
        return true;
    }

    public IEnumerable<DeviceInfo> ListOnline()
    {
        var cutoff = clock.GetUtcNow() - PeerTimeout;
        foreach (var d in _devices.Values)
        {
            if (d.LastSeen < cutoff)
            {
                _devices.TryRemove(d.DeviceId, out _);
                continue;
            }
            yield return new DeviceInfo(d.DeviceId, d.Alias, true, d.LastSeen);
        }
    }

    // --- Señalización de sesión ---

    /// <summary>
    /// Crea una sesión: valida que el destino existe y que la contraseña de conexión coincide,
    /// emite un token de relay aleatorio y deja al agente un billete pendiente. Devuelve el
    /// billete del controlador, o null si la validación falla.
    /// </summary>
    public SessionTicket? CreateSession(CreateSessionRequest req)
    {
        if (!_devices.TryGetValue(req.ToDeviceId, out var target)) return null;
        if (HashPassword(req.ConnectionPassword) != target.ConnectionPasswordHash) return null;

        var sessionId = RandomToken(12);
        var relayTokenHex = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RelayProtocol.TokenSize));

        _pendingByDevice[req.ToDeviceId] = new SessionTicket(
            sessionId, RelayHost, RelayPort, relayTokenHex, RelayProtocol.Role.Agent.ToString(),
            Peer: req.FromDeviceId); // el agente sabrá quién le controla

        return new SessionTicket(
            sessionId, RelayHost, RelayPort, relayTokenHex, RelayProtocol.Role.Controller.ToString(),
            Peer: req.ToDeviceId);
    }

    /// <summary>El agente sondea su billete pendiente (y lo consume).</summary>
    public SessionTicket? TakePendingSession(string deviceId) =>
        _pendingByDevice.TryRemove(deviceId, out var ticket) ? ticket : null;

    // --- Utilidades ---

    public static string HashPassword(string password) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password)));

    private static string RandomToken(int bytes) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
