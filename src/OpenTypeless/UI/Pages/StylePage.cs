using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;

namespace OpenTypeless.UI;

public sealed class StylePage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly TextBox _vocabulary;
    private readonly CardSection _learning = new()
    {
        Title = L("自动学习", "Learning"),
        Footer = L("粘贴后如果你在输入框里改了某个识别错的词（比如把 TypeList 改成 Typeless），离开输入框或发送后会自动把它加进词汇表。只学发音相近的改动，不学改写、改数字和普通词替换。输入框内容只在本机读取。",
                   "If you fix a misrecognised word after pasting (say TypeList → Typeless), it's added to the vocabulary once you leave the field or send. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps. The field is read on this PC only."),
    };

    public StylePage() : base(SettingsPage.Style)
    {
        var vocabulary = new CardSection
        {
            Title = L("专有名词 / 词汇表", "Vocabulary"),
            Footer = L("用逗号或换行分隔。整理时会按这里的写法纠正识别错误，比如人名、产品名、项目名。",
                       "Comma- or line-separated. Clean-up uses these spellings to fix recognition errors — names, products, projects."),
        };
        _vocabulary = Editor(_settings.Vocabulary, 110, text => _settings.Vocabulary = text);
        vocabulary.Body.Add(_vocabulary);
        Body.Children.Add(vocabulary);

        RenderLearning();
        Body.Children.Add(_learning);

        var preferences = new CardSection
        {
            Title = L("整理偏好（可选）", "Clean-up preferences (optional)"),
            Footer = L("例如：「不要使用列表」「保留我的口语语气」「英文术语保持小写」。",
                       "e.g. “Never use bullet lists”, “Keep my casual tone”, “Keep technical terms lowercase”."),
        };
        preferences.Body.Add(Editor(_settings.ExtraInstructions, 150, text => _settings.ExtraInstructions = text));
        Body.Children.Add(preferences);

        _settings.PropertyChanged += OnSettingsChanged;
    }

    protected override void OnClosed() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.LearnedTerms):
                DispatcherQueue.TryEnqueue(RenderLearning);
                break;
            case nameof(AppSettings.Vocabulary):
                // A term was learned (or forgotten) while the page is open: show it, unless the user is typing.
                DispatcherQueue.TryEnqueue(() =>
                {
                    var text = _settings.Vocabulary;
                    if (_vocabulary.FocusState == FocusState.Unfocused && Normalize(_vocabulary.Text) != text) _vocabulary.Text = text;
                });
                break;
        }
    }

    private void RenderLearning()
    {
        _learning.Body.Clear();
        var toggle = new ToggleSwitch { IsOn = _settings.LearnFromEdits, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => _settings.LearnFromEdits = toggle.IsOn;
        _learning.Body.Add(new CardRow
        {
            Glyph = Glyphs.Dictionary, Tint = Tint.Orange, Title = L("从我的修改中学习", "Learn from my corrections"),
            Trailing = toggle,
        });
        foreach (var learned in Enumerable.Reverse(_settings.LearnedTerms))
        {
            _learning.Body.Add(new CardDivider { Inset = 14 });
            _learning.Body.Add(LearnedRow(learned));
        }
    }

    private FrameworkElement LearnedRow(LearnedTerm learned)
    {
        var heard = Ui.Text(L("听成了 ", "heard as ") + string.Join(L("、", ", "), learned.HeardAs.Select(h => $"“{h}”")), 11.5, foreground: Ui.Secondary);
        heard.TextTrimming = TextTrimming.CharacterEllipsis;
        heard.VerticalAlignment = VerticalAlignment.Center;
        var term = Ui.Text(learned.Term, 13, FontWeights.Medium);
        term.VerticalAlignment = VerticalAlignment.Center;
        var date = Ui.Text(learned.Date.ToLocalTime().ToString("d"), 11, foreground: Ui.Tertiary);
        date.VerticalAlignment = VerticalAlignment.Center;
        var forget = new Button
        {
            Content = Ui.Icon(Glyphs.Cancel, 11, Ui.Secondary),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Ui.Label(forget, L("从词汇表删除，以后不再自动学习", "Remove from the vocabulary and never learn it again"));
        forget.Click += (_, _) => _settings.Forget(learned);

        var row = new Grid { Padding = new Thickness(14, 6, 10, 6), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(term);
        Grid.SetColumn(heard, 1);
        row.Children.Add(heard);
        Grid.SetColumn(date, 2);
        row.Children.Add(date);
        Grid.SetColumn(forget, 3);
        row.Children.Add(forget);
        return row;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

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
        editor.TextChanged += (_, _) => save(Normalize(editor.Text));
        return editor;
    }
}
