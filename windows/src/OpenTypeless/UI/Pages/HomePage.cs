using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Input;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

/// <summary>
/// The first page. It opens on how to dictate, then says what dictating has added up to: the time it saved and three
/// more figures on one card, a year of activity and the spend, and the last few dictations. Before the first dictation
/// there's nothing to count, so it shows how the key works instead.
/// </summary>
public sealed class HomePage : PageBase
{
    private readonly SettingsWindow _window;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly UsageStore _usage = UsageStore.Shared;
    private readonly PriceStore _prices = PriceStore.Shared;
    private readonly HistoryStore _history = HistoryStore.Shared;
    /// <summary>The setup issues last drawn, so a change of settings that isn't one of them doesn't redraw the page.</summary>
    private string _issuesShown = "";

    public HomePage(SettingsWindow window) : base(SettingsPage.Home, maxWidth: 1012, showsTitle: false)
    {
        _window = window;
        Body.Spacing = 20;
        _usage.Changed += OnChanged;
        _prices.Changed += OnChanged;
        _history.Changed += OnChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        Render();
        _prices.RefreshIfStale(TimeSpan.FromHours(1));
    }

    protected override void OnClosed()
    {
        _usage.Changed -= OnChanged;
        _prices.Changed -= OnChanged;
        _history.Changed -= OnChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Render);

