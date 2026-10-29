using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Audio;
using OpenTypeless.Input;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenTypeless.UI;

/// <summary>
/// Every dictation, newest first and grouped by day, with search; the selected one's text, details and actions on
/// the right. How long recordings are kept, and exporting what the list shows, sit in the toolbar, next to transcribing
/// a file (also by dropping one on the page).
/// </summary>
public sealed class HistoryPage : PageBase
{
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SettingsWindow _window;
    private readonly SessionController _controller;
    /// <summary>Just the icon, with "Export" as its tooltip: the toolbar has no room for a label next to the retention picker.</summary>
    private readonly DropDownButton _export = new();
    private readonly FontIcon _exportIcon = Ui.Icon(Glyphs.Export, 14);
    private bool _exporting;
    private readonly FileTranscriber _files;
    private readonly AutoSuggestBox _search = new()
    {
        PlaceholderText = L("搜索听写内容", "Search dictations"),
        QueryIcon = new SymbolIcon(Symbol.Find),
        MinWidth = 120,
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
    /// <summary>A dialog is open (one at a time: a second ShowAsync while one is open would throw).</summary>
    private bool _confirming;
    private string? _selection;
    /// <summary>What the detail was built from, so it's only rebuilt when that changes.</summary>
    private (DictationRecord? Record, bool HasAudio, bool Idle, FileTranscription.Progress? Progress)? _shownDetail;
    /// <summary>The detail's progress line and bar while its file is under way, moved on in place as the file gets further.</summary>
    private (TextBlock Label, ProgressBar Bar)? _detailProgress;

    public HistoryPage(SettingsWindow window, SessionController controller) : base(SettingsPage.History, scrolls: false)
    {
        _window = window;
        _controller = controller;
        _files = controller.Files;
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
        _files.Changed += Update;
        // Audio and video files dropped anywhere on the page are offered for transcribing.
        AllowDrop = true;
        // A transparent background, so a drop over the page's empty space reaches it too.
        if (Content is Panel root) root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DragOver += OnDragOver;
        Drop += OnDrop;
        Render();
    }

    protected override void OnClosed()
    {
        _searchDelay.Stop();
        _history.Changed -= OnHistoryChanged;
        _files.Changed -= Update;
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
                ExportButton(),
            },
        };
        var transcribe = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Ui.Icon(Glyphs.OpenFile, 13), Ui.Text(L("转写文件…", "Transcribe File…"), 13) },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(transcribe, L("转写语音备忘录、会议录音或视频（也可以直接拖到这里）",
                                                "Transcribe a voice memo, meeting recording or video (or drop it here)"));
        transcribe.Click += async (_, _) => await PickFiles();
        var leadingTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _search, transcribe } };
        var toolbar = new Grid { ColumnSpacing = 12 };
        // The search box gives way when the window is narrow or the labels are long, up to 260 wide otherwise (like the
        // macOS toolbar), rather than pushing Export off the edge.
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MaxWidth = 260 });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(leadingTools);
        Grid.SetColumn(trailing, 2);
        toolbar.Children.Add(trailing);
        RefreshStorage();
        return toolbar;
    }

    /// <summary>Exports what the list shows: everything, or what the search found.</summary>
    private DropDownButton ExportButton()
    {
        _export.Content = _exportIcon;
        Ui.Label(_export, L("导出", "Export"));
        var flyout = new MenuFlyout();
        // Filled as it opens, with the list as it is then.
        flyout.Opening += (_, _) =>
        {
            var records = Filtered;
            flyout.Items.Clear();
            flyout.Items.Add(new MenuFlyoutItem { Text = ExportTitle(records.Count, searching: records.Count != _history.Records.Count), IsEnabled = false });
            flyout.Items.Add(new MenuFlyoutSeparator());
            (HistoryExport.Format Format, string Text)[] choices =
            [
                (HistoryExport.Format.Csv, L("CSV（可用 Excel 打开）…", "CSV (opens in Excel)…")),
                (HistoryExport.Format.Json, "JSON…"),
                (HistoryExport.Format.Markdown, "Markdown…"),
            ];
            foreach (var (format, text) in choices)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += async (_, _) => await Export(records, format);
                flyout.Items.Add(item);
            }
        };
        _export.Flyout = flyout;
        return _export;
    }

    private static string ExportTitle(int count, bool searching)
    {
        var n = UsageFormat.Count(count);
        if (searching)
        {
            return count == 1 ? L("导出搜索到的 1 条听写", "Export the 1 matching dictation") : L($"导出搜索到的 {n} 条听写", $"Export the {n} matching dictations");
        }
        return count == 1 ? L("导出 1 条听写", "Export 1 dictation") : L($"导出全部 {n} 条听写", $"Export all {n} dictations");
    }

    private async Task Export(List<DictationRecord> records, HistoryExport.Format format)
    {
        if (_exporting) return;
        // What the list showed when Export was chosen, even if a dictation arrives while the save dialog is open.
        var entries = records.Select(r => r.ExportEntry()).ToList();
        _exporting = true;
        _export.IsEnabled = false;
        bool saved;
        try
        {
            var typeLabel = format switch
            {
                HistoryExport.Format.Csv => "CSV (UTF-8)",
                HistoryExport.Format.Json => "JSON",
                _ => "Markdown",
            };
            saved = await Exports.Save(this, _window.Hwnd, Exports.FileName(L("OpenTypeless 历史记录", "OpenTypeless History"), DateTimeOffset.Now),
                                       typeLabel, format.FileExtension(), () => HistoryExport.Data(entries, format), L("导出失败", "Couldn't export"));
        }
        finally
        {
            _exporting = false;
            _export.IsEnabled = Filtered.Count > 0;
        }
        if (!saved) return;
        _exportIcon.Glyph = Glyphs.CheckMark;
        await Task.Delay(1500);
        _exportIcon.Glyph = Glyphs.Export;
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
            return _history.Records.Where(r => r.OutputText.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                                                || r.FinalText.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                                                || r.RawText.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                                                || r.SourceName?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) == true).ToList();
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
        _export.IsEnabled = records.Count > 0 && !_exporting;
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
        _export.IsEnabled = records.Count > 0 && !_exporting;
        _syncing = true;
        Sync(_entries, Entries(records));
        _syncing = false;
        SelectAndShow(records);
    }

    private void ShowEmpty()
    {
        _export.IsEnabled = false;
        _shownDetail = null;
        _detailProgress = null;
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
                Ui.Text(L("也可以把音频或视频文件拖到这里转写。", "You can also drop audio or video files here to transcribe them."), 12, foreground: Ui.Secondary),
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
        // Rebuilt when the record changed (records are replaced, not edited), lost its recording, the retry button's
        // state would differ, or its file has got further.
        (DictationRecord? Record, bool HasAudio, bool Idle, FileTranscription.Progress? Progress) shown =
            (record, record?.HasAudio ?? false, _controller.State == SessionController.SessionState.Idle,
             record == null ? null : _files.Progress(record.Id));
        if (_shownDetail == shown) return;
        // Only the file's progress moved: the line and bar are updated, not the whole detail rebuilt (no flicker).
        if (_shownDetail is { } before && ReferenceEquals(before.Record, shown.Record) && before.HasAudio == shown.HasAudio
            && before.Idle == shown.Idle && before.Progress != null && shown.Progress is { } moved && _detailProgress is { } line)
        {
            _shownDetail = shown;
            line.Label.Text = moved.Label;
            line.Bar.IsIndeterminate = moved.Fraction == null;
            line.Bar.Value = moved.Fraction ?? 0;
            return;
        }
        _shownDetail = shown;
        _detailProgress = null;
        _detailHost.Content = record == null ? Card.Make() : Detail(record);
    }

    /// <summary>An item in the list: a day's header, or a dictation under it.</summary>
    private sealed class Entry
    {
        public string Key { get; }
        /// <summary>The header's text ("Today", "Fri, Sep 26"); null for a dictation.</summary>
        public string? Title { get; }
        public DictationRecord? Record { get; }
        /// <summary>How far a file being transcribed has got.</summary>
        public FileTranscription.Progress? Progress { get; }

        private Entry(string key, string? title, DictationRecord? record, FileTranscription.Progress? progress = null)
        {
            Key = key;
            Title = title;
            Record = record;
            Progress = progress;
        }

        public static Entry Header(DateTime day) => new("day " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), DayTitle(day), null);

        public static Entry Of(DictationRecord record, FileTranscription.Progress? progress) => new(record.Id, null, record, progress);

        /// <summary>Looks the same on screen. The store replaces a record when it's updated, so a changed one is a new object.</summary>
        public bool LooksLike(Entry other) => ReferenceEquals(Record, other.Record) && Title == other.Title && Equals(Progress, other.Progress);
    }

    /// <summary>The records with a header before each day's first one.</summary>
    private List<Entry> Entries(List<DictationRecord> records)
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
            entries.Add(Entry.Of(record, _files.Progress(record.Id)));
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
            holder.Child = Row(record, entry.Progress);
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

    /// <summary>An earlier form of the text, folded away under <paramref name="title"/>.</summary>
    private static Expander Disclosure(string title, string text)
    {
        var content = Ui.Text(text, 12.5, foreground: Ui.Secondary, wrap: true);
        content.IsTextSelectionEnabled = true;
        return new Expander
        {
            Header = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
    }

    private static string Preview(DictationRecord record)
    {
        if (record.OutputText.Length > 0) return record.OutputText;
        return record.Status switch
        {
            DictationStatus.Recording => L("正在录音…", "Recording…"),
            DictationStatus.Processing => L("正在处理…", "Processing…"),
            _ => record.Error ?? L("（没有文字）", "(no text)"),
        };
    }

    /// <summary>
    /// A list entry: time, duration and a status badge when it isn't simply done, then the start of the text. A file shows
    /// its name, and how far it has got while it's being transcribed.
    /// </summary>
    private static FrameworkElement Row(DictationRecord record, FileTranscription.Progress? progress)
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

        var row = new StackPanel { Spacing = 3, Padding = new Thickness(0, 7, 0, 7), Children = { meta } };
        if (record.IsFile && record.SourceName is { } name) row.Children.Add(FileName(name, 11.5));
        if (progress != null)
        {
            row.Children.Add(Ui.Text(progress.Label, 12, foreground: Ui.Secondary));
            row.Children.Add(FileProgressBar(progress));
            return row;
        }
        var text = Ui.Text(Preview(record), 13, foreground: record.OutputText.Length == 0 ? Ui.Secondary : null, wrap: true);
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(text);
        return row;
    }

    /// <summary>The name of the file a record was transcribed from, after a file icon.</summary>
    private static FrameworkElement FileName(string name, double size)
    {
        var text = Ui.Text(name, size, FontWeights.Medium, Ui.Secondary);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxLines = 1;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Icon(Glyphs.Page, size, Ui.Secondary), text } };
    }

    /// <summary>How far a file being transcribed has got: a bar when that can be measured, moving dots otherwise.</summary>
    private static ProgressBar FileProgressBar(FileTranscription.Progress progress) => new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = progress.Fraction ?? 0,
        IsIndeterminate = progress.Fraction == null,
    };

    // MARK: Detail

    /// <summary>
    /// The selected dictation: when, how long, how many words and what it cost; the text that went in; what was said before
    /// snippets were expanded, and the raw transcript when it was cleaned up, behind expanders; and the actions.
    /// </summary>
    private FrameworkElement Detail(DictationRecord record)
    {
        var details = new List<string> { Formatting.DurationLabel(record.Duration) };
        // The words said, not what snippets put in.
        var words = WordCount.Count(record.FinalText);
        if (words > 0) details.Add(L($"{UsageFormat.Count(words)} 字", $"{UsageFormat.Count(words)} words"));
        if (record.Cost is { } cost) details.Add(UsageFormat.Money(cost));
        if (record.Translation is { } translation) details.Add(L($"翻译 → {translation.DisplayName}", $"Translation → {translation.DisplayName}"));
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
        if (record.IsFile && record.SourceName is { } name) body.Children.Add(FileName(name, 12));
        if (record.Error is { } error) body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange, error));
        if (_files.Progress(record.Id) is { } progress)
        {
            var label = Ui.Text(progress.Label, 13, foreground: Ui.Secondary);
            var bar = FileProgressBar(progress);
            _detailProgress = (label, bar);
            body.Children.Add(new StackPanel { Spacing = 8, Margin = new Thickness(0, 40, 0, 40), Children = { label, bar } });
        }
        else if (record.OutputText.Length == 0 && record.Status is DictationStatus.Recording or DictationStatus.Processing)
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
            var text = Ui.Text(record.OutputText.Length == 0 ? L("（没有文字）", "(no text)") : record.OutputText, 14,
                               foreground: record.OutputText.Length == 0 ? Ui.Secondary : null, wrap: true);
            text.IsTextSelectionEnabled = true;
            text.LineHeight = 22;
            body.Children.Add(text);
            // What was said, before snippets replaced their triggers.
            if (record.InsertedText != null) body.Children.Add(Disclosure(L("快捷短语替换前", "Before snippets"), record.FinalText));
            if (record.PolishedText != null && record.RawText.Length > 0) body.Children.Add(Disclosure(L("原始转写", "Raw transcript"), record.RawText));
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
            IsEnabled = record.OutputText.Length > 0,
        };
        copy.Click += async (_, _) =>
        {
            TextInserter.CopyToClipboard(record.OutputText);
            copyLabel.Text = L("已复制", "Copied");
            copyIcon.Glyph = Glyphs.CheckMark;
            await Task.Delay(1500);
            copyLabel.Text = L("复制", "Copy");
            copyIcon.Glyph = Glyphs.Copy;
        };
        leading.Children.Add(copy);
        var active = _files.IsActive(record.Id);
        if (active)
        {
            var cancel = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Ui.Icon(Glyphs.Cancel, 13), Ui.Text(L("取消", "Cancel"), 13) },
                },
            };
            cancel.Click += (_, _) => _files.Cancel(record.Id);
            leading.Children.Add(cancel);
        }
        else if (record.HasAudio && record.IsFile)
        {
            // A file goes back through the file queue: a long one mustn't hold up dictating.
            var again = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Ui.Icon(Glyphs.Refresh, 13), Ui.Text(L("重新转写", "Re-transcribe"), 13) },
                },
            };
            ToolTipService.SetToolTip(again, L("用保存的音频重新转写", "Transcribes the saved audio again"));
            again.Click += (_, _) => _files.Retry(record);
            leading.Children.Add(again);
        }
        if (record.HasAudio && !active && !record.IsFile)
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
        }
        if (record.HasAudio)
        {
            // Showing it in File Explorer on a click, saving a copy from the arrow, as on macOS.
            var folderIcon = Ui.Icon(Glyphs.Folder, 14);
            var folder = new SplitButton { Content = folderIcon };
            Ui.Label(folder, L("在文件资源管理器中显示录音", "Show recording in File Explorer"));
            folder.Click += (_, _) => RevealRecording(record);
            var reveal = new MenuFlyoutItem { Text = L("在文件资源管理器中显示录音", "Show recording in File Explorer") };
            reveal.Click += (_, _) => RevealRecording(record);
            var save = new MenuFlyoutItem
            {
                Text = L("将录音另存为…", "Save recording…"),
                // Still being written while it records; a copy then would stop part way.
                IsEnabled = record.Status is not (DictationStatus.Recording or DictationStatus.Processing),
            };
            save.Click += async (_, _) => await SaveRecording(record, folderIcon);
            folder.Flyout = new MenuFlyout { Items = { reveal, save } };
            leading.Children.Add(folder);
        }
        buttons.Children.Add(leading);
        var delete = new Button { Content = Ui.Icon(Glyphs.Delete, 14, Palette.Brush(Tint.Red)), IsEnabled = !active };
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

    private static void RevealRecording(DictationRecord record) => Process.Start("explorer.exe", $"/select,\"{record.AudioPath}\"");

    /// <summary>Copies the recording (16 kHz mono WAV) to where the user chooses.</summary>
    private async Task SaveRecording(DictationRecord record, FontIcon icon)
    {
        var saved = await Exports.Copy(this, _window.Hwnd, Exports.FileName("OpenTypeless", record.Date, time: true), "WAV", "wav",
                                       record.AudioPath, L("无法保存录音", "Couldn't save the recording"));
        if (!saved) return;
        icon.Glyph = Glyphs.CheckMark;
        await Task.Delay(1500);
        icon.Glyph = Glyphs.Folder;
    }

    // MARK: Transcribing files

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = L("松开即可转写音频或视频文件", "Drop audio or video files to transcribe them");
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        catch (Exception error)
        {
            AppLog.Debug("files", "couldn't read the dropped files: " + error.Message);
            return;
        }
        finally
        {
            deferral.Complete();
        }
        await OfferFiles(items.Where(item => item.IsOfType(StorageItemTypes.File)).Select(item => item.Path));
    }

    private async Task PickFiles()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        foreach (var extension in AudioLoader.Extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Hwnd);
        IReadOnlyList<StorageFile> files;
        try
        {
            files = await picker.PickMultipleFilesAsync();
        }
        catch (Exception error)
        {
            AppLog.Debug("files", "the file picker failed: " + error.Message);
            return;
        }
        await OfferFiles(files.Select(file => file.Path));
    }

    /// <summary>
    /// Before files are transcribed: which ones and how long, what their speech-to-text should cost when the model has a
    /// per-minute price, and whether to clean the text up. Transcribes the audio and video files among
    /// <paramref name="paths"/> if the user goes ahead.
    /// </summary>
    private async Task OfferFiles(IEnumerable<string> paths)
    {
        var media = paths.Where(AudioLoader.IsMedia).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (media.Count == 0 || _confirming || XamlRoot == null) return;
        _confirming = true;
        try
        {
            var configured = _settings.SttEndpoint != null && _settings.IsConfigured(_settings.SttProvider);
            var list = new StackPanel();
            var durations = new Dictionary<string, TextBlock>();
            foreach (var path in media)
            {
                if (list.Children.Count > 0) list.Children.Add(new CardDivider { Inset = 14 });
                var name = Ui.Text(System.IO.Path.GetFileName(path), 13);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var length = Ui.Text("…", 12, foreground: Ui.Secondary);
                length.HorizontalAlignment = HorizontalAlignment.Right;
                durations[path] = length;
                var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 9, 14, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Icon(AudioLoader.IsVideo(path) ? Glyphs.Video : Glyphs.Volume, 13, Ui.Secondary));
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                Grid.SetColumn(length, 2);
                row.Children.Add(length);
                list.Children.Add(row);
            }
            var estimate = Ui.Text(L("正在读取文件长度…", "Reading how long the files are…"), 12, foreground: Ui.Secondary, wrap: true);
            var cleanUp = new ToggleSwitch
            {
                IsOn = _settings.PolishEnabled && _settings.FileCleanUp, IsEnabled = _settings.PolishEnabled,
                OnContent = "", OffContent = "", MinWidth = 0,
            };
            var content = new StackPanel
            {
                Spacing = 14, Width = 440,
                Children =
                {
                    Card.Make(new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }),
                    estimate,
                    Card.Make(new CardRow
                    {
                        Glyph = Glyphs.Font, Tint = Tint.Green, Title = L("整理文字", "Clean up text"),
                        Subtitle = _settings.PolishEnabled
                            ? L("用整理模型分段整理转写结果，另行计费", "Runs the transcript through the clean-up model, in parts (billed on top)")
                            : L("文字整理已在设置里关闭", "Clean-up is turned off in Settings"),
                        Trailing = cleanUp,
                    }),
                },
            };
            if (!configured)
            {
                var name = _settings.SttProvider.DisplayName();
                content.Children.Add(new Banner(Glyphs.Warning, Tint.Orange,
                    L($"请先在设置里配置语音转文字服务商（{name}）", $"Set up the speech-to-text provider ({name}) in Settings first")));
            }
            content.Children.Add(Ui.Text(L("结果保存在历史记录里，不会粘贴。", "The text is kept in History; nothing is pasted."), 11.5,
                                         foreground: Ui.Tertiary, wrap: true));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L("转写文件", "Transcribe Files"),
                Content = content,
                PrimaryButtonText = L("开始转写", "Transcribe"),
                CloseButtonText = L("取消", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = configured,
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }
            _ = Probe();
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (Exception error)
            {
                // Another dialog already open in this window: the files are simply not offered.
                AppLog.Debug("files", "the import dialog couldn't open: " + error.Message);
                return;
            }
            if (result != ContentDialogResult.Primary) return;
            if (_settings.PolishEnabled) _settings.FileCleanUp = cleanUp.IsOn;
            // The newest record is the last file's: show it.
            _selection = null;
            _files.Transcribe(media, _settings.PolishEnabled && _settings.FileCleanUp);

            // Each file's length, or why it can't be transcribed, then the estimate for them all.
            async Task Probe()
            {
                var seconds = 0.0;
                foreach (var path in media)
                {
                    try
                    {
                        // Off the UI thread: opening a file can take a while (a network drive, a large video).
                        var length = await Task.Run(() => AudioLoader.Duration(path));
                        seconds += length;
                        durations[path].Text = length > 0 ? Formatting.DurationLabel(length) : "";
                    }
                    catch (Exception error)
                    {
                        durations[path].Text = FileTranscriptionException.Reading(error).Message;
                        durations[path].Foreground = Palette.Brush(Tint.Orange);
                        durations[path].TextWrapping = TextWrapping.Wrap;
                        durations[path].MaxWidth = 220;
                    }
                }
                estimate.Text = Estimate(seconds);
            }
        }
        finally
        {
            _confirming = false;
        }
    }

    /// <summary>"1 h 12 min in all; speech-to-text should cost about $0.43 (whisper-1 at $0.006/min). Silence isn't sent."</summary>
    private string Estimate(double seconds)
    {
        var total = UsageFormat.Duration(seconds);
        var model = _settings.SttModel;
        var price = PriceStore.Shared.Price(_settings.SttProvider, _settings.SttModel);
        if (FileTranscription.EstimatedCost(seconds, price) is { } cost && price?.PerMinute is { } perMinute)
        {
            var rate = UsageFormat.Rate(perMinute);
            var money = UsageFormat.Money(cost);
            return L($"共 {total}，语音转文字预计约 {money}（{model} 每分钟 {rate}）。静音部分不会发送。",
                     $"{total} in all; speech-to-text should cost about {money} ({model} at {rate}/min). Silence isn't sent.");
        }
        return L($"共 {total}。{model} 没有按分钟的价格，无法预估费用。",
                 $"{total} in all. {model} has no per-minute price, so the cost can't be estimated.");
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
