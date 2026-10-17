using System;
using System.Net;
using System.Runtime.InteropServices;

namespace VpnSafeNotify.Services;

// Read-only IP Helper and WinHTTP calls. Layouts follow Microsoft's WinSDK netioapi.h,
// ws2ipdef.h and winhttp.h. Native buffers are always freed by the caller.
internal static class NetworkNativeMethods
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpInterfaceTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr memory);

    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinHttpGetIEProxyConfigForCurrentUser(out CurrentUserProxy configuration);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern IntPtr GlobalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CurrentUserProxy
    {
        internal int AutoDetect;
        internal IntPtr AutoConfigUrl;
        internal IntPtr Proxy;
        internal IntPtr ProxyBypass;
    }

    // SOCKADDR_INET is the 28-byte union of SOCKADDR_IN and SOCKADDR_IN6.
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SocketAddress
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(4)] internal uint IPv4;
        [FieldOffset(8)] internal ulong IPv6Low;
        [FieldOffset(16)] internal ulong IPv6High;
        [FieldOffset(24)] internal uint Scope;

        internal readonly IPAddress? Address()
        {
            if (Family == 2)
            {
                return new IPAddress(BitConverter.GetBytes(IPv4));
            }

            if (Family != 23)
            {
                return null;
            }

            var bytes = new byte[16];
            BitConverter.GetBytes(IPv6Low).CopyTo(bytes, 0);
            BitConverter.GetBytes(IPv6High).CopyTo(bytes, 8);
            return new IPAddress(bytes, Scope);
        }
    }

    // IP_ADDRESS_PREFIX has four-byte alignment; the enclosing row has eight-byte alignment.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct AddressPrefix
    {
        [FieldOffset(0)] internal SocketAddress Prefix;
        [FieldOffset(28)] internal byte Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal struct ForwardRow
    {
        [FieldOffset(0)] internal ulong InterfaceLuid;
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(12)] internal AddressPrefix Destination;
        [FieldOffset(44)] internal SocketAddress NextHop;
        [FieldOffset(72)] internal byte SitePrefixLength;
        [FieldOffset(76)] internal uint ValidLifetime;
        [FieldOffset(80)] internal uint PreferredLifetime;
        [FieldOffset(84)] internal uint Metric;
        [FieldOffset(88)] internal uint Protocol;
        [FieldOffset(92)] internal byte Loopback;
        [FieldOffset(93)] internal byte AutoconfigureAddress;
        [FieldOffset(94)] internal byte Publish;
        [FieldOffset(95)] internal byte Immortal;
        [FieldOffset(96)] internal uint Age;
        [FieldOffset(100)] internal uint Origin;
    }

    [StructLayout(LayoutKind.Explicit, Size = 168)]
    internal struct IpInterfaceRow
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(8)] internal ulong InterfaceLuid;
        [FieldOffset(16)] internal uint InterfaceIndex;
        // The intervening SDK fields include BOOLEAN flags, enums and ZoneIndices[16].
        [FieldOffset(148)] internal uint Metric;
        [FieldOffset(156)] internal byte Connected;
        [FieldOffset(166)] internal byte DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ForwardTable
    {
        internal uint Count;
        internal ForwardRow First;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IpInterfaceTable
    {
        internal uint Count;
        internal IpInterfaceRow First;
    }
}
