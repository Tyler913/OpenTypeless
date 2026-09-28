using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Services;

namespace OpenTypeless.UI;

public enum SettingsPage { Home, General, Shortcut, Providers, Models, Style, History }

public static class SettingsPageInfo
{
    public static readonly SettingsPage[] All =
        [SettingsPage.Home, SettingsPage.General, SettingsPage.Shortcut, SettingsPage.Providers, SettingsPage.Models, SettingsPage.Style, SettingsPage.History];

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
        _ => L("历史记录", "History"),
    };

    public static string Subtitle(this SettingsPage page) => page switch
    {
        SettingsPage.Home => L("你用语音写了多少、省了多少时间、花了多少钱", "What you've dictated, the time it saved and what it cost"),
        SettingsPage.General => L("界面语言、开机启动与系统权限", "Language, startup and system permissions"),
        SettingsPage.Shortcut => L("选择用来开始听写的按键", "Choose the key that starts dictation"),
        SettingsPage.Providers => L("填写 API Key。语音转文字和文字整理可以使用不同的服务商。",
                                    "Add API keys. Speech-to-text and clean-up can use different providers."),
        SettingsPage.Models => L("为语音转文字和文字整理分别选择模型", "Pick a model for each step"),
        SettingsPage.Style => L("让整理结果更符合你的用词和习惯", "Teach the clean-up your terms and preferences"),
        _ => L("每次听写都会保存，失败的可以重试", "Every dictation is saved; failed ones can be retried"),
    };

    public static string Glyph(this SettingsPage page) => page switch
    {
        SettingsPage.Home => Glyphs.Home,
        SettingsPage.General => Glyphs.Settings,
        SettingsPage.Shortcut => Glyphs.Keyboard,
        SettingsPage.Providers => Glyphs.Key,
        SettingsPage.Models => Glyphs.Component,
        SettingsPage.Style => Glyphs.Font,
        _ => Glyphs.History,
    };

    public static Tint Tint(this SettingsPage page) => page switch
    {
        SettingsPage.Home => UI.Tint.Blue,
        SettingsPage.General => UI.Tint.Gray,
        SettingsPage.Shortcut => UI.Tint.Blue,
        SettingsPage.Providers => UI.Tint.Orange,
        SettingsPage.Models => UI.Tint.Purple,
        SettingsPage.Style => UI.Tint.Green,
        _ => UI.Tint.Indigo,
    };
}

/// <summary>Title + subtitle + (optionally scrolling) content, shared by every page.</summary>
public abstract class PageBase : UserControl
{
    protected readonly StackPanel Body = new() { Spacing = 22 };

    protected PageBase(SettingsPage page, bool scrolls = true)
    {
        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Text(page.Title(), 26, FontWeights.SemiBold),
                Ui.Text(page.Subtitle(), 13, foreground: Ui.Secondary, wrap: true),
            },
        };
        if (scrolls)
        {
            var stack = new StackPanel { Spacing = 22, Padding = new Thickness(32, 8, 32, 28), Children = { header, Body } };
            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        else
        {
            var grid = new Grid { Padding = new Thickness(32, 8, 32, 28), RowSpacing = 22 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(Body, 1);
            grid.Children.Add(header);
            grid.Children.Add(Body);
            Content = grid;
        }
        Unloaded += (_, _) => OnClosed();
    }

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
