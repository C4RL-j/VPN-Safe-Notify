using System;
using System.Collections.Generic;

namespace VpnSafeNotify.Infrastructure;

/// <summary>Physical desktop pixels, including negative virtual-desktop origins.</summary>
public readonly record struct OverlayPixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record OverlayMonitor(string DeviceName, OverlayPixelBounds Bounds, uint DpiX, uint DpiY, bool IsPrimary = false);
public sealed record OverlayMonitorLayout(OverlayMonitor Monitor, double WidthDip, double HeightDip);

/// <summary>
/// The native rectangle is never converted through DIPs: Windows positions each overlay
/// in physical pixels. Only the WPF content size uses the monitor's scale.
/// </summary>
public static class ScreenEdgeOverlayLayout
{
    public static IReadOnlyList<OverlayMonitorLayout> CreatePlan(IEnumerable<OverlayMonitor> monitors, bool allMonitors = true)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var result = new List<OverlayMonitorLayout>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayMonitor monitor in monitors)
        {
            if (!allMonitors && (!monitor.IsPrimary || result.Count > 0)) continue;
            if (monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0 || !devices.Add(monitor.DeviceName)) continue;
            var normalized = monitor with
            {
                DpiX = monitor.DpiX == 0 ? 96u : monitor.DpiX,
                DpiY = monitor.DpiY == 0 ? 96u : monitor.DpiY
            };
            result.Add(new OverlayMonitorLayout(normalized,
                normalized.Bounds.Width * 96d / normalized.DpiX,
                normalized.Bounds.Height * 96d / normalized.DpiY));
        }
        return result;
    }
}