    /// <summary>A key entered, the engine changed, another shortcut: the headline and the setup notices follow.</summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IssuesKey() != _issuesShown || e.PropertyName is null or nameof(AppSettings.Hotkey)) Render();
    });

    private string IssuesKey() => string.Join("\n", SetupIssues.Current(_settings).Select(issue => issue.Text));

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

        Body.Children.Add(Headline());
        // What still stands in the way of a dictation, each with the page that fixes it.
        _issuesShown = IssuesKey();
        foreach (var (text, page) in SetupIssues.Current(_settings))
        {
            var fix = new Button { Content = L("设置", "Fix") };
            fix.Click += (_, _) => _window.Navigate(page);
            Body.Children.Add(new NoticeBar(Glyphs.Warning, Tint.Orange, text, fix));
        }

        if (ledger.IsEmpty)
        {
            // Before the first dictation: what the key does, instead of zeros and an empty grid.
            var note = Ui.Text(L($"按住 {_settings.Hotkey.DisplayName} 说话，你的统计就会出现在这里。",
                                 $"Hold {_settings.Hotkey.DisplayName} and talk. Your stats will show up here."),
                               12, foreground: Ui.Secondary, wrap: true);
            note.Margin = new Thickness(1, 0, 0, 0);
            Body.Children.Add(new StackPanel { Spacing = 8, Children = { DictationTips.Make(), note } });
        }
        else
        {
            var speed = all.SpeakingWordsPerMinute;
            Body.Children.Add(FiguresCard(
                new Stat(L("节省的时间", "Time saved"), UsageFormat.Duration(all.SavedSeconds(wpm)), L($"按每分钟打 {wpm} 字算", $"vs. typing at {wpm} wpm"),
                         Help: all.Words == 0 ? null : L(
                             $"说这 {UsageFormat.Count(all.Words)} 个字用了 {UsageFormat.Duration(all.SpeakingSeconds)}；按每分钟 {wpm} 字打出来要 {UsageFormat.Duration(all.TypingSeconds(wpm))}。打字速度可以在「通用」里修改。",
                             $"Saying these {UsageFormat.Count(all.Words)} words took {UsageFormat.Duration(all.SpeakingSeconds)}; typing them at {wpm} wpm would take {UsageFormat.Duration(all.TypingSeconds(wpm))}. Change the typing speed under General.")),
                [
                    new Stat(L("总字数", "Words dictated"), UsageFormat.Count(all.Words),
                             L($"今天 {UsageFormat.Count(todayTotals.Words)}", $"{UsageFormat.Count(todayTotals.Words)} today"),
                             Tooltip: L($"本月 {UsageFormat.Count(month.Words)}", $"{UsageFormat.Count(month.Words)} this month")),
                    new Stat(L("说话速度", "Speaking speed"), speed is { } s ? L($"{s:0} 字/分钟", $"{s:0} wpm") : "—",
                             speed is { } v ? L($"是打字的 {v / wpm:0.0} 倍", $"{v / wpm:0.0}× your typing speed") : L("说几句就能算出来", "Dictate a little to see it")),
                    new Stat(L("听写次数", "Dictations"), UsageFormat.Count(all.Dictations),
                             streak > 0 ? L($"连续 {streak} 天 · 最长 {longest} 天", $"{streak}-day streak · Best {longest}")
                                        : L($"最长连续 {longest} 天", $"Longest streak {longest} days")),
                ]));

            // The activity grid takes two thirds of the width, the spend the rest; both cards as tall as the taller one.
            var spend = new StackPanel();
            spend.Children.Add(SpendLine(L("今天", "Today"), todayTotals));
            spend.Children.Add(new CardDivider());
            spend.Children.Add(SpendLine(L("本月", "This month"), month, limit: _settings.ActiveSpendLimit));
            spend.Children.Add(new CardDivider());
            spend.Children.Add(SpendLine(L("累计", "All time"), all, showsSplit: true));
            // A new heatmap on every render: XAML refuses (E_INVALIDARG) to put an element into a second card, and one
            // thrown from a dispatcher callback (a new price list, a finished dictation) ends the app with 0xC000027B.
            var heatmap = new ActivityHeatmap { Margin = new Thickness(16, 14, 16, 14), Ledger = ledger };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(TitledCard(L("活跃度", "Activity"), null, heatmap));
            var spendCard = TitledCard(L("花费", "Spend"), SpendHelp(), spend);
            Grid.SetColumn(spendCard, 1);
            row.Children.Add(spendCard);
            Body.Children.Add(row);
            if (all.Unpriced > 0)
            {
                var button = new Button { Content = L("设置价格", "Set prices") };
                button.Click += (_, _) => _window.Navigate(SettingsPage.Models);
                Body.Children.Add(new NoticeBar(Glyphs.Warning, Tint.Orange,
                    L($"有 {all.Unpriced} 次请求的模型没有价格，没有计入花费。", $"{all.Unpriced} requests used a model with no price, so they aren't in the spend."),
                    button));
            }
        }

        if (Recent() is { } recent) Body.Children.Add(recent);
    }

    /// <summary>Home's title: how to dictate, with the shortcut's keys as they are on the keyboard, and the Scratchpad to try it in.</summary>
    private Grid Headline()
    {
        static TextBlock Word(string text)
        {
            // Windows' TitleTextBlockStyle, as the other pages' titles.
            var block = Ui.Text(text, 28, FontWeights.SemiBold);
            block.VerticalAlignment = VerticalAlignment.Center;
            return block;
        }
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Word(L("按住", "Hold")),
                new KeyCaps { Caps = _settings.Hotkey.KeyCaps, Large = true, VerticalAlignment = VerticalAlignment.Center },
                Word(L("说话", "and speak")),
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(line, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        var tip = Ui.Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"), 14, foreground: Ui.Secondary);
        tip.TextTrimming = TextTrimming.CharacterEllipsis;
        var open = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { Ui.Icon(Glyphs.Page, 16), new TextBlock { Text = L("打开草稿本", "Open Scratchpad") } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, L("打开草稿本", "Open Scratchpad"));
        open.Click += (_, _) => ScratchpadWindow.Open();
        var grid = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 6, Children = { line, tip } });
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        return grid;
    }

    /// <summary>One headline figure: its label, the number, and a line of context; more of the same on a tooltip.</summary>
    private sealed record Stat(string Title, string Value, string Caption, string? Help = null, string? Tooltip = null);

    /// <summary>
    /// What dictating has added up to, on one card: the time it saved in large figures, then the other three side by side
    /// under a hairline, read across like a line of a table rather than as four boxes.
    /// </summary>
    private static Border FiguresCard(Stat hero, Stat[] stats)
    {
        var heroValue = Ui.Text(hero.Value, 40, FontWeights.SemiBold);
        heroValue.TextTrimming = TextTrimming.CharacterEllipsis;
        var heroCaption = Ui.Text(hero.Caption, 12, foreground: Ui.Secondary);
        // On the figures' baseline, more or less: a 40 px line leaves about 9 px under it.
        heroCaption.VerticalAlignment = VerticalAlignment.Bottom;
        heroCaption.Margin = new Thickness(0, 0, 0, 9);
        var top = new StackPanel
        {
            Spacing = 0,
            Padding = new Thickness(20, 16, 20, 12),
            Children =
            {
                new TitleLine(Ui.Text(hero.Title, 12.5, foreground: Ui.Secondary)) { Help = hero.Help },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { heroValue, heroCaption } },
            },
        };
        var rule = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = Palette.Theme("DividerStrokeColorDefaultBrush"),
            Margin = new Thickness(20, 0, 20, 0),
        };
        var cells = new Grid { Padding = new Thickness(4, 0, 4, 0) };
        for (var i = 0; i < stats.Length; i++)
        {
            var stat = stats[i];
            cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var value = Ui.Text(stat.Value, 22, FontWeights.SemiBold);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            var caption = Ui.Text(stat.Caption, 12, foreground: Ui.Secondary, wrap: true);
            caption.MaxLines = 2;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            var cell = new StackPanel
            {
                Spacing = 1,
                Padding = new Thickness(16, 12, 16, 14),
                // So that all of the cell counts for the tooltip, not only its text.
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Children = { value, new TitleLine(Ui.Text(stat.Title, 12.5)) { Help = stat.Help }, caption },
            };
            if (stat.Tooltip != null) ToolTipService.SetToolTip(cell, stat.Tooltip);
            Grid.SetColumn(cell, i);
            cells.Children.Add(cell);
        }
        return Card.Make(new StackPanel { Children = { top, rule, cells } }, large: true);
    }

    /// <summary>The last few dictations, a line each: when, the text, how long it took to say. A click copies one; History has the rest.</summary>
    private StackPanel? Recent()
    {
        var records = _history.Records.Where(r => r.OutputText.Length > 0).Take(5).ToList();
        if (records.Count == 0) return null;
        var title = Ui.Text(L("最近", "Recent"), 14, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(1, 0, 0, 0);
        var all = new HyperlinkButton
        {
            Padding = new Thickness(8, 4, 6, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { new TextBlock { Text = SettingsPage.History.Title(), FontSize = 12.5 }, Ui.Icon(Glyphs.ChevronRight, 12) },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(all, SettingsPage.History.Title());
        all.Click += (_, _) => _window.Navigate(SettingsPage.History);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(all, 1);
        header.Children.Add(all);

        var list = new StackPanel();
        foreach (var record in records)
        {
            if (list.Children.Count > 0) list.Children.Add(new CardDivider { Inset = 16 });
            list.Children.Add(RecentRow(record));
        }
        return new StackPanel { Spacing = 4, Children = { header, Card.Make(list, large: true) } };
    }

    private Button RecentRow(DictationRecord record)
    {
        var stamp = Ui.Text(Formatting.ShortStamp(record.Date), 12, foreground: Ui.Secondary);
        stamp.TextTrimming = TextTrimming.CharacterEllipsis;
        stamp.VerticalAlignment = VerticalAlignment.Center;
        // One line: a dictation's own line breaks would otherwise end it at its first.
        var text = Ui.Text(record.OutputText.ReplaceLineEndings(" "), 14);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
        text.MaxLines = 1;
        text.VerticalAlignment = VerticalAlignment.Center;
        var duration = Ui.Text(Formatting.DurationLabel(record.Duration), 12, foreground: Ui.Tertiary);
        duration.VerticalAlignment = VerticalAlignment.Center;
        // The copy icon shows under the pointer, and turns into a tick for a moment once clicked.
        var icon = Ui.Icon(Glyphs.Copy, 12, Ui.Secondary);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Opacity = 0;

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.Children.Add(stamp);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(duration, 2);
        grid.Children.Add(duration);
        Grid.SetColumn(icon, 3);
        grid.Children.Add(icon);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 11, 16, 12),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, record.OutputText);
        ToolTipService.SetToolTip(button, L("点击复制", "Click to copy"));
        var copied = false;
        button.PointerEntered += (_, _) => icon.Opacity = 1;
        button.PointerExited += (_, _) => icon.Opacity = copied ? 1 : 0;
        button.Click += (_, _) =>
        {
            TextInserter.CopyToClipboard(record.OutputText);
            copied = true;
            Ui.SetGlyph(icon, Glyphs.Completed, filled: true);
            icon.Foreground = Palette.Brush(Tint.Green);
            icon.Opacity = 1;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.2);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                copied = false;
                Ui.SetGlyph(icon, Glyphs.Copy);
                icon.Foreground = Ui.Secondary;
                icon.Opacity = 0;
            };
            timer.Start();
        };
        return button;
    }

    /// A section title above a card that fills the rest of its grid cell.
    private static Grid TitledCard(string title, string? help, UIElement content)
    {
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var label = new TitleLine(Ui.Text(title, 14, FontWeights.SemiBold)) { Margin = new Thickness(1, 0, 0, 0), Help = help };
        grid.Children.Add(label);
        var card = Card.Make(content, large: true);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(card, 1);
        grid.Children.Add(card);
        return grid;
    }

    /// <summary>
    /// A Fluent icon, in the secondary text colour unless <paramref name="foreground"/> says otherwise: one colour for every
    /// icon, so colour is left to mean state (a warning, a recording, a selection).
    /// </summary>
    internal static FontIcon IconTile(string glyph, double size = 20, Brush? foreground = null) =>
        Ui.Icon(glyph, size, foreground ?? Ui.Secondary);

    /// One line of the Spend card: label and amount, with the split between the two steps on a tooltip, or under it.
    /// With the monthly spending limit (this month's line), how much of it is spent, as a bar.
    private static StackPanel SpendLine(string title, UsageTotals totals, bool showsSplit = false, double? limit = null)
    {
        var amount = Ui.Text(UsageFormat.Money(totals.Cost), 15, FontWeights.SemiBold);
        amount.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(amount, 1);
        var label = Ui.Text(title, 12.5, foreground: Ui.Secondary);
        label.VerticalAlignment = VerticalAlignment.Center;
        var figures = new Grid { ColumnSpacing = 8 };
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        figures.Children.Add(label);
        figures.Children.Add(amount);
        var line = new StackPanel { Spacing = 2, Padding = new Thickness(14, 9, 14, 9), Children = { figures } };
        if (limit is { } cap) line.Children.Add(LimitBar(totals.Cost, cap, new Thickness(0, 4, 0, 0)));
        if (totals.Cost <= 0) return line;
        var split = L($"转写 {UsageFormat.Money(totals.TranscriptionCost)} · 整理 {UsageFormat.Money(totals.CleanupCost)}",
                      $"Speech {UsageFormat.Money(totals.TranscriptionCost)} · Clean-up {UsageFormat.Money(totals.CleanupCost)}");
        if (showsSplit) line.Children.Add(Ui.Text(split, 10.5, foreground: Ui.Tertiary, wrap: true));
        else ToolTipService.SetToolTip(line, split);
        return line;
    }

    /// <summary>
    /// This month's spend against the monthly limit: a bar that turns orange at the reminder's share and red at the limit,
    /// and how much of the limit that is. The tray panel shows the same.
    /// </summary>
    internal static StackPanel LimitBar(double spent, double limit, Thickness margin)
    {
        var level = SpendLimit.LevelOf(spent, limit);
        var percent = SpendLimit.Percent(spent, limit);
        var cap = UsageFormat.Money(limit);
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = SpendLimit.Progress(spent, limit),
            Foreground = level switch
            {
                SpendLimit.Level.Reached => Palette.Brush(Tint.Red),
                SpendLimit.Level.Warning => Palette.Brush(Tint.Orange),
                _ => Palette.Brush(Tint.Accent),
            },
        };
        var caption = Ui.Text(L($"上限 {cap} 的 {percent}%", $"{percent}% of your {cap} limit"), 10.5, foreground: Ui.Secondary);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 4, Margin = margin, Children = { bar, caption } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, caption.Text);
        return panel;
    }

    private string SpendHelp()
    {
        var catalog = _prices.Catalog;
        var updated = catalog.IsEmpty ? L("还没有下载", "not downloaded yet") : Formatting.ShortStamp(catalog.FetchedAt);
        return L($"OpenRouter 按每次请求实际扣费计算（价格表更新于 {updated}）。其他服务商按你在「模型」里填写的价格计算。",
                 $"OpenRouter requests count what OpenRouter billed for them (price list updated {updated}). Other providers use the prices you set under Models.");
    }
}

