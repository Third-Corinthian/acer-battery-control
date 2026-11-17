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
| — | 12–14 | Not tested yet (calibration) |
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
