using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Infrastructure;

public sealed record OverlayWindowSnapshot(IntPtr Handle, string DeviceName, OverlayPixelBounds MonitorBounds,
    OverlayPixelBounds WindowBounds, uint DpiX, uint DpiY, bool IsVisible, long ExtendedStyle, bool IsPrimary = false);

/// <summary>
/// Owns one passive window per physical display. The alert controller alone owns
/// animation, duration, and scheduling; this class only presents its current frame.
/// </summary>
public sealed class ScreenEdgeOverlayHost : IOverlayPresenter
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, EdgeWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;
    private bool _layoutReady;
    private bool _visible;
    private EdgeEffect _effect;
    private double _intensity;
    private int _refreshQueued;
    private AppSettings _settings = new();

    public ScreenEdgeOverlayHost()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.VerifyAccess();
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    public void Show(EdgeEffect effect, double intensity)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _effect = effect;
        _intensity = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : 0;
        // Zero intensity must not leave an invisible topmost surface present.
        if (_intensity == 0) { Hide(); return; }
        _visible = true;
        if (!_layoutReady) TryReconcileDisplays();
        else foreach (EdgeWindow window in _windows.Values) TryPresent(window);
    }

    public void Configure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        AppSettings normalized = SettingsService.Normalize(settings);
        bool scopeChanged = _settings.AllMonitors != normalized.AllMonitors;
        _settings = normalized;
        foreach (EdgeWindow window in _windows.Values) window.Glow.Configure(_settings);
        if (scopeChanged)
        {
            _layoutReady = false;
            // Reconcile immediately so a removed secondary screen cannot retain
            // a visible effect until a future animation frame.
            TryReconcileDisplays();
        }
    }

    public void Hide()
    {
        _dispatcher.VerifyAccess();
        _visible = false;
        HideWindows();
    }

    /// <summary>Read-only native placement/style evidence; no network or user data.</summary>
    public IReadOnlyList<OverlayWindowSnapshot> GetWindowSnapshots()
    {
        _dispatcher.VerifyAccess();
        return _windows.Values.Select(window => window.Snapshot()).ToArray();
    }

    /// <summary>Coalesces display notifications, including those received off the UI thread.</summary>
    public void RefreshDisplayLayout()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_disposed) TryReconcileDisplays();
            }));
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _visible = false;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        foreach (EdgeWindow window in _windows.Values) window.CloseSafely();
        _windows.Clear();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args) => RefreshDisplayLayout();
    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => RefreshDisplayLayout();

    private void HideWindows()
    {
        foreach (EdgeWindow window in _windows.Values) window.HideSafely();
    }

    private void TryReconcileDisplays()
    {
        try { ReconcileDisplays(); }
        catch (Win32Exception)
        {
            // A monitor may disappear between enumeration and HWND placement. Fail
            // closed and retry on the next controller frame or display event.
            _layoutReady = false;
            HideWindows();
            Debug.WriteLine("Screen-edge display layout temporarily unavailable; overlays hidden.");
        }
    }

    private void TryPresent(EdgeWindow window)
    {
        try { Present(window); }
        catch (Win32Exception)
        {
            _layoutReady = false;
            window.HideSafely();
            Debug.WriteLine("Screen-edge display placement temporarily unavailable; overlay hidden.");
        }
    }

    private void ReconcileDisplays()
    {
        IReadOnlyList<OverlayMonitorLayout> plan = ScreenEdgeOverlayLayout.CreatePlan(
            ScreenEdgeOverlayNative.EnumerateMonitors(), _settings.AllMonitors);
        var devices = plan.Select(layout => layout.Monitor.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string device in _windows.Keys.Where(device => !devices.Contains(device)).ToArray())
        {
            _windows[device].CloseSafely();
            _windows.Remove(device);
        }
        foreach (OverlayMonitorLayout layout in plan)
        {
            if (!_windows.TryGetValue(layout.Monitor.DeviceName, out EdgeWindow? window))
            {
                // Keep lazy startup: no HWND is created until a visible effect is requested.
                if (!_visible) continue;
                window = new EdgeWindow(layout.Monitor, RefreshDisplayLayout);
                window.Glow.Configure(_settings);
                _windows.Add(layout.Monitor.DeviceName, window);
            }
            window.Align(layout.Monitor);
            if (_visible) Present(window);
            else window.HideSafely();
        }
        // New displays noticed while hidden remain lazy, but must get a window
        // before the next effect rather than inheriting a partially complete layout.
        _layoutReady = plan.Count > 0 && plan.All(layout => _windows.ContainsKey(layout.Monitor.DeviceName));
    }

    private void Present(EdgeWindow window)
    {
        if (window.IsClosed) return;
        window.Glow.SetEffect(_effect, _intensity);
        if (!window.IsVisible)
        {
            window.Show();
            // WPF can choose an initial location during Show. Native positioning wins.
            window.Align(window.Monitor);
        }
    }

    private sealed class EdgeWindow : Window
    {
        private readonly Action _displayChanged;
        private readonly IntPtr _handle;
        private HwndSource? _source;

        internal OverlayMonitor Monitor { get; private set; }
        internal ScreenEdgeGlow Glow { get; } = new();
        internal bool IsClosed { get; private set; }

        internal EdgeWindow(OverlayMonitor monitor, Action displayChanged)
        {
            Monitor = monitor;
            _displayChanged = displayChanged;
            Title = "VPN Safe Notify edge warning";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            Topmost = true;
            Width = 1;
            Height = 1;
            Content = Glow;
            try
            {
                _handle = new WindowInteropHelper(this).EnsureHandle();
                _source = HwndSource.FromHwnd(_handle);
                _source?.AddHook(WindowProcedure);
                Closed += OnClosed;
                ScreenEdgeOverlayNative.ConfigureStyles(_handle);
                Align(monitor);
            }
            catch (Win32Exception)
            {
                // In particular, never display a window whose passive native styles
                // could not be applied. HWND creation itself does not show it.
                CloseSafely();
                throw;
            }
        }

        internal void HideSafely()
        {
            if (IsClosed) return;
            ScreenEdgeOverlayNative.Hide(_handle);
            try { Hide(); }
            catch (Win32Exception) { Debug.WriteLine("Screen-edge native window already unavailable during hide."); }
        }

        internal void CloseSafely()
        {
            if (IsClosed) return;
            HideSafely();
            try { Close(); }
            catch (Win32Exception)
            {
                // Native hide above is the fail-closed guarantee. Detach the hook
                // even when Windows is already destroying this HWND.
                OnClosed(this, EventArgs.Empty);
                Debug.WriteLine("Screen-edge native window already unavailable during close.");
            }
        }

        internal void Align(OverlayMonitor monitor)
        {
            Monitor = monitor;
            // First place the HWND on the correct monitor so WPF receives its DPI.
            ScreenEdgeOverlayNative.Place(_handle, monitor.Bounds);
            uint dpi = ScreenEdgeOverlayNative.GetWindowDpi(_handle);
            double scale = dpi == 0 ? monitor.DpiX / 96d : dpi / 96d;
            Width = monitor.Bounds.Width / scale;
            Height = monitor.Bounds.Height / scale;
            ScreenEdgeOverlayNative.Place(_handle, monitor.Bounds);
        }

        internal OverlayWindowSnapshot Snapshot()
        {
            uint dpi = ScreenEdgeOverlayNative.GetWindowDpi(_handle);
            return new OverlayWindowSnapshot(_handle, Monitor.DeviceName, Monitor.Bounds,
                ScreenEdgeOverlayNative.GetBounds(_handle), dpi, dpi, IsVisible, ScreenEdgeOverlayNative.GetStyles(_handle), Monitor.IsPrimary);
        }

        private IntPtr WindowProcedure(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case 0x0084: // WM_NCHITTEST; native transparent style also covers other processes.
                    handled = true;
                    return new IntPtr(-1); // HTTRANSPARENT
                case 0x0021: // WM_MOUSEACTIVATE
                    handled = true;
                    return new IntPtr(3); // MA_NOACTIVATE
                case 0x02E0: // WM_DPICHANGED: let WPF update its composition scale first.
                case 0x007E: // WM_DISPLAYCHANGE
                case 0x001A: // WM_SETTINGCHANGE (includes scaling/taskbar configuration)
                    _displayChanged();
                    break;
            }
            return IntPtr.Zero;
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            IsClosed = true;
            _source?.RemoveHook(WindowProcedure);
            _source = null;
            Closed -= OnClosed;
        }
    }
}
