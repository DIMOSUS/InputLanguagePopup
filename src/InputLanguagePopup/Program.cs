using System;
using System.IO;
using System.Threading;
using InputLanguagePopup.Diagnostics;
using InputLanguagePopup.Settings;
using static InputLanguagePopup.Interop.Win32Ui;

namespace InputLanguagePopup;

internal static class Program
{
    private const string AppFolderName = "InputLanguagePopup";

    // Session-local (not Global\): one instance *per user session*, so Fast User
    // Switching / RDP sessions each get their own tray indicator.
    private const string SingleInstanceMutexName = "Local\\InputLanguagePopup_SingleInstance_{2F3C0B0A-9A1E-4B7A-9C1D-4A9F0E1B2C3D}";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Run();
            return;
        }

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

        // Per-Monitor V2 DPI awareness comes from app.manifest (read by the OS before
        // any managed code runs), so there is nothing to set here.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);

        try
        {
            var settingsService = new SettingsService(appDataDir, logger);
            var settings = settingsService.Load();

            using var app = new TrayApplication(logger, settingsService, settings);
            RunMessageLoop(logger);
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

    /// <summary>
    /// The classic Win32 message pump (replaces <c>Application.Run</c>). Exits when
    /// a window posts WM_QUIT.
    /// </summary>
    private static void RunMessageLoop(Logger logger)
    {
        while (true)
        {
            var result = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (result == 0)
            {
                return; // WM_QUIT
            }

            if (result == -1)
            {
                logger.Error($"GetMessage failed. Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
                return;
            }

            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }
}
