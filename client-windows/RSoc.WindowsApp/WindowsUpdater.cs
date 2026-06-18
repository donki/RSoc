using System.Diagnostics;
using System.IO.Compression;

namespace RSoc.WindowsApp;

/// <summary>
/// Instala una actualización ya descargada y verificada del cliente Windows. Como un ejecutable
/// en uso no puede sobrescribirse a sí mismo, extrae el paquete a un staging junto al .exe y lanza
/// un .cmd que espera a que el proceso actual termine, sustituye los ficheros (preservando la
/// config del usuario <c>rsoc-client-conf.json</c>) y relanza la app.
/// </summary>
internal static class WindowsUpdater
{
    public static void InstallAndRestart(string zipPath)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(appDir, "RSoc.WindowsApp.exe");
        string staging = Path.Combine(appDir, ".update_staging");

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        int pid = Environment.ProcessId;
        string cmdPath = Path.Combine(Path.GetTempPath(), $"rsoc_update_{pid}.cmd");

        // robocopy: códigos 0..7 = éxito. /XF preserva la config del usuario. Espera a que el
        // proceso actual (pid) desaparezca antes de tocar los ficheros.
        string cmd = $"""
@echo off
:wait
tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
robocopy "{staging}" "{appDir}" /E /XF rsoc-client-conf.json /R:10 /W:1 /NFL /NDL /NJH /NJS /NC /NS /NP >nul
rmdir /s /q "{staging}"
del "{zipPath}" 2>nul
start "" "{exe}"
del "%~f0"
""";
        File.WriteAllText(cmdPath, cmd);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
