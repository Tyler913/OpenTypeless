using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Input;
using OpenTypeless.Native;
using OpenTypeless.Services;
using TypelessCore;
using Windows.Graphics;

namespace OpenTypeless.UI;

/// <summary>
/// The Scratchpad: plain-text notes to dictate into when there's no text field to dictate into, where dictations with
/// nowhere to go are kept too (see <see cref="ScratchpadCapture"/>). The notes on the left, the last edited first; the
/// selected one on the right. Closing it only hides it, like the settings window.
/// </summary>
public sealed class ScratchpadWindow : Window
{
    private static ScratchpadWindow? _shared;

    /// <summary>Brings up the Scratchpad (made the first time it's asked for), from the tray panel or its shortcut.</summary>
    public static void Open()
    {
        _shared ??= new ScratchpadWindow(ScratchpadStore.Shared);
        _shared.Show();
    }

    /// <summary>Closes it, its notes saved, when the app quits.</summary>
    public static void CloseForQuit()
    {
        _shared?.CloseForExit();
        _shared = null;
    }

    /// <summary>
    /// Types <paramref name="text"/> at the editor's cursor (over a selection), when the Scratchpad is in front with the
    /// cursor in its editor. Nothing goes through the clipboard: a paste into this app's own window would only be slower
    /// and less sure. With <paramref name="fit"/>, the spacing and capitals are fitted to the text either side, as for a
    /// paste (see <see cref="InsertionJoiner"/>). False when the editor doesn't have the cursor.
    /// </summary>
    public static bool TryInsert(string text, bool fit, IReadOnlyList<string> vocabulary) =>
        _shared?.Insert(text, fit, vocabulary) ?? false;

    private readonly ScratchpadStore _store;
    private AppLanguage _language;
    private ListView _list = null!;
    private TextBox _editor = null!;
    private TextBlock _edited = null!;
    private Button _copy = null!;
    private Grid _note = null!;
    private StackPanel _empty = null!;
    private Grid _undoBar = null!;
    private string? _selectedId;
    /// <summary>The note just deleted, which Undo brings back.</summary>
    private string? _deletedId;
    private bool _allowClose;

    public nint Hwnd { get; }

    public ScratchpadWindow(ScratchpadStore store)
    {
        _store = store;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scaled(600);
            presenter.PreferredMinimumHeight = Scaled(380);
        }
        AppWindow.Resize(new SizeInt32(Scaled(820), Scaled(560)));
        Center();

