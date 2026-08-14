using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PingBoard.App.ViewModels;
using PingBoard.Core;
using Windows.System;
using WinRT.Interop;

namespace PingBoard.App;

/// <summary>
/// The board itself. Hosted by <see cref="MainWindow"/>, which keeps window-level concerns
/// (placement, tray, close-to-tray) out of here.
/// </summary>
public sealed partial class BoardView : UserControl
{
    private readonly IntPtr _ownerHwnd;

    public BoardView(MainViewModel vm, IntPtr ownerHwnd)
    {
        // Assigned before InitializeComponent: the generated x:Bind code reads Vm during it.
        Vm = vm;
        _ownerHwnd = ownerHwnd;

        InitializeComponent();

        BuildColumnsMenu();
        UpdateSortIndicator();

        // Read from the registry rather than from our own saved state: the entry can be removed
        // by another tool, or by the uninstaller, and the menu should show what is actually true.
        StartWithWindows.IsChecked = Autostart.IsEnabled;

        AddZoomAccelerators();

        KeyDown += OnKeyDown;

        // Focus the board once it is up. Keyboard accelerators only fire when focus is somewhere
        // inside their scope, and a freshly opened window has focus nowhere — so Ctrl+= did
        // nothing until the user happened to click a row first. This also makes Ins, Del and F2
        // work immediately rather than after a click, which was always the case and always wrong.
        Loaded += (_, _) => BoardList.Focus(FocusState.Programmatic);
    }

    public MainViewModel Vm { get; }

    /// <summary>
    /// The draggable caption strip. Exposed for <see cref="MainWindow"/> to hand to
    /// <c>SetTitleBar</c> — x:Name fields are private, and content extended into the title bar
    /// leaves the window unmovable unless a drag region is nominated.
    /// </summary>
    public UIElement TitleBar => TitleBarArea;

    private TargetRow? Selected => BoardList.SelectedItem as TargetRow;

    private List<TargetRow> SelectedRows => BoardList.SelectedItems.OfType<TargetRow>().ToList();

    // ---------------------------------------------------------------- targets

