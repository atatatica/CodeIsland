using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace CodeIsland.Windows.Island;

/// Colours, fonts and motion shared by the island, taken from the macOS island
/// (SessionCardStatus, AgendaStyle, QuotaStyle, NotchAnimation).
public static class IslandStyle
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas");
    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Color Rgb(double r, double g, double b, double a = 1) =>
        Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush White(double opacity) => Brush(Rgb(1, 1, 1, opacity));
    public static SolidColorBrush WithOpacity(Color color, double opacity) =>
        Brush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));

    public static readonly SolidColorBrush Black = Brush(Colors.Black);
    public static readonly SolidColorBrush Primary = White(0.92);
    public static readonly SolidColorBrush Secondary = White(0.55);
    public static readonly SolidColorBrush Tertiary = White(0.32);
    public static readonly SolidColorBrush Fill = White(0.06);
    public static readonly SolidColorBrush FillStrong = White(0.10);

    // Card status colours (SessionCardStatus.color).
    public static readonly Color NeedsYou = Rgb(1.0, 0.6, 0.2);
    public static readonly Color Working = Rgb(0.3, 0.85, 0.4);
    public static readonly Color Done = Rgb(1, 1, 1, 0.62);
    public static readonly Color Idle = Rgb(1, 1, 1, 0.55);
    public static readonly Color Error = Rgb(1.0, 0.4, 0.4);

    // Approval and links.
    public static readonly Color Allow = Rgb(0.16, 0.50, 0.24);
    public static readonly Color Deny = Rgb(0.92, 0.38, 0.38);
    public static readonly Color Link = Rgb(0.45, 0.72, 1.0);
    public static readonly Color Reply = Rgb(0.85, 0.47, 0.34);
    public static readonly Color ActiveCount = Rgb(0.4, 1.0, 0.5);

    // Plan usage (QuotaStyle).
    public static readonly Color QuotaNormal = Rgb(1, 1, 1, 0.85);
    public static readonly Color QuotaWarning = Rgb(1.0, 0.7, 0.28);
    public static readonly Color QuotaCritical = Rgb(1.0, 0.4, 0.4);

    public static TextBlock Text(string? text, double size, Brush brush, FontFamily? family = null,
        FontWeight? weight = null, TextTrimming trimming = TextTrimming.CharacterEllipsis)
    {
        var block = new TextBlock
        {
            Text = text ?? string.Empty,
            FontSize = size,
            Foreground = brush,
            FontFamily = family ?? Ui,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = trimming,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(block, TextRenderingMode.ClearType);
        return block;
    }

    public static TextBlock Icon(string glyph, double size, Brush brush) =>
        Text(glyph, size, brush, Icons, FontWeights.Normal, TextTrimming.None);

    /// Small rounded label: status chip, tool chip, tag.
    public static Border Chip(string text, Color color, double size, double fillOpacity = 0.15,
        bool bold = true, FontFamily? family = null, double radius = 4)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = WithOpacity(color, fillOpacity),
            Padding = new Thickness(5, 1, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, size, Brush(color), family ?? Mono, bold ? FontWeights.Bold : FontWeights.Medium, TextTrimming.None)
        };
    }

    /// Duration a spring with this response settles in, for WPF timelines.
    public static Duration SpringDuration(double response) => TimeSpan.FromSeconds(response * 1.6);
}

/// Damped spring with SwiftUI's (response, dampingFraction) parameters,
/// normalised over <see cref="IslandStyle.SpringDuration"/>.
public sealed class SpringEase : EasingFunctionBase
{
    public double Response { get; set; } = 0.45;
    public double Damping { get; set; } = 0.8;

    // EaseIn makes WPF use EaseInCore as written; the curve already eases out.
    public SpringEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1) return 1;
        var t = normalizedTime * Response * 1.6;
        var omega = 2 * Math.PI / Response;
        var zeta = Math.Clamp(Damping, 0.05, 0.999);
        var omegaD = omega * Math.Sqrt(1 - zeta * zeta);
        var envelope = Math.Exp(-zeta * omega * t);
        return 1 - envelope * (Math.Cos(omegaD * t) + zeta * omega / omegaD * Math.Sin(omegaD * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
