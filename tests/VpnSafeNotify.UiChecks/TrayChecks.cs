using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using VpnSafeNotify.Infrastructure;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.UiChecks;

internal static class TrayChecks
{
    public static void Run(Action<bool, string> expect)
    {
        var callbacks = new List<string>();
        using var tray = new TrayIconService(() => callbacks.Add("open"), () => callbacks.Add("pause"),
            () => callbacks.Add("exit"), () => callbacks.Add("pause15"), () => callbacks.Add("resume"),
            () => callbacks.Add("settings"));
        var menu = GetField<ContextMenuStrip>(tray, "_menu");
        var icon = GetField<NotifyIcon>(tray, "_icon");
        var actions = menu.Items.OfType<ToolStripMenuItem>().ToDictionary(item => item.Text ?? string.Empty);
        expect(actions.Keys.OrderBy(value => value).SequenceEqual(new[]
            { "Open", "Open Settings", "Pause Monitoring", "Pause for 15 minutes", "Resume", "Exit" }.OrderBy(value => value)),
            "Tray must expose Open, Settings, Pause, timed Pause, Resume, and Exit actions.");
        expect(icon.Visible, "The test's own tray icon must be registered before verifying it.");

        tray.Update(true, null);
        expect(icon.Text.Contains("Checking", StringComparison.OrdinalIgnoreCase), "Tray tooltip must show the initial checking state.");
        expect(actions["Pause Monitoring"].Enabled && actions["Pause for 15 minutes"].Enabled && !actions["Resume"].Enabled,
            "Active monitoring must enable Pause actions and disable Resume.");
        foreach (var (label, expectedCallback) in new[]
        {
            ("Open", "open"), ("Open Settings", "settings"), ("Pause Monitoring", "pause"),
            ("Pause for 15 minutes", "pause15")
        })
        {
            int before = callbacks.Count;
            actions[label].PerformClick();
            expect(callbacks.Count == before + 1 && callbacks.Last() == expectedCallback,
                "Tray action must invoke only its own callback: " + label);
        }
        foreach (InternetState internet in Enum.GetValues<InternetState>())
        foreach (VpnState vpn in Enum.GetValues<VpnState>())
        {
            var snapshot = new MonitoringSnapshot(new ConnectivityResult(internet, "fixture internet"),
                new VpnDetectionResult(vpn, "fixture VPN", Array.Empty<string>()), DateTimeOffset.Now);
            tray.Update(true, snapshot);
            expect(icon.Text.Contains(internet.ToString(), StringComparison.OrdinalIgnoreCase),
                "Tray tooltip must include current internet state: " + internet);
            if (internet != InternetState.Offline)
                expect(icon.Text.Contains(vpn == VpnState.Connected ? "detected" : vpn.ToString(), StringComparison.OrdinalIgnoreCase),
                    "Tray tooltip must include current VPN detection state: " + vpn);
            expect(icon.Text.Length <= 63, "Tray tooltip must remain concise enough for the Windows notification area.");
            expect(!icon.Text.Contains("protected", StringComparison.OrdinalIgnoreCase),
                "Tray tooltip must not claim complete traffic protection.");
        }
        var resumeAt = DateTimeOffset.Now.AddMinutes(15);
        tray.Update(false, null, resumeAt: resumeAt);
        expect(icon.Text.Contains("Paused", StringComparison.OrdinalIgnoreCase) &&
            icon.Text.Contains(resumeAt.ToLocalTime().ToString("HH:mm"), StringComparison.Ordinal),
            "Timed pause tooltip must show the paused state and resume time.");
        expect(!actions["Pause Monitoring"].Enabled && !actions["Pause for 15 minutes"].Enabled && actions["Resume"].Enabled,
            "Paused monitoring must disable Pause actions and enable Resume.");
        int beforeDisabled = callbacks.Count;
        actions["Pause Monitoring"].PerformClick();
        actions["Pause for 15 minutes"].PerformClick();
        expect(callbacks.Count == beforeDisabled, "Disabled Pause menu items must not invoke callbacks.");
        actions["Resume"].PerformClick();
        expect(callbacks.Last() == "resume", "Resume must invoke its explicit callback.");
        foreach (bool running in new[] { false, true })
        {
            tray.Update(running, null, busy: true);
            expect(!actions["Pause Monitoring"].Enabled && !actions["Pause for 15 minutes"].Enabled && !actions["Resume"].Enabled,
                "All monitoring actions must remain disabled during a pending monitoring operation.");
            expect(actions["Open"].Enabled && actions["Open Settings"].Enabled && actions["Exit"].Enabled,
                "Open, Settings, and Exit must remain available while monitoring changes.");
        }
        actions["Exit"].PerformClick();
        expect(callbacks.Last() == "exit", "Exit must invoke the clean-shutdown callback.");
        tray.Dispose();
        expect(!icon.Visible, "Disposing the tray must remove its icon.");
        expect(menu.IsDisposed, "Disposing the tray must dispose its menu and event handlers.");
        tray.Dispose();
        tray.Update(true, null);
        tray.ShowWarning("fixture", "Disposed icons must not display notifications.");
        expect(!icon.Visible, "Repeated disposal and late updates must not recreate the tray icon.");
    }

    private static T GetField<T>(object value, string name) where T : class =>
        (T)(value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing tray verification field: " + name));
}
