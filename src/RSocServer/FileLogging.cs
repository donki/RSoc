using System.Text;

namespace RSocServer;

/// <summary>
/// Logger de archivo con rotación por tamaño: un fichero base "<nombre>.log" y hasta
/// (maxFiles-1) rotados "<nombre>.log.1".."<nombre>.log.N". Al superar maxBytes se desplazan
/// (el más viejo se borra) y se abre uno nuevo. Pensado para diagnóstico detallado.
/// </summary>
public sealed class RollingFileLoggerProvider(string path, long maxBytes, int maxFiles, LogLevel minLevel)
    : ILoggerProvider
{
    private readonly RollingFileWriter _writer = new(path, maxBytes, maxFiles);

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

/// <summary>Escritor con bloqueo y rotación. Compartido por todos los loggers del provider.</summary>
internal sealed class RollingFileWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private StreamWriter? _writer;
    private long _size;

    public RollingFileWriter(string path, long maxBytes, int maxFiles)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Open();
    }

    private void Open()
    {
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        _size = new FileInfo(_path).Length;
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            long len = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_size + len > _maxBytes && _size > 0) Roll();
            _writer.WriteLine(line);
            _size += len;
        }
    }

    private void Roll()
    {
        _writer!.Dispose();
        // Borra el más viejo y desplaza el resto: .(N-1) -> .N, …, base -> .1
        var oldest = $"{_path}.{_maxFiles - 1}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = _maxFiles - 2; i >= 1; i--)
        {
            var src = $"{_path}.{i}";
            if (File.Exists(src)) File.Move(src, $"{_path}.{i + 1}", overwrite: true);
        }
        if (File.Exists(_path)) File.Move(_path, $"{_path}.1", overwrite: true);
        Open();
        _size = 0;
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
