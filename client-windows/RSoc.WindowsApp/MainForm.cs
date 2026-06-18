using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.WindowsApp;

/// <summary>
/// Ventana principal del cliente RSoc. El mismo binario hace de agente (control desatendido)
/// y de controlador (lista de equipos + abrir <see cref="ViewerForm"/>). Conexión al servidor
/// automática y resiliente (sin botones, con reconexión). Cromo y tema vía <see cref="ChromeForm"/>.
/// </summary>
public sealed class MainForm : ChromeForm
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Color _online = Color.FromArgb(34, 197, 94);
    private readonly Color _offline = Color.FromArgb(150, 156, 168);
    private readonly Color _selected;

    private readonly ClientConfig _cfg = ClientConfig.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _statusPill;
    private readonly ListBox _list = new();
    private readonly Label _hint = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    private HttpClient? _http;
    private RSocApiClient? _api;
    private List<DeviceInfo> _devices = [];
    private bool _loggedIn;
    private ClipboardBridge? _clip;

    private sealed record ActiveSession(SessionHost Host, string Peer);
    private readonly List<ActiveSession> _hosts = [];
    private Label _sessionLabel = null!;
    private Button _sendFileBtn = null!;

    private NotifyIcon _tray = null!;
    private CheckBox _confirmCheck = null!;
    private ToolStripMenuItem _confirmMenuItem = null!;
    private bool _confirmAccess;
    private bool _reallyExit;
    private bool _hiddenOnce;

    public MainForm()
    {
        TitleText = $"RSoc — {_cfg.Alias}";
        Width = 420;
        Height = 560;
        MinimumSize = new Size(360, 420);
        _selected = IsDark ? Color.FromArgb(45, 50, 64) : Color.FromArgb(235, 241, 254);
        _confirmAccess = _cfg.ConfirmAccess;
        _statusPill = AddCaptionStatus();
        SetPill(false);

        BuildContent();
        BuildTray();

        _list.DoubleClick += async (_, _) => await ConnectSelectedAsync();
        _timer.Tick += async (_, _) => await TickAsync();
        Load += async (_, _) => await StartAsync();
        // Arranca minimizado al área de notificaciones.
        Shown += (_, _) => { if (!_hiddenOnce) { _hiddenOnce = true; HideToTray(); } };
        FormClosing += OnFormClosing;
    }

    private void BuildContent()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = SurfaceBg };

        var sectionBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = SurfaceBg, Padding = new Padding(18, 12, 18, 0) };
        sectionBar.Controls.Add(new Label
        {
            Text = "EQUIPOS DISPONIBLES",
            ForeColor = SubText,
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        });

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 8), BackColor = SurfaceBg };
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = CardBg;
        _list.ForeColor = SurfaceText;
        _list.IntegralHeight = false;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 56;
        _list.DrawItem += DrawDeviceItem;
        listHost.Controls.Add(_list);

        // Panel inferior: control remoto activo + confirmación + enviar fichero.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = SurfaceBg, Padding = new Padding(18, 6, 18, 10) };

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 22;
        _hint.ForeColor = SubText;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Text = "Doble clic en un equipo para controlarlo";

        _sendFileBtn = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Text = "Enviar archivo al remoto",
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
        };
        _sendFileBtn.FlatAppearance.BorderSize = 0;
        _sendFileBtn.Click += async (_, _) => await SendFileToRemotesAsync();

        _confirmCheck = new CheckBox
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Pedir confirmación cuando alguien controle este equipo",
            ForeColor = SurfaceText,
            Checked = _confirmAccess,
        };
        _confirmCheck.CheckedChanged += (_, _) => SetConfirmAccess(_confirmCheck.Checked);

        _sessionLabel = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = SurfaceText };

        var sessTitle = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 18,
            Text = "CONTROL REMOTO",
            ForeColor = SubText,
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
        };

        bottom.Controls.Add(_hint);
        bottom.Controls.Add(_sendFileBtn);
        bottom.Controls.Add(_confirmCheck);
        bottom.Controls.Add(_sessionLabel);
        bottom.Controls.Add(sessTitle);

        // Orden de z importante para el docking: el control Fill (listHost) debe ir PRIMERO
        // (al fondo) para que se acople el último y ocupe solo el hueco; si va al frente, la
        // cabecera Top se dibuja encima y tapa el primer equipo de la lista.
        content.Controls.Add(listHost);
        content.Controls.Add(bottom);
        content.Controls.Add(sectionBar);
        Controls.Add(content);

        UpdateSessionsUi();
    }

    private void DrawDeviceItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _devices.Count) return;
        var d = _devices[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? _selected : CardBg))
            g.FillRectangle(bg, e.Bounds);

        var r = e.Bounds;
        var dot = new Rectangle(r.Left + 16, r.Top + r.Height / 2 - 5, 10, 10);
        using (var db = new SolidBrush(d.Online ? _online : _offline))
            g.FillEllipse(db, dot);

        using var fAlias = new Font("Segoe UI Semibold", 10.5f);
        using var fId = new Font("Segoe UI", 8.5f);
        using var bMain = new SolidBrush(SurfaceText);
        using var bDim = new SolidBrush(SubText);
        g.DrawString(d.Alias, fAlias, bMain, r.Left + 38, r.Top + 9);
        g.DrawString(d.DeviceId, fId, bDim, r.Left + 38, r.Top + 30);

        using var pen = new Pen(Divider);
        g.DrawLine(pen, r.Left + 14, r.Bottom - 1, r.Right - 14, r.Bottom - 1);
    }

    private async Task StartAsync()
    {
        AutoStart.Apply(_cfg.AutoStart); // arranque con Windows (activo por defecto)

        _http = RSocHttp.Create(_cfg.Server, _cfg.AcceptSelfSignedCerts); // HTTPS
        _api = new RSocApiClient(_http);

        _clip = new ClipboardBridge(this);
        _clip.LocalTextCopied += t => { lock (_hosts) foreach (var h in _hosts) _ = h.Host.SendClipboardAsync(t, _cts.Token); };

        var agent = new DeviceAgent(_api, _cfg.DeviceId, _cfg.Alias, _cfg.ConnectionPassword, "rsoc-pubkey")
        {
            RelayHostOverride = string.IsNullOrWhiteSpace(_cfg.RelayHost) ? null : _cfg.RelayHost,
            RelayPortOverride = _cfg.RelayPort,
        };
        agent.SessionAccepted += OnSessionAcceptedAsync;
        _ = Task.Run(() => agent.RunAsync(_cts.Token));

        _ = Task.Run(() => UpdateLoopAsync(_cts.Token));

        _timer.Start();
        await TickAsync();
    }

    private async Task TickAsync()
    {
        if (_api is null) return;
        if (!_loggedIn)
        {
            try { await _api.LoginAsync(_cfg.ApiUser, _cfg.ApiPassword, _cts.Token); _loggedIn = true; }
            catch { SetPill(false); return; }
        }
        try
        {
            var all = await _api.ListDevicesAsync(_cts.Token);
            _devices = all.Where(d => d.DeviceId != _cfg.DeviceId).ToList();
            int sel = _list.SelectedIndex;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var _ in _devices) _list.Items.Add(string.Empty);
            if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndex = sel;
            _list.EndUpdate();
            SetPill(true);
        }
        catch { _loggedIn = false; SetPill(false); }
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
            if (!allow) { relay.Dispose(); return; }
        }

        var host = new SessionHost();
        host.ClipboardReceived += t => _clip?.ApplyRemoteText(t);
        host.FileReceived += p => ShowHint($"Archivo recibido: {Path.GetFileName(p)}");
        var session = new ActiveSession(host, ticket.Peer);
        lock (_hosts) _hosts.Add(session);
        UpdateSessionsUi();
        try { await host.RunAsync(relay.Stream, _cts.Token); }
        catch { }
        finally { lock (_hosts) _hosts.Remove(session); UpdateSessionsUi(); relay.Dispose(); }
    }

    private void UpdateSessionsUi()
    {
        void Apply()
        {
            List<string> peers;
            lock (_hosts) peers = _hosts.Select(s => string.IsNullOrWhiteSpace(s.Peer) ? "remoto" : s.Peer).ToList();
            if (peers.Count == 0)
            {
                _sessionLabel.Text = "Nadie está controlando este equipo";
                _sessionLabel.ForeColor = SubText;
                _sendFileBtn.Enabled = false;
            }
            else
            {
                _sessionLabel.Text = $"Controlado por: {string.Join(", ", peers)}";
                _sessionLabel.ForeColor = _online;
                _sendFileBtn.Enabled = true;
            }
        }
        if (IsHandleCreated) BeginInvoke(Apply); else Apply();
    }

    private async Task SendFileToRemotesAsync()
    {
        List<SessionHost> targets;
        lock (_hosts) targets = _hosts.Select(s => s.Host).ToList();
        if (targets.Count == 0) return;

        using var dlg = new OpenFileDialog { Title = "Enviar archivo a los equipos remotos conectados", Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        foreach (var f in dlg.FileNames)
            foreach (var h in targets)
            {
                try { await h.SendFileAsync(f, _cts.Token); } catch { }
            }
        ShowHint($"Enviado: {string.Join(", ", dlg.FileNames.Select(Path.GetFileName))}");
    }

    private void ShowHint(string text)
    {
        if (IsHandleCreated) BeginInvoke(() => _hint.Text = text);
    }

    // --- Autoactualización ---
    //
    // Al conectar, consulta la versión del servidor; si es más nueva, descarga (respetando el
    // rollout escalonado del servidor) y se reinstala. El jitter inicial y la semilla por
    // dispositivo reparten las consultas/descargas en el tiempo para no saturar al servidor.
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        if (_api is null) return;
        int seed = _cfg.DeviceId.GetHashCode();
        var rnd = new Random(seed);
        try { await Task.Delay(TimeSpan.FromSeconds(15 + rnd.Next(0, 90)), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan nextWait = TimeSpan.FromHours(6);
            try
            {
                var upd = new UpdateClient(_api, "windows");
                var m = await upd.CheckAsync(ct);
                if (m is { UpdateAvailable: true } && !string.IsNullOrEmpty(m.Sha256))
                {
                    ShowHint($"Actualización disponible (v{m.LatestVersion}). Descargando…");
                    var dir = Path.Combine(Path.GetTempPath(), "rsoc_update");
                    Directory.CreateDirectory(dir);
                    var dest = Path.Combine(dir, m.FileName);
                    if (await upd.DownloadWithRolloutAsync(m, dest, seed, ct: ct))
                    {
                        ShowHint($"Instalando actualización v{m.LatestVersion}…");
                        WindowsUpdater.InstallAndRestart(dest);
                        if (IsHandleCreated) BeginInvoke(() => { _reallyExit = true; Close(); });
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { nextWait = TimeSpan.FromMinutes(30); } // servidor caído u otro fallo: reintenta antes

            try { await Task.Delay(nextWait, ct); } catch { return; }
        }
    }

    // --- Bandeja del sistema, confirmación de acceso y arranque con Windows ---

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
            Text = $"RSoc — {_cfg.Alias}",
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
        // La X minimiza a la bandeja; se sale de verdad desde el menú "Salir".
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
        t.Tick += (_, _) => { t.Stop(); f.DialogResult = DialogResult.No; f.Close(); }; // auto-deniega a los 30 s
        // La petición debe verse SIEMPRE en este equipo (el controlado): lo traemos al frente
        // aunque la app esté minimizada en la bandeja, y avisamos con sonido.
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

    private async Task ConnectSelectedAsync()
    {
        if (_api is null) return;
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _devices.Count) return;
        var target = _devices[i];

        var pwd = InputBox.Show($"Password para «{target.Alias}»", "Contraseña de conexión:", _cfg.ConnectionPassword);
        if (pwd is null) return;

        try
        {
            var ticket = await _api.CreateSessionAsync(_cfg.DeviceId, target.DeviceId, pwd, _cts.Token);
            var relay = await RelayConnection.OpenAsync(ticket, _cts.Token);
            new ViewerForm(relay, target.Alias, _cfg.AcceptSelfSignedCerts).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo conectar: {ex.Message}", "RSoc", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