/// <summary>
/// What still stands between the user and a dictation, each with the page that fixes it: a missing key (or, with
/// on-device recognition, a model not downloaded) and microphone permission. Home and the tray panel show the same list.
/// </summary>
internal static class SetupIssues
{
    public static List<(string Text, SettingsPage Page)> Current(AppSettings settings)
    {
        var items = new List<(string Text, SettingsPage Page)>();
        if (SpeechModel.SetupIssue(settings) is { } issue)
        {
            var name = settings.SttProvider.DisplayName();
            items.Add(settings.SpeechEngine == SpeechEngine.OnDevice
                          ? (issue.Message, SettingsPage.Models)
                          : (L($"还没有配置 {name} 的 API Key", $"No API key for {name}"), SettingsPage.Providers));
        }
        if (!Permissions.MicrophoneGranted) items.Add((L("需要麦克风权限", "Microphone permission needed"), SettingsPage.General));
        return items;
    }
}

/// <summary>What the dictation key does, in three rows: hold it, tap it, and Esc. The onboarding ends on the same card.</summary>
internal static class DictationTips
{
    public static Border Make() => Card.Make(new StackPanel
    {
        Children =
        {
            new CardRow
            {
                Glyph = Glyphs.Microphone, Title = L("按住说话", "Hold to talk"),
                Subtitle = L("按住快捷键说话，松开后自动整理并插入到光标处", "Hold the shortcut while you speak; release to clean up and insert"),
            },
            new CardDivider { Inset = 50 },
            new CardRow
            {
                Glyph = Glyphs.TouchPointer, Title = L("轻点一下：免手持", "Tap once: hands-free"),
                Subtitle = L("适合长段口述，说完再按一次结束", "For long dictation — press again when done"),
            },
            new CardDivider { Inset = 50 },
            new CardRow
            {
                Glyph = Glyphs.Cancel, Title = L("Esc 取消", "Esc to cancel"),
                Subtitle = L("录音或处理过程中随时取消", "Cancel any time while recording or processing"),
            },
        },
    }, large: true);
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
    /// The square under the pointer, whose day is shown in a bubble right away (tooltips take a second and are
    /// easy to miss on squares this small).
    private Border? _hovered;

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
                var row = day;
                square.PointerEntered += (_, _) => ShowBubble(square, cell, row);
                square.PointerExited += (_, _) =>
                {
                    if (_hovered != square) return;
                    _hovered = null;
                    _bubble.Visibility = Visibility.Collapsed;
                };
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

