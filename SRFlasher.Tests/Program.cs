using System;
using System.Collections.Generic;
using System.Linq;

using SRFlasher.Utilities;

// Tests for the pure naming/routing rules that live in
// SRFlasher/Utilities/AdditionalFileRules.cs (internal — visible to this
// assembly via InternalsVisibleTo). These are the rules that decide where a
// dropped file flashes and what a backup file means — a regression here can
// point an image at the wrong partition, so they are locked down.
namespace SRFlasher.Tests
{
    public static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> Failures = new List<string>();

        private static void Check(string name, bool condition)
        {
            if (condition) { _passed++; Console.WriteLine("PASS  " + name); }
            else { _failed++; Failures.Add(name); Console.WriteLine("FAIL  " + name); }
        }

        private static void CheckEqual(string name, string expected, string actual)
        {
            Check(name + " (expected '" + expected + "', got '" + actual + "')",
                  string.Equals(expected, actual, StringComparison.Ordinal));
        }

        public static int Main()
        {
            GuessPartitionImageTests();
            RecoveryInstallerZipTests();
            RootManagerZipTests();
            ParseBackupStampTests();
            SlotTagTests();
            BundledToolsTests();

            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            if (_failed > 0)
            {
                Console.WriteLine("Failed:");
                foreach (var f in Failures) Console.WriteLine("  - " + f);
                return 1;
            }
            return 0;
        }

