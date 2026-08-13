using System.Net.Mail;
using System.Text;
using System.Threading.Channels;

namespace PingBoard.Core;

/// <summary>Outcome of the most recent delivery attempt, for the settings dialog to display.</summary>
/// <param name="Ok">False when the last attempt failed. Null <see cref="Error"/> means none attempted yet.</param>
public readonly record struct AlertHealth(bool Ok, string? Error, DateTimeOffset? LastAttempt, long Sent, long Dropped);

/// <summary>
/// Delivers transition alerts to the configured sinks, off the probe path.
/// <para>
/// Everything here is built around one rule: <b>a slow or dead alert endpoint must never affect
/// monitoring.</b> An SMTP server that stops answering blocks for the full TCP timeout, and a
/// webhook pointed at a host that is itself down blocks for longer. Sending inline from the
/// transition callback would put that latency on the scheduler's thread — so an outage on the
/// alerting path would degrade the very thing raising the alerts. Transitions are therefore
/// queued and drained by a single background worker.
/// </para>
/// <para>
/// The queue is bounded and drops the <em>oldest</em> entry when full. A hundred queued alerts
/// during a total network outage are not a hundred things you need to read; the newest state is
/// what matters, and unbounded queueing here would reintroduce the growing-list leak that the
/// ring buffers exist to avoid.
/// </para>
/// </summary>
public sealed class AlertDispatcher : IAsyncDisposable
{
    /// <summary>Deep enough to absorb a board-wide outage, shallow enough to stay bounded.</summary>
    private const int QueueCapacity = 256;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Channel<AlertPayload> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _gate = new();

    /// <summary>Last alert time per target, for the flap suppression window.</summary>
    private readonly Dictionary<string, long> _lastSentTick = new(StringComparer.OrdinalIgnoreCase);

    private AlertSettings _settings;
    private volatile string? _lastError;
    private DateTimeOffset? _lastAttempt;
    private long _sent;
    private long _dropped;

    public AlertDispatcher(AlertSettings settings)
    {
        _settings = settings;

        _queue = Channel.CreateBounded<AlertPayload>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    public void ApplySettings(AlertSettings settings) => _settings = settings;

    public AlertHealth Health()
    {
        lock (_gate)
            return new AlertHealth(_lastError is null, _lastError, _lastAttempt, _sent, _dropped);
    }

    /// <summary>
    /// Queues a transition for delivery. Returns immediately and never throws — the caller is the
    /// probe scheduler, and nothing on this path may interrupt monitoring.
    /// </summary>
    public void Enqueue(in StateTransition transition, string address)
    {
        var settings = _settings;
        if (!settings.AnyEnabled) return;
        if (transition.Up && !settings.NotifyOnRecovery) return;

        if (IsWithinCooldown(transition, settings)) return;

        if (!_queue.Writer.TryWrite(AlertPayload.From(transition, address)))
        {
            lock (_gate) _dropped++;
        }
    }

    /// <summary>
    /// True when this target alerted too recently. A recovery is never suppressed: it closes an
    /// alert the user has already been shown, and leaving that dangling is worse than one extra
    /// message.
    /// </summary>
    private bool IsWithinCooldown(in StateTransition transition, AlertSettings settings)
    {
        if (settings.MinIntervalSeconds <= 0 || transition.Up) return false;

        var now = Environment.TickCount64;
        var window = (long)settings.MinIntervalSeconds * 1000;

        lock (_gate)
        {
            if (_lastSentTick.TryGetValue(transition.TargetName, out var last) && now - last < window)
            {
                _dropped++;
                return true;
            }

            _lastSentTick[transition.TargetName] = now;
            return false;
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await DeliverAsync(payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DeliverAsync(AlertPayload payload, CancellationToken ct)
    {
        var settings = _settings;
        string? error = null;

        if (settings.WebhookEnabled)
            error = await PostWebhookAsync(payload, settings, ct).ConfigureAwait(false);

        if (settings.EmailEnabled)
            error = await SendMailAsync(payload, settings, ct).ConfigureAwait(false) ?? error;

        lock (_gate)
        {
            _lastAttempt = DateTimeOffset.Now;
            _lastError = error;
            if (error is null) _sent++;
        }
    }

    /// <returns>Null on success, otherwise a short message for the settings dialog.</returns>
    private static async Task<string?> PostWebhookAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.TimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl)
            {
                Content = new StringContent(payload.ToJson(), Encoding.UTF8, "application/json"),
            };

            var authorization = ProtectedValue.Unprotect(settings.WebhookAuthorization);
            if (authorization.Length > 0)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? null
                : $"webhook returned {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;   // shutting down; not a delivery failure worth reporting
        }
        catch (OperationCanceledException)
        {
            return $"webhook timed out after {settings.TimeoutMs} ms";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            return "webhook failed: " + ex.Message;
        }
    }

    /// <returns>Null on success, otherwise a short message for the settings dialog.</returns>
    private static async Task<string?> SendMailAsync(AlertPayload payload, AlertSettings settings, CancellationToken ct)
    {
        try
        {
            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.SmtpUseStartTls,
                Timeout = settings.TimeoutMs,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            var password = ProtectedValue.Unprotect(settings.SmtpPassword);
            if (settings.SmtpUser.Length > 0)
            {
                client.UseDefaultCredentials = false;
                client.Credentials = new System.Net.NetworkCredential(settings.SmtpUser, password);
            }

            using var message = new MailMessage(settings.EmailFrom, settings.EmailTo)
            {
                Subject = payload.Summary(),
                Body = payload.Body(),
                IsBodyHtml = false,
            };

            await client.SendMailAsync(message, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException
                                     or System.Net.Sockets.SocketException or FormatException
                                     or ObjectDisposedException or OperationCanceledException)
        {
            return "email failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Sends one alert immediately and reports the result, for the settings dialog's Test button.
    /// Bypasses the queue and the cooldown — the user is standing there waiting for an answer.
    /// </summary>
    public async Task<string?> SendTestAsync(AlertSettings settings, CancellationToken ct)
    {
        var payload = new AlertPayload(
            Target: "test",
            Address: "0.0.0.0",
            Event: "down",
            Status: "TIMEOUT",
            When: DateTimeOffset.Now,
            OutageSeconds: 0,
            Threshold: settings.MinIntervalSeconds,
            Host: Environment.MachineName);

        string? error = null;
        if (settings.WebhookEnabled) error = await PostWebhookAsync(payload, settings, ct).ConfigureAwait(false);
        if (settings.EmailEnabled) error = await SendMailAsync(payload, settings, ct).ConfigureAwait(false) ?? error;
        if (!settings.AnyEnabled) error = "no alert sink is enabled";

        return error;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // Give whatever is already queued a brief chance to go out. An alert that says the link
        // just dropped is worth a moment on shutdown; it is not worth hanging the app.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* fall through to cancellation */ }
        catch (OperationCanceledException) { /* expected */ }

        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
