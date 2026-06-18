using System.Security.Cryptography;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Lógica de autoactualización agnóstica de plataforma: consulta al servidor, descarga el
/// artefacto respetando el rollout escalonado (si el servidor responde "sin ranura", reintenta
/// con espera + jitter para no saturarlo) y verifica el SHA-256. La instalación concreta (swap
/// del cliente Windows, intent de APK en Android) la hace cada plataforma con el fichero ya
/// descargado y verificado.
/// </summary>
public sealed class UpdateClient(RSocApiClient api, string platform)
{
    public string CurrentVersion => AppVersion.Current;

    /// <summary>Manifiesto del servidor, o null si la función está deshabilitada.</summary>
    public Task<UpdateManifest?> CheckAsync(CancellationToken ct = default) =>
        api.CheckUpdateAsync(platform, AppVersion.Current, ct);

    /// <summary>
    /// Descarga a <paramref name="destPath"/> respetando el rollout: si el servidor no da ranura,
    /// espera RetryAfter + jitter (sembrado con <paramref name="seed"/> para repartir en el tiempo)
    /// y reintenta. Devuelve true si se descargó y el SHA-256 coincide.
    /// </summary>
    public async Task<bool> DownloadWithRolloutAsync(UpdateManifest m, string destPath, int seed,
        int maxAttempts = 240, CancellationToken ct = default)
    {
        var rnd = new Random(seed);
        for (int attempt = 0; attempt < maxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            bool got = await api.DownloadUpdateAsync(platform, destPath, ct);
            if (got)
            {
                if (await VerifyAsync(destPath, m.Sha256, ct)) return true;
                try { File.Delete(destPath); } catch { }
                throw new InvalidOperationException("La actualización descargada no supera la verificación SHA-256.");
            }
            // 503: sin ranura de rollout. Espera Retry-After + jitter [0, RetryAfter).
            int baseSecs = Math.Max(5, m.RetryAfterSeconds);
            int wait = baseSecs + rnd.Next(0, baseSecs);
            await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        }
        return false;
    }

    /// <summary>Verifica el SHA-256 (hex) de un fichero.</summary>
    public static async Task<bool> VerifyAsync(string path, string expectedSha, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedSha)) return false;
        await using var s = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
        return string.Equals(hash, expectedSha, StringComparison.OrdinalIgnoreCase);
    }
}
