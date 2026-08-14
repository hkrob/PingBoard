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
            ThresholdLoweredMidOutage();
            ConcurrencyCeilingFollowsSettings();
            AlertSecretsAndValidation();
            AlertConfigSurvivesAutosave(scratch);
            WebhookDeliversATransition();
            TraceRouteFindsThePath();
            TabsGroupWithoutGating(scratch);
            UpdateVersionComparison();
            HttpProbeJudgesTheStatusCode();
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

        // Answers by path: /ok -> 200, /boom -> 500, /moved -> 302.
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
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

    private static void Check(string name, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}"); }
    }
}
