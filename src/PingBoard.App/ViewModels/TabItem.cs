using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PingBoard.Core;

namespace PingBoard.App.ViewModels;

/// <summary>
/// One tab in the strip above the board.
/// <para>
/// Carries a live tally rather than just a name, because the whole point of putting hosts in
/// separate tabs is that you are not looking at most of them: a tab that says "WAN 1 down" is
/// doing the job even while another tab is on screen. A bare label would make grouping actively
/// dangerous — it would hide problems behind a tab nobody clicked.
/// </para>
/// </summary>
public sealed partial class TabItem : ObservableObject
{
    public TabItem(string name) => Name = name;

    /// <summary>
    /// Settable rather than init-only, so <see cref="MainViewModel.RenameTab"/> can rename this
    /// instance in place — the tab keeps its position in the strip and its live tally instead of
    /// being torn down and rebuilt.
    /// </summary>
    [ObservableProperty] public partial string Name { get; set; } = "";

    /// <summary>
    /// True for the one tab named "General" — the fallback every ungrouped target resolves to via
    /// <see cref="TabConfig.Normalise"/>. Never toggles for a given instance: renaming or deleting
    /// this specific tab is refused (see <see cref="MainViewModel.RenameTab"/>), so unlike
    /// <see cref="Name"/> this needs no change notification of its own.
    /// </summary>
    public bool IsDefaultTab => string.Equals(Name, TabConfig.DefaultName, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>False stops every target in this tab from being probed.</summary>
    [ObservableProperty] public partial bool IsEnabled { get; set; } = true;

    /// <summary>True silences this group's alerts while it carries on being probed.</summary>
    [ObservableProperty] public partial bool IsMuted { get; set; }

    /// <summary>
    /// Bell-with-slash on a muted tab. Shown permanently and on purpose: a silenced group you have
    /// forgotten about is worse than one that never alerted, because you are still trusting it.
    /// </summary>
    [ObservableProperty] public partial Visibility MutedMarkVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>
    /// True when either SelectedTags or SelectedSites is narrowing this tab's membership. Set by
    /// <see cref="MainViewModel.SetTabTags"/> and <see cref="MainViewModel.SetTabSites"/> — one
    /// glyph covers both, since what matters to the user is "this tab is not showing everything",
    /// not which of the two dimensions did it.
    /// </summary>
    [ObservableProperty] public partial bool HasFilter { get; set; }

    /// <summary>
    /// Filter-funnel glyph on a tab with an active filter. Shown permanently, for the same reason
    /// as <see cref="MutedMarkVisibility"/> — a filter narrowing what you see and forgotten about is
    /// worse than one that never existed, because you are still trusting the full picture.
    /// </summary>
    [ObservableProperty] public partial Visibility FilterMarkVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>"LAN", or "LAN · 2 down" when something in it is failing.</summary>
    [ObservableProperty] public partial string Label { get; private set; } = "";

    /// <summary>Struck through and dimmed when the tab is switched off.</summary>
    [ObservableProperty] public partial double TabOpacity { get; private set; } = 1.0;

    [ObservableProperty] public partial Visibility DisabledMarkVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>Highlight for the selected tab. A plain bool would need a converter in XAML.</summary>
    [ObservableProperty] public partial double SelectionOpacity { get; private set; } = 0.55;

    public void Update(int total, int down, bool selected)
    {
        IsSelected = selected;

        Label = down > 0 ? $"{Name} · {down} down" : $"{Name} · {total}";

        TabOpacity = IsEnabled ? 1.0 : 0.45;
        DisabledMarkVisibility = IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        MutedMarkVisibility = IsMuted ? Visibility.Visible : Visibility.Collapsed;
        FilterMarkVisibility = HasFilter ? Visibility.Visible : Visibility.Collapsed;
        SelectionOpacity = selected ? 1.0 : 0.55;
    }
}
