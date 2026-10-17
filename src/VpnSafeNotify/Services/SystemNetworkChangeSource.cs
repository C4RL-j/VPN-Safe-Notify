using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace VpnSafeNotify.Services;

/// <summary>Passive OS notifications only. Callback data is neither inspected nor retained.</summary>
public sealed class SystemNetworkChangeSource : INetworkChangeSource, IDisposable
{
    private readonly object _sync = new();
    private readonly RouteChangeCallback _routeCallback;
    private Action? _changed;
    private IntPtr _routeHandle;
    private bool _disposed;
    public string Status { get; private set; } = "Network notifications are paused.";

    public SystemNetworkChangeSource() => _routeCallback = (_, _, _) => RaiseChanged();

    public event Action? Changed
    {
        add
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool first = _changed is null;
                _changed += value;
                if (first)
                {
                    NetworkChange.NetworkAddressChanged += OnAddressChanged;
                    NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                    uint error = NotifyRouteChange2(0, _routeCallback, IntPtr.Zero, false, out _routeHandle);
                    Status = error == 0 ? "Listening for adapter, address, and IPv4/IPv6 route changes." :
                        $"Listening for adapter/address changes; route notifications unavailable ({error}). Slow fallback remains active.";
                }
            }
        }
        remove
        {
            IntPtr handle = IntPtr.Zero;
            lock (_sync)
            {
                _changed -= value;
                if (_changed is null) handle = DetachListeners();
            }
            CancelNative(handle);
        }
    }

    private void OnAddressChanged(object? sender, EventArgs args) => RaiseChanged();
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => RaiseChanged();
    private void RaiseChanged()
    {
        // Do not acquire _sync: CancelMibChangeNotify2 waits for native callbacks.
        try { Volatile.Read(ref _changed)?.Invoke(); }
        catch (Exception) { /* Never propagate an exception across a native callback. */ }
    }

    private IntPtr DetachListeners()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        var handle = _routeHandle;
        _routeHandle = IntPtr.Zero;
        Status = "Network notifications are paused.";
        return handle;
    }

    private void CancelNative(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CancelMibChangeNotify2(handle);
        GC.KeepAlive(_routeCallback);
    }

    public void Dispose()
    {
        IntPtr handle;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _changed = null;
            handle = DetachListeners();
        }
        CancelNative(handle);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RouteChangeCallback(IntPtr context, IntPtr row, int notificationType);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint NotifyRouteChange2(ushort addressFamily, RouteChangeCallback callback,
        IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr notificationHandle);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint CancelMibChangeNotify2(IntPtr notificationHandle);
}
