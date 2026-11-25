using System;
using System.IO;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// Pure naming/routing rules for the ADDITIONAL FILES slot and the
    /// stock-recovery backup filenames. Extracted here so they can be unit
    /// tested (SRFlasher.Tests) without spinning up the ViewModel or a
    /// device. Keep this class free of device I/O and WPF dependencies.
    /// </summary>
    internal static class AdditionalFileRules
    {
        /// <summary>
        /// Guess the target partition for a dropped .img from its file name.
        /// Unknown names fall back to the name itself (the user can see and
        /// correct it in the flash button caption).
        /// </summary>
        public static string GuessPartitionImage(string path)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path ?? "") ?? "";
                var n = name.ToLowerInvariant();
                if (n.Contains("vbmeta_system")) return "vbmeta_system";
                if (n.Contains("twrp") || n.Contains("orangefox") || n.Contains("ofx")) return "recovery";
                if (n.Contains("vbmeta")) return "vbmeta";
                if (n.Contains("init_boot")) return "init_boot";
                if (n.Contains("vendor_boot")) return "vendor_boot";
                if (n.Contains("vendor_dlkm")) return "vendor_dlkm";
                if (n.Contains("vendor")) return "vendor";
                if (n.Contains("recovery")) return "recovery";
                if (n.Contains("dtbo")) return "dtbo";
                if (n.Contains("boot")) return "boot";
                if (n.Contains("magisk") || n.Contains("patched") || n.Contains("root")) return "boot";
                return string.IsNullOrWhiteSpace(n) ? "boot" : n;
            }
            catch { return "boot"; }
        }

        /// <summary>
        /// A zip whose name marks it as a custom-recovery installer
        /// (OrangeFox / TWRP installer packages). The name test only — the
        /// caller still checks the extension and context.
        /// </summary>
        public static bool IsRecoveryInstallerZipName(string fileName)
        {
            var name = (fileName ?? "").ToLowerInvariant();
            return name.Contains("orangefox") || name.Contains("twrp-installer") ||
                   (name.Contains("twrp") && name.Contains("installer"));
        }

        /// <summary>
        /// A zip that installs a root manager from recovery (KernelSU(-Next),
        /// APatch, legacy Magisk installer zips). Modern Magisk is an .apk —
        /// it is patched in the app, never sideloaded.
        /// </summary>
        public static bool IsRootManagerZipName(string fileName)
        {
            var name = (fileName ?? "").ToLowerInvariant();
            return name.Contains("kernelsu") || name.Contains("apatch") ||
                   (name.Contains("magisk") && name.Contains("installer"));
        }

        /// <summary>
        /// Parses a stock-recovery backup file name:
        /// "&lt;part&gt;-backup-&lt;yyyyMMdd-HHmmss&gt;[-&lt;a|b&gt;]".
        /// Returns the timestamp stamp; the slot via <paramref name="slot"/>
        /// when the backup was taken on an A/B device (null otherwise).
        /// Returns "" when the name does not match the pattern.
        /// </summary>
        public static string ParseBackupStamp(string name, out string slot)
        {
            slot = null;
            if (string.IsNullOrEmpty(name)) return "";
            var idx = name.LastIndexOf("-backup-", StringComparison.Ordinal);
            if (idx < 0) return "";
            var stamp = name.Substring(idx + 8);
            if (stamp.Length > 2 && stamp[stamp.Length - 2] == '-')
            {
                var last = stamp.Substring(stamp.Length - 1).ToLowerInvariant();
                if (last == "a" || last == "b")
                {
                    slot = last;
                    stamp = stamp.Substring(0, stamp.Length - 2);
                }
            }
            return stamp;
        }

        /// <summary>
        /// The slot tag appended to backup file names ("-a"/"-b") — empty for
        /// non-A/B devices or when the slot is unknown.
        /// </summary>
        public static string SlotTagForBackup(bool isAbDevice, string activeSlot)
        {
            if (!isAbDevice) return "";
            var slot = (activeSlot ?? "").Trim().TrimStart('_').ToLowerInvariant();
            return (slot == "a" || slot == "b") ? "-" + slot : "";
        }
    }
}
