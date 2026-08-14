using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Where a transition should be sent, beyond the tray notification and the CSV.
/// <para>
/// Both sinks exist for the same reason: a tray balloon only reaches you while you are sitting in
/// front of this machine, which is exactly when you would have noticed the board turn red anyway.
/// The alert that earns its keep is the one that reaches you when you are somewhere else.
/// </para>
/// </summary>
public sealed class AlertSettings
{
    public bool WebhookEnabled { get; set; }

    /// <summary>Endpoint to POST to. Anything that accepts a JSON body — Home Assistant, ntfy, Discord, Slack.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>
    /// Optional <c>Authorization</c> header value, sent verbatim (e.g. <c>Bearer abc123</c>).
    /// Stored DPAPI-protected — see <see cref="ProtectedValue"/>.
    /// </summary>
    public string WebhookAuthorization { get; set; } = "";

    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string SmtpUser { get; set; } = "";

    /// <summary>SMTP password. Stored DPAPI-protected — see <see cref="ProtectedValue"/>.</summary>
    public string SmtpPassword { get; set; } = "";

    public string EmailFrom { get; set; } = "";
    public string EmailTo { get; set; } = "";

    /// <summary>
    /// Suppresses repeat alerts for the same target within this window. Transitions are already
    /// rare by construction, but a target flapping on a threshold of 1 can still produce a
    /// down/up pair every few seconds, and an inbox full of those trains you to ignore the lot.
    /// Zero disables the suppression.
    /// </summary>
    public int MinIntervalSeconds { get; set; } = 60;

    /// <summary>Send an alert when the target recovers, not only when it goes down.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>Clamps every value into a sane range. Applied after loading a hand-edited file.</summary>
    public void Validate()
    {
        SmtpPort = Math.Clamp(SmtpPort, 1, 65535);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 0, 86_400);
        TimeoutMs = Math.Clamp(TimeoutMs, 1000, 120_000);

        WebhookUrl = WebhookUrl.Trim();
        SmtpHost = SmtpHost.Trim();
        EmailFrom = EmailFrom.Trim();
        EmailTo = EmailTo.Trim();

        // An enabled sink with nowhere to send is a silent misconfiguration: the user believes
        // they are covered and no alert ever arrives. Treat it as off, visibly.
        if (WebhookUrl.Length == 0 || !IsHttpUrl(WebhookUrl)) WebhookEnabled = false;
        if (SmtpHost.Length == 0 || EmailFrom.Length == 0 || EmailTo.Length == 0) EmailEnabled = false;
    }

    public static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public AlertSettings Clone() => (AlertSettings)MemberwiseClone();

    /// <summary>True when at least one sink is configured and enabled.</summary>
    public bool AnyEnabled => WebhookEnabled || EmailEnabled;
}

/// <summary>
/// One transition, flattened into the fields an alert wants. Kept separate from
/// <see cref="StateTransition"/> so the payload shape is a deliberate contract rather than
/// whatever the engine happens to carry internally.
/// </summary>
public readonly record struct AlertPayload(
    string Target,
    string Address,
    string Event,
    string Status,
    DateTimeOffset When,
    double OutageSeconds,
    int Threshold,
    string Host)
{
    public static AlertPayload From(in StateTransition t, string address) => new(
        Target: t.TargetName,
        Address: address,
        Event: t.Up ? "recovered" : "down",
        Status: t.Status.Label(),
        When: t.When,
        OutageSeconds: t.Up ? Math.Round(t.DownFor.TotalSeconds, 1) : 0,
        Threshold: t.Threshold,
        Host: Environment.MachineName);

    /// <summary>
    /// "name (1.2.3.4)", or bare "name" when there is no address to report.
    /// <para>
    /// A DNS failure never resolved to anything, so the address is legitimately empty — and
    /// "bad-name () is DOWN" reads like a bug in the alert rather than a fact about the network,
    /// which is not what you want to be parsing at 3am.
    /// </para>
    /// </summary>
    private string Where => Address.Length == 0 ? Target : $"{Target} ({Address})";

    /// <summary>One-line summary, used as the mail subject and as the webhook's <c>text</c> field.</summary>
    public string Summary() => Event == "down"
        ? $"{Host}: {Where} is DOWN — {Status} after {Threshold} consecutive failures"
        : $"{Host}: {Where} recovered after {FormatDuration(OutageSeconds)}";

    public string Body()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary());
        sb.AppendLine();
        sb.Append("Target:    ").AppendLine(Target);
        sb.Append("Address:   ").AppendLine(Address.Length == 0 ? "(unresolved)" : Address);
        sb.Append("Event:     ").AppendLine(Event);
        sb.Append("Status:    ").AppendLine(Status);
        sb.Append("When:      ").AppendLine(When.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        if (Event == "recovered")
            sb.Append("Outage:    ").AppendLine(FormatDuration(OutageSeconds));
        sb.Append("Threshold: ").AppendLine(Threshold.ToString(CultureInfo.InvariantCulture));
        sb.Append("Monitor:   ").AppendLine(Host);
        return sb.ToString();
    }

    /// <summary>
    /// Hand-rolled rather than <c>JsonSerializer</c>: the payload is eight known scalars, and this
    /// keeps the shape visible in one place for anyone writing the receiving end.
    /// </summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        Field(sb, "target", Target).Append(',');
        Field(sb, "address", Address).Append(',');
        Field(sb, "event", Event).Append(',');
        Field(sb, "status", Status).Append(',');
        Field(sb, "when", When.ToString("o", CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"outage_seconds\":")
          .Append(OutageSeconds.ToString("0.#", CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"threshold\":").Append(Threshold.ToString(CultureInfo.InvariantCulture)).Append(',');
        Field(sb, "host", Host).Append(',');
        Field(sb, "text", Summary());
        sb.Append('}');
        return sb.ToString();

        static StringBuilder Field(StringBuilder sb, string name, string value) =>
            sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string FormatDuration(double seconds) => seconds switch
    {
        < 60 => $"{seconds:0.#}s",
        < 3600 => $"{seconds / 60:0.#}m",
        _ => $"{seconds / 3600:0.##}h",
    };
}
