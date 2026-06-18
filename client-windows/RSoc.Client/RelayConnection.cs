using System.Net.Sockets;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Conexión del plano de datos contra RSocRelay. Abre el TCP, envía el handshake
/// (magic + versión + rol + token) y expone el <see cref="NetworkStream"/> ya emparejado,
/// listo para transportar bytes de sesión (cifrados extremo a extremo por la capa superior).
/// </summary>
public sealed class RelayConnection : IDisposable
{
    private readonly TcpClient _tcp;

    private RelayConnection(TcpClient tcp) => _tcp = tcp;

    public NetworkStream Stream => _tcp.GetStream();

    /// <summary>Conecta al relay según un <see cref="SessionTicket"/> y completa el handshake.</summary>
    public static async Task<RelayConnection> OpenAsync(SessionTicket ticket, CancellationToken ct = default)
    {
        var role = Enum.Parse<RelayProtocol.Role>(ticket.Role);
        var token = Convert.FromHexString(ticket.RelayTokenHex);
        return await OpenAsync(ticket.RelayHost, ticket.RelayPort, role, token, ct);
    }

    public static async Task<RelayConnection> OpenAsync(string host, int port,
        RelayProtocol.Role role, byte[] token, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);
        var handshake = RelayProtocol.BuildHandshake(role, token);
        await tcp.GetStream().WriteAsync(handshake, ct);
        await tcp.GetStream().FlushAsync(ct);
        return new RelayConnection(tcp);
    }

    public void Dispose() => _tcp.Dispose();
}
