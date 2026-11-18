# Testing

## Automated verification

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1 [-OutFile out\verify.txt]
```

Builds the GUI/service, the CLI and `tests.exe` with the system `csc` (`/langversion:5`) and runs only
`tests.exe`, with `ACERBAT_DRYRUN=1` and temporary data under `out/`. In simulated mode every access to WMI,
the power plan, the registry, services and installation throws a `[dry-run]` exception: the tests use a
simulated firmware and power plan and pipes with random names `acerbat-test-*`. A test that hangs is
killed after 180 s; a self-test checks that a crash on a thread produces an error, not a window.

What the tests cover (88 checks in 2.1.0):

- double read before every write, calibration started externally or saved, null, short and
  inconsistent reads, commands after stop;
- hung WMI driver: timeout, stop, no thread build-up, recovery when the driver responds;
- lid restore after errors and crashes; state file missing, corrupt or lost;
- pipe: incomplete, long or control-character requests, silent clients, impostor server, reconnection;
- data folder created by a user, hardlinks and junctions;
- UI: late callbacks, exceptions, 500 icon redraws without handle leaks;
- log rotation.

Version 2.2.0 retains those checks and adds simulated calibration safety coverage:

- two fresh floor samples, confirmed stop/readback, both setting restores and desired limit enforcement;
- invalid and single outlier samples, charging precedence, discharge flags and negative AC rate;
- critical write/apply/readback failures, fallback floors and an unreadable critical level;
- low-charge cadence, telemetry timeout through simultaneous firmware failures, restore retries and recovery;
- journal save failure, restart with and without saved calibration intent, shutdown, natural end and scheme changes;
- strict journal validation, legacy compatibility, uninstall refusal/force semantics and tray stop blocking;
- read-only shared warning and the real critical-write dry-run barrier.

Not covered: the real SCM, real pipe and folder ACLs, UAC, the visible GUI and, above all, firmware
writes. These need the checklist below.

## Hardware checklist (AN515-55)

Before installing: close Acer Care Center.

| # | Step | Expected result |
|---|---|---|
| 1 | `build.cmd` on Windows (only if you build it yourself) | Builds without errors with the system `csc`. Confirms the C# 5 constraint |
| 2 | `acerbat probe` (administrator prompt) | `BatteryControl found.`, `Valid: True`, `80% limit   supported: True`. Note the raw `list=0x..` |
| 3 | Sensor readings in `probe` | Plausible temperature (20–45 °C), voltage between 10 and 17 V, capacity and cycles present. If any are missing, note which |
| 4 | Double-click `AcerBatteryControl.exe` → Yes | Installation message, green icon, `Acer Battery Control` service running (`services.msc`) |
| 5 | `acerbat status` from a normal prompt | It responds: the pipe is accessible to the interactive user |
| 6 | Menu → uncheck "80% charge limit" | Orange icon; in the log `[manual] 80% limit: on -> off`. With the battery below 100% and the charger connected, charging resumes |
| 7 | Check it again | Green icon; in the log `[manual] 80% limit: off -> on` |
| 8 | Restart Windows, then check the log | Line `[service] starting`. No correction if nothing resets the limit. If `[startup+Nm] ... off -> on` appears, some process is resetting it: see "Possible conflicts" |
| 9 | Sleep and resume | The `resume` check runs; it appears in the log only if it corrects something |
| 10 | Unplug and replug the charger | The panel updates the charger state and the power with the correct sign |
| 11 | `acerbat enforce` with the limit already correct | No line in the log |
| 12 | Calibration (when you have a few hours): menu → Start calibration… | Blue icon. In the log: start, "automatic sleep blocked", "lid close (plugged in): Do nothing". `powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION` → AC 0 |
| 13 | During calibration: the limit state in the panel | Note whether the firmware still reports the limit as active and whether the battery charges past 80%. **Missing data, to be documented in FIRMWARE.md** |
| 14 | End of calibration (or Stop calibration) | "Calibration ended." or "Calibration stopped." event. Lid restored (powercfg). Limit reapplied. Green icon |
| 15 | Settings > Apps > Installed apps: the "Acer Battery Control" entry is there, with version and icon. Uninstall | UAC, then service removed, Run key and Installed apps entry removed, folders deleted, message with the firmware state |
| 16 | Before a 2.2.0 calibration, open the tray start dialog and cancel; compare with `powercfg /q SCHEME_CURRENT SUB_BATTERY BATACTIONCRIT` and `powercfg /q SCHEME_CURRENT SUB_BATTERY BATLEVELCRIT` | Warning shows the current AC action and level, hours on AC, charge to 100%, discharge, guards, safety floor, limit resumption and no unplug/shutdown. Cancelling changes nothing |
| 17 | Start once from the tray or `acerbat calibrate start`; compare both AC and DC values with the saved pre-start readings | CLI prints the warning before requesting start, without a prompt. AC critical action and lid action are 0. DC values and critical levels are unchanged. Journal contains original actions and schemes; log records changes |
| 18 | During a supervised discharge, observe the last part of calibration | Checks every 15 s at or below 10%. Two valid discharging readings at or below 3% cause a confirmed stop, one safety notification, both AC actions restored, normal charging resumed and charging stopped at 80% if enabled. If charging begins first, no floor stop |
| 19 | Stop calibration normally; separately test service stop/shutdown and restart recovery with adequate charge | Both AC actions restored. Failed restores retain their journal and are retried; no limit writes while firmware calibration remains active |
| 20 | Inspect Installed apps after upgrading to 2.2.0 | DisplayVersion is 2.2.0. Uninstall refuses pending critical-action recovery. Before using `--force`, manually restore both AC actions; reported active calibration still blocks uninstall |
| 21 | With an older version installed and its tray icon running, double-click the new `AcerBatteryControl.exe` from the extracted zip | A dialog shows the installed and new versions. Yes + UAC: service stopped, old icon closed, files replaced, settings kept, service and icon started again. During a calibration the update is refused with a message |

Steps 16–20 and the automatic floor's charging behavior are **not yet hardware-verified**. Use injected tests for
write failures, invalid telemetry and the two-minute timeout; do not deliberately remove battery protection or
break telemetry on the real notebook to exercise them. Preserve the observations below as historical results.

## Results on AN515-55-71UB

| Date | # | Result |
|---|---|---|
| 2026-11-17 | 1 | OK. `scripts\verify.ps1`: GUI, CLI and test build with the system `csc` without errors, dry-run tests green |
| 2026-11-17 | 2 | OK. `list=0x03 status=00-00-00-00-00 ret=00-00`: limit and calibration supported, both off |
| 2026-11-17 | 3 | Partial. Temp 23.9 °C, voltage 16584 mV, current +192 mA (AC, 97%), capacity 42104 / 58751 mWh (health ~72%), model `SMP AP18E7M`. **Cycles missing** (`BatteryCycleCount` absent or 0) |
| 2026-11-17 | 4 | OK. Installed to `C:\Program Files\AcerBatteryControl`, service started |
| 2026-11-17 | 6 | OK. Limit turned off from the app; the firmware confirmed the write (`uReturn=0`) |
| 2026-11-17 | 7 | OK. Limit turned back on from the app; the firmware confirmed the write (`uReturn=0`) |
| 2026-11-17 | 8 | Note. 10 minutes after start the service found the limit off and re-enabled it: something resets the limit after boot. See "Possible conflicts" in the panel; to be investigated |
| 2026-11-17 | 6–7 | OK. With the limit on and the charger plugged in, charging stopped at 80% |
| 2026-11-18 | 12 | OK. Calibration started from the app; the firmware confirmed the command (`uReturn=0`); automatic sleep blocked |
| 2026-11-18 | 13 | OK. The battery charged to 100% with the 80% limit on, then discharged with the system running. Whether the firmware kept reporting the limit as active was not recorded |
| 2026-11-18 | 14 | Partial. At 5% Windows' critical-battery action (hibernate at 5% in the active plan) shut the PC down: the service logged the shutdown with calibration in progress and released its guards. At the next start the firmware reported calibration off and the service re-enabled the limit (`uReturn=0`); no lid restore pending. Battery health after calibration: 68% (72% before): the gauge recalculated the full capacity |
| — | 15 | Not tested yet (uninstall) |

## Useful data to collect

- The `fw_raw` lines in the panel or in `acerbat status`, in particular the value of `uReturn` after a
  write. Its meaning is not known yet.
- `acerbat log 200` after 2–3 days of normal use, to see how many corrections there are.

## Additional testing

Beyond the checklist:

- Service: repeated start and stop, shutdown, sleep and resume; time taken to stop; lid action
  restored even after errors.
- Pipe accessible to the interactive user, denied from the network.
- Data folder: after installation `(Get-Acl C:\ProgramData\AcerBatteryControl).Owner` is
  `BUILTIN\Administrators` and users have read-only access. If the folder was created beforehand by a standard user,
  the installer moves it to `AcerBatteryControl.untrusted-<date>` (date format `yyyyMMdd-HHmmss`) and creates a new one.
- With the service running `acerbat status` responds; with the service stopped it answers "Service unreachable".
- Tray: "Stop service…" → UAC → red icon and warning in the panel; "Start service…" → green icon;
  stop refused during a calibration; "About and credits".
- Appearance: light, dark and high-contrast themes; 100–200% scaling; moving between monitors; Explorer restart
  leaves a single icon.
