using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PingBoard.Core;

namespace PingBoard.App.ViewModels;

public enum SortKey { Name, Status, Ip, Hostname, Rtt, Loss, LastOk, LastNok, Cumulative, Uptime }

/// <summary>
/// Owns the engine and projects it into the board.
/// <para>
/// The refresh model is the important part: probes complete on threadpool threads and mutate the
/// engine, while a single 4 Hz timer on the UI thread pulls snapshots. Nothing on a probe path
/// ever touches the dispatcher, so probe rate and render rate are fully independent.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Render rate. Fast enough to feel live, slow enough to cost nothing.</summary>
    private const int RefreshMs = 250;

    /// <summary>Counter flush interval. Never per probe — that would be continuous disk writes.</summary>
    private const int AutosaveMs = 60_000;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _autosaveTimer;

    private ProbeScheduler _scheduler;
    private SystemWatcher? _watcher;
    private TransitionLog? _log;
    private Settings _settings;
    private bool _countersDirty;
    private bool _disposed;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = new Settings();
        _scheduler = new ProbeScheduler(_settings);

        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(RefreshMs);
        _refreshTimer.Tick += (_, _) => RefreshRows();

        _autosaveTimer = dispatcher.CreateTimer();
        _autosaveTimer.Interval = TimeSpan.FromMilliseconds(AutosaveMs);
        _autosaveTimer.Tick += (_, _) => SaveCountersIfDirty();
    }

    public ObservableCollection<TargetRow> Rows { get; } = [];

    [ObservableProperty] public partial string ConfigPath { get; private set; } = "";
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial string BannerText { get; private set; } = "";
    [ObservableProperty] public partial bool BannerVisible { get; private set; }
    [ObservableProperty] public partial SortKey Sort { get; private set; } = SortKey.Name;
    [ObservableProperty] public partial bool SortDescending { get; private set; }

    public Settings Settings => _settings;
    public ColumnLayout Columns => ColumnLayout.Instance;

    /// <summary>Filename of the active config, for the caption. Full path lives in the tooltip.</summary>
    public string ConfigName => ConfigPath.Length == 0 ? "" : Path.GetFileName(ConfigPath);

    partial void OnConfigPathChanged(string value) => OnPropertyChanged(nameof(ConfigName));

    /// <summary>Raised on a target transition so the window can raise a toast.</summary>
    public event Action<StateTransition>? Transition;

    // ---------------------------------------------------------------- lifecycle

    public async Task LoadAsync(string configPath)
    {
        await StopEngineAsync().ConfigureAwait(true);

        ConfigPath = configPath;

        BoardConfig config;
        try
        {
            config = ConfigStore.Load(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBanner($"Could not read {Path.GetFileName(configPath)} — {ex.Message}");
            config = new BoardConfig(new Settings(), []);
        }

        _settings = config.Settings;

        var counters = StateStore.Load(ConfigStore.StatePathFor(configPath));

        _scheduler = new ProbeScheduler(_settings);
        _scheduler.Transition += OnTransition;
        _scheduler.SuspendChanged += OnSuspendChanged;

        Rows.Clear();
        foreach (var targetConfig in config.Targets)
        {
            counters.TryGetValue(targetConfig.Name, out var saved);
            var target = new PingTarget(targetConfig, _settings, saved);
            _scheduler.AddTarget(target);
            Rows.Add(new TargetRow(target));
        }

        _log = _settings.LogEnabled ? new TransitionLog(ResolveLogPath()) : null;

        _watcher = new SystemWatcher(_scheduler, _settings);
        _watcher.Start();

        _scheduler.Start();
        _refreshTimer.Start();
        _autosaveTimer.Start();

        ApplySort();
        RefreshRows();
    }

    private string ResolveLogPath()
    {
        var configured = _settings.LogPath;
        if (Path.IsPathRooted(configured)) return configured;

        // A relative log path is resolved next to the config file, so a board you copy between
        // machines keeps its log alongside it.
        var dir = Path.GetDirectoryName(Path.GetFullPath(ConfigPath)) ?? AppPaths.DataDirectory;
        return Path.Combine(dir, configured);
    }

    private async Task StopEngineAsync()
    {
        _refreshTimer.Stop();
        _autosaveTimer.Stop();

        _watcher?.Dispose();
        _watcher = null;

        _scheduler.Transition -= OnTransition;
        _scheduler.SuspendChanged -= OnSuspendChanged;
        await _scheduler.DisposeAsync().ConfigureAwait(true);

        _log?.Dispose();
        _log = null;
    }

    // ---------------------------------------------------------------- refresh

    private void RefreshRows()
    {
        foreach (var row in Rows) row.Refresh();

        var up = 0;
        var down = 0;
        var idle = 0;

        foreach (var row in Rows)
        {
            var status = row.Snapshot.Status;
            if (status.IsOk()) up++;
            else if (status.IsFailure()) down++;
            else idle++;
        }

        StatusText = _scheduler.IsSuspended
            ? $"Paused — {_scheduler.SuspendReason}"
            : $"{Rows.Count} targets · {up} up · {down} down"
              + (idle > 0 ? $" · {idle} idle" : "");

        // Re-sorting on a status-dependent key would make rows jump under the cursor at 4 Hz.
        // Sorting is applied on demand instead, when the user picks a column.
    }

    private void OnTransition(StateTransition transition)
    {
        _countersDirty = true;
        _log?.Write(transition);

        // Arrives on a threadpool thread; the toast and any UI work must hop to the dispatcher.
        _dispatcher.TryEnqueue(() => Transition?.Invoke(transition));
    }

    private void OnSuspendChanged(bool suspended, string reason) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (suspended) ShowBanner($"Probing paused — {reason}. Counters are frozen.");
            else HideBanner();
        });

    public void ShowBanner(string text)
    {
        BannerText = text;
        BannerVisible = true;
    }

    public void HideBanner()
    {
        BannerVisible = false;
        BannerText = "";
    }

    // ---------------------------------------------------------------- sorting

    public void SortBy(SortKey key)
    {
        if (Sort == key) SortDescending = !SortDescending;
        else { Sort = key; SortDescending = false; }

        ApplySort();
    }

    private void ApplySort()
    {
        var ordered = Rows.ToList();

        Comparison<TargetRow> compare = Sort switch
        {
            SortKey.Status => (a, b) => Rank(a).CompareTo(Rank(b)),
            SortKey.Ip => (a, b) => CompareIp(a.Snapshot.DisplayIp, b.Snapshot.DisplayIp),
            SortKey.Hostname => (a, b) => string.Compare(a.Hostname, b.Hostname, StringComparison.OrdinalIgnoreCase),
            SortKey.Rtt => (a, b) => a.Snapshot.LastRttMs.CompareTo(b.Snapshot.LastRttMs),
            SortKey.Loss => (a, b) => a.Snapshot.Stats.LossPercent.CompareTo(b.Snapshot.Stats.LossPercent),
            SortKey.LastOk => (a, b) => Nullable.Compare(a.Snapshot.LastOk, b.Snapshot.LastOk),
            SortKey.LastNok => (a, b) => Nullable.Compare(a.Snapshot.LastNok, b.Snapshot.LastNok),
            SortKey.Cumulative => (a, b) => a.Snapshot.NokCount.CompareTo(b.Snapshot.NokCount),
            SortKey.Uptime => (a, b) => a.Target.Counters.UptimePercent.CompareTo(b.Target.Counters.UptimePercent),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };

        ordered.Sort(compare);
        if (SortDescending) ordered.Reverse();

        // Move rather than clear-and-refill: clearing collapses the ListView's selection and
        // scroll position, and forces every container to be rebuilt.
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Rows.IndexOf(ordered[target]);
            if (current != target) Rows.Move(current, target);
        }

        // Sorting by status puts what is broken at the top, which is the only ordering that
        // matters when something is wrong.
        static int Rank(TargetRow row) => row.Snapshot.Status switch
        {
            TargetStatus.Unreachable => 0,
            TargetStatus.DnsFail => 1,
            TargetStatus.Refused => 2,
            TargetStatus.Timeout => 3,
            TargetStatus.Unknown => 4,
            TargetStatus.Suspended => 5,
            TargetStatus.Paused => 6,
            _ => 7,
        };
    }

    /// <summary>Orders addresses numerically, so .9 sorts before .10 rather than after it.</summary>
    private static int CompareIp(string a, string b)
    {
        var okA = System.Net.IPAddress.TryParse(a, out var ipA);
        var okB = System.Net.IPAddress.TryParse(b, out var ipB);

        if (!okA || !okB) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

        var bytesA = ipA!.GetAddressBytes();
        var bytesB = ipB!.GetAddressBytes();
        if (bytesA.Length != bytesB.Length) return bytesA.Length.CompareTo(bytesB.Length);

        for (var i = 0; i < bytesA.Length; i++)
            if (bytesA[i] != bytesB[i])
                return bytesA[i].CompareTo(bytesB[i]);

        return 0;
    }

    // ---------------------------------------------------------------- mutation

    public bool NameExists(string name, TargetRow? except = null) =>
        Rows.Any(r => r != except && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddTarget(TargetConfig config)
    {
        var target = new PingTarget(config, _settings);
        _scheduler.AddTarget(target);
        Rows.Add(new TargetRow(target));
        SaveConfig();
        ApplySort();
    }

    public void UpdateTarget(TargetRow row, TargetConfig config)
    {
        row.Target.UpdateConfig(config);
        row.Refresh();
        SaveConfig();
        ApplySort();
    }

    public void RemoveTarget(TargetRow row)
    {
        Rows.Remove(row);
        _scheduler.RemoveTarget(row.Target);
        SaveConfig();
    }

    public void TogglePaused(TargetRow row)
    {
        var config = row.Target.Config.Clone();
        config.Enabled = !config.Enabled;
        UpdateTarget(row, config);
    }

    public void ResetStats(TargetRow? row = null)
    {
        if (row is not null) row.Target.ResetStats();
        else foreach (var r in Rows) r.Target.ResetStats();

        _countersDirty = true;
        SaveCountersIfDirty();
        RefreshRows();
    }

    public void ApplySettings(Settings settings)
    {
        settings.Validate();
        _settings = settings;
        _scheduler.ApplySettings(settings);
        _watcher?.ApplySettings(settings);

        _log?.Dispose();
        _log = settings.LogEnabled ? new TransitionLog(ResolveLogPath()) : null;

        SaveConfig();
    }

    // ---------------------------------------------------------------- persistence

    public void SaveConfig()
    {
        if (ConfigPath.Length == 0) return;

        try
        {
            ConfigStore.Save(ConfigPath, _settings, Rows.Select(r => r.Target.Config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBanner($"Could not save config — {ex.Message}");
        }
    }

    private void SaveCountersIfDirty()
    {
        // Counters change on every probe but are only worth writing periodically. Skipping the
        // write when nothing has transitioned keeps an idle board completely silent on disk.
        if (!_countersDirty && !Rows.Any(r => r.Snapshot.OkCount + r.Snapshot.NokCount > 0)) return;
        SaveCounters();
    }

    public void SaveCounters()
    {
        if (ConfigPath.Length == 0) return;

        try
        {
            StateStore.Save(ConfigStore.StatePathFor(ConfigPath), Rows.Select(r => r.Target));
            _countersDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBanner($"Could not save counters — {ex.Message}");
        }
    }

    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Name,Address,IP,Hostname,Status,RTTms,LossPercent,AvgMs,MinMs,MaxMs,OK,NOK,LastOK,LastNOK");

        foreach (var row in Rows)
        {
            var s = row.Snapshot;
            sb.Append(Csv(s.Name)).Append(',')
              .Append(Csv(s.Address)).Append(',')
              .Append(Csv(s.DisplayIp)).Append(',')
              .Append(Csv(s.DisplayHostname)).Append(',')
              .Append(Csv(s.Status.Label())).Append(',')
              .Append(s.LastRttMs >= 0 ? s.LastRttMs.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(s.Stats.HasData ? s.Stats.LossPercent.ToString("F2", CultureInfo.InvariantCulture) : "").Append(',')
              .Append(s.Stats.OkSamples > 0 ? s.Stats.AvgMs.ToString("F1", CultureInfo.InvariantCulture) : "").Append(',')
              .Append(s.Stats.OkSamples > 0 ? s.Stats.MinMs.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(s.Stats.OkSamples > 0 ? s.Stats.MaxMs.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(s.OkCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.NokCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.LastOk?.ToString("o", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(s.LastNok?.ToString("o", CultureInfo.InvariantCulture) ?? "")
              .AppendLine();
        }

        return sb.ToString();

        static string Csv(string value) =>
            value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
                ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
                : value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        SaveCounters();
        await StopEngineAsync().ConfigureAwait(false);
    }
}
