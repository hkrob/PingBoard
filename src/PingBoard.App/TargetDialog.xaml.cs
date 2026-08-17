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

        // Offer the tabs that already exist, but editable so a new one can be created here rather
        // than only by hand-editing the ini.
        foreach (var tab in vm.Tabs) TabBox.Items.Add(tab.Name);

        // Same for sites, plus the abbreviation registry SelectionChanged reads from.
        foreach (var site in vm.Sites) SiteBox.Items.Add(site.Name);

        if (editing is not null)
        {
            var config = editing.Target.Config;
            AddressBox.Text = config.Address;
            NameBox.Text = config.Name;
            ProbeBox.SelectedIndex = IndexFor(config.Probe);
            PortBox.Value = config.Port;
            EnabledSwitch.IsOn = config.Enabled;
            PathBox.Text = config.Path;
            SetOptional(ExpectStatusBox, config.ExpectStatus);
            TabBox.Text = TabConfig.Normalise(config.Tab);
            MaintenanceBox.Text = config.Maintenance;

            SiteBox.Text = config.Site;
            SiteAbbreviationBox.Text = vm.Sites
                .FirstOrDefault(s => string.Equals(s.Name, config.Site, StringComparison.OrdinalIgnoreCase))
                ?.Abbreviation ?? "";

            SetOptional(IntervalBox, config.IntervalMs);
            SetOptional(TimeoutBox, config.TimeoutMs);
            SetOptional(PayloadBox, config.PayloadBytes);
            SetOptional(TtlBox, config.Ttl);
            SetOptional(FailuresBox, config.FailuresBeforeDown);
            SetOptional(DegradedLatencyBox, config.DegradedLatencyMs);
            SetOptional(DegradedLossBox, config.DegradedLossPercent);

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
            ClearOptional(ExpectStatusBox);

            // A new target lands in whichever tab is on screen, which is almost always the one
            // the user meant.
            TabBox.Text = vm.SelectedTabName;
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

    private static void SetOptional(NumberBox box, double? value) =>
        box.Value = value ?? double.NaN;

    private static int? ReadOptional(NumberBox box) =>
        double.IsNaN(box.Value) ? null : (int)box.Value;

    /// <summary>
    /// As <see cref="ReadOptional"/>, keeping the fraction — half a percent of loss is a
    /// distinction worth having on a threshold.
    /// </summary>
    private static double? ReadOptionalDouble(NumberBox box) =>
        double.IsNaN(box.Value) ? null : box.Value;

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

    /// <summary>
    /// Fills the abbreviation in from the registry when an existing site is picked from the
    /// dropdown — never on free-typing or on merely losing focus, both of which would otherwise
    /// risk silently overwriting an abbreviation the user just finished editing by hand.
    /// </summary>
    private void OnSiteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteBox.SelectedItem is not string name) return;

        var match = _vm.Sites.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) SiteAbbreviationBox.Text = match.Abbreviation;
    }

    private static int IndexFor(ProbeKind kind) => kind switch
    {
        ProbeKind.Tcp => 1,
        ProbeKind.Http => 2,
        ProbeKind.Https => 3,
        _ => 0,
    };

    private static ProbeKind KindFor(int index) => index switch
    {
        1 => ProbeKind.Tcp,
        2 => ProbeKind.Http,
        3 => ProbeKind.Https,
        _ => ProbeKind.Icmp,
    };

    private void UpdateProbeUi()
    {
        var kind = KindFor(ProbeBox.SelectedIndex);
        var isIcmp = kind == ProbeKind.Icmp;
        var isHttp = kind is ProbeKind.Http or ProbeKind.Https;

        // Payload and TTL shape an ICMP echo and mean nothing to the others.
        PortBox.IsEnabled = !isIcmp;
        PayloadBox.IsEnabled = isIcmp;
        TtlBox.IsEnabled = isIcmp;

        HttpPanel.Visibility = isHttp
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // Follow the conventional port when switching scheme, unless the user has set something
        // that is not simply the other scheme's default.
        if (isHttp)
        {
            var current = (int)PortBox.Value;
            if (kind == ProbeKind.Http && current is 443) PortBox.Value = 80;
            if (kind == ProbeKind.Https && current is 80) PortBox.Value = 443;
        }

        ProbeHint.Text = kind switch
        {
            ProbeKind.Tcp =>
                "TCP connect. A completed handshake proves reachability more strongly than an echo reply, and works against hosts that drop ICMP. A refused connection is reported separately from a timeout — it means the host is up but the port is closed.",

            ProbeKind.Http or ProbeKind.Https =>
                "HTTP request, judged on the status code. A TCP connect to 80 or 443 only proves the socket opens — a wedged server accepts connections and returns 500 to everything, and the board would show it green. Redirects are not followed, so a 301 is reported as it is rather than silently measuring somewhere else.",

            _ =>
                "ICMP echo. If a host silently drops ping — common on firewalled or corporate networks — switch to TCP connect against a port you know is open.",
        };
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

        var kind = KindFor(ProbeBox.SelectedIndex);
        var isIcmp = kind == ProbeKind.Icmp;
        var isHttp = kind is ProbeKind.Http or ProbeKind.Https;

        var path = PathBox.Text.Trim();
        if (path.Length == 0) path = "/";

        if (isHttp && path.Contains(' ', StringComparison.Ordinal))
        {
            Reject(args, "A request path cannot contain spaces.");
            return;
        }

        // Rejected here rather than silently ignored. The parser deliberately fails open — a typo
        // silences nothing — but a user who typed a window and got no warning would reasonably
        // believe it was in force, and only find out when an alert arrives mid-maintenance.
        var maintenance = MaintenanceBox.Text.Trim();
        if (maintenance.Length > 0 && MaintenanceSchedule.Parse(maintenance).IsEmpty)
        {
            Reject(args, "Could not read that maintenance window. Use HH:mm-HH:mm, optionally "
                         + "prefixed with days — for example “Sat 22:00-02:00” or “Mon-Fri 01:30-02:00”.");
            return;
        }

        var site = SiteBox.Text.Trim();

        // Whatever is in the box when Save is clicked is what gets stored for the site, matching
        // how every other field here works — no hidden "leave it alone if blank" behaviour. The
        // dropdown's SelectionChanged is what protects against an accidental clobber, by only ever
        // filling this box from the registry on a deliberate pick, never merely on losing focus.
        _vm.SetSiteAbbreviation(site, SiteAbbreviationBox.Text.Trim());

        Result = new TargetConfig
        {
            Name = name,
            Address = address,
            Probe = kind,
            Port = isIcmp ? 443 : (int)PortBox.Value,
            Enabled = EnabledSwitch.IsOn,
            Tab = TabConfig.Normalise(TabBox.Text),
            Site = site,
            Maintenance = maintenance,
            Path = isHttp ? path : "/",
            ExpectStatus = isHttp ? ReadOptional(ExpectStatusBox) : null,
            IntervalMs = ReadOptional(IntervalBox),
            TimeoutMs = ReadOptional(TimeoutBox),
            PayloadBytes = isIcmp ? ReadOptional(PayloadBox) : null,
            Ttl = isIcmp ? ReadOptional(TtlBox) : null,
            FailuresBeforeDown = ReadOptional(FailuresBox),
            DegradedLatencyMs = ReadOptional(DegradedLatencyBox),
            DegradedLossPercent = ReadOptionalDouble(DegradedLossBox),
        };
    }

    private void Reject(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
