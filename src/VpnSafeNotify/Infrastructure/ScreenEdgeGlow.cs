using System;
using System.Windows;
using System.Windows.Media;
using VpnSafeNotify.Models;
using VpnSafeNotify.Services;

namespace VpnSafeNotify.Infrastructure;

/// <summary>A retained vector glow: no bitmap downloads, blur effects, or animation clock.</summary>
public sealed class ScreenEdgeGlow : FrameworkElement
{
    private EdgeEffect _effect;
    private double _borderThickness = 96;
    private double _borderOpacity = .27;

    public ScreenEdgeGlow()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
    }

    public void SetEffect(EdgeEffect effect, double intensity)
    {
        if (_effect != effect)
        {
            _effect = effect;
            InvalidateVisual();
        }
        Opacity = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : 0;
    }

    public void Configure(AppSettings settings)
    {
        AppSettings normalized = SettingsService.Normalize(settings);
        if (_borderThickness == normalized.BorderThickness && _borderOpacity == normalized.BorderOpacity) return;
        _borderThickness = normalized.BorderThickness;
        _borderOpacity = normalized.BorderOpacity;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double shortest = Math.Min(ActualWidth, ActualHeight);
        if (shortest <= 0) return;
        // Keep most of the desktop completely transparent, including on small monitors.
        double depth = Math.Min(shortest * 0.23, _borderThickness);
        Color color = _effect == EdgeEffect.Green ? Color.FromRgb(50, 226, 135) : Color.FromRgb(246, 51, 66);
        // Continuous gradients avoid the repeated anti-aliased stroke boundaries of
        // concentric rings. Their overlapping falloffs form soft rounded corners.
        // Apply opacity to the combined drawing so corners remain just as gentle as
        // the edges, rather than accumulating two strong translucent red layers.
        var glow = new DrawingGroup { Opacity = _borderOpacity };
        using (DrawingContext drawing = glow.Open())
        {
            drawing.DrawRectangle(Gradient(color, new Point(0, 0), new Point(1, 0)), null,
                new Rect(0, 0, depth, ActualHeight));
            drawing.DrawRectangle(Gradient(color, new Point(1, 0), new Point(0, 0)), null,
                new Rect(ActualWidth - depth, 0, depth, ActualHeight));
            drawing.DrawRectangle(Gradient(color, new Point(0, 0), new Point(0, 1)), null,
                new Rect(0, 0, ActualWidth, depth));
            drawing.DrawRectangle(Gradient(color, new Point(0, 1), new Point(0, 0)), null,
                new Rect(0, ActualHeight - depth, ActualWidth, depth));
        }
        glow.Freeze();
        context.DrawDrawing(glow);
    }

    private static LinearGradientBrush Gradient(Color color, Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        // Enough samples to approximate a cubic falloff while the native gradient
        // continuously interpolates between them; no discrete geometric rings.
        const int steps = 16;
        for (int index = 0; index <= steps; index++)
        {
            double offset = index / (double)steps;
            double remaining = 1 - offset;
            byte alpha = (byte)Math.Round(255 * remaining * remaining * remaining);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), offset));
        }
        brush.Freeze();
        return brush;
    }
}
