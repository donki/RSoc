using System.Text;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Log a fichero del cliente (agente y UI). Escribe en <c>logs\RSocClient.log</c> junto al
/// ejecutable, con rotación por tamaño (10 MB) y retención por edad (7 días) vía
/// <see cref="RollingFileWriter"/>. Es estático y seguro entre hilos: cualquier capa del
/// cliente puede registrar sin acoplarse a un framework de logging.
/// </summary>
public static class FileLog
{
    private static RollingFileWriter? _writer;
    private static readonly object _gate = new();

    /// <summary>Inicializa el log en <paramref name="dir"/> (se crea si no existe). Idempotente.</summary>
    public static void Init(string dir)
    {
        lock (_gate)
        {
            if (_writer is not null) return;
            try { _writer = new RollingFileWriter(Path.Combine(dir, "RSocClient.log"), 10L * 1024 * 1024, TimeSpan.FromDays(7)); }
            catch { _writer = null; }
        }
    }

    public static void Info(string msg) => Write("INF", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WRN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        var w = _writer;
        if (w is null) return;
        var sb = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ").Append(msg);
        if (ex is not null) sb.Append(Environment.NewLine).Append(ex);
        try { w.Write(sb.ToString()); } catch { /* no dejar que el log tumbe la app */ }
    }

    public static void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}
