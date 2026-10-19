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
    private readonly SettingsWindow _window;
    private readonly TextBox _vocabulary;
    private readonly DropDownButton _export = new();
    private readonly TextBlock _exportLabel = Ui.Text(L("导出", "Export"), 13);
    private readonly FontIcon _exportIcon = Ui.Icon(Glyphs.Export, 13);
    private readonly CardSection _learning = new()
    {
        Title = L("自动学习", "Learning"),
        Footer = L("粘贴后如果你改了某个识别错的词（比如把 TypeList 改成 Typeless），离开输入框或发送后会自动把它加进词汇表。能读取的输入框直接比较前后内容；读不到的会跟随你粘贴后的按键，但用中文、日文、韩文输入法打的字跟随不了。只学发音相近的改动，不学改写、改数字和普通词替换。一切都在本机完成。",
                   "If you fix a misrecognised word after pasting (say TypeList → Typeless), it's added to the vocabulary once you leave the field or send. Where the field can be read, its text is compared; where it can't, the keys you press after the paste are followed, though not text typed with a Chinese, Japanese or Korean input method. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps. All of it stays on this PC."),
    };

    public StylePage(SettingsWindow window) : base(SettingsPage.Style)
    {
        _window = window;
        var vocabulary = new CardSection
        {
            Title = L("专有名词 / 词汇表", "Vocabulary"),
            Footer = L("用逗号或换行分隔，比如人名、产品名、项目名。支持的语音模型会把它们当作拼写提示，整理时也会按这里的写法纠正识别错误。",
                       "Comma- or line-separated: names, products, projects. Speech-to-text models that take them get these as spelling hints, and clean-up uses these spellings to fix recognition errors."),
        };
        _vocabulary = Editor(_settings.Vocabulary, 110, text => _settings.Vocabulary = text);
        vocabulary.Body.Add(_vocabulary);
        vocabulary.Body.Add(new CardDivider { Inset = 0 });
        vocabulary.Body.Add(TransferButtons());
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
                    _export.IsEnabled = _settings.VocabularyList.Count > 0;
                });
                break;
        }
    }

    // MARK: Import and export

    private StackPanel TransferButtons()
    {
        var import = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Ui.Icon(Glyphs.Import, 13), Ui.Text(L("导入…", "Import…"), 13) },
            },
        };
        ToolTipService.SetToolTip(import, L("从 CSV 或文本文件添加词汇，已有的词不会重复添加", "Add terms from a CSV or text file; ones you already have aren't added twice"));
        import.Click += async (_, _) => await ImportVocabulary();

        var asCsv = new MenuFlyoutItem { Text = L("CSV（含学到的误听写法）…", "CSV (with how learned terms were misheard)…") };
        asCsv.Click += async (_, _) => await ExportVocabulary(csv: true);
        var asText = new MenuFlyoutItem { Text = L("纯文本（每行一个词）…", "Plain text (one term per line)…") };
        asText.Click += async (_, _) => await ExportVocabulary(csv: false);
        _export.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _exportIcon, _exportLabel } };
        _export.Flyout = new MenuFlyout { Items = { asCsv, asText } };
        _export.IsEnabled = _settings.VocabularyList.Count > 0;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 8, 12, 8),
            Children = { import, _export },
        };
    }

    /// <summary>Reads the file, then asks before adding what's new to the vocabulary.</summary>
    private async Task ImportVocabulary()
    {
        var path = await Exports.ChooseFile(this, _window.Hwnd, "csv", "txt", "tsv");
        if (path == null) return;
        var (terms, failure) = await Exports.ReadVocabulary(path);
        if (terms == null)
        {
            await Exports.ShowDialog(XamlRoot, L("无法导入", "Couldn't import"), failure ?? "");
            return;
        }
        if (terms.Count == 0)
        {
            await Exports.ShowDialog(XamlRoot, L("文件里没有词", "No terms in this file"),
                                     L("每行一个词，或用逗号分隔；CSV 文件取第一列。",
                                       "Put one term per line or separate them with commas; from a CSV file, the first column is used."));
            return;
        }
        var added = VocabularyTransfer.NewTerms(terms, _settings.VocabularyList);
        if (added.Count == 0)
        {
            // Nothing to add, but importing still means the user wants these terms (see AppSettings.ImportVocabulary).
            _settings.ImportVocabulary(terms);
            var count = DistinctCount(terms);
            await Exports.ShowDialog(XamlRoot, L("没有新词可导入", "Nothing new to import"),
                                     count == 1
                                         ? L("文件里的词已经在词汇表里了。", "The term in the file is already in your vocabulary.")
                                         : L($"文件里的 {count} 个词都已经在词汇表里了。", $"All {count} terms in the file are already in your vocabulary."));
            return;
        }
        var title = added.Count == 1 ? L("导入 1 个新词？", "Import 1 new term?") : L($"导入 {added.Count} 个新词？", $"Import {added.Count} new terms?");
        if (await Exports.ShowDialog(XamlRoot, title, ImportSummary(terms, added), primary: L("导入", "Import"))) _settings.ImportVocabulary(terms);
    }

    /// <summary>The first few new terms, and how many of the file's terms are already in the vocabulary.</summary>
    private static string ImportSummary(List<string> terms, List<string> added)
    {
        var summary = string.Join(L("、", ", "), added.Take(10)) + (added.Count > 10 ? "…" : "");
        var known = DistinctCount(terms) - added.Count;
        if (known > 0)
        {
            summary += "\n\n" + (known == 1 ? L("另有 1 个已经在词汇表里。", "1 more is already in your vocabulary.")
                                            : L($"另有 {known} 个已经在词汇表里。", $"{known} more are already in your vocabulary."));
        }
        return summary;
    }

    /// <summary>Terms counted once however often the file repeats them, compared the way the vocabulary is.</summary>
    private static int DistinctCount(List<string> terms) => terms.Select(t => t.ToLowerInvariant()).Distinct().Count();

    private async Task ExportVocabulary(bool csv)
    {
        var terms = _settings.VocabularyForExport;
        var saved = await Exports.Save(this, _window.Hwnd, L("OpenTypeless 词汇表", "OpenTypeless Vocabulary"),
                                       csv ? "CSV (UTF-8)" : L("纯文本", "Plain text"), csv ? "csv" : "txt",
                                       () => VocabularyTransfer.Data(terms, csv), L("导出失败", "Couldn't export"));
        if (!saved) return;
        _exportLabel.Text = L("已导出", "Exported");
        _exportIcon.Glyph = Glyphs.CheckMark;
        await Task.Delay(1500);
        _exportLabel.Text = L("导出", "Export");
        _exportIcon.Glyph = Glyphs.Export;
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