        // Closing only hides: the window and its notes are kept, like the settings window.
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
        _store.Changed += OnStoreChanged;
        Build();
        Select(_store.Notes.Notes.FirstOrDefault()?.Id);
    }

    /// <summary>
    /// Brings the window up. Opened anew (<paramref name="startNote"/>), it starts a fresh note at the top (unless the
    /// newest one is still empty), with the cursor in it, ready to dictate.
    /// </summary>
    public void Show(bool startNote = true)
    {
        if (_language != Localization.Resolved) Build();
        if (!AppWindow.IsVisible && startNote)
        {
            _deletedId = null;
            var newest = _store.Notes.Notes.FirstOrDefault();
            Select(newest is { IsBlank: true } ? newest.Id : _store.Add());
        }
        else if (_selectedId == null)
        {
            Select(_store.Notes.Notes.FirstOrDefault()?.Id);
        }
        UpdateUndoBar();
        AppWindow.Show();
        Activate();
        ForegroundHelper.Bring(Hwnd);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusEditor);
    }

    /// <summary>Hides the window; a note never typed in goes, the rest is saved.</summary>
    public void Hide()
    {
        _store.RemoveBlank(null);
        _store.Save();
        _deletedId = null;
        AppWindow.Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _store.Changed -= OnStoreChanged;
        _store.Save();
        Close();
    }

    // MARK: - Layout

    private void Build()
    {
        _language = Localization.Resolved;
        Title = L("草稿本", "Scratchpad");

        var titleBar = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(32) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(260) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        Grid.SetColumnSpan(titleBar, 2);
        root.Children.Add(titleBar);

        var sidebar = Sidebar();
        Grid.SetRow(sidebar, 1);
        root.Children.Add(sidebar);

        var detail = Detail();
        Grid.SetRow(detail, 1);
        Grid.SetColumn(detail, 1);
        root.Children.Add(detail);

        Content = root;
        SetTitleBar(titleBar);
        RefreshList();
        ShowNote();
    }

    /// <summary>The title, New note, the notes, and Undo after a delete.</summary>
    private Grid Sidebar()
    {
        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };

        var header = new Grid { Padding = new Thickness(16, 2, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Ui.Text(L("草稿本", "Scratchpad"), 14, FontWeights.SemiBold);
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        var add = IconButton(Glyphs.Add, L("新建笔记", "New Note"));
        add.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        add.Click += (_, _) => NewNote();
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        grid.Children.Add(header);

        _list = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 0, 0, 8) };
        _list.SelectionChanged += (_, _) =>
        {
            // Only a click: the list is also rebuilt and selected from code, which changes nothing here.
            if (_list.SelectedItem is ListViewItem { Tag: string id } && id != _selectedId) Select(id);
        };
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);

        _undoBar = new Grid { Padding = new Thickness(16, 6, 12, 12), Visibility = Visibility.Collapsed };
        _undoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _undoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var deleted = Ui.Text(L("笔记已删除", "Note deleted"), 12, foreground: Ui.Secondary);
        deleted.VerticalAlignment = VerticalAlignment.Center;
        _undoBar.Children.Add(deleted);
        var undo = new Button { Content = L("撤销", "Undo") };
        undo.Click += (_, _) => Undo();
        Grid.SetColumn(undo, 1);
        _undoBar.Children.Add(undo);
        Grid.SetRow(_undoBar, 2);
        grid.Children.Add(_undoBar);
        return grid;
    }

    /// <summary>The selected note on a layer of its own, as in Windows 11 apps: when it was edited, Copy, Delete, the editor.</summary>
    private Border Detail()
    {
        _note = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };
        var header = new Grid { Padding = new Thickness(20, 6, 10, 6), ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _edited = Ui.Text("", 12, foreground: Ui.Secondary);
        _edited.VerticalAlignment = VerticalAlignment.Center;
        _edited.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_edited);
        _copy = IconButton(Glyphs.Copy, L("复制笔记", "Copy Note"));
        _copy.Click += (_, _) => CopyNote();
        Grid.SetColumn(_copy, 1);
        header.Children.Add(_copy);
        var delete = IconButton(Glyphs.Delete, L("删除笔记", "Delete Note"));
        delete.Click += (_, _) => DeleteNote();
        Grid.SetColumn(delete, 2);
        header.Children.Add(delete);
        _note.Children.Add(header);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Padding = new Thickness(20, 10, 20, 16),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        // A page to write on rather than a field in a form: no box, no underline, whatever its state.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
                 })
        {
            _editor.Resources[key] = clear;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editor, L("笔记", "Note"));
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.TextChanged += (_, _) =>
        {
            // Raised a moment after the text changes, also when a note is shown: only a real change is an edit.
            if (_selectedId is not { } id || _store.Notes.Note(id) is not { } note) return;
            var text = FromBox(_editor.Text);
            if (text != FromBox(note.Text)) _store.SetText(text, id);
        };
        Grid.SetRow(_editor, 1);
        _note.Children.Add(_editor);

        _empty = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.Icon(Glyphs.Page, 32, Ui.Tertiary);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(icon);
        var none = Ui.Text(L("没有选中的笔记", "No note selected"), 13, foreground: Ui.Secondary);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(none);
        var create = new Button { Content = L("新建笔记", "New Note"), HorizontalAlignment = HorizontalAlignment.Center };
        create.Click += (_, _) => NewNote();
        _empty.Children.Add(create);

        return new Border
        {
            Background = Palette.Theme("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Palette.Theme("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(8, 0, 0, 0),
            Child = new Grid { Children = { _note, _empty } },
        };
    }

    /// <summary>An icon button with no fill until the pointer is over it, as in Windows' own toolbars.</summary>
    private static Button IconButton(string glyph, string label)
    {
        var button = new Button
        {
            Content = Ui.Icon(glyph, 16),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(9, 7, 9, 7),
        };
        Ui.Label(button, label);
        return button;
    }

    // MARK: - Notes

    private void OnStoreChanged()
    {
        RefreshList();
        // A dictation with nowhere to go added to the note on screen: the editor follows, the cursor where it was.
        if (_selectedId is { } id && _store.Notes.Note(id) is { } note && FromBox(_editor.Text) != FromBox(note.Text))
        {
            var caret = _editor.SelectionStart;
            _editor.Text = ToBox(note.Text);
            _editor.Select(Math.Min(caret, _editor.Text.Length), 0);
        }
        if (_selectedId is { } shown && _store.Notes.Note(shown) is { Deleted: null } current) UpdateHeader(current);
    }

    /// <summary>The list as the notes are now: rebuilt when they're in another order, otherwise only the rows' text.</summary>
    private void RefreshList()
    {
        var notes = _store.Notes.Notes;
        var items = _list.Items.OfType<ListViewItem>().ToList();
        if (!notes.Select(n => (string?)n.Id).SequenceEqual(items.Select(i => i.Tag as string)))
        {
            _list.Items.Clear();
            foreach (var note in notes) _list.Items.Add(new ListViewItem { Tag = note.Id, Content = Row(note), Padding = new Thickness(12, 6, 12, 6) });
        }
        else
        {
            for (var i = 0; i < notes.Count; i++) items[i].Content = Row(notes[i]);
        }
        var selected = _list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == _selectedId);
        if (!ReferenceEquals(_list.SelectedItem, selected)) _list.SelectedItem = selected;
    }

    /// <summary>A note in the list: its first line, and when it was last edited.</summary>
    private static StackPanel Row(ScratchpadNote note)
    {
        var title = Ui.Text(note.IsBlank ? L("新笔记", "New Note") : note.Title, 13.5, FontWeights.Medium,
                            note.IsBlank ? Ui.Secondary : null);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxLines = 1;
        return new StackPanel
        {
            Spacing = 1,
            Children = { title, Ui.Text(Formatting.ShortStamp(note.Modified), 11.5, foreground: Ui.Secondary) },
        };
    }

    /// <summary>Shows <paramref name="id"/>'s note (none: null); moving on from a note never typed in drops it.</summary>
    private void Select(string? id)
    {
        _selectedId = id;
        _store.RemoveBlank(id);
        RefreshList();
        ShowNote();
    }

    private void ShowNote()
    {
        var note = _selectedId is { } id ? _store.Notes.Note(id) : null;
        if (note is null || note.Deleted != null)
        {
            _selectedId = null;
            _note.Visibility = Visibility.Collapsed;
            _empty.Visibility = Visibility.Visible;
            return;
        }
        _note.Visibility = Visibility.Visible;
        _empty.Visibility = Visibility.Collapsed;
        _editor.Text = ToBox(note.Text);
        _editor.Select(_editor.Text.Length, 0);
        UpdateHeader(note);
    }

    private void UpdateHeader(ScratchpadNote note)
    {
        _edited.Text = L($"编辑于 {Formatting.ShortStamp(note.Modified)}", $"Edited {Formatting.ShortStamp(note.Modified)}");
        ToolTipService.SetToolTip(_edited, L($"创建于 {Formatting.ShortStamp(note.Created)}", $"Created {Formatting.ShortStamp(note.Created)}"));
        _copy.IsEnabled = !note.IsBlank;
    }

    private void UpdateUndoBar() => _undoBar.Visibility = _deletedId != null ? Visibility.Visible : Visibility.Collapsed;

    private void FocusEditor()
    {
        if (_selectedId == null) return;
        _editor.Focus(FocusState.Programmatic);
        _editor.Select(_editor.Text.Length, 0);
    }

    private void NewNote()
    {
        _deletedId = null;
        UpdateUndoBar();
        Select(_store.Add());
        FocusEditor();
    }

    private void CopyNote()
    {
        if (_selectedId is { } id && _store.Notes.Note(id) is { IsBlank: false } note) TextInserter.CopyToClipboard(note.Text);
    }

    /// <summary>Takes the note off the list, with Undo under the list to bring it back; the next note down is selected.</summary>
    private void DeleteNote()
    {
        if (_selectedId is not { } id || _store.Notes.Note(id) is not { } note) return;
        var index = Math.Max(0, _store.Notes.Notes.FindIndex(n => n.Id == id));
        _store.Delete(id);
        var rest = _store.Notes.Notes;
        // An empty note goes without a trace; one with text can be brought back.
        _deletedId = note.IsBlank ? null : id;
        UpdateUndoBar();
        Select(rest.Count == 0 ? null : rest[Math.Min(index, rest.Count - 1)].Id);
    }

    private void Undo()
    {
        if (_deletedId is not { } id) return;
        _deletedId = null;
        UpdateUndoBar();
        _store.Restore(id);
        Select(id);
    }

    // MARK: - Dictation

    private bool Insert(string text, bool fit, IReadOnlyList<string> vocabulary)
    {
        if (!AppWindow.IsVisible || Win32.GetForegroundWindow() != Hwnd || _selectedId == null || _editor.XamlRoot == null
            || !ReferenceEquals(FocusManager.GetFocusedElement(_editor.XamlRoot), _editor))
        {
            return false;
        }
        var box = _editor.Text;
        var start = Math.Clamp(_editor.SelectionStart, 0, box.Length);
        var length = Math.Clamp(_editor.SelectionLength, 0, box.Length - start);
        var inserted = text;
        if (fit)
        {
            // A dozen characters each side, without cutting a character in half.
            var first = Math.Max(0, start - 12);
            if (first > 0 && char.IsLowSurrogate(box[first])) first--;
            var end = Math.Min(box.Length, start + length + 8);
            if (end < box.Length && char.IsLowSurrogate(box[end])) end++;
            inserted = InsertionJoiner.Fit(text, FromBox(box[first..start]), FromBox(box[(start + length)..end]), vocabulary,
                                           SpellChecker.IsCommonWord).Text;
        }
        var typed = ToBox(inserted);
        _editor.SelectedText = typed;
        _editor.Select(start + typed.Length, 0);
        return true;
    }

    /// <summary>A text box keeps line breaks as "\r"; the notes keep "\n", as on macOS.</summary>
    private static string FromBox(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string ToBox(string text) => text.Replace("\r\n", "\r").Replace('\n', '\r');

    private int Scaled(double dips) => (int)Math.Round(dips * Win32.GetDpiForWindow(Hwnd) / 96.0);

    private void Center()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
