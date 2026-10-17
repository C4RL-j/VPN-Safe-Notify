using System;

namespace VpnSafeNotify.Models;

public sealed record AppSettings
{
    public bool MonitoringEnabled { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    public bool WarnWhenVpnUnverified { get; init; } = true;
    public int RedAlertDurationSeconds { get; init; } = 6;
    public int RepeatIntervalSeconds { get; init; } = 60;
    public int BorderThickness { get; init; } = 96;
    public double BorderOpacity { get; init; } = 0.27;
    public bool AllMonitors { get; init; } = true;
    public bool WarningSoundEnabled { get; init; }
    public bool LaunchAtSignIn { get; init; }
    public bool StartMinimized { get; init; }
    public int PollIntervalSeconds { get; init; } = 60;
    public int AlertCooldownSeconds { get; init; } = 60;
    public string[] AdditionalVpnAdapterKeywords { get; init; } = Array.Empty<string>();
}
