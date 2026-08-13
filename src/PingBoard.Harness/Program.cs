using System.Globalization;
using PingBoard.Core;

namespace PingBoard.Harness;

/// <summary>
/// Console driver for the engine. Renders the same columns the real UI will, so the networking
/// layer can be verified — including the awkward paths like DNS failure and unreachable-vs-timeout
/// — before any XAML exists.
/// <para>Usage: <c>PingBoard.Harness [config.ini] [--seconds N]</c></para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();

        var configPath = PositionalArg(args);
        var seconds = ArgValue(args, "--seconds", 0);
        var save = args.Contains("--save");

        var config = configPath is not null && File.Exists(configPath)
            ? ConfigStore.Load(configPath)
            : new BoardConfig(new Settings { IntervalMs = 1000, TimeoutMs = 1000 }, DefaultTargets());

        Console.WriteLine($"PingBoard harness — {config.Targets.Count} targets, "
                          + $"interval {config.Settings.IntervalMs} ms, timeout {config.Settings.TimeoutMs} ms");
        Console.WriteLine(configPath is not null ? $"config: {configPath}" : "config: built-in test set");
        Console.WriteLine();

        var counters = configPath is not null
            ? StateStore.Load(ConfigStore.StatePathFor(configPath))
            : [];

        await using var scheduler = new ProbeScheduler(config.Settings);

        foreach (var target in config.Targets)
        {
            counters.TryGetValue(target.Name, out var saved);
            scheduler.AddTarget(new PingTarget(target, config.Settings, saved));
        }

        scheduler.Transition += t => Console.WriteLine(
            t.Up
                ? $"  >> RECOVERED  {t.TargetName} at {t.When:HH:mm:ss} after {Format(t.DownFor)}"
                : $"  >> DOWN       {t.TargetName} at {t.When:HH:mm:ss} ({t.Status.Label()})");

        scheduler.SuspendChanged += (suspended, reason) => Console.WriteLine(
            suspended ? $"  >> SUSPENDED  {reason}" : "  >> RESUMED");

        using var watcher = new SystemWatcher(scheduler, config.Settings);
        watcher.Start();
        scheduler.Start();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (seconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            var deadline = seconds > 0 ? Environment.TickCount64 + seconds * 1000L : long.MaxValue;
            var ticks = 0;

            while (!cts.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                ticks++;

                // Periodic memory line with a forced full collection: a managed heap that is flat
                // across a long run is the proof that the ring buffers are actually bounded.
                if (ticks % 60 == 0) ReportMemory();
                else Render(scheduler);
            }
        }
        catch (OperationCanceledException) { /* expected on Ctrl+C or deadline */ }

        Console.WriteLine();
        Console.WriteLine("Stopping…");
        await scheduler.StopAsync().ConfigureAwait(false);

        Render(scheduler);

        // Counters are flushed on exit, not per probe — the same debounce the real app uses.
        if (save && configPath is not null)
        {
            var statePath = ConfigStore.StatePathFor(configPath);
            StateStore.Save(statePath, scheduler.Targets);
            Console.WriteLine($"counters written to {statePath}");
        }

        ReportMemory();
        return 0;
    }

    /// <summary>
    /// The deliberate mix from the plan's verification table: each entry exercises a distinct
    /// code path, including the two that are easiest to get wrong.
    /// </summary>
    private static List<TargetConfig> DefaultTargets() =>
    [
        new() { Name = "loopback",   Address = "127.0.0.1" },                       // always OK
        new() { Name = "test-net",   Address = "192.0.2.1" },                       // RFC 5737: never routes
        new() { Name = "bad-dns",    Address = "no-such-host.invalid" },            // DNS FAIL, not TIMEOUT
        new() { Name = "gateway",    Address = "10.1.10.1" },
        new() { Name = "hass-tcp",   Address = "10.1.10.12", Probe = ProbeKind.Tcp, Port = 8123 },
        new() { Name = "gpu-box",    Address = "10.1.10.82" },
        new() { Name = "google-dns", Address = "8.8.8.8" },
        new() { Name = "by-name",    Address = "one.one.one.one" },                 // forward resolution
    ];

    private static void Render(ProbeScheduler scheduler)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"{"NAME",-12} {"STATUS",-12} {"IP",-16} {"HOSTNAME",-24} "
            + $"{"RTT",5} {"LOSS%",7} {"AVG",7} {"OK",7} {"NOK",7}  LAST OK");
        Console.WriteLine(new string('-', 124));

        foreach (var target in scheduler.Targets)
        {
            var s = target.Snapshot();
            var rtt = s.LastRttMs >= 0 ? s.LastRttMs.ToString(CultureInfo.InvariantCulture) : "—";
            var loss = s.Stats.HasData ? s.Stats.LossPercent.ToString("F1", CultureInfo.InvariantCulture) : "—";
            var avg = s.Stats.HasData && s.Stats.OkSamples > 0
                ? s.Stats.AvgMs.ToString("F1", CultureInfo.InvariantCulture) : "—";
            var lastOk = s.LastOk?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "never";

            var hostname = s.DisplayHostname.Length > 24 ? s.DisplayHostname[..24] : s.DisplayHostname;

            Console.WriteLine(
                $"{s.Name,-12} {s.Status.Label(),-12} {s.DisplayIp,-16} {hostname,-24} "
                + $"{rtt,5} {loss,7} {avg,7} {s.OkCount,7} {s.NokCount,7}  {lastOk}");
        }
    }

    /// <summary>
    /// The ring buffer is the whole defence against unbounded growth, so the harness reports
    /// working set — a run that climbs steadily means something is retaining results.
    /// </summary>
    private static void ReportMemory()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var managed = GC.GetTotalMemory(forceFullCollection: true) / 1024d / 1024d;
        var working = process.WorkingSet64 / 1024d / 1024d;
        Console.WriteLine();
        Console.WriteLine(
            $"managed heap {managed:F1} MB · working set {working:F1} MB · "
            + $"gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    }

    private static string Format(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:D2}s"
        : $"{span.TotalSeconds:F1}s";

    private static int ArgValue(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    /// <summary>Flags that consume the following token, so it isn't mistaken for the config path.</summary>
    private static readonly string[] ValueFlags = ["--seconds"];

    private static string? PositionalArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (ValueFlags.Contains(args[i])) i++;   // skip its value too
                continue;
            }
            return args[i];
        }
        return null;
    }
}
