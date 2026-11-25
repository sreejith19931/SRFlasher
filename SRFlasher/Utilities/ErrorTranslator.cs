using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SRFlasher.Services;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// Translates raw ADB/Fastboot error messages into user-friendly descriptions with solutions.
    /// </summary>
    public class ErrorTranslator
    {
        private readonly LoggingService _logger;

        private static readonly Dictionary<string, ErrorInfo> AdbErrorMap = new Dictionary<string, ErrorInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "device offline", new ErrorInfo(
                "Device is offline or not responding",
                "Try disconnecting and reconnecting the USB cable, or restarting ADB server with: adb kill-server")},

            { "no devices found", new ErrorInfo(
                "No Android devices detected",
                "Ensure the device is connected via USB. You may need to install USB drivers or enable USB debugging in Developer Options")},

            { "permission denied", new ErrorInfo(
                "Permission denied when accessing device",
                "This may require elevated privileges. Try running as Administrator")},

            { "insufficient space", new ErrorInfo(
                "Not enough storage space on device",
                "Free up storage space on the device's internal storage or SD card")},

            { "file not found", new ErrorInfo(
                "File cannot be found on device",
                "Verify the file path is correct and the file exists")},

            { "failed to read", new ErrorInfo(
                "Failed to read data from device",
                "Check the USB connection and try reconnecting the device")},

            { "failed to write", new ErrorInfo(
                "Failed to write data to device",
                "Ensure the device has sufficient storage and the USB connection is stable")},

            { "connection refused", new ErrorInfo(
                "Connection to device was refused",
                "The device may have disconnected. Try reconnecting and ensure USB debugging is enabled")},

            { "timeout", new ErrorInfo(
                "Operation timed out",
                "The device is not responding. Check USB connection and try again")},

            { "protocol error", new ErrorInfo(
                "Communication protocol error with device",
                "Try restarting ADB server or reconnecting the device")},

            { "invalid apk", new ErrorInfo(
                "The APK file is invalid or corrupted",
                "Download the APK again from a trusted source")},

            { "push failed", new ErrorInfo(
                "Failed to push file to device",
                "Ensure device storage is not full and USB connection is stable")},

            { "pull failed", new ErrorInfo(
                "Failed to pull file from device",
                "Check USB connection and ensure file exists on device")},
        };

        private static readonly Dictionary<string, ErrorInfo> FastbootErrorMap = new Dictionary<string, ErrorInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "device not found", new ErrorInfo(
                "Device not in Fastboot mode",
                "Try rebooting device to Fastboot: adb reboot bootloader")},

            { "bootloader locked", new ErrorInfo(
                "Device bootloader is locked (OEM Lock is ON)",
                "Unlock the bootloader: fastboot oem unlock")},

            { "unlock failed", new ErrorInfo(
                "Failed to unlock bootloader",
                "Some manufacturers require additional steps or approval. Check device manufacturer's documentation")},

            { "image too large", new ErrorInfo(
                "Image file is too large for partition",
                "Try a different ROM version or verify the image is not corrupted")},

            { "partition not found", new ErrorInfo(
                "Partition does not exist on device",
                "Verify you're using the correct partition name or try flashing to a different partition")},

            { "flash failed", new ErrorInfo(
                "Failed to flash partition",
                "Verify the image is compatible with your device and the partition is valid")},

            { "bootloader update required", new ErrorInfo(
                "Bootloader needs to be updated first",
                "Update bootloader first using: fastboot flash bootloader [bootloader_file]")},

            { "device offline", new ErrorInfo(
                "Device lost connection during operation",
                "Ensure USB cable is properly connected and the device didn't reboot")},

            { "unknown command", new ErrorInfo(
                "Fastboot command not recognized",
                "Verify fastboot executable is not corrupted and supports the command")},

            { "cannot flash while locked", new ErrorInfo(
                "Cannot flash system partition while bootloader is locked",
                "Unlock the bootloader first: fastboot oem unlock")},
        };

        public class ErrorInfo
        {
            public string UserMessage { get; set; }
            public string Solution { get; set; }

            public ErrorInfo(string userMessage, string solution)
            {
                UserMessage = userMessage;
                Solution = solution;
            }
        }

        public ErrorTranslator()
        {
            _logger = Services.LoggingService.Instance;
        }

        /// <summary>
        /// Translate an ADB error message to user-friendly text.
        /// </summary>
        public ErrorInfo TranslateAdbError(string errorOutput)
        {
            return TranslateError(errorOutput, AdbErrorMap, "ADB");
        }

        /// <summary>
        /// Translate a Fastboot error message to user-friendly text.
        /// </summary>
        public ErrorInfo TranslateFastbootError(string errorOutput)
        {
            return TranslateError(errorOutput, FastbootErrorMap, "Fastboot");
        }

        /// <summary>
        /// Generic error translation for any tool output.
        /// </summary>
        public ErrorInfo TranslateError(string errorOutput, Dictionary<string, ErrorInfo> errorMap, string toolName)
        {
            if (string.IsNullOrWhiteSpace(errorOutput))
            {
                return new ErrorInfo("Unknown error occurred", "Check the full log for details");
            }

            errorOutput = errorOutput.ToLower();

            // Try exact matches first
            foreach (var kvp in errorMap)
            {
                if (errorOutput.Contains(kvp.Key))
                {
                    _logger.Information("Translated {ToolName} error: {ErrorKey}", toolName, kvp.Key);
                    return kvp.Value;
                }
            }

            // Try pattern matching for numeric error codes
            var errorCodeMatch = Regex.Match(errorOutput, @"error[:\s]+(\d+)", RegexOptions.IgnoreCase);
            if (errorCodeMatch.Success)
            {
                string errorCode = errorCodeMatch.Groups[1].Value;
                _logger.Warning("Unknown {ToolName} error code: {ErrorCode}", toolName, errorCode);
                return new ErrorInfo($"Error code: {errorCode}", "Check the full log output for more details");
            }

            // Return generic message if no match found
            _logger.Warning("Unable to translate {ToolName} error, returning generic message", toolName);
            return new ErrorInfo(
                $"An error occurred during {toolName} operation",
                "Check the full log output for technical details");
        }

        /// <summary>
        /// Extract just the error line from multiline output.
        /// </summary>
        public string ExtractErrorMessage(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                string lower = trimmed.ToLower();
                if (lower.Contains("error") ||
                    lower.Contains("failed") ||
                    lower.Contains("exception"))
                {
                    return trimmed;
                }
            }

            // If no error line found, return last non-empty line
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var trimmed = lines[i].Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    return trimmed;
            }

            return "Unknown error";
        }

        /// <summary>
        /// Create a user-friendly error summary from raw tool output.
        /// </summary>
        public string CreateErrorSummary(string rawOutput, string toolName)
        {
            var errorMessage = ExtractErrorMessage(rawOutput);
            var errorInfo = TranslateError(rawOutput, toolName.Equals("adb", StringComparison.OrdinalIgnoreCase) ? AdbErrorMap : FastbootErrorMap, toolName);

            return $"[{toolName.ToUpper()}] {errorInfo.UserMessage}\n\nSolution: {errorInfo.Solution}\n\nDetails: {errorMessage}";
        }
    }
}
