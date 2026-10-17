using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VpnSafeNotify.Infrastructure;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.UiChecks;

/// <summary>Uses only synthetic displays and our own windows; never changes a display or the network.</summary>
internal static class EdgeOverlayChecks
{
    public static void Run(string output, Action<bool, string> expect)
    {
        CheckSyntheticLayouts(expect);
        CheckVisuals(output, expect);
        CheckAppearanceSettings(expect);
        CheckNativeWindows(output, expect);
    }

    private static void CheckSyntheticLayouts(Action<bool, string> expect)
    {
        OverlayMonitor[] monitors =
        {
            new("left-100", new(-1920, 0, 0, 1080), 96, 96),
            new("above-150", new(0, -1440, 2560, 0), 144, 144, IsPrimary: true),
            new("right-200", new(2560, 0, 6400, 2160), 192, 192)
        };
        (double Width, double Height)[] expectedSizes = { (1920, 1080), (1706.6666666667, 960), (1920, 1080) };
        var plan = ScreenEdgeOverlayLayout.CreatePlan(monitors);
        expect(plan.Count == 3, "Mixed DPI layout must keep one overlay per monitor.");
        for (int index = 0; index < monitors.Length; index++)
        {
            var source = monitors[index];
            var target = plan[index];
            expect(target.Monitor.Bounds == source.Bounds, "Mixed DPI layout must preserve exact native pixel bounds, including negative origins.");
            expect(Math.Abs(target.WidthDip - expectedSizes[index].Width) < 0.001,
                "Overlay width must scale independently from native monitor position.");
            expect(Math.Abs(target.HeightDip - expectedSizes[index].Height) < 0.001,
                "Overlay height must respect each monitor's own DPI.");
        }
        var changed = ScreenEdgeOverlayLayout.CreatePlan(new[]
        {
            new OverlayMonitor("left-100", new(-1280, -200, 0, 824), 144, 144),
            new OverlayMonitor("above-150", new(0, -2160, 3840, 0), 192, 192)
        });
        expect(changed.Count == 2, "A removed display must disappear from the next layout plan.");
        expect(changed[0].Monitor.Bounds == new OverlayPixelBounds(-1280, -200, 0, 824),
            "A moved and resized monitor must retain its new physical bounds.");
        expect(Math.Abs(changed[0].WidthDip - 853.3333333333) < 0.001 && Math.Abs(changed[0].HeightDip - 682.6666666667) < 0.001,
            "A scaling change must recompute the moved display's logical width and height.");
        expect(Math.Abs(changed[1].WidthDip - 1920d) < 0.001 && Math.Abs(changed[1].HeightDip - 1080d) < 0.001,
            "A resolution and scaling change must recompute both logical dimensions.");
        var primaryOnly = ScreenEdgeOverlayLayout.CreatePlan(monitors, allMonitors: false);
        expect(primaryOnly.Count == 1 && primaryOnly[0].Monitor.DeviceName == "above-150" && primaryOnly[0].Monitor.IsPrimary,
            "Primary-only scope must use Windows' primary flag rather than monitor order or coordinates.");
        expect(primaryOnly[0].Monitor.Bounds == monitors[1].Bounds && Math.Abs(primaryOnly[0].WidthDip - 1706.6666666667) < .001,
            "Primary-only scope preserves the selected monitor's negative origin, physical bounds, and own DPI.");
        expect(ScreenEdgeOverlayLayout.CreatePlan(monitors.Select(item => item with { IsPrimary = false }), allMonitors: false).Count == 0,
            "An ambiguous primary-only topology must fail closed rather than draw on an arbitrary monitor.");
    }

