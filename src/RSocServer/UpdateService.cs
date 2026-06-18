using System.Security.Cryptography;
using RSoc.Protocol;

namespace RSocServer;

/// <summary>
/// Distribución y señalización de actualizaciones del cliente. El servidor hospeda en
/// <c>updates/&lt;plataforma&gt;/</c> el artefacto del cliente (zip de Windows o APK de Android) y un
/// <c>version.txt</c> con su versión. Al consultar, compara con la versión del cliente y, para no
/// saturarse, solo autoriza la descarga si hay una RANURA libre (rollout escalonado): así nunca
/// se actualizan todos a la vez. El hash SHA-256 permite al cliente verificar la descarga.
/// </summary>
public sealed class UpdateService
{
    private sealed record Artifact(string Version, string Path, string FileName, long Size, string Sha256, DateTime Stamp);

    private readonly string _dir;
    private readonly SemaphoreSlim _slots;
    private readonly object _lock = new();
    private readonly Dictionary<string, Artifact> _cache = new();
    private readonly ILogger<UpdateService> _log;

    public bool Enabled { get; }
    public int MaxConcurrent { get; }
    public int AvailableSlots => _slots.CurrentCount;
    public int RetryAfterSeconds { get; }

    public UpdateService(IConfiguration config, ILogger<UpdateService> log)
    {
        _log = log;
        Enabled = config.GetValue("Update:Enabled", true);
        MaxConcurrent = Math.Max(1, config.GetValue("Update:MaxConcurrentDownloads", 3));
        RetryAfterSeconds = Math.Max(5, config.GetValue("Update:RetryAfterSeconds", 30));
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        _dir = Path.Combine(AppContext.BaseDirectory, "updates");
    }

    private static string FileFor(string platform) =>
        platform.Equals("android", StringComparison.OrdinalIgnoreCase) ? "RSoc.apk" : "RSocClient.zip";

    /// <summary>Construye el manifiesto para una plataforma y versión de cliente dadas.</summary>
    public UpdateManifest Check(string platform, string clientVersion)
    {
        platform = platform.Equals("android", StringComparison.OrdinalIgnoreCase) ? "android" : "windows";
        var art = GetArtifact(platform);
        if (art is null)
            return new UpdateManifest(platform, clientVersion, false, false, 0, "", FileFor(platform), RetryAfterSeconds);

        bool newer = AppVersion.IsNewer(art.Version, clientVersion);
        bool allowed = newer && _slots.CurrentCount > 0;
        return new UpdateManifest(platform, art.Version, newer, allowed, art.Size, art.Sha256,
            art.FileName, RetryAfterSeconds);
    }

    /// <summary>Ruta y nombre del artefacto a descargar, o null si no existe.</summary>
    public (string Path, string FileName)? GetFile(string platform)
    {
        var art = GetArtifact(platform);
        return art is null ? null : (art.Path, art.FileName);
    }

    /// <summary>Intenta tomar una ranura de descarga (rollout). False si no hay libres.</summary>
    public bool TryBeginDownload() => _slots.Wait(0);

    /// <summary>Libera la ranura al terminar (o abortar) la descarga.</summary>
    public void EndDownload() => _slots.Release();

    // Localiza el artefacto y cachea su hash; recalcula si el fichero cambió (fecha/tamaño).
    private Artifact? GetArtifact(string platform)
    {
        var file = Path.Combine(_dir, platform, FileFor(platform));
        if (!File.Exists(file)) return null;

        var fi = new FileInfo(file);
        lock (_lock)
        {
            if (_cache.TryGetValue(platform, out var cached) &&
                cached.Stamp == fi.LastWriteTimeUtc && cached.Size == fi.Length)
                return cached;

            var verFile = Path.Combine(_dir, platform, "version.txt");
            var version = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : AppVersion.Current;
            string sha = ComputeSha256(file);
            var art = new Artifact(version, file, FileFor(platform), fi.Length, sha, fi.LastWriteTimeUtc);
            _cache[platform] = art;
            _log.LogInformation("Artefacto de actualización {Platform} v{Version} ({Size} bytes, sha256 {Sha8}…)",
                platform, version, fi.Length, sha.Length >= 8 ? sha[..8] : sha);
            return art;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
