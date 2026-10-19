using System;
using System.Windows;

static class App
{
    public const string Name = "העברת הספרייה של זית";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest.Run(args[1]);
        if (args.Length >= 3 && args[0] == "--prefs") return SelfTest.PrefsIo(args[1], args[2]);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, Name, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            e.Handled = true;
        };
        return app.Run(new MainWindow());
    }
}
