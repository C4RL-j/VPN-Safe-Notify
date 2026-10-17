using System;
using System.Collections.Generic;

namespace VpnSafeNotify.Models;

public enum InternetState { Unknown, Offline, Online }
public enum VpnState { Unverified, Disconnected, Connected }

public sealed record ConnectivityResult(InternetState State, string Detail)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
public sealed record VpnDetectionResult(VpnState State, string Detail, IReadOnlyList<string> Evidence)
{
    public IReadOnlyList<AdapterDiagnostic> Adapters { get; init; } = Array.Empty<AdapterDiagnostic>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public string TrafficProtection => "Unverified — tunnel detection does not confirm all traffic uses the tunnel.";
}
public sealed record MonitoringSnapshot(ConnectivityResult Internet, VpnDetectionResult Vpn, DateTimeOffset CheckedAt)
{
    public bool NeedsWarning => Internet.State == InternetState.Online && Vpn.State != VpnState.Connected;
}
