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
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                ConfigStore.Save(path, new Settings(), SeedTargets());
            }

            await Vm.LoadAsync(path);

            _uiState.LastConfigPath = path;
            _uiState.Save();

            Notifications.Initialize();
            _tray = new TrayIcon(this);

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
        }
    }

    private static IEnumerable<TargetConfig> SeedTargets() =>
    [
        new() { Name = "loopback", Address = "127.0.0.1" },
        new() { Name = "gateway", Address = "10.1.10.1" },
        new() { Name = "cloudflare-dns", Address = "1.1.1.1" },
    ];

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
            }
            else
            {
                MatrixTheme.Revert(_board);
                RootGrid.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
            }

            _board.ApplyPalette();
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

    // ---------------------------------------------------------------- placement

    private void RestorePlacement()
    {
        ColumnLayout.Instance.HiddenCsv = _uiState.HiddenColumns;

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
        _uiState.HiddenColumns = ColumnLayout.Instance.HiddenCsv;
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
        _tray.ShowHiddenHint();
    }

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
    public void StartHidden() => AppWindow.Hide();

    public void BringToFront()
    {
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        Activate();
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

        // Transitions only, never individual failed probes — and the engine emits none at all
        // while suspended, so waking from sleep is silent rather than a burst of forty toasts.
        var title = transition.Up
            ? $"{transition.TargetName} recovered"
            : $"{transition.TargetName} is down";

        var body = transition.Up
            ? $"Back up after {TargetRow.FormatSpan(transition.DownFor)}."
            // Threshold comes from the transition, not from global settings — this target may
            // have its own, and quoting the global would report a number never applied to it.
            : $"{transition.Status.Label()} after {transition.Threshold} consecutive attempts.";

        // Toast first; tray balloon when the App SDK path is unavailable, which is the normal case
        // under self-contained deployment. Exactly one of the two fires.
        if (!Notifications.Show(title, body)) _tray?.Flash(title, body);
    }
}
