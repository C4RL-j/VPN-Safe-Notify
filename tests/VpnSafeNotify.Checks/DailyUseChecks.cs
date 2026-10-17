using System;
using System.Threading.Tasks;
using VpnSafeNotify.Infrastructure;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class DailyUseChecks
{
    public static int AssertionCount { get; private set; }

    public static Task RunAsync()
    {
        AssertionCount = 0;
        var clock = new FixedClock();
        var timer = new ManualTimer();
        int resumes = 0;
        using var pause = new TimedPauseController(timer, () => resumes++, clock);
        pause.PauseFor(TimeSpan.FromMinutes(15));
        Expect(timer.Delay == TimeSpan.FromMinutes(15) && pause.ResumeAt == clock.GetUtcNow().AddMinutes(15),
            "A timed pause has one fifteen-minute deadline and an explicit display time.");
        Action old = timer.Callback!;
        pause.Cancel();
        old();
        Expect(resumes == 0 && pause.ResumeAt is null && timer.Callback is null,
            "Manual Resume/Pause cancels a queued timed resume.");
        pause.PauseFor(TimeSpan.FromMinutes(15));
        old = timer.Callback!;
        pause.PauseFor(TimeSpan.FromMinutes(15));
        old();
        Expect(resumes == 0, "Replacing a pause invalidates its previous callback.");
        Action due = timer.Callback!;
        due();
        due();
        Expect(resumes == 1 && pause.ResumeAt is null, "An expired pause resumes exactly once and clears its deadline.");
        pause.PauseFor(TimeSpan.FromMinutes(15));
        old = timer.Callback!;
        pause.Dispose();
        old();
        pause.PauseFor(TimeSpan.FromMinutes(15));
        Expect(resumes == 1 && timer.Disposed && pause.ResumeAt is null,
            "Shutdown disposes the deadline and stale work cannot resume monitoring.");

        foreach (InternetState internet in Enum.GetValues<InternetState>())
        foreach (VpnState vpn in Enum.GetValues<VpnState>())
        {
            var snapshot = new MonitoringSnapshot(new(internet, "test internet"),
                new(vpn, "test VPN", Array.Empty<string>()), DateTimeOffset.UnixEpoch);
            var shown = TrayPresentation.Create(true, snapshot);
            var expected = internet == InternetState.Offline ? TrayTone.Gray : vpn switch
            {
                VpnState.Connected => TrayTone.Green,
                VpnState.Unverified => TrayTone.Amber,
                _ => internet == InternetState.Online ? TrayTone.Red : TrayTone.Gray
            };
            Expect(shown.Tone == expected, $"Tray tone accurately represents {internet}/{vpn}.");
            Expect(shown.Tooltip.Length <= 63, "Tray status fits Windows' concise tooltip limit.");
            var paused = TrayPresentation.Create(false, snapshot);
            Expect(paused.Tone == TrayTone.Gray && paused.Tooltip.Contains("Paused", StringComparison.Ordinal),
                "Paused overrides stale connection evidence with gray, obvious status.");
        }
        Expect(TrayPresentation.Create(true, null).Tooltip.Contains("Checking", StringComparison.Ordinal),
            "No snapshot is labeled Checking instead of making a protection claim.");
        Expect(TrayPresentation.Create(false, null, clock.GetUtcNow()).Tooltip.Contains("Paused until", StringComparison.Ordinal),
            "A timed pause is distinguished from an indefinite pause.");
        return Task.CompletedTask;
    }

    private static void Expect(bool condition, string detail)
    {
        AssertionCount++;
        if (!condition) throw new InvalidOperationException(detail);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ManualTimer : IAlertTimer
    {
        public Action? Callback { get; private set; }
        public TimeSpan Delay { get; private set; }
        public bool Disposed { get; private set; }
        public void Schedule(TimeSpan delay, Action callback) { Delay = delay; Callback = callback; }
        public void Cancel() => Callback = null;
        public void Dispose() { Cancel(); Disposed = true; }
    }
}
