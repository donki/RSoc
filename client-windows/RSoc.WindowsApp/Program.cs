using System.Diagnostics;
using RSoc.Client;

namespace RSoc.WindowsApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Log detallado a fichero (logs\ junto al exe real): 10 MB máx. por fichero, 7 días de retención.
        FileLog.Init(Path.Combine(ClientConfig.AppDir, "logs"));
        FileLog.Info($"=== Arranque RSoc cliente v{RSoc.Protocol.AppVersion.Current} · exe='{Path.GetFileName(Environment.ProcessPath)}' · pid={Environment.ProcessId} ===");

        // Errores no controlados -> al log (para diagnóstico de crashes).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLog.Error("Excepción no controlada", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => FileLog.Error("Excepción en hilo de UI", e.Exception);

        // Instancia única: si ya hay otra copia de ESTE mismo ejecutable en marcha, se cierra la
        // anterior y arranca esta. (Solo el mismo binario: gestor y remoto pueden convivir.)
        CloseOtherInstances();

        ApplicationConfiguration.Initialize();
        var cfg = ClientConfig.Load();

        // El rol se decide, por este orden de prioridad:
        //   1) El nombre del ejecutable (RSocRemoto.exe / RSocGestor.exe): así un mismo publish
        //      ofrece los dos roles como binarios distintos junto a los mismos DLL.
        //   2) La config (rsoc-client-conf.json / RSOC_ROLE) si el nombre no lo indica.
        cfg.Role = ResolveRole(cfg);
        FileLog.Info($"Rol={cfg.Role} · alias='{cfg.Alias}' · deviceId='{cfg.DeviceId}' · grupo='{cfg.Group}' · servidor='{cfg.Server}'");

        // El gestor ve y controla todos los equipos; el remoto solo se ofrece para ser controlado
        // y nunca recibe la lista de los demás.
        Form main = cfg.IsManager ? new MainForm(cfg) : new RemoteForm(cfg);
        try { Application.Run(main); }
        finally { FileLog.Info("=== Cierre del cliente ==="); FileLog.Dispose(); }
    }

    private static string ResolveRole(ClientConfig cfg)
    {
        var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").ToLowerInvariant();
        if (exe.Contains("remot")) return ClientRole.Remote;
        if (exe.Contains("gestor") || exe.Contains("manager")) return ClientRole.Manager;
        return cfg.IsManager ? ClientRole.Manager : ClientRole.Remote;
    }

    // Mata cualquier otro proceso con el mismo nombre de ejecutable (misma copia del cliente),
    // dando prioridad a la instancia recién lanzada. No toca otros binarios (servidor, relay,
    // ni el otro rol de cliente).
    private static void CloseOtherInstances()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                try { FileLog.Info($"Instancia única: cerrando instancia previa pid={p.Id}"); p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                catch (Exception ex) { FileLog.Warn($"No se pudo cerrar la instancia previa pid={p.Id}", ex); }
                finally { p.Dispose(); }
            }
        }
        catch { /* enumerar procesos puede fallar por permisos: no es crítico */ }
    }
}
