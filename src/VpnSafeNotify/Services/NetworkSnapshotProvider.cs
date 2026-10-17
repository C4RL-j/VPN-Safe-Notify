using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Services;

/// <summary>Shares one passive, read-only adapter/routing snapshot between both detectors.</summary>
public sealed class NetworkSnapshotProvider : INetworkSnapshotProvider
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkSnapshot? _cached;
    private long _cachedAt;

    public async Task<NetworkSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cached is not null && Stopwatch.GetElapsedTime(_cachedAt) < CacheLifetime)
            {
                return _cached;
            }

            // Keep the gate until every native read finishes, even if the caller cancels.
            // This prevents a resumed monitoring session from overlapping an older native read.
            NetworkSnapshot snapshot = await Task.Run(() => Read(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _cached = snapshot;
            _cachedAt = Stopwatch.GetTimestamp();
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static NetworkSnapshot Read(CancellationToken cancellationToken)
    {
        var adapters = new List<NetworkAdapterSnapshot>();
        var routes = new List<NetworkRouteSnapshot>();
        var diagnostics = new List<string>();
        bool adaptersComplete = true;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var addresses = new List<IPAddress>();
                int ipv4Index = 0;
                int ipv6Index = 0;
                bool complete = true;
                try
                {
                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    if (adapter.Supports(NetworkInterfaceComponent.IPv4))
                    {
                        ipv4Index = properties.GetIPv4Properties()?.Index ?? 0;
                    }

                    if (adapter.Supports(NetworkInterfaceComponent.IPv6))
                    {
                        ipv6Index = properties.GetIPv6Properties()?.Index ?? 0;
                    }

                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (address.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Preferred or
                            DuplicateAddressDetectionState.Deprecated)
                        {
                            addresses.Add(address.Address);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    complete = false;
                    adaptersComplete = false;
                    diagnostics.Add("Some adapter addresses or interface indexes could not be read.");
                }

                adapters.Add(new(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                    adapter.OperationalStatus, ipv4Index, ipv6Index, addresses.AsReadOnly())
                { InspectionComplete = complete });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            adaptersComplete = false;
            diagnostics.Add("Adapter enumeration was incomplete.");
        }

        bool routesComplete = true;
        try
        {
            ReadRoutes(routes, diagnostics, ref routesComplete, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            routesComplete = false;
            diagnostics.Add("Windows IPv4/IPv6 routing information was unavailable.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool? proxyConfigured = ReadProxyFlag();
        diagnostics.Add(proxyConfigured switch
        {
            true => "An explicit system proxy or PAC is configured. Proxy use and encryption are unverified.",
            false => "No explicit system proxy or PAC is configured. App-specific proxies may be invisible.",
            null => "System proxy configuration could not be read. Proxy-only connections remain unverified."
        });
        return new(adapters.AsReadOnly(), routes.AsReadOnly(), adaptersComplete, routesComplete, proxyConfigured)
        { Diagnostics = diagnostics.AsReadOnly() };
    }

    private static void ReadRoutes(List<NetworkRouteSnapshot> routes, List<string> diagnostics,
        ref bool complete, CancellationToken cancellationToken)
    {
        var interfaces = new Dictionary<(ushort Family, uint Index), NetworkNativeMethods.IpInterfaceRow>();
        IntPtr table = IntPtr.Zero;
        try
        {
            uint result = NetworkNativeMethods.GetIpInterfaceTable(0, out table);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }

            uint count = ReadBoundedCount(table);
            int start = Marshal.OffsetOf<NetworkNativeMethods.IpInterfaceTable>("First").ToInt32();
            int size = Marshal.SizeOf<NetworkNativeMethods.IpInterfaceRow>();
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<NetworkNativeMethods.IpInterfaceRow>(
                    IntPtr.Add(table, checked(start + index * size)));
                interfaces[(row.Family, row.InterfaceIndex)] = row;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            complete = false;
            diagnostics.Add("Interface metrics or native interface connectivity could not be read.");
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                NetworkNativeMethods.FreeMibTable(table);
            }
        }

        table = IntPtr.Zero;
        try
        {
            uint result = NetworkNativeMethods.GetIpForwardTable2(0, out table);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }

            uint count = ReadBoundedCount(table);
            int start = Marshal.OffsetOf<NetworkNativeMethods.ForwardTable>("First").ToInt32();
            int size = Marshal.SizeOf<NetworkNativeMethods.ForwardRow>();
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<NetworkNativeMethods.ForwardRow>(
                    IntPtr.Add(table, checked(start + index * size)));
                IPAddress? destination = row.Destination.Prefix.Address();
                if (destination is null || row.InterfaceIndex > int.MaxValue)
                {
                    complete = false;
                    continue;
                }

                bool knownInterface = interfaces.TryGetValue((row.Destination.Prefix.Family, row.InterfaceIndex),
                    out NetworkNativeMethods.IpInterfaceRow ipInterface);
                if (!knownInterface)
                {
                    complete = false;
                }

                IPAddress? nextHop = row.NextHop.Address();
                routes.Add(new(destination, row.Destination.Length, (int)row.InterfaceIndex,
                    row.Metric, row.Loopback != 0)
                {
                    IsUsable = row.ValidLifetime > 0 && knownInterface && ipInterface.Connected != 0 &&
                        !(row.Destination.Length == 0 && ipInterface.DisableDefaultRoutes != 0),
                    InterfaceMetric = knownInterface ? ipInterface.Metric : null,
                    IsOnLink = nextHop is not null &&
                        (nextHop.Equals(IPAddress.Any) || nextHop.Equals(IPAddress.IPv6Any)),
                    IsLocal = row.Protocol == 2
                });
            }
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                NetworkNativeMethods.FreeMibTable(table);
            }
        }

        diagnostics.Add($"Read {routes.Count} IPv4/IPv6 route entries. Route and interface metrics are compared for competing prefixes.");
    }

    private static uint ReadBoundedCount(IntPtr table)
    {
        if (table == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows returned no routing table buffer.");
        }

        uint count = unchecked((uint)Marshal.ReadInt32(table));
        return count <= 100000 ? count : throw new InvalidOperationException("Windows returned an invalid route count.");
    }

    private static bool? ReadProxyFlag()
    {
        NetworkNativeMethods.CurrentUserProxy configuration = default;
        try
        {
            if (!NetworkNativeMethods.WinHttpGetIEProxyConfigForCurrentUser(out configuration))
            {
                return Marshal.GetLastWin32Error() == 2 ? false : null;
            }

            // Inspect only pointer presence and the first character. Never materialize URLs,
            // proxy credentials, bypass lists, or fetch a PAC/WPAD resource.
            return IsNonempty(configuration.Proxy) || IsNonempty(configuration.AutoConfigUrl);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (configuration.Proxy != IntPtr.Zero) NetworkNativeMethods.GlobalFree(configuration.Proxy);
            if (configuration.ProxyBypass != IntPtr.Zero) NetworkNativeMethods.GlobalFree(configuration.ProxyBypass);
            if (configuration.AutoConfigUrl != IntPtr.Zero) NetworkNativeMethods.GlobalFree(configuration.AutoConfigUrl);
        }
    }

    private static bool IsNonempty(IntPtr value) => value != IntPtr.Zero && Marshal.ReadInt16(value) != 0;
}
