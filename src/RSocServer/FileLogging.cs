using System.Text;
using RSoc.Protocol;

namespace RSocServer;

/// <summary>
/// Proveedor de logging a fichero con rotación por tamaño (10 MB) y retención por edad (7 días),
/// apoyado en <see cref="RollingFileWriter"/> (compartido con el cliente). Diagnóstico detallado.
/// </summary>
public sealed class RollingFileLoggerProvider(string path, long maxBytes, TimeSpan maxAge, LogLevel minLevel)
    : ILoggerProvider
{
    private readonly RollingFileWriter _writer = new(path, maxBytes, maxAge);

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, _writer, minLevel);

    public void Dispose() => _writer.Dispose();
}

internal sealed class RollingFileLogger(string category, RollingFileWriter writer, LogLevel minLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        var sb = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Short(logLevel)).Append("] ")
            .Append(category).Append(": ").Append(msg);
        if (exception is not null) sb.Append(Environment.NewLine).Append(exception);
        writer.Write(sb.ToString());
    }

    private static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
