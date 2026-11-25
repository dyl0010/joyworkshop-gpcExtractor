using System;
using System.Windows;

namespace GpcSessionBrowser;

public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        var app = new App
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        app.Run(new MainWindow());
    }
}
