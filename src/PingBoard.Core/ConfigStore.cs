using System.Globalization;

namespace PingBoard.Core;

/// <summary>Everything loaded from one config file.</summary>
public sealed record BoardConfig(Settings Settings, IReadOnlyList<TargetConfig> Targets);

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
    public const string TargetPrefix = "Target:";

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
        }

        // The file may have been hand-edited into nonsense; clamp before anything uses it.
        settings.Validate();

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
                IntervalMs = section.GetIntOrNull(nameof(TargetConfig.IntervalMs)),
                TimeoutMs = section.GetIntOrNull(nameof(TargetConfig.TimeoutMs)),
                PayloadBytes = section.GetIntOrNull(nameof(TargetConfig.PayloadBytes)),
                Ttl = section.GetIntOrNull(nameof(TargetConfig.Ttl)),

                // Clamped like Port above: a hand-edited 0 would mean "alert before any failure",
                // which the record logic cannot express.
                FailuresBeforeDown = section.GetIntOrNull(nameof(TargetConfig.FailuresBeforeDown)) is { } f
                    ? Math.Clamp(f, 1, 100)
                    : null,
            });
        }

        return new BoardConfig(settings, targets);
    }

    public static void Save(string path, Settings settings, IEnumerable<TargetConfig> targets)
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

        foreach (var t in targets)
        {
            var section = ini.GetOrAdd(TargetPrefix + t.Name);
            section.Set(nameof(TargetConfig.Address), t.Address);
            section.Set(nameof(TargetConfig.Probe), t.Probe == ProbeKind.Tcp ? "tcp" : "icmp");
            if (t.Probe == ProbeKind.Tcp) section.Set(nameof(TargetConfig.Port), t.Port);
            if (!t.Enabled) section.Set(nameof(TargetConfig.Enabled), false);

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
