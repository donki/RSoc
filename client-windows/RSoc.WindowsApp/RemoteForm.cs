using System.Runtime.InteropServices;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.WindowsApp;

/// <summary>
/// Cliente RSoc en modo REMOTO: este equipo solo se ofrece para ser controlado. Se registra en
/// el servidor y acepta el control entrante, pero —a diferencia del gestor (<see cref="MainForm"/>)—
/// NO hace login del API ni recibe la lista de los demás equipos conectados. La ventana muestra su
/// propia identidad (ID + contraseña de conexión) y el estado, y vive en la bandeja del sistema.
/// </summary>
public sealed class RemoteForm : ChromeForm
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Color _online = Success;

    private readonly ClientConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _statusPill;
    private ClipboardBridge? _clip;

    private Label _sessionLabel = null!;
    private CheckBox _confirmCheck = null!;
    private ToolStripMenuItem _confirmMenuItem = null!;
    private bool _confirmAccess;

    private readonly List<SessionHost> _hosts = [];

    private NotifyIcon _tray = null!;
    private bool _reallyExit;
    private bool _hiddenOnce;

    public RemoteForm(ClientConfig cfg)
    {
        _cfg = cfg;
        TitleText = $"RSoc remoto — {_cfg.Alias}";
        Width = 420;
        Height = 420;
        MinimumSize = new Size(360, 360);
        _confirmAccess = _cfg.ConfirmAccess;
        _statusPill = AddCaptionStatus();
        SetPill(false);

        BuildContent();
        BuildTray();

        Load += async (_, _) => await StartAsync();
        Shown += (_, _) => { if (!_hiddenOnce) { _hiddenOnce = true; HideToTray(); } };
        FormClosing += OnFormClosing;
    }

    private void BuildContent()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = SurfaceBg, Padding = new Padding(22, 18, 22, 16) };

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "ESTE EQUIPO SE PUEDE CONTROLAR EN REMOTO",
            ForeColor = SubText,
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
        };

        // Tarjeta con la identidad de este equipo: alias, ID y contraseña de conexión.
        var card = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = CardBg, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 8, 0, 0) };
        card.Controls.Add(Field("Contraseña de conexión", MaskOrEmpty(_cfg.ConnectionPassword), top: 96));
        card.Controls.Add(Field("ID de este equipo", _cfg.DeviceId, top: 52));
        card.Controls.Add(Field("Grupo", string.IsNullOrWhiteSpace(_cfg.Group) ? "Sin grupo" : _cfg.Group, top: 8, valueOnRight: true));
        card.Controls.Add(new Label
        {
            Text = _cfg.Alias,
            Left = 16, Top = 8, Width = 240, Height = 26,
            ForeColor = SurfaceText,
            Font = new Font("Segoe UI Semibold", 13f),
        });

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = SurfaceBg };

        _confirmCheck = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Pedir confirmación cuando alguien controle este equipo",
            ForeColor = SurfaceText,
            Checked = _confirmAccess,
        };
        _confirmCheck.CheckedChanged += (_, _) => SetConfirmAccess(_confirmCheck.Checked);

        _sessionLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            ForeColor = SubText,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        content.Controls.Add(_confirmCheck);
        content.Controls.Add(spacer);
        content.Controls.Add(card);
        content.Controls.Add(heading);
        content.Controls.Add(_sessionLabel);
        Controls.Add(content);

        UpdateSessionsUi();
    }

    // Etiqueta "título pequeño + valor" para la tarjeta de identidad.
    private Panel Field(string caption, string value, int top, bool valueOnRight = false)
    {
        var p = new Panel { Left = 16, Top = top, Width = 350, Height = 40, BackColor = CardBg };
        p.Controls.Add(new Label
        {
            Text = caption, Left = 0, Top = 0, Width = 340, Height = 16,
            ForeColor = SubText, Font = new Font("Segoe UI", 8f),
        });
        p.Controls.Add(new Label
        {
            Text = value, Left = 0, Top = 16, Width = 340, Height = 22,
            ForeColor = SurfaceText, Font = new Font("Segoe UI Semibold", 10.5f),
            TextAlign = valueOnRight ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
        });
        return p;
    }

    private static string MaskOrEmpty(string pwd) =>
        string.IsNullOrEmpty(pwd) ? "(sin contraseña)" : new string('•', Math.Min(pwd.Length, 12));

    private async Task StartAsync()
    {
        FileLog.Info("Remoto iniciando: solo agente (sin lista de dispositivos)");
        AutoStart.Apply(_cfg.AutoStart);

        var http = RSocHttp.Create(_cfg.Server, _cfg.AcceptSelfSignedCerts);
        var api = new RSocApiClient(http);

        _clip = new ClipboardBridge(this);
        _clip.LocalTextCopied += t => { lock (_hosts) foreach (var h in _hosts) _ = h.SendClipboardAsync(t, _cts.Token); };

        var agent = new DeviceAgent(api, _cfg.DeviceId, _cfg.Alias, _cfg.ConnectionPassword, "rsoc-pubkey")
        {
            Group = _cfg.Group,
            Kind = ClientKind.Remote,
            RelayHostOverride = string.IsNullOrWhiteSpace(_cfg.RelayHost) ? null : _cfg.RelayHost,
            RelayPortOverride = _cfg.RelayPort,
        };
        agent.SessionAccepted += OnSessionAcceptedAsync;
        agent.ConnectivityChanged += connected => { if (IsHandleCreated) BeginInvoke(() => SetPill(connected)); };
        _ = Task.Run(() => agent.RunAsync(_cts.Token));

        _ = Task.Run(() => UpdateLoopAsync(api, _cts.Token));
        await Task.CompletedTask;
    }

    private void SetPill(bool connected)
    {
        _statusPill.Text = connected ? "● Conectado" : "● Reconectando…";
        _statusPill.ForeColor = connected ? Color.FromArgb(190, 255, 214) : Color.FromArgb(255, 226, 184);
    }

    private async Task OnSessionAcceptedAsync(RelayConnection relay, SessionTicket ticket)
    {
        if (_confirmAccess)
        {
            bool allow = (bool)Invoke(new Func<bool>(() => AskAllow(ticket.Peer)))!;
            if (!allow) { FileLog.Info($"Control entrante DENEGADO por el usuario ('{ticket.Peer}', sesión {ticket.SessionId})"); relay.Dispose(); return; }
        }

        var host = new SessionHost();
        host.ClipboardReceived += t => _clip?.ApplyRemoteText(t);
        lock (_hosts) _hosts.Add(host);
        UpdateSessionsUi();
        FileLog.Info($"Control entrante ACEPTADO de '{ticket.Peer}' (sesión {ticket.SessionId})");
        try { await host.RunAsync(relay.Stream, _cts.Token); }
        catch (Exception ex) { FileLog.Warn($"Sesión con '{ticket.Peer}' terminó con error", ex); }
        finally { lock (_hosts) _hosts.Remove(host); UpdateSessionsUi(); relay.Dispose(); FileLog.Info($"Control entrante de '{ticket.Peer}' finalizado (sesión {ticket.SessionId})"); }
    }

    private void UpdateSessionsUi()
    {
        void Apply()
        {
            int n;
            lock (_hosts) n = _hosts.Count;
            if (n == 0)
            {
                _sessionLabel.Text = "Nadie está controlando este equipo";
                _sessionLabel.ForeColor = SubText;
            }
            else
            {
                _sessionLabel.Text = n == 1 ? "Te están controlando (1 sesión)" : $"Te están controlando ({n} sesiones)";
                _sessionLabel.ForeColor = _online;
            }
        }
        if (IsHandleCreated) BeginInvoke(Apply); else Apply();
    }

    // --- Autoactualización (igual que el gestor: el remoto también se actualiza solo) ---
    private async Task UpdateLoopAsync(RSocApiClient api, CancellationToken ct)
    {
        int seed = _cfg.DeviceId.GetHashCode();
        var rnd = new Random(seed);
        try { await Task.Delay(TimeSpan.FromSeconds(15 + rnd.Next(0, 90)), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan nextWait = TimeSpan.FromHours(6);
            try
            {
                var upd = new UpdateClient(api, "windows");
                var m = await upd.CheckAsync(ct);
                if (m is { UpdateAvailable: true } && !string.IsNullOrEmpty(m.Sha256))
                {
                    FileLog.Info($"Actualización disponible v{m.LatestVersion}; descargando…");
                    var dir = Path.Combine(Path.GetTempPath(), "rsoc_update");
                    Directory.CreateDirectory(dir);
                    var dest = Path.Combine(dir, m.FileName);
                    if (await upd.DownloadWithRolloutAsync(m, dest, seed, ct: ct))
                    {
                        FileLog.Info($"Instalando actualización v{m.LatestVersion} y reiniciando");
                        WindowsUpdater.InstallAndRestart(dest);
                        if (IsHandleCreated) BeginInvoke(() => { _reallyExit = true; Close(); });
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { nextWait = TimeSpan.FromMinutes(30); FileLog.Warn("Fallo en la comprobación/descarga de actualización", ex); }

            try { await Task.Delay(nextWait, ct); } catch { return; }
        }
    }

    // --- Bandeja, confirmación y cierre (idénticos en espíritu al gestor) ---

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir RSoc", null, (_, _) => ShowFromTray());

        _confirmMenuItem = new ToolStripMenuItem("Confirmar cuando me controlen", null,
            (_, _) => SetConfirmAccess(!_confirmAccess)) { Checked = _confirmAccess };
        menu.Items.Add(_confirmMenuItem);

        var autoItem = new ToolStripMenuItem("Iniciar con Windows") { Checked = _cfg.AutoStart };
        autoItem.Click += (_, _) => { autoItem.Checked = !autoItem.Checked; SetAutoStart(autoItem.Checked); };
        menu.Items.Add(autoItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => { _reallyExit = true; Close(); });

        _tray = new NotifyIcon
        {
            Icon = Icon ?? System.Drawing.SystemIcons.Application,
            Text = $"RSoc remoto — {_cfg.Alias}",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            _tray.ShowBalloonTip(1500, "RSoc", "Sigue activo en la bandeja del sistema.", ToolTipIcon.Info);
            return;
        }
        _cts.Cancel();
        _clip?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
    }

    private void SetConfirmAccess(bool value)
    {
        _confirmAccess = value;
        _cfg.ConfirmAccess = value;
        _cfg.Save();
        if (_confirmCheck.Checked != value) _confirmCheck.Checked = value;
        _confirmMenuItem.Checked = value;
    }

    private void SetAutoStart(bool value)
    {
        _cfg.AutoStart = value;
        _cfg.Save();
        AutoStart.Apply(value);
    }

    private bool AskAllow(string peer)
    {
        var who = string.IsNullOrWhiteSpace(peer) ? "Un equipo remoto" : peer;
        using var f = new Form
        {
            Text = "RSoc — Solicitud de control",
            Width = 400, Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true, MinimizeBox = false, MaximizeBox = false,
        };
        var lbl = new Label { Left = 16, Top = 16, Width = 360, Height = 60, Text = $"«{who}» quiere controlar este equipo.\n¿Permitir el acceso?" };
        var allow = new Button { Text = "Permitir", Left = 196, Top = 96, Width = 85, DialogResult = DialogResult.Yes };
        var deny = new Button { Text = "Denegar", Left = 291, Top = 96, Width = 85, DialogResult = DialogResult.No };
        f.Controls.Add(lbl); f.Controls.Add(allow); f.Controls.Add(deny);
        f.AcceptButton = allow; f.CancelButton = deny;

        using var t = new System.Windows.Forms.Timer { Interval = 30000 };
        t.Tick += (_, _) => { t.Stop(); f.DialogResult = DialogResult.No; f.Close(); };
        f.Shown += (_, _) =>
        {
            t.Start();
            f.Activate();
            f.BringToFront();
            SetForegroundWindow(f.Handle);
            System.Media.SystemSounds.Exclamation.Play();
        };
        return f.ShowDialog() == DialogResult.Yes;
    }
}
