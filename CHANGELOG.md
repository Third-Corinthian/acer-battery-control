# Changelog

All notable changes are recorded here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[2.1.0]: https://github.com/Third-Corinthian/acer-battery-control/releases/tag/v2.1.0
