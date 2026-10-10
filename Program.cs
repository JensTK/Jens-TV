using System.Windows;
using JensTV.Native;

namespace JensTV;

public static class Program
{
    [STAThread]
    public static int Main()
    {
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
}
