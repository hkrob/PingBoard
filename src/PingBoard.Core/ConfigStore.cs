using System.Globalization;

namespace PingBoard.Core;

/// <summary>Everything loaded from one config file.</summary>
public sealed record BoardConfig(
    Settings Settings,
    IReadOnlyList<TargetConfig> Targets,
    AlertSettings Alerts,
    IReadOnlyList<TabConfig> Tabs,
    IReadOnlyList<SiteConfig> Sites)
{
    /// <summary>Convenience for callers that predate alerting and have no alert settings to supply.</summary>
    public BoardConfig(Settings settings, IReadOnlyList<TargetConfig> targets)
        : this(settings, targets, new AlertSettings(), [], []) { }

    public BoardConfig(Settings settings, IReadOnlyList<TargetConfig> targets, AlertSettings alerts)
        : this(settings, targets, alerts, [], []) { }
}

/// <summary>
/// Maps <see cref="BoardConfig"/> to and from the user's <c>.ini</c>.
/// <para>
/// Counters deliberately live elsewhere — see <see cref="StateStore"/>. Keeping churning numbers
/// out of the config means the file you hand-edit and copy between machines stays readable and
/// diff-able.
/// </para>
/// </summary>
public static class ConfigStore
{
    public const string SettingsSection = "Settings";
    public const string AlertsSection = "Alerts";
    public const string TargetPrefix = "Target:";
    public const string TabPrefix = "Tab:";
    public const string SitePrefix = "Site:";

    private const string HeaderComment =
        "PingBoard configuration.\n" +
        "Any numeric key from [Settings] may be repeated inside a [Target:...] section to\n" +
        "override it for that target only.";

