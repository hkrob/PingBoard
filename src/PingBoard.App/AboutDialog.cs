using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>
/// About box: what this is, which version, where it came from, and whether a newer one exists.
/// <para>
/// Built in code rather than XAML because it is a handful of stacked TextBlocks whose only dynamic
/// part is the update line, and a separate .xaml/.xaml.cs pair for that would be more ceremony
/// than content.
/// </para>
/// </summary>
public static class AboutDialog
{
    /// <summary>
    /// Version of the running assembly. Comes from the csproj, which is also what the update check
    /// compares against the newest release tag.
    /// </summary>
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentDisplay => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static async Task ShowAsync(XamlRoot root)
    {
        var status = new TextBlock
        {
            Text = "",
            Opacity = 0.75,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var check = new Button { Content = "Check for updates" };
        var download = new Button
        {
            Content = "Download and install",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var link = new HyperlinkButton
        {
            Content = UpdateCheck.ProjectUrl,
            NavigateUri = new Uri(UpdateCheck.ProjectUrl),
            Padding = new Thickness(0, 4, 0, 4),
        };

        var body = new StackPanel { Spacing = 2, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = "PingBoard", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Version " + CurrentDisplay, Opacity = 0.8 });
        body.Children.Add(new TextBlock
        {
            Text = "Always-on ping monitor for Windows 11.",
            Opacity = 0.65,
            Margin = new Thickness(0, 6, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(link);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(check);
        buttons.Children.Add(download);
        body.Children.Add(buttons);
        body.Children.Add(status);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "About",
            Content = body,
            CloseButtonText = "Close",
        };

        var pending = new UpdateInfo();

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Checking…";

            pending = await UpdateCheck.CheckAsync(Current, CancellationToken.None);

            check.IsEnabled = true;

            if (pending.Error is { } error)
            {
                status.Text = "Could not check for updates — " + error;
                return;
            }

            if (!pending.Available)
            {
                status.Text = $"You are on the latest version ({CurrentDisplay}).";
                return;
            }

            status.Text = $"Version {pending.LatestVersion} is available.";

            // Offered, never taken automatically: this replaces the binary of something the user
            // is relying on to be watching their network.
            download.Visibility = pending.DownloadUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (pending.DownloadUrl.Length == 0)
                status.Text += " That release has no installer attached — see the release page.";
        };

        download.Click += async (_, _) =>
        {
            download.IsEnabled = false;
            status.Text = "Downloading…";

            var (path, error) = await UpdateInstaller.DownloadAsync(pending.DownloadUrl, CancellationToken.None);

            if (error is not null)
            {
                status.Text = "Download failed — " + error;
                download.IsEnabled = true;
                return;
            }

            status.Text = "Starting the installer. PingBoard will close.";
            UpdateInstaller.Launch(path);
        };

        await dialog.ShowAsync();
    }
}
