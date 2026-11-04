using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Input;
using OpenTypeless.Native;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

/// <summary>
/// Settings for particular apps (see <see cref="AppProfile"/>): a card for each app, opened to change what it overrides.
/// There are none until the user adds one.
/// </summary>
public sealed class AppsPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SettingsWindow _window;
    private readonly StackPanel _content = new() { Spacing = 8 };
    private readonly HashSet<string> _expanded = new();
    /// <summary>The running apps are being listed for the Add menu.</summary>
    private bool _listing;

    public AppsPage(SettingsWindow window) : base(SettingsPage.Apps, help:
        L("给一个应用添加配置后，在它里面听写时就用这里的设置，比如在 Slack 里说完直接发送，在邮件里用更正式的语气。其他应用照常使用你平时的设置。",
          "Add an app to give it its own settings: send messages right away in Slack, say, or a more formal tone in your email app. Everywhere else your usual settings apply.")
        + "\n\n" + L("开始录音时在最前面的应用决定用哪个配置，松开按键前换到别的应用也不变。配置里设为「默认」的项目跟随你平时的设置。",
                      "The app in front when you start recording decides which profile is used, even if you switch apps before you finish. Whatever a profile leaves on Default follows your usual settings."))
    {
        _window = window;
        // The snapshots show the first sample profile opened. Nothing about running apps is read until Add is clicked.
        if (UISnapshots.Active && _settings.AppProfiles.FirstOrDefault() is { } first) _expanded.Add(first.Id);
        Body.Children.Add(_content);
        Render();
    }

    private void Render()
    {
        _content.Children.Clear();
        var profiles = _settings.AppProfiles;
        if (profiles.Count == 0)
        {
            var title = Ui.Text(L("还没有应用配置", "No app profiles yet"), 14, FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            var text = Ui.Text(L("让某个应用用自己的设置，比如在 Slack 里说完直接发送", "Give an app its own settings, like sending right away in Slack"),
                               12.5, foreground: Ui.Secondary, wrap: true);
            text.TextAlignment = TextAlignment.Center;
            var add = AddButton();
            add.HorizontalAlignment = HorizontalAlignment.Center;
            add.Margin = new Thickness(0, 4, 0, 0);
            _content.Children.Add(Card.Make(new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(16, 24, 16, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    Ui.Icon(SettingsPage.Apps.Glyph(), 28, Ui.Tertiary),
                    title,
                    text,
                    add,
                },
            }));
            return;
        }
        foreach (var profile in profiles)
        {
            var id = profile.Id;
            _content.Children.Add(new AppProfileCard(profile, _expanded.Contains(id),
                expanded =>
                {
                    if (expanded) _expanded.Add(id); else _expanded.Remove(id);
                },
                () =>
                {
                    _expanded.Remove(id);
                    Render();
                }));
        }
        var more = AddButton();
        more.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(more);
    }

    /// <summary>Lists the running apps that have no profile yet, and a file picker for any other program, to add one.</summary>
    private Button AddButton()
    {
        var label = L("添加应用", "Add app");
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Ui.Icon(Glyphs.Add, 12), new TextBlock { Text = label } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += async (_, _) =>
        {
            if (_listing) return;
            _listing = true;
            List<RunningApp> running;
            try
            {
                // Asking every process for its window takes a moment: not on the UI thread.
                running = await Task.Run(() => RunningApp.All());
            }
            finally
            {
                _listing = false;
            }
            // The page went away meanwhile.
            if (button.XamlRoot == null) return;
            var profiles = _settings.AppProfiles;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            var candidates = running.Where(app => !profiles.Any(p => p.Matches(app.Executable))).ToList();
            if (candidates.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem { Text = L("没有其他正在运行的应用", "No other running apps"), IsEnabled = false });
            }
            foreach (var app in candidates)
            {
                var item = new MenuFlyoutItem { Text = app.Label };
                item.Click += (_, _) => Add(app);
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new MenuFlyoutSeparator());
            var choose = new MenuFlyoutItem { Text = L("选择程序…", "Choose a program…") };
            choose.Click += async (_, _) => await ChooseProgram();
            flyout.Items.Add(choose);
            flyout.ShowAt(button);
        };
        return button;
    }

    private void Add(RunningApp app)
    {
        var profile = _settings.AddAppProfile(app.Executable, app.Name);
        _expanded.Add(profile.Id);
        Render();
    }

    /// <summary>A program that isn't running: picked by its .exe, like an app from the Applications folder on macOS.</summary>
    private async Task ChooseProgram()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".exe");
            // An unpackaged app has to tell the picker which window it belongs to.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            // Its version resource is read from disk: not on the UI thread.
            var path = file.Path;
            var name = await Task.Run(() => RunningApp.DisplayName(path));
            // The page went away meanwhile.
            if (XamlRoot == null) return;
            Add(new RunningApp(file.Name, name));
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The picker can't open (it refuses to when the app runs as administrator): the running apps are still listed.
            AppLog.Debug("apps", "can't show the file picker: " + e.Message);
        }
    }
}