    public static BoardConfig Load(string path)
    {
        // Resilient load: recovers from the .bak if the main file was lost, and clears any .tmp
        // orphaned by an interrupted save.
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
            return new BoardConfig(new Settings(), []);

        var ini = IniFile.LoadResilient(path);
        var settings = new Settings();

        if (ini.Find(SettingsSection) is { } s)
        {
            settings.IntervalMs = s.GetInt(nameof(Settings.IntervalMs), settings.IntervalMs);
            settings.TimeoutMs = s.GetInt(nameof(Settings.TimeoutMs), settings.TimeoutMs);
            settings.PayloadBytes = s.GetInt(nameof(Settings.PayloadBytes), settings.PayloadBytes);
            settings.Ttl = s.GetInt(nameof(Settings.Ttl), settings.Ttl);
            settings.RollingWindow = s.GetInt(nameof(Settings.RollingWindow), settings.RollingWindow);
            settings.PreferIPv4 = s.GetBool(nameof(Settings.PreferIPv4), settings.PreferIPv4);
            settings.DnsCacheSeconds = s.GetInt(nameof(Settings.DnsCacheSeconds), settings.DnsCacheSeconds);
            settings.MaxConcurrent = s.GetInt(nameof(Settings.MaxConcurrent), settings.MaxConcurrent);
            settings.NotifyOnChange = s.GetBool(nameof(Settings.NotifyOnChange), settings.NotifyOnChange);
            settings.FailuresBeforeDown = s.GetInt(nameof(Settings.FailuresBeforeDown), settings.FailuresBeforeDown);
            settings.FailuresBeforeReresolve =
                s.GetInt(nameof(Settings.FailuresBeforeReresolve), settings.FailuresBeforeReresolve);
            settings.LogPath = s.GetString(nameof(Settings.LogPath), settings.LogPath);
            settings.LogEnabled = s.GetBool(nameof(Settings.LogEnabled), settings.LogEnabled);
            settings.ResumeSettleMs = s.GetInt(nameof(Settings.ResumeSettleMs), settings.ResumeSettleMs);
            settings.TraceOnFailure = s.GetBool(nameof(Settings.TraceOnFailure), settings.TraceOnFailure);
            settings.TraceMaxHops = s.GetInt(nameof(Settings.TraceMaxHops), settings.TraceMaxHops);
            settings.TraceHopTimeoutMs = s.GetInt(nameof(Settings.TraceHopTimeoutMs), settings.TraceHopTimeoutMs);
            settings.DegradedLatencyMs = s.GetInt(nameof(Settings.DegradedLatencyMs), settings.DegradedLatencyMs);
            settings.DegradedLossPercent = s.GetDouble(nameof(Settings.DegradedLossPercent), settings.DegradedLossPercent);
            settings.DegradedSamples = s.GetInt(nameof(Settings.DegradedSamples), settings.DegradedSamples);
            settings.NotifyOnDegraded = s.GetBool(nameof(Settings.NotifyOnDegraded), settings.NotifyOnDegraded);
            settings.OutageLogEnabled = s.GetBool(nameof(Settings.OutageLogEnabled), settings.OutageLogEnabled);
            settings.CertCheckHours = s.GetInt(nameof(Settings.CertCheckHours), settings.CertCheckHours);
            settings.CertWarnDays = s.GetInt(nameof(Settings.CertWarnDays), settings.CertWarnDays);
        }

        // The file may have been hand-edited into nonsense; clamp before anything uses it.
        settings.Validate();

        var alerts = new AlertSettings();

        if (ini.Find(AlertsSection) is { } a)
        {
            alerts.WebhookEnabled = a.GetBool(nameof(AlertSettings.WebhookEnabled), false);
            alerts.WebhookUrl = a.GetString(nameof(AlertSettings.WebhookUrl), "");
            alerts.WebhookAuthorization = a.GetString(nameof(AlertSettings.WebhookAuthorization), "");
            alerts.EmailEnabled = a.GetBool(nameof(AlertSettings.EmailEnabled), false);
            alerts.SmtpHost = a.GetString(nameof(AlertSettings.SmtpHost), "");
            alerts.SmtpPort = a.GetInt(nameof(AlertSettings.SmtpPort), alerts.SmtpPort);
            alerts.SmtpUseStartTls = a.GetBool(nameof(AlertSettings.SmtpUseStartTls), true);
            alerts.SmtpUser = a.GetString(nameof(AlertSettings.SmtpUser), "");
            alerts.SmtpPassword = a.GetString(nameof(AlertSettings.SmtpPassword), "");
            alerts.EmailFrom = a.GetString(nameof(AlertSettings.EmailFrom), "");
            alerts.EmailTo = a.GetString(nameof(AlertSettings.EmailTo), "");
            alerts.MinIntervalSeconds = a.GetInt(nameof(AlertSettings.MinIntervalSeconds), alerts.MinIntervalSeconds);
            alerts.NotifyOnRecovery = a.GetBool(nameof(AlertSettings.NotifyOnRecovery), true);
            alerts.NotifyOnDegraded = a.GetBool(nameof(AlertSettings.NotifyOnDegraded), false);
            alerts.TimeoutMs = a.GetInt(nameof(AlertSettings.TimeoutMs), alerts.TimeoutMs);
        }

        alerts.Validate();

        // Tabs are read before targets so a target naming a tab that has its own section picks up
        // that section's state rather than a default invented here.
        var tabs = new List<TabConfig>();
        var tabsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;

        foreach (var section in ini.WithPrefix(TabPrefix))
        {
            var name = TabConfig.Normalise(section.Name[TabPrefix.Length..]);
            if (!tabsSeen.Add(name)) continue;

            tabs.Add(new TabConfig
            {
                Name = name,
                Enabled = section.GetBool(nameof(TabConfig.Enabled), true),
                Muted = section.GetBool(nameof(TabConfig.Muted), false),
                Order = section.GetInt(nameof(TabConfig.Order), order++),
            });
        }

        // Read the same way tabs are, and for the same reason: a target naming a site that already
        // has its own section should pick up that section's abbreviation rather than a blank one
        // invented here.
        var sites = new List<SiteConfig>();
        var sitesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in ini.WithPrefix(SitePrefix))
        {
            var name = section.Name[SitePrefix.Length..].Trim();
            if (name.Length == 0 || !sitesSeen.Add(name)) continue;

            sites.Add(new SiteConfig
            {
                Name = name,
                Abbreviation = section.GetString(nameof(SiteConfig.Abbreviation), ""),
            });
        }

