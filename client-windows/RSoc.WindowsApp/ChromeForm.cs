using Microsoft.Win32;

namespace RSoc.WindowsApp;

/// <summary>
/// Ventana base con cromo ESTÁNDAR de Windows: barra de título nativa del sistema (minimizar /
/// maximizar / cerrar, arrastre y redimensionado los gestiona el sistema operativo). Bajo el
/// título nativo se coloca una franja índigo (cabecera/acento de la familia sOCratic) donde las
/// ventanas derivadas añaden botones de acción y etiquetas de estado con
/// <see cref="AddCaptionButton"/> / <see cref="AddCaptionStatus"/>. Paleta índigo unificada.
/// </summary>
public class ChromeForm : Form
{
    // Paleta índigo sOCratic (uso comercial).
    protected static readonly Color Primary = Color.FromArgb(0x35, 0x25, 0xCD);      // #3525CD
    protected static readonly Color PrimaryDark = Color.FromArgb(0x2A, 0x1C, 0xB8);  // #2A1CB8
    protected static readonly Color PrimaryLight = Color.FromArgb(0x63, 0x5B, 0xF2); // #635BF2
    protected static readonly Color Accent = Primary;                               // franja/botones primarios
    protected static readonly Color Danger = Color.FromArgb(0xBA, 0x1A, 0x1A);       // #BA1A1A
    protected static readonly Color Success = Color.FromArgb(0x27, 0xAE, 0x60);      // #27AE60

    public bool IsDark { get; } = SystemUsesDarkMode();
    protected Color SurfaceBg, SurfaceText, SubText, CardBg, Divider;

    // Franja índigo de cabecera (NO es la barra de título; el título lo dibuja el sistema).
    protected readonly Panel CaptionBar = new() { Dock = DockStyle.Top, Height = 40, BackColor = Accent };
    private readonly FlowLayoutPanel _buttons = new();

    private bool _fullscreen;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;
    private FormBorderStyle _restoreBorder;
    private readonly Button _btnExitFull;

    public ChromeForm()
    {
        // Cromo estándar del sistema: barra de título nativa, botones de sistema y borde redimensionable.
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5f);
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { }

        // Paleta índigo unificada (superficies claras de la familia sOCratic).
        SurfaceBg = Color.FromArgb(0xF8, 0xF9, 0xFA);   // #F8F9FA
        SurfaceText = Color.FromArgb(0x19, 0x1C, 0x1D); // #191C1D
        SubText = Color.FromArgb(0x46, 0x45, 0x55);     // #464555
        CardBg = Color.White;                           // #FFFFFF
        Divider = Color.FromArgb(0xE8, 0xEB, 0xF0);
        BackColor = SurfaceBg;
        ForeColor = SurfaceText;

        _buttons.Dock = DockStyle.Right;
        _buttons.FlowDirection = FlowDirection.LeftToRight;
        _buttons.WrapContents = false;
        _buttons.AutoSize = true;
        _buttons.Margin = Padding.Empty;
        _buttons.Padding = Padding.Empty;
        _buttons.BackColor = Accent;

        CaptionBar.Controls.Add(_buttons);

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

    /// <summary>Título mostrado en la barra de título NATIVA del sistema.</summary>
    protected string TitleText { set => Text = value; }

    /// <summary>Añade un botón de acción a la franja índigo, a la izquierda de los demás.</summary>
    protected Button AddCaptionButton(string text, EventHandler onClick)
    {
        var b = CaptionButton(text, wide: true);
        b.Click += onClick;
        _buttons.Controls.Add(b);
        _buttons.Controls.SetChildIndex(b, 0);
        return b;
    }

    /// <summary>Añade una etiqueta de estado a la franja índigo (a la izquierda de los botones).</summary>
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

    // Pantalla completa: se oculta la franja y el borde del sistema, ocupando todo el monitor.
    protected void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreBounds = Bounds;
            _restoreState = WindowState;
            _restoreBorder = FormBorderStyle;
            _fullscreen = true;
            CaptionBar.Visible = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromHandle(Handle).Bounds;
            _btnExitFull.Visible = true;
            _btnExitFull.BringToFront();
            PositionExitFull();
        }
        else
        {
            _fullscreen = false;
            _btnExitFull.Visible = false;
            FormBorderStyle = _restoreBorder;
            CaptionBar.Visible = true;
            WindowState = _restoreState;
            if (_restoreState == FormWindowState.Normal) Bounds = _restoreBounds;
        }
    }

    private void PositionExitFull()
    {
        if (!_btnExitFull.Visible) return;
        _btnExitFull.Location = new Point(ClientSize.Width - _btnExitFull.Width - 16, 12);
    }

    // Botón de acción de la franja índigo (texto blanco sobre índigo, sin borde, hover más claro).
    private Button CaptionButton(string text, bool wide = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Size = new Size(46, 40),
            Font = new Font("Segoe UI", 11f),
            TabStop = false,
            Margin = Padding.Empty,
        };
        if (wide) b.AutoSize = true;
        b.FlatAppearance.BorderSize = 0;
        var hover = ControlPaint.Light(Accent, 0.15f);
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(hover, 0.05f);
        return b;
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
