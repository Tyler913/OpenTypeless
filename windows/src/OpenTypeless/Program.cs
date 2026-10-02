using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenTypeless;

public static class Program
{
    private const string MutexName = @"Local\OpenTypeless.SingleInstance";
    private const string ActivateEventName = @"Local\OpenTypeless.Activate";

    /// <summary><c>OpenTypeless.exe --snapshot-ui &lt;dir&gt;</c> renders every settings page, the popover and the HUD states to PNGs.</summary>
    public static string? SnapshotDirectory { get; private set; }

    internal static int SnapshotExitCode { get; set; }

    /// <summary>Started by the login Run entry rather than by the user.</summary>
    public static bool LaunchedAtLogin { get; private set; }

    /// <summary>Raised (on a background thread) when a second copy of the app is launched.</summary>
    public static event Action? ActivationRequested;

    [STAThread]
    public static int Main(string[] args)
    {
        // Started by the updater from a freshly downloaded copy: install it over the running one, no UI.
        if (args.Contains(Services.UpdateInstaller.Argument)) return Services.UpdateInstaller.Run(args);

        var snapshot = Array.IndexOf(args, "--snapshot-ui");
        if (snapshot >= 0 && snapshot + 1 < args.Length) SnapshotDirectory = Path.GetFullPath(args[snapshot + 1]);
        LaunchedAtLogin = args.Contains("--autostart");

        Mutex? mutex = null;
        if (SnapshotDirectory == null)
        {
            // One copy per user session: launching again just brings up the settings of the running one.
            mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                try
                {
                    using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                    activate.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            new Thread(() =>
            {
                while (signal.WaitOne()) ActivationRequested?.Invoke();
            }) { IsBackground = true, Name = "OpenTypeless activation" }.Start();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        GC.KeepAlive(mutex);
        return SnapshotExitCode;
    }
}
