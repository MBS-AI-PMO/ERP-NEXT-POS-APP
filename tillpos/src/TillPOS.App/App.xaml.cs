using System.Diagnostics;
using System.Reflection;
using System.Windows;
using TillPOS.App.Dialogs;

namespace TillPOS.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Global\TillPOS.SingleInstance";
    // Held for the life of the process; Windows releases it when the process exits (Restart releases it early).
    private static Mutex? singleInstance;
    private AppHost? host;

    /// <summary>The build, e.g. "0.3.1-field", shown on the login screen and in errors.log so field feedback can name it.</summary>
    public static string Version { get; } = ReadVersion();

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

        if (!TryClaimSingleInstance())
        {
            MessageBox.Show("TillPOS is already running.", "TillPOS");
            Shutdown(0);
            return;
        }

        TillSettings? resolved;
        try
        {
            resolved = SettingsStore.Resolve(LogError);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be read from {SettingsStore.ProgramDataPath}:\n{ex.Message}", "TillPOS");
            Shutdown(1);
            return;
        }
        if (resolved is null)
        {
            MessageBox.Show($"Settings not found. Put settings.json in {SettingsStore.ProgramDataPath} or next to TillPOS.exe.", "TillPOS");
            Shutdown(1);
            return;
        }
        var settings = resolved;

        // Last resort: an unexpected error in a screen must not close the till in front of a customer.
        // Bills are saved before printing, so showing the error and carrying on is safe.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(settings, args.Exception);
            MessageBox.Show($"Something went wrong: {args.Exception.Message}\nThe till keeps running. Tell a supervisor if it repeats.", "TillPOS");
            args.Handled = true;
        };

        // WPF makes the first window created the MainWindow, so with OnMainWindowClose closing the setup window would end
        // the app before the till window exists. Shut down only explicitly until the till window is the MainWindow.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!settings.SetupDone)
        {
            var setup = new SetupDialog(settings, firstRun: true);
            if (setup.ShowDialog() != true || setup.Result is null)
            {
                Shutdown(0);
                return;
            }
            settings = setup.Result; // already saved; the till has not been built yet, so no restart is needed
        }

        try
        {
            var window = new MainWindow();
            // The invoice popup asks for the header only after a sale, when the host (and POS settings) exist.
            var dialogs = new WpfDialogs(window, () => (host!.Output.Header(), settings.PaperWidth), Restart);
            host = new AppHost(settings, Dispatcher, dialogs, ex => LogError(settings, ex));
            host.Shell.Version = Version;
            window.DataContext = host.Shell;
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
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

    /// <summary>Two copies on one till would share the database, printer and drawer; only the first may run.</summary>
    private static bool TryClaimSingleInstance()
    {
        try
        {
            singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // another Windows user's session already holds it
        }
    }

    /// <summary>Starts a fresh copy (same exe, inherited environment incl. TILLPOS_SETTINGS) and closes this one. The
    /// single-instance mutex is released first so the new copy is not refused.</summary>
    private void Restart()
    {
        try
        {
            singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing below still lets Windows hand it over when this process exits.
        }
        singleInstance?.Dispose();
        singleInstance = null;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings were saved, but TillPOS could not restart itself ({ex.Message}). Start it again.", "TillPOS");
        }
        Shutdown();
    }

    /// <summary>The informational version without the "+commit" suffix the SDK may append.</summary>
    private static string ReadVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    private static void LogError(TillSettings settings, Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settings.DbPath)!, "errors.log");
            var lines = $"{DateTimeOffset.Now:O} {ex}".Split(["\r\n", "\n"], StringSplitOptions.None);
            System.IO.File.AppendAllLines(path, lines.Select(line => $"[{Version}] {line}"));
        }
        catch (Exception)
        {
            // Logging must never take the till down.
        }
    }
}
