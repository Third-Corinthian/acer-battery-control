using System;
using System.Collections.Generic;
using System.Globalization;

namespace AcerBatteryControl
{
    enum TrayState { Unknown, LimitOn, LimitOff, Calibrating, Error, Stopped }

    // Service state according to SCM, readable by a standard user (does not use the pipe).
    static class ServiceInfo
    {
        public const string Running = "running", Stopped = "stopped", Starting = "starting",
            Stopping = "stopping", Absent = "not installed", Unknown = "unknown";

        public const string ActionStart = "start", ActionStop = "stop";

        // Single state machine for menu and panel: Stop when running, Start when stopped, otherwise no action.
        public static string Action(string state)
        {
            if (state == Running) return ActionStop;
            if (state == Stopped) return ActionStart;
            return null;
        }

        // Wait for SCM to reach the expected state; false on timeout.
        public static bool WaitFor(string expected, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (State() == expected) return true;
                System.Threading.Thread.Sleep(300);
            }
            return State() == expected;
        }

        public static string State()
        {
            try
            {
                using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(Paths.ServiceName))
                {
                    switch (sc.Status)
                    {
                        case System.ServiceProcess.ServiceControllerStatus.Running: return Running;
                        case System.ServiceProcess.ServiceControllerStatus.Stopped: return Stopped;
                        case System.ServiceProcess.ServiceControllerStatus.StartPending: return Starting;
                        case System.ServiceProcess.ServiceControllerStatus.StopPending: return Stopping;
                        default: return Unknown;
                    }
                }
            }
            catch (InvalidOperationException) { return Absent; }
            catch { return Unknown; }
        }
    }

    // Service state rendered as English text, shared by GUI and CLI.
    static class StatusView
    {
        public static readonly string[] Labels =
        {
            "Charge", "AC adapter", "Power", "Voltage / current", "Temperature",
            "Capacity", "Cycles", "Battery", "80% limit", "Calibration",
            "Automatic corrections (24 h)", "Possible conflicts", "Last error"
        };

        static string G(IpcResult r, string k)
        {
            return r.Get(k);
        }

        static int? I(IpcResult r, string k)
        {
            int v;
            return int.TryParse(r.Get(k), NumberStyles.Integer, Util.Inv, out v) ? (int?)v : null;
        }

        static double? D(IpcResult r, string k)
        {
            double v;
            return double.TryParse(r.Get(k), NumberStyles.Float, Util.Inv, out v) ? (double?)v : null;
        }

        static bool Flag(IpcResult r, string k)
        {
            return r.Get(k) == "1";
        }

        static string Dash(string s)
        {
            return string.IsNullOrEmpty(s) ? "—" : s;
        }

        static string Time(string iso, bool withDate)
        {
            DateTime d;
            if (!DateTime.TryParse(iso, Util.Inv, DateTimeStyles.RoundtripKind, out d)) return "";
            d = d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
            CultureInfo culture = CultureInfo.CurrentCulture;
            return d.ToString("t", culture) + (withDate ? " on " + d.ToString("M", culture) : "");
        }

        public static string ChargeLine(IpcResult r)
        {
            int? p = I(r, "percent");
            string pct = p.HasValue ? p.Value + "%" : "?%";
            string state;
            if (Flag(r, "charging")) state = "charging";
            else if (Flag(r, "discharging")) state = "discharging";
            else if (Flag(r, "on_ac")) state = "not charging";
            else state = "";
            return state.Length > 0 ? pct + " · " + state : pct;
        }

        public static string LimitLine(IpcResult r)
        {
            bool desired = G(r, "limit_desired") == "on";
            if (Flag(r, "calibrating")) return "suspended (calibration in progress)";
            if (G(r, "fw_valid") != "1") return (desired ? "requested on" : "requested off") + " · firmware not ready";
            if (G(r, "health_supported") != "1") return "not supported by firmware";
            bool fw = Flag(r, "health_on");
            if (fw == desired) return desired ? "enabled" : "disabled";
            return (desired ? "requested on" : "requested off") + " · firmware " + (fw ? "enabled" : "disabled") + " (correcting)";
        }

        public static TrayState State(IpcResult r)
        {
            if (!r.Ok) return TrayState.Error;
            if (Flag(r, "calibrating")) return TrayState.Calibrating;
            if (G(r, "fw_valid") != "1") return TrayState.Unknown;
            return Flag(r, "health_on") ? TrayState.LimitOn : TrayState.LimitOff;
        }

        public static string[] Values(IpcResult r)
        {
            string[] v = new string[Labels.Length];
            v[0] = ChargeLine(r);
            v[1] = G(r, "on_ac") == "" ? "—" : (Flag(r, "on_ac") ? "connected" : "disconnected");

            int? rate = I(r, "rate_mw");
            v[2] = rate.HasValue ? (rate.Value > 0 ? "+" : "") + (rate.Value / 1000.0).ToString("0.0", Util.It) + " W" : "—";

            int? mv = I(r, "voltage_mv");
            int? ma = I(r, "current_ma");
            string vs = mv.HasValue ? (mv.Value / 1000.0).ToString("0.00", Util.It) + " V" : "—";
            string cs = ma.HasValue ? (ma.Value > 0 ? "+" : "") + (ma.Value / 1000.0).ToString("0.00", Util.It) + " A" : "—";
            v[3] = vs + " · " + cs;

            double? t = D(r, "temp_c");
            v[4] = t.HasValue ? t.Value.ToString("0.0", Util.It) + " °C" : "not available";

            int? full = I(r, "full_mwh");
            int? design = I(r, "design_mwh");
            if (full.HasValue && design.HasValue && design.Value > 0)
                v[5] = (full.Value / 1000.0).ToString("0.0", Util.It) + " Wh of " + (design.Value / 1000.0).ToString("0.0", Util.It)
                       + " Wh design capacity (health " + (full.Value * 100.0 / design.Value).ToString("0", Util.It) + "%)";
            else if (full.HasValue)
                v[5] = (full.Value / 1000.0).ToString("0.0", Util.It) + " Wh";
            else
                v[5] = "—";

            v[6] = Dash(G(r, "cycles"));
            v[7] = Dash(G(r, "model"));
            v[8] = LimitLine(r);

            if (Flag(r, "calibrating"))
            {
                string since = Time(G(r, "calib_started"), true);
                v[9] = "in progress" + (since.Length > 0 ? " since " + since : "");
            }
            else if (G(r, "calib_supported") == "0") v[9] = "not supported";
            else v[9] = "not in progress";

            int? corr = I(r, "corrections_24h");
            string last = Time(G(r, "last_correction"), false);
            v[10] = corr.HasValue ? corr.Value + (corr.Value > 0 && last.Length > 0 ? ", last at " + last : "") : "—";
            v[11] = string.IsNullOrEmpty(G(r, "conflicts")) ? "none detected" : G(r, "conflicts");
            v[12] = Dash(G(r, "last_error"));
            return v;
        }
    }
}
