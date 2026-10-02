using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenTypeless.Native;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OpenTypeless.UI;

/// <summary>
/// <c>OpenTypeless.exe --snapshot-ui &lt;dir&gt;</c> shows every settings page (in Chinese and English), the tray
/// popover and each HUD state, and saves them as PNGs, for checking the UI without clicking through it.
/// <c>--lang en|zh|ja|…</c> renders one language only; <c>--demo</c> treats permissions as granted (pair it with
/// OPENTYPELESS_DATA_DIR pointing at sample history, so no real dictations end up in screenshots).
/// </summary>
public static class UISnapshots
{
    public static async Task Run(string directory)
    {
        var args = Environment.GetCommandLineArgs();
        Permissions.AssumeGranted = args.Contains("--demo");
        var lang = Array.IndexOf(args, "--lang") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        AppLanguage[] languages = Localization.ParseLanguage(lang) is var one and not AppLanguage.System
            ? [one]
            : [AppLanguage.Zh, AppLanguage.En];
        var settings = AppSettings.Shared;
        var original = settings.AppLanguage;
        try
        {
            Directory.CreateDirectory(directory);
            var controller = new SessionController();
            foreach (var language in languages)
            {
                settings.AppLanguage = language;
                var window = new SettingsWindow(controller);
                foreach (var page in SettingsPageInfo.All)
                {
                    window.Show(page);
                    await Task.Delay(900);
                    await Capture((FrameworkElement)window.Content, Path.Combine(directory, $"settings-{language.RawValue()}-{page.RawValue()}.png"));
                }
                window.CloseForExit();

                foreach (var step in Enum.GetValues<OnboardingStep>())
                {
                    var onboarding = new OnboardingWindow(controller, () => { }, step);
                    onboarding.Show();
                    await Task.Delay(900);
                    await Capture((FrameworkElement)onboarding.Content, Path.Combine(directory, $"onboarding-{language.RawValue()}-{(int)step + 1}.png"));
                    onboarding.CloseForExit();
                }

                var popover = new PopoverWindow(controller, _ => { }, () => { });
                var (work, _, _) = WindowHelpers.MonitorAtCursor();
                popover.Open(new Win32.RECT { Left = work.Right - 200, Right = work.Right - 180, Top = work.Bottom + 4, Bottom = work.Bottom + 30 });
                await Task.Delay(900);
                await Capture((FrameworkElement)popover.Content, Path.Combine(directory, $"popover-{language.RawValue()}.png"));
                popover.Dismiss();
            }

            var hud = controller.Hud;
            hud.Model.SetLevels(Enumerable.Range(0, 18).Select(i => (float)Math.Abs(Math.Sin(i * 0.7)) * 0.8f + 0.1f).ToArray());
            hud.Model.StartedAt = DateTimeOffset.Now.AddSeconds(-83);
            (string Name, HudPhase Phase)[] phases =
            [
                ("hud-recording", new HudPhase.Recording()),
                ("hud-working", new HudPhase.Working()),
                ("hud-copied", new HudPhase.Copied()),
                ("hud-learned", new HudPhase.Learned("Typeless、罗技")),
                ("hud-error", new HudPhase.Error(L("转写失败，可在托盘菜单重试", "Failed — retry from the tray menu"))),
            ];
            foreach (var (name, phase) in phases)
            {
                hud.Show(phase);
                await Task.Delay(600);
                await Capture((FrameworkElement)hud.Window.Content, Path.Combine(directory, name + ".png"));
            }
            hud.Show(new HudPhase.Hidden());
            Console.WriteLine($"✓ snapshots in {directory}");
        }
        catch (Exception error)
        {
            Program.SnapshotExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), error.ToString());
        }
        finally
        {
            settings.AppLanguage = original;
            Application.Current.Exit();
        }
    }

    private static async Task Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            throw new InvalidOperationException($"The UI rendered no pixels: {path}");
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        // Backdrops (Mica, Acrylic) aren't part of the XAML tree, so composite over the theme's window colour.
        var background = element.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] + background * (255 - alpha) / 255);
            pixels[i + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = element.XamlRoot?.RasterizationScale * 96 ?? 96;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                             (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
