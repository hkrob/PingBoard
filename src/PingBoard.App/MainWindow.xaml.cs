using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PingBoard.App.ViewModels;
using PingBoard.Core;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace PingBoard.App;

/// <summary>
/// Window shell: placement, tray, and the close-to-tray behaviour. The board itself lives in
/// <see cref="BoardView"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly UiState _uiState = UiState.Load();
    private readonly BoardView _board;
    private TrayIcon? _tray;
    private bool _closingForReal;

    public MainWindow()
    {
        InitializeComponent();

        Vm = new MainViewModel(DispatcherQueue);
        Vm.Transition += OnTransition;

        // The board in use is remembered the moment it changes. It used to be written only by
        // SavePlacement — on hide to tray or on Exit — so a board opened with "Open config" and
        // then ended any other way (sign-out, shutdown, a crash, the installer closing the app for
        // an update) was forgotten, and the next launch quietly went back to the previous board.
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.ConfigPath) || Vm.ConfigPath.Length == 0) return;
            if (string.Equals(_uiState.LastConfigPath, Vm.ConfigPath, StringComparison.OrdinalIgnoreCase)) return;

            _uiState.LastConfigPath = Vm.ConfigPath;
            _uiState.Save();
        };

        Title = "PingBoard";

        _board = new BoardView(Vm, WindowNative.GetWindowHandle(this));
        RootGrid.Children.Add(_board);

        // Restore without notifying, or we would write the state file back on every launch.
        _board.SelectTheme(_uiState.Theme, notify: false);
        ApplyTheme(_uiState.Theme);

        _board.ThemeChanged += theme =>
        {
            ApplyTheme(theme);
            _uiState.Theme = theme;
            _uiState.Save();
        };

        // Restored before the first refresh tick, so a mute that was in force at shutdown is still
        // in force at startup rather than lifting itself silently.
        NotificationMute.Restore(_uiState.MuteUntil);

        _board.MuteChanged += () =>
        {
            _uiState.MuteUntil = NotificationMute.Serialize();
            _uiState.Save();
        };

        // Before AutoFit, so the first fit measures the arrangement the user actually left.
        if (_uiState.ColumnOrder.Length > 0) ColumnLayout.Instance.OrderCsv = _uiState.ColumnOrder;

        _board.ColumnOrderChanged += order =>
        {
            _uiState.ColumnOrder = order;
            _uiState.Save();
        };

        ColumnLayout.Instance.Zoom = _uiState.ZoomPercent / 100.0;
        ColumnLayout.Instance.AutoFit = _uiState.AutoFitColumns;
        _board.SetAutoFitChecked(_uiState.AutoFitColumns);

        _board.AutoFitChanged += on =>
        {
            _uiState.AutoFitColumns = on;
            _uiState.Save();
        };

        _board.SetUpdateCheckChecked(_uiState.CheckUpdatesOnStartup);

        _board.UpdateCheckChanged += on =>
        {
            _uiState.CheckUpdatesOnStartup = on;
            _uiState.Save();
        };

        _board.ZoomChanged += zoom =>
        {
            _uiState.ZoomPercent = (int)Math.Round(zoom * 100);
            _uiState.Save();
        };

        // Extending into the title bar is what makes Mica read as one continuous surface rather
        // than a themed window with a grey strip bolted on top.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_board.TitleBar);

        // SetIcon throws rather than degrading when the file is absent, and a missing icon is not
        // a reason to fail startup.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "pingboard.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        RestorePlacement();

        // When following Windows, a live theme switch changes ActualTheme without going through
        // ApplyTheme, so the caption buttons need recolouring here too.
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme(_uiState.Theme);

        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;

        // AppWindow.Changed covers minimise and restore; these two cover the first Activate and
        // any show/hide that does not pass through it. A board wrongly believed hidden would stop
        // updating on screen, so every route in is watched.
        VisibilityChanged += (_, _) => SyncBoardVisibility();
        Activated += (_, _) => SyncBoardVisibility();

        // Minimising and restoring never passes through BringToFront, which only the tray uses,
        // so the presenter state is watched directly. Changed fires for moves and resizes too;
        // ReportWhileAway is a no-op unless the window was actually put away.
        AppWindow.Changed += (sender, _) =>
        {
            SyncBoardVisibility();

            if (sender.Presenter is not OverlappedPresenter presenter) return;

            if (presenter.State == OverlappedPresenterState.Minimized) _awaySince ??= DateTimeOffset.Now;
            else ReportWhileAway();
        };

        _ = InitializeAsync();
    }

    public MainViewModel Vm { get; }

    private async Task InitializeAsync()
    {
        try
        {
            var path = Program.RequestedConfigPath
                       ?? _uiState.LastConfigPath
                       ?? AppPaths.DefaultConfigFile;

            // First run: seed a config so the board is immediately useful and the file the user is
            // meant to hand-edit actually exists.
            //
            // Only when there is no .bak either. A missing config with its backup still beside it
            // is not a first run — it is exactly what the resilient loader exists to recover from,
            // and seeding here used to pre-empt that: the three sample hosts were written in its
            // place, and the next save copied them over the .bak, destroying the user's real board.
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                ConfigStore.Save(path, new Settings(), SeedTargets());
            }

            await Vm.LoadAsync(path);

            _uiState.LastConfigPath = path;
            _uiState.Save();

            Notifications.Initialize();
            _tray = new TrayIcon(this);

            _ = CheckForUpdatesAsync();

            // A minimized start with no tray icon would leave the app running with no way to reach
            // it — invisible, and apparently unresponsive. Showing the window is the lesser evil.
            if (Program.StartMinimized && !_tray.IsVisible)
            {
                BringToFront();
                Vm.ShowBanner("Started minimized, but the tray icon could not be created — showing the window instead.");
            }

            // Keep the tray tooltip current, so the up/down tally is readable on hover without
            // restoring the window.
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusText))
                    _tray?.SetTooltip($"PingBoard — {Vm.StatusText}");
            };
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Startup problem: {ex.Message} — logged to {CrashLog.Path}");

            // The tray icon is normally created above, after the load. If the load is what threw,
            // an autostart launch — window hidden, waiting for the tray — would be left running with
            // neither, reachable only through Task Manager, and monitoring nothing. A failed start
            // is something the user has to see, so it gets the window regardless of --minimized.
            try { _tray ??= new TrayIcon(this); }
            catch (Exception trayError) { CrashLog.Write(trayError); }

            BringToFront();
        }
    }

    /// <summary>
    /// The first-run board: this machine's own default gateway, found rather than assumed, and two
    /// public resolvers — the smallest set that tells a local fault from an internet one.
    /// <para>
    /// The gateway used to be a fixed private address, which was only ever right for the network
    /// it was written on; everywhere else it was a permanently red row on a brand-new install.
    /// A machine with no gateway (offline, or unusual routing) simply gets no gateway row.
    /// </para>
    /// </summary>
    private static List<TargetConfig> SeedTargets()
    {
        var seed = new List<TargetConfig>();

        try
        {
            var gateway = HostCatalog.DetectLocalNetwork()
                .FirstOrDefault(e => e.Name.StartsWith("gateway", StringComparison.Ordinal));

            if (gateway.Address is { Length: > 0 } address)
                seed.Add(new TargetConfig { Name = "gateway", Address = address });
        }
        catch (System.Net.NetworkInformation.NetworkInformationException ex)
        {
            CrashLog.Write(ex);
        }

        seed.Add(new TargetConfig { Name = "cloudflare-dns", Address = "1.1.1.1" });
        seed.Add(new TargetConfig { Name = "google-dns", Address = "8.8.8.8" });
        return seed;
    }

    /// <summary>
    /// Applies the theme to the window's content root.
    /// <para>
    /// It must be the content root, not a child: a system backdrop such as Mica reads its
    /// light/dark cue from <c>Window.Content</c>, so theming a nested element restyles the text
    /// while leaving the backdrop dark.
    /// </para>
    /// <para>
    /// <c>Default</c> follows Windows live. Because the status colours resolve through theme
    /// dictionaries at bind time rather than being baked into the row objects, a system
    /// light/dark switch recolours every row without a restart.
    /// </para>
    /// </summary>
    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" or MatrixTheme.Name => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // A theme is a cosmetic preference. Nothing here is worth taking the board down for, so a
        // failure degrades to the standard palette rather than surfacing as an unrecoverable error.
        try
        {
            if (theme == MatrixTheme.Name)
            {
                MatrixTheme.Apply(_board);
                RootGrid.Background = MatrixTheme.PlateBrush;

                // Column fitting measures text, so it has to measure in the face actually drawn.
                Vm.BoardFont = MatrixTheme.Font;
            }
            else
            {
                MatrixTheme.Revert(_board);
                RootGrid.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
                Vm.BoardFont = null;
            }

            _board.ApplyPalette();
            Vm.FitColumnsNow();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MatrixTheme.Revert(_board);
            RootGrid.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
        }

        // The caption buttons sit outside the XAML tree, so they need colouring separately or the
        // minimise/close glyphs stay light-on-light after a switch to the light theme.
        var caption = AppWindow.TitleBar;
        caption.ButtonBackgroundColor = Colors.Transparent;
        caption.ButtonInactiveBackgroundColor = Colors.Transparent;

        var isLight = RootGrid.ActualTheme == ElementTheme.Light;
        var glyph = MatrixTheme.IsApplied
            ? MatrixTheme.CaptionForeground
            : isLight ? Colors.Black : Colors.White;

        caption.ButtonForegroundColor = glyph;
        caption.ButtonHoverForegroundColor = glyph;
        caption.ButtonHoverBackgroundColor = isLight
            ? Color.FromArgb(24, 0, 0, 0)
            : Color.FromArgb(24, 255, 255, 255);
        caption.ButtonInactiveForegroundColor = isLight
            ? Color.FromArgb(160, 0, 0, 0)
            : Color.FromArgb(160, 255, 255, 255);
    }

    /// <summary>
    /// Asks GitHub for a newer release shortly after startup, at most once a day.
    /// <para>
    /// Fire-and-forget and entirely silent unless there is something to say. A monitoring tool that
    /// interrupts you at launch to report that it is already up to date has spent your attention on
    /// nothing — and attention is the currency this whole application is trying to protect. A
    /// failed check says nothing at all: the network being down is what the board is for, not a
    /// reason to nag.
    /// </para>
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (!_uiState.CheckUpdatesOnStartup) return;

        if (DateTimeOffset.TryParse(_uiState.LastUpdateCheck, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var last)
            && DateTimeOffset.Now - last < TimeSpan.FromDays(1))
        {
            return;
        }

        try
        {
            // A moment's grace so the check never competes with getting the board on screen.
            await Task.Delay(TimeSpan.FromSeconds(5));

            var info = await UpdateCheck.CheckAsync(AboutDialog.Current, CancellationToken.None);

            _uiState.LastUpdateCheck = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            _uiState.Save();

            if (info is { Available: true, LatestVersion: { } version })
            {
                DispatcherQueue.TryEnqueue(() =>
                    Vm.ShowBanner($"PingBoard {version} is available — open About to install it."));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    // ---------------------------------------------------------------- placement

    private void RestorePlacement()
    {
        ColumnLayout.Instance.RestoreHidden(_uiState.HiddenColumns, _uiState.ColumnsByTab);

        if (_uiState.WindowWidth > 200 && _uiState.WindowHeight > 150)
        {
            AppWindow.MoveAndResize(new RectInt32(
                _uiState.WindowX, _uiState.WindowY, _uiState.WindowWidth, _uiState.WindowHeight));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(1360, 720));
        }
    }

    private void SavePlacement()
    {
        // Never persist a minimized window's placement — restoring to it would put the window
        // somewhere off-screen.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return;

        var position = AppWindow.Position;
        var size = AppWindow.Size;

        _uiState.WindowX = position.X;
        _uiState.WindowY = position.Y;
        _uiState.WindowWidth = size.Width;
        _uiState.WindowHeight = size.Height;
        var (sharedDefault, byTab) = ColumnLayout.Instance.SnapshotForSave();
        _uiState.HiddenColumns = sharedDefault;
        _uiState.ColumnsByTab.Clear();
        foreach (var (tab, hidden) in byTab) _uiState.ColumnsByTab[tab] = hidden;

        _uiState.LastConfigPath = Vm.ConfigPath;
        _uiState.Save();
    }

    // ---------------------------------------------------------------- lifetime

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Closing hides to the tray rather than exiting: this is a tool you leave running, and
        // losing a week of counters to a stray Alt+F4 would be a poor trade. Exit is on the tray
        // menu.
        if (_closingForReal || _tray is null) return;

        args.Cancel = true;
        SavePlacement();
        Vm.SaveCounters();
        AppWindow.Hide();
        SyncBoardVisibility();
        _tray.ShowHiddenHint();

        _awaySince = DateTimeOffset.Now;
    }

    /// <summary>
    /// Tells the view model whether anyone can see the board, so it can stop redrawing one that is
    /// in the tray or minimised. Called on every placement change and at each explicit hide/show,
    /// rather than trusting any single event to cover all of them.
    /// </summary>
    private void SyncBoardVisibility() =>
        Vm.SetBoardVisible(AppWindow.IsVisible
            && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized });

    /// <summary>Exits for real, bypassing hide-to-tray. Called from the tray menu.</summary>
    public void ExitApplication()
    {
        _closingForReal = true;
        SavePlacement();
        Close();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            _tray?.Dispose();
            _tray = null;

            await Vm.DisposeAsync();
            Notifications.Shutdown();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// Comes up hidden, for an autostart launch. Deliberately not Activate-then-Hide: that shows
    /// a window on the screen for a frame or two at every login, which is precisely the behaviour
    /// this is meant to avoid.
    /// </summary>
    public void StartHidden()
    {
        AppWindow.Hide();
        SyncBoardVisibility();
        _awaySince = DateTimeOffset.Now;
    }

    // ---------------------------------------------------------------- while you were away

    /// <summary>
    /// When the window was last put away, or null while it is on screen.
    /// <para>
    /// "Away" means hidden to the tray or minimised — states where the board is genuinely not
    /// being looked at. Losing focus deliberately does not count: alt-tabbing to another window
    /// for ten seconds is not being away, and treating it as such would fire this summary
    /// constantly until the user learned to ignore it.
    /// </para>
    /// </summary>
    private DateTimeOffset? _awaySince;

    /// <summary>
    /// Below this, there is nothing worth summarising and the interruption is not earned.
    /// </summary>
    private static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(2);

    private void ReportWhileAway()
    {
        if (_awaySince is not { } since) return;

        // Still not being looked at: hidden in the tray, or minimised. Leave the clock running
        // rather than clearing it, or a stray window event would swallow the summary before
        // anyone could read it.
        if (!AppWindow.IsVisible) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        _awaySince = null;

        var now = DateTimeOffset.Now;
        if (now - since < AwayThreshold) return;

        // Null when nothing transitioned, and nothing is shown in that case. Silence already
        // means everything was fine.
        if (Vm.Journal.Summarise(since, now) is { } summary) Vm.ShowBanner(summary);
    }

    public void BringToFront()
    {
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        Activate();
        SyncBoardVisibility();
        ReportWhileAway();
    }

    public void ShowFatalError(Exception ex)
    {
        try
        {
            Vm.ShowBanner($"Unrecoverable error: {ex.Message} — logged to {CrashLog.Path}");
        }
        catch (Exception)
        {
            // Nothing further we can usefully do.
        }
    }

    // ---------------------------------------------------------------- notifications

    private void OnTransition(StateTransition transition)
    {
        if (!Vm.Settings.NotifyOnChange) return;

        // Soft transitions are opt-in and off by default. A host that is merely slow is a thing to
        // notice on the board, not a thing to be interrupted by — and the outage log records it
        // either way, so nothing is lost by staying quiet.
        if (transition.Kind != TransitionKind.Hard && !Vm.Settings.NotifyOnDegraded) return;

        // Muted suppresses the popup only. Webhook and email alerting is left running on purpose:
        // it exists to reach you when you are away from this machine, and silencing it because
        // someone quietened a desktop toast would be the opposite of what they asked for.
        if (NotificationMute.IsMuted) return;

        // Transitions only, never individual failed probes — and the engine emits none at all
        // while suspended, so waking from sleep is silent rather than a burst of forty toasts.
        var (title, body) = transition.Kind switch
        {
            TransitionKind.Certificate => (
                $"{transition.TargetName} certificate expiring",
                CertificateBody(transition)),

            TransitionKind.Degraded when transition.Up => (
                $"{transition.TargetName} back to normal",
                $"Within its thresholds again after {TargetRow.FormatSpan(transition.DownFor)}."),

            TransitionKind.Degraded => (
                $"{transition.TargetName} is degraded",
                "Still replying, but past its latency or loss threshold."),

            _ when transition.Up => (
                $"{transition.TargetName} recovered",
                $"Back up after {TargetRow.FormatSpan(transition.DownFor)}."),

            // Threshold comes from the transition, not from global settings — this target may
            // have its own, and quoting the global would report a number never applied to it.
            _ => (
                $"{transition.TargetName} is down",
                $"{transition.Status.Label()} after {transition.Threshold} consecutive attempts."),
        };

        // Toast first; tray balloon when the App SDK path is unavailable, which is the normal case
        // under self-contained deployment. Exactly one of the two fires.
        if (!Notifications.Show(title, body)) _tray?.Flash(title, body);
    }

    /// <summary>
    /// For a certificate transition <c>DownFor</c> carries the time remaining, not time elapsed,
    /// and goes negative once expiry has passed.
    /// </summary>
    private static string CertificateBody(in StateTransition transition)
    {
        var remaining = transition.DownFor;

        return remaining <= TimeSpan.Zero
            ? $"The TLS certificate expired {TargetRow.FormatSpan(-remaining)} ago."
            : $"The TLS certificate expires in {(int)remaining.TotalDays} days.";
    }
}