    private static void CheckAppearanceSettings(Action<bool, string> expect)
    {
        byte[] Render(int thickness, double opacity)
        {
            var visual = new ScreenEdgeGlow();
            visual.Configure(new AppSettings { BorderThickness = thickness, BorderOpacity = opacity });
            visual.SetEffect(EdgeEffect.Red, 1);
            visual.Measure(new Size(960, 540));
            visual.Arrange(new Rect(0, 0, 960, 540));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[960 * 540 * 4];
            bitmap.CopyPixels(pixels, 960 * 4, 0);
            return pixels;
        }

        byte[] thin = Render(24, .05);
        byte[] thick = Render(180, .65);
        byte[] defaults = Render(96, .27);
        expect(Pixel(thin, 960, 35, 270)[3] == 0 && Pixel(defaults, 960, 35, 270)[3] > 0,
            "Border thickness changes the retained gradient's depth in DIPs without extending a thin border inward.");
        expect(Pixel(thick, 960, 100, 270)[3] > 0 && Pixel(defaults, 960, 100, 270)[3] == 0,
            "A thicker configured glow extends farther inward than the default border.");
        expect(Pixel(thin, 960, 4, 270)[3] < Pixel(defaults, 960, 4, 270)[3] &&
            Pixel(defaults, 960, 4, 270)[3] < Pixel(thick, 960, 4, 270)[3],
            "The supported opacity range visibly changes edge strength while remaining translucent.");
        expect(Pixel(thin, 960, 480, 270)[3] == 0 && Pixel(thick, 960, 480, 270)[3] == 0,
            "Both minimum and maximum appearance settings preserve a completely transparent screen center.");
        expect(Pixel(thick, 960, 126, 270)[3] == 0,
            "Even the thickest requested border is limited to twenty-three percent of the shortest display dimension.");
    }

