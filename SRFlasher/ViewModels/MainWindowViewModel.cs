using SRFlasher.Services;
using SRFlasher.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SRFlasher.ViewModels
{
    /// <summary>
    /// ViewModel for MainWindow containing all business logic for ROM flashing operations.
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly LoggingService _logger;
        private readonly ProcessManager _processManager;
        private readonly RomValidator _romValidator;
        private readonly ErrorTranslator _errorTranslator;
        private readonly FlashService _flashService;
        private readonly DeviceDetailsService _deviceDetailsService;
        private readonly DispatcherTimer _deviceMonitorTimer;

        public enum DeviceMode
        {
            Disconnected,
            AdbSystem,
            AdbRecovery,
            Sideload,
            Fastboot,
            FastbootD,
            Unauthorized,
            Edl
        }

        private DeviceMode _currentPhoneMode = DeviceMode.Disconnected;
        private DateTime _lastEdlProbe = DateTime.MinValue;
        private string _currentActiveSlot = "Unknown";
        private string _currentLockStatus = "Unknown";
        private string _selectedFilePath = string.Empty;
        private bool _isFlashing = false;
        private bool _isDetectingMode = false;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _batchCts;

        // Partition selection state bound to the ROM tab checkboxes.
        private bool _chkDtbo;
        private bool _chkInitBoot;
        private bool _chkVendorBoot;
        private bool _chkRecovery;
        private bool _chkBoot;

        // Observable properties for UI binding
        private ObservableCollection<string> _logMessages;
        private List<string> _logBuffer;
        // Cap on the rolling log buffer (and therefore the visible list).
        private const int MaxLogEntries = 1000;
        private double _progressValue;
        private string _progressText;

        public ObservableCollection<string> LogMessages
        {
            get => _logMessages;
            set => SetField(ref _logMessages, value);
        }

        public double ProgressValue
        {
            get => _progressValue;
            set => SetField(ref _progressValue, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => SetField(ref _progressText, value);
        }

        // ==================== Device card (sidebar) ====================
        private string _deviceBadgeLetter = "?";
        private Brush _deviceBadgeBrush;
        private string _deviceName = "No device";
        private string _deviceCodename = "";
        private string _deviceAndroidDisplay = "—";
        private string _deviceSecurityPatch = "";
        private string _deviceBootloaderState = "Unknown";
        private Brush _deviceBootloaderBrush;
        private double _deviceBatteryLevel;
        private Visibility _deviceBatteryBarVisibility = Visibility.Collapsed;
        private string _deviceBatteryDisplay = "—";
        private Brush _deviceBatteryBrush;
        private Brush _deviceBatteryBarBrush;
        private string _deviceSlotDisplay = "—";
        private string _deviceModeDisplay = "Disconnected";
        private string _deviceSerialDisplay = "—";
        private string _deviceLastSeen = "";
        private bool _deviceConnected;
        private ImageSource _deviceLogoImage;
        private Visibility _deviceLogoVisibility = Visibility.Collapsed;
        private Visibility _deviceBadgeVisibility = Visibility.Visible;
        private string _deviceAntiDisplay = "—";
        private Brush _deviceAntiBrush;
        private Visibility _deviceAntiVisibility = Visibility.Collapsed;

        public string DeviceBadgeLetter { get => _deviceBadgeLetter; private set => SetField(ref _deviceBadgeLetter, value); }
        public Brush DeviceBadgeBrush { get => _deviceBadgeBrush; private set => SetField(ref _deviceBadgeBrush, value); }
        public string DeviceName { get => _deviceName; private set => SetField(ref _deviceName, value); }
        public string DeviceCodename
        {
            get => _deviceCodename;
            private set
            {
                if (SetField(ref _deviceCodename, value))
                {
                    RaiseRomGate();
                    RefreshButtonStates();
                }
            }
        }
        public string DeviceAndroidDisplay { get => _deviceAndroidDisplay; private set => SetField(ref _deviceAndroidDisplay, value); }
        public string DeviceSecurityPatch { get => _deviceSecurityPatch; private set => SetField(ref _deviceSecurityPatch, value); }
        public string DeviceBootloaderState { get => _deviceBootloaderState; private set => SetField(ref _deviceBootloaderState, value); }
        public Brush DeviceBootloaderBrush { get => _deviceBootloaderBrush; private set => SetField(ref _deviceBootloaderBrush, value); }
        public double DeviceBatteryLevel { get => _deviceBatteryLevel; private set => SetField(ref _deviceBatteryLevel, value); }
        public Visibility DeviceBatteryBarVisibility { get => _deviceBatteryBarVisibility; private set => SetField(ref _deviceBatteryBarVisibility, value); }
        public string DeviceBatteryDisplay { get => _deviceBatteryDisplay; private set => SetField(ref _deviceBatteryDisplay, value); }
        public Brush DeviceBatteryBrush { get => _deviceBatteryBrush; private set => SetField(ref _deviceBatteryBrush, value); }
        public Brush DeviceBatteryBarBrush { get => _deviceBatteryBarBrush; private set => SetField(ref _deviceBatteryBarBrush, value); }
        public string DeviceSlotDisplay { get => _deviceSlotDisplay; private set => SetField(ref _deviceSlotDisplay, value); }
        public string DeviceModeDisplay
        {
            get => _deviceModeDisplay;
            private set
            {
                if (SetField(ref _deviceModeDisplay, value))
                {
                    OnPropertyChanged(nameof(WindowTitle));
                    OnPropertyChanged(nameof(IdleFooterLeft));
                    OnPropertyChanged(nameof(IdleFooterVisibility));
                }
            }
        }
        public string DeviceSerialDisplay { get => _deviceSerialDisplay; private set => SetField(ref _deviceSerialDisplay, value); }
        public string DeviceLastSeen { get => _deviceLastSeen; private set => SetField(ref _deviceLastSeen, value); }
        public bool DeviceConnected
        {
            get => _deviceConnected;
            private set
            {
                if (SetField(ref _deviceConnected, value))
                {
                    OnPropertyChanged(nameof(IdleFooterVisibility));
                    OnPropertyChanged(nameof(IdleFooterLeft));
                    OnPropertyChanged(nameof(WelcomeVisibility));
                    OnPropertyChanged(nameof(WelcomeNoDeviceVisibility));
                    OnPropertyChanged(nameof(WelcomeDeviceVisibility));
                }
            }
        }
        public ImageSource DeviceLogoImage { get => _deviceLogoImage; private set => SetField(ref _deviceLogoImage, value); }
        public Visibility DeviceLogoVisibility { get => _deviceLogoVisibility; private set => SetField(ref _deviceLogoVisibility, value); }
        public Visibility DeviceBadgeVisibility { get => _deviceBadgeVisibility; private set => SetField(ref _deviceBadgeVisibility, value); }
        public string DeviceAntiDisplay { get => _deviceAntiDisplay; private set => SetField(ref _deviceAntiDisplay, value); }
        public Brush DeviceAntiBrush { get => _deviceAntiBrush; private set => SetField(ref _deviceAntiBrush, value); }
        public Visibility DeviceAntiVisibility { get => _deviceAntiVisibility; private set => SetField(ref _deviceAntiVisibility, value); }

        // Brand accent colors for the letter badge.
        private static readonly Dictionary<string, string> BrandColorMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "google", "#4285F4" }, { "samsung", "#1428A0" }, { "xiaomi", "#FF6900" },
            { "redmi", "#FF6900" }, { "poco", "#FFD600" }, { "oneplus", "#EB0028" },
            { "nothing", "#0944AB" }, { "motorola", "#5C92FA" }, { "oppo", "#046A38" },
            { "vivo", "#415FFF" }, { "realme", "#FFC915" }, { "asus", "#00539B" },
            { "nokia", "#124191" }, { "hmd", "#124191" }, { "sony", "#37474F" },
            { "fairphone", "#3EB3C2" }, { "honor", "#0F4C81" }, { "huawei", "#CF0A2C" },
            { "tecno", "#0B5FA5" }, { "infinix", "#0B5FA5" }, { "lenovo", "#E2231A" }
        };

        private Brush GetBrandBrush(string brand)
        {
            if (!string.IsNullOrWhiteSpace(brand) && BrandColorMap.TryGetValue(brand.Trim(), out var hex))
            {
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    return brush;
                }
                catch { /* fall through to theme color */ }
            }

            return Application.Current?.TryFindResource("ActionBlue") as Brush ?? Brushes.SteelBlue;
        }

        private static Brush FrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Frozen horizontal gradient brush (used for the battery bar and accents).
        /// </summary>
        private static Brush FrozenGradient(string startHex, string endHex)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(startHex), 0.0));
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(endHex), 1.0));
            brush.Freeze();
            return brush;
        }

        private static string ShortModeName(string mode)
        {
            switch (mode)
            {
                case "AdbSystem": return "ADB (System)";
                case "AdbRecovery": return "ADB (Recovery)";
                case "Sideload": return "ADB (Sideload)";
                case "Fastboot": return "Fastboot";
                case "FastbootD": return "FastbootD";
                case "Unauthorized": return "UNAUTHORIZED";
                case "Edl": return "EDL (9008)";
                case "Disconnected": return "Disconnected";
                default: return mode ?? "Unknown";
            }
        }

        /// <summary>
        /// Map a DeviceDetails snapshot onto the sidebar device card bindings.
        /// Keeps the last connected snapshot cached so a mid-flash reboot
        /// (device vanishes from adb/fastboot for a while) can hold the card
        /// instead of demoting it to "Disconnected" while SR Flasher is
        /// driving the phone.
        /// </summary>
        private DeviceDetails _lastConnectedDetails;

        private void UpdateDeviceCard(DeviceDetails d)
        {
            if (!string.IsNullOrEmpty(d.Mode) && d.Mode != "Disconnected")
                _lastConnectedDetails = d;
            var brand = d.Brand ?? string.Empty;
            DeviceBadgeLetter = string.IsNullOrEmpty(brand) ? "?" : char.ToUpperInvariant(brand.Trim()[0]).ToString();
            DeviceBadgeBrush = GetBrandBrush(brand);

            // Optional brand logo override (Logos\<Brand>.png next to the exe)
            DeviceLogoImage = LoadBrandLogo(brand);
            DeviceLogoVisibility = DeviceLogoImage != null ? Visibility.Visible : Visibility.Collapsed;
            DeviceBadgeVisibility = DeviceLogoImage == null ? Visibility.Visible : Visibility.Collapsed;

            DeviceName = d.MarketingName ?? d.Model ?? d.Codename
                ?? (string.IsNullOrWhiteSpace(d.Serial) ? "No device" : d.Serial);
            DeviceCodename = string.IsNullOrWhiteSpace(d.Codename) ? string.Empty : d.Codename;
            DeviceAndroidDisplay = string.IsNullOrWhiteSpace(d.AndroidVersion) ? "—" : $"Android {d.AndroidVersion}";
            DeviceSecurityPatch = string.IsNullOrWhiteSpace(d.SecurityPatch) ? string.Empty : $"Security patch: {d.SecurityPatch}";

            DeviceBootloaderState = string.IsNullOrWhiteSpace(d.BootloaderState) ? "Unknown" : d.BootloaderState;
            DeviceBootloaderBrush =
                DeviceBootloaderState == "Unlocked" ? FrozenBrush("#00B894") :
                DeviceBootloaderState == "Locked" ? FrozenBrush("#E74C3C") :
                FrozenBrush("#8A93A6");

            DeviceBatteryLevel = d.BatteryLevel ?? 0;
            DeviceBatteryBarVisibility = d.BatteryLevel.HasValue ? Visibility.Visible : Visibility.Collapsed;
            DeviceBatteryDisplay = d.BatteryLevel.HasValue
                ? $"{d.BatteryLevel.Value}%" + (d.BatteryCharging == true ? " (charging)" : "")
                : "—";
            bool batteryLow = d.BatteryLevel.HasValue && d.BatteryCharging != true && d.BatteryLevel.Value < _batteryThreshold;
            DeviceBatteryBrush = batteryLow ? FrozenBrush("#E17055") : FrozenBrush("#00B894");
            DeviceBatteryBarBrush = batteryLow ? FrozenGradient("#E17055", "#F09A7E") : FrozenGradient("#00B894", "#55EFC4");

            DeviceConnected = !string.IsNullOrEmpty(d.Mode) && d.Mode != "Disconnected";

            DeviceAntiDisplay = d.AntiVersion.HasValue
                ? $"v{d.AntiVersion.Value}" + (d.AntiVersion.Value > 0 ? " (active)" : "")
                : "—";
            DeviceAntiBrush = d.AntiVersion.HasValue && d.AntiVersion.Value > 0
                ? FrozenBrush("#E17055")
                : FrozenBrush("#8A93A6");
            DeviceAntiVisibility = d.AntiVersion.HasValue ? Visibility.Visible : Visibility.Collapsed;

            DeviceSlotDisplay = string.IsNullOrWhiteSpace(d.ActiveSlot) ? "—" : d.ActiveSlot;
            DeviceModeDisplay = ShortModeName(d.Mode);
            DeviceSerialDisplay = string.IsNullOrWhiteSpace(d.Serial) ? "—" : d.Serial;
            DeviceLastSeen = d.Mode == "Disconnected" && d.LastSeen != default
                ? $"Last seen {d.LastSeen:HH:mm}"
                : string.Empty;
        }

        // Guided step banners shown on the ROM and Additional Files tabs.
        private string _romStepText = "STEP 1: Drag a ROM package onto the drop zone, or click Browse to pick one.";
        private string _additionalStepText = "";

        public string RomStepText
        {
            get => _romStepText;
            set
            {
                if (SetField(ref _romStepText, value))
                {
                    // The instruction banner is state-colored: red while something
                    // needs attention or went wrong, green on completion, neutral
                    // otherwise. All ~30 step messages route through this setter.
                    var t = (value ?? string.Empty).ToLowerInvariant();
                    bool bad = t.Contains("failed") || t.Contains("error") || t.Contains("mismatch")
                               || t.Contains("cancelled") || t.Contains("not detected") || t.Contains("refus");
                    bool good = t.Contains("complete") || t.Contains("✓") || t.Contains("finished");
                    RomStepBorderBrush = bad ? System.Windows.Application.Current.Resources["AccentRed"] as System.Windows.Media.Brush
                                        : good ? System.Windows.Application.Current.Resources["SuccessTintBorder"] as System.Windows.Media.Brush
                                        : System.Windows.Application.Current.Resources["BorderMedium"] as System.Windows.Media.Brush;
                    RomStepBg = bad ? System.Windows.Application.Current.Resources["DangerTintBg"] as System.Windows.Media.Brush
                               : good ? System.Windows.Application.Current.Resources["SuccessTintBg"] as System.Windows.Media.Brush
                               : System.Windows.Application.Current.Resources["InstructionBoxBg"] as System.Windows.Media.Brush;
                }
            }
        }

        private System.Windows.Media.Brush _romStepBorderBrush;
        /// <summary>Instruction banner border, state-colored from the step text.</summary>
        public System.Windows.Media.Brush RomStepBorderBrush { get => _romStepBorderBrush; private set => SetField(ref _romStepBorderBrush, value); }

        private System.Windows.Media.Brush _romStepBg;
        /// <summary>Instruction banner background, state-colored from the step text.</summary>
        public System.Windows.Media.Brush RomStepBg { get => _romStepBg; private set => SetField(ref _romStepBg, value); }

        public string AdditionalStepText
        {
            get => _additionalStepText;
            set => SetField(ref _additionalStepText, value);
        }

        /// <summary>
        /// True when the currently detected device is in a fastboot mode.
        /// </summary>
        private bool IsFastbootMode =>
            _currentPhoneMode == DeviceMode.Fastboot || _currentPhoneMode == DeviceMode.FastbootD;

        /// <summary>
        /// True when the currently detected device is reachable over ADB
        /// (system, recovery, or sideload).
        /// </summary>
        private bool IsModeAdb =>
            _currentPhoneMode == DeviceMode.AdbSystem ||
            _currentPhoneMode == DeviceMode.AdbRecovery ||
            _currentPhoneMode == DeviceMode.Sideload;

        // ==================== Button state machine ====================
        // One source of truth for every button's IsEnabled + reason tooltip.

        private string BusyReason() => IsFlashing ? "An operation is in progress" : null;

        public bool RebootBootloaderEnabled => !IsFlashing && IsModeAdb;
        public string RebootBootloaderReason =>
            BusyReason() ?? (IsModeAdb
                ? "Reboot the connected device to the bootloader"
                : "Requires a device connected in ADB mode");

        // One-click install: active as soon as a ROM is loaded, verified for
        // the connected device and checksum-clean — in ANY connected mode.
        // The flow reboots the device to the bootloader itself; the lock state
        // is verified at flash time (fastboot pre-flight), so an adb-connected
        // device with an unknown lock state does not block the button.
        public bool FlashEnabled => !IsFlashing && (IsFastbootMode || IsModeAdb) &&
            !string.IsNullOrWhiteSpace(SelectedFilePath) &&
            (_isExpertMode || !RomFastbootNotApplicable) &&
            (GetFlashPartitions().Count > 0 || RomFlashesWholePackage) &&
            !RomDeviceMismatch &&
            _romChecksumState != ChecksumState.Mismatch && _romChecksumState != ChecksumState.Verifying &&
            (IsFastbootMode ? _currentLockStatus == "Unlocked" : true);
        public string FlashReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!IsFastbootMode && !IsModeAdb)
                    return "Connect the device (USB debugging or fastboot) — the install reboots it to the bootloader itself";
                if (IsFastbootMode && _currentLockStatus == "Locked")
                    return "Bootloader is locked — unlocking is required before flashing";
                if (IsFastbootMode && _currentLockStatus != "Unlocked")
                    return "Bootloader lock state unknown — cannot verify the unlock";
                if (string.IsNullOrWhiteSpace(SelectedFilePath)) return "Select a ROM file first";
                if (RomDeviceMismatch) return $"Package is built for {RomDeviceTargets}, but this device is {DeviceCodename}";
                if (_romChecksumState == ChecksumState.Mismatch) return "ROM does not match its published checksum — re-download it";
                if (_romChecksumState == ChecksumState.Verifying) return "Verifying the ROM checksum…";
                if (!_isExpertMode && RomFastbootNotApplicable)
                    return RomNeedsSideload
                        ? "This package installs from recovery — use INSTALL FROM RECOVERY (SIDELOAD)"
                        : "This app cannot flash this package — see the notice above";
                if (RomIsPayloadOta && !_isExpertMode)
                    return "Two-stage install: flash the boot chain, then recovery applies the package";
                if (GetFlashPartitions().Count == 0 && !RomFlashesWholePackage)
                    return _isExpertMode
                        ? "Select at least one partition"
                        : "The package does not name a partition this app can write";
                return "Flash the selected partitions to the device";
            }
        }

        // ==================== ROM package checks ====================
        // Two gates sit between "a file is selected" and "flashing is allowed":
        // the package must be built for the connected device, and its hash must
        // not contradict a published checksum. A package that names no device
        // cannot be compared, so it is allowed through with a warning instead.

        public enum ChecksumState { Unknown, Verifying, Verified, Mismatch }

        private string[] _romDeviceIdentifiers = new string[0];
        private string[] _romPlanPartitions = new string[0];
        private RomPackageKind _romKind = RomPackageKind.Unknown;
        private RomInstallMethod _romInstallMethod = RomInstallMethod.None;
        private string _romKindDisplay = string.Empty;
        private string _romPlanNote = string.Empty;
        private ChecksumState _romChecksumState = ChecksumState.Unknown;
        private string _romIntegrityText = string.Empty;
        private Brush _romIntegrityBrush;

        // Shared state colours, so reading a bound property never allocates.
        private static readonly Brush GateOk = FrozenBrush("#00B894");
        private static readonly Brush GateWarn = FrozenBrush("#E17055");
        private static readonly Brush GateBad = FrozenBrush("#E74C3C");

        /// <summary>Device codename(s) the selected package declares, if any.</summary>
        public string RomDeviceTargets => _romDeviceIdentifiers == null || _romDeviceIdentifiers.Length == 0
            ? string.Empty
            : string.Join(", ", _romDeviceIdentifiers);

        /// <summary>
        /// True only when the package names a device and that device is not the
        /// connected one. A package that names nothing is not a mismatch.
        /// </summary>
        public bool RomDeviceMismatch =>
            _romDeviceIdentifiers != null && _romDeviceIdentifiers.Length > 0 &&
            !string.IsNullOrWhiteSpace(DeviceCodename) &&
            !RomValidator.MatchesDevice(_romDeviceIdentifiers, DeviceCodename);

        public string RomCompatibilityText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SelectedFilePath)) return string.Empty;
                if (_romDeviceIdentifiers == null || _romDeviceIdentifiers.Length == 0)
                    return "Package does not name a device — check it is built for this phone";
                if (string.IsNullOrWhiteSpace(DeviceCodename))
                    return $"• Package is built for {RomDeviceTargets}";

                return RomDeviceMismatch
                    ? $"✗ Built for {RomDeviceTargets} — this device is {DeviceCodename}"
                    : $"✓ Built for {DeviceCodename}";
            }
        }

        public Brush RomCompatibilityBrush
        {
            get
            {
                if (_romDeviceIdentifiers == null || _romDeviceIdentifiers.Length == 0)
                    return GateWarn;
                return RomDeviceMismatch ? GateBad : GateOk;
            }
        }

        public Visibility RomCompatibilityVisibility =>
            string.IsNullOrWhiteSpace(SelectedFilePath) ? Visibility.Collapsed : Visibility.Visible;

        public string RomIntegrityText
        {
            get => _romIntegrityText;
            private set => SetField(ref _romIntegrityText, value);
        }

        public Brush RomIntegrityBrush
        {
            get => _romIntegrityBrush;
            private set => SetField(ref _romIntegrityBrush, value);
        }

        public Visibility RomIntegrityVisibility =>
            string.IsNullOrWhiteSpace(_romIntegrityText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>What the selected package is — payload ROM, boot chain, tgz…</summary>
        public string RomKindDisplay => _romKindDisplay;

        /// <summary>How this package has to be installed.</summary>
        public string RomPlanNote => _romPlanNote;

        public Visibility RomKindVisibility =>
            string.IsNullOrWhiteSpace(SelectedFilePath) ? Visibility.Collapsed : Visibility.Visible;

        public Brush RomKindBrush => _romInstallMethod == RomInstallMethod.FastbootFlash ? GateOk
            : _romInstallMethod == RomInstallMethod.Sideload ? GateWarn
            : GateBad;

        /// <summary>True when the package installs itself from recovery, not over fastboot.</summary>
        public bool RomNeedsSideload => _romInstallMethod == RomInstallMethod.Sideload;

        /// <summary>True when the package installs every image it carries (MIUI/HyperOS .tgz).</summary>
        public bool RomFlashesWholePackage => _romKind == RomPackageKind.FastbootTgz;

        /// <summary>
        /// True when the fastboot path would not install this package. A payload
        /// ROM is EXCLUDED: its two-stage install starts in fastboot (boot chain,
        /// then a reboot to recovery). Expert mode may also write boot chains
        /// deliberately; unknown packages stay blocked.
        /// </summary>
        public bool RomFastbootNotApplicable =>
            (_romInstallMethod == RomInstallMethod.Sideload && _romKind != RomPackageKind.PayloadOta) ||
            _romInstallMethod == RomInstallMethod.Unsupported;

        /// <summary>True when the package is a payload ROM handled as a two-stage install.</summary>
        public bool RomIsPayloadOta => _romKind == RomPackageKind.PayloadOta;

        /// <summary>
        /// Boot-chain partitions asked from a payload. payload-dumper-go extracts
        /// whichever of these the payload actually carries; the rest are logged
        /// as absent, so nothing is invented.
        /// </summary>
        public static readonly string[] PayloadBootChain =
            { "boot", "init_boot", "vendor_boot", "dtbo", "recovery", "vbmeta", "vbmeta_system" };

        /// <summary>Banner that explains the route when fastboot alone cannot install the package.</summary>
        public Visibility RomRouteVisibility =>
            !string.IsNullOrWhiteSpace(SelectedFilePath) && RomFastbootNotApplicable
                ? Visibility.Visible : Visibility.Collapsed;

        public string RomRouteHeadline => RomIsPayloadOta
            ? "TWO-STAGE INSTALL — FASTBOOT THEN RECOVERY"
            : RomNeedsSideload
                ? "THIS PACKAGE INSTALLS FROM RECOVERY"
                : "THIS PACKAGE CANNOT BE FLASHED BY THIS APP";

        public string RomRouteText => _romPlanNote;

        /// <summary>Show the recovery-install button only for packages that cannot use the fastboot path.</summary>
        public Visibility RomSideloadVisibility =>
            RomNeedsSideload && !RomIsPayloadOta && !string.IsNullOrWhiteSpace(SelectedFilePath)
                ? Visibility.Visible : Visibility.Collapsed;

        public bool RomSideloadEnabled => !IsFlashing && !string.IsNullOrWhiteSpace(SelectedFilePath) &&
            !string.IsNullOrWhiteSpace(CurrentDeviceSerial);

        public string RomSideloadReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(SelectedFilePath)) return "Select a ROM package first";
                if (string.IsNullOrWhiteSpace(CurrentDeviceSerial))
                    return "Connect the device (ADB, recovery or fastboot) to install from recovery";
                return "Reboot through recovery into sideload and apply this package";
            }
        }

        /// <summary>Label for the primary action, so it names what it really does.</summary>
        public string FlashActionLabel
        {
            get
            {
                if (_isExpertMode) return "INSTALL ROM";
                switch (_romKind)
                {
                    case RomPackageKind.BootChainZip: return "FLASH BOOT CHAIN";
                    case RomPackageKind.FastbootTgz: return "INSTALL FIRMWARE PACKAGE";
                    case RomPackageKind.PayloadOta: return "INSTALL ROM";
                    case RomPackageKind.AddOnZip: return "INSTALL FROM RECOVERY";
                    default: return "INSTALL ROM";
                }
            }
        }

        /// <summary>Warning line for the confirm dialog (empty when unneeded).</summary>
        public string RomPackageNote => _romDeviceIdentifiers == null || _romDeviceIdentifiers.Length == 0
            ? "This package does not declare a target device, so it could not be checked against the connected phone."
            : string.Empty;

        // ==================== Simple / Expert mode ====================
        // Simple shows one action plus a one-line plan; Expert reveals the
        // partition, slot and vbmeta controls the plan is built from.

        private bool _isExpertMode;

        public bool IsExpertMode
        {
            get => _isExpertMode;
            set
            {
                if (!SetField(ref _isExpertMode, value)) return;

                try
                {
                    Properties.Settings.Default.ExpertMode = value;
                    Properties.Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to save expert mode: {Message}", ex.Message);
                }

                OnPropertyChanged(nameof(IsExpertMode));
                OnPropertyChanged(nameof(IsSimpleMode));
                OnPropertyChanged(nameof(RomRouteBannerVisibility));
                OnPropertyChanged(nameof(AdvancedVisibility));
                OnPropertyChanged(nameof(SimpleWipeVisibility));
                OnPropertyChanged(nameof(SimplePlanVisibility));
                OnPropertyChanged(nameof(FlashActionLabel));
                RaiseRomPlan();
                RefreshButtonStates();
            }
        }

        public Visibility AdvancedVisibility => _isExpertMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SimplePlanVisibility => _isExpertMode ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Bindable inverse of IsExpertMode for the segmented mode switch.</summary>
        public bool IsSimpleMode
        {
            get => !_isExpertMode;
            set { if (value && _isExpertMode) IsExpertMode = false; }
        }

        // ==================== Plan chips (THE PLAN card, Simple mode) ====================
        private string _romPlanPartitionsChip = string.Empty;
        public string RomPlanPartitionsChip { get => _romPlanPartitionsChip; private set { if (SetField(ref _romPlanPartitionsChip, value)) OnPropertyChanged(nameof(RomPlanPartitionsVisibility)); } }
        public Visibility RomPlanPartitionsVisibility => string.IsNullOrEmpty(_romPlanPartitionsChip) ? Visibility.Collapsed : Visibility.Visible;

        private string _romPlanWipeChip = string.Empty;
        public string RomPlanWipeChip { get => _romPlanWipeChip; private set => SetField(ref _romPlanWipeChip, value); }

        private string _romPlanAfterChip = string.Empty;
        public string RomPlanAfterChip { get => _romPlanAfterChip; private set => SetField(ref _romPlanAfterChip, value); }

        /// <summary>Refresh the plan sentence and its fact chips together.</summary>
        private void RaiseRomPlan()
        {
            OnPropertyChanged(nameof(RomPlanSummary));

            if (RomIsPayloadOta)
                RomPlanPartitionsChip = "STAGE 1 WRITES: " + string.Join(" · ", PayloadBootChain);
            else if (_romInstallMethod == RomInstallMethod.FastbootFlash && _romPlanPartitions != null && _romPlanPartitions.Length > 0)
                RomPlanPartitionsChip = "WRITES: " + string.Join(" · ", _romPlanPartitions);
            else
                RomPlanPartitionsChip = string.Empty;

            RomPlanWipeChip = ChkWipeDataBeforeFlash ? "CLEAN INSTALL — DATA ERASED" : "KEEPS YOUR DATA";

            RomPlanAfterChip = RomIsPayloadOta
                ? "AFTER ROM: REBOOTS ITSELF"
                : "AFTER: STAYS IN FASTBOOT";
        }

        /// <summary>What the ROM tab's primary action is going to do.</summary>
        public string RomPlanSummary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SelectedFilePath))
                    return "Choose a package and the app will state exactly what it writes.";

                // A package that has to install itself from recovery is described by
                // its route, not by a partition list it will never honour.
                if (RomIsPayloadOta)
                    return "Two-stage install: the boot-chain images are flashed over fastboot, the device " +
                           "reboots to recovery, and the package is sideloaded so its installer writes " +
                           "system, vendor and product.";
                if (_romInstallMethod == RomInstallMethod.Sideload)
                    return "This package is applied from recovery. Use INSTALL FROM RECOVERY below.";

                if (_romInstallMethod == RomInstallMethod.Unsupported)
                    return _romPlanNote;

                var data = ChkWipeDataBeforeFlash ? "erases all data" : "keeps your data";
                var after = "stays in fastboot";

                if (RomFlashesWholePackage)
                    return $"This writes every image in the package — a full firmware install — {data}, then {after}.";

                var parts = GetFlashPartitions();
                if (parts.Count == 0) return _romPlanNote;

                var slot = SlotChoice == "Auto" ? "active slot"
                    : SlotChoice == "Both" ? "both slots"
                    : $"slot {SlotChoice}";

                return $"This writes {string.Join(", ", parts)} only to the {slot}, {data}, then {after}.";
            }
        }

        /// <summary>
        /// The single source of truth for what a flash writes: what the package
        /// itself declares, or — in Expert mode — the partition check boxes.
        /// Nothing is ever assumed from a saved selection the package contradicts.
        /// A payload ROM asks for its whole boot chain; payload-dumper extracts
        /// whichever of those the payload actually carries.
        /// </summary>
        public List<string> GetFlashPartitions()
        {
            if (_isExpertMode) return GetSelectedPartitions();
            if (RomIsPayloadOta) return PayloadBootChain.ToList();
            return _romPlanPartitions.ToList();
        }

        private void ResetRomChecks()
        {
            _romDeviceIdentifiers = new string[0];
            _romPlanPartitions = new string[0];
            _romKind = RomPackageKind.Unknown;
            _romInstallMethod = RomInstallMethod.None;
            _romKindDisplay = string.Empty;
            _romPlanNote = string.Empty;
            _romChecksumState = ChecksumState.Unknown;
            RomIntegrityText = string.Empty;
            RomIntegrityBrush = null;
            RaiseRomGate();
        }

        private void RaiseRomGate()
        {
            OnPropertyChanged(nameof(RomRouteBannerVisibility));
            OnPropertyChanged(nameof(RomDeviceTargets));
            OnPropertyChanged(nameof(RomDeviceMismatch));
            OnPropertyChanged(nameof(RomCompatibilityText));
            OnPropertyChanged(nameof(RomCompatibilityBrush));
            OnPropertyChanged(nameof(RomCompatibilityVisibility));
            OnPropertyChanged(nameof(RomIntegrityVisibility));
            OnPropertyChanged(nameof(RomPackageNote));
            OnPropertyChanged(nameof(RomKindDisplay));
            OnPropertyChanged(nameof(RomPlanNote));
            OnPropertyChanged(nameof(RomKindVisibility));
            OnPropertyChanged(nameof(RomKindBrush));
            OnPropertyChanged(nameof(RomNeedsSideload));
            OnPropertyChanged(nameof(RomFlashesWholePackage));
            OnPropertyChanged(nameof(RomIsPayloadOta));
            OnPropertyChanged(nameof(RomFastbootNotApplicable));
            OnPropertyChanged(nameof(RomRouteVisibility));
            OnPropertyChanged(nameof(RomRouteHeadline));
            OnPropertyChanged(nameof(RomRouteText));
            OnPropertyChanged(nameof(RomSideloadVisibility));
            RaiseRomPlan();
            OnPropertyChanged(nameof(FlashActionLabel));
            RefreshButtonStates();
        }

        private void SetRomIntegrity(ChecksumState state, string text)
        {
            _romChecksumState = state;
            RomIntegrityText = text;
            RomIntegrityBrush = state == ChecksumState.Verified ? GateOk
                : state == ChecksumState.Mismatch ? GateBad
                : GateWarn;
            OnPropertyChanged(nameof(RomIntegrityVisibility));
            RefreshButtonStates();
        }

        private async Task RunChecksumVerificationAsync(string filePath, string algorithm, string expected, string origin)
        {
            SetRomIntegrity(ChecksumState.Verifying, $"Verifying {algorithm} from {origin}…");
            Log($"[Checksum] Calculating {algorithm} for {Path.GetFileName(filePath)} ({origin})…");

            try
            {
                var actual = await _romValidator.CalculateHashAsync(filePath, algorithm);

                // The user may have picked another file while the hash was running.
                if (!string.Equals(filePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    SetRomIntegrity(ChecksumState.Verified, $"✓ {algorithm} verified ({origin})");
                    ShowToast("Success", "Checksum verified", $"{algorithm} check passed ({origin}).");
                    Log($"[✓] Checksum verified ({algorithm}): {actual}");
                }
                else
                {
                    SetRomIntegrity(ChecksumState.Mismatch, $"✗ {algorithm} mismatch — this is not the published file");
                    ShowToast("Error", "Checksum mismatch", "The ROM does not match its published checksum — re-download it.");
                    Log($"[ERROR] Checksum mismatch: expected {expected}, file is {actual}. Re-download the ROM before flashing.");
                }
            }
            catch (Exception ex)
            {
                SetRomIntegrity(ChecksumState.Unknown, "Checksum check failed — see LOGS");
                Log($"[ERROR] Checksum verification failed: {ex.Message}");
            }
        }

        public bool AdditionalFlashEnabled => !IsFlashing && IsFastbootMode;
        public string AdditionalFlashReason =>
            BusyReason() ?? (IsFastbootMode
                ? "Flash extra image files (name each file after its partition)"
                : "Requires the device in fastboot/bootloader mode");

        public bool RebootRecoveryEnabled => !IsFlashing && (IsFastbootMode || _currentPhoneMode == DeviceMode.AdbSystem);
        public string RebootRecoveryReason =>
            BusyReason() ?? ((IsFastbootMode || _currentPhoneMode == DeviceMode.AdbSystem)
                ? "Reboot the device to recovery"
                : "Requires a connected device in fastboot or ADB mode");

        public bool WipeCacheEnabled => !IsFlashing &&
            (IsFastbootMode || RecoveryInRecoveryMode ||
             _currentPhoneMode == DeviceMode.AdbSystem || _currentPhoneMode == DeviceMode.AdbRecovery);
        public string WipeCacheReason =>
            BusyReason() ?? (RecoveryInRecoveryMode
                ? "Wipe cache + dalvik via the twrp CLI"
                : IsFastbootMode
                    ? "Wipe the cache partition via fastboot"
                    : "Reboots to the bootloader first, then wipes cache via fastboot");

        public bool FinishRebootEnabled => RebootRecoveryEnabled || RecoveryInRecoveryMode;
        public string FinishRebootReason =>
            RecoveryInRecoveryMode
                ? "Reboots the system via the twrp CLI (TWRP does not reboot on its own)"
                : RebootRecoveryReason;

        public bool SideloadEnabled => !IsFlashing &&
            (_currentPhoneMode == DeviceMode.Sideload || _currentPhoneMode == DeviceMode.AdbRecovery ||
             _currentPhoneMode == DeviceMode.AdbSystem || IsFastbootMode);
        public string SideloadReason =>
            BusyReason() ?? (_currentPhoneMode == DeviceMode.Sideload
                ? "Apply a ZIP package via ADB sideload"
                : _currentPhoneMode == DeviceMode.AdbRecovery || _currentPhoneMode == DeviceMode.AdbSystem || IsFastbootMode
                    ? "Select a ZIP — the app will reboot through recovery into sideload automatically"
                    : "Requires a connected device (system, recovery, sideload, or fastboot)");

        public bool CanClearCompletedJobs => !IsFlashing &&
            BatchOperationManager.Instance.JobQueue.Any(j =>
                j.Status == RomFlashJob.JobStatus.Completed ||
                j.Status == RomFlashJob.JobStatus.Failed ||
                j.Status == RomFlashJob.JobStatus.Cancelled);

        public bool ProcessBatchEnabled => !IsFlashing &&
            BatchOperationManager.Instance.GetQueuedJobCount() > 0 && (IsFastbootMode || IsModeAdb);
        public string ProcessBatchReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (BatchOperationManager.Instance.GetQueuedJobCount() == 0) return "The batch queue is empty";
                if (!IsFastbootMode && !IsModeAdb) return "Requires a connected device";
                return "Process all queued jobs sequentially";
            }
        }

        /// <summary>
        /// Re-raise every button-state property (called whenever any input to
        /// the state machine changes: mode, flashing, lock state, selection).
        /// </summary>
        // ==================== Easy Flow · step trail ====================
        // Five stages the session walks through: CONNECT · MODE · FILE ·
        // FLASH · FINISH. States recompute on every RefreshButtonStates call;
        // done steps turn green, the current one carries the red accent.
        public class StepTrailItem : ViewModelBase
        {
            public string Label { get; set; }
            private string _state = "Pending";
            public string State { get => _state; set => SetField(ref _state, value); }   // Done / Current / Pending
            public bool IsFirst { get; set; }
        }

        public ObservableCollection<StepTrailItem> StepTrailSteps { get; private set; }

        private string _stepTrailHint = "";
        public string StepTrailHint { get => _stepTrailHint; private set => SetField(ref _stepTrailHint, value); }

        private void InitStepTrail()
        {
            StepTrailSteps = new ObservableCollection<StepTrailItem>
            {
                new StepTrailItem { Label = "CONNECT", IsFirst = true },
                new StepTrailItem { Label = "MODE" },
                new StepTrailItem { Label = "FILE" },
                new StepTrailItem { Label = "FLASH" },
                new StepTrailItem { Label = "EXTRAS" },
                new StepTrailItem { Label = "FINISH" },
            };
            UpdateStepTrail();
        }

        private void UpdateStepTrail()
        {
            if (StepTrailSteps == null) return;

            bool connected = !string.IsNullOrWhiteSpace(CurrentDeviceSerial) &&
                _currentPhoneMode != DeviceMode.Disconnected;
            bool usableMode = connected && _currentPhoneMode != DeviceMode.Unauthorized && _currentPhoneMode != DeviceMode.Edl;
            bool fileStaged = !string.IsNullOrWhiteSpace(_recoveryFilePath) || !string.IsNullOrWhiteSpace(SelectedFilePath);
            bool flashDone = _payloadStage >= 2 || _lastFlashSummary?.Contains("successfully") == true;
            bool sessionDone = !_payloadSessionActive && !_romInstallWaitingRecovery && flashDone &&
                _currentPhoneMode == DeviceMode.AdbSystem;

            var state = new[] { "Pending", "Pending", "Pending", "Pending", "Pending", "Pending" };

            if (connected) state[0] = "Done"; else state[0] = "Current";

            if (!connected)
            {
                /* CONNECT stays current; everything after stays pending */
            }
            else if (!usableMode)
            {
                state[1] = "Current";
            }
            else
            {
                state[1] = "Done";
                if (!fileStaged) state[2] = "Current";
                else
                {
                    state[2] = "Done";
                    if (IsFlashing) state[3] = "Current";
                    else if (flashDone) state[3] = "Done";
                    else state[3] = "Current";

                    if (flashDone)
                    {
                        // EXTRAS: the add-on decision. Current while the session
                        // is still driving the phone (ROM applying, waiting for
                        // recovery, or the user flashing/skipping extras); done
                        // once the extras decision is made (session over — the
                        // user either applied extras or skipped to system).
                        bool sessionActive = _payloadSessionActive || _romInstallWaitingRecovery;
                        state[4] = sessionActive ? "Current" : "Done";

                        // FINISH: current while the phone boots into the new
                        // system after the session ended; done when it's up.
                        if (sessionActive) state[5] = "Pending";
                        else state[5] = sessionDone ? "Done" : "Current";
                    }
                }
            }

            for (int i = 0; i < StepTrailSteps.Count; i++)
                StepTrailSteps[i].State = state[i];

            // One hint at a time: the most useful next physical action.
            string hint;
            if (_currentPhoneMode == DeviceMode.Edl)
                hint = "The device is in EMERGENCY DOWNLOAD (9008) — see the LOGS tab.";
            else if (_currentPhoneMode == DeviceMode.Unauthorized)
                hint = "Accept the USB debugging prompt on the phone screen.";
            else if (!connected)
                hint = "Connect the device with USB debugging enabled.";
            else if (IsFlashing)
                hint = "Keep the cable connected — do not unplug during the operation.";
            else if (_payloadSessionActive || _romInstallWaitingRecovery)
                hint = _currentPhoneMode == DeviceMode.Sideload
                    ? "The package is installing — follow the phone's progress."
                    : "Waiting for the device to reach recovery — keep the cable connected.";
            else if (_currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode && flashDone)
                hint = "Extras step: flash GAPPS, Magisk or other zips on ADDITIONAL FILES — or FINISH & REBOOT to skip.";
            else if (_currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode)
                hint = "Recovery session active — sideload or flash from the ADDITIONAL FILES tab.";
            else if (!fileStaged && usableMode)
                hint = "Add a ROM or file on the FLASH ROM / ADDITIONAL FILES tabs.";
            else if (usableMode)
                hint = "Ready — the app chains reboots between modes automatically.";
            else
                hint = "";
            StepTrailHint = hint;
        }

        private void RefreshButtonStates()
        {
            UpdateStepTrail();
            RaiseRomPlan();
            OnPropertyChanged(nameof(RebootBootloaderEnabled));
            OnPropertyChanged(nameof(RebootBootloaderReason));
            OnPropertyChanged(nameof(FlashEnabled));
            OnPropertyChanged(nameof(FlashReason));
            OnPropertyChanged(nameof(AdditionalFlashEnabled));
            OnPropertyChanged(nameof(AdditionalFlashReason));
            OnPropertyChanged(nameof(RebootRecoveryEnabled));
            OnPropertyChanged(nameof(RebootRecoveryReason));
            OnPropertyChanged(nameof(WipeCacheEnabled));
            OnPropertyChanged(nameof(WipeCacheReason));
            OnPropertyChanged(nameof(FinishRebootEnabled));
            OnPropertyChanged(nameof(FinishRebootReason));
            OnPropertyChanged(nameof(SideloadEnabled));
            OnPropertyChanged(nameof(SideloadReason));
            OnPropertyChanged(nameof(RecoveryZipSideloadEnabled));
            OnPropertyChanged(nameof(RecoveryZipSideloadReason));
            OnPropertyChanged(nameof(ProcessBatchEnabled));
            OnPropertyChanged(nameof(ProcessBatchReason));
            OnPropertyChanged(nameof(CanClearCompletedJobs));
            OnPropertyChanged(nameof(EnterSideloadEnabled));
            OnPropertyChanged(nameof(EnterSideloadReason));
            OnPropertyChanged(nameof(AdditionalZipActionEnabled));
            OnPropertyChanged(nameof(AdditionalZipActionReason));
            OnPropertyChanged(nameof(AdditionalImageActionEnabled));
            OnPropertyChanged(nameof(AdditionalImageActionReason));
            OnPropertyChanged(nameof(AdditionalImagePartition));
            OnPropertyChanged(nameof(AdditionalImageIsRecovery));
            OnPropertyChanged(nameof(AdditionalRecoveryVisibility));
            OnPropertyChanged(nameof(AdditionalPlainImageVisibility));
            OnPropertyChanged(nameof(BootRecoveryImageEnabled));
            OnPropertyChanged(nameof(BootRecoveryImageReason));
            OnPropertyChanged(nameof(FlashRecoveryImageEnabled));
            OnPropertyChanged(nameof(FlashRecoveryImageReason));
            OnPropertyChanged(nameof(RecoveryZipCliVisibility));
            OnPropertyChanged(nameof(RecoveryImageCliVisibility));
            OnPropertyChanged(nameof(RecoveryCliInstallEnabled));
            OnPropertyChanged(nameof(RecoveryCliInstallReason));
            OnPropertyChanged(nameof(RecoveryCliFlashImageEnabled));
            OnPropertyChanged(nameof(RecoveryCliFlashImageReason));
            OnPropertyChanged(nameof(RecoveryStockRestoreEnabled));
            OnPropertyChanged(nameof(RecoveryBothSlotsVisibility));
            OnPropertyChanged(nameof(RecoveryBothSlotsEnabled));
            OnPropertyChanged(nameof(RecoveryBothSlotsReason));
            OnPropertyChanged(nameof(RecoveryStockRestoreReason));
            OnPropertyChanged(nameof(RecoveryBackupText));
            OnPropertyChanged(nameof(RecoveryBackupVisibility));
            ScanRecoveryBackup();
            OnPropertyChanged(nameof(EraseFrpEnabled));
            OnPropertyChanged(nameof(EraseFrpReason));
            OnPropertyChanged(nameof(EraseMetadataEnabled));
            OnPropertyChanged(nameof(EraseMetadataReason));
            OnPropertyChanged(nameof(EraseMiscEnabled));
            OnPropertyChanged(nameof(EraseMiscReason));
            OnPropertyChanged(nameof(SwitchSlotEnabled));
            OnPropertyChanged(nameof(SwitchSlotReason));
            OnPropertyChanged(nameof(BootFastbootdEnabled));
            OnPropertyChanged(nameof(BootFastbootdReason));
            OnPropertyChanged(nameof(RecoveryTwrpBackupVisibility));
            OnPropertyChanged(nameof(RecoveryTwrpBackupEnabled));
            OnPropertyChanged(nameof(RecoveryTwrpBackupReason));
            OnPropertyChanged(nameof(RecoveryTwrpRestoreEnabled));
            OnPropertyChanged(nameof(RecoveryTwrpRestoreReason));
            OnPropertyChanged(nameof(RebootSystemEnabled));
            OnPropertyChanged(nameof(RebootSystemReason));
            OnPropertyChanged(nameof(FactoryWipeEnabled));
            OnPropertyChanged(nameof(FactoryWipeReason));
            OnPropertyChanged(nameof(RomSideloadEnabled));
            OnPropertyChanged(nameof(RomSideloadReason));
        }

        public string SelectedFilePath
        {
            get => _selectedFilePath;
            set
            {
                if (SetField(ref _selectedFilePath, value))
                {
                    // A new file has not been checked yet; ValidateAndSelectRomAsync
                    // fills these in straight afterwards.
                    RomFileSizeDisplay = string.Empty;
                    ResetRomChecks();
                    OnPropertyChanged(nameof(RomFileName));
                    OnPropertyChanged(nameof(RomIsFileLoaded));
                    OnPropertyChanged(nameof(RomFileLoadedVisibility));
                    OnPropertyChanged(nameof(RomFileEmptyVisibility));
                }
            }
        }

        // ==================== File card (drop zone, loaded state) ====================
        public bool RomIsFileLoaded => !string.IsNullOrWhiteSpace(SelectedFilePath);
        public Visibility RomFileLoadedVisibility => RomIsFileLoaded ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RomFileEmptyVisibility => RomIsFileLoaded ? Visibility.Collapsed : Visibility.Visible;
        public string RomFileName => RomIsFileLoaded ? Path.GetFileName(SelectedFilePath) : "";

        private string _romFileSizeDisplay = string.Empty;
        public string RomFileSizeDisplay
        {
            get => _romFileSizeDisplay;
            private set
            {
                if (SetField(ref _romFileSizeDisplay, value))
                    OnPropertyChanged(nameof(RomFileSizeVisibility));
            }
        }
        public Visibility RomFileSizeVisibility => string.IsNullOrEmpty(_romFileSizeDisplay) ? Visibility.Collapsed : Visibility.Visible;

        public bool ChkDtbo
        {
            get => _chkDtbo;
            set { if (SetField(ref _chkDtbo, value)) { SavePartitionsSetting(); RefreshButtonStates(); } }
        }

        public bool ChkInitBoot
        {
            get => _chkInitBoot;
            set { if (SetField(ref _chkInitBoot, value)) { SavePartitionsSetting(); RefreshButtonStates(); } }
        }

        public bool ChkVendorBoot
        {
            get => _chkVendorBoot;
            set { if (SetField(ref _chkVendorBoot, value)) { SavePartitionsSetting(); RefreshButtonStates(); } }
        }

        public bool ChkRecovery
        {
            get => _chkRecovery;
            set { if (SetField(ref _chkRecovery, value)) { SavePartitionsSetting(); RefreshButtonStates(); } }
        }

        public bool ChkBoot
        {
            get => _chkBoot;
            set { if (SetField(ref _chkBoot, value)) { SavePartitionsSetting(); RefreshButtonStates(); } }
        }

        // ==================== Per-slot target selection ====================
        private string _slotChoice = "Auto";
        public string SlotChoice
        {
            get => _slotChoice;
            private set
            {
                if (SetField(ref _slotChoice, value))
                {
                    OnPropertyChanged(nameof(SlotAuto));
                    OnPropertyChanged(nameof(SlotA));
                    OnPropertyChanged(nameof(SlotB));
                    OnPropertyChanged(nameof(SlotBoth));
                    RaiseRomPlan();
                    Log($"[Slot] Target slot: {(value == "Auto" ? "auto (active slot)" : value)}");
                }
            }
        }

        public bool SlotAuto { get => _slotChoice == "Auto"; set { if (value) SlotChoice = "Auto"; } }
        public bool SlotA { get => _slotChoice == "A"; set { if (value) SlotChoice = "A"; } }
        public bool SlotB { get => _slotChoice == "B"; set { if (value) SlotChoice = "B"; } }
        public bool SlotBoth { get => _slotChoice == "Both"; set { if (value) SlotChoice = "Both"; } }

        // ==================== P1: custom recovery (TWRP / OrangeFox) ====================

        // True when the added .img looks like a custom recovery image
        // (TWRP / OrangeFox / a plain recovery.img). Such images offer a
        // temporary BOOT that writes nothing, next to the permanent flash.
        public bool AdditionalImageIsRecovery
        {
            get
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(_recoveryFilePath ?? "") ?? "";
                var n = name.ToLowerInvariant();
                return n.Contains("twrp") || n.Contains("orangefox") || n.Contains("ofx") || n.Contains("recovery");
            }
        }
        public Visibility AdditionalRecoveryVisibility =>
            AdditionalImageChosenVisibility == Visibility.Visible && AdditionalImageIsRecovery
                ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AdditionalPlainImageVisibility =>
            AdditionalImageChosenVisibility == Visibility.Visible && !AdditionalImageIsRecovery
                ? Visibility.Visible : Visibility.Collapsed;

        // BOOT RECOVERY: `fastboot boot <img>` runs the recovery once without
        // writing anything — the safest way to use TWRP/OrangeFox.
        public bool BootRecoveryImageEnabled => !IsFlashing && AdditionalImageIsRecovery &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            IsFastbootMode && _currentLockStatus == "Unlocked";
        public string BootRecoveryImageReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath))
                    return "Add a custom recovery image (drag & drop or browse)";
                if (!AdditionalImageIsRecovery)
                    return "The added image is not a recovery image — use FLASH IMAGE";
                if (!IsFastbootMode)
                    return _currentPhoneMode == DeviceMode.AdbRecovery
                        ? "The recovery is already running — reboot to bootloader to swap it"
                        : "Reboot to bootloader — the recovery boots over fastboot";
                if (_currentLockStatus == "Locked") return "Bootloader is locked — unlocking is required";
                if (_currentLockStatus != "Unlocked") return "Bootloader lock state unknown — cannot verify the unlock";
                return "Runs the recovery once — nothing is written; a normal reboot restores the previous recovery";
            }
        }

        // FLASH RECOVERY (persistent): fine on devices with a dedicated
        // recovery partition; on A/B devices the recovery ramdisk lives in
        // boot/vendor_boot, so this is refused with guidance.
        public bool FlashRecoveryImageEnabled => !IsFlashing && AdditionalImageIsRecovery &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            IsFastbootMode && _currentLockStatus == "Unlocked" && !IsAbDevice;
        public string FlashRecoveryImageReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!AdditionalImageIsRecovery) return "The added image is not a recovery image";
                if (!IsFastbootMode) return "Reboot to bootloader first";
                if (_currentLockStatus != "Unlocked") return "Bootloader must be unlocked";
                if (IsAbDevice)
                    return "A/B devices have no separate recovery partition — boot the recovery temporarily instead; installing it permanently is done from inside TWRP (Install Recovery Ramdisk)";
                return "Writes the recovery permanently to the 'recovery' partition";
            }
        }

        // An A/B device exposes an active slot; a dedicated-recovery device
        // reports no slot at all.
        public bool IsAbDevice =>
            _currentActiveSlot != "Unknown" &&
            !string.IsNullOrWhiteSpace(_currentActiveSlot);

        public async System.Threading.Tasks.Task BootRecoveryImageAsync()
        {
            if (IsFlashing || !BootRecoveryImageEnabled) return;
            var path = _recoveryFilePath;
            IsFlashing = true;
            StartFlashStopwatch();
            SetFlashPhase("Done", "Done", "Done", "Active", "WRITE");
            try
            {
                Log("[Recovery] Temporarily booting " + System.IO.Path.GetFileName(path) +
                    " — nothing is written to the device.");
                var output = await GetCombinedToolOutputAsync(
                    ToolPathResolver.GetToolPath("fastboot"),
                    "-s \"" + CurrentDeviceSerial + "\" boot \"" + path + "\"",
                    TimeSpan.FromSeconds(60), AbortToken);
                var ok = !string.IsNullOrWhiteSpace(output) &&
                    (output.Contains("OKAY") || output.ToLowerInvariant().Contains("booting"));
                Log("[Recovery] " + (ok ? "Recovery is booting. The monitor picks it up as ADB (Recovery) in a few seconds."
                                        : "Temporary recovery boot failed — if the device loops back to the bootloader, flash a vbmeta with verity disabled and try again."));
                ShowToast(ok ? "Info" : "Error", ok ? "Recovery booting (temporary)" : "Boot failed",
                    ok ? "Nothing was written — a normal reboot restores the previous recovery."
                       : "The recovery image could not be booted — check the log.");
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                SetFlashPhase("Done", "Done", "Done", "Done", indeterminate: false);
                RefreshButtonStates();
            }
        }

        public async System.Threading.Tasks.Task FlashRecoveryImageAsync()
        {
            if (IsFlashing || !FlashRecoveryImageEnabled) return;
            var path = _recoveryFilePath;
            if (IsAbDevice)
            {
                Log("[Recovery] A/B device — there is no separate recovery partition to flash. " +
                    "Boot the recovery temporarily instead; installing it permanently is done from inside TWRP.");
                ShowToast("Info", "A/B device", "No recovery partition exists — use BOOT RECOVERY (temporary).");
                return;
            }
            // Safety net: pull the current stock recovery first. If it cannot
            // be backed up, the user decides whether to continue unprotected.
            var backedUp = await BackupStockRecoveryAutoAsync(CurrentDeviceSerial);
            if (!backedUp)
            {
                var proceed = await System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    Dialogs.ConfirmDialog.Show(
                        System.Windows.Application.Current?.MainWindow,
                        "BACKUP FAILED",
                        "The stock recovery could not be backed up." + "\n" +
                        "Without a backup there is no way back if the custom recovery fails to load." + "\n\n" +
                        "Continue anyway?",
                        "CONTINUE ANYWAY", "CANCEL", danger: true));
                if (!proceed)
                {
                    Log("[Backup] User cancelled the flash — no backup, no write.");
                    return;
                }
            }
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                Log("[Recovery] Flashing " + System.IO.Path.GetFileName(path) + " -> recovery (persistent).");
                var result = await _flashService.FlashPartitionAsync(
                    CurrentDeviceSerial, "recovery", path, null, _cts?.Token ?? CancellationToken.None);
                if (result.Success)
                {
                    Log("[✓] Custom recovery installed. Use REBOOT TO RECOVERY to enter it.");
                    ShowToast("Success", "Recovery installed",
                        "A custom recovery is now permanent — use REBOOT TO RECOVERY.");
                }
                else
                {
                    Log("[ERROR] " + result.Message);
                    if (!string.IsNullOrWhiteSpace(result.TechnicalDetails)) Log("    " + result.TechnicalDetails);
                    ShowToast("Error", "Flash failed", result.Message);
                }
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                RefreshButtonStates();
            }
        }

        // ==================== A/B: recovery repair — fetch, compare, flash both slots ====================
        // onyx (POCO F7) is an A/B device WITH a dedicated recovery partition.
        // When a sideloaded installer zip reports success but the device still
        // boots the old recovery, this action gathers evidence first: it
        // FETCHES the current recovery_a and hash-compares it with the zip's
        // recovery.img, then (with confirmation) writes the zip's image to
        // recovery_a AND recovery_b and re-fetches to verify the write landed.
        public bool RecoveryBothSlotsEnabled => !IsFlashing && IsAbDevice &&
            AdditionalZipIsRecoveryInstaller &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath);
        public string RecoveryBothSlotsReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!IsAbDevice) return "Only for A/B devices";
                if (!AdditionalZipIsRecoveryInstaller) return "Add an OrangeFox/TWRP installer zip first";
                return "Checks what is really in the recovery partitions, then flashes the zip's recovery.img to BOTH slots (with write verification)";
            }
        }
        public System.Windows.Visibility RecoveryBothSlotsVisibility =>
            RecoveryBothSlotsEnabled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private static string Sha256OfFile(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Deferred write verification: when fastboot fetch is unsupported, the
        // repair flash cannot be read back in fastboot. The expected hash is
        // kept here and verified over adb (dd) the next time the device
        // reaches a recovery with shell access.
        private string _pendingRecoveryVerifyHash;
        private string _pendingRecoveryVerifyPartition;

        /// <summary>
        /// Read a partition back from a recovery session via dd, pull the
        /// image over adb and hash-compare it with the expected value.
        /// </summary>
        private async System.Threading.Tasks.Task VerifyRecoveryReadbackViaAdbAsync(string partition, string expectedHash)
        {
            try
            {
                var adb = ToolPathResolver.GetToolPath("adb");
                var devPath = "/dev/block/by-name/" + partition;
                var remote = "/sdcard/srflasher-verify-" + partition + ".img";
                Log("[Verify] Reading " + partition + " back via dd (" + devPath + ")...");
                var dd = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell dd if=" + devPath + " of=" + remote,
                    TimeSpan.FromMinutes(5), AbortToken);
                Log("[Verify] dd: " + (string.IsNullOrWhiteSpace(dd) ? "(done)" : dd.Trim().Split('\n').LastOrDefault()?.Trim()));

                var dir = BackupDirForSerial(CurrentDeviceSerial);
                Directory.CreateDirectory(dir);
                var local = System.IO.Path.Combine(dir, partition + "-readback-verify.img");
                try { if (File.Exists(local)) File.Delete(local); } catch { }
                var pull = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" pull \"" + remote + "\" \"" + local + "\"",
                    TimeSpan.FromMinutes(5), AbortToken);
                // Clean the temp copy on the device either way.
                await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell rm -f \"" + remote + "\"",
                    TimeSpan.FromSeconds(15), AbortToken);

                if (!File.Exists(local) || new FileInfo(local).Length < 1024)
                {
                    Log("[Verify] " + partition + " readback unavailable (dd/pull failed) — write stays verified by fastboot OKAY only.");
                    return;
                }
                var actual = Sha256OfFile(local);
                if (actual == expectedHash)
                    Log("[✓] " + partition + " VERIFIED by dd readback — the image matches what was flashed.");
                else
                    Log("[ERROR] " + partition + " dd readback MISMATCH — the flashed image does not read back the same! (sha256 " +
                        actual.Substring(0, 16) + "…)");
                try { File.Delete(local); } catch { }
            }
            catch (Exception ex)
            {
                Log("[Verify] Readback verification could not run: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<string> FetchPartitionToTempAsync(string fastboot, string partition, string label)
        {
            var dir = BackupDirForSerial(CurrentDeviceSerial);
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var file = System.IO.Path.Combine(dir, partition + "-" + label + "-" + stamp + ".img");
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            var output = await GetCombinedToolOutputAsync(fastboot,
                "-s \"" + CurrentDeviceSerial + "\" fetch " + partition + " \"" + file + "\"",
                TimeSpan.FromMinutes(6), AbortToken);
            var exists = File.Exists(file) && new FileInfo(file).Length > 1024;
            if (!exists)
            {
                var reason = (output ?? "").Trim();
                if (reason.Contains("does not support fetch") || reason.Contains("max-fetch-size"))
                    Log("[Repair] " + partition + " readback unavailable — this device's bootloader does not support 'fastboot fetch' (no max-fetch-size). " +
                        "Verification will rely on fastboot's OKAY, or a dd read from a recovery session.");
                else if (string.IsNullOrWhiteSpace(reason))
                    Log("[Repair] " + partition + " fetch produced no output — readback unavailable.");
                else
                    Log("[Repair] " + partition + " fetch failed: " + reason);
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                return null;
            }
            return file;
        }

        public async System.Threading.Tasks.Task FlashRecoveryBothSlotsAsync()
        {
            if (IsFlashing || !RecoveryBothSlotsEnabled) return;

            var proceed = await System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                Dialogs.ConfirmDialog.Show(
                    System.Windows.Application.Current?.MainWindow,
                    "REPAIR RECOVERY (BOTH SLOTS)",
                    "This reads the current recovery_a, compares it with the zip's\n" +
                    "recovery.img, then flashes the zip's image to BOTH recovery\n" +
                    "partitions (recovery_a and recovery_b) and verifies the write.\n\n" +
                    "Use when a sideloaded installer did not replace the recovery.\n\n" +
                    "Continue?",
                    "CHECK + FLASH", "CANCEL", danger: true));
            if (!proceed) return;

            IsFlashing = true;
            StartFlashStopwatch();
            string extracted = null;
            var workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SRFlasher", "recovery_repair");
            try
            {
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                var fastboot = ToolPathResolver.GetToolPath("fastboot");

                // Extract recovery.img from the zip (never flash the ZIP itself
                // via fastboot — the OrangeFox wiki forbids that).
                using (var archive = System.IO.Compression.ZipFile.OpenRead(_recoveryFilePath))
                {
                    var entry = archive.Entries.FirstOrDefault(en =>
                        en.Name.Equals("recovery.img", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        Log("[ERROR] The installer zip contains no recovery.img — " +
                            "download the separate .img build and flash it with FLASH RECOVERY (PERSISTENT) instead.");
                        ShowToast("Error", "No recovery.img in zip", "Use the .img build for repair flashing.");
                        return;
                    }
                    Directory.CreateDirectory(workDir);
                    extracted = System.IO.Path.Combine(workDir, "recovery.img");
                    System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, extracted, overwrite: true);
                }
                var wantedHash = Sha256OfFile(extracted);
                Log("[Repair] Extracted recovery.img (" + (new FileInfo(extracted).Length / 1024 / 1024) + " MB) — sha256 " +
                    wantedHash.Substring(0, 16) + "…");

                // Evidence first: what is in recovery_a right now? The fetched
                // image is KEPT in the serial's backup folder — it is the
                // pre-repair state and the rollback image if anything goes wrong.
                var currentA = await FetchPartitionToTempAsync(fastboot, "recovery_a", "pre-repair");
                if (currentA != null)
                {
                    var currentHash = Sha256OfFile(currentA);
                    Log("[Repair] Pre-repair state saved to " + System.IO.Path.GetFileName(currentA) + " — keep it; it restores the old recovery via fastboot if ever needed.");
                    if (currentHash == wantedHash)
                        Log("[Repair] recovery_a ALREADY holds the OrangeFox image (hash match) — the sideload write DID land. " +
                            "If a reboot still shows the old recovery, the bootloader is loading the recovery from another partition (vendor_boot). Do not re-flash.");
                    else
                        Log("[Repair] recovery_a holds something DIFFERENT (sha256 " + currentHash.Substring(0, 16) + "…) — " +
                            "the sideload write did not stick. Flashing both slots now.");
                }
                else
                    Log("[Repair] Could not read the current recovery_a — no pre-repair comparison possible; flashing now.");

                var allOk = true;
                var verifiedAny = false;
                foreach (var partition in new[] { "recovery_a", "recovery_b" })
                {
                    Log("[Repair] Flashing recovery.img -> " + partition + "...");
                    var result = await _flashService.FlashPartitionAsync(
                        CurrentDeviceSerial, partition, extracted, null, _cts?.Token ?? CancellationToken.None);
                    if (result.Success)
                    {
                        Log("[✓] " + partition + " written (fastboot reported OKAY).");
                        // Verify the write landed by reading it back (kept as backup).
                        var verifyFile = await FetchPartitionToTempAsync(fastboot, partition, "repaired");
                        if (verifyFile != null)
                        {
                            var verifyHash = Sha256OfFile(verifyFile);
                            Log(verifyHash == wantedHash
                                ? "[✓] " + partition + " verified — readback matches the OrangeFox image (" + System.IO.Path.GetFileName(verifyFile) + ")."
                                : "[ERROR] " + partition + " readback MISMATCH — the write did not stick! (sha256 " + verifyHash.Substring(0, 16) + "…)");
                            if (verifyHash == wantedHash) verifiedAny = true;
                            else allOk = false;
                        }
                        else
                            Log("[Repair] " + partition + ": readback unavailable (fastboot fetch unsupported) — relying on fastboot's OKAY only.");
                    }
                    else
                    {
                        allOk = false;
                        Log("[ERROR] " + partition + ": " + result.Message);
                        if (!string.IsNullOrWhiteSpace(result.TechnicalDetails)) Log("    " + result.TechnicalDetails);
                    }
                }
                if (allOk)
                {
                    Log(verifiedAny
                        ? "[✓] Both recovery partitions now hold the OrangeFox image (verified by readback). Reboot to recovery to enter it."
                        : "[✓] Both recovery partitions flashed OKAY — fastboot fetch is unsupported here, so the write will be VERIFIED by dd readback the next time the device reaches recovery.");
                    if (!verifiedAny)
                    {
                        // Defer the readback verification to the next recovery
                        // session — dd over adb reads what actually boots.
                        _pendingRecoveryVerifyHash = wantedHash;
                        _pendingRecoveryVerifyPartition = "recovery_a";
                    }
                    ShowToast("Success", "Recovery repaired",
                        "recovery_a and recovery_b both flashed OKAY — REBOOT TO RECOVERY to enter it (and complete verification).");
                }
                else
                    ShowToast("Error", "Repair incomplete", "Not every partition was written — check the LOGS tab.");
            }
            catch (Exception ex)
            {
                Log("[ERROR] Recovery repair failed: " + ex.Message);
                ShowToast("Error", "Repair failed", ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
                IsFlashing = false;
                StopFlashStopwatch();
                RefreshButtonStates();
            }
        }

        /// <summary>
        /// Reboot to recovery, tolerating the recovery's own ADB authorization
        /// prompt: an "unauthorized" rejection retries for ~30 s while the
        /// user accepts the prompt on the phone screen.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> RebootToRecoveryWithAuthRetryAsync()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var rec = await _flashService.RebootViaAdbAsync(CurrentDeviceSerial, "recovery", token);
                if (rec.Success) return true;
                var lower = (rec.Message ?? "").ToLowerInvariant();
                if (!lower.Contains("unauthorized") && !lower.Contains("not authorized"))
                {
                    Log("[ERROR] Reboot to recovery failed: " + rec.Message);
                    return false;
                }
                if (attempt < 3)
                    Log("[Recovery] The recovery's ADB is not authorized — accept the debugging prompt on the phone screen. Retrying in 10 seconds (" + attempt + " of 3)...");
                await System.Threading.Tasks.Task.Delay(10000, token);
            }
            Log("[ERROR] The recovery ADB stayed unauthorized — accept the prompt on the phone screen, then use REBOOT TO RECOVERY.");
            return false;
        }

        // ==================== P3: direct-install routes via the twrp CLI ====================

        // Route B (zips): push to device storage, then `twrp install`. No
        // sideload session needed — useful when sideload is flaky, for queued
        // add-ons in one recovery stay, and when a zip must read files from
        // device storage.
        public bool RecoveryCliInstallEnabled => !IsFlashing &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            _currentPhoneMode == DeviceMode.AdbRecovery && RecoveryCliAvailable;
        public string RecoveryCliInstallReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath))
                    return "Add a ZIP package first";
                if (_currentPhoneMode != DeviceMode.AdbRecovery)
                    return "Requires the device in recovery";
                if (!_recoveryProbeDone) return "Waiting for the recovery probe...";
                if (!RecoveryCliAvailable) return "No twrp CLI in this recovery — sideload is the install route";
                return RecoverySdcardMounted
                    ? "Pushes the zip to /sdcard and installs it with the twrp CLI — no sideload session needed"
                    : "Data is locked: pushes to /tmp (RAM) if the zip fits — otherwise use sideload";
            }
        }

        public async System.Threading.Tasks.Task InstallZipViaRecoveryCliAsync()
        {
            if (IsFlashing || !RecoveryCliInstallEnabled) return;
            var path = _recoveryFilePath;
            var fileName = System.IO.Path.GetFileName(path);

            // Pick the on-device target: /sdcard when data is readable,
            // otherwise /tmp — but tmpfs is RAM, so size-gate it (256 MB
            // headroom for the recovery itself).
            string dest;
            if (RecoverySdcardMounted)
                dest = "/sdcard/" + fileName;
            else
            {
                var size = new FileInfo(path).Length;
                if (_recoveryProbeTmpFreeBytes < size + 256L * 1024 * 1024)
                {
                    Log("[Recovery] Data is locked and /tmp has no room for " + fileName +
                        " (" + (size / 1024 / 1024) + " MB) — use APPLY UPDATE.ZIP (SIDELOAD) instead.");
                    ShowToast("Info", "Not enough recovery storage",
                        "Data is locked and /tmp is too small for this zip — use the sideload route.");
                    return;
                }
                dest = "/tmp/" + fileName;
            }

            IsFlashing = true;
            StartFlashStopwatch();
            SetFlashPhase("Done", "Active", "Pending", "Pending", "WRITE — INSTALLING");
            try
            {
                var adb = ToolPathResolver.GetToolPath("adb");
                Log("[Recovery] Pushing " + fileName + " -> " + dest + " ...");
                var push = await _processManager.ExecuteAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" push \"" + path + "\" \"" + dest + "\"",
                    TimeSpan.FromMinutes(15), _cts?.Token ?? CancellationToken.None);
                if (push.Cancelled || push.TimedOut ||
                    (push.Error ?? "").ToLowerInvariant().Contains("failed") ||
                    (push.Error ?? "").Contains("No such device"))
                {
                    Log("[ERROR] Push to " + dest + " failed — check the log.");
                    ShowToast("Error", "Push failed", "The zip could not be copied to the device — use the sideload route.");
                    return;
                }

                Log("[Recovery] Installing via the twrp CLI: twrp install " + dest);
                var output = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell twrp install \"" + dest + "\"",
                    TimeSpan.FromMinutes(20), AbortToken);
                var o = (output ?? string.Empty);
                var oLower = o.ToLowerInvariant();
                var failed = oLower.Contains("failed") || oLower.Contains("e:error") || o.Contains("E:");
                var succeeded = oLower.Contains("completed successfully") || oLower.Contains("success");
                Log(!string.IsNullOrWhiteSpace(o) ? o.Trim() : "[Recovery] (no CLI output)");
                if (succeeded && !failed)
                {
                    if (dest.StartsWith("/tmp/"))
                    {
                        // tmpfs is RAM: do not leave the copy behind.
                        await GetCombinedToolOutputAsync(adb,
                            "-s \"" + CurrentDeviceSerial + "\" shell rm -f \"" + dest + "\"",
                            TimeSpan.FromSeconds(15));
                    }
                    Log("[✓] ZIP installed via the twrp CLI. Flash more zips, or press FINISH & REBOOT when done.");
                    AdditionalStepText = "ZIP installed from storage — flash more zips, or press FINISH & REBOOT when done.";
                    ShowToast("Success", "ZIP installed", "Installed via the twrp CLI — FINISH & REBOOT when done.");
                }
                else
                {
                    Log("[ERROR] The twrp CLI install did not report success — check the phone screen and the log.");
                    ShowToast("Error", "Install failed", "The twrp CLI reported a problem — check the phone screen.");
                }
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                SetFlashPhase("Done", "Done", "Done", "Done", indeterminate: false);
                RefreshButtonStates();
            }
        }

        // Images through the recovery CLI: `twrp flash <img>` writes the image
        // while staying inside recovery (no bootloader round-trip).
        public bool RecoveryCliFlashImageEnabled => !IsFlashing &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".img", StringComparison.OrdinalIgnoreCase) &&
            _currentPhoneMode == DeviceMode.AdbRecovery && RecoveryCliAvailable;
        public string RecoveryCliFlashImageReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath))
                    return "Add an .img image first";
                if (_currentPhoneMode != DeviceMode.AdbRecovery)
                    return "Requires the device in recovery";
                if (!_recoveryProbeDone) return "Waiting for the recovery probe...";
                if (!RecoveryCliAvailable) return "No twrp CLI in this recovery — use fastboot instead";
                return "Writes the image with the twrp CLI — no bootloader round-trip; partition detected from the file name";
            }
        }

        public async System.Threading.Tasks.Task InstallImageViaRecoveryCliAsync()
        {
            if (IsFlashing || !RecoveryCliFlashImageEnabled) return;
            var path = _recoveryFilePath;
            var fileName = System.IO.Path.GetFileName(path);
            var partition = AdditionalImagePartition;

            string dest;
            if (RecoverySdcardMounted)
                dest = "/sdcard/" + fileName;
            else
            {
                var size = new FileInfo(path).Length;
                if (_recoveryProbeTmpFreeBytes < size + 256L * 1024 * 1024)
                {
                    Log("[Recovery] Data is locked and /tmp has no room for " + fileName +
                        " — reboot to bootloader and use FLASH IMAGE over fastboot instead.");
                    ShowToast("Info", "Not enough recovery storage",
                        "Data is locked and /tmp is too small — use the fastboot route.");
                    return;
                }
                dest = "/tmp/" + fileName;
            }

            IsFlashing = true;
            StartFlashStopwatch();
            SetFlashPhase("Done", "Active", "Pending", "Pending", "WRITE — INSTALLING");
            try
            {
                var adb = ToolPathResolver.GetToolPath("adb");
                Log("[Recovery] Pushing " + fileName + " -> " + dest + " ...");
                var push = await _processManager.ExecuteAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" push \"" + path + "\" \"" + dest + "\"",
                    TimeSpan.FromMinutes(15), _cts?.Token ?? CancellationToken.None);
                if (push.Cancelled || push.TimedOut ||
                    (push.Error ?? "").ToLowerInvariant().Contains("failed"))
                {
                    Log("[ERROR] Push to " + dest + " failed — check the log.");
                    ShowToast("Error", "Push failed", "The image could not be copied to the device — use the fastboot route.");
                    return;
                }

                Log("[Recovery] Flashing via the twrp CLI: twrp flash " + dest + " (partition: " + partition + ")");
                var output = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + CurrentDeviceSerial + "\" shell twrp flash \"" + dest + "\"",
                    TimeSpan.FromMinutes(20), AbortToken);
                var o = (output ?? string.Empty);
                var oLower = o.ToLowerInvariant();
                var failed = oLower.Contains("failed") || oLower.Contains("e:error") || o.Contains("E:");
                var succeeded = oLower.Contains("completed successfully") || oLower.Contains("success");
                Log(!string.IsNullOrWhiteSpace(o) ? o.Trim() : "[Recovery] (no CLI output)");
                if (succeeded && !failed)
                {
                    if (dest.StartsWith("/tmp/"))
                        await GetCombinedToolOutputAsync(adb,
                            "-s \"" + CurrentDeviceSerial + "\" shell rm -f \"" + dest + "\"",
                            TimeSpan.FromSeconds(15));
                    Log("[✓] Image written to '" + partition + "' via the twrp CLI. Use REBOOT TO SYSTEM when ready.");
                    AdditionalStepText = "Image written to '" + partition + "' — use REBOOT TO SYSTEM when ready.";
                    ShowToast("Success", "Image flashed",
                        "'" + fileName + "' written to '" + partition + "' via the twrp CLI.");
                }
                else
                {
                    Log("[ERROR] The twrp CLI flash did not report success — check the phone screen and the log.");
                    ShowToast("Error", "Flash failed", "The twrp CLI reported a problem — check the phone screen.");
                }
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                SetFlashPhase("Done", "Done", "Done", "Done", indeterminate: false);
                RefreshButtonStates();
            }
        }

        // The stock-recovery backup + rescue coordinator now lives in
        // MainWindowViewModel.Backup.cs (session 3, stage 1 extraction).


        // ==================== P2: recovery capability probe ====================

        // One probe per recovery session: identifies TWRP/OrangeFox vs stock,
        // checks the twrp CLI and the data mount state. Reset when the device
        // leaves recovery.
        private bool _recoveryProbeDone;
        private string _recoveryProbeKind = string.Empty;
        private string _recoveryProbeSummary = string.Empty;
        private bool _recoveryProbeCli;
        private bool _recoveryProbeSdcard;
        private long _recoveryProbeTmpFreeBytes = -1;

        // P3 gates: the twrp CLI unlocks the direct-install routes (no
        // sideload session needed) for zips and images.
        public bool RecoveryCliAvailable => _recoveryProbeDone && _recoveryProbeCli;
        public bool RecoverySdcardMounted => _recoveryProbeDone && _recoveryProbeSdcard;
        public bool RecoveryInRecoveryMode => _currentPhoneMode == DeviceMode.AdbRecovery && RecoveryCliAvailable;
        public Visibility RecoveryZipCliVisibility =>
            !string.IsNullOrWhiteSpace(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            RecoveryInRecoveryMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RecoveryImageCliVisibility =>
            !string.IsNullOrWhiteSpace(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".img", StringComparison.OrdinalIgnoreCase) &&
            RecoveryInRecoveryMode ? Visibility.Visible : Visibility.Collapsed;

        public string RecoveryProbeKind { get => _recoveryProbeKind; private set => SetField(ref _recoveryProbeKind, value); }
        public string RecoveryProbeSummary { get => _recoveryProbeSummary; private set => SetField(ref _recoveryProbeSummary, value); }
        public Visibility RecoveryProbeVisibility =>
            _currentPhoneMode == DeviceMode.AdbRecovery && !string.IsNullOrEmpty(_recoveryProbeSummary)
                ? Visibility.Visible : Visibility.Collapsed;

        private void ResetRecoveryProbe()
        {
            _recoveryProbeDone = false;
            _recoveryProbeCli = false;
            _recoveryProbeSdcard = false;
            _recoveryProbeTmpFreeBytes = -1;
            RecoveryProbeKind = string.Empty;
            RecoveryProbeSummary = string.Empty;
            OnPropertyChanged(nameof(RecoveryCliAvailable));
            OnPropertyChanged(nameof(RecoverySdcardMounted));
            OnPropertyChanged(nameof(RecoveryInRecoveryMode));
            OnPropertyChanged(nameof(RecoveryZipCliVisibility));
            OnPropertyChanged(nameof(RecoveryImageCliVisibility));
            OnPropertyChanged(nameof(RecoveryCliInstallEnabled));
            OnPropertyChanged(nameof(RecoveryCliInstallReason));
            OnPropertyChanged(nameof(RecoveryCliFlashImageEnabled));
            OnPropertyChanged(nameof(RecoveryCliFlashImageReason));
            OnPropertyChanged(nameof(RecoveryProbeVisibility));
            OnPropertyChanged(nameof(AdditionalImageBothSlotsVisible));
            OnPropertyChanged(nameof(RecoveryProbeRecheckEnabled));
            OnPropertyChanged(nameof(RecoveryProbeRecheckReason));
            OnPropertyChanged(nameof(DeviceDashboardRefreshEnabled));
            OnPropertyChanged(nameof(DeviceDashboardRefreshReason));
        }

        // 2.1: the probe is one-shot per recovery session, but the device
        // state can change while still in recovery (data mount toggled,
        // recovery re-attached) — let the user re-run it.
        public bool RecoveryProbeRecheckEnabled => !IsFlashing && _currentPhoneMode == DeviceMode.AdbRecovery;
        public string RecoveryProbeRecheckReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (_currentPhoneMode != DeviceMode.AdbRecovery) return "Requires the device in recovery";
                return "Re-runs the recovery detection (CLI, mounts, /tmp space) in case the device changed";
            }
        }

        public async System.Threading.Tasks.Task RecheckRecoveryProbeAsync()
        {
            if (IsFlashing || _currentPhoneMode != DeviceMode.AdbRecovery) return;
            if (string.IsNullOrWhiteSpace(CurrentDeviceSerial)) return;
            _recoveryProbeDone = false;
            Log("[Probe] Re-checking recovery capabilities...");
            await ProbeRecoveryCapabilitiesAsync(CurrentDeviceSerial);
        }

        private async System.Threading.Tasks.Task ProbeRecoveryCapabilitiesAsync(string serial)
        {
            _recoveryProbeDone = true;
            try
            {
                var adb = ToolPathResolver.GetToolPath("adb");
                System.Func<string, System.Threading.Tasks.Task<string>> sh = async args =>
                    await GetCombinedToolOutputAsync(adb, "-s \"" + serial + "\" shell " + args,
                        TimeSpan.FromSeconds(10), AbortToken);

                // --- Identify the recovery ---
                var twrp = (await sh("getprop ro.twrp.version") ?? "").Trim();
                var fox = (await sh("getprop ro.fox.version") ?? "").Trim();
                if (string.IsNullOrEmpty(fox)) fox = (await sh("getprop ro.orangefox.version") ?? "").Trim();
                string kind;
                if (!string.IsNullOrEmpty(fox)) kind = "OrangeFox " + fox;
                else if (!string.IsNullOrEmpty(twrp)) kind = "TWRP " + twrp;
                else kind = "Stock recovery";

                // --- twrp CLI available? ---
                var cliHelp = (await sh("twrp -v 2>/dev/null") ?? "");
                var cliLower = cliHelp.ToLowerInvariant();
                var cli = cliLower.Contains("openrecoveryscript") || cliLower.Contains("twrp version");

                // --- /data mounted (decrypted or unencrypted)? ---
                var mounts = (await sh("cat /proc/mounts 2>/dev/null") ?? "");
                var dataMounted = mounts.Contains(" /data ");
                var sdcardMounted = mounts.Contains(" /data/media") ||
                    (await sh("ls /sdcard 2>/dev/null | head -1") ?? "").Trim().Length > 0;
                // --- /tmp free space (tmpfs — big pushes may not fit) ---
                var tmpFree = string.Empty;
                var df = (await sh("df -k /tmp 2>/dev/null") ?? "");
                var dfLines = df.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (dfLines.Length >= 2)
                {
                    var cols = dfLines[dfLines.Length - 1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length >= 4 && long.TryParse(cols[cols.Length - 4], out var availKb))
                    {
                        _recoveryProbeTmpFreeBytes = availKb * 1024L;
                        tmpFree = (availKb / 1024) + " MB free";
                    }
                }

                _recoveryProbeCli = cli;
                _recoveryProbeSdcard = sdcardMounted;
                var summary = kind +
                    (cli ? " · twrp CLI ✓" : " · twrp CLI ✗") +
                    (sdcardMounted ? " · /sdcard mounted ✓" : " · /sdcard unavailable") +
                    (dataMounted ? " · data mounted ✓" : " · data locked") +
                    (string.IsNullOrEmpty(tmpFree) ? "" : " · /tmp " + tmpFree);
                RecoveryProbeKind = kind;
                RecoveryProbeSummary = summary;
                OnPropertyChanged(nameof(RecoveryCliAvailable));
                OnPropertyChanged(nameof(RecoverySdcardMounted));
                OnPropertyChanged(nameof(RecoveryInRecoveryMode));
                OnPropertyChanged(nameof(RecoveryZipCliVisibility));
                OnPropertyChanged(nameof(RecoveryImageCliVisibility));
                Log("[Recovery] " + summary);
                if (kind == "Stock recovery")
                    Log("[Recovery] No TWRP/OrangeFox detected — ZIP sideload works, twrp CLI actions stay hidden.");
            }
            catch (Exception ex)
            {
                Log("[Recovery] Capability probe failed: " + ex.Message);
                RecoveryProbeKind = "Unknown";
                RecoveryProbeSummary = "Recovery probe failed — ZIP sideload still works.";
            }
            OnPropertyChanged(nameof(RecoveryProbeVisibility));
        }

        // ==================== Custom recovery (TWRP / OrangeFox) ====================
        private string _recoveryFilePath = "";
        public string RecoveryFilePath
        {
            get => _recoveryFilePath;
            set
            {
                if (SetField(ref _recoveryFilePath, value))
                {
                    OnPropertyChanged(nameof(RecoveryFileName));
                    OnPropertyChanged(nameof(RecoveryFileEmptyVisibility));
                    OnPropertyChanged(nameof(RecoveryFileLoadedVisibility));
                    OnPropertyChanged(nameof(AdditionalZipChosenVisibility));
                    OnPropertyChanged(nameof(AdditionalZipIsRecoveryInstaller));
                    OnPropertyChanged(nameof(AdditionalZipInstallerVisibility));
                    OnPropertyChanged(nameof(AdditionalZipPlainVisibility));
                    OnPropertyChanged(nameof(AdditionalImageChosenVisibility));
                    var ext = System.IO.Path.GetExtension(value ?? "").ToLowerInvariant();
                    var zipName = System.IO.Path.GetFileName(value ?? "").ToLowerInvariant();
                    var zipInstaller = ext == ".zip" &&
                        Utilities.AdditionalFileRules.IsRecoveryInstallerZipName(zipName);
                    var zipRoot = ext == ".zip" &&
                        Utilities.AdditionalFileRules.IsRootManagerZipName(zipName);
                    RecoveryFileTypeDisplay = ext == ".img" ? ".IMG \u2192 BOOT / FLASH"
                        : zipInstaller ? ".ZIP \u2192 INSTALLS THE CUSTOM RECOVERY"
                        : zipRoot ? ".ZIP \u2192 INSTALLS THE ROOT MANAGER"
                        : ext == ".zip" ? ".ZIP \u2192 SIDELOAD" : string.Empty;
                    RefreshButtonStates();
                }
            }
        }

        // P6: what the chosen recovery file can do (.img -> boot/flash, .zip -> sideload)
        private string _recoveryFileTypeDisplay = string.Empty;
        public string RecoveryFileTypeDisplay
        {
            get => _recoveryFileTypeDisplay;
            private set { if (SetField(ref _recoveryFileTypeDisplay, value)) OnPropertyChanged(nameof(RecoveryFileTypeVisibility)); }
        }
        public Visibility RecoveryFileTypeVisibility => string.IsNullOrEmpty(_recoveryFileTypeDisplay) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RecoveryFileEmptyVisibility => string.IsNullOrWhiteSpace(_recoveryFilePath) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RecoveryFileLoadedVisibility => string.IsNullOrWhiteSpace(_recoveryFilePath) ? Visibility.Collapsed : Visibility.Visible;
        // Which contextual action block applies to the added file.
        // Recovery-INSTALLER zips: OrangeFox / TWRP distribute their recovery
        // as a flashable zip that replaces the current recovery of a custom
        // ROM when sideloaded (or CLI-installed) from a recovery. Never
        // flashable via fastboot.
        public bool AdditionalZipIsRecoveryInstaller
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_recoveryFilePath)) return false;
                if (!System.IO.Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return false;
                return Utilities.AdditionalFileRules.IsRecoveryInstallerZipName(
                    System.IO.Path.GetFileName(_recoveryFilePath));
            }
        }

        // Root-manager installers that flash from recovery: KernelSU(-Next),
        // APatch, KernelSU-associated kernel zips. Magisk is an .apk now — it
        // is installed from inside Android, never sideloaded.
        public bool AdditionalZipIsRootManager
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_recoveryFilePath)) return false;
                if (!System.IO.Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return false;
                if (AdditionalZipIsRecoveryInstaller) return false;
                return Utilities.AdditionalFileRules.IsRootManagerZipName(
                    System.IO.Path.GetFileName(_recoveryFilePath));
            }
        }
        public Visibility AdditionalZipInstallerVisibility =>
            AdditionalZipChosenVisibility == Visibility.Visible && AdditionalZipIsRecoveryInstaller
                ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AdditionalZipPlainVisibility =>
            AdditionalZipChosenVisibility == Visibility.Visible && !AdditionalZipIsRecoveryInstaller
                ? Visibility.Visible : Visibility.Collapsed;

        public Visibility AdditionalZipChosenVisibility =>
            !string.IsNullOrWhiteSpace(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AdditionalImageChosenVisibility =>
            !string.IsNullOrWhiteSpace(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".img", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;

        public string RecoveryFileName => string.IsNullOrWhiteSpace(_recoveryFilePath)
            ? "No recovery image selected."
            : Path.GetFileName(_recoveryFilePath);

        private static bool IsRecoveryImg(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path) &&
            Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase);

        public bool RecoveryZipSideloadEnabled => !IsFlashing &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            (_currentPhoneMode == DeviceMode.Sideload || _currentPhoneMode == DeviceMode.AdbRecovery ||
             _currentPhoneMode == DeviceMode.AdbSystem || IsFastbootMode);
        public string RecoveryZipSideloadReason =>
            BusyReason() ?? (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath)
                ? "Browse to select a recovery or update ZIP first"
                : !Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    ? "Select a .zip package — .img files are booted or flashed as recovery images"
                    : !IsModeAdb && !IsFastbootMode
                        ? "Requires a connected device (system/recovery/sideload/fastboot)"
                        : "Sideload this selected ZIP (the app handles the reboot into sideload)");

        public bool EnterSideloadEnabled => !IsFlashing && IsModeAdb;
        public string EnterSideloadReason =>
            BusyReason() ?? (IsModeAdb
                ? "Reboot the device into sideload mode (recovery → Apply update from ADB)"
                : "Requires a device connected in ADB mode");

        // ---------- Unified file slot: type- and mode-aware gating ----------
        // .zip  -> installs through recovery sideload (GAPPS, Magisk, OTAs)
        // .img  -> flashes through fastboot (boot, vbmeta, recovery, ...)
        // Recovery mode accepts only .zip; bootloader only .img.
        public bool AdditionalZipActionEnabled => !IsFlashing &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            (_currentPhoneMode == DeviceMode.AdbRecovery || _currentPhoneMode == DeviceMode.AdbSystem ||
             _currentPhoneMode == DeviceMode.Sideload);
        public string AdditionalZipActionReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath))
                    return "Add a .zip package (drag & drop or browse)";
                if (!Path.GetExtension(_recoveryFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    return "The added file is an .img — images flash in fastboot: reboot to bootloader";
                if (IsFastbootMode)
                    return "Bootloader mode accepts .img files only — reboot to recovery for ZIPs";
                return "Sideload this ZIP (the app reboots through recovery into sideload by itself)";
            }
        }

        public bool AdditionalImageActionEnabled => !IsFlashing &&
            !string.IsNullOrWhiteSpace(_recoveryFilePath) && File.Exists(_recoveryFilePath) &&
            Path.GetExtension(_recoveryFilePath).Equals(".img", StringComparison.OrdinalIgnoreCase) &&
            IsFastbootMode && _currentLockStatus == "Unlocked";
        public string AdditionalImageActionReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (string.IsNullOrWhiteSpace(_recoveryFilePath) || !File.Exists(_recoveryFilePath))
                    return "Add an .img image (drag & drop or browse)";
                if (!Path.GetExtension(_recoveryFilePath).Equals(".img", StringComparison.OrdinalIgnoreCase))
                    return "The added file is a .zip — ZIPs install through recovery sideload";
                if (!IsFastbootMode)
                    return _currentPhoneMode == DeviceMode.AdbRecovery
                        ? "Recovery accepts .zip packages only — reboot to bootloader to flash .img files"
                        : "Reboot to bootloader — .img images flash over fastboot";
                if (_currentLockStatus == "Locked") return "Bootloader is locked — unlocking is required before flashing";
                if (_currentLockStatus != "Unlocked") return "Bootloader lock state unknown — cannot verify the unlock";
                return "Flash to the '" + AdditionalImagePartition + "' partition via fastboot";
            }
        }

        // Which partition a dropped .img targets (name convention: boot.img -> boot,
        // magisk_patched-XXXX.img -> boot, vbmeta.img -> vbmeta, ...).
        public string AdditionalImagePartition => Utilities.AdditionalFileRules.GuessPartitionImage(_recoveryFilePath);

        // 2.2: patched-root boot images land on the ACTIVE slot only by
        // default. The other slot still holds the unpatched boot of the
        // (previous) ROM — the next OTA slot flip would silently drop root.
        // This opt-in writes the image to BOTH slots (default OFF — rooting
        // both slots is a deliberate choice).
        private bool _additionalImageBothSlots;
        public bool AdditionalImageBothSlots
        {
            get => _additionalImageBothSlots;
            set
            {
                if (SetField(ref _additionalImageBothSlots, value)) RefreshButtonStates();
            }
        }
        public bool AdditionalImageBothSlotsVisible =>
            AdditionalImagePartition.Equals("boot", StringComparison.OrdinalIgnoreCase) &&
            IsAbDevice && IsFastbootMode && _currentLockStatus == "Unlocked";

        // Accepts a dropped/browsed file for the unified slot — enforces the
        // mode/type rules (recovery: .zip only; bootloader: .img only).
        public void SetAdditionalFile(string path)
        {
            if (IsFlashing) { Log("[Files] An operation is in progress — try again after it finishes."); return; }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log("[Files] File not found.");
                ShowToast("Error", "File not found", "The dropped file could not be read.");
                return;
            }
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".zip" && ext != ".img")
            {
                Log("[Files] " + System.IO.Path.GetFileName(path) + " rejected — only .zip packages and .img images are accepted here.");
                ShowToast("Error", "Unsupported file", "Only .zip packages and .img images are accepted on this page.");
                return;
            }
            if (IsFastbootMode && ext == ".zip")
            {
                // Staging is not an action: a ZIP may be added in any mode.
                // INSTALL RECOVERY ZIP (SIDELOAD) auto-chains to recovery.
                Log("[Files] " + System.IO.Path.GetFileName(path) + " staged while in bootloader mode — INSTALL RECOVERY ZIP (SIDELOAD) reboots to recovery automatically.");
            }
            if (_currentPhoneMode == DeviceMode.AdbRecovery && ext == ".img")
            {
                // The twrp CLI can flash images from recovery — allow the
                // image when the probe has confirmed the CLI (or is still
                // running); otherwise keep the old fastboot-only rule.
                if (_recoveryProbeDone && !RecoveryCliAvailable)
                {
                    Log("[Files] " + System.IO.Path.GetFileName(path) + " rejected — this recovery has no twrp CLI. Reboot to bootloader for .img files.");
                    ShowToast("Info", "Wrong mode for an image",
                        "This recovery has no twrp CLI — reboot to bootloader for .img files.");
                    return;
                }
                Log("[Files] " + System.IO.Path.GetFileName(path) + " will flash via the twrp CLI once the probe confirms it.");
            }
            RecoveryFilePath = path;
            RecentFilesManager.Instance.AddRecentAdditional(path);
            RefreshRecentAdditional();
            if (AdditionalZipIsRecoveryInstaller)
            {
                Log("[Files] Added recovery installer zip " + System.IO.Path.GetFileName(path) + " — sideloading it replaces the device's current recovery.");
                ShowToast("Success", "Recovery installer added",
                    "INSTALL RECOVERY ZIP (SIDELOAD) replaces the current recovery, then REBOOT TO RECOVERY enters the new one.");
            }
            else if (AdditionalImageIsRecovery)
            {
                Log("[Files] Added custom recovery " + System.IO.Path.GetFileName(path) + " — BOOT RECOVERY (temporary) runs it without writing anything.");
                ShowToast("Success", "Custom recovery added",
                    "BOOT RECOVERY (temporary) runs it without writing; FLASH RECOVERY installs it permanently.");
            }
            else
            {
                Log("[Files] Added " + System.IO.Path.GetFileName(path) + " — " +
                    (ext == ".zip" ? "sideload package." : "image, targets partition '" + AdditionalImagePartition + "'."));
                ShowToast("Success", "File added",
                    ext == ".zip" ? "ZIP package added — APPLY UPDATE.ZIP (SIDELOAD) installs it."
                                   : "Image added — FLASH IMAGE writes it to '" + AdditionalImagePartition + "'.");
            }
        }

        /// <summary>
        /// One-press image flash: writes the added .img to the partition its
        /// name indicates (boot.img -> boot, magisk_patched -> boot, ...).
        /// Requires the device in fastboot/bootloader mode with an unlocked
        /// bootloader. Flashes to the ACTIVE slot (no explicit suffix).
        /// </summary>
        public async System.Threading.Tasks.Task FlashDroppedImageAsync(bool isRootBoot = false)
        {
            if (IsFlashing) return;
            var path = _recoveryFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !System.IO.Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase))
            {
                Log("[Files] Add an .img image first.");
                return;
            }
            if (_currentLockStatus != "Unlocked")
            {
                Log("[Files] Bootloader must be unlocked to flash images.");
                ShowToast("Error", "Bootloader locked", "Unlock the bootloader before flashing images.");
                return;
            }

            var partition = AdditionalImagePartition;
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                // One-press chain: reach bootloader automatically from any ADB
                // mode — no more "use REBOOT TO BOOTLOADER and try again".
                if (!IsFastbootMode)
                {
                    if (!DeviceConnected || !IsModeAdb)
                    {
                        Log("[Files] Flashing needs fastboot — the device is not reachable over ADB right now.");
                        ShowToast("Error", "No device", "Reboot to bootloader manually and retry.");
                        return;
                    }
                    Log("[Files] Device is in " + DeviceModeDisplay + " — rebooting to bootloader for the flash...");
                    var rb = await _flashService.RebootToBootloaderAsync(
                        CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                    if (!rb.Success)
                    {
                        Log("[ERROR] Reboot to bootloader failed: " + rb.Message);
                        ShowToast("Error", "Reboot failed", rb.Message);
                        return;
                    }
                    Log("[Files] Device is in fastboot/bootloader mode.");
                }

                Log("[Files] Flashing " + System.IO.Path.GetFileName(path) + " -> " + partition);
                AdditionalLastMapping.Clear();
                AdditionalLastMapping.Add("  " + System.IO.Path.GetFileName(path) + "  ->  " + partition);
                RaiseAdditionalMapping();
                // P6: vbmeta is always flashed with verity/verification
                // disabled here — that is the whole point of dropping a
                // vbmeta.img on this page (custom recoveries bootloops without
                // it on strict-verity devices).
                System.Threading.Tasks.Task<FlashResult> flashTask;
                if (partition.Equals("vbmeta", StringComparison.OrdinalIgnoreCase))
                {
                    flashTask = _flashService.FlashVbmetaDisabledAsync(
                        CurrentDeviceSerial, partition, path, null, _cts?.Token ?? CancellationToken.None);
                }
                else if (_additionalImageBothSlots && partition.Equals("boot", StringComparison.OrdinalIgnoreCase) && IsAbDevice)
                {
                    // Write the image to both A/B slots so root survives the
                    // next OTA slot flip.
                    flashTask = FlashBootBothSlotsAsync(path);
                }
                else
                {
                    flashTask = _flashService.FlashPartitionAsync(
                        CurrentDeviceSerial, partition, path, null, _cts?.Token ?? CancellationToken.None);
                }
                var result = await flashTask;
                if (result.Success)
                {
                    Log("[✓] Image written to '" + partition + "'" +
                        (partition.Equals("vbmeta", StringComparison.OrdinalIgnoreCase)
                            ? " with verity/verification DISABLED." : "."));
                    if (isRootBoot)
                    {
                        Log("[Root] Patched boot written. On the first boot the kernel applies root — " +
                            "open the Magisk app afterwards to confirm it shows Installed; if the phone does not boot, " +
                            "flash the unpatched boot image back (it is part of the ROM's payload).");
                    }

                    // One-press chain part 2: straight back into the system on
                    // success — the user never reboots manually.
                    Log("[Files] Flash OK — rebooting to system automatically...");
                    var reboot = await _flashService.RebootAsync(
                        CurrentDeviceSerial, "system", _cts?.Token ?? CancellationToken.None);
                    if (reboot.Success)
                    {
                        Log("[✓] Device is rebooting to system.");
                        ShowToast("Success", isRootBoot ? "Root boot flashed" : "Image flashed",
                            isRootBoot
                                ? "'" + System.IO.Path.GetFileName(path) + "' written to boot. The phone is rebooting — verify root in the Magisk app."
                                : "'" + System.IO.Path.GetFileName(path) + "' written to '" + partition + "'. The phone is rebooting to system.");
                    }
                    else
                    {
                        Log("[ERROR] Reboot to system failed: " + reboot.Message + " — use REBOOT TO SYSTEM when ready.");
                        ShowToast("Success", isRootBoot ? "Root boot flashed" : "Image flashed",
                            "'" + System.IO.Path.GetFileName(path) + "' written to '" + partition + "'. Use REBOOT TO SYSTEM.");
                    }
                }
                else
                {
                    Log("[ERROR] " + result.Message);
                    if (!string.IsNullOrWhiteSpace(result.TechnicalDetails)) Log("    " + result.TechnicalDetails);
                    ShowToast("Error", "Flash failed", result.Message);
                }
            }
            finally
            {
                StopFlashStopwatch();
                IsFlashing = false;
            }
        }

        private async System.Threading.Tasks.Task<FlashResult> FlashBootBothSlotsAsync(string path)
        {
            foreach (var slot in new[] { "a", "b" })
            {
                var result = await _flashService.FlashPartitionAsync(
                    CurrentDeviceSerial, "boot", path, slot, _cts?.Token ?? CancellationToken.None);
                if (!result.Success) return result;
            }
            return FlashResult.Ok("Boot image written to both slots.");
        }

        /// <summary>Reboot to system — from any reachable mode: fastboot via fastboot,
        /// custom recovery via the twrp CLI, everything else via adb.</summary>
        public bool RebootSystemEnabled => !IsFlashing &&
            (IsFastbootMode || _currentPhoneMode == DeviceMode.AdbSystem ||
             _currentPhoneMode == DeviceMode.AdbRecovery || _currentPhoneMode == DeviceMode.Sideload);
        public string RebootSystemReason =>
            BusyReason() ?? (IsFastbootMode
                ? "Reboot the device to the operating system"
                : _currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode
                    ? "Reboot to system via the recovery's CLI"
                    : _currentPhoneMode == DeviceMode.AdbRecovery || _currentPhoneMode == DeviceMode.Sideload
                        ? "Reboot to system via adb — accept the recovery's ADB prompt if it asks"
                        : _currentPhoneMode == DeviceMode.AdbSystem
                            ? "Reboot the device to the operating system"
                            : "Requires a connected device in fastboot, recovery, sideload or system mode");


        public bool FactoryWipeEnabled => !IsFlashing && _currentLockStatus == "Unlocked" &&
            (IsFastbootMode || RecoveryInRecoveryMode ||
             _currentPhoneMode == DeviceMode.AdbSystem || _currentPhoneMode == DeviceMode.AdbRecovery);
        public string FactoryWipeReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (RecoveryInRecoveryMode)
                    return "FORMAT DATA via the twrp CLI — erases everything INCLUDING encryption keys and the screen lock";
                if (_currentLockStatus == "Locked") return "Bootloader is locked — unlocking is required first";
                if (_currentLockStatus != "Unlocked") return "Bootloader lock state unknown — cannot verify the unlock";
                return IsFastbootMode
                    ? "Erase userdata + cache (fastboot -w) — required when crossing ROM families"
                    : "Reboots to the bootloader first, then erases userdata + cache (fastboot -w)";
            }
        }

        // vbmeta verity/verification disable toggle (cross-ROM migrations)
        private bool _chkVbmetaVerityOff;
        public bool ChkVbmetaVerityOff
        {
            get => _chkVbmetaVerityOff;
            set => SetField(ref _chkVbmetaVerityOff, value);
        }

        // Clean vs dirty flash: wipe userdata before flashing (session-only, default = dirty)
        private bool _chkWipeDataBeforeFlash;
        public bool ChkWipeDataBeforeFlash
        {
            get => _chkWipeDataBeforeFlash;
            set
            {
                if (SetField(ref _chkWipeDataBeforeFlash, value))
                {
                    OnPropertyChanged(nameof(IsCleanFlash));
                    OnPropertyChanged(nameof(IsDirtyFlash));
                    RaiseRomPlan();
                    RefreshButtonStates();
                }
            }
        }

        // Simple-mode clean/dirty choice — same backing flag as the Expert checkbox
        public bool IsCleanFlash
        {
            get => _chkWipeDataBeforeFlash;
            set { if (_chkWipeDataBeforeFlash != value) ChkWipeDataBeforeFlash = value; }
        }
        public bool IsDirtyFlash
        {
            get => !_chkWipeDataBeforeFlash;
            set { if (_chkWipeDataBeforeFlash == value) ChkWipeDataBeforeFlash = !value; }
        }
        public Visibility SimpleWipeVisibility => _isExpertMode ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Returns the partitions selected in the ROM tab, in the order the UI
        /// lists them, so plan sentences and queue rows read naturally.
        /// </summary>
        public List<string> GetSelectedPartitions()
        {
            var partitions = new List<string>();
            if (_chkBoot) partitions.Add("boot");
            if (_chkInitBoot) partitions.Add("init_boot");
            if (_chkVendorBoot) partitions.Add("vendor_boot");
            if (_chkDtbo) partitions.Add("dtbo");
            if (_chkRecovery) partitions.Add("recovery");
            return partitions;
        }

        public bool IsFlashing
        {
            get => _isFlashing;
            set
            {
                if (SetField(ref _isFlashing, value))
                {
                    OnPropertyChanged(nameof(IsNotFlashing));
                    OnPropertyChanged(nameof(FlashProgressVisibility));
                    OnPropertyChanged(nameof(FlashOverlayVisibility));
                    OnPropertyChanged(nameof(QueueBadgeVisibility));
                    OnPropertyChanged(nameof(QueueBadgeText));
                    OnPropertyChanged(nameof(IdleFooterVisibility));
                    RefreshButtonStates();
                }
            }
        }

        public bool IsNotFlashing => !_isFlashing;

        public Visibility FlashProgressVisibility =>
            (IsFlashing || _payloadSessionActive || _romInstallWaitingRecovery) ? Visibility.Visible : Visibility.Collapsed;

        // Full-content flash overlay: shown only while an operation is actively
        // running (IsFlashing). During the session's "waiting for sideload"
        // stretch the window stays interactive — the guidance dialog covers it.
        public Visibility FlashOverlayVisibility =>
            IsFlashing ? Visibility.Visible : Visibility.Collapsed;

        // ---------- Full-page welcome (sidebar becomes a full view) ----------
        // The app opens on a full-width page that shows only the device status.
        // Once a device is connected the page asks "What do you want to do
        // today?" with FLASH A ROM / FLASH FILES; picking one slides the page
        // left into the sidebar and reveals the matching tab. Once left, the
        // page never returns for the rest of the session.
        private bool _appEntered;
        public Visibility WelcomeVisibility => _appEntered ? Visibility.Collapsed : Visibility.Visible;
        public Visibility WelcomeNoDeviceVisibility => DeviceConnected ? Visibility.Collapsed : Visibility.Visible;
        public Visibility WelcomeDeviceVisibility => DeviceConnected ? Visibility.Visible : Visibility.Collapsed;

        // The main TabControl index (ROM=0, ADDITIONAL FILES=1, LOGS=2).
        public int SelectedTabIndex { get => _selectedTabIndex; set => SetField(ref _selectedTabIndex, value); }
        private int _selectedTabIndex;

        private void EnterAppCore()
        {
            if (_appEntered) return;
            _appEntered = true;
            OnPropertyChanged(nameof(WelcomeVisibility));
        }

        public void EnterAppFlashRom() { EnterAppCore(); SelectedTabIndex = 0; }
        public void EnterAppFlashFiles() { EnterAppCore(); SelectedTabIndex = 1; }
        public void EnterAppSkip() { EnterAppCore(); }

        // Payload ROM install session: the footer and progress bar stay visible
        // from stage 1 until the system actually boots. Progress contract:
        //   60%  stage 1 (boot chain) written
        //   90%  ROM applied via sideload
        //   95%  GAPPS / add-on zip applied
        //   100% system successfully booted
        private bool _payloadSessionActive;
        private int _payloadStage;
        // One-shot log guard: note the mid-install device hold once per session.
        private bool _deviceHoldLogged;
        // Stage 1 of a two-stage ROM install is written, the device is
        // rebooting to recovery and the package will install by itself. Until
        // the sideload session takes over, the flashing footer must stay
        // visible — the install is NOT finished just because the writes are.
        private bool _romInstallWaitingRecovery;
        /// <summary>Sets the stage-1 wait flag and refreshes the footer/abort bindings.</summary>
        private void SetRomInstallWaitingRecovery(bool value)
        {
            if (_romInstallWaitingRecovery == value) return;
            _romInstallWaitingRecovery = value;
            OnPropertyChanged(nameof(FlashProgressVisibility));
            OnPropertyChanged(nameof(FooterAbortEnabled));
        }
        public bool PayloadSessionActive => _payloadSessionActive;
        public bool FooterAbortEnabled => IsFlashing || _payloadSessionActive || _romInstallWaitingRecovery;

        // Reads the device's build fingerprint over adb (null when the device
        // is unreachable or unauthorized - callers treat null as "unknown").
        private async Task<string> GetDeviceFingerprintAsync(string serial)
        {
            try
            {
                var output = await _processManager.GetOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s " + serial + " shell getprop ro.build.fingerprint",
                    timeout: TimeSpan.FromSeconds(10));
                return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
            }
            catch
            {
                return null;
            }
        }

        // Reads the device's active A/B slot suffix ("_a"/"_b"; "" on
        // non-A/B devices - callers treat empty as "no signal").
        private async Task<string> GetDeviceSlotAsync(string serial)
        {
            try
            {
                var output = await _processManager.GetOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "-s " + serial + " shell getprop ro.boot.slot_suffix",
                    timeout: TimeSpan.FromSeconds(10));
                return output == null ? null : output.Trim();
            }
            catch
            {
                return null;
            }
        }

        private void BeginPayloadSession()
        {
            _sessionRomFile = SelectedFilePath;
            _sessionBackups.Clear();
            // Only a FRESH session starts at stage 0. Re-entering the session
            // for stage 2 must keep the stage stage 1 set when the boot chain
            // was written: the sideload result (90% / 95%) and the boot
            // completion (100%) both branch on it. Resetting it here left the
            // bar parked at 90% forever after the device booted to system.
            var startingFresh = !_payloadSessionActive;
            _payloadSessionActive = true;
            // The session now owns the progress UI — the stage-1 wait flag
            // hands over here.
            SetRomInstallWaitingRecovery(false);
            if (startingFresh) _payloadStage = 0; // stage 0 = partition flashing occupies 0-15%
            _additionalFilesSwitched = false;
            OnPropertyChanged(nameof(PayloadSessionActive));
            OnPropertyChanged(nameof(FlashProgressVisibility));
            OnPropertyChanged(nameof(FooterAbortEnabled));
        }

        private void EndPayloadSession()
        {
            _payloadSessionActive = false;
            _payloadStage = 0;
            SetRomInstallWaitingRecovery(false);
            _additionalFilesSwitched = false;
            OnPropertyChanged(nameof(PayloadSessionActive));
            OnPropertyChanged(nameof(FlashProgressVisibility));
            OnPropertyChanged(nameof(FooterAbortEnabled));
        }

        // Guards the monitor-driven stage-2 auto-apply against double-firing.
        private bool _payloadAutoApplyRunning;

        // Set once per payload session when the monitor moves the app to the
        // ADDITIONAL FILES tab because the device rebooted to recovery.
        private bool _additionalFilesSwitched;

        // The device's build fingerprint captured before a payload install.
        // When a dropped sideload link leaves the outcome unknown, the monitor
        // re-reads the fingerprint after the reboot: a change proves the new
        // ROM applied - no user confirmation needed.
        private string _preFlashFingerprint;

        // The fingerprint observed after the install completed (session report).
        private string _lastFlashFingerprint;

        // The active A/B slot captured before a payload install. Applying a
        // payload OTA switches the slot even when re-flashing the identical
        // ROM, so this catches the "same ROM again" case that a fingerprint
        // comparison cannot.
        private string _preFlashSlot;

        // Set when a stage-1 sideload attempt ends in an adb error: the link
        // can drop the moment the package finishes and the device reboots, so
        // a failure here is AMBIGUOUS — resolved when the next device state
        // is known (see the monitor's AdbSystem branch).
        private bool _sideloadAttemptFailed;

        // The themed RECOVERY BOOTED guidance window closes itself the moment
        // the app detects sideload — through any of its detection paths.
        public event Action SideloadGuidanceClosed;
        private void RaiseSideloadGuidanceClosed()
        {
            try { SideloadGuidanceClosed?.Invoke(); }
            catch { /* closing a popup must never take the flow down */ }
        }

        // ---------- Sideload progress ramp ----------
        // adb sideload does not print the device's install percentage on every
        // build, so the bar is driven by a time-based estimate over the package
        // size (clamped below 90 until the real completion event). If adb DOES
        // stream percentages they override the estimate, so the bar tracks the
        // same 0-100 the recovery screen shows, mapped onto 15-90.
        private DispatcherTimer _sideloadRampTimer;
        private DateTime _sideloadRampStart;
        private double _sideloadRampExpectedMs;
        private double _sideloadRampStreamedPct = -1;
        private bool _sideloadRampActive;

        private void StartSideloadRamp(string filePath)
        {
            StopSideloadRamp();
            long bytes = 0;
            try { bytes = new FileInfo(filePath).Length; } catch { }
            _sideloadRampExpectedMs = bytes > 0
                ? bytes / 6.5e6 * 1000.0
                : TimeSpan.FromMinutes(10).TotalMilliseconds;
            _sideloadRampStart = DateTime.UtcNow;
            _sideloadRampStreamedPct = -1;
            _sideloadRampActive = true;
            if (_sideloadRampTimer == null)
            {
                _sideloadRampTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _sideloadRampTimer.Tick += (s, e) =>
                {
                    if (!_sideloadRampActive) { _sideloadRampTimer.Stop(); return; }
                    if (_payloadSessionActive && _payloadStage != 1 && _payloadStage != 2) { StopSideloadRamp(); return; }
                    var elapsed = (DateTime.UtcNow - _sideloadRampStart).TotalMilliseconds;
                    var estPct = _sideloadRampExpectedMs > 0
                        ? Math.Min(1.0, elapsed / _sideloadRampExpectedMs) * 100
                        : 0;
                    var shownPct = _sideloadRampStreamedPct >= 0 ? _sideloadRampStreamedPct : estPct;
                    if (_payloadSessionActive && _payloadStage == 1)
                    {
                        var mapped = Math.Min(15 + shownPct * 0.75, 89.5);
                        UpdateProgress(mapped, "SIDELOADING ROM…");
                    }
                    else if (_payloadSessionActive && _payloadStage == 2)
                    {
                        var mapped = Math.Min(90 + shownPct * 0.05, 94.9);
                        UpdateProgress(mapped, "APPLYING ADD-ON ZIP…");
                    }
                    else
                    {
                        // Standalone sideload (installer zips, add-ons applied
                        // outside a payload session): the bar used to freeze —
                        // now it follows the same estimated-pace ramp.
                        var mapped = Math.Min(5 + shownPct * 0.85, 89.5);
                        UpdateProgress(mapped, "SIDELOADING PACKAGE…");
                    }
                };
            }
            _sideloadRampTimer.Start();
        }

        private void StopSideloadRamp()
        {
            _sideloadRampActive = false;
            _sideloadRampTimer?.Stop();
        }

        private async Task AutoApplyPayloadStage2Async()
        {
            try
            {
                if (_payloadStage != 1 || IsFlashing) return;
                // A mid-transfer failure (USB glitch, adb hiccup) leaves the
                // device STILL in sideload with no state change to re-trigger
                // the monitor — retry a bounded number of times while the
                // device keeps offering sideload.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (_payloadStage != 1) break;
                    var applied = await SideloadUpdateAsync(SelectedFilePath, allowRebootChain: false);
                    if (applied) return;
                    Log($"[Plan] Stage 2 — sideload attempt {attempt} of 3 failed" +
                        (attempt < 3 ? "; retrying in 10 seconds…" : "; the session keeps waiting. " +
                        "Enter 'Apply update from ADB' again to retry."));
                    if (attempt >= 3) break;
                    await Task.Delay(10000);
                    var det = await _flashService.DetectModeAsync(
                        CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                    if (det != "Sideload")
                    {
                        Log("[Plan] Stage 2 — the device left sideload; waiting for it to re-enter (no time limit).");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Stage 2 auto-apply failed: {ex.Message}");
            }
            finally
            {
                _payloadAutoApplyRunning = false;
            }
        }

        // C22: the window title carries the current state
        public string WindowTitle => IsFlashing
            ? "SR Flasher — FLASHING"
            : $"SR Flasher — {_deviceModeDisplay}";

        // D2: a live badge on the ADDITIONAL FILES tab while the queue runs
        public Visibility QueueBadgeVisibility =>
            IsFlashing && BatchOperationManager.Instance.JobQueue.Any(j => j.Status == RomFlashJob.JobStatus.Queued)
                ? Visibility.Visible : Visibility.Collapsed;
        public string QueueBadgeText =>
            $"QUEUE {BatchOperationManager.Instance.JobQueue.Count(j => j.Status == RomFlashJob.JobStatus.Queued)} LEFT";

        // D3: idle footer bar (thin, always readable when a device is connected)
        public Visibility IdleFooterVisibility =>
            (!IsFlashing && DeviceConnected) ? Visibility.Visible : Visibility.Collapsed;
        public string IdleFooterLeft => DeviceConnected ? $"DEVICE: {_deviceModeDisplay}" : "NO DEVICE CONNECTED";
        public string IdleFooterRight =>
            string.IsNullOrEmpty(_lastFlashSummary) ? null : _lastFlashSummary;
        private string _lastFlashSummary = "";

        // ==================== After care (Simple mode) ====================
        // Shown after a successful flash in Simple mode: three honest next
        // steps instead of leaving the user staring at a finished flash.
        private string _flashJournal = string.Empty;

        public bool ShowAfterCare { get; private set; }

        public Visibility AfterCareVisibility => ShowAfterCare ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// The red route banner is only needed when THE PLAN does not already
        /// explain the route: Simple mode + payload reads the two-stage story
        /// from THE PLAN card, so the banner would repeat it right above.
        /// </summary>
        public Visibility RomRouteBannerVisibility
            => RomRouteVisibility == Visibility.Visible && !(IsSimpleMode && RomIsPayloadOta)
                ? Visibility.Visible
                : Visibility.Collapsed;

        private void RaiseAfterCare()
        {
            OnPropertyChanged(nameof(ShowAfterCare));
            OnPropertyChanged(nameof(AfterCareVisibility));
        }

        public string AfterCareHeadline { get; private set; } = "";

        public string AfterCareBody { get; private set; } = "";

        private void BeginFlashJournal(string headline)
        {
            _flashJournal = $"[{DateTime.Now:HH:mm:ss}] {headline}" + Environment.NewLine;
            ShowAfterCare = false;
            RaiseAfterCare();
        }

        private void Journal(string line) => _flashJournal += $"[{DateTime.Now:HH:mm:ss}] {line}" + Environment.NewLine;

        private void ShowAfterCarePanel(string headline, string body)
        {
            Journal(headline);
            _lastFlashSummary = headline;
            UpdateStepTrail();
            OnPropertyChanged(nameof(IdleFooterRight));
            AfterCareHeadline = headline;
            AfterCareBody = body + Environment.NewLine + Environment.NewLine + _flashJournal.TrimEnd();
            ShowAfterCare = true;
            RaiseAfterCare();
            OnPropertyChanged(nameof(AfterCareHeadline));
            OnPropertyChanged(nameof(AfterCareBody));
        }

        // ==================== Flash phases (footer) ====================
        // "Pending" (dim) / "Active" (red) / "Done" (green). The mapper below is
        // driven from the FlashService step names so the footer reflects the
        // pipeline instead of a percent that can sit frozen for minutes.
        public string FlashPhaseChecks { get; private set; } = "Pending";
        public string FlashPhaseWrite { get; private set; } = "Pending";
        public string FlashPhaseVerify { get; private set; } = "Pending";
        public string FlashPhaseReboot { get; private set; } = "Pending";

        /// <summary>The WRITE chip's caption: a counter, PREPARING, or APPLYING.</summary>
        public string FlashPhaseWriteLabel { get; private set; } = "WRITE";

        /// <summary>True while a phase runs without measurable progress (extraction, reboots, sideload).</summary>
        public bool FlashBarIndeterminate { get; private set; }

        private void SetFlashPhase(string checks, string write, string verify, string reboot,
                                   string writeLabel = null, bool indeterminate = false)
        {
            FlashPhaseChecks = checks; FlashPhaseWrite = write;
            FlashPhaseVerify = verify; FlashPhaseReboot = reboot;
            if (writeLabel != null) FlashPhaseWriteLabel = writeLabel;
            FlashBarIndeterminate = indeterminate;
            OnPropertyChanged(nameof(FlashPhaseChecks)); OnPropertyChanged(nameof(FlashPhaseWrite));
            OnPropertyChanged(nameof(FlashPhaseVerify)); OnPropertyChanged(nameof(FlashPhaseReboot));
            OnPropertyChanged(nameof(FlashPhaseWriteLabel)); OnPropertyChanged(nameof(FlashBarIndeterminate));
        }

        private void UpdateFlashPhases(int percent, string step, string partition, string message)
        {
            step = step ?? string.Empty;
            var d = percent >= 100;

            // A payload session owns a defined progress slice at every stage
            // (partitions 0-15, ROM 15-90, add-on 90-95), so the value bar must
            // always track its percentage — never swap it for the indeterminate
            // pulse during reboots, extraction or sideload.
            var noPulse = _payloadSessionActive;

            if (d)
            {
                SetFlashPhase("Done", "Done", "Done", "Done", "WRITE", indeterminate: false);
                return;
            }

            if (step == "Flash partition")
            {
                if (partition == null)
                {
                    // The 85% "all partitions flashed" event.
                    SetFlashPhase("Done", "Done", "Active", "Pending", "WRITE");
                    return;
                }
                // Count "image i of n" from the message when present.
                var label = "WRITE";
                var m = System.Text.RegularExpressions.Regex.Match(message ?? "",
                    @"\((\d+)/(\d+)\)");
                if (m.Success) label = $"WRITE {m.Groups[1].Value}/{m.Groups[2].Value}";
                SetFlashPhase("Done", "Active", "Pending", "Pending", label);
                return;
            }

            if (step == "Prepare images" || step == "Sideload")
            {
                // Inside a payload session the boot chain is already written:
                // the sideloaded package applying itself is the VERIFY slice.
                if (_payloadSessionActive && step == "Sideload")
                {
                    SetFlashPhase("Done", "Done", "Active", "Pending",
                        "VERIFY — APPLYING", indeterminate: !noPulse);
                    return;
                }
                SetFlashPhase("Done", "Active", "Pending", "Pending",
                    step == "Sideload" ? "WRITE — APPLYING" : "WRITE — PREPARING",
                    indeterminate: !noPulse);
                return;
            }

            if (step == "Boot recovery" || step.StartsWith("Reboot"))
            {
                if (step == "Reboot to bootloader" && percent < 50)
                {
                    // Early travel: device is being brought INTO the bootloader.
                    SetFlashPhase("Active", "Pending", "Pending", "Pending", "WRITE", indeterminate: !noPulse);
                    return;
                }
                // Both the fastboot reboot after a partition flash and the
                // "Boot recovery" handoff light REBOOT — the write is done.
                SetFlashPhase("Done", "Done", "Done", "Active", "WRITE");
                return;
            }

            // Detect device / battery / lock / anti / wipe / slot all belong to CHECKS.
            SetFlashPhase("Active", "Pending", "Pending", "Pending", "WRITE");
        }


        // Theme and UI Features
        private string _logSearchTerm = string.Empty;
        private ObservableCollection<string> _filteredLogs;
        private ObservableCollection<string> _recentFiles;
        private ObservableCollection<RomFlashJob> _jobQueue;
        private bool _hasBatchJobs;
        private string _currentDeviceSerial = string.Empty;

        private string _logLevelFilter = "All";
        /// <summary>"All", "Errors" or "Warnings" — the severity chips on the LOGS tab.</summary>
        public string LogLevelFilter
        {
            get => _logLevelFilter;
            set
            {
                if (SetField(ref _logLevelFilter, value))
                {
                    OnPropertyChanged(nameof(LogEmptyMessage));
                    UpdateFilteredLogs();
                }
            }
        }

        private bool _followLogs = true;
        /// <summary>Keep the log scrolled to the newest line (L2).</summary>
        public bool FollowLogs
        {
            get => _followLogs;
            set => SetField(ref _followLogs, value);
        }

        public string LogSearchTerm
        {
            get => _logSearchTerm;
            set
            {
                if (SetField(ref _logSearchTerm, value))
                {
                    // The empty-state hint depends on the term, not just on
                    // whether any lines are visible.
                    OnPropertyChanged(nameof(LogEmptyMessage));
                    UpdateFilteredLogs();
                }
            }
        }

        /// <summary>
        /// Hint shown in the LOGS tab when the list is empty. Distinguishes
        /// "nothing logged yet" from "the search filter hid every line".
        /// </summary>
        public string LogEmptyMessage
        {
            get
            {
                var levelNote = _logLevelFilter == "All"
                    ? ""
                    : _logLevelFilter == "Errors" ? " with severity ERRORS" : " with severity WARNINGS";
                if (!string.IsNullOrWhiteSpace(_logSearchTerm))
                    return $"No log lines match \u201c{_logSearchTerm.Trim()}\u201d{levelNote}. Clear the log search or the severity filter to see everything again.";
                return "No log output yet — connect a device or select a ROM to see adb/fastboot activity here.";
            }
        }

        public ObservableCollection<string> FilteredLogs
        {
            get => _filteredLogs;
            set => SetField(ref _filteredLogs, value);
        }

        public ObservableCollection<string> RecentFiles
        {
            get => _recentFiles;
            set => SetField(ref _recentFiles, value);
        }

        public ObservableCollection<RomFlashJob> JobQueue
        {
            get => _jobQueue;
            set => SetField(ref _jobQueue, value);
        }

        public bool HasBatchJobs { get => _hasBatchJobs; private set => SetField(ref _hasBatchJobs, value); }

        /// <summary>
        /// Raw device serial as reported by adb/fastboot.
        /// Empty when no device is connected (meaning "any device" for tool commands).
        /// </summary>
        public string CurrentDeviceSerial
        {
            get => _currentDeviceSerial;
            private set
            {
                if (SetField(ref _currentDeviceSerial, value))
                {
                    OnPropertyChanged(nameof(CurrentDeviceSerialDisplay));
                    // FIX: the settings-page backup path is per-device; it was
                    // never notified, so it went stale as devices changed.
                    OnPropertyChanged(nameof(BackupDirDisplay));
                }
            }
        }

        /// <summary>
        /// Friendly display text for the toolbar (never empty).
        /// </summary>
        public string CurrentDeviceSerialDisplay =>
            string.IsNullOrWhiteSpace(_currentDeviceSerial) ? "No device" : _currentDeviceSerial;

        // Battery guard settings
        private int _batteryThreshold = 30;

        /// <summary>True when the reported battery reading is below the
        /// recommended threshold (an unknown battery never warns).</summary>
        public bool IsBatteryBelowRecommended =>
            DeviceBatteryBarVisibility == Visibility.Visible && DeviceBatteryLevel < _batteryThreshold;

        /// <summary>
        /// Flash-time battery guard: when the reading is below the recommended
        /// value, a popup informs the user and lets them charge first or
        /// continue at their own risk. Continuing sets a one-run bypass on
        /// FlashService so its pre-flight logs a warning instead of failing.
        /// Returns false when the user chooses to charge first (abort flash).
        /// </summary>
        public bool ConfirmLowBatteryBeforeFlash()
        {
            _flashService.AllowBelowMinBattery = false;   // every run re-prompts
            if (!IsBatteryBelowRecommended) return true;
            var ok = Dialogs.ConfirmDialog.Show(
                System.Windows.Application.Current?.MainWindow,
                "LOW BATTERY",
                $"The device battery is at {(int)DeviceBatteryLevel}% — below the recommended {_batteryThreshold}%.\n\n" +
                "If the phone loses power mid-flash, recovering it can be difficult.\n\n" +
                "Continue with the flash anyway?",
                "FLASH ANYWAY", "CHARGE FIRST", danger: true);
            if (!ok)
            {
                Log("[Battery] Flash cancelled at the low-battery warning — charge the device first.");
                return false;
            }
            _flashService.AllowBelowMinBattery = true;
            Log($"[Battery] Below the recommended {_batteryThreshold}% ({(int)DeviceBatteryLevel}%) — user chose to continue.");
            return true;
        }

        public int BatteryThreshold
        {
            get => _batteryThreshold;
            set
            {
                var clamped = Math.Max(0, Math.Min(90, value));
                if (SetField(ref _batteryThreshold, clamped))
                {
                    try
                    {
                        Properties.Settings.Default.BatteryThreshold = clamped;
                        Properties.Settings.Default.Save();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Failed to save battery threshold: {Message}", ex.Message);
                    }
                    _flashService.MinBatteryLevel = clamped;
                    OnPropertyChanged(nameof(DeviceBatteryBrush));
                    RefreshButtonStates();
                }
            }
        }

        public MainWindowViewModel()
        {
            _logger = LoggingService.Instance;
            _processManager = new ProcessManager();
            _romValidator = new RomValidator();
            _errorTranslator = new ErrorTranslator();
            _flashService = new FlashService();
            _flashService.MinBatteryLevel = _batteryThreshold;
            _deviceDetailsService = new DeviceDetailsService(_processManager);

            InitStepTrail();

            // Wire flash service progress and logging to the UI.
            _flashService.ProgressChanged += (s, e) =>
            {
                double percent = e.Percent;
                string text = null;
                // Payload session remap: the sideload stages occupy a fixed
                // slice of the whole install so the bar hits exactly 90% when
                // the ROM is applied and 95% when an add-on zip is applied.
                if (_payloadSessionActive)
                {
                    if (_payloadStage == 0 && e.Step != "Sideload")
                    {
                        // Stage 1: partition flashes occupy 0-15% of the whole
                        // install. Sideload events are EXCLUDED here — a raw
                        // device "1%" mapped into this slice would show ~1%
                        // instead of the slice start.
                        percent = e.Percent * 0.15;
                    }
                    else if (e.Step == "Sideload")
                    {
                        // Sideload events at ANY stage: the slice contract wins.
                        // Stage 0 (shouldn't happen) and stage 1 park/track on
                        // 15-90; stage 2 (add-on zip) tracks on 90-95.
                        var isRomStage = _payloadStage < 2;
                        var sliceBase = isRomStage ? 15.0 : 90.0;
                        var sliceWidth = isRomStage ? 0.75 : 0.05;
                        if (e.Percent <= 5)
                        {
                            // The fixed "Applying update via ADB sideload..."
                            // kickoff event — park the bar on the slice start;
                            // the ramp timer takes it from here.
                            percent = sliceBase;
                        }
                        else
                        {
                            // adb streamed the device's real install percentage:
                            // track the same 0-100 the recovery screen shows,
                            // mapped onto the stage slice (15-90 / 90-95).
                            _sideloadRampStreamedPct = e.Percent;
                            percent = sliceBase + e.Percent * sliceWidth;
                        }
                        // Bar and text must agree — and the status text carries
                        // no percentage: the phone's own screen shows the ROM's
                        // progress. Raw device values never leak into the text.
                        text = _payloadStage < 2
                            ? "SIDELOADING ROM…"
                            : "APPLYING ADD-ON ZIP…";
                    }
                    else if (_payloadStage >= 2)
                    {
                        percent = _payloadStage == 2 ? 90 : 95;
                        text = _payloadStage == 2
                            ? "ROM installed — waiting for system boot"
                            : "Add-on installed — waiting for system boot";
                    }
                }
                if (text == null)
                    text = string.IsNullOrWhiteSpace(e.Message)
                        ? $"{Math.Round(percent)}%"
                        : e.Message;
                if (!string.IsNullOrWhiteSpace(e.Partition))
                    text = $"[{e.Partition}] {text}";
                UpdateProgress(percent, text);
                UpdateFlashPhases((int)Math.Round(percent), e.Step, e.Partition, e.Message);
            };
            _flashService.LogMessage += (s, m) => Log(m);

            _cts = new CancellationTokenSource();

            // Keep the toolbar theme label in sync with ThemeManager.
            ThemeManager.Instance.ThemeChanged += (s, e) =>
            {
                UpdateThemeLabel();
                OnPropertyChanged(nameof(IsThemeDark));
                OnPropertyChanged(nameof(IsThemeLight));
                OnPropertyChanged(nameof(IsThemeOled));
                OnPropertyChanged(nameof(IsThemeAuto));
                OnPropertyChanged(nameof(AboutLogoPath));
            };
            UpdateThemeLabel();

            // Button states depend on the batch queue contents.
            BatchOperationManager.Instance.QueueChanged += (s, e) =>
            {
                HasBatchJobs = BatchOperationManager.Instance.JobQueue.Count > 0;
                RefreshButtonStates();
            };
            BatchOperationManager.Instance.JobStatusChanged += (s, e) => RefreshButtonStates();

            // Initialize the device card to the disconnected state.
            UpdateDeviceCard(new DeviceDetails { Mode = "Disconnected", BootloaderState = "Unknown", ActiveSlot = "Unknown" });

            LogMessages = new ObservableCollection<string>();
            _logBuffer = new List<string>();
            FilteredLogs = new ObservableCollection<string>();
            RecentFiles = new ObservableCollection<string>();
            RecentAdditional = new ObservableCollection<string>();
            BackupInventory = new System.Collections.ObjectModel.ObservableCollection<BackupInventoryItem>();
            RomLooseImages = new ObservableCollection<string>();
            DeviceDashboard = new ObservableCollection<DeviceInfoRow>();
            JobQueue = BatchOperationManager.Instance.JobQueue;
            HasBatchJobs = JobQueue.Count > 0;
            BatchOperationManager.Instance.JobQueue.CollectionChanged += (s, e) => HasBatchJobs = JobQueue.Count > 0;

            // Batch queue persistence: restore the previous session's queued jobs,
            // then keep the saved snapshot in sync as the queue changes.
            try
            {
                BatchOperationManager.Instance.RestoreQueue(Properties.Settings.Default.BatchQueue);
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to restore batch queue: {Message}", ex.Message);
            }
            BatchOperationManager.Instance.QueueChanged += (s, e) => SaveBatchQueue();
            BatchOperationManager.Instance.JobStatusChanged += (s, e) => SaveBatchQueue();

            // Initialize theme manager
            ThemeManager.Instance.ApplyTheme(ThemeManager.Instance.CurrentTheme);

            // Load recent files
            RefreshRecentFiles();
            RefreshRecentAdditional();

            _logger.Information("MainWindowViewModel initialized");

            // Restore the last used partition selection, then the view mode.
            WirePartitionOptions();
            LoadSavedPartitions();
            RefreshToolStatus();
            _isExpertMode = Properties.Settings.Default.ExpertMode;
            try { _batteryThreshold = Math.Max(0, Math.Min(90, Properties.Settings.Default.BatteryThreshold)); } catch { }
            try { _logRetentionDays = Math.Max(7, Math.Min(365, Properties.Settings.Default.LogRetentionDays)); } catch { }
            try { Utilities.Motion.ReduceMotion = Properties.Settings.Default.ReduceMotion; } catch { }
            // FIX: sync the persisted battery gate into FlashService — before this,
            // a saved threshold above 30% was silently ignored until the user
            // touched the slider again.
            _flashService.MinBatteryLevel = _batteryThreshold;

            // Remove leftover payload extraction folders from previous runs.
            _flashService.CleanTempExtractions();

            // Setup device monitor timer
            _deviceMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _deviceMonitorTimer.Tick += async (s, e) => await MonitorDeviceStateAsync();
        }

        /// <summary>
        /// Start monitoring device connection state.
        /// </summary>
        public void StartDeviceMonitoring()
        {
            _logger.Information("Starting device monitoring");
            _deviceMonitorTimer.Start();
        }

        /// <summary>
        /// Stop monitoring device connection state.
        /// </summary>
        public void StopDeviceMonitoring()
        {
            _logger.Information("Stopping device monitoring");
            _deviceMonitorTimer.Stop();
        }

        /// <summary>
        /// Toolbar label reflecting the current theme mode.
        /// </summary>
        public string ThemeLabel
        {
            get => _themeLabel;
            private set => SetField(ref _themeLabel, value);
        }

        private string _themeLabel = "Theme: Dark";

        private void UpdateThemeLabel()
        {
            var mode = ThemeManager.Instance.CurrentTheme;
            ThemeLabel = mode == ThemeManager.ThemeMode.Auto ? "Theme: Auto"
                : mode == ThemeManager.ThemeMode.Light ? "Theme: Light"
                : mode == ThemeManager.ThemeMode.Oled ? "Theme: OLED"
                : "Theme: Dark";
            UpdateWelcomeLogo();
        }

        // The welcome page's brand logo: the "on dark" art for Dark/OLED/Auto,
        // the "on light" art for Light. Rebuilt whenever the theme changes.
        public ImageSource WelcomeLogoImage
        {
            get => _welcomeLogoImage;
            private set => SetField(ref _welcomeLogoImage, value);
        }
        private ImageSource _welcomeLogoImage;

        private void UpdateWelcomeLogo()
        {
            var light = ThemeManager.Instance.EffectiveTheme == ThemeManager.ThemeMode.Light;
            var name = light ? "flasher-logo-on-light-256.png" : "flasher-logo-on-dark-256.png";
            try
            {
                WelcomeLogoImage = BitmapFrame.Create(new Uri(
                    "pack://application:,,,/Assets/" + name, UriKind.Absolute));
            }
            catch (Exception ex)
            {
                _logger.Debug("Welcome logo load failed: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Persist the current queued (pending) batch jobs to user settings.
        /// </summary>
        public void SaveBatchQueue()
        {
            try
            {
                Properties.Settings.Default.BatchQueue = BatchOperationManager.Instance.SerializeQueue();
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to save batch queue: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Load an optional brand logo PNG from a "Logos" folder next to the exe
        /// (e.g. Logos\Google.png). Returns null when absent — the letter badge is
        /// used as fallback.
        /// </summary>
        private ImageSource LoadBrandLogo(string brand)
        {
            if (string.IsNullOrWhiteSpace(brand)) return null;
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logos", $"{brand.Trim()}.png");
                if (!File.Exists(path)) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load brand logo for '{Brand}': {Message}", brand, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Force an immediate device state refresh (F5).
        /// </summary>
        public async Task RefreshDeviceStateAsync()
        {
            if (IsFlashing || _isDetectingMode)
                return;

            await MonitorDeviceStateAsync();
        }

        private async Task MonitorDeviceStateAsync()
        {
            if (IsFlashing || _isDetectingMode)
                return;

            _isDetectingMode = true;
            try
            {
                var (detectedMode, detectedSerial) = await DetectConnectedDeviceAsync();

                if (detectedSerial != _currentDeviceSerial)
                {
                    CurrentDeviceSerial = detectedSerial;
                }

                // One service call gathers identity, versions, lock state, slot,
                // and battery (merging with cached values per serial).
                var details = await _deviceDetailsService.GetDetailsAsync(
                    detectedSerial, detectedMode.ToString(), _cts?.Token ?? CancellationToken.None);

                var previousState = $"{_currentPhoneMode}|{_currentActiveSlot}|{_currentLockStatus}";
                _currentPhoneMode = detectedMode;
                _currentActiveSlot = string.IsNullOrWhiteSpace(details.ActiveSlot) ? "Unknown" : details.ActiveSlot;
                _currentLockStatus = string.IsNullOrWhiteSpace(details.BootloaderState) ? "Unknown" : details.BootloaderState;
                var currentState = $"{_currentPhoneMode}|{_currentActiveSlot}|{_currentLockStatus}";

                if (previousState != currentState)
                {
                    var serialSuffix = string.IsNullOrWhiteSpace(detectedSerial)
                        ? string.Empty
                        : $" ({detectedSerial})";
                    Log($"[Monitor] Device state changed to: {_currentPhoneMode}{serialSuffix}");
                    if (_currentPhoneMode == DeviceMode.Unauthorized)
                        Log("[Monitor] The phone is connected but not authorized — accept the USB debugging prompt on its screen (the new ROM starts with no trusted hosts).");
                    if (_currentPhoneMode == DeviceMode.Edl)
                        Log("[Monitor] The phone is in EMERGENCY DOWNLOAD mode (Qualcomm 9008) — this is the hard-brick state. " +
                            "SR Flasher cannot talk to it from here; recovery requires an authorized EDL flasher (e.g. MiFlash with the right credentials) or manufacturer service. " +
                            "Nothing was lost by trying — the device is simply not flashable in this state.");
                }

                // P2: identify a booted custom recovery once per recovery
                // session (TWRP/OrangeFox vs stock, CLI, mounts, /tmp space).
                if (detectedMode == DeviceMode.AdbRecovery)
                {
                    if (!_recoveryProbeDone && !IsFlashing && !string.IsNullOrWhiteSpace(detectedSerial))
                        await ProbeRecoveryCapabilitiesAsync(detectedSerial);

                    // A deferred write verification from the recovery repair —
                    // run it now that the device offers a recovery shell.
                    if (!string.IsNullOrWhiteSpace(_pendingRecoveryVerifyHash) &&
                        !IsFlashing && !string.IsNullOrWhiteSpace(detectedSerial))
                    {
                        var pendingHash = _pendingRecoveryVerifyHash;
                        var pendingPartition = _pendingRecoveryVerifyPartition;
                        _pendingRecoveryVerifyHash = null;
                        _pendingRecoveryVerifyPartition = null;
                        await VerifyRecoveryReadbackViaAdbAsync(pendingPartition, pendingHash);
                    }
                }
                else if (_recoveryProbeDone)
                {
                    ResetRecoveryProbe();
                }

                // Payload session stage 2 auto-resume: whenever the device
                // shows up in sideload while the session is waiting (stage 1),
                // apply the ROM — no matter how long the user took on the
                // recovery screen. The ROM tab stays the home of the flow.
                if (_payloadSessionActive && _payloadStage == 1 && !IsFlashing &&
                    !_payloadAutoApplyRunning &&
                    detectedMode == DeviceMode.Sideload &&
                    !string.IsNullOrWhiteSpace(SelectedFilePath) && File.Exists(SelectedFilePath))
                {
                    _payloadAutoApplyRunning = true;
                    Log("[Plan] Stage 2 — sideload detected by the monitor; installing the ROM package...");
                    RaiseSideloadGuidanceClosed();
                    _ = AutoApplyPayloadStage2Async();
                }

                // Stage 2 follow-up: the ROM's own updater can send the device
                // back to recovery (e.g. the user chose "flash additional
                // files"). Move the app to the ADDITIONAL FILES tab; when the
                // device instead reboots to system, the 100% branch below ends
                // the session.
                // Stage 1 + a failed sideload link + the device booting to
                // system: the package may have applied anyway (adb often
                // errors out the moment the device reboots). Ask the user.
                if (_payloadSessionActive && _payloadStage == 1 && _sideloadAttemptFailed &&
                    detectedMode == DeviceMode.AdbSystem && !string.IsNullOrWhiteSpace(CurrentDeviceSerial))
                {
                    _sideloadAttemptFailed = false;
                    Log("[Plan] The sideload link dropped, but the device has rebooted to system - checking whether the new ROM is running...");
                    var currentFingerprint = await GetDeviceFingerprintAsync(CurrentDeviceSerial);
                    var currentSlot = await GetDeviceSlotAsync(CurrentDeviceSerial);
                    var fingerprintChanged =
                        !string.IsNullOrWhiteSpace(currentFingerprint) &&
                        !string.IsNullOrWhiteSpace(_preFlashFingerprint) &&
                        !string.Equals(currentFingerprint.Trim(), _preFlashFingerprint.Trim(),
                            StringComparison.OrdinalIgnoreCase);
                    // A payload OTA switches the active slot even when
                    // re-flashing the SAME ROM, so the slot covers the case a
                    // fingerprint comparison misses.
                    var slotSwitched =
                        !string.IsNullOrWhiteSpace(currentSlot) &&
                        !string.IsNullOrWhiteSpace(_preFlashSlot) &&
                        !string.Equals(currentSlot.Trim(), _preFlashSlot.Trim(),
                            StringComparison.OrdinalIgnoreCase);
                    var newRomRunning = fingerprintChanged || slotSwitched;
                    if (newRomRunning)
                    {
                        Log(newRomRunning
                            ? "[Plan] " +
                              (fingerprintChanged
                                  ? "Build fingerprint changed"
                                  : "Active slot switched (" + _preFlashSlot.Trim() + " -> " + currentSlot.Trim() + ")") +
                              " — the new ROM is running. Completing the install (100%)."
                            : "[Plan] The selected SYSTEM destination has booted after sideload. Completing the install (100%).");

                        if (RecoveryBackupExists)
                            Log("[Plan] Reminder: the ROM's payload replaced any custom recovery. " +
                                "The stock-recovery backup from " + _recoveryBackupStamp + " is on the PC if you need the old recovery back.");
                        _lastFlashFingerprint = currentFingerprint?.Trim();
                        SaveSessionReport();
                        Log("[Root] To root this ROM: install the Magisk app on the phone, let it patch the NEW boot image " +
                            "(Magisk \u2192 Install \u2192 Select and Patch a File), then drop magisk_patched-*.img " +
                            "on ADDITIONAL FILES and FLASH IMAGE \u2014 it maps to boot.");
                        UpdateProgress(100, "System is up — install complete");
                            RomStepText = "STEP 4: Install complete — the ROM is installed and the system booted (100%).";
                            Journal("System booted — payload install complete (100%, sideload link had dropped).");
                            ShowToast("Success", "Install complete",
                                "The ROM is installed and the system is up.");
                            EndPayloadSession();
                            RefreshButtonStates();
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                Dialogs.ConfirmDialog.Show(
                                    System.Windows.Application.Current?.MainWindow,
                                    "INSTALL COMPLETE",
                                    "INSTALL COMPLETE" + "\n" + "\n" + "The ROM is installed and the system booted successfully.",
                                    "OK", null, danger: false);
                            }));
                        }
                        else
                        {
                            Log("[Plan] The build fingerprint is unchanged - the new ROM did not install. The session stays open; press INSTALL ROM to re-run.");
                            RomStepText = "STEP 3: The install did not finish. Press INSTALL ROM to re-run it " +
                                "(stage 1 re-flashes harmlessly and the package installs again).";
                            ShowToast("Install unfinished", "The ROM did not apply",
                                "The system booted the previous ROM. Press INSTALL ROM to re-run the install.");
                        }
                    }

                if (_payloadSessionActive && _payloadStage >= 2 &&
                    detectedMode == DeviceMode.AdbRecovery && !_additionalFilesSwitched)
                {
                    _additionalFilesSwitched = true;
                    Log("[Plan] Device returned to recovery after the ROM — switching to the ADDITIONAL FILES tab.");
                    RomStepText = "STEP 4: ROM installed — the device is in recovery. Flash GAPPS or other add-ons on the " +
                        "ADDITIONAL FILES tab (progress reaches 95%). The install completes at 100% once the system boots.";
                    SelectedTabIndex = 1;
                    RefreshButtonStates();
                }

                // Payload session final stage: the system actually booted — 100%.
                if (_payloadSessionActive && _payloadStage >= 2 &&
                    detectedMode == DeviceMode.AdbSystem)
                {
                    Log("[Plan] The system is up — install complete (100%).");
                    _sideloadAttemptFailed = false;
                    var finalFingerprint = await GetDeviceFingerprintAsync(CurrentDeviceSerial);
                    _lastFlashFingerprint = finalFingerprint?.Trim();
                    SaveSessionReport();
                    UpdateProgress(100, "System is up — install complete");
                    RomStepText = "STEP 4: Install complete — the ROM is installed and the system booted (100%).";
                    Journal("System booted — payload install complete (100%).");
                    ShowToast("Success", "Install complete",
                        "The ROM is installed and the system is up.");
                    EndPayloadSession();
                    RefreshButtonStates();
                    var popupText = "INSTALL COMPLETE\nThe ROM is installed and the system booted successfully.";
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        new Action(() => Dialogs.ConfirmDialog.Show(
                            System.Windows.Application.Current?.MainWindow,
                            "INSTALL COMPLETE", popupText, "OK", null, danger: false)));
                }

                bool sessionBusy = _romInstallWaitingRecovery || _payloadSessionActive;
                if (sessionBusy && _lastConnectedDetails != null &&
                    (string.IsNullOrWhiteSpace(details.Mode) || details.Mode == "Disconnected"))
                {
                    // The flash session is driving the phone: the stage-1 →
                    // recovery reboot and the post-add-on reboot make the device
                    // vanish from adb/fastboot for a minute. Hold the last known
                    // identity instead of demoting the card to "Disconnected" —
                    // the install is still running.
                    if (!_deviceHoldLogged)
                    {
                        _deviceHoldLogged = true;
                        Log("[Monitor] The phone left adb/fastboot view mid-install — expected while it reboots; holding the last known card state until it returns.");
                    }
                    var held = _lastConnectedDetails;
                    held.Mode = "Flash running";
                    UpdateDeviceCard(held);
                }
                else
                {
                    if (!sessionBusy)
                        _deviceHoldLogged = false;
                    UpdateDeviceCard(details);
                }
                RefreshButtonStates();
            }
            catch (Exception ex)
            {
                _logger.Error("Error monitoring device state: {Message}", ex.Message);
            }
            finally
            {
                _isDetectingMode = false;
            }
        }

        private async Task<(DeviceMode Mode, string Serial)> DetectConnectedDeviceAsync()
        {
            try
            {
                var adbOutput = await _processManager.GetOutputAsync(
                    ToolPathResolver.GetToolPath("adb"),
                    "devices",
                    timeout: TimeSpan.FromSeconds(5));

                // adb devices prints a header line, then "serial\tstate" rows.
                if (!string.IsNullOrWhiteSpace(adbOutput))
                {
                    foreach (var line in adbOutput.Split(new[] { "\r\n", "\r", "\n" },
                                 StringSplitOptions.RemoveEmptyEntries).Skip(1))
                    {
                        var parts = line.Trim().Split(new[] { '\t' }, 2);
                        if (parts.Length != 2)
                            continue;
                        var serial = parts[0].Trim();
                        var state = parts[1].Trim();
                        if (state == "sideload") return (DeviceMode.Sideload, serial);
                        if (state == "device") return (DeviceMode.AdbSystem, serial);
                        if (state == "recovery") return (DeviceMode.AdbRecovery, serial);
                        // A freshly installed ROM starts with no authorized
                        // hosts: adb reports "unauthorized" until the RSA
                        // prompt is accepted on the phone. This must NOT read
                        // as Disconnected — the user would think the cable
                        // came loose during the most fragile moment.
                        if (state == "unauthorized") return (DeviceMode.Unauthorized, serial);
                    }
                }

                var fastbootOutput = await _processManager.GetOutputAsync(
                    ToolPathResolver.GetToolPath("fastboot"),
                    "devices",
                    timeout: TimeSpan.FromSeconds(5));

                // fastboot devices prints "serial\tfastboot" rows (no header).
                if (!string.IsNullOrWhiteSpace(fastbootOutput))
                {
                    foreach (var line in fastbootOutput.Split(new[] { "\r\n", "\r", "\n" },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Trim().Split(new[] { '\t' }, 2);
                        if (parts.Length != 2 || parts[1].Trim() != "fastboot")
                            continue;
                        var serial = parts[0].Trim();

                        // Distinguish fastbootd (userspace) from the bootloader.
                        var userspace = await GetCombinedToolOutputAsync(
                            ToolPathResolver.GetToolPath("fastboot"),
                            $"-s \"{serial}\" getvar is-userspace",
                            TimeSpan.FromSeconds(5));
                        if (userspace != null &&
                            userspace.ToLowerInvariant().Contains("yes"))
                            return (DeviceMode.FastbootD, serial);

                        return (DeviceMode.Fastboot, serial);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Error detecting device mode: {Message}", ex.Message);
            }

            // Nothing on adb or fastboot — before declaring Disconnected,
            // check for a Qualcomm Emergency Download device (VID 05C6 PID
            // 9008): a hard-bricked phone looks exactly like "cable came
            // loose" unless we name it. Throttled — pnputil is cheap but
            // not free, and this only runs while nothing else is connected.
            if ((DateTime.Now - _lastEdlProbe).TotalSeconds >= 8)
            {
                _lastEdlProbe = DateTime.Now;
                try
                {
                    var pnp = await GetCombinedToolOutputAsync("pnputil", "/enum-devices /connected",
                        TimeSpan.FromSeconds(4));
                    if (!string.IsNullOrWhiteSpace(pnp) &&
                        pnp.ToUpperInvariant().Contains("VID_05C6&PID_9008"))
                        return (DeviceMode.Edl, "9008");
                }
                catch { }
            }

            return (DeviceMode.Disconnected, string.Empty);
        }

        /// <summary>
        /// Run a tool and return the combined stdout + stderr.
        /// fastboot writes getvar results and errors to stderr, so both
        /// streams must be considered.
        /// </summary>
        private async Task<string> GetCombinedToolOutputAsync(string tool, string args, TimeSpan timeout,
            CancellationToken ct = default)
        {
            var result = await _processManager.ExecuteAsync(tool, args, timeout, ct);
            if (result.TimedOut)
                return "[TIMEOUT]";

            var sb = new StringBuilder();
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

        // ==================== Settings page (gear icon) ====================
        // One page for everything that is a preference rather than an
        // action. Values apply immediately (no Save button) — they persist
        // the same way the theme and expert mode always have.

        /// <summary>Sets the theme from the settings page chips.</summary>
        public void SetTheme(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "dark": ThemeManager.Instance.CurrentTheme = ThemeManager.ThemeMode.Dark; break;
                case "light": ThemeManager.Instance.CurrentTheme = ThemeManager.ThemeMode.Light; break;
                case "oled": ThemeManager.Instance.CurrentTheme = ThemeManager.ThemeMode.Oled; break;
                case "auto": ThemeManager.Instance.CurrentTheme = ThemeManager.ThemeMode.Auto; break;
            }
        }

        // Getters reflect the live theme (refreshed on ThemeChanged); setters
        // route into SetTheme so the TwoWay IsChecked bindings on the settings
        // chips are legal — a read-only TwoWay source crashes at startup.
        public bool IsThemeDark
        {
            get => ThemeManager.Instance.CurrentTheme == ThemeManager.ThemeMode.Dark;
            set { if (value) SetTheme("Dark"); }
        }
        public bool IsThemeLight
        {
            get => ThemeManager.Instance.CurrentTheme == ThemeManager.ThemeMode.Light;
            set { if (value) SetTheme("Light"); }
        }
        public bool IsThemeOled
        {
            get => ThemeManager.Instance.CurrentTheme == ThemeManager.ThemeMode.Oled;
            set { if (value) SetTheme("Oled"); }
        }
        public bool IsThemeAuto
        {
            get => ThemeManager.Instance.CurrentTheme == ThemeManager.ThemeMode.Auto;
            set { if (value) SetTheme("Auto"); }
        }

        // Log retention: how many days of daily app-*.log files to keep.
        private int _logRetentionDays = 30;
        public int LogRetentionDays
        {
            get => _logRetentionDays;
            set
            {
                var clamped = Math.Max(7, Math.Min(365, value));
                if (!SetField(ref _logRetentionDays, clamped)) return;
                try
                {
                    Properties.Settings.Default.LogRetentionDays = clamped;
                    Properties.Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to save log retention: {Message}", ex.Message);
                }
                LoggingService.Instance.PruneOldLogs(clamped);
            }
        }

        public string BackupDirDisplay => BackupDirForSerial(CurrentDeviceSerial);

        /// <summary>Theme-aware logo for the About card (dark vs light variant).</summary>
        public string AboutLogoPath
        {
            get
            {
                var dark = ThemeManager.Instance.CurrentTheme != ThemeManager.ThemeMode.Light;
                return dark ? "Assets/flasher-logo-on-dark-256.png" : "Assets/flasher-logo-on-light-256.png";
            }
        }

        // Reduce motion (A8): disables the code-driven entrances/pops. Style
        // pulses stay (they only run mid-flash); Windows' own "animate
        // controls" setting is honoured regardless.
        public bool ReduceMotion
        {
            get => Utilities.Motion.ReduceMotion;
            set
            {
                if (Utilities.Motion.ReduceMotion == value) return;
                Utilities.Motion.ReduceMotion = value;
                try
                {
                    Properties.Settings.Default.ReduceMotion = value;
                    Properties.Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to save reduce-motion: {Message}", ex.Message);
                }
            }
        }

        public string AppVersionDisplay
        {
            get
            {
                try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
                catch { return "(unknown)"; }
            }
        }

        // ==================== Update checker (GitHub releases) ====================
        // Automatic check at most once a day (Settings.CheckUpdates), silent on
        // failure; the About card on the SETTINGS tab always offers CHECK NOW.
        // Privacy: the check is a plain GET to api.github.com — no device data,
        // no serials, no logs ever leave the machine.
        private string _updateStatusText;
        private bool _updateAvailable;
        private string _updateReleaseUrl;

        /// <summary>Auto-check toggle, persisted with the other user settings.</summary>
        public bool CheckUpdates
        {
            get { return Properties.Settings.Default.CheckUpdates; }
            set
            {
                if (Properties.Settings.Default.CheckUpdates == value) return;
                Properties.Settings.Default.CheckUpdates = value;
                try { Properties.Settings.Default.Save(); }
                catch (Exception ex) { _logger.Debug("Failed to save update-check setting: {Message}", ex.Message); }
                OnPropertyChanged(nameof(CheckUpdates));
            }
        }

        public string UpdateStatusText
        {
            get { return _updateStatusText; }
            private set { _updateStatusText = value; OnPropertyChanged(nameof(UpdateStatusText)); }
        }

        /// <summary>True only when a NEWER release was found — drives the
        /// OPEN RELEASES PAGE button in the About card.</summary>
        public bool UpdateAvailable
        {
            get { return _updateAvailable; }
            private set { _updateAvailable = value; OnPropertyChanged(nameof(UpdateAvailable)); OnPropertyChanged(nameof(UpdateAvailableVisibility)); }
        }

        public Visibility UpdateAvailableVisibility => _updateAvailable ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Run the GitHub release check. Manual runs always go online and
        /// surface failures in the status line; automatic runs are throttled
        /// to once per 24 h and stay silent when offline.
        /// </summary>
        public async System.Threading.Tasks.Task CheckForUpdatesAsync(bool manual)
        {
            if (!manual && !Properties.Settings.Default.CheckUpdates)
            {
                UpdateStatusText = "Automatic update checks are off — use CHECK FOR UPDATES to look manually.";
                return;
            }

            // Throttle: the automatic check runs at most once a day.
            if (!manual)
            {
                var last = Properties.Settings.Default.LastUpdateCheck;
                DateTime lastTime;
                if (DateTime.TryParse(last, out lastTime) &&
                    (DateTime.Now - lastTime).TotalHours < 24)
                {
                    return; // checked recently — nothing to do
                }
            }

            UpdateStatusText = "Checking for updates…";
            var info = await Services.UpdateChecker.FetchLatestAsync(System.Threading.CancellationToken.None).ConfigureAwait(true);
            if (info == null)
            {
                UpdateStatusText = manual
                    ? "Update check failed — no connection? The releases page is always up to date on GitHub."
                    : "Update check skipped (offline).";
                return;
            }

            try
            {
                Properties.Settings.Default.LastUpdateCheck = DateTime.Now.ToString("o");
                Properties.Settings.Default.Save();
            }
            catch (Exception ex) { _logger.Debug("Failed to save update-check stamp: {Message}", ex.Message); }

            if (Services.UpdateChecker.IsNewerVersion(info.LatestTag, AppVersionDisplay))
            {
                _updateReleaseUrl = string.IsNullOrEmpty(info.ReleaseUrl)
                    ? Services.UpdateChecker.ReleasesUrl : info.ReleaseUrl;
                UpdateAvailable = true;
                UpdateStatusText = "Update available: " + info.LatestTag + " — you are running v" + AppVersionDisplay + ".";
                Log("[Update] New release available: " + info.LatestTag + " — " + _updateReleaseUrl);
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatusText = "Up to date — v" + AppVersionDisplay + " is the latest release.";
            }
        }

        /// <summary>Open the GitHub releases page in the browser (update row button).</summary>
        public void OpenReleasesPage()
        {
            var url = _updateAvailable && !string.IsNullOrEmpty(_updateReleaseUrl)
                ? _updateReleaseUrl : Services.UpdateChecker.ReleasesUrl;
            try
            {
                System.Diagnostics.Process.Start(url);
                Log("[Update] Opened releases page: " + url);
            }
            catch (Exception ex)
            {
                Log("[Update] Could not open the browser: " + ex.Message + " — the page is at " + url);
                ShowToast("Info", "Open in browser failed", "The releases page is at github.com/" + Services.UpdateChecker.RepoOwner + "/" + Services.UpdateChecker.RepoName + "/releases");
            }
        }

        // ==================== 4.4: loose images inside the ROM zip ====================
        // Some packages (factory images, boot-chain zips, some custom ROMs)
        // ship images as loose files beside payload.bin — those are exactly
        // what Magisk needs to patch. Payload-only zips (RR-style) get an
        // honest "use a payload dumper" note instead of pretending.
        public ObservableCollection<string> RomLooseImages { get; private set; }

        public bool RomHasLooseBoot => RomLooseImages != null &&
            RomLooseImages.Any(n => n.EndsWith("/boot.img", StringComparison.OrdinalIgnoreCase) ||
                                    n.Equals("boot.img", StringComparison.OrdinalIgnoreCase));
        public Visibility RomLooseBootVisibility => RomHasLooseBoot
            ? Visibility.Visible : Visibility.Collapsed;
        public string RomLooseImagesText =>
            RomLooseImages == null || RomLooseImages.Count == 0
                ? string.Empty
                : "This package ships loose images: " + string.Join(", ", RomLooseImages.Select(n => System.IO.Path.GetFileName(n)));

        private async System.Threading.Tasks.Task ScanRomLooseImagesAsync(string romPath)
        {
            try
            {
                var found = await System.Threading.Tasks.Task.Run(() =>
                {
                    var names = new List<string>();
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(romPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            var n = (entry.FullName ?? "").Replace('\\', '/');
                            if (!n.Contains('/')) // top-level only
                                names.Add(n);
                        }
                    }
                    return names;
                });
                RomLooseImages = new ObservableCollection<string>(found
                    .Where(n => n.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n)
                    .ToList());
            }
            catch (Exception ex)
            {
                _logger.Debug("Loose-image scan failed: {Message}", ex.Message);
                RomLooseImages = new ObservableCollection<string>();
            }
            OnPropertyChanged(nameof(RomHasLooseBoot));
            OnPropertyChanged(nameof(RomLooseBootVisibility));
            OnPropertyChanged(nameof(RomLooseImagesText));
        }

        /// <summary>
        /// Extracts the rootable boot-chain images (boot, init_boot,
        /// vendor_boot) from the selected package to Documents\SRFlasher        /// Extracted\&lt;rom&gt; — ready to patch in the Magisk app.
        /// </summary>
        public async System.Threading.Tasks.Task ExtractBootImagesAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath)) return;
            var romPath = SelectedFilePath;
            var outDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SRFlasher", "Extracted",
                System.IO.Path.GetFileNameWithoutExtension(romPath));
            Directory.CreateDirectory(outDir);
            IsFlashing = true;
            try
            {
                var extracted = await System.Threading.Tasks.Task.Run(() =>
                {
                    var list = new List<string>();
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(romPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            var n = (entry.FullName ?? "").Replace('\\', '/');
                            if (n.Contains('/')) continue;
                            var baseName = System.IO.Path.GetFileName(n).ToLowerInvariant();
                            if (baseName != "boot.img" && baseName != "init_boot.img" && baseName != "vendor_boot.img")
                                continue;
                            var target = System.IO.Path.Combine(outDir, baseName);
                            using (var inStream = entry.Open())
                            using (var outStream = File.Create(target))
                                inStream.CopyTo(outStream);
                            list.Add(baseName);
                        }
                    }
                    return list;
                });
                if (extracted.Count == 0)
                {
                    Log("[Extract] This package carries no loose boot image — the boot image lives inside payload.bin. " +
                        "Extract it with a payload dumper, patch THAT file in the Magisk app, then drop the result here.");
                    ShowToast("Info", "Nothing to extract",
                        "The boot image is inside payload.bin — use a payload dumper, then patch the extracted boot.img.");
                    return;
                }
                Log("[Extract] Extracted to " + outDir + ": " + string.Join(", ", extracted));
                Log("[Root] Next: install the Magisk app on the phone, patch the extracted boot image " +
                    "(Magisk -> Install -> Select and Patch a File), then drop magisk_patched-*.img on ADDITIONAL FILES.");
                ShowToast("Success", "Boot image extracted",
                    "Saved to " + outDir + " — patch it in the Magisk app, then flash the patched image.");
            }
            catch (Exception ex)
            {
                Log("[Extract] Extraction failed: " + ex.Message);
                ShowToast("Error", "Extraction failed", ex.Message);
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        // ==================== 3.3: session report ====================
        private string _sessionRomFile;
        private readonly List<string> _sessionBackups = new List<string>();
        private string _lastSessionReport;

        private void NoteSessionBackup(string description)
        {
            if (!string.IsNullOrWhiteSpace(description)) _sessionBackups.Add(description);
        }

        /// <summary>
        /// 3.2: one pasteable support bundle — app version, OS, device state,
        /// probe summary, backup inventory, last session report and the most
        /// recent log lines.
        /// </summary>
        public string BuildDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SR Flasher diagnostics ===");
            try
            {
                sb.AppendLine("App version: " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            }
            catch { }
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString +
                (Environment.Is64BitOperatingSystem ? " (64-bit)" : ""));
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("--- Device ---");
            sb.AppendLine("Mode: " + DeviceModeDisplay + "    Serial: " + CurrentDeviceSerial);
            sb.AppendLine("Active slot: " + _currentActiveSlot + "    Bootloader: " + _currentLockStatus);
            sb.AppendLine("Recovery probe: " + (string.IsNullOrWhiteSpace(RecoveryProbeSummary) ? "(n/a)" : RecoveryProbeSummary));
            sb.AppendLine();
            sb.AppendLine("--- Backups ---");
            sb.AppendLine("PC backup dir: " + BackupDirForSerial(CurrentDeviceSerial) +
                (RecoveryBackupExists ? "  (latest set: " + _recoveryBackupStamp + ")" : "  (none)"));
            if (_lastSessionReport != null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Last session report ---");
                sb.Append(_lastSessionReport);
            }
            sb.AppendLine();
            sb.AppendLine("--- Log tail (last 200 lines) ---");
            try
            {
                var tail = FilteredLogs.Skip(Math.Max(0, FilteredLogs.Count - 200));
                foreach (var line in tail) sb.AppendLine(line);
            }
            catch (Exception ex)
            {
                sb.AppendLine("(log tail unavailable: " + ex.Message + ")");
            }
            return sb.ToString();
        }

        private string BuildSessionReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SR Flasher — install session report");
            sb.AppendLine("Finished: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("ROM: " + (_sessionRomFile ?? "(unknown)"));
            sb.AppendLine("Device serial: " + CurrentDeviceSerial);
            sb.AppendLine("Mode: " + DeviceModeDisplay + "    Slot: " + _currentActiveSlot);
            if (!string.IsNullOrWhiteSpace(_preFlashFingerprint))
                sb.AppendLine("Fingerprint before: " + _preFlashFingerprint.Trim());
            if (!string.IsNullOrWhiteSpace(_lastFlashFingerprint))
                sb.AppendLine("Fingerprint after:  " + _lastFlashFingerprint.Trim());
            if (_flashStopwatch != null)
                sb.AppendLine("Duration: " + _flashStopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
            sb.AppendLine("Backups taken this session: " +
                (_sessionBackups.Count == 0 ? "(none)" : ""));
            foreach (var b in _sessionBackups) sb.AppendLine("  - " + b);
            sb.AppendLine();
            sb.AppendLine("Stock recovery backups on the PC live under " + BackupDirForSerial(CurrentDeviceSerial));
            return sb.ToString();
        }

        private void SaveSessionReport()
        {
            try
            {
                var report = BuildSessionReport();
                _lastSessionReport = report;
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SRFlasher", "Reports");
                Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(file, report);
                Log("[Report] Session report saved: " + file);
            }
            catch (Exception ex)
            {
                Log("[Report] Could not save the session report: " + ex.Message);
            }
        }

        // Live abort token for CLI-side operations (twrp CLI, dd backups,
        // rescue erases). AbortFlash cancels + recreates the source.
        private CancellationToken AbortToken => _cts?.Token ?? CancellationToken.None;

        /// <summary>
        /// Validate and select a ROM file.
        /// </summary>
        public async Task<bool> ValidateAndSelectRomAsync(string filePath)
        {
            try
            {
                _logger.Information("Validating ROM file: {FilePath}", filePath);
                var validationResult = await _romValidator.ValidateAsync(filePath);

                if (!validationResult.IsValid)
                {
                    var errorMsg = string.Join("\n", validationResult.Errors);
                    Log($"[ERROR] ROM Validation Failed:\n{errorMsg}");
                    _logger.Error("ROM validation failed: {@Errors}", validationResult.Errors);
                    return false;
                }

                if (validationResult.Warnings.Length > 0)
                {
                    Log($"[WARNING] {string.Join("\n", validationResult.Warnings)}");
                }

                SelectedFilePath = filePath;
                await ScanRomLooseImagesAsync(filePath);

                _romDeviceIdentifiers = validationResult.DeviceIdentifiers ?? new string[0];
                _romPlanPartitions = validationResult.PlanPartitions ?? new string[0];
                _romKind = validationResult.Kind;
                _romInstallMethod = validationResult.InstallMethod;
                _romKindDisplay = validationResult.KindDisplay ?? string.Empty;
                _romPlanNote = validationResult.PlanNote ?? string.Empty;
                RaiseRomGate();
                RefreshButtonStates();

                if (_romDeviceIdentifiers.Length > 0)
                {
                    Log($"[✓] Package targets {RomDeviceTargets} (from {validationResult.DeviceIdentifierSource})");
                }
                else
                {
                    Log("[WARNING] Package does not declare a device — verify it is built for this phone before flashing.");
                }

                // What the package is decides everything that follows, so state it
                // plainly — this is the line that used to be missing.
                Log($"[✓] Package type: {_romKindDisplay}");
                Log($"[Plan] {_romPlanNote}");
                if (_romPlanPartitions.Length > 0)
                    if (RomIsPayloadOta)
                        Log($"[Plan] Stage 1 requests the boot chain: {string.Join(", ", PayloadBootChain)} — " +
                            "only the images the payload actually carries are flashed.");
                    else if (_romPlanPartitions.Length > 0)
                        Log($"[Plan] Partitions this package can write over fastboot: {string.Join(", ", _romPlanPartitions)}");

                if (RomIsPayloadOta)
                {
                    Log("[Plan] Fastboot cannot write this package's images directly. Press INSTALL ROM — " +
                        "the boot chain is flashed first, then the package installs itself from recovery.");
                }
                else if (RomNeedsSideload)
                {
                    Log("[Plan] Fastboot cannot install this package. Use INSTALL FROM RECOVERY (SIDELOAD).");
                }
                else if (_romInstallMethod == RomInstallMethod.Unsupported)
                {
                    Log("[Plan] This package cannot be flashed by this app. Expert mode can still write its boot chain, if you know it is safe.");
                }

                var fsize = validationResult.FileInfo?.FileSize ?? 0;
                if (fsize > 0)
                    RomFileSizeDisplay = fsize >= 1024L * 1024 * 1024
                        ? $"{fsize / (double)(1024L * 1024 * 1024):0.0#} GB"
                        : $"{fsize / (double)(1024 * 1024):0.0#} MB";
                Log($"[✓] ROM validated successfully: {validationResult.FileInfo.FileName}");
                RomStepText = RomIsPayloadOta
                    ? $"STEP 2: '{Path.GetFileName(filePath)}' validated — click INSTALL ROM. The app reboots the device itself and installs in two stages."
                    : RomNeedsSideload
                        ? $"STEP 2: '{Path.GetFileName(filePath)}' installs from recovery — click INSTALL FROM RECOVERY below."
                        : _romInstallMethod == RomInstallMethod.Unsupported
                            ? $"STEP 2: '{Path.GetFileName(filePath)}' cannot be flashed here — see the notice above."
                            : $"STEP 2: '{Path.GetFileName(filePath)}' validated. Check the plan above, then click {FlashActionLabel}.";

                // A checksum file published next to the ROM is verified automatically;
                // without one, integrity simply shows as unverified.
                var expected = _romValidator.FindExpectedChecksum(filePath);
                if (expected != null)
                {
                    Log($"[Checksum] Found {Path.GetFileName(expected.SourcePath)} next to the ROM — verifying…");
                    await RunChecksumVerificationAsync(filePath, expected.Algorithm, expected.Hash,
                        Path.GetFileName(expected.SourcePath));
                }
                else
                {
                    SetRomIntegrity(ChecksumState.Unknown, "No checksum file next to it — integrity not verified");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("Error validating ROM: {Message}", ex.Message);
                Log($"[ERROR] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Log a message to the UI and logging service.
        /// </summary>
        // C18: live severity counts shown on the filter chips
        private int _errorCount;
        private int _warningCount;
        public string ErrorsChipText => _errorCount > 0 ? $"ERRORS · {_errorCount}" : "ERRORS";
        public string WarningsChipText => _warningCount > 0 ? $"WARN · {_warningCount}" : "WARN";
        private void RaiseSeverityCounts()
        {
            OnPropertyChanged(nameof(ErrorsChipText));
            OnPropertyChanged(nameof(WarningsChipText));
        }

        public void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}";

            _logBuffer.Add(logEntry);
            LogMessages.Add(logEntry);
            _logger.Information(message);

            if (logEntry.Contains("[ERROR]"))
            {
                _errorCount++;
                RaiseSeverityCounts();
            }
            else if (logEntry.Contains("[WARN]"))
            {
                _warningCount++;
                RaiseSeverityCounts();
            }

            // Append to the visible list only when the entry passes the active
            // filter, instead of clearing and refilling it for every line.
            if (MatchesFilter(logEntry))
                FilteredLogs.Add(logEntry);

            // Rolling buffer: drop the oldest entries one by one so the bound
            // collections are never rebuilt while the UI is watching them.
            while (_logBuffer.Count > MaxLogEntries)
            {
                var oldest = _logBuffer[0];
                _logBuffer.RemoveAt(0);
                LogMessages.Remove(oldest);
                FilteredLogs.Remove(oldest);
            }
        }

        /// <summary>
        /// Update progress bar value and text. The bar is set instantly so the
        /// fill always matches the percentage in the text — the old easing made
        /// the bar and the text disagree (two values at once).
        /// </summary>
        public void UpdateProgress(double value, string text = null)
        {
            value = Math.Max(0, Math.Min(100, value));
            _progressTarget = value;
            // Never jump: ease the bar toward the new target so streamed
            // percentages glide instead of stepping.
            StartProgressSmoothing();
            ProgressText = text ?? $"{Math.Round(value)}%";
            RefreshElapsedText();
            // Any run reaching 100 is finished: settle every phase chip to
            // Done so VERIFY/REBOOT are never stranded on Pending.
            if (value >= 100)
            {
                SetFlashPhase("Done", "Done", "Done", "Done", indeterminate: false);
                // Completion snaps: the glide must not undershoot 100.
                _progressSmoothTimer?.Stop();
                ProgressValue = 100;
            }
        }

        // Target value requested by the last progress event.
        private double _progressTarget;
        // Timer that eases ProgressValue toward _progressTarget (~30 fps).
        private DispatcherTimer _progressSmoothTimer;

        private void StartProgressSmoothing()
        {
            if (_progressSmoothTimer == null)
            {
                _progressSmoothTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _progressSmoothTimer.Tick += (s, e) =>
                {
                    var diff = _progressTarget - _progressValue;
                    if (Math.Abs(diff) < 0.15)
                    {
                        _progressSmoothTimer.Stop();
                        if (_progressValue != _progressTarget)
                            ProgressValue = _progressTarget;
                        return;
                    }
                    // Exponential ease-out: big gaps glide fast at first, then
                    // settle gently (22% of the remaining distance per tick).
                    ProgressValue = _progressValue + diff * 0.22;
                };
            }

            _progressSmoothTimer.Start();
        }

        // Stopwatch for the currently running long operation (flash, batch, sideload).
        private System.Diagnostics.Stopwatch _flashStopwatch;
        // 1-second ticker so the elapsed label runs continuously.
        private DispatcherTimer _elapsedTimer;
        private string _progressElapsed = string.Empty;

        public string ProgressElapsed
        {
            get => _progressElapsed;
            private set => SetField(ref _progressElapsed, value);
        }

        private void StartFlashStopwatch()
        {
            _flashStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // The elapsed label must tick every second on its own — progress
            // events are bursty (long silences during reboots/sideload), so
            // updating it only on events makes the time jump.
            if (_elapsedTimer == null)
            {
                _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _elapsedTimer.Tick += (s, e) => RefreshElapsedText();
            }
            RefreshElapsedText();
            _elapsedTimer.Start();
        }

        private void RefreshElapsedText()
        {
            if (_flashStopwatch == null)
            {
                ProgressElapsed = string.Empty;
                return;
            }
            ProgressElapsed = $"Elapsed {_flashStopwatch.Elapsed:hh\\:mm\\:ss}";
        }

        private void StopFlashStopwatch()
        {
            _elapsedTimer?.Stop();
            _flashStopwatch?.Stop();
            _flashStopwatch = null;
            ProgressElapsed = string.Empty;
        }

        // ==================== 4.1: device dashboard ====================
        public class DeviceInfoRow
        {
            public string Label { get; set; }
            public string Value { get; set; }
        }

        public ObservableCollection<DeviceInfoRow> DeviceDashboard { get; private set; }

        public bool DeviceDashboardRefreshEnabled => !IsFlashing && DeviceConnected && IsModeAdb;
        public string DeviceDashboardRefreshReason
        {
            get
            {
                if (IsFlashing) return "An operation is in progress";
                if (!DeviceConnected) return "No device connected";
                if (!IsModeAdb) return "Full details need ADB — reboot out of bootloader mode";
                return "Re-reads battery, fingerprint, encryption and storage state";
            }
        }

        private void AddDashboardRow(List<DeviceInfoRow> rows, string label, string value)
        {
            rows.Add(new DeviceInfoRow { Label = label, Value = string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim() });
        }

        /// <summary>
        /// One honest snapshot of everything known about the device — the
        /// visible source of truth behind every enable/disable reason.
        /// </summary>
        public async System.Threading.Tasks.Task RefreshDeviceDashboardAsync()
        {
            if (IsFlashing) return;
            var serial = CurrentDeviceSerial;

            // No ADB session (disconnected, bootloader/fastboot, or no serial):
            // show an honest minimal snapshot from cached state instead of an
            // empty page — the tab must never render as a blank card.
            if (!DeviceConnected || !IsModeAdb || string.IsNullOrWhiteSpace(serial))
            {
                var mini = new List<DeviceInfoRow>();
                AddDashboardRow(mini, "Mode", DeviceModeDisplay);
                AddDashboardRow(mini, "Serial", serial);
                AddDashboardRow(mini, "Bootloader", _currentLockStatus);
                AddDashboardRow(mini, "Active slot", _currentActiveSlot);
                AddDashboardRow(mini, "Recovery probe",
                    string.IsNullOrWhiteSpace(RecoveryProbeSummary) ? null : RecoveryProbeSummary);
                AddDashboardRow(mini, "PC stock-recovery backup",
                    RecoveryBackupExists ? _recoveryBackupStamp + " (" + string.Join(", ", _recoveryBackupFiles.Keys) + ")" : "(none)");
                DeviceDashboard = new ObservableCollection<DeviceInfoRow>(mini);
                OnPropertyChanged(nameof(DeviceDashboard));
                return;
            }

            IsFlashing = true;
            try
            {
                var rows = new List<DeviceInfoRow>();
                var adb = ToolPathResolver.GetToolPath("adb");
                var token = CancellationToken.None;

                System.Func<string, System.Threading.Tasks.Task<string>> prop = async name =>
                    (await GetCombinedToolOutputAsync(adb,
                        "-s \"" + serial + "\" shell getprop " + name,
                        TimeSpan.FromSeconds(8), token))?.Trim();

                AddDashboardRow(rows, "Mode", DeviceModeDisplay);
                AddDashboardRow(rows, "Serial", serial);
                AddDashboardRow(rows, "Active slot", _currentActiveSlot);
                AddDashboardRow(rows, "Bootloader", _currentLockStatus);

                var model = await prop("ro.product.model");
                AddDashboardRow(rows, "Model", model);

                var android = await prop("ro.build.version.release");
                var fingerprint = await prop("ro.build.fingerprint");
                AddDashboardRow(rows, "Android", android);
                AddDashboardRow(rows, "Fingerprint", fingerprint);

                var cryptoState = await prop("ro.crypto.state");
                var cryptoType = await prop("ro.crypto.type");
                AddDashboardRow(rows, "Encryption",
                    string.IsNullOrWhiteSpace(cryptoState) ? null : cryptoState + (string.IsNullOrWhiteSpace(cryptoType) ? "" : " / " + cryptoType));

                var battery = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + serial + "\" shell dumpsys battery",
                    TimeSpan.FromSeconds(8), token);
                if (!string.IsNullOrWhiteSpace(battery))
                {
                    string level = null, acPowered = null, usbPowered = null;
                    foreach (var line in battery.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("level:", StringComparison.OrdinalIgnoreCase))
                            level = t.Substring(6).Trim();
                        else if (t.StartsWith("AC powered:", StringComparison.OrdinalIgnoreCase))
                            acPowered = t.Substring(11).Trim();
                        else if (t.StartsWith("USB powered:", StringComparison.OrdinalIgnoreCase))
                            usbPowered = t.Substring(12).Trim();
                    }
                    var charging = (acPowered == "true" || usbPowered == "true") ? " — charging" : "";
                    AddDashboardRow(rows, "Battery", level != null ? level + "%" + charging : null);
                }

                var df = await GetCombinedToolOutputAsync(adb,
                    "-s \"" + serial + "\" shell df -B1 /data",
                    TimeSpan.FromSeconds(8), token);
                if (!string.IsNullOrWhiteSpace(df))
                {
                    var dfLines = df.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (dfLines.Length >= 2)
                    {
                        var cols = dfLines[dfLines.Length - 1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length >= 4 && long.TryParse(cols[3], out var freeBytes) && freeBytes > 0)
                            AddDashboardRow(rows, "Data free", (freeBytes / 1024 / 1024 / 1024.0).ToString("F1") + " GB");
                    }
                }

                AddDashboardRow(rows, "Recovery probe",
                    string.IsNullOrWhiteSpace(RecoveryProbeSummary) ? null : RecoveryProbeSummary);
                AddDashboardRow(rows, "PC stock-recovery backup",
                    RecoveryBackupExists ? _recoveryBackupStamp + " (" + string.Join(", ", _recoveryBackupFiles.Keys) + ")" : "(none)");

                DeviceDashboard = new ObservableCollection<DeviceInfoRow>(rows);
                OnPropertyChanged(nameof(DeviceDashboard));
                Log("[Device] Dashboard refreshed (" + rows.Count + " rows).");
            }
            catch (Exception ex)
            {
                Log("[Device] Dashboard refresh failed: " + ex.Message);
            }
            finally
            {
                IsFlashing = false;
                RefreshButtonStates();
            }
        }

        /// <summary>
        /// Cleanup resources on shutdown.
        /// </summary>
        public void Shutdown()
        {
            _logger.Information("MainWindowViewModel shutting down");
            StopDeviceMonitoring();

            // Cancel single-flash, batch queue, and any running tool processes.
            _batchCts?.Cancel();
            _cts?.Cancel();
            _flashService.Cancel();

            _progressSmoothTimer?.Stop();
            _elapsedTimer?.Stop();
            StopSideloadRamp();

            _cts?.Dispose();
            // 2.5: a hung twrp install / sideload wait must not survive the
            // app. The adb SERVER is deliberately left alone.
            _processManager.KillChildren();
            _flashService.CleanTempExtractions();
            _logger.Flush();
        }

        /// <summary>
        /// Update filtered logs based on search term.
        /// </summary>
        private void UpdateFilteredLogs()
        {
            // Only runs when the search term changes, so a full rebuild here is fine.
            FilteredLogs.Clear();

            foreach (var log in LogMessages)
            {
                if (MatchesFilter(log))
                    FilteredLogs.Add(log);
            }
        }

        /// <summary>
        /// True when a log entry is visible under the current search term.
        /// </summary>
        private bool MatchesFilter(string entry)
        {
            var termOk = string.IsNullOrWhiteSpace(_logSearchTerm)
                || entry.IndexOf(_logSearchTerm, StringComparison.OrdinalIgnoreCase) >= 0;

            var levelOk = true;
            if (_logLevelFilter == "Errors")
                levelOk = entry.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0;
            else if (_logLevelFilter == "Warnings")
                levelOk = entry.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0
                       || entry.IndexOf("[WARNING]", StringComparison.OrdinalIgnoreCase) >= 0
                       || entry.IndexOf("[WARN]", StringComparison.OrdinalIgnoreCase) >= 0;

            return termOk && levelOk;
        }

        /// <summary>
        /// Clear all logs.
        /// </summary>
        public void ClearLogs()
        {
            LogMessages.Clear();
            _logBuffer.Clear();
            FilteredLogs.Clear();
            _errorCount = 0;
            _warningCount = 0;
            RaiseSeverityCounts();
            _logger.Information("Logs cleared by user");
        }

        #region User settings persistence

        /// <summary>
        /// Restore the partition checkboxes from the saved setting.
        /// </summary>
        private void LoadSavedPartitions()
        {
            // Default: ALL partitions selected (a full custom-ROM install).
            _chkDtbo = true;
            _chkInitBoot = true;
            _chkVendorBoot = true;
            _chkRecovery = true;
            _chkBoot = true;

            try
            {
                var saved = Properties.Settings.Default.SelectedPartitions;
                if (string.IsNullOrWhiteSpace(saved))
                {
                    // No saved preference yet — keep the all-selected default.
                    OnPropertyChanged(nameof(ChkDtbo));
                    OnPropertyChanged(nameof(ChkInitBoot));
                    OnPropertyChanged(nameof(ChkVendorBoot));
                    OnPropertyChanged(nameof(ChkRecovery));
                    OnPropertyChanged(nameof(ChkBoot));
                    SyncPartitionOptions();
                    RefreshButtonStates();
                    return;
                }

                var parts = saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().ToLowerInvariant())
                    .ToList();

                _chkDtbo = parts.Contains("dtbo");
                _chkInitBoot = parts.Contains("init_boot");
                _chkVendorBoot = parts.Contains("vendor_boot");
                _chkRecovery = parts.Contains("recovery");
                _chkBoot = parts.Contains("boot");
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load saved partitions: {Message}", ex.Message);
            }

            OnPropertyChanged(nameof(ChkDtbo));
            OnPropertyChanged(nameof(ChkInitBoot));
            OnPropertyChanged(nameof(ChkVendorBoot));
            OnPropertyChanged(nameof(ChkRecovery));
            OnPropertyChanged(nameof(ChkBoot));
            SyncPartitionOptions();
            RefreshButtonStates();
        }

        /// <summary>
        /// Persist the partition checkboxes (called on each checkbox change).
        /// </summary>
        private void SavePartitionsSetting()
        {
            try
            {
                Properties.Settings.Default.SelectedPartitions =
                    string.Join(",", GetSelectedPartitions());
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to save partition selection: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Restore the main window size and position from settings.
        /// </summary>
        public void ApplySavedWindowBounds(Window window)
        {
            try
            {
                var s = Properties.Settings.Default;
                if (s.WindowWidth >= window.MinWidth && s.WindowHeight >= window.MinHeight)
                {
                    window.Width = s.WindowWidth;
                    window.Height = s.WindowHeight;
                }

                if (s.WindowTop != 0 || s.WindowLeft != 0)
                {
                    // Only restore the position when it is still on a connected screen.
                    var virtualScreen = new Rect(
                        SystemParameters.VirtualScreenLeft,
                        SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth,
                        SystemParameters.VirtualScreenHeight);

                    if (virtualScreen.Contains(new Point(s.WindowLeft, s.WindowTop)))
                    {
                        window.Left = s.WindowLeft;
                        window.Top = s.WindowTop;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to restore window bounds: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Persist the main window size and position.
        /// </summary>
        public void SaveWindowBounds(double top, double left, double width, double height)
        {
            try
            {
                var s = Properties.Settings.Default;
                s.WindowTop = top;
                s.WindowLeft = left;
                s.WindowWidth = width;
                s.WindowHeight = height;
                s.Save();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to save window bounds: {Message}", ex.Message);
            }
        }

        #endregion

        /// <summary>
        /// Refresh recent files list.
        /// </summary>
        // ==================== 3.4: ADDITIONAL FILES recents ====================
        public ObservableCollection<string> RecentAdditional { get; private set; }

        public Visibility RecentAdditionalVisibility =>
            RecentAdditional != null && RecentAdditional.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

        public void RefreshRecentAdditional()
        {
            try
            {
                if (RecentAdditional == null) return;
                RecentAdditional.Clear();
                foreach (var file in RecentFilesManager.Instance.GetRecentAdditional())
                    RecentAdditional.Add(file);
                OnPropertyChanged(nameof(RecentAdditionalVisibility));
            }
            catch (Exception ex)
            {
                _logger.Debug("RecentAdditional refresh failed: {Message}", ex.Message);
            }
        }

        public void RefreshRecentFiles()
        {
            RecentFiles.Clear();
            var recentList = RecentFilesManager.Instance.GetRecentFiles();
            foreach (var file in recentList)
            {
                RecentFiles.Add(file);
            }
            OnPropertyChanged(nameof(RecentFileDisplays));
        }

        // S4: the combo shows file names; the code-behind maps the index back to
        // the full path in RecentFiles.
        public System.Collections.Generic.IReadOnlyList<string> RecentFileDisplays
            => RecentFiles.Select(System.IO.Path.GetFileName).ToList();

        // S4: clear the recent list.
        public void ClearRecentFiles()
        {
            RecentFilesManager.Instance.ClearRecentFiles();
            RefreshRecentFiles();
        }

        /// <summary>
        /// Load a recent ROM file.
        /// </summary>
        public async Task LoadRecentFileAsync(string filePath)
        {
            var isValid = await ValidateAndSelectRomAsync(filePath);
            if (isValid)
            {
                RecentFilesManager.Instance.AddRecentFile(filePath);
            }
        }

        #region Flashing operations

        /// <summary>
        /// Flash the selected ROM to the selected partitions.
        /// </summary>
        // Outcome of the last FlashRomAsync run, read by the payload two-stage
        // orchestrator to decide whether stage 2 may start.
        private bool _lastFlashFailed = false;

        // ==================== Tools status (sidebar) ====================
        private bool _toolAdbReady, _toolFastbootReady, _toolDumperReady;

        public bool ToolAdbReady { get => _toolAdbReady; private set => SetField(ref _toolAdbReady, value); }
        public bool ToolFastbootReady { get => _toolFastbootReady; private set => SetField(ref _toolFastbootReady, value); }
        public bool ToolDumperReady { get => _toolDumperReady; private set => SetField(ref _toolDumperReady, value); }

        private static bool ToolReady(string name)
        {
            try
            {
                var path = Services.ToolPathResolver.GetToolPath(name);
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch { return false; }
        }

        public Visibility ToolAdbMissingVisibility => ToolAdbReady ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ToolFastbootMissingVisibility => ToolFastbootReady ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ToolDumperMissingVisibility => ToolDumperReady ? Visibility.Collapsed : Visibility.Visible;

        public void RefreshToolStatus()
        {
            ToolAdbReady = ToolReady("adb");
            ToolFastbootReady = ToolReady("fastboot");
            ToolDumperReady = ToolReady("payload-dumper-go");
            OnPropertyChanged(nameof(ToolAdbMissingVisibility));
            OnPropertyChanged(nameof(ToolFastbootMissingVisibility));
            OnPropertyChanged(nameof(ToolDumperMissingVisibility));
        }

        // ==================== R5: data-driven partition list ====================
        public sealed class PartitionOption : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isSelected;
            public string Name { get; set; }
            public System.Action<bool> OnChanged { get; set; }
            public System.Collections.IList SyncGuardOwner { get; set; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    System.ComponentModel.PropertyChangedEventHandler h = PropertyChanged;
                    h?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                    if (SyncGuardOwner == null || !SyncGuardOwner.IsSynchronized)
                        OnChanged?.Invoke(value);
                }
            }

            /// <summary>Push a value from settings load without side effects.</summary>
            public void SyncFrom(bool value)
            {
                _isSelected = value;
                System.ComponentModel.PropertyChangedEventHandler h = PropertyChanged;
                h?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        public System.Collections.ObjectModel.ObservableCollection<PartitionOption> PartitionOptions { get; }
            = new System.Collections.ObjectModel.ObservableCollection<PartitionOption>
            {
                new PartitionOption { Name = "boot" },
                new PartitionOption { Name = "init_boot" },
                new PartitionOption { Name = "vendor_boot" },
                new PartitionOption { Name = "dtbo" },
                new PartitionOption { Name = "recovery" },
            };

        private bool _partitionSyncing;
        public string PartitionSelectionCount => $"{PartitionOptions.Count(o => o.IsSelected)} of {PartitionOptions.Count} selected";

        private void WirePartitionOptions()
        {
            foreach (var opt in PartitionOptions)
            {
                opt.SyncGuardOwner = PartitionOptions;
                opt.OnChanged = value =>
                {
                    if (_partitionSyncing) return;
                    switch (opt.Name)
                    {
                        case "boot": _chkBoot = value; break;
                        case "init_boot": _chkInitBoot = value; break;
                        case "vendor_boot": _chkVendorBoot = value; break;
                        case "dtbo": _chkDtbo = value; break;
                        case "recovery": _chkRecovery = value; break;
                    }
                    SavePartitionsSetting();
                    RefreshButtonStates();
                    OnPropertyChanged(nameof(PartitionSelectionCount));
                    OnPropertyChanged(nameof(ChkBoot)); OnPropertyChanged(nameof(ChkInitBoot));
                    OnPropertyChanged(nameof(ChkVendorBoot)); OnPropertyChanged(nameof(ChkDtbo));
                    OnPropertyChanged(nameof(ChkRecovery));
                };
            }
        }

        private bool GetPartitionField(string name)
        {
            switch (name)
            {
                case "boot": return _chkBoot;
                case "init_boot": return _chkInitBoot;
                case "vendor_boot": return _chkVendorBoot;
                case "dtbo": return _chkDtbo;
                case "recovery": return _chkRecovery;
                default: return false;
            }
        }

        private void SyncPartitionOptions()
        {
            _partitionSyncing = true;
            foreach (var opt in PartitionOptions)
            {
                opt.SyncFrom(GetPartitionField(opt.Name));
            }
            _partitionSyncing = false;
            OnPropertyChanged(nameof(PartitionSelectionCount));
        }

        /// <summary>Select or clear every partition chip (ALL / NONE buttons).</summary>
        public void SetAllPartitions(bool selected)
        {
            _chkBoot = selected; _chkInitBoot = selected; _chkVendorBoot = selected;
            _chkDtbo = selected; _chkRecovery = selected;
            SyncPartitionOptions();
            SavePartitionsSetting();
            RefreshButtonStates();
            OnPropertyChanged(nameof(ChkBoot)); OnPropertyChanged(nameof(ChkInitBoot));
            OnPropertyChanged(nameof(ChkVendorBoot)); OnPropertyChanged(nameof(ChkDtbo));
            OnPropertyChanged(nameof(ChkRecovery));
        }

        // ==================== Flash phases (footer) ====================
        // Cross-tab outcome notices: success/info auto-dismiss after six
        // seconds, failures stay until dismissed, at most three visible.
        public sealed class ToastItem
        {
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public string Kind { get; set; } = "Info"; // Info | Success | Error
            public string Icon => Kind == "Error" ? "" : Kind == "Success" ? "" : "";
            public bool IsSticky => Kind == "Error";
            /// <summary>True once the exit animation started — guards double-dismiss.</summary>
            public bool Closing { get; set; }
        }

        public System.Collections.ObjectModel.ObservableCollection<ToastItem> Toasts { get; }
            = new System.Collections.ObjectModel.ObservableCollection<ToastItem>();

        public Visibility ToastsVisibility => Toasts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RaiseToastsVisibility() => OnPropertyChanged(nameof(ToastsVisibility));

        /// <summary>Raised when a toast should leave — the view plays the exit
        /// animation and calls RemoveToast when it has finished (or at once
        /// when motion is disabled).</summary>
        public event Action<ToastItem> ToastCloseRequested;

        private void RequestToastClose(ToastItem item)
        {
            if (item == null || item.Closing) return;
            item.Closing = true;
            ToastCloseRequested?.Invoke(item);
        }

        /// <summary>Actually removes a toast from the list. Idempotent.</summary>
        public void RemoveToast(ToastItem item)
        {
            if (item == null) return;
            if (!Toasts.Remove(item)) return;
            RaiseToastsVisibility();
        }

        public void ShowToast(string kind, string title, string message)
        {
            try
            {
                while (Toasts.Count >= 3) Toasts.RemoveAt(0);
                var item = new ToastItem { Title = title, Message = message, Kind = kind };
                Toasts.Add(item);
                RaiseToastsVisibility();
                if (!item.IsSticky) _ = AutoDismissToastAsync(item);
            }
            catch { /* toasts must never take the operation down */ }
        }

        private async System.Threading.Tasks.Task AutoDismissToastAsync(ToastItem item)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(6000);
                RequestToastClose(item);
            }
            catch (Exception ex)
            {
                // Toast auto-removal must never take the flow down.
                _logger.Debug("Toast auto-dismiss failed: " + ex.Message);
            }
        }

        public void DismissToast(ToastItem item)
        {
            RequestToastClose(item);
        }

        public async Task FlashRomAsync()
        {
            if (IsFlashing)
            {
                Log("[Flash] A flash operation is already in progress.");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedFilePath))
            {
                Log("[ERROR] No ROM file selected.");
                return;
            }

            // The fastboot path cannot install a recovery-only ZIP. The button is
            // disabled in Simple mode; re-check here so a stale UI or a
            // programmatic call cannot get past it. A payload ROM is allowed —
            // its two-stage install starts in fastboot.
            if (!_isExpertMode && RomFastbootNotApplicable)
            {
                Log("[ERROR] This package installs from recovery. Use INSTALL FROM RECOVERY (SIDELOAD).");
                return;
            }

            var partitions = GetFlashPartitions();
            if (partitions.Count == 0 && !RomFlashesWholePackage)
            {
                Log($"[ERROR] {_romPlanNote}");
                Log("[ERROR] No writable partition could be derived from this package.");
                return;
            }

            // The button is disabled in these states; re-check here so a
            // programmatic or stale-UI call cannot slip past the gate.
            if (RomDeviceMismatch)
            {
                Log($"[ERROR] Refusing to flash: package is built for {RomDeviceTargets}, " +
                    $"connected device is {DeviceCodename}.");
                return;
            }

            if (_romChecksumState == ChecksumState.Mismatch)
            {
                Log("[ERROR] Refusing to flash: the ROM does not match its published checksum. Re-download it.");
                return;
            }

            var context = new FlashContext
            {
                Serial = CurrentDeviceSerial,
                RomFilePath = SelectedFilePath,
                SelectedPartitions = partitions,
                WipeCache = true,
                RebootAfterFlash = true,
                TargetSlot = SlotChoice == "Auto" ? null : SlotChoice,
                DisableVerity = ChkVbmetaVerityOff,
                WipeData = ChkWipeDataBeforeFlash,

                // Two-stage payload install: stage 1 always hands the device
                // to recovery. Partition flashes stay in fastboot so more
                // images can be written right away.
                PostFlashTarget = RomIsPayloadOta ? "recovery" : null,

                // Name the result for what it was: a boot-chain write is not a
                // ROM install, and the log is where people look afterwards. The
                // payload two-stage flow adds the recovery stage right after.
                SuccessMessage = RomIsPayloadOta
                    ? "Stage 1 complete — boot chain flashed, rebooting to recovery for the package install."
                    : RomFlashesWholePackage
                        ? "Firmware package installed successfully."
                        : $"Flashed {string.Join(", ", partitions)} successfully — this wrote the boot chain only; the ROM's system images were not installed."
            };

            // Register a batch job so progress is visible in the queue UI,
            // carrying the same options that are about to be flashed.
            var job = BatchOperationManager.Instance.AddJob(SelectedFilePath, partitions,
                context.TargetSlot, context.DisableVerity, context.WipeData, context.PostFlashTarget,
                _romDeviceIdentifiers);
            SetRomInstallWaitingRecovery(false);
            IsFlashing = true;
            StartFlashStopwatch();

            try
            {
                RomStepText = "STEP 3: Flashing — keep the device connected and do not unplug the cable.";
                SetFlashPhase("Active", "Pending", "Pending", "Pending", "WRITE");
                BeginFlashJournal($"Flashed {Path.GetFileName(SelectedFilePath)}");
                Journal($"Partitions: {string.Join(", ", partitions)}");
                Log($"[Flash] Starting flash of {Path.GetFileName(SelectedFilePath)} -> {string.Join(", ", partitions)}");
                var result = await _flashService.FlashRomAsync(context, _cts?.Token ?? CancellationToken.None);
                _lastFlashFailed = !result.Success;

                if (result.Success)
                {
                    Log($"[✓] {result.Message}");
                    if (RomIsPayloadOta)
                    {
                        Log("[Plan] Stage 1 done — the boot chain is in place. Waiting for recovery, then the package installs itself.");
                    }
                    else if (RomNeedsSideload || _romKind == RomPackageKind.AddOnZip)
                    {
                        Log("[Plan] Reminder: this package's own installer did not run. The system images were not written.");
                    }
                    BatchOperationManager.Instance.UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Completed, result.Message, 100);
                    OnPropertyChanged(nameof(QueueBadgeVisibility));
                    OnPropertyChanged(nameof(QueueBadgeText));
                    if (RomIsPayloadOta)
                    {
                        // Stage 1 of 2 only: the ROM itself is still not
                        // installed — do NOT announce completion here. The bar
                        // parks at 15% until the sideload stage streams the ROM.
                        _payloadStage = 1;
                        Journal("Stage 1 complete — boot chain written.");
                        Journal("Stage 2 — waiting for recovery, then sideloading the same package.");
                        // The install is still running: keep the flashing
                        // footer visible through the reboot-to-recovery wait
                        // until the sideload session takes over.
                        SetRomInstallWaitingRecovery(true);
                        UpdateProgress(15, "Stage 1 complete — boot chain written");
                        // The write finished; the sideload install is the
                        // verify slice (15-90%) of the payload session.
                        SetFlashPhase("Done", "Done", "Active", "Pending", "WRITE");
                        RomStepText = "STEP 3: Stage 1 of 2 complete — the boot chain is written. The device is rebooting to RECOVERY to install the ROM — keep the cable connected. " +
                            "Note: the payload includes the ROM's own recovery, which replaces any custom recovery you had installed.";
                        AdditionalStepText = "Stage 2 (ROM install) starts automatically once the device reaches recovery.";
                        ShowToast("Info", "Stage 1 of 2 — boot chain written",
                            "Waiting for recovery, then the package installs automatically.");
                    }
                    else
                    {
                        UpdateProgress(100, "Flash complete");
                        Journal("Stage 1 complete — boot chain written.");
                        if (!IsExpertMode)
                        {
                            ShowAfterCarePanel("Flash complete",
                                "The device is doing what STEP 4 says. The exact partitions written are in the report below.");
                        }
                        ShowToast("Success", "Flash complete", "Package written. Follow STEP 4 below.");
                        RomStepText = "STEP 4: Flash complete — the device is still in fastboot. On the ADDITIONAL FILES tab add the .img and press FLASH IMAGE; for ZIPs use REBOOT TO RECOVERY first.";
                        AdditionalStepText = "Core flash complete — device still in fastboot: add the .img and FLASH IMAGE; for ZIPs reboot to recovery.";
                    }
                }
                else
                {
                    Log($"[ERROR] {result.Message}");
                    if (!string.IsNullOrWhiteSpace(result.TechnicalDetails))
                        Log($"    {result.TechnicalDetails}");
                    BatchOperationManager.Instance.UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Failed, result.Message);
                    UpdateProgress(0, "Flash failed");
                    SetRomInstallWaitingRecovery(false);
                    RomStepText = "Flash failed — see the LOGS tab for details, then retry.";
                    Journal("FAILED — see the LOGS tab.");
                    _lastFlashSummary = "Last flash FAILED — see the LOGS tab.";
                    UpdateStepTrail();
                    OnPropertyChanged(nameof(IdleFooterRight));
                    ShowToast("Error", "Flash failed", "Details in the LOGS tab. The device is still in fastboot.");
                }
            }
            catch (OperationCanceledException)
            {
                _lastFlashFailed = true;
                Log("[Flash] Cancelled by user.");
                BatchOperationManager.Instance.UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Cancelled, "Cancelled by user");
                UpdateProgress(0, "Cancelled");
                SetRomInstallWaitingRecovery(false);
                RomStepText = "Flash cancelled — check the device state before retrying.";
                _lastFlashSummary = "Last flash CANCELLED by user.";
                UpdateStepTrail();
                OnPropertyChanged(nameof(IdleFooterRight));
                ShowToast("Info", "Flash cancelled", "Check the device state before retrying.");
            }
            catch (Exception ex)
            {
                _lastFlashFailed = true;
                Log($"[ERROR] {ex.Message}");
                BatchOperationManager.Instance.UpdateJobStatus(job.Id, RomFlashJob.JobStatus.Failed, ex.Message);
                UpdateProgress(0, "Flash error");
                RomStepText = "Flash failed — see the LOGS tab for details, then retry.";
                _lastFlashSummary = "Last flash FAILED — see the LOGS tab.";
                UpdateStepTrail();
                OnPropertyChanged(nameof(IdleFooterRight));
                ShowToast("Error", "Flash failed", $"Unexpected error — details in the LOGS tab.");
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
            }
        }

        /// <summary>
        /// The themed "RECOVERY BOOTED" guidance window: tells the user exactly
        /// what to tap on the phone. Non-modal (the wait loop keeps running) and
        /// it CLOSES ITSELF the moment the app detects sideload mode — see
        /// RaiseSideloadGuidanceClosed().
        /// </summary>
        private void ShowSideloadGuidance()
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => SideloadGuidanceWindow.Show(this)));
        }

        /// <summary>
        /// Abort the currently running flash operation (or batch queue).
        /// </summary>
        public void AbortFlash()
        {
            _flashService.Cancel();
            _batchCts?.Cancel();
            // End the stage-1 wait too: an abort during the reboot-to-recovery
            // stretch means the user no longer wants the automatic stage 2.
            if (_romInstallWaitingRecovery)
            {
                SetRomInstallWaitingRecovery(false);
                _payloadStage = 0;
                Log("[Flash] Stage-2 install wait ended by user.");
            }
            // Cancel any in-flight CLI-side operation (twrp install/backup,
            // dd backup, rescue erase) and give future calls a fresh source.
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            if (_payloadSessionActive)
            {
                EndPayloadSession();
                UpdateProgress(0, "Install session ended");
                Log("[Flash] ROM install session ended by user.");
                ShowToast("Info", "Session ended",
                    "The install session was closed. The device may still be mid-install.");
            }
            Log("[Flash] Abort requested.");
        }

        /// <summary>
        /// Apply an update package via ADB sideload (device must be in sideload mode).
        /// </summary>
        /// <summary>
        /// Apply an update package via ADB sideload, handling mode transitions
        /// automatically:
        /// - already in sideload → flash directly;
        /// - in recovery → reboot into sideload, then flash;
        /// - in system (allowRebootChain) → reboot to recovery, then sideload, then flash.
        /// This covers the GAPPS / Magisk / kernel-zip workflow without any
        /// manual navigation on the device.
        /// </summary>
        public async Task<bool> SideloadUpdateAsync(string filePath, bool allowRebootChain = true)
        {
            if (IsFlashing)
            {
                Log("[Sideload] A flash operation is already in progress.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Log($"[ERROR] Update package not found: {filePath}");
                return false;
            }

            // Safety net for recovery INSTALLER zips: pull the current stock
            // recovery to the PC before anything can replace it. Works in
            // recovery (dd) and fastboot (fetch); other modes skip with a
            // logged warning.
            var installerZip = System.IO.Path.GetFileName(filePath).ToLowerInvariant();
            var isRecoveryInstallerZip =
                System.IO.Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                (installerZip.Contains("orangefox") || installerZip.Contains("twrp-installer"));
            if (isRecoveryInstallerZip)
            {
                var backedUp = await BackupStockRecoveryAutoAsync(CurrentDeviceSerial);
                if (!backedUp)
                    Log("[Backup] Proceeding without a stock recovery backup — keep this in mind if the new recovery fails to load.");
            }

            // Lock the action before changing device mode so another flash/reboot
            // cannot start during the recovery → sideload transition.
            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                var ready = await EnsureSideloadModeAsync(allowRebootChain, isRecoveryInstallerZip);
                if (!ready) return false;

                Log($"[Sideload] Applying {Path.GetFileName(filePath)}...");
                // Every sideload shows the transfer ramp — standalone flows
                // used to leave the bar frozen (the ramp was payload-only).
                StartSideloadRamp(filePath);
                if (!_payloadSessionActive || _payloadStage != 2)
                    SetFlashPhase("Done", "Active", "Pending", "Pending", "WRITE");
                var result = await _flashService.SideloadAsync(
                    CurrentDeviceSerial, filePath, _cts?.Token ?? CancellationToken.None);
                StopSideloadRamp();
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
                if (!result.Success && !string.IsNullOrWhiteSpace(result.TechnicalDetails))
                    Log($"    {result.TechnicalDetails}");
                // Remember the outcome of a stage-1 attempt: the adb link can
                // drop right as the package finishes (the device reboots),
                // which adb reports as an error even on success.
                _sideloadAttemptFailed = !result.Success && _payloadSessionActive && _payloadStage == 1;
                if (result.Success)
                {
                    if (_payloadSessionActive && _payloadStage == 1)
                    {
                        // The ROM itself is now installed: exactly 90%.
                        _payloadStage = 2;
                        UpdateProgress(90, "ROM installed — 90%");
                        // The package applied — the reboot to the new system
                        // is the last slice of the install.
                        SetFlashPhase("Done", "Done", "Done", "Active", "WRITE");
                        // The ROM tab is done the moment the package applies:
                        // add-ons, reboots and completion all live on the
                        // ADDITIONAL FILES tab, so switch there regardless of
                        // the after-flash choice.
                        _additionalFilesSwitched = true;
                        SelectedTabIndex = 1;
                        Log("[Plan] ROM applied (90%) — switching to the ADDITIONAL FILES tab.");
                        RefreshButtonStates();
                        RomStepText = "STEP 4: ROM installed — 90%. The device is rebooting on its own. " +
                            "To flash GAPPS or other zips: add the ZIP on the ADDITIONAL FILES tab, then press APPLY UPDATE.ZIP (SIDELOAD) — " +
                            "the progress reaches 95% when the zip is applied. The install completes at 100% once the system boots.";
                        AdditionalStepText = "ROM applied (90%) — the device is rebooting. Flash zips via APPLY UPDATE.ZIP (SIDELOAD); " +
                            "the session completes when the system boots.";
                        ShowToast("Success", "ROM installed",
                            "The ROM is applied. Flash GAPPS now or let the device boot.");
                        Dialogs.ConfirmDialog.Show(
                            System.Windows.Application.Current?.MainWindow,
                            "ROM FLASH COMPLETE",
                            "ROM installed. The device is still in recovery.\n" +
                            "Before the first boot you can:\n" +
                            "•  flash add-on zips (GAPPS etc.) now on the ADDITIONAL FILES tab by rebooting to recovery on the device.\n" +
                            "•  or REBOOT TO SYSTEM for the first boot.",
                            "OK", null, danger: false);
                    }
                    else if (_payloadSessionActive && _payloadStage == 2)
                    {
                        // An add-on zip (GAPPS etc.) is applied: exactly 95%.
                        _payloadStage = 3;
                        UpdateProgress(95, "GAPPS applied — 95%");
                        RomStepText = "STEP 4: Add-on package applied — 95%. The device is rebooting. " +
                            "The install completes at 100% as soon as the system is up.";
                        AdditionalStepText = "Add-on applied (95%) — the session completes when the system boots.";
                        ShowToast("Success", "Package applied",
                            "The add-on zip is applied. The install completes when the system boots.");
                    }
                    else
                    {
                        // A session that never recorded the stage-1 mark (older
                        // build, interrupted resume) still has to complete on
                        // boot: mark the ROM as applied instead of stranding the
                        // bar at 90%.
                        if (_payloadSessionActive && _payloadStage == 0)
                            _payloadStage = 2;
                        // Settle the bar: the ramp stops holding "SIDELOADING…"
                        // and the phase chips show the transfer completed.
                        UpdateProgress(90, "PACKAGE APPLIED");
                        SetFlashPhase("Done", "Done", "Done", "Active", "WRITE");
                        // Standalone sideload: a recovery installer zip lands
                        // in the NEW recovery; TWRP/OrangeFox stay in their
                        // menu after a sideload instead of rebooting.
                        var customRecoveryRunning = !string.IsNullOrEmpty(RecoveryProbeKind) &&
                            RecoveryProbeKind != "Stock recovery" && RecoveryProbeKind != "Unknown";
                        if (AdditionalZipIsRecoveryInstaller)
                        {
                            AdditionalStepText = "Recovery installer applied — the custom recovery is installed. " +
                                "Use REBOOT TO RECOVERY to boot into it.";
                            RomStepText = "STEP 4: Recovery zip applied — press REBOOT TO RECOVERY; the device boots into the freshly installed custom recovery.";
                            ShowToast("Success", "Recovery installed",
                                "The installer zip applied — REBOOT TO RECOVERY enters the new recovery.");

                            // Prompt the reboot right here: the sideload left the
                            // device in its recovery menu, so one confirmation
                            // finishes the whole install.
                            var rebootNow = await System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                Dialogs.ConfirmDialog.Show(
                                    System.Windows.Application.Current?.MainWindow,
                                    "REBOOT TO RECOVERY?",
                                    "!!OrangeFox was applied successfully.\n\n" +
                                    "The custom recovery is now installed on the device.\n" +
                                    "Reboot into it now?",
                                    "REBOOT TO RECOVERY", "STAY HERE", danger: false));
                            if (rebootNow)
                            {
                                Log("[Recovery] Rebooting into the freshly installed recovery...");
                                UpdateProgress(96, "REBOOTING TO RECOVERY…");
                                if (await RebootToRecoveryWithAuthRetryAsync())
                                {
                                    UpdateProgress(100, "CUSTOM RECOVERY INSTALLED");
                                    SetFlashPhase("Done", "Done", "Done", "Done", "WRITE", indeterminate: false);
                                    Log("[✓] The device is booting into OrangeFox. The recovery probe will identify it on arrival.");
                                    ShowToast("Success", "Rebooting to recovery",
                                        "The device boots into the freshly installed OrangeFox.");
                                }
                                else
                                {
                                    Log("[Recovery] The reboot did not complete — REBOOT TO RECOVERY is still available on the ADDITIONAL FILES tab.");
                                }
                            }
                            else
                            {
                                Log("[Recovery] Staying in the current recovery menu — use REBOOT TO RECOVERY when ready.");
                            }
                        }
                        else
                        {
                            if (customRecoveryRunning)
                            {
                                AdditionalStepText = "ZIP applied — TWRP/OrangeFox stays in its menu. Flash more zips (INSTALL RECOVERY ZIP / twrp install), or press FINISH & REBOOT when done.";
                                RomStepText = "STEP 4: ZIP applied. The custom recovery does not reboot on its own — flash more add-ons or press FINISH & REBOOT.";
                                ShowToast("Success", "ZIP applied",
                                    "The custom recovery stays in its menu — flash more zips or press FINISH & REBOOT.");
                            }
                            else
                            {
                                AdditionalStepText = "Package applied — the device reboots itself after sideload. " +
                                    "Leave it plugged in; the first boot after an install can take several minutes.";
                                RomStepText = "STEP 4: Package applied — the device is rebooting on its own. " +
                                    "The ADDITIONAL FILES recovery actions re-enable only if the device returns to recovery.";
                                ShowToast("Success", "Package applied", "The device is rebooting itself — wait for the first boot.");
                            }
                        }
                    }
                }

                return result.Success;
            }
            finally
            {
                StopSideloadRamp();
                IsFlashing = false;
                StopFlashStopwatch();
            }
        }

        /// <summary>
        /// Drive the device into sideload mode from wherever it currently is.
        /// Returns false (with guidance logged) when the transition is not possible.
        /// </summary>
        private async Task<bool> EnsureSideloadModeAsync(bool allowRebootChain, bool guided = false)
        {
            // The monitor cache can lag the real device by seconds (it polls on
            // a timer) — right after "Device reached Sideload mode" the cache
            // may still say recovery/fastboot and refuse a valid install. Ask
            // the device fresh, then fall back to the cached value.
            DeviceMode mode;
            try
            {
                var fresh = await _flashService.DetectModeAsync(
                    CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                mode = MapDetectedMode(fresh);
                if (mode == DeviceMode.Disconnected)
                    mode = _currentPhoneMode;
            }
            catch
            {
                mode = _currentPhoneMode;
            }

            if (mode == DeviceMode.Sideload)
                return true;

            if (mode == DeviceMode.AdbRecovery ||
                (mode == DeviceMode.AdbSystem && allowRebootChain))
            {
                if (mode == DeviceMode.AdbSystem)
                {
                    Log("[Sideload] Device is booted into the ROM — rebooting to recovery first...");
                    var rec = await _flashService.RebootViaAdbAsync(
                        CurrentDeviceSerial, "recovery", _cts?.Token ?? CancellationToken.None);
                    if (!rec.Success)
                    {
                        Log($"[ERROR] Reboot to recovery failed: {rec.Message}");
                        return false;
                    }
                    if (!await WaitForSideloadChainModeAsync(DeviceMode.AdbRecovery)) return false;
                }

                if (guided)
                {
                    // Recovery-installer zips (OrangeFox/TWRP): the package is
                    // applied through the recovery menu itself — the user taps
                    // Apply update → Apply update via ADB, the phone enters
                    // sideload-wait on its own, and the transfer starts here.
                    return await WaitForGuidedSideloadAsync();
                }

                Log("[Sideload] Rebooting recovery into sideload mode...");
                var res = await _flashService.RebootViaAdbAsync(
                    CurrentDeviceSerial, "sideload", _cts?.Token ?? CancellationToken.None);
                if (!res.Success)
                {
                    Log($"[ERROR] Entering sideload failed: {res.Message}");
                    return false;
                }
                return await WaitForSideloadChainModeAsync(DeviceMode.Sideload);
            }

            if (IsFastbootMode && allowRebootChain)
            {
                if (IsRecoveryImg(_recoveryFilePath))
                {
                    Log("[Sideload] Device is in fastboot and a recovery .img is selected — booting it temporarily first...");
                    var boot = await _flashService.BootRecoveryImageAsync(
                        CurrentDeviceSerial, _recoveryFilePath, _cts?.Token ?? CancellationToken.None);
                    if (!boot.Success)
                    {
                        Log($"[ERROR] Booting recovery failed: {boot.Message}");
                        return false;
                    }
                }
                else
                {
                    Log("[Sideload] Device is in fastboot — requesting reboot to recovery...");
                    var reboot = await _flashService.RebootAsync(
                        CurrentDeviceSerial, "recovery", _cts?.Token ?? CancellationToken.None);
                    if (!reboot.Success)
                    {
                        Log($"[ERROR] Reboot to recovery failed: {reboot.Message}");
                        return false;
                    }
                }

                if (!await WaitForSideloadChainModeAsync(DeviceMode.AdbRecovery)) return false;
                if (guided)
                    return await WaitForGuidedSideloadAsync();
                Log("[Sideload] Recovery detected — requesting sideload mode...");
                var enter = await _flashService.RebootViaAdbAsync(
                    CurrentDeviceSerial, "sideload", _cts?.Token ?? CancellationToken.None);
                if (!enter.Success)
                {
                    Log($"[ERROR] Entering sideload failed: {enter.Message}");
                    return false;
                }
                return await WaitForSideloadChainModeAsync(DeviceMode.Sideload);
            }

            Log("[Sideload] Device must be in sideload/recovery mode, or in ADB/fastboot with a recovery available. " +
                "Boot TWRP/OrangeFox, open 'Apply update from ADB', and retry.");
            return false;
        }

        /// <summary>
        /// Poll the device mode until it reaches the expected state (the reboot
        /// takes a few seconds). Times out after ~40 seconds.
        /// </summary>
        /// <summary>
        /// Guided sideload for recovery-installer zips: show the phone-side
        /// steps (Apply update → Apply update via ADB), then poll until the
        /// phone enters sideload-wait on its own — the transfer starts
        /// automatically. Cancels via the dialog's button or AbortToken;
        /// times out after 4 minutes with an honest log.
        /// </summary>
        private async Task<bool> WaitForGuidedSideloadAsync()
        {
            Log("[Sideload] On the phone: 'Apply update' → 'Apply update via ADB'. Waiting for the phone...");
            // Show the wait on the progress panel too — a frozen bar while the
            // user navigates the phone reads as a hung app.
            UpdateProgress(2, "WAITING FOR PHONE — tap 'Apply update via ADB'…");
            SetFlashPhase("Active", "Pending", "Pending", "Pending", "WRITE");
            var app = System.Windows.Application.Current;
            Dialogs.SideloadGuideDialog dlg = null;
            if (app != null)
                dlg = await app.Dispatcher.InvokeAsync(() =>
                    Dialogs.SideloadGuideDialog.Show(app.MainWindow));
            try
            {
                bool unauthorizedHintShown = false;
                // SetStatus touches WPF objects — always marshal to the UI
                // thread. Calling it directly from this background continuation
                // throws and silently kills the whole wait.
                System.Action<string> status = text =>
                {
                    if (dlg == null) return;
                    if (app != null) app.Dispatcher.InvokeAsync(() => dlg.SetStatus(text));
                    else dlg.SetStatus(text);
                };
                var deadline = DateTime.UtcNow.AddMinutes(4);
                var lastWaitLog = DateTime.UtcNow;
                while (DateTime.UtcNow < deadline)
                {
                    if (dlg != null && dlg.Cancelled)
                    {
                        Log("[Sideload] Guided sideload cancelled — nothing was written; the phone stays in recovery.");
                        return false;
                    }
                    if (_cts?.Token.IsCancellationRequested == true)
                    {
                        Log("[Sideload] Operation aborted — nothing was written; the phone stays in recovery.");
                        return false;
                    }

                    await Task.Delay(1500);
                    string detected;
                    try
                    {
                        detected = await _flashService.DetectModeAsync(
                            CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                    }
                    catch
                    {
                        // Transient adb hiccups (mode transitions restart the
                        // daemon) — keep polling, but say so in the log.
                        if ((DateTime.UtcNow - lastWaitLog).TotalSeconds >= 30)
                        {
                            lastWaitLog = DateTime.UtcNow;
                            Log("[Sideload] Still waiting — adb is not answering right now; keep the cable connected.");
                        }
                        continue;
                    }

                    if (detected == "Unauthorized")
                    {
                        if (!unauthorizedHintShown)
                        {
                            unauthorizedHintShown = true;
                            Log("[Sideload] Device is connected but not authorized — accept the USB debugging prompt on the device screen.");
                        }
                        status("Accept the USB debugging prompt on the phone");
                        continue;
                    }

                    if (MapDetectedMode(detected) == DeviceMode.Sideload)
                    {
                        Log("[Sideload] Phone is waiting for the transfer — sideloading automatically...");
                        status("Phone is ready — starting the transfer...");
                        await Task.Delay(600);   // let the user see the status flip
                        return true;
                    }

                    var left = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                    status("Waiting for the phone — tap 'Apply update via ADB' on it (" + left + "s)");
                    if ((DateTime.UtcNow - lastWaitLog).TotalSeconds >= 30)
                    {
                        lastWaitLog = DateTime.UtcNow;
                        Log("[Sideload] Still waiting for the phone to enter sideload (" + left + "s left)...");
                    }
                }
                Log("[ERROR] The phone did not enter sideload mode within 4 minutes. " +
                    "Nothing was written; the phone stays in recovery — retry when ready.");
                return false;
            }
            finally
            {
                if (app != null && dlg != null)
                    await app.Dispatcher.InvokeAsync(() => dlg.Close());
            }
        }

        private async Task<bool> WaitForSideloadChainModeAsync(DeviceMode expected, int iterations = 60)
        {
            bool unauthorizedHintShown = false;
            var lastProgressLog = DateTime.UtcNow;
            for (int i = 0; i < iterations; i++)
            {
                await Task.Delay(1500);
                var detected = await _flashService.DetectModeAsync(
                    CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);

                // Recovery adb needs an RSA authorization prompt on the device
                // screen — that state must not read as "disconnected", or the
                // user never learns the device is fine and just needs a tap.
                if (detected == "Unauthorized")
                {
                    if (!unauthorizedHintShown)
                    {
                        unauthorizedHintShown = true;
                        Log("[Sideload] Device is connected but not authorized — accept the USB debugging prompt on the device screen to continue.");
                    }
                    if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= 30)
                    {
                        lastProgressLog = DateTime.UtcNow;
                        Log("[Sideload] Still waiting for authorization (accept the prompt on the phone)...");
                    }
                    continue;
                }

                var mapped = MapDetectedMode(detected);
                if (mapped == expected)
                {
                    Log($"[Sideload] Device reached {expected} mode.");
                    return true;
                }
                // The user may have gone straight through the recovery menu to
                // 'Apply update via ADB' — the phone then reports sideload
                // while we are still waiting for recovery. Sideloading works
                // from there, so treat it as success instead of timing out.
                if (expected == DeviceMode.AdbRecovery && mapped == DeviceMode.Sideload)
                {
                    Log("[Sideload] Phone already entered sideload mode — applying the package.");
                    return true;
                }
                if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= 30)
                {
                    lastProgressLog = DateTime.UtcNow;
                    Log("[Sideload] Waiting for the device to reach " + expected + " mode...");
                }
            }
            Log($"[ERROR] Device did not reach {expected} mode within 90 seconds. " +
                "Check the device screen (some recoveries need 'Apply update from ADB' confirmed manually) and retry.");
            return false;
        }

        /// <summary>
        /// Map a FlashService mode string to the ViewModel DeviceMode enum.
        /// </summary>
        private DeviceMode MapDetectedMode(string detected)
        {
            switch (detected)
            {
                case "Fastboot": return DeviceMode.Fastboot;
                case "FastbootD": return DeviceMode.FastbootD;
                case "AdbSystem": return DeviceMode.AdbSystem;
                case "AdbRecovery": return DeviceMode.AdbRecovery;
                case "Sideload": return DeviceMode.Sideload;
                default: return DeviceMode.Disconnected;
            }
        }

        /// <summary>
        /// Two-stage payload ROM install:
        ///   Stage 1 — flash the boot-chain images from the payload over fastboot,
        ///             then reboot to recovery (FlashRomAsync, recovery handoff).
        ///   Stage 2 — wait for the device to reach recovery, then sideload the
        ///             same package so its own installer writes system, vendor
        ///             and product.
        /// </summary>
        public async Task InstallPayloadRomAsync()
        {
            if (IsFlashing)
            {
                Log("[ROM] A flash operation is already in progress.");
                return;
            }

            if (!RomIsPayloadOta || string.IsNullOrWhiteSpace(SelectedFilePath))
            {
                Log("[ROM] This flow is only for payload ROM packages.");
                return;
            }

            Log("[Plan] Two-stage install starting: fastboot boot chain, then recovery install of the package.");

            // Snapshot the current build fingerprint so a dropped sideload
            // link can be resolved automatically after the reboot.
            _preFlashFingerprint = await GetDeviceFingerprintAsync(CurrentDeviceSerial);
            _preFlashSlot = await GetDeviceSlotAsync(CurrentDeviceSerial);
            Log("[Plan] Pre-install active slot: '" + (_preFlashSlot ?? "?") + "'.");
            Log(_preFlashFingerprint != null
                ? "[Plan] Pre-install build fingerprint captured: " + _preFlashFingerprint
                : "[Plan] Pre-install build fingerprint unavailable - the post-reboot check will be inconclusive.");
            BeginFlashJournal($"Two-stage install of {Path.GetFileName(SelectedFilePath)}");

            // The session starts HERE so stage 1's partition flashes occupy the
            // 0-15% slice of the whole install (progress contract:
            // partitions 0-15, ROM sideload 15-90, add-on 90-95, boot 100).
            BeginPayloadSession();

            // Stage 1: the existing pipeline (reboots from ADB to bootloader
            // itself, extracts the payload images, flashes them, reboots to
            // recovery). It also runs the device-mismatch and checksum gates.
            await FlashRomAsync();

            // A failed or cancelled stage 1 leaves the device wherever it was;
            // do not continue into recovery handoff.
            if (_lastFlashFailed) return;

            // Stage 2 begins: the footer and progress bar stay up for the whole
            // session (60% now, 90% when the ROM is applied, 100% at boot).
            BeginPayloadSession();

            // Recovery adb often needs an RSA authorization the user must tap,
            // but sideload mode never requires it — so the recovery wait is
            // skipped and the user is told to enter sideload themselves.
            RomStepText = "STEP 3: Boot chain written. On the DEVICE screen choose: Apply update → Apply from ADB. " +
                          "The app detects sideload and installs the ROM automatically — stay on this tab.";
            AdditionalStepText = "Waiting for sideload mode — choose 'Apply update from ADB' on the device. No time limit.";
            Log("[Plan] Stage 2 — waiting for the device to enter sideload " +
                "(on the device: Apply update → Apply from ADB)...");

            // One-click guidance: as soon as the device reaches recovery, pop a
            // short dialog telling the user exactly what to tap on the phone.
            // If the user was quick and is already in sideload, skip it.
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(1500);
                var det = await _flashService.DetectModeAsync(
                    CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                if (det == "Sideload") { RaiseSideloadGuidanceClosed(); break; } // already in sideload
                if (det == "AdbRecovery" || det == "Unauthorized" || i == 39)
                {
                    Log("[Plan] Recovery detected — showing the on-device instruction dialog.");
                    ShowSideloadGuidance();
                    break;
                }
            }

            if (!await WaitForSideloadChainModeAsync(DeviceMode.Sideload, iterations: 160))
            {
                // Not a dead end: the session stays open and the monitor applies
                // the ROM automatically whenever the user enters sideload.
                Log("[Plan] Still waiting for sideload — on the DEVICE screen choose: Apply update → Apply from ADB. " +
                    "The ROM installs automatically the moment sideload is detected.");
                Journal("Stage 2 — waiting for the user to enter 'Apply update from ADB'.");
                RomStepText = "STEP 3: Waiting for sideload — on the DEVICE screen choose: Apply update → Apply from ADB. " +
                    "The ROM installs automatically as soon as sideload is detected.";
                AdditionalStepText = "Waiting for sideload mode (no time limit) — pick 'Apply update from ADB' on the device.";
                return;
            }

            // Stage 2: the device is in sideload — apply the same package. Its
            // result sets the 90% stage-2 banner (see SideloadUpdateAsync).
            Log("[Sideload] Sideload detected — installing the ROM package...");
            RaiseSideloadGuidanceClosed(); // auto-close the guidance dialog
            await SideloadUpdateAsync(SelectedFilePath, allowRebootChain: false);
            Journal("Stage 2 — sideload handed off to the package's own installer.");
        }

        /// <summary>
        /// Install the selected ROM package through recovery (adb sideload), which
        /// is the only route that writes a payload ROM's system images. With the
        /// clean-install chip ticked, userdata is erased in fastboot first.
        /// </summary>
        public async Task InstallSelectedRomViaSideloadAsync()
        {
            if (IsFlashing)
            {
                Log("[ROM] A flash operation is already in progress.");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
            {
                Log("[ERROR] No ROM package selected.");
                return;
            }

            if (!RomNeedsSideload)
            {
                Log("[ROM] This package does not install from recovery.");
                return;
            }

            if (RomDeviceMismatch)
            {
                Log($"[ERROR] Refusing to install: package is built for {RomDeviceTargets}, " +
                    $"connected device is {DeviceCodename}.");
                return;
            }

            if (_romChecksumState == ChecksumState.Mismatch)
            {
                Log("[ERROR] Refusing to install: the ROM does not match its published checksum. Re-download it.");
                return;
            }

            // A clean install has to happen in fastboot before the recovery handoff,
            // because recovery owns the device once sideload starts.
            if (ChkWipeDataBeforeFlash)
            {
                IsFlashing = true;
                StartFlashStopwatch();
                try
                {
                    RomStepText = "STEP 3: Erasing user data (clean install) before the recovery install…";
                    UpdateProgress(8, "Erasing user data…");

                    if (!await EnsureFastbootForWipeAsync())
                    {
                        RomStepText = "Clean install stopped at the wipe step — nothing was installed.";
                        return;
                    }

                    Log("[Wipe] CLEAN INSTALL requested — erasing userdata and cache before the recovery install...");
                    var wipe = await _flashService.FactoryWipeAsync(
                        CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);

                    if (!wipe.Success)
                    {
                        Log($"[ERROR] Wipe failed: {wipe.Message}");
                        RomStepText = "Clean install failed at the wipe step — nothing was installed.";
                        return;
                    }

                    Log("[✓] User data erased.");
                    UpdateProgress(15, "User data erased");
                }
                finally
                {
                    IsFlashing = false;
                    StopFlashStopwatch();
                }
            }

            RomStepText = $"STEP 3: Installing {Path.GetFileName(SelectedFilePath)} from recovery — keep the cable connected.";
            AdditionalStepText = "A ROM install is running through recovery/sideload.";
            Log($"[ROM] Installing {Path.GetFileName(SelectedFilePath)} through recovery (adb sideload)...");

            var applied = await SideloadUpdateAsync(SelectedFilePath, allowRebootChain: true);
            RomStepText = applied
                ? "STEP 4: ROM installed through recovery. Reboot to system (ADDITIONAL FILES → FINISH & REBOOT) and check that it boots."
                : "ROM install did not complete — check the LOGS tab. The phone stays in recovery, so it can be retried.";
        }

        /// <summary>
        /// Get the device into fastboot so a factory wipe can run, rebooting it
        /// there when it is currently reachable over ADB.
        /// </summary>
        private async Task<bool> EnsureFastbootForWipeAsync()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            var mode = await _flashService.DetectModeAsync(CurrentDeviceSerial, token);

            if (mode == "Fastboot" || mode == "FastbootD")
                return true;

            if (mode == "AdbSystem" || mode == "AdbRecovery" || mode == "Sideload")
            {
                Log("[Wipe] Rebooting the device to the bootloader first...");
                var res = await _flashService.RebootToBootloaderAsync(CurrentDeviceSerial, token);
                if (!res.Success)
                {
                    Log($"[ERROR] Reboot to bootloader failed: {res.Message}");
                    return false;
                }

                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(1500);
                    var detected = await _flashService.DetectModeAsync(CurrentDeviceSerial, token);
                    if (detected == "Fastboot" || detected == "FastbootD")
                    {
                        Log("[Wipe] Device reached fastboot mode.");
                        return true;
                    }
                }

                Log("[ERROR] Device did not reach fastboot mode within 30 seconds.");
                return false;
            }

            Log("[Wipe] Device is not reachable — connect it over ADB or fastboot and retry.");
            return false;
        }

        /// <summary>
        /// Whitelist for partition names derived from additional image files.
        /// </summary>
        private static readonly Regex SafePartitionRegex =
            new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// Flash additional image files via fastboot. Each file is flashed to
        /// the partition matching its file name (e.g. boot.img -> boot).
        /// </summary>
        // ==================== P1: what the last additional flash wrote ====================
        public System.Collections.ObjectModel.ObservableCollection<string> AdditionalLastMapping { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();
        public Visibility AdditionalLastMappingVisibility
            => AdditionalLastMapping.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        private void RaiseAdditionalMapping()
        {
            OnPropertyChanged(nameof(AdditionalLastMappingVisibility));
            OnPropertyChanged(nameof(AdditionalMappingHeadline));
        }
        public string AdditionalMappingHeadline
            => AdditionalLastMapping.Count > 1 ? $"{AdditionalLastMapping.Count} IMAGES FLASHED" : "IMAGE FLASHED";

        public async Task FlashAdditionalFilesAsync(IEnumerable<string> filePaths)
        {
            if (IsFlashing)
            {
                Log("[Files] A flash operation is already in progress.");
                return;
            }

            var images = new List<KeyValuePair<string, string>>();
            foreach (var path in filePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    Log($"[ERROR] Additional file not found: {path}");
                    return;
                }

                var partition = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (!SafePartitionRegex.IsMatch(partition))
                {
                    Log($"[ERROR] Cannot derive a valid partition name from '{Path.GetFileName(path)}'. " +
                        "Rename the file to match the partition (e.g. boot.img). ");
                    return;
                }

                images.Add(new KeyValuePair<string, string>(partition, path));
            }

            if (images.Count == 0)
            {
                Log("[Files] No additional files to flash.");
                return;
            }

            // Record the mapping for the transparency chips on the ADDITIONAL FILES tab.
            AdditionalLastMapping.Clear();
            foreach (var img in images)
                AdditionalLastMapping.Add($"{System.IO.Path.GetFileName(img.Value)}  →  {img.Key}");
            RaiseAdditionalMapping();

            IsFlashing = true;
            StartFlashStopwatch();
            try
            {
                // One-press chain: reach bootloader automatically instead of
                // sending the user to REBOOT TO BOOTLOADER and back.
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }

                var token = _cts?.Token ?? CancellationToken.None;
                for (int i = 0; i < images.Count; i++)
                {
                    var partition = images[i].Key;
                    var imagePath = images[i].Value;
                    UpdateProgress((double)i / images.Count * 100, $"[{partition}] Flashing ({i + 1}/{images.Count})...");
                    Log($"[Files] Flashing {Path.GetFileName(imagePath)} -> {partition}");

                    var result = await _flashService.FlashPartitionAsync(CurrentDeviceSerial, partition, imagePath, null, token);
                    if (!result.Success)
                    {
                        Log($"[ERROR] {result.Message}");
                        if (!string.IsNullOrWhiteSpace(result.TechnicalDetails))
                            Log($"    {result.TechnicalDetails}");
                        AdditionalStepText = $"Additional flash failed on '{partition}' — check the LOGS tab.";
                        UpdateProgress(0, "Additional flash failed");
                        return;
                    }
                }

                UpdateProgress(100, "Additional files flashed");
                AdditionalStepText = "Additional files flashed successfully — you can wipe cache or finish & reboot.";
                Log("[✓] All additional files flashed successfully.");
            }
            catch (OperationCanceledException)
            {
                Log("[Files] Cancelled by user.");
                UpdateProgress(0, "Cancelled");
                AdditionalStepText = "Additional flash cancelled.";
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
            }
        }

        /// <summary>
        /// Move a queued batch job up (-1) or down (+1) in the queue.
        /// </summary>
        public void MoveJobInBatch(int jobId, int direction)
        {
            if (!BatchOperationManager.Instance.MoveJob(jobId, direction))
                Log($"[Batch] Job {jobId} cannot be moved (running or already at the edge).");
        }

        /// <summary>
        /// Process all queued batch ROM jobs sequentially.
        /// </summary>
        public async Task ProcessBatchQueueAsync()
        {
            if (IsFlashing)
            {
                Log("[Batch] A flash operation is already in progress.");
                return;
            }

            var pending = BatchOperationManager.Instance.GetNextPendingJob();
            if (pending == null)
            {
                Log("[Batch] No queued jobs to process.");
                return;
            }

            // Battery pre-flight for the whole queue (only possible over ADB;
            // once flashing starts the device moves to fastboot where it cannot
            // be queried).
            var adbMode = _currentPhoneMode == DeviceMode.AdbSystem ||
                          _currentPhoneMode == DeviceMode.AdbRecovery ||
                          _currentPhoneMode == DeviceMode.Sideload;
            if (adbMode)
            {
                var batteryLevel = await _flashService.GetBatteryLevelAsync(
                    CurrentDeviceSerial, CancellationToken.None);
                if (batteryLevel.HasValue && batteryLevel.Value < BatteryThreshold)
                {
                    Log($"[Batch] Battery too low to start the queue: {batteryLevel.Value}% " +
                        $"(minimum {BatteryThreshold}%). Charge the device and try again.");
                    return;
                }
            }

            IsFlashing = true;
            _batchCts = new CancellationTokenSource();
            StartFlashStopwatch();
            try
            {
                await BatchOperationManager.Instance.ProcessQueueAsync(
                    async (job, ct) =>
                    {
                        // A job flashes exactly the partitions recorded when it was
                        // queued. Entries that recorded none are refused rather than
                        // filled in from the current UI, so the queue can never flash
                        // a set the UI does not show.
                        var partitions = job.SelectedPartitions ?? new List<string>();
                        if (partitions.Count == 0)
                        {
                            Log($"[Batch] Job {job.Id} has no recorded partitions — remove and re-add it. Skipping.");
                            return false;
                        }

                        // A package whose images live inside a payload.bin installs
                        // itself from recovery. Writing its boot chain here would
                        // report a success that did not install a ROM, so refuse it.
                        if (_romValidator.ReadZipFacts(job.RomFilePath).HasPayloadBin)
                        {
                            Log($"[Batch] Refusing job {job.Id}: {job.RomFileName} is a payload ROM — " +
                                "it installs from recovery, not over fastboot. Remove it from the queue and use " +
                                "INSTALL FROM RECOVERY on the ROM tab.");
                            return false;
                        }

                        // Never flash a package built for another phone: that is the
                        // one mistake fastboot cannot recover from. Jobs queued
                        // before this check existed carry no identifiers and pass.
                        var jobDevices = job.DeviceIdentifiers ?? new string[0];
                        if (jobDevices.Length > 0 && !string.IsNullOrWhiteSpace(DeviceCodename) &&
                            !RomValidator.MatchesDevice(jobDevices, DeviceCodename))
                        {
                            Log($"[Batch] Refusing job {job.Id}: package is built for " +
                                $"{string.Join(", ", jobDevices)}, connected device is {DeviceCodename}. Stopping the queue.");
                            _batchCts?.Cancel();
                            return false;
                        }

                        // A reboot takes the device out of fastboot, so a job's
                        // post-flash destination is honoured only when nothing is
                        // left to flash after it.
                        var stillQueued = BatchOperationManager.Instance.GetQueuedJobCount();
                        var postFlash = stillQueued == 0 ? job.PostFlashTarget : null;

                        // Record the decision so the row keeps telling the truth
                        // after later jobs finish.
                        job.PostFlashApplied = !string.IsNullOrWhiteSpace(postFlash);
                        if (stillQueued > 0 && !string.IsNullOrWhiteSpace(job.PostFlashTarget))
                        {
                            Log($"[Batch] Job {job.Id}: staying in fastboot instead of rebooting to " +
                                $"{job.PostFlashTarget} — {stillQueued} job(s) still queued.");
                        }

                        Log($"[Batch] Starting job {job.Id}: {job.RomFileName} -> {string.Join(", ", partitions)}");
                        var context = new FlashContext
                        {
                            Serial = CurrentDeviceSerial,
                            RomFilePath = job.RomFilePath,
                            SelectedPartitions = partitions,
                            WipeCache = true,
                            RebootAfterFlash = true,
                            // Replay the options captured when the job was queued.
                            TargetSlot = job.TargetSlot,
                            DisableVerity = job.DisableVerity,
                            WipeData = job.WipeData,
                            PostFlashTarget = postFlash
                        };

                        var result = await _flashService.FlashRomAsync(context, ct);
                        if (result.Success)
                            Log($"[Batch] Job {job.Id} completed: {result.Message}");
                        else
                            Log($"[Batch] Job {job.Id} failed: {result.Message}");
                        return result.Success;
                    },
                    _batchCts.Token);
            }
            finally
            {
                IsFlashing = false;
                StopFlashStopwatch();
                _batchCts?.Dispose();
                _batchCts = null;
            }
        }

        /// <summary>
        /// Abort an in-progress batch queue.
        /// </summary>
        public void AbortBatch()
        {
            _batchCts?.Cancel();
            Log("[Batch] Abort requested.");
        }

        /// <summary>
        /// Reboot the connected device to the bootloader.
        /// </summary>
        public async Task RebootToBootloaderAsync()
        {
            if (IsFlashing) return;
            IsFlashing = true;
            try
            {
                var result = await _flashService.RebootToBootloaderAsync(CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
            }
            finally
            {
                IsFlashing = false;
            }
        }

        /// <summary>
        /// Reboot the connected device to recovery.
        /// Uses fastboot when the device is in fastboot mode, adb otherwise.
        /// </summary>
        public async Task RebootToRecoveryAsync()
        {
            if (IsFlashing) return;
            IsFlashing = true;
            try
            {
                var result = IsFastbootMode
                    ? await _flashService.RebootAsync(CurrentDeviceSerial, "recovery", _cts?.Token ?? CancellationToken.None)
                    : await _flashService.RebootViaAdbAsync(CurrentDeviceSerial, "recovery", _cts?.Token ?? CancellationToken.None);
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
            }
            finally
            {
                IsFlashing = false;
            }
        }

        /// <summary>
        /// Wipe the cache partition. Requires the device to be in fastboot mode.
        /// </summary>
        public async Task WipeCacheAsync()
        {
            if (IsFlashing) return;
            // In a TWRP/OrangeFox recovery: cache + dalvik via the CLI.
            if (_currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode)
            {
                IsFlashing = true;
                try
                {
                    Log("[Cache] Wiping cache and dalvik via the twrp CLI...");
                    var output = await GetCombinedToolOutputAsync(
                        ToolPathResolver.GetToolPath("adb"),
                        "-s \"" + CurrentDeviceSerial + "\" shell twrp wipe cache && -s \"" + CurrentDeviceSerial + "\" shell twrp wipe dalvik",
                        TimeSpan.FromMinutes(5), AbortToken);
                    var oLower = (output ?? "").ToLowerInvariant();
                    var failed = oLower.Contains("failed") || (output ?? "").Contains("E:");
                    Log(!string.IsNullOrWhiteSpace(output) ? output.Trim() : "[Cache] (no CLI output)");
                    if (!failed)
                        Log("[✓] Cache and dalvik wiped via the twrp CLI.");
                    else
                        ShowToast("Error", "Wipe failed", "The twrp CLI reported a problem — check the log.");
                }
                finally
                {
                    IsFlashing = false;
                }
                return;
            }
            IsFlashing = true;
            try
            {
                // One-press chain: reach bootloader automatically instead of
                // asking for a manual REBOOT TO BOOTLOADER first.
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    return;
                }
                var result = await _flashService.WipeCacheAsync(CurrentDeviceSerial, _cts?.Token ?? CancellationToken.None);
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
            }
            finally
            {
                IsFlashing = false;
            }
        }

        /// <summary>
        /// Finish the session and reboot to system.
        /// Uses fastboot when the device is in fastboot mode, adb otherwise.
        /// </summary>
        public async Task FinishAndRebootAsync()
        {
            if (IsFlashing) return;
            // In a TWRP/OrangeFox recovery the twrp CLI does the reboot —
            // TWRP itself never reboots on its own.
            if (_currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode)
            {
                IsFlashing = true;
                try
                {
                    Log("[Finish] Rebooting to system via the twrp CLI...");
                    var output = await GetCombinedToolOutputAsync(
                        ToolPathResolver.GetToolPath("adb"),
                        "-s \"" + CurrentDeviceSerial + "\" shell twrp reboot system",
                        TimeSpan.FromSeconds(30), AbortToken);
                    Log(!string.IsNullOrWhiteSpace(output) ? output.Trim() : "[Finish] (no CLI output)");
                    Log("[✓] Reboot requested — the monitor completes the session when the system is up.");
                }
                finally
                {
                    IsFlashing = false;
                }
                return;
            }
            // In sideload the recovery is busy holding the ADB sideload link —
            // the twrp CLI may not respond; a plain adb reboot works from there.
            if (_currentPhoneMode == DeviceMode.Sideload)
                Log("[Finish] Leaving sideload — rebooting to system via adb...");
            IsFlashing = true;
            try
            {
                // A stock recovery may hold ADB unauthorized until the user
                // accepts its prompt — retry while they do.
                for (int attempt = 1; ; attempt++)
                {
                    FlashResult result;
                    if (IsFastbootMode)
                        result = await _flashService.RebootAsync(CurrentDeviceSerial, "system", _cts?.Token ?? CancellationToken.None);
                    else
                        result = await _flashService.RebootViaAdbAsync(CurrentDeviceSerial, "system", _cts?.Token ?? CancellationToken.None);
                    var lower = (result.Message ?? "").ToLowerInvariant();
                    var unauthorized = !result.Success &&
                        (lower.Contains("unauthorized") || lower.Contains("not authorized"));
                    if (!unauthorized || attempt >= 3)
                    {
                        Log(result.Success ? $"[✓] {result.Message}"
                            : $"[ERROR] {result.Message}" + (unauthorized
                                ? " — accept the debugging prompt on the phone screen, then try REBOOT TO SYSTEM again."
                                : ""));
                        break;
                    }
                    Log("[Finish] The recovery's ADB is not authorized — accept the prompt on the phone screen. Retrying in 10 seconds (" + attempt + " of 3)...");
                    await System.Threading.Tasks.Task.Delay(10000, AbortToken);
                }
            }
            finally
            {
                IsFlashing = false;
            }
        }

        #endregion

        /// <summary>
        /// Reboot an ADB-connected device into sideload mode (recovery → Apply
        /// update from ADB), so APPLY UPDATE.ZIP (SIDELOAD) becomes usable.
        /// </summary>
        public async Task EnterSideloadModeAsync()
        {
            if (IsFlashing) return;
            if (!IsModeAdb)
            {
                Log("[Sideload] Entering sideload requires a device in ADB mode (system/recovery/sideload).");
                return;
            }

            IsFlashing = true;
            try
            {
                var result = await _flashService.RebootViaAdbAsync(CurrentDeviceSerial, "sideload",
                    _cts?.Token ?? CancellationToken.None);
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
                if (result.Success)
                {
                    RomStepText = "Device is entering sideload mode — when it appears, use APPLY UPDATE.ZIP (SIDELOAD) on the ADDITIONAL FILES tab.";
                }
            }
            finally
            {
                IsFlashing = false;
            }
        }

        /// <summary>
        /// Full factory wipe (fastboot -w): erases userdata + cache.
        /// The caller (code-behind) is responsible for the confirmation dialog.
        /// </summary>
        public async Task FactoryWipeAsync()
        {
            if (IsFlashing) return;
            // In a TWRP/OrangeFox recovery: FORMAT DATA via the CLI — stronger
            // than fastboot -w: it also removes encryption keys and the
            // screen lock (needed when a locked /data blocks everything).
            if (_currentPhoneMode == DeviceMode.AdbRecovery && RecoveryInRecoveryMode)
            {
                IsFlashing = true;
                RomStepText = "Formatting data via the twrp CLI — EVERYTHING is being erased, including encryption keys...";
                try
                {
                    Log("[Wipe] twrp format data — erases userdata AND encryption keys / screen lock.");
                    var output = await GetCombinedToolOutputAsync(
                        ToolPathResolver.GetToolPath("adb"),
                        "-s \"" + CurrentDeviceSerial + "\" shell twrp format data",
                        TimeSpan.FromMinutes(10), AbortToken);
                    var oLower = (output ?? "").ToLowerInvariant();
                    var failed = oLower.Contains("failed") || (output ?? "").Contains("E:");
                    Log(!string.IsNullOrWhiteSpace(output) ? output.Trim() : "[Wipe] (no CLI output)");
                    if (!failed)
                    {
                        Log("[✓] Data formatted. Wipe cache/dalvik too, then REBOOT TO SYSTEM and set the phone up fresh.");
                        ShowToast("Success", "Data formatted",
                            "Encryption keys removed — the phone boots unencrypted until the ROM re-encrypts.");
                    }
                    else
                        ShowToast("Error", "Format failed", "The twrp CLI reported a problem — check the log.");
                }
                finally
                {
                    IsFlashing = false;
                }
                return;
            }
            IsFlashing = true;
            RomStepText = "Factory wipe in progress — userdata and cache are being erased...";
            try
            {
                // One-press chain: reach bootloader automatically instead of
                // asking for a manual REBOOT TO BOOTLOADER first.
                if (!IsFastbootMode && !await EnsureFastbootForWipeAsync())
                {
                    ShowToast("Error", "No fastboot device", "Could not reach the bootloader — check the LOGS tab.");
                    RomStepText = "Factory wipe stopped — the bootloader was not reachable.";
                    return;
                }
                var result = await _flashService.FactoryWipeAsync(CurrentDeviceSerial,
                    _cts?.Token ?? CancellationToken.None);
                Log(result.Success ? $"[✓] {result.Message}" : $"[ERROR] {result.Message}");
                if (result.Success && !string.IsNullOrWhiteSpace(result.TechnicalDetails))
                    Log($"    {result.TechnicalDetails}");
                RomStepText = result.Success
                    ? "Factory wipe complete — the device is ready for a different ROM family (e.g. AOSP <-> HyperOS)."
                    : "Factory wipe failed — check the LOGS tab for details.";
            }
            finally
            {
                IsFlashing = false;
            }
        }

        // ==================== Guided cross-ROM recipes (R4) ====================
        private string _selectedRecipe = "None";
        public string SelectedRecipe
        {
            get => _selectedRecipe;
            set
            {
                if (SetField(ref _selectedRecipe, value))
                {
                    RecipeText = BuildRecipeText(value);
                }
            }
        }

        private string _recipeText = "";
        public string RecipeText
        {
            get => _recipeText;
            private set => SetField(ref _recipeText, value);
        }

        public static readonly string[] RecipeChoices =
        {
            "None", "AOSP → HyperOS/MIUI", "HyperOS/MIUI → AOSP", "Install a custom recovery"
        };

        private string BuildRecipeText(string recipe)
        {
            switch (recipe)
            {
                case "AOSP → HyperOS/MIUI":
                    return "AOSP → HyperOS/MIUI:\n" +
                        "1. Tick 'wipe data before flash' (CLEAN install) — MIUI/HyperOS needs a clean data partition.\n" +
                        "2. Select the HyperOS fastboot .tgz package (TARGET SLOT / VBMETA DISABLE VERITY as needed) and FLASH.\n" +
                        "3. Check ANTI-ROLLBACK in the device card — flashing older firmware than the anti version is irreversible.\n" +
                        "4. If the ROM boots, install TWRP/OrangeFox via CUSTOM RECOVERY if you still want it.";

                case "HyperOS/MIUI → AOSP":
                    return "HyperOS/MIUI → AOSP:\n" +
                        "1. Tick 'wipe data before flash' (CLEAN install) — custom ROMs need a clean data partition.\n" +
                        "2. Select the AOSP ROM zip (payload.bin), pick partitions, tick VBMETA DISABLE VERITY (anti-bootloop), then FLASH.\n" +
                        "3. To sideload extras: install/boot TWRP via CUSTOM RECOVERY, reboot to recovery, ENTER SIDELOAD MODE, then APPLY UPDATE.ZIP (SIDELOAD).\n" +
                        "4. FINISH & REBOOT when done.";

                case "Install a custom recovery":
                    return "Install a custom recovery (TWRP / OrangeFox):\n" +
                        "1. Select the recovery .img below.\n" +
                        "2. BOOT RECOVERY to test it temporarily, or FLASH RECOVERY to install permanently.\n" +
                        "3. Reboot to recovery → choose 'Apply update from ADB'.\n" +
                        "4. ENTER SIDELOAD MODE here, then APPLY UPDATE.ZIP (SIDELOAD) on the Additional Files tab.";

                default:
                    return "";
            }
        }

        /// <summary>
        /// Add a ROM to the batch queue with the partitions currently selected in the UI.
        /// </summary>
        public void AddJobToBatch(string romFilePath)
        {
            var job = BatchOperationManager.Instance.AddJob(romFilePath, GetSelectedPartitions(),
                SlotChoice == "Auto" ? null : SlotChoice, ChkVbmetaVerityOff,
                ChkWipeDataBeforeFlash, null, _romDeviceIdentifiers);
            if (job != null)
            {
                Log($"[Batch] Queued {job.RomFileName} (job {job.Id}) with " +
                    (job.SelectedPartitions?.Count ?? 0) + " partition(s).");
            }
        }

        /// <summary>
        /// Removes one queued job by id (running jobs are refused by the manager).
        /// </summary>
        public void RemoveJobFromBatch(int jobId)
        {
            BatchOperationManager.Instance.RemoveJob(jobId);
            Log($"[Batch] Job {jobId} removed from the queue.");
        }

        /// <summary>
        /// Clears completed/failed/cancelled jobs from the queue.
        /// </summary>
        public void ClearCompletedBatchJobs()
        {
            BatchOperationManager.Instance.ClearCompleted();
            Log("[Batch] Completed jobs cleared from the queue.");
        }
    }
}
