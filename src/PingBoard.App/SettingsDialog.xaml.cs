using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>Edits the global <c>[Settings]</c> block and the <c>[Alerts]</c> block.</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MainViewModel? _vm;

    public SettingsDialog(Settings current, AlertSettings alerts, MainViewModel? vm = null)
    {
        InitializeComponent();

        _vm = vm;

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

        LoadAlerts(alerts);

        PrimaryButtonClick += (_, _) =>
        {
            Result = Build(current);
            AlertResult = BuildAlerts(alerts);
        };
    }

    /// <summary>Populated on Apply; null if the user cancelled.</summary>
    public Settings? Result { get; private set; }

    /// <summary>Alert settings as edited. Null if the user cancelled.</summary>
    public AlertSettings? AlertResult { get; private set; }

    private void LoadAlerts(AlertSettings alerts)
    {
        WebhookCheck.IsChecked = alerts.WebhookEnabled;
        WebhookUrlBox.Text = alerts.WebhookUrl;

        // Decrypted for editing, never shown as ciphertext: a field holding "dpapi:AQAAA…" looks
        // like corruption and invites the user to clear it.
        WebhookAuthBox.Password = ProtectedValue.Unprotect(alerts.WebhookAuthorization);

        EmailCheck.IsChecked = alerts.EmailEnabled;
        SmtpHostBox.Text = alerts.SmtpHost;
        SmtpPortBox.Value = alerts.SmtpPort;
        StartTlsCheck.IsChecked = alerts.SmtpUseStartTls;
        SmtpUserBox.Text = alerts.SmtpUser;
        SmtpPasswordBox.Password = ProtectedValue.Unprotect(alerts.SmtpPassword);
        EmailFromBox.Text = alerts.EmailFrom;
        EmailToBox.Text = alerts.EmailTo;

        MinIntervalBox.Value = alerts.MinIntervalSeconds;
        NotifyRecoveryCheck.IsChecked = alerts.NotifyOnRecovery;
    }

    private AlertSettings BuildAlerts(AlertSettings current)
    {
        var alerts = current.Clone();

        alerts.WebhookEnabled = WebhookCheck.IsChecked == true;
        alerts.WebhookUrl = WebhookUrlBox.Text.Trim();
        alerts.WebhookAuthorization = ProtectedValue.Protect(WebhookAuthBox.Password);

        alerts.EmailEnabled = EmailCheck.IsChecked == true;
        alerts.SmtpHost = SmtpHostBox.Text.Trim();
        alerts.SmtpPort = Read(SmtpPortBox, current.SmtpPort);
        alerts.SmtpUseStartTls = StartTlsCheck.IsChecked == true;
        alerts.SmtpUser = SmtpUserBox.Text.Trim();
        alerts.SmtpPassword = ProtectedValue.Protect(SmtpPasswordBox.Password);
        alerts.EmailFrom = EmailFromBox.Text.Trim();
        alerts.EmailTo = EmailToBox.Text.Trim();

        alerts.MinIntervalSeconds = Read(MinIntervalBox, current.MinIntervalSeconds);
        alerts.NotifyOnRecovery = NotifyRecoveryCheck.IsChecked == true;

        // Validate clamps the numbers and switches off any sink with nowhere to send, which is the
        // failure mode worth catching here: enabled, misconfigured, and silent.
        alerts.Validate();
        return alerts;
    }

    /// <summary>
    /// Sends one alert with the settings as currently typed, without saving them.
    /// <para>
    /// The only way to find out whether an SMTP server will actually accept these credentials is
    /// to try. Discovering it does not at the moment something breaks is the worst possible time.
    /// </para>
    /// </summary>
    private async void OnSendTestAlert(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_vm is null) return;

        TestAlertButton.IsEnabled = false;
        TestAlertResult.Text = "Sending…";

        try
        {
            var candidate = BuildAlerts(new AlertSettings());

            var error = await _vm.SendTestAlertAsync(candidate, CancellationToken.None)
                                 .ConfigureAwait(true);

            TestAlertResult.Text = error is null
                ? "Sent."
                : "Failed — " + error;
        }
        catch (Exception ex)
        {
            // An async void handler that throws takes the process with it.
            CrashLog.Write(ex);
            TestAlertResult.Text = "Failed — " + ex.Message;
        }
        finally
        {
            TestAlertButton.IsEnabled = true;
        }
    }

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
