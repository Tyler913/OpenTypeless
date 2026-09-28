using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;

namespace OpenTypeless.UI;

public sealed class StylePage : PageBase
{
    public StylePage() : base(SettingsPage.Style)
    {
        var settings = AppSettings.Shared;

        var vocabulary = new CardSection
        {
            Title = L("专有名词 / 词汇表", "Vocabulary"),
            Footer = L("用逗号或换行分隔。整理时会按这里的写法纠正识别错误，比如人名、产品名、项目名。",
                       "Comma- or line-separated. Clean-up uses these spellings to fix recognition errors — names, products, projects."),
        };
        vocabulary.Body.Add(Editor(settings.Vocabulary, 110, text => settings.Vocabulary = text));
        Body.Children.Add(vocabulary);

        var preferences = new CardSection
        {
            Title = L("整理偏好（可选）", "Clean-up preferences (optional)"),
            Footer = L("例如：「不要使用列表」「保留我的口语语气」「英文术语保持小写」。",
                       "e.g. “Never use bullet lists”, “Keep my casual tone”, “Keep technical terms lowercase”."),
        };
        preferences.Body.Add(Editor(settings.ExtraInstructions, 150, text => settings.ExtraInstructions = text));
        Body.Children.Add(preferences);
    }

    private static TextBox Editor(string text, double height, Action<string> save)
    {
        var editor = new TextBox
        {
            Text = text,
            Height = height,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Margin = new Thickness(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        editor.TextChanged += (_, _) => save(editor.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
        return editor;
    }
}
