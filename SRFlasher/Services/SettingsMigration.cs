using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Xml;

namespace SRFlasher.Services
{
    /// <summary>
    /// One-time carry-over of user settings from the pre-rename app.
    /// .NET stores User-scope settings per product and exe name
    /// (%LOCALAPPDATA%\CustomROMFlashingTool\CustomROMFlashingTool.exe_Url_*\1.0.0.0\user.config),
    /// so renaming the app pointed it at an empty settings file and silently reset
    /// theme, window bounds, recent files and the batch queue. This copies the old
    /// values across the first time the renamed app runs, then never again.
    /// </summary>
    internal static class SettingsMigration
    {
        private const string LegacyProduct = "CustomROMFlashingTool";

        public static void Run()
        {
            try
            {
                var targetPath = ConfigurationManager
                    .OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal)
                    .FilePath;

                // Settings already exist under the new name: the user has run SR Flasher
                // before (or cleared them on purpose), so leave them alone.
                if (File.Exists(targetPath))
                {
                    return;
                }

                var legacyPath = FindLegacyConfig();
                if (legacyPath == null)
                {
                    return;
                }

                var doc = new XmlDocument();
                doc.Load(legacyPath);

                var settings = Properties.Settings.Default;
                var applied = 0;
                var nodes = doc.SelectNodes("//setting");
                if (nodes != null)
                {
                    foreach (XmlNode node in nodes)
                    {
                        var nameAttr = node.Attributes == null ? null : node.Attributes["name"];
                        if (nameAttr == null)
                        {
                            continue;
                        }

                        var valueNode = node["value"];
                        var raw = valueNode == null ? string.Empty : valueNode.InnerText;
                        if (Apply(settings, nameAttr.Value, raw))
                        {
                            applied++;
                        }
                    }
                }

                if (applied == 0)
                {
                    return;
                }

                settings.Save();
                // positional placeholders: LoggingService formats with string.Format
                LoggingService.Instance.Information(
                    "Carried {0} settings over from the previous app name ({1})", applied, legacyPath);
            }
            catch (Exception ex)
            {
                // A failed carry-over must never block startup: the app just starts on defaults.
                try
                {
                    LoggingService.Instance.Warning("Settings carry-over skipped: {0}", ex.Message);
                }
                catch
                {
                    // logging itself is best-effort here
                }
            }
        }

        private static bool Apply(Properties.Settings settings, string name, string raw)
        {
            switch (name)
            {
                case "Theme": settings.Theme = raw; return true;
                case "RecentFiles": settings.RecentFiles = raw; return true;
                case "SelectedPartitions": settings.SelectedPartitions = raw; return true;
                case "BatchQueue": settings.BatchQueue = raw; return true;
                case "ExpertMode": return ApplyBool(raw, v => settings.ExpertMode = v);
                case "WindowTop": return ApplyDouble(raw, v => settings.WindowTop = v);
                case "WindowLeft": return ApplyDouble(raw, v => settings.WindowLeft = v);
                case "WindowWidth": return ApplyDouble(raw, v => settings.WindowWidth = v);
                case "WindowHeight": return ApplyDouble(raw, v => settings.WindowHeight = v);
                default: return false;
            }
        }

        private static bool ApplyDouble(string raw, Action<double> set)
        {
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            set(value);
            return true;
        }

        private static bool ApplyBool(string raw, Action<bool> set)
        {
            bool value;
            if (!bool.TryParse(raw, out value))
            {
                return false;
            }

            set(value);
            return true;
        }

        private static string FindLegacyConfig()
        {
            string best = null;
            var bestStamp = DateTime.MinValue;

            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                var productDir = Path.Combine(root, LegacyProduct);
                if (!Directory.Exists(productDir))
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.GetFiles(productDir, "user.config", SearchOption.AllDirectories))
                    {
                        var stamp = File.GetLastWriteTimeUtc(file);
                        if (stamp > bestStamp)
                        {
                            bestStamp = stamp;
                            best = file;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // unreadable corner of the profile: skip it
                }
            }

            return best;
        }
    }
}
