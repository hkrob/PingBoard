using Microsoft.UI.Xaml.Controls;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>Edits the global <c>[Settings]</c> block.</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Settings current)
    {
        InitializeComponent();

        IntervalBox.Value = current.IntervalMs;
        TimeoutBox.Value = current.TimeoutMs;
        MaxConcurrentBox.Value = current.MaxConcurrent;
        PayloadBox.Value = current.PayloadBytes;
        TtlBox.Value = current.Ttl;
        WindowBox.Value = current.RollingWindow;
        DnsTtlBox.Value = current.DnsCacheSeconds;
        ReresolveBox.Value = current.FailuresBeforeReresolve;
        FailuresBox.Value = current.FailuresBeforeDown;
        SettleBox.Value = current.ResumeSettleMs;

        PreferIPv4Check.IsChecked = current.PreferIPv4;
        NotifyCheck.IsChecked = current.NotifyOnChange;
        LogCheck.IsChecked = current.LogEnabled;
        LogPathBox.Text = current.LogPath;

        PrimaryButtonClick += (_, _) => Result = Build(current);
    }

    /// <summary>Populated on Apply; null if the user cancelled.</summary>
    public Settings? Result { get; private set; }

    private Settings Build(Settings current)
    {
        var settings = current.Clone();

        settings.IntervalMs = Read(IntervalBox, current.IntervalMs);
        settings.TimeoutMs = Read(TimeoutBox, current.TimeoutMs);
        settings.MaxConcurrent = Read(MaxConcurrentBox, current.MaxConcurrent);
        settings.PayloadBytes = Read(PayloadBox, current.PayloadBytes);
        settings.Ttl = Read(TtlBox, current.Ttl);
        settings.RollingWindow = Read(WindowBox, current.RollingWindow);
        settings.DnsCacheSeconds = Read(DnsTtlBox, current.DnsCacheSeconds);
        settings.FailuresBeforeReresolve = Read(ReresolveBox, current.FailuresBeforeReresolve);
        settings.FailuresBeforeDown = Read(FailuresBox, current.FailuresBeforeDown);
        settings.ResumeSettleMs = Read(SettleBox, current.ResumeSettleMs);

        settings.PreferIPv4 = PreferIPv4Check.IsChecked == true;
        settings.NotifyOnChange = NotifyCheck.IsChecked == true;
        settings.LogEnabled = LogCheck.IsChecked == true;

        var logPath = LogPathBox.Text.Trim();
        settings.LogPath = logPath.Length > 0 ? logPath : current.LogPath;

        // Clamping happens in the view model via Settings.Validate, which is also what protects a
        // hand-edited config file.
        return settings;
    }

    private static int Read(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)box.Value;
}
