using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Checks;

internal static class VpnRouteChecks
{
    public static int Assertions { get; private set; }

    public static async Task RunAsync()
    {
        Assertions = 0;
        NetworkAdapterSnapshot tunnel = Adapter("NetMod TUN", 7, "10.20.0.2");
        NetworkAdapterSnapshot ethernet = Adapter("Intel Ethernet", 3, "192.168.1.20");
        NetworkRouteSnapshot defaultRoute = Route("0.0.0.0", 0, 7);
        Expect(State(new[] { ethernet }, new[] { Route("0.0.0.0", 0, 3) }) == VpnState.Disconnected,
            "A physical uplink with internet routes is not a PC VPN.");
        Expect(State(new[] { ethernet with { Name = "Phone hotspot", InterfaceType = NetworkInterfaceType.Wireless80211 } },
            new[] { Route("0.0.0.0", 0, 3) }) == VpnState.Disconnected,
            "Phone hotspot internet access must not prove the phone VPN is on the PC.");
        Expect(State(new[] { tunnel }, new[] { defaultRoute }) == VpnState.Connected,
            "Recognized Up tunnel, assigned address and usable default route establishes tunnel evidence.");
        Expect(State(new[] { tunnel }, new[] { Route("172.16.0.0", 12, 7) }) == VpnState.Connected,
            "A remote split route is valid active tunnel evidence.");
        VpnDetectionResult split = Classify(new[] { tunnel }, new[] { Route("172.16.0.0", 12, 7) });
        Expect(split.Evidence.Any(item => item.Contains("split-network", StringComparison.Ordinal)), "Split routing must be described.");
        Expect(split.TrafficProtection.Contains("Unverified", StringComparison.Ordinal), "A split tunnel must never claim all-traffic protection.");
        VpnDetectionResult redirect = Classify(new[] { tunnel }, new[] { Route("0.0.0.0", 1, 7), Route("128.0.0.0", 1, 7) });
        Expect(redirect.State == VpnState.Connected && redirect.Evidence.Any(item => item.Contains("broad redirect", StringComparison.Ordinal)),
            "OpenVPN-style /1 redirect pairs should be recognized without a default route.");
        Expect(redirect.TrafficProtection.Contains("Unverified", StringComparison.Ordinal), "Even paired redirect routes cannot prove full protection.");
        Expect(State(new[] { tunnel with { Status = OperationalStatus.Down } }, new[] { defaultRoute }) == VpnState.Disconnected,
            "Down installed adapter plus stale routes must not prove an active tunnel.");
        Expect(State(new[] { tunnel }, new[] { defaultRoute with { IsUsable = false } }) == VpnState.Unverified,
            "Expired/disconnected native forwarding rows must not prove an active tunnel.");
        Expect(State(new[] { tunnel with { Status = OperationalStatus.Unknown } }, new[] { defaultRoute }) == VpnState.Unverified,
            "An unknown operational state must remain unverified.");
        Expect(State(new[] { tunnel with { Addresses = Array.Empty<IPAddress>() } }, new[] { defaultRoute }) == VpnState.Unverified,
            "Enabled Up adapter with no assigned address is not proof.");
        Expect(State(new[] { tunnel with { Addresses = new[] { IPAddress.Parse("169.254.2.3"), IPAddress.Parse("fe80::1") } } },
            new[] { defaultRoute }) == VpnState.Unverified, "APIPA and link-local-only tunnel adapters are insufficient.");
        Expect(State(new[] { tunnel }, Array.Empty<NetworkRouteSnapshot>()) == VpnState.Unverified,
            "An Up/addressed adapter without forwarding routes must remain unverified.");
        foreach (NetworkRouteSnapshot local in new[]
        {
            Route("10.20.0.0", 24, 7) with { IsLocal = true, IsOnLink = true },
            Route("10.20.0.0", 24, 7) with { IsOnLink = true },
            Route("10.20.0.2", 32, 7), Route("203.0.113.9", 32, 7), Route("224.0.0.0", 4, 7),
            Route("127.0.0.0", 8, 7), Route("169.254.0.0", 16, 7), defaultRoute with { IsLoopback = true }
        })
            Expect(State(new[] { tunnel }, new[] { local }) == VpnState.Unverified,
                "Own-subnet, host, loopback, multicast and link-local routes alone must not prove forwarding.");
        Expect(State(new[] { tunnel }, new[] { defaultRoute with { InterfaceIndex = 99 } }) == VpnState.Unverified,
            "A route on another adapter must never count.");
        Expect(State(new[] { ethernet with { Name = "Company VPN", Description = "Ordinary Ethernet" } },
            new[] { Route("0.0.0.0", 0, 3) }) == VpnState.Unverified,
            "A VPN name alone must not turn physical adapter routes into VPN proof.");
        Expect(!VpnEvidenceClassifier.IsRecognizedVpnAdapter("VPN Ethernet", "", NetworkInterfaceType.Ethernet),
            "A generic VPN substring is not a recognized tunnel identity.");
        Expect(VpnEvidenceClassifier.IsRecognizedVpnAdapter("Interface 7", "", NetworkInterfaceType.Tunnel),
            "A genuine tunnel interface type may identify a candidate without a VPN name.");
        foreach (string identity in new[] { "VMware VPN", "Hyper-V WireGuard", "Teredo", "ISATAP", "6to4", "vEthernet" })
            Expect(!VpnEvidenceClassifier.IsRecognizedVpnAdapter(identity, "", NetworkInterfaceType.Tunnel),
                "Common virtual uplinks and transition tunnels are excluded.");
        Expect(State(new[] { ethernet }, Array.Empty<NetworkRouteSnapshot>(), proxy: true) == VpnState.Unverified,
            "Explicit proxy-only configuration cannot confirm a VPN tunnel.");
        Expect(State(new[] { ethernet }, Array.Empty<NetworkRouteSnapshot>(), proxy: null) == VpnState.Unverified,
            "Unreadable proxy settings must not establish a definite disconnected state.");
        NetworkSnapshot noTunnel = new(new[] { ethernet }, Array.Empty<NetworkRouteSnapshot>(), true, true, false);
        VpnDetectionResult runningClient = VpnEvidenceClassifier.Classify(noTunnel, proxyClientRunning: true);
        Expect(runningClient.State == VpnState.Unverified && runningClient.Evidence.Count == 0,
            "Running NetMod alone is an ambiguity hint, never active VPN proof.");
        Expect(runningClient.Diagnostics.Any(item => item.Contains("process presence is not connection proof", StringComparison.Ordinal)),
            "A running NetMod client without tunnel evidence must explain the proxy-only limitation.");
        Expect(VpnEvidenceClassifier.Classify(noTunnel with
            { Adapters = new[] { tunnel with { Status = OperationalStatus.Down } }, Routes = new[] { defaultRoute } },
            proxyClientRunning: true).State == VpnState.Unverified,
            "Running NetMod plus a down/installed adapter and stale route is still unverified.");
        Expect(VpnEvidenceClassifier.Classify(noTunnel with { Adapters = new[] { tunnel }, Routes = new[] { defaultRoute } },
            proxyClientRunning: true).State == VpnState.Connected,
            "Independent valid tunnel route evidence still establishes Connected while NetMod is running.");
        Expect(State(new[] { tunnel }, new[] { defaultRoute }, proxy: true) == VpnState.Connected,
            "A proxy configuration must not erase independently supported active tunnel evidence.");
        var incomplete = new NetworkSnapshot(new[] { tunnel }, new[] { defaultRoute }, true, false, false);
        Expect(VpnEvidenceClassifier.Classify(incomplete).State == VpnState.Unverified,
            "Incomplete routes cannot prove preferred active tunnel forwarding.");
        Expect(VpnEvidenceClassifier.Classify(incomplete with { RoutesComplete = true, AdaptersComplete = false }).State == VpnState.Unverified,
            "Incomplete adapter inspection cannot prove route preference.");
        Expect(VpnEvidenceClassifier.Classify(new(new[] { ethernet }, Array.Empty<NetworkRouteSnapshot>(), true, true, false),
            windowsVpnInspectionComplete: false).State == VpnState.Unverified, "RAS failure must remain unverified without independent tunnel proof.");
        VpnDetectionResult ras = VpnEvidenceClassifier.Classify(NetworkSnapshot.Unavailable, windowsVpnConnected: true);
        Expect(ras.State == VpnState.Connected, "Connected Windows VPN session can independently establish an active tunnel session.");
        Expect(ras.TrafficProtection.Contains("Unverified", StringComparison.Ordinal), "Windows VPN session alone must not prove full traffic protection.");
        Expect(VpnEvidenceClassifier.Classify(new(new[] { tunnel with { Name = "Custom Vendor" } },
            Array.Empty<NetworkRouteSnapshot>(), true, true, false), additionalKeywords: new[] { "Custom Vendor" }).State == VpnState.Unverified,
            "Custom keywords identify candidates only, never bypass operational/address/route checks.");

        NetworkRouteSnapshot ethernetDefault = Route("0.0.0.0", 0, 3) with { Metric = 1, InterfaceMetric = 4 };
        NetworkAdapterSnapshot[] both = { tunnel, ethernet };
        Expect(State(both, new[] { defaultRoute with { Metric = 20, InterfaceMetric = 5 }, ethernetDefault }) == VpnState.Unverified,
            "A stale higher-cost tunnel default must not count while an uplink default is preferred.");
        Expect(State(both, new[] { defaultRoute with { Metric = 1, InterfaceMetric = 5 }, ethernetDefault }) == VpnState.Unverified,
            "Interface metric must be included, rather than selecting the lowest route metric alone.");
        Expect(State(both, new[] { defaultRoute with { Metric = 1, InterfaceMetric = 4 }, ethernetDefault }) == VpnState.Unverified,
            "Equal-cost cross-interface route selection is ambiguous.");
        Expect(State(both, new[] { defaultRoute with { Metric = 1, InterfaceMetric = 1 }, ethernetDefault }) == VpnState.Connected,
            "A lower total tunnel route metric supplies preferred route evidence.");
        Expect(State(both, new[] { defaultRoute with { InterfaceMetric = null }, ethernetDefault }) == VpnState.Unverified,
            "Missing metrics on competing routes must not establish preference.");
        Expect(State(both, new[] { defaultRoute with { Metric = 20 }, ethernetDefault, Route("172.16.0.0", 12, 7) }) == VpnState.Connected,
            "A split route can prove active tunnel forwarding even if another uplink wins the default.");
        Expect(State(new[] { tunnel, ethernet with { Status = OperationalStatus.Down } },
            new[] { defaultRoute, ethernetDefault }) == VpnState.Connected, "A down uplink's stale routes must not defeat an active tunnel.");

        NetworkAdapterSnapshot ipv6 = tunnel with { IPv6Index = 9, Addresses = new[] { IPAddress.Parse("fd00::2") } };
        Expect(State(new[] { ipv6 }, new[] { Route("::", 0, 9) }) == VpnState.Connected, "IPv6-only tunnels and forwarding routes must work.");
        Expect(State(new[] { ipv6 }, new[] { Route("::", 0, 7) }) == VpnState.Unverified, "IPv6 routes must use the IPv6 interface index.");
        Expect(State(new[] { tunnel with { IPv6Index = 9 } }, new[] { Route("::", 0, 9) }) == VpnState.Unverified,
            "An IPv4 assigned address cannot justify an IPv6 route with no usable IPv6 address.");
        foreach (NetworkRouteSnapshot local in new[] { Route("fd00::", 64, 9) with { IsLocal = true }, Route("fd00::2", 128, 9),
            Route("fe80::", 64, 9), Route("ff00::", 8, 9) })
            Expect(State(new[] { ipv6 }, new[] { local }) == VpnState.Unverified, "IPv6 local-only routes must not prove remote forwarding.");
        Expect(!split.Adapters.Select(adapter => adapter.Reason).Concat(split.Diagnostics).Concat(split.Evidence)
            .Any(text => text.Contains("172.16.0.0", StringComparison.Ordinal) || text.Contains("10.20.0.2", StringComparison.Ordinal)),
            "Diagnostics must not expose assigned IP addresses or route destinations.");
        CheckNativeLayouts();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new NetworkSnapshotProvider().ReadAsync(cancellation.Token);
            throw new InvalidOperationException("Already-canceled snapshot check unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
            Assertions++;
        }
    }

