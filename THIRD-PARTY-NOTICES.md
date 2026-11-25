# Third-Party Notices

SR Flasher bundles and builds upon the following projects. All are redistributed
under licenses that permit redistribution; this file credits them.

## Bundled executables (in `SRFlasher/tools/`)

### Android platform-tools (`adb.exe`, `fastboot.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`)
- Source: https://developer.android.com/tools/releases/platform-tools
- License: **Apache License 2.0** (portions BSD / Apache 2.0)
- Copyright © The Android Open Source Project
- https://android.googlesource.com/platform/system/core/+/main/NOTICE

### payload-dumper-go (`payload-dumper-go.exe`)
- Source: https://github.com/ssut/payload-dumper-go
- License: **Apache License 2.0**
- Copyright © ssut and contributors
- Used to extract partition images from OTA `payload.bin`.

## NuGet dependencies (restored at build time)

### Serilog (Serilog, Serilog.Sinks.Console, Serilog.Sinks.File)
- Source: https://serilog.net / https://github.com/serilog/serilog
- License: **Apache License 2.0**

### Microsoft.Bcl.AsyncInterfaces, System.Threading.Channels, System.Threading.Tasks.Extensions, System.Runtime.CompilerServices.Unsafe
- Source: https://github.com/dotnet/runtime (and related .NET Foundation projects)
- License: **MIT** (Copyright © .NET Foundation and Contributors)

## Brand assets

- The SR Flasher logo, icon and UI artwork in `brand/` and `SRFlasher/Assets/`
  are original works © 2026 SR, distributed under the repository's MIT license.
- The device-manufacturer logos in `SRFlasher/Logos/` (Samsung, Xiaomi/POCO,
  OnePlus, Google, etc.) are **trademarks of their respective owners**, used here
  solely and nominatively to identify which device a brand profile applies to.
  No affiliation with or endorsement by any manufacturer is implied.
