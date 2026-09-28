using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Input;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class HistoryPage : PageBase
{
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SessionController _controller;
    private string? _selection;
    private CardRow? _retentionRow;
    private long? _storageBytes;

    public HistoryPage(SessionController controller) : base(SettingsPage.History, scrolls: false)
    {
        _controller = controller;
        _history.Changed += Render;
        Render();
    }

    protected override void OnClosed() => _history.Changed -= Render;

    private string? SelectedId => _selection != null && _history.Records.Any(r => r.Id == _selection) ? _selection : _history.Records.FirstOrDefault()?.Id;

    private void Render()
    {
        Body.Children.Clear();
        Body.Children.Add(RetentionCard());
        RefreshStorage();
        if (_history.Records.Count == 0)
        {
            var empty = new StackPanel
            {
                Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { Ui.Icon(Glyphs.Microphone, 36, Ui.Tertiary), Ui.Text(L("还没有听写记录", "No dictations yet"), 13, foreground: Ui.Secondary) },
            };
            Body.Children.Add(new Grid { Children = { empty } });
            return;
        }

        var selectedId = SelectedId;
        var detailHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 4, 0, 4) };
        foreach (var record in _history.Records)
        {
            var row = Row(record);
            list.Items.Add(row);
            if (record.Id == selectedId) list.SelectedItem = row;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is FrameworkElement { Tag: string id } && id != _shownDetail)
            {
                _selection = id;
                ShowDetail(detailHost);
            }
        };

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Card.Make(list));
        Grid.SetColumn(detailHost, 1);
        grid.Children.Add(detailHost);
        Body.Children.Add(grid);
        ShowDetail(detailHost);
    }

    private string? _shownDetail;

    private FrameworkElement RetentionCard()
    {
        var options = HistoryRetentionExtensions.All;
        var picker = new ComboBox { MinWidth = 150 };
        foreach (var option in options) picker.Items.Add(option.Label());
        picker.SelectedIndex = Math.Max(0, Array.IndexOf(options, _settings.HistoryRetention));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || options[picker.SelectedIndex] == _settings.HistoryRetention) return;
            _settings.HistoryRetention = options[picker.SelectedIndex];
            _history.ApplyRetention();
            RefreshStorage();
        };
        _retentionRow = new CardRow
        {
            Glyph = Glyphs.Folder, Tint = Tint.Gray, Title = L("录音保存时间", "Keep recordings"),
            Subtitle = RetentionSubtitle(), Trailing = picker,
        };
        return Card.Make(_retentionRow);
    }

    /// <summary>Recomputes the space history takes (off the UI thread) and shows it under the retention picker.</summary>
    private async void RefreshStorage()
    {
        var row = _retentionRow;
        var bytes = await Task.Run(HistoryStore.StorageBytes);
        _storageBytes = bytes;
        if (row != null && row == _retentionRow) row.Subtitle = RetentionSubtitle();
    }

    private string RetentionSubtitle()
    {
        var used = _storageBytes is { } bytes ? L("目前占用 ", "Using ") + Formatting.Bytes(bytes) + L("。", ". ") : "";
        return used + L("每分钟录音约 1.9 MB。转写失败的录音会一直保留，方便重试。",
                        "Recordings take about 1.9 MB per minute. Failed dictations keep theirs so you can retry.");
    }

    private void ShowDetail(ContentControl host)
    {
        var record = _history.Records.FirstOrDefault(r => r.Id == SelectedId);
        _shownDetail = record?.Id;
        host.Content = record == null ? null : Detail(record);
    }

    private static FrameworkElement Row(DictationRecord record)
    {
        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new Ellipse { Width = 6, Height = 6, Fill = Palette.Brush(record.Status.Tint()), VerticalAlignment = VerticalAlignment.Center },
                Ui.Text(Formatting.ShortStamp(record.Date), 11.5, foreground: Ui.Secondary),
            },
        });
        var duration = Ui.Text(Formatting.DurationLabel(record.Duration), 11.5, foreground: Ui.Tertiary);
        Grid.SetColumn(duration, 1);
        meta.Children.Add(duration);

        var empty = record.FinalText.Length == 0;
        var text = Ui.Text(empty ? record.Error ?? L("（空）", "(empty)") : record.FinalText, 13, foreground: empty ? Ui.Secondary : null, wrap: true);
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return new StackPanel { Spacing = 4, Padding = new Thickness(0, 8, 0, 8), Tag = record.Id, Children = { meta, text } };
    }

    private FrameworkElement Detail(DictationRecord record)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text(record.Date.ToLocalTime().ToString("g"), 12.5, FontWeights.Medium),
                Ui.Text("·", 12.5, foreground: Ui.Tertiary),
                Ui.Text(Formatting.DurationLabel(record.Duration), 12.5, foreground: Ui.Secondary),
            },
        });
        var pill = new StatusPill(record.Status.Label(), record.Status.Tint());
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);

        var texts = new StackPanel { Spacing = 14 };
        if (record.PolishedText is { } polished) texts.Children.Add(TextBlock(L("整理后", "Cleaned up"), polished));
        texts.Children.Add(TextBlock(L("原始转写", "Raw transcript"), record.RawText));

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = L("复制", "Copy"), IsEnabled = record.FinalText.Length > 0 };
        copy.Click += (_, _) => TextInserter.CopyToClipboard(record.FinalText);
        leading.Children.Add(copy);
        if (record.HasAudio)
        {
            var retry = new Button { Content = L("重新转写", "Re-transcribe"), IsEnabled = _controller.State == SessionController.SessionState.Idle };
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

        var layout = new Grid { Padding = new Thickness(16), RowSpacing = 14 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(header);
        var notes = new StackPanel { Spacing = 8 };
        if (record.Error is { } error) notes.Children.Add(new Banner(Glyphs.Warning, Tint.Orange, error));
        var summary = string.Join(" · ", new[] { record.Timing?.Summary, record.Cost is { } cost ? L("花费 ", "Cost ") + UsageFormat.Money(cost) : null }
            .OfType<string>());
        if (summary.Length > 0)
        {
            // Where the wait after releasing the key went, and what it cost.
            var line = Ui.Text(summary, 11, foreground: Ui.Secondary, wrap: true);
            line.IsTextSelectionEnabled = true;
            notes.Children.Add(line);
        }
        if (notes.Children.Count > 0)
        {
            Grid.SetRow(notes, 1);
            layout.Children.Add(notes);
        }
        var scroll = new ScrollViewer { Content = texts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        return Card.Make(layout);
    }

    private static FrameworkElement TextBlock(string title, string text) => new StackPanel
    {
        Spacing = 6,
        Children =
        {
            Ui.Text(title, 11.5, FontWeights.SemiBold, Ui.Secondary),
            new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                Background = Palette.Theme("ControlFillColorSecondaryBrush"),
                Child = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = text.Length == 0 ? L("（空）", "(empty)") : text,
                    FontSize = 13.5,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
        },
    };
}