        // The app must ship its own platform-tools so it works on a machine
        // with no adb/fastboot installed. These files are Content items in
        // SRFlasher.csproj (CopyToOutputDirectory=PreserveNewest) and are
        // packed into the portable release. If any of them goes missing from
        // the build output, the app silently degrades — so the tests lock
        // them down.
        private static void BundledToolsTests()
        {
            Console.WriteLine("--- Bundled platform-tools ---");
            var tools = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            foreach (var name in new[] { "adb.exe", "fastboot.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "payload-dumper-go.exe" })
                Check(name + " bundled in output tools\\", System.IO.File.Exists(System.IO.Path.Combine(tools, name)));

            Check("ToolPathResolver resolves adb", System.IO.File.Exists(SRFlasher.Services.ToolPathResolver.GetToolPath("adb")));
            Check("ToolPathResolver resolves fastboot", System.IO.File.Exists(SRFlasher.Services.ToolPathResolver.GetToolPath("fastboot")));
            Check("ToolPathResolver resolves payload-dumper-go", System.IO.File.Exists(SRFlasher.Services.ToolPathResolver.GetToolPath("payload-dumper-go")));
        }

        private static void GuessPartitionImageTests()
        {
            Console.WriteLine("--- GuessPartitionImage ---");
            Func<string, string> g = p => AdditionalFileRules.GuessPartitionImage(p);

            CheckEqual("vbmeta_system.img", "vbmeta_system", g(@"C:\fw\vbmeta_system.img"));
            CheckEqual("twrp.img", "recovery", g(@"C:\dl\twrp.img"));
            CheckEqual("OrangeFox-R11.1.img", "recovery", g(@"C:\dl\OrangeFox-R11.1.img"));
            CheckEqual("ofx-recovery.img", "recovery", g(@"C:\dl\ofx-recovery.img"));
            CheckEqual("vbmeta.img", "vbmeta", g(@"C:\fw\vbmeta.img"));
            CheckEqual("init_boot.img", "init_boot", g(@"C:\fw\init_boot.img"));
            CheckEqual("vendor_boot.img", "vendor_boot", g(@"C:\fw\vendor_boot.img"));
            CheckEqual("vendor_dlkm.img", "vendor_dlkm", g(@"C:\fw\vendor_dlkm.img"));
            CheckEqual("vendor.img", "vendor", g(@"C:\fw\vendor.img"));
            CheckEqual("recovery.img", "recovery", g(@"C:\fw\recovery.img"));
            CheckEqual("dtbo.img", "dtbo", g(@"C:\fw\dtbo.img"));
            CheckEqual("boot.img", "boot", g(@"C:\fw\boot.img"));
            CheckEqual("magisk_patched-30600", "boot", g(@"C:\dl\magisk_patched-30600.img"));
            CheckEqual("patched-boot", "boot", g(@"C:\dl\patched-boot.img"));
            CheckEqual("rootboot", "boot", g(@"C:\dl\rootboot.img"));
            CheckEqual("unknown name -> itself", "xxyyzz", g(@"C:\dl\xxyyzz.img"));
            CheckEqual("empty -> boot", "boot", g(""));
            CheckEqual("null path -> boot", "boot", g(null));
            Check("boot beats vendor for 'bootloader.img'", g(@"C:\fw\bootloader.img") == "boot");
        }

        private static void RecoveryInstallerZipTests()
        {
            Console.WriteLine("--- IsRecoveryInstallerZipName ---");
            Check("orangefox.zip", AdditionalFileRules.IsRecoveryInstallerZipName("OrangeFox-R11.1-A13-onyx.zip"));
            Check("twrp-installer.zip", AdditionalFileRules.IsRecoveryInstallerZipName("twrp-installer-3.7.0-onyx.zip"));
            Check("twrp+installer combo", AdditionalFileRules.IsRecoveryInstallerZipName("TWRP.3.7.0.installer.onyx.zip"));
            Check("plain twrp zip is NOT an installer", !AdditionalFileRules.IsRecoveryInstallerZipName("twrp-3.7.0-onyx.img.zip"));
            Check("gapps.zip is NOT an installer", !AdditionalFileRules.IsRecoveryInstallerZipName("MindTheGapps-14.zip"));
            Check("empty is NOT an installer", !AdditionalFileRules.IsRecoveryInstallerZipName(""));
            Check("null is NOT an installer", !AdditionalFileRules.IsRecoveryInstallerZipName(null));
        }

        private static void RootManagerZipTests()
        {
            Console.WriteLine("--- IsRootManagerZipName ---");
            Check("kernelsu.zip", AdditionalFileRules.IsRootManagerZipName("KernelSU-v0.9.5.zip"));
            Check("apatch.zip", AdditionalFileRules.IsRootManagerZipName("APatch-10665.zip"));
            Check("magisk-installer.zip", AdditionalFileRules.IsRootManagerZipName("Magisk-installer-v27.zip"));
            Check("plain magisk apk-like zip is NOT root manager", !AdditionalFileRules.IsRootManagerZipName("Magisk-v27.zip"));
            Check("gapps.zip is NOT root manager", !AdditionalFileRules.IsRootManagerZipName("MindTheGapps-14.zip"));
            Check("empty is NOT root manager", !AdditionalFileRules.IsRootManagerZipName(""));
            Check("null is NOT root manager", !AdditionalFileRules.IsRootManagerZipName(null));
        }

        private static void ParseBackupStampTests()
        {
            Console.WriteLine("--- ParseBackupStamp ---");

            string s = AdditionalFileRules.ParseBackupStamp("boot-backup-20261125-143052-b", out var slot);
            CheckEqual("stamp with slot b", "20261125-143052", s);
            CheckEqual("slot b detected", "b", slot);

            s = AdditionalFileRules.ParseBackupStamp("vendor_boot-backup-20261125-143052-a", out slot);
            CheckEqual("vendor_boot stamp with slot a", "20261125-143052", s);
            CheckEqual("slot a detected", "a", slot);

            s = AdditionalFileRules.ParseBackupStamp("recovery-backup-20261125-143052", out slot);
            CheckEqual("stamp without slot", "20261125-143052", s);
            Check("no slot -> null", slot == null);

            s = AdditionalFileRules.ParseBackupStamp("boot-backup-20261125-1430529", out slot);
            CheckEqual("digit after dash not a slot", "20261125-1430529", s);
            Check("trailing digit is not a slot", slot == null);

            s = AdditionalFileRules.ParseBackupStamp("no-marker.img", out slot);
            CheckEqual("no backup marker -> empty", "", s);
            Check("no marker -> slot null", slot == null);

            s = AdditionalFileRules.ParseBackupStamp("", out slot);
            CheckEqual("empty name -> empty", "", s);
            Check("empty name -> slot null", slot == null);
        }

        private static void SlotTagTests()
        {
            Console.WriteLine("--- SlotTagForBackup ---");
            CheckEqual("AB + slot b", "-b", AdditionalFileRules.SlotTagForBackup(true, "b"));
            CheckEqual("AB + slot _a normalizes", "-a", AdditionalFileRules.SlotTagForBackup(true, "_a"));
            CheckEqual("AB + slot A uppercases", "-a", AdditionalFileRules.SlotTagForBackup(true, "A"));
            CheckEqual("AB + unknown slot", "", AdditionalFileRules.SlotTagForBackup(true, "Unknown"));
            CheckEqual("non-AB ignores slot", "", AdditionalFileRules.SlotTagForBackup(false, "a"));
            CheckEqual("null slot", "", AdditionalFileRules.SlotTagForBackup(true, null));
        }
    }
}
