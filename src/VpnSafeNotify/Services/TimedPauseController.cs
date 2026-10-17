using System;

namespace VpnSafeNotify.Services;

/// <summary>One cancellable pause deadline, separate from the single visual-alert schedule.</summary>
public sealed class TimedPauseController : IDisposable
{
    private readonly IAlertTimer _timer;
    private readonly TimeProvider _clock;
    private readonly Action _resume;
    private long _generation;
    private bool _disposed;

    public TimedPauseController(IAlertTimer timer, Action resume, TimeProvider? clock = null)
    {
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _resume = resume ?? throw new ArgumentNullException(nameof(resume));
        _clock = clock ?? TimeProvider.System;
    }

    public DateTimeOffset? ResumeAt { get; private set; }

    public void PauseFor(TimeSpan duration)
    {
        if (_disposed) return;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Cancel();
        ResumeAt = _clock.GetUtcNow() + duration;
        long generation = _generation;
        _timer.Schedule(duration, () =>
        {
            if (_disposed || generation != _generation) return;
            ++_generation;
            ResumeAt = null;
            _resume();
        });
    }

    public void Cancel()
    {
        if (_disposed) return;
        ++_generation;
        ResumeAt = null;
        _timer.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _disposed = true;
        _timer.Dispose();
    }
}
