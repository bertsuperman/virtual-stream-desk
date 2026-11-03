using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VirtualStreamDeck.App.UI;

/// <summary>Colours and small builders shared by all windows (the UI is built in code, no XAML).</summary>
internal static class Ui
{
    public static readonly IBrush WindowBg = B("#17171A");
    public static readonly IBrush PanelBg = B("#1C1C21");
    public static readonly IBrush Line = B("#2A2A30");
    public static readonly IBrush Tile = B("#26262C");
    public static readonly IBrush TileHover = B("#31313A");
    public static readonly IBrush TilePressed = B("#3A3A44");
    public static readonly IBrush EmptyTile = B("#1D1D21");
    public static readonly IBrush Label = B("#DADAE0");
    public static readonly IBrush Glyph = B("#B4B4BF");
    public static readonly IBrush EmptyGlyph = B("#5A5A63");
    public static readonly IBrush Muted = B("#9A9AA4");
    public static readonly IBrush RailHover = B("#2C2C33");
    public static readonly IBrush RailActiveBg = B("#2A3550");
    public static readonly IBrush RailActive = B("#8DB4FF");
    public static readonly IBrush Accent = B("#4C8DFF");
    public static readonly IBrush Error = B("#E5484D");
    public static readonly IBrush ErrorText = B("#FFC9CB");
    public static readonly IBrush Ok = B("#5CCB6E");
    public static readonly IBrush Warn = B("#F0B44E");
    public static readonly IBrush ToastBg = B("#F0242429");
    public static readonly IBrush Transparent = Brushes.Transparent;

    public static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, IBrush? fg = null,
        double opacity = 1, bool wrap = true, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = fg ?? Label,
        Opacity = opacity,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = margin ?? default,
    };

    public static TextBlock Title(string text) => Text(text, 15, FontWeight.SemiBold, margin: new Thickness(0, 0, 0, 10));

    public static TextBlock Hint(string text, Thickness? margin = null) =>
        Text(text, 12, fg: Muted, margin: margin ?? new Thickness(0, 6, 0, 0));

    public static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        Foreground = Label,
        Opacity = 0.85,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 12, 0),
    };

    public static Button Button(string text, Action onClick, double minWidth = 0)
    {
        var b = new Button { Content = text, MinWidth = minWidth, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button AccentButton(string text, Action onClick, double minWidth = 0)
    {
        var b = Button(text, onClick, minWidth);
        b.Classes.Add("accent"); // Fluent theme: accent colour
        return b;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    public static WrapPanel Wrap(params Control[] children)
    {
        var p = new WrapPanel();
        foreach (var c in children)
        {
            c.Margin = new Thickness(0, 0, 8, 8);
            p.Children.Add(c);
        }
        return p;
    }

    /// <summary>A transparent clickable area with a hover background (rail buttons, small icon buttons).</summary>
    public static Border Clickable(Control content, Action onClick, double width, double height, string? tip = null)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(8),
            Background = Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        if (tip != null) ToolTip.SetTip(border, tip);
        bool active = false;
        border.PointerEntered += (_, _) => { if (!active) border.Background = RailHover; };
        border.PointerExited += (_, _) => { if (!active) border.Background = Transparent; };
        border.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var p = e.GetPosition(border);
            if (p.X >= 0 && p.Y >= 0 && p.X <= border.Bounds.Width && p.Y <= border.Bounds.Height) onClick();
        };
        border.Tag = (Action<bool>)(on =>
        {
            active = on;
            border.Background = on ? RailActiveBg : Transparent;
        });
        return border;
    }

    /// <summary>Marks a <see cref="Clickable"/> as the current section (blue background).</summary>
    public static void SetActive(Border clickable, bool on)
    {
        if (clickable.Tag is Action<bool> set) set(on);
    }
}

