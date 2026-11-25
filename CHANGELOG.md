# Changelog

All notable changes to SR Flasher are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/) and the
project adheres to [Semantic Versioning](https://semver.org/) from v1.0 onward.

## [Unreleased]

### Added
- In-app update checker: queries the GitHub releases API at most once a
  day (toggleable in Settings) and flags newer versions in the About card
  with a direct link to the releases page. Manual CHECK NOW always goes
  online; automatic checks stay silent when offline. No device data is sent.

### Hardening
- Anti-rollback consent: when the device reports an ACTIVE anti version,
  flashing now asks with a danger dialog before writing anything (was:
  log-only). Background runs without a UI are refused (fail-closed).
- Both-slot boot-chain guard: flashing boot/init_boot/vendor_boot/dtbo to
  BOTH slots warns that the fallback slot is being removed and offers the
  safer active-slot switch.

## [1.0.0] — 2026-11

First public release.

### Added
- Payload-package flashing: `payload.bin` extraction via bundled
  payload-dumper-go, per-partition flash with wipe options and vbmeta
  disable-verity/verification.
- A/B device awareness: dual-slot detection, both-slot custom-recovery
  install and evidence-driven recovery repair with a pre-repair partition
  backup (kept per device serial under `Documents\SRFlasher`).
- Guided two-stage ROM install: boot-chain write → reboot to recovery →
  sideload apply → extras (GAPPS/Magisk) → first boot, with automatic mode
  chaining and unauthorized-prompt retry.
- One-press install chains and a whole-session progress contract
  (60 / 90 / 95 / 100) with the flashing footer visible through reboot
  stretches.
- Easy Flow step trail: CONNECT · MODE · FILE · FLASH · EXTRAS · FINISH.
- DEVICE dashboard: mode, active slot, bootloader state, battery,
  anti-rollback version, PC backup inventory; brand logo in a mini phone
  frame.
- Rescue tools with plain-language guidance and hard-brick honesty
  (EDL 9008 requires an authorized flasher).
- LOGS tab: full command output, level filters, copy/diagnostics bundle;
  adb server cleanup on app exit.
- SETTINGS: Dark/Light/OLED/Auto themes, reduce-motion toggle, expert mode.
- 59 regression tests covering naming, routing, staging rules and tool
  bundling.
- Release build auto-packages `SRFlasher_v1.0_Portable.zip`.
