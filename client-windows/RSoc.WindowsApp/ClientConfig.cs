using System.Text.Json;

namespace RSoc.WindowsApp;

/// <summary>Roles del cliente (valores de <see cref="ClientConfig.Role"/>).</summary>
public static class ClientRole
{
    public const string Manager = "Manager";
    public const string Remote = "Remote";
}

/// <summary>
/// Configuración del cliente Windows. Se lee de <c>rsoc-client-conf.json</c> junto al ejecutable
/// si existe; si no, usa valores por defecto (LAN local). El usuario final no toca nada: el
/// JSON viene preconfigurado en el paquete.
/// </summary>
public sealed class ClientConfig
{
    public string Server { get; set; } = "https://127.0.0.1:21114";
    public string ApiUser { get; set; } = "";
    public string ApiPassword { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Alias { get; set; } = "";
    public string ConnectionPassword { get; set; } = "";

    /// <summary>
    /// Rol de este cliente:
    /// <list type="bullet">
    /// <item><c>"Manager"</c> (gestor): ve TODOS los dispositivos (agrupados) y puede controlarlos.</item>
    /// <item><c>"Remote"</c> (remoto): solo se ofrece para ser controlado; NO recibe la lista de los demás.</item>
    /// </list>
    /// Por defecto <c>Manager</c> para no cambiar el comportamiento de instalaciones existentes.
    /// </summary>
    public string Role { get; set; } = ClientRole.Manager;

    /// <summary>Grupo al que pertenece este equipo (lo verá el gestor). El gestor puede reasignarlo.</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// Dirección del relay que usa ESTE equipo cuando actúa de agente (controlado), por si la
    /// que el servidor entrega al controlador no es alcanzable localmente (p.ej. detrás de NAT).
    /// Vacío = usar la del ticket. Para un servidor local, "127.0.0.1".
    /// </summary>
    public string RelayHost { get; set; } = "";
    public int RelayPort { get; set; } = 0;

    /// <summary>Si está activo, pide confirmación al usuario cuando alguien intenta controlar este equipo.</summary>
    public bool ConfirmAccess { get; set; } = false;

    /// <summary>
    /// Aceptar certificados TLS autofirmados (API HTTPS y sesión cifrada sobre el relay). Activo por
    /// defecto: RSoc genera certificados autofirmados. Ponlo en false solo si instalas certificados
    /// de una CA en la que el sistema confíe (si no, no podrá conectar).
    /// </summary>
    public bool AcceptSelfSignedCerts { get; set; } = true;

    /// <summary>Arrancar automáticamente con Windows (al iniciar sesión). Activo por defecto.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>true si el rol es gestor (comparación insensible a mayúsculas; cualquier valor no
    /// reconocido como "Remote" se trata como gestor por seguridad de compatibilidad).</summary>
    public bool IsManager => !string.Equals(Role, ClientRole.Remote, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Directorio del ejecutable REAL (RSocGestor.exe / RSocRemoto.exe), no el temporal de
    /// extracción del single-file. La config y la carpeta de logs viven aquí, junto al .exe, para
    /// que sean visibles y editables por el usuario (con <c>IncludeAllContentForSelfExtract</c> el
    /// <c>AppContext.BaseDirectory</c> apunta al temporal de extracción, que no sirve).
    /// </summary>
    public static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } d ? d : AppContext.BaseDirectory;

    public static ClientConfig Load()
    {
        var path = Path.Combine(AppDir, "rsoc-client-conf.json");
        ClientConfig cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                : new();
        }
        catch { cfg = new(); }

        // Overrides por entorno (útil para lanzar varias instancias de demo en una máquina).
        cfg.Server = Env("RSOC_SERVER") ?? cfg.Server;
        cfg.DeviceId = Env("RSOC_DEVICE_ID") ?? cfg.DeviceId;
        cfg.Alias = Env("RSOC_ALIAS") ?? cfg.Alias;
        cfg.ConnectionPassword = Env("RSOC_CONN_PWD") ?? cfg.ConnectionPassword;
        cfg.Role = Env("RSOC_ROLE") ?? cfg.Role;
        cfg.Group = Env("RSOC_GROUP") ?? cfg.Group;

        if (string.IsNullOrWhiteSpace(cfg.DeviceId))
            cfg.DeviceId = Environment.MachineName.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(cfg.Alias))
            cfg.Alias = Environment.MachineName;
        cfg.Role = cfg.IsManager ? ClientRole.Manager : ClientRole.Remote; // normaliza
        return cfg;

        static string? Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <summary>Persiste la configuración en rsoc-client-conf.json (junto al ejecutable).</summary>
    public void Save()
    {
        try
        {
            var path = Path.Combine(AppDir, "rsoc-client-conf.json");
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignorar errores de escritura */ }
    }
}
