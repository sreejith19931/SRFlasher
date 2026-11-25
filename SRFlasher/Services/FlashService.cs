using SRFlasher.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// Describes a single flash operation request.
    /// </summary>
    public class FlashContext
    {
        public string Serial { get; set; }
        public string RomFilePath { get; set; }
        public List<string> SelectedPartitions { get; set; } = new List<string>();
        public bool WipeCache { get; set; }
        /// <summary>"system", "recovery", "bootloader", or null (= stay in the current mode after flashing).</summary>
        public string PostFlashTarget { get; set; }
        public bool RebootAfterFlash { get; set; } = true;
        /// <summary>"A", "B", "Both", or null (= auto: flash the active slot).</summary>
        public string TargetSlot { get; set; }
        /// <summary>Flash vbmeta with --disable-verity --disable-verification (required for many cross-ROM migrations).</summary>
        public bool DisableVerity { get; set; }
        /// <summary>Run a full factory wipe (fastboot -w) BEFORE flashing (clean install).</summary>
        public bool WipeData { get; set; }

        /// <summary>
        /// What the caller wants logged on success. A boot-chain write must not
        /// be reported as a ROM install, so the caller owns the wording.
        /// </summary>
        public string SuccessMessage { get; set; }
    }

    /// <summary>
    /// Result of a flash operation.
    /// </summary>
    public class FlashResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string TechnicalDetails { get; set; }
        public ErrorTranslator.ErrorInfo ErrorInfo { get; set; }
        public TimeSpan Elapsed { get; set; }

        public static FlashResult Ok(string message) =>
            new FlashResult { Success = true, Message = message };

        public static FlashResult Fail(string message,
            string technicalDetails = null,
            ErrorTranslator.ErrorInfo errorInfo = null) =>
            new FlashResult
            {
                Success = false,
                Message = message,
                TechnicalDetails = technicalDetails,
                ErrorInfo = errorInfo
            };
    }

    /// <summary>
    /// Progress reported while a flash operation is running.
    /// </summary>
    public class FlashProgressEventArgs : EventArgs
    {
        public int Percent { get; set; }
        public string Step { get; set; }
        public string Partition { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Orchestrates the full ROM flashing pipeline: ADB/fastboot execution,
    /// payload.bin extraction via payload-dumper-go, partition flashing,
    /// cache wipe, and reboot. All operations support cancellation and progress
    /// reporting.
    /// </summary>
    public class FlashService
    {
        private readonly ProcessManager _processManager;
        private readonly ErrorTranslator _errorTranslator;
        private readonly LoggingService _logger;
        private CancellationTokenSource _cts;
        private readonly object _ctsLock = new object();

        public event EventHandler<FlashProgressEventArgs> ProgressChanged;
        public event EventHandler<string> LogMessage;

        /// <summary>
        /// Minimum battery percentage required before a flash is allowed (0 = disabled).
        /// </summary>
        public int MinBatteryLevel { get; set; } = 30;

        /// <summary>When the user explicitly confirms a low-battery flash at
        /// the popup, the pre-flight check logs a warning and continues
        /// instead of failing the run. Reset per flash by the view model.</summary>
        public bool AllowBelowMinBattery { get; set; }

        private static readonly string[] PayloadExtractablePartitions =
        {
            "boot", "vendor_boot", "init_boot", "dtbo", "recovery",
            "vbmeta", "vbmeta_system"
        };

        // Whitelist of characters allowed in an ADB/fastboot device serial.
        // Anything outside this set is rejected to prevent command injection
        // when the serial is interpolated into shell arguments.
        private static readonly Regex SerialRegex = new Regex(
            @"^[A-Za-z0-9:._-]+$", RegexOptions.Compiled);

        private static bool IsValidSerial(string serial)
        {
            return string.IsNullOrWhiteSpace(serial) || SerialRegex.IsMatch(serial);
        }

        public FlashService()
        {
            _processManager = new ProcessManager();
            _errorTranslator = new ErrorTranslator();
            _logger = LoggingService.Instance;
        }

        /// <summary>
        /// Cancel the currently running flash operation, if any.
        /// </summary>
        public void Cancel()
        {
            lock (_ctsLock)
            {
                _cts?.Cancel();
                Log("[FlashService] Cancellation requested by user.");
            }
        }

        private string AdbPath => ToolPathResolver.GetToolPath("adb");
        private string FastbootPath => ToolPathResolver.GetToolPath("fastboot");
        private string PayloadDumperPath => ToolPathResolver.GetToolPath("payload-dumper-go");

        #region Logging / progress helpers

        private void ReportProgress(int percent, string step, string partition = null, string message = null)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            ProgressChanged?.Invoke(this, new FlashProgressEventArgs
            {
                Percent = percent,
                Step = step,
                Partition = partition,
                Message = message
            });
        }

        private void Log(string message)
        {
            LogMessage?.Invoke(this, message);
            _logger.Information(message);
        }

        /// <summary>
        /// Run a tool and log its output line-by-line. Returns the combined stdout.
        /// When <paramref name="verbose"/> is false, command and output logging is
        /// suppressed (used for periodic background polling).
        /// </summary>
        private async Task<string> RunToolAsync(string tool, string arguments, CancellationToken ct,
            Action<string> outputCallback = null, TimeSpan? timeout = null, bool verbose = true)
        {
            var toolName = Path.GetFileNameWithoutExtension(tool);
            if (verbose)
                Log($"[{toolName}] {arguments}");

            var result = await _processManager.ExecuteAsync(tool, arguments, timeout, ct, outputCallback);

            if (result.Cancelled)
            {
                Log($"[{toolName}] operation cancelled.");
                return null;
            }

            if (result.TimedOut)
            {
                Log($"[{toolName}] operation timed out after {timeout?.TotalMilliseconds ?? 0}ms.");
            }

            var combinedOutput = new System.Text.StringBuilder();
            AppendToolOutput(combinedOutput, result.Output, "OUT");
            AppendToolOutput(combinedOutput, result.Error, "ERR");

            if (combinedOutput.Length > 0 && verbose)
            {
                Log(combinedOutput.ToString().TrimEnd());
            }

            // Fastboot writes most of its meaningful output (getvar results,
            // OKAY/FAILED lines, flash errors) to stderr, so the combined
            // stdout + stderr text must be returned for reliable parsing.
            var rawCombined = result.Output ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(result.Error))
                rawCombined = rawCombined + Environment.NewLine + result.Error;
            return rawCombined.Trim();
        }

        private static void AppendToolOutput(System.Text.StringBuilder sb, string output, string prefix)
        {
            if (string.IsNullOrWhiteSpace(output))
                return;

            foreach (var line in output.Split(new[] { "\r\n", "\r", "\n" },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                sb.AppendLine($"[{prefix}] {trimmed}");
            }
        }

        #endregion

        #region Device detection

        /// <summary>
        /// Returns true when the given serial (or any device when serial is empty)
        /// is detected in fastboot/bootloader mode.
        /// </summary>
        public async Task<bool> IsDeviceInFastbootAsync(string serial, CancellationToken ct)
        {
            var args = string.IsNullOrWhiteSpace(serial) ? "devices" : $"-s \"{serial}\" devices";
            var output = await RunToolAsync(FastbootPath, args, ct);
            if (output == null)
                return false;

            // Parse rows like "serial\tfastboot". Some fastboot builds pad the
            // state column ("serial\t fastboot"), so the state must be trimmed —
            // a raw Contains("\tfastboot") misses those and the flash aborts
            // claiming the device is not in fastboot mode.
            foreach (var line in output.Split(new[] { "\r\n", "\r", "\n" },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(new[] { '\t' }, 2);
                if (parts.Length == 2 &&
                    parts[1].Trim().Equals("fastboot", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Query the bootloader lock state of a device in fastboot mode.
        /// Returns "Unlocked", "Locked", or "Unknown".
        /// </summary>
        public async Task<string> QueryBootloaderLockStateAsync(string serial, CancellationToken ct, bool verbose = true)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(serial)
                    ? "getvar unlocked"
                    : $"-s \"{serial}\" getvar unlocked";
                var output = await RunToolAsync(FastbootPath, args, ct, verbose: verbose);

                if (output != null)
                {
                    var lower = output.ToLowerInvariant();
                    if (lower.Contains("unlocked: yes"))
                        return "Unlocked";
                    if (lower.Contains("unlocked: no"))
                        return "Locked";
                }

                var oemArgs = string.IsNullOrWhiteSpace(serial)
                    ? "oem device-info"
                    : $"-s \"{serial}\" oem device-info";
                var oemOutput = await RunToolAsync(FastbootPath, oemArgs, ct, verbose: verbose);
                if (oemOutput != null)
                {
                    var lower = oemOutput.ToLowerInvariant();
                    if (lower.Contains("device unlocked: true") || lower.Contains("unlocked: true"))
                        return "Unlocked";
                    if (lower.Contains("device unlocked: false") || lower.Contains("unlocked: false"))
                        return "Locked";
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Error querying bootloader lock state: {Message}", ex.Message);
            }

            return "Unknown";
        }

        /// <summary>
        /// Query the active slot of an A/B device in fastboot mode.
        /// Returns "A", "B", or "Unknown" (or "N/A (Non-A/B)").
        /// </summary>
        public async Task<string> QueryActiveSlotAsync(string serial, CancellationToken ct, bool verbose = true)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(serial)
                    ? "getvar current-slot"
                    : $"-s \"{serial}\" getvar current-slot";
                var output = await RunToolAsync(FastbootPath, args, ct, verbose: verbose);
                if (output == null)
                    return "Unknown";

                var match = System.Text.RegularExpressions.Regex.Match(output,
                    @"current-slot:\s*_?([a-bA-B0-1])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var slot = match.Groups[1].Value.ToUpperInvariant();
                    return (slot == "0" || slot == "A") ? "A" : "B";
                }

                var lower = output.ToLowerInvariant();
                if (lower.Contains("unknown variable") || lower.Contains("FAILED"))
                    return "N/A (Non-A/B)";
            }
            catch (Exception ex)
            {
                _logger.Debug("Error querying active slot: {Message}", ex.Message);
            }

            return "Unknown";
        }

        /// <summary>
        /// Query the battery level of an ADB-connected device via dumpsys.
        /// Returns null if the level cannot be determined (e.g. device not in ADB mode).
        /// </summary>
        public async Task<int?> GetBatteryLevelAsync(string serial, CancellationToken ct)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(serial)
                    ? "shell dumpsys battery"
                    : $"-s \"{serial}\" shell dumpsys battery";
                var output = await RunToolAsync(AdbPath, args, ct, timeout: TimeSpan.FromSeconds(10));
                if (output == null)
                    return null;

                var match = System.Text.RegularExpressions.Regex.Match(output,
                    @"level:\s*(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var level))
                {
                    Log($"Battery level: {level}%");
                    return level;
                }

                _logger.Debug("Could not parse battery level from dumpsys output.");
            }
            catch (Exception ex)
            {
                _logger.Debug("Error querying battery level: {Message}", ex.Message);
            }

            return null;
        }

        #endregion

        #region Reboot helpers

        /// <summary>
        /// Apply an update package via ADB sideload. The device must already be
        /// in sideload mode (Recovery -> Apply update from ADB).
        /// </summary>
        public async Task<FlashResult> SideloadAsync(string serial, string filePath, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return FlashResult.Fail($"Update package not found: {filePath}");

            ReportProgress(5, "Sideload", null, "Applying update via ADB sideload...");
            Log($"Applying update package via sideload: {filePath}");

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"sideload \"{filePath}\""
                : $"-s \"{serial}\" sideload \"{filePath}\"";

            // adb sideload streams the device's own install percentage (~NN%)
            // to stderr while the package applies. Parse it live so the app's
            // progress bar mirrors the ROM's real flashing progress instead of
            // sitting frozen until completion.
            int lastStreamedPercent = 0;
            void StreamSideloadProgress(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(?<pct>\d{1,3})\s*%");
                if (!m.Success) return;
                if (!int.TryParse(m.Groups["pct"].Value, out var pct)) return;
                if (pct > 100) return;
                if (pct <= lastStreamedPercent) return;
                lastStreamedPercent = pct;
                ReportProgress(pct, "Sideload", null, $"Streaming update — {pct}%...");
            }

            // Large OTA packages can take many minutes over sideload.
            var output = await RunToolAsync(AdbPath, args, ct, StreamSideloadProgress,
                timeout: TimeSpan.FromMinutes(30));
            if (output == null)
                return FlashResult.Fail("Sideload was cancelled.");

            var lower = output.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error") || lower.Contains("cannot read"))
            {
                var errorInfo = _errorTranslator.TranslateAdbError(output);
                return FlashResult.Fail(
                    $"Sideload failed: {errorInfo.UserMessage}",
                    _errorTranslator.ExtractErrorMessage(output),
                    errorInfo);
            }

            ReportProgress(100, "Sideload", null, "Sideload completed successfully.");
            return FlashResult.Ok("Update package applied via sideload.");
        }

        /// <summary>
        /// Reboot an ADB-connected device into the bootloader.
        /// </summary>
        public async Task<FlashResult> RebootToBootloaderAsync(string serial, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            ReportProgress(10, "Reboot to bootloader", null, "Rebooting device to bootloader...");
            Log("Rebooting device to bootloader via ADB.");

            var args = string.IsNullOrWhiteSpace(serial)
                ? "reboot bootloader"
                : $"-s \"{serial}\" reboot bootloader";
            var output = await RunToolAsync(AdbPath, args, ct, timeout: TimeSpan.FromSeconds(30));
            if (output == null)
                return FlashResult.Fail("Reboot to bootloader was cancelled.");

            ReportProgress(15, "Reboot to bootloader", null,
                "Waiting for device to appear in fastboot...");

            var deadline = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < deadline)
            {
                if (ct.IsCancellationRequested)
                    return FlashResult.Fail("Reboot to bootloader cancelled.");
                await Task.Delay(1500, ct);
                if (await IsDeviceInFastbootAsync(serial, ct))
                {
                    ReportProgress(18, "Reboot to bootloader", null,
                        "Device detected in fastboot.");
                    return FlashResult.Ok("Device is in fastboot/bootloader mode.");
                }
            }

            return FlashResult.Fail(
                "Device did not appear in fastboot mode after rebooting.",
                "Ensure the device is connected and the bootloader supports fastboot.");
        }

        /// <summary>
        /// Reboot a fastboot-connected device to the given target (system, recovery, bootloader).
        /// </summary>
        public async Task<FlashResult> RebootAsync(string serial, string target, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            var step = $"Reboot to {target}";
            ReportProgress(95, step, null, $"Rebooting device to {target}...");
            Log($"Rebooting device to {target}.");

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"reboot {target}"
                : $"-s \"{serial}\" reboot {target}";
            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(30));
            if (output == null)
                return FlashResult.Fail($"Reboot to {target} was cancelled.");

            ReportProgress(100, step, null, $"Rebooted to {target}.");
            return FlashResult.Ok($"Device rebooted to {target}.");
        }

        /// <summary>
        /// Reboot an ADB-connected device to the given target (system, recovery, bootloader).
        /// </summary>
        public async Task<FlashResult> RebootViaAdbAsync(string serial, string target, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            var step = $"Reboot to {target}";
            ReportProgress(95, step, null, $"Rebooting device to {target} via ADB...");
            Log($"Rebooting device to {target} via ADB.");

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"reboot {target}"
                : $"-s \"{serial}\" reboot {target}";
            var output = await RunToolAsync(AdbPath, args, ct, timeout: TimeSpan.FromSeconds(30));
            if (output == null)
                return FlashResult.Fail($"Reboot to {target} was cancelled.");

            ReportProgress(100, step, null, $"Rebooted to {target}.");
            return FlashResult.Ok($"Device rebooted to {target} via ADB.");
        }

        #endregion

        #region Cache wipe

        /// <summary>
        /// Wipe the cache partition of a device in fastboot mode.
        /// </summary>
        public async Task<FlashResult> WipeCacheAsync(string serial, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            ReportProgress(90, "Wipe cache", "cache", "Wiping cache partition...");
            Log("Wiping cache partition via fastboot.");

            var args = string.IsNullOrWhiteSpace(serial)
                ? "erase cache"
                : $"-s \"{serial}\" erase cache";
            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(60));
            if (output == null)
                return FlashResult.Fail("Cache wipe was cancelled.");

            var lower = (output ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error"))
            {
                // A/B devices (Android 10+) have no cache partition — 'erase
                // cache' always fails there. Confirm before degrading to a
                // warning so a genuine erase failure on a device that HAS a
                // cache partition still aborts the flash.
                var probeArgs = string.IsNullOrWhiteSpace(serial)
                    ? "getvar partition-size:cache"
                    : $"-s \"{serial}\" getvar partition-size:cache";
                var probe = await RunToolAsync(FastbootPath, probeArgs, ct, timeout: TimeSpan.FromSeconds(15));
                var probeLower = (probe ?? string.Empty).ToLowerInvariant();
                var hasCache = !probeLower.Contains("not found") && !probeLower.Contains("failed");
                if (!hasCache)
                {
                    Log("[Wipe] No cache partition on this device (A/B layout) — wipe skipped, continuing.");
                    ReportProgress(92, "Wipe cache", "cache", "No cache partition — skipped.");
                    return FlashResult.Ok("No cache partition (A/B device) — skipped.");
                }
                return TranslateFastbootFailure(output, "cache wipe");
            }

            ReportProgress(92, "Wipe cache", "cache", "Cache wiped successfully.");
            return FlashResult.Ok("Cache partition wiped.");
        }

        #endregion

        #region Partition flashing

        /// <summary>
        /// Flash a single image to a partition. Handles A/B slot suffixing automatically.
        /// </summary>
        public async Task<FlashResult> FlashPartitionAsync(string serial, string partition,
            string imagePath, string slot, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            if (!File.Exists(imagePath))
                return FlashResult.Fail($"Image file not found: {imagePath}");

            // NOTE: no ReportProgress(0) here — callers own the percent step
            // (per-partition counting); a zero event would yank the bar back
            // to the start of its slice on every image.
            Log($"Flashing partition '{partition}' from {imagePath} (slot={slot ?? "default"}).");

            var targetPartition = partition;
            if (!string.IsNullOrWhiteSpace(slot))
            {
                // Fastboot slot suffixes are lowercase on the device (boot_a);
                // uppercase (boot_A) reports "partition size: 0" and fails
                // with remote 'Error flashing partition'.
                targetPartition = $"{partition}_{slot.ToLowerInvariant()}";
            }

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"flash \"{targetPartition}\" \"{imagePath}\""
                : $"-s \"{serial}\" flash \"{targetPartition}\" \"{imagePath}\"";

            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(300));
            if (output == null)
                return FlashResult.Fail($"Flash of {partition} was cancelled.");

            var lower = output.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error") || lower.Contains("cannot flash"))
            {
                return TranslateFastbootFailure(output, $"flash {partition}");
            }

            Log($"[✓] Partition '{partition}' flashed successfully.");
            return FlashResult.Ok($"Partition '{partition}' flashed.");
        }

        /// <summary>
        /// Flash vbmeta with verity/verification disabled. Required by many
        /// cross-ROM migrations (AOSP <-> HyperOS/MIUI) to prevent bootloops.
        /// </summary>
        public async Task<FlashResult> FlashVbmetaDisabledAsync(string serial, string partition,
            string imagePath, string slot, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            if (!File.Exists(imagePath))
                return FlashResult.Fail($"Image file not found: {imagePath}");

            var targetPartition = string.IsNullOrWhiteSpace(slot) ? partition : $"{partition}_{slot.ToLowerInvariant()}";
            Log($"Flashing '{targetPartition}' with verity/verification DISABLED (anti-bootloop for cross-ROM migrations).");

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"--disable-verity --disable-verification flash \"{targetPartition}\" \"{imagePath}\""
                : $"-s \"{serial}\" --disable-verity --disable-verification flash \"{targetPartition}\" \"{imagePath}\"";

            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(300));
            if (output == null)
                return FlashResult.Fail($"Flash of {partition} was cancelled.");

            var lower = output.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error"))
            {
                return TranslateFastbootFailure(output, $"flash {partition} (verity disabled)");
            }

            Log($"'{targetPartition}' flashed with verity/verification disabled.");
            return FlashResult.Ok($"'{targetPartition}' flashed (verity disabled).");
        }

        #endregion

        #region Custom recovery (TWRP / OrangeFox)

        /// <summary>
        /// Temp-boot a recovery image via 'fastboot boot'. Only .img files are
        /// bootable — .zip recovery packages must be sideloaded from recovery.
        /// </summary>
        public async Task<FlashResult> BootRecoveryImageAsync(string serial, string imagePath, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return FlashResult.Fail($"Recovery image not found: {imagePath}");

            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext != ".img")
            {
                return FlashResult.Fail(
                    "Only .img recovery images can be booted.",
                    "ZIP recovery packages are installed via sideload: boot or flash a .img recovery first, then use APPLY UPDATE.ZIP (SIDELOAD).");
            }

            ReportProgress(5, "Boot recovery", null, "Booting recovery image (temporary, not installed)...");
            Log($"Booting recovery image (fastboot boot): {imagePath}");

            var args = string.IsNullOrWhiteSpace(serial)
                ? $"boot \"{imagePath}\""
                : $"-s \"{serial}\" boot \"{imagePath}\"";

            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(60));
            if (output == null)
                return FlashResult.Fail("Boot recovery was cancelled.");

            var lower = output.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error") || lower.Contains("unknown command"))
            {
                return TranslateFastbootFailure(output, "boot recovery");
            }

            ReportProgress(100, "Boot recovery", null, "Recovery booted (temporary).");
            return FlashResult.Ok("Recovery booted temporarily — it will disappear on the next reboot.");
        }

        /// <summary>
        /// Permanently install a recovery image to the recovery partition.
        /// On A/B devices the active slot is targeted automatically.
        /// </summary>
        public async Task<FlashResult> FlashRecoveryImageAsync(string serial, string imagePath, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return FlashResult.Fail($"Recovery image not found: {imagePath}");

            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext != ".img")
            {
                return FlashResult.Fail(
                    "Only .img recovery images can be flashed to the recovery partition.",
                    "ZIP recovery packages are installed via sideload: boot or flash a .img recovery first, then use APPLY UPDATE.ZIP (SIDELOAD).");
            }

            ReportProgress(5, "Flash recovery", null, "Detecting slot...");
            var slot = await QueryActiveSlotAsync(serial, ct);
            var isAB = slot == "A" || slot == "B";
            if (isAB) Log($"[Slot] Flashing recovery to active slot {slot}.");

            return await FlashPartitionAsync(serial, "recovery", imagePath, isAB ? slot : null, ct);
        }

        #endregion

        #region Migration safety (wipe / anti-rollback)

        /// <summary>
        /// Full factory wipe via 'fastboot -w' (userdata + cache). Required when
        /// crossing ROM families (AOSP <-> MIUI/HyperOS). User data is destroyed.
        /// </summary>
        public async Task<FlashResult> FactoryWipeAsync(string serial, CancellationToken ct)
        {
            if (!IsValidSerial(serial))
            {
                return FlashResult.Fail("Invalid device serial.",
                    "The device serial contains characters that are not allowed.",
                    _errorTranslator.TranslateFastbootError("device not found"));
            }

            Log("[WIPE] Performing factory wipe (fastboot -w): userdata + cache will be ERASED.");
            ReportProgress(10, "Factory wipe", null, "Erasing userdata and cache...");

            var args = string.IsNullOrWhiteSpace(serial) ? "-w" : $"-s \"{serial}\" -w";
            var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromMinutes(10));
            if (output == null)
                return FlashResult.Fail("Factory wipe was cancelled.");

            var lower = output.ToLowerInvariant();
            if (lower.Contains("failed") || lower.Contains("error"))
            {
                return TranslateFastbootFailure(output, "factory wipe");
            }

            ReportProgress(100, "Factory wipe", null, "Factory wipe complete.");
            return FlashResult.Ok("Factory wipe complete (userdata + cache erased).");
        }

        /// <summary>
        /// Query the device's anti-rollback version (Xiaomi/HyperOS: 'getvar anti').
        /// Returns null when the device does not expose it.
        /// </summary>
        public async Task<int?> QueryAntiRollbackAsync(string serial, CancellationToken ct)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(serial) ? "getvar anti" : $"-s \"{serial}\" getvar anti";
                var output = await RunToolAsync(FastbootPath, args, ct, timeout: TimeSpan.FromSeconds(10));
                var match = Regex.Match(output ?? string.Empty, @"anti:\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var anti))
                {
                    return anti;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to query anti-rollback version: {Message}", ex.Message);
            }
            return null;
        }

        #endregion

        #region Payload extraction (OTA zips)

        /// <summary>
        /// Extract all flashable *.img files from a MIUI/HyperOS-style fastboot
        /// package (.tgz / .tar.gz). Partition names are derived from the file
        /// names (boot.img -> boot). Returns partition -> extracted path pairs.
        /// </summary>
        private List<KeyValuePair<string, string>> ExtractTarGzImages(string tgzPath, string outputDirectory)
        {
            var results = new List<KeyValuePair<string, string>>();
            var validName = new Regex("^[a-z0-9_]+$");
            Directory.CreateDirectory(outputDirectory);

            using (var source = new FileStream(tgzPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress))
            {
                var header = new byte[512];
                var buffer = new byte[64 * 1024];
                string pendingLongName = null;

                while (true)
                {
                    if (!ReadExactly(gzip, header, 512)) break;

                    // Two consecutive zero blocks mark the archive end.
                    if (header.All(b => b == 0)) break;

                    var name = pendingLongName ?? ParseTarName(header, 0, 100);
                    pendingLongName = null;

                    var sizeStr = System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                    if (!long.TryParse(sizeStr, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
                        break;

                    var typeflag = (char)(header[156] == 0 ? (byte)'0' : header[156]);
                    var dataBlocks = (size + 511) / 512 * 512;

                    if (typeflag == 'L')
                    {
                        // GNU long-name entry: data holds the next entry's name.
                        var nameBytes = new byte[size];
                        if (!ReadExactly(gzip, nameBytes, (int)size)) break;
                        Skip(gzip, dataBlocks - size);
                        pendingLongName = System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                        continue;
                    }

                    if (typeflag == 'x' || typeflag == 'g' || typeflag == '2' || typeflag == '3')
                    {
                        // PAX header / global header: skip the payload.
                        Skip(gzip, dataBlocks);
                        continue;
                    }

                    var baseName = Path.GetFileName(name.Replace('\\', '/')) ?? string.Empty;
                    var partition = Path.GetFileNameWithoutExtension(baseName).ToLowerInvariant();

                    if ((typeflag == '0' || typeflag == '\0') &&
                        baseName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                        validName.IsMatch(partition) && size > 0)
                    {
                        var outPath = Path.Combine(outputDirectory, baseName);
                        using (var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                        {
                            long remaining = size;
                            while (remaining > 0)
                            {
                                var chunk = (int)Math.Min(buffer.Length, remaining);
                                var read = gzip.Read(buffer, 0, chunk);
                                if (read <= 0) break;
                                outStream.Write(buffer, 0, read);
                                remaining -= read;
                            }
                        }
                        Skip(gzip, dataBlocks - size);
                        results.Add(new KeyValuePair<string, string>(partition, outPath));
                        _logger.Debug("Extracted from tar: {Partition} ({Path})", partition, outPath);
                    }
                    else
                    {
                        Skip(gzip, dataBlocks);
                    }
                }
            }

            return results;
        }

        private static string ParseTarName(byte[] header, int offset, int count)
        {
            var end = offset;
            while (end < offset + count && header[end] != 0) end++;
            return System.Text.Encoding.UTF8.GetString(header, offset, end - offset).Trim();
        }

        private static bool ReadExactly(System.IO.Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0) return false;
                total += read;
            }
            return true;
        }

        private static void Skip(System.IO.Stream stream, long count)
        {
            var buffer = new byte[64 * 1024];
            while (count > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, count);
                var read = stream.Read(buffer, 0, chunk);
                if (read <= 0) break;
                count -= read;
            }
        }

        /// <summary>
        /// Delete leftover payload extraction folders from previous runs
        /// (%TEMP%\SRFlasher\extract_*). Fails silently on files
        /// still in use; they are removed on a later run.
        /// </summary>
        private string _lastExtractDir;

        /// <summary>
        /// Deletes the extraction folder of the last completed flash right
        /// away — a hard kill can no longer leave gigabytes behind; the
        /// startup/close sweeps stay as the fallback.
        /// </summary>
        private void CleanLastExtraction()
        {
            var dir = _lastExtractDir;
            _lastExtractDir = null;
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    _logger.Debug("Removed temp extraction folder after flash: {Dir}", dir);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Temp extraction cleanup deferred to next sweep: {Message}", ex.Message);
            }
        }

        public void CleanTempExtractions()
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "SRFlasher");
                if (!Directory.Exists(root))
                    return;

                foreach (var dir in Directory.GetDirectories(root, "extract_*"))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        _logger.Debug("Removed temp extraction folder: {Dir}", dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Could not remove temp extraction folder {Dir}: {Message}", dir, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Temp extraction cleanup failed: {Message}", ex.Message);
            }
        }

        public async Task<List<KeyValuePair<string, string>>> ExtractPayloadAsync(
            string zipPath, string outputDirectory, IEnumerable<string> partitions, CancellationToken ct)
        {
            var results = new List<KeyValuePair<string, string>>();
            string payloadPath = await ExtractPayloadBinFromZipAsync(zipPath, outputDirectory, ct);
            if (payloadPath == null)
            {
                Log("payload.bin not found in ROM zip; payload extraction skipped.");
                return results;
            }
            Directory.CreateDirectory(outputDirectory);

            // Partition filter MUST use -partitions (comma-separated): positional
            // args are ignored by payload-dumper-go, which then dumps every
            // partition in the payload (gigabytes of wasted extraction).
            // Go's flag parser stops at the first positional argument, so ALL
            // flags must come BEFORE the payload path — flags placed after it
            // are silently ignored (-o included, which breaks the output path).
            var arguments = $"-o \"{outputDirectory}\" -partitions \"{string.Join(",", partitions)}\" \"{payloadPath}\"";
            Log($"Running payload-dumper-go: {arguments}");
            var output = await RunToolAsync(PayloadDumperPath, arguments, ct,
                timeout: TimeSpan.FromMinutes(10));
            if (output == null)
            {
                Log("payload-dumper-go extraction was cancelled.");
                return results;
            }
            foreach (var partition in partitions)
            {
                // Some payload-dumper-go builds write into an
                // extracted_<timestamp> subdirectory of the output path, others
                // write directly — search recursively so both work.
                var candidate = Directory.EnumerateFiles(
                        outputDirectory, $"{partition}.img", SearchOption.AllDirectories)
                    .OrderBy(f => f.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(candidate))
                {
                    results.Add(new KeyValuePair<string, string>(partition, candidate));
                    Log($"Extracted {partition} -> {candidate}");
                }
                else
                {
                    Log($"Warning: {partition} not found in extracted payload output.");
                }
            }
            return results;
        }

        /// <summary>
        /// Extract loose *.img entries from a boot-chain zip (a package that ships
        /// its images directly rather than inside a payload.bin).
        /// </summary>
        private async Task<List<KeyValuePair<string, string>>> ExtractLooseImagesAsync(
            string zipPath, string outputDirectory, ICollection<string> wanted, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var results = new List<KeyValuePair<string, string>>();
                Directory.CreateDirectory(outputDirectory);

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var name = (entry.FullName ?? string.Empty).Replace('\\', '/');
                        if (!name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var partition = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(partition)) continue;

                        if (wanted != null && wanted.Count > 0 &&
                            !wanted.Contains(partition, StringComparer.OrdinalIgnoreCase))
                            continue;

                        var destination = Path.Combine(outputDirectory, partition + ".img");
                        entry.ExtractToFile(destination, overwrite: true);
                        results.Add(new KeyValuePair<string, string>(partition, destination));
                    }
                }

                return results;
            }, ct);
        }

        private async Task<string> ExtractPayloadBinFromZipAsync(string zipPath, string outputDirectory,
            CancellationToken ct)
        {
            try
            {
                // ZipFile.OpenRead + ExtractToFile are blocking I/O; run them on a
                // worker thread so the UI stays responsive on large OTA packages.
                var payloadPath = await Task.Run(() =>
                {
                    Directory.CreateDirectory(outputDirectory);
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        var payloadEntry = archive.GetEntry("payload.bin");
                        if (payloadEntry == null)
                            return null;

                        var path = Path.Combine(outputDirectory, "payload.bin");
                        payloadEntry.ExtractToFile(path, overwrite: true);
                        return path;
                    }
                }, ct);

                if (payloadPath == null)
                    return null;

                Log($"Extracted payload.bin from ZIP to {payloadPath}");
                return payloadPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to extract payload.bin from ZIP: {Message}", ex.Message);
                return null;
            }
        }

        #endregion

        #region Full ROM flash pipeline

        public async Task<FlashResult> FlashRomAsync(FlashContext context, CancellationToken ct)
        {
            CancellationTokenSource localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_ctsLock) { _cts = localCts; }
            var token = localCts.Token;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var serial = context.Serial;
                Log($"=== Starting ROM flash ===");
                Log($"ROM: {context.RomFilePath}");
                Log($"Serial: {serial ?? "(any)"}");
                Log($"Partitions: {string.Join(", ", context.SelectedPartitions)}");

                if (string.IsNullOrWhiteSpace(context.RomFilePath) || !File.Exists(context.RomFilePath))
                {
                    return FlashResult.Fail("No valid ROM file selected.");
                }

                var isTarGzEarly = Path.GetExtension(context.RomFilePath).ToLowerInvariant() == ".tgz" ||
                    (Path.GetExtension(context.RomFilePath).ToLowerInvariant() == ".gz" &&
                     Path.GetFileName(context.RomFilePath).ToLowerInvariant().EndsWith(".tar.gz"));

                if ((context.SelectedPartitions == null || context.SelectedPartitions.Count == 0) && !isTarGzEarly)
                {
                    return FlashResult.Fail("No partitions selected for flashing.");
                }

                if (!IsValidSerial(serial))
                {
                    return FlashResult.Fail("Invalid device serial.",
                        "The device serial contains characters that are not allowed.",
                        _errorTranslator.TranslateFastbootError("device not found"));
                }

                ReportProgress(5, "Detect device", null, "Detecting device mode...");
                var mode = await DetectDeviceModeAsync(serial, token);
                Log($"Detected device mode: {mode}");

                // Battery pre-flight check (only possible while in ADB mode).
                if (mode == "AdbSystem" || mode == "AdbRecovery" || mode == "Sideload")
                {
                    ReportProgress(6, "Check battery", null, "Checking battery level...");
                    var batteryLevel = await GetBatteryLevelAsync(serial, token);
                    if (batteryLevel.HasValue)
                    {
                        Log($"Battery level: {batteryLevel.Value}% (minimum required: {MinBatteryLevel}%)");
                        if (batteryLevel.Value < MinBatteryLevel && !AllowBelowMinBattery)
                        {
                            return FlashResult.Fail(
                                $"Battery too low to flash: {batteryLevel.Value}% (minimum {MinBatteryLevel}%).",
                                "Please charge the device above the threshold and try again.",
                                new ErrorTranslator.ErrorInfo(
                                    $"Battery too low to flash: {batteryLevel.Value}% (minimum {MinBatteryLevel}%).",
                                    "Please charge the device above the threshold and try again."));
                        }
                        if (batteryLevel.Value < MinBatteryLevel)
                        {
                            Log("[Battery] Below the recommended minimum — continuing at the user's request (confirmed at the low-battery popup).");
                        }
                    }
                    else
                    {
                        Log("[Battery] Could not determine battery level; proceeding with caution.");
                    }
                }

                if (mode == "AdbSystem" || mode == "AdbRecovery" || mode == "Sideload")
                {
                    var rebootRes = await RebootToBootloaderAsync(serial, token);
                    if (!rebootRes.Success)
                        return rebootRes;
                    mode = "Fastboot";
                }

                if (mode != "Fastboot" && mode != "FastbootD")
                {
                    return FlashResult.Fail(
                        "Device is not in fastboot/bootloader mode.",
                        "Reboot the device to the bootloader (adb reboot bootloader) and try again.",
                        _errorTranslator.TranslateFastbootError("device not found"));
                }

                ReportProgress(12, "Check bootloader", null, "Checking bootloader lock state...");
                var lockState = await QueryBootloaderLockStateAsync(serial, token);
                Log($"Bootloader lock state: {lockState}");
                if (lockState == "Locked")
                {
                    return FlashResult.Fail(
                        "Bootloader is locked. Flashing is not permitted.",
                        "Unlock the bootloader first (fastboot oem unlock). Note: this wipes all user data.",
                        _errorTranslator.TranslateFastbootError("cannot flash while locked"));
                }

                ReportProgress(13, "Anti-rollback check", null, "Checking anti-rollback version...");
                var anti = await QueryAntiRollbackAsync(serial, token);
                if (anti.HasValue)
                {
                    Log($"[Anti-rollback] Device anti version: {anti.Value}");
                    if (anti.Value > 0)
                    {
                        Log($"[WARNING] Anti-rollback is ACTIVE ({anti.Value}). Flashing firmware older " +
                            "than this version may PERMANENTLY BRICK the device — this cannot be undone.");
                    }
                }
                else
                {
                    Log("[Anti-rollback] Device does not expose an anti version (non-Xiaomi or unsupported).");
                }

                // Optional pre-flash wipe (clean install): erase userdata + cache
                // BEFORE any image is written. Deselected = dirty flash.
                if (context.WipeData)
                {
                    Log("[Wipe] CLEAN FLASH requested — performing factory wipe (userdata + cache) before flashing...");
                    ReportProgress(13, "Factory wipe", null, "Erasing userdata and cache (clean flash)...");
                    var wipeRes = await FactoryWipeAsync(serial, token);
                    if (!wipeRes.Success)
                    {
                        Log($"[ERROR] Pre-flash wipe failed: {wipeRes.Message}");
                        return wipeRes;
                    }
                }
                else
                {
                    Log("[Wipe] DIRTY FLASH — existing data is preserved (no pre-flash wipe).\n" +
                        "        If the ROM is from a different family (e.g. HyperOS <-> AOSP), consider WIPE DATA.");
                }

                ReportProgress(14, "Detect slot", null, "Detecting active slot...");
                var activeSlot = await QueryActiveSlotAsync(serial, token);
                Log($"Active slot: {activeSlot}");
                bool isAB = activeSlot == "A" || activeSlot == "B";

                // Resolve the requested target slot: null/"Auto" flashes the active slot;
                // "Both" flashes both slots; "A"/"B" force one slot regardless of active.
                var slotsToFlash = new List<string>();
                if (!isAB)
                {
                    slotsToFlash.Add(null); // non-A/B device: no slot suffix
                }
                else if (string.Equals(context.TargetSlot, "Both", StringComparison.OrdinalIgnoreCase))
                {
                    slotsToFlash.Add("A");
                    slotsToFlash.Add("B");
                    Log("[Slot] Target: BOTH slots (A and B).");
                }
                else if (string.Equals(context.TargetSlot, "A", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(context.TargetSlot, "B", StringComparison.OrdinalIgnoreCase))
                {
                    var forced = context.TargetSlot.ToUpperInvariant();
                    slotsToFlash.Add(forced);
                    if (forced != activeSlot)
                        Log($"[Slot] Target: {forced} (inactive slot — image must match this slot's bootloader).") ;
                    else
                        Log($"[Slot] Target: {forced} (active slot).");
                }
                else
                {
                    slotsToFlash.Add(activeSlot);
                    Log($"[Slot] Target: auto (active slot {activeSlot}).");
                }

                ReportProgress(18, "Prepare images", null, "Preparing flash images...");
                var extLower = Path.GetExtension(context.RomFilePath).ToLowerInvariant();
                var fileNameLower = Path.GetFileName(context.RomFilePath).ToLowerInvariant();
                var isZip = extLower == ".zip";
                var isTarGz = extLower == ".tgz" || (extLower == ".gz" && fileNameLower.EndsWith(".tar.gz"));
                List<KeyValuePair<string, string>> imagesToFlash;

                if (isZip)
                {
                    var extractDir = Path.Combine(Path.GetTempPath(), "SRFlasher",
                        $"extract_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(extractDir);
                    _lastExtractDir = extractDir;

                    var partitionsToExtract = context.SelectedPartitions
                        .Where(p => PayloadExtractablePartitions.Contains(p, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (partitionsToExtract.Count > 0)
                    {
                        imagesToFlash = await ExtractPayloadAsync(
                            context.RomFilePath, extractDir, partitionsToExtract, token);
                    }
                    else
                    {
                        imagesToFlash = new List<KeyValuePair<string, string>>();
                        Log("No payload-extractable partitions selected.");
                    }

                    // A boot-chain zip carries its images as loose .img files
                    // instead of a payload — flash the ones it actually ships.
                    if (imagesToFlash.Count == 0)
                    {
                        imagesToFlash = await ExtractLooseImagesAsync(
                            context.RomFilePath, extractDir, context.SelectedPartitions, token);

                        if (imagesToFlash.Count > 0)
                        {
                            Log($"Loose images found in the archive: {string.Join(", ", imagesToFlash.Select(k => k.Key))}");
                        }
                    }
                }
                else if (isTarGz)
                {
                    // MIUI/HyperOS fastboot package: loose *.img files inside a tar.gz.
                    // All valid images are flashed; partition selection is not used.
                    var extractDir = Path.Combine(Path.GetTempPath(), "SRFlasher",
                        $"extract_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(extractDir);
                    _lastExtractDir = extractDir;

                    Log("HyperOS/MIUI fastboot package detected — extracting all flashable images...");
                    imagesToFlash = await Task.Run(() => ExtractTarGzImages(context.RomFilePath, extractDir), token);

                    if (imagesToFlash.Count == 0)
                    {
                        return FlashResult.Fail(
                            "No flashable images were found in the package.",
                            "Ensure the file is an unmodified HyperOS/MIUI fastboot ROM (.tgz).");
                    }
                    Log($"Extracted {imagesToFlash.Count} image(s): " +
                        string.Join(", ", imagesToFlash.Select(k => k.Key)));
                }
                else
                {
                    var firstPartition = context.SelectedPartitions.First();
                    imagesToFlash = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(firstPartition, context.RomFilePath)
                    };

                    if (context.SelectedPartitions.Count > 1)
                    {
                        Log($"[WARNING] A single image file can only be flashed to one partition. " +
                            $"\"{firstPartition}\" will be flashed; the other selected partitions " +
                            $"({string.Join(", ", context.SelectedPartitions.Skip(1))}) will be skipped. " +
                            "Use a ROM zip to flash multiple partitions.");
                    }
                }

                if (imagesToFlash.Count == 0 && isZip)
                {
                    return FlashResult.Fail(
                        "No flashable images were extracted from the ROM zip.",
                        "Ensure the ROM contains a payload.bin with the selected partitions.");
                }

                int total = imagesToFlash.Count * slotsToFlash.Count;
                int index = 0;
                foreach (var kvp in imagesToFlash)
                {
                    if (token.IsCancellationRequested)
                        return FlashResult.Fail("Flash operation cancelled by user.");

                    var partition = kvp.Key;
                    var imagePath = kvp.Value;

                    foreach (var targetSlot in slotsToFlash)
                    {
                        if (token.IsCancellationRequested)
                            return FlashResult.Fail("Flash operation cancelled by user.");

                        index++;
                        int pct = 20 + (int)((double)(index - 1) / total * 60);
                        var slotLabel = isAB ? targetSlot : null;
                        ReportProgress(pct, "Flash partition", partition,
                            $"Flashing {partition}{(isAB ? $" ({targetSlot})" : "")} ({index}/{total})...");

                        // vbmeta with verity/verification disabled (cross-ROM migrations)
                        FlashResult flashRes;
                        if (context.DisableVerity && partition.Equals("vbmeta", StringComparison.OrdinalIgnoreCase))
                        {
                            flashRes = await FlashVbmetaDisabledAsync(serial, partition, imagePath, slotLabel, token);
                        }
                        else
                        {
                            flashRes = await FlashPartitionAsync(serial, partition, imagePath, slotLabel, token);
                        }
                        if (!flashRes.Success)
                            return flashRes;
                    }
                }

                ReportProgress(85, "Flash partition", null, "All selected partitions flashed.");

                // The extracted images are written — the temp payload
                // extraction (often 1-2 GB) can go immediately.
                CleanLastExtraction();

                if (context.WipeCache)
                {
                    var wipeRes = await WipeCacheAsync(serial, token);
                    if (!wipeRes.Success)
                        return wipeRes;
                }

                if (context.PostFlashTarget != null)
                {
                    Log($"[Post-flash] Rebooting to {context.PostFlashTarget}.");
                    return await RebootAsync(serial, context.PostFlashTarget, token);
                }

                Log("[Post-flash] Staying in the current mode (no reboot requested).");

                ReportProgress(100, "Complete", null, "Flash operation completed successfully.");
                stopwatch.Stop();
                Log($"=== ROM flash completed in {stopwatch.Elapsed:hh\\:mm\\:ss} ===");
                return FlashResult.Ok(string.IsNullOrWhiteSpace(context.SuccessMessage)
                    ? "ROM flashed successfully."
                    : context.SuccessMessage);
            }
            catch (OperationCanceledException)
            {
                return FlashResult.Fail("Flash operation cancelled by user.");
            }
            catch (Exception ex)
            {
                _logger.Error("Flash operation failed: {Message}", ex.Message);
                Log($"Unexpected error: {ex.Message}");
                return FlashResult.Fail("An unexpected error occurred during flashing.", ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                lock (_ctsLock)
                {
                    if (_cts == localCts)
                        _cts = null;
                }
                localCts?.Dispose();
            }
        }

        /// <summary>
        /// Detect the current device mode ('Fastboot', 'FastbootD', 'AdbSystem',
        /// 'AdbRecovery', 'Sideload', or null). Public so the ViewModel can poll
        /// mode transitions (e.g. rebooting into sideload for zips).
        /// </summary>
        public async Task<string> DetectModeAsync(string serial, CancellationToken ct)
            => await DetectDeviceModeAsync(serial, ct);

        private async Task<string> DetectDeviceModeAsync(string serial, CancellationToken ct)
        {
            if (await IsDeviceInFastbootAsync(serial, ct))
                return "Fastboot";

            var adbArgs = string.IsNullOrWhiteSpace(serial) ? "devices" : $"-s \"{serial}\" devices";
            var adbOutput = await RunToolAsync(AdbPath, adbArgs, ct, timeout: TimeSpan.FromSeconds(10));
            if (adbOutput == null)
                return "Disconnected";

            // Same robust row parsing as the fastboot check above: state column
            // split, then trimmed, so padding in any adb/fastboot build is fine.
            foreach (var line in adbOutput.Split(new[] { "\r\n", "\r", "\n" },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(new[] { '\t' }, 2);
                if (parts.Length != 2)
                    continue;

                switch (parts[1].Trim().ToLowerInvariant())
                {
                    case "sideload": return "Sideload";
                    case "sideload-auth": return "Sideload";
                    case "device": return "AdbSystem";
                    case "recovery": return "AdbRecovery";
                    case "unauthorized": return "Unauthorized";
                }
            }

            return "Disconnected";
        }

        private FlashResult TranslateFastbootFailure(string output, string operation)
        {
            var errorInfo = _errorTranslator.TranslateFastbootError(output);
            var details = _errorTranslator.ExtractErrorMessage(output);
            return FlashResult.Fail(
                $"Fastboot {operation} failed: {errorInfo.UserMessage}",
                details,
                errorInfo);
        }

        #endregion
    }
}