        var targets = new List<TargetConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in ini.WithPrefix(TargetPrefix))
        {
            var name = section.Name[TargetPrefix.Length..].Trim();
            var address = section.GetString(nameof(TargetConfig.Address), "").Trim();

            // A target with no address cannot be probed, and duplicate names would collide in the
            // state sidecar. Skip both rather than half-loading them.
            if (name.Length == 0 || address.Length == 0) continue;
            if (!seen.Add(name)) continue;

            var kind = section.GetString(nameof(TargetConfig.Probe), "icmp").Trim().ToLowerInvariant() switch
            {
                "tcp" => ProbeKind.Tcp,
                "http" => ProbeKind.Http,
                "https" => ProbeKind.Https,
                _ => ProbeKind.Icmp,
            };

            // Each scheme carries its own conventional port, so a target that names one need not
            // also state the obvious.
            var defaultPort = kind switch
            {
                ProbeKind.Http => 80,
                ProbeKind.Https => 443,
                _ => 443,
            };

            targets.Add(new TargetConfig
            {
                Name = name,
                Address = address,
                Probe = kind,
                Port = Math.Clamp(section.GetInt(nameof(TargetConfig.Port), defaultPort), 1, 65535),
                Path = section.GetString(nameof(TargetConfig.Path), "/").Trim(),
                Maintenance = section.GetString(nameof(TargetConfig.Maintenance), "").Trim(),
                ExpectStatus = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.ExpectStatus)), 100, 599),
                Enabled = section.GetBool(nameof(TargetConfig.Enabled), true),
                Tab = section.GetString(nameof(TargetConfig.Tab), "").Trim(),
                Site = section.GetString(nameof(TargetConfig.Site), "").Trim(),

                // Every override is clamped to the same range Settings.Validate applies globally.
                // An override that skipped validation would be the one value in the file that
                // could still be nonsense — and Ttl=0 in particular is not merely odd, it makes
                // PingOptions throw on construction, which surfaces as a permanent unexplained
                // TIMEOUT on that target rather than as a config error.
                IntervalMs = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.IntervalMs)), 250, 3_600_000),
                TimeoutMs = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.TimeoutMs)), 100, 60_000),
                PayloadBytes = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.PayloadBytes)), 0, 65_500),
                Ttl = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.Ttl)), 1, 255),

                // Zero is preserved rather than clamped away: it is how a target switches the
                // degraded state off for itself when the global default has it on.
                DegradedLatencyMs = ClampOrNull(
                    section.GetIntOrNull(nameof(TargetConfig.DegradedLatencyMs)), 0, 600_000),
                DegradedLossPercent = ClampOrNull(
                    section.GetDoubleOrNull(nameof(TargetConfig.DegradedLossPercent)), 0, 100),

                // A hand-edited 0 would mean "alert before any failure", which the record logic
                // cannot express.
                FailuresBeforeDown = ClampOrNull(
                    section.GetIntOrNull(nameof(TargetConfig.FailuresBeforeDown)), 1, 100),
            });
        }

        // Any tab a target names but that has no section of its own still has to exist, or those
        // targets would have nowhere to appear. Same for the default group.
        foreach (var target in targets)
        {
            var name = TabConfig.Normalise(target.Tab);
            if (tabsSeen.Add(name)) tabs.Add(new TabConfig { Name = name, Order = order++ });
        }

        tabs.Sort((a, b) => a.Order.CompareTo(b.Order));

        // Same reconstruction as tabs, but only for a target that actually names one: unlike Tab,
        // blank Site means "no site" rather than a default group, and there is nothing to invent a
        // record for.
        foreach (var target in targets)
        {
            var name = target.Site.Trim();
            if (name.Length > 0 && sitesSeen.Add(name)) sites.Add(new SiteConfig { Name = name });
        }

        return new BoardConfig(settings, targets, alerts, tabs, sites);
    }

    /// <summary>
    /// Writes the <c>[Tab:...]</c> sections, or copies the existing ones across when the caller
    /// passed none.
    /// <para>
    /// Only tabs that carry non-default state are written. A tab that is merely a name with every
    /// target pointing at it needs no section — it is reconstructed on load from the memberships —
    /// so grouping a board does not litter the file with empty stanzas.
    /// </para>
    /// </summary>
    private static void WriteTabs(IniFile ini, string path, IEnumerable<TabConfig>? tabs)
    {
        if (tabs is null)
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return;

            foreach (var existing in IniFile.LoadResilient(path).WithPrefix(TabPrefix))
            {
                var copy = ini.GetOrAdd(existing.Name);
                foreach (var (key, value) in existing.Entries) copy.Set(key, value);
            }

            return;
        }

        // Every tab gets a section, including ones in an otherwise default state.
        //
        // An earlier version skipped those to keep the file tidy, and that lost the tab order: a
        // tab with no section is reconstructed on load from whichever targets happen to name it,
        // so the strip came back in target order — alphabetical — rather than the user's. Order is
        // state the user chose, not a default worth inferring.
        var order = 0;
        foreach (var tab in tabs)
        {
            var section = ini.GetOrAdd(TabPrefix + tab.Name);
            section.Set(nameof(TabConfig.Enabled), tab.Enabled);
            section.Set(nameof(TabConfig.Muted), tab.Muted);
            section.Set(nameof(TabConfig.Order), order++);
        }
    }

    /// <summary>
    /// Writes the <c>[Site:...]</c> sections, or copies the existing ones across when the caller
    /// passed none — same null-preserves-the-file contract as <see cref="WriteTabs"/>.
    /// <para>
    /// Every known site gets a section unconditionally, on the same reasoning <see cref="WriteTabs"/>
    /// settled on: a site with no section is reconstructed on load purely from target membership,
    /// which knows the name but never the abbreviation — skipping "default-looking" sites here would
    /// silently lose the one piece of state a site actually exists to carry.
    /// </para>
    /// </summary>
    private static void WriteSites(IniFile ini, string path, IEnumerable<SiteConfig>? sites)
    {
        if (sites is null)
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return;

            foreach (var existing in IniFile.LoadResilient(path).WithPrefix(SitePrefix))
            {
                var copy = ini.GetOrAdd(existing.Name);
                foreach (var (key, value) in existing.Entries) copy.Set(key, value);
            }

            return;
        }

        foreach (var site in sites)
        {
            var section = ini.GetOrAdd(SitePrefix + site.Name);
            section.Set(nameof(SiteConfig.Abbreviation), site.Abbreviation);
        }
    }

    /// <summary>Clamps an optional override, preserving "inherit from [Settings]" as null.</summary>
    private static int? ClampOrNull(int? value, int min, int max) =>
        value is { } v ? Math.Clamp(v, min, max) : null;

    private static double? ClampOrNull(double? value, double min, double max) =>
        value is { } v ? Math.Clamp(v, min, max) : null;

    /// <summary>
    /// Writes the <c>[Alerts]</c> section, or copies the existing one across verbatim when the
    /// caller passed none. Secrets are re-protected on the way out, so a password hand-typed into
    /// the file as plaintext is encrypted the next time the board saves.
    /// </summary>
    private static void WriteAlerts(IniFile ini, string path, AlertSettings? alerts)
    {
        if (alerts is null)
        {
            var existing = File.Exists(path) || File.Exists(path + ".bak")
                ? IniFile.LoadResilient(path).Find(AlertsSection)
                : null;

            if (existing is null) return;

            var copy = ini.GetOrAdd(AlertsSection);
            foreach (var (key, value) in existing.Entries) copy.Set(key, value);
            return;
        }

        var section = ini.GetOrAdd(AlertsSection);
        section.Comment =
            "Alerting. Webhook posts JSON; email uses SMTP.\n" +
            "WebhookAuthorization and SmtpPassword are DPAPI-encrypted for the current Windows\n" +
            "user, so copying this file to another machine requires re-entering them. A value\n" +
            "typed in as plaintext still works and is encrypted on the next save.";

        section.Set(nameof(AlertSettings.WebhookEnabled), alerts.WebhookEnabled);
        section.Set(nameof(AlertSettings.WebhookUrl), alerts.WebhookUrl);
        section.Set(nameof(AlertSettings.WebhookAuthorization), ProtectedValue.Protect(alerts.WebhookAuthorization));
        section.Set(nameof(AlertSettings.EmailEnabled), alerts.EmailEnabled);
        section.Set(nameof(AlertSettings.SmtpHost), alerts.SmtpHost);
        section.Set(nameof(AlertSettings.SmtpPort), alerts.SmtpPort);
        section.Set(nameof(AlertSettings.SmtpUseStartTls), alerts.SmtpUseStartTls);
        section.Set(nameof(AlertSettings.SmtpUser), alerts.SmtpUser);
        section.Set(nameof(AlertSettings.SmtpPassword), ProtectedValue.Protect(alerts.SmtpPassword));
        section.Set(nameof(AlertSettings.EmailFrom), alerts.EmailFrom);
        section.Set(nameof(AlertSettings.EmailTo), alerts.EmailTo);
        section.Set(nameof(AlertSettings.MinIntervalSeconds), alerts.MinIntervalSeconds);
        section.Set(nameof(AlertSettings.NotifyOnRecovery), alerts.NotifyOnRecovery);
        section.Set(nameof(AlertSettings.NotifyOnDegraded), alerts.NotifyOnDegraded);
        section.Set(nameof(AlertSettings.TimeoutMs), alerts.TimeoutMs);
    }

    /// <param name="alerts">
    /// Null means "leave the alert configuration alone". The board autosaves whenever a target is
    /// edited, and those paths have no alert settings in hand — without this, the first autosave
    /// after startup would quietly delete the user's webhook and SMTP credentials.
    /// </param>
    /// <param name="tabs">
    /// Null leaves any <c>[Tab:...]</c> sections on disk alone, for the same reason
    /// <paramref name="alerts"/> does: autosave paths that know nothing about tabs must not delete
    /// the user's grouping.
    /// </param>
    /// <param name="sites">Null leaves any <c>[Site:...]</c> sections on disk alone, for the same reason.</param>
    public static void Save(
        string path,
        Settings settings,
        IEnumerable<TargetConfig> targets,
        AlertSettings? alerts = null,
        IEnumerable<TabConfig>? tabs = null,
        IEnumerable<SiteConfig>? sites = null)
    {
        var ini = new IniFile();

        var s = ini.GetOrAdd(SettingsSection);
        s.Comment = HeaderComment;
        s.Set(nameof(Settings.IntervalMs), settings.IntervalMs);
        s.Set(nameof(Settings.TimeoutMs), settings.TimeoutMs);
        s.Set(nameof(Settings.PayloadBytes), settings.PayloadBytes);
        s.Set(nameof(Settings.Ttl), settings.Ttl);
        s.Set(nameof(Settings.RollingWindow), settings.RollingWindow);
        s.Set(nameof(Settings.PreferIPv4), settings.PreferIPv4);
        s.Set(nameof(Settings.DnsCacheSeconds), settings.DnsCacheSeconds);
        s.Set(nameof(Settings.MaxConcurrent), settings.MaxConcurrent);
        s.Set(nameof(Settings.NotifyOnChange), settings.NotifyOnChange);
        s.Set(nameof(Settings.FailuresBeforeDown), settings.FailuresBeforeDown);
        s.Set(nameof(Settings.FailuresBeforeReresolve), settings.FailuresBeforeReresolve);
        s.Set(nameof(Settings.LogEnabled), settings.LogEnabled);
        s.Set(nameof(Settings.LogPath), settings.LogPath);
        s.Set(nameof(Settings.ResumeSettleMs), settings.ResumeSettleMs);
        s.Set(nameof(Settings.TraceOnFailure), settings.TraceOnFailure);
        s.Set(nameof(Settings.TraceMaxHops), settings.TraceMaxHops);
        s.Set(nameof(Settings.TraceHopTimeoutMs), settings.TraceHopTimeoutMs);
        s.Set(nameof(Settings.DegradedLatencyMs), settings.DegradedLatencyMs);
        s.Set(nameof(Settings.DegradedLossPercent), settings.DegradedLossPercent);
        s.Set(nameof(Settings.DegradedSamples), settings.DegradedSamples);
        s.Set(nameof(Settings.NotifyOnDegraded), settings.NotifyOnDegraded);
        s.Set(nameof(Settings.OutageLogEnabled), settings.OutageLogEnabled);
        s.Set(nameof(Settings.CertCheckHours), settings.CertCheckHours);
        s.Set(nameof(Settings.CertWarnDays), settings.CertWarnDays);

        WriteAlerts(ini, path, alerts);
        WriteTabs(ini, path, tabs);
        WriteSites(ini, path, sites);

        foreach (var t in targets)
        {
            var section = ini.GetOrAdd(TargetPrefix + t.Name);
            section.Set(nameof(TargetConfig.Address), t.Address);
            section.Set(nameof(TargetConfig.Probe), t.Probe switch
            {
                ProbeKind.Tcp => "tcp",
                ProbeKind.Http => "http",
                ProbeKind.Https => "https",
                _ => "icmp",
            });

            if (t.Probe is ProbeKind.Tcp or ProbeKind.Http or ProbeKind.Https)
                section.Set(nameof(TargetConfig.Port), t.Port);

            if (t.Probe is ProbeKind.Http or ProbeKind.Https)
            {
                if (t.Path is { Length: > 0 } && t.Path != "/") section.Set(nameof(TargetConfig.Path), t.Path);
                section.SetOptional(nameof(TargetConfig.ExpectStatus), t.ExpectStatus);
            }
            if (!t.Enabled) section.Set(nameof(TargetConfig.Enabled), false);

            // Written only when the target actually names a tab, so a board that never used them
            // round-trips byte for byte.
            if (t.Tab is { Length: > 0 }) section.Set(nameof(TargetConfig.Tab), t.Tab);
            if (t.Site is { Length: > 0 }) section.Set(nameof(TargetConfig.Site), t.Site);
            if (t.Maintenance is { Length: > 0 }) section.Set(nameof(TargetConfig.Maintenance), t.Maintenance);

            section.SetOptional(nameof(TargetConfig.IntervalMs), t.IntervalMs);
            section.SetOptional(nameof(TargetConfig.TimeoutMs), t.TimeoutMs);
            section.SetOptional(nameof(TargetConfig.PayloadBytes), t.PayloadBytes);
            section.SetOptional(nameof(TargetConfig.Ttl), t.Ttl);
            section.SetOptional(nameof(TargetConfig.FailuresBeforeDown), t.FailuresBeforeDown);
            section.SetOptional(nameof(TargetConfig.DegradedLatencyMs), t.DegradedLatencyMs);
            section.SetOptional(nameof(TargetConfig.DegradedLossPercent), t.DegradedLossPercent);
        }

        ini.SaveAtomic(path);
    }

    /// <summary>Sidecar path for persisted counters: <c>config.ini</c> → <c>config.state.ini</c>.</summary>
    public static string StatePathFor(string configPath) => SidecarFor(configPath, ".state.ini");

    /// <summary>
    /// Sidecar path for the outage log: <c>config.ini</c> → <c>config.outages.csv</c>.
    /// <para>
    /// Keyed to the board rather than fixed beside the application, and the distinction matters
    /// because two boards are explicitly supported side by side — <c>--config</c> keys the
    /// single-instance guard precisely so they can be. A fixed path would have them writing to one
    /// file and each loading the other's outages at startup, so a board would open showing hosts it
    /// has never heard of. Counters and history already live beside their config for the same
    /// reason; an outage belongs to the board that recorded it, not to the machine.
    /// </para>
    /// </summary>
    public static string OutagePathFor(string configPath) => SidecarFor(configPath, ".outages.csv");

    private static string SidecarFor(string configPath, string suffix)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(configPath);
        return Path.Combine(directory, stem + suffix);
    }
}

