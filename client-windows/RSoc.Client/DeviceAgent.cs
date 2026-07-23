using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Lado "agente" (equipo controlado, desatendido). Se registra en RSocServer, mantiene el
/// keep-alive y sondea sesiones pendientes; cuando llega un billete, abre la conexión de
/// relay y la entrega a <see cref="SessionAccepted"/> para que la capa de medios nativa
/// (captura + códec + input) arranque sobre ese stream.
/// </summary>
public sealed class DeviceAgent(
    RSocApiClient api,
    string deviceId,
    string alias,
    string connectionPassword,
    string publicKey)
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Grupo que este equipo declara al registrarse (el gestor puede reasignarlo).</summary>
    public string Group { get; init; } = "";

    /// <summary>Tipo de cliente que se anuncia (<c>Manager</c> o <c>Remote</c>).</summary>
    public string Kind { get; init; } = ClientKind.Remote;

    /// <summary>Si se indica, el agente se conecta al relay por esta dirección en vez de la del
    /// ticket (útil cuando el relay del servidor no es alcanzable localmente por NAT).</summary>
    public string? RelayHostOverride { get; init; }
    public int RelayPortOverride { get; init; }

    /// <summary>Se dispara cuando un controlador abre una sesión contra este equipo.</summary>
    public event Func<RelayConnection, SessionTicket, Task>? SessionAccepted;

    /// <summary>Se dispara cuando cambia la conectividad con el servidor (true = registrado).</summary>
    public event Action<bool>? ConnectivityChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        bool registered = false;
        var nextHeartbeat = DateTimeOffset.UtcNow;

        // Bucle self-healing: si el servidor cae o reinicia, reintenta y se re-registra solo.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!registered)
                {
                    await api.RegisterAsync(deviceId, alias, connectionPassword, publicKey, Group, Kind, ct);
                    registered = true;
                    nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;
                    ConnectivityChanged?.Invoke(true);
                    FileLog.Info($"Agente registrado en el servidor (kind={Kind}, grupo='{Group}')");
                }

                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    if (!await api.HeartbeatAsync(deviceId, ct))
                    {
                        registered = false; // el servidor ya no nos conoce -> re-registrar
                        FileLog.Warn("Heartbeat rechazado: el servidor no reconoce el dispositivo; se re-registrará");
                    }
                    nextHeartbeat = DateTimeOffset.UtcNow + HeartbeatInterval;
                }

                var ticket = await api.GetPendingSessionAsync(deviceId, ct);
                if (ticket is not null)
                {
                    FileLog.Info($"Sesión entrante {ticket.SessionId}: controlado por '{ticket.Peer}' (relay {ticket.RelayHost}:{ticket.RelayPort})");
                    if (!string.IsNullOrWhiteSpace(RelayHostOverride))
                        ticket = ticket with
                        {
                            RelayHost = RelayHostOverride!,
                            RelayPort = RelayPortOverride > 0 ? RelayPortOverride : ticket.RelayPort,
                        };
                    var relay = await RelayConnection.OpenAsync(ticket, ct);
                    var handler = SessionAccepted;
                    if (handler is not null)
                        _ = handler(relay, ticket); // sesión concurrente; el agente sigue disponible
                    else
                        relay.Dispose();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (registered) { registered = false; ConnectivityChanged?.Invoke(false); FileLog.Warn("Conectividad con el servidor perdida", ex); }
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
