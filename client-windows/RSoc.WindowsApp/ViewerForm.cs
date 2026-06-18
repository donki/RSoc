using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.WindowsApp;

/// <summary>
/// Ventana de visualización/control de una sesión remota. Cromo y tema vía <see cref="ChromeForm"/>:
/// franja azul con estado, "Enviar archivo" y pantalla completa. El escritorio remoto se pinta
/// en un lienzo oscuro (manteniendo proporción) y el teclado/ratón se reenvían al agente.
/// </summary>
public sealed class ViewerForm : ChromeForm
{
    private static readonly Color CanvasBg = Color.FromArgb(24, 26, 32);

    private readonly RelayConnection _relay;
    private readonly SessionViewer _viewer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Canvas _canvas;
    private readonly Label _state;
    private ClipboardBridge? _clip;

    private Bitmap? _bitmap;
    private int _remoteW, _remoteH;
    private bool _closing; // true = cierre iniciado localmente

    private Button _monitorBtn = null!;
    private int _monitorCount = 1;
    private int _currentMonitor;

    private Button _qualityBtn = null!;
    private int _quality = 60;          // calidad actual (1..100)
    private bool _grayscale;            // blanco y negro
    private static readonly (string Label, int Q)[] QualityLevels =
        [("Alta", 85), ("Media", 60), ("Baja", 35), ("Mínima", 15)];

    private Label _net = null!;          // indicador de red (KB/s y fps)
    private readonly System.Windows.Forms.Timer _netTimer = new() { Interval = 1000 };
    private long _lastBytes;
    private int _lastFrames;

    public ViewerForm(RelayConnection relay, string title, bool acceptSelfSigned = true)
    {
        _relay = relay;
        _viewer = new SessionViewer { AcceptSelfSigned = acceptSelfSigned };
        TitleText = $"RSoc — {title}";
        Width = 1180;
        Height = 760;

        _state = AddCaptionStatus();
        SetState("● Conectando…", Color.FromArgb(210, 224, 255));
        _net = AddCaptionStatus();
        _net.ForeColor = Color.FromArgb(210, 224, 255);
        AddCaptionButton("Enviar archivo…", async (_, _) => await SendFileDialogAsync());
        _qualityBtn = AddCaptionButton("Calidad ▾", (_, _) => ShowQualityMenu());
        _monitorBtn = AddCaptionButton("🖥 Pantalla", (_, _) => ShowMonitorMenu());
        _monitorBtn.Visible = false; // solo si el remoto reporta más de una pantalla
        EnableFullscreenButton();

        _netTimer.Tick += (_, _) => UpdateNetStats();

        _canvas = new Canvas(this) { Dock = DockStyle.Fill, BackColor = CanvasBg };
        Controls.Add(_canvas);

        AllowDrop = true;
        _canvas.AllowDrop = true;
        _canvas.DragEnter += (_, e) => e!.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _canvas.DragDrop += async (_, e) => await SendDroppedAsync(e!);

        _viewer.ConfigReceived += (w, h) =>
        {
            _remoteW = w; _remoteH = h;
            SetState("● Conectado", Color.FromArgb(190, 255, 214));
        };
        _viewer.FrameReceived += OnFrameReceived;
        _viewer.MonitorInfoReceived += OnMonitorInfo;
        _viewer.FileReceived += p => SetState($"● Archivo recibido: {Path.GetFileName(p)}", Color.FromArgb(190, 255, 214));

        _canvas.MouseMove += (_, e) => Send(MouseMoveMsg(e.Location));
        _canvas.MouseDown += (_, e) => Send(Button(e, down: true));
        _canvas.MouseUp += (_, e) => Send(Button(e, down: false));
        _canvas.MouseWheel += (_, e) => Send(new InputMessage((int)NativeCore.InputKind.Wheel, 0, 0, 0, 0, e.Delta));
        KeyPreview = true;
        KeyDown += (_, e) => { Send(Key(e, down: true)); e.Handled = true; };
        KeyUp += (_, e) => { Send(Key(e, down: false)); e.Handled = true; };

        _clip = new ClipboardBridge(this);
        _clip.LocalTextCopied += t => _ = _viewer.SendClipboardAsync(t, _cts.Token);
        _viewer.ClipboardReceived += t => _clip!.ApplyRemoteText(t);

        Load += (_, _) => { _netTimer.Start(); _ = RunAsync(); };
        FormClosing += (_, _) => { _closing = true; _netTimer.Stop(); _cts.Cancel(); _clip?.Dispose(); _relay.Dispose(); };
    }

