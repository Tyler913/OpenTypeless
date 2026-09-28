using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Text;

namespace OpenTypeless.UI;

/// <summary>The accent colours used for icon badges, pills and banners (the macOS system palette).</summary>
public enum Tint { Gray, Blue, Orange, Purple, Green, Indigo, Red, Teal, Brown, Pink, Secondary, Accent }

public static class Palette
{
    public static Color Of(Tint tint) => tint switch
    {
        Tint.Gray => Color.FromArgb(255, 142, 142, 147),
        Tint.Blue => Color.FromArgb(255, 0, 122, 255),
        Tint.Orange => Color.FromArgb(255, 255, 149, 0),
        Tint.Purple => Color.FromArgb(255, 175, 82, 222),
        Tint.Green => Color.FromArgb(255, 40, 180, 80),
        Tint.Indigo => Color.FromArgb(255, 88, 86, 214),
        Tint.Red => Color.FromArgb(255, 255, 59, 48),
        Tint.Teal => Color.FromArgb(255, 48, 176, 199),
        Tint.Brown => Color.FromArgb(255, 162, 132, 94),
        Tint.Pink => Color.FromArgb(255, 255, 45, 85),
        Tint.Accent => Resource<Color>("SystemAccentColor", Color.FromArgb(255, 0, 103, 192)),
        _ => Color.FromArgb(255, 128, 128, 128),
    };

    public static SolidColorBrush Brush(Tint tint, double opacity = 1) =>
        new(Of(tint)) { Opacity = opacity };

    public static Brush Theme(string key) => (Brush)Application.Current.Resources[key];

    public static T Resource<T>(string key, T fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed ? typed : fallback;

    public static FontFamily Symbols => Resource<FontFamily>("SymbolThemeFontFamily", new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"));

    public static readonly Color BrandStart = Color.FromArgb(255, 92, 77, 242);
    public static readonly Color BrandEnd = Color.FromArgb(255, 31, 158, 250);

    public static LinearGradientBrush Brand() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops = { new GradientStop { Color = BrandStart, Offset = 0 }, new GradientStop { Color = BrandEnd, Offset = 1 } },
    };
}

/// <summary>Segoe Fluent Icons code points (the SF Symbols of Windows).</summary>
public static class Glyphs
{
    public const string Microphone = "";
    public const string EaseOfAccess = "";
    public const string Globe = "";
    public const string Power = "";
    public const string Paste = "";
    public const string Volume = "";
    public const string Stopwatch = "";
    public const string Settings = "";
    public const string Keyboard = "";
    public const string Key = "";
    public const string Component = "";
    public const string Font = "";
    public const string History = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Refresh = "";
    public const string Copy = "";
    public const string CheckMark = "";
    public const string Completed = "";
    public const string ErrorBadge = "";
    public const string Delete = "";
    public const string Folder = "";
    public const string OpenInNew = "";
    public const string List = "";
    public const string ChevronRight = "";
    public const string Cancel = "";
    public const string Link = "";
    public const string Star = "";
    public const string StarFill = "";
    public const string Lightning = "";
    public const string Cloud = "";
    public const string Search = "";
    public const string Code = "";
    public const string Message = "";
    public const string Record = "";
    public const string Dictionary = "";
    public const string Download = "";
    public const string TouchPointer = "";
    public const string Home = "\uE80F";
    public const string Money = "\uE8C7";
    public const string Calendar = "\uE787";
    public const string Speech = "\uE720";
}

public static class Ui
{
    public static FontIcon Icon(string glyph, double size, Brush? foreground = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size, FontFamily = Palette.Symbols };
        if (foreground != null) icon.Foreground = foreground;
        return icon;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? foreground = null, bool wrap = false)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        if (weight is { } w) block.FontWeight = w;
        if (foreground != null) block.Foreground = foreground;
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>Tooltip plus accessible name, for buttons that show only an icon.</summary>
    public static void Label(FrameworkElement element, string text)
    {
        ToolTipService.SetToolTip(element, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
    }

    public static Brush Secondary => Palette.Theme("TextFillColorSecondaryBrush");
    public static Brush Tertiary => Palette.Theme("TextFillColorTertiaryBrush");
    public static Brush Primary => Palette.Theme("TextFillColorPrimaryBrush");
}

/// <summary>The app's gradient tile with the waveform glyph (the same artwork as the app icon).</summary>
public sealed class AppTile : UserControl
{
    /// <summary>Relative bar heights of the waveform, shared with the icon and tray glyphs.</summary>
    public static readonly double[] Bars = [0.26, 0.6, 1.0, 0.55, 0.82, 0.33];

    private double _tileSize = 32;
    public double TileSize
    {
        get => _tileSize;
        set { _tileSize = value; Render(); }
    }

    public AppTile()
    {
        Render();
    }

