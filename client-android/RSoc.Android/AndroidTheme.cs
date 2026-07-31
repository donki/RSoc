using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace RSoc.Android;

/// <summary>
/// Paleta y helpers de UI que respetan el modo claro/oscuro del sistema (DayNight).
/// Mantiene el acento indigo sOCratic coherente con el resto de la familia de apps.
/// </summary>
internal static class AndroidTheme
{
    public static bool IsDark(Context c) =>
        ((UiMode)((int)c.Resources!.Configuration!.UiMode & (int)UiMode.NightMask)) == UiMode.NightYes;

    // Paleta sOCratic INDIGO: Primary #3525CD, PrimaryDark #2A1CB8, PrimaryLight #635BF2.
    public static Color Accent(Context c) => IsDark(c) ? Color.Argb(255, 99, 91, 242) : Color.Argb(255, 53, 37, 205);
    public static Color Bg(Context c) => IsDark(c) ? Color.Argb(255, 20, 19, 24) : Color.Argb(255, 248, 249, 250);
    public static Color Surface(Context c) => IsDark(c) ? Color.Argb(255, 32, 31, 39) : Color.White;
    public static Color Text(Context c) => IsDark(c) ? Color.Argb(255, 230, 225, 233) : Color.Argb(255, 25, 28, 29);
    public static Color SubText(Context c) => IsDark(c) ? Color.Argb(255, 199, 196, 216) : Color.Argb(255, 70, 69, 85);
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
