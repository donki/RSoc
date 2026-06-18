namespace RSoc.Protocol;

/// <summary>
/// Versión del software RSoc, compartida por cliente y servidor. Es la fuente única de verdad
/// que se hornea en cada build (vía RSoc.Protocol.dll). El servidor publica la versión del
/// artefacto que hospeda (updates/&lt;plataforma&gt;/version.txt); el cliente reporta esta constante
/// al conectarse y, si la del servidor es más nueva, se autoactualiza.
/// </summary>
public static class AppVersion
{
    /// <summary>Versión de ESTE build (semántica MAJOR.MINOR.PATCH[.BUILD]).</summary>
    public const string Current = "2026.06.18.3";

    /// <summary>
    /// Compara dos versiones tipo "1.2.3". Devuelve &gt;0 si <paramref name="a"/> es más nueva que
    /// <paramref name="b"/>, &lt;0 si más vieja, 0 si iguales. Tolera componentes ausentes y basura.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < 4; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>True si <paramref name="latest"/> es estrictamente más nueva que <paramref name="current"/>.</summary>
    public static bool IsNewer(string? latest, string? current) => Compare(latest, current) > 0;

    private static int[] Parse(string? s)
    {
        var v = new int[4];
        if (string.IsNullOrWhiteSpace(s)) return v;
        var parts = s.Trim().Split('.', '+', '-', ' ');
        for (int i = 0; i < 4 && i < parts.Length; i++)
            _ = int.TryParse(parts[i], out v[i]);
        return v;
    }
}



