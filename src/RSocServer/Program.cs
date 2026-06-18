using RSoc.Protocol;
using RSocServer;

var builder = WebApplication.CreateBuilder(args);

// Configuración por JSON junto al ejecutable (IPs, puertos, usuario/clave del API).
// Se resuelve desde la carpeta del exe (no del directorio de trabajo) para que funcione
// igual lanzado como servicio o desde cualquier CWD.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "rsoc-server-config.json"), optional: true, reloadOnChange: true);

// La API se sirve SIEMPRE por HTTPS con certificado autofirmado (los clientes lo aceptan).
var apiPort = builder.Configuration.GetValue("Server:ApiPort", 21114);
var serverCert = SelfSignedCert.LoadOrCreate(
    Path.Combine(AppContext.BaseDirectory, "rsoc-server.pfx"), "RSoc Server");
builder.WebHost.ConfigureKestrel(k =>
    k.ListenAnyIP(apiPort, lo => lo.UseHttps(serverCert)));
var urls = $"https://0.0.0.0:{apiPort}";

// Logging detallado a fichero rotativo (10 MB x 10 ficheros) en logs\ junto al exe.
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
builder.Logging.AddProvider(new RollingFileLoggerProvider(
    Path.Combine(logDir, "RSocServer.log"), 10L * 1024 * 1024, 10, LogLevel.Information));
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RegistryStore>();
builder.Services.AddSingleton<UpdateService>();

var app = builder.Build();

var log = app.Logger;
log.LogInformation("RSocServer iniciando en {Urls}", urls);

// Traza de cada petición HTTP (método, ruta, estado, duración, IP de origen).
app.Use(async (ctx, next) =>
{
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    await next();
    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    log.LogInformation("HTTP {Method} {Path} -> {Status} ({Ms:F1} ms) desde {Ip}",
        ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, ms,
        ctx.Connection.RemoteIpAddress);
});

// --- API de gestión ---

app.MapPost("/api/login", (LoginRequest req, RegistryStore store) =>
{
    var token = store.Login(req);
    if (token is null)
    {
        log.LogWarning("Login RECHAZADO para usuario '{User}'", req.User);
        return Results.Unauthorized();
    }
    log.LogInformation("Login OK para usuario '{User}'", req.User);
    return Results.Ok(new LoginResponse(token));
});

// --- Dispositivos ---

app.MapPost("/api/devices/register", (RegisterRequest req, RegistryStore store) =>
{
    store.Register(req);
    log.LogInformation("Registro de dispositivo {DeviceId} (alias '{Alias}')", req.DeviceId, req.Alias);
    return Results.NoContent();
});

app.MapPost("/api/devices/{deviceId}/heartbeat", (string deviceId, RegistryStore store) =>
{
    var ok = store.Heartbeat(deviceId);
    log.LogDebug("Heartbeat de {DeviceId}: {Resultado}", deviceId, ok ? "OK" : "DESCONOCIDO");
    return ok ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/devices", (HttpRequest http, RegistryStore store) =>
    RequireApiToken(http, store, () =>
    {
        var list = store.ListOnline().ToList();
        log.LogInformation("Lista de dispositivos: {Count} online", list.Count);
        return Results.Ok(list);
    }));

// --- Señalización de sesión ---

app.MapPost("/api/sessions", (CreateSessionRequest req, HttpRequest http, RegistryStore store) =>
    RequireApiToken(http, store, () =>
    {
        var ticket = store.CreateSession(req);
        if (ticket is null)
        {
            log.LogWarning("Sesión RECHAZADA {From} -> {To} (destino inexistente o contraseña incorrecta)",
                req.FromDeviceId, req.ToDeviceId);
            return Results.Problem("Dispositivo destino inexistente o contraseña de conexión incorrecta.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        log.LogInformation("Sesión {SessionId} creada {From} -> {To} (relay {Host}:{Port})",
            ticket.SessionId, req.FromDeviceId, req.ToDeviceId, ticket.RelayHost, ticket.RelayPort);
        return Results.Ok(ticket);
    }));

app.MapGet("/api/sessions/pending/{deviceId}", (string deviceId, RegistryStore store) =>
{
    var ticket = store.TakePendingSession(deviceId);
    if (ticket is not null)
        log.LogInformation("Sesión pendiente entregada al agente {DeviceId} (sesión {SessionId}, controlado por {Peer})",
            deviceId, ticket.SessionId, ticket.Peer);
    return ticket is null ? Results.NoContent() : Results.Ok(ticket);
});

// --- Autoactualización del cliente ---

app.MapGet("/api/update/check", (string? platform, string? version, UpdateService upd) =>
{
    if (!upd.Enabled) return Results.NotFound();
    var m = upd.Check(platform ?? "windows", version ?? "0.0.0");
    if (m.UpdateAvailable)
        log.LogInformation("Update {Platform}: cliente v{Cur} -> v{Latest}, permitido={Allowed} (ranuras {Slots}/{Max})",
            m.Platform, version, m.LatestVersion, m.DownloadAllowed, upd.AvailableSlots, upd.MaxConcurrent);
    return Results.Ok(m);
});

app.MapGet("/api/update/download", async (HttpContext ctx, string? platform, UpdateService upd) =>
{
    if (!upd.Enabled) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    var file = upd.GetFile(platform ?? "windows");
    if (file is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

    // Rollout: si no hay ranura libre, pide reintentar más tarde (el cliente añade jitter).
    if (!upd.TryBeginDownload())
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = upd.RetryAfterSeconds.ToString();
        log.LogInformation("Update download {Platform}: sin ranuras, 503 (reintentar {Sec}s)", platform, upd.RetryAfterSeconds);
        return;
    }
    try
    {
        log.LogInformation("Update download {Platform} iniciada desde {Ip} (ranuras libres {Slots})",
            platform, ctx.Connection.RemoteIpAddress, upd.AvailableSlots);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{file.Value.FileName}\"";
        await ctx.Response.SendFileAsync(file.Value.Path);
    }
    finally { upd.EndDownload(); }
});

app.MapGet("/", () => "RSocServer up");

app.Run();

static IResult RequireApiToken(HttpRequest http, RegistryStore store, Func<IResult> onAuthorized)
{
    var auth = http.Headers.Authorization.ToString();
    var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth["Bearer ".Length..]
        : null;
    return store.IsApiTokenValid(token) ? onAuthorized() : Results.Unauthorized();
}

// Hace accesible Program a WebApplicationFactory en el proyecto de tests.
public partial class Program;
