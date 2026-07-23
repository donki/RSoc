using System.Diagnostics;
using System.IO.Compression;

namespace RSoc.WindowsApp;

/// <summary>
/// Instala una actualización ya descargada y verificada del cliente Windows (single-file). Como un
/// ejecutable en uso no puede sobrescribirse a sí mismo, extrae el paquete a un staging junto al
/// .exe y lanza un .cmd que espera a que el proceso actual termine, copia el nuevo binario sobre el
/// ejecutable ACTUAL (conservando su nombre de rol: RSocGestor.exe / RSocRemoto.exe) y lo relanza.
/// La config del usuario (<c>rsoc-client-conf.json</c>) queda intacta: está fuera del .exe.
/// </summary>
internal static class WindowsUpdater
{
    public static void InstallAndRestart(string zipPath)
    {
        // Ejecutable realmente en marcha (single-file): su nombre define el rol y hay que preservarlo.
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RSoc.WindowsApp.exe");
        string appDir = Path.GetDirectoryName(exe)!.TrimEnd('\\');
        string staging = Path.Combine(appDir, ".update_staging");

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        // El paquete single-file contiene el binario genérico RSocClient.exe.
        string staged = Path.Combine(staging, "RSocClient.exe");

        int pid = Environment.ProcessId;
        string cmdPath = Path.Combine(Path.GetTempPath(), $"rsoc_update_{pid}.cmd");

        // Espera a que el proceso actual (pid) desaparezca, copia el nuevo .exe sobre el actual
        // (mismo nombre de rol) y relanza. No toca la config (vive suelta junto al .exe).
        string cmd = $"""
@echo off
:wait
tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
copy /Y "{staged}" "{exe}" >nul
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