    private void Render()
    {
        var size = _tileSize;
        var glyph = size * 0.5;
        var pitch = glyph / (Bars.Length - 0.35);
        var barWidth = Math.Max(1.5, pitch * 0.5);
        var canvas = new Canvas { Width = size, Height = size };
        var x0 = size / 2 - pitch * (Bars.Length - 1) / 2;
        for (var i = 0; i < Bars.Length; i++)
        {
            var h = Math.Max(barWidth, glyph * Bars[i]);
            var bar = new Rectangle { Width = barWidth, Height = h, RadiusX = barWidth / 2, RadiusY = barWidth / 2, Fill = new SolidColorBrush(Colors.White) };
            Canvas.SetLeft(bar, x0 + i * pitch - barWidth / 2);
            Canvas.SetTop(bar, size / 2 - h / 2);
            canvas.Children.Add(bar);
        }
        Content = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.26),
            Background = Palette.Brand(),
            Child = canvas,
        };
    }
}

/// <summary>Coloured rounded square with a white glyph, like the macOS System Settings icons.</summary>
public sealed class IconBadge : UserControl
{
    private readonly Border _border = new();
    private readonly FontIcon _icon = new() { Foreground = new SolidColorBrush(Colors.White) };
    private Tint _tint = Tint.Blue;
    private double _badgeSize = 26;

    public IconBadge()
    {
        _icon.FontFamily = Palette.Symbols;
        _border.Child = _icon;
        Content = _border;
        Update();
    }

    public string Glyph { get => _icon.Glyph; set => _icon.Glyph = value; }
    public Tint Tint { get => _tint; set { _tint = value; Update(); } }
    public double BadgeSize { get => _badgeSize; set { _badgeSize = value; Update(); } }

    private void Update()
    {
        var color = Palette.Of(_tint);
        _border.Width = _border.Height = _badgeSize;
        _border.CornerRadius = new CornerRadius(_badgeSize * 0.27);
        _border.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(217, color.R, color.G, color.B), Offset = 0 },
                new GradientStop { Color = color, Offset = 1 },
            },
        };
        _icon.FontSize = _badgeSize * 0.52;
    }
}

/// <summary>A grouped container with a hairline border (Windows 11 card).</summary>
public static class Card
{
    public static Border Make(UIElement? child = null) => new()
    {
        Background = Palette.Theme("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Palette.Theme("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = child,
    };
}

/// <summary>Section title above a card, with an optional footer note below it.</summary>
[ContentProperty(Name = nameof(Body))]
public sealed class CardSection : UserControl
{
    private readonly TextBlock _title = Ui.Text("", 12, FontWeights.SemiBold, Ui.Secondary);
    private readonly TextBlock _footer = Ui.Text("", 11.5, foreground: Ui.Secondary, wrap: true);
    private readonly Border _card = Card.Make();
    private readonly StackPanel _stack = new();

    public CardSection()
    {
        _title.Margin = new Thickness(4, 0, 0, 0);
        _footer.Margin = new Thickness(4, 0, 4, 0);
        _footer.Visibility = Visibility.Collapsed;
        _card.Child = _stack;
        Content = new StackPanel { Spacing = 8, Children = { _title, _card, _footer } };
    }

    public string Title { get => _title.Text; set => _title.Text = value; }

