using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;

namespace PingBoard.App;

/// <summary>
/// Renames one tab and repoints every target that belonged to it.
/// <para>
/// A single text box rather than a form, on the same small-utility, code-only pattern as
/// <see cref="ArrangeColumnsDialog"/> and <see cref="ExportDialog"/> — nothing here earns a XAML
/// file of its own. Validation and the rename itself both happen in the Primary click handler,
/// matching those dialogs rather than <see cref="TargetDialog"/>'s heavier Result-then-apply
/// shape, because there is no extra branching the caller needs to do afterwards.
/// </para>
/// </summary>
public static class RenameTabDialog
{
    public static async Task ShowAsync(XamlRoot root, MainViewModel vm, TabItem tab)
    {
        var box = new TextBox { Text = tab.Name, MinWidth = 280 };

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
            Title = $"Rename “{tab.Name}”",
            Content = body,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",

            // Unlike the destructive confirmations elsewhere, which default to Close on purpose: a
            // rename is trivially reversible, and Enter-to-confirm is what typing a new name into a
            // single box and being done with it should feel like.
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var problem = vm.RenameTab(tab, box.Text);
            if (problem is null) return;

            args.Cancel = true;
            error.Message = problem;
            error.IsOpen = true;
        };

        dialog.Loaded += (_, _) => box.SelectAll();

        await dialog.ShowAsync();
    }
}
