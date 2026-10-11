using System.Windows;
using JensTV.Native;

namespace JensTV;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Diagnostics: JensTV.exe --tv-key KEY_HDMI2 [KEY_...] sends remote keys to the configured Samsung TV.
        if (args.Length > 1 && args[0] == "--tv-key")
            return SendTvKeys(args[1..]);

        using var mutex = new Mutex(true, @"Local\JensTV.SingleInstance", out var isFirstInstance);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\JensTV.Activate");
        if (!isFirstInstance)
        {
            // Started again (e.g. via the launch hotkey): let the running instance take focus and show home.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            activate.Set();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // A TV box shouldn't crash to the desktop over one bad call; log it and carry on.
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };

        var config = ConfigStore.Load(out var configError);
        var window = new MainWindow(config, configError);
        app.SessionEnding += (_, _) => window.ShutDown();

        var wait = ThreadPool.RegisterWaitForSingleObject(activate,
            (_, _) => window.Dispatcher.InvokeAsync(window.ShowHome), null, Timeout.Infinite, executeOnlyOnce: false);

        Log.Info("Started");
        var exitCode = app.Run(window);
        wait.Unregister(null);
        return exitCode;
    }

    static int SendTvKeys(string[] keys)
    {
        var tv = ConfigStore.Load(out _).SamsungTv;
        if (tv?.Host == null)
        {
            Log.Error("--tv-key: no samsungTv.host in the config");
            return 2;
        }
        try
        {
            SamsungTv.SendKeysAsync(tv.Host, tv.Token, keys).GetAwaiter().GetResult();
            Log.Info($"--tv-key: sent {string.Join(", ", keys)}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("--tv-key failed", ex);
            return 1;
        }
    }
}
