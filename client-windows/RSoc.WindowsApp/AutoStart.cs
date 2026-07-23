using Microsoft.Win32;

namespace RSoc.WindowsApp;

/// <summary>Arranque automático con Windows vía la clave Run del usuario actual (sin admin).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Valor por ejecutable (RSoc-RSocGestor / RSoc-RSocRemoto): así gestor y remoto pueden
    // arrancar ambos con Windows sin pisarse la entrada.
    private static string ValueName =>
        "RSoc-" + Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RSoc");

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* ignorar */ }
    }
}
