using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

public sealed record VpnAdapterEvidence(string Name, string Description, NetworkInterfaceType InterfaceType,
    OperationalStatus Status, IReadOnlyList<IPAddress> UnicastAddresses)
{
    public int IPv4Index { get; init; }
    public int IPv6Index { get; init; }
    public IReadOnlyList<NetworkRouteSnapshot> Routes { get; init; } = Array.Empty<NetworkRouteSnapshot>();
}

/// <summary>Pure, conservative tunnel evidence rules; never an encryption or full-traffic guarantee.</summary>
public static class VpnEvidenceClassifier
{
    private static readonly string[] RecognizedKeywords =
    {
        "wireguard", "wintun", "openvpn", "ovpn-dco", "tap-windows", "tap-win32",
        "netmod", "net mod", "nordlynx", "mullvad", "surfshark", "windscribe", "softether",
        "fortinet", "forticlient", "cisco anyconnect", "cisco secure client", "globalprotect",
        "pangp virtual", "palo alto", "check point", "checkpoint", "tailscale", "zerotier", "tun2socks"
    };
    private static readonly string[] ExcludedKeywords =
    {
        "loopback", "teredo", "isatap", "6to4", "ip-https", "hyper-v", "vethernet",
        "vmware", "virtualbox", "docker", "wsl"
    };
    private static readonly Regex TunOrTapToken = new(
        @"(?:^|[^a-z0-9])(?:tun|tap)(?:[0-9]+)?(?:$|[^a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsConnectedRasVpn(int connectionState, uint errorCode, string? deviceType, int subState = 0) =>
        connectionState == 0x2000 && errorCode == 0 && (subState == 0 || subState == 0x2000) &&
        string.Equals(deviceType, "vpn", StringComparison.OrdinalIgnoreCase);

    public static VpnDetectionResult Classify(NetworkSnapshot snapshot, bool windowsVpnConnected = false,
        bool windowsVpnInspectionComplete = true, IEnumerable<string>? additionalKeywords = null,
        bool proxyClientRunning = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evidence = new List<string>();
        var diagnostics = new List<string>(snapshot.Diagnostics);
        var adapters = new List<AdapterDiagnostic>();
        bool ambiguous = !snapshot.AdaptersComplete || !snapshot.RoutesComplete || !windowsVpnInspectionComplete;
        if (!windowsVpnInspectionComplete) diagnostics.Add("Windows RAS session inspection was incomplete.");
        if (windowsVpnConnected)
            evidence.Add("Windows RAS reports a connected VPN session with no connection error or reconnecting substate.");

        foreach (NetworkAdapterSnapshot adapter in snapshot.Adapters)
        {
            bool recognized = IsRecognizedVpnAdapter(adapter.Name, adapter.Description, adapter.InterfaceType, additionalKeywords);
            int addresses = adapter.Addresses.Count(IsMeaningfulAddress);
            NetworkRouteSnapshot[] relevant = snapshot.Routes.Where(route => IsRelevantRoute(adapter, route)).ToArray();
            string reason;
            if (!adapter.InspectionComplete)
            {
                ambiguous = true;
                reason = "Adapter address/index inspection was incomplete.";
            }
            else if (adapter.Status == OperationalStatus.Unknown)
            {
                ambiguous = true;
                reason = "Operational status is unknown; an active tunnel cannot be confirmed.";
            }
            else if (adapter.Status != OperationalStatus.Up)
                reason = recognized ? "Recognized tunnel adapter is not operational; installation alone is no evidence."
                    : "Adapter is not operational.";
            else if (!recognized)
            {
                bool suspicious = IsAmbiguousAdapter(adapter);
                ambiguous |= suspicious;
                reason = suspicious ? "Active PPP/virtual or VPN-labelled adapter has no recognized tunnel identity; unverified."
                    : "Ordinary uplink or excluded virtualization/transition adapter; no local VPN tunnel evidence.";
            }
            else if (addresses == 0)
            {
                ambiguous = true;
                reason = "Recognized tunnel is operational but has no usable assigned unicast address.";
            }
            else if (!snapshot.RoutesComplete || !snapshot.AdaptersComplete)
            {
                ambiguous = true;
                reason = "Recognized tunnel has an address, but complete route selection evidence is unavailable.";
            }
            else if (relevant.Length == 0)
            {
                ambiguous = true;
                reason = "Recognized tunnel has an address but no forwarding route beyond local subnet/host routes.";
            }
            else
            {
                NetworkRouteSnapshot[] preferred = relevant.Where(route => IsPreferredRoute(snapshot, route)).ToArray();
                if (preferred.Length == 0)
                {
                    ambiguous = true;
                    reason = "Tunnel routes exist, but competing routes have a lower/equal metric or metrics are unavailable.";
                }
                else
                {
                    bool broad = preferred.Any(route => route.PrefixLength == 0) ||
                        HasRedirectPair(preferred, AddressFamily.InterNetwork) || HasRedirectPair(preferred, AddressFamily.InterNetworkV6);
                    string scope = broad ? "default/broad redirect" : "split-network";
                    evidence.Add($"Active tunnel adapter: {adapter.Name}. Usable address and {scope} forwarding route evidence.");
                    reason = $"Active tunnel evidence: usable address and preferred {scope} route(s). Traffic coverage/encryption unverified.";
                }
            }
            adapters.Add(new(adapter.Name, adapter.Description, adapter.InterfaceType.ToString(), adapter.Status.ToString(),
                addresses, relevant.Length, reason));
        }

        ambiguous |= snapshot.ProxyConfigured != false || proxyClientRunning;
        if (proxyClientRunning && evidence.Count == 0)
            diagnostics.Add("NetMod is running but no local tunnel could be confirmed; process presence is not connection proof.");
        diagnostics.Add("Only local PC tunnels can be detected. A phone hotspot cannot reveal whether the phone uses a VPN.");
        diagnostics.Add("Forwarding routes can be stale. Tunnel detection does not test encryption, VPN-server reachability, or every traffic path.");
        VpnState state = evidence.Count > 0 ? VpnState.Connected : ambiguous ? VpnState.Unverified : VpnState.Disconnected;
        string detail = state switch
        {
            VpnState.Connected => "An active local tunnel/session was detected. Split routing is possible; full traffic protection is unverified.",
            VpnState.Unverified => "VPN status is unverified: tunnel, routing, proxy-only, or incomplete-check evidence is inconclusive.",
            _ => "No active recognized local VPN tunnel was detected. App-specific proxies and a phone's VPN are not visible."
        };
        return new(state, detail, evidence.AsReadOnly()) { Adapters = adapters.AsReadOnly(), Diagnostics = diagnostics.AsReadOnly() };
    }

    public static bool IsRecognizedVpnAdapter(string name, string description, NetworkInterfaceType interfaceType,
        IEnumerable<string>? additionalKeywords = null)
    {
        if (interfaceType == NetworkInterfaceType.Loopback) return false;
        string identity = name + " " + description;
        if (ExcludedKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return false;
        if (interfaceType == NetworkInterfaceType.Tunnel || TunOrTapToken.IsMatch(identity) ||
            RecognizedKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return true;
        return additionalKeywords?.Any(keyword => !string.IsNullOrWhiteSpace(keyword) &&
            identity.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)) == true;
    }

    // Retained for integrations: address/status alone no longer proves an active tunnel.
    public static bool IsActiveVpnAdapter(VpnAdapterEvidence adapter, IEnumerable<string>? additionalKeywords = null)
    {
        var networkAdapter = new NetworkAdapterSnapshot("test", adapter.Name, adapter.Description, adapter.InterfaceType,
            adapter.Status, adapter.IPv4Index, adapter.IPv6Index, adapter.UnicastAddresses);
        return Classify(new(new[] { networkAdapter }, adapter.Routes, true, true, false),
            additionalKeywords: additionalKeywords).State == VpnState.Connected;
    }

    public static bool IsRelevantRoute(NetworkAdapterSnapshot adapter, NetworkRouteSnapshot route)
    {
        int bits = route.Destination.AddressFamily == AddressFamily.InterNetwork ? 32 :
            route.Destination.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 0;
        int interfaceIndex = bits == 32 ? adapter.IPv4Index : adapter.IPv6Index;
        if (!route.IsUsable || route.IsLoopback || route.IsLocal || interfaceIndex <= 0 || route.InterfaceIndex != interfaceIndex ||
            bits == 0 || route.PrefixLength < 0 || route.PrefixLength >= bits) return false;
        if (!adapter.Addresses.Any(address => address.AddressFamily == route.Destination.AddressFamily &&
            IsMeaningfulAddress(address))) return false;
        if (route.PrefixLength == 0)
            return route.Destination.Equals(bits == 32 ? IPAddress.Any : IPAddress.IPv6Any);
        if (bits == 32)
        {
            byte[] bytes = route.Destination.GetAddressBytes();
            if ((bytes[0] == 0 && route.PrefixLength > 1) || bytes[0] == 127 || bytes[0] >= 224 ||
                (bytes[0] == 169 && bytes[1] == 254)) return false;
        }
        else if (route.Destination.IsIPv6LinkLocal || route.Destination.IsIPv6Multicast || IPAddress.IsLoopback(route.Destination))
            return false;
        // Connected on-link networks containing the adapter's own address are insufficient.
        // /1 redirect routes are broader than any connected subnet.
        return route.PrefixLength <= 1 || !route.IsOnLink || !adapter.Addresses.Any(address =>
            address.AddressFamily == route.Destination.AddressFamily && PrefixContains(route.Destination, route.PrefixLength, address));
    }

    private static bool IsPreferredRoute(NetworkSnapshot snapshot, NetworkRouteSnapshot route)
    {
        foreach (NetworkRouteSnapshot other in snapshot.Routes)
        {
            if (!other.IsUsable || other.IsLoopback || other.InterfaceIndex == route.InterfaceIndex ||
                other.PrefixLength != route.PrefixLength || other.Destination.AddressFamily != route.Destination.AddressFamily ||
                !PrefixContains(route.Destination, route.PrefixLength, other.Destination)) continue;
            bool competitorUp = snapshot.Adapters.Any(adapter => adapter.InspectionComplete && adapter.Status == OperationalStatus.Up &&
                adapter.Addresses.Any(address => address.AddressFamily == other.Destination.AddressFamily && IsMeaningfulAddress(address)) &&
                (other.Destination.AddressFamily == AddressFamily.InterNetwork ? adapter.IPv4Index : adapter.IPv6Index) == other.InterfaceIndex);
            if (competitorUp && (route.InterfaceMetric is null || other.InterfaceMetric is null ||
                (ulong)other.Metric + other.InterfaceMetric.Value <= (ulong)route.Metric + route.InterfaceMetric.Value)) return false;
        }
        return true;
    }

    private static bool HasRedirectPair(IEnumerable<NetworkRouteSnapshot> routes, AddressFamily family) =>
        routes.Where(route => route.Destination.AddressFamily == family && route.PrefixLength == 1)
            .Select(route => (route.Destination.GetAddressBytes()[0] & 0x80) != 0).Distinct().Count() == 2;

    private static bool IsAmbiguousAdapter(NetworkAdapterSnapshot adapter)
    {
        string identity = adapter.Name + " " + adapter.Description;
        return !ExcludedKeywords.Any(keyword => identity.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
            (adapter.InterfaceType == NetworkInterfaceType.Ppp || identity.Contains("vpn", StringComparison.OrdinalIgnoreCase) ||
             identity.Contains("virtual", StringComparison.OrdinalIgnoreCase) || identity.Contains("tunnel", StringComparison.OrdinalIgnoreCase));
    }

    private static bool PrefixContains(IPAddress prefix, int bits, IPAddress address)
    {
        if (prefix.AddressFamily != address.AddressFamily) return false;
        byte[] network = prefix.GetAddressBytes();
        byte[] candidate = address.GetAddressBytes();
        int whole = bits / 8;
        for (int index = 0; index < whole; index++) if (network[index] != candidate[index]) return false;
        int remaining = bits % 8;
        int mask = 0xff << (8 - remaining);
        return remaining == 0 || (network[whole] & mask) == (candidate[whole] & mask);
    }

    public static bool IsMeaningfulAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.IPv6Any) &&
            !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
    }
}