/// <summary>
/// Persists lifetime counters to a sidecar file so they survive a restart.
/// <para>
/// Written on a debounce — every 60 s and on exit — never per probe. At one probe per second
/// across dozens of targets, saving on each result would mean continuous disk writes for numbers
/// nobody reads between refreshes.
/// </para>
/// </summary>
public static class StateStore
{
    private const string Header =
        "PingBoard persisted counters and probe history. Generated file — safe to delete.\n" +
        "Deleting it resets all statistics, including the sparkline and latency graph.";

    /// <summary>
    /// Encodes retained probe history as <c>status:rtt</c> pairs.
    /// <para>
    /// Only status and round-trip time are kept, because only those are ever read back: the
    /// rolling statistics, the sparkline and the latency graph all work from them alone. The
    /// timestamps are deliberately dropped — a monotonic tick from a process that has exited means
    /// nothing, and a wall-clock time would imply the samples are positioned in time when both
    /// charts plot them by index.
    /// </para>
    /// <para>
    /// Compact on purpose: 300 samples across 40 targets is 12,000 of these, and the sidecar is
    /// rewritten every minute.
    /// </para>
    /// </summary>
    public static string EncodeHistory(IReadOnlyList<ProbeResult> samples)
    {
        var sb = new System.Text.StringBuilder(samples.Count * 6);

        foreach (var sample in samples)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append((int)sample.Status).Append(':').Append(sample.RttMs);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decodes what <see cref="EncodeHistory"/> wrote, skipping anything malformed rather than
    /// throwing — a corrupt sidecar should cost the history, never the launch.
    /// </summary>
    public static List<ProbeResult> DecodeHistory(string encoded)
    {
        var samples = new List<ProbeResult>();
        if (encoded.Length == 0) return samples;

        foreach (var pair in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0) continue;

            if (!int.TryParse(pair.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
                continue;

            if (!int.TryParse(pair.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rtt))
                continue;

            if (!Enum.IsDefined(typeof(TargetStatus), status)) continue;

            var kind = (TargetStatus)status;

            samples.Add(kind.IsOk() && rtt >= 0
                ? ProbeResult.Ok(rtt, System.Net.IPAddress.None, 0, default)
                : ProbeResult.Fail(kind, 0, default));
        }

        return samples;
    }

    public static Dictionary<string, TargetCounters> Load(string path)
    {
        var result = new Dictionary<string, TargetCounters>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return result;

        IniFile ini;
        try { ini = IniFile.LoadResilient(path); }
        catch (IOException) { return result; }          // unreadable sidecar simply means no history
        catch (UnauthorizedAccessException) { return result; }

        foreach (var section in ini.WithPrefix(ConfigStore.TargetPrefix))
        {
            var name = section.Name[ConfigStore.TargetPrefix.Length..].Trim();
            if (name.Length == 0) continue;

            result[name] = new TargetCounters
            {
                OkCount = Math.Max(0, section.GetLong(nameof(TargetCounters.OkCount), 0)),
                NokCount = Math.Max(0, section.GetLong(nameof(TargetCounters.NokCount), 0)),
                LastOk = section.GetDate(nameof(TargetCounters.LastOk)),
                LastNok = section.GetDate(nameof(TargetCounters.LastNok)),
            };
        }

        return result;
    }

    /// <summary>
    /// Probe history per target name, for restoring the sparkline and latency graph across a
    /// restart. Empty when the sidecar predates history or has none.
    /// </summary>
    public static Dictionary<string, List<ProbeResult>> LoadHistory(string path)
    {
        var result = new Dictionary<string, List<ProbeResult>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return result;

        IniFile ini;
        try { ini = IniFile.LoadResilient(path); }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        foreach (var section in ini.WithPrefix(ConfigStore.TargetPrefix))
        {
            var name = section.Name[ConfigStore.TargetPrefix.Length..].Trim();
            if (name.Length == 0) continue;

            var encoded = section.GetString(HistoryKey, "");
            if (encoded.Length == 0) continue;

            result[name] = DecodeHistory(encoded);
        }

        return result;
    }

    /// <summary>
    /// Hourly availability buckets per target name, for the 24h/7d/30d figures. Empty when the
    /// sidecar predates them.
    /// </summary>
    public static Dictionary<string, AvailabilityLog> LoadAvailability(string path)
    {
        var result = new Dictionary<string, AvailabilityLog>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return result;

        IniFile ini;
        try { ini = IniFile.LoadResilient(path); }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        foreach (var section in ini.WithPrefix(ConfigStore.TargetPrefix))
        {
            var name = section.Name[ConfigStore.TargetPrefix.Length..].Trim();
            if (name.Length == 0) continue;

            var encoded = section.GetString(AvailabilityKey, "");
            if (encoded.Length == 0) continue;

            result[name] = AvailabilityLog.Decode(encoded);
        }

        return result;
    }

    private const string HistoryKey = "History";
    private const string AvailabilityKey = "Availability";

    public static void Save(string path, IEnumerable<PingTarget> targets)
    {
        var ini = new IniFile();
        var first = true;

        foreach (var target in targets)
        {
            var section = ini.GetOrAdd(ConfigStore.TargetPrefix + target.Config.Name);
            if (first) { section.Comment = Header; first = false; }

            var c = target.Counters;
            section.Set(nameof(TargetCounters.OkCount), c.OkCount);
            section.Set(nameof(TargetCounters.NokCount), c.NokCount);
            if (c.LastOk is { } ok)
                section.Set(nameof(TargetCounters.LastOk), ok.ToString("o", CultureInfo.InvariantCulture));
            if (c.LastNok is { } nok)
                section.Set(nameof(TargetCounters.LastNok), nok.ToString("o", CultureInfo.InvariantCulture));

            // Last, so the readable counters stay at the top of each section rather than being
            // pushed below a very long line.
            var history = target.HistorySnapshot();
            if (history.Length > 0) section.Set(HistoryKey, EncodeHistory(history));

            var availability = target.Availability.Encode();
            if (availability.Length > 0) section.Set(AvailabilityKey, availability);
        }

        ini.SaveAtomic(path);
    }
}
