using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Input;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;

namespace OpenTypeless.UI;

/// <summary>
/// Every dictation, newest first and grouped by day, with search; the selected one's text, details and actions on
/// the right. How long recordings are kept sits in the toolbar.
/// </summary>
public sealed class HistoryPage : PageBase
{
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SessionController _controller;
    private readonly AutoSuggestBox _search = new()
    {
        PlaceholderText = L("搜索听写内容", "Search dictations"),
        QueryIcon = new SymbolIcon(Symbol.Find),
        Width = 260,
    };
    private readonly TextBlock _storage = Ui.Text("", 12, foreground: Ui.Secondary);
    private readonly ContentControl _content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private string? _selection;
    private string? _shownDetail;

    public HistoryPage(SessionController controller) : base(SettingsPage.History, scrolls: false)
    {
        _controller = controller;
        // The toolbar is built once, so typing in the search box survives new dictations arriving.
        var grid = new Grid { RowSpacing = 14 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Toolbar());
        Grid.SetRow(_content, 1);
        grid.Children.Add(_content);
        Body.Children.Add(grid);
        _search.TextChanged += (_, _) => Render();
        _history.Changed += OnHistoryChanged;
        Render();
    }

    protected override void OnClosed() => _history.Changed -= OnHistoryChanged;

    private void OnHistoryChanged()
    {
        Render();
        RefreshStorage();
    }

    // MARK: Toolbar

