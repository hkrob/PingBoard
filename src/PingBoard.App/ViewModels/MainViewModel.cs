using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    private OutageStore? _outages;
    private AlertDispatcher? _alerts;
    private Settings _settings;

    /// <summary>
    /// Alert configuration, or null when it could not be read.
    /// <para>
    /// Null is meaningful rather than merely absent: <see cref="ConfigStore.Save"/> treats it as
    /// "leave the [Alerts] section on disk alone". If a config fails to load and the user then
    /// edits a target, the resulting autosave must not replace their webhook and SMTP credentials
    /// with the empty defaults we fell back to.
    /// </para>
    /// </summary>
    private AlertSettings? _alertSettings;
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

    /// <summary>
    /// Every row on the board, across all tabs. Persistence, counters and duplicate-name checks all
    /// work from this — filtering it to the selected tab would mean the next save wrote only the
    /// visible targets and silently deleted the rest.
    /// </summary>
    public ObservableCollection<TargetRow> Rows { get; } = [];

    /// <summary>The rows the board actually shows: those in the selected tab.</summary>
    public ObservableCollection<TargetRow> VisibleRows { get; } = [];

    public ObservableCollection<TabItem> Tabs { get; } = [];

    /// <summary>Tab definitions as they will be written back to the config.</summary>
    private List<TabConfig> _tabs = [];

    private string _selectedTab = TabConfig.DefaultName;

    /// <summary>
    /// Free-text filter over name, IP and hostname.
    /// <para>
    /// A filter is a view over the board, never a change to it: filtered-out targets keep being
    /// probed, keep their counters, and still raise alerts. Anything else would turn a search box
    /// into a way to silently stop monitoring.
    /// </para>
    /// </summary>
    [ObservableProperty] public partial string FilterText { get; set; } = "";

    /// <summary>Status filter: 0 = all, 1 = problems only, 2 = OK only, 3 = paused/idle only.</summary>
    [ObservableProperty] public partial int StatusFilterIndex { get; set; }

    [ObservableProperty] public partial Visibility FilterActiveVisibility { get; private set; } = Visibility.Collapsed;

    partial void OnFilterTextChanged(string value) => RebuildVisibleRows();

    partial void OnStatusFilterIndexChanged(int value) => RebuildVisibleRows();

    /// <summary>True when this row passes the tab, text and status filters.</summary>
    private bool Passes(TargetRow row)
    {
        if (!string.Equals(TabConfig.Normalise(row.Target.Config.Tab), _selectedTab, StringComparison.OrdinalIgnoreCase))
            return false;

        if (FilterText is { Length: > 0 } text)
        {
            var hit = row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                      || row.Ip.Contains(text, StringComparison.OrdinalIgnoreCase)
                      || row.Hostname.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (!hit) return false;
        }

        var status = row.Snapshot.Status;

        return StatusFilterIndex switch
        {
            1 => status.IsFailure(),
            2 => status.IsOk(),
            3 => status is TargetStatus.Paused or TargetStatus.Suspended or TargetStatus.Unknown,
            _ => true,
        };
    }

    /// <summary>The tab strip is hidden entirely until there is more than one group to choose.</summary>
    [ObservableProperty] public partial Visibility TabStripVisibility { get; private set; } = Visibility.Collapsed;

    [ObservableProperty] public partial string ConfigPath { get; private set; } = "";
    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    /// <summary>
    /// Wall-clock time for the status bar. Updated from the existing refresh tick rather than a
    /// timer of its own — the setter is change-checked, so redundant ticks within the same second
    /// raise no notification and repaint nothing.
    /// </summary>
    [ObservableProperty] public partial string Clock { get; private set; } = "";
    /// <summary>Bell or crossed-out bell, from Segoe Fluent Icons. Driven by live mute state.</summary>
    [ObservableProperty] public partial string MuteGlyph { get; private set; } = "";

    [ObservableProperty] public partial string MuteTooltip { get; private set; } = "Mute desktop notifications";

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
            _alertSettings = config.Alerts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBanner($"Could not read {Path.GetFileName(configPath)} — {ex.Message}");
            config = new BoardConfig(new Settings(), []);

            // Null, not the fallback defaults: a later autosave must preserve the alert section we
            // failed to read rather than overwrite it with blanks.
            _alertSettings = null;
        }

        _settings = config.Settings;

        var statePath = ConfigStore.StatePathFor(configPath);
        var counters = StateStore.Load(statePath);
        var history = StateStore.LoadHistory(statePath);
        var availability = StateStore.LoadAvailability(statePath);

        _scheduler = new ProbeScheduler(_settings);
        _scheduler.Transition += OnTransition;
        _scheduler.SuspendChanged += OnSuspendChanged;
        _scheduler.TraceCompleted += OnTraceCompleted;

        Rows.Clear();
        foreach (var targetConfig in config.Targets)
        {
            counters.TryGetValue(targetConfig.Name, out var saved);
            var target = new PingTarget(targetConfig, _settings, saved);

            // Restored before the row is built, so the sparkline and graph have something to draw
            // on the first frame rather than filling in over the next five minutes.
            if (history.TryGetValue(targetConfig.Name, out var samples)) target.RestoreHistory(samples);

            // Without this the 7- and 30-day figures would reset on every restart, which for a
            // number whose whole point is spanning weeks would make it meaningless.
            if (availability.TryGetValue(targetConfig.Name, out var log)) target.RestoreAvailability(log);

            _scheduler.AddTarget(target);
            Rows.Add(new TargetRow(target));
        }

        _log = _settings.LogEnabled ? new TransitionLog(ResolveLogPath()) : null;

        // Loaded before probing starts, so the outage window has the previous run's history the
        // first time it is opened rather than only after something new happens. This is also what
        // lets the "while you were away" banner span a restart: the journal it summarises used to
        // begin empty every launch, so an outage that happened while the app was closed left no
        // trace anywhere the user would ever see it.
        _outages = _settings.OutageLogEnabled
            ? new OutageStore(ConfigStore.OutagePathFor(ConfigPath))
            : null;
        if (_outages is not null) Journal.Restore(_outages.Load());

        // Constructed unconditionally, even with every sink disabled: Enqueue is a cheap early
        // return in that case, and having the dispatcher already there means enabling a sink from
        // the settings dialog takes effect immediately instead of at the next config reload.
        _alerts = new AlertDispatcher(_alertSettings ?? new AlertSettings());

        _watcher = new SystemWatcher(_scheduler, _settings);
        _watcher.Start();

        _scheduler.Start();
        _refreshTimer.Start();
        _autosaveTimer.Start();

        BuildTabs(config.Tabs);

        ApplySort();
        RefreshRows();
        FitColumnsNow();
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
        _scheduler.TraceCompleted -= OnTraceCompleted;
        await _scheduler.DisposeAsync().ConfigureAwait(true);

        // After the scheduler, so a transition raised during its drain is still queued, and the
        // dispatcher's own shutdown gets its brief window to flush it.
        if (_alerts is not null)
        {
            await _alerts.DisposeAsync().ConfigureAwait(true);
            _alerts = null;
        }

        _log?.Dispose();
        _log = null;

        // Nothing to flush: every transition was appended as it happened.
        _outages = null;
    }

    // ---------------------------------------------------------------- refresh

    private void RefreshRows()
    {
        foreach (var row in Rows) row.Refresh(_settings.CertWarnDays, row.Target.TimeoutMsFrom(_settings));

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

        Clock = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

        // An alert sink that fails quietly is the worst state this app can be in: the board looks
        // healthy, and the user believes they will be told when it is not.
        var alertProblem = _alerts?.Health() is { Ok: false, Error: { } error }
            ? " · ⚠ " + (error.Length > 60 ? error[..60] + "…" : error)
            : "";

        RefreshMuteState();
        RefreshTabs();
        RefreshFilterMembership();
        FitColumnsIfDue();

        // Shown for the same reason as the alert failure above: being muted is a state you must be
        // able to see, or you will trust a monitor that has been told to stay quiet.
        var muted = NotificationMute.Describe() is { } description ? " · 🔕 " + description : "";

        // A filter hides rows, so say so. Counts that silently describe a subset of the board are
        // how someone concludes everything is fine while looking at three of forty targets.
        var filtered = VisibleRows.Count != Rows.Count
            ? $" · showing {VisibleRows.Count} of {Rows.Count}"
            : "";

        // Labelled, not a bare percentage. Sitting beside "6 targets · 6 up · 0 down" an unlabelled
        // number reads as another statistic about the board rather than a display setting.
        var zoom = ColumnLayout.Instance.IsDefaultZoom
            ? ""
            : $" · zoom {ColumnLayout.Instance.ZoomLabel}";

        StatusText = _scheduler.IsSuspended
            ? $"Paused — {_scheduler.SuspendReason}"
            : $"{Rows.Count} targets · {up} up · {down} down"
              + (idle > 0 ? $" · {idle} idle" : "")
              + filtered
              + zoom
              + alertProblem
              + muted;

        // Re-sorting on a status-dependent key would make rows jump under the cursor at 4 Hz.
        // Sorting is applied on demand instead, when the user picks a column.
    }

    /// <summary>
    /// Recent transitions, so the window can report what happened while it was in the tray.
    /// </summary>
    public TransitionJournal Journal { get; } = new();

    /// <summary>Recorded outages, newest first.</summary>
    public IReadOnlyList<Outage> Outages() => Journal.Outages(DateTimeOffset.Now);

    /// <summary>
    /// Forgets every recorded outage, on disk as well as in memory. The events CSV is deliberately
    /// left alone — it is the copy kept for evidence, and a button in the app should not be able to
    /// erase that.
    /// </summary>
    public void ClearOutages()
    {
        Journal.Clear();
        _outages?.Rewrite([]);
    }

    public string ExportOutages() => Export.Outages(Outages());

    public string ExportBoard() => Export.Board(_scheduler.Targets, DateTimeOffset.Now);

    public string ExportHistory() => Export.History(_scheduler.Targets);

    private void OnTransition(StateTransition transition)
    {
        _countersDirty = true;
        _log?.Write(transition);
        Journal.Add(transition);

        // Appended one line at a time rather than rewritten, so a power cut costs the last
        // transition instead of the whole file. Compaction happens when the file has grown past
        // twice what the journal can hold, which on a healthy board is never.
        if (_outages is not null)
        {
            _outages.Append(transition);
            // SnapshotForPersist, not Snapshot: compaction must keep the open outages the ring has
            // already evicted, or the file loses on disk exactly what the pinned set protects in
            // memory.
            if (_outages.NeedsCompaction) _outages.Rewrite(Journal.SnapshotForPersist());
        }

        // Queued, not sent, and deliberately from this threadpool thread rather than the
        // dispatcher: an unreachable SMTP server blocks for its full TCP timeout, and putting that
        // on the UI thread would freeze the board every time the network it monitors goes down.
        _alerts?.Enqueue(transition, AddressOf(transition.TargetName));

        // Arrives on a threadpool thread; the toast and any UI work must hop to the dispatcher.
        _dispatcher.TryEnqueue(() => Transition?.Invoke(transition));
    }

    /// <summary>
    /// The address currently being probed for a target, for the alert payload.
    /// <para>
    /// Read from the scheduler rather than from <see cref="Rows"/>: this runs on a threadpool
    /// thread, and <see cref="ObservableCollection{T}"/> is not safe to touch off the UI thread.
    /// <see cref="ProbeScheduler.Targets"/> hands back a snapshot taken under its own lock.
    /// </para>
    /// </summary>
    private string AddressOf(string targetName)
    {
        foreach (var target in _scheduler.Targets)
            if (string.Equals(target.Config.Name, targetName, StringComparison.OrdinalIgnoreCase))
                return target.Snapshot().DisplayIp;

        return "";
    }

    /// <summary>
    /// A failure trace has finished. Arrives seconds after the transition that caused it, on a
    /// threadpool thread; the row already holds the result, so this only has to persist it.
    /// </summary>
    private void OnTraceCompleted(TraceResult trace) => _log?.WriteTrace(trace);

    private void OnSuspendChanged(bool suspended, string reason) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (suspended) ShowBanner($"Probing paused — {reason}. Counters are frozen.");
            else HideBanner();
        });

    /// <summary>
    /// Syncs the mute button to actual state. Called from the refresh tick as well as on click,
    /// so a timed mute that lapses on its own is reflected without any user action.
    /// </summary>
    public void RefreshMuteState()
    {
        var muted = NotificationMute.IsMuted;

        // RingerSilent / Ringer.
        MuteGlyph = muted ? "" : "";
        MuteTooltip = NotificationMute.Describe() ?? "Mute desktop notifications";
    }

    /// <summary>
    /// Runs a trace for one row on request. Returns null when the target has no resolved address.
    /// </summary>
    public Task<TraceResult?> TraceNowAsync(TargetRow row) => _scheduler.TraceNowAsync(row.Target);

    // ---------------------------------------------------------------- tabs

    /// <summary>Rebuilds the tab strip from the loaded configuration.</summary>
    private void BuildTabs(IReadOnlyList<TabConfig> tabs)
    {
        _tabs = tabs.Count > 0 ? [.. tabs.Select(t => t.Clone())] : [new TabConfig()];

        Tabs.Clear();
        foreach (var tab in _tabs)
            Tabs.Add(new TabItem(tab.Name) { IsEnabled = tab.Enabled, IsMuted = tab.Muted });

        if (!_tabs.Any(t => string.Equals(t.Name, _selectedTab, StringComparison.OrdinalIgnoreCase)))
            _selectedTab = _tabs[0].Name;

        // Establishes the initial tab context for column choices even before the user clicks
        // anything — SelectTab only fires on a later, different click, and the tab shown first
        // needs its own choices loaded (or the shared starting point) from the moment it appears.
        ColumnLayout.Instance.SwitchToTab(_selectedTab);

        ApplyTabStateToTargets();
        RebuildVisibleRows();
    }

    /// <summary>
    /// Pushes each tab's enabled state onto its targets. This is the only thing tab state does to
    /// the engine — nothing anywhere asks which tab is <em>selected</em>, because a background tab
    /// must keep being probed. A tab you are not looking at is exactly where an outage hides.
    /// </summary>
    private void ApplyTabStateToTargets()
    {
        foreach (var row in Rows)
        {
            var name = TabConfig.Normalise(row.Target.Config.Tab);
            var tab = _tabs.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            row.Target.TabEnabled = tab?.Enabled ?? true;
            row.Target.TabMuted = tab?.Muted ?? false;
        }
    }

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();

        foreach (var row in Rows)
            if (Passes(row))
                VisibleRows.Add(row);

        TabStripVisibility = Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        FilterActiveVisibility = FilterText.Length > 0 || StatusFilterIndex != 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Re-applies the filters if — and only if — membership actually changed.
    /// <para>
    /// A status filter is live: rows join and leave it as targets fail and recover. But rebuilding
    /// the collection every tick would drop the user's selection and scroll position four times a
    /// second, so the set is compared first and the list is only touched when it really differs.
    /// </para>
    /// </summary>
    private void RefreshFilterMembership()
    {
        if (StatusFilterIndex == 0) return;      // only a status filter can change under us

        var changed = false;
        var index = 0;

        foreach (var row in Rows)
        {
            if (!Passes(row)) continue;

            if (index >= VisibleRows.Count || !ReferenceEquals(VisibleRows[index], row))
            {
                changed = true;
                break;
            }

            index++;
        }

        if (changed || index != VisibleRows.Count) RebuildVisibleRows();
    }

    /// <summary>The tab currently on screen, so a new target defaults to where the user is looking.</summary>
    public string SelectedTabName => _selectedTab;

    public void SelectTab(TabItem tab)
    {
        if (string.Equals(_selectedTab, tab.Name, StringComparison.OrdinalIgnoreCase)) return;

        _selectedTab = tab.Name;

        // Loads this tab's own column choices, capturing the one being left behind first — see
        // ColumnLayout.SwitchToTab.
        ColumnLayout.Instance.SwitchToTab(tab.Name);

        RebuildVisibleRows();
    }

    /// <summary>Switches a whole group of targets on or off, and persists the choice.</summary>
    public void SetTabEnabled(TabItem tab, bool enabled)
    {
        tab.IsEnabled = enabled;

        var config = _tabs.Find(t => string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase));
        if (config is not null) config.Enabled = enabled;

        ApplyTabStateToTargets();
        SaveConfig();
    }

    /// <summary>
    /// Silences a group's alerts while it carries on being probed, and persists the choice.
    /// <para>
    /// Deliberately not the same as disabling it. Muting keeps the data, the history and the
    /// statistics and withholds only the interruption; switching the tab off would stop the probes
    /// and throw away the record of what those hosts did while you were not being told.
    /// </para>
    /// </summary>
    public void SetTabMuted(TabItem tab, bool muted)
    {
        tab.IsMuted = muted;

        var config = _tabs.Find(t => string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase));
        if (config is not null) config.Muted = muted;

        ApplyTabStateToTargets();
        SaveConfig();
    }

    /// <summary>
    /// Renames a tab and repoints every target that belonged to it, or returns why it could not be
    /// done. Null means it succeeded.
    /// <para>
    /// A tab is identified to callers by the <see cref="TabItem"/> they already have rather than by
    /// name, so the rename dialog cannot race a name that changed under it between opening and
    /// confirming.
    /// </para>
    /// </summary>
    public string? RenameTab(TabItem tab, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return "Enter a name.";

        // TabConfig.Normalise hard-codes "General" as where an ungrouped target lands. Renaming
        // this specific tab away would not make it disappear — the next target left with a blank
        // Tab value would simply resurrect an empty one under the old name, while the hosts that
        // used to be there now sit under a name nothing defaults to. Refusing is simpler and more
        // honest than a rename that quietly does not do what it says.
        if (tab.IsDefaultTab)
            return "The General tab can't be renamed — it's where ungrouped targets are kept.";

        // Typing the same name back (or only changing its case, which nothing else here treats as
        // meaningfully different) is not an error — it is closing the dialog having decided not to
        // change anything.
        if (string.Equals(trimmed, tab.Name, StringComparison.Ordinal)) return null;

        var collision = _tabs.Any(t =>
            !string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (collision) return $"A tab named “{trimmed}” already exists.";

        var config = _tabs.Find(t => string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase));
        if (config is not null) config.Name = trimmed;

        var oldName = tab.Name;
        tab.Name = trimmed;

        RepointTargets(oldName, trimmed);
        ColumnLayout.Instance.RenameTab(oldName, trimmed);

        if (string.Equals(_selectedTab, oldName, StringComparison.OrdinalIgnoreCase))
            _selectedTab = trimmed;

        RefreshTabs();
        RebuildVisibleRows();
        SaveConfig();
        return null;
    }

    /// <summary>
    /// Deletes a tab, moving every target that belonged to it back to General rather than deleting
    /// them along with it — losing a grouping is not the same as losing the hosts in it, and a
    /// "delete tab" action must never be a surprise way to stop monitoring something. Returns why it
    /// could not be done, or null on success.
    /// </summary>
    public string? DeleteTab(TabItem tab)
    {
        if (tab.IsDefaultTab)
            return "The General tab can't be deleted — it's where ungrouped targets are kept.";

        var config = _tabs.Find(t => string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase));
        if (config is not null) _tabs.Remove(config);
        Tabs.Remove(tab);

        RepointTargets(tab.Name, "");
        ColumnLayout.Instance.DeleteTab(tab.Name, TabConfig.DefaultName);

        if (string.Equals(_selectedTab, tab.Name, StringComparison.OrdinalIgnoreCase))
            _selectedTab = TabConfig.DefaultName;

        ApplyTabStateToTargets();
        RefreshTabs();
        RebuildVisibleRows();
        SaveConfig();
        return null;
    }

    /// <summary>
    /// Moves every target in <paramref name="fromTab"/> onto <paramref name="toTab"/>, via each
    /// target's own <see cref="PingTarget.UpdateConfig"/> rather than the view model's public
    /// <see cref="UpdateTarget"/> — that path calls <see cref="EnsureTab"/> and saves once per
    /// target, both wrong here: no new tab is being introduced, and this can move many targets at
    /// once, which must persist as one write rather than one per target.
    /// </summary>
    private void RepointTargets(string fromTab, string toTab)
    {
        foreach (var row in Rows)
        {
            if (!string.Equals(TabConfig.Normalise(row.Target.Config.Tab), fromTab, StringComparison.OrdinalIgnoreCase))
                continue;

            var updated = row.Target.Config.Clone();
            updated.Tab = toTab;
            row.Target.UpdateConfig(updated);
            row.Refresh(_settings.CertWarnDays, row.Target.TimeoutMsFrom(_settings));
        }
    }

    /// <summary>Refreshes each tab's tally. Called from the render tick, like everything else.</summary>
    private void RefreshTabs()
    {
        foreach (var tab in Tabs)
        {
            var total = 0;
            var down = 0;

            foreach (var row in Rows)
            {
                if (!string.Equals(TabConfig.Normalise(row.Target.Config.Tab), tab.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                total++;
                if (row.Snapshot.Status.IsFailure()) down++;
            }

            tab.Update(total, down, string.Equals(tab.Name, _selectedTab, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------- column auto-fit

    private readonly ColumnFitter _fitter = new();
    private long _nextFitTick;

    /// <summary>
    /// How often the fit is recomputed. Measuring every row on every 4 Hz tick would be wasted
    /// work, and — more to the point — columns that re-measure continuously twitch as the latency
    /// digits change. Two seconds tracks a genuine change in shape without the board moving under
    /// the cursor.
    /// </summary>
    private const int FitIntervalMs = 2000;

    /// <summary>Recomputes column widths now, ignoring the throttle. Used after a structural change.</summary>
    public void FitColumnsNow()
    {
        if (!ColumnLayout.Instance.AutoFit) return;

        _nextFitTick = Environment.TickCount64 + FitIntervalMs;

        var layout = ColumnLayout.Instance;
        ColumnLayout.Instance.ApplyFit(_fitter.Measure(Rows, layout.CellFontSize, layout.HeaderFontSize, BoardFont));
    }

    private void FitColumnsIfDue()
    {
        if (!ColumnLayout.Instance.AutoFit) return;

        var now = Environment.TickCount64;
        if (now < _nextFitTick) return;

        _nextFitTick = now + FitIntervalMs;

        var layout = ColumnLayout.Instance;
        layout.ApplyFit(_fitter.Measure(Rows, layout.CellFontSize, layout.HeaderFontSize, BoardFont));
    }

    /// <summary>
    /// Face the board is currently rendered in, so measurement matches what is drawn. Null means
    /// the inherited default; the Matrix theme swaps it for a monospaced family.
    /// </summary>
    public Microsoft.UI.Xaml.Media.FontFamily? BoardFont { get; set; }

    /// <summary>Asks every row to re-resolve its status brush after a palette change.</summary>
    public void RefreshStatusBrushes()
    {
        foreach (var row in Rows) row.RefreshStatusBrush();
    }

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

        // The visible list takes its order from Rows, so it has to follow a re-sort.
        RebuildVisibleRows();

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
        // A target added into a tab that does not exist yet brings that tab into being, so the new
        // host is reachable rather than orphaned behind a tab nobody can select.
        EnsureTab(config.Tab);

        var target = new PingTarget(config, _settings);
        _scheduler.AddTarget(target);
        Rows.Add(new TargetRow(target));

        ApplyTabStateToTargets();
        SaveConfig();
        ApplySort();
    }

    /// <summary>
    /// Adds several targets at once, skipping any whose name or address is already on the board.
    /// <para>
    /// One save and one re-sort at the end rather than per target: adding a catalogue category is
    /// forty writes of the config file otherwise, each one rewriting the whole thing.
    /// </para>
    /// </summary>
    /// <returns>How many were actually added, so the caller can say what happened.</returns>
    public int AddTargets(IEnumerable<TargetConfig> configs)
    {
        var added = 0;

        foreach (var config in configs)
        {
            if (NameExists(config.Name)) continue;
            if (Rows.Any(r => string.Equals(r.Target.Config.Address, config.Address, StringComparison.OrdinalIgnoreCase)))
                continue;

            EnsureTab(config.Tab);

            var target = new PingTarget(config, _settings);
            _scheduler.AddTarget(target);
            Rows.Add(new TargetRow(target));
            added++;
        }

        if (added == 0) return 0;

        ApplyTabStateToTargets();
        SaveConfig();
        ApplySort();
        FitColumnsNow();

        return added;
    }

    /// <summary>Adds a tab for <paramref name="name"/> if the board does not have one already.</summary>
    private void EnsureTab(string? name)
    {
        var normalised = TabConfig.Normalise(name);
        if (_tabs.Any(t => string.Equals(t.Name, normalised, StringComparison.OrdinalIgnoreCase))) return;

        _tabs.Add(new TabConfig { Name = normalised, Order = _tabs.Count });
        Tabs.Add(new TabItem(normalised));
    }

    public void UpdateTarget(TargetRow row, TargetConfig config)
    {
        EnsureTab(config.Tab);

        row.Target.UpdateConfig(config);
        row.Refresh(_settings.CertWarnDays, row.Target.TimeoutMsFrom(_settings));

        ApplyTabStateToTargets();
        SaveConfig();
        ApplySort();
    }

    public void RemoveTarget(TargetRow row)
    {
        Rows.Remove(row);
        VisibleRows.Remove(row);
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

        // Switching the outage log back on reattaches to the existing file rather than reloading
        // it: the journal in memory is the authority for this session, and re-reading would
        // duplicate everything already in it.
        _outages = settings.OutageLogEnabled
            ? _outages ?? new OutageStore(ConfigStore.OutagePathFor(ConfigPath))
            : null;

        SaveConfig();
    }

    /// <summary>Alert configuration for the settings dialog to edit.</summary>
    public AlertSettings AlertSettings => (_alertSettings ??= new AlertSettings()).Clone();

    public void ApplyAlertSettings(AlertSettings alerts)
    {
        alerts.Validate();
        _alertSettings = alerts;
        _alerts?.ApplySettings(alerts);
        SaveConfig();
    }

    /// <summary>
    /// Sends one alert now and reports what happened, for a Test button. Returns null on success.
    /// </summary>
    public Task<string?> SendTestAlertAsync(AlertSettings alerts, CancellationToken ct) =>
        _alerts?.SendTestAsync(alerts, ct) ?? Task.FromResult<string?>("the engine is not running");

    // ---------------------------------------------------------------- persistence

    public void SaveConfig()
    {
        if (ConfigPath.Length == 0) return;

        try
        {
            ConfigStore.Save(ConfigPath, _settings, Rows.Select(r => r.Target.Config), _alertSettings, _tabs);
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

    /// <summary>
    /// The board as CSV.
    /// <para>
    /// Now a thin wrapper over <see cref="Export.Board"/>, which writes the same rows plus the
    /// columns this had grown to omit — tab, probe kind, jitter, the rolling availability figures
    /// and certificate expiry. The hand-rolled version here predated all of them and had quietly
    /// become an export of the board as it looked several versions ago.
    /// </para>
    /// </summary>
    public string ToCsv() => ExportBoard();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        SaveCounters();
        await StopEngineAsync().ConfigureAwait(false);
    }
}
