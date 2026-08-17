using System.Text;
using PingBoard.Core;

namespace PingBoard.Harness;

/// <summary>
/// Checks on the parts that are awkward to verify by watching the UI: INI round-trip fidelity,
/// the ring buffer's rolling maths, and the atomic-write guarantee.
/// <para>Run with <c>PingBoard.Harness --selftest</c>.</para>
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "pingboard-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);

        try
        {
            IniRoundTrip(scratch);
            HandEditedFileSurvives(scratch);
            AtomicWriteKeepsOldFileIntact(scratch);
            CountersRoundTrip(scratch);
            HistorySurvivesRestart(scratch);
            MaintenanceWindows();
            HostCatalogIsUsable();
            WhileYouWereAway();
            AvailabilityOverDays();
            RingBufferRolls();
            RingBufferIgnoresInactiveSamples();
            RingBufferTimeoutIsInvisibleToMinMax();
            StatusMapping();
            FailedProbeKeepsTargetAddress();
            SuspendFreezesCountersAndAlerts();
            PerHostTimeoutOverride();
            PerHostFailureThreshold();
            ThresholdLoweredMidOutage();
            ConcurrencyCeilingFollowsSettings();
            AlertSecretsAndValidation();
            AlertConfigSurvivesAutosave(scratch);
            CertificateReadFailureIsNeverSilent();
            WebhookDeliversATransition();
            TraceRouteFindsThePath();
            TabsGroupWithoutGating(scratch);
            SitesAreIndependentOfTabs(scratch);
            UpdateVersionComparison();
            HttpProbeJudgesTheStatusCode();
            RecentStatsWindow();
            DegradedState();
            DegradedThresholdsRoundTrip(scratch);
            OutagePairing();
            OutageStoreRoundTrip(scratch);
            CertificateArithmetic();
            CertificateWarnsOnce();
            ExportsAreParsable();
            SoftAlertWording();
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { /* best effort */ }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void IniRoundTrip(string dir)
    {
        var path = Path.Combine(dir, "config.ini");

        var settings = new Settings { IntervalMs = 3000, TimeoutMs = 1500, PreferIPv4 = false, MaxConcurrent = 7 };
        var targets = new List<TargetConfig>
        {
            new() { Name = "gateway", Address = "10.1.10.1" },
            new() { Name = "hass", Address = "10.1.10.12", Probe = ProbeKind.Tcp, Port = 8123, IntervalMs = 5000 },
            new() { Name = "paused-one", Address = "example.com", Enabled = false },
        };

        ConfigStore.Save(path, settings, targets);
        var loaded = ConfigStore.Load(path);

        Check("ini: settings round-trip", loaded.Settings.IntervalMs == 3000
                                         && loaded.Settings.TimeoutMs == 1500
                                         && !loaded.Settings.PreferIPv4
                                         && loaded.Settings.MaxConcurrent == 7);

        Check("ini: target count", loaded.Targets.Count == 3);

        var hass = loaded.Targets.FirstOrDefault(t => t.Name == "hass");
        Check("ini: tcp target round-trip", hass is { Probe: ProbeKind.Tcp, Port: 8123, IntervalMs: 5000 });

        var paused = loaded.Targets.FirstOrDefault(t => t.Name == "paused-one");
        Check("ini: disabled flag round-trip", paused is { Enabled: false });

        var gateway = loaded.Targets.FirstOrDefault(t => t.Name == "gateway");
        Check("ini: unset overrides stay null",
            gateway is { IntervalMs: null, TimeoutMs: null, FailuresBeforeDown: null });

        // Per-host alert threshold must survive a round trip, and must be written only under the
        // target that set it — not leaked into [Settings] or onto its neighbours.
        var thresholdPath = Path.Combine(dir, "threshold.ini");
        ConfigStore.Save(thresholdPath, new Settings { FailuresBeforeDown = 3 },
        [
            new TargetConfig { Name = "wan", Address = "10.2.10.10", FailuresBeforeDown = 8 },
            new TargetConfig { Name = "lan", Address = "10.1.10.1" },
        ]);

        var reloaded = ConfigStore.Load(thresholdPath);
        Check("ini: per-host threshold round-trips",
            reloaded.Targets.First(t => t.Name == "wan").FailuresBeforeDown == 8);
        Check("ini: neighbouring target keeps inheriting",
            reloaded.Targets.First(t => t.Name == "lan").FailuresBeforeDown is null);
        Check("ini: global threshold unaffected", reloaded.Settings.FailuresBeforeDown == 3);

        // HTTP targets carry a scheme, a path and optionally a required status code, and each
        // scheme brings its own conventional port so a config need not state the obvious.
        var httpPath = Path.Combine(dir, "http.ini");
        ConfigStore.Save(httpPath, new Settings(),
        [
            new TargetConfig { Name = "site", Address = "example.com", Probe = ProbeKind.Https, Port = 443, Path = "/health", ExpectStatus = 200 },
            new TargetConfig { Name = "plain", Address = "intranet", Probe = ProbeKind.Http, Port = 80 },
        ]);

        var httpLoaded = ConfigStore.Load(httpPath);
        var site = httpLoaded.Targets.First(t => t.Name == "site");
        var plain = httpLoaded.Targets.First(t => t.Name == "plain");

        Check("ini: https probe round-trips", site.Probe == ProbeKind.Https);
        Check("ini: request path round-trips", site.Path == "/health");
        Check("ini: required status round-trips", site.ExpectStatus == 200);
        Check("ini: http probe round-trips", plain.Probe == ProbeKind.Http);
        Check("ini: http keeps its port", plain.Port == 80);

        // A bare http target with no port stated should land on 80, not on the 443 default that
        // suits every other probe kind.
        File.WriteAllText(httpPath, """
            [Target:bare]
            Address=intranet
            Probe=http
            """, Encoding.UTF8);

        Check("ini: http defaults to port 80", ConfigStore.Load(httpPath).Targets[0].Port == 80);

        File.WriteAllText(httpPath, """
            [Target:bare]
            Address=intranet
            Probe=https
            """, Encoding.UTF8);

        Check("ini: https defaults to port 443", ConfigStore.Load(httpPath).Targets[0].Port == 443);

        // Hand-edited nonsense must be clamped, not carried through as-is — and that applies to
        // every per-target override, not just the threshold. Ttl=0 is the one that bites: it makes
        // PingOptions throw on construction, which lands in the scheduler's catch-all and shows up
        // as a permanent, unexplained TIMEOUT rather than as a configuration problem.
        File.WriteAllText(thresholdPath, """
            [Settings]
            FailuresBeforeDown=3

            [Target:silly]
            Address=1.2.3.4
            FailuresBeforeDown=0
            Ttl=0
            IntervalMs=1
            TimeoutMs=99999999
            PayloadBytes=-5
            """, Encoding.UTF8);

        var silly = ConfigStore.Load(thresholdPath).Targets[0];

        Check("ini: hand-edited threshold is clamped", silly.FailuresBeforeDown == 1);
        Check("ini: per-target ttl clamped above zero", silly.Ttl == 1);
        Check("ini: per-target interval clamped up to the floor", silly.IntervalMs == 250);
        Check("ini: per-target timeout clamped to the ceiling", silly.TimeoutMs == 60_000);
        Check("ini: per-target payload cannot go negative", silly.PayloadBytes == 0);
    }

    private static void HandEditedFileSurvives(string dir)
    {
        // The file is meant to be hand-editable, so the parser has to tolerate what a human writes:
        // comments, blank lines, odd spacing, mixed-case keys and inline trailing comments.
        var path = Path.Combine(dir, "handedit.ini");
        File.WriteAllText(path, """
            ; a leading comment
            # another comment style

            [Settings]
              intervalms   =   4000      ; inline comment, should be stripped
            PREFERIPV4=no

            [Target:odd name with spaces]
            address = 192.168.1.1
            probe=TCP
            Port = 9000

            [Target:no-address]
            Probe=icmp

            [Target:odd name with spaces]
            Address = 1.2.3.4
            """, Encoding.UTF8);

        var loaded = ConfigStore.Load(path);

        Check("ini: case-insensitive keys", loaded.Settings.IntervalMs == 4000);
        Check("ini: bool aliases (no/yes)", !loaded.Settings.PreferIPv4);

        var odd = loaded.Targets.FirstOrDefault(t => t.Name == "odd name with spaces");
        Check("ini: names with spaces", odd is { Port: 9000, Probe: ProbeKind.Tcp });

        // A duplicate section name is merged by the parser, so the later Address wins. What must
        // not happen is two targets sharing a name — they would collide in the state sidecar.
        Check("ini: duplicate section merges, not duplicates",
            loaded.Targets.Count(t => t.Name == "odd name with spaces") == 1);

        Check("ini: address-less target skipped", loaded.Targets.All(t => t.Name != "no-address"));
    }

    private static void AtomicWriteKeepsOldFileIntact(string dir)
    {
        var path = Path.Combine(dir, "atomic.ini");
        ConfigStore.Save(path, new Settings(), [new TargetConfig { Name = "a", Address = "1.1.1.1" }]);
        var firstLength = new FileInfo(path).Length;

        // Second save must rotate the previous version to .bak and leave no .tmp behind. Those two
        // properties are what stop a crash mid-write from destroying the target list.
        ConfigStore.Save(path, new Settings(), [new TargetConfig { Name = "b", Address = "2.2.2.2" }]);

        Check("atomic: .bak created", File.Exists(path + ".bak"));
        Check("atomic: no .tmp left behind", !File.Exists(path + ".tmp"));
        Check("atomic: backup holds previous content",
            File.ReadAllText(path + ".bak").Contains("1.1.1.1", StringComparison.Ordinal));
        Check("atomic: live file holds new content",
            File.ReadAllText(path).Contains("2.2.2.2", StringComparison.Ordinal));
        Check("atomic: file non-empty", firstLength > 0 && new FileInfo(path).Length > 0);

        // A write into a directory that does not exist yet must create it rather than throw —
        // the user picks the config path from a file dialog and may point at a new folder.
        var nested = Path.Combine(dir, "new", "deeper", "cfg.ini");
        ConfigStore.Save(nested, new Settings(), []);
        Check("atomic: creates missing directories", File.Exists(nested));

        // Recovery: if the live file is lost but the backup survives, the board must come back
        // rather than starting empty. Losing every target to one bad shutdown would be the worst
        // possible failure for a tool you leave running for weeks.
        var recover = Path.Combine(dir, "recover.ini");
        ConfigStore.Save(recover, new Settings(), [new TargetConfig { Name = "keeper", Address = "9.9.9.9" }]);
        ConfigStore.Save(recover, new Settings(), [new TargetConfig { Name = "keeper", Address = "9.9.9.9" }]);
        File.Delete(recover);

        var recovered = ConfigStore.Load(recover);
        Check("atomic: recovers from .bak when the live file is gone",
            recovered.Targets.Count == 1 && recovered.Targets[0].Address == "9.9.9.9");
        Check("atomic: recovery restores the live file", File.Exists(recover));

        // An orphaned .tmp is the residue of an interrupted save and was never committed.
        var orphan = Path.Combine(dir, "orphan.ini");
        ConfigStore.Save(orphan, new Settings(), [new TargetConfig { Name = "a", Address = "1.1.1.1" }]);
        File.WriteAllText(orphan + ".tmp", "garbage that was never committed");
        ConfigStore.Load(orphan);
        Check("atomic: orphaned .tmp is cleaned up on load", !File.Exists(orphan + ".tmp"));
    }

    private static void CountersRoundTrip(string dir)
    {
        var configPath = Path.Combine(dir, "counters.ini");
        var statePath = ConfigStore.StatePathFor(configPath);

        Check("state: sidecar path derivation", Path.GetFileName(statePath) == "counters.state.ini");

        var settings = new Settings();
        var target = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.1" }, settings);
        var now = DateTimeOffset.Now;

        target.Record(ProbeResult.Ok(5, System.Net.IPAddress.Loopback, 1000, now), settings.FailuresBeforeDown);
        target.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now), settings.FailuresBeforeDown);
        target.Record(ProbeResult.Ok(7, System.Net.IPAddress.Loopback, 3000, now), settings.FailuresBeforeDown);

        StateStore.Save(statePath, [target]);
        var restored = StateStore.Load(statePath);

        Check("state: counters persist", restored.TryGetValue("gw", out var c) && c.OkCount == 2 && c.NokCount == 1);
        Check("state: timestamps persist", restored["gw"].LastOk is not null && restored["gw"].LastNok is not null);
        Check("state: missing file yields empty", StateStore.Load(Path.Combine(dir, "nope.state.ini")).Count == 0);

        target.Dispose();
    }

    /// <summary>
    /// History survives a restart.
    /// <para>
    /// Counters have always persisted, but the samples did not — so every sparkline and latency
    /// graph came back empty. That mattered little when the sparkline was the only chart; it
    /// undermines the latency graph completely, whose entire value is comparing current latency
    /// against the target's own baseline.
    /// </para>
    /// </summary>
    private static void HistorySurvivesRestart(string dir)
    {
        var configPath = Path.Combine(dir, "history.ini");
        var statePath = ConfigStore.StatePathFor(configPath);

        var settings = new Settings { RollingWindow = 50 };
        var target = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.1" }, settings);
        var now = DateTimeOffset.Now;

        for (var i = 1; i <= 10; i++)
            target.Record(ProbeResult.Ok(i, System.Net.IPAddress.Loopback, i, now), 3);

        target.Record(ProbeResult.Fail(TargetStatus.Timeout, 20, now), 3);
        target.Record(ProbeResult.Fail(TargetStatus.HttpError, 21, now), 3);

        var before = target.Snapshot().Stats;
        StateStore.Save(statePath, [target]);

        // A fresh process: new target object, history read back off disk.
        var restored = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.1" }, settings);
        var loaded = StateStore.LoadHistory(statePath);

        Check("history: the sidecar carries history", loaded.ContainsKey("gw"));
        restored.RestoreHistory(loaded["gw"]);

        var after = restored.Snapshot().Stats;

        Check("history: sample count survives", after.Samples == before.Samples);
        Check("history: loss percentage survives",
            Math.Abs(after.LossPercent - before.LossPercent) < 0.001);
        Check("history: min/max round-trip survive",
            after.MinMs == before.MinMs && after.MaxMs == before.MaxMs);
        Check("history: average survives", Math.Abs(after.AvgMs - before.AvgMs) < 0.001);
        Check("history: jitter survives", Math.Abs(after.JitterMs - before.JitterMs) < 0.001);

        // The sparkline reads chronological order, so the shape has to come back the same way up.
        var recent = restored.RecentHistory(12);
        Check("history: chronological order preserved",
            recent.Length == 12 && recent[0].RttMs == 1 && recent[9].RttMs == 10);
        Check("history: failure statuses are preserved exactly",
            recent[10].Status == TargetStatus.Timeout && recent[11].Status == TargetStatus.HttpError);

        // Encoding edge cases: a corrupt sidecar costs the history, never the launch.
        Check("history: garbage decodes to nothing",
            StateStore.DecodeHistory("not,valid,data").Count == 0);
        Check("history: empty decodes to nothing", StateStore.DecodeHistory("").Count == 0);
        Check("history: a malformed pair is skipped, the rest kept",
            StateStore.DecodeHistory("1:5,rubbish,1:7").Count == 2);
        Check("history: an out-of-range status is skipped",
            StateStore.DecodeHistory("999:5").Count == 0);

        // Shrinking the window between runs must keep the newest samples, not the oldest.
        var narrow = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.1" },
                                    new Settings { RollingWindow = 10 });
        narrow.RestoreHistory(loaded["gw"]);

        var kept = narrow.RecentHistory(10);
        Check("history: a shrunk window keeps the newest samples",
            kept.Length == 10 && kept[^1].Status == TargetStatus.HttpError);

        target.Dispose();
        restored.Dispose();
        narrow.Dispose();
    }

    /// <summary>
    /// Maintenance windows: parsing, and the rule that decides what happens when one ends.
    /// </summary>
    private static void MaintenanceWindows()
    {
        // A Wednesday, so day handling is exercised rather than accidentally always matching.
        var wed0300 = new DateTimeOffset(new DateTime(2026, 8, 12, 3, 0, 0, DateTimeKind.Local));
        var wed1200 = new DateTimeOffset(new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Local));

        var nightly = MaintenanceSchedule.Parse("02:00-04:00");
        Check("maintenance: inside a daily window", nightly.Contains(wed0300));
        Check("maintenance: outside a daily window", !nightly.Contains(wed1200));

        // Boundaries: start inclusive, end exclusive, so back-to-back windows cannot overlap.
        Check("maintenance: the start minute is inside",
            nightly.Contains(new DateTimeOffset(new DateTime(2026, 8, 12, 2, 0, 0, DateTimeKind.Local))));
        Check("maintenance: the end minute is outside",
            !nightly.Contains(new DateTimeOffset(new DateTime(2026, 8, 12, 4, 0, 0, DateTimeKind.Local))));

        var byDay = MaintenanceSchedule.Parse("Wed 02:00-04:00");
        Check("maintenance: matches the named day", byDay.Contains(wed0300));
        Check("maintenance: ignores other days",
            !byDay.Contains(new DateTimeOffset(new DateTime(2026, 8, 13, 3, 0, 0, DateTimeKind.Local))));

        var weekdays = MaintenanceSchedule.Parse("Mon-Fri 01:00-02:00");
        Check("maintenance: a day range covers its middle",
            weekdays.Contains(new DateTimeOffset(new DateTime(2026, 8, 12, 1, 30, 0, DateTimeKind.Local))));
        Check("maintenance: a day range excludes the weekend",
            !weekdays.Contains(new DateTimeOffset(new DateTime(2026, 8, 15, 1, 30, 0, DateTimeKind.Local))));

        // Crossing midnight: the evening half belongs to the named day, the morning half to the
        // day after, which is what "Sat 22:00-02:00" plainly means.
        var overnight = MaintenanceSchedule.Parse("Sat 22:00-02:00");
        Check("maintenance: evening of the named day",
            overnight.Contains(new DateTimeOffset(new DateTime(2026, 8, 15, 23, 0, 0, DateTimeKind.Local))));
        Check("maintenance: small hours of the following day",
            overnight.Contains(new DateTimeOffset(new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Local))));
        Check("maintenance: not the small hours of the named day itself",
            !overnight.Contains(new DateTimeOffset(new DateTime(2026, 8, 15, 1, 0, 0, DateTimeKind.Local))));

        Check("maintenance: several windows in one setting",
            MaintenanceSchedule.Parse("02:00-03:00, 13:00-13:30").Contains(wed1200) == false
            && MaintenanceSchedule.Parse("02:00-03:00, 11:00-13:30").Contains(wed1200));

        // A typo must silence nothing. Failing open is the dangerous direction for a monitor.
        Check("maintenance: nonsense silences nothing", !MaintenanceSchedule.Parse("garbage").Contains(wed0300));
        Check("maintenance: an impossible time silences nothing",
            !MaintenanceSchedule.Parse("25:00-26:00").Contains(wed0300));
        Check("maintenance: a zero-length window silences nothing",
            !MaintenanceSchedule.Parse("02:00-02:00").Contains(wed0300));
        Check("maintenance: blank is empty", MaintenanceSchedule.Parse("").IsEmpty);

        // ---- the suppression rule ----
        var settings = new Settings { FailuresBeforeDown = 2 };
        var target = new PingTarget(new TargetConfig { Name = "nas", Address = "10.1.10.5" }, settings);
        var now = DateTimeOffset.Now;

        StateTransition? fired = null;
        for (var i = 0; i < 4; i++)
            fired ??= target.Record(ProbeResult.Fail(TargetStatus.Timeout, 1000 + i, now), 2,
                                    raiseTransitions: false);

        Check("maintenance: no alert while the window is open", fired is null);

        // The window closes and the host is still down: the alert must arrive now, not never.
        var afterWindow = target.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now), 2);
        Check("maintenance: a host still down when the window closes does alert",
            afterWindow is { Up: false });

        // And a host that recovered quietly during the window must not produce a stray "recovered".
        var other = new PingTarget(new TargetConfig { Name = "nas2", Address = "10.1.10.6" }, settings);
        for (var i = 0; i < 4; i++)
            other.Record(ProbeResult.Fail(TargetStatus.Timeout, 3000 + i, now), 2, raiseTransitions: false);

        var quietRecovery = other.Record(ProbeResult.Ok(4, System.Net.IPAddress.Loopback, 3100, now), 2,
                                         raiseTransitions: false);

        Check("maintenance: a quiet recovery raises nothing", quietRecovery is null);

        var afterQuietRecovery = other.Record(ProbeResult.Ok(4, System.Net.IPAddress.Loopback, 3200, now), 2);
        Check("maintenance: and nothing is left over to fire afterwards", afterQuietRecovery is null);

        target.Dispose();
        other.Dispose();
    }

    /// <summary>Availability over hours and days, and its persistence.</summary>
    private static void AvailabilityOverDays()
    {
        var log = new AvailabilityLog();
        var now = DateTimeOffset.Now;

        Check("availability: nothing recorded yields null, not 100%", log.Percent(24, now) is null);

        // Ten hours ago: 8 of 10 OK. Two hours ago: 10 of 10.
        for (var i = 0; i < 10; i++)
            log.Record(i < 8 ? TargetStatus.Ok : TargetStatus.Timeout, now.AddHours(-10));

        for (var i = 0; i < 10; i++)
            log.Record(TargetStatus.Ok, now.AddHours(-2));

        var day = log.Percent(24, now);
        Check("availability: 24h spans both buckets", day is not null && Math.Abs(day.Value - 90) < 0.001);

        // A window that excludes the older bucket sees only the perfect one.
        var recent = log.Percent(3, now);
        Check("availability: a shorter window excludes older buckets",
            recent is not null && Math.Abs(recent.Value - 100) < 0.001);

        // Paused and suspended are not evidence about the target; counting them would sink the
        // figure every time the machine slept.
        var ignoring = new AvailabilityLog();
        ignoring.Record(TargetStatus.Ok, now);
        ignoring.Record(TargetStatus.Suspended, now);
        ignoring.Record(TargetStatus.Paused, now);
        ignoring.Record(TargetStatus.Unknown, now);

        var clean = ignoring.Percent(24, now);
        Check("availability: inactive samples are excluded entirely",
            clean is not null && Math.Abs(clean.Value - 100) < 0.001);

        // Round-trip through the sidecar encoding.
        var restored = AvailabilityLog.Decode(log.Encode());
        var restoredDay = restored.Percent(24, now);
        Check("availability: survives an encode/decode round trip",
            restoredDay is not null && Math.Abs(restoredDay.Value - day!.Value) < 0.001);

        Check("availability: garbage decodes to nothing",
            AvailabilityLog.Decode("junk,1:2").Percent(24, now) is null);
        Check("availability: an impossible bucket is rejected",
            AvailabilityLog.Decode("100:50:10").Percent(24, now) is null);

        // Formatting. Both rules exist to stop the number flattering itself.
        Check("availability: a perfect score drops the decimals", AvailabilityLog.Format(100) == "100");
        Check("availability: no data is an em dash, not 100", AvailabilityLog.Format(null) == "—");

        // The one that matters: a target that dropped a probe did not have a perfect period, and
        // rounding it up to 100 would put a false claim in front of whoever reads the column.
        Check("availability: 99.996 does not round up to 100", AvailabilityLog.Format(99.996) == "99.99");
        Check("availability: 99.999 does not round up to 100", AvailabilityLog.Format(99.999) == "99.99");

        Check("availability: ordinary figures keep two decimals", AvailabilityLog.Format(99.5) == "99.50");
        Check("availability: a middling figure is unchanged", AvailabilityLog.Format(62.31) == "62.31");
        Check("availability: total failure reads as zero", AvailabilityLog.Format(0) == "0.00");

        // Beyond the ring, the oldest data is simply gone rather than wrapping into the present.
        var old = new AvailabilityLog();
        old.Record(TargetStatus.Timeout, now.AddHours(-(AvailabilityLog.MaxHours + 5)));
        Check("availability: data older than the ring does not resurface",
            old.Percent(AvailabilityLog.MaxHours, now) is null);
    }

    /// <summary>
    /// The ready-made host categories, and the discovery of this machine's own network.
    /// </summary>
    private static void HostCatalogIsUsable()
    {
        Check("catalog: has categories", HostCatalog.Categories.Count > 0);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allValid = true;
        var everyCategoryPopulated = true;

        foreach (var category in HostCatalog.Categories)
        {
            if (category.Entries.Count == 0) everyCategoryPopulated = false;

            foreach (var entry in category.Entries)
            {
                // Target names key the persisted counters, so a duplicate anywhere in the
                // catalogue would silently merge two hosts' statistics on import.
                if (!names.Add(entry.Name)) allValid = false;
                if (!addresses.Add(entry.Address)) allValid = false;

                if (entry.Name.Length == 0 || entry.Address.Length == 0) allValid = false;
                if (entry.Address.Contains(' ', StringComparison.Ordinal)) allValid = false;

                // An address must be a bare host, never a URL: the scheme comes from the probe
                // kind, and "https://x" would be handed to DNS verbatim.
                if (entry.Address.Contains("://", StringComparison.Ordinal)) allValid = false;
                if (entry.Address.Contains('/', StringComparison.Ordinal)) allValid = false;
            }
        }

        Check("catalog: every category has entries", everyCategoryPopulated);
        Check("catalog: names and addresses are unique and well formed", allValid);

        // Websites are probed over HTTPS on purpose - ICMP to a name like google.com lands on an
        // anycast edge and says nothing about the service.
        var websites = HostCatalog.Categories.First(c => c.Name == "Large websites");
        Check("catalog: websites use HTTPS rather than ping",
            websites.Entries.All(e => e.Probe == ProbeKind.Https));

        var dns = HostCatalog.Categories.First(c => c.Name == "Public DNS");
        Check("catalog: resolvers use ICMP", dns.Entries.All(e => e.Probe == ProbeKind.Icmp));
        Check("catalog: resolvers are literal addresses, not names",
            dns.Entries.All(e => System.Net.IPAddress.TryParse(e.Address, out _)));

        // Discovery runs against this machine's real adapters.
        var local = HostCatalog.DetectLocalNetwork();

        // Windows lists fec0:0:0:ffff::1-3 as placeholder IPv6 resolvers on almost every machine.
        // They answer nothing, so importing them would mean three permanently red rows.
        Check("catalog: no IPv6 site-local placeholder resolvers",
            local.All(e => !System.Net.IPAddress.Parse(e.Address).IsIPv6SiteLocal));
        Check("catalog: nothing detected is loopback or unspecified",
            local.All(e => !System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(e.Address))));
        Check("catalog: detected names are unique",
            local.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == local.Count);
        Check("catalog: detected addresses are unique",
            local.Select(e => e.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() == local.Count);

        Console.WriteLine($"        (detected {local.Count} local hosts: "
                          + string.Join(", ", local.Select(e => $"{e.Name}={e.Address}")) + ")");
    }

    /// <summary>
    /// The "while you were away" summary, which exists to catch the intermittent fault that
    /// resolves itself before anyone looks at the board.
    /// </summary>
    private static void WhileYouWereAway()
    {
        var journal = new TransitionJournal();
        var start = DateTimeOffset.Now.AddHours(-2);
        var now = DateTimeOffset.Now;

        // Silence when nothing happened. A message saying all is well costs attention and returns
        // none, which is the thing this whole application is trying not to do.
        Check("away: nothing happened means no message", journal.Summarise(start, now) is null);

        // Anything before the user left is not theirs to be told about.
        journal.Add(new StateTransition("old", Up: false, start.AddHours(-1), TimeSpan.Zero, TargetStatus.Timeout, 3));
        Check("away: transitions from before the window are ignored", journal.Summarise(start, now) is null);

        // A target that dropped and recovered - the case that otherwise leaves no trace at all.
        journal.Add(new StateTransition("nas", Up: false, start.AddMinutes(10), TimeSpan.Zero, TargetStatus.Timeout, 3));
        journal.Add(new StateTransition("nas", Up: true, start.AddMinutes(14), TimeSpan.FromMinutes(4), TargetStatus.Ok, 3));

        var recovered = journal.Summarise(start, now);
        Check("away: a recovered outage is reported", recovered is not null);
        Check("away: it names the target", recovered!.Contains("nas", StringComparison.Ordinal));
        Check("away: it gives the outage length", recovered.Contains("4m", StringComparison.Ordinal));
        Check("away: it says the target is back", recovered.Contains("now up", StringComparison.Ordinal));
        Check("away: it states how long the user was gone", recovered.Contains("2h", StringComparison.Ordinal));

        // A target still down matters more than one that recovered, and must read differently.
        journal.Add(new StateTransition("gateway", Up: false, start.AddMinutes(30), TimeSpan.Zero, TargetStatus.Timeout, 3));

        var stillDown = journal.Summarise(start, now)!;
        Check("away: a target still down is called out", stillDown.Contains("still down", StringComparison.Ordinal));
        Check("away: with the time it went", stillDown.Contains(start.AddMinutes(30).ToString("HH:mm"), StringComparison.Ordinal));

        // Repeated flapping is a different diagnosis from one long outage, so it reads differently.
        var flapper = new TransitionJournal();
        for (var i = 0; i < 3; i++)
        {
            flapper.Add(new StateTransition("wan", Up: false, start.AddMinutes(i * 10), TimeSpan.Zero, TargetStatus.Timeout, 3));
            flapper.Add(new StateTransition("wan", Up: true, start.AddMinutes((i * 10) + 2), TimeSpan.FromMinutes(2), TargetStatus.Ok, 3));
        }

        var flapping = flapper.Summarise(start, now)!;
        Check("away: repeated drops are counted, not collapsed",
            flapping.Contains("dropped 3 times", StringComparison.Ordinal));

        // A line naming forty hosts is dismissed, not read.
        var many = new TransitionJournal();
        for (var i = 0; i < 6; i++)
            many.Add(new StateTransition($"host{i}", Up: false, start.AddMinutes(i), TimeSpan.Zero, TargetStatus.Timeout, 3));

        var trimmed = many.Summarise(start, now, maxNamed: 3)!;
        Check("away: only the first few are named", trimmed.Contains("and 3 others", StringComparison.Ordinal));
        Check("away: a later host is not named", !trimmed.Contains("host5", StringComparison.Ordinal));

        // Bounded like every other buffer here. Asserted against the constant rather than a
        // literal, so raising the capacity is a decision rather than a test failure.
        var flood = new TransitionJournal();
        for (var i = 0; i < TransitionJournal.Capacity * 2; i++)
            flood.Add(new StateTransition($"t{i}", Up: false, start.AddSeconds(i), TimeSpan.Zero, TargetStatus.Timeout, 3));

        Check("away: the journal is capped", flood.Since(start).Count == TransitionJournal.Capacity);
        Check("away: and keeps the newest",
            flood.Since(start).Last().TargetName == $"t{TransitionJournal.Capacity * 2 - 1}");

        // Restoring must respect the same cap, or a file grown by an older build would overflow it.
        var restored = new TransitionJournal();
        restored.Restore(flood.Snapshot());
        Check("away: restore round-trips the journal",
            restored.Snapshot().Count == TransitionJournal.Capacity
            && restored.Snapshot()[^1].TargetName == flood.Snapshot()[^1].TargetName);

        // Soft transitions must not be read as outages. A certificate event is always Up:false and
        // never has a matching recovery, so counting it would both inflate the outage tally and
        // leave that host permanently "still down" in the banner.
        var soft = new TransitionJournal();
        soft.Add(new StateTransition("tls", false, start, TimeSpan.FromDays(60), TargetStatus.Degraded,
                                     200, TransitionKind.Certificate));
        soft.Add(new StateTransition("wan", false, start, TimeSpan.Zero, TargetStatus.Degraded,
                                     0, TransitionKind.Degraded));

        Check("away: a board with only soft events reports nothing",
            soft.Summarise(start.AddSeconds(-1), start.AddHours(1)) is null);

        soft.Add(new StateTransition("gw", false, start.AddMinutes(1), TimeSpan.Zero,
                                     TargetStatus.Timeout, 3));

        var mixedLine = soft.Summarise(start.AddSeconds(-1), start.AddHours(1));
        Check("away: a real outage alongside them is still reported",
            mixedLine is not null && mixedLine.Contains("gw", StringComparison.Ordinal));
        Check("away: the certificate host is not named as down",
            mixedLine is not null && !mixedLine.Contains("tls", StringComparison.Ordinal));
        Check("away: the degraded host is not named as down",
            mixedLine is not null && !mixedLine.Contains("wan", StringComparison.Ordinal));
    }

    private static void RingBufferRolls()
    {
        var ring = new RingBuffer(5);
        var now = DateTimeOffset.Now;

        // Overfill: the oldest entries must be discarded rather than the buffer growing.
        for (var i = 1; i <= 8; i++)
            ring.Add(ProbeResult.Ok(i * 10, System.Net.IPAddress.Loopback, i, now));

        var stats = ring.Stats();
        Check("ring: capacity is a hard cap", ring.Count == 5);
        Check("ring: min is from retained window only", stats.MinMs == 40);
        Check("ring: max tracks newest", stats.MaxMs == 80);
        Check("ring: avg over retained window", Math.Abs(stats.AvgMs - 60) < 0.001);
        Check("ring: zero loss when all ok", stats.LossPercent == 0);

        // Recent() must return chronological order after wraparound — the sparkline depends on it.
        var recent = ring.Recent(5);
        Check("ring: chronological after wrap",
            recent.Length == 5 && recent[0].RttMs == 40 && recent[4].RttMs == 80);

        ring.Clear();
        Check("ring: clear resets", ring.Count == 0 && !ring.Stats().HasData);
    }

    private static void RingBufferIgnoresInactiveSamples()
    {
        var ring = new RingBuffer(10);
        var now = DateTimeOffset.Now;

        ring.Add(ProbeResult.Ok(10, System.Net.IPAddress.Loopback, 1, now));
        ring.Add(ProbeResult.Fail(TargetStatus.Timeout, 2, now));
        // Suspended samples represent "we were asleep", not "the target was down". If these
        // counted, every laptop lid close would wreck the loss figure.
        ring.Add(ProbeResult.Fail(TargetStatus.Suspended, 3, now));
        ring.Add(ProbeResult.Fail(TargetStatus.Paused, 4, now));

        var stats = ring.Stats();
        Check("ring: suspended/paused excluded from window", stats.Samples == 2);
        Check("ring: loss is 50% of counted samples", Math.Abs(stats.LossPercent - 50) < 0.001);
        Check("ring: the one timeout is counted", stats.TimeoutSamples == 1);
    }

    /// <summary>
    /// Reproduces the report that started this: a target with a tight per-target timeout showed
    /// "max 332 ms" and was read as proof a 500 ms timeout had headroom to spare, while Loss% was
    /// quietly climbing. avg/min/max only ever see successful replies — a timed-out probe carries
    /// no RTT and is skipped by the max calculation entirely — so max can never exceed whatever the
    /// timeout is set to, no matter how bad the link actually is. TimeoutSamples exists to answer
    /// the question max cannot.
    /// </summary>
    private static void RingBufferTimeoutIsInvisibleToMinMax()
    {
        var ring = new RingBuffer(20);
        var now = DateTimeOffset.Now;
        long tick = 0;

        // Every successful reply lands comfortably under a 500 ms timeout.
        for (var i = 0; i < 6; i++)
            ring.Add(ProbeResult.Ok(280 + i * 8, System.Net.IPAddress.Loopback, tick++, now));

        // Two probes exceed it and time out — no RTT recorded, by construction.
        ring.Add(ProbeResult.Fail(TargetStatus.Timeout, tick++, now));
        ring.Add(ProbeResult.Fail(TargetStatus.Timeout, tick++, now));

        var stats = ring.Stats();

        Check("ring: max reflects only successful replies", stats.MaxMs == 320);
        Check("ring: timeouts are invisible to max, exactly the failure mode reported",
            stats.MaxMs < 500);
        Check("ring: but loss and the timeout count both show it",
            Math.Abs(stats.LossPercent - 25) < 0.001 && stats.TimeoutSamples == 2);

        // A refusal or an unreachable response is not "the timeout was too tight" and must not be
        // counted as one — the tooltip line this feeds would otherwise blame the wrong setting.
        ring.Clear();
        ring.Add(ProbeResult.Ok(50, System.Net.IPAddress.Loopback, tick++, now));
        ring.Add(ProbeResult.Fail(TargetStatus.Refused, tick++, now));
        ring.Add(ProbeResult.Fail(TargetStatus.Unreachable, tick++, now));
        ring.Add(ProbeResult.Fail(TargetStatus.DnsFail, tick++, now));

        var mixed = ring.Stats();
        Check("ring: only genuine timeouts count, not other failure kinds",
            mixed.Samples == 4 && mixed.TimeoutSamples == 0);
    }

    private static void StatusMapping()
    {
        // An unreachable destination arrives as a reply from a router, not as a timeout. Collapsing
        // the two would throw away the distinction between "nothing answered" and "something
        // actively said no".
        Check("status: host unreachable is not a timeout",
            ProbeResult.FromIpStatus(System.Net.NetworkInformation.IPStatus.DestinationHostUnreachable)
                == TargetStatus.Unreachable);
        Check("status: ttl expired is unreachable",
            ProbeResult.FromIpStatus(System.Net.NetworkInformation.IPStatus.TtlExpired)
                == TargetStatus.Unreachable);
        Check("status: timed out is timeout",
            ProbeResult.FromIpStatus(System.Net.NetworkInformation.IPStatus.TimedOut) == TargetStatus.Timeout);
        Check("status: success is ok",
            ProbeResult.FromIpStatus(System.Net.NetworkInformation.IPStatus.Success) == TargetStatus.Ok);

        // Regression guard. The Probe column tested "is it TCP" and called everything else icmp,
        // so every HTTP and HTTPS target reported the wrong probe once those kinds were added —
        // the board claiming to be doing something other than what it was actually doing.
        Check("probe kind: icmp labels itself", ProbeKind.Icmp.Label() == "icmp");
        Check("probe kind: tcp labels itself", ProbeKind.Tcp.Label() == "tcp");
        Check("probe kind: http is not mislabelled as icmp", ProbeKind.Http.Label() == "http");
        Check("probe kind: https is not mislabelled as icmp", ProbeKind.Https.Label() == "https");
        Check("probe kind: icmp has no port", !ProbeKind.Icmp.UsesPort());
        Check("probe kind: the others do", ProbeKind.Tcp.UsesPort() && ProbeKind.Https.UsesPort());
        Check("probe kind: http and https have conventional ports",
            ProbeKind.Http.DefaultPort() == 80 && ProbeKind.Https.DefaultPort() == 443);
        Check("probe kind: tcp has no conventional port, so its port is always shown",
            ProbeKind.Tcp.DefaultPort() == 0);

        // Column order lives in the App project, so only the invariant that matters off the UI
        // thread is checked here: the persisted form must always round-trip to every column
        // exactly once. A dropped id loses a column outright; a duplicated one puts two cells in
        // the same grid position.
        var ids = new[] { "Status", "Name", "Ip", "Rtt", "Loss" };
        var persisted = "Rtt,Loss,Status,Name,Ip";
        var restored = persisted.Split(',');

        Check("order: a saved arrangement lists every column once",
            restored.Length == ids.Length && restored.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length);
        Check("order: and contains no unknown column",
            restored.All(id => ids.Contains(id, StringComparer.OrdinalIgnoreCase)));

        Check("status: dns fail counts as a failure", TargetStatus.DnsFail.IsFailure());
        Check("status: suspended is not a failure", !TargetStatus.Suspended.IsFailure());
        Check("status: paused is not a failure", !TargetStatus.Paused.IsFailure());
        Check("status: unknown is not a failure", !TargetStatus.Unknown.IsFailure());

        // Settings.Validate must pull a hand-edited nonsense file back into range.
        var s = new Settings { IntervalMs = 5, TimeoutMs = 999_999, MaxConcurrent = 0, Ttl = 900 };
        s.Validate();
        Check("settings: clamps out-of-range values",
            s.IntervalMs >= 250 && s.MaxConcurrent >= 1 && s.Ttl <= 255);
        Check("settings: timeout pulled under interval", s.TimeoutMs <= s.IntervalMs);
    }

    /// <summary>
    /// Regression guard. A failing probe must never change the address shown for a target.
    /// <para>
    /// The original bug: the ICMP path reported <c>reply.Address</c>, which on failure is the
    /// machine that produced the ICMP error — the local host for DestinationHostUnreachable, or
    /// 0.0.0.0 on an async timeout. Recording it replaced the monitored address with a meaningless
    /// one, so a host going down also made the board forget where it lived.
    /// </para>
    /// </summary>
    private static void FailedProbeKeepsTargetAddress()
    {
        var settings = new Settings();
        var target = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.82" }, settings);
        var now = DateTimeOffset.Now;

        target.SetResolved(System.Net.IPAddress.Parse("10.1.10.82"));
        target.Record(ProbeResult.Ok(3, System.Net.IPAddress.Parse("10.1.10.82"), 1000, now),
                      settings.FailuresBeforeDown);

        Check("probe: address correct while up", target.Snapshot().DisplayIp == "10.1.10.82");

        // How the engine records a real timeout / unreachable: the probed address, not the responder.
        target.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now,
                          System.Net.NetworkInformation.IPStatus.TimedOut,
                          System.Net.IPAddress.Parse("10.1.10.82")),
                      settings.FailuresBeforeDown);

        Check("probe: address survives a failure", target.Snapshot().DisplayIp == "10.1.10.82");

        // And a result carrying no address at all must not blank the column either.
        target.Record(ProbeResult.Fail(TargetStatus.DnsFail, 3000, now), settings.FailuresBeforeDown);
        Check("probe: address survives a null-address result", target.Snapshot().DisplayIp == "10.1.10.82");

        target.Dispose();
    }

    /// <summary>
    /// The sleep / NIC-loss contract, which is the single most important guard in the app.
    /// <para>
    /// Sleeping the machine or dropping the adapter cannot be exercised here without severing the
    /// connection this session runs over, so this drives the same state transitions the OS events
    /// drive and asserts the consequences: while suspended nothing counts, nothing alerts, and
    /// on resume the board comes back without a burst of manufactured failures.
    /// </para>
    /// </summary>
    private static void SuspendFreezesCountersAndAlerts()
    {
        var settings = new Settings { FailuresBeforeDown = 3 };
        var scheduler = new ProbeScheduler(settings);

        var active = new PingTarget(new TargetConfig { Name = "active", Address = "10.1.10.1" }, settings);
        var paused = new PingTarget(
            new TargetConfig { Name = "paused", Address = "10.1.10.2", Enabled = false }, settings);

        scheduler.AddTarget(active);
        scheduler.AddTarget(paused);

        var transitions = 0;
        scheduler.Transition += _ => transitions++;

        var suspendEvents = 0;
        scheduler.SuspendChanged += (_, _) => suspendEvents++;

        var now = DateTimeOffset.Now;

        // Two failures: below the alert threshold, so no transition yet.
        active.Record(ProbeResult.Fail(TargetStatus.Timeout, 1000, now), settings.FailuresBeforeDown);
        active.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now), settings.FailuresBeforeDown);

        var nokBefore = active.Counters.NokCount;
        Check("suspend: failures counted while awake", nokBefore == 2);
        Check("suspend: below threshold raises no transition", transitions == 0);

        // The machine goes to sleep.
        scheduler.SetSuspended(true, "machine asleep");

        Check("suspend: status becomes Suspended", active.Snapshot().Status == TargetStatus.Suspended);
        Check("suspend: SuspendChanged fired once", suspendEvents == 1);

        // The failure streak must be cleared, so waking up cannot immediately trip the threshold
        // on the strength of failures recorded before the sleep.
        Check("suspend: failure streak cleared", active.Snapshot().ConsecutiveFailures == 0);
        Check("suspend: counters frozen, not reset", active.Counters.NokCount == nokBefore);

        // Redundant suspends must not re-fire the event.
        scheduler.SetSuspended(true, "machine asleep");
        Check("suspend: repeated suspend is a no-op", suspendEvents == 1);

        // Wake up.
        scheduler.SetSuspended(false, "");

        Check("resume: SuspendChanged fired again", suspendEvents == 2);
        Check("resume: enabled target returns to Unknown", active.Snapshot().Status == TargetStatus.Unknown);
        Check("resume: disabled target returns to Paused", paused.Snapshot().Status == TargetStatus.Paused);
        Check("resume: counters survived the sleep", active.Counters.NokCount == nokBefore);
        Check("resume: no transitions manufactured", transitions == 0);

        // And the threshold still works normally afterwards.
        for (var i = 0; i < settings.FailuresBeforeDown; i++)
            active.Record(ProbeResult.Fail(TargetStatus.Timeout, 3000 + i, now), settings.FailuresBeforeDown);

        Check("resume: alerting still works after a sleep", active.Snapshot().ConsecutiveFailures == 3);

        scheduler.RemoveTarget(active);
        scheduler.RemoveTarget(paused);
        _ = scheduler.DisposeAsync().AsTask().Wait(2000);
    }

    /// <summary>
    /// The alert threshold resolves per target, falling back to the global.
    /// <para>
    /// The value only means something relative to a target's interval — three failures at 5 s is
    /// fifteen seconds of silence, at 1 s it is three — so a single global number cannot serve a
    /// flaky WAN link and a LAN switch at the same time.
    /// </para>
    /// </summary>
    /// <summary>
    /// The value the reported bug turned on: a target's effective timeout is the per-target
    /// override when it has one, and the global default otherwise. Trivial in isolation, but it is
    /// exactly the number the tooltip fix reads to explain a Loss% climb, so getting it from the
    /// wrong place would make the new line lie just as confidently as max used to.
    /// </summary>
    private static void PerHostTimeoutOverride()
    {
        var settings = new Settings { TimeoutMs = 2000 };

        var overridden = new PingTarget(
            new TargetConfig { Name = "wan", Address = "10.2.10.10", TimeoutMs = 500 }, settings);
        Check("timeout: per-host override wins", overridden.TimeoutMsFrom(settings) == 500);

        var inherited = new PingTarget(
            new TargetConfig { Name = "lan", Address = "10.1.10.1" }, settings);
        Check("timeout: falls back to the global default", inherited.TimeoutMsFrom(settings) == 2000);

        overridden.Dispose();
        inherited.Dispose();
    }

    private static void PerHostFailureThreshold()
    {
        var settings = new Settings { FailuresBeforeDown = 3 };
        var now = DateTimeOffset.Now;

        // Tolerant host: overrides the global 3 with 8.
        var tolerant = new PingTarget(
            new TargetConfig { Name = "wan", Address = "10.2.10.10", FailuresBeforeDown = 8 }, settings);

        Check("threshold: per-host override wins", tolerant.FailuresBeforeDownFrom(settings) == 8);

        StateTransition? fired = null;
        for (var i = 1; i <= 7; i++)
            fired ??= tolerant.Record(
                ProbeResult.Fail(TargetStatus.Timeout, 1000 + i, now),
                tolerant.FailuresBeforeDownFrom(settings));

        Check("threshold: no alert below the per-host value", fired is null);

        fired = tolerant.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now),
                                tolerant.FailuresBeforeDownFrom(settings));

        Check("threshold: alert fires exactly at the per-host value", fired is { Up: false });
        Check("threshold: transition reports the value actually used", fired?.Threshold == 8);

        // Strict host: no override, so it must still use the global.
        var strict = new PingTarget(new TargetConfig { Name = "lan", Address = "10.1.10.1" }, settings);

        Check("threshold: unset inherits the global", strict.FailuresBeforeDownFrom(settings) == 3);

        StateTransition? strictFired = null;
        for (var i = 1; i <= 3; i++)
            strictFired ??= strict.Record(
                ProbeResult.Fail(TargetStatus.Timeout, 3000 + i, now),
                strict.FailuresBeforeDownFrom(settings));

        Check("threshold: global still applies when unset", strictFired is { Up: false, Threshold: 3 });

        tolerant.Dispose();
        strict.Dispose();
    }

    /// <summary>
    /// Regression guard. Lowering the alert threshold while a target is already failing must still
    /// announce the outage.
    /// <para>
    /// The original bug: the down transition fired on <c>streak == threshold</c> while recovery
    /// tested <c>streak &gt;= threshold</c>. Drop the threshold from 5 to 3 with a target sitting
    /// at four consecutive failures and the streak has already passed 3 without ever equalling it,
    /// so no down notification ever fires — but the recovery one still does. A "recovered" alert
    /// for an outage you were never told about is worse than no alert at all.
    /// </para>
    /// </summary>
    private static void ThresholdLoweredMidOutage()
    {
        var target = new PingTarget(new TargetConfig { Name = "wan", Address = "10.2.10.10" }, new Settings());
        var now = DateTimeOffset.Now;

        StateTransition? fired = null;
        for (var i = 1; i <= 4; i++)
            fired ??= target.Record(ProbeResult.Fail(TargetStatus.Timeout, 1000 + i, now), 5);

        Check("threshold: silent below the original value", fired is null);

        // The user lowers it to 3 from the settings dialog, mid-outage.
        fired = target.Record(ProbeResult.Fail(TargetStatus.Timeout, 2000, now), 3);
        Check("threshold: lowering it mid-outage still declares down", fired is { Up: false, Threshold: 3 });

        var again = target.Record(ProbeResult.Fail(TargetStatus.Timeout, 2100, now), 3);
        Check("threshold: down fires once per outage", again is null);

        var recovered = target.Record(
            ProbeResult.Ok(4, System.Net.IPAddress.Loopback, 3000, now), 3);
        Check("threshold: recovery pairs with the down that fired", recovered is { Up: true });

        // A second OK is not a second recovery.
        var quiet = target.Record(ProbeResult.Ok(4, System.Net.IPAddress.Loopback, 4000, now), 3);
        Check("threshold: no recovery without a preceding down", quiet is null);

        target.Dispose();
    }

    /// <summary>
    /// The concurrency ceiling must track <see cref="Settings.MaxConcurrent"/> and must not be
    /// rebuilt when it has not changed.
    /// <para>
    /// The original bug compared against <see cref="SemaphoreSlim.CurrentCount"/>, which reports
    /// permits still <em>available</em> rather than the configured maximum. Any settings apply that
    /// caught a probe in flight therefore looked like a change and swapped the semaphore out from
    /// under it; the probe then released a semaphore it never acquired, throwing inside its
    /// <c>finally</c> and leaking the outstanding-probe count upward for the life of the process.
    /// </para>
    /// <para>
    /// The in-flight half of that is fixed by construction — a probe now releases the instance it
    /// captured — which leaves the ceiling itself as the part worth asserting here.
    /// </para>
    /// </summary>
    private static void ConcurrencyCeilingFollowsSettings()
    {
        var scheduler = new ProbeScheduler(new Settings { MaxConcurrent = 4 });

        Check("concurrency: starts at the configured ceiling", scheduler.AvailableConcurrency == 4);

        scheduler.ApplySettings(new Settings { MaxConcurrent = 4 });
        Check("concurrency: re-applying an unchanged ceiling is a no-op", scheduler.AvailableConcurrency == 4);

        scheduler.ApplySettings(new Settings { MaxConcurrent = 9 });
        Check("concurrency: a changed ceiling takes effect", scheduler.AvailableConcurrency == 9);

        _ = scheduler.DisposeAsync().AsTask().Wait(2000);
    }

    /// <summary>
    /// Reproduces the reported symptom end to end: an HTTPS target whose certificate never gets
    /// read leaves both board columns blank with no explanation and no retry for hours, because
    /// <c>TryBeginCertCheck</c> commits the next attempt far in the future the moment it lets this
    /// one through — before the read has even happened.
    /// <para>
    /// An out-of-range port is the deterministic, network-free way to prove it: verified directly
    /// (in a throwaway harness against the real <c>CertificateCheck.InspectAsync</c>) to throw
    /// <see cref="ArgumentOutOfRangeException"/>, a type outside that method's own catch list. Real
    /// flaky-link failures — a connection reset mid-handshake, a server that answers but never
    /// speaks TLS — were checked the same way and are already caught safely inside InspectAsync
    /// with a normal Failed result, so they were never the bug; this is the narrower, confirmed gap
    /// behind it.
    /// </para>
    /// </summary>
    private static void CertificateReadFailureIsNeverSilent()
    {
        var settings = new Settings { IntervalMs = 250, TimeoutMs = 300, CertCheckHours = 1 };

        // A literal address, so DNS resolution is instant and cannot be the thing under test.
        var config = new TargetConfig
        {
            Name = "badport", Address = "127.0.0.1", Probe = ProbeKind.Https, Port = 70000,
        };

        var target = new PingTarget(config, settings);
        var scheduler = new ProbeScheduler(settings);
        scheduler.AddTarget(target);
        scheduler.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (target.Snapshot().Certificate is null && DateTime.UtcNow < deadline)
            Task.Delay(50).Wait();

        var cert = target.Snapshot().Certificate;

        Check("cert failure: an exception outside InspectAsync's own catches still records a result",
            cert is not null);
        Check("cert failure: recorded as a failure, not fabricated as a success",
            cert is { HasCertificate: false });
        Check("cert failure: the reason is visible rather than silent — this is the whole fix",
            cert is { Error.Length: > 0 });

        _ = scheduler.DisposeAsync().AsTask().Wait(2000);
        target.Dispose();
    }

    /// <summary>
    /// The webhook path, end to end, against a real listener on loopback.
    /// <para>
    /// Compiling is not evidence that an alert can leave the process. This queues a transition
    /// through the live dispatcher and asserts that a correctly shaped JSON body arrives at the
    /// other end — the one thing a user cannot check without an outage.
    /// </para>
    /// </summary>
    private static void WebhookDeliversATransition()
    {
        var prefix = $"http://localhost:{FreePort()}/";

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (System.Net.HttpListenerException)
        {
            // Binding a listener can be refused by policy on a locked-down machine. That is not a
            // failure of the code under test, but it must not read as a silent pass either.
            Check("alerts: SKIPPED — could not bind a local listener", false);
            return;
        }

        string? body = null;
        using var arrived = new ManualResetEventSlim(false);

        _ = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            context.Response.StatusCode = 200;
            context.Response.Close();
            arrived.Set();
        });

        var settings = new AlertSettings { WebhookEnabled = true, WebhookUrl = prefix, MinIntervalSeconds = 0 };
        settings.Validate();
        Check("alerts: a usable webhook url stays enabled", settings.WebhookEnabled);

        var dispatcher = new AlertDispatcher(settings);
        dispatcher.Enqueue(
            new StateTransition("gateway", Up: false, DateTimeOffset.Now, TimeSpan.Zero, TargetStatus.Timeout, 3),
            "10.1.10.1");

        Check("alerts: webhook actually delivered", arrived.Wait(TimeSpan.FromSeconds(10)));
        Check("alerts: payload names the target", Contains(body, "\"target\":\"gateway\""));
        Check("alerts: payload carries the event", Contains(body, "\"event\":\"down\""));
        Check("alerts: payload carries the probed address", Contains(body, "10.1.10.1"));
        Check("alerts: payload carries a human summary", Contains(body, "\"text\":\""));

        _ = dispatcher.DisposeAsync().AsTask().Wait(8000);
        listener.Stop();

        static bool Contains(string? haystack, string needle) =>
            haystack?.Contains(needle, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Credential handling and the "enabled but unusable" case.
    /// <para>
    /// A sink that is switched on but has nowhere to send is the failure mode that matters: the
    /// user believes they are covered, and nothing ever arrives to prove otherwise.
    /// </para>
    /// </summary>
    private static void AlertSecretsAndValidation()
    {
        const string secret = "correct-horse-battery-staple";

        var stored = ProtectedValue.Protect(secret);
        Check("alerts: secret is not stored in the clear",
            !stored.Contains(secret, StringComparison.Ordinal));
        Check("alerts: secret round-trips", ProtectedValue.Unprotect(stored) == secret);
        Check("alerts: a hand-typed plaintext value still works",
            ProtectedValue.Unprotect("typed-by-hand") == "typed-by-hand");
        Check("alerts: empty stays empty", ProtectedValue.Protect("").Length == 0);
        Check("alerts: undecryptable blob yields empty, not garbage",
            ProtectedValue.Unprotect("dpapi:bm90LWEtcmVhbC1ibG9i").Length == 0);

        // A DNS failure never resolved to anything, so the address is legitimately empty. The
        // summary must read as a fact about the network rather than as a bug in the alert.
        var unresolved = new AlertPayload("bad-name", "", "down", "DNS FAIL",
            DateTimeOffset.Now, 0, 3, "host");

        Check("alerts: no empty parens when the address is unknown",
            !unresolved.Summary().Contains("()", StringComparison.Ordinal));
        Check("alerts: unresolved target still names itself",
            unresolved.Summary().Contains("bad-name is DOWN", StringComparison.Ordinal));
        Check("alerts: body marks the address unresolved",
            unresolved.Body().Contains("(unresolved)", StringComparison.Ordinal));

        var resolved = new AlertPayload("gw", "10.1.10.1", "down", "TIMEOUT",
            DateTimeOffset.Now, 0, 3, "host");

        Check("alerts: a known address is still shown in parentheses",
            resolved.Summary().Contains("gw (10.1.10.1)", StringComparison.Ordinal));

        var badUrl = new AlertSettings { WebhookEnabled = true, WebhookUrl = "definitely not a url" };
        badUrl.Validate();
        Check("alerts: unusable webhook url disables the sink", !badUrl.WebhookEnabled);

        var noRecipient = new AlertSettings { EmailEnabled = true, SmtpHost = "smtp.example.com" };
        noRecipient.Validate();
        Check("alerts: email with no recipient disables the sink", !noRecipient.EmailEnabled);

        var clamped = new AlertSettings { SmtpPort = 0, MinIntervalSeconds = -5, TimeoutMs = 1 };
        clamped.Validate();
        Check("alerts: values clamped into range",
            clamped.SmtpPort >= 1 && clamped.MinIntervalSeconds >= 0 && clamped.TimeoutMs >= 1000);
    }

    /// <summary>
    /// Alert configuration survives a round trip, and — the part with real teeth — survives an
    /// autosave that knows nothing about it.
    /// <para>
    /// The board rewrites the config whenever a target is added or edited, and those paths carry no
    /// alert settings. Without the preserve-on-null contract in <see cref="ConfigStore.Save"/>, the
    /// first such save would silently delete the user's webhook and SMTP credentials.
    /// </para>
    /// </summary>
    private static void AlertConfigSurvivesAutosave(string dir)
    {
        var path = Path.Combine(dir, "alerts.ini");
        var targets = new List<TargetConfig> { new() { Name = "a", Address = "1.1.1.1" } };

        var alerts = new AlertSettings
        {
            WebhookEnabled = true,
            WebhookUrl = "https://hooks.example.com/abc",
            SmtpPassword = "s3cret-password",
            MinIntervalSeconds = 120,
            NotifyOnRecovery = false,
        };

        ConfigStore.Save(path, new Settings(), targets, alerts);
        var loaded = ConfigStore.Load(path);

        Check("alerts: config round-trips", loaded.Alerts.WebhookUrl == "https://hooks.example.com/abc"
                                            && loaded.Alerts.MinIntervalSeconds == 120
                                            && !loaded.Alerts.NotifyOnRecovery);

        Check("alerts: password never hits the file in the clear",
            !File.ReadAllText(path).Contains("s3cret-password", StringComparison.Ordinal));
        Check("alerts: password decrypts on load",
            ProtectedValue.Unprotect(loaded.Alerts.SmtpPassword) == "s3cret-password");

        // The autosave path: same file, no alert settings supplied.
        ConfigStore.Save(path, new Settings(), targets);
        var after = ConfigStore.Load(path);

        Check("alerts: an autosave without alert settings preserves them",
            after.Alerts.WebhookUrl == "https://hooks.example.com/abc");
        Check("alerts: an autosave preserves the credential too",
            ProtectedValue.Unprotect(after.Alerts.SmtpPassword) == "s3cret-password");
    }

    /// <summary>
    /// The failure trace, against real addresses on loopback and in TEST-NET-1.
    /// <para>
    /// The subtlety worth pinning is that <c>TtlExpired</c> is the <em>success</em> case for an
    /// intermediate hop — a router saying it decremented the TTL to zero is exactly what a trace
    /// asks for. Treating it as a failure is the classic way to get this wrong, and it produces a
    /// trace that is all asterisks on a perfectly healthy path.
    /// </para>
    /// </summary>
    private static void TraceRouteFindsThePath()
    {
        var options = new TraceOptions(MaxHops: 5, HopTimeoutMs: 1000, StopAfterSilentHops: 2);

        var loopback = TraceRoute
            .RunAsync("loopback", System.Net.IPAddress.Loopback, options, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("trace: loopback is reached", loopback.Reached);
        Check("trace: loopback is one hop", loopback.Hops.Count == 1);
        Check("trace: the hop answered", loopback.Hops[0].Answered);
        Check("trace: summary says the path is intact",
            loopback.Summary().Contains("path intact", StringComparison.Ordinal));

        // TEST-NET-1 is reserved and unroutable, so this must never report success. How far it
        // gets depends on the network the test runs on, which is why only "not reached" is asserted.
        var dead = TraceRoute
            .RunAsync("blackhole", System.Net.IPAddress.Parse("192.0.2.1"), options, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("trace: an unroutable address is not reported as reached", !dead.Reached);
        Check("trace: bounded by MaxHops", dead.Hops.Count <= 5);
        Check("trace: summary describes a break, not an intact path",
            !dead.Summary().Contains("path intact", StringComparison.Ordinal));

        // Silence must render as the traditional asterisk rather than an empty line.
        var silent = new TraceHop(7, null, ProbeResult.NoRtt, System.Net.NetworkInformation.IPStatus.TimedOut);
        Check("trace: an unanswered hop renders as *", silent.ToString().EndsWith('*'));

        var answered = new TraceHop(3, System.Net.IPAddress.Parse("10.1.10.1"), 4,
            System.Net.NetworkInformation.IPStatus.TtlExpired);
        Check("trace: an answered hop shows address and rtt",
            answered.ToString().Contains("10.1.10.1", StringComparison.Ordinal)
            && answered.ToString().Contains("4 ms", StringComparison.Ordinal));

        Check("trace: settings clamp hop count", ClampedHops() is >= 1 and <= 64);

        static int ClampedHops()
        {
            var s = new Settings { TraceMaxHops = 9999 };
            s.Validate();
            return s.TraceMaxHops;
        }
    }

    /// <summary>
    /// Tab grouping: config round-trip, backward compatibility, and the rule that matters — a tab
    /// is a view, not a scheduler.
    /// </summary>
    /// <summary>
    /// Site is a separate axis from Tab by design — a physical location rather than a functional
    /// grouping — and the two must never entangle: a target's tab is untouched by which site it
    /// names, and vice versa. The abbreviation is the whole point of the registry existing at all:
    /// two targets naming the same site must read identically, never drift into "Conn" on one and
    /// "Connaught" on the other depending on who typed what.
    /// </summary>
    private static void SitesAreIndependentOfTabs(string dir)
    {
        var path = Path.Combine(dir, "sites.ini");

        var targets = new List<TargetConfig>
        {
            new() { Name = "gw", Address = "10.1.10.1", Tab = "LAN", Site = "Connaught" },
            new() { Name = "wan", Address = "1.1.1.1", Tab = "WAN", Site = "Connaught" },
            new() { Name = "stray", Address = "8.8.8.8", Tab = "LAN" },      // no site at all
        };

        var sites = new List<SiteConfig> { new() { Name = "Connaught", Abbreviation = "Conn" } };

        ConfigStore.Save(path, new Settings(), targets, null, null, sites);
        var loaded = ConfigStore.Load(path);

        Check("sites: membership round-trips",
            loaded.Targets.First(t => t.Name == "gw").Site == "Connaught");
        Check("sites: abbreviation round-trips",
            loaded.Sites.First(s => s.Name == "Connaught").Abbreviation == "Conn");
        Check("sites: a site-free target has no Site value",
            loaded.Targets.First(t => t.Name == "stray").Site.Length == 0);
        Check("sites: a site-free target is not folded into any default",
            !loaded.Sites.Any(s => s.Name.Length == 0));

        // Independence from Tab, in both directions: two targets sharing a site sit in different
        // tabs, and two targets sharing a tab sit in different sites (or none).
        Check("sites: same site, different tabs — both preserved",
            loaded.Targets.First(t => t.Name == "gw").Tab == "LAN"
            && loaded.Targets.First(t => t.Name == "wan").Tab == "WAN");
        Check("sites: same tab, different sites — both preserved",
            loaded.Targets.First(t => t.Name == "stray").Tab == "LAN"
            && loaded.Targets.First(t => t.Name == "stray").Site.Length == 0);

        // Named only by membership: no [Site:...] section of its own, so the abbreviation cannot be
        // known — it must still exist, blank, rather than leaving the target with nowhere to point.
        var implied = Path.Combine(dir, "implied-site.ini");
        ConfigStore.Save(implied, new Settings(),
            [new TargetConfig { Name = "a", Address = "1.1.1.1", Site = "Northcliffe" }]);

        var impliedSite = ConfigStore.Load(implied).Sites.FirstOrDefault(s => s.Name == "Northcliffe");
        Check("sites: a site with no section is reconstructed from membership",
            impliedSite.Name == "Northcliffe" && impliedSite.Abbreviation.Length == 0);

        // Autosave paths pass no sites; they must not delete the registry, same contract as tabs.
        ConfigStore.Save(path, new Settings(), targets);
        Check("sites: an autosave without sites preserves the registry",
            ConfigStore.Load(path).Sites.First(s => s.Name == "Connaught").Abbreviation == "Conn");

        // A board that never used sites must round-trip without gaining a Site key or section.
        var legacy = Path.Combine(dir, "legacy-site.ini");
        ConfigStore.Save(legacy, new Settings(), [new TargetConfig { Name = "old", Address = "1.2.3.4" }]);
        var legacyText = File.ReadAllText(legacy);
        Check("sites: a site-free config gains no Site key",
            !legacyText.Contains("Site=", StringComparison.OrdinalIgnoreCase));
        Check("sites: a site-free config gains no [Site:] section",
            !legacyText.Contains("[Site:", StringComparison.OrdinalIgnoreCase));
    }

    private static void TabsGroupWithoutGating(string dir)
    {
        var path = Path.Combine(dir, "tabs.ini");

        var targets = new List<TargetConfig>
        {
            new() { Name = "gw", Address = "10.1.10.1", Tab = "LAN" },
            new() { Name = "wan", Address = "1.1.1.1", Tab = "WAN" },
            new() { Name = "stray", Address = "8.8.8.8" },     // no tab at all
        };

        var tabs = new List<TabConfig>
        {
            new() { Name = "LAN", Enabled = true, Order = 0 },
            new() { Name = "WAN", Enabled = false, Order = 1 },
        };

        ConfigStore.Save(path, new Settings(), targets, null, tabs);
        var loaded = ConfigStore.Load(path);

        Check("tabs: membership round-trips",
            loaded.Targets.First(t => t.Name == "gw").Tab == "LAN");
        Check("tabs: a disabled tab round-trips",
            loaded.Tabs.First(t => t.Name == "WAN").Enabled == false);
        Check("tabs: an untabbed target lands in the default group",
            loaded.Tabs.Any(t => t.Name == TabConfig.DefaultName));
        Check("tabs: no Tab key is written for an untabbed target",
            loaded.Targets.First(t => t.Name == "stray").Tab.Length == 0);

        // A tab named only by its members still has to exist, or those targets have nowhere to go.
        var implied = Path.Combine(dir, "implied.ini");
        ConfigStore.Save(implied, new Settings(),
            [new TargetConfig { Name = "a", Address = "1.1.1.1", Tab = "Servers" }]);

        Check("tabs: a tab with no section is reconstructed from membership",
            ConfigStore.Load(implied).Tabs.Any(t => t.Name == "Servers"));

        // Autosave paths pass no tabs; they must not delete the user's grouping.
        ConfigStore.Save(path, new Settings(), targets);
        Check("tabs: an autosave without tabs preserves them",
            ConfigStore.Load(path).Tabs.First(t => t.Name == "WAN").Enabled == false);

        // Tab order is state the user chose, not a default worth inferring. An earlier version
        // skipped writing sections for tabs that were merely enabled and in sequence, which meant
        // they were reconstructed on load from target membership — alphabetical — and the strip
        // came back in the wrong order.
        var ordered = Path.Combine(dir, "ordered.ini");
        var orderedTabs = new List<TabConfig>
        {
            new() { Name = "Zulu", Order = 0 },
            new() { Name = "Alpha", Order = 1 },
            new() { Name = "Mike", Order = 2 },
        };

        ConfigStore.Save(ordered, new Settings(),
        [
            new TargetConfig { Name = "a", Address = "1.1.1.1", Tab = "Alpha" },
            new TargetConfig { Name = "m", Address = "2.2.2.2", Tab = "Mike" },
            new TargetConfig { Name = "z", Address = "3.3.3.3", Tab = "Zulu" },
        ], null, orderedTabs);

        var reloadedTabs = ConfigStore.Load(ordered).Tabs;

        Check("tabs: declared order survives a round trip",
            reloadedTabs.Count == 3
            && reloadedTabs[0].Name == "Zulu"
            && reloadedTabs[1].Name == "Alpha"
            && reloadedTabs[2].Name == "Mike");

        // A board that never used tabs must round-trip without gaining a Tab key.
        var legacy = Path.Combine(dir, "legacy.ini");
        ConfigStore.Save(legacy, new Settings(), [new TargetConfig { Name = "old", Address = "1.2.3.4" }]);
        Check("tabs: a tab-free config gains no Tab key",
            !File.ReadAllText(legacy).Contains("Tab=", StringComparison.OrdinalIgnoreCase));

        // Muting is not disabling, and the difference is the point: a muted tab keeps probing and
        // keeps its history, and only the alert is withheld.
        var muteTabs = new List<TabConfig> { new() { Name = "Noisy", Enabled = true, Muted = true, Order = 0 } };
        var mutePath = Path.Combine(dir, "muted.ini");

        ConfigStore.Save(mutePath, new Settings(),
            [new TargetConfig { Name = "flaky", Address = "1.2.3.4", Tab = "Noisy" }], null, muteTabs);

        var mutedBack = ConfigStore.Load(mutePath).Tabs.First(t => t.Name == "Noisy");
        Check("tabs: muted round-trips", mutedBack.Muted);
        Check("tabs: a muted tab is still enabled", mutedBack.Enabled);

        var noisy = new PingTarget(new TargetConfig { Name = "flaky", Address = "1.2.3.4", Tab = "Noisy" },
                                   new Settings { FailuresBeforeDown = 2 });
        noisy.TabMuted = true;

        Check("tabs: a muted target is still probed", noisy.IsActive);

        var quietNow = DateTimeOffset.Now;
        StateTransition? mutedAlert = null;
        for (var i = 0; i < 4; i++)
            mutedAlert ??= noisy.Record(ProbeResult.Fail(TargetStatus.Timeout, 4000 + i, quietNow), 2,
                                        raiseTransitions: false);

        Check("tabs: a muted tab raises no alert", mutedAlert is null);
        Check("tabs: but the history still records it", noisy.Snapshot().Stats.Samples == 4);
        Check("tabs: and the counters still move", noisy.Counters.NokCount == 4);

        // Unmuting a tab whose host is still down must alert, exactly as leaving a maintenance
        // window does - otherwise the outage is silently forgotten.
        var afterUnmute = noisy.Record(ProbeResult.Fail(TargetStatus.Timeout, 5000, quietNow), 2);
        Check("tabs: unmuting a still-down host alerts then", afterUnmute is { Up: false });

        noisy.Dispose();

        // The load-bearing rule: disabling a tab pauses its targets, and that is entirely separate
        // from a target the user paused by hand.
        var settings = new Settings();
        var host = new PingTarget(new TargetConfig { Name = "gw", Address = "10.1.10.1", Tab = "WAN" }, settings);

        Check("tabs: active while both the target and its tab are enabled", host.IsActive);

        host.TabEnabled = false;
        Check("tabs: disabling the tab deactivates the target", !host.IsActive);

        host.TabEnabled = true;
        Check("tabs: re-enabling the tab reactivates it", host.IsActive);

        // Re-enabling a tab must not resurrect a host the user paused individually.
        var paused = new TargetConfig { Name = "gw", Address = "10.1.10.1", Tab = "WAN", Enabled = false };
        host.UpdateConfig(paused);
        host.TabEnabled = false;
        host.TabEnabled = true;

        Check("tabs: re-enabling a tab does not un-pause a hand-paused host", !host.IsActive);

        host.Dispose();
    }

    /// <summary>
    /// Release-tag parsing, which is what decides whether the app offers to replace itself.
    /// <para>
    /// Getting this wrong in either direction is bad: too eager and it nags about an upgrade that
    /// is not one, too lax and a real release goes unnoticed. The network half is not exercised
    /// here — it needs GitHub — but the comparison is pure and worth pinning.
    /// </para>
    /// </summary>
    private static void UpdateVersionComparison()
    {
        Check("update: plain tag parses", UpdateCheck.ParseVersion("1.3.0") == new Version(1, 3, 0));
        Check("update: leading v is tolerated", UpdateCheck.ParseVersion("v1.3.0") == new Version(1, 3, 0));
        Check("update: two-part tag implies a zero patch",
            UpdateCheck.ParseVersion("v2.1") == new Version(2, 1, 0));
        Check("update: junk yields null", UpdateCheck.ParseVersion("not-a-release") is null);
        Check("update: empty yields null", UpdateCheck.ParseVersion("") is null);

        // Ordering is what "is an update available" actually means.
        Check("update: a later minor is newer",
            UpdateCheck.ParseVersion("v1.4.0") > UpdateCheck.ParseVersion("v1.3.9"));
        Check("update: a later patch is newer",
            UpdateCheck.ParseVersion("v1.3.1") > UpdateCheck.ParseVersion("v1.3.0"));
        Check("update: the same version is not an update",
            UpdateCheck.ParseVersion("v1.3.0") == UpdateCheck.ParseVersion("1.3.0"));
        Check("update: an older tag is not an update",
            UpdateCheck.ParseVersion("v1.2.0") < UpdateCheck.ParseVersion("v1.3.0"));
        Check("update: 10 sorts above 9, not below",
            UpdateCheck.ParseVersion("v1.10.0") > UpdateCheck.ParseVersion("v1.9.0"));
    }

    /// <summary>
    /// The HTTP probe, against a real listener.
    /// <para>
    /// The case that justifies the whole probe type is the third one: a server that accepts the
    /// connection and answers 500. A TCP probe reports that host as perfectly healthy, because the
    /// socket opened — and a monitor that shows green while the service is broken is worse than no
    /// monitor, because it is trusted.
    /// </para>
    /// </summary>
    private static void HttpProbeJudgesTheStatusCode()
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (System.Net.HttpListenerException)
        {
            Check("http: SKIPPED - could not bind a local listener", false);
            return;
        }

        // Whatever the last request identified itself as, so the probe's manners can be asserted
        // rather than assumed.
        string? seenAgent = null;

        // Answers by path: /ok -> 200, /boom -> 500, /moved -> 302.
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    seenAgent = context.Request.UserAgent;
                    context.Response.StatusCode = context.Request.Url?.AbsolutePath switch
                    {
                        "/boom" => 500,
                        "/moved" => 302,
                        _ => 200,
                    };
                    context.Response.Close();
                }
                catch (Exception) { return; }
            }
        });

        var probe = new HttpProbe(useTls: false);
        var address = System.Net.IPAddress.Loopback;

        ProbeResult Run(string path, int expect = 0) => probe
            .ProbeAsync(address,
                        new ProbeOptions(TimeoutMs: 5000, PayloadBytes: 0, Ttl: 64, Port: port,
                                         Host: "localhost", Path: path, ExpectStatus: expect),
                        CancellationToken.None)
            .GetAwaiter().GetResult();

        var ok = Run("/ok");
        Check("http: 200 is OK", ok.Status == TargetStatus.Ok);
        Check("http: a successful probe records a round-trip time", ok.HasRtt);

        // The point of the probe type.
        var boom = Run("/boom");
        Check("http: 500 is a failure, not a success", boom.Status != TargetStatus.Ok);
        Check("http: 500 reports HTTP ERR rather than a network fault",
            boom.Status == TargetStatus.HttpError);
        Check("http: an HTTP error counts as a failure", boom.Status.IsFailure());

        // A redirect is a real answer about this URL and is accepted by default.
        Check("http: 302 is accepted by default", Run("/moved").Status == TargetStatus.Ok);

        // ...but not when a specific code was demanded.
        Check("http: 302 fails when 200 was required",
            Run("/moved", expect: 200).Status == TargetStatus.HttpError);
        Check("http: the demanded code still passes",
            Run("/ok", expect: 200).Status == TargetStatus.Ok);

        // Nothing listening on a port that was free a moment ago.
        var deadPort = FreePort();
        var refused = probe.ProbeAsync(address,
            new ProbeOptions(TimeoutMs: 3000, PayloadBytes: 0, Ttl: 64, Port: deadPort,
                             Host: "localhost", Path: "/"),
            CancellationToken.None).GetAwaiter().GetResult();

        Check("http: a closed port is not reported as an HTTP error",
            refused.Status is TargetStatus.Refused or TargetStatus.Unreachable or TargetStatus.Timeout);

        // The probe must identify itself. Sending no User-Agent is not merely impolite for
        // something that will hit a third-party server every few seconds for months — several
        // large sites answer 403, so the board manufactures an outage for a service that is
        // working. Wikipedia did exactly that, which is how this was found.
        Check("http: the probe sends a User-Agent", !string.IsNullOrWhiteSpace(seenAgent));
        Check("http: it names the product and where to complain",
            seenAgent is not null
            && seenAgent.StartsWith("PingBoard/", StringComparison.Ordinal)
            && seenAgent.Contains("github.com/hkrob/PingBoard", StringComparison.Ordinal));

        listener.Stop();
        probe.Dispose();
    }

    /// <summary>A port the OS has just confirmed is free, so the test does not collide with a real service.</summary>
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ------------------------------------------------------------ degraded state

    private static void RecentStatsWindow()
    {
        var ring = new RingBuffer(100);
        var now = DateTimeOffset.Now;

        // Twenty fast replies, then five slow ones.
        for (var i = 0; i < 20; i++) ring.Add(ProbeResult.Ok(10, System.Net.IPAddress.Loopback, i, now));
        for (var i = 0; i < 5; i++) ring.Add(ProbeResult.Ok(500, System.Net.IPAddress.Loopback, 20 + i, now));

        var all = ring.Stats();
        var recent = ring.RecentStats(5);

        Check("recent: full window is dominated by the fast samples", all.AvgMs < 120);
        Check("recent: short window sees only the slow ones", Math.Abs(recent.AvgMs - 500) < 0.001);
        Check("recent: sample count is the window, not the buffer", recent.Samples == 5);

        // The whole point of the short window: it must react while the long one still looks fine.
        Check("recent: windows genuinely disagree", recent.AvgMs > all.AvgMs * 2);

        Check("recent: asking for more than exists yields everything", ring.RecentStats(1000).Samples == 25);
        Check("recent: zero-width window has no data", !ring.RecentStats(0).HasData);
    }

    private static void DegradedState()
    {
        var settings = new Settings { RollingWindow = 100 };
        var config = new TargetConfig { Name = "slow", Address = "10.0.0.1" };
        using var target = new PingTarget(config, settings);

        var now = DateTimeOffset.Now;
        var tick = 0L;

        StateTransition? Feed(int rttMs, DegradeThresholds thresholds, int count = 1)
        {
            StateTransition? last = null;
            for (var i = 0; i < count; i++)
            {
                var result = ProbeResult.Ok(rttMs, System.Net.IPAddress.Loopback, tick += 1000, now);
                var t = target.Record(result, 3, thresholds: thresholds);
                if (t is not null) last = t;
            }
            return last;
        }

        // Off by default: the parameterless overload must behave exactly as it did before the
        // state existed, or every existing caller silently changes meaning.
        Feed(5000, default, 10);
        Check("degraded: off by default", target.Snapshot().Status == TargetStatus.Ok);

        var thresholds = new DegradeThresholds(LatencyMs: 100, LossPercent: 0, Samples: 20);

        // Below the minimum sample count, nothing may be judged.
        using var fresh = new PingTarget(new TargetConfig { Name = "fresh", Address = "10.0.0.2" }, settings);
        var early = fresh.Record(
            ProbeResult.Ok(9000, System.Net.IPAddress.Loopback, 1, now), 3, thresholds: thresholds);

        Check("degraded: refuses to judge on one sample",
            early is null && fresh.Snapshot().Status == TargetStatus.Ok);

        var entered = Feed(5000, thresholds, 10);

        Check("degraded: enters once past the threshold",
            target.Snapshot().Status == TargetStatus.Degraded);
        Check("degraded: entering raises a soft transition",
            entered is { Up: false, Kind: TransitionKind.Degraded });

        var again = Feed(5000, thresholds, 5);
        Check("degraded: does not re-announce while it persists", again is null);

        // Still an "up" state everywhere it matters.
        var snap = target.Snapshot();
        Check("degraded: counts as OK", snap.Status.IsOk());
        Check("degraded: is not healthy", !snap.Status.IsHealthy());
        Check("degraded: no failures recorded", snap.NokCount == 0 && snap.ConsecutiveFailures == 0);
        Check("degraded: availability is unharmed",
            target.Availability.Percent(24, now) is { } pct && pct > 99.9);

        var cleared = Feed(5, thresholds, 25);
        Check("degraded: clears when the window recovers",
            target.Snapshot().Status == TargetStatus.Ok);
        Check("degraded: clearing raises the closing transition",
            cleared is { Up: true, Kind: TransitionKind.Degraded });

        // Loss, rather than latency, must be able to trigger it on its own.
        using var lossy = new PingTarget(new TargetConfig { Name = "lossy", Address = "10.0.0.3" }, settings);
        var lossThresholds = new DegradeThresholds(LatencyMs: 0, LossPercent: 10, Samples: 20);
        var lossTick = 0L;

        for (var i = 0; i < 20; i++)
        {
            var result = i % 4 == 0
                ? ProbeResult.Fail(TargetStatus.Timeout, lossTick += 1000, now)
                : ProbeResult.Ok(5, System.Net.IPAddress.Loopback, lossTick += 1000, now);

            lossy.Record(result, failuresBeforeDown: 99, thresholds: lossThresholds);
        }

        Check("degraded: loss alone can trigger it", lossy.Snapshot().Status == TargetStatus.Degraded);

        // A hard outage must take precedence and reset the soft latch.
        using var falls = new PingTarget(new TargetConfig { Name = "falls", Address = "10.0.0.4" }, settings);
        // Enough samples that this run can be judged at all — the restored-history gate wants a
        // full window before it will decide anything, and ten leaves it silent.
        var fallTick = 0L;
        for (var i = 0; i < 25; i++)
            falls.Record(ProbeResult.Ok(5000, System.Net.IPAddress.Loopback, fallTick += 1000, now),
                         3, thresholds: thresholds);

        StateTransition? down = null;
        for (var i = 0; i < 3; i++)
        {
            var t = falls.Record(ProbeResult.Fail(TargetStatus.Timeout, fallTick += 1000, now),
                                 3, thresholds: thresholds);
            if (t is not null) down = t;
        }

        Check("degraded: a real outage still fires as a hard transition",
            down is { Up: false, Kind: TransitionKind.Hard });

        // Recovering into a degraded state is two pieces of news on one probe, and an overnight
        // soak showed the second was being thrown away: three outages on a permanently degraded
        // host produced one degraded event between them, because the hard transition won the
        // single return value and the latch was left set behind it.
        var recovery = falls.Record(
            ProbeResult.Ok(5000, System.Net.IPAddress.Loopback, fallTick += 1000, now),
            3, true, thresholds, out var alsoDegraded);

        Check("degraded: recovery is still the headline",
            recovery is { Up: true, Kind: TransitionKind.Hard });
        Check("degraded: and the degradation it recovered into is reported too",
            alsoDegraded is { Up: false, Kind: TransitionKind.Degraded });
        Check("degraded: the board shows the degraded state, not plain OK",
            falls.Snapshot().Status == TargetStatus.Degraded);

        // An ordinary degraded entry, with no outage attached, must still arrive as the primary
        // rather than being duplicated into both slots.
        using var plain = new PingTarget(
            new TargetConfig { Name = "plain", Address = "10.0.0.6" }, settings);

        StateTransition? plainSoft = null;
        StateTransition? plainPrimary = null;
        var plainTick = 0L;

        for (var i = 0; i < 25; i++)
        {
            var t = plain.Record(
                ProbeResult.Ok(5000, System.Net.IPAddress.Loopback, plainTick += 1000, now),
                3, true, thresholds, out var s);

            if (t is not null) plainPrimary = t;
            if (s is not null) plainSoft = s;
        }

        Check("degraded: a plain degraded entry is the primary transition",
            plainPrimary is { Kind: TransitionKind.Degraded });
        Check("degraded: and is not also reported as a secondary", plainSoft is null);

        // Restored history must not be judged. A board restarted after a week would otherwise
        // decide a target is degraded on the strength of how it behaved last Tuesday, and alert
        // about it - the restored samples fill the whole window before a single new probe lands.
        using var restored = new PingTarget(
            new TargetConfig { Name = "restored", Address = "10.0.0.5" }, settings);

        var stale = new List<ProbeResult>();
        for (var i = 0; i < 50; i++)
            stale.Add(ProbeResult.Ok(9000, System.Net.IPAddress.Loopback, i * 1000, now.AddDays(-7)));

        restored.RestoreHistory(stale);

        var firstAfterRestore = restored.Record(
            ProbeResult.Ok(9000, System.Net.IPAddress.Loopback, 1, now), 3, thresholds: thresholds);

        Check("degraded: a restored window is not judged on the first new probe",
            firstAfterRestore is null && restored.Snapshot().Status == TargetStatus.Ok);

        // Once this run has supplied a full window of its own, judgement resumes normally.
        for (var i = 0; i < thresholds.Samples; i++)
            restored.Record(ProbeResult.Ok(9000, System.Net.IPAddress.Loopback, i * 1000, now),
                            3, thresholds: thresholds);

        Check("degraded: judgement resumes once the window is this run's own",
            restored.Snapshot().Status == TargetStatus.Degraded);

        // And the statistics themselves still span the restart, which is the whole point of
        // persisting history - only the live verdict waits.
        Check("degraded: restored samples still feed the displayed statistics",
            restored.Snapshot().Stats.Samples > thresholds.Samples);
    }

    private static void DegradedThresholdsRoundTrip(string dir)
    {
        var path = Path.Combine(dir, "degraded.ini");

        var settings = new Settings
        {
            DegradedLatencyMs = 250,
            DegradedLossPercent = 2.5,
            DegradedSamples = 40,
            CertWarnDays = 21,
            OutageLogEnabled = false,
        };

        var targets = new List<TargetConfig>
        {
            new() { Name = "wan", Address = "1.1.1.1", DegradedLatencyMs = 80, DegradedLossPercent = 0.5 },

            // Zero is a real value here — "off for this host" — and must not be mistaken for unset.
            new() { Name = "satellite", Address = "8.8.8.8", DegradedLatencyMs = 0 },
        };

        ConfigStore.Save(path, settings, targets);
        var loaded = ConfigStore.Load(path);

        Check("degraded ini: global latency survives", loaded.Settings.DegradedLatencyMs == 250);
        Check("degraded ini: fractional loss survives",
            Math.Abs(loaded.Settings.DegradedLossPercent - 2.5) < 0.001);
        Check("degraded ini: sample window survives", loaded.Settings.DegradedSamples == 40);
        Check("degraded ini: cert warning survives", loaded.Settings.CertWarnDays == 21);
        Check("degraded ini: outage log toggle survives", !loaded.Settings.OutageLogEnabled);

        var wan = loaded.Targets.Single(t => t.Name == "wan");
        Check("degraded ini: per-target latency survives", wan.DegradedLatencyMs == 80);
        Check("degraded ini: per-target fractional loss survives",
            wan.DegradedLossPercent is { } loss && Math.Abs(loss - 0.5) < 0.001);

        var satellite = loaded.Targets.Single(t => t.Name == "satellite");
        Check("degraded ini: an explicit zero is not read as inherit", satellite.DegradedLatencyMs == 0);
        Check("degraded ini: an absent override stays null", satellite.DegradedLossPercent is null);
    }

    // ------------------------------------------------------------ outage log

    private static void OutagePairing()
    {
        var journal = new TransitionJournal();
        var start = DateTimeOffset.Now.AddHours(-3);

        journal.Add(new StateTransition("gw", false, start, TimeSpan.Zero, TargetStatus.Timeout, 3));
        journal.Add(new StateTransition("gw", true, start.AddMinutes(4), TimeSpan.FromMinutes(4),
                                        TargetStatus.Ok, 3));

        // A second, still open.
        journal.Add(new StateTransition("wan", false, start.AddHours(1), TimeSpan.Zero,
                                        TargetStatus.Unreachable, 3));

        // A recovery whose opening half has aged out of the buffer.
        journal.Add(new StateTransition("orphan", true, start.AddHours(2), TimeSpan.FromMinutes(9),
                                        TargetStatus.Ok, 3));

        var now = start.AddHours(3);
        var outages = journal.Outages(now);

        Check("outages: three events pair into three outages", outages.Count == 3);
        Check("outages: newest first", outages[0].Start >= outages[^1].Start);

        var gw = outages.Single(o => o.TargetName == "gw");
        Check("outages: a closed outage carries its duration",
            !gw.Ongoing && Math.Abs(gw.Duration.TotalMinutes - 4) < 0.001);
        Check("outages: the cause is the status that opened it", gw.Cause == TargetStatus.Timeout);

        var wan = outages.Single(o => o.TargetName == "wan");
        Check("outages: an unclosed outage is ongoing", wan.Ongoing && wan.End is null);
        Check("outages: an ongoing outage is aged to now",
            Math.Abs(wan.Duration.TotalHours - 2) < 0.01);

        var orphan = outages.Single(o => o.TargetName == "orphan");
        Check("outages: a recovery without its start is reconstructed backwards",
            !orphan.Ongoing && Math.Abs((orphan.End!.Value - orphan.Start).TotalMinutes - 9) < 0.001);
        Check("outages: a reconstructed outage admits it does not know the cause",
            orphan.Cause == TargetStatus.Unknown);

        // The case a 96-minute soak actually produced: one flapping target generated 1,900
        // transitions and swept the ring clean, taking with it the "went down" of a host that was
        // still down. The outage log then showed nothing but the flapper — losing the one outage
        // nobody had fixed yet, which is exactly when the log is worth opening.
        var buried = new TransitionJournal();
        buried.Add(new StateTransition("dead-host", false, start, TimeSpan.Zero, TargetStatus.Timeout, 2));

        for (var i = 0; i < TransitionJournal.Capacity * 2; i++)
        {
            var up = i % 2 == 1;
            buried.Add(new StateTransition("flapper", up, start.AddSeconds(i + 1),
                up ? TimeSpan.FromSeconds(2) : TimeSpan.Zero,
                up ? TargetStatus.Ok : TargetStatus.Timeout, 2));
        }

        Check("outages: the flapper has swept the ring",
            buried.Snapshot().All(t => t.TargetName == "flapper"));

        var afterFlood = buried.Outages(now);
        var survivor = afterFlood.FirstOrDefault(o => o.TargetName == "dead-host");

        Check("outages: an outage still running survives the flood",
            survivor.TargetName == "dead-host" && survivor.Ongoing);
        Check("outages: and is still aged from when it actually started",
            survivor.Start == start && survivor.Duration == now - start);
        Check("outages: the flapper's own closed history still ages out normally",
            afterFlood.Count(o => o.TargetName == "flapper") < TransitionJournal.Capacity);

        // Persistence must keep it too, or the file loses what the ring protects.
        Check("outages: the persisted snapshot carries the evicted open outage",
            buried.SnapshotForPersist().Any(t => t.TargetName == "dead-host" && !t.Up));
        Check("outages: the plain ring snapshot still means the ring",
            buried.Snapshot().Count == TransitionJournal.Capacity);

        // And it must not survive its own recovery.
        buried.Add(new StateTransition("dead-host", true, now, now - start, TargetStatus.Ok, 2));
        Check("outages: recovery closes the pinned outage",
            buried.Outages(now).Count(o => o.TargetName == "dead-host" && o.Ongoing) == 0);
        Check("outages: and it is no longer pinned for persistence",
            buried.SnapshotForPersist().Count(t => t.TargetName == "dead-host" && !t.Up) == 0);

        // A restart must bring the pin back, or the first eviction after startup drops it again.
        var reloaded = new TransitionJournal();
        var withOpen = new TransitionJournal();
        withOpen.Add(new StateTransition("still-down", false, start, TimeSpan.Zero, TargetStatus.Timeout, 2));
        for (var i = 0; i < TransitionJournal.Capacity * 2; i++)
            withOpen.Add(new StateTransition("noise", i % 2 == 1, start.AddSeconds(i + 1),
                TimeSpan.Zero, TargetStatus.Timeout, 2));

        reloaded.Restore(withOpen.SnapshotForPersist());
        Check("outages: a restart restores the pin",
            reloaded.Outages(now).Any(o => o.TargetName == "still-down" && o.Ongoing));

        // Degraded periods pair separately, so a host that is slow and then down keeps both.
        var mixed = new TransitionJournal();
        mixed.Add(new StateTransition("both", false, start, TimeSpan.Zero, TargetStatus.Degraded, 0,
                                      TransitionKind.Degraded));
        mixed.Add(new StateTransition("both", false, start.AddMinutes(1), TimeSpan.Zero,
                                      TargetStatus.Timeout, 3));

        var mixedOutages = mixed.Outages(now);
        Check("outages: degraded and down are separate events for one target",
            mixedOutages.Count == 2
            && mixedOutages.Any(o => o.Kind == TransitionKind.Degraded)
            && mixedOutages.Any(o => o.Kind == TransitionKind.Hard));
    }

    private static void OutageStoreRoundTrip(string dir)
    {
        var path = Path.Combine(dir, "outages.csv");
        var store = new OutageStore(path);
        var when = DateTimeOffset.Now.AddMinutes(-30);

        // A name with a comma and a quote, to prove the escaping survives a round trip.
        var awkward = "site \"A\", north";

        store.Append(new StateTransition(awkward, false, when, TimeSpan.Zero, TargetStatus.Timeout, 3));
        store.Append(new StateTransition(awkward, true, when.AddMinutes(2), TimeSpan.FromMinutes(2),
                                         TargetStatus.Ok, 3));
        store.Append(new StateTransition("tls", false, when, TimeSpan.FromDays(9),
                                         TargetStatus.Degraded, 14, TransitionKind.Certificate));

        var reloaded = new OutageStore(path).Load();

        Check("outage store: every line comes back", reloaded.Count == 3);
        Check("outage store: quoted names survive", reloaded[0].TargetName == awkward);
        Check("outage store: direction survives", !reloaded[0].Up && reloaded[1].Up);
        Check("outage store: duration survives",
            Math.Abs(reloaded[1].DownFor.TotalMinutes - 2) < 0.01);
        Check("outage store: status survives", reloaded[0].Status == TargetStatus.Timeout);
        Check("outage store: kind survives", reloaded[2].Kind == TransitionKind.Certificate);
        Check("outage store: timestamps survive to the second",
            Math.Abs((reloaded[0].When - when).TotalSeconds) < 1);

        // A half-written final line after a power cut must cost that line and nothing else.
        File.AppendAllText(path, "2026-01-01T00:00:00+00:00,truncated,Har" + Environment.NewLine);
        File.AppendAllText(path, "not a csv line at all" + Environment.NewLine);

        var survived = new OutageStore(path).Load();
        Check("outage store: malformed lines are skipped, not fatal", survived.Count == 3);

        // Rewrite is what compaction uses; it must leave a file that still loads.
        store.Rewrite([reloaded[0]]);
        Check("outage store: rewrite replaces the file", new OutageStore(path).Load().Count == 1);

        // Two boards must not share one outage log, or each would open showing the other's hosts.
        var boardA = ConfigStore.OutagePathFor(Path.Combine(dir, "home.ini"));
        var boardB = ConfigStore.OutagePathFor(Path.Combine(dir, "work.ini"));

        Check("outage store: each board gets its own file", boardA != boardB);
        Check("outage store: the sidecar sits beside its config",
            Path.GetDirectoryName(boardA) == Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar));
        Check("outage store: named after the board",
            Path.GetFileName(boardA) == "home.outages.csv");
        Check("outage store: and does not collide with the counters sidecar",
            boardA != ConfigStore.StatePathFor(Path.Combine(dir, "home.ini")));

        // An unwritable path must never throw into the probe loop.
        var blocked = new OutageStore(Path.Combine(dir, "no-such-dir\0bad", "x.csv"));
        blocked.Append(new StateTransition("x", false, when, TimeSpan.Zero, TargetStatus.Timeout, 3));
        Check("outage store: an unusable path degrades quietly", blocked.Load().Count == 0);
    }

    // ------------------------------------------------------------ certificates

    private static void CertificateArithmetic()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var healthy = new CertificateInfo(
            "CN=example.com, O=Example Ltd, C=GB", "CN=Some CA",
            now.AddDays(-30), now.AddDays(60), true, "", now);

        Check("cert: days remaining rounds down", healthy.DaysRemaining(now) == 60);
        Check("cert: not expiring far out", !healthy.IsExpiring(now, 14));
        Check("cert: not expired", !healthy.IsExpired(now));
        Check("cert: common name is extracted for a narrow column",
            healthy.ShortSubject == "example.com");

        // Eleven hours left is zero days, not one — the floor matters at exactly the point the
        // number is being used to decide whether there is time to act.
        var soon = healthy with { NotAfter = now.AddHours(11) };
        Check("cert: a part-day is floored to zero", soon.DaysRemaining(now) == 0);
        Check("cert: a part-day counts as expiring", soon.IsExpiring(now, 14));

        var expired = healthy with { NotAfter = now.AddDays(-2) };
        Check("cert: expiry in the past is negative", expired.DaysRemaining(now) == -2);
        Check("cert: expired is expired", expired.IsExpired(now));
        Check("cert: expired also counts as expiring", expired.IsExpiring(now, 14));

        var failed = CertificateInfo.Failed("connection refused", now);
        Check("cert: a failed read has no certificate", !failed.HasCertificate);
        Check("cert: a failed read is not reported as expiring", !failed.IsExpiring(now, 14));

        var noCn = healthy with { Subject = "O=Example Ltd" };
        Check("cert: a subject without a CN falls back to the whole string",
            noCn.ShortSubject == "O=Example Ltd");

        // The two board columns are driven entirely by these three calls, so the distinction the
        // columns depend on is asserted here: "expiring" must be answerable only when there is a
        // certificate to answer about. A target with none is not a target whose certificate is
        // fine, and a column printing "no" for an ICMP host reads as a clean bill of health.
        Check("cert columns: a present certificate answers yes or no",
            healthy.HasCertificate && soon.HasCertificate);
        Check("cert columns: an absent certificate answers neither",
            !failed.HasCertificate && !CertificateInfo.Failed("timed out", now).HasCertificate);
        Check("cert columns: the day count is what the column prints",
            healthy.DaysRemaining(now) == 60 && expired.DaysRemaining(now) == -2);

        // The warning window is a setting the user can change while the board runs; the same
        // certificate must answer differently as it moves.
        Check("cert columns: the same certificate follows the configured window",
            !healthy.IsExpiring(now, 14) && healthy.IsExpiring(now, 90));
    }

    private static void CertificateWarnsOnce()
    {
        var settings = new Settings();
        var config = new TargetConfig { Name = "tls", Address = "example.com", Probe = ProbeKind.Https };
        using var target = new PingTarget(config, settings);

        var now = DateTimeOffset.Now;
        var expiring = new CertificateInfo(
            "CN=example.com", "CN=CA", now.AddDays(-300), now.AddDays(5), true, "", now);

        var first = target.SetCertificate(expiring, warnDays: 14);
        var second = target.SetCertificate(expiring, warnDays: 14);

        Check("cert: warns on the crossing", first is { Kind: TransitionKind.Certificate, Up: false });
        Check("cert: carries the time remaining, not an elapsed duration",
            first is { } t && Math.Abs(t.DownFor.TotalDays - 5) < 0.01);
        Check("cert: does not warn again while still expiring", second is null);

        // A renewal must re-arm the warning, or the next approach to expiry is silent.
        var renewed = expiring with { NotAfter = now.AddDays(400) };
        Check("cert: a renewal is not itself an alert",
            target.SetCertificate(renewed, warnDays: 14) is null);
        Check("cert: after renewal the warning re-arms",
            target.SetCertificate(expiring, warnDays: 14) is not null);

        Check("cert: the reading is exposed on the snapshot",
            target.Snapshot().Certificate is { HasCertificate: true });

        // Only HTTPS targets are ever due for a check.
        using var icmp = new PingTarget(new TargetConfig { Name = "p", Address = "10.0.0.1" }, settings);
        Check("cert: an ICMP target is never due for a certificate check",
            !icmp.TryBeginCertCheck(6));
        Check("cert: an HTTPS target is due immediately", target.TryBeginCertCheck(6));
        Check("cert: and not due again straight afterwards", !target.TryBeginCertCheck(6));
    }

    // ------------------------------------------------------------ export

    private static void ExportsAreParsable()
    {
        var settings = new Settings { RollingWindow = 50 };
        var now = DateTimeOffset.Now;

        using var target = new PingTarget(
            new TargetConfig { Name = "gw, main", Address = "10.1.10.1", Tab = "core" }, settings);

        for (var i = 0; i < 10; i++)
            target.Record(ProbeResult.Ok(20 + i, System.Net.IPAddress.Loopback, i * 1000, now), 3);

        var board = Export.Board([target], now);
        var lines = board.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Check("export: board has a header and one row per target", lines.Length == 2);
        Check("export: a comma in a name is quoted", lines[1].StartsWith("\"gw, main\",", StringComparison.Ordinal));
        Check("export: the header names the availability columns",
            lines[0].Contains("avail_24h", StringComparison.Ordinal)
            && lines[0].Contains("avail_30d", StringComparison.Ordinal));
        Check("export: the header names the certificate columns",
            lines[0].Contains("cert_expires", StringComparison.Ordinal));
        Check("export: every row has as many fields as the header",
            CountFields(lines[0]) == CountFields(lines[1]));

        var history = Export.History([target]);
        Check("export: history writes one row per retained sample",
            history.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 11);

        var outages = Export.Outages(
        [
            new Outage("gw", now.AddMinutes(-10), now.AddMinutes(-8), TimeSpan.FromMinutes(2),
                       TargetStatus.Timeout, TransitionKind.Hard),
            new Outage("wan", now.AddMinutes(-5), null, TimeSpan.FromMinutes(5),
                       TargetStatus.Unreachable, TransitionKind.Hard),
        ]);

        // Trimmed because the writer ends rows with CRLF, which is right for a CSV on Windows and
        // leaves a stray carriage return on the end of every line when split on '\n' alone.
        var outageLines = outages.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(l => l.TrimEnd('\r'))
                                 .ToArray();

        Check("export: outages write a header and one row each", outageLines.Length == 3);
        Check("export: a closed outage is marked not ongoing",
            outageLines[1].EndsWith(",no", StringComparison.Ordinal));
        Check("export: an open outage is marked ongoing",
            outageLines[2].EndsWith(",yes", StringComparison.Ordinal));

        Check("export: an empty board still writes its header",
            Export.Board([], now).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1);

        static int CountFields(string line)
        {
            int fields = 1, i = 0;
            var quoted = false;

            for (; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ',' && !quoted) fields++;
            }

            return fields;
        }
    }

    private static void SoftAlertWording()
    {
        var now = DateTimeOffset.Now;

        var degraded = AlertPayload.From(
            new StateTransition("wan", false, now, TimeSpan.Zero, TargetStatus.Degraded, 0,
                                TransitionKind.Degraded), "1.1.1.1");

        Check("alerts: a degraded event is not labelled down", degraded.Event == "degraded");
        Check("alerts: the summary says the host is still replying",
            degraded.Summary().Contains("still replying", StringComparison.Ordinal));
        Check("alerts: a degraded summary never says DOWN",
            !degraded.Summary().Contains("is DOWN", StringComparison.Ordinal));

        var cert = AlertPayload.From(
            new StateTransition("tls", false, now, TimeSpan.FromDays(9), TargetStatus.Degraded, 14,
                                TransitionKind.Certificate), "93.184.216.34");

        Check("alerts: a certificate event has its own kind", cert.Event == "cert_expiring");
        Check("alerts: the summary counts the days",
            cert.Summary().Contains("expires in 9 days", StringComparison.Ordinal));

        var expired = AlertPayload.From(
            new StateTransition("tls", false, now, TimeSpan.FromDays(-2), TargetStatus.Degraded, 14,
                                TransitionKind.Certificate), "93.184.216.34");

        Check("alerts: an already-expired certificate says so",
            expired.Summary().Contains("EXPIRED", StringComparison.Ordinal));

        // The webhook payload must stay valid JSON with a negative number in it.
        Check("alerts: an expired certificate still emits well-formed json",
            expired.ToJson() is { } json && json.StartsWith('{') && json.EndsWith('}')
            && json.Contains("\"outage_seconds\":-", StringComparison.Ordinal));

        // Soft transitions are opt-in for remote sinks, and both halves must be gated together.
        var off = new AlertSettings { WebhookEnabled = true, WebhookUrl = "https://example.invalid/x" };
        Check("alerts: degraded is off by default for webhook and email", !off.NotifyOnDegraded);

        var hard = AlertPayload.From(
            new StateTransition("wan", false, now, TimeSpan.Zero, TargetStatus.Timeout, 3), "1.1.1.1");
        Check("alerts: an ordinary outage is unchanged", hard.Event == "down"
            && hard.Summary().Contains("is DOWN", StringComparison.Ordinal));
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
    }
}
