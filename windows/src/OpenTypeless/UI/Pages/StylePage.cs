using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Input;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class StylePage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly TextBox _vocabulary;
    private readonly CardSection _learning = new()
    {
        Title = L("自动学习", "Learning"),
        Footer = L("粘贴后如果你改了某个识别错的词（比如把 TypeList 改成 Typeless），离开输入框或发送后会自动把它加进词汇表。能读取的输入框直接比较前后内容；读不到的会跟随你粘贴后的按键，但用中文、日文、韩文输入法打的字跟随不了。只学发音相近的改动，不学改写、改数字和普通词替换。一切都在本机完成。",
                   "If you fix a misrecognised word after pasting (say TypeList → Typeless), it's added to the vocabulary once you leave the field or send. Where the field can be read, its text is compared; where it can't, the keys you press after the paste are followed, though not text typed with a Chinese, Japanese or Korean input method. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps. All of it stays on this PC."),
    };

    /// <summary>
    /// Translation mode: on or off, the language dictations are translated into, and the key that switches a recording to
    /// translation while the dictation shortcut is held.
    /// </summary>
    private readonly CardSection _translation = new()
    {
        Title = L("翻译", "Translation"),
        Footer = L("说什么语言都可以，插入的是翻译。只需一次模型请求，和整理一样快；翻译不可用时，会改为插入整理后的原文。",
                   "Speak in any language; what goes in is the translation. It takes one model request, as quick as a clean-up. When the translation can't be used, the cleaned-up original goes in instead."),
    };
    private readonly CardRow _translationMode;
    private readonly List<UIElement> _translationDetails = [];
    private readonly List<UIElement> _otherLanguage = [];

    public StylePage() : base(SettingsPage.Style)
    {
        var vocabulary = new CardSection
        {
            Title = L("专有名词 / 词汇表", "Vocabulary"),
            Footer = L("用逗号或换行分隔，比如人名、产品名、项目名。支持的语音模型会把它们当作拼写提示，整理时也会按这里的写法纠正识别错误。",
                       "Comma- or line-separated: names, products, projects. Speech-to-text models that take them get these as spelling hints, and clean-up uses these spellings to fix recognition errors."),
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

        // Translation: the rows past the switch show while it's on, the language's name only for another language.
        var enabled = new ToggleSwitch { IsOn = _settings.TranslationEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        enabled.Toggled += (_, _) => _settings.TranslationEnabled = enabled.IsOn;
        _translationMode = new CardRow { Glyph = Glyphs.Speech, Tint = Tint.Indigo, Title = L("翻译模式", "Translation mode"), Trailing = enabled };
        _translation.Body.Add(_translationMode);

        string[] codes = [.. TranslationLanguages.All.Select(l => l.Code()), AppSettings.OtherTranslationLanguage];
        var language = new ComboBox { MinWidth = 150 };
        foreach (var option in TranslationLanguages.All) language.Items.Add(option.NativeName());
        language.Items.Add(L("其他…", "Other…"));
        language.SelectedIndex = Math.Max(0, Array.IndexOf(codes, _settings.TranslationLanguage));
        language.SelectionChanged += (_, _) => _settings.TranslationLanguage = codes[Math.Max(0, language.SelectedIndex)];
        AddTranslationDetail(new CardDivider());
        AddTranslationDetail(new CardRow { Glyph = Glyphs.Globe, Tint = Tint.Teal, Title = L("翻译成", "Translate into"), Trailing = language });

        var otherName = new TextBox { Text = _settings.TranslationOtherLanguage, Width = 180, PlaceholderText = L("语言", "Language") };
        otherName.TextChanged += (_, _) => _settings.TranslationOtherLanguage = otherName.Text;
        _otherLanguage.Add(new CardDivider());
        _otherLanguage.Add(new CardRow
        {
            Glyph = Glyphs.Font, Tint = Tint.Gray, Title = L("语言", "Language"),
            Subtitle = L("用它的名字，比如 Italiano、繁體中文、Tiếng Việt", "By its name: Italiano, 繁體中文, Tiếng Việt…"),
            Trailing = otherName,
        });
        foreach (var element in _otherLanguage) _translation.Body.Add(element);

        var chords = TranslationChords.All;
        var chord = new ComboBox { MinWidth = 120 };
        foreach (var option in chords) chord.Items.Add(option.Label());
        chord.SelectedIndex = Math.Max(0, Array.IndexOf(chords, _settings.TranslationChord));
        chord.SelectionChanged += (_, _) => _settings.TranslationChord = chords[Math.Max(0, chord.SelectedIndex)];
        AddTranslationDetail(new CardDivider());
        AddTranslationDetail(new CardRow
        {
            Glyph = Glyphs.Keyboard, Tint = Tint.Blue, Title = L("切换键", "Switch key"),
            Subtitle = L("按住听写快捷键时按下它（左右两边都可以）", "Pressed while you hold the dictation shortcut (either side)"),
            Trailing = chord,
        });
        UpdateTranslation();
        Body.Children.Add(_translation);

        _settings.PropertyChanged += OnSettingsChanged;
    }

    /// <summary>A row shown while translation mode is on.</summary>
    private void AddTranslationDetail(UIElement element)
    {
        _translationDetails.Add(element);
        _translation.Body.Add(element);
    }

    /// <summary>Shows the translation rows that apply, and names the switch key in the first row.</summary>
    private void UpdateTranslation()
    {
        var enabled = _settings.TranslationEnabled;
        var key = _settings.TranslationChord.Label();
        _translationMode.Subtitle = L($"按住听写快捷键时再按一下 {key}，这次听写会翻译后再插入",
                                      $"Hold the dictation shortcut and press {key} too: that dictation goes in translated");
        foreach (var element in _translationDetails) element.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        var other = enabled && _settings.TranslationLanguage == AppSettings.OtherTranslationLanguage;
        foreach (var element in _otherLanguage) element.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosed() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.LearnedTerms):
                DispatcherQueue.TryEnqueue(RenderLearning);
                break;
            case nameof(AppSettings.TranslationEnabled) or nameof(AppSettings.TranslationLanguage) or nameof(AppSettings.TranslationChord):
                DispatcherQueue.TryEnqueue(UpdateTranslation);
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
        var date = Ui.Text(learned.Date.ToLocalTime().ToString("d", Formatting.DateCulture), 11, foreground: Ui.Tertiary);
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
