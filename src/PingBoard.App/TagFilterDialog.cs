using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;

namespace PingBoard.App;

/// <summary>
/// Picks which tags turn a tab into a saved view — see <see cref="PingBoard.Core.TabConfig.SelectedTags"/>.
/// <para>
/// A checkbox per tag currently in use anywhere on the board, not a text box — tags are assigned
/// to targets in <see cref="TargetDialog"/>, so this only ever needs to choose among ones that
/// already exist. Same small-utility, code-only shape as <see cref="RenameTabDialog"/>.
/// </para>
/// </summary>
public static class TagFilterDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm, TabItem tab)
    {
        var known = vm.AllKnownTags;
        var current = vm.GetTabTags(tab);

        var body = new StackPanel { Spacing = 6, MinWidth = 260 };
        var boxes = new List<CheckBox>();

        if (known.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No tags yet — add tags to a target first.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var tag in known)
            {
                var box = new CheckBox
                {
                    Content = tag,
                    IsChecked = current.Contains(tag, StringComparer.OrdinalIgnoreCase),
                };
                boxes.Add(box);
                body.Children.Add(box);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Filter “{tab.Name}” by tags",
            Content = new ScrollViewer { Content = body, MaxHeight = 360 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (known.Count > 0)
        {
            dialog.SecondaryButtonText = "Clear filter";
            dialog.SecondaryButtonClick += (_, _) => vm.SetTabTags(tab, []);
        }

        dialog.PrimaryButtonClick += (_, _) =>
            vm.SetTabTags(tab, [.. boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content)]);

        await dialog.ShowAsync();
    }
}
