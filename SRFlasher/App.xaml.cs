using SRFlasher.Services;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SRFlasher
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Carry user settings over from the pre-rename app once, before
            // anything reads them (theme, window bounds, recents, batch queue).
            SettingsMigration.Run();

            // Single instance: two copies fight over the same adb server and
            // can flash each other's devices.
            _instanceMutex = new Mutex(true, @"Global\SRFlasher.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "SR Flasher is already running.\n\n" +
                    "Only one instance can talk to the device — check the taskbar or system tray.",
                    "SR Flasher", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Crash safety: a failure mid-flash must leave a trace, never
            // vanish silently. All three pipelines funnel into one writer.
            DispatcherUnhandledException += (s, args) =>
            {
                WriteCrashLog(args.Exception, "UI thread");
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                WriteCrashLog(args.Exception, "Unobserved task");
                args.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrashLog(args.ExceptionObject as Exception, "Fatal");

            // Prune old daily logs so %APPDATA% does not grow forever
            // (retention configurable on the settings page, 7-365 days).
            try { LoggingService.Instance.PruneOldLogs(SRFlasher.Properties.Settings.Default.LogRetentionDays); }
            catch { LoggingService.Instance.PruneOldLogs(30); }

            // Apply the saved theme BEFORE any window is created, so resource
            // lookups in MainWindow.xaml resolve against the theme dictionary.
            var _ = ThemeManager.Instance;

            base.OnStartup(e);
        }

        /// <summary>
        /// Writes one exception to crash-yyyy-MM-dd.log under the app data
        /// folder (kept beyond the 30-day prune window) and to the normal log.
        /// Never throws — it runs inside error handling already.
        /// </summary>
        private static void WriteCrashLog(Exception ex, string source)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SRFlasher", "Logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(file,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + source + ": " + ex + Environment.NewLine);
            }
            catch { /* crash logging must never throw */ }

            try
            {
                LoggingService.Instance.Fatal("Unhandled exception (" + source + "): " + ex);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            // Smart ADB cleanup: stop the adb server this app started so it
            // doesn't linger in the background after the window is gone.
            // Best-effort — a missing adb must not block shutdown.
            try
            {
                var adb = SRFlasher.Services.ToolPathResolver.GetToolPath("adb");
                if (!string.IsNullOrWhiteSpace(adb) && System.IO.File.Exists(adb))
                {
                    using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = adb,
                        Arguments = "kill-server",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        process?.WaitForExit(3000);
                    }
                }
            }
            catch { }
            base.OnExit(e);
        }
    }
}
