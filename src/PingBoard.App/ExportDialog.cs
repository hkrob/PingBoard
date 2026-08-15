using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;
using WinRT.Interop;

namespace PingBoard.App;

/// <summary>
/// Chooses what to export, then where to put it.
/// <para>
/// Three separate sheets rather than one combined file, because they answer different questions and
/// have different shapes: one row per target, one row per outage, one row per probe. Merging them
/// would give a spreadsheet that cannot be sorted usefully by anything.
/// </para>
/// </summary>
public static class ExportDialog
{
    /// <summary>
    /// The window handle to parent the file picker to. An unpackaged WinUI app must supply this
    /// explicitly or <c>PickSaveFileAsync</c> throws rather than showing a dialog.
    /// </summary>
    public static nint OwnerHwnd { get; set; }

    public static async Task ShowAsync(XamlRoot root, MainViewModel vm, Action<string> report)
    {
        var board = new RadioButton { Content = "Board — one row per target, with availability", IsChecked = true };
        var outages = new RadioButton { Content = "Outages — one row per outage, with durations" };
        var history = new RadioButton { Content = "History — every retained probe sample" };

        var note = new TextBlock
        {
            Text = "History covers the retained window only — a few hundred samples per target, "
                 + "not the whole record.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(board);
        body.Children.Add(outages);
        body.Children.Add(history);
        body.Children.Add(note);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Export",
            Content = body,
            PrimaryButtonText = "Export…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var (name, content) =
            outages.IsChecked == true ? ("pingboard-outages", (Func<string>)vm.ExportOutages)
            : history.IsChecked == true ? ("pingboard-history", vm.ExportHistory)
            : ("pingboard-board", vm.ExportBoard);

        var saved = await SaveAsync(root, name, content);
        if (saved is { } path) report($"Exported to {path}");
    }

    /// <summary>
    /// Renders and writes one export, returning the path written or null if the user cancelled.
    /// <para>
    /// The content is produced through a callback rather than passed in, so nothing is rendered
    /// until a destination has actually been chosen — the history export of a large board is
    /// megabytes of string, and building it to throw away on Cancel would be the one place this
    /// application allocates seriously for no reason.
    /// </para>
    /// </summary>
    public static async Task<string?> SaveAsync(XamlRoot root, string suggestedName, Func<string> content)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            InitializeWithWindow.Initialize(picker, OwnerHwnd);
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            picker.SuggestedFileName = $"{suggestedName}-{DateTimeOffset.Now:yyyyMMdd-HHmm}";

            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;

            await File.WriteAllTextAsync(file.Path, content(), System.Text.Encoding.UTF8);
            return file.Path;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);

            await new ContentDialog
            {
                XamlRoot = root,
                Title = "Export failed",
                Content = ex.Message,
                CloseButtonText = "Close",
            }.ShowAsync();

            return null;
        }
    }
}
