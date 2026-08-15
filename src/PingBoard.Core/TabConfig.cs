namespace PingBoard.Core;

/// <summary>
/// A group of targets, shown as a tab.
/// <para>
/// Membership lives on the target (<see cref="TargetConfig.Tab"/>) rather than as a list of names
/// here, so a target belongs to exactly one tab by construction and renaming a tab does not mean
/// editing a membership list that can drift out of step with reality.
/// </para>
/// <para>
/// <b>A tab is a view, not a scheduler.</b> Targets are probed regardless of which tab is on
/// screen — a monitor that only watched the tab you happened to be looking at would be worse than
/// useless, because the tabs you are <em>not</em> watching are exactly where an outage goes
/// unnoticed. <see cref="Enabled"/> is the separate, explicit way to stop probing a group.
/// </para>
/// </summary>
public sealed class TabConfig
{
    /// <summary>Where targets that name no tab are collected.</summary>
    public const string DefaultName = "General";

    public string Name { get; set; } = DefaultName;

    /// <summary>
    /// False stops every target in this tab from being probed, exactly as if each had been paused
    /// individually. Paused samples are already excluded from the rolling loss figures, so
    /// disabling a tab overnight does not corrupt its statistics the way counting the silence would.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display order. Ties fall back to the order the sections appear in the file.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Silences notifications for this group without stopping it being probed.
    /// <para>
    /// Distinct from <see cref="Enabled"/>, and the difference is the whole point. Disabling a tab
    /// stops the probes: no data, no history, no statistics. Muting keeps every one of those and
    /// only withholds the interruption — for the group of hosts you know is noisy and want on the
    /// board but not in your face. Turning a tab off to stop it nagging would throw away the
    /// record of what it did.
    /// </para>
    /// <para>
    /// It suppresses desktop notifications and webhook and email alerts alike, unlike the global
    /// mute button which is deliberately desktop-only. Muting a tab is a statement about those
    /// hosts; muting the app is a statement about this machine, and something reaching you
    /// elsewhere should survive the latter.
    /// </para>
    /// </summary>
    public bool Muted { get; set; }

    public TabConfig Clone() => (TabConfig)MemberwiseClone();

    /// <summary>Normalises a tab name, mapping blank onto the default group.</summary>
    public static string Normalise(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? DefaultName : trimmed;
    }
}
