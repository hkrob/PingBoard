using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PingBoard.App.Controls;
using Windows.UI;

namespace PingBoard.App;

/// <summary>
/// The green-phosphor terminal theme: black plate, monospaced everything, Matrix green.
/// <para>
/// <b>Why this is not a fourth theme dictionary, and not a repaint.</b> WinUI picks a dictionary by
/// <see cref="ElementTheme"/>, of which there are exactly three, so there is no slot for a fourth
/// palette. The obvious workaround — reach into the existing brushes and change their colour — does
/// not work either: brushes handed out from a <c>ResourceDictionary</c> are sealed, and assigning
/// <see cref="SolidColorBrush.Color"/> throws <see cref="UnauthorizedAccessException"/>.
/// </para>
/// <para>
/// So this supplies <em>new</em> brushes through <see cref="BoardPalette"/> instead. The two
/// consumers that resolve by key on every use — the row status colours and the sparkline — pick
/// them up on the next redraw. The handful of elements that bind a brush once in XAML are
/// reassigned explicitly by <see cref="BoardView.ApplyPalette"/>, because a <c>{ThemeResource}</c>
/// reference that has already been realised is not re-evaluated just because a palette changed.
/// </para>
/// </summary>
public static class MatrixTheme
{
    public const string Name = "Matrix";

    /// <summary>Classic phosphor green, as the brightest thing on the screen.</summary>
    public static readonly Color Phosphor = Color.FromArgb(0xFF, 0x00, 0xFF, 0x41);

    private static readonly Color Plate = Color.FromArgb(0xFF, 0x00, 0x0A, 0x03);
    private static readonly Color Text = Color.FromArgb(0xFF, 0x33, 0xFF, 0x66);

    /// <summary>
    /// The palette.
    /// <para>
    /// Failure states stay chromatically distinct rather than collapsing into shades of green. The
    /// board's rule is that status is never carried by colour alone — every row has a glyph and a
    /// text label — but rendering "unreachable" and "OK" as two similar greens would still make the
    /// board harder to read at a glance, which is the one thing it exists to be good at.
    /// </para>
    /// </summary>
    private static readonly (string Key, Color Color)[] Palette =
    [
        ("StatusOkBrush",          Phosphor),
        ("StatusTimeoutBrush",     Color.FromArgb(0xFF, 0xE8, 0xC5, 0x00)),
        ("StatusUnreachableBrush", Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30)),
        ("StatusDnsBrush",         Color.FromArgb(0xFF, 0x3B, 0xFF, 0xD4)),
        ("StatusRefusedBrush",     Color.FromArgb(0xFF, 0xFF, 0x8C, 0x1A)),
        ("StatusIdleBrush",        Color.FromArgb(0xFF, 0x2A, 0x7A, 0x38)),
        ("SparkOkBrush",           Color.FromArgb(0xFF, 0x00, 0xC8, 0x33)),
        ("SparkBadBrush",          Color.FromArgb(0xFF, 0xB3, 0x1D, 0x14)),
        ("HeaderBackgroundBrush",  Color.FromArgb(0x1C, 0x00, 0xFF, 0x41)),
        ("RowSeparatorBrush",      Color.FromArgb(0x38, 0x00, 0xFF, 0x41)),
    ];

    public static bool IsApplied { get; private set; }

    /// <summary>Caption-button glyph colour, so the title bar is not the one grey thing left.</summary>
    public static Color CaptionForeground => Phosphor;

    /// <summary>Black plate for the window root, replacing the Mica backdrop.</summary>
    public static Brush PlateBrush => new SolidColorBrush(Plate);

    /// <summary>Default text colour. Inherits to every TextBlock that does not set its own.</summary>
    public static Brush TextBrush => new SolidColorBrush(Text);

    public static FontFamily Font => new("Cascadia Mono, Consolas, Courier New");

    /// <summary>
    /// Installs the palette. The caller sets the element theme to Dark first — this palette assumes
    /// a dark plate underneath it.
    /// </summary>
    public static void Apply(Control board)
    {
        var palette = new Dictionary<string, Brush>(StringComparer.Ordinal);
        foreach (var (key, color) in Palette) palette[key] = new SolidColorBrush(color);

        BoardPalette.SetOverride(palette);
        IsApplied = true;

        // Font properties inherit from a Control, so one assignment puts the whole board on a
        // monospaced face. The numeric columns already used Consolas to stop digits jittering at
        // 4 Hz; a terminal theme just extends that to everything.
        board.FontFamily = Font;
        board.Foreground = TextBrush;
    }

    /// <summary>Removes the palette. Safe to call when it was never applied.</summary>
    public static void Revert(Control board)
    {
        BoardPalette.SetOverride(null);
        IsApplied = false;

        board.ClearValue(Control.FontFamilyProperty);
        board.ClearValue(Control.ForegroundProperty);
    }
}
