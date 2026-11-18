using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace AcerBatteryControl
{
    // Service logic. Enforcement rules:
    //  - desired state lives in config.ini; align the firmware only when different;
    //  - a mismatch requires two consistent reads before writing (concurrent Acer WMI calls are unreliable);
    //  - firmware "no mode" = transient state: retry, never write;
    //  - during calibration the limit is suspended and left untouched;
    //  - log only changes, plus errors and abnormal states.
    class Engine
    {
        readonly object sync = new object();
        readonly Config cfg;
        readonly CalibrationGuards guards;
        readonly IBatteryHardware hardware;
        readonly Action<int> delay;
        readonly Func<long> milliseconds;
        long? lastValidSafetyAt;
        long? missingSafetySince;
        bool fastCalibrationPoll, criticalSuspended;
        int floorReadings;
        int? loggedFallbackFloor;
        int telemetryErrors;

        HealthStatus hw;
        bool hwValidNow;
        DateTime hwAt = DateTime.MinValue;

        Telemetry tel;
        DateTime telAt = DateTime.MinValue;
        DateTime fwSensorsAt = DateTime.MinValue;
        double? lastTemp;
        int? lastFwVoltage;
        int? lastFwCurrent;

        readonly List<DateTime> corrections = new List<DateTime>();
        string lastError = "";
        long eventSeq;
        string eventText = "";
        string conflicts = "";
        DateTime conflictsAt = DateTime.MinValue;
        bool? lastAc;
        int invalidStreak;
        int errorStreak;
        int inconsistentStreak;
        volatile bool stopping;
        public event Action WorkRequested;

        void CheckRunning()
        {
            if (stopping) throw new AcerException("Service stopping: command not executed.");
        }

        void RequestWork()
        {
            Action request = WorkRequested;
            if (request != null) request();
        }

        // BatteryService.OnStart verifies the data folder before constructing the engine.
        public Engine() : this(Config.Load(), new WmiHardware(), new CalibrationGuards(), Thread.Sleep)
        {
        }

        internal Engine(Config config, IBatteryHardware device, CalibrationGuards protection, Action<int> wait)
            : this(config, device, protection, wait,
                delegate { return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency)); })
        {
        }

        internal Engine(Config config, IBatteryHardware device, CalibrationGuards protection, Action<int> wait, Func<long> clock)
        {
            cfg = config;
            hardware = device;
            guards = protection;
            delay = wait;
            milliseconds = clock;
        }

        public bool IsCalibrating
        {
            get { lock (sync) { return cfg.Calibrating; } }
        }

        public int CalibrationPollSeconds
        {
            get { lock (sync) { return fastCalibrationPoll ? CalibrationSafety.FastPollSeconds : CalibrationSafety.PollSeconds; } }
        }

        public void Startup()
        {
            lock (sync)
            {
                Log.Write("[service] starting v" + Paths.Version + ", desired limit: " + Util.OnOff(cfg.LimitDesired)
                          + (cfg.Calibrating ? ", calibration marked as in progress" : ""));
                // defaults must not erase a missing or invalid journal: rewrite it
                // with recovery_unknown set, which uninstall rejects without --force.
                string why;
                bool invalidJournal = Config.LoadStrict(out why) == null;
                if (invalidJournal) cfg.RecoveryUnknown = true;
                // Recover both journals even after a crash during calibration.
                guards.RestoreCritical(cfg);
                guards.RestoreLid(cfg);
                if (invalidJournal)
                {
                    try { cfg.Save(); Log.Write("[service] WARNING: invalid journal (" + why + "), recovery state unknown"); }
                    catch (Exception ex) { Log.Write("[service] cannot rewrite config.ini: " + ex.Message); }
                }
            }
        }

        public void Shutdown(string why)
        {
            BeginStop();
            lock (sync)
            {
                if (cfg.Calibrating)
                    Log.Write("[service] WARNING: " + why + " during calibration");
                guards.Release(cfg);
                Log.Write("[service] stopping (" + why + ")");
            }
        }

        public void BeginStop() { stopping = true; }

        // ------------------------------------------------------------------ enforcement

        // Return true if a retry is needed soon.
        public bool Enforce(string origin)
        {
            lock (sync)
            {
                try
                {
                    CheckRunning();
                    // Sample independently of firmware success and of STATUS/display caches.
                    Telemetry safety = cfg.Calibrating ? ReadSafetyTelemetry() : null;
                    if (!cfg.Calibrating) guards.Release(cfg);
                    HealthStatus a = hardware.GetStatus();
                    if (!a.Valid) return Invalid(origin, a);

                    bool mismatch = a.HealthOn != cfg.LimitDesired || a.CalibOn != cfg.Calibrating;
                    if (mismatch)
                    {
                        delay(500);
                        HealthStatus b = hardware.GetStatus();
                        if (!b.Valid) return Invalid(origin, b);
                        if (!Coherent(a, b))
                        {
                            floorReadings = 0;
                            inconsistentStreak++;
                            if (inconsistentStreak == 1 || inconsistentStreak % 10 == 0)
                                Log.Write("[" + origin + "] inconsistent firmware reads, retrying (" + inconsistentStreak + ")");
                            SetError("Inconsistent firmware reads: no write.");
                            return true;
                        }
                        a = b;
                    }

                    if (invalidStreak > 0) Log.Write("[" + origin + "] firmware ready again (" + a.Raw + ")");
                    invalidStreak = 0;
                    inconsistentStreak = 0;
                    SetHw(a);
                    SyncCalibration(a, origin);

                    if (cfg.Calibrating)
                    {
                        CheckRunning();
                        guards.Acquire(cfg);
                        if (safety == null) safety = ReadSafetyTelemetry();
                        MonitorCalibration(safety);
                        return false;
                    }
                    if (!a.HealthSupported)
                    {
                        SetError("The firmware does not report support for the 80% limit.");
                        return false;
                    }
                    if (a.HealthOn != cfg.LimitDesired)
                        ApplyLimit(cfg.LimitDesired, origin, true, a.HealthOn);
                    else
                        lastError = "";
                    errorStreak = 0;
                    return false;
                }
                catch (Exception ex)
                {
                    floorReadings = 0;
                    hwValidNow = false;
                    errorStreak++;
                    SetError(ex.Message);
                    if (errorStreak == 1 || errorStreak % 10 == 0)
                        Log.Write("[" + origin + "] error (" + errorStreak + "): " + ex.Message);
                    return errorStreak < 30;
                }
            }
        }

        bool Invalid(string origin, HealthStatus h)
        {
            floorReadings = 0;
            invalidStreak++;
            hwValidNow = false;
            if (invalidStreak == 1 || invalidStreak % 10 == 0)
                Log.Write("[" + origin + "] firmware not ready, no mode reported (" + h.Raw + "), attempt " + invalidStreak);
            SetError("Firmware not ready: no mode reported. Retrying soon.");
            return invalidStreak < 60;
        }

        void ApplyLimit(bool on, string origin, bool isCorrection, bool before)
        {
            CheckRunning();
            if (cfg.Calibrating) throw new AcerException("Limit suspended during calibration.");
            int ret = hardware.SetMode(AcerWmi.ModeHealth, on);
            delay(300);
            HealthStatus after = hardware.GetStatus();
            if (after.Valid) SetHw(after);
            else hwValidNow = false;
            if (!after.Valid || after.HealthOn != on)
                throw new AcerException("The firmware did not apply the command (uReturn=" + ret + ").");
            Log.Write(string.Format("[{0}] 80% limit: {1} -> {2} (uReturn={3})",
                origin, Util.OnOff(before), Util.OnOff(after.HealthOn), ret));
            lastError = "";
            if (isCorrection)
            {
                corrections.RemoveAll(delegate(DateTime d) { return d < DateTime.Now.AddHours(-24); });
                corrections.Add(DateTime.Now);
                RaiseEvent("80% limit " + (on ? "re-enabled" : "disabled") + " automatically (" + origin + ").");
            }
        }

        void SyncCalibration(HealthStatus a, string origin)
        {
            if (cfg.Calibrating && !a.CalibOn)
            {
                cfg.Calibrating = false;
                ResetSafety();
                try { cfg.Save(); }
                finally { guards.Release(cfg); }
                Log.Write("[" + origin + "] calibration no longer active in firmware (completed or stopped): resuming limit control");
                RaiseEvent("Calibration ended. 80% limit control resumed.");
            }
            else if (!cfg.Calibrating && a.CalibOn)
            {
                cfg.Calibrating = true;
                ResetSafety();
                cfg.CalibStartedUtc = DateTime.UtcNow;
                cfg.Save();
                Log.Write("[" + origin + "] calibration detected (started outside this app): limit suspended");
                RaiseEvent("Calibration detected: 80% limit suspended until it ends.");
            }
        }

        // ------------------------------------------------------------------ commands

        public string EnforceCommand()
        {
            lock (sync)
            {
                CheckRunning();
                Enforce("manual");
                RequestWork();
                if (lastError.Length != 0) throw new AcerException(lastError);
                return "Check completed.";
            }
        }

        static bool Coherent(HealthStatus a, HealthStatus b)
        {
            return a.Valid && b.Valid && a.HealthOn == b.HealthOn && a.CalibOn == b.CalibOn
                && a.HealthSupported == b.HealthSupported && a.CalibSupported == b.CalibSupported;
        }

        HealthStatus ReadConfirmed()
        {
            CheckRunning();
            HealthStatus a = hardware.GetStatus();
            if (!a.Valid) throw new AcerException("Firmware not ready: no write.");
            delay(500);
            HealthStatus b = hardware.GetStatus();
            if (!Coherent(a, b)) throw new AcerException("Invalid or inconsistent firmware reads: no write.");
            CheckRunning();
            SetHw(b);
            return b;
        }

        public string SetLimit(bool on)
        {
            lock (sync)
            {
                CheckRunning();
                bool changed = cfg.LimitDesired != on;
                bool previous = cfg.LimitDesired;
                cfg.LimitDesired = on;
                try { cfg.Save(); }
                catch { cfg.LimitDesired = previous; throw; }
                RequestWork();
                if (changed) Log.Write("[manual] desired limit: " + Util.OnOff(on));
                if (cfg.Calibrating)
                    return "Setting saved: it will be applied when calibration ends.";
                HealthStatus a = ReadConfirmed();
                SyncCalibration(a, "manual");
                if (a.CalibOn)
                {
                    guards.Acquire(cfg);
                    MonitorCalibration(ReadSafetyTelemetry());
                    return "Setting saved: it will be applied when calibration ends.";
                }
                if (!a.HealthSupported) throw new AcerException("The firmware does not support the 80% limit.");
                SetHw(a);
                if (a.HealthOn == on) return "80% limit already " + (on ? "enabled." : "disabled.");
                ApplyLimit(on, "manual", false, a.HealthOn);
                return "80% limit " + (on ? "enabled." : "disabled.");
            }
        }

        public string StartCalibration()
        {
            lock (sync)
            {
                CheckRunning();
                Telemetry t = ReadTelemetry(true);
                if (t.OnAC != true) throw new AcerException("Connect the AC adapter before starting calibration.");
                HealthStatus a = ReadConfirmed();
                if (!a.CalibSupported) throw new AcerException("The firmware does not support calibration.");
                try
                {
                    if (a.CalibOn)
                    {
                        SyncCalibration(a, "manual");
                        guards.Acquire(cfg);
                        MonitorCalibration(ReadSafetyTelemetry());
                        return "Calibration already in progress.";
                    }
                    // Persist intent before writing: an uncertain outcome does not release the guards.
                    cfg.Calibrating = true;
                    ResetSafety();
                    cfg.CalibStartedUtc = DateTime.UtcNow;
                    cfg.Save();
                    guards.Acquire(cfg);
                    Telemetry safety = ReadSafetyTelemetry();
                    if (CalibrationSafety.Valid(safety)) guards.UpdateCritical(cfg, safety.OnAC == true);
                    CheckRunning();
                    int ret = hardware.SetMode(AcerWmi.ModeCalibration, true);
                    delay(500);
                    HealthStatus after = hardware.GetStatus();
                    if (!after.Valid || !after.CalibOn)
                    {
                        Log.Write("[manual] calibration start rejected by firmware (uReturn=" + ret + ", " + after.Raw + ")");
                        throw new AcerException("The firmware did not start calibration (uReturn=" + ret + ").");
                    }
                    SetHw(after);
                    lastAc = t.OnAC;
                    Log.Write("[manual] calibration started (uReturn=" + ret + "), 80% limit suspended");
                    RaiseEvent("Calibration started. Keep the PC on and connected to the AC adapter.");
                    return "Calibration started.";
                }
                finally { RequestWork(); } // Uses the cadence chosen by fresh start telemetry, also on an uncertain start.
            }
        }

        public string StopCalibration()
        {
            return StopCalibration(false);
        }

        string StopCalibration(bool safetyFloor)
        {
            lock (sync)
            {
                CheckRunning();
                RequestWork();
                HealthStatus a = ReadConfirmed();
                if (a.CalibOn)
                {
                    CheckRunning();
                    int ret = hardware.SetMode(AcerWmi.ModeCalibration, false);
                    delay(500);
                    HealthStatus after = hardware.GetStatus();
                    if (!after.Valid || after.CalibOn)
                        throw new AcerException("The firmware did not stop calibration (uReturn=" + ret + ").");
                    SetHw(after);
                    Log.Write("[" + (safetyFloor ? "safety floor" : "manual") + "] calibration stopped (uReturn=" + ret + ")");
                }
                bool was = cfg.Calibrating;
                cfg.Calibrating = false;
                ResetSafety();
                try { cfg.Save(); }
                catch
                {
                    if (safetyFloor)
                        RaiseEvent("WARNING: Calibration stopped at the safety floor. Power settings and the 80% limit need recovery verification.");
                    throw;
                }
                finally { guards.Release(cfg); }
                Enforce("calibration end");
                if (safetyFloor)
                    RaiseEvent("WARNING: Calibration stopped at the safety floor so charging can resume. "
                        + (lastError.Length == 0 ? "80% limit control resumed." : "80% limit needs verification."));
                else if (was) RaiseEvent("Calibration stopped.");
                if (lastError.Length > 0) throw new AcerException("Calibration stopped; limit needs verification: " + lastError);
                return "Calibration stopped, 80% limit restored to the desired setting.";
            }
        }

        public void PowerChanged()
        {
            lock (sync)
            {
                try { CheckRunning(); ReadTelemetry(true); } catch { }
            }
        }

        void ResetSafety()
        {
            floorReadings = 0;
            fastCalibrationPoll = false;
            criticalSuspended = false;
            lastValidSafetyAt = missingSafetySince = null;
            loggedFallbackFloor = null;
            telemetryErrors = 0;
        }

        Telemetry ReadSafetyTelemetry()
        {
            Telemetry t = null;
            try { CheckRunning(); t = hardware.ReadStandard(); }
            catch (Exception ex)
            {
                telemetryErrors++;
                if (telemetryErrors == 1 || telemetryErrors % 10 == 0)
                    Log.Write("[calibration] safety telemetry unavailable: " + ex.Message + " (" + telemetryErrors + ")");
            }
            long now = milliseconds();
            if (CalibrationSafety.Valid(t))
            {
                lastValidSafetyAt = now;
                missingSafetySince = null;
                telemetryErrors = 0;
                fastCalibrationPoll = t.Percent <= CalibrationSafety.FastPollPercent;
            }
            else
            {
                floorReadings = 0;
                // Unknown charge may conceal a descent below 10%; use the fast cadence as well.
                fastCalibrationPoll = true;
                if (!missingSafetySince.HasValue) missingSafetySince = lastValidSafetyAt ?? now;
                if (now - missingSafetySince.Value > CalibrationSafety.TelemetryTimeoutSeconds * 1000L)
                {
                    if (!criticalSuspended)
                    {
                        criticalSuspended = true;
                        Log.Write("[calibration] WARNING: valid telemetry missing for more than two minutes; restoring Windows critical battery protection");
                        RaiseEvent("WARNING: Calibration telemetry unavailable. Restoring Windows critical battery protection; Windows may sleep or shut down.");
                    }
                    guards.RestoreCritical(cfg); // Retry failures; never disable again during this calibration.
                }
            }
            return t ?? new Telemetry();
        }

        void MonitorCalibration(Telemetry t)
        {
            if (!CalibrationSafety.Valid(t)) { floorReadings = 0; return; }
            guards.UpdateCritical(cfg, !criticalSuspended && t.OnAC == true);
            int floor = CalibrationSafety.FloorPercent;
            if (!guards.CriticalProtected)
            {
                floor = guards.CriticalLevel.HasValue
                    ? Math.Min(CalibrationSafety.MaximumPercent, Math.Max(floor, guards.CriticalLevel.Value + CalibrationSafety.FallbackMarginPercent))
                    : CalibrationSafety.MaximumPercent;
                if (loggedFallbackFloor != floor)
                {
                    loggedFallbackFloor = floor;
                    Log.Write("[calibration] critical action guard unavailable: fallback safety floor " + floor + "%"
                        + (guards.CriticalLevel.HasValue ? " (critical level plus margin)" : " (critical level unreadable; stop on confirmed discharge)"));
                }
            }
            else loggedFallbackFloor = null;
            if (t.Percent <= floor) fastCalibrationPoll = true;
            if (CalibrationSafety.Discharging(t) && t.Percent <= floor) floorReadings++;
            else floorReadings = 0;
            if (floorReadings >= CalibrationSafety.RequiredReadings)
                StopCalibration(true); // Same confirmation, write and readback as the manual stop.
        }

        // ------------------------------------------------------------------ state

        void SetHw(HealthStatus h)
        {
            hw = h;
            hwValidNow = true;
            hwAt = DateTime.UtcNow;
        }

        void SetError(string e)
        {
            lastError = e;
        }

        void RaiseEvent(string text)
        {
            eventSeq++;
            eventText = text;
        }

        Telemetry ReadTelemetry(bool force)
        {
            if (!force && tel != null && (DateTime.UtcNow - telAt).TotalSeconds < 3) return tel;
            CheckRunning(); // no new native calls while stopping
            Telemetry t = hardware.ReadStandard();

            if (force || (DateTime.UtcNow - fwSensorsAt).TotalSeconds >= 15)
            {
                CheckRunning();
                Telemetry fw = new Telemetry();
                hardware.ReadFirmwareSensors(fw);
                lastTemp = fw.TempC;
                lastFwVoltage = fw.VoltageMv;
                lastFwCurrent = fw.CurrentMa;
                fwSensorsAt = DateTime.UtcNow;
            }
            t.TempC = lastTemp;
            if (lastFwVoltage.HasValue) t.VoltageMv = lastFwVoltage;
            if (lastFwCurrent.HasValue) t.CurrentMa = lastFwCurrent;
            else if (t.RateMw.HasValue && t.VoltageMv.HasValue && t.VoltageMv.Value > 0)
                t.CurrentMa = (int)Math.Round(t.RateMw.Value * 1000.0 / t.VoltageMv.Value);

            if (cfg.Calibrating && lastAc == true && t.OnAC == false)
            {
                Log.Write("[calibration] WARNING: AC adapter disconnected during calibration");
                RaiseEvent("WARNING: AC adapter disconnected during calibration. Reconnect it.");
            }
            if (t.OnAC.HasValue) lastAc = t.OnAC;
            tel = t;
            telAt = DateTime.UtcNow;
            return t;
        }

        void RefreshConflicts()
        {
            if ((DateTime.UtcNow - conflictsAt).TotalSeconds < 120) return;
            conflictsAt = DateTime.UtcNow;
            List<string> found = new List<string>();
            try
            {
                foreach (ServiceController s in ServiceController.GetServices())
                {
                    using (s)
                    {
                        if (s.ServiceName.Equals(Paths.ServiceName, StringComparison.OrdinalIgnoreCase)) continue;
                        string n = (s.ServiceName + " " + s.DisplayName).ToLowerInvariant();
                        if ((n.Contains("acer") || n.Contains("care center")) && s.Status == ServiceControllerStatus.Running)
                            found.Add(s.DisplayName);
                    }
                }
            }
            catch { }
            conflicts = string.Join("; ", found.ToArray());
        }

        static string N(int? v)
        {
            return v.HasValue ? v.Value.ToString(Util.Inv) : "";
        }

        static string Bo(bool? v)
        {
            return v.HasValue ? (v.Value ? "1" : "0") : "";
        }

        public string Snapshot()
        {
            lock (sync)
            {
                CheckRunning();
                if (hw == null || (DateTime.UtcNow - hwAt).TotalSeconds > 10)
                {
                    try
                    {
                        HealthStatus h = hardware.GetStatus();
                        if (h.Valid) SetHw(h);
                        else { hwValidNow = false; hwAt = DateTime.UtcNow; }
                    }
                    catch (Exception ex)
                    {
                        hwValidNow = false;
                        hwAt = DateTime.UtcNow;
                        SetError(ex.Message);
                    }
                }
                Telemetry t;
                try { t = ReadTelemetry(false); }
                catch { t = tel ?? new Telemetry(); }
                if (!stopping) RefreshConflicts();

                DateTime cut = DateTime.Now.AddHours(-24);
                corrections.RemoveAll(delegate(DateTime d) { return d < cut; });

                StringBuilder sb = new StringBuilder();
                Action<string, string> kv = delegate(string k, string v) { sb.Append(k).Append('=').Append(v ?? "").Append('\n'); };
                kv("version", Paths.Version);
                kv("fw_valid", hwValidNow && hw != null ? "1" : "0");
                if (hw != null)
                {
                    kv("health_supported", hw.HealthSupported ? "1" : "0");
                    kv("calib_supported", hw.CalibSupported ? "1" : "0");
                    kv("health_on", hw.HealthOn ? "1" : "0");
                    kv("calib_on", hw.CalibOn ? "1" : "0");
                    kv("fw_raw", hw.Raw);
                }
                kv("limit_desired", Util.OnOff(cfg.LimitDesired));
                kv("calibrating", cfg.Calibrating ? "1" : "0");
                kv("power_restore_pending", cfg.SavedLidAction >= 0 || cfg.SavedCritAction >= 0 ? "1" : "0");
                kv("calib_started", cfg.CalibStartedUtc == DateTime.MinValue ? "" : cfg.CalibStartedUtc.ToLocalTime().ToString("o", Util.Inv));
                kv("percent", N(t.Percent));
                kv("on_ac", Bo(t.OnAC));
                kv("charging", Bo(t.Charging));
                kv("discharging", Bo(t.Discharging));
                kv("rate_mw", N(t.RateMw));
                kv("voltage_mv", N(t.VoltageMv));
                kv("current_ma", N(t.CurrentMa));
                kv("temp_c", t.TempC.HasValue ? t.TempC.Value.ToString("0.0", Util.Inv) : "");
                kv("remaining_mwh", N(t.RemainingMwh));
                kv("full_mwh", N(t.FullMwh));
                kv("design_mwh", N(t.DesignMwh));
                kv("cycles", N(t.Cycles));
                kv("model", t.Model);
                kv("corrections_24h", corrections.Count.ToString(Util.Inv));
                kv("last_correction", corrections.Count > 0 ? corrections[corrections.Count - 1].ToString("o", Util.Inv) : "");
                kv("last_error", lastError.Replace('\n', ' '));
                kv("event_seq", eventSeq.ToString(Util.Inv));
                kv("event_text", eventText);
                kv("conflicts", conflicts);
                return sb.ToString();
            }
        }
    }
}
