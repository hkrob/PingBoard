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
        ColumnsFlyout.Opening += (_, _) => RefreshColumnsMenu();
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
            var dialog = new SettingsDialog(Vm.Settings, Vm.AlertSettings, Vm) { XamlRoot = XamlRoot };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Alerts first: ApplySettings saves the config, and it must write the alert block the
            // user just edited rather than the one loaded at startup.
            if (dialog.AlertResult is { } alerts) Vm.ApplyAlertSettings(alerts);
            if (dialog.Result is { } settings) Vm.ApplySettings(settings);
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
            ExportDialog.OwnerHwnd = _ownerHwnd;
            await ExportDialog.ShowAsync(XamlRoot, Vm, Vm.ShowBanner);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Could not export — {ex.Message}");
        }
    }

    private async void OnOutageLog(object sender, RoutedEventArgs e)
    {
        try
        {
            ExportDialog.OwnerHwnd = _ownerHwnd;
            await OutageLogDialog.ShowAsync(XamlRoot, Vm, Vm.ShowBanner);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Vm.ShowBanner($"Could not open the outage log — {ex.Message}");
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

    private async void OnAddWellKnownHosts(object sender, RoutedEventArgs e)
    {
        try
        {
            await AddHostsDialog.ShowAsync(XamlRoot, Vm);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        try
        {
            await AboutDialog.ShowAsync(XamlRoot);
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the process with it.
            CrashLog.Write(ex);
        }
    }

    // ---------------------------------------------------------------- columns

    /// <summary>Raised when auto-fit is toggled, so the window can persist it.</summary>
    public event Action<bool>? AutoFitChanged;

    /// <summary>Reflects the restored setting in the menu without reporting it back as a change.</summary>
    public void SetAutoFitChecked(bool on) => AutoFitColumns.IsChecked = on;

    /// <summary>Raised when the startup update check is toggled, so the window can persist it.</summary>
    public event Action<bool>? UpdateCheckChanged;

    public void SetUpdateCheckChecked(bool on) => CheckUpdatesOnStartup.IsChecked = on;

    private void OnToggleUpdateCheck(object sender, RoutedEventArgs e) =>
        UpdateCheckChanged?.Invoke(CheckUpdatesOnStartup.IsChecked);

    /// <summary>Raised when the column order changes, so the window can persist it.</summary>
    public event Action<string>? ColumnOrderChanged;

    private async void OnArrangeColumns(object sender, RoutedEventArgs e)
    {
        try
        {
            await ArrangeColumnsDialog.ShowAsync(XamlRoot, () =>
            {
                // Widths were fitted to the old arrangement; refit so a column that moved next to
                // a wider neighbour is not left at the wrong size until the next throttled pass.
                Vm.FitColumnsNow();
                ColumnOrderChanged?.Invoke(ColumnLayout.Instance.OrderCsv);
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void OnToggleAutoFit(object sender, RoutedEventArgs e)
    {
        ColumnLayout.Instance.AutoFit = AutoFitColumns.IsChecked;

        // Measure straight away rather than waiting for the throttle: the user just asked for it,
        // and a menu item that takes two seconds to do anything reads as broken.
        Vm.FitColumnsNow();
        AutoFitChanged?.Invoke(AutoFitColumns.IsChecked);
    }

    /// <summary>One-shot fit, for when auto-fit is off or the content has just changed shape.</summary>
    private void OnFitColumnsNow(object sender, RoutedEventArgs e)
    {
        var wasOn = ColumnLayout.Instance.AutoFit;

        ColumnLayout.Instance.AutoFit = true;
        Vm.FitColumnsNow();

        if (!wasOn)
        {
            // Leave the fitted widths in place but stop tracking, so a one-off fit does not
            // silently turn continuous fitting on.
            ColumnLayout.Instance.StopTracking();
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

    private void OnMuteTab(object sender, RoutedEventArgs e) => SetTabMuted(sender, true);
    private void OnUnmuteTab(object sender, RoutedEventArgs e) => SetTabMuted(sender, false);

    private async void OnRenameTab(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: TabItem tab })
                await RenameTabDialog.ShowAsync(XamlRoot, Vm, tab);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnFilterTabByTags(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: TabItem tab })
                await TagFilterDialog.ShowAsync(XamlRoot, Vm, tab);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnFilterTabBySite(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: TabItem tab })
                await SiteFilterDialog.ShowAsync(XamlRoot, Vm, tab);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnNewTab(object sender, RoutedEventArgs e)
    {
        try { await NewTabDialog.ShowAsync(XamlRoot, Vm); }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    private async void OnDeleteTab(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { DataContext: TabItem tab }) return;

            if (tab.IsDefaultTab)
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Can't delete this tab",
                    Content = "The General tab can't be deleted — it's where ungrouped targets are kept.",
                    CloseButtonText = "OK",
                }.ShowAsync();
                return;
            }

            var count = Vm.Rows.Count(r => string.Equals(
                TabConfig.Normalise(r.Target.Config.Tab), tab.Name, StringComparison.OrdinalIgnoreCase));

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete tab",
                Content = count > 0
                    ? $"Delete “{tab.Name}”? Its {count} target{(count == 1 ? "" : "s")} "
                      + $"{(count == 1 ? "moves" : "move")} to General — nothing is removed."
                    : $"Delete “{tab.Name}”?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            Vm.DeleteTab(tab);
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }

    /// <summary>
    /// Muting a tab keeps it running and only withholds the alerts, which is the difference
    /// between it and disabling: that stops the probes and loses the record.
    /// </summary>
    private void SetTabMuted(object sender, bool muted)
    {
        if (sender is FrameworkElement { DataContext: TabItem tab }) Vm.SetTabMuted(tab, muted);
    }

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
                if (s is not ToggleMenuFlyoutItem { Tag: string columnId } toggle) return;

                ColumnLayout.Instance.SetVisible(columnId, toggle.IsChecked);

                // Showing or hiding a column changes how much room every other visible column
                // could use, not just the one just toggled — hiding a wide column frees space the
                // others could grow into, showing one takes space away from them. FitColumnsNow
                // re-measures every currently-visible column for exactly this reason, ignoring the
                // normal throttle so the board reflows immediately rather than up to two seconds
                // later.
                Vm.FitColumnsNow();
            };

            ColumnsFlyout.Items.Add(item);
        }
    }

    /// <summary>
    /// Resyncs every checkbox to what is actually visible right now, immediately before the menu
    /// is shown.
    /// <para>
    /// The items themselves are built once at construction and never recreated — column choices
    /// are now per tab, so which columns are checked has to track whichever tab is on screen, and
    /// rebuilding the whole menu on every switch just to flip some checkmarks would be wasteful for
    /// what is otherwise identical, static content.
    /// </para>
    /// </summary>
    private void RefreshColumnsMenu()
    {
        foreach (var entry in ColumnsFlyout.Items)
            if (entry is ToggleMenuFlyoutItem { Tag: string id } toggle)
                toggle.IsChecked = ColumnLayout.Instance.IsVisible(id);
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
