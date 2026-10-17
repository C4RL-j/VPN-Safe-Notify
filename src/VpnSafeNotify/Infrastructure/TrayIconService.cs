using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VpnSafeNotify.Models;

namespace VpnSafeNotify.Infrastructure;

/// <summary>Owns the tray icon and all native icon handles for the lifetime of the app.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _pause15;
    private readonly ToolStripMenuItem _resume;
    private readonly Icon _neutral = CreateIcon(Color.FromArgb(145, 184, 255));
    private readonly Icon _detected = CreateIcon(Color.FromArgb(117, 223, 181));
    private readonly Icon _warning = CreateIcon(Color.FromArgb(255, 198, 117));
    private readonly Icon _danger = CreateIcon(Color.FromArgb(255, 141, 145));
    private readonly Icon _paused = CreateIcon(Color.FromArgb(150, 160, 180));
    private bool _disposed;

    public TrayIconService(Action open, Action toggleMonitoring, Action exit,
        Action? pause15 = null, Action? resume = null, Action? openSettings = null)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open", null, (_, _) => open());
        _menu.Items.Add("Open Settings", null, (_, _) => (openSettings ?? open)());
        _toggle = new ToolStripMenuItem("Pause Monitoring", null, (_, _) => toggleMonitoring());
        _menu.Items.Add(_toggle);
        _pause15 = new ToolStripMenuItem("Pause for 15 minutes", null, (_, _) => (pause15 ?? toggleMonitoring)());
        _menu.Items.Add(_pause15);
        _resume = new ToolStripMenuItem("Resume", null, (_, _) => (resume ?? toggleMonitoring)());
        _menu.Items.Add(_resume);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon { Icon = _neutral, Text = "VPN Safe Notify", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
    }

    public Icon WindowIcon => _neutral;

    public void Update(bool running, MonitoringSnapshot? snapshot, bool busy = false, DateTimeOffset? resumeAt = null)
    {
        if (_disposed) return;
        _toggle.Enabled = running && !busy;
        _pause15.Enabled = running && !busy;
        _resume.Enabled = !running && !busy;
        var presentation = TrayPresentation.Create(running, snapshot, resumeAt);
        _icon.Icon = presentation.Tone switch
        {
            TrayTone.Green => _detected, TrayTone.Amber => _warning, TrayTone.Red => _danger, _ => _paused
        };
        _icon.Text = presentation.Tooltip;
    }

    public void ShowWarning(string title, string message)
    {
        if (!_disposed) _icon.ShowBalloonTip(8000, title, message, ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _neutral.Dispose(); _detected.Dispose(); _warning.Dispose(); _danger.Dispose(); _paused.Dispose();
    }

    private static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var shield = new GraphicsPath();
            shield.AddLines(new[] { new PointF(16, 2), new PointF(29, 7), new PointF(27, 21), new PointF(22, 27), new PointF(16, 31), new PointF(10, 27), new PointF(5, 21), new PointF(3, 7) });
            shield.CloseFigure();
            using var fill = new SolidBrush(color);
            graphics.FillPath(fill, shield);
            using var mark = new Pen(Color.FromArgb(24, 32, 48), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(mark, 16, 10, 16, 18);
            graphics.FillEllipse(Brushes.Black, 14.5f, 22, 3, 3);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
