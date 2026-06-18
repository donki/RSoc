using System.Collections.Concurrent;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Transferencia de ficheros sobre el canal de sesión. Envía un fichero troceado
/// (offer → chunks → end) y, en recepción, ensambla los trozos en una carpeta de descargas.
/// Lo usan por igual el agente y el controlador (transferencia en ambos sentidos).
/// </summary>
public sealed class FileTransfer
{
    private readonly SessionChannel _channel;
    private readonly string _downloadDir;
    private int _nextId;
    private readonly ConcurrentDictionary<int, FileStream> _incoming = new();
    private readonly ConcurrentDictionary<int, string> _incomingPaths = new();

    /// <summary>Se dispara con la ruta completa cuando se recibe un fichero entero.</summary>
    public event Action<string>? FileReceived;

    public FileTransfer(SessionChannel channel, string? downloadDir = null)
    {
        _channel = channel;
        _downloadDir = downloadDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RSoc");
        Directory.CreateDirectory(_downloadDir);
    }

    public async Task SendFileAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        int id = Interlocked.Increment(ref _nextId);
        await _channel.SendFileOfferAsync(id, info.Name, info.Length, ct);

        var buf = new byte[64 * 1024];
        await using var fs = File.OpenRead(path);
        int r;
        while ((r = await fs.ReadAsync(buf, ct)) > 0)
            await _channel.SendFileChunkAsync(id, buf, r, ct);

        await _channel.SendFileEndAsync(id, ct);
    }

    // Enrutado desde el bucle de lectura de la sesión.

    public void OnOffer(int id, string name, long length)
    {
        var dest = Unique(Path.Combine(_downloadDir, Path.GetFileName(name)));
        _incoming[id] = new FileStream(dest, FileMode.Create, FileAccess.Write);
        _incomingPaths[id] = dest;
    }

    public void OnChunk(int id, byte[] data)
    {
        if (_incoming.TryGetValue(id, out var fs)) fs.Write(data, 0, data.Length);
    }

    public void OnEnd(int id)
    {
        if (!_incoming.TryRemove(id, out var fs)) return;
        fs.Dispose();
        if (_incomingPaths.TryRemove(id, out var path))
            FileReceived?.Invoke(path);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
