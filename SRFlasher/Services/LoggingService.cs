using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SRFlasher.Services
{
    /// <summary>
    /// Structured logging service for consistent, leveled logging throughout the application.
    /// This is a simplified version that doesn't depend on external libraries to ensure compatibility.
    /// </summary>
    public class LoggingService
    {
        private static LoggingService _instance;
        private readonly string _logFilePath;
        private readonly bool _enableFileLogging;

        public enum LogLevel
        {
            Verbose,
            Debug,
            Information,
            Warning,
            Error,
            Fatal
        }

        private LoggingService()
        {
            _logFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SRFlasher",
                "Logs",
                $"app-{DateTime.Now:yyyy-MM-dd}.log"
            );

            _enableFileLogging = true;
            EnsureLogDirectoryExists();
        }

        /// <summary>
        /// Gets the singleton instance of the logging service.
        /// </summary>
        public static LoggingService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (typeof(LoggingService))
                    {
                        if (_instance == null)
                        {
                            _instance = new LoggingService();
                        }
                    }
                }
                return _instance;
            }
        }

        private void EnsureLogDirectoryExists()
        {
            try
            {
                string directory = Path.GetDirectoryName(_logFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create log directory: {ex.Message}");
            }
        }

        private void WriteLog(LogLevel level, string messageTemplate, params object[] propertyValues)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string levelStr = level.ToString().ToUpper().PadRight(4);

                string message = messageTemplate;
                if (propertyValues != null && propertyValues.Length > 0)
                {
                    try
                    {
                        message = string.Format(messageTemplate, propertyValues);
                    }
                    catch
                    {
                        // string.Format fails when the template contains literal braces
                        // (e.g. "{Message}"). Keep the template and append the values so
                        // nothing is lost.
                        message = messageTemplate;
                        if (propertyValues != null && propertyValues.Length > 0)
                        {
                            message += " [" + string.Join(", ", propertyValues.Select(v => v?.ToString() ?? "null")) + "]";
                        }
                    }
                }

                string logEntry = $"{timestamp} [{levelStr}] {message}";

                // Write to debug output
                System.Diagnostics.Debug.WriteLine(logEntry);

                // Write to file if enabled
                if (_enableFileLogging)
                {
                    try
                    {
                        File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
                    }
                    catch { /* Ignore file write errors */ }
                }
            }
            catch { /* Silently fail */ }
        }

        /// <summary>
        /// Log a verbose message.
        /// </summary>
        public void Verbose(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Verbose, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Log a debug message.
        /// </summary>
        public void Debug(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Debug, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Log an informational message.
        /// </summary>
        public void Information(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Information, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Log a warning message.
        /// </summary>
        public void Warning(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Warning, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Log an error message.
        /// </summary>
        public void Error(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Error, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Log a fatal error message.
        /// </summary>
        public void Fatal(string messageTemplate, params object[] propertyValues)
        {
            WriteLog(LogLevel.Fatal, messageTemplate, propertyValues);
        }

        /// <summary>
        /// Get the path to the current log file.
        /// </summary>
        public string GetLogFilePath()
        {
            return _logFilePath;
        }

        /// <summary>
        /// Get the directory path where logs are stored.
        /// </summary>
        public string GetLogDirectory()
        {
            return Path.GetDirectoryName(_logFilePath);
        }

        /// <summary>
        /// Flush any pending log entries.
        /// </summary>
        public void Flush()
        {
            // No-op for file-based logging
        }

        /// <summary>
        /// Deletes daily app logs older than <paramref name="keepDays"/> so
        /// the log folder does not grow forever. Crash logs (crash-*.log) are
        /// never pruned by this.
        /// </summary>
        public void PruneOldLogs(int keepDays = 30)
        {
            try
            {
                var dir = GetLogDirectory();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var file in Directory.GetFiles(dir, "app-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        try { File.Delete(file); }
                        catch { /* in use or permission — keep it, retry next startup */ }
                    }
                }
            }
            catch
            {
                // Pruning is housekeeping — a failure here must never block startup.
            }
        }
    }
}
