using System.Windows;

namespace TimeHero.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, @"Local\TimeHero.SingleInstance", out var isFirst);
        if (!isFirst) return; // già in esecuzione: l'icona è nella tray

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "TimeHero — errore", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        var host = new TrayApp();
        app.Run();
        host.Store.Dispose();
    }
}
