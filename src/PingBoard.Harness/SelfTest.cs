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
            RingBufferRolls();
            RingBufferIgnoresInactiveSamples();
            StatusMapping();
            FailedProbeKeepsTargetAddress();
            SuspendFreezesCountersAndAlerts();
            PerHostFailureThreshold();
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

        // A hand-edited nonsense value must be clamped, not carried through as-is.
        File.WriteAllText(thresholdPath, """
            [Settings]
            FailuresBeforeDown=3

            [Target:silly]
            Address=1.2.3.4
            FailuresBeforeDown=0
            """, Encoding.UTF8);

        Check("ini: hand-edited threshold is clamped",
            ConfigStore.Load(thresholdPath).Targets[0].FailuresBeforeDown == 1);
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

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
    }
}
