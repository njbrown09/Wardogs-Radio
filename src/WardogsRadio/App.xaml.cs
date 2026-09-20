using System.Windows;
using System.Windows.Threading;
using WardogsRadio.Setup;

namespace WardogsRadio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode: no window, just do the driver setup and report back via exit code.
        if (e.Args.Contains("--elevated-setup", StringComparer.OrdinalIgnoreCase))
        {
            int code;
            try { code = VbCableInstaller.RunElevatedSetup(); }
            catch { code = VbCableInstaller.ExitCodes.RenameFailed; }
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Wardogs Radio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
