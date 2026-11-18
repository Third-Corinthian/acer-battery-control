# Architecture

## Goal and scope

It does the same things Acer Care Center does for the battery, and nothing more:

- 80% charge limit (the firmware calls this function "health mode");
- battery calibration, started manually;
- telemetry readout.

Care Center's weak point is precisely the persistence of the limit. On the Nitro 5 it is documented that the
limit goes back to 100% at every shutdown. This application does the opposite: it owns the desired state and
keeps reapplying it.

The features are those of the battery part of [acerctl](https://github.com/FnnDK/acerctl), a Linux project.
The code is not a port: on Linux acerctl goes through kernel modules and sysfs, here the firmware is called
directly via WMI.

## Components and processes

```
 interactive user                            LocalSystem
┌──────────────────────────┐   named pipe   ┌──────────────────────────────────┐
│ AcerBatteryControl.exe   │ ─────────────► │ AcerBatteryControl.exe --service │
│ (TrayContext, MainForm)  │  "STATUS" ...  │ BatteryService → Engine          │
├──────────────────────────┤                │   ├─ AcerWmi (root\wmi)          │
│ acerbat.exe (Cli)        │ ─────────────► │   ├─ CalibrationGuards (powrprof)│
└──────────────────────────┘                │   └─ Config / Log (ProgramData)  │
                                            └──────────────────────────────────┘
```

The same `AcerBatteryControl.exe` works as a service when launched with `--service`, as an installer with
`install`/`uninstall`, and as a tray icon with no arguments. `acerbat.exe` is compiled from the same sources
with `/define:CLI`.

### Why a service and not a scheduled task

The graphical interface needs a privileged process that is always reachable:

- WMI calls to the firmware and changes to the power plan require elevated privileges;
- the tray icon runs as a normal user.

A service with a named pipe solves both. In addition, it receives suspend, resume and power source change
events directly from SCM through `OnPowerEvent`.

### Why .NET Framework 4.8 and C# 5

- It is preinstalled on Windows 10 and 11: no runtime to distribute, executables of about 70 KB.
- It can be recompiled on the PC with the system `csc.exe`, so it can be verified without installing a toolchain.
- The system `csc.exe` supports at most C# 5. Hence the language constraint.
- Rejected alternatives: .NET 8 (runtime to install, or a self-contained executable of 60 MB or more),
  Python with pystray (not a "real program", it must be packaged), Go (GUI and WMI calls with arrays
  too awkward).

## Engine: enforcement rules

Desired state: `config.ini`, key `limit=on|off`. Default `on`.

`Engine.Enforce(origin)` works under `lock(sync)`. Manual commands use `ReadConfirmed`: two valid reads
with consistent state and capabilities, also for calibration start/stop. The limit stays suspended
if the calibration is saved or read from the firmware. An invalid stop verification keeps the guards.

Enforcement:

1. Reads the firmware state (`GetBatteryHealthControlStatus`).
2. If the read is not valid (`uFunctionList == 0`) it calls `Invalid()`: it does not write, it asks for a retry
   and stops retrying at short intervals after 60 attempts.
3. If there is a mismatch with `cfg` (limit different from the desired one, or calibration active or
   inactive differently from what was recorded), it waits 500 ms and reads again. If the two reads do not
   match, it logs "inconsistent firmware reads" and retries. It does not write on a single read.
4. `SyncCalibration` aligns `cfg.Calibrating` with the firmware:
   - calibration finished or interrupted → releases the guards and takes back control of the limit;
   - calibration started externally (for example by Care Center) → adopts it.
5. If a calibration is in progress: acquires the guards and **does not touch the limit**.
6. Otherwise, if the limit differs from the desired one, it calls `ApplyLimit`: writes, waits 300 ms, reads
   again and verifies. If verification fails it throws an exception. It counts the correction and raises an
   event for the tray icon.
7. Exceptions are throttled by `errorStreak`: logged on the 1st error and then every 10th, retries at short
   intervals up to 30.

### Scheduling (`BatteryService`)

| Event | Delay | `origin` in the log |
|---|---|---|
| Service start | 20 s, then +100 s, +180 s, +300 s | `startup`, `startup+2m`, `startup+5m`, `startup+10m` |
| `ResumeAutomatic` / `ResumeSuspend` | 20 s | `resume` |
| `PowerStatusChange` | 5 s (and telemetry immediately) | `power` |
| Firmware not ready or error | 60 s | `retry` |
| Calibration in progress | 60 s; 15 s at or below 10%, at the fallback floor, or with invalid telemetry | `calibration` |
| Steady state | 30 min | `periodic` |
| IPC command `ENFORCE` | immediately | `manual` |

There is a single one-shot timer, with no concurrent engine callbacks. Exceptions are contained
at the callback boundary. A manual command requests a check within 60 s; calibration and retry
take precedence over startup checks. Power events queue at most one job.

`ScheduleIfEarlier` moves it only if the new time is earlier than the one already
scheduled. `OnTimer` clears `plannedAt` before the enforce, so an event that arrives during execution
is not overwritten by the periodic rescheduling.

The close-spaced scans at startup are there to catch whoever resets the limit after logon. The
corrections in the last 24 hours and the "Possible conflicts" entry (running services
with "Acer" or "Care Center" in their name) are for diagnosis.

## Calibration

- **Start** (`StartCalibration`): requires the AC adapter to be connected and firmware support. It writes
  mode 0x02 only after confirmation. Before that it persists `cfg.Calibrating=1`, the start time and the
  guards; after the write it verifies by reading again. An uncertain outcome keeps the intent and the guards.
- **Guards** (`CalibrationGuards`):
  - `PowerCreateRequest`/`PowerSetRequest(SystemRequired)` to block automatic sleep;
  - "lid close" action on AC power set to 0 (do nothing) on the active scheme;
  - critical-battery action on AC power set to 0 with valid telemetry, saved before the write and read back afterward.
    The AC critical level is only read. DC settings are never changed.
    The original values and schemes are saved in `config.ini` (`saved_lid_action`, `saved_lid_scheme`,
    `saved_crit_action`, `saved_crit_scheme`). An action of -1 and empty scheme mean no restore is pending.
- **Release**: at the end of the calibration, on interruption, when the service stops and when Windows shuts
  down. At startup both power-setting journals are recovered even when calibration is marked as in progress.
  A write or save error keeps the backup for the next attempt; an error saving the final state does not
  skip the release of the guards. The calibration intent and each power-setting backup are persisted before the
  respective writes.
- **End of calibration**: detected by the 60 s polling, when the firmware reports calibration off.
  Normal enforcement then resumes.
- **Warning**: the tray confirmation and CLI start output share `CalibrationSafety.Warning`, using read-only
  `PowerAccess` reads of the active scheme's AC action and critical level before sending the existing start command.
- **Safety floor**: each enforcement pass with calibration active samples Windows battery telemetry independently
  of the display cache and firmware sensors. Percentage must be 0–100, AC state known and rate or charging/discharging
  flags available. Positive rate or a charging flag takes precedence over a stale discharging flag. Discharge means
  a negative rate on AC or an explicit discharging flag. Two consecutive valid samples at or below 3% trigger the
  existing `StopCalibration` path: two consistent firmware reads, calibration-only write, readback, release guards,
  then ordinary confirmed limit enforcement. Invalid telemetry, invalid/inconsistent firmware, errors, a higher
  percentage or charging evidence reset the sample count. The final warning event follows limit enforcement so
  a correction event cannot hide it. The limit is never written while calibration is recorded active.
- **Fallback**: failure to journal, write, apply or verify the AC critical action selects the current critical level
  plus two percentage points, bounded to 3–100%. If the level cannot be read, the floor is 100%: do not suppress
  Windows protection, stop on two valid discharge readings. Floor changes are logged once. Power writes that may
  have partially succeeded are restored; a failed restore keeps its original journal. On a scheme change, restore
  the old scheme before saving the new one; failure leaves the old backup intact and uses the fallback floor.
- **Telemetry loss**: no firmware stop on invalid/missing data. A monotonic clock tracks the last valid service
  sample. After more than 120 seconds without valid data, restore the critical action and retry failures at the
  fast cadence, even if firmware reads also fail. Suppression stays disabled for the remainder of the calibration;
  valid data returning uses the fallback floor. This also applies above 10% or with unknown charge, because a hidden
  discharge may cross the low-charge threshold. Until the deadline the existing override is retained, but invalid
  data never acquires a new override. Windows protection takes priority over completing calibration.
- **Constants**: `CalibrationSafety` contains the 3% floor, two-point fallback margin, 10% fast-poll threshold,
  15/60-second cadences, two required readings and 120-second timeout. `BatteryService` retains its one-shot timer
  and earlier-deadline scheduling. Calibration retries also use the fast cadence when appropriate.
- **AC adapter unplugged during calibration**: detected by comparing `lastAc` with the telemetry.
  It raises a "WARNING" notification, which the tray shows with the warning icon.
- As in acerctl, the 80% limit is not touched. Observed on the AN515-55: during calibration the firmware
  charges to 100% even with the limit active (`docs/TESTING.md`).
- **Windows shutdown during calibration** (for example the critical-battery action): the service logs it,
  releases the guards and keeps `calibrating=1`; at the next start it reads the firmware, finds calibration
  off and resumes normal enforcement.

## IPC

- **Pipe**: `\\.\pipe\AcerBatteryControl`, four fixed workers and four overlapped instances.
  `Engine` serializes anyway. A request must complete within 2 s; a response is written within
  5 s. Stopping closes listener and clients, without dummy connections or `WaitForPipeDrain`.
- **ACL**: access denied to NETWORK; FullControl for SYSTEM and Administrators; ReadWrite for INTERACTIVE.
- **Request**: one UTF-8 line terminated by `\n`, at most 256 bytes including the terminator. EOF, control bytes and requests that are too long
  are rejected before dispatch, without executing a truncated prefix.
- **Response**: `OK` or `ERR <message>`, then `\n`, then the body. The server closes the connection at the end. The client limits the response to 256 KiB and
  to 30 s overall after connecting, and rejects an incomplete header. No automatic retry of commands.

| Command | Response body |
|---|---|
| `STATUS` | `key=value` lines (below) |
| `LIMIT ON` / `LIMIT OFF` | message. Saves the desired state, applies it immediately if no calibration is in progress |
| `CALIBRATE START` / `CALIBRATE STOP` | message |
| `ENFORCE` | message |
| `LOG <n>` | last n lines of the log (1–2000) |

`STATUS` keys (numbers in invariant culture; booleans `1`/`0`; empty = not available):

| Key | Content |
|---|---|
| `version` | service version |
| `fw_valid` | last firmware read valid |
| `health_supported`, `calib_supported`, `health_on`, `calib_on` | firmware state |
| `fw_raw` | raw read, for diagnosis |
| `limit_desired` (`on`/`off`), `calibrating`, `calib_started` (local ISO 8601) | app state |
| `power_restore_pending` | either lid or critical-action journal still needs restoration; used to block tray service stop |
| `percent`, `on_ac`, `charging`, `discharging` | charge |
| `rate_mw` | + charging, − discharging |
| `voltage_mv`, `current_ma` (signed), `temp_c` | sensors |
| `remaining_mwh`, `full_mwh`, `design_mwh`, `cycles`, `model` | battery |
| `corrections_24h`, `last_correction` | automatic corrections |
| `last_error` | last error |
| `event_seq`, `event_text` | increasing counter and text of the last event; warnings start with `WARNING:`. The tray shows a notification when `event_seq` increases |
| `conflicts` | running Acer or Care Center services |

Cache in `Engine.Snapshot`: firmware state 10 s, Windows telemetry 3 s, firmware sensors (indices 8,
9 and 0x0A) 15 s, conflicts 120 s. They avoid querying the firmware on every panel poll (every 4 s).

## Thread boundaries and WMI

Two separate `BoundedCall` channels: firmware `BatteryControl` 8 s per operation (covers the first
connection to `root\wmi` after Windows starts), Windows telemetry 12 s for the whole group
(2 s queries). The background worker holds `AcerWmi.Gate` for BatteryControl. After a timeout the
channel stays closed to new accesses while the old worker is in the driver, and reopens by itself when it
returns: no growing queue, at most one thread held, no manual restart.
A result that arrives after the deadline is discarded; the state is read again at the next check. Permission is rechecked before every call, so an instance
lookup that completes late does not go on to a write. `Thread.Abort` is not used: a native call
already started cannot be cancelled with this guarantee and its outcome remains uncertain.

The order is `Engine.sync` → wait for the worker; the worker uses `AcerWmi.Gate` and releases the gate before
signalling completion. `schedLock` does not wait on `Engine.sync`. Stopping marks the engine
before closing the pipes; commands already queued are rejected. A failed startup is reported
to SCM after cleanup, not turned into a false successful start.

All WMI objects, collections and parameters are disposed deterministically. `IBatteryHardware` and
`PowerAccess` allow fault injection in tests without entering WMI or powrprof. In dry-run, real WMI reads
are blocked as well.

## Telemetry

- **Windows classes in `root\wmi`** (battery driver, they do not go through the Acer firmware):
  - `BatteryStatus`: RemainingCapacity, Voltage, ChargeRate, DischargeRate, PowerOnline, Charging, Discharging;
  - `BatteryFullChargedCapacity`;
  - `BatteryStaticData`: DesignedCapacity, ManufactureName, DeviceName;
  - `BatteryCycleCount`.
- **Fallback**: `Win32_Battery` for the percentage and the AC adapter state.
- **Sensors from the Acer firmware** (`GetBattInfoInterface`): temperature, voltage and current. When they are
  valid they take precedence.
- **Percentage** = Remaining / FullCharged, i.e. as Windows computes it.
  **Health** = FullCharged / Designed.

## Graphical interface

- **`TrayContext`** (ApplicationContext):
  - a hidden `Control` acts as the invoker for `BeginInvoke`; polling runs on the ThreadPool, so the interface
    does not freeze if the service does not respond;
  - polling every 15 s, every 4 s with the panel open;
  - the "Service unreachable" warning fires only after 2 consecutive failures;
  - the icon is redrawn only when state, percentage (in 5% steps) or size change
    (taskbar metrics at the current DPI, with a system fallback).
- Icon colors: green = limit active, orange = limit not active, blue = calibration,
  grey = unknown, red = service stopped, red X = service unreachable.
- **Service** (`ServiceInfo`): state read from SCM, not from the pipe. A single function decides Stop
  (running), Start (stopped) or no action (transitioning, not installed) for menu and panel. Start and
  stop go through elevated `sc.exe` (UAC); the outcome is the exit code plus the SCM state reached. Before
  stopping, the state is read again: with `calibrating`, `calib_on` or `power_restore_pending` the stop is refused.
- The IPC client sends a command only if `GetNamedPipeServerProcessId` matches the service PID
  according to SCM, so a process squatting on the pipe name does not receive commands.
- The tooltip text is truncated to 63 characters, the NotifyIcon limit in .NET Framework.
- **`MainForm`**: closing hides it without destroying it. The rows are in `StatusView.Labels`/`Values`,
  the same ones used by the CLI.
- Polling and commands go through `UiDispatch`: callbacks after exit are ignored, dispatch exceptions
  contained. UAC and waiting for the service to start also stay off the UI thread; one command at a time.
- Exit: disposal of both timers, invoker, menu, font, icon and SystemEvents subscriptions.
  `NotifyIcon` already handles `TaskbarCreated`; no second icon is added.
- DPI: Windows 10 manifest and PerMonitorV2 `.config`, `AutoScaleMode.Font`, Framework 4.8 target.
  Light/dark theme, Segoe UI Variable Text font with Segoe UI fallback, DWM attributes for dark title bar,
  corners and Mica backdrop where supported; in high contrast the system colors are used.
  Visual rendering and monitor changes require manual testing.

## Installation

`Installer.Install()` requires administrator rights and performs these steps:

1. Checks that the firmware exposes `BatteryControl`; if it is missing, installation stops.
2. Stops the service if it already exists, closes the other instances, copies the exe, DPI configuration and README into `%ProgramFiles%\AcerBatteryControl`.
3. Deletes the `AcerChargeLimit` task if present, to prevent it from competing with the service.
4. Protects `%ProgramData%\AcerBatteryControl` with `DataDirTrust.Secure` (the service also does this at every
   start, before reading the configuration or writing the log):
   - an already secure folder is used as is: owner SYSTEM, Administrators or TrustedInstaller, no
     reparse points or hardlinks, no write rights for others, checked without following links;
   - an insecure folder is not repaired in place (a handle opened earlier would survive the DACL change):
     it is moved to `AcerBatteryControl.untrusted-<date>` (date format `yyyyMMdd-HHmmss`) and a new one is
     created with a DACL protected from creation (owner Administrators, SYSTEM and Administrators FullControl,
     Users read-only);
   - if the move fails it refuses; if the folder is not trusted the service does not start and does not write.
5. Creates the service with `sc.exe create`, or `config` if it already exists: automatic start, description,
   automatic restart on failure. Then starts it.
6. Adds the `HKLM\...\Run` key and the `HKLM\...\Uninstall\AcerBatteryControl` entry (Installed apps),
   in the 64-bit view.
7. Starts the tray icon with `explorer.exe <exe>`, so it runs as a normal, non-elevated user.

`Uninstall(force)` refuses, restarting the service if it had stopped it, when: a calibration is present (in the
service or in the firmware, read directly while the service is stopped); the service is running but not
responding; the journal (`config.ini`) is missing, invalid, sits in an untrusted folder or reports a pending
lid or critical-action restore. Strict parsing rejects incomplete critical-action pairs, invalid actions/schemes
and duplicate keys; legacy journals with both critical keys absent remain readable. A lost journal is marked by
the service with `recovery_unknown=1` and never becomes "safe" again
by itself: `uninstall --force` is required after manually checking both AC actions. It skips journal restrictions
and accepts unreadable firmware state, but never bypasses calibration reported active by the service or firmware.
It does not change the firmware state. When launched from the installation folder, it deletes it after a few seconds.

## Open decisions / TODO

- Validate the 3% floor on hardware: charging resumption after an early confirmed stop, gauge accuracy without
  the firmware's final calibration recharge, and margin for rapid discharge or slow driver calls. The AC guard
  cannot prevent firmware cutoff, manual shutdown, policy interference or discharge while the service is absent.
- The critical setting and subgroup GUIDs follow Microsoft's [critical battery action](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/battery-settings-critical-battery-action)
  and [critical battery threshold](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/battery-settings-critical-battery-threshold)
  definitions. Real power-plan apply/restore and policy interactions still require manual validation.
- Code signing: the executables are not signed and SmartScreen warns.
- Possible addition: blocking user-requested sleep during calibration. Today only automatic sleep
  is blocked.
- A direct administrative stop (`services.msc`, `sc stop`) during a calibration cannot be reliably
  prevented by `ServiceBase`: the service releases the guards and keeps `calibrating=1` for resuming.
- Possible addition: reading methods 22/23 (`Get/SetBatteryFunctionData`, adaptive charging).
  On ANV15-52 they return 1 (not supported).
- Fans, performance profiles, keyboard and RGB controls are out of scope.

## Log

Rotation before the 512 KiB threshold, keeping a tail of at most 128 KiB; single messages
limited to 4096 characters. Guard errors and inconsistent reads: first event and every tenth.
The corrections buffer is pruned even without STATUS requests.
