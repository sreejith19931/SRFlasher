using System;
using System.IO;

namespace SRFlasher.Services
{
    /// <summary>
    /// Resolves the path to a bundled or system-installed ADB/fastboot tool.
    /// Search order: local tools folder, application root, then PATH.
    /// </summary>
    public class ToolPathResolver
    {
        private static readonly string ToolsDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "tools");

        /// <summary>
        /// Get the full path to a tool (adb, fastboot, payload-dumper-go, etc.).
        /// Returns the bare tool name if not found anywhere, letting the caller fail naturally.
        /// </summary>
        public static string GetToolPath(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return toolName;

            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            string executable = isWindows && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? $"{toolName}.exe"
                : toolName;

            string localToolsPath = Path.Combine(ToolsDirectory, executable);
            if (File.Exists(localToolsPath))
                return localToolsPath;

            string appRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, executable);
            if (File.Exists(appRootPath))
                return appRootPath;

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string fullPath = Path.Combine(dir.Trim(), executable);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch { }
            }

            return executable;
        }
    }
}
