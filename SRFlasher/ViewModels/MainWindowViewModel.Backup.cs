using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SRFlasher.Services;
using SRFlasher.Utilities;

namespace SRFlasher.ViewModels
{
    // Session 3, stage 1: the recovery-backup + rescue coordinator, split out
    // of MainWindowViewModel.cs. Same class, second file — zero behaviour
    // change; the full service extraction (state decoupling) comes next,
    // once this file is proven on device.
    public partial class MainWindowViewModel
    {

        // Shared fastboot runner for the small tools.
        private async System.Threading.Tasks.Task<string> RunFastbootAsync(string args, TimeSpan timeout)
        {
            return await GetCombinedToolOutputAsync(
                ToolPathResolver.GetToolPath("fastboot"),
                "-s \"" + CurrentDeviceSerial + "\" " + args, timeout, AbortToken);
        }

        private static bool FastbootFailed(string output)
        {
            var o = (output ?? "").ToLowerInvariant();
            return o.Contains("failed") || o.Contains("error") || o.Contains("unknown partition")
                || o.Contains("cancel");
        }

        // Dedicated-recovery devices: the 'recovery' partition. A/B devices:
        // the recovery ramdisk lives in boot/vendor_boot (+ init_boot).
        private static string BackupsRoot => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SRFlasher", "Backups");
        private string BackupDirForSerial(string serial) =>
            System.IO.Path.Combine(BackupsRoot, string.IsNullOrWhiteSpace(serial) ? "unknown" : serial);

        private string[] RecoveryBackupPartitions => IsAbDevice
            ? new[] { "boot", "vendor_boot", "init_boot" }
            : new[] { "recovery" };

        // partition -> latest backup file for the current serial.
        private readonly Dictionary<string, string> _recoveryBackupFiles = new Dictionary<string, string>();
        // partition -> the A/B slot the backup was taken from ('a'/'b'), when known.
        private readonly Dictionary<string, string> _recoveryBackupSlots = new Dictionary<string, string>();
        private string _recoveryBackupStamp = string.Empty;

        public bool RecoveryBackupExists => _recoveryBackupFiles.Count > 0;
        public string RecoveryBackupText => _recoveryBackupFiles.Count == 0
            ? string.Empty
            : "Stock recovery backed up (" + _recoveryBackupStamp + "): " +
              string.Join(", ", _recoveryBackupFiles.Keys) +
              " — restore it if the custom recovery fails to load.";
        public Visibility RecoveryBackupVisibility =>
            _recoveryBackupFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;


        public class BackupInventoryItem
        {
            public string FileName { get; set; }
            public string SizeDisplay { get; set; }
            public string Recorded { get; set; }
            public string FullPath { get; set; }
        }

        public System.Collections.ObjectModel.ObservableCollection<BackupInventoryItem> BackupInventory { get; private set; }

        public Visibility BackupInventoryVisibility =>
            BackupInventory != null && BackupInventory.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

        private void RefreshBackupInventory()
        {
            try
            {
                if (BackupInventory == null) return;
                BackupInventory.Clear();
                var dir = BackupDirForSerial(CurrentDeviceSerial);
                if (!Directory.Exists(dir))
                {
                    OnPropertyChanged(nameof(BackupInventoryVisibility));
                    return;
                }
                var files = Directory.GetFiles(dir, "*.img")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .ToList();
                foreach (var fi in files)
                {
                    BackupInventory.Add(new BackupInventoryItem
                    {
                        FileName = fi.Name,
                        SizeDisplay = (fi.Length / 1024 / 1024) + " MB",
                        Recorded = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        FullPath = fi.FullName
                    });
                }
                OnPropertyChanged(nameof(BackupInventoryVisibility));
            }
            catch (Exception ex)
            {
                _logger.Debug("Backup inventory failed: {Message}", ex.Message);
            }
        }

