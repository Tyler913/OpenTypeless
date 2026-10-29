using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
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
    private readonly DispatcherQueueTimer _searchDelay;
    /// <summary>
    /// The list is bound to <see cref="_entries"/> and updated in place, so only the rows on screen exist, a new or
    /// changed dictation redraws just its row, and the list keeps its scroll position and selection.
    /// </summary>
    private readonly ListView _list;
    private ObservableCollection<Entry> _entries = [];
    private readonly Border _listCard = Card.Make();
    private readonly TextBlock _noMatches = Ui.Text(L("没有匹配的听写", "No matching dictations"), 12, foreground: Ui.Secondary);
    private readonly ContentControl _detailHost = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly Grid _split = new() { ColumnSpacing = 14 };
    /// <summary>Set while the page itself changes the list, whose selection changes then aren't the user's.</summary>
    private bool _syncing;
    private bool _confirming;
    private string? _selection;
    /// <summary>What the detail was built from, so it's only rebuilt when that changes.</summary>
    private (DictationRecord? Record, bool HasAudio, bool Idle)? _shownDetail;

    public HistoryPage(SessionController controller) : base(SettingsPage.History, scrolls: false)
    {
        _controller = controller;
        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Padding = new Thickness(0, 4, 0, 4),
            // An empty holder; each row's content is filled in when it comes on screen (OnContainerContentChanging).
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Border/></DataTemplate>"),
        };
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.SelectionChanged += OnSelectionChanged;
        _noMatches.HorizontalAlignment = HorizontalAlignment.Center;
        _noMatches.VerticalAlignment = VerticalAlignment.Center;
        _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _split.Children.Add(_listCard);
        Grid.SetColumn(_detailHost, 1);
        _split.Children.Add(_detailHost);

        // The toolbar is built once, so typing in the search box survives new dictations arriving.
        var grid = new Grid { RowSpacing = 14 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Toolbar());
        Grid.SetRow(_content, 1);
        grid.Children.Add(_content);
        Body.Children.Add(grid);
        // A new search rebuilds the whole list, so a burst of typing does that once, when it pauses.
        _searchDelay = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _searchDelay.Interval = TimeSpan.FromMilliseconds(150);
        _searchDelay.IsRepeating = false;
        _searchDelay.Tick += (_, _) => Render();
        _search.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        _history.Changed += OnHistoryChanged;
        Render();
    }

    protected override void OnClosed()
    {
        _searchDelay.Stop();
        _history.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged()
    {
        Update();
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
        var dates = Formatting.DateCulture.DateTimeFormat;
        if (day.Year != today.Year) return day.ToString(dates.ShortDatePattern, dates);
        var weekday = dates.AbbreviatedDayNames[(int)day.DayOfWeek];
        var monthDay = day.ToString(dates.MonthDayPattern.Replace("MMMM", "MMM"), dates);
        return L($"{monthDay} {weekday}", $"{weekday}, {monthDay}");
    }

    /// <summary>Shows the history from scratch: when the page opens and when the search changes.</summary>
    private void Render()
    {
        if (_history.Records.Count == 0)
        {
            ShowEmpty();
            return;
        }
        var records = Filtered;
        _listCard.Child = records.Count == 0 ? _noMatches : (UIElement)_list;
        _syncing = true;
        _entries = new ObservableCollection<Entry>(Entries(records));
        _list.ItemsSource = _entries;
        _syncing = false;
        _content.Content = _split;
        SelectAndShow(records);
    }

    /// <summary>Brings the list up to date after a change to the history, changing only the entries that differ.</summary>
    private void Update()
    {
        if (_history.Records.Count == 0 || !ReferenceEquals(_content.Content, _split))
        {
            Render();
            return;
        }
        var records = Filtered;
        _listCard.Child = records.Count == 0 ? _noMatches : (UIElement)_list;
        _syncing = true;
        Sync(_entries, Entries(records));
        _syncing = false;
        SelectAndShow(records);
    }

    private void ShowEmpty()
    {
        _shownDetail = null;
        _detailHost.Content = null;
        _syncing = true;
        _entries = [];
        _list.ItemsSource = _entries;
        _syncing = false;
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
    }

    /// <summary>Selects the dictation the user picked, or the newest one when it's gone (or none was picked), and shows it.</summary>
    private void SelectAndShow(List<DictationRecord> records)
    {
        var selectedId = _selection != null && records.Any(r => r.Id == _selection) ? _selection : records.FirstOrDefault()?.Id;
        var entry = selectedId == null ? null : _entries.FirstOrDefault(e => e.Record?.Id == selectedId);
        if (!ReferenceEquals(_list.SelectedItem, entry))
        {
            _syncing = true;
            _list.SelectedItem = entry;
            _syncing = false;
        }
        ShowDetail(entry?.Record);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _list.SelectedItem is not Entry { Record: { } record }) return;
        _selection = record.Id;
        ShowDetail(record);
    }

    private void ShowDetail(DictationRecord? record)
    {
        // Rebuilt when the record changed (records are replaced, not edited), lost its recording, or the retry
        // button's state would differ.
        (DictationRecord? Record, bool HasAudio, bool Idle) shown =
            (record, record?.HasAudio ?? false, _controller.State == SessionController.SessionState.Idle);
        if (_shownDetail == shown) return;
        _shownDetail = shown;
        _detailHost.Content = record == null ? Card.Make() : Detail(record);
    }

    /// <summary>An item in the list: a day's header, or a dictation under it.</summary>
    private sealed class Entry
    {
        public string Key { get; }
        /// <summary>The header's text ("Today", "Fri, Sep 26"); null for a dictation.</summary>
        public string? Title { get; }
        public DictationRecord? Record { get; }

        private Entry(string key, string? title, DictationRecord? record)
        {
            Key = key;
            Title = title;
            Record = record;
        }

        public static Entry Header(DateTime day) => new("day " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DayTitle(day), null);

        public static Entry Of(DictationRecord record) => new(record.Id, null, record);

        /// <summary>Looks the same on screen. The store replaces a record when it's updated, so a changed one is a new object.</summary>
        public bool LooksLike(Entry other) => ReferenceEquals(Record, other.Record) && Title == other.Title;
    }

    /// <summary>The records with a header before each day's first one.</summary>
    private static List<Entry> Entries(List<DictationRecord> records)
    {
        var entries = new List<Entry>(records.Count + 32);
        DateTime? currentDay = null;
        foreach (var record in records)
        {
            var day = record.Date.LocalDateTime.Date;
            if (day != currentDay)
            {
                currentDay = day;
                entries.Add(Entry.Header(day));
            }
            entries.Add(Entry.Of(record));
        }
        return entries;
    }

    /// <summary>
    /// Turns <paramref name="entries"/> into <paramref name="wanted"/> one insert, removal or replacement at a time:
    /// a new dictation is one insert, an update one replacement, and the rest of the list is left alone.
    /// </summary>
    private static void Sync(ObservableCollection<Entry> entries, List<Entry> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var entry = wanted[i];
            if (i >= entries.Count || entries[i].Key != entry.Key)
            {
                var found = -1;
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (entries[j].Key == entry.Key)
                    {
                        found = j;
                        break;
                    }
                }
                if (found < 0)
                {
                    entries.Insert(i, entry);
                    continue;
                }
                // Whatever sits before it in the list is no longer wanted there.
                for (var j = found - 1; j >= i; j--) entries.RemoveAt(j);
            }
            if (!entries[i].LooksLike(entry)) entries[i] = entry;
        }
        while (entries.Count > wanted.Count) entries.RemoveAt(entries.Count - 1);
    }

    /// <summary>Fills a row as it comes on screen. Rows are reused for other entries, so each one sets everything it needs.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not Entry entry || args.ItemContainer.ContentTemplateRoot is not Border holder) return;
        var container = args.ItemContainer;
        if (entry.Record is { } record)
        {
            container.ClearValue(UIElement.IsHitTestVisibleProperty);
            container.ClearValue(Control.IsTabStopProperty);
            container.ClearValue(FrameworkElement.MinHeightProperty);
            container.ClearValue(Control.PaddingProperty);
            holder.Child = Row(record);
        }
        else
        {
            // A day header between the rows; it can't be selected or focused.
            container.IsHitTestVisible = false;
            container.IsTabStop = false;
            container.MinHeight = 0;
            container.Padding = new Thickness(12, 10, 12, 2);
            holder.Child = Ui.Text(entry.Title ?? "", 11.5, FontWeights.SemiBold, Ui.Secondary);
        }
        args.Handled = true;
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
            Children = { Ui.Text(record.Date.ToLocalTime().ToString("t", Formatting.DateCulture), 11.5, FontWeights.Medium, Ui.Secondary) },
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
        return new StackPanel { Spacing = 3, Padding = new Thickness(0, 7, 0, 7), Children = { meta, text } };
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
        // The app profile it was made with, if any.
        if (record.ProfileName is { } profile) details.Add(profile);

        var header = new Grid { Padding = new Thickness(16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Ui.Text(record.Date.ToLocalTime().ToString("f", Formatting.DateCulture), 14, FontWeights.SemiBold),
                Ui.Text(string.Join(" · ", details), 11.5, foreground: Ui.Secondary),
            },
        });
        var pill = new StatusPill(record.Status.Label(), record.Status.Tint()) { VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);

        var body = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        if (record.Status == DictationStatus.Cancelled)
        {
            var until = Formatting.ShortStamp(CancelPolicy.Expiry(record.Date));
            body.Children.Add(new Banner(Glyphs.Cancel, Tint.Gray,
                L($"已取消，没有插入。这条会保留到 {until}，之前可以复制或重新转写。",
                  $"Cancelled, so nothing was inserted. It's kept until {until} to copy or re-transcribe.")));
        }
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
        delete.Click += async (_, _) =>
        {
            // Text alone goes straight away; a recording can't be got back, so that asks first.
            if (record.HasAudio && !await ConfirmDelete(record)) return;
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

    private async Task<bool> ConfirmDelete(DictationRecord record)
    {
        // One dialog at a time: a second ShowAsync while one is open would throw.
        if (_confirming || XamlRoot == null) return false;
        _confirming = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L("删除这条听写？", "Delete this dictation?"),
                Content = record.Status == DictationStatus.Failed
                    ? L("这次听写还没转写成功，删除后录音也会删掉，无法再重试。",
                        "It was never transcribed. Its recording is deleted too, so it can't be retried.")
                    : L("录音也会一起删除，之后无法重新转写。",
                        "Its recording is deleted too, so it can't be re-transcribed."),
                PrimaryButtonText = L("删除", "Delete"),
                CloseButtonText = L("取消", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _confirming = false;
        }
    }
}
