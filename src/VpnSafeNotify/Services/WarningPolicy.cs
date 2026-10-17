using System;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

/// <summary>Accepts settled observations and retains one cooldown across network changes.</summary>
public sealed class WarningPolicy
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cooldown;
    private long? _lastWarningTimestamp;

    public WarningPolicy(int cooldownSeconds, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldown = TimeSpan.FromSeconds(Math.Clamp(cooldownSeconds, 30, 3600));
    }

    public bool ShouldWarn(MonitoringSnapshot snapshot, bool notificationsEnabled)
    {
        if (!notificationsEnabled || !snapshot.NeedsWarning)
        {
            Reset();
            return false;
        }

        var now = _timeProvider.GetTimestamp();
        if (_lastWarningTimestamp is long lastWarning &&
            _timeProvider.GetElapsedTime(lastWarning, now) < _cooldown)
            return false;

        _lastWarningTimestamp = now;
        return true;
    }

    // The coordinator owns the two-second debounce. Pausing never bypasses cooldown.
    public void Reset() { }
}
