using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace RSoc.Android;

/// <summary>
/// Paleta y helpers de UI que respetan el modo claro/oscuro del sistema (DayNight).
/// Mantiene un acento azul coherente con el cliente Windows.
/// </summary>
internal static class AndroidTheme
{
    public static bool IsDark(Context c) =>
        ((UiMode)((int)c.Resources!.Configuration!.UiMode & (int)UiMode.NightMask)) == UiMode.NightYes;

    public static Color Accent(Context c) => IsDark(c) ? Color.Argb(255, 59, 130, 246) : Color.Argb(255, 37, 99, 235);
    public static Color Bg(Context c) => IsDark(c) ? Color.Argb(255, 18, 19, 22) : Color.Argb(255, 246, 248, 251);
    public static Color Surface(Context c) => IsDark(c) ? Color.Argb(255, 30, 31, 34) : Color.White;
    public static Color Text(Context c) => IsDark(c) ? Color.Argb(255, 232, 232, 232) : Color.Argb(255, 28, 31, 38);
    public static Color SubText(Context c) => IsDark(c) ? Color.Argb(255, 154, 160, 166) : Color.Argb(255, 107, 114, 128);
    public static Color Divider(Context c) => IsDark(c) ? Color.Argb(255, 44, 46, 51) : Color.Argb(255, 230, 232, 236);

    public static int Dp(Context c, float dp) => (int)(dp * c.Resources!.DisplayMetrics!.Density + 0.5f);

    /// <summary>Drawable redondeado con color de relleno (botones/tarjetas).</summary>
    public static GradientDrawable Rounded(Color fill, int radiusPx)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Rectangle);
        d.SetColor(fill);
        d.SetCornerRadius(radiusPx);
        return d;
    }

    public static Button AccentButton(Context c, string text)
    {
        var b = new Button(c) { Text = text };
        b.SetTextColor(Color.White);
        b.SetAllCaps(false);
        b.Background = Rounded(Accent(c), Dp(c, 10));
        b.SetPadding(Dp(c, 16), Dp(c, 12), Dp(c, 16), Dp(c, 12));
        return b;
    }
}
