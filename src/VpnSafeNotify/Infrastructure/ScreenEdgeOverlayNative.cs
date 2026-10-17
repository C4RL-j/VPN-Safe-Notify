using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VpnSafeNotify.Infrastructure;

internal static class ScreenEdgeOverlayNative
{
    internal const int ExtendedStyleIndex = -20;
    internal const long Transparent = 0x00000020L;
    internal const long ToolWindow = 0x00000080L;
    internal const long AppWindow = 0x00040000L;
    internal const long Layered = 0x00080000L;
    internal const long NoActivate = 0x08000000L;
    private static readonly IntPtr Topmost = new(-1);

    internal static IReadOnlyList<OverlayMonitor> EnumerateMonitors()
    {
        var monitors = new List<OverlayMonitor>();
        MonitorEnumeration callback = (IntPtr monitor, IntPtr _, ref NativeRect __, IntPtr ___) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (GetMonitorInfo(monitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                // Initial planning only. GetDpiForWindow supplies the authoritative DPI
                // once the PMv2 HWND has been placed on this monitor.
                if (GetDpiForMonitor(monitor, 0, out uint x, out uint y) == 0 && x > 0 && y > 0)
                { dpiX = x; dpiY = y; }
                monitors.Add(new OverlayMonitor(info.Device, info.Monitor.ToBounds(), dpiX, dpiY,
                    IsPrimary: (info.Flags & 1) != 0)); // MONITORINFOF_PRIMARY
            }
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate displays for edge warnings.");
        GC.KeepAlive(callback);
        return monitors;
    }

    internal static void ConfigureStyles(IntPtr handle)
    {
        long styles = GetStyles(handle);
        styles = (styles | Transparent | ToolWindow | Layered | NoActivate) & ~AppWindow;
        if (IntPtr.Size == 8) SetWindowLongPtr64(handle, ExtendedStyleIndex, new IntPtr(styles));
        else SetWindowLong32(handle, ExtendedStyleIndex, unchecked((int)styles));
        const long required = Transparent | ToolWindow | Layered | NoActivate;
        long actual = GetStyles(handle);
        if ((actual & required) != required || (actual & AppWindow) != 0)
            throw new Win32Exception("Windows could not make the screen-edge overlay passive and click-through.");
    }

    internal static long GetStyles(IntPtr handle) => IntPtr.Size == 8
        ? GetWindowLongPtr64(handle, ExtendedStyleIndex).ToInt64()
        : unchecked((uint)GetWindowLong32(handle, ExtendedStyleIndex));

    internal static void Place(IntPtr handle, OverlayPixelBounds bounds)
    {
        // In a PerMonitorV2 process these coordinates are physical desktop pixels.
        // Never set WPF Left/Top from a DPI-converted virtual-desktop coordinate.
        const uint noActivate = 0x0010, noOwnerZOrder = 0x0200, frameChanged = 0x0020;
        if (!SetWindowPos(handle, Topmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                noActivate | noOwnerZOrder | frameChanged))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not align an edge warning with its display.");
    }

    internal static OverlayPixelBounds GetBounds(IntPtr handle) => GetWindowRect(handle, out NativeRect bounds)
        ? bounds.ToBounds() : default;

    internal static uint GetWindowDpi(IntPtr handle) => GetDpiForWindow(handle);

    internal static void Hide(IntPtr handle)
    {
        if (handle != IntPtr.Zero) ShowWindow(handle, 0); // SW_HIDE; no activation.
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly OverlayPixelBounds ToBounds() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    private delegate bool MonitorEnumeration(IntPtr monitor, IntPtr hdc, ref NativeRect rectangle, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumeration callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
