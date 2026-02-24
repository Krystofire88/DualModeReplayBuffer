using System.Windows.Media;

namespace DRB.App.UI;

public static class Theme
{
    public static bool IsDark { get; private set; } = true;

    // ── Resolved colors (read these everywhere in UI code) ───────
    public static System.Windows.Media.Color Deep      => IsDark ? _dd.Deep      : _dl.Deep;
    public static System.Windows.Media.Color Surface   => IsDark ? _dd.Surface   : _dl.Surface;
    public static System.Windows.Media.Color Card      => IsDark ? _dd.Card      : _dl.Card;
    public static System.Windows.Media.Color Hover     => IsDark ? _dd.Hover     : _dl.Hover;
    public static System.Windows.Media.Color Blue      => IsDark ? _dd.Blue      : _dl.Blue;
    public static System.Windows.Media.Color BlueDim   => IsDark ? _dd.BlueDim   : _dl.BlueDim;
    public static System.Windows.Media.Color Green     => IsDark ? _dd.Green     : _dl.Green;
    public static System.Windows.Media.Color Red       => IsDark ? _dd.Red       : _dl.Red;
    public static System.Windows.Media.Color TextPrimary   => IsDark ? _dd.TextPrimary   : _dl.TextPrimary;
    public static System.Windows.Media.Color TextSecondary => IsDark ? _dd.TextSecondary : _dl.TextSecondary;
    public static System.Windows.Media.Color TextMuted     => IsDark ? _dd.TextMuted     : _dl.TextMuted;
    public static System.Windows.Media.Color TextBlue      => IsDark ? _dd.TextBlue      : _dl.TextBlue;

    // ── Corner radii (same in both modes) ────────────────────────
    public const double RLg = 12;
    public const double RMd = 7;
    public const double RSm = 4;

    // ── Palette sets ─────────────────────────────────────────────
    private static readonly Palette _dd = new Palette
    {
        Deep            = System.Windows.Media.Color.FromRgb(15,  15,  15 ),
        Surface         = System.Windows.Media.Color.FromRgb(24,  24,  24 ),
        Card            = System.Windows.Media.Color.FromRgb(34,  34,  34 ),
        Hover           = System.Windows.Media.Color.FromRgb(44,  44,  44 ),
        Blue            = System.Windows.Media.Color.FromRgb(68,  108, 205),
        BlueDim         = System.Windows.Media.Color.FromRgb(50,  80,  160),
        Green           = System.Windows.Media.Color.FromRgb(65,  185, 105),
        Red             = System.Windows.Media.Color.FromRgb(195, 65,  65 ),
        TextPrimary     = System.Windows.Media.Color.FromRgb(225, 225, 225),
        TextSecondary   = System.Windows.Media.Color.FromRgb(145, 145, 145),
        TextMuted       = System.Windows.Media.Color.FromRgb(80,  80,  80 ),
        TextBlue        = System.Windows.Media.Color.FromRgb(95,  155, 225),
    };

    private static readonly Palette _dl = new Palette
    {
        Deep            = System.Windows.Media.Color.FromRgb(240, 240, 242),
        Surface         = System.Windows.Media.Color.FromRgb(250, 250, 252),
        Card            = System.Windows.Media.Color.FromRgb(225, 225, 230),
        Hover           = System.Windows.Media.Color.FromRgb(210, 210, 215),
        Blue            = System.Windows.Media.Color.FromRgb(45,  90,  190),
        BlueDim         = System.Windows.Media.Color.FromRgb(30,  65,  150),
        Green           = System.Windows.Media.Color.FromRgb(40,  155, 80 ),
        Red             = System.Windows.Media.Color.FromRgb(185, 45,  45 ),
        TextPrimary     = System.Windows.Media.Color.FromRgb(20,  20,  25 ),
        TextSecondary   = System.Windows.Media.Color.FromRgb(90,  90,  100),
        TextMuted       = System.Windows.Media.Color.FromRgb(160, 160, 165),
        TextBlue        = System.Windows.Media.Color.FromRgb(40,  80,  180),
    };

    // ── Event fired when theme changes ───────────────────────────
    public static event Action? ThemeChanged;

    public static void Apply(bool isDark)
    {
        IsDark = isDark;
        ThemeChanged?.Invoke();
    }

    // ── Helpers ──────────────────────────────────────────────────
    public static SolidColorBrush Brush(System.Windows.Media.Color c)
        => new(c);
    public static SolidColorBrush Brush(System.Windows.Media.Color c, byte alpha)
        => new(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    public static System.Windows.CornerRadius CR(double r)   => new(r);
    public static System.Windows.CornerRadius CRTop(double r) => new(r, r, 0, 0);
    public static System.Windows.CornerRadius CRBottom(double r) => new(0, 0, r, r);

    // Subtle overlay tint — adjusts alpha by mode
    // Dark mode: white tint. Light mode: black tint.
    public static System.Windows.Media.Color Tint(byte alpha)
        => IsDark
            ? System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(alpha, 0,   0,   0  );

    private class Palette
    {
        public System.Windows.Media.Color Deep, Surface, Card, Hover;
        public System.Windows.Media.Color Blue, BlueDim, Green, Red;
        public System.Windows.Media.Color TextPrimary, TextSecondary, TextMuted, TextBlue;
    }
}
