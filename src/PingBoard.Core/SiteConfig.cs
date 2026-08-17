namespace PingBoard.Core;

/// <summary>
/// A physical location a target can be tagged with — "Connaught", "Northcliffe" — kept entirely
/// separate from <see cref="TabConfig"/>. A tab is a functional grouping chosen for the board
/// ("Public DNS", "Large websites"); a site is a place. A tab can hold targets from several sites,
/// and a site's targets can be spread across several tabs, so the two are orthogonal rather than
/// one being a kind of the other.
/// <para>
/// Membership lives on the target (<see cref="TargetConfig.Site"/>) rather than as a list of names
/// here, for the same reason <see cref="TabConfig"/> works this way: a target belongs to at most one
/// site by construction, and renaming or re-abbreviating a site does not mean editing a membership
/// list that can drift out of step with reality.
/// </para>
/// <para>
/// Unlike a tab, having no site at all is a normal, common state — most targets are not tied to a
/// physical location worth naming — so there is no default-name concept analogous to
/// <see cref="TabConfig.DefaultName"/>. A blank <see cref="TargetConfig.Site"/> means exactly that:
/// no site, shown as "—" rather than folded into a catch-all.
/// </para>
/// </summary>
public sealed class SiteConfig
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Short form shown in the Site Abbreviation column, e.g. "Conn" for "Connaught". Defined once
    /// per site rather than typed per target, so every target at a site reads identically instead
    /// of drifting between "Conn", "CONN" and "Connaught" depending on who added which host.
    /// </summary>
    public string Abbreviation { get; set; } = "";

    public SiteConfig Clone() => (SiteConfig)MemberwiseClone();
}
