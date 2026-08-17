using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;

namespace PingBoard.App;

/// <summary>
/// Creates a new, empty tab from the "+" on the tab strip — the counterpart to
/// <see cref="RenameTabDialog"/>, same single-text-box, code-only shape.
/// </summary>
public static class NewTabDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm)
    {
        var box = new TextBox { MinWidth = 280, PlaceholderText = "Tab name" };

        var error = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(box);
        body.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "New tab",
            Content = body,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var problem = vm.CreateTab(box.Text);
            if (problem is null) return;

            args.Cancel = true;
            error.Message = problem;
            error.IsOpen = true;
        };

        dialog.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();
    }
}