        public void OpenBackupsFolder()
        {
            try
            {
                var dir = BackupDirForSerial(CurrentDeviceSerial);
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Log("[Backup] Could not open the backups folder: " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes every backup file EXCEPT the newest set (the one the
        /// restore card points at). Returns the number of files removed.
        /// </summary>
        public int DeleteOldBackups()
        {
            var removed = 0;
            try
            {
                var dir = BackupDirForSerial(CurrentDeviceSerial);
                if (!Directory.Exists(dir)) return 0;
                var latestStamp = _recoveryBackupFiles.Values
                    .Select(f => Utilities.AdditionalFileRules.ParseBackupStamp(System.IO.Path.GetFileNameWithoutExtension(f), out _))
                    .OrderByDescending(s => s)
                    .FirstOrDefault();
                foreach (var f in Directory.GetFiles(dir, "*-backup-*.img"))
                {
                    var stamp = Utilities.AdditionalFileRules.ParseBackupStamp(System.IO.Path.GetFileNameWithoutExtension(f), out _);
                    if (stamp != latestStamp)
                    {
                        try { File.Delete(f); removed++; } catch { }
                    }
                }
                if (removed > 0)
                {
                    Log("[Backup] Removed " + removed + " old backup file(s) — the newest set was kept.");
                    ScanRecoveryBackup();
                }
            }
            catch (Exception ex)
            {
                Log("[Backup] Deleting old backups failed: " + ex.Message);
            }
            return removed;
        }

        private void ScanRecoveryBackup()
        {
            var dir = BackupDirForSerial(CurrentDeviceSerial);
            _recoveryBackupFiles.Clear();
            _recoveryBackupStamp = string.Empty;
            try
            {
                if (Directory.Exists(dir))
                {
                    _recoveryBackupSlots.Clear();
                    var files = Directory.GetFiles(dir, "*-backup-*.img");
                    string latest = null;
                    foreach (var f in files)
                    {
                        var stamp = Utilities.AdditionalFileRules.ParseBackupStamp(System.IO.Path.GetFileNameWithoutExtension(f), out _);
                        if (latest == null || string.CompareOrdinal(stamp, latest) > 0)
                            latest = stamp;
                    }
                    if (latest != null)
                    {
                        foreach (var f in files)
                        {
                            var stamp = Utilities.AdditionalFileRules.ParseBackupStamp(System.IO.Path.GetFileNameWithoutExtension(f), out var slot);
                            if (stamp != latest) continue;
                            var name = System.IO.Path.GetFileNameWithoutExtension(f) ?? "";
                            var idx = name.LastIndexOf("-backup-", StringComparison.Ordinal);
                            if (idx <= 0) continue;
                            var part = name.Substring(0, idx);
                            if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024)
                            {
                                _recoveryBackupFiles[part] = f;
                                if (!string.IsNullOrEmpty(slot))
                                    _recoveryBackupSlots[part] = slot;
                            }
                        }
                        _recoveryBackupStamp = latest.Insert(4, "/").Insert(7, "/");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[Backup] Scan failed: " + ex.Message);
            }
            OnPropertyChanged(nameof(RecoveryBackupExists));
            OnPropertyChanged(nameof(RecoveryBackupText));
            OnPropertyChanged(nameof(RecoveryBackupVisibility));
            OnPropertyChanged(nameof(RecoveryStockRestoreEnabled));
            OnPropertyChanged(nameof(RecoveryStockRestoreReason));
            OnPropertyChanged(nameof(BackupDirDisplay));
            RefreshBackupInventory();
        }

        // Pulls the recovery-carrying partitions to the PC while the device
        // is in fastboot (fastboot fetch reads the ACTIVE slot by default).
        private async System.Threading.Tasks.Task<bool> BackupStockRecoveryViaFastbootAsync(string serial)
        {
            var dir = BackupDirForSerial(serial);
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var fastboot = ToolPathResolver.GetToolPath("fastboot");
            var okAny = false;
            foreach (var part in RecoveryBackupPartitions)
            {
                var slotTag = Utilities.AdditionalFileRules.SlotTagForBackup(IsAbDevice, _currentActiveSlot);
                var file = System.IO.Path.Combine(dir, part + "-backup-" + stamp + slotTag + ".img");
                Log("[Backup] Fetching " + part + " -> " + System.IO.Path.GetFileName(file) + " ...");
                var output = await GetCombinedToolOutputAsync(fastboot,
                    "-s \"" + serial + "\" fetch " + part + " \"" + file + "\"",
                    TimeSpan.FromMinutes(6), AbortToken);
                var exists = File.Exists(file) && new FileInfo(file).Length > 1024 * 1024;
                if (exists)
                {
                    okAny = true;
                    Log("[✓] " + part + " backed up (" + (new FileInfo(file).Length / 1024 / 1024) + " MB).");
                }
                else
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { _logger.Debug("Backup temp file cleanup failed: " + ex.Message); }
                    var primary = part == (IsAbDevice ? "boot" : "recovery");
                    if (primary)
                        Log("[ERROR] " + part + " backup failed: " + (output ?? "no output").Trim());
                    else
                        Log("[Backup] " + part + " not present on this device — skipped.");
                }
            }
            return okAny;
        }

        // Recovery-side backup (dd + adb pull) for the installer-zip route:
        // the device is booted in a recovery, where adbd runs as root.
        private async System.Threading.Tasks.Task<bool> BackupStockRecoveryViaRecoveryAsync(string serial)
        {
            var dir = BackupDirForSerial(serial);
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var adb = ToolPathResolver.GetToolPath("adb");
            var slot = _currentActiveSlot != null ? _currentActiveSlot.Trim().TrimStart('_').ToLowerInvariant() : "";
            var suffix = IsAbDevice && !string.IsNullOrEmpty(slot) ? "_" + slot : "";
            var okAny = false;
            foreach (var part in RecoveryBackupPartitions)
            {
                var dev = "/dev/block/by-name/" + part + suffix;
                var remote = "/tmp/" + part + "-backup.img";
                var slotTag = IsAbDevice && !string.IsNullOrEmpty(slot) ? "-" + slot : "";
                var local = System.IO.Path.Combine(dir, part + "-backup-" + stamp + slotTag + ".img");
                Log("[Backup] Reading " + part + suffix + " on the device ...");
                var dd = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + serial + "\" shell dd if=\"" + dev + "\" of=\"" + remote + "\" bs=4194304",
                    TimeSpan.FromMinutes(4), AbortToken);
                var ddOk = !string.IsNullOrWhiteSpace(dd) &&
                    (dd.Contains("records in") || dd.Contains("bytes copied") || dd.Contains("bytes transferred"));
                if (!ddOk)
                {
                    var primary = part == (IsAbDevice ? "boot" : "recovery");
                    if (primary)
                        Log("[ERROR] " + part + " backup (dd) failed: " + (dd ?? "no output").Trim());
                    else
                        Log("[Backup] " + part + " not readable on this device — skipped.");
                    continue;
                }
                var pull = await _processManager.ExecuteAsync(adb,
                    "-s \"" + serial + "\" pull \"" + remote + "\" \"" + local + "\"",
                    TimeSpan.FromMinutes(4), _cts?.Token ?? CancellationToken.None);
                await GetCombinedToolOutputAsync(adb,
                    "-s \"" + serial + "\" shell rm -f \"" + remote + "\"",
                    TimeSpan.FromSeconds(15));
                if (File.Exists(local) && new FileInfo(local).Length > 1024 * 1024)
                {
                    okAny = true;
                    Log("[✓] " + part + " backed up (" + (new FileInfo(local).Length / 1024 / 1024) + " MB).");
                }
                else
                {
                    try { if (File.Exists(local)) File.Delete(local); } catch (Exception ex) { _logger.Debug("Backup temp file cleanup failed: " + ex.Message); }
                    Log("[ERROR] " + part + " backup pull failed.");
                }
            }
            return okAny;
        }

        // The one entry point: picks the route by the CURRENT mode. Returns
        // true when a usable backup exists afterwards.
        private async System.Threading.Tasks.Task<bool> BackupStockRecoveryAutoAsync(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            Log("[Backup] Backing up the stock recovery before touching the custom recovery...");
            bool ok;
            if (_currentPhoneMode == DeviceMode.AdbRecovery)
                ok = await BackupStockRecoveryViaRecoveryAsync(serial);
            else if (IsFastbootMode)
                ok = await BackupStockRecoveryViaFastbootAsync(serial);
            else
            {
                Log("[Backup] Skipped — the device is in " + _currentPhoneMode +
                    "; recovery backups need recovery (dd) or fastboot (fetch) mode.");
                return false;
            }
            if (ok)
            {
                Log("[Backup] Stock recovery saved under " + BackupDirForSerial(serial) +
                    " — restore it if the custom recovery fails to load.");
                NoteSessionBackup("PC backup: " + BackupDirForSerial(serial));
                ScanRecoveryBackup();
            }
            return ok;
        }

        // Restore helper: writes the latest backup set back to the device.
        public bool RecoveryStockRestoreEnabled => !IsFlashing && DeviceConnected &&
            _currentLockStatus == "Unlocked" && RecoveryBackupExists;
        public string RecoveryStockRestoreReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!RecoveryBackupExists) return "No stock recovery backup found for this device";
                if (!DeviceConnected) return "No device connected";
                if (_currentLockStatus != "Unlocked") return "Bootloader must be unlocked";
                return "Reboots to the bootloader first, then writes the backed-up stock recovery images back (fastboot, active slot)";
            }
        }