/// <summary>
/// Line icons on a 24×24 grid (drawn in the style of the open-source Lucide set), the same on Windows and macOS.
/// Shapes listed in <see cref="Filled"/> are also filled.
/// </summary>
internal static class Icons
{
    private const string Speaker = "M11 5 L6 9 L2 9 L2 15 L6 15 L11 19 Z";
    private static string Circle(double cx, double cy, double r) =>
        FormattableString.Invariant($"M{cx - r} {cy} A{r} {r} 0 1 0 {cx + r} {cy} A{r} {r} 0 1 0 {cx - r} {cy} Z");

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["play"] = "M7 4 L19 12 L7 20 Z",
        ["playpause"] = "M3 5 L11 12 L3 19 Z M15 5 L15 19 M20 5 L20 19",
        ["next"] = "M5 4 L15 12 L5 20 Z M19 5 L19 19",
        ["prev"] = "M19 4 L9 12 L19 20 Z M5 5 L5 19",
        ["stop"] = "M6 6 L18 6 L18 18 L6 18 Z",
        ["volup"] = Speaker + " M15.5 8.5 A5 5 0 0 1 15.5 15.5 M19 5 A10 10 0 0 1 19 19",
        ["voldown"] = Speaker + " M15.5 8.5 A5 5 0 0 1 15.5 15.5",
        ["mute"] = Speaker + " M16 9 L22 15 M22 9 L16 15",
        ["mic"] = "M9 5 A3 3 0 0 1 15 5 L15 12 A3 3 0 0 1 9 12 Z M19 10 L19 12 A7 7 0 0 1 5 12 L5 10 M12 19 L12 22",
        ["globe"] = Circle(12, 12, 10) + " M2 12 L22 12 M12 2 A14 14 0 0 1 12 22 A14 14 0 0 1 12 2",
        ["folder"] = "M3 6 A2 2 0 0 1 5 4 L9 4 L11 6 L19 6 A2 2 0 0 1 21 8 L21 18 A2 2 0 0 1 19 20 L5 20 A2 2 0 0 1 3 18 Z",
        ["file"] = "M14 3 L7 3 A2 2 0 0 0 5 5 L5 19 A2 2 0 0 0 7 21 L17 21 A2 2 0 0 0 19 19 L19 8 Z M14 3 L14 8 L19 8",
        ["app"] = "M4 4 L10 4 L10 10 L4 10 Z M14 4 L20 4 L20 10 L14 10 Z M4 14 L10 14 L10 20 L4 20 Z M14 14 L20 14 L20 20 L14 20 Z",
        ["keyboard"] = "M4 6 L20 6 A2 2 0 0 1 22 8 L22 16 A2 2 0 0 1 20 18 L4 18 A2 2 0 0 1 2 16 L2 8 A2 2 0 0 1 4 6 Z " +
                       "M6 10 L6.01 10 M10 10 L10.01 10 M14 10 L14.01 10 M18 10 L18.01 10 M8 14 L16 14",
        ["layers"] = "M12 2 L22 7 L12 12 L2 7 Z M2 12 L12 17 L22 12 M2 17 L12 22 L22 17",
        ["sliders"] = "M4 21 L4 14 M4 10 L4 3 M12 21 L12 12 M12 8 L12 3 M20 21 L20 16 M20 12 L20 3 M1 14 L7 14 M9 8 L15 8 M17 16 L23 16",
        ["book"] = "M2 4 L8 4 A4 4 0 0 1 12 8 L12 21 A3 3 0 0 0 9 18 L2 18 Z M22 4 L16 4 A4 4 0 0 0 12 8 L12 21 A3 3 0 0 1 15 18 L22 18 Z",
        ["download"] = "M12 3 L12 15 M7 10 L12 15 L17 10 M5 21 L19 21",
        ["plus"] = "M12 5 L12 19 M5 12 L19 12",
        ["chevronleft"] = "M15 18 L9 12 L15 6",
        ["chevronright"] = "M9 18 L15 12 L9 6",
        ["record"] = Circle(12, 12, 9) + " " + Circle(12, 12, 3.5),
        ["stream"] = "M12 12 L12.01 12 M8.5 8.5 A5 5 0 0 0 8.5 15.5 M15.5 8.5 A5 5 0 0 1 15.5 15.5 M5 5 A10 10 0 0 0 5 19 M19 5 A10 10 0 0 1 19 19",
        ["eye"] = "M2 12 C5 5 19 5 22 12 C19 19 5 19 2 12 Z " + Circle(12, 12, 3),
        ["save"] = "M5 3 L16 3 L21 8 L21 19 A2 2 0 0 1 19 21 L5 21 A2 2 0 0 1 3 19 L3 5 A2 2 0 0 1 5 3 Z M7 3 L7 8 L15 8 L15 3 M7 21 L7 14 L17 14 L17 21",
        ["monitor"] = "M4 3 L20 3 A2 2 0 0 1 22 5 L22 15 A2 2 0 0 1 20 17 L4 17 A2 2 0 0 1 2 15 L2 5 A2 2 0 0 1 4 3 Z M8 21 L16 21 M12 17 L12 21",
        ["video"] = "M16 10 L22 6 L22 18 L16 14 Z M3 6 L14 6 A2 2 0 0 1 16 8 L16 16 A2 2 0 0 1 14 18 L3 18 A1 1 0 0 1 2 17 L2 7 A1 1 0 0 1 3 6 Z",
        ["image"] = "M5 3 L19 3 A2 2 0 0 1 21 5 L21 19 A2 2 0 0 1 19 21 L5 21 A2 2 0 0 1 3 19 L3 5 A2 2 0 0 1 5 3 Z " +
                    Circle(9, 9, 1.8) + " M21 15 L16 10 L5 21",
        ["phone"] = "M8 2 L16 2 A2 2 0 0 1 18 4 L18 20 A2 2 0 0 1 16 22 L8 22 A2 2 0 0 1 6 20 L6 4 A2 2 0 0 1 8 2 Z M12 18 L12.01 18",
        ["close"] = "M18 6 L6 18 M6 6 L18 18",
        ["check"] = "M20 6 L9 17 L4 12",
        ["grid"] = "M4 4 L10 4 L10 10 L4 10 Z M14 4 L20 4 L20 10 L14 10 Z M4 14 L10 14 L10 20 L4 20 Z M14 14 L20 14 L20 20 L14 20 Z",
    };

    private static readonly HashSet<string> Filled = new() { "play", "playpause", "next", "prev", "stop" };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Control Make(string name, double size, IBrush brush, double stroke = 2)
    {
        if (!Cache.TryGetValue(name, out var geometry))
        {
            geometry = Geometry.Parse(Paths.TryGetValue(name, out var d) ? d : Paths["plus"]);
            Cache[name] = geometry;
        }
        var path = new Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Filled.Contains(name) ? brush : null,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
