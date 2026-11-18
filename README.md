# Acer Battery Control

80% charge limit and battery calibration for Acer laptops on Windows, **without Acer Care Center**.
A system service keeps the limit in place, a notification-area icon shows the battery state and the `acerbat`
command controls everything from the command line.

![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
![Charge limit](https://img.shields.io/badge/charge%20limit-tested-2EA043)
![Calibration](https://img.shields.io/badge/calibration-tested-2EA043)

**[⬇ Download AcerBatteryControl-2.2.0.zip](https://github.com/Third-Corinthian/acer-battery-control/releases/download/v2.2.0/AcerBatteryControl-2.2.0.zip)**
(about 110 KB, no installer framework, no dependencies) · [all releases](https://github.com/Third-Corinthian/acer-battery-control/releases)

> [!WARNING]
> **Personal project, use at your own risk.** I wrote this for my own laptop and published it so that anyone can
> study it, adapt it or use it. It writes to the battery firmware and changes power settings, and it comes with
> no warranty and no promise of support. Read the [disclaimer](#disclaimer) before installing.
>
> **Status.** Version 2.2.0 passes automated tests in simulated mode. On a Nitro 5 AN515-55 the charge limit
> works: charging stops at 80%, and every on/off switch is confirmed by the firmware. Battery calibration has been
> run too: the battery gauge was recalibrated and the limit came back on by itself afterwards. The new critical-action
> guard and automatic safety stop still need hardware validation: see [Calibration](#calibration).

## Contents

- [Disclaimer](#disclaimer)
- [Why](#why)
- [Features](#features)
- [Compatibility](#compatibility)
- [Installation](#installation)
- [Usage](#usage)
- [How the limit is kept](#how-the-limit-is-kept)
- [Calibration](#calibration)
- [Security](#security)
- [Uninstalling](#uninstalling)
- [Building from source](#building-from-source)
- [Known limitations](#known-limitations)
- [Credits and license](#credits-and-license)

## Disclaimer

This software was written for personal use, on one specific laptop (an Acer Nitro 5 AN515-55). It is made public
so that everyone can read the code, check how it works, customize it and, if they want, use it, **entirely at
their own risk**.

- It runs as a Windows system service and writes to the battery firmware through an interface that Acer does not
  document publicly. A firmware that behaves differently from the one it was written on can react in unexpected
  ways.
- It is provided "as is", without warranty of any kind, as stated in the [GPL-3.0](LICENSE) license (sections 15
  and 16). The author is not liable for damage to hardware, batteries or data, or for any other consequence of
  its use.
- There is no promise of support, fixes or updates. Issues and pull requests are welcome but may go unanswered.
- It is an independent project, **not affiliated with, endorsed or supported by Acer Inc.**

If in doubt, run `acerbat probe` first (read-only) and read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) to see
exactly what the program writes and when.

## Why

Acer firmware can stop charging at 80% to extend battery life, but the only official way to turn it on is Acer
Care Center, which on several models (Nitro included) resets the limit to 100% at every shutdown. This project
keeps the desired state on its own and re-applies it whenever something changes it.

## Features

- **Persistent 80% limit**: checked at startup, on resume from sleep, on power source changes and every
  30 minutes. It writes only after two consistent firmware reads and verifies by reading back.
- **Calibration**, started by hand after a clear warning, with automatic sleep blocked, AC lid and critical-battery
  actions guarded, and an automatic low-charge safety stop. Original power settings are journaled for recovery.
- **Telemetry**: charge, power, voltage and current, temperature, capacity and health, cycle count, conflicts
  with Acer services.
- **Icon and panel** that fit Windows 11: light/dark theme, high DPI, status colors, notifications, service
  state with Stop/Start commands.
- **`acerbat` CLI** for status, commands, log and firmware diagnostics.
- **No dependencies**: a ~110 KB executable for .NET Framework 4.8, which ships with Windows.
- **Robustness**: WMI calls with timeouts, protected and authenticated command pipe, protected data folder,
  no firmware writes in uncertain states.

## Compatibility

You need an Acer laptop whose BIOS exposes the WMI class `BatteryControl` (`root\wmi`,
GUID `79772EC5-04B1-4BFD-843C-61E7F77B6CC9`). `acerbat probe` checks this without writing anything.

System: 64-bit Windows 10 or 11.

### Tested with this program

| Model | Result |
|---|---|
| Nitro 5 AN515-55 (AN515-55-71UB), Windows 11 | Charge limit works: charging stops at 80%, every on/off switch confirmed by the firmware. Calibration works: full charge, discharge, battery gauge recalibrated, limit restored afterwards |

### Probably compatible

The two Linux projects below drive the **same firmware interface** (same WMI GUID and methods) and their users
report the charge limit working on these models. This program has **not** been tested on them: they will most
likely work, but check with `acerbat probe` first. Model names are as reported by their users.

| Line | Models | Source |
|---|---|---|
| Nitro 5 | AN515-44, AN515-57, AN515-58, AN517-54 | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Nitro V | ANV15-51 · ANV16-42 | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Nitro V | ANV15-52 (BIOS V1.31; charge limit and calibration exposed) | [acerctl](https://github.com/FnnDK/acerctl#tested-models) |
| Nitro 16 | AN16-43 | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Predator | Helios Neo 16 (PHN16-71), Triton 300 SE | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Aspire 3 | A315-24PT, A315-44P, A315-59, A315-510P | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Aspire 5 / 7 | A515-45, A515-46, A715-42G | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Aspire Go / Vero | AG15-42P · AV15-53P | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Aspire Go | AG15-71P (BIOS V1.13; charge limit and calibration exposed) | [acerctl](https://github.com/FnnDK/acerctl#tested-models) |
| Swift | SF314-34, SF314-43, Edge SFE16-44, Go 14 SFG14-63, Go SFG16-72, X SFX14-71G, X 16 SFX16-61G | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |
| Enduro | N3 Urban EUN314A-51W | [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md) |

### Reported not working

Nitro 5 AN515-47, Aspire 3 A315-56, Aspire E15 and E17, TravelMate P259-62-M
([source](https://github.com/frederik-h/acer-wmi-battery/blob/main/MODELS.md)). If the firmware interface is missing,
setup detects it and refuses to install; if it is present but behaves differently, the program may not work
correctly.

Tried it on another model? Open an issue with the output of `acerbat probe`, whether it worked or not: it helps
the next person.

## Installation

1. Download `AcerBatteryControl-<version>.zip` from the [Releases](https://github.com/Third-Corinthian/acer-battery-control/releases)
   page and, if you like, compare its hash with `SHA256SUMS.txt` (`Get-FileHash .\AcerBatteryControl-2.2.0.zip`).
2. Extract the zip to any folder.
3. Optional but recommended: from an **administrator** prompt in that folder run `acerbat probe`.
   It should print `BatteryControl found.` and `80% limit   supported: True`.
4. Close Acer Care Center if it is open.
5. Double-click `AcerBatteryControl.exe` and answer **Yes**; UAC confirmation is required. Setup:
   - copies the files to `C:\Program Files\AcerBatteryControl\`;
   - registers the "Acer Battery Control" service (automatic start, system account, restarted if it stops);
   - creates `C:\ProgramData\AcerBatteryControl\`, writable only by SYSTEM and administrators;
   - starts the icon at every sign-in and adds an entry under **Settings > Apps > Installed apps**;
   - removes the `AcerChargeLimit` task, if present, to prevent conflicting limit changes.

Windows SmartScreen may warn you because the executables are not signed ("More info" > "Run anyway").
Alternatively, [build them yourself](#building-from-source).

## Usage

### Icon

| Color | Meaning |
|---|---|
| Green | 80% limit active in the firmware |
| Orange | Limit off, or being corrected |
| Blue | Calibration in progress |
| Grey | Firmware not ready or state unknown |
| Red | Service stopped: the limit is not being kept |
| Grey with a red X | Service unreachable |

- **Left click**: status panel with all the data, the "Services" section and the command buttons.
- **Right click**: limit on/off, calibration, panel, log, stop/start the service (with UAC), "About and
  credits", close the icon. Closing the icon does not stop the service.
- **Notifications** when the limit is corrected, calibration starts or ends, the charger is unplugged during
  calibration, or the service stops responding.

### Command line

`acerbat.exe` is in `C:\Program Files\AcerBatteryControl\`.

```
acerbat status                 full status
acerbat limit on|off           80% charge limit
acerbat calibrate start|stop   calibration (charger plugged in)
acerbat enforce                check the limit right now
acerbat log [lines]            last lines of the log
acerbat probe                  direct firmware test, without the service (administrator)
acerbat install|uninstall      install/remove service and icon (administrator)
acerbat uninstall --force      remove even with a lost recovery journal (administrator)
```

## How the limit is kept

| When | Check |
|---|---|
| Service start | after 20 s, 2, 5 and 10 minutes (catches whatever resets the limit after sign-in) |
| Resume from sleep or hibernation | after 20 s |
| Power source change | after 5 s |
| Steady state | every 30 minutes |
| Calibration in progress or firmware not ready | every 60 s |

- The firmware is written only when its state really differs from the desired one, confirmed by **two
  consistent reads**: concurrent WMI calls to the Acer firmware can return wrong values.
- If the firmware reports no mode at all (a transient state) nothing is written and the check is retried.
- Every write is verified by reading back; the log records only changes, errors and abnormal states.

If corrections show up at every start, something else is resetting the limit, usually Acer Care Center: the
panel lists the running Acer services under "Possible conflicts".

## Calibration

Calibration makes the charge indicator accurate again. Lithium batteries have no memory effect, so it is not a
way to "train" the battery. The firmware charges the battery to 100%, discharges it with the system running and
charges it again: it takes several hours with the charger always plugged in. It is started **only by hand**.

Before starting, the tray asks for confirmation and `acerbat calibrate start` prints the same explanation without
an interactive prompt. It shows the current AC critical-battery action and level, for example "Hibernate at 5%",
read without changing any settings.

During calibration the service leaves the limit untouched, blocks automatic sleep, and temporarily sets both
the lid-close action and the critical-battery action to **Do nothing on AC power only**. The original actions and
their power schemes are saved before changes. Battery-power (DC) settings and critical battery levels are never
changed. Keep the charger connected; do not shut down, restart or put the PC to sleep.

During calibration the firmware charges the battery to 100% even when the 80% limit is on: this is expected.

If the firmware is still calibrating and discharging at **3% or below on two consecutive valid readings**, the
service stops calibration using confirmed firmware reads and readback. It restores the power settings and resumes
normal limit control so charging can resume, limited to 80% if enabled. A tray notification explains the stop.
If the firmware begins recharging first, it is allowed to finish. The safety stop deliberately ends calibration
before a full firmware discharge/recharge cycle; its effect on gauge accuracy needs hardware testing.

If the critical action cannot be changed, the fallback floor is the current AC critical level plus two percentage
points (at least 3%, at most 100%). If that level cannot be read, Windows protection is retained and calibration
is stopped on two valid discharge readings. Polling is normally every 60 seconds, every 15 seconds at 10% or
below, at the fallback floor, or when telemetry is invalid.

Invalid telemetry never triggers a firmware stop. If valid data is missing for more than two minutes, the service
restores the original critical action and keeps it for the rest of that calibration, even if telemetry returns.
This rule also applies when the last known charge was above 10%, because an unseen discharge could cross it.
Windows may then sleep or shut down; its protection takes priority over finishing calibration. Restore failures
retain the journal and are retried. Settings are also restored at normal completion, manual stop, service stop,
Windows shutdown, and recovery after a crash at the next service start.

## Security

The service runs as LocalSystem. In short:

- the command pipe refuses network access and accepts only short, complete requests; the tray and the CLI check
  that the other end really is the service process;
- the data folder is owned by the administrators; an untrusted pre-existing folder is moved aside, never repaired
  in place, and links and hardlinks are refused;
- starting and stopping the service from the tray goes through UAC.

To report a security issue see [SECURITY.md](SECURITY.md).

## Uninstalling

From **Settings > Apps > Installed apps > Acer Battery Control > Uninstall**, or run
`AcerBatteryControl.exe uninstall` / `acerbat uninstall` from an administrator prompt. This removes the service,
the icon autostart, the Installed apps entry and the folders.

Uninstalling refuses, and leaves the service running, if a calibration is in progress, a lid or critical-action
restore is pending or the service does not respond. If the state file (`config.ini`) has been lost, check and
restore both AC actions in Power Options before using `AcerBatteryControl.exe uninstall --force`. This override
accepts a missing, invalid or pending journal and unreadable firmware state; it never bypasses calibration
reported active by the service or firmware. It does not restore settings on your behalf.

The firmware state is left as it is: to go back to charging to 100%, run `acerbat limit off` first.

## Building from source

On Windows, in the repository folder:

```
build.cmd
```

It uses the .NET Framework `csc.exe` that ships with Windows: no SDK to install. The code is C# 5.
To build and run the tests in simulated mode, without touching the firmware or the system:

```
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

Developer documentation:

| File | Content |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | components, enforcement rules, scheduling, IPC, installation |
| [docs/FIRMWARE.md](docs/FIRMWARE.md) | the `BatteryControl` WMI interface, parameters, known behavior, sources |
| [docs/TESTING.md](docs/TESTING.md) | automated checks and hardware test checklist |

## Known limitations

- The 3% safety stop and subsequent charging have been tested with simulated hardware only. A sudden charge drop,
  stalled driver, firmware cutoff, policy override or failed restore can still cause shutdown. The two-minute
  telemetry timeout deliberately returns control to Windows; it cannot guarantee uninterrupted calibration.
- The firmware offers only the fixed 80% threshold.
- On the AN515-55 the cycle count is not reported.
- Sleep started by hand during a calibration is not blocked; stopping the service from `services.msc` during a
  calibration removes the protections.
- Fans, performance profiles and keyboard lighting are deliberately not handled.

## Credits and license

- The firmware interface is documented by [acerctl](https://github.com/FnnDK/acerctl) and
  [acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery). This project reuses their battery
  functions with code written from scratch for Windows.
- Released under the [GPL-3.0](LICENSE) license.
- Independent project, **not affiliated with or endorsed by Acer Inc.** "Acer" and "Nitro" are trademarks of
  their respective owners. The software comes with no warranty: use it at your own risk.
