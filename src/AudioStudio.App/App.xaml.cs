using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AudioStudio.App;

public partial class App : Application
{
    /// <summary>
    /// Beside the executable itself. AppContext.BaseDirectory would point at the
    /// self-extraction temp folder for a single-file build, where nobody would
    /// ever find the log.
    /// </summary>
    private static readonly string CrashLogPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "AudioStudio-crash.log");

    public App()
    {
        // A failure while XAML is loading happens before any window exists, so
        // there is nowhere to show it. Write it down instead.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception);
    }

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.WriteAllText(CrashLogPath,
                $"{DateTimeOffset.Now:O}\n{ex}\n\nInner: {ex.InnerException}");
        }
        catch (IOException) { /* nothing useful left to do */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A crash inside a background render or analysis task should surface as
        // a message, not vanish or take the window down with it.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        MessageBox.Show(
            $"{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "Something went wrong",
            MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
