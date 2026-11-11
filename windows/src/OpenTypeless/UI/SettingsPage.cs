using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Services;

namespace OpenTypeless.UI;

public enum SettingsPage { Home, General, Shortcut, Providers, Models, Style, Apps, History }

public static class SettingsPageInfo
{
    public static readonly SettingsPage[] All =
        [SettingsPage.Home, SettingsPage.General, SettingsPage.Shortcut, SettingsPage.Providers, SettingsPage.Models, SettingsPage.Style, SettingsPage.Apps,
         SettingsPage.History];

    /// <summary>The sidebar, top to bottom: what you come back to look at, then how dictation behaves, then the services behind it.</summary>
    public static readonly SettingsPage[][] Groups =
    [
        [SettingsPage.Home, SettingsPage.History],
        [SettingsPage.General, SettingsPage.Shortcut, SettingsPage.Style, SettingsPage.Apps],
        [SettingsPage.Providers, SettingsPage.Models],
    ];

    public static string RawValue(this SettingsPage page) => page.ToString().ToLowerInvariant();

    public static SettingsPage? Parse(string? raw) => All.Cast<SettingsPage?>().FirstOrDefault(p => p!.Value.RawValue() == raw);

    public static string Title(this SettingsPage page) => page switch
    {
        SettingsPage.Home => L("主页", "Home"),
        SettingsPage.General => L("通用", "General"),
        SettingsPage.Shortcut => L("快捷键", "Shortcut"),
        SettingsPage.Providers => L("服务商", "Providers"),
        SettingsPage.Models => L("模型", "Models"),
        SettingsPage.Style => L("词汇与风格", "Vocabulary & Style"),
        SettingsPage.Apps => L("应用", "Apps"),
        _ => L("历史记录", "History"),
    };

    public static string Glyph(this SettingsPage page) => page switch
    {
        SettingsPage.Home => Glyphs.Home,
        SettingsPage.General => Glyphs.Settings,
        SettingsPage.Shortcut => Glyphs.Keyboard,
        SettingsPage.Providers => Glyphs.Key,
        SettingsPage.Models => Glyphs.Component,
        SettingsPage.Style => Glyphs.Font,
        SettingsPage.Apps => Glyphs.AllApps,
        _ => Glyphs.History,
    };
}

/// <summary>
/// The page's title, with an explanation behind a "?" and controls for the whole page at its end, above the (optionally
/// scrolling) content. Shared by every page.
/// </summary>
public abstract class PageBase : UserControl
{
    /// <summary>How wide a page of settings gets, margins included: past this, rows only put more space between a title and its control.</summary>
    private const double FormWidth = 784;

    /// <summary>The content of a page that scrolls.</summary>
    protected readonly StackPanel Body = new() { Spacing = 28 };
    /// <summary>The content of a page that doesn't: it's given the height left under the title (a stack would let it grow without end).</summary>
    protected readonly Grid Fill = new();
    private readonly ContentPresenter _accessory = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <param name="page">The page.</param>
    /// <param name="help">What the title's "?" explains.</param>
    /// <param name="scrolls">Whether the content scrolls.</param>
    /// <param name="formWidth">Whether the content stops at <see cref="FormWidth"/>, rather than filling the window for pages that have use for the room.</param>
    /// <param name="maxWidth">A width of the page's own to stop at, margins included (Home's).</param>
    /// <param name="showsTitle">False for a page that opens on a headline of its own (Home).</param>
    protected PageBase(SettingsPage page, string? help = null, bool scrolls = true, bool formWidth = true, double? maxWidth = null,
                       bool showsTitle = true)
    {
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            // Windows' TitleTextBlockStyle, as Settings' page titles.
            Children = { Ui.Text(page.Title(), 28, FontWeights.SemiBold) },
        };
        if (help != null) title.Children.Add(new HelpTip(help, page.Title()));
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_accessory, 1);
        header.Children.Add(_accessory);
        if (scrolls)
        {
            var stack = new StackPanel { Spacing = 24, Padding = new Thickness(36, showsTitle ? 6 : 14, 36, 36) };
            if (showsTitle) stack.Children.Add(header);
            stack.Children.Add(Body);
            if (maxWidth is { } width) stack.MaxWidth = width;
            else if (formWidth) stack.MaxWidth = FormWidth;
            // In a grid so that the width limit centres it.
            Content = new ScrollViewer { Content = new Grid { Children = { stack } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        else
        {
            var grid = new Grid { Padding = new Thickness(36, 6, 36, 28), RowSpacing = 20 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(Fill, 1);
            grid.Children.Add(header);
            grid.Children.Add(Fill);
            Content = grid;
        }
        Unloaded += (_, _) => OnClosed();
    }

    /// <summary>Controls for the whole page, at the end of the title.</summary>
    protected UIElement? Accessory { set => _accessory.Content = value; }

    /// <summary>Called when the page leaves the window (navigation, language change, window hidden).</summary>
    protected virtual void OnClosed() { }
}

public static class StatusStyle
{
    public static string Label(this DictationStatus status) => status switch
    {
        DictationStatus.Done => L("完成", "Done"),
        DictationStatus.PolishFailed => L("未整理", "Not cleaned up"),
        DictationStatus.Failed => L("失败", "Failed"),
        DictationStatus.Cancelled => L("已取消", "Cancelled"),
        _ => L("进行中", "In progress"),
    };

    public static Tint Tint(this DictationStatus status) => status switch
    {
        DictationStatus.Done => UI.Tint.Green,
        DictationStatus.PolishFailed => UI.Tint.Orange,
        DictationStatus.Failed => UI.Tint.Red,
        DictationStatus.Cancelled => UI.Tint.Gray,
        _ => UI.Tint.Blue,
    };
}