        _bubble.Visibility = Visibility.Collapsed;
        _hovered = null;
        if (_overlay.Parent is Panel previous) previous.Children.Remove(_overlay);
        Content = new StackPanel { Spacing = 8, Children = { new Grid { Children = { grid, _overlay } }, legend } };
    }

    private readonly TextBlock _bubbleText = new() { FontSize = 11, FontWeight = FontWeights.Medium };
    private readonly Border _bubble = new()
    {
        Padding = new Thickness(8, 4, 8, 5),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Visibility = Visibility.Collapsed,
    };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };

    /// The hovered day's date, dictations and words, above the square (below it in the top rows).
    private void ShowBubble(Border square, HeatmapCell cell, int row)
    {
        if (_bubble.Child == null)
        {
            _bubble.Child = _bubbleText;
            _bubble.Background = Palette.Resource<Brush>("AcrylicInAppFillColorDefaultBrush", Palette.Theme("CardBackgroundFillColorDefaultBrush"));
            _bubble.BorderBrush = Palette.Resource<Brush>("SurfaceStrokeColorFlyoutBrush", Palette.Theme("CardStrokeColorDefaultBrush"));
            _overlay.Children.Add(_bubble);
        }
        _hovered = square;
        _bubbleText.Text = Tooltip(cell);
        _bubble.Visibility = Visibility.Visible;
        _bubble.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bubble.DesiredSize;
        var at = square.TransformToVisual(_overlay).TransformPoint(new Windows.Foundation.Point(0, 0));
        Canvas.SetLeft(_bubble, Math.Clamp(at.X + Cell / 2 - size.Width / 2, 0, Math.Max(0, _overlay.ActualWidth - size.Width)));
        Canvas.SetTop(_bubble, row < 3 ? at.Y + Cell + 6 : at.Y - size.Height - 6);
    }

    private static string Tooltip(HeatmapCell cell)
    {
        var date = DateLabel(cell.Date);
        if (cell.Dictations == 0) return L($"{date}：没有听写", $"{date}: no dictation");
        var words = UsageFormat.Count(cell.Words);
        return cell.Dictations == 1
            ? L($"{date}：1 次听写 · {words} 字", $"{date}: 1 dictation · {words} words")
            : L($"{date}：{cell.Dictations} 次听写 · {words} 字", $"{date}: {cell.Dictations} dictations · {words} words");
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

    // Labels in the UI language: "Sep" / "9月", "Mon" / "周一", "Sep 28" / "9月28日".
    private static DateTimeFormatInfo Dates => Localization.Resolved.Culture().DateTimeFormat;

    private static string MonthName(int month) => Dates.AbbreviatedMonthNames[month - 1];

    private static string WeekdayName(int weekday) => Dates.AbbreviatedDayNames[weekday];

    private static string DateLabel(DateOnly day) => day.ToString(Dates.MonthDayPattern.Replace("MMMM", "MMM"), Dates);
}
