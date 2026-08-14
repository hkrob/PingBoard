using System.Globalization;

namespace PingBoard.Core;

/// <summary>Everything loaded from one config file.</summary>
public sealed record BoardConfig(
    Settings Settings,
    IReadOnlyList<TargetConfig> Targets,
    AlertSettings Alerts,
    IReadOnlyList<TabConfig> Tabs)
{
    /// <summary>Convenience for callers that predate alerting and have no alert settings to supply.</summary>
    public BoardConfig(Settings settings, IReadOnlyList<TargetConfig> targets)
        : this(settings, targets, new AlertSettings(), []) { }

    public BoardConfig(Settings settings, IReadOnlyList<TargetConfig> targets, AlertSettings alerts)
        : this(settings, targets, alerts, []) { }
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
                Order = section.GetInt(nameof(TabConfig.Order), order++),
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
                _ => ProbeKind.Icmp,
            };

            targets.Add(new TargetConfig
            {
                Name = name,
                Address = address,
                Probe = kind,
                Port = Math.Clamp(section.GetInt(nameof(TargetConfig.Port), 443), 1, 65535),
                Enabled = section.GetBool(nameof(TargetConfig.Enabled), true),
                Tab = section.GetString(nameof(TargetConfig.Tab), "").Trim(),

                // Every override is clamped to the same range Settings.Validate applies globally.
                // An override that skipped validation would be the one value in the file that
                // could still be nonsense — and Ttl=0 in particular is not merely odd, it makes
                // PingOptions throw on construction, which surfaces as a permanent unexplained
                // TIMEOUT on that target rather than as a config error.
                IntervalMs = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.IntervalMs)), 250, 3_600_000),
                TimeoutMs = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.TimeoutMs)), 100, 60_000),
                PayloadBytes = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.PayloadBytes)), 0, 65_500),
                Ttl = ClampOrNull(section.GetIntOrNull(nameof(TargetConfig.Ttl)), 1, 255),

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

        return new BoardConfig(settings, targets, alerts, tabs);
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
            section.Set(nameof(TabConfig.Order), order++);
        }
    }

    /// <summary>Clamps an optional override, preserving "inherit from [Settings]" as null.</summary>
    private static int? ClampOrNull(int? value, int min, int max) =>
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
    public static void Save(
        string path,
        Settings settings,
        IEnumerable<TargetConfig> targets,
        AlertSettings? alerts = null,
        IEnumerable<TabConfig>? tabs = null)
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

        WriteAlerts(ini, path, alerts);
        WriteTabs(ini, path, tabs);

        foreach (var t in targets)
        {
            var section = ini.GetOrAdd(TargetPrefix + t.Name);
            section.Set(nameof(TargetConfig.Address), t.Address);
            section.Set(nameof(TargetConfig.Probe), t.Probe == ProbeKind.Tcp ? "tcp" : "icmp");
            if (t.Probe == ProbeKind.Tcp) section.Set(nameof(TargetConfig.Port), t.Port);
            if (!t.Enabled) section.Set(nameof(TargetConfig.Enabled), false);

            // Written only when the target actually names a tab, so a board that never used them
            // round-trips byte for byte.
            if (t.Tab is { Length: > 0 }) section.Set(nameof(TargetConfig.Tab), t.Tab);

            section.SetOptional(nameof(TargetConfig.IntervalMs), t.IntervalMs);
            section.SetOptional(nameof(TargetConfig.TimeoutMs), t.TimeoutMs);
            section.SetOptional(nameof(TargetConfig.PayloadBytes), t.PayloadBytes);
            section.SetOptional(nameof(TargetConfig.Ttl), t.Ttl);
            section.SetOptional(nameof(TargetConfig.FailuresBeforeDown), t.FailuresBeforeDown);
        }

        ini.SaveAtomic(path);
    }

    /// <summary>Sidecar path for persisted counters: <c>config.ini</c> → <c>config.state.ini</c>.</summary>
    public static string StatePathFor(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(configPath);
        return Path.Combine(directory, stem + ".state.ini");
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
        "PingBoard persisted counters. Generated file — safe to delete.\n" +
        "Deleting it resets all lifetime statistics.";

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
        }

        ini.SaveAtomic(path);
    }
}
