using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TeklaBackupManager;

public partial class App : System.Windows.Application
{
    private ExclusiveLease? profileLease;

    internal void EnsureProfileHeld() =>
        (profileLease ?? throw new InvalidOperationException("Profile lock is not held.")).EnsureHeld();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeklaBackupManager");
            profileLease = ExclusiveLease.Acquire(profile, ".tekla-profile.lock");
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            profileLease?.Dispose();
            profileLease = null;
            LogStartupError(ex);
            System.Windows.MessageBox.Show(
                "Demarrage refuse : une autre instance utilise peut-etre ce profil, ou son verrou ne peut pas etre garanti.\nStartup refused: this profile may already be in use or cannot be locked.\n\nDetails: %AppData%\\TeklaBackupManager\\startup-error.log",
                "Tekla Backup Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { profileLease?.Dispose(); profileLease = null; }
        finally { base.OnExit(e); }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogStartupError(e.Exception);
        System.Windows.MessageBox.Show(
            "Une erreur inattendue est survenue. Le detail est enregistre dans %AppData%\\TeklaBackupManager\\startup-error.log.",
            "Tekla Backup Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogStartupError(ex);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogStartupError(e.Exception);
        e.SetObserved();
    }

    private static void LogStartupError(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TeklaBackupManager");
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "startup-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Last-resort logging must never hide the original startup error.
        }
    }
}
