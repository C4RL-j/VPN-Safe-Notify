using System;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

/// <summary>
/// Owns the only alert schedule and effect. Call on the UI thread with settled
/// observations; network detection and its debounce remain the monitor's job.
/// </summary>
public sealed class AlertController : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    private readonly IOverlayPresenter _overlays;
    private readonly IAlertTimer _timer;
    private readonly Action<string, string> _notify;
    private readonly TimeProvider _clock;
    private readonly WarningPolicy _notificationPolicy;
    private readonly Action? _playWarningSound;
    private TimeSpan _redRepeatInterval = TimeSpan.FromSeconds(60);
    private int _redDurationSeconds = 6;
    private bool _warningSoundEnabled;
    private MonitoringSnapshot? _snapshot;
    private EffectSession? _effect;
    private long? _lastRedStart;
    private long _scheduleGeneration;
    private bool _monitoring;
    private bool _suspended;
    private bool _timerScheduled;
    private bool _notificationsEnabled = true;
    private bool _warnWhenVpnUnverified = true;
    private bool _disposed;

    public AlertController(IOverlayPresenter overlays, IAlertTimer timer,
        Action<string, string> notify, int notificationCooldownSeconds = 60,
        TimeProvider? clock = null, Action? playWarningSound = null)
    {
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _clock = clock ?? TimeProvider.System;
        _playWarningSound = playWarningSound;
        _notificationPolicy = new WarningPolicy(notificationCooldownSeconds, _clock);
    }

    /// <summary>Applies appearance and policy without restarting an existing warning episode.</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed) return;
        AppSettings normalized = SettingsService.Normalize(settings);
        bool durationChanged = _redDurationSeconds != normalized.RedAlertDurationSeconds;
        bool repeatChanged = _redRepeatInterval.TotalSeconds != normalized.RepeatIntervalSeconds;
        _redDurationSeconds = normalized.RedAlertDurationSeconds;
        _redRepeatInterval = TimeSpan.FromSeconds(normalized.RepeatIntervalSeconds);
        _warningSoundEnabled = normalized.WarningSoundEnabled;
        _notificationsEnabled = normalized.NotificationsEnabled;
        _overlays.Configure(normalized);
        SetWarnWhenVpnUnverified(normalized.WarnWhenVpnUnverified);

        if (durationChanged && _effect?.Effect == EdgeEffect.Red)
        {
            CancelSchedule();
            RenderFrame();
        }
        else if (repeatChanged && _effect is null)
        {
            CancelSchedule();
            Reconcile();
        }
        NotifyIfNeeded();
    }

    public void SetMonitoring(bool enabled)
    {
        if (_disposed) return;
        if (_monitoring == enabled)
        {
            if (!enabled) CancelEffect();
            return;
        }

        _monitoring = enabled;
        _suspended = false;
        _snapshot = null;
        _lastRedStart = null;
        CancelEffect();
    }

    public void Evaluate(MonitoringSnapshot snapshot, bool notificationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed) return;
        _notificationsEnabled = notificationsEnabled;
        if (!_monitoring) return;

        var previous = _snapshot;
        bool stateChanged = previous is null ||
            previous.Internet.State != snapshot.Internet.State || previous.Vpn.State != snapshot.Vpn.State;
        bool wasUnsafe = IsUnsafe(previous);
        bool isUnsafe = IsUnsafe(snapshot);
        bool becameConnected = previous is not null &&
            (previous.Vpn.State is VpnState.Disconnected or VpnState.Unverified) &&
            snapshot.Vpn.State == VpnState.Connected;

        _snapshot = snapshot;
        _suspended = false;
        // Disconnected and an opted-in Unverified state belong to the same
        // warning episode. Keep its current pulse and original repeat deadline.
        // A real observation still preempts an explicit preview when it changes.
        if (stateChanged && (!(wasUnsafe && isUnsafe) || _effect?.IsPreview == true)) CancelEffect();
        if (!isUnsafe || !wasUnsafe) _lastRedStart = null;

        NotifyIfNeeded();
        if (becameConnected)
            StartEffect(EdgeEffect.Green);
        else
            Reconcile();
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        if (_disposed) return;
        _notificationsEnabled = enabled;
        NotifyIfNeeded();
    }

    /// <summary>Controls automatic edge warnings for uncertain VPN observations without changing detection.</summary>
    public void SetWarnWhenVpnUnverified(bool enabled)
    {
        if (_disposed || _warnWhenVpnUnverified == enabled) return;
        bool wasUnsafe = IsUnsafe(_snapshot);
        _warnWhenVpnUnverified = enabled;
        bool isUnsafe = IsUnsafe(_snapshot);
        if (wasUnsafe == isUnsafe) return;

        _lastRedStart = null;
        // Opting in takes effect immediately on a settled unsafe observation,
        // even if a preview currently owns the overlay. Opting out only cancels
        // automatic warnings, so a deliberately requested preview can finish.
        if ((_monitoring && !_suspended && isUnsafe) || _effect?.IsPreview != true) CancelEffect();
        Reconcile();
    }

    /// <summary>Hide immediately until the next debounced observation, preserving VPN history and cooldown.</summary>
    public void SuspendForNetworkChange()
    {
        if (_disposed) return;
        _suspended = true;
        CancelEffect();
    }

    /// <summary>Replaces the visible effect without changing detection or real warning deadlines.</summary>
    public void Preview(EdgeEffect effect)
    {
        if (_disposed) return;
        if (effect is not EdgeEffect.Red and not EdgeEffect.Green)
            throw new ArgumentOutOfRangeException(nameof(effect));
        CancelEffect();
        StartEffect(effect, isPreview: true);
    }

    private bool IsUnsafe(MonitoringSnapshot? snapshot) =>
        snapshot?.Internet.State == InternetState.Online &&
        (snapshot.Vpn.State == VpnState.Disconnected ||
            (_warnWhenVpnUnverified && snapshot.Vpn.State == VpnState.Unverified));

    private void Reconcile()
    {
        if (_disposed || _effect is not null) return;
        if (!_monitoring || _suspended || !IsUnsafe(_snapshot))
        {
            CancelSchedule();
            return;
        }
        if (_timerScheduled) return;

        var elapsed = _lastRedStart is long last ? _clock.GetElapsedTime(last) : _redRepeatInterval;
        if (elapsed >= _redRepeatInterval)
        {
            _lastRedStart = _clock.GetTimestamp();
            NotifyIfNeeded();
            if (_warningSoundEnabled && _playWarningSound is not null)
            {
                try { _playWarningSound(); }
                catch (Exception) { /* Audio availability must not affect visual warnings. */ }
            }
            StartEffect(EdgeEffect.Red);
        }
        else
        {
            Schedule(_redRepeatInterval - elapsed, Reconcile);
        }
    }

    private void StartEffect(EdgeEffect effect, bool isPreview = false)
    {
        _effect = new EffectSession(effect, _clock.GetTimestamp(), isPreview);
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_disposed || _effect is not EffectSession effect) return;
        var elapsed = _clock.GetElapsedTime(effect.StartedAt);
        var duration = EdgeEffectEnvelope.Duration(effect.Effect, _redDurationSeconds);
        if (elapsed >= duration)
        {
            _effect = null;
            _overlays.Hide();
            Reconcile();
            return;
        }

        _overlays.Show(effect.Effect, EdgeEffectEnvelope.Intensity(effect.Effect, elapsed, _redDurationSeconds));
        var remaining = duration - elapsed;
        Schedule(remaining < FrameInterval ? remaining : FrameInterval, RenderFrame);
    }

    private void NotifyIfNeeded()
    {
        if (!_monitoring || _suspended || _snapshot is null ||
            !_notificationPolicy.ShouldWarn(_snapshot, _notificationsEnabled)) return;

        bool unverified = _snapshot.Vpn.State == VpnState.Unverified;
        try
        {
            _notify(unverified ? "VPN tunnel is unverified" : "VPN disconnected",
                unverified
                    ? "Internet access is available, but an active local VPN tunnel could not be confirmed. Check your VPN app; proxy-only modes may remain unverified."
                    : "Internet access is available, but no active local VPN tunnel was detected. Check that your VPN app is connected.");
        }
        catch (Exception)
        {
            // Windows may suppress or reject a desktop notification. Keep its
            // cooldown and continue the independent visual warning schedule.
        }
    }

    private void Schedule(TimeSpan delay, Action callback)
    {
        long generation = ++_scheduleGeneration;
        _timerScheduled = true;
        _timer.Schedule(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), () =>
        {
            if (_disposed || generation != _scheduleGeneration) return;
            _timerScheduled = false;
            callback();
        });
    }

    private void CancelSchedule()
    {
        ++_scheduleGeneration;
        _timerScheduled = false;
        _timer.Cancel();
    }

    private void CancelEffect()
    {
        CancelSchedule();
        _effect = null;
        _overlays.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelEffect();
        _timer.Dispose();
        _overlays.Dispose();
    }

    private sealed record EffectSession(EdgeEffect Effect, long StartedAt, bool IsPreview);
}

/// <summary>Continuous opacity envelopes, with no abrupt on/off or rapid flashing.</summary>
public static class EdgeEffectEnvelope
{
    public static TimeSpan Duration(EdgeEffect effect, int redDurationSeconds = 6) => effect == EdgeEffect.Red
        ? TimeSpan.FromSeconds(Math.Clamp(redDurationSeconds, 5, 7)) : TimeSpan.FromSeconds(1);

    public static double Intensity(EdgeEffect effect, TimeSpan elapsed, int redDurationSeconds = 6)
    {
        double seconds = elapsed.TotalSeconds;
        double duration = Duration(effect, redDurationSeconds).TotalSeconds;
        if (seconds <= 0 || seconds >= duration) return 0;
        if (effect == EdgeEffect.Green)
            return SmoothStep(seconds / .22) * SmoothStep((duration - seconds) / .4);

        // One gentle pulse per 2.4 seconds, above a steady soft base. The
        // independent eased envelope always starts and ends at zero opacity.
        double pulse = .89 - .11 * Math.Cos(2 * Math.PI * seconds / 2.4);
        return SmoothStep(seconds / .8) * SmoothStep((duration - seconds) / 1.0) * pulse;
    }

    private static double SmoothStep(double value)
    {
        double t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
