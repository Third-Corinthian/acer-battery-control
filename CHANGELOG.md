# Changelog

All notable changes are recorded here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.2.0] — 2026-11-18

### Added

- Calibration warning in the tray and CLI showing the current AC critical-battery action and level.
- Journaled AC critical-action guard with readback, restore retries and recovery after a crash; DC settings stay unchanged.
- Automatic confirmed calibration stop after two valid discharge readings at or below 3%, followed by power-setting
  restoration, normal charge-limit enforcement and a tray notification.
- Fault-injection tests for safety floors, telemetry loss, power-setting failures, restart recovery and uninstall policy.

### Changed

- Calibration polling accelerates from 60 to 15 seconds at low or unknown charge and at the fallback floor.
- Failed critical-action suppression uses the current critical level plus two percentage points; unreadable levels
  stop calibration on confirmed discharge. More than two minutes without valid telemetry restores Windows protection.
- Uninstall and tray service-stop checks include pending critical-action recovery. Forced uninstall requires checking both AC actions.

### Fixed

- Calibration no longer silently relies on Windows' AC critical-battery action during the discharge phase.
  The guarded safety stop is intended to allow charging to resume before shutdown; hardware validation is pending.
- Double-clicking a newer `AcerBatteryControl.exe` while a version is installed did nothing (it exited silently
  when the tray icon was running). It now offers to update, closing the running icon and service.
- Installing or updating is refused while a calibration is reported in progress.

## [2.1.0] — 2026-11-17

### Added

- Persistent 80% charge limit with double-read confirmation before firmware writes and readback verification.
- Manual battery calibration with automatic sleep and lid-close guards, and crash-safe restoration.
- Tray icon and status panel with battery telemetry, notifications and service stop/start controls.
- `acerbat` CLI for status, charge-limit control, calibration, logs and firmware diagnostics.
- Installed-apps uninstall entry and `--force` for a missing or invalid recovery journal.
- WMI timeouts on separate firmware and telemetry channels.
- Authenticated command pipe with service-process verification, access controls and bounded requests.
- Protected data folder that rejects links and moves untrusted folders aside.
- Dry-run mode and `scripts/verify.ps1` for automated verification without hardware access.

[2.2.0]: https://github.com/Third-Corinthian/acer-battery-control/releases/tag/v2.2.0
[2.1.0]: https://github.com/Third-Corinthian/acer-battery-control/releases/tag/v2.1.0
