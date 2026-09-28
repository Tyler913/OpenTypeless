using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

/// <summary>
/// The first page: how much you've dictated, the time that saved over typing, what it cost, and a year of activity.
/// </summary>
public sealed class HomePage : PageBase
{
    private readonly SettingsWindow _window;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly UsageStore _usage = UsageStore.Shared;
    private readonly PriceStore _prices = PriceStore.Shared;
    private readonly ActivityHeatmap _heatmap = new();

    public HomePage(SettingsWindow window) : base(SettingsPage.Home)
    {
        _window = window;
        _usage.Changed += OnChanged;
        _prices.Changed += OnChanged;
        Render();
        _prices.RefreshIfStale(TimeSpan.FromHours(1));
    }

    protected override void OnClosed()
    {
        _usage.Changed -= OnChanged;
        _prices.Changed -= OnChanged;
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        Body.Children.Clear();
        var ledger = _usage.Ledger;
        var today = UsageStore.Today;
        var all = ledger.Totals();
        var todayTotals = ledger.Today(today);
        var month = ledger.Month(today);
        var wpm = _settings.TypingWordsPerMinute;
        var (streak, longest) = ledger.Streaks(today);

        if (ledger.IsEmpty)
        {
            Body.Children.Add(new Banner(Glyphs.Info, Tint.Blue,
                L($"按住 {_settings.Hotkey.DisplayName} 说话，你的统计就会出现在这里。",
                  $"Hold {_settings.Hotkey.DisplayName} and talk. Your stats will show up here.")));
        }

        // Four headline figures, two by two.
        var speed = all.SpeakingWordsPerMinute;
        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddTile(tiles, 0, 0, Glyphs.Font, Tint.Indigo, L("总字数", "Words dictated"), UsageFormat.Count(all.Words),
                L($"今天 {UsageFormat.Count(todayTotals.Words)} · 本月 {UsageFormat.Count(month.Words)}",
                  $"Today {UsageFormat.Count(todayTotals.Words)} · This month {UsageFormat.Count(month.Words)}"));
        AddTile(tiles, 0, 1, Glyphs.Stopwatch, Tint.Green, L("节省的时间", "Time saved"), UsageFormat.Duration(all.SavedSeconds(wpm)),
                L($"和每分钟打 {wpm} 字相比", $"Compared with typing at {wpm} wpm"));
        AddTile(tiles, 1, 0, Glyphs.Speech, Tint.Orange, L("说话速度", "Speaking speed"),
                speed is { } s ? L($"{s:0} 字/分钟", $"{s:0} wpm") : "—",
                speed is { } v ? L($"是打字的 {v / wpm:0.0} 倍", $"{v / wpm:0.0}× your typing speed") : L("说几句就能算出来", "Dictate a little to see it"));
        AddTile(tiles, 1, 1, Glyphs.Calendar, Tint.Purple, L("听写次数", "Dictations"), UsageFormat.Count(all.Dictations),
                streak > 0 ? L($"已连续 {streak} 天 · 最长 {longest} 天", $"{streak}-day streak · Longest {longest} days")
                           : L($"最长连续 {longest} 天", $"Longest streak {longest} days"));
        var explanation = Ui.Text(all.Words == 0 ? "" : L(
            $"说这 {UsageFormat.Count(all.Words)} 个字用了 {UsageFormat.Duration(all.SpeakingSeconds)}；按每分钟 {wpm} 字打出来要 {UsageFormat.Duration(all.TypingSeconds(wpm))}。打字速度可以在「通用」里修改。",
            $"Saying these {UsageFormat.Count(all.Words)} words took {UsageFormat.Duration(all.SpeakingSeconds)}; typing them at {wpm} wpm would take {UsageFormat.Duration(all.TypingSeconds(wpm))}. Change the typing speed under General."),
            11.5, foreground: Ui.Secondary, wrap: true);
        explanation.Margin = new Thickness(4, 0, 4, 0);
        Body.Children.Add(new StackPanel { Spacing = 8, Children = { tiles, explanation } });

        // Spend
        var spend = new CardSection { Title = L("花费", "Spend"), Footer = SpendFooter() };
        spend.Body.Add(SpendRow(Glyphs.Calendar, Tint.Blue, L("今天", "Today"), todayTotals));
        spend.Body.Add(new CardDivider());
        spend.Body.Add(SpendRow(Glyphs.Calendar, Tint.Teal, L("本月", "This month"), month));
        spend.Body.Add(new CardDivider());
        spend.Body.Add(SpendRow(Glyphs.Money, Tint.Green, L("累计", "All time"), all));
        Body.Children.Add(spend);
        if (all.Unpriced > 0)
        {
            var button = new Button { Content = L("设置价格", "Set prices") };
            button.Click += (_, _) => _window.Navigate(SettingsPage.Models);
            Body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange,
                L($"有 {all.Unpriced} 次请求的模型没有价格，没有计入花费。", $"{all.Unpriced} requests used a model with no price, so they aren't in the spend."),
                button));
        }

        // Activity
        var activity = new CardSection
        {
            Title = L("活跃度", "Activity"),
            Footer = L("颜色越深，那天说得越多。", "The darker the square, the more you dictated that day."),
        };
        _heatmap.Margin = new Thickness(14, 12, 14, 12);
        _heatmap.Ledger = ledger;
        if (_heatmap.Parent is Panel old) old.Children.Remove(_heatmap);
        activity.Body.Add(_heatmap);
        Body.Children.Add(activity);
    }

    private static void AddTile(Grid grid, int row, int column, string glyph, Tint tint, string title, string value, string caption)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new IconBadge { Glyph = glyph, Tint = tint, BadgeSize = 22 },
                new TextBlock { Text = title, FontSize = 12.5, Foreground = Ui.Secondary, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var valueText = Ui.Text(value, 28, FontWeights.SemiBold);
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        var tile = Card.Make(new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(16, 14, 16, 14),
            Children = { header, valueText, Ui.Text(caption, 12, foreground: Ui.Secondary, wrap: true) },
        });
        Grid.SetRow(tile, row);
        Grid.SetColumn(tile, column);
        grid.Children.Add(tile);
    }

    private static CardRow SpendRow(string glyph, Tint tint, string title, UsageTotals totals) => new()
    {
        Glyph = glyph,
        Tint = tint,
        Title = title,
        Subtitle = totals.Cost > 0
            ? L($"语音转文字 {UsageFormat.Money(totals.TranscriptionCost)} · 文字整理 {UsageFormat.Money(totals.CleanupCost)}",
                $"Speech-to-text {UsageFormat.Money(totals.TranscriptionCost)} · Clean-up {UsageFormat.Money(totals.CleanupCost)}")
            : null,
        Trailing = Ui.Text(UsageFormat.Money(totals.Cost), 15, FontWeights.SemiBold),
    };

    private string SpendFooter()
    {
        var catalog = _prices.Catalog;
        var updated = catalog.IsEmpty ? L("还没有下载", "not downloaded yet") : Formatting.ShortStamp(catalog.FetchedAt);
        return L($"OpenRouter 按每次请求实际扣费计算（价格表更新于 {updated}）。其他服务商按你在「模型」里填写的价格计算。",
                 $"OpenRouter requests count what OpenRouter billed for them (price list updated {updated}). Other providers use the prices you set under Models.");
    }
}

