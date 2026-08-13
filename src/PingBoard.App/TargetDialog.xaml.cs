using System.Net;
using Microsoft.UI.Xaml.Controls;
using PingBoard.App.ViewModels;
using PingBoard.Core;

namespace PingBoard.App;

/// <summary>Add or edit a single target. Validates before allowing the dialog to close.</summary>
public sealed partial class TargetDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly TargetRow? _editing;
    private bool _nameEditedByUser;

    public TargetDialog(MainViewModel vm, TargetRow? editing)
    {
        InitializeComponent();

        _vm = vm;
        _editing = editing;

        Title = editing is null ? "Add target" : $"Edit {editing.Name}";

        if (editing is not null)
        {
            var config = editing.Target.Config;
            AddressBox.Text = config.Address;
            NameBox.Text = config.Name;
            ProbeBox.SelectedIndex = config.Probe == ProbeKind.Tcp ? 1 : 0;
            PortBox.Value = config.Port;
            EnabledSwitch.IsOn = config.Enabled;

            SetOptional(IntervalBox, config.IntervalMs);
            SetOptional(TimeoutBox, config.TimeoutMs);
            SetOptional(PayloadBox, config.PayloadBytes);
            SetOptional(TtlBox, config.Ttl);
            SetOptional(FailuresBox, config.FailuresBeforeDown);

            _nameEditedByUser = true;
        }
        else
        {
            ProbeBox.SelectedIndex = 0;
            ClearOptional(IntervalBox);
            ClearOptional(TimeoutBox);
            ClearOptional(PayloadBox);
            ClearOptional(TtlBox);
            ClearOptional(FailuresBox);
        }

        NameBox.TextChanged += (_, _) => _nameEditedByUser = true;
        UpdateProbeUi();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    /// <summary>Populated on Save; null if the user cancelled.</summary>
    public TargetConfig? Result { get; private set; }

    private static void SetOptional(NumberBox box, int? value) =>
        box.Value = value ?? double.NaN;

    private static void ClearOptional(NumberBox box) => box.Value = double.NaN;

    private static int? ReadOptional(NumberBox box) =>
        double.IsNaN(box.Value) ? null : (int)box.Value;

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        // Auto-fill the display name from the address until the user takes it over, so adding a
        // target is a one-field operation in the common case.
        if (_nameEditedByUser) return;

        var address = AddressBox.Text.Trim();
        NameBox.Text = address.Contains('.', StringComparison.Ordinal)
                       && !IPAddress.TryParse(address, out _)
            ? address.Split('.')[0]
            : address;
    }

    private void OnProbeChanged(object sender, SelectionChangedEventArgs e) => UpdateProbeUi();

    private void UpdateProbeUi()
    {
        var isTcp = ProbeBox.SelectedIndex == 1;

        PortBox.IsEnabled = isTcp;
        PayloadBox.IsEnabled = !isTcp;
        TtlBox.IsEnabled = !isTcp;

        ProbeHint.Text = isTcp
            ? "TCP connect. A completed handshake proves reachability more strongly than an echo reply, and works against hosts that drop ICMP. A refused connection is reported separately from a timeout — it means the host is up but the port is closed."
            : "ICMP echo. If a host silently drops ping — common on firewalled or corporate networks — switch to TCP connect against a port you know is open.";
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var address = AddressBox.Text.Trim();
        var name = NameBox.Text.Trim();

        if (address.Length == 0)
        {
            Reject(args, "Enter an IP address or hostname.");
            return;
        }

        if (address.Contains(' ', StringComparison.Ordinal))
        {
            Reject(args, "An address cannot contain spaces.");
            return;
        }

        if (name.Length == 0) name = address;

        // Names key the persisted counters, so a duplicate would silently merge two targets'
        // statistics.
        if (_vm.NameExists(name, _editing))
        {
            Reject(args, $"A target named “{name}” already exists. Names must be unique.");
            return;
        }

        var isTcp = ProbeBox.SelectedIndex == 1;

        Result = new TargetConfig
        {
            Name = name,
            Address = address,
            Probe = isTcp ? ProbeKind.Tcp : ProbeKind.Icmp,
            Port = isTcp ? (int)PortBox.Value : 443,
            Enabled = EnabledSwitch.IsOn,
            IntervalMs = ReadOptional(IntervalBox),
            TimeoutMs = ReadOptional(TimeoutBox),
            PayloadBytes = isTcp ? null : ReadOptional(PayloadBox),
            Ttl = isTcp ? null : ReadOptional(TtlBox),
            FailuresBeforeDown = ReadOptional(FailuresBox),
        };
    }

    private void Reject(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
