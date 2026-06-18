using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.Android;

/// <summary>
/// Pantalla principal del cliente Android (controlador): muestra directamente la lista de
/// equipos online. La configuración (servidor, credenciales, contraseña de conexión) se edita
/// desde el menú ⋮ y se guarda en SharedPreferences. Look profesional con modo claro/oscuro.
/// </summary>
[Activity(Label = "RSoc", MainLauncher = true)]
public class MainActivity : Activity
{
    private TextView _status = null!;
    private TextView _empty = null!;
    private ListView _list = null!;

    private AndroidConfig _cfg = null!;
    private RSocApiClient? _api;
    private List<DeviceInfo> _devices = [];
    private readonly string _deviceId = "ANDROID-" + (Build.Model?.Replace(' ', '-') ?? "device");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        Window?.SetStatusBarColor(AndroidTheme.Accent(this));
        _cfg = AndroidConfig.Load(this);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(AndroidTheme.Bg(this));

        // App bar: título + menú ⋮
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetBackgroundColor(AndroidTheme.Accent(this));
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 12), AndroidTheme.Dp(this, 8), AndroidTheme.Dp(this, 12));

        var brand = new TextView(this) { Text = "RSoc", TextSize = 20 };
        brand.SetTextColor(Color.White);
        brand.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        brand.SetSingleLine(true);
        brand.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        brand.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        bar.AddView(brand);

        var menu = new Button(this) { Text = "⋮", TextSize = 22 };
        menu.SetTextColor(Color.White);
        menu.Background = null;
        menu.SetMinimumWidth(AndroidTheme.Dp(this, 44));
        menu.Click += (_, _) => ShowMenu(menu);
        bar.AddView(menu);
        root.AddView(bar);

        _status = new TextView(this) { Text = "Conectando…" };
        _status.SetTextColor(AndroidTheme.SubText(this));
        _status.SetPadding(AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 12), AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 4));
        root.AddView(_status);

        var section = new TextView(this) { Text = "EQUIPOS DISPONIBLES", TextSize = 12 };
        section.SetTextColor(AndroidTheme.SubText(this));
        section.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        section.SetPadding(AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 4), AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 4));
        root.AddView(section);

        _empty = new TextView(this) { Text = "Sin equipos. Abre ⋮ → Configuración para ajustar el servidor.", Visibility = ViewStates.Gone };
        _empty.SetTextColor(AndroidTheme.SubText(this));
        _empty.SetPadding(AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 16), 0);
        root.AddView(_empty);

        _list = new ListView(this);
        _list.SetBackgroundColor(AndroidTheme.Surface(this));
        _list.Divider = new global::Android.Graphics.Drawables.ColorDrawable(AndroidTheme.Divider(this));
        _list.DividerHeight = AndroidTheme.Dp(this, 1);
        _list.ItemClick += OnDeviceClick;
        _list.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        root.AddView(_list);

        SetContentView(root);

        _ = RefreshAsync();
        _ = CheckForUpdateAsync();
    }

    // --- Autoactualización ---
    //
    // Al arrancar, consulta la versión del servidor por HTTPS; si es más nueva, descarga el APK
    // (también por HTTPS, aceptando el cert autofirmado), respetando el rollout escalonado y
    // verificando el SHA-256, y lo ofrece a instalar por el sistema (el usuario confirma).
    private async Task CheckForUpdateAsync()
    {
        try
        {
            int seed = _deviceId.GetHashCode();
            var rnd = new Random(seed);
            await Task.Delay(TimeSpan.FromSeconds(10 + rnd.Next(0, 60)));

            using var http = RSocHttp.Create(_cfg.Server, _cfg.AcceptSelfSignedCerts);
            var upd = new UpdateClient(new RSocApiClient(http), "android");

            var m = await upd.CheckAsync();
            if (m is null || !m.UpdateAvailable || string.IsNullOrEmpty(m.Sha256)) return;

            var dir = new Java.IO.File(GetExternalFilesDir(null), "updates");
            dir.Mkdirs();
            var dest = new Java.IO.File(dir, "RSoc-update.apk").AbsolutePath;

            RunOnUiThread(() =>
                Toast.MakeText(this, $"Descargando actualización v{m.LatestVersion}…", ToastLength.Short)?.Show());

            // DownloadWithRolloutAsync espera ranura (con jitter) y verifica el SHA-256.
            if (await upd.DownloadWithRolloutAsync(m, dest, seed))
                RunOnUiThread(() => InstallApk(dest));
        }
        catch { /* silencioso: se reintenta en el próximo arranque */ }
    }

    private void InstallApk(string path)
    {
        try
        {
            var file = new Java.IO.File(path);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, $"{PackageName}.fileprovider", file);
            var install = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            install.SetDataAndType(uri, "application/vnd.android.package-archive");
            install.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission
                             | global::Android.Content.ActivityFlags.NewTask);
            StartActivity(install);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, $"No se pudo instalar la actualización: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    private void ShowMenu(View anchor)
    {
        var pm = new PopupMenu(this, anchor);
        pm.Menu!.Add(0, 1, 0, "Configuración");
        pm.Menu.Add(0, 2, 1, "Refrescar");
        pm.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) ShowSettings();
            else if (e.Item.ItemId == 2) _ = RefreshAsync();
        };
        pm.Show();
    }

    private void ShowSettings()
    {
        var pad = AndroidTheme.Dp(this, 16);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(pad, pad, pad, 0);

        EditText Field(string label, string value)
        {
            layout.AddView(new TextView(this) { Text = label });
            var e = new EditText(this) { Text = value };
            layout.AddView(e);
            return e;
        }

        var server = Field("Servidor (https://IP:21114)", _cfg.Server);
        var user = Field("Usuario API", _cfg.ApiUser);
        var pass = Field("Contraseña API", _cfg.ApiPassword);
        var conn = Field("Contraseña de conexión", _cfg.ConnectionPassword);
        var accept = new CheckBox(this) { Text = "Aceptar certificados autofirmados", Checked = _cfg.AcceptSelfSignedCerts };
        layout.AddView(accept);

        new AlertDialog.Builder(this)
            .SetTitle("Configuración")!
            .SetView(layout)!
            .SetPositiveButton("Guardar", (_, _) =>
            {
                _cfg.Server = server.Text!;
                _cfg.ApiUser = user.Text!;
                _cfg.ApiPassword = pass.Text!;
                _cfg.ConnectionPassword = conn.Text!;
                _cfg.AcceptSelfSignedCerts = accept.Checked;
                _cfg.Save(this);
                _api = null; // forzar nuevo login
                _ = RefreshAsync();
            })!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Show();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _status.Text = "Conectando…";
            if (_api is null)
            {
                _api = new RSocApiClient(RSocHttp.Create(_cfg.Server, _cfg.AcceptSelfSignedCerts)); // HTTPS
                await _api.LoginAsync(_cfg.ApiUser, _cfg.ApiPassword);
            }
            var all = await _api.ListDevicesAsync();
            _devices = all.Where(d => d.DeviceId != _deviceId).ToList();

            RunOnUiThread(() =>
            {
                _list.Adapter = new DeviceAdapter(this, _devices);
                _empty.Visibility = _devices.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
                _status.Text = $"{_devices.Count} equipo(s) online · {_cfg.Server}";
            });
        }
        catch (Exception ex)
        {
            _api = null;
            RunOnUiThread(() => _status.Text = $"Error: {ex.Message}");
        }
    }

    private void OnDeviceClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        if (_api is null || e.Position < 0 || e.Position >= _devices.Count) return;
        var target = _devices[e.Position];

        const InputTypes hidden = InputTypes.ClassText | InputTypes.TextVariationPassword;
        const InputTypes shown = InputTypes.ClassText | InputTypes.TextVariationVisiblePassword;

        var input = new EditText(this) { Text = _cfg.ConnectionPassword, InputType = hidden };
        var see = new CheckBox(this) { Text = "Ver contraseña" };
        see.CheckedChange += (_, e) => { input.InputType = e.IsChecked ? shown : hidden; input.SetSelection(input.Text!.Length); };

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(AndroidTheme.Dp(this, 16), AndroidTheme.Dp(this, 8), AndroidTheme.Dp(this, 16), 0);
        layout.AddView(input);
        layout.AddView(see);

        new AlertDialog.Builder(this)
            .SetTitle($"Password para {target.Alias}")!
            .SetView(layout)!
            .SetPositiveButton("Conectar", async (_, _) => await ConnectAsync(target, input.Text!))!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Show();
    }

    private async Task ConnectAsync(DeviceInfo target, string pwd)
    {
        try
        {
            _status.Text = $"Abriendo sesión con {target.Alias}…";
            var ticket = await _api!.CreateSessionAsync(_deviceId, target.DeviceId, pwd);

            var intent = new global::Android.Content.Intent(this, typeof(ViewerActivity));
            intent.PutExtra("relayHost", ticket.RelayHost);
            intent.PutExtra("relayPort", ticket.RelayPort);
            intent.PutExtra("relayToken", ticket.RelayTokenHex);
            intent.PutExtra("role", ticket.Role);
            intent.PutExtra("alias", target.Alias);
            intent.PutExtra("acceptSelfSigned", _cfg.AcceptSelfSignedCerts);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => _status.Text = $"No se pudo conectar: {ex.Message}");
        }
    }

    /// <summary>Adapter de lista con dos líneas (alias + id) y colores temáticos.</summary>
    private sealed class DeviceAdapter(MainActivity ctx, List<DeviceInfo> items) : BaseAdapter<DeviceInfo>
    {
        public override DeviceInfo this[int position] => items[position];
        public override int Count => items.Count;
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var d = items[position];
            var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            row.SetBackgroundColor(AndroidTheme.Surface(ctx));
            row.SetPadding(AndroidTheme.Dp(ctx, 16), AndroidTheme.Dp(ctx, 12), AndroidTheme.Dp(ctx, 16), AndroidTheme.Dp(ctx, 12));

            var alias = new TextView(ctx) { Text = d.Alias, TextSize = 16 };
            alias.SetTextColor(AndroidTheme.Text(ctx));
            alias.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);

            var id = new TextView(ctx) { Text = d.DeviceId, TextSize = 12 };
            id.SetTextColor(AndroidTheme.SubText(ctx));

            row.AddView(alias);
            row.AddView(id);
            return row;
        }
    }
}
