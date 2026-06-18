using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.Android;

/// <summary>
/// Visor/control de una sesión remota en Android, pensado para uso táctil cómodo:
/// cursor visible (arrastrar para mover, tocar para clic, doble toque = doble clic),
/// zoom con dos dedos (pinch) y paneo a dos dedos, más una barra con clic derecho, rueda y
/// reset de zoom. Distingue cierre local ("la sesión fue cerrada") de cierre remoto
/// ("la sesión se canceló").
/// </summary>
[Activity(Label = "RSoc — Visor",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize)]
public class ViewerActivity : Activity
{
    private RemoteView _remote = null!;
    private TextView _state = null!;
    private TextView _speed = null!;
    private Button _monitorBtn = null!;
    private int _monitorCount = 1;
    private int _currentMonitor;
    private int _quality = 60;
    private bool _grayscale;
    private long _lastBytes;
    private int _lastFrames;
    private System.Threading.Timer? _statsTimer;
    private static readonly (string Label, int Q)[] QualityLevels =
        [("Alta", 85), ("Media", 60), ("Baja", 35), ("Mínima", 15)];
    private AndroidSessionViewer _viewer = null!;
    private readonly CancellationTokenSource _cts = new();
    private bool _closing;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        Window?.SetStatusBarColor(AndroidTheme.Accent(this));