    private static void CheckVisuals(string output, Action<bool, string> expect)
    {
        foreach (EdgeEffect effect in Enum.GetValues<EdgeEffect>())
        {
            var visual = new ScreenEdgeGlow();
            visual.SetEffect(effect, 1);
            visual.Measure(new Size(960, 540));
            visual.Arrange(new Rect(0, 0, 960, 540));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[960 * 540 * 4];
            bitmap.CopyPixels(pixels, 960 * 4, 0);
            byte[] center = Pixel(pixels, 960, 480, 270);
            byte[] left = Pixel(pixels, 960, 4, 270);
            byte[] top = Pixel(pixels, 960, 480, 4);
            byte[] right = Pixel(pixels, 960, 955, 270);
            byte[] bottom = Pixel(pixels, 960, 480, 535);
            expect(center[3] == 0, effect + " must leave the center completely transparent.");
            expect(new[] { left, top, right, bottom }.All(pixel => pixel[3] > 0), effect + " must render on all four edges.");
            expect(effect == EdgeEffect.Red ? left[2] > left[1] && left[2] > left[0] : left[1] > left[2] && left[1] > left[0],
                effect + " must use the expected edge color.");
            byte[] inner = Pixel(pixels, 960, 35, 270);
            expect(left[3] > inner[3], effect + " must fade softly toward the transparent center.");
            string path = Path.Combine(output, effect == EdgeEffect.Red ? "edge-red.png" : "edge-green.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            SaveCompositedPreview(bitmap, Path.Combine(output, effect == EdgeEffect.Red ? "edge-red-preview.png" : "edge-green-preview.png"));
            visual.SetEffect(effect, 0);
            visual.UpdateLayout();
            var hidden = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
            hidden.Render(visual);
            hidden.CopyPixels(pixels, 960 * 4, 0);
            expect(Pixel(pixels, 960, 4, 270)[3] == 0, "Zero effect intensity must remove the visible " + effect + " edge.");
        }
        Console.WriteLine("Window-only effect artifacts: " + Path.Combine(output, "edge-red.png") + "; " + Path.Combine(output, "edge-green.png"));
    }

    private static void CheckNativeWindows(string output, Action<bool, string> expect)
    {
        using var host = new ScreenEdgeOverlayHost();
        IntPtr foreground = Native.GetForegroundWindow();
        host.Show(EdgeEffect.Red, 0.75);
        Pump();
        expect(Native.GetForegroundWindow() == foreground, "Showing red overlays must not change the foreground window.");
        var red = host.GetWindowSnapshots();
        expect(red.Count > 0, "The actual desktop must receive an overlay per attached monitor.");
        foreach (var snapshot in red)
        {
            Console.WriteLine($"Overlay monitor evidence: {snapshot.MonitorBounds.Width}x{snapshot.MonitorBounds.Height} pixels at ({snapshot.MonitorBounds.Left},{snapshot.MonitorBounds.Top}), HWND DPI {snapshot.DpiX}x{snapshot.DpiY}.");
            expect(snapshot.IsVisible, "An active warning must show each overlay.");
            expect(snapshot.WindowBounds == snapshot.MonitorBounds, "Actual overlay HWND bounds must exactly cover native monitor pixels.");
            long style = Native.GetWindowLongPtr(snapshot.Handle, -20).ToInt64();
            const long required = 0x00000020L | 0x00000080L | 0x00080000L | 0x08000000L;
            expect((style & required) == required, "Each overlay needs TRANSPARENT, TOOLWINDOW, LAYERED, and NOACTIVATE extended styles.");
            expect((style & 0x00040000L) == 0, "An overlay must not have the APPWINDOW extended style.");
            expect(Native.SendMessage(snapshot.Handle, 0x0084, IntPtr.Zero, IntPtr.Zero).ToInt64() == -1,
                "The native overlay hit-test must report HTTRANSPARENT.");
            expect(Native.SendMessage(snapshot.Handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3,
                "The native overlay mouse activation must report MA_NOACTIVATE.");
            var window = HwndSource.FromHwnd(snapshot.Handle)?.RootVisual as Window;
            expect(window is { ShowInTaskbar: false, ShowActivated: false, Focusable: false },
                "An overlay must be absent from the taskbar and avoid WPF activation and keyboard focus.");
        }
        CheckHostVisual(red[0].Handle, EdgeEffect.Red, output, expect);
        host.Show(EdgeEffect.Green, 0.75);
        Pump();
        expect(Native.GetForegroundWindow() == foreground, "Replacing red with green must not change the foreground window.");
        var green = host.GetWindowSnapshots();
        expect(red.Select(item => item.Handle).SequenceEqual(green.Select(item => item.Handle)),
            "Replacing an effect must reuse the same overlay windows without stacking.");
        CheckHostVisual(green[0].Handle, EdgeEffect.Green, output, expect);
        host.Configure(new AppSettings { AllMonitors = false, BorderThickness = 120, BorderOpacity = .4 });
        Pump();
        var primary = host.GetWindowSnapshots();
        expect(primary.Count == 1 && primary.All(item => item.IsPrimary && item.IsVisible),
            "Applying primary-only scope immediately leaves only the visible Windows primary monitor overlay.");
        expect(red.Where(item => !item.IsPrimary).All(item => !Native.IsWindow(item.Handle)),
            "Switching to primary-only scope removes secondary overlay HWNDs immediately.");
        expect(Native.GetForegroundWindow() == foreground, "Live appearance and monitor-scope changes cannot steal keyboard focus.");
        host.Configure(new AppSettings());
        Pump();
        expect(host.GetWindowSnapshots().Count == red.Count && host.GetWindowSnapshots().All(item => item.IsVisible),
            "Restoring all-monitor scope immediately displays the current effect without stacking windows.");
        host.Hide();
        Pump();
        expect(host.GetWindowSnapshots().All(item => !item.IsVisible), "Hiding overlays must hide every monitor immediately.");
        expect(Native.GetForegroundWindow() == foreground, "Hiding overlays must not change the foreground window.");
        host.RefreshDisplayLayout();
        Pump();
        expect(host.GetWindowSnapshots().Count == red.Count, "Refreshing the current display topology must not duplicate windows.");
        expect(host.GetWindowSnapshots().All(item => !item.IsVisible), "A display refresh must preserve the hidden state.");
        host.Configure(new AppSettings { AllMonitors = false });
        Pump();
        expect(host.GetWindowSnapshots().All(item => !item.IsVisible), "Changing monitor scope while hidden must not start an overlay.");
        host.Configure(new AppSettings());
        Pump();
        expect(host.GetWindowSnapshots().All(item => !item.IsVisible), "Restoring all monitors while hidden must preserve the hidden state.");
        CheckRealCursorPassThrough(host, red[0].MonitorBounds, expect);
        var handles = host.GetWindowSnapshots().Select(item => item.Handle).ToArray();
        host.Dispose();
        Pump();
        expect(handles.All(handle => !Native.IsWindow(handle)), "Disposing the renderer must destroy its native overlay windows.");
        Console.WriteLine($"Actual overlay HWND, geometry, activation, and style checks: {red.Count} attached monitor(s).");
    }

    private static void CheckHostVisual(IntPtr handle, EdgeEffect effect, string output, Action<bool, string> expect)
    {
        var window = (Window)HwndSource.FromHwnd(handle)!.RootVisual;
        window.UpdateLayout();
        var glow = (ScreenEdgeGlow)window.Content;
        int width = (int)Math.Ceiling(glow.ActualWidth);
        int height = (int)Math.Ceiling(glow.ActualHeight);
        expect(width > 10 && height > 10, "The actual overlay must lay out its visible glow across the monitor.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(glow);
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        expect(Pixel(pixels, width, width / 2, height / 2)[3] == 0,
            "The actual " + effect + " overlay window must leave its center transparent.");
        byte[] edge = Pixel(pixels, width, 4, height / 2);
        expect(edge[3] > 0 && (effect == EdgeEffect.Red ? edge[2] > edge[1] : edge[1] > edge[2]),
            "The actual overlay window must render the current " + effect + " color at its edge.");
        string path = Path.Combine(output, effect == EdgeEffect.Red ? "edge-red-monitor.png" : "edge-green-monitor.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CheckRealCursorPassThrough(ScreenEdgeOverlayHost host, OverlayPixelBounds monitor, Action<bool, string> expect)
    {
        var fixture = new Window
        {
            Title = "VPN Safe Notify click-through verification fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Focusable = false, Width = 96, Height = 96, Background = Brushes.DimGray
        };
        bool hadCursor = Native.GetCursorPos(out var savedCursor);
        int passThroughChecks = 0;
        try
        {
            IntPtr foreground = Native.GetForegroundWindow();
            fixture.Show();
            IntPtr handle = new WindowInteropHelper(fixture).Handle;
            Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000080L));
            foreach ((string name, Native.Point point) in new[]
            {
                ("visible edge", new Native.Point(monitor.Left + 4, monitor.Top + monitor.Height / 2)),
                ("transparent center", new Native.Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2))
            })
            {
                host.Hide();
                Native.SetWindowPos(handle, new IntPtr(-1), point.X - 2, point.Y - 48, 96, 96, 0x0010 | 0x0040);
                Pump();
                if (!hadCursor || !Native.SetCursorPos(point.X, point.Y) || Native.WindowFromPoint(point) != handle)
                {
                    Console.WriteLine("LIMIT: Real cursor hit-test unavailable at " + name + "; the non-activating fixture could not be reached on the current desktop.");
                    continue;
                }
                expect(Native.WindowFromPoint(point) == handle, "The real cursor must initially hit our own fixture at the " + name + ".");
                host.Show(EdgeEffect.Red, 0.75);
                Pump();
                expect(Native.WindowFromPoint(point) == handle, "Real cursor hit-testing must pass through the overlay's " + name + " to the same fixture HWND.");
                passThroughChecks++;
            }
            expect(Native.GetForegroundWindow() == foreground, "Real pointer verification and non-activating overlays must preserve keyboard focus.");
            Console.WriteLine($"Real cursor pass-through: {passThroughChecks}/2 positions verified; foreground HWND preserved.");
        }
        finally
        {
            host.Hide();
            fixture.Close();
            if (hadCursor) Native.SetCursorPos(savedCursor.X, savedCursor.Y);
            Pump();
        }
    }

    private static byte[] Pixel(byte[] pixels, int width, int x, int y) => pixels.AsSpan((y * width + x) * 4, 4).ToArray();

    private static void SaveCompositedPreview(BitmapSource effect, string path)
    {
        // A synthetic dark backdrop makes alpha falloff reviewable in image
        // viewers that display transparent RGB without compositing it correctly.
        var scene = new DrawingVisual();
        using (DrawingContext context = scene.RenderOpen())
        {
            var bounds = new Rect(0, 0, effect.PixelWidth, effect.PixelHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 29, 37)), null, bounds);
            context.DrawImage(effect, bounds);
        }
        var bitmap = new RenderTargetBitmap(effect.PixelWidth, effect.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Point
        {
            public readonly int X;
            public readonly int Y;
            public Point(int x, int y) { X = x; Y = y; }
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
