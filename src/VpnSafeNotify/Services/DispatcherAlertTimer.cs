using System;
using System.Windows.Threading;

namespace VpnSafeNotify.Services;

/// <summary>A single reusable UI-thread timer, always armed as one-shot.</summary>
public sealed class DispatcherAlertTimer : IAlertTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _callback;
    private long _scheduledAt;
    private TimeSpan _delay;
    private bool _disposed;

    public DispatcherAlertTimer(Dispatcher? dispatcher = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? Dispatcher.CurrentDispatcher);
        _timer.Tick += OnTick;
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        _timer.Stop();
        _callback = callback;
        _delay = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _scheduledAt = TimeProvider.System.GetTimestamp();
        _timer.Interval = _delay;
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _callback = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed || _callback is null) return;
        // A queued tick from a replaced wait must not invoke the new callback
        // early. The controller independently rejects old callback generations.
        var elapsed = TimeProvider.System.GetElapsedTime(_scheduledAt);
        if (elapsed < _delay)
        {
            _timer.Interval = _delay - elapsed;
            _timer.Start();
            return;
        }
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _timer.Tick -= OnTick;
    }
}
