using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace PingBoard.Core;

/// <summary>
/// Watches the two conditions under which a probe failure says nothing about the target:
/// the machine was asleep, or the local network was down.
/// <para>
/// This is the single most important guard in the application. Without it, closing a laptop lid
/// for an hour manufactures thousands of failures across every target, destroys the rolling loss
/// figures, and produces an alert storm on wake. A monitoring tool that cries wolf after every
/// sleep gets ignored, and then it may as well not exist.
/// </para>
/// <para>
/// Both signals feed one derived state: probing is suspended when <em>either</em> holds, and
/// resumes only when both have cleared and the network has had time to settle.
/// </para>
/// </summary>
public sealed class SystemWatcher : IDisposable
{
    private readonly ProbeScheduler _scheduler;
    private readonly Lock _gate = new();

    private Settings _settings;
    private Timer? _settleTimer;
    private bool _powerSuspended;
    private bool _networkDown;
    private bool _started;
    private bool _disposed;

    public SystemWatcher(ProbeScheduler scheduler, Settings settings)
    {
        _scheduler = scheduler;
        _settings = settings;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            // Seed from current reality — the app may well have been launched with the WiFi off.
            _networkDown = !NetworkInterface.GetIsNetworkAvailable();
        }

        Reevaluate(immediate: true);
    }

    public void ApplySettings(Settings settings) => _settings = settings;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                lock (_gate) _powerSuspended = true;
                // Suspend immediately: results still in flight as the machine goes down would
                // otherwise land as failures.
                Reevaluate(immediate: true);
                break;

            case PowerModes.Resume:
                lock (_gate) _powerSuspended = false;
                // Deliberately not immediate. On wake the NIC is typically still negotiating and
                // DNS is unreachable for a few seconds; probing straight away produces a burst of
                // failures that are purely an artefact of our own timing.
                Reevaluate(immediate: false);
                break;

            case PowerModes.StatusChange:
            default:
                break;  // AC/battery transitions are irrelevant here.
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        lock (_gate) _networkDown = !e.IsAvailable;

        // Losing the local NIC suspends at once; regaining it waits for the stack to settle for
        // the same reason as a wake.
        Reevaluate(immediate: !e.IsAvailable);
    }

    private void Reevaluate(bool immediate)
    {
        bool shouldSuspend;
        string reason;

        lock (_gate)
        {
            if (_disposed) return;

            shouldSuspend = _powerSuspended || _networkDown;
            reason = _powerSuspended ? "machine asleep"
                : _networkDown ? "no local network"
                : "";

            _settleTimer?.Dispose();
            _settleTimer = null;

            if (!shouldSuspend && !immediate)
            {
                var settle = Math.Max(0, _settings.ResumeSettleMs);
                if (settle > 0)
                {
                    // One-shot timer; re-check state when it fires rather than trusting the state
                    // captured now, since another suspend may have arrived in the meantime.
                    _settleTimer = new Timer(_ => Reevaluate(immediate: true), null, settle, Timeout.Infinite);
                    return;
                }
            }
        }

        _scheduler.SetSuspended(shouldSuspend, reason);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_started)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            }

            _settleTimer?.Dispose();
            _settleTimer = null;
        }
    }
}
