using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PingBoard.App.Controls;

/// <summary>
/// One place that resolves a status resource key to a brush, with an optional override layered on
/// top of the theme dictionaries.
/// <para>
/// The override exists because a XAML brush cannot be repainted. Brushes that live in a
/// <c>ResourceDictionary</c> are sealed once the framework has handed them out, and assigning
/// <see cref="SolidColorBrush.Color"/> on one throws <see cref="UnauthorizedAccessException"/> —
/// which is exactly how the first attempt at the Matrix theme failed. A palette that wants to
/// replace those colours therefore has to supply <em>new</em> brush objects and have consumers ask
/// for them by key, which is what this provides.
/// </para>
/// <para>
/// Both dynamic consumers already resolve by key on every use — the row status colours through
/// <see cref="BrushKeyConverter"/> and the sparkline on every arrange pass — so an override takes
/// effect on the next redraw without touching the theme dictionaries at all.
/// </para>
/// </summary>
public static class BoardPalette
{
    private static IReadOnlyDictionary<string, Brush>? _override;

    /// <summary>Installs a palette, or passes null to fall back to the theme dictionaries.</summary>
    public static void SetOverride(IReadOnlyDictionary<string, Brush>? palette) => _override = palette;

    public static bool HasOverride => _override is not null;

    /// <summary>The brush for a key: the override first, then the active theme dictionary.</summary>
    public static Brush? Find(string key)
    {
        if (_override is { } palette && palette.TryGetValue(key, out var overridden)) return overridden;

        return Application.Current.Resources.TryGetValue(key, out var resource) ? resource as Brush : null;
    }

    public static Brush Resolve(string key, Color fallback) => Find(key) ?? new SolidColorBrush(fallback);
}
