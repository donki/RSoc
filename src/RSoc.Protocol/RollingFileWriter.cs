using System.Text;

namespace RSoc.Protocol;

/// <summary>
/// Escritor de log a fichero con doble límite, compartido por servidor y cliente:
///   • por TAMAÑO: el fichero activo nunca supera <c>maxBytes</c> (por defecto 10 MB); al llegar
///     al límite se archiva con marca de tiempo (<c>&lt;nombre&gt;-yyyyMMdd-HHmmss-fff.log</c>) y se
///     abre uno nuevo.
///   • por EDAD: al arrancar y en cada rotación se borran los ficheros archivados con más de
///     <c>maxAge</c> (por defecto 7 días). Además, si al arrancar el fichero activo es de un día
///     anterior, se archiva para que también pueda caducar.
/// Es seguro entre hilos (un único escritor con bloqueo).
/// </summary>
public sealed class RollingFileWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;      // fichero activo, p.ej. logs\RSocClient.log
    private readonly string _dir;
    private readonly string _stem;      // "RSocClient"
    private readonly string _ext;       // ".log"
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private StreamWriter? _writer;
    private long _size;

    public RollingFileWriter(string path, long maxBytes, TimeSpan maxAge)
    {
        _path = path;
        _dir = Path.GetDirectoryName(path)!;
        _stem = Path.GetFileNameWithoutExtension(path);
        _ext = Path.GetExtension(path);
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxAge = maxAge;
        Directory.CreateDirectory(_dir);

        // Si el fichero activo viene de un día anterior (o ya está lleno), se archiva al arrancar.
        try
        {
            if (File.Exists(_path))
            {
                var fi = new FileInfo(_path);
                if (fi.Length > 0 && (fi.Length >= _maxBytes || fi.LastWriteTime.Date < DateTime.Now.Date))
                    Archive();
            }
        }
        catch { /* si no se puede archivar, se continúa sobre el mismo fichero */ }

        Open();
        Purge();
    }

    private void Open()
    {
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        _size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            long len = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_size > 0 && _size + len > _maxBytes)
            {
                Archive();
                Open();
                Purge();
            }
            _writer!.WriteLine(line);
            _size += len;
        }
    }

    // Cierra el fichero activo y lo renombra con marca de tiempo (queda como archivado).
    private void Archive()
    {
        _writer?.Dispose();
        _writer = null;
        if (!File.Exists(_path)) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dest = Path.Combine(_dir, $"{_stem}-{stamp}{_ext}");
        try { File.Move(_path, dest, overwrite: true); }
        catch { /* si falla el rename, se reabrirá y se seguirá anexando */ }
    }

    // Borra los ficheros archivados (<stem>-*.<ext>) con más de maxAge de antigüedad.
    private void Purge()
    {
        try
        {
            var cutoff = DateTime.Now - _maxAge;
            foreach (var f in Directory.EnumerateFiles(_dir, $"{_stem}-*{_ext}"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                catch { /* en uso o sin permiso: se reintentará en la próxima rotación */ }
            }
        }
        catch { /* directorio inaccesible: no es crítico */ }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