        public async System.Threading.Tasks.Task RestoreStockRecoveryAsync()
        {
            if (IsFlashing || !RecoveryStockRestoreEnabled) return;
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }

                // 2.3: if the backup came from the other slot, say so BEFORE
                // writing — a restore to the wrong slot can put the wrong
                // ramdisk where the next boot reads it.
                var curSlot = (_currentActiveSlot ?? "").Trim().TrimStart('_').ToLowerInvariant();
                if (IsAbDevice && (curSlot == "a" || curSlot == "b"))
                {
                    foreach (var pair in _recoveryBackupFiles)
                    {
                        if (_recoveryBackupSlots.TryGetValue(pair.Key, out var backedSlot) &&
                            backedSlot != curSlot)
                            Log("[WARNING] " + pair.Key + " was backed up from slot " + backedSlot.ToUpperInvariant() +
                                " but the active slot is now " + curSlot.ToUpperInvariant() +
                                " — this restore writes the other slot's ramdisk.");
                    }
                }
                foreach (var pair in _recoveryBackupFiles)
                {
                    Log("[Backup] Restoring " + pair.Key + " from " + System.IO.Path.GetFileName(pair.Value) + " ...");
                    var result = await _flashService.FlashPartitionAsync(
                        CurrentDeviceSerial, pair.Key, pair.Value, null, _cts?.Token ?? CancellationToken.None);
                    Log(result.Success ? "[✓] " + pair.Key + " restored." : "[ERROR] " + pair.Key + ": " + result.Message);
                }
                Log("[Backup] Stock recovery restore finished. REBOOT TO RECOVERY to verify.");
                ShowToast("Success", "Stock recovery restored",
                    "The backed-up images are written back — REBOOT TO RECOVERY to verify.");
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                RefreshButtonStates();
            }
        }

        // --- ERASE FRP ---
        public bool EraseFrpEnabled => !IsFlashing && DeviceConnected && _currentLockStatus == "Unlocked";
        public string EraseFrpReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!DeviceConnected) return "No device connected";
                if (_currentLockStatus != "Unlocked") return "Bootloader must be unlocked";
                return "Reboots to the bootloader first, then erases the frp partition (Google account lock counter) — for repurposing a device you own";
            }
        }

        public async System.Threading.Tasks.Task EraseFrpAsync()
        {
            if (IsFlashing || !EraseFrpEnabled) return;
            IsFlashing = true;
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                Log("[Rescue] Erasing the frp partition (removes the Google account lock marker)...");
                var output = await RunFastbootAsync("erase frp", TimeSpan.FromSeconds(60));
                if (!FastbootFailed(output))
                {
                    Log("[✓] FRP partition erased. FRP is cleared — do not add a Google account to hand the device over.");
                    ShowToast("Success", "FRP erased", "The frp partition was wiped via fastboot.");
                }
                else
                {
                    Log("[ERROR] FRP erase failed: " + (output ?? "no output").Trim());
                    Log("    Many bootloaders do not expose 'frp' to fastboot — on those devices FRP removal " +
                        "needs an authorized EDL/service flash. Nothing was changed.");
                    ShowToast("Error", "FRP erase failed", "This bootloader refuses the erase — nothing was changed.");
                }
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // --- WIPE METADATA ---
        public bool EraseMetadataEnabled => !IsFlashing && DeviceConnected && _currentLockStatus == "Unlocked";
        public string EraseMetadataReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!DeviceConnected) return "No device connected";
                if (_currentLockStatus != "Unlocked") return "Bootloader must be unlocked";
                return "Reboots to the bootloader first, then erases the metadata partition (encryption keys + fbe state) — helps when the phone is stuck encrypting or refuses to decrypt";
            }
        }

        public async System.Threading.Tasks.Task EraseMetadataAsync()
        {
            if (IsFlashing || !EraseMetadataEnabled) return;
            IsFlashing = true;
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                Log("[Rescue] Erasing the metadata partition (encryption keys / fbe state)...");
                var output = await RunFastbootAsync("erase metadata", TimeSpan.FromSeconds(60));
                if (!FastbootFailed(output))
                {
                    Log("[✓] Metadata erased — the phone boots unencrypted and the ROM re-encrypts fresh.");
                    ShowToast("Success", "Metadata erased", "Encryption state reset — the ROM re-encrypts on next setup.");
                }
                else
                {
                    Log("[ERROR] Metadata erase failed: " + (output ?? "no output").Trim());
                    ShowToast("Error", "Erase failed", "The bootloader refused the erase — nothing was changed.");
                }
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // --- ERASE MISC (unstuck recovery boot) ---
        public bool EraseMiscEnabled => !IsFlashing && DeviceConnected && _currentLockStatus == "Unlocked";
        public string EraseMiscReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!DeviceConnected) return "No device connected";
                if (_currentLockStatus != "Unlocked") return "Bootloader must be unlocked";
                return "Reboots to the bootloader first, then erases the misc partition — clears the boot-into-recovery command when the phone is stuck in a recovery boot loop";
            }
        }

        public async System.Threading.Tasks.Task EraseMiscAsync()
        {
            if (IsFlashing || !EraseMiscEnabled) return;
            IsFlashing = true;
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                Log("[Rescue] Erasing the misc partition (clears the stuck boot-into-recovery command)...");
                var output = await RunFastbootAsync("erase misc", TimeSpan.FromSeconds(60));
                if (!FastbootFailed(output))
                {
                    Log("[✓] Misc erased — the pending recovery-boot instruction is gone. Reboot to system.");
                    ShowToast("Success", "Misc erased", "The recovery boot loop command is cleared.");
                }
                else
                {
                    Log("[ERROR] Misc erase failed: " + (output ?? "no output").Trim());
                    ShowToast("Error", "Erase failed", "The bootloader refused the erase — nothing was changed.");
                }
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // --- SWITCH ACTIVE SLOT (A/B rescue) ---
        public bool SwitchSlotEnabled => !IsFlashing && DeviceConnected && IsAbDevice;
        public string SwitchSlotReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!DeviceConnected) return "No device connected";
                return "Reboots to the bootloader first, then makes the OTHER slot the next boot — the standard rescue when one slot does not boot";
            }
        }

        public async System.Threading.Tasks.Task SwitchActiveSlotAsync()
        {
            if (IsFlashing || !SwitchSlotEnabled) return;
            IsFlashing = true;
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                var cur = await RunFastbootAsync("getvar current-slot", TimeSpan.FromSeconds(15));
                var m = System.Text.RegularExpressions.Regex.Match(cur ?? "", @"current-slot:\s*([ab])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    Log("[Rescue] Could not read the current slot: " + (cur ?? "no output").Trim());
                    return;
                }
                var other = m.Groups[1].Value.ToLowerInvariant() == "a" ? "b" : "a";
                Log("[Rescue] Current slot " + m.Groups[1].Value.ToLowerInvariant() + " -> switching to " + other + "...");
                var output = await RunFastbootAsync("--set-active=" + other, TimeSpan.FromSeconds(30));
                if (!FastbootFailed(output))
                {
                    Log("[✓] Slot " + other.ToUpperInvariant() + " is now the active slot — the next boot starts from it.");
                    ShowToast("Success", "Slot switched", "Slot " + other.ToUpperInvariant() + " is active — reboot and see if that slot boots.");
                }
                else
                {
                    Log("[ERROR] Slot switch failed: " + (output ?? "no output").Trim());
                    ShowToast("Error", "Switch failed", "The bootloader refused the slot switch.");
                }
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // --- BOOT FASTBOOTD (userspace fastboot for dynamic partitions) ---
        public bool BootFastbootdEnabled => !IsFlashing &&
            (_currentPhoneMode == DeviceMode.AdbSystem || _currentPhoneMode == DeviceMode.AdbRecovery);
        public string BootFastbootdReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                return "Boots fastbootd (userspace fastboot) — needed to flash dynamic partitions like system/vendor";
            }
        }

        public async System.Threading.Tasks.Task BootFastbootdAsync()
        {
            if (IsFlashing || !BootFastbootdEnabled) return;
            IsFlashing = true;
            try
            {
                Log("[Rescue] Rebooting to fastbootd (userspace fastboot for dynamic partitions)...");
                var output = await GetCombinedToolOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s \"" + CurrentDeviceSerial + "\" reboot fastbootd",
                    TimeSpan.FromSeconds(30), AbortToken);
                Log(string.IsNullOrWhiteSpace(output) ? "[✓] Reboot requested — fastbootd comes up as 'fastbootd' mode in a few seconds." : output.Trim());
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // ==================== P5: TWRP boot backup / restore ====================
        // A small, fast safety net inside the recovery: `twrp backup B`
        // stores the boot image (kernel + recovery ramdisk on A/B) on
        // /sdcard. Needs readable data; the stock-recovery PC backup above
        // covers the "way back" when data is locked.

        public Visibility RecoveryTwrpBackupVisibility =>
            _currentPhoneMode == DeviceMode.AdbRecovery ? Visibility.Visible : Visibility.Collapsed;
        public bool RecoveryTwrpBackupEnabled => !IsFlashing && RecoveryInRecoveryMode && RecoverySdcardMounted;
        public string RecoveryTwrpBackupReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (_currentPhoneMode != DeviceMode.AdbRecovery) return "Requires the device in recovery";
                if (!RecoveryCliAvailable) return "No twrp CLI in this recovery";
                if (!RecoverySdcardMounted) return "Data is locked — no storage for a TWRP backup (the PC stock-recovery backup still protects the way back)";
                return "Saves the boot image (kernel + recovery ramdisk) to /sdcard via the twrp CLI";
            }
        }

        public async System.Threading.Tasks.Task BackupBootViaTwrpAsync()
        {
            if (IsFlashing || !RecoveryTwrpBackupEnabled) return;
            var name = "SRF-boot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                // 2.4: a half-completed backup on a full /sdcard is worse
                // than none — check space before offering the partition the
                // chance to fail silently mid-write.
                var bootDev = "/dev/block/by-name/boot" +
                    (_currentActiveSlot != null ? "_" + _currentActiveSlot.Trim().TrimStart('_').ToLowerInvariant() : "");
                var sizeOut = await GetCombinedToolOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s \"" + CurrentDeviceSerial + "\" shell stat -c %s " + bootDev,
                    TimeSpan.FromSeconds(15), AbortToken);
                long bootBytes = 0;
                long.TryParse((sizeOut ?? "").Trim(), out bootBytes);
                var dfOut = await GetCombinedToolOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s \"" + CurrentDeviceSerial + "\" shell df -B1 /sdcard",
                    TimeSpan.FromSeconds(15), AbortToken);
                long freeBytes = 0;
                if (!string.IsNullOrWhiteSpace(dfOut))
                {
                    var dfLines = dfOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (dfLines.Length >= 2)
                    {
                        var cols = dfLines[dfLines.Length - 1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length >= 4) long.TryParse(cols[3], out freeBytes);
                    }
                }
                if (bootBytes > 0 && freeBytes > 0 && freeBytes < bootBytes * 2)
                {
                    Log("[Backup] Not enough free space on /sdcard: " + (freeBytes / 1024 / 1024) +
                        " MB free, boot image is " + (bootBytes / 1024 / 1024) +
                        " MB (2x headroom required). Free space first — nothing was written.");
                    ShowToast("Error", "Not enough space",
                        "/sdcard has " + (freeBytes / 1024 / 1024) + " MB free — the boot backup needs about " +
                        (bootBytes / 1024 / 1024) + " MB.");
                    return;
                }

                Log("[Backup] twrp backup B " + name + " (boot image -> /sdcard)...");
                var output = await GetCombinedToolOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s \"" + CurrentDeviceSerial + "\" shell twrp backup B " + name,
                    TimeSpan.FromMinutes(15), AbortToken);
                var oLower = (output ?? "").ToLowerInvariant();
                var failed = oLower.Contains("failed") || (output ?? "").Contains("E:");
                Log(!string.IsNullOrWhiteSpace(output) ? output.Trim() : "[Backup] (no CLI output)");
                if (!failed)
                {
                    // 2.4: verify the backup actually landed with real content
                    // (TWRP stores the boot image as boot.img.emmc.win).
                    var verify = await GetCombinedToolOutputAsync(
                        ToolPathResolver.GetToolPath("adb"),
                        "-s \"" + CurrentDeviceSerial + "\" shell ls -l /sdcard/TWRP/BACKUPS/*/SRF-boot-" +
                        name.Substring(9) + "/",
                        TimeSpan.FromSeconds(20), AbortToken);
                    var verified = !string.IsNullOrWhiteSpace(verify) &&
                        verify.Contains("boot.img.emmc.win");
                    if (verified)
                    {
                        Log("[✓] Boot backup '" + name + "' saved on the device and verified (TWRP/BACKUPS).");
                        NoteSessionBackup("TWRP boot backup on /sdcard: " + name);
                        ShowToast("Success", "Boot backup saved",
                            "Stored and verified on /sdcard under TWRP/BACKUPS — restore it via RESTORE BOOT (TWRP).");
                    }
                    else
                    {
                        Log("[WARNING] Backup command finished but boot.img.emmc.win was not found in TWRP/BACKUPS — " +
                            "the backup may not have completed. Check /sdcard free space and try again.");
                        ShowToast("Warning", "Backup unverified",
                            "The backup folder could not be verified — check the log before relying on it.");
                    }
                }
                else
                    ShowToast("Error", "Backup failed", "The twrp CLI reported a problem — check the log.");
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                RefreshButtonStates();
            }
        }

        public bool RecoveryTwrpRestoreEnabled => !IsFlashing && RecoveryInRecoveryMode && RecoverySdcardMounted;
        public string RecoveryTwrpRestoreReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (_currentPhoneMode != DeviceMode.AdbRecovery) return "Requires the device in recovery";
                if (!RecoveryCliAvailable) return "No twrp CLI in this recovery";
                if (!RecoverySdcardMounted) return "Data is locked — TWRP backups on /sdcard are unreadable";
                return "Restores the newest SRF-boot backup via the twrp CLI";
            }
        }

        public async System.Threading.Tasks.Task RestoreBootViaTwrpAsync()
        {
            if (IsFlashing || !RecoveryTwrpRestoreEnabled) return;
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                var adb = ToolPathResolver.GetToolPath("adb");
                Log("[Backup] Looking for the newest SRF-boot backup on the device...");
                var listing = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell ls /sdcard/TWRP/BACKUPS/*/",
                    TimeSpan.FromSeconds(20), AbortToken);
                var candidates = (listing ?? "")
                    .Split(new[] { '\r', '\n', '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.StartsWith("SRF-boot-", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Trim())
                    .Distinct()
                    .OrderByDescending(l => l)
                    .ToList();
                if (candidates.Count == 0)
                {
                    Log("[Backup] No SRF-boot backup found on the device.");
                    ShowToast("Info", "No backup found", "No SRF-boot TWRP backup exists on /sdcard yet.");
                    return;
                }
                var name = candidates[0];
                Log("[Backup] Restoring '" + name + "' via the twrp CLI...");
                var output = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell twrp restore " + name,
                    TimeSpan.FromMinutes(15), AbortToken);
                var oLower = (output ?? "").ToLowerInvariant();
                var failed = oLower.Contains("failed") || (output ?? "").Contains("E:");
                Log(!string.IsNullOrWhiteSpace(output) ? output.Trim() : "[Backup] (no CLI output)");
                if (!failed)
                {
                    Log("[✓] Boot restored from '" + name + "'. Reboot to system to use it.");
                    ShowToast("Success", "Boot restored", "Restored from '" + name + "' — FINISH & REBOOT when ready.");
                }
                else
                    ShowToast("Error", "Restore failed", "The twrp CLI reported a problem — check the log.");
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                RefreshButtonStates();
            }
        }
    }
}