    private async Task RunAsync()
    {
        // No marcamos "Conectado" hasta el primer frame de config: si el remoto tiene la
        // confirmación activada, hasta que su usuario autorice no llega vídeo. Así queda claro
        // que la autorización ocurre en el equipo controlado (destino), no aquí (origen).
        SetState("● Esperando autorización del remoto…", Color.FromArgb(255, 226, 184));
        try
        {
            await Task.Run(() => _viewer.RunAsync(_relay.Stream, _cts.Token));
            // El bucle terminó solo = el extremo remoto cerró la sesión.
            if (!_closing) SetState("● La sesión se canceló (remoto)", Color.FromArgb(255, 226, 184));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_closing)
        {
            SetState($"● La sesión se canceló (remoto): {ex.Message}", Color.FromArgb(255, 196, 196));
        }
        catch (Exception) { /* cierre local en curso */ }
    }

    private void OnMonitorInfo(int count, int current)
    {
        void Apply()
        {
            _monitorCount = Math.Max(1, count);
            _currentMonitor = Math.Clamp(current, 0, _monitorCount - 1);
            _monitorBtn.Text = $"🖥 Pantalla {_currentMonitor + 1}/{_monitorCount}";
            _monitorBtn.Visible = _monitorCount > 1;
        }
        if (IsHandleCreated) BeginInvoke(Apply); else Apply();
    }

    private void ShowMonitorMenu()
    {
        if (_monitorCount <= 1) return;
        var menu = new ContextMenuStrip();
        for (int i = 0; i < _monitorCount; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem($"Pantalla {i + 1}") { Checked = i == _currentMonitor };
            item.Click += (_, _) => _ = _viewer.SendSelectMonitorAsync(idx, _cts.Token);
            menu.Items.Add(item);
        }
        menu.Show(_monitorBtn, new Point(0, _monitorBtn.Height));
    }

