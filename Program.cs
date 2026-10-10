using System.Windows;

namespace JensTV;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        using var mutex = new Mutex(true, @"Local\JensTV.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
            return 0;

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

        Log.Info("Started");
        return app.Run(window);
    }
}
