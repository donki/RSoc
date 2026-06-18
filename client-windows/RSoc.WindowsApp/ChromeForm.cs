using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace RSoc.WindowsApp;

/// <summary>
/// Ventana base con cromo propio: barra de título integrada en la franja azul (botones de
/// minimizar / maximizar / cerrar, y opcionalmente pantalla completa), arrastre, redimensionado
/// y tema claro/oscuro según el sistema. Las ventanas derivadas añaden su contenido y botones
/// extra a la franja con <see cref="AddCaptionButton"/> / <see cref="AddCaptionStatus"/>.
/// </summary>
public class ChromeForm : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    protected static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public bool IsDark { get; } = SystemUsesDarkMode();
    protected Color SurfaceBg, SurfaceText, SubText, CardBg, Divider;

    protected readonly Panel CaptionBar = new() { Dock = DockStyle.Top, Height = 40, BackColor = Accent };
    private readonly Label _title = new();
    private readonly FlowLayoutPanel _buttons = new();
    private Button _btnExitFull = null!;
    private Button _btnMax = null!;

    private bool _fullscreen;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;

    public ChromeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f);
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { }

        if (IsDark)
        {
            SurfaceBg = Color.FromArgb(32, 33, 36); SurfaceText = Color.FromArgb(232, 232, 232);
            SubText = Color.FromArgb(154, 160, 166); CardBg = Color.FromArgb(44, 45, 48); Divider = Color.FromArgb(58, 60, 64);
        }
        else
        {
            SurfaceBg = Color.FromArgb(248, 249, 252); SurfaceText = Color.FromArgb(28, 31, 38);
            SubText = Color.FromArgb(120, 128, 142); CardBg = Color.White; Divider = Color.FromArgb(232, 235, 240);
        }
        BackColor = SurfaceBg;
        ForeColor = SurfaceText;

        _title.AutoSize = false;
        _title.Dock = DockStyle.Fill;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.ForeColor = Color.White;
        _title.Font = new Font("Segoe UI Semibold", 10.5f);
        _title.Padding = new Padding(12, 0, 0, 0);
        _title.MouseDown += DragCaption;
        _title.MouseDoubleClick += (_, _) => ToggleMaximize();

        _buttons.Dock = DockStyle.Right;
        _buttons.FlowDirection = FlowDirection.LeftToRight;
        _buttons.WrapContents = false;
        _buttons.AutoSize = true;
        _buttons.Margin = Padding.Empty;
        _buttons.Padding = Padding.Empty;
        _buttons.BackColor = Accent;

        var min = CaptionButton("—"); min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax = CaptionButton("□"); _btnMax.Click += (_, _) => ToggleMaximize();
        var close = CaptionButton("✕", closeButton: true); close.Click += (_, _) => Close();
        _buttons.Controls.Add(min);
        _buttons.Controls.Add(_btnMax);
        _buttons.Controls.Add(close);

        CaptionBar.Controls.Add(_title);
        CaptionBar.Controls.Add(_buttons);
        CaptionBar.MouseDown += DragCaption;
        CaptionBar.MouseDoubleClick += (_, _) => ToggleMaximize();

        // Botón flotante para salir de pantalla completa (oculto salvo en fullscreen).
        _btnExitFull = new Button
        {
            Text = "✕  Salir de pantalla completa",
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            AutoSize = true,
            Visible = false,
            Padding = new Padding(10, 6, 10, 6),
            Font = new Font("Segoe UI Semibold", 9f),
        };
        _btnExitFull.FlatAppearance.BorderSize = 0;
        _btnExitFull.Click += (_, _) => ToggleFullscreen();

        Controls.Add(CaptionBar);
        Controls.Add(_btnExitFull);

        Resize += (_, _) => PositionExitFull();
    }

    protected string TitleText { set => _title.Text = value; }

    /// <summary>Añade un botón a la franja, a la izquierda de los botones de sistema.</summary>
    protected Button AddCaptionButton(string text, EventHandler onClick)
    {
        var b = CaptionButton(text, wide: true);
        b.Click += onClick;
        _buttons.Controls.Add(b);
        _buttons.Controls.SetChildIndex(b, 0);
        return b;
    }

    /// <summary>Añade una etiqueta de estado a la franja (a la izquierda de los botones).</summary>
    protected Label AddCaptionStatus()
    {
        var l = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Margin = new Padding(8, 11, 8, 0),
        };
        _buttons.Controls.Add(l);
        _buttons.Controls.SetChildIndex(l, 0);
        return l;
    }

    /// <summary>Activa el botón de pantalla completa en la franja.</summary>
    protected void EnableFullscreenButton()
    {
        AddCaptionButton("⛶", (_, _) => ToggleFullscreen());
    }

    protected void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreBounds = Bounds;
            _restoreState = WindowState;
            _fullscreen = true;
            CaptionBar.Visible = false;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromHandle(Handle).Bounds;
            _btnExitFull.Visible = true;
            _btnExitFull.BringToFront();
            PositionExitFull();
        }
        else
        {
            _fullscreen = false;
            _btnExitFull.Visible = false;
            CaptionBar.Visible = true;
            WindowState = _restoreState;
            if (_restoreState == FormWindowState.Normal) Bounds = _restoreBounds;
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_btnMax is not null)
            _btnMax.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        PositionExitFull();
    }

    // Estilos nativos para que la barra de tareas minimice/restaure y maximice correctamente,
    // aunque la ventana no tenga marco del sistema.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00010000 | 0x00080000; // WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU
            return cp;
        }
    }

    private void PositionExitFull()
    {
        if (!_btnExitFull.Visible) return;
        _btnExitFull.Location = new Point(ClientSize.Width - _btnExitFull.Width - 16, 12);
    }

    private void DragCaption(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _fullscreen) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 0x2 /*HTCAPTION*/, 0);
    }

    private Button CaptionButton(string text, bool closeButton = false, bool wide = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Size = new Size(wide ? 46 : 46, 40),
            Font = new Font("Segoe UI", 11f),
            TabStop = false,
            Margin = Padding.Empty,
        };
        if (wide) b.AutoSize = true;
        b.FlatAppearance.BorderSize = 0;
        var hover = closeButton ? Color.FromArgb(232, 17, 35) : ControlPaint.Light(Accent, 0.15f);
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(hover, 0.05f);
        return b;
    }

    // Redimensionado por los bordes (la franja cubre el borde superior; se redimensiona por
    // izquierda/derecha/abajo y esquinas inferiores).
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int WM_GETMINMAXINFO = 0x24;

        if (m.Msg == WM_GETMINMAXINFO)
        {
            var scr = Screen.FromHandle(Handle);
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            mmi.MaxPosition = new POINT { X = scr.WorkingArea.Left - scr.Bounds.Left, Y = scr.WorkingArea.Top - scr.Bounds.Top };
            mmi.MaxSize = new POINT { X = scr.WorkingArea.Width, Y = scr.WorkingArea.Height };
            mmi.MinTrackSize = new POINT { X = MinimumSize.Width, Y = MinimumSize.Height };
            Marshal.StructureToPtr(mmi, m.LParam, true);
            return;
        }

        if (m.Msg == WM_NCHITTEST && !_fullscreen && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1 /*HTCLIENT*/)
            {
                var p = PointToClient(new Point(m.LParam.ToInt32()));
                const int g = 6;
                bool left = p.X <= g, right = p.X >= ClientSize.Width - g, bottom = p.Y >= ClientSize.Height - g, top = p.Y <= g;
                if (bottom && right) m.Result = (IntPtr)17;
                else if (bottom && left) m.Result = (IntPtr)16;
                else if (bottom) m.Result = (IntPtr)15;
                else if (right) m.Result = (IntPtr)11;
                else if (left) m.Result = (IntPtr)10;
                else if (top && right) m.Result = (IntPtr)14;
                else if (top && left) m.Result = (IntPtr)13;
            }
            return;
        }
        base.WndProc(ref m);
    }

    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