    private void ShowQualityMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var (label, q) in QualityLevels)
        {
            var item = new ToolStripMenuItem($"{label} ({q})") { Checked = q == _quality };
            item.Click += (_, _) => { _quality = q; ApplyQuality(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var bw = new ToolStripMenuItem("Blanco y negro") { Checked = _grayscale };
        bw.Click += (_, _) => { _grayscale = !_grayscale; ApplyQuality(); };
        menu.Items.Add(bw);
        menu.Show(_qualityBtn, new Point(0, _qualityBtn.Height));
    }

    private void ApplyQuality()
    {
        var lvl = Array.Find(QualityLevels, x => x.Q == _quality).Label ?? _quality.ToString();
        _qualityBtn.Text = _grayscale ? $"Calidad: {lvl} B/N ▾" : $"Calidad: {lvl} ▾";
        _ = _viewer.SendSetQualityAsync(_quality, _grayscale, _cts.Token);
    }

    // Calcula KB/s y fps del último segundo y actualiza el indicador de red con color por calidad.
    private void UpdateNetStats()
    {
        long bytes = _viewer.TotalEncodedBytes;
        int frames = _viewer.TotalFrames;
        double kbps = (bytes - _lastBytes) / 1024.0;   // KB en 1 s
        int fps = frames - _lastFrames;
        _lastBytes = bytes;
        _lastFrames = frames;

        Color c = fps >= 10 ? Color.FromArgb(190, 255, 214)   // buena
                : fps >= 4 ? Color.FromArgb(255, 226, 184)     // regular
                : Color.FromArgb(255, 196, 196);               // pobre
        _net.ForeColor = c;
        _net.Text = $"⬇ {kbps:F0} KB/s · {fps} fps";
    }

    private void SetState(string text, Color color)
    {
        if (!IsHandleCreated) { _state.Text = text; _state.ForeColor = color; return; }
        BeginInvoke(() => { _state.Text = text; _state.ForeColor = color; });
    }

    private async Task SendFileDialogAsync()
    {
        using var dlg = new OpenFileDialog { Title = "Enviar archivo al equipo remoto", Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) await SendOneAsync(f);
    }

    private async Task SendDroppedAsync(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) if (File.Exists(f)) await SendOneAsync(f);
    }

    private async Task SendOneAsync(string path)
    {
        try
        {
            SetState($"● Enviando {Path.GetFileName(path)}…", Color.FromArgb(210, 224, 255));
            await _viewer.SendFileAsync(path, _cts.Token);
            SetState($"● Enviado: {Path.GetFileName(path)}", Color.FromArgb(190, 255, 214));
        }
        catch (Exception ex) { SetState($"● Error envío: {ex.Message}", Color.FromArgb(255, 196, 196)); }
    }

    private void OnFrameReceived(int w, int h, int stride, byte[] bgra)
    {
        _remoteW = w; _remoteH = h;
        lock (_lock)
        {
            if (_bitmap is null || _bitmap.Width != w || _bitmap.Height != h)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            }
            var data = _bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int rowBytes = Math.Min(stride, data.Stride);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(bgra, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
            finally { _bitmap.UnlockBits(data); }
        }
        if (IsHandleCreated) BeginInvoke(_canvas.Invalidate);
    }

    private Rectangle FitRect(Size area)
    {
        if (_remoteW <= 0 || _remoteH <= 0) return new Rectangle(Point.Empty, area);
        double scale = Math.Min((double)area.Width / _remoteW, (double)area.Height / _remoteH);
        int w = Math.Max(1, (int)(_remoteW * scale));
        int h = Math.Max(1, (int)(_remoteH * scale));
        return new Rectangle((area.Width - w) / 2, (area.Height - h) / 2, w, h);
    }

    private void PaintCanvas(Graphics g, Size area)
    {
        lock (_lock)
        {
            using var back = new SolidBrush(CanvasBg);
            g.FillRectangle(back, new Rectangle(Point.Empty, area));
            if (_bitmap is not null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(_bitmap, FitRect(area));
            }
        }
    }

    private void Send(InputMessage m) => _ = _viewer.SendInputAsync(m, _cts.Token);

    private InputMessage MouseMoveMsg(Point p)
    {
        var rect = FitRect(_canvas.ClientSize);
        int rx = Math.Clamp(p.X - rect.X, 0, Math.Max(1, rect.Width));
        int ry = Math.Clamp(p.Y - rect.Y, 0, Math.Max(1, rect.Height));
        int nx = rect.Width > 0 ? (int)((long)rx * 65535 / rect.Width) : 0;
        int ny = rect.Height > 0 ? (int)((long)ry * 65535 / rect.Height) : 0;
        return new InputMessage((int)NativeCore.InputKind.MouseMove,
            Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535), 0, 0, 0);
    }

    private static InputMessage Button(MouseEventArgs e, bool down)
    {
        int code = e.Button switch
        {
            MouseButtons.Left => 0,
            MouseButtons.Right => 1,
            MouseButtons.Middle => 2,
            _ => -1,
        };
        return new InputMessage((int)NativeCore.InputKind.MouseButton, 0, 0, code, down ? 1 : 0, 0);
    }

    private static InputMessage Key(KeyEventArgs e, bool down) =>
        new((int)NativeCore.InputKind.Key, 0, 0, e.KeyValue, down ? 1 : 0, 0);

    private sealed class Canvas : Panel
    {
        private readonly ViewerForm _owner;
        public Canvas(ViewerForm owner) { _owner = owner; DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaint(PaintEventArgs e) => _owner.PaintCanvas(e.Graphics, ClientSize);
    }
}