    public string? Footer
    {
        get => _footer.Text;
        set
        {
            _footer.Text = value ?? "";
            _footer.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The rows inside the card.</summary>
    public UIElementCollection Body => _stack.Children;
}

/// <summary>A settings row: icon badge, title, optional subtitle, and a control on the trailing edge.</summary>
[ContentProperty(Name = nameof(Trailing))]
public sealed class CardRow : UserControl
{
    private readonly IconBadge _badge = new() { BadgeSize = 24, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = Ui.Text("", 13.5);
    private readonly TextBlock _subtitle = Ui.Text("", 12, foreground: Ui.Secondary, wrap: true);
    private readonly ContentPresenter _trailing = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    public CardRow()
    {
        _badge.Visibility = Visibility.Collapsed;
        _subtitle.Visibility = Visibility.Collapsed;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } };
        var grid = new Grid
        {
            Padding = new Thickness(14, 10, 14, 10),
            MinHeight = 48,
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_trailing, 2);
        grid.Children.Add(_badge);
        grid.Children.Add(text);
        grid.Children.Add(_trailing);
        Content = grid;
    }

    public string Glyph
    {
        get => _badge.Glyph;
        set { _badge.Glyph = value; _badge.Visibility = Visibility.Visible; }
    }

    public Tint Tint { get => _badge.Tint; set => _badge.Tint = value; }
    public string Title { get => _title.Text; set => _title.Text = value; }

    public string? Subtitle
    {
        get => _subtitle.Text;
        set
        {
            _subtitle.Text = value ?? "";
            _subtitle.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public object? Trailing { get => _trailing.Content; set => _trailing.Content = value; }
}

public sealed class CardDivider : UserControl
{
    private readonly Rectangle _line = new() { Height = 1 };

    public CardDivider()
    {
        _line.Fill = Palette.Theme("DividerStrokeColorDefaultBrush");
        Content = _line;
        Inset = 50;
    }

    public double Inset
    {
        get => _line.Margin.Left;
        set => _line.Margin = new Thickness(value, 0, 0, 0);
    }
}

/// <summary>Keyboard key caps, e.g. [Right Ctrl] or [Alt] [Space].</summary>
public sealed class KeyCaps : UserControl
{
    private IReadOnlyList<string> _caps = [];
    private bool _large;

    public IReadOnlyList<string> Caps { get => _caps; set { _caps = value; Render(); } }
    public bool Large { get => _large; set { _large = value; Render(); } }

    private void Render()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = _large ? 8 : 4, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var cap in _caps)
        {
            row.Children.Add(new Border
            {
                MinWidth = _large ? 56 : 24,
                MinHeight = _large ? 50 : 24,
                Padding = new Thickness(_large ? 18 : 7, 0, _large ? 18 : 7, 0),
                CornerRadius = new CornerRadius(_large ? 10 : 5),
                Background = Palette.Theme("ControlFillColorDefaultBrush"),
                BorderBrush = Palette.Theme("ControlElevationBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = cap,
                    FontSize = _large ? 21 : 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        Content = row;
    }
}

public sealed class StatusPill : UserControl
{
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = Ui.Text("", 11.5, FontWeights.Medium);
    private readonly Border _border = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3) };
    private Tint _tint = Tint.Green;

    public StatusPill()
    {
        _border.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _dot, _text } };
        _border.VerticalAlignment = VerticalAlignment.Center;
        Content = _border;
        Update();
    }

    public StatusPill(string text, Tint tint) : this()
    {
        Text = text;
        Tint = tint;
    }

    public string Text { get => _text.Text; set => _text.Text = value; }
    public Tint Tint { get => _tint; set { _tint = value; Update(); } }

    private void Update()
    {
        if (_tint == Tint.Secondary)
        {
            _dot.Fill = Ui.Secondary;
            _text.Foreground = Ui.Secondary;
            _border.Background = Palette.Theme("ControlFillColorSecondaryBrush");
            return;
        }
        _dot.Fill = Palette.Brush(_tint);
        _text.Foreground = Palette.Brush(_tint);
        _border.Background = Palette.Brush(_tint, 0.13);
    }
}

/// <summary>Tinted inline banner for warnings and hints.</summary>
[ContentProperty(Name = nameof(Trailing))]
public sealed class Banner : UserControl
{
    private readonly FontIcon _icon = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = Ui.Text("", 12.5, wrap: true);
    private readonly ContentPresenter _trailing = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _border = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), BorderThickness = new Thickness(1) };
    private Tint _tint = Tint.Orange;

    public Banner()
    {
        _icon.FontFamily = Palette.Symbols;
        _text.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(_trailing, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(_text);
        grid.Children.Add(_trailing);
        _border.Child = grid;
        Content = _border;
        Update();
    }

    public Banner(string glyph, Tint tint, string text, UIElement? trailing = null) : this()
    {
        Glyph = glyph;
        Tint = tint;
        Text = text;
        Trailing = trailing;
    }

    public string Glyph { get => _icon.Glyph; set => _icon.Glyph = value; }
    public string Text { get => _text.Text; set => _text.Text = value; }
    public Tint Tint { get => _tint; set { _tint = value; Update(); } }
    public object? Trailing { get => _trailing.Content; set => _trailing.Content = value; }

    private void Update()
    {
        _icon.Foreground = Palette.Brush(_tint);
        _border.Background = Palette.Brush(_tint, 0.1);
        _border.BorderBrush = Palette.Brush(_tint, 0.3);
    }
}

public static class Formatting
{
    /// <summary>"14:32" today, "Yesterday 09:10", otherwise "9/26 18:02".</summary>
    public static string ShortStamp(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var time = local.ToString("t");
        var today = DateTime.Today;
        if (local.Date == today) return time;
        if (local.Date == today.AddDays(-1)) return L("昨天", "Yesterday") + " " + time;
        return $"{local.Month}/{local.Day} {time}";
    }

    public static string DurationLabel(double seconds)
    {
        var s = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return s >= 60 ? $"{s / 60}:{s % 60:00}" : $"{s}s";
    }

    /// <summary>"820 KB", "12.4 MB", "1.3 GB" (decimal units, like File Explorer's size column but 1000-based as on macOS).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 1000) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1000;
            unit++;
        } while (value >= 1000 && unit < units.Length - 1);
        return value.ToString(value < 10 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
