using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Klip.Linux;

/// <summary>
/// Visual language shared by every Klip window: light/dark palette (follows the desktop), cards, section headers,
/// platform glyphs and status dots. Everything is built in code, like the rest of the UI.
/// </summary>
internal static class KlipTheme
{
    public const string Background = "Klip.Background";
    public const string Surface = "Klip.Surface";
    public const string SurfaceAlt = "Klip.SurfaceAlt";
    public const string Stroke = "Klip.Stroke";
    public const string TextSecondary = "Klip.TextSecondary";
    public const string Accent = "Klip.Accent";
    public const string AccentSoft = "Klip.AccentSoft";
    public const string Success = "Klip.Success";
    public const string SuccessSoft = "Klip.SuccessSoft";
    public const string Danger = "Klip.Danger";
    public const string DangerSoft = "Klip.DangerSoft";
    public const string Neutral = "Klip.Neutral";

    public static readonly Color AccentColor = Color.Parse("#2563EB");

    public static ResourceDictionary Resources()
    {
        static SolidColorBrush B(string hex) => new(Color.Parse(hex));
        var light = new ResourceDictionary
        {
            [Background] = B("#F4F6FA"),
            [Surface] = B("#FFFFFF"),
            [SurfaceAlt] = B("#EEF1F6"),
            [Stroke] = B("#E2E6EE"),
            [TextSecondary] = B("#5F6673"),
            [Accent] = B("#2563EB"),
            [AccentSoft] = B("#E6EEFE"),
            [Success] = B("#15803D"),
            [SuccessSoft] = B("#DCFCE7"),
            [Danger] = B("#B91C1C"),
            [DangerSoft] = B("#FEE2E2"),
            [Neutral] = B("#8A8F98"),
        };
        var dark = new ResourceDictionary
        {
            [Background] = B("#15171C"),
            [Surface] = B("#1F2229"),
            [SurfaceAlt] = B("#2A2E37"),
            [Stroke] = B("#30343D"),
            [TextSecondary] = B("#A3A9B5"),
            [Accent] = B("#60A5FA"),
            [AccentSoft] = B("#1E3A5F"),
            [Success] = B("#4ADE80"),
            [SuccessSoft] = B("#14361F"),
            [Danger] = B("#F87171"),
            [DangerSoft] = B("#3F1D1D"),
            [Neutral] = B("#7C828D"),
        };
        var dictionary = new ResourceDictionary
        {
            // Fluent accent: buttons with the "accent" class and switches use it.
            ["SystemAccentColor"] = AccentColor,
            ["SystemAccentColorDark1"] = Color.Parse("#1D4ED8"),
            ["SystemAccentColorDark2"] = Color.Parse("#1E40AF"),
            ["SystemAccentColorLight1"] = Color.Parse("#3B82F6"),
            ["SystemAccentColorLight2"] = Color.Parse("#60A5FA"),
        };
        dictionary.ThemeDictionaries[ThemeVariant.Light] = light;
        dictionary.ThemeDictionaries[ThemeVariant.Dark] = dark;
        return dictionary;
    }

    /// <summary>Binds a property to a theme resource, so it follows light/dark switches live.</summary>
    public static T Res<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static Border Card(Control content, Thickness? padding = null) =>
        new Border
        {
            Child = content,
            Padding = padding ?? new Thickness(16, 14),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        }.Res(Border.BackgroundProperty, Surface).Res(Border.BorderBrushProperty, Stroke);

    public static TextBlock Secondary(string text = "", double size = 12.5) =>
        new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }.Res(TextBlock.ForegroundProperty, TextSecondary);

    public static TextBlock Title(string text, double size = 14) =>
        new() { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Section header: title on the left, optional actions on the right.</summary>
    public static Control Section(string title, Control? trailing = null, Control? badge = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(2, 18, 0, 8) };
        var t = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(t);
        if (badge is not null)
        {
            Grid.SetColumn(badge, 1);
            badge.Margin = new Thickness(8, 0, 0, 0);
            grid.Children.Add(badge);
        }
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 3);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    public static Border Pill(string text, string foreground, string background, bool dot = true)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (dot) panel.Children.Add(new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, foreground));
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center }
            .Res(TextBlock.ForegroundProperty, foreground));
        return new Border { Child = panel, Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center }
            .Res(Border.BackgroundProperty, background);
    }

    public static Border Dot(string brush) =>
        new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, brush);

    // 24×24 glyphs, even-odd fill.
    private static readonly Geometry Phone = Geometry.Parse("M8,2h8a2,2 0 0 1 2,2v16a2,2 0 0 1 -2,2h-8a2,2 0 0 1 -2,-2v-16a2,2 0 0 1 2,-2z M8,4v13h8v-13z M11,18.5h2v1.5h-2z");
    private static readonly Geometry Monitor = Geometry.Parse("M3,4h18a1,1 0 0 1 1,1v11a1,1 0 0 1 -1,1h-7v2h3v2h-10v-2h3v-2h-7a1,1 0 0 1 -1,-1v-11a1,1 0 0 1 1,-1z M4,6v9h16v-9z");
    private static readonly Geometry Terminal = Geometry.Parse("M3,4h18a1,1 0 0 1 1,1v14a1,1 0 0 1 -1,1h-18a1,1 0 0 1 -1,-1v-14a1,1 0 0 1 1,-1z M4,6v12h16v-12z M6,8.5l1.4,-1.4l4,4l-4,4l-1.4,-1.4l2.6,-2.6z M12,14h6v1.8h-6z");

    /// <summary>Round tile with the glyph of the platform.</summary>
    public static Control PlatformIcon(string platform)
    {
        var geometry = platform switch { "android" => Phone, "devpeer" => Terminal, _ => Monitor };
        var path = new Avalonia.Controls.Shapes.Path { Data = geometry, Width = 20, Height = 20, Stretch = Stretch.Uniform }.Res(Shape.FillProperty, Accent);
        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Child = path,
            VerticalAlignment = VerticalAlignment.Center,
        }.Res(Border.BackgroundProperty, AccentSoft);
    }

    public static string PlatformLabel(string p) => p switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "linux" => "Linux",
        "devpeer" => "DevPeer",
        _ => p,
    };

    public static Button Subtle(string text) => new Button
    {
        Content = text,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, 5),
        FontWeight = FontWeight.Medium,
        VerticalAlignment = VerticalAlignment.Center,
    }.Res(Button.ForegroundProperty, Accent);

    public static Button Accented(string text) => new()
    {
        Content = text,
        Classes = { "accent" },
        Padding = new Thickness(14, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