        var alias = Intent?.GetStringExtra("alias") ?? "Equipo remoto";
        _viewer = new AndroidSessionViewer
        {
            AcceptSelfSigned = Intent?.GetBooleanExtra("acceptSelfSigned", true) ?? true,
        };
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Argb(255, 24, 26, 32));

        // Barra superior.
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetBackgroundColor(AndroidTheme.Accent(this));
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(8), Dp(8), Dp(12), Dp(8));

        var close = new Button(this) { Text = "✕  Cerrar" };
        close.SetAllCaps(false); close.SetTextColor(Color.White); close.Background = null;
        close.Click += (_, _) =>
        {
            _closing = true;
            Toast.MakeText(this, "Se cerró la sesión", ToastLength.Short)?.Show();
            Finish();
        };
        bar.AddView(close);

        var title = new TextView(this) { Text = alias, TextSize = 16 };
        title.SetTextColor(Color.White); title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        title.SetSingleLine(true);                       // que no se apile en vertical al estrechar
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        var tlp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(8) };
        title.LayoutParameters = tlp;
        bar.AddView(title);

        _monitorBtn = new Button(this) { Text = "🖥", Visibility = ViewStates.Gone };
        _monitorBtn.SetAllCaps(false); _monitorBtn.SetTextColor(Color.White); _monitorBtn.Background = null;
        _monitorBtn.Click += (_, _) => ShowMonitorPicker();
        bar.AddView(_monitorBtn);

        var qualityBtn = new Button(this) { Text = "🎚" };
        qualityBtn.SetAllCaps(false); qualityBtn.SetTextColor(Color.White); qualityBtn.Background = null;
        qualityBtn.Click += (_, _) => ShowQualityPicker();
        bar.AddView(qualityBtn);

        var fileBtn = new Button(this) { Text = "📎" };
        fileBtn.SetAllCaps(false); fileBtn.SetTextColor(Color.White); fileBtn.Background = null;
        fileBtn.Click += (_, _) => PickFile();
        bar.AddView(fileBtn);

        _state = new TextView(this) { Text = "● Conectando…", TextSize = 12 };
        _state.SetTextColor(Color.Argb(255, 210, 224, 255));
        _state.SetSingleLine(true);
        bar.AddView(_state);

        _speed = new TextView(this) { Text = "", TextSize = 11 };
        _speed.SetTextColor(Color.Argb(255, 210, 224, 255));
        _speed.SetPadding(Dp(8), 0, 0, 0);
        _speed.SetSingleLine(true);
        bar.AddView(_speed);
        root.AddView(bar);

        // Área de control: RemoteView a pantalla completa, todo por gestos.
        _remote = new RemoteView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f),
        };
        _remote.Move += (nx, ny) => Send(new InputMessage(0, nx, ny, 0, 0, 0));
        _remote.ButtonEvent += (code, down) => Send(new InputMessage(1, 0, 0, code, down ? 1 : 0, 0));
        _remote.Wheel += d => Send(new InputMessage(3, 0, 0, 0, 0, d));
        root.AddView(_remote);

        SetContentView(root);

        _viewer.DownloadDir = System.IO.Path.Combine(GetExternalFilesDir(null)!.AbsolutePath, "RSoc");
        _viewer.FileReceived += p => RunOnUiThread(() =>
            Toast.MakeText(this, $"Archivo recibido: {System.IO.Path.GetFileName(p)}", ToastLength.Long)?.Show());

        _viewer.MonitorInfoReceived += (count, current) => RunOnUiThread(() =>
        {
            _monitorCount = Math.Max(1, count);
            _currentMonitor = Math.Clamp(current, 0, _monitorCount - 1);
            _monitorBtn.Text = _monitorCount > 1 ? $"🖥 {_currentMonitor + 1}/{_monitorCount}" : "🖥";
            _monitorBtn.Visibility = _monitorCount > 1 ? ViewStates.Visible : ViewStates.Gone;
        });

        _viewer.FrameReady += bmp => RunOnUiThread(() =>
        {
            _remote.SetFrame(bmp, bmp.Width, bmp.Height);
            if (_state.Text != "● Conectado")
            {
                _state.Text = "● Conectado";
                _state.SetTextColor(Color.Argb(255, 190, 255, 214));
                Toast.MakeText(this,
                    "Arrastra: cursor · Toca: clic · Mantén: arrastrar · 2 dedos: clic der / scroll · Pellizco: zoom",
                    ToastLength.Long)?.Show();
            }
        });

        _statsTimer = new System.Threading.Timer(_ => RunOnUiThread(UpdateSpeed), null, 1000, 1000);

        _ = RunAsync(TicketFromIntent());
    }

    private void UpdateSpeed()
    {
        long bytes = _viewer.TotalEncodedBytes;
        int frames = _viewer.TotalFrames;
        double kbps = (bytes - _lastBytes) / 1024.0;
        int fps = frames - _lastFrames;
        _lastBytes = bytes;
        _lastFrames = frames;
        var c = fps >= 10 ? Color.Argb(255, 190, 255, 214)
              : fps >= 4 ? Color.Argb(255, 255, 226, 184)
              : Color.Argb(255, 255, 196, 196);
        _speed.SetTextColor(c);
        _speed.Text = $"⬇ {kbps:F0} KB/s · {fps} fps";
    }

    private void ShowQualityPicker()
    {
        var names = new string[QualityLevels.Length + 1];
        for (int i = 0; i < QualityLevels.Length; i++)
            names[i] = $"{QualityLevels[i].Label} ({QualityLevels[i].Q})";
        names[^1] = _grayscale ? "Blanco y negro ✓" : "Blanco y negro";
        new AlertDialog.Builder(this)
            .SetTitle("Calidad de imagen")!
            .SetItems(names, (_, e) =>
            {
                if (e.Which < QualityLevels.Length) _quality = QualityLevels[e.Which].Q;
                else _grayscale = !_grayscale;
                _ = _viewer.SendSetQualityAsync(_quality, _grayscale, _cts.Token);
            })!
            .SetPositiveButton("Cerrar", (_, _) => { })!
            .Show();
    }

    private int Dp(float dp) => AndroidTheme.Dp(this, dp);
    private void Send(InputMessage m) => _ = _viewer.SendInputAsync(m, _cts.Token);

    private void ShowMonitorPicker()
    {
        if (_monitorCount <= 1) return;
        var names = new string[_monitorCount];
        for (int i = 0; i < _monitorCount; i++) names[i] = $"Pantalla {i + 1}";
        new AlertDialog.Builder(this)
            .SetTitle("Elegir pantalla")!
            .SetSingleChoiceItems(names, _currentMonitor, (_, e) =>
            {
                _ = _viewer.SendSelectMonitorAsync(e.Which, _cts.Token);
            })!
            .SetPositiveButton("Cerrar", (_, _) => { })!
            .Show();
    }

    private const int PickFileRequest = 4711;

    private void PickFile()
    {
        var i = new global::Android.Content.Intent(global::Android.Content.Intent.ActionOpenDocument);
        i.AddCategory(global::Android.Content.Intent.CategoryOpenable!);
        i.SetType("*/*");
        StartActivityForResult(i, PickFileRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PickFileRequest && resultCode == Result.Ok && data?.Data is { } uri)
            _ = SendUriAsync(uri);
    }

    private async Task SendUriAsync(global::Android.Net.Uri uri)
    {
        try
        {
            var name = QueryDisplayName(uri) ?? "archivo";
            var tmp = System.IO.Path.Combine(CacheDir!.AbsolutePath, name);
            using (var input = ContentResolver!.OpenInputStream(uri))
            using (var outp = System.IO.File.Create(tmp))
                if (input is not null) await input.CopyToAsync(outp);

            RunOnUiThread(() => Toast.MakeText(this, $"Enviando {name}…", ToastLength.Short)?.Show());
            await _viewer.SendFileAsync(tmp);
            RunOnUiThread(() => Toast.MakeText(this, $"Enviado: {name}", ToastLength.Short)?.Show());
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => Toast.MakeText(this, $"Error envío: {ex.Message}", ToastLength.Long)?.Show());
        }
    }

    private string? QueryDisplayName(global::Android.Net.Uri uri)
    {
        using var c = ContentResolver!.Query(uri, null, null, null, null);
        if (c is not null && c.MoveToFirst())
        {
            int idx = c.GetColumnIndex("_display_name"); // OpenableColumns.DISPLAY_NAME
            if (idx >= 0) return c.GetString(idx);
        }
        return null;
    }

    private async Task RunAsync(SessionTicket ticket)
    {
        try
        {
            using var relay = await RelayConnection.OpenAsync(ticket, _cts.Token);
            await _viewer.RunAsync(relay.Stream, _cts.Token);
            if (!_closing) Ended("● La sesión se canceló (remoto)");
        }
        catch (System.OperationCanceledException) { /* cierre local */ }
        catch (Exception) when (_closing) { /* cierre local en curso */ }
        catch (Exception)
        {
            if (!_closing) Ended("● La sesión se canceló (remoto)");
        }
    }

    private void Ended(string text) => RunOnUiThread(() =>
    {
        _state.Text = text;
        _state.SetTextColor(Color.Argb(255, 255, 196, 196));
        Toast.MakeText(this, text.TrimStart('●', ' '), ToastLength.Long)?.Show();
    });

    private SessionTicket TicketFromIntent()
    {
        var i = Intent!;
        return new SessionTicket(
            SessionId: "",
            RelayHost: i.GetStringExtra("relayHost") ?? "127.0.0.1",
            RelayPort: i.GetIntExtra("relayPort", 21117),
            RelayTokenHex: i.GetStringExtra("relayToken") ?? "",
            Role: i.GetStringExtra("role") ?? "Controller");
    }

    protected override void OnDestroy()
    {
        _closing = true;
        _statsTimer?.Dispose();
        _cts.Cancel();
        base.OnDestroy();
    }
}
