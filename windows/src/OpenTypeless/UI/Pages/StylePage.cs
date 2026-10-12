using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private readonly CardSection _snippets = new()
    {
        Title = L("快捷短语", "Snippets"),
        Footer = L("说出触发词，插入时会换成你写好的内容，一字不改，句子中间说也行。触发词会作为词汇提示交给语音识别和整理，让它原样保留。最好选一个平时不会说的说法，免得误替换。",
                   "Say a trigger and the text you wrote goes in instead, word for word, even mid-sentence. Triggers go to speech-to-text and clean-up as vocabulary, so they come through intact. Pick a phrase you wouldn't otherwise say, so it isn't replaced by accident."),
    };
    /// <summary>The snippet open in the editor; a new one isn't in the list until it's saved.</summary>
    private Snippet? _editing;
    /// <summary>What the editor holds, kept apart from its text boxes so a redraw of the list doesn't lose it.</summary>
    private string _draftTrigger = "", _draftExpansion = "";

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

        RenderSnippets();
        Body.Children.Add(_snippets);

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

    // MARK: Snippets

    /// <summary>The snippets, each opening into an editor in place, and a new one added at the end.</summary>
    private void RenderSnippets()
    {
        _snippets.Body.Clear();
        var snippets = _settings.Snippets;
        if (snippets.Count == 0 && _editing == null)
        {
            var first = new Button { Content = L("添加快捷短语", "Add Snippet") };
            first.Click += (_, _) => Edit(new Snippet());
            _snippets.Body.Add(new CardRow
            {
                Glyph = Glyphs.Message, Tint = Tint.Teal, Title = L("还没有快捷短语", "No snippets yet"),
                Subtitle = L("例如：说「我的邮箱」，插入 name@example.com", "For example, say “my email” to insert name@example.com"),
                Trailing = first,
            });
            return;
        }
        for (var i = 0; i < snippets.Count; i++)
        {
            if (i > 0) _snippets.Body.Add(new CardDivider { Inset = 14 });
            _snippets.Body.Add(_editing?.Id == snippets[i].Id ? SnippetEditor(snippets[i], snippets) : SnippetRow(snippets[i]));
        }
        if (_editing is { } editing && snippets.All(s => s.Id != editing.Id))
        {
            if (snippets.Count > 0) _snippets.Body.Add(new CardDivider { Inset = 14 });
            _snippets.Body.Add(SnippetEditor(editing, snippets));
            return;
        }
        _snippets.Body.Add(new CardDivider { Inset = 14 });
        var add = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { Ui.Icon(Glyphs.Add, 12), Ui.Text(L("添加快捷短语", "Add Snippet"), 13) },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        add.Click += (_, _) => Edit(new Snippet());
        _snippets.Body.Add(add);
    }

    private void Edit(Snippet snippet)
    {
        _editing = snippet;
        _draftTrigger = snippet.Trigger;
        _draftExpansion = snippet.Expansion;
        RenderSnippets();
    }

    private FrameworkElement SnippetRow(Snippet snippet)
    {
        var trigger = Ui.Text(snippet.Trigger, 13, FontWeights.Medium);
        trigger.TextTrimming = TextTrimming.CharacterEllipsis;
        var preview = Ui.Text(string.Join(" ↵ ", snippet.Expansion.Split('\n', StringSplitOptions.RemoveEmptyEntries)), 11.5,
                              foreground: Ui.Secondary);
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        preview.MaxLines = 1;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { trigger, preview } };
        var edit = IconButton(Glyphs.Edit, L("编辑快捷短语", "Edit snippet"));
        edit.Click += (_, _) => Edit(snippet);
        var delete = IconButton(Glyphs.Cancel, L("删除", "Delete"));
        delete.Click += (_, _) =>
        {
            if (_editing?.Id == snippet.Id) _editing = null;
            _settings.DeleteSnippet(snippet);
            RenderSnippets();
        };

        var row = new Grid { Padding = new Thickness(14, 6, 10, 6), ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        Grid.SetColumn(edit, 1);
        row.Children.Add(edit);
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        return row;
    }

    /// <summary>A snippet being written: its trigger, its expansion (several lines if need be), and why it can't be saved yet.</summary>
    private FrameworkElement SnippetEditor(Snippet original, List<Snippet> snippets)
    {
        var trigger = new TextBox
        {
            Text = _draftTrigger,
            PlaceholderText = L("触发词，比如：我的邮箱", "Trigger, e.g. my email"),
            MaxLength = SnippetExpander.MaxTriggerLength,
        };
        var expansion = new TextBox
        {
            Text = _draftExpansion,
            PlaceholderText = L("替换成的内容，可以多行", "What goes in instead (can be several lines)"),
            MaxLength = SnippetExpander.MaxExpansionLength,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(expansion, ScrollBarVisibility.Auto);
        var taken = Ui.Text(L("已经有这个触发词了", "That trigger is already in use"), 11.5, foreground: Palette.Brush(Tint.Orange));
        taken.VerticalAlignment = VerticalAlignment.Center;
        var cancel = new Button { Content = L("取消", "Cancel") };
        var save = new Button { Content = L("保存", "Save"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

        // Another snippet already has this trigger (compared the way dictations are searched for it).
        void Update()
        {
            var key = SnippetExpander.Key(_draftTrigger);
            var isTaken = key.Length > 0 && snippets.Any(s => s.Id != original.Id && SnippetExpander.Key(s.Trigger) == key);
            taken.Visibility = isTaken ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = key.Length > 0 && _draftExpansion.Trim().Length > 0 && !isTaken;
        }
        trigger.TextChanged += (_, _) =>
        {
            _draftTrigger = trigger.Text;
            Update();
        };
        expansion.TextChanged += (_, _) =>
        {
            _draftExpansion = Normalize(expansion.Text);
            Update();
        };
        cancel.Click += (_, _) =>
        {
            _editing = null;
            RenderSnippets();
        };
        save.Click += (_, _) =>
        {
            _editing = null;
            _settings.SaveSnippet(original with { Trigger = _draftTrigger.Trim(), Expansion = _draftExpansion.Trim() });
            RenderSnippets();
        };
        Update();

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(taken);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        return new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 10), Children = { trigger, expansion, footer } };
    }

    /// <summary>A borderless button showing only a glyph, named for Narrator.</summary>
    private static Button IconButton(string glyph, string label)
    {
        var button = new Button
        {
            Content = Ui.Icon(glyph, 11, Ui.Secondary),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.Label(button, label);
        return button;
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
