using Android.Content;

namespace RSoc.Android;

/// <summary>Configuración persistente del cliente Android (SharedPreferences).</summary>
internal sealed class AndroidConfig
{
    private const string Prefs = "rsoc";

    public string Server { get; set; } = "https://10.0.2.2:21114";
    public string ApiUser { get; set; } = "";
    public string ApiPassword { get; set; } = "";
    public string ConnectionPassword { get; set; } = "";

    /// <summary>Aceptar certificados TLS autofirmados (true por defecto). Ponlo en false con CA de confianza.</summary>
    public bool AcceptSelfSignedCerts { get; set; } = true;

    public static AndroidConfig Load(Context c)
    {
        var p = c.GetSharedPreferences(Prefs, FileCreationMode.Private)!;
        return new AndroidConfig
        {
            Server = p.GetString("server", "https://10.0.2.2:21114")!,
            ApiUser = p.GetString("user", "")!,
            ApiPassword = p.GetString("pass", "")!,
            ConnectionPassword = p.GetString("conn", "")!,
            AcceptSelfSignedCerts = p.GetBoolean("acceptSelfSigned", true),
        };
    }

    public void Save(Context c)
    {
        var e = c.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!;
        e.PutString("server", Server);
        e.PutString("user", ApiUser);
        e.PutString("pass", ApiPassword);
        e.PutString("conn", ConnectionPassword);
        e.PutBoolean("acceptSelfSigned", AcceptSelfSignedCerts);
        e.Apply();
    }
}