/// <summary>
/// One app's profile: its name and what it changes; opened, a picker for each setting, its own clean-up preferences and
/// Remove.
/// </summary>
internal sealed class AppProfileCard : UserControl
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private AppProfile _profile;
    private readonly TextBlock _summary = Ui.Text("", 12, foreground: Ui.Secondary);
    private readonly FontIcon _chevron = Ui.Icon(Glyphs.ChevronRight, 11);
    private readonly Border _divider = new() { Height = 1 };
    private readonly StackPanel _details = new();
    private readonly TextBox _instructions;
    private readonly ComboBox _layout;
    private readonly Action<bool> _onExpanded;
    private bool _expanded;

    public AppProfileCard(AppProfile profile, bool expanded, Action<bool> onExpanded, Action onRemoved)
    {
        _profile = profile;
        _expanded = expanded;
        _onExpanded = onExpanded;

        // Header (the whole row toggles the card).
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Text(profile.Name, 13.5, FontWeights.Medium), _summary } };
        var header = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        _chevron.Foreground = Ui.Tertiary;
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        _chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        header.Children.Add(titles);
        Grid.SetColumn(_chevron, 1);
        header.Children.Add(_chevron);
        var toggle = new Button
        {
            Content = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 8, 14, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, profile.Name);
        toggle.Click += (_, _) =>
        {
            _expanded = !_expanded;
            _onExpanded(_expanded);
            UpdateExpansion();
        };

        // A picker for each setting.
        _details.Children.Add(new CardRow
        {
            Glyph = Glyphs.Sparkle, Title = L("用 AI 整理文字", "Clean up with AI"),
            Trailing = OverrideBox(profile.Polish, _settings.PolishEnabled, value => Update(p => p with { Polish = value })),
        });
        _details.Children.Add(new CardDivider());
        _details.Children.Add(new CardRow
        {
            Glyph = Glyphs.Enter, Title = L("插入后自动回车", "Press Enter after inserting"),
            Trailing = OverrideBox(profile.PressEnter, _settings.PressEnterAfterInsert, value => Update(p => p with { PressEnter = value })),
        });
        _details.Children.Add(new CardDivider());
        _details.Children.Add(new CardRow
        {
            Glyph = Glyphs.Translate, Title = L("说话语言", "Spoken language"),
            Trailing = LanguageBox(),
        });
        _details.Children.Add(new CardDivider());
        _layout = LayoutBox();
        _details.Children.Add(new CardRow
        {
            Glyph = Glyphs.Paragraph, Title = L("排版", "Layout"), Help = LayoutHelp,
            Trailing = _layout,
        });
        _details.Children.Add(new CardDivider());

        // The app's own clean-up preferences.
        var heading = L("这个应用的整理偏好", "Clean-up preferences for this app");
        var label = new TitleLine(Ui.Text(heading, 13.5))
        {
            Help = L("加在「词汇与风格」里的整理偏好之后，只在这个应用里生效。例如：「语气正式一些」「不要使用列表」。",
                     "Added after your clean-up preferences under Vocabulary & Style, in this app only. e.g. “Use a more formal tone”, “Never use bullet lists”."),
        };
        _instructions = new TextBox
        {
            Text = profile.Instructions,
            Height = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_instructions, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_instructions, heading);
        _instructions.TextChanged += (_, _) =>
        {
            var text = Normalize(_instructions.Text);
            Update(p => p with { Instructions = text });
        };
        _details.Children.Add(new StackPanel { Spacing = 6, Padding = new Thickness(14), Children = { label, _instructions } });

        // What it matches, and Remove.
        var executable = Ui.Text(profile.App, 11, foreground: Ui.Tertiary);
        executable.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        executable.IsTextSelectionEnabled = true;
        executable.VerticalAlignment = VerticalAlignment.Center;
        var removeLabel = L("移除", "Remove");
        var remove = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { Ui.Icon(Glyphs.Delete, 12), new TextBlock { Text = removeLabel } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, removeLabel);
        remove.Click += (_, _) =>
        {
            _settings.RemoveAppProfile(_profile.Id);
            onRemoved();
        };
        var footer = new Grid
        {
            Padding = new Thickness(14, 10, 14, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        footer.Children.Add(executable);
        Grid.SetColumn(remove, 1);
        footer.Children.Add(remove);
        _details.Children.Add(new CardDivider { Inset = 0 });
        _details.Children.Add(footer);

        _divider.Background = Palette.Theme("DividerStrokeColorDefaultBrush");
        Content = Card.Make(new StackPanel { Children = { toggle, _divider, _details } });
        RefreshHeader();
        UpdateExpansion();
    }

    private void UpdateExpansion()
    {
        _divider.Visibility = _details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.RenderTransform = new RotateTransform { Angle = _expanded ? 90 : 0 };
    }

    /// <summary>Saves one change to this profile, on top of what's stored now.</summary>
    private void Update(Func<AppProfile, AppProfile> change)
    {
        _profile = change(_settings.AppProfiles.FirstOrDefault(p => p.Id == _profile.Id) ?? _profile);
        _settings.UpdateAppProfile(_profile);
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        _summary.Text = Summary(_profile);
        // Only used while the text is cleaned up.
        _instructions.IsEnabled = _profile.Polish ?? _settings.PolishEnabled;
        _layout.IsEnabled = _instructions.IsEnabled;
    }

    /// <summary>On, off, or the global setting (named with its current value).</summary>
    private static ComboBox OverrideBox(bool? value, bool global, Action<bool?> set)
    {
        var box = new ComboBox { MinWidth = 150 };
        box.Items.Add(global ? L("默认（开）", "Default (on)") : L("默认（关）", "Default (off)"));
        box.Items.Add(L("开", "On"));
        box.Items.Add(L("关", "Off"));
        box.SelectedIndex = value is null ? 0 : value.Value ? 1 : 2;
        box.SelectionChanged += (_, _) => set(box.SelectedIndex switch { 1 => true, 2 => false, _ => null });
        return box;
    }

    /// <summary>The global language (named), auto-detect, or one of the languages the Models page offers.</summary>
    private ComboBox LanguageBox()
    {
        var box = new ComboBox { MinWidth = 150 };
        List<string?> codes = [null, "", .. Localization.Supported.Select(l => l.RawValue())];
        // A language this version doesn't list, kept as it was saved.
        if (_profile.Language is { Length: > 0 } saved && !codes.Contains(saved)) codes.Add(saved);
        var global = LanguageName(_settings.SttLanguage);
        foreach (var code in codes) box.Items.Add(code is null ? L($"默认（{global}）", $"Default ({global})") : LanguageName(code));
        box.SelectedIndex = Math.Max(0, codes.IndexOf(_profile.Language));
        box.SelectionChanged += (_, _) =>
        {
            var code = codes[Math.Max(0, box.SelectedIndex)];
            Update(p => p with { Language = code });
        };
        return box;
    }

    /// <summary>The app's built-in layout (named), or one of the layouts.</summary>
    private ComboBox LayoutBox()
    {
        var box = new ComboBox { MinWidth = 150 };
        List<TextLayout?> layouts = [null, .. TextLayouts.All.Select(l => (TextLayout?)l)];
        var builtIn = LayoutName(TextLayouts.BuiltIn(_profile.App) ?? TextLayout.Natural);
        foreach (var layout in layouts) box.Items.Add(layout is { } chosen ? LayoutName(chosen) : L($"默认（{builtIn}）", $"Default ({builtIn})"));
        box.SelectedIndex = Math.Max(0, layouts.IndexOf(_profile.Layout));
        box.SelectionChanged += (_, _) =>
        {
            var layout = layouts[Math.Max(0, box.SelectedIndex)];
            Update(p => p with { Layout = layout });
        };
        return box;
    }

    /// <summary>A layout's name in the picker and the card's summary.</summary>
    private static string LayoutName(TextLayout layout) => layout switch
    {
        TextLayout.Chat => L("聊天", "Chat"),
        TextLayout.Structured => L("轻结构", "Light structure"),
        TextLayout.Email => L("邮件", "Email"),
        _ => L("自然", "Natural"),
    };

    private static string LayoutHelp =>
        L("整理后的文字怎么分段、要不要列表。聊天：一整段，不列表，句尾不加句号。自然：短的不分段，明确列举三项以上才列表。轻结构（写给 AI 的提示词、文档）：更积极地分段，两项以上的并列就列表。邮件：说了称呼和落款就各占一行。只改排版，不改你说的话。常见的聊天、AI、邮件应用默认就用对应的排版。",
          "How the cleaned-up text is laid out. Chat: one block, no lists, no full stop at the end. Natural: short text stays one paragraph, a list only for three or more items you enumerate. Light structure (prompts for an AI, documents): more paragraphs, and a list for two or more parallel items. Email: a greeting and sign-off you dictate go on lines of their own. Only the layout changes, never your words. Common chat, AI and mail apps use their layout by default.");

    /// <summary>"Clean-up off · Enter on · English", or that everything follows the usual settings.</summary>
    private static string Summary(AppProfile profile)
    {
        var parts = new List<string>();
        if (profile.Polish is { } polish) parts.Add(polish ? L("整理：开", "Clean-up on") : L("整理：关", "Clean-up off"));
        if (profile.PressEnter is { } enter) parts.Add(enter ? L("回车：开", "Enter on") : L("回车：关", "Enter off"));
        if (profile.Language is { } language) parts.Add(LanguageName(language));
        if (profile.Layout is { } layout) parts.Add(LayoutName(layout));
        if (profile.Instructions.Trim().Length > 0) parts.Add(L("自定义整理偏好", "Own clean-up preferences"));
        return parts.Count == 0 ? L("全部跟随你平时的设置", "Uses your usual settings") : string.Join(" · ", parts);
    }

    /// <summary>A spoken-language code as the Models page names it; "" is auto-detect.</summary>
    private static string LanguageName(string code)
    {
        if (code.Length == 0) return L("自动检测", "Auto-detect");
        if (!Localization.Supported.Any(l => l.RawValue() == code)) return code;
        var language = Localization.ParseLanguage(code);
        return language == AppLanguage.Zh ? "中文" : language.NativeName();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}

/// <summary>An app in the Add menu: one with a window on screen, other than OpenTypeless itself.</summary>
internal sealed record RunningApp(string Executable, string Name)
{
    /// <summary>
    /// Shell processes whose windows aren't apps to dictate into: a Store app's frame when the app behind it can't be
    /// found (one suspended in the background), the touch keyboard, Start, search, the lock screen.
    /// </summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "LockApp.exe",
    };

    /// <summary>"Google Chrome (chrome.exe)", or just the file name when that's all there is.</summary>
    public string Label =>
        string.Equals(Name, Path.GetFileNameWithoutExtension(Executable), StringComparison.OrdinalIgnoreCase) ? Executable : $"{Name} ({Executable})";

    /// <summary>
    /// Every visible top-level window that's neither a tool window nor owned by another, by the app it belongs to (looking
    /// past ApplicationFrameHost to the Store app it frames, as the profile lookup does), one entry per executable.
    /// </summary>
    public static List<RunningApp> All()
    {
        var windows = new List<nint>();
        Win32.EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, 0);
        var self = (uint)Environment.ProcessId;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (!Win32.IsWindowVisible(window) || Win32.GetWindow(window, Win32.GW_OWNER) != 0
                || ((long)Win32.GetWindowLongPtr(window, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) continue;
            Win32.GetWindowThreadProcessId(window, out var pid);
            if (pid == self || FocusProbe.ExecutablePath(window) is not { } path) continue;
            var executable = Path.GetFileName(path);
            if (Hosts.Contains(executable) || apps.ContainsKey(executable)) continue;
            apps[executable] = new RunningApp(executable, DisplayName(path));
        }
        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The name the executable gives itself ("Google Chrome" for chrome.exe), or its file name.</summary>
    public static string DisplayName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No version resource to read.
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