/// <summary>
/// GitHub-style grid of the last weeks: one column per week, one square per day, shaded by how much was dictated.
/// As many weeks as fit the width are shown, up to a year.
/// </summary>
internal sealed class ActivityHeatmap : UserControl
{
    private const double Cell = 11;
    private const double Gap = 3;
    private const double LabelWidth = 30;
    private UsageLedger? _ledger;
    private int _weeks;

    public ActivityHeatmap()
    {
        MinHeight = 7 * (Cell + Gap) + 40;
        SizeChanged += (_, e) =>
        {
            if (WeeksFor(e.NewSize.Width) != _weeks) Render();
        };
    }

    public UsageLedger? Ledger
    {
        get => _ledger;
        set
        {
            _ledger = value;
            Render();
        }
    }

    private static int WeeksFor(double width) => width <= 0 ? 0 : Math.Clamp((int)((width - LabelWidth) / (Cell + Gap)), 4, 53);

    private void Render()
    {
        _weeks = WeeksFor(ActualWidth);
        if (_ledger is null || _weeks == 0)
        {
            Content = null;
            return;
        }
        var firstWeekday = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var today = UsageStore.Today;
        var columns = _ledger.Heatmap(today, _weeks, firstWeekday);

        var grid = new Grid { ColumnSpacing = Gap, RowSpacing = Gap };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth - Gap) });
        for (var i = 0; i < columns.Count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Cell) });

        // Weekday labels on alternate rows, like GitHub.
        for (var row = 1; row < 7; row += 2)
        {
            var label = Ui.Text(WeekdayName((firstWeekday + row) % 7), 9.5, foreground: Ui.Secondary);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, row + 1);
            grid.Children.Add(label);
        }

        var lastMonth = -1;
        for (var week = 0; week < columns.Count; week++)
        {
            var first = columns[week][0].Date;
            // Month names above the first week that starts in that month (skipped when too close to the edge).
            if (first.Month != lastMonth && week < columns.Count - 2)
            {
                if (lastMonth != -1 || first.Day <= 7)
                {
                    var month = Ui.Text(MonthName(first.Month), 9.5, foreground: Ui.Secondary);
                    Grid.SetColumn(month, week + 1);
                    Grid.SetColumnSpan(month, Math.Min(4, columns.Count - week));
                    grid.Children.Add(month);
                }
                lastMonth = first.Month;
            }
            for (var day = 0; day < 7; day++)
            {
                var cell = columns[week][day];
                if (cell.IsFuture) continue;
                var square = new Border { CornerRadius = new CornerRadius(2.5), Background = Shade(cell.Level) };
                ToolTipService.SetToolTip(square, cell.Words > 0
                    ? L($"{cell.Date.Month}月{cell.Date.Day}日：{UsageFormat.Count(cell.Words)} 字",
                        $"{MonthName(cell.Date.Month)} {cell.Date.Day}: {UsageFormat.Count(cell.Words)} words")
                    : L($"{cell.Date.Month}月{cell.Date.Day}日：没有听写", $"{MonthName(cell.Date.Month)} {cell.Date.Day}: no dictation"));
                Grid.SetRow(square, day + 1);
                Grid.SetColumn(square, week + 1);
                grid.Children.Add(square);
            }
        }

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        legend.Children.Add(new TextBlock { Text = L("少", "Less"), FontSize = 10, Foreground = Ui.Secondary, Margin = new Thickness(0, 0, 3, 0) });
        for (var level = 0; level <= 4; level++)
        {
            legend.Children.Add(new Border { Width = Cell, Height = Cell, CornerRadius = new CornerRadius(2.5), Background = Shade(level) });
        }
        legend.Children.Add(new TextBlock { Text = L("多", "More"), FontSize = 10, Foreground = Ui.Secondary, Margin = new Thickness(3, 0, 0, 0) });

        Content = new StackPanel { Spacing = 8, Children = { grid, legend } };
    }

    /// <summary>Empty days in neutral grey, busier days in deeper shades of the accent colour.</summary>
    public static Brush Shade(int level) => level switch
    {
        0 => Palette.Brush(Tint.Gray, 0.18),
        1 => Palette.Brush(Tint.Accent, 0.3),
        2 => Palette.Brush(Tint.Accent, 0.5),
        3 => Palette.Brush(Tint.Accent, 0.75),
        _ => Palette.Brush(Tint.Accent),
    };

    private static string MonthName(int month) =>
        L($"{month}月", CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames[month - 1]);

    private static string WeekdayName(int weekday) =>
        L("周" + "日一二三四五六"[weekday], CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[weekday]);
}