    private Grid Toolbar()
    {
        var options = HistoryRetentionExtensions.All;
        var picker = new ComboBox { MinWidth = 140 };
        foreach (var option in options) picker.Items.Add(option.Label());
        picker.SelectedIndex = Math.Max(0, Array.IndexOf(options, _settings.HistoryRetention));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || options[picker.SelectedIndex] == _settings.HistoryRetention) return;
            _settings.HistoryRetention = options[picker.SelectedIndex];
            _history.ApplyRetention();
            RefreshStorage();
        };
        var storage = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Icon(Glyphs.Folder, 12, Ui.Secondary), _storage },
        };
        ToolTipService.SetToolTip(storage, L("录音和转写占用的空间。每分钟录音约 1.9 MB；转写失败的录音会一直保留，方便重试。",
                                             "Space used by recordings and transcripts. Recordings take about 1.9 MB per minute; failed dictations keep theirs so you can retry."));
        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                storage,
                new TextBlock { Text = L("保留录音", "Keep recordings"), VerticalAlignment = VerticalAlignment.Center },
                picker,
            },
        };
        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_search);
        Grid.SetColumn(trailing, 2);
        toolbar.Children.Add(trailing);
        RefreshStorage();
        return toolbar;
    }

    /// <summary>Recomputes the space history takes (off the UI thread).</summary>
    private async void RefreshStorage()
    {
        var bytes = await Task.Run(HistoryStore.StorageBytes);
        _storage.Text = Formatting.Bytes(bytes);
    }

    // MARK: List

    private List<DictationRecord> Filtered
    {
        get
        {
            var needle = _search.Text.Trim();
            if (needle.Length == 0) return _history.Records.ToList();
            return _history.Records.Where(r => r.FinalText.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                                                || r.RawText.Contains(needle, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
    }

    /// <summary>"Today", "Yesterday", "Fri, Sep 26" (with the year when it isn't this year).</summary>
    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return L("今天", "Today");
        if (day == today.AddDays(-1)) return L("昨天", "Yesterday");
        var weekday = L("周" + "日一二三四五六"[(int)day.DayOfWeek], CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)day.DayOfWeek]);
        var month = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames[day.Month - 1];
        return day.Year == today.Year
            ? L($"{day.Month}月{day.Day}日 {weekday}", $"{weekday}, {month} {day.Day}")
            : L($"{day.Year}年{day.Month}月{day.Day}日", $"{month} {day.Day}, {day.Year}");
    }

    private void Render()
    {
        if (_history.Records.Count == 0)
        {
            _shownDetail = null;
            var hotkey = _settings.Hotkey.DisplayName;
            _content.Content = new StackPanel
            {
                Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Ui.Icon(Glyphs.Microphone, 36, Ui.Tertiary),
                    Ui.Text(L("还没有听写记录", "No dictations yet"), 14, FontWeights.Medium),
                    Ui.Text(L($"按住 {hotkey} 说话，每次听写都会保存在这里。", $"Hold {hotkey} and talk. Every dictation is kept here."), 12, foreground: Ui.Secondary),
                },
            };
            return;
        }

        var records = Filtered;
        var selectedId = _selection != null && records.Any(r => r.Id == _selection) ? _selection : records.FirstOrDefault()?.Id;
        var detailHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

        FrameworkElement listContent;
        if (records.Count == 0)
        {
            var none = Ui.Text(L("没有匹配的听写", "No matching dictations"), 12, foreground: Ui.Secondary);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.VerticalAlignment = VerticalAlignment.Center;
            listContent = none;
        }
        else
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 4, 0, 4) };
            DateTime? currentDay = null;
            foreach (var record in records)
            {
                var day = record.Date.LocalDateTime.Date;
                if (day != currentDay)
                {
                    currentDay = day;
                    // A day header between the rows; it can't be selected or focused.
                    list.Items.Add(new ListViewItem
                    {
                        Content = Ui.Text(DayTitle(day), 11.5, FontWeights.SemiBold, Ui.Secondary),
                        IsHitTestVisible = false,
                        IsTabStop = false,
                        MinHeight = 0,
                        Padding = new Thickness(12, 10, 12, 2),
                    });
                }
                var row = Row(record);
                list.Items.Add(row);
                if (record.Id == selectedId) list.SelectedItem = row;
            }
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is FrameworkElement { Tag: string id } && id != _shownDetail)
                {
                    _selection = id;
                    ShowDetail(detailHost, id);
                }
            };
            listContent = list;
        }

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Card.Make(listContent));
        Grid.SetColumn(detailHost, 1);
        grid.Children.Add(detailHost);
        _content.Content = grid;
        _shownDetail = null;
        ShowDetail(detailHost, selectedId);
    }

    private void ShowDetail(ContentControl host, string? id)
    {
        var record = _history.Records.FirstOrDefault(r => r.Id == id);
        _shownDetail = record?.Id;
        host.Content = record == null ? Card.Make() : Detail(record);
    }

    private static string Preview(DictationRecord record)
    {
        if (record.FinalText.Length > 0) return record.FinalText;
        return record.Status switch
        {
            DictationStatus.Recording => L("正在录音…", "Recording…"),
            DictationStatus.Processing => L("正在处理…", "Processing…"),
            _ => record.Error ?? L("（没有文字）", "(no text)"),
        };
    }

    /// <summary>A list entry: time, duration and a status badge when it isn't simply done, then the start of the text.</summary>
    private static FrameworkElement Row(DictationRecord record)
    {
        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leading = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Ui.Text(record.Date.ToLocalTime().ToString("t"), 11.5, FontWeights.Medium, Ui.Secondary) },
        };
        if (record.Status != DictationStatus.Done)
        {
            leading.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Palette.Brush(record.Status.Tint()), VerticalAlignment = VerticalAlignment.Center });
            leading.Children.Add(Ui.Text(record.Status.Label(), 11, FontWeights.Medium, Palette.Brush(record.Status.Tint())));
        }
        meta.Children.Add(leading);
        var duration = Ui.Text(Formatting.DurationLabel(record.Duration), 11.5, foreground: Ui.Tertiary);
        Grid.SetColumn(duration, 1);
        meta.Children.Add(duration);

        var text = Ui.Text(Preview(record), 13, foreground: record.FinalText.Length == 0 ? Ui.Secondary : null, wrap: true);
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return new StackPanel { Spacing = 3, Padding = new Thickness(0, 7, 0, 7), Tag = record.Id, Children = { meta, text } };
    }

    // MARK: Detail

    /// <summary>
    /// The selected dictation: when, how long, how many words and what it cost; the text itself; the raw transcript
    /// behind an expander when it was cleaned up; and the actions.
    /// </summary>
    private FrameworkElement Detail(DictationRecord record)
    {
        var details = new List<string> { Formatting.DurationLabel(record.Duration) };
        var words = WordCount.Count(record.FinalText);
        if (words > 0) details.Add(L($"{UsageFormat.Count(words)} 字", $"{UsageFormat.Count(words)} words"));
        if (record.Cost is { } cost) details.Add(UsageFormat.Money(cost));

        var header = new Grid { Padding = new Thickness(16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Ui.Text(record.Date.ToLocalTime().ToString("f"), 14, FontWeights.SemiBold),
                Ui.Text(string.Join(" · ", details), 11.5, foreground: Ui.Secondary),
            },
        });
        var pill = new StatusPill(record.Status.Label(), record.Status.Tint()) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);

        var body = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        if (record.Error is { } error) body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange, error));
        if (record.FinalText.Length == 0 && record.Status is DictationStatus.Recording or DictationStatus.Processing)
        {
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 40),
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                    Ui.Text(record.Status == DictationStatus.Recording ? L("正在录音…", "Recording…") : L("正在处理…", "Processing…"),
                            13, foreground: Ui.Secondary),
                },
            });
        }
        else
        {
            var text = Ui.Text(record.FinalText.Length == 0 ? L("（没有文字）", "(no text)") : record.FinalText, 14,
                               foreground: record.FinalText.Length == 0 ? Ui.Secondary : null, wrap: true);
            text.IsTextSelectionEnabled = true;
            text.LineHeight = 22;
            body.Children.Add(text);
            if (record.PolishedText != null && record.RawText.Length > 0)
            {
                var raw = Ui.Text(record.RawText, 12.5, foreground: Ui.Secondary, wrap: true);
                raw.IsTextSelectionEnabled = true;
                body.Children.Add(new Expander
                {
                    Header = L("原始转写", "Raw transcript"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = raw,
                });
            }
        }
        if (record.Timing?.Summary is { } timing)
        {
            // Where the wait after releasing the key went.
            var line = Ui.Text(timing, 11, foreground: Ui.Tertiary, wrap: true);
            line.IsTextSelectionEnabled = true;
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Ui.Icon(Glyphs.Stopwatch, 11, Ui.Tertiary), line },
            });
        }

        var buttons = new Grid { ColumnSpacing = 8, Padding = new Thickness(16, 12, 16, 12) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copyLabel = Ui.Text(L("复制", "Copy"), 13);
        var copyIcon = Ui.Icon(Glyphs.Copy, 13);
        var copy = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copyIcon, copyLabel } },
            IsEnabled = record.FinalText.Length > 0,
        };
        copy.Click += async (_, _) =>
        {
            TextInserter.CopyToClipboard(record.FinalText);
            copyLabel.Text = L("已复制", "Copied");
            copyIcon.Glyph = Glyphs.CheckMark;
            await Task.Delay(1500);
            copyLabel.Text = L("复制", "Copy");
            copyIcon.Glyph = Glyphs.Copy;
        };
        leading.Children.Add(copy);
        if (record.HasAudio)
        {
            var retry = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Ui.Icon(Glyphs.Refresh, 13), Ui.Text(L("重新转写", "Re-transcribe"), 13) },
                },
                IsEnabled = _controller.State == SessionController.SessionState.Idle,
            };
            ToolTipService.SetToolTip(retry, L("完成后复制到剪贴板", "Copies the result to the clipboard"));
            retry.Click += (_, _) => _controller.Retry(record, paste: false);
            leading.Children.Add(retry);
            var folder = new Button { Content = Ui.Icon(Glyphs.Folder, 14) };
            Ui.Label(folder, L("在文件资源管理器中显示录音", "Show recording in File Explorer"));
            folder.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{record.AudioPath}\"");
            leading.Children.Add(folder);
        }
        buttons.Children.Add(leading);
        var delete = new Button { Content = Ui.Icon(Glyphs.Delete, 14, Palette.Brush(Tint.Red)) };
        Ui.Label(delete, L("删除", "Delete"));
        delete.Click += (_, _) =>
        {
            _selection = null;
            _history.Delete(record);
        };
        Grid.SetColumn(delete, 2);
        buttons.Children.Add(delete);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(header);
        var topDivider = new CardDivider { Inset = 0 };
        Grid.SetRow(topDivider, 1);
        layout.Children.Add(topDivider);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        var bottomDivider = new CardDivider { Inset = 0 };
        Grid.SetRow(bottomDivider, 3);
        layout.Children.Add(bottomDivider);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);
        return Card.Make(layout);
    }
}
