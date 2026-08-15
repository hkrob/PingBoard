using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PingBoard.App.ViewModels;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>
/// Every recorded outage, newest first.
/// <para>
/// This is the view the rest of the application was missing. The board answers "what is wrong now",
/// the availability columns answer "how good has this been" — and nothing answered the question
/// those two provoke, which is "so when did it actually break, and for how long". That was being
/// recorded all along and shown to nobody: transitions went to a CSV nobody opens and to an
/// in-memory journal that surfaced once, as a single line of banner text, and was then discarded at
/// the next restart.
/// </para>
/// </summary>
public static class OutageLogDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm, Action<string> report)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            Height = 420,
            MinWidth = 660,
        };

        var summary = new TextBlock { Opacity = 0.75, Margin = new Thickness(2, 0, 0, 8) };

        var showDegraded = new CheckBox
        {
            Content = "Include degraded periods",
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 0),
        };

        void Fill()
        {
            var all = vm.Outages();
            var wanted = showDegraded.IsChecked == true
                ? all
                : [.. all.Where(o => o.Kind != TransitionKind.Degraded)];

            list.Items.Clear();

            if (wanted.Count == 0)
            {
                // Says why it is empty. "No outages" on a board that has been running for ten
                // minutes means something very different from the same words after a month, and
                // the difference is the entire value of the statement.
                list.Items.Add(new TextBlock
                {
                    Text = "Nothing recorded yet.",
                    Opacity = 0.7,
                    Margin = new Thickness(4),
                });

                summary.Text = "No outages recorded.";
                return;
            }

            foreach (var outage in wanted) list.Items.Add(Row(outage));

            var down = wanted.Count(o => o.Kind == TransitionKind.Hard);
            var ongoing = wanted.Count(o => o.Ongoing);
            var since = wanted[^1].Start;

            summary.Text = $"{down} outage{(down == 1 ? "" : "s")}"
                + (wanted.Count > down ? $", {wanted.Count - down} degraded" : "")
                + (ongoing > 0 ? $", {ongoing} still open" : "")
                + $" · since {since:ddd d MMM HH:mm}";
        }

        showDegraded.Checked += (_, _) => Fill();
        showDegraded.Unchecked += (_, _) => Fill();
        Fill();

        var export = new Button { Content = "Export…", MinWidth = 110 };
        var clear = new Button { Content = "Clear history", MinWidth = 110 };

        export.Click += async (_, _) =>
        {
            var saved = await ExportDialog.SaveAsync("pingboard-outages", vm.ExportOutages, report);
            if (saved is { } path) report($"Outages exported to {path}");
        };

        // Confirmed in place, on the button itself, rather than by opening a confirmation dialog.
        //
        // WinUI permits exactly one ContentDialog at a time: showing a second while this one is up
        // throws, and from an async void Click handler that exception has nowhere to go but the
        // top of the stack. The first version of this did precisely that, so the one destructive
        // button in the application was also the one guaranteed to crash it.
        var armed = false;

        void Disarm()
        {
            armed = false;
            clear.Content = "Clear history";
        }

        clear.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                clear.Content = "Confirm clear";
                summary.Text = "Clearing forgets every recorded outage. The events CSV is not touched.";
                return;
            }

            vm.ClearOutages();
            Disarm();
            Fill();
        };

        // Any other interaction cancels the pending confirmation, so an armed button cannot sit
        // waiting to be hit by a later, unrelated click.
        list.PointerPressed += (_, _) => { if (armed) { Disarm(); Fill(); } };
        export.Click += (_, _) => { if (armed) { Disarm(); Fill(); } };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(export);
        buttons.Children.Add(clear);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(showDegraded, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(showDegraded);
        footer.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(summary);
        body.Children.Add(list);
        body.Children.Add(footer);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Outage log",
            Content = body,
            CloseButtonText = "Close",
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// One outage as a row: when it started, how long it lasted, and what it was.
    /// <para>
    /// Built as a Grid rather than a formatted string so the durations line up down the column.
    /// A list of outages is read by scanning for the long ones.
    /// </para>
    /// </summary>
    private static UIElement Row(in Outage outage)
    {
        var grid = new Grid { Padding = new Thickness(2, 4, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        Add(grid, 0, outage.Start.ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture), 0.85);
        Add(grid, 1, outage.TargetName, 1.0);
        Add(grid, 2, outage.DurationText, 1.0);

        var what = outage.Kind == TransitionKind.Degraded
            ? "degraded"
            : outage.Cause == TargetStatus.Unknown ? "down" : outage.Cause.Label().ToLowerInvariant();

        Add(grid, 3, outage.Ongoing ? what + " · ongoing" : what, 0.85);
        return grid;
    }

    private static void Add(Grid grid, int column, string text, double opacity)
    {
        var block = new TextBlock
        {
            Text = text,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = column is 0 or 2 ? new FontFamily("Consolas") : FontFamily.XamlAutoFontFamily,
        };

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
