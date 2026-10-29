using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Services;
using TypelessCore;
using Windows.Storage.Pickers;

namespace OpenTypeless.UI;

/// <summary>
/// Saving History, a recording or the vocabulary to a file the user picks, and reading a vocabulary file back. Files
/// are written off the UI thread, so thousands of dictations never freeze the window, and atomically (a temporary file
/// that then takes the old one's place), so a failure never leaves half a file. A failure is shown in a dialog in the
/// app's words, never a crash.
/// </summary>
public static class Exports
{
    /// <summary>A dialog is open: a second ShowAsync while one is would throw.</summary>
    private static bool _dialogOpen;

    /// <summary>"OpenTypeless History 2026-10-12", "OpenTypeless 2026-10-12 14-32-05": the name the save dialog suggests.</summary>
    /// <remarks>
    /// Dashes in the time, as on macOS: Windows refuses a colon, and with dots the save dialog would take ".05" for the
    /// extension and not add the chosen one.
    /// </remarks>
    public static string FileName(string stem, DateTimeOffset? date = null, bool time = false) =>
        date is { } d ? stem + " " + d.ToLocalTime().ToString(time ? "yyyy-MM-dd HH-mm-ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture) : stem;

    /// <summary>
    /// Asks where to save, makes the file's contents off the UI thread and writes them. True when it was saved; a dialog
    /// says what went wrong otherwise (nothing happens when the user cancels).
    /// </summary>
    public static Task<bool> Save(FrameworkElement owner, nint hwnd, string suggestedName, string typeLabel, string extension,
                                  Func<byte[]> contents, string failureTitle) =>
        WriteTo(owner, hwnd, suggestedName, typeLabel, extension, path => SafeFile.WriteAllBytes(path, contents()), failureTitle);

    /// <summary>Asks where to save a copy of a file (a recording), then copies it there off the UI thread. True when it was saved.</summary>
    public static Task<bool> Copy(FrameworkElement owner, nint hwnd, string suggestedName, string typeLabel, string extension,
                                  string source, string failureTitle) =>
        WriteTo(owner, hwnd, suggestedName, typeLabel, extension, path => SafeFile.Copy(source, path), failureTitle);

    private static async Task<bool> WriteTo(FrameworkElement owner, nint hwnd, string suggestedName, string typeLabel, string extension,
                                            Action<string> write, string failureTitle)
    {
        var path = await ChooseDestination(owner, hwnd, suggestedName, typeLabel, extension);
        if (path == null) return false;
        try
        {
            await Task.Run(() => write(path));
            return true;
        }
        catch (Exception e)
        {
            // Any failure, not only a file error (making the file's contents, say): one that reached the button's async
            // click handler would close the app.
            AppLog.Debug("export", $"can't write {path}: {e.GetType().Name}: {e.Message}");
            await ShowDialog(owner.XamlRoot, failureTitle, IsFileError(e) ? Message(e) : e.Message);
            return false;
        }
    }

    /// <summary>The path the user chose to save to, or null when they cancelled or the dialog couldn't open (which it says).</summary>
    private static async Task<string?> ChooseDestination(FrameworkElement owner, nint hwnd, string suggestedName, string typeLabel, string extension)
    {
        string failure;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
            };
            picker.FileTypeChoices.Add(typeLabel, new List<string> { "." + extension });
            // An unpackaged app has to tell the picker which window it belongs to.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return null;
            if (!string.IsNullOrEmpty(file.Path)) return file.Path;
            // A place with no path on disk (a library's virtual folder, a phone): nothing a file can be written to.
            failure = NotOnThisPC;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // The picker refuses to open when the app runs as administrator.
            AppLog.Debug("export", "can't show the save dialog: " + e.Message);
            failure = PickerUnavailable;
        }
        await ShowDialog(owner.XamlRoot, L("无法打开文件对话框", "The file dialog couldn't open"), failure);
        return null;
    }

    /// <summary>The file the user chose to open, or null when they cancelled or the dialog couldn't open (which it says).</summary>
    public static async Task<string?> ChooseFile(FrameworkElement owner, nint hwnd, params string[] extensions)
    {
        string failure;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            foreach (var extension in extensions) picker.FileTypeFilter.Add("." + extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return null;
            if (!string.IsNullOrEmpty(file.Path)) return file.Path;
            failure = NotOnThisPC;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AppLog.Debug("export", "can't show the open dialog: " + e.Message);
            failure = PickerUnavailable;
        }
        await ShowDialog(owner.XamlRoot, L("无法打开文件对话框", "The file dialog couldn't open"), failure);
        return null;
    }

    /// <summary>Reads a vocabulary file off the UI thread: its terms, or null and what to tell the user.</summary>
    public static Task<(List<string>? Terms, string? Failure)> ReadVocabulary(string path) => Task.Run(() => ReadVocabularyFile(path));

    private static (List<string>? Terms, string? Failure) ReadVocabularyFile(string path)
    {
        try
        {
            // A vocabulary is a few KB; anything this big was picked by mistake (a History export, say).
            if (new FileInfo(path).Length > 10_000_000) return (null, L("这个文件太大了，不像是词汇表。", "That file is too big to be a vocabulary list."));
            var text = VocabularyTransfer.Decode(File.ReadAllBytes(path));
            if (text == null)
            {
                return (null, L("这个文件不是 UTF-8 文本。请另存为 UTF-8 格式（Excel 里选择“CSV UTF-8”）后再导入。",
                                "This file isn't UTF-8 text. Save it as UTF-8 (in Excel, choose “CSV UTF-8”) and import it again."));
            }
            return (VocabularyTransfer.TermsFromFile(text, Path.GetExtension(path).TrimStart('.')), null);
        }
        catch (Exception e)
        {
            // Any failure, not only a file error: one that reached the button's async click handler would close the app.
            AppLog.Debug("export", $"can't read {path}: {e.GetType().Name}: {e.Message}");
            return (null, IsFileError(e) ? Message(e) : e.Message);
        }
    }

    private static bool IsFileError(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;

    /// <summary>The usual failures in the app's words; anything else as the system describes it.</summary>
    public static string Message(Exception e) => e switch
    {
        UnauthorizedAccessException or SecurityException => L("没有权限使用这个位置，换个文件夹试试。", "No permission to use that location. Try another folder."),
        FileNotFoundException or DirectoryNotFoundException => L("文件已经不在了。", "The file is no longer there."),
        // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        IOException when (e.HResult & 0xFFFF) is 39 or 112 => L("磁盘空间不足。", "There isn't enough space on the disk."),
        // ERROR_WRITE_PROTECT
        IOException when (e.HResult & 0xFFFF) is 19 => L("这个位置是只读的，换个文件夹试试。", "That location is read-only. Try another folder."),
        _ => e.Message,
    };

    private static string PickerUnavailable =>
        L("以管理员身份运行 OpenTypeless 时，Windows 不允许打开文件对话框。请以普通方式重新打开 OpenTypeless。",
          "Windows doesn't allow it while OpenTypeless runs as administrator. Open OpenTypeless normally and try again.");

    private static string NotOnThisPC => L("请选择这台电脑上的文件夹。", "Choose a folder on this PC.");

    /// <summary>A message with OK, or a question with <paramref name="primary"/> and Cancel. True when the primary button was chosen.</summary>
    public static async Task<bool> ShowDialog(XamlRoot? root, string title, string message, string? primary = null)
    {
        if (root == null || _dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = primary == null ? L("好", "OK") : L("取消", "Cancel"),
                DefaultButton = primary == null ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };
            if (primary != null) dialog.PrimaryButtonText = primary;
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // Another dialog (History's delete confirmation, say) is already open.
            AppLog.Debug("export", "can't show a dialog: " + e.Message);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
