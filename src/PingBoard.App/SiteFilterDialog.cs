using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;

namespace PingBoard.App;

/// <summary>
/// Picks which sites turn a tab into a saved view — the site analogue of <see cref="TagFilterDialog"/>,
/// independent of it (see <see cref="PingBoard.Core.TabConfig.SelectedSites"/>).
/// <para>
/// A checkbox per registered site rather than a text box, drawn from the site registry
/// (<see cref="MainViewModel.Sites"/>) rather than scanned from targets — unlike tags, sites already
/// have a canonical list to offer.
/// </para>
/// </summary>
public static class SiteFilterDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm, TabItem tab)
    {
        var known = vm.Sites;
        var current = vm.GetTabSites(tab);

        var body = new StackPanel { Spacing = 6, MinWidth = 260 };
        var boxes = new List<CheckBox>();

        if (known.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No sites yet — add a site to a target first.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var site in known)
            {
                var box = new CheckBox
                {
                    Content = site.Name,
                    IsChecked = current.Contains(site.Name, StringComparer.OrdinalIgnoreCase),
                };
                boxes.Add(box);
                body.Children.Add(box);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Filter “{tab.Name}” by site",
            Content = new ScrollViewer { Content = body, MaxHeight = 360 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (known.Count > 0)
        {
            dialog.SecondaryButtonText = "Clear filter";
            dialog.SecondaryButtonClick += (_, _) => vm.SetTabSites(tab, []);
        }

        dialog.PrimaryButtonClick += (_, _) =>
            vm.SetTabSites(tab, [.. boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content)]);

        await dialog.ShowAsync();
    }
}