    private async void OnAddTarget(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new TargetDialog(Vm, null) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } config)
                Vm.AddTarget(config);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnEditTarget(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected is not { } row) return;

            var dialog = new TargetDialog(Vm, row) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } config)
                Vm.UpdateTarget(row, config);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnRemoveTarget(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = SelectedRows;
            if (rows.Count == 0) return;

            var what = rows.Count == 1 ? $"“{rows[0].Name}”" : $"{rows.Count} targets";
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remove target",
                Content = $"Remove {what}? Saved statistics for them will be lost.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            foreach (var row in rows) Vm.RemoveTarget(row);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows) Vm.TogglePaused(row);
    }

    private void OnResetSelected(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows) Vm.ResetStats(row);
    }

    private async void OnResetAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Reset all statistics",
                Content = "Clear every counter and history buffer on the board? The targets themselves are kept.",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary) Vm.ResetStats();
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SettingsDialog(Vm.Settings) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } settings)
                Vm.ApplySettings(settings);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    // ---------------------------------------------------------------- files

    private async void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _ownerHwnd);
            picker.FileTypeFilter.Add(".ini");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            Vm.SaveCounters();
            await Vm.LoadAsync(file.Path);
            UpdateSortIndicator();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Could not open config — {ex.Message}");
        }
    }

    private async void OnSaveConfigAs(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            InitializeWithWindow.Initialize(picker, _ownerHwnd);
            picker.FileTypeChoices.Add("Configuration", [".ini"]);
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(Vm.ConfigPath) is { Length: > 0 } stem
                ? stem
                : "pingboard";

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            // Carry the counters across too, so "save as" preserves history rather than silently
            // resetting every target to zero.
            ConfigStore.Save(file.Path, Vm.Settings, Vm.Rows.Select(r => r.Target.Config));
            StateStore.Save(ConfigStore.StatePathFor(file.Path), Vm.Rows.Select(r => r.Target));
            await Vm.LoadAsync(file.Path);
            UpdateSortIndicator();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Could not save config — {ex.Message}");
        }
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            InitializeWithWindow.Initialize(picker, _ownerHwnd);
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            picker.SuggestedFileName = "pingboard-export";

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await File.WriteAllTextAsync(file.Path, Vm.ToCsv());
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Could not export — {ex.Message}");
        }
    }

    private async void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(Vm.ConfigPath));
            if (directory is not null && Directory.Exists(directory))
                await Launcher.LaunchFolderPathAsync(directory);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private void OnBannerClosed(InfoBar sender, object args) => Vm.HideBanner();

    // ---------------------------------------------------------------- theme

    /// <summary>Raised when the user picks a theme, so the window can persist it.</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// Expands or collapses the hop rows under a target. The button lives inside the item
    /// template, so its DataContext is the row it belongs to.
    /// </summary>
    private void OnToggleDetail(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TargetRow row }) row.ToggleDetail();
    }

    /// <summary>
    /// Traces the right-clicked target on demand, rather than waiting for it to fail. Useful on a
    /// target that is merely slow, or to capture a known-good path to compare against later.
    /// </summary>
    private async void OnTraceNow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TargetRow row }) return;

        row.BeginTrace();

        try
        {
            // The row picks the finished trace up through its normal refresh; only the "could not
            // run at all" case needs reporting here.
            if (await Vm.TraceNowAsync(row).ConfigureAwait(true) is null)
                row.TraceUnavailable("No resolved address for this target — nothing to trace. "
                                     + "A name that will not resolve fails at DNS, not along the path.");
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the process down with it.
            CrashLog.Write(ex);
            row.TraceUnavailable("Trace failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>Raised when the zoom changes, so the window can persist it.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>
    /// Ctrl+wheel zooms; a bare wheel scrolls as usual.
    /// <para>
    /// Marked handled only when Ctrl is down, so the ListView still receives every ordinary wheel
    /// event and scrolling is untouched.
    /// </para>
    /// </summary>
    private void OnBoardPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!IsControlDown()) return;

        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (delta > 0) ColumnLayout.Instance.ZoomIn();
        else ColumnLayout.Instance.ZoomOut();

        ZoomChanged?.Invoke(ColumnLayout.Instance.Zoom);
        e.Handled = true;
    }

    /// <summary>
    /// Registers Ctrl with plus, minus and zero as accelerators rather than handling them in
    /// <c>KeyDown</c>.
    /// <para>
    /// KeyDown only fires when something inside the board has focus, so a shortcut wired that way
    /// does nothing while the caret sits in the filter box — which is exactly where it will be
    /// when someone decides the text is too small. An accelerator fires for the whole control
    /// regardless of what holds focus.
    /// </para>
    /// <para>
    /// Both the number row and the keypad are registered. Binding only one leaves half the users
    /// pressing a key that does nothing.
    /// </para>
    /// </summary>
    private void AddZoomAccelerators()
    {
        var layout = ColumnLayout.Instance;

        Register(VirtualKey.Add, layout.ZoomIn);
        Register((VirtualKey)0xBB, layout.ZoomIn);           // OemPlus, '=' unshifted
        Register(VirtualKey.Subtract, layout.ZoomOut);
        Register((VirtualKey)0xBD, layout.ZoomOut);          // OemMinus
        Register(VirtualKey.Number0, layout.ZoomReset);
        Register(VirtualKey.NumberPad0, layout.ZoomReset);

        void Register(VirtualKey key, Action apply)
        {
            var accelerator = new KeyboardAccelerator
            {
                Modifiers = VirtualKeyModifiers.Control,
                Key = key,
            };

            accelerator.Invoked += (_, args) =>
            {
                apply();
                ZoomChanged?.Invoke(ColumnLayout.Instance.Zoom);
                args.Handled = true;
            };

            KeyboardAccelerators.Add(accelerator);
        }
    }

    private static bool IsControlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---------------------------------------------------------------- tabs

    private void OnSelectTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabItem tab }) Vm.SelectTab(tab);
    }

    private void OnEnableTab(object sender, RoutedEventArgs e) => SetTabEnabled(sender, true);
    private void OnDisableTab(object sender, RoutedEventArgs e) => SetTabEnabled(sender, false);

    /// <summary>
    /// Switching a tab off pauses every target in it. Note that this is the <em>only</em> thing a
    /// tab does to the engine — selecting a different tab changes what is on screen and nothing
    /// else, because the hosts you are not looking at are exactly the ones worth still probing.
    /// </summary>
    private void SetTabEnabled(object sender, bool enabled)
    {
        if (sender is FrameworkElement { DataContext: TabItem tab }) Vm.SetTabEnabled(tab, enabled);
    }

    // ---------------------------------------------------------------- mute

    /// <summary>Raised when the mute changes, so the window can persist it across a restart.</summary>
    public event Action? MuteChanged;

    private void OnMuteHour(object sender, RoutedEventArgs e) => SetMute(TimeSpan.FromHours(1));
    private void OnMuteTwelveHours(object sender, RoutedEventArgs e) => SetMute(TimeSpan.FromHours(12));

    private void OnMuteIndefinitely(object sender, RoutedEventArgs e)
    {
        NotificationMute.MuteIndefinitely();
        AfterMuteChanged();
    }

    private void OnUnmute(object sender, RoutedEventArgs e)
    {
        NotificationMute.Unmute();
        AfterMuteChanged();
    }

    private void SetMute(TimeSpan duration)
    {
        NotificationMute.MuteFor(duration);
        AfterMuteChanged();
    }

    private void AfterMuteChanged()
    {
        // Refresh immediately rather than waiting up to 250 ms for the next tick — the user just
        // clicked, and a button that does not visibly change reads as one that did not work.
        Vm.RefreshMuteState();
        MuteChanged?.Invoke();
    }

    /// <summary>
    /// Re-resolves the brushes that XAML binds only once, and asks every row to re-resolve its
    /// status colour.
    /// <para>
    /// A <c>{ThemeResource}</c> reference is evaluated when the element is realised and again on an
    /// actual theme change — but not because a palette was swapped underneath it. The row colours
    /// and the sparkline resolve by key on every use and need no help; these three brushes do.
    /// </para>
    /// </summary>
    public void ApplyPalette()
    {
        HeaderBar.Background = Controls.BoardPalette.Find("HeaderBackgroundBrush");
        HeaderBar.BorderBrush = Controls.BoardPalette.Find("RowSeparatorBrush");
        StatusBar.BorderBrush = Controls.BoardPalette.Find("RowSeparatorBrush");

        // The status key itself has not changed, so the converter would not otherwise re-run.
        Vm.RefreshStatusBrushes();
    }

    private void OnThemeSystem(object sender, RoutedEventArgs e) => SelectTheme("System");
    private void OnThemeLight(object sender, RoutedEventArgs e) => SelectTheme("Light");
    private void OnThemeDark(object sender, RoutedEventArgs e) => SelectTheme("Dark");
    private void OnThemeMatrix(object sender, RoutedEventArgs e) => SelectTheme(MatrixTheme.Name);

    /// <summary>
    /// Toggles the Windows startup entry, reverting the tick if the write was refused. A menu that
    /// shows "on" while the registry says otherwise is worse than no toggle at all — the user would
    /// only find out at the next login, which is the moment they were relying on it.
    /// </summary>
    private void OnToggleAutostart(object sender, RoutedEventArgs e)
    {
        if (Autostart.Set(StartWithWindows.IsChecked) is { } error)
        {
            Vm.ShowBanner(error);
            StartWithWindows.IsChecked = Autostart.IsEnabled;
        }
    }

    /// <summary>
    /// Reflects the chosen theme in the menu and reports it upward.
    /// <para>
    /// The theme is deliberately <em>not</em> applied here. A system backdrop such as Mica takes
    /// its light/dark cue from <c>Window.Content</c>, so setting <c>RequestedTheme</c> on this
    /// control restyles the text while leaving the backdrop dark — light-grey text on a dark
    /// plate. <see cref="MainWindow"/> applies it to the content root instead.
    /// </para>
    /// </summary>
    public void SelectTheme(string theme, bool notify = true)
    {
        ThemeSystem.IsChecked = theme is not ("Light" or "Dark" or MatrixTheme.Name);
        ThemeLight.IsChecked = theme == "Light";
        ThemeDark.IsChecked = theme == "Dark";
        ThemeMatrix.IsChecked = theme == MatrixTheme.Name;

        if (notify) ThemeChanged?.Invoke(theme);
    }

    // ---------------------------------------------------------------- sorting & columns

    private void OnSortStatus(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Status);
    private void OnSortName(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Name);
    private void OnSortIp(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Ip);
    private void OnSortHostname(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Hostname);
    private void OnSortLastOk(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.LastOk);
    private void OnSortLastNok(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.LastNok);
    private void OnSortCumulative(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Cumulative);
    private void OnSortRtt(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Rtt);
    private void OnSortLoss(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Loss);
    private void OnSortUptime(object sender, TappedRoutedEventArgs e) => SortBy(SortKey.Uptime);

    private void SortBy(SortKey key)
    {
        Vm.SortBy(key);
        UpdateSortIndicator();
    }

    /// <summary>Maps each sortable column to the header that should carry the arrow.</summary>
    private IEnumerable<(SortKey Key, TextBlock Header)> SortableHeaders =>
    [
        (SortKey.Status, HdrStatus),
        (SortKey.Name, HdrName),
        (SortKey.Ip, HdrIp),
        (SortKey.Hostname, HdrHostname),
        (SortKey.LastOk, HdrLastOk),
        (SortKey.LastNok, HdrLastNok),
        (SortKey.Cumulative, HdrCumulative),
        (SortKey.Rtt, HdrRtt),
        (SortKey.Loss, HdrLoss),
        (SortKey.Uptime, HdrUptime),
    ];

    /// <summary>
    /// Puts the sort arrow on the active column's own header and restores every other header to
    /// its plain label. The label is held in <c>Tag</c> so appending the arrow is non-destructive.
    /// </summary>
    private void UpdateSortIndicator()
    {
        var arrow = Vm.SortDescending ? " ▼" : " ▲";

        foreach (var (key, header) in SortableHeaders)
        {
            var label = header.Tag as string ?? header.Text;
            header.Text = key == Vm.Sort ? label + arrow : label;
            header.Opacity = key == Vm.Sort ? 1.0 : 0.75;
        }
    }

    private void BuildColumnsMenu()
    {
        foreach (var id in ColumnLayout.AllIds)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = ColumnLayout.HeaderFor(id),
                IsChecked = ColumnLayout.Instance.IsVisible(id),
                Tag = id,
            };

            item.Click += (s, _) =>
            {
                if (s is ToggleMenuFlyoutItem { Tag: string columnId } toggle)
                    ColumnLayout.Instance.SetVisible(columnId, toggle.IsChecked);
            };

            ColumnsFlyout.Items.Add(item);
        }
    }

    // ---------------------------------------------------------------- keyboard

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl-modified keys are claimed by the zoom accelerators before reaching here; guarding
        // anyway so Ctrl+Delete can never be read as "remove the selected target".
        if (IsControlDown()) return;

        switch (e.Key)
        {
            case VirtualKey.Insert:
                OnAddTarget(sender, e);
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                OnRemoveTarget(sender, e);
                e.Handled = true;
                break;

            case VirtualKey.F2:
                OnEditTarget(sender, e);
                e.Handled = true;
                break;
        }
    }
}
