namespace RSoc.Protocol;

// DTOs del plano de control (cliente <-> RSocServer), serializados como JSON.

/// <summary>Login del API de gestión (ver lista de dispositivos, crear sesiones).</summary>
public record LoginRequest(string User, string Password);

public record LoginResponse(string ApiToken);

/// <summary>
/// Alta/refresco de un dispositivo. El dispositivo se anuncia como disponible y fija su
/// contraseña de conexión permanente (para control desatendido). Se envía un hash, nunca
/// la contraseña en claro.
/// </summary>
public record RegisterRequest(
    string DeviceId,
    string Alias,
    string ConnectionPasswordHash,
    string PublicKey);

/// <summary>Entrada de la lista de dispositivos (solo online).</summary>
public record DeviceInfo(
    string DeviceId,
    string Alias,
    bool Online,
    DateTimeOffset LastSeen);

/// <summary>
/// Petición de un controlador para abrir sesión contra un dispositivo destino. Debe aportar
/// la contraseña de conexión del destino (control desatendido).
/// </summary>
public record CreateSessionRequest(
    string FromDeviceId,
    string ToDeviceId,
    string ConnectionPassword);

/// <summary>
/// Billete de sesión que RSocServer entrega a cada extremo: a dónde conectar (RSocRelay) y
/// con qué token emparejarse. El mismo token se entrega al controlador (respuesta directa)
/// y al agente (sondeo de sesiones pendientes).
/// </summary>
public record SessionTicket(
    string SessionId,
    string RelayHost,
    int RelayPort,
    string RelayTokenHex,
    string Role,
    string Peer = ""); // id del otro extremo (para el agente, quién le controla)

/// <summary>
/// Respuesta del servidor a la consulta de actualización (GET /api/update/check). El cliente
/// envía su plataforma y versión actual; el servidor responde con la última versión que hospeda
/// y, para no saturarse, si puede descargar AHORA (rollout escalonado por ranura de descarga).
/// </summary>
public record UpdateManifest(
    string Platform,        // "windows" | "android"
    string LatestVersion,   // versión del artefacto hospedado
    bool UpdateAvailable,   // LatestVersion es más nueva que la del cliente
    bool DownloadAllowed,   // hay ranura libre: el cliente puede descargar ya
    long Size,              // tamaño del artefacto en bytes
    string Sha256,          // hash para verificar la descarga
    string FileName,        // nombre sugerido del fichero (p.ej. RSocClient.zip / RSoc.apk)
    int RetryAfterSeconds); // si no se permite, reintentar pasados ~N s (con jitter en el cliente)
