using System.Windows;

namespace TillPOS.App;

public partial class App : Application
{
    private AppHost? host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--protect-secret", var secret])
        {
            Clipboard.SetText(SecretProtector.Protect(secret));
            MessageBox.Show("The protected secret was copied to the clipboard. Paste it into settings.json as ApiSecretProtected.", "TillPOS");
            Shutdown();
            return;
        }

        TillSettings settings;
        try
        {
            settings = TillSettings.Load(TillSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be read from {TillSettings.DefaultPath}:\n{ex.Message}", "TillPOS");
            Shutdown(1);
            return;
        }

        // Last resort: an unexpected error in a screen must not close the till in front of a customer.
        // Bills are saved before printing, so showing the error and carrying on is safe.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(settings, args.Exception);
            MessageBox.Show($"Something went wrong: {args.Exception.Message}\nThe till keeps running. Tell a supervisor if it repeats.", "TillPOS");
            args.Handled = true;
        };

        try
        {
            var window = new MainWindow();
            host = new AppHost(settings, Dispatcher, new WpfDialogs(window), ex => LogError(settings, ex));
            window.DataContext = host.Shell;
            MainWindow = window;
            window.Show();
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            LogError(settings, ex);
            MessageBox.Show($"TillPOS could not start:\n{ex.Message}", "TillPOS");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        host?.Stop();
        base.OnExit(e);
    }

    private static void LogError(TillSettings settings, Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settings.DbPath)!, "errors.log");
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:O} {ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never take the till down.
        }
    }
}
