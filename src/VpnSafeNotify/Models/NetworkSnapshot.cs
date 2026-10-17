using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace VpnSafeNotify.Models;

// Addresses/routes stay in memory for classification. Diagnostics expose counts and reasons, not addresses.
public sealed record NetworkAdapterSnapshot(string Id, string Name, string Description,
    NetworkInterfaceType InterfaceType, OperationalStatus Status, int IPv4Index, int IPv6Index,
    IReadOnlyList<IPAddress> Addresses)
{
    public bool InspectionComplete { get; init; } = true;
}

public sealed record NetworkRouteSnapshot(IPAddress Destination, int PrefixLength, int InterfaceIndex,
    uint Metric, bool IsLoopback = false)
{
    public bool IsUsable { get; init; } = true;
    public bool IsOnLink { get; init; }
    public bool IsLocal { get; init; }
    public uint? InterfaceMetric { get; init; }
}

public sealed record NetworkSnapshot(IReadOnlyList<NetworkAdapterSnapshot> Adapters,
    IReadOnlyList<NetworkRouteSnapshot> Routes, bool AdaptersComplete, bool RoutesComplete, bool? ProxyConfigured)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public static NetworkSnapshot Unavailable { get; } = new(Array.Empty<NetworkAdapterSnapshot>(),
        Array.Empty<NetworkRouteSnapshot>(), false, false, null)
    { Diagnostics = new[] { "Network adapter and routing information could not be read." } };
}

public sealed record AdapterDiagnostic(string Name, string Description, string Type, string Status,
    int UsableAddressCount, int RelevantRouteCount, string Reason);
