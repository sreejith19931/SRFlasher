using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SRFlasher.Services
{
    /// <summary>
    /// What a package is, decided from its own contents rather than its file
    /// extension. This decides how it may be installed.
    /// </summary>
    public enum RomPackageKind
    {
        /// <summary>Nothing recognisable — no assumption is made.</summary>
        Unknown,
        /// <summary>A single boot-chain image (.img).</summary>
        BootImage,
        /// <summary>A zip carrying loose boot-chain images (boot.img, dtbo.img…).</summary>
        BootChainZip,
        /// <summary>A zip whose images live inside payload.bin (most A/B custom ROMs).</summary>
        PayloadOta,
        /// <summary>A factory image zip (android-info.txt + nested image archives).</summary>
        FactoryImage,
        /// <summary>A recovery-flashable zip (GApps, Magisk, kernel packages).</summary>
        AddOnZip,
        /// <summary>A HyperOS/MIUI fastboot package (.tar.gz).</summary>
        FastbootTgz
    }

    /// <summary>How a package has to be installed.</summary>
    public enum RomInstallMethod
    {
        /// <summary>Not decided yet (nothing selected).</summary>
        None,
        /// <summary>Partitions can be written directly over fastboot.</summary>
        FastbootFlash,
        /// <summary>The package installs itself from recovery (adb sideload).</summary>
        Sideload,
        /// <summary>Needs a method this app does not implement.</summary>
        Unsupported
    }

    /// <summary>
    /// Validates ROM files for compatibility, integrity, and format requirements.
    /// </summary>
    public class RomValidator
    {
        private readonly LoggingService _logger;

        // Supported ROM file formats
        private static readonly string[] SupportedExtensions = { ".zip", ".tar", ".tar.gz", ".tgz" };

        // Minimum ROM size (50 MB)
        private const long MinimumRomSize = 50 * 1024 * 1024;

        // Maximum ROM size (5 GB)
        private const long MaximumRomSize = 5L * 1024 * 1024 * 1024;

        // Partitions this app can flash, in the order the UI offers them.
        private static readonly string[] KnownPartitions = { "boot", "init_boot", "vendor_boot", "dtbo", "recovery" };

        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public string[] Errors { get; set; }
            public string[] Warnings { get; set; }
            public RomFileInfo FileInfo { get; set; }

            /// <summary>Codename(s) the package declares; empty when it names none.</summary>
            public string[] DeviceIdentifiers { get; set; }

            /// <summary>Archive entry the identifiers came from, for the log.</summary>
            public string DeviceIdentifierSource { get; set; }

            /// <summary>Partitions the archive carries; empty when it names none.</summary>
            public string[] FlashablePartitions { get; set; } = new string[0];

            // ==================== What this package actually is ====================
            // The distinction that matters: a package whose images are inside a
            // payload.bin cannot be installed by writing partitions over fastboot.
            // Classifying it here stops the app from calling a boot-chain write a
            // ROM install.

            /// <summary>Package family, decided from the archive's own contents.</summary>
            public RomPackageKind Kind { get; set; } = RomPackageKind.Unknown;

            /// <summary>How this package is meant to be installed.</summary>
            public RomInstallMethod InstallMethod { get; set; } = RomInstallMethod.None;

            /// <summary>Short human name for the package family.</summary>
            public string KindDisplay { get; set; } = string.Empty;

            /// <summary>One sentence stating what installing this package writes.</summary>
            public string PlanNote { get; set; } = string.Empty;

            /// <summary>
            /// Partitions that a fastboot flash of this package may write, as the
            /// package itself declares them. Empty means "not derivable" — never
            /// fall back to the saved check boxes, which would guess.
            /// </summary>
            public string[] PlanPartitions { get; set; } = new string[0];
        }

        public class RomFileInfo
        {
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public string FileExtension { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime ModifiedDate { get; set; }
        }

        /// <summary>What the app needs to know about a ROM archive's contents.</summary>
        public class ZipFacts
        {
            public bool HasPayloadBin { get; set; }
            public string[] DeviceIdentifiers { get; set; } = new string[0];
            public string DeviceIdentifierSource { get; set; }

            /// <summary>
            /// Partitions the archive actually carries as .img files, in the
            /// order the UI lists them. Empty for payload.bin packages, whose
            /// images are inside the payload rather than in the archive.
            /// </summary>
            public string[] FlashablePartitions { get; set; } = new string[0];

            /// <summary>True for a factory image (fastboot update package).</summary>
            public bool HasAndroidInfo { get; set; }

            /// <summary>True for a recovery-flashable package (updater-script).</summary>
            public bool HasUpdaterScript { get; set; }

            /// <summary>True when the archive carries nested archives (factory images).</summary>
            public bool HasNestedArchive { get; set; }

            public string Error { get; set; }
        }

        /// <summary>A checksum published next to the ROM file.</summary>
        public class ExpectedChecksum
        {
            public string Algorithm { get; set; }
            public string Hash { get; set; }
            public string SourcePath { get; set; }
        }

        public RomValidator()
        {
            _logger = LoggingService.Instance;
        }

        /// <summary>
        /// Validate a ROM file comprehensively.
        /// </summary>
        public async Task<ValidationResult> ValidateAsync(string romFilePath)
        {
            var result = new ValidationResult
            {
                Errors = new string[0],
                Warnings = new string[0],
                DeviceIdentifiers = new string[0]
            };

            var errors = new List<string>();
            var warnings = new List<string>();

            _logger.Information("Starting ROM validation for {RomPath}", romFilePath);

            // Check if file exists
            if (!File.Exists(romFilePath))
            {
                errors.Add($"ROM file not found: {romFilePath}");
                _logger.Error("ROM file not found: {RomPath}", romFilePath);
                result.Errors = errors.ToArray();
                result.IsValid = false;
                return result;
            }

            try
            {
                var fileInfo = new FileInfo(romFilePath);
                result.FileInfo = new RomFileInfo
                {
                    FileName = fileInfo.Name,
                    FileSize = fileInfo.Length,
                    FileExtension = fileInfo.Extension,
                    CreatedDate = fileInfo.CreationTime,
                    ModifiedDate = fileInfo.LastWriteTime
                };

                // Validate extension
                if (!IsValidExtension(fileInfo.Extension))
                {
                    errors.Add($"Unsupported file format: {fileInfo.Extension}. Supported formats: {string.Join(", ", SupportedExtensions)}");
                    _logger.Warning("Unsupported ROM file format: {Extension}", fileInfo.Extension);
                }

                // Validate file size
                if (fileInfo.Length < MinimumRomSize)
                {
                    errors.Add($"ROM file is too small ({FormatFileSize(fileInfo.Length)}). Minimum required: {FormatFileSize(MinimumRomSize)}");
                    _logger.Warning("ROM file too small: {FileSize} bytes", fileInfo.Length);
                }

                if (fileInfo.Length > MaximumRomSize)
                {
                    errors.Add($"ROM file is too large ({FormatFileSize(fileInfo.Length)}). Maximum allowed: {FormatFileSize(MaximumRomSize)}");
                    _logger.Warning("ROM file too large: {FileSize} bytes", fileInfo.Length);
                }

                // Check file readability
                try
                {
                    using (var stream = File.OpenRead(romFilePath))
                    {
                        var buffer = new byte[4096];
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                        {
                            errors.Add("ROM file is empty or unreadable");
                            _logger.Error("ROM file is empty or unreadable: {RomPath}", romFilePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Cannot read ROM file: {ex.Message}");
                    _logger.Error("Failed to read ROM file: {RomPath}: {Message}", romFilePath, ex.Message);
                }

                // Read what the package says about itself: whether it carries a
                // payload, and which device(s) it was built for. The device list
                // is what the compatibility gate compares against the phone.
                ZipFacts facts = null;
                if (fileInfo.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    facts = ReadZipFacts(romFilePath);
                    if (facts.Error != null)
                    {
                        warnings.Add($"Could not read ZIP contents: {facts.Error}");
                        _logger.Warning("Failed to read ZIP contents: {Message}", facts.Error);
                    }

                    if (facts.HasPayloadBin)
                    {
                        _logger.Information("ROM detected as OTA format (contains payload.bin)");
                    }

                    result.DeviceIdentifiers = facts.DeviceIdentifiers ?? new string[0];
                    result.DeviceIdentifierSource = facts.DeviceIdentifierSource;
                    result.FlashablePartitions = facts.FlashablePartitions;
                }
                else if (fileInfo.Extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase) ||
                         (fileInfo.Extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) &&
                          fileInfo.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.Information("ROM detected as HyperOS/MIUI fastboot package (tar.gz)");
                    warnings.Add("HyperOS/MIUI fastboot package: all flashable images inside will be flashed (partition selection is not used).");
                }

                // Decide what the package is and how it must be installed. This is
                // the difference between "flash the boot chain" and "install a ROM",
                // and it is what every plan sentence in the UI is built from.
                ApplyPackagePlan(result, result.FileInfo, facts);

                // Check available disk space for extraction
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(romFilePath));
                    if (drive.AvailableFreeSpace < fileInfo.Length * 2)
                    {
                        warnings.Add($"Limited disk space available. Ensure at least {FormatFileSize(fileInfo.Length * 2)} for extraction");
                        _logger.Warning("Low disk space for ROM extraction");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("Failed to check disk space: {Message}", ex.Message);
                }

                result.IsValid = errors.Count == 0;
            }
            catch (Exception ex)
            {
                errors.Add($"Validation error: {ex.Message}");
                _logger.Error("ROM validation failed: {Message}", ex.Message);
                result.IsValid = false;
            }

            result.Errors = errors.ToArray();
            result.Warnings = warnings.ToArray();

            if (result.IsValid)
            {
                _logger.Information("ROM validation passed: {FileName} ({FileSize})",
                    result.FileInfo?.FileName, FormatFileSize(result.FileInfo?.FileSize ?? 0));
            }
            else
            {
                _logger.Warning("ROM validation failed with {ErrorCount} error(s)", result.Errors.Length);
            }

            return result;
        }

        /// <summary>
        /// Read the facts this app cares about out of a ROM archive. Recovery and
        /// A/B packages declare their device in META-INF/com/android/metadata
        /// (pre-device=...), fastboot firmware packages in android-info.txt
        /// (require board=...). Anything unreadable is reported, never thrown.
        /// </summary>
        public ZipFacts ReadZipFacts(string romFilePath)
        {
            var facts = new ZipFacts();

            if (string.IsNullOrWhiteSpace(romFilePath) || !File.Exists(romFilePath))
                return facts;

            if (!romFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return facts;

            try
            {
                using (var archive = ZipFile.OpenRead(romFilePath))
                {
                    facts.HasPayloadBin = FindEntry(archive, "payload.bin") != null;

                    // What the package actually carries: a ROM that ships only
                    // boot and dtbo must not be flashed to five partitions.
                    facts.FlashablePartitions = ReadFlashablePartitions(archive);

                    // Family markers: a factory image declares android-info.txt and
                    // ships nested archives; a recovery package ships an updater-script.
                    var names = archive.Entries
                        .Select(e => (e.FullName ?? string.Empty).Replace('\\', '/'))
                        .ToList();

                    facts.HasAndroidInfo = names.Any(n =>
                        string.Equals(n, "android-info.txt", StringComparison.OrdinalIgnoreCase));
                    facts.HasUpdaterScript = names.Any(n =>
                        string.Equals(n, "META-INF/com/google/android/updater-script", StringComparison.OrdinalIgnoreCase));
                    facts.HasNestedArchive = names.Any(n =>
                        n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                    var metadata = FindEntry(archive, "META-INF/com/android/metadata");
                    if (metadata != null)
                    {
                        facts.DeviceIdentifiers = ParseMetadataDevices(ReadEntryText(metadata));
                        if (facts.DeviceIdentifiers.Length > 0)
                            facts.DeviceIdentifierSource = "META-INF/com/android/metadata";
                    }

                    if (facts.DeviceIdentifiers.Length == 0)
                    {
                        var androidInfo = FindEntry(archive, "android-info.txt");
                        if (androidInfo != null)
                        {
                            facts.DeviceIdentifiers = ParseAndroidInfoDevices(ReadEntryText(androidInfo));
                            if (facts.DeviceIdentifiers.Length > 0)
                                facts.DeviceIdentifierSource = "android-info.txt";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                facts.Error = ex.Message;
            }

            return facts;
        }

        /// <summary>
        /// .img files in the archive that map to a partition this app can flash,
        /// in the order the UI lists them. Names are separator-normalised first,
        /// because Windows-built archives can store nested paths with backslashes.
        /// </summary>
        private static string[] ReadFlashablePartitions(ZipArchive archive)
        {
            var present = new HashSet<string>(
                archive.Entries
                    .Select(e => (e.FullName ?? string.Empty).Replace('\\', '/'))
                    .Where(n => n.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    .Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return KnownPartitions.Where(present.Contains).ToArray();
        }

        /// <summary>
        /// Decide the package family and the installation route from the archive's
        /// own contents. Attached to the validation result so the UI and the flash
        /// pipeline work from one classification instead of guessing per surface.
        /// </summary>
        private static void ApplyPackagePlan(ValidationResult result, RomFileInfo file, ZipFacts facts)
        {
            var ext = (file.FileExtension ?? string.Empty).ToLowerInvariant();
            var isTarGz = ext == ".tgz" ||
                (ext == ".gz" && file.FileName != null &&
                 file.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));

            if (isTarGz)
            {
                result.Kind = RomPackageKind.FastbootTgz;
                result.InstallMethod = RomInstallMethod.FastbootFlash;
                result.KindDisplay = "Fastboot ROM package (.tar.gz)";
                result.PlanNote = "Writes every image inside the package, including system images — a full firmware install.";
                return;
            }

            if (ext == ".tar")
            {
                result.Kind = RomPackageKind.Unknown;
                result.InstallMethod = RomInstallMethod.Unsupported;
                result.KindDisplay = "Plain .tar archive";
                result.PlanNote = "Only gzipped .tar.gz / .tgz packages can be extracted.";
                return;
            }

            if (facts == null)
            {
                result.Kind = RomPackageKind.Unknown;
                result.InstallMethod = RomInstallMethod.Unsupported;
                result.KindDisplay = "Unreadable package";
                result.PlanNote = "The archive could not be inspected.";
                return;
            }

            // A payload.bin ROM: fastboot can only write its boot-chain images, so
            // the install is two-stage — stage 1 writes the boot chain over
            // fastboot and reboots to recovery; stage 2 sideloads the package so
            // its own installer writes system, vendor and product.
            if (facts.HasPayloadBin)
            {
                result.Kind = RomPackageKind.PayloadOta;
                result.InstallMethod = RomInstallMethod.Sideload;
                result.KindDisplay = "Payload ROM package (2-stage: fastboot boot chain → recovery install)";
                result.PlanPartitions = facts.FlashablePartitions;
                result.PlanNote = "Stage 1 flashes the boot-chain images from the payload over fastboot, then " +
                    "reboots to recovery. Stage 2 sideloads this package so its own installer writes " +
                    "system, vendor and product — fastboot cannot write those.";
                return;
            }

            if (facts.HasAndroidInfo)
            {
                result.Kind = RomPackageKind.FactoryImage;
                result.InstallMethod = RomInstallMethod.Unsupported;
                result.KindDisplay = "Factory image package";
                result.PlanNote = "Factory images install with 'fastboot update', which this app does not run yet.";
                return;
            }

            if (facts.FlashablePartitions.Length > 0)
            {
                result.Kind = RomPackageKind.BootChainZip;
                result.InstallMethod = RomInstallMethod.FastbootFlash;
                result.KindDisplay = "Boot-chain package (loose images)";
                result.PlanPartitions = facts.FlashablePartitions;
                result.PlanNote = $"Writes only the partitions this package ships: {string.Join(", ", facts.FlashablePartitions)}.";
                return;
            }

            if (facts.HasUpdaterScript)
            {
                result.Kind = RomPackageKind.AddOnZip;
                result.InstallMethod = RomInstallMethod.Sideload;
                result.KindDisplay = "Recovery-flashable ZIP (add-on)";
                result.PlanNote = "Recovery-only package: it is applied from recovery, not written with fastboot.";
                return;
            }

            result.Kind = RomPackageKind.Unknown;
            result.InstallMethod = RomInstallMethod.Unsupported;
            result.KindDisplay = facts.HasNestedArchive
                ? "Archive of archives (not a flashable package)"
                : "Unrecognised package";
            result.PlanNote = "Nothing in this archive identifies it as a ROM, boot image or recovery ZIP.";
        }

        /// <summary>
        /// True when one of the package's declared codenames is the connected
        /// device. Device names are compared case-insensitively.
        /// </summary>
        public static bool MatchesDevice(string[] identifiers, string codename)
        {
            if (identifiers == null || identifiers.Length == 0 || string.IsNullOrWhiteSpace(codename))
                return false;

            var device = codename.Trim();
            return identifiers.Any(id => string.Equals(id, device, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Look for a checksum file published next to the ROM
        /// (rom.zip.sha256, rom.sha256, rom.zip.md5, …).
        /// </summary>
        public ExpectedChecksum FindExpectedChecksum(string romFilePath)
        {
            if (string.IsNullOrWhiteSpace(romFilePath) || !File.Exists(romFilePath))
                return null;

            var candidates = new[]
            {
                romFilePath + ".sha256",
                romFilePath + ".sha256sum",
                Path.ChangeExtension(romFilePath, ".sha256"),
                romFilePath + ".md5",
                Path.ChangeExtension(romFilePath, ".md5")
            };

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    // Both a bare digest and "digest  filename" are common, and a
                    // release can publish one list for several files.
                    foreach (var line in File.ReadAllLines(candidate))
                    {
                        var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) continue;

                        var token = parts[0].TrimStart('*');
                        var algorithm = DetectAlgorithm(token);
                        if (algorithm == null) continue;

                        // A line that names a file only counts for that file.
                        if (parts.Length > 1)
                        {
                            var named = Path.GetFileName(parts[1].TrimStart('*').Replace('/', '\\'));
                            if (!string.Equals(named, Path.GetFileName(romFilePath), StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        return new ExpectedChecksum
                        {
                            Algorithm = algorithm,
                            Hash = token.ToLowerInvariant(),
                            SourcePath = candidate
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("Could not read checksum file {Path}: {Message}", candidate, ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Work out the algorithm from a digest (64 hex = SHA-256, 32 hex = MD5).
        /// Returns null for anything else, including pasted junk.
        /// </summary>
        public static string DetectAlgorithm(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return null;

            var value = hash.Trim();
            if (!value.All(Uri.IsHexDigit))
                return null;

            if (value.Length == 64) return "SHA256";
            if (value.Length == 32) return "MD5";
            return null;
        }

        /// <summary>
        /// Calculate SHA256 hash of a ROM file (for integrity verification).
        /// </summary>
        public Task<string> CalculateSha256Async(string filePath, Action<double> progressCallback = null)
        {
            return CalculateHashAsync(filePath, "SHA256", progressCallback);
        }

        /// <summary>
        /// Calculate a file hash with the given algorithm ("SHA256" or "MD5").
        /// </summary>
        public async Task<string> CalculateHashAsync(string filePath, string algorithm, Action<double> progressCallback = null)
        {
            try
            {
                _logger.Information("Calculating {Algorithm} hash for {FilePath}", algorithm, filePath);

                using (var hasher = CreateHasher(algorithm))
                using (var fileStream = File.OpenRead(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    long totalBytes = fileInfo.Length;
                    long processedBytes = 0;

                    var buffer = new byte[4096 * 1024]; // 4MB buffer
                    int bytesRead;

                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.TransformBlock(buffer, 0, bytesRead, null, 0);
                        processedBytes += bytesRead;
                        progressCallback?.Invoke((double)processedBytes / totalBytes);
                    }

                    hasher.TransformFinalBlock(buffer, 0, 0);
                    string hashString = BitConverter.ToString(hasher.Hash).Replace("-", "").ToLowerInvariant();

                    _logger.Information("{Algorithm} calculated: {Hash} for {FilePath}", algorithm, hashString, filePath);
                    return hashString;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to calculate {Algorithm} for {FilePath}: {Message}", algorithm, filePath, ex.Message);
                throw;
            }
        }

        private static HashAlgorithm CreateHasher(string algorithm)
        {
            switch ((algorithm ?? string.Empty).ToUpperInvariant())
            {
                case "MD5":
                    return MD5.Create();
                case "SHA256":
                    return SHA256.Create();
                default:
                    throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm));
            }
        }

        private static ZipArchiveEntry FindEntry(ZipArchive archive, string path)
        {
            // Archives created by Windows tools can store nested names with
            // backslashes, which the ZIP spec does not allow but readers see.
            var wanted = path.Replace('\\', '/');
            return archive.Entries.FirstOrDefault(e =>
                string.Equals((e.FullName ?? string.Empty).Replace('\\', '/'), wanted,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open()))
            {
                return reader.ReadToEnd();
            }
        }

        private static string[] ParseMetadataDevices(string text)
        {
            foreach (var line in (text ?? string.Empty).Split('\n'))
            {
                var trimmed = line.Trim();
                // pre-device is the device installed onto; post-device applies to a
                // different device afterwards and is not what we protect against.
                if (trimmed.StartsWith("pre-device=", StringComparison.OrdinalIgnoreCase))
                    return SplitDevices(trimmed.Substring("pre-device=".Length));
            }

            return new string[0];
        }

        private static string[] ParseAndroidInfoDevices(string text)
        {
            var keys = new[] { "require board=", "board=", "require device=", "device=" };

            foreach (var line in (text ?? string.Empty).Split('\n'))
            {
                var trimmed = line.Trim();
                foreach (var key in keys)
                {
                    if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        var ids = SplitDevices(trimmed.Substring(key.Length));
                        if (ids.Length > 0)
                            return ids;
                    }
                }
            }

            return new string[0];
        }

        private static string[] SplitDevices(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0 && v.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private bool IsValidExtension(string extension)
        {
            return Array.Exists(SupportedExtensions, ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
}
