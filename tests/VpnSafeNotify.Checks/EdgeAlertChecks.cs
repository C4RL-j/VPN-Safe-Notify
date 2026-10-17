using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class EdgeAlertChecks
{
    public static int AssertionCount { get; private set; }

    public static Task RunAsync()
    {
        AssertionCount = 0;
        InitialStateMatrix();
        UnverifiedSettingTransitions();
        EligibleStateCadence();
        ConnectedTransitions();
        RepeatingWarning();
        SuppressionAndNotifications();
        PauseAndResume();
        NetworkChangeSuspension();
        PreviewOwnership();
        StaleCallbacksAndDisposal();
        ConfigurableTiming();
        WarningSounds();
        Envelopes();
        return Task.CompletedTask;
    }

    private static void InitialStateMatrix()
    {
        foreach (bool warnWhenUnverified in new[] { true, false })
        foreach (InternetState internet in Enum.GetValues<InternetState>())
        foreach (VpnState vpn in Enum.GetValues<VpnState>())
        {
            using var test = new Harness(warnWhenVpnUnverified: warnWhenUnverified);
            test.Start(internet, vpn);
            bool unsafeState = internet == InternetState.Online &&
                (vpn == VpnState.Disconnected || (warnWhenUnverified && vpn == VpnState.Unverified));
            Expect(test.Overlay.VisibleEffect == (unsafeState ? EdgeEffect.Red : null),
                $"Initial {internet}/{vpn} follows the unverified-warning setting ({warnWhenUnverified}).");
            Expect(test.Overlay.GreenStarts == 0, "Initial connected never produces a reconnect animation.");
            Expect(test.Notifications.Count == (internet == InternetState.Online && vpn != VpnState.Connected ? 1 : 0),
                "Only online/disconnected or online/unverified can issue a notification.");
            test.Advance(6);
            Expect(test.Overlay.VisibleEffect is null, "The initial effect finishes within six seconds.");
            Expect(test.Timer.Pending == (unsafeState ? 1 : 0), "Only a continuing unsafe state needs a future timer.");
        }
    }

    private static void UnverifiedSettingTransitions()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Unverified);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red, "Unverified warnings are enabled by default.");
        test.Advance(.75);
        var canceledFrame = test.Timer.Callback!;
        test.Controller.SetWarnWhenVpnUnverified(false);
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0,
            "Disabling unverified warnings immediately cancels its active red effect and timer.");
        int shows = test.Overlay.ShowCalls;
        canceledFrame();
        Expect(test.Overlay.ShowCalls == shows && test.Timer.Pending == 0,
            "A canceled unverified frame cannot revive the effect after the setting is disabled.");
        test.Advance(120);
        Expect(test.Overlay.RedStarts == 1, "An opted-out unverified state has no delayed automatic red pulses.");
        test.Controller.SetWarnWhenVpnUnverified(true);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red && test.Overlay.RedStarts == 2,
            "Enabling unverified warnings evaluates the current Online/Unverified observation immediately.");
        int schedules = test.Timer.ScheduleCalls;
        test.Controller.SetWarnWhenVpnUnverified(true);
        Expect(test.Timer.ScheduleCalls == schedules && test.Overlay.RedStarts == 2,
            "Repeating the same setting value cannot restart the active effect.");
        test.Advance(6);
        var canceledRepeat = test.Timer.Callback!;
        test.Controller.SetWarnWhenVpnUnverified(false);
        canceledRepeat();
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0 && test.Overlay.RedStarts == 2,
            "Disabling the setting also cancels a queued unverified repeat.");
        test.Controller.SetWarnWhenVpnUnverified(true);
        test.Advance(.4);
        test.Evaluate(InternetState.Online, VpnState.Connected);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Green && test.Overlay.GreenStarts == 1,
            "Connected cancels an unverified red pulse and preserves the real reconnection effect.");
        test.Controller.SetWarnWhenVpnUnverified(false);
        test.Controller.SetWarnWhenVpnUnverified(true);
        test.Advance(.6);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Green && test.Overlay.GreenStarts == 1,
            "Changing the unverified setting cannot cancel or restart a genuine connection effect.");
        test.Advance(.4);
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0,
            "The preserved connection effect still ends at one second.");

        using var disconnected = new Harness();
        disconnected.Start(InternetState.Online, VpnState.Disconnected);
        disconnected.Advance(2);
        schedules = disconnected.Timer.ScheduleCalls;
        disconnected.Controller.SetWarnWhenVpnUnverified(false);
        Expect(disconnected.Overlay.VisibleEffect == EdgeEffect.Red && disconnected.Overlay.RedStarts == 1 &&
            disconnected.Timer.ScheduleCalls == schedules,
            "Disabling unverified warnings leaves an ongoing Disconnected warning and its frame schedule intact.");
        disconnected.Advance(4);
        schedules = disconnected.Timer.ScheduleCalls;
        disconnected.Controller.SetWarnWhenVpnUnverified(true);
        Expect(disconnected.Timer.ScheduleCalls == schedules,
            "Changing the unverified setting leaves a Disconnected repeat deadline intact.");
        disconnected.Advance(54);
        Expect(disconnected.Overlay.RedStarts == 2, "Disconnected continues to repeat at sixty seconds under either setting.");

        foreach (InternetState internet in new[] { InternetState.Offline, InternetState.Unknown })
        {
            using var suppressed = new Harness(warnWhenVpnUnverified: false);
            suppressed.Start(internet, VpnState.Unverified);
            suppressed.Controller.SetWarnWhenVpnUnverified(true);
            Expect(suppressed.Overlay.VisibleEffect is null && suppressed.Timer.Pending == 0,
                "Enabling unverified warnings cannot bypass Offline/Unknown internet suppression.");
        }

        using var paused = new Harness();
        paused.Start(InternetState.Online, VpnState.Unverified);
        paused.Controller.SetMonitoring(false);
        paused.Controller.SetWarnWhenVpnUnverified(false);
        paused.Controller.SetWarnWhenVpnUnverified(true);
        paused.Advance(120);
        Expect(paused.Overlay.VisibleEffect is null && paused.Timer.Pending == 0 && paused.Overlay.RedStarts == 1,
            "Changing this setting while paused cannot produce automatic red.");
        paused.Controller.SetMonitoring(true);
        paused.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(paused.Overlay.VisibleEffect == EdgeEffect.Red && paused.Overlay.RedStarts == 2,
            "Resuming uses the saved current unverified-warning policy after detection settles.");

        using var unsettled = new Harness(warnWhenVpnUnverified: false);
        unsettled.Start(InternetState.Online, VpnState.Unverified);
        unsettled.Controller.SuspendForNetworkChange();
        unsettled.Controller.SetWarnWhenVpnUnverified(true);
        Expect(unsettled.Overlay.VisibleEffect is null && unsettled.Timer.Pending == 0,
            "A setting change does not bypass a pending detection debounce.");
        unsettled.Advance(2);
        unsettled.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(unsettled.Overlay.VisibleEffect == EdgeEffect.Red,
            "The enabled unverified policy starts red once the debounced observation settles.");

        using var accurate = new Harness();
        accurate.Controller.SetMonitoring(true);
        var observation = new MonitoringSnapshot(new ConnectivityResult(InternetState.Online, "Online test"),
            new VpnDetectionResult(VpnState.Unverified, "Proxy-only tunnel cannot be verified", Array.Empty<string>()), DateTimeOffset.UnixEpoch);
        accurate.Controller.Evaluate(observation, true);
        accurate.Controller.SetWarnWhenVpnUnverified(false);
        accurate.Controller.SetWarnWhenVpnUnverified(true);
        Expect(observation.Vpn.State == VpnState.Unverified && observation.Vpn.Detail == "Proxy-only tunnel cannot be verified",
            "Warning policy never relabels an Unverified observation or rewrites its detection explanation.");
    }

    private static void EligibleStateCadence()
    {
        foreach (VpnState before in new[] { VpnState.Unverified, VpnState.Disconnected })
        {
            VpnState after = before == VpnState.Unverified ? VpnState.Disconnected : VpnState.Unverified;
            using var test = new Harness();
            test.Start(InternetState.Online, before);
            test.Advance(2);
            int schedules = test.Timer.ScheduleCalls;
            double intensity = test.Overlay.Intensity;
            test.Evaluate(InternetState.Online, after);
            Expect(test.Overlay.VisibleEffect == EdgeEffect.Red && test.Overlay.RedStarts == 1 &&
                test.Timer.ScheduleCalls == schedules && test.Overlay.Intensity == intensity,
                "Switching eligible Unverified/Disconnected observations preserves the running pulse without replacing its frame.");
            test.Advance(4);
            Expect(test.Overlay.VisibleEffect is null, "An eligible state switch does not extend the six-second pulse.");
            test.Advance(7);
            schedules = test.Timer.ScheduleCalls;
            test.Evaluate(InternetState.Online, before);
            Expect(test.Timer.ScheduleCalls == schedules && test.Overlay.RedStarts == 1,
                "An eligible switch during the repeat wait preserves the single original schedule.");
            test.Advance(46.999);
            Expect(test.Overlay.VisibleEffect is null, "Eligible state changes do not produce an early repeat.");
            test.Advance(.001);
            Expect(test.Overlay.RedStarts == 2 && test.Timer.MaxPending == 1,
                "Eligible states share one sixty-second start-to-start warning cadence.");

            using var debounced = new Harness();
            debounced.Start(InternetState.Online, before);
            debounced.Advance(3);
            debounced.Controller.SuspendForNetworkChange();
            debounced.Advance(2);
            debounced.Evaluate(InternetState.Online, after);
            Expect(debounced.Overlay.VisibleEffect is null && debounced.Overlay.RedStarts == 1 && debounced.Timer.Pending == 1,
                "An eligible switch after detection debounce preserves the warning episode without restarting its interrupted pulse.");
            debounced.Advance(55);
            Expect(debounced.Overlay.RedStarts == 2 && debounced.Timer.MaxPending == 1,
                "Network debounce plus an eligible state switch retains the original repeat deadline.");
        }

        using var optedOut = new Harness(warnWhenVpnUnverified: false);
        optedOut.Start(InternetState.Online, VpnState.Disconnected);
        optedOut.Advance(2);
        optedOut.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(optedOut.Overlay.VisibleEffect is null && optedOut.Timer.Pending == 0,
            "Opted-out Unverified cancels a Disconnected pulse when the actual classification changes.");
        optedOut.Advance(10);
        optedOut.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(optedOut.Overlay.VisibleEffect == EdgeEffect.Red && optedOut.Overlay.RedStarts == 2,
            "Disconnected after opted-out Unverified begins a fresh eligible warning episode.");
    }

    private static void ConnectedTransitions()
    {
        foreach (VpnState before in new[] { VpnState.Disconnected, VpnState.Unverified })
        foreach (InternetState internet in Enum.GetValues<InternetState>())
        {
            using var test = new Harness();
            test.Start(internet, before);
            test.Advance(.4);
            test.Evaluate(internet, VpnState.Connected);
            Expect(test.Overlay.VisibleEffect == EdgeEffect.Green, "A known disconnected/unverified VPN becoming connected shows green regardless of internet state.");
            test.Advance(.5);
            Expect(test.Overlay.VisibleEffect == EdgeEffect.Green && test.Overlay.Intensity > .8, "Reconnect green has a smooth visible middle.");
            test.Evaluate(internet, VpnState.Connected);
            test.Advance(.5);
            Expect(test.Overlay.VisibleEffect is null, "Duplicate connected observations do not restart the one-second effect.");
            Expect(test.Timer.Pending == 0, "Connected does not leave a repeat timer behind.");
            test.Advance(90);
            Expect(test.Overlay.GreenStarts == 1, "Connected does not repeat its success animation.");
        }

        using var dropped = new Harness();
        dropped.Start(InternetState.Online, VpnState.Connected);
        dropped.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(dropped.Overlay.VisibleEffect == EdgeEffect.Red, "A settled VPN drop starts red immediately; the detection coordinator already debounced it.");
        dropped.Evaluate(InternetState.Online, VpnState.Connected);
        Expect(dropped.Overlay.VisibleEffect == EdgeEffect.Green, "A reconnect preempts the red warning.");
        dropped.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(dropped.Overlay.VisibleEffect == EdgeEffect.Red && dropped.Overlay.RedStarts == 2,
            "A genuine new drop preempts green and starts a new warning episode without waiting for a stale deadline.");
    }

    private static void RepeatingWarning()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        test.Advance(3);
        int schedules = test.Timer.ScheduleCalls;
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Timer.ScheduleCalls == schedules, "An unchanged detector observation leaves the active frame schedule alone.");
        test.Advance(3);
        Expect(test.Overlay.VisibleEffect is null && test.Overlay.RedStarts == 1, "Red finishes at six seconds without detector polling.");
        test.Advance(10);
        schedules = test.Timer.ScheduleCalls;
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Timer.ScheduleCalls == schedules, "An unchanged detector observation also leaves the existing repeat wait alone.");
        test.Advance(43.999);
        Expect(test.Overlay.VisibleEffect is null, "The next pulse waits until sixty seconds from the previous start.");
        test.Advance(.001);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red && test.Overlay.RedStarts == 2, "The controller repeats at sixty seconds without a detector callback.");
        Expect(test.Notifications.Count == 2, "Normal repeat can issue a notification at its existing cooldown.");
        test.Advance(120);
        Expect(test.Overlay.RedStarts == 4 && test.Timer.MaxPending == 1, "Repeated effects use one replaceable timer and never stack.");
    }

    private static void SuppressionAndNotifications()
    {
        foreach (InternetState suppressed in new[] { InternetState.Unknown, InternetState.Offline })
        foreach (VpnState vpn in new[] { VpnState.Disconnected, VpnState.Unverified })
        {
            using var test = new Harness();
            test.Start(InternetState.Online, vpn);
            test.Advance(.7);
            test.Evaluate(suppressed, vpn);
            Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "Offline/Unknown immediately suppresses red and its future repeats.");
            test.Advance(120);
            Expect(test.Overlay.RedStarts == 1, "Suppressed internet states never produce delayed red warnings.");
            test.Evaluate(InternetState.Online, vpn);
            Expect(test.Overlay.RedStarts == 2, "Returning online without a VPN begins a fresh unsafe condition.");
        }

        using var uncertain = new Harness(warnWhenVpnUnverified: false);
        uncertain.Start(InternetState.Online, VpnState.Unverified);
        Expect(uncertain.Notifications.Count == 1 && uncertain.Notifications[0].Contains("unverified", StringComparison.OrdinalIgnoreCase),
            "Online/unverified explains detection uncertainty with an optional notification.");
        uncertain.Advance(5);
        uncertain.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(uncertain.Overlay.VisibleEffect is null && uncertain.Notifications.Count == 1, "Unverified produces no red/green and notification cooldown prevents duplicates.");
        uncertain.Controller.Preview(EdgeEffect.Green);
        uncertain.Advance(1);
        Expect(uncertain.Notifications.Count == 1 && uncertain.Overlay.VisibleEffect is null, "Preview green says nothing about actual VPN state or notifications.");

        using var disabled = new Harness();
        disabled.Start(InternetState.Online, VpnState.Disconnected, notificationsEnabled: false);
        Expect(disabled.Notifications.Count == 0 && disabled.Overlay.VisibleEffect == EdgeEffect.Red,
            "Disabling desktop notifications does not disable screen warnings.");
        disabled.Controller.SetNotificationsEnabled(true);
        Expect(disabled.Notifications.Count == 1, "Enabling notifications evaluates the current stable unsafe state.");
        disabled.Controller.SetNotificationsEnabled(true);
        Expect(disabled.Notifications.Count == 1, "Repeated settings updates respect notification cooldown.");

        using var failure = new Harness(throwNotification: true);
        failure.Start(InternetState.Online, VpnState.Disconnected);
        failure.Advance(1);
        Expect(failure.Overlay.VisibleEffect == EdgeEffect.Red && failure.Notifications.Count == 1,
            "Rejected Windows notifications do not interrupt visual warnings or bypass cooldown.");
    }

    private static void PauseAndResume()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        test.Advance(.5);
        test.Controller.SetMonitoring(false);
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "Pause immediately hides effects and cancels all schedules.");
        test.Advance(120);
        Expect(test.Overlay.RedStarts == 1, "Paused monitoring cannot produce pending warnings.");
        test.Controller.SetMonitoring(true);
        test.Evaluate(InternetState.Online, VpnState.Connected);
        Expect(test.Overlay.VisibleEffect is null && test.Overlay.GreenStarts == 0, "Resume resets the baseline, so an already connected VPN needs no animation.");
        test.Controller.SetMonitoring(false);
        test.Controller.SetMonitoring(true);
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red, "Resuming evaluates the current settled unsafe state immediately.");
        test.Controller.SetMonitoring(false);
        test.Controller.Preview(EdgeEffect.Red);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red, "A clearly requested preview is available while monitoring is paused.");
        test.Controller.SetMonitoring(false);
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "Pausing hides even a preview when monitoring was already paused.");
    }

    private static void NetworkChangeSuspension()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        test.Advance(3);
        test.Controller.SuspendForNetworkChange();
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "A pending network change immediately hides and suspends alerts.");
        test.Advance(2);
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Overlay.VisibleEffect is null && test.Notifications.Count == 1,
            "Settling back to the same unsafe state retains the real warning/notification deadlines.");
        test.Advance(54.999);
        Expect(test.Overlay.RedStarts == 1, "Network churn does not restart the same unsafe warning.");
        test.Advance(.001);
        Expect(test.Overlay.RedStarts == 2, "Network suspension preserves the original sixty-second deadline.");
        test.Controller.SuspendForNetworkChange();
        test.Advance(2);
        test.Evaluate(InternetState.Online, VpnState.Connected);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Green, "Network debounce retains VPN history for a real reconnect.");
        test.Controller.SuspendForNetworkChange();
        test.Advance(2);
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Red && test.Overlay.RedStarts == 3,
            "A genuine VPN drop after settling creates a new immediate warning episode.");
    }

    private static void PreviewOwnership()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        test.Advance(2);
        test.Controller.Preview(EdgeEffect.Green);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Green, "Preview replaces, rather than stacks on, the current warning.");
        test.Advance(.3);
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Overlay.VisibleEffect == EdgeEffect.Green, "Unchanged detection does not cancel an explicit preview.");
        test.Advance(.7);
        Expect(test.Overlay.VisibleEffect is null && test.Notifications.Count == 1,
            "Preview completes at its own duration without issuing or consuming a notification.");
        test.Advance(57);
        Expect(test.Overlay.RedStarts == 2 && test.Notifications.Count == 2,
            "Preview leaves the original real warning and notification deadlines intact.");

        test.Controller.Preview(EdgeEffect.Red);
        test.Advance(.3);
        test.Controller.Preview(EdgeEffect.Green);
        test.Advance(1);
        Expect(test.Overlay.VisibleEffect is null, "A repeated preview cancels the previous effect's duration.");
        test.Controller.Preview(EdgeEffect.Red);
        test.Controller.SetWarnWhenVpnUnverified(false);
        test.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "A real state change cancels a stale preview; Unverified cannot show green.");
        test.Controller.Preview(EdgeEffect.Red);
        test.Controller.SuspendForNetworkChange();
        Expect(test.Overlay.VisibleEffect is null, "A network change also cancels an explicit preview until the next settled state.");

        using var due = new Harness();
        due.Start(InternetState.Online, VpnState.Disconnected);
        due.Advance(59);
        due.Controller.Preview(EdgeEffect.Red);
        due.Advance(6);
        Expect(due.Overlay.VisibleEffect == EdgeEffect.Red && due.Overlay.RedStarts == 3,
            "If a real deadline passes during a preview, a real warning resumes when preview ownership ends.");
        Expect(due.Notifications.Count == 2 && due.Timer.MaxPending == 1, "Preview schedules never duplicate real timers or consume desktop cooldowns.");

        using var paused = new Harness();
        paused.Controller.Preview(EdgeEffect.Red);
        paused.Advance(6);
        Expect(paused.Overlay.VisibleEffect is null && paused.Timer.Pending == 0 && paused.Notifications.Count == 0,
            "A paused preview finishes cleanly without starting monitoring or leaving a repeat.");
        paused.Controller.Preview(EdgeEffect.Green);
        paused.Advance(1);
        Expect(paused.Overlay.VisibleEffect is null && paused.Timer.Pending == 0, "Paused green previews also have no follow-up timer.");

        using var settingsPreview = new Harness();
        settingsPreview.Start(InternetState.Online, VpnState.Unverified);
        settingsPreview.Advance(2);
        settingsPreview.Controller.Preview(EdgeEffect.Red);
        settingsPreview.Controller.SetWarnWhenVpnUnverified(false);
        Expect(settingsPreview.Overlay.VisibleEffect == EdgeEffect.Red,
            "Turning off automatic unverified warnings leaves a clearly requested red preview visible.");
        settingsPreview.Advance(6);
        Expect(settingsPreview.Overlay.VisibleEffect is null && settingsPreview.Timer.Pending == 0,
            "When that explicit preview ends, the disabled automatic warning does not return.");
        settingsPreview.Controller.Preview(EdgeEffect.Green);
        var staleGreenPreview = settingsPreview.Timer.Callback!;
        settingsPreview.Controller.SetWarnWhenVpnUnverified(true);
        Expect(settingsPreview.Overlay.VisibleEffect == EdgeEffect.Red,
            "Enabling automatic unverified warnings immediately preempts an active green preview on a settled Online/Unverified observation.");
        int shows = settingsPreview.Overlay.ShowCalls;
        staleGreenPreview();
        Expect(settingsPreview.Overlay.VisibleEffect == EdgeEffect.Red && settingsPreview.Overlay.ShowCalls == shows,
            "A canceled green preview frame cannot overwrite the immediately enabled automatic red warning.");
        settingsPreview.Advance(1);
        Expect(settingsPreview.Overlay.VisibleEffect == EdgeEffect.Red,
            "The newly enabled automatic warning follows its own six-second duration after preempting a preview.");
        settingsPreview.Advance(5);
        Expect(settingsPreview.Overlay.VisibleEffect is null && settingsPreview.Timer.Pending == 1,
            "The automatic warning finishes six seconds after enabling and retains one repeat timer.");
    }

    private static void StaleCallbacksAndDisposal()
    {
        var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        var redCallback = test.Timer.Callback!;
        test.Evaluate(InternetState.Online, VpnState.Connected);
        int shows = test.Overlay.ShowCalls;
        int schedules = test.Timer.ScheduleCalls;
        redCallback();
        Expect(test.Overlay.ShowCalls == shows && test.Timer.ScheduleCalls == schedules && test.Overlay.VisibleEffect == EdgeEffect.Green,
            "A queued callback from a canceled red generation cannot overwrite a newer green effect.");
        var greenCallback = test.Timer.Callback!;
        test.Controller.SetMonitoring(false);
        greenCallback();
        Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 0, "A queued frame cannot revive an effect after pause.");
        test.Controller.Preview(EdgeEffect.Red);
        var previewCallback = test.Timer.Callback!;
        test.Controller.Preview(EdgeEffect.Green);
        shows = test.Overlay.ShowCalls;
        previewCallback();
        Expect(test.Overlay.ShowCalls == shows && test.Overlay.VisibleEffect == EdgeEffect.Green,
            "An old preview callback cannot overwrite a newer preview.");
        var finalCallback = test.Timer.Callback!;
        test.Dispose();
        finalCallback();
        test.Controller.Preview(EdgeEffect.Red);
        test.Controller.SetMonitoring(true);
        test.Evaluate(InternetState.Online, VpnState.Disconnected);
        Expect(test.Timer.Disposed && test.Overlay.Disposed && test.Timer.Pending == 0 && test.Overlay.VisibleEffect is null,
            "Disposal cancels callbacks, hides the effect, and releases timer/presenter ownership.");

        using var waiting = new Harness();
        waiting.Start(InternetState.Online, VpnState.Disconnected);
        waiting.Advance(6);
        var repeatCallback = waiting.Timer.Callback!;
        waiting.Evaluate(InternetState.Unknown, VpnState.Disconnected);
        repeatCallback();
        Expect(waiting.Overlay.VisibleEffect is null && waiting.Overlay.RedStarts == 1 && waiting.Timer.Pending == 0,
            "A canceled repeat callback cannot revive red after internet becomes Unknown.");
    }

    private static void ConfigurableTiming()
    {
        foreach (int duration in new[] { 5, 7 })
        foreach (int interval in new[] { 15, 300 })
        {
            using var test = new Harness();
            test.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = duration, RepeatIntervalSeconds = interval });
            test.Start(InternetState.Online, VpnState.Disconnected);
            test.Advance(duration - .001);
            Expect(test.Overlay.VisibleEffect == EdgeEffect.Red, "Configured red remains active until its own duration boundary.");
            test.Advance(.001);
            Expect(test.Overlay.VisibleEffect is null && test.Timer.Pending == 1,
                "Configured red ends at its exact five/seven-second boundary and leaves one repeat.");
            test.Advance(interval - duration - .001);
            Expect(test.Overlay.RedStarts == 1, "Configured fifteen/three-hundred-second repeats never start early.");
            test.Advance(.001);
            Expect(test.Overlay.RedStarts == 2 && test.Timer.MaxPending == 1,
                "Configured repeats retain one start-to-start schedule.");
        }

        using var extended = new Harness();
        extended.Start(InternetState.Online, VpnState.Unverified);
        extended.Advance(4);
        extended.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = 7, RepeatIntervalSeconds = 15 });
        extended.Advance(2.999);
        Expect(extended.Overlay.VisibleEffect == EdgeEffect.Red && extended.Overlay.RedStarts == 1,
            "Increasing duration applies to elapsed time without restarting the warning.");
        extended.Advance(.001);
        Expect(extended.Overlay.VisibleEffect is null, "An extended warning still ends seven seconds from its original start.");
        extended.Advance(8);
        Expect(extended.Overlay.RedStarts == 2, "Changing duration and repeat together preserves the original repeat origin.");

        using var shortened = new Harness();
        shortened.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = 7 });
        shortened.Start(InternetState.Online, VpnState.Disconnected);
        shortened.Advance(6);
        var staleFrame = shortened.Timer.Callback!;
        shortened.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = 5, RepeatIntervalSeconds = 15 });
        Expect(shortened.Overlay.VisibleEffect is null && shortened.Timer.Pending == 1,
            "Shortening below the warning's elapsed duration hides immediately and reschedules its original repeat.");
        int shows = shortened.Overlay.ShowCalls;
        staleFrame();
        Expect(shortened.Overlay.ShowCalls == shows && shortened.Timer.Pending == 1,
            "A frame canceled by a live duration change cannot revive the old warning.");
        shortened.Advance(9);
        Expect(shortened.Overlay.RedStarts == 2, "A shortened warning repeats fifteen seconds from the first start.");

        using var repeat = new Harness();
        repeat.Start(InternetState.Online, VpnState.Disconnected);
        repeat.Advance(10);
        var staleRepeat = repeat.Timer.Callback!;
        repeat.Controller.ApplySettings(new AppSettings { RepeatIntervalSeconds = 300 });
        repeat.Advance(100);
        Expect(repeat.Overlay.RedStarts == 1, "Lengthening a repeat wait keeps the original episode without an early pulse.");
        repeat.Controller.ApplySettings(new AppSettings { RepeatIntervalSeconds = 15 });
        Expect(repeat.Overlay.RedStarts == 2 && repeat.Overlay.VisibleEffect == EdgeEffect.Red,
            "Shortening a repeat past its elapsed deadline evaluates immediately, producing only one current warning.");
        shows = repeat.Overlay.ShowCalls;
        staleRepeat();
        Expect(repeat.Overlay.ShowCalls == shows && repeat.Timer.MaxPending == 1,
            "The original long-wait callback cannot stack another warning after a live repeat change.");
        repeat.Advance(2);
        int schedules = repeat.Timer.ScheduleCalls;
        repeat.Controller.ApplySettings(new AppSettings { RepeatIntervalSeconds = 15, BorderThickness = 120, BorderOpacity = .4, AllMonitors = false });
        Expect(repeat.Timer.ScheduleCalls == schedules && repeat.Overlay.RedStarts == 2,
            "Appearance-only updates preserve the current animation frame and repeat origin.");
        Expect(repeat.Overlay.Settings is { BorderThickness: 120, BorderOpacity: .4, AllMonitors: false },
            "The single existing presenter receives live appearance and monitor-scope settings.");

        using var green = new Harness();
        green.Start(InternetState.Online, VpnState.Disconnected);
        green.Evaluate(InternetState.Online, VpnState.Connected);
        green.Advance(.4);
        green.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = 7, RepeatIntervalSeconds = 15 });
        green.Advance(.599);
        Expect(green.Overlay.VisibleEffect == EdgeEffect.Green && green.Overlay.GreenStarts == 1,
            "Live red timing changes preserve the separate one-second connection effect.");
        green.Advance(.001);
        Expect(green.Overlay.VisibleEffect is null && green.Timer.Pending == 0,
            "Configured red timing never extends green or schedules connected repeats.");

        foreach (InternetState internet in new[] { InternetState.Offline, InternetState.Unknown })
        {
            using var suppressed = new Harness();
            suppressed.Start(internet, VpnState.Unverified);
            suppressed.Controller.ApplySettings(new AppSettings { RedAlertDurationSeconds = 5, RepeatIntervalSeconds = 15, WarningSoundEnabled = true });
            suppressed.Advance(300);
            Expect(suppressed.Overlay.RedStarts == 0 && suppressed.SoundPlays == 0 && suppressed.Timer.Pending == 0,
                "Live configuration cannot bypass Offline/Unknown suppression.");
        }
    }

    private static void WarningSounds()
    {
        using var test = new Harness();
        test.Start(InternetState.Online, VpnState.Disconnected);
        test.Advance(2);
        Expect(test.SoundPlays == 0, "Optional warning audio is disabled by default.");
        test.Controller.ApplySettings(new AppSettings { WarningSoundEnabled = true, RepeatIntervalSeconds = 15 });
        Expect(test.SoundPlays == 0 && test.Overlay.RedStarts == 1,
            "Enabling sound during a warning does not replay or restart the current pulse.");
        test.Advance(13);
        Expect(test.SoundPlays == 1 && test.Overlay.RedStarts == 2, "Each new automatic red pulse plays one enabled warning sound.");
        test.Advance(2);
        test.Evaluate(InternetState.Online, VpnState.Unverified);
        Expect(test.SoundPlays == 1, "Changing between eligible VPN classifications cannot duplicate warning sound.");
        test.Evaluate(InternetState.Online, VpnState.Connected);
        test.Advance(1);
        test.Controller.Preview(EdgeEffect.Red);
        test.Advance(6);
        test.Controller.Preview(EdgeEffect.Green);
        test.Advance(1);
        Expect(test.SoundPlays == 1, "Green connection effects and explicit previews never play the automatic warning sound.");
        test.Controller.SetMonitoring(false);
        test.Controller.ApplySettings(new AppSettings { WarningSoundEnabled = true, RepeatIntervalSeconds = 15 });
        test.Advance(300);
        Expect(test.SoundPlays == 1 && test.Timer.Pending == 0, "Paused monitoring cancels future audio together with red warnings.");
        test.Start(InternetState.Online, VpnState.Disconnected);
        Expect(test.SoundPlays == 2, "A settled unsafe observation after resuming uses the enabled sound preference.");
        test.Controller.ApplySettings(new AppSettings { WarningSoundEnabled = false, RepeatIntervalSeconds = 15 });
        test.Advance(15);
        Expect(test.SoundPlays == 2 && test.Overlay.RedStarts == 5, "Disabling sound takes effect before the next automatic red pulse.");

        using var failedSound = new Harness(throwSound: true);
        failedSound.Controller.ApplySettings(new AppSettings { WarningSoundEnabled = true });
        failedSound.Start(InternetState.Online, VpnState.Disconnected);
        failedSound.Advance(1);
        Expect(failedSound.SoundPlays == 1 && failedSound.Overlay.VisibleEffect == EdgeEffect.Red,
            "Unavailable audio cannot interrupt red rendering or the one-timer schedule.");
    }

    private static void Envelopes()
    {
        foreach (EdgeEffect effect in Enum.GetValues<EdgeEffect>())
        {
            double duration = EdgeEffectEnvelope.Duration(effect).TotalSeconds;
            Expect(duration == (effect == EdgeEffect.Red ? 6 : 1), "Effect durations match the six-second red and one-second green contract.");
            Expect(EdgeEffectEnvelope.Intensity(effect, TimeSpan.Zero) == 0 &&
                EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromSeconds(duration)) == 0 &&
                EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromSeconds(-1)) == 0 &&
                EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromSeconds(duration + 1)) == 0,
                "Both eased envelopes are zero at and outside their boundaries.");
            double max = 0;
            double previous = 0;
            bool allBounded = true;
            bool allContinuous = true;
            for (int milliseconds = 1; milliseconds <= (int)(duration * 1000); milliseconds++)
            {
                double current = EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromMilliseconds(milliseconds));
                allBounded &= current >= 0 && current <= 1 && double.IsFinite(current);
                allContinuous &= Math.Abs(current - previous) < .007;
                max = Math.Max(max, current);
                previous = current;
            }
            Expect(allBounded, "Every sampled opacity is finite and bounded.");
            Expect(allContinuous, "The continuous envelope has no abrupt opacity steps or flashing.");
            Expect(max > .95, "The effect has a visible, smoothly reached peak.");
            Expect(EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromMilliseconds(1)) < .001 &&
                EdgeEffectEnvelope.Intensity(effect, TimeSpan.FromSeconds(duration - .001)) < .001,
                "Start/end easing approaches transparency gently.");
        }

        double trough = EdgeEffectEnvelope.Intensity(EdgeEffect.Red, TimeSpan.FromSeconds(2.4));
        double peak = EdgeEffectEnvelope.Intensity(EdgeEffect.Red, TimeSpan.FromSeconds(3.6));
        Expect(trough > .75 && peak > .99 && peak - trough < .23,
            "The red pulse has a gentle 2.4-second period over a steady base, without an off/on flash.");
    }

    private static void Expect(bool condition, string message)
    {
        AssertionCount++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Harness : IDisposable
    {
        private readonly Clock _clock = new();
        public readonly Presenter Overlay = new();
        public readonly List<string> Notifications = new();
        public readonly ManualTimer Timer;
        public readonly AlertController Controller;
        public int SoundPlays;

        public Harness(bool throwNotification = false, bool warnWhenVpnUnverified = true, bool throwSound = false)
        {
            Timer = new ManualTimer(_clock);
            Controller = new AlertController(Overlay, Timer, (title, _) =>
            {
                Notifications.Add(title);
                if (throwNotification) throw new InvalidOperationException("Desktop notifications unavailable.");
            }, clock: _clock, playWarningSound: () =>
            {
                SoundPlays++;
                if (throwSound) throw new InvalidOperationException("Audio unavailable.");
            });
            if (!warnWhenVpnUnverified) Controller.SetWarnWhenVpnUnverified(false);
        }

        public void Start(InternetState internet, VpnState vpn, bool notificationsEnabled = true)
        {
            Controller.SetMonitoring(true);
            Evaluate(internet, vpn, notificationsEnabled);
        }

        public void Evaluate(InternetState internet, VpnState vpn, bool notificationsEnabled = true) =>
            Controller.Evaluate(new MonitoringSnapshot(new ConnectivityResult(internet, "Test observation"),
                new VpnDetectionResult(vpn, "Test observation", Array.Empty<string>()), _clock.GetUtcNow()), notificationsEnabled);

        public void Advance(double seconds) => Timer.Advance(TimeSpan.FromSeconds(seconds));
        public void Dispose() => Controller.Dispose();
    }

    private sealed class Presenter : IOverlayPresenter
    {
        public EdgeEffect? VisibleEffect;
        public double Intensity;
        public int RedStarts;
        public int GreenStarts;
        public int ShowCalls;
        public bool Disposed;
        public AppSettings? Settings;

        public void Configure(AppSettings settings) => Settings = settings;

        public void Show(EdgeEffect effect, double intensity)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Presenter));
            if (VisibleEffect != effect)
            {
                if (effect == EdgeEffect.Red) RedStarts++;
                else GreenStarts++;
            }
            VisibleEffect = effect;
            Intensity = intensity;
            ShowCalls++;
        }

        public void Hide() { VisibleEffect = null; Intensity = 0; }
        public void Dispose() { Hide(); Disposed = true; }
    }

    private sealed class Clock : TimeProvider
    {
        public long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Timestamp);
    }

    private sealed class ManualTimer : IAlertTimer
    {
        private readonly Clock _clock;
        private long _due;
        public Action? Callback;
        public int ScheduleCalls;
        public int MaxPending;
        public bool Disposed;
        public int Pending => Callback is null ? 0 : 1;
        public ManualTimer(Clock clock) => _clock = clock;

        public void Schedule(TimeSpan delay, Action callback)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ManualTimer));
            if (delay <= TimeSpan.Zero) throw new InvalidOperationException("One-shot waits must be positive.");
            Callback = callback;
            _due = _clock.Timestamp + delay.Ticks;
            ScheduleCalls++;
            MaxPending = Math.Max(MaxPending, Pending);
        }

        public void Cancel() => Callback = null;
        public void Dispose() { Cancel(); Disposed = true; }

        public void Advance(TimeSpan elapsed)
        {
            long target = _clock.Timestamp + elapsed.Ticks;
            int callbacks = 0;
            while (Callback is not null && _due <= target)
            {
                if (++callbacks > 100000) throw new InvalidOperationException("Unexpected timer loop.");
                _clock.Timestamp = _due;
                var callback = Callback;
                Callback = null;
                callback();
            }
            _clock.Timestamp = target;
        }
    }
}
