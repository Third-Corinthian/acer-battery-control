# Acer firmware interface — BatteryControl

Sources:

- Linux kernel documentation, [acer-wmi-battery](https://docs.kernel.org/wmi/devices/acer-wmi-battery.html);
- driver [frederik-h/acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery) (`acer-wmi-battery.c`, `MODELS.md`);
- [acerctl](https://github.com/FnnDK/acerctl): `docs/firmware-wmi.md` §6 and §9, `daemon/internal/service/calibration.go`.

## Class

- Namespace `root\wmi`, class `BatteryControl`, GUID `{79772EC5-04B1-4BFD-843C-61E7F77B6CC9}`.
- ACPI method `WMBE`. The MOF is embedded in the BIOS ("Class used to control smart battery, Version 2.88").
- On Windows it is exposed by the Microsoft WMI-ACPI driver. If `SELECT * FROM BatteryControl` returns no
  instances, the notebook does not have this interface.
- **Nitro models reported working on Linux**: AN515-57, AN515-58, AN517-54, ANV15-51, ANV15-52 (acerctl).
- **Reported not working**: AN515-47.
- **AN515-55** (71UB, SMP AP18E7M battery): `BatteryControl` present on Windows, `uFunctionList = 0x03`
  (limit and calibration), sensors 0x08/0x09/0x0A valid. Verified with `acerbat probe` on 2026-11-17;
  writing not yet tested.

## Methods used

`uBatteryNo` is always 1: there are no Acer notebooks with two batteries.

### 20 — GetBatteryHealthControlStatus

| Direction | Parameter | Value |
|---|---|---|
| in | `uBatteryNo` (uint8) | 1 |
| in | `uFunctionQuery` (uint8) | 1 (Care Center always uses fixed parameters) |
| in | `uReserved` (uint8[2]) | 0,0 |
| out | `uFunctionList` (uint8) | bitmask of supported functions: 0x01 80% limit, 0x02 calibration |
| out | `uReturn` (uint8[2]) | |
| out | `uFunctionStatus` (uint8[5]) | [0] limit active, [1] calibration active (non-zero = active) |

### 21 — SetBatteryHealthControl

| Direction | Parameter | Value |
|---|---|---|
| in | `uBatteryNo` (uint8) | 1 |
| in | `uFunctionMask` (uint8) | 0x01 limit, 0x02 calibration (other values unknown) |
| in | `uFunctionStatus` (uint8) | 1 = enable, 0 = disable |
| in | `uReservedIn` (uint8[5]) | zeros |
| out | `uReturn` (uint16), `uReservedOut` (uint16) | |

The meaning of `uReturn` is not documented. The outcome is verified by reading again with method 20.

### 19 — GetBattInfoInterface

Parameters: `uBatteryInfoIndex` (uint32), `uBatteryNo` (uint32). Returns `uReturn` (uint32).
The index is a Smart Battery (SBS) command:

| Index | Data | Conversion in the code |
|---|---|---|
| 0x08 | temperature in 0.1 K | °C = (v − 2731) / 10 |
| 0x09 | voltage in mV | |
| 0x0A | current in mA, signed | `(short)(ushort)v` |

Unsupported indices return 1. The code discards out-of-range values: temperature outside
−20…100 °C, voltage outside 1000…30000 mV.

The CLI labels these readings `Temperature` in status output and `Temp`, `Voltage`, `Current`,
`Capacity` and `Cycles` in probe output. Display numbers follow the current culture; IPC numbers
and stored values use invariant formatting.

### 22/23 — Get/SetBatteryFunctionData

Adaptive charging ("BAC"). Not used. On ANV15-52 they return ReturnCode 1.

## Known behaviors

- **The state lives in the EC** and survives reboots. Care Center resets it: this is the problem that gave
  rise to this project.
- **Concurrent calls corrupt the responses.** acerctl measured between 1% and 5% of bad reads with
  several processes using Acer WMI methods at the same time. The AML methods are `Serialized` only with respect
  to themselves and share the SMM path. Hence the `AcerWmi.Gate` lock and the double read before every write.
  A running Care Center is a source of concurrency outside our control.
- **"No mode" state**: after a calibration interrupted by a shutdown, at the next boot the
  firmware responded for a while with `uFunctionList = 0` (acerctl 0.1.8). It is transient: nothing is written and the read is retried.
- **Calibration**: the firmware discharges the battery by running the system. A sleep halts the
  discharge, a shutdown interrupts it abruptly. acerctl holds a sleep and lid inhibitor for the whole
  duration and requires the AC adapter at start. The same is done here: see
  `CalibrationGuards`. Observed on the AN515-55: the firmware charges to 100% even with the 80% limit on,
  then discharges; after a shutdown at 5% (Windows' critical-battery action) it reported calibration off at
  the next boot, and the battery gauge had recalculated the full charge capacity.
- **80% limit**: if it is enabled above 80%, the battery is not discharged. Charging stops and
  resumes when the level drops below the threshold. The threshold is fixed and not configurable.

## Other Acer WMI classes (out of scope)

`AcerGamingFunction` (`7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56`, method `WMBH`) handles profiles, fans,
LEDs and keyboard. Almost everything goes through SMM and the buffer formats can only be discovered by trial and error
(acerctl `docs/firmware-wmi.md` §2–3). Do not use it without an explicit decision.
