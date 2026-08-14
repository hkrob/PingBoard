using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>
/// Adds ready-made groups of hosts, each landing in its own tab.
/// <para>
/// Built in code rather than XAML because the content is generated from
/// <see cref="HostCatalog"/> — a XAML page would only be a container for a list this file has to
/// build anyway.
/// </para>
/// </summary>
public static class AddHostsDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm)
    {
        var checks = new List<(CheckBox Box, string Tab, IReadOnlyList<CatalogEntry> Entries)>();

        var body = new StackPanel { Spacing = 2, MinWidth = 460 };

        // Declared before the rows that follow: each checkbox handler updates it, and a local
        // function cannot capture a variable that is not yet assigned.
        var summary = new TextBlock
        {
            Opacity = 0.75,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        body.Children.Add(new TextBlock
        {
            Text = "Each group is added as its own tab. Hosts already on the board are skipped.",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // Discovered first: it is the most useful group and the only one specific to this machine.
        var local = HostCatalog.DetectLocalNetwork();
        if (local.Count > 0)
        {
            body.Children.Add(Row(
                HostCatalog.LocalNetworkCategory,
                "This machine's own gateway and resolvers — " + string.Join(", ", local.Select(e => e.Address)),
                local,
                checkedByDefault: true));
        }

        foreach (var category in HostCatalog.Categories)
            body.Children.Add(Row(category.Name, category.Description, category.Entries, checkedByDefault: false));

        body.Children.Add(summary);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Add well-known hosts",
            Content = new ScrollViewer { Content = body, MaxHeight = 460 },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        UpdateSummary();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var wanted = checks
            .Where(c => c.Box.IsChecked == true)
            .SelectMany(c => c.Entries.Select(e => new TargetConfig
            {
                Name = e.Name,
                Address = e.Address,
                Probe = e.Probe,
                Port = e.Probe switch { ProbeKind.Https => 443, ProbeKind.Http => 80, _ => 443 },
                Tab = c.Tab,
            }))
            .ToList();

        if (wanted.Count == 0) return;

        var added = vm.AddTargets(wanted);

        // Say what happened rather than leaving the user to count rows. Skipping duplicates is the
        // helpful behaviour, but silently adding fewer than asked would look like a failure.
        vm.ShowBanner(added == wanted.Count
            ? $"Added {added} hosts."
            : $"Added {added} of {wanted.Count} hosts — the rest were already on the board.");

        return;

        StackPanel Row(string name, string description, IReadOnlyList<CatalogEntry> entries, bool checkedByDefault)
        {
            var box = new CheckBox
            {
                Content = $"{name}  ({entries.Count})",
                IsChecked = checkedByDefault,
                MinWidth = 0,
            };

            box.Checked += (_, _) => UpdateSummary();
            box.Unchecked += (_, _) => UpdateSummary();

            checks.Add((box, name, entries));

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock
            {
                Text = description,
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(30, 0, 0, 0),
            });

            return panel;
        }

        void UpdateSummary()
        {
            var total = checks.Where(c => c.Box.IsChecked == true).Sum(c => c.Entries.Count);

            summary.Text = total == 0
                ? "Nothing selected."
                : $"{total} hosts will be added. Every one is a probe on every interval, so prune "
                  + "anything you will not actually look at.";
        }
    }
}
