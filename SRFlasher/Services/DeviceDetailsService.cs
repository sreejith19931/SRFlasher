using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// Snapshot of everything we know about the connected device.
    /// Fields the current mode cannot provide are carried over from cache.
    /// </summary>
    public class DeviceDetails
    {
        public string Serial { get; set; }
        public string Mode { get; set; }
        public string Brand { get; set; }
        public string MarketingName { get; set; }
        public string Model { get; set; }
        public string Codename { get; set; }
        public string AndroidVersion { get; set; }
        public string SecurityPatch { get; set; }
        public string BootloaderState { get; set; }   // Unlocked / Locked / Unknown
        public int? BatteryLevel { get; set; }
        public bool? BatteryCharging { get; set; }
        public string ActiveSlot { get; set; }        // A / B / N/A (Non-A/B) / Unknown
        /// <summary>Anti-rollback version (Xiaomi/HyperOS 'getvar anti'); null when unsupported.</summary>
        public int? AntiVersion { get; set; }
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Gathers device identity, version, bootloader, battery, and slot details
    /// via ADB/fastboot, caching per serial so identity fields gathered in ADB
    /// mode remain visible when the device moves to fastboot (and vice versa).
    /// All tool output is parsed quietly (no UI log spam).
    /// </summary>
    public class DeviceDetailsService
    {
        private readonly ProcessManager _processManager;
        private readonly LoggingService _logger = LoggingService.Instance;
        private readonly Dictionary<string, DeviceDetails> _cache =
            new Dictionary<string, DeviceDetails>(StringComparer.OrdinalIgnoreCase);

        public DeviceDetailsService(ProcessManager processManager)
        {
            _processManager = processManager;
        }

        private string AdbPath => ToolPathResolver.GetToolPath("adb");
        private string FastbootPath => ToolPathResolver.GetToolPath("fastboot");

        /// <summary>
        /// Get a fresh snapshot for the device, merged with cached knowledge.
        /// </summary>
        public async Task<DeviceDetails> GetDetailsAsync(string serial, string mode, CancellationToken ct)
        {
            if (mode == "Disconnected")
            {
                // Show the last known state of the most recent device, flagged cached.
                var last = _cache.Values.OrderByDescending(d => d.LastSeen).FirstOrDefault();
                if (last != null)
                {
                    var snapshot = Clone(last);
                    snapshot.Mode = mode;
                    return snapshot;
                }

                return new DeviceDetails
                {
                    Serial = serial,
                    Mode = mode,
                    BootloaderState = "Unknown",
                    ActiveSlot = "Unknown"
                };
            }

            var details = _cache.TryGetValue(serial ?? string.Empty, out var existing)
                ? Clone(existing)
                : new DeviceDetails();
            details.Serial = serial;
            details.Mode = mode;
            details.LastSeen = DateTime.Now;

            try
            {
                if (mode == "AdbSystem" || mode == "AdbRecovery" || mode == "Sideload")
                {
                    await FillFromAdbAsync(details, serial, ct);
                }
                else if (mode == "Fastboot" || mode == "FastbootD")
                {
                    await FillFromFastbootAsync(details, serial, ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug("Device details refresh failed: {Message}", ex.Message);
            }

            _cache[serial ?? string.Empty] = Clone(details);
            return details;
        }

        private static DeviceDetails Clone(DeviceDetails d) => new DeviceDetails
        {
            Serial = d.Serial,
            Mode = d.Mode,
            Brand = d.Brand,
            MarketingName = d.MarketingName,
            Model = d.Model,
            Codename = d.Codename,
            AndroidVersion = d.AndroidVersion,
            SecurityPatch = d.SecurityPatch,
            BootloaderState = d.BootloaderState,
            BatteryLevel = d.BatteryLevel,
            BatteryCharging = d.BatteryCharging,
            ActiveSlot = d.ActiveSlot,
            LastSeen = d.LastSeen
        };

        private async Task FillFromAdbAsync(DeviceDetails details, string serial, CancellationToken ct)
        {
            // One getprop call provides brand, names, versions, slot, lock props.
            var output = await RunAsync(AdbPath, Args(serial, "shell getprop"), ct, TimeSpan.FromSeconds(10));
            if (!string.IsNullOrWhiteSpace(output))
            {
                var props = ParseGetprop(output);

                string Get(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                            return value.Trim();
                    }
                    return null;
                }

                details.Brand = Get("ro.product.brand", "ro.product.manufacturer");
                details.MarketingName = Get(
                    "ro.product.marketname", "ro.product.odm.marketname", "ro.product.system.marketname");
                details.Model = Get("ro.product.model");
                details.Codename = Get("ro.product.device");
                details.AndroidVersion = Get("ro.build.version.release");
                details.SecurityPatch = Get("ro.build.version.security_patch");

                var slot = Get("ro.boot.slot_suffix");
                if (!string.IsNullOrEmpty(slot))
                {
                    var s = slot.TrimStart('_').ToUpperInvariant();
                    if (s == "A" || s == "B")
                        details.ActiveSlot = s;
                }

                var vbs = Get("ro.boot.verifiedbootstate")?.ToLowerInvariant();
                var flashLocked = Get("ro.boot.flash.locked")?.Trim();
                if (vbs == "orange" || vbs == "yellow")
                    details.BootloaderState = "Unlocked";
                else if (vbs == "green")
                    details.BootloaderState = "Locked";
                else if (flashLocked == "0")
                    details.BootloaderState = "Unlocked";
                else if (flashLocked == "1")
                    details.BootloaderState = "Locked";
            }

            // Battery (may be unavailable in recovery/sideload; cache keeps the last value).
            var batteryOutput = await RunAsync(
                AdbPath, Args(serial, "shell dumpsys battery"), ct, TimeSpan.FromSeconds(5));
            if (!string.IsNullOrWhiteSpace(batteryOutput))
            {
                var levelMatch = Regex.Match(batteryOutput, @"level:\s*(\d+)", RegexOptions.IgnoreCase);
                if (levelMatch.Success && int.TryParse(levelMatch.Groups[1].Value, out var level))
                {
                    details.BatteryLevel = Math.Max(0, Math.Min(100, level));
                    details.BatteryCharging = Regex.IsMatch(
                        batteryOutput, @"(AC|USB) powered:\s*true", RegexOptions.IgnoreCase);
                }
            }
        }

        private async Task FillFromFastbootAsync(DeviceDetails details, string serial, CancellationToken ct)
        {
            // Bootloader lock state (fastboot writes to stderr; RunAsync merges).
            var unlocked = await RunAsync(FastbootPath, Args(serial, "getvar unlocked"), ct, TimeSpan.FromSeconds(5));
            var lower = (unlocked ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("unlocked: yes"))
                details.BootloaderState = "Unlocked";
            else if (lower.Contains("unlocked: no"))
                details.BootloaderState = "Locked";
            else
            {
                var oem = await RunAsync(FastbootPath, Args(serial, "oem device-info"), ct, TimeSpan.FromSeconds(5));
                var oemLower = (oem ?? string.Empty).ToLowerInvariant();
                if (oemLower.Contains("unlocked: true") || oemLower.Contains("device unlocked: true"))
                    details.BootloaderState = "Unlocked";
                else if (oemLower.Contains("unlocked: false") || oemLower.Contains("device unlocked: false"))
                    details.BootloaderState = "Locked";
            }

            // Active slot.
            var slotOut = await RunAsync(FastbootPath, Args(serial, "getvar current-slot"), ct, TimeSpan.FromSeconds(5));
            var slotLower = (slotOut ?? string.Empty).ToLowerInvariant();
            var slotMatch = Regex.Match(slotLower, @"current-slot:\s*_?([ab01])");
            if (slotMatch.Success)
            {
                details.ActiveSlot = (slotMatch.Groups[1].Value == "a" || slotMatch.Groups[1].Value == "0")
                    ? "A" : "B";
            }
            else if (slotLower.Contains("unknown variable") || slotLower.Contains("failed"))
            {
                details.ActiveSlot = "N/A (Non-A/B)";
            }

            // Anti-rollback version (Xiaomi/HyperOS devices expose 'getvar anti').
            var antiOut = await RunAsync(FastbootPath, Args(serial, "getvar anti"), ct, TimeSpan.FromSeconds(5));
            var antiMatch = Regex.Match(antiOut ?? string.Empty, @"anti:\s*(\d+)", RegexOptions.IgnoreCase);
            if (antiMatch.Success && int.TryParse(antiMatch.Groups[1].Value, out var antiVersion))
            {
                details.AntiVersion = antiVersion;
            }
            else
            {
                details.AntiVersion = null;
            }

            // Product codename (fastboot cannot provide brand/marketing name).
            var product = await RunAsync(FastbootPath, Args(serial, "getvar product"), ct, TimeSpan.FromSeconds(5));
            var productMatch = Regex.Match(product ?? string.Empty, @"product:\s*([\w\.\-]+)", RegexOptions.IgnoreCase);
            if (productMatch.Success && string.IsNullOrWhiteSpace(details.Codename))
                details.Codename = productMatch.Groups[1].Value;
        }

        private static string Args(string serial, string command) =>
            string.IsNullOrWhiteSpace(serial) ? command : $"-s \"{serial}\" {command}";

        private async Task<string> RunAsync(string tool, string args, CancellationToken ct, TimeSpan timeout)
        {
            var result = await _processManager.ExecuteAsync(tool, args, timeout, ct);
            if (result.TimedOut)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Output))
                sb.Append(result.Output);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(result.Error);
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> ParseGetprop(string output)
        {
            // getprop lines look like: [ro.product.brand]: [google]
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(output, @"\[([^\]]+)\]:\s*\[([^\]]*)\]"))
                map[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
            return map;
        }
    }
}