    private static void CheckNativeLayouts()
    {
        Type native = typeof(NetworkSnapshotProvider).Assembly.GetType("VpnSafeNotify.Services.NetworkNativeMethods")!;
        Type Get(string name) => native.GetNestedType(name, BindingFlags.NonPublic)!;
        Expect(Marshal.SizeOf(Get("SocketAddress")) == 28, "SOCKADDR_INET SDK size is 28 bytes.");
        Expect(Marshal.SizeOf(Get("AddressPrefix")) == 32, "IP_ADDRESS_PREFIX SDK size is 32 bytes.");
        Expect(Marshal.SizeOf(Get("ForwardRow")) == 104, "MIB_IPFORWARD_ROW2 SDK size is 104 bytes.");
        Expect(Marshal.OffsetOf(Get("ForwardRow"), "Destination").ToInt32() == 12 &&
            Marshal.OffsetOf(Get("ForwardRow"), "NextHop").ToInt32() == 44 &&
            Marshal.OffsetOf(Get("ForwardRow"), "Metric").ToInt32() == 84, "Route fields must use the SDK offsets.");
        Expect(Marshal.SizeOf(Get("IpInterfaceRow")) == 168 && Marshal.OffsetOf(Get("IpInterfaceRow"), "Metric").ToInt32() == 148 &&
            Marshal.OffsetOf(Get("IpInterfaceRow"), "Connected").ToInt32() == 156, "Interface metrics/status must use SDK offsets.");
        Expect(Marshal.OffsetOf(Get("ForwardTable"), "First").ToInt32() == 8 &&
            Marshal.OffsetOf(Get("IpInterfaceTable"), "First").ToInt32() == 8, "Native route tables require padding before their row arrays.");
        Expect(Marshal.SizeOf(Get("CurrentUserProxy")) == (IntPtr.Size == 8 ? 32 : 16), "WinHTTP proxy configuration must use pointer-size alignment.");
    }

    private static NetworkAdapterSnapshot Adapter(string name, int index, string address) =>
        new(name, name, "", NetworkInterfaceType.Ethernet, OperationalStatus.Up, index, index, new[] { IPAddress.Parse(address) });
    private static NetworkRouteSnapshot Route(string destination, int prefix, int index) =>
        new(IPAddress.Parse(destination), prefix, index, 5) { InterfaceMetric = 5 };
    private static VpnDetectionResult Classify(NetworkAdapterSnapshot[] adapters, NetworkRouteSnapshot[] routes, bool? proxy = false) =>
        VpnEvidenceClassifier.Classify(new(adapters, routes, true, true, proxy));
    private static VpnState State(NetworkAdapterSnapshot[] adapters, NetworkRouteSnapshot[] routes, bool? proxy = false) =>
        Classify(adapters, routes, proxy).State;
    private static void Expect(bool value, string reason)
    {
        Assertions++;
        if (!value) throw new InvalidOperationException(reason);
    }
}
