using System;

namespace AcerBatteryControl
{
    static class CalibrationSafety
    {
        public const int FloorPercent = 3;
        public const int FallbackMarginPercent = 2;
        public const int FastPollPercent = 10;
        public const int FastPollSeconds = 15;
        public const int PollSeconds = 60;
        public const int TelemetryTimeoutSeconds = 120;
        public const int RequiredReadings = 2;
        public const int MaximumPercent = 100;

        public static bool Valid(Telemetry t)
        {
            return t != null && t.Percent.HasValue && t.Percent >= 0 && t.Percent <= MaximumPercent
                && t.OnAC.HasValue && (t.RateMw.HasValue || t.Discharging.HasValue || t.Charging == true);
        }

        public static bool Discharging(Telemetry t)
        {
            // Positive charging evidence wins over a stale discharging flag.
            return Valid(t) && t.Charging != true && !(t.RateMw > 0)
                && ((t.OnAC == true && t.RateMw < 0) || t.Discharging == true);
        }

        public static string ActionName(uint action)
        {
            switch (action)
            {
                case 0: return "Do nothing";
                case 1: return "Sleep";
                case 2: return "Hibernate";
                case 3: return "Shut down";
                default: return "Unknown action (" + action + ")";
            }
        }

        // Read-only, shared by the confirmation dialog and CLI before sending START.
        public static string Warning(PowerAccess power)
        {
            string current;
            try
            {
                Guid scheme = power.ActiveScheme();
                current = ActionName(power.ReadAcCriticalAction(scheme)) + " at "
                    + power.ReadAcCriticalLevel(scheme) + "%";
            }
            catch { current = "unavailable; Windows protection will be kept if it cannot be saved"; }
            return "Calibration takes several hours with the charger plugged in. The firmware charges to 100%, "
                + "then discharges the battery while the PC is running.\n\n"
                + "Automatic sleep and lid-close sleep on AC are blocked. Windows' critical-battery action on AC "
                + "is temporarily set to Do nothing (currently: " + current + "). Battery-power settings are unchanged.\n\n"
                + "If still discharging at " + FloorPercent + "%, calibration is stopped automatically after two valid readings "
                + "so charging can resume. The original power settings are restored and the 80% limit is re-applied if enabled. "
                + "If the firmware starts recharging first, it is allowed to finish.\n\n"
                + "If the critical action cannot be changed, the safety floor is the current critical level plus "
                + FallbackMarginPercent + " percentage points. If that level cannot be read, calibration stops on confirmed discharge. "
                + "Missing valid telemetry for more than two minutes restores Windows protection; Windows may then sleep or shut down.\n\n"
                + "Do not unplug the charger, shut down, restart or put the PC to sleep.";
        }
    }
}
