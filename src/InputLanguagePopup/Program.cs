using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Settings;

namespace InputLanguagePopup;

internal static class Program
{
    private const string AppFolderName = "InputLanguagePopup";
    private const string SingleInstanceMutexName = "Global\\InputLanguagePopup_SingleInstance_{2F3C0B0A-9A1E-4B7A-9C1D-4A9F0E1B2C3D}";

    [STAThread]
    private static void Main()
    {
        // Ensure only one instance runs (multiple hooks would be wasteful/confusing).
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
        Directory.CreateDirectory(appDataDir);

        var logger = new Logger(appDataDir);

        // Manifest already sets Per-Monitor V2, but keep the WinForms mode in sync.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Log otherwise-unhandled exceptions instead of crashing silently.
        Application.ThreadException += (_, e) =>
            logger.Error("Unhandled UI thread exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            var settingsService = new SettingsService(appDataDir, logger);
            var settings = settingsService.Load();

            using var context = new TrayApplicationContext(logger, settingsService, settings);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.Error("Fatal error in Main.", ex);
            throw;
        }
        finally
        {
            GC.KeepAlive(mutex);
        }
    }
}
