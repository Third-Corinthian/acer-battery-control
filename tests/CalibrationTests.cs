using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AcerBatteryControl;

static class CalibrationTests
{
    static int fails;
    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) fails++;
    }
    static bool Refused(Action work)
    {
        try { work(); return false; }
        catch (AcerException) { return true; }
    }
    static HealthStatus State(bool limit, bool calibration)
    {
        return new HealthStatus { Valid = true, HealthSupported = true, CalibSupported = true,
            HealthOn = limit, CalibOn = calibration };
    }
    sealed class Journal : Config
    {
        public bool FailSave;
        public int PersistedAction = -1;
        public override void Save()
        {
            if (FailSave) throw new IOException("Simulated journal failure");
            PersistedAction = SavedCritAction;
        }
    }
    sealed class Battery : IBatteryHardware
    {
        public HealthStatus Current = State(false, true);
        public Telemetry Sample = new Telemetry { Percent = 3, OnAC = true, Discharging = true, RateMw = -15000 };
        public bool FailTelemetry, FailStatus, BadStopReadback;
        public Action OnStop;
        public readonly Queue<HealthStatus> Reads = new Queue<HealthStatus>();
        public readonly List<string> Trace = new List<string>();
        public readonly List<string> Writes = new List<string>();
        public HealthStatus GetStatus()
        {
            Trace.Add("read");
            if (FailStatus) throw new AcerException("Simulated firmware timeout");
            return Reads.Count > 0 ? Reads.Dequeue() : Current;
        }
        public int SetMode(byte mode, bool on)
        {
            string write = mode + ":" + on;
            Writes.Add(write); Trace.Add(write);
            Current = State(mode == 1 ? on : Current.HealthOn, mode == 2 ? on : Current.CalibOn);
            if (mode == 2 && !on && OnStop != null) OnStop();
            if (mode == 2 && !on && BadStopReadback) Reads.Enqueue(new HealthStatus());
            return 0;
        }
        public Telemetry ReadStandard()
        {
            if (FailTelemetry) throw new AcerException("Simulated telemetry timeout");
            return Sample;
        }
        public void ReadFirmwareSensors(Telemetry t) { }
    }
    sealed class Plan : PowerAccess
    {
        public readonly Dictionary<Guid, uint> Actions = new Dictionary<Guid, uint>();
        public Guid Scheme = Guid.Empty;
        public uint Level = 5, Lid = 1;
        public bool FailDisable, FailRestore, FailLevel, IgnoreDisable, FailApply;
        public int DisableWrites, RestoreWrites;
        public Journal Journal;
        public bool SavedBeforeWrite = true;
        public Plan() { Actions[Scheme] = 2; }
        public override Guid ActiveScheme() { return Scheme; }
        public override uint ReadAcLid(Guid scheme) { return Lid; }
        public override void WriteAcLid(Guid scheme, uint value) { Lid = value; }
        public override uint ReadAcCriticalAction(Guid scheme) { return Actions[scheme]; }
        public override uint ReadAcCriticalLevel(Guid scheme)
        {
            if (FailLevel) throw new IOException("Simulated level read failure");
            return Level;
        }
        public override void WriteAcCriticalAction(Guid scheme, uint value)
        {
            if (value == 0)
            {
                DisableWrites++;
                if (Journal != null) SavedBeforeWrite &= Journal.PersistedAction >= 0;
                if (FailDisable) throw new IOException("Simulated critical-action write failure");
                if (!IgnoreDisable) Actions[scheme] = value;
                if (FailApply) throw new IOException("Simulated scheme apply failure");
            }
            else
            {
                RestoreWrites++;
                if (FailRestore) throw new IOException("Simulated critical-action restore failure");
                Actions[scheme] = value;
            }
        }
        public override IntPtr KeepAwake() { return new IntPtr(1); }
        public override void Release(IntPtr request) { }
        public uint Action { get { return Actions[Scheme]; } }
    }
    sealed class Rig
    {
        public readonly Journal Config = new Journal { Calibrating = true };
        public readonly Battery Hardware = new Battery();
        public readonly Plan Power = new Plan();
        public readonly CalibrationGuards Guards;
        public readonly Engine Engine;
        public long Now;
        public Rig()
        {
            Power.Journal = Config;
            Guards = new CalibrationGuards(Power);
            Engine = new Engine(Config, Hardware, Guards, delegate { }, delegate { return Now; });
        }
        public void Poll() { Engine.Enforce("test"); }
    }
    static IpcResult Snapshot(Engine engine)
    {
        var r = new IpcResult { Ok = true };
        foreach (string line in engine.Snapshot().Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) r.Data[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return r;
    }

    static void FloorTests()
    {
        var r = new Rig();
        r.Poll();
        Check("floor first sample only journals the guards", r.Hardware.Writes.Count == 0 && r.Power.Action == 0 && r.Power.SavedBeforeWrite);
        r.Now += 15000; r.Poll();
        int stop = r.Hardware.Trace.IndexOf("2:False");
        Check("floor second sample uses confirmed stop and readback", stop >= 2 && r.Hardware.Trace[stop - 1] == "read"
            && r.Hardware.Trace[stop - 2] == "read" && r.Hardware.Trace[stop + 1] == "read");
        Check("floor restores both guards then resumes desired limit", r.Hardware.Writes.Count == 2
            && r.Hardware.Writes[0] == "2:False" && r.Hardware.Writes[1] == "1:True"
            && !r.Config.Calibrating && r.Config.SavedCritAction == -1 && r.Config.SavedLidAction == -1
            && r.Power.Action == 2 && r.Power.Lid == 1);
        IpcResult snap = Snapshot(r.Engine);
        r.Poll();
        Check("floor warning persists once after normal enforcement", snap.Get("event_text").StartsWith("WARNING: Calibration stopped at the safety floor")
            && snap.Get("event_seq") == Snapshot(r.Engine).Get("event_seq"));

        r = new Rig(); r.Config.LimitDesired = false; r.Poll(); r.Poll();
        Check("floor preserves disabled limit preference", r.Hardware.Writes.Count == 1 && !r.Hardware.Current.HealthOn);

        r = new Rig(); r.Poll(); r.Hardware.Sample.Percent = null; r.Poll(); r.Hardware.Sample.Percent = 3; r.Poll();
        Check("invalid sample resets consecutive floor readings", r.Hardware.Writes.Count == 0);
        r.Poll(); Check("two fresh valid readings after invalid sample stop", r.Hardware.Writes.Count == 2);
        r = new Rig(); r.Poll(); r.Hardware.Sample.Percent = 4; r.Poll();
        Check("single low outlier does not stop calibration", r.Hardware.Writes.Count == 0);
        r = new Rig(); r.Hardware.Sample.Percent = -1; r.Poll(); r.Poll();
        Check("out of range telemetry cannot stop calibration or suppress protection", r.Hardware.Writes.Count == 0 && r.Power.DisableWrites == 0);

        r = new Rig(); r.Poll(); r.Hardware.Sample.RateMw = 1000; r.Poll(); r.Poll();
        Check("positive recharge rate overrides discharging flag", r.Config.Calibrating && r.Hardware.Writes.Count == 0);
        r.Hardware.Sample.RateMw = null; r.Hardware.Sample.Charging = true; r.Poll(); r.Poll();
        Check("charging flag also cancels the floor streak", r.Hardware.Writes.Count == 0);
        r = new Rig(); r.Hardware.Sample.Discharging = null; r.Poll(); r.Poll();
        Check("negative rate on AC is sufficient discharge evidence", !r.Config.Calibrating);
        r = new Rig(); r.Hardware.Sample.RateMw = null; r.Poll(); r.Poll();
        Check("discharging telemetry alone is sufficient evidence", !r.Config.Calibrating);

        r = new Rig(); r.Poll(); r.Hardware.Reads.Enqueue(State(false, true)); r.Hardware.Reads.Enqueue(State(false, true));
        r.Hardware.Reads.Enqueue(State(false, true)); r.Hardware.Reads.Enqueue(State(true, true)); r.Poll();
        Check("floor rejects inconsistent stop confirmation", r.Hardware.Writes.Count == 0 && r.Config.Calibrating && r.Power.Action == 0);
        r = new Rig(); r.Hardware.BadStopReadback = true; r.Poll(); r.Poll();
        Check("uncertain floor stop retains intent and guards without limit write", r.Hardware.Writes.Count == 1
            && r.Config.Calibrating && r.Config.SavedCritAction == 2 && r.Power.Action == 0);
        r.Poll();
        Check("later confirmed firmware end recovers uncertain floor stop", !r.Config.Calibrating && r.Power.Action == 2 && r.Hardware.Current.HealthOn);

        r = new Rig(); r.Power.FailDisable = true; r.Hardware.Sample.Percent = 8; r.Poll(); r.Poll();
        Check("failed critical write keeps Windows action and waits above fallback floor", r.Hardware.Writes.Count == 0 && r.Power.Action == 2);
        r.Hardware.Sample.Percent = 7; r.Poll(); r.Poll();
        Check("critical write failure uses current critical level plus two", !r.Config.Calibrating && r.Hardware.Current.HealthOn);
        r = new Rig(); r.Power.FailLevel = true; r.Hardware.Sample.Percent = 60; r.Poll(); r.Poll();
        Check("unreadable critical level stops on confirmed discharge without suppressing protection", !r.Config.Calibrating && r.Power.DisableWrites == 0);
        r = new Rig(); r.Power.IgnoreDisable = true; r.Hardware.Sample.Percent = 7; r.Poll(); r.Poll();
        Check("critical action readback mismatch selects fallback floor", !r.Config.Calibrating && r.Power.Action == 2);
        r = new Rig(); r.Power.FailApply = true; r.Hardware.Sample.Percent = 7; r.Poll(); r.Poll();
        Check("partial power write restores the original action and selects fallback", !r.Config.Calibrating && r.Power.Action == 2);
        r = new Rig(); r.Power.FailApply = r.Power.FailRestore = true; r.Hardware.Sample.Percent = 40; r.Poll();
        r.Power.FailApply = false; r.Poll();
        Check("partial apply with failed restoration retries applying the guard", r.Power.DisableWrites == 2 && r.Guards.CriticalProtected);

        r = new Rig(); r.Hardware.OnStop = delegate { r.Config.FailSave = true; }; r.Poll(); r.Poll();
        Check("confirmed floor stop still notifies and releases after final save error", !r.Config.Calibrating
            && r.Power.Action == 2 && r.Power.Lid == 1 && Snapshot(r.Engine).Get("event_text").Contains("safety floor"));
        r.Config.FailSave = false; r.Poll();
        Check("floor final save failure recovers journals and desired limit on retry", r.Config.SavedCritAction == -1
            && r.Config.SavedLidAction == -1 && r.Hardware.Current.HealthOn);
    }

    static void TelemetryTests()
    {
        var r = new Rig(); r.Hardware.Sample.Percent = 11; r.Poll();
        Check("calibration above ten percent polls every sixty seconds", r.Engine.CalibrationPollSeconds == 60);
        r.Hardware.Sample.Percent = 10; r.Poll();
        Check("calibration at ten percent polls every fifteen seconds", r.Engine.CalibrationPollSeconds == 15);
        r.Hardware.FailTelemetry = true; r.Now = 120000; r.Poll();
        Check("telemetry grace period makes no firmware write", r.Hardware.Writes.Count == 0 && r.Power.Action == 0);
        r.Now++; r.Hardware.FailStatus = true; r.Poll();
        Check("telemetry timeout restores critical action even when firmware reads fail", r.Power.Action == 2
            && r.Config.SavedCritAction == -1 && r.Hardware.Writes.Count == 0 && r.Engine.CalibrationPollSeconds == 15);
        r.Hardware.FailStatus = false; r.Hardware.FailTelemetry = false; r.Hardware.Sample.Percent = 7; r.Poll();
        Check("telemetry recovery does not disable Windows protection again", r.Power.Action == 2 && r.Hardware.Writes.Count == 0);
        r.Poll(); Check("telemetry recovery uses fallback floor", !r.Config.Calibrating);

        r = new Rig(); r.Hardware.Sample.Percent = 8; r.Poll(); r.Power.FailRestore = true;
        r.Hardware.Sample.Percent = null; r.Now = 120001; r.Poll();
        Check("telemetry timeout retains a failed restore journal", r.Config.SavedCritAction == 2 && r.Hardware.Writes.Count == 0);
        r.Power.FailRestore = false; r.Now += 15000; r.Poll();
        Check("telemetry timeout retries critical restoration without valid data", r.Config.SavedCritAction == -1 && r.Power.Action == 2);

        r = new Rig(); r.Hardware.Sample.Percent = null; r.Poll(); r.Poll();
        Check("initial unknown telemetry never suppresses critical action", r.Power.DisableWrites == 0 && r.Hardware.Writes.Count == 0);
        r = new Rig(); r.Hardware.Sample.RateMw = null; r.Hardware.Sample.Discharging = null; r.Hardware.Sample.Charging = false;
        r.Poll(); r.Poll();
        Check("not charging alone is insufficient telemetry for suppressing protection", r.Power.DisableWrites == 0 && r.Hardware.Writes.Count == 0);
        r = new Rig(); r.Hardware.Sample.Percent = 8; r.Poll(); r.Hardware.Sample.Percent = null;
        r.Now = 90000; r.Poll(); r.Hardware.Sample.Percent = 8; r.Now = 100000; r.Poll();
        r.Hardware.Sample.Percent = null; r.Now = 150000; r.Poll();
        Check("valid telemetry resets the missing data deadline", r.Power.Action == 0);

        r = new Rig(); r.Poll(); Snapshot(r.Engine); Snapshot(r.Engine);
        Check("display polls do not count as floor confirmation samples", r.Hardware.Writes.Count == 0);
        r = new Rig(); r.Config.Calibrating = false; r.Hardware.Current = State(false, false);
        r.Hardware.Sample.Percent = 10; int requested = 0, seconds = 0;
        r.Engine.WorkRequested += delegate { requested++; seconds = r.Engine.CalibrationPollSeconds; };
        r.Engine.StartCalibration();
        Check("low charge start requests the fifteen second cadence once", requested == 1 && seconds == 15 && r.Power.Action == 0);
        Check("start persists critical backup before the calibration firmware write", r.Config.PersistedAction == 2
            && r.Power.SavedBeforeWrite && r.Hardware.Writes.Count == 1 && r.Hardware.Writes[0] == "2:True");
        r = new Rig(); r.Power.FailDisable = true; r.Power.Level = 20; r.Hardware.Sample.Percent = 22; r.Poll();
        Check("high fallback floor also selects fast polling", r.Engine.CalibrationPollSeconds == 15);
    }

    static void RecoveryTests()
    {
        var r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); r.Power.FailRestore = true;
        r.Engine.StopCalibration();
        Check("stop restores lid despite failed critical restore and keeps journal", r.Config.SavedCritAction == 2
            && r.Config.SavedLidAction == -1 && r.Power.Lid == 1);
        IpcResult snap = Snapshot(r.Engine);
        Check("pending critical restore is visible and blocks tray stop", snap.Get("power_restore_pending") == "1" && TrayContext.StopBlocked(snap));
        r.Power.FailRestore = false; r.Poll();
        Check("normal enforcement retries pending critical restore", r.Config.SavedCritAction == -1 && r.Power.Action == 2);

        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); r.Config.FailSave = true;
        r.Guards.RestoreCritical(r.Config);
        Check("restore save failure retains original critical backup", r.Config.SavedCritAction == 2 && r.Power.Action == 2);
        r.Config.FailSave = false; r.Guards.RestoreCritical(r.Config);
        Check("restore save failure succeeds on retry", r.Config.SavedCritAction == -1);
        r = new Rig(); r.Config.FailSave = true; r.Hardware.Sample.Percent = 40; r.Poll();
        Check("critical journal save failure prevents disabling Windows action", r.Power.DisableWrites == 0 && r.Power.Action == 2);

        foreach (bool calibrating in new bool[] { false, true })
        {
            var config = new Config { Calibrating = calibrating, SavedCritAction = 2, SavedCritScheme = Guid.Empty.ToString() };
            config.Save();
            var power = new Plan(); power.Actions[Guid.Empty] = 0;
            var engine = new Engine(Config.Load(), new Battery(), new CalibrationGuards(power), delegate { });
            engine.Startup();
            Check("restart restores persisted critical action with calibration " + calibrating,
                Config.Load().SavedCritAction == -1 && power.Action == 2);
        }
        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); r.Engine.Shutdown("test shutdown");
        Check("shutdown restores critical action while retaining calibration intent", r.Config.Calibrating && r.Power.Action == 2 && r.Config.SavedCritAction == -1);
        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); r.Hardware.Current = State(false, false); r.Poll();
        Check("natural firmware end restores both settings and limit", !r.Config.Calibrating && r.Power.Action == 2 && r.Power.Lid == 1 && r.Hardware.Current.HealthOn);

        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); Guid original = r.Power.Scheme;
        r.Power.Scheme = Guid.NewGuid(); r.Power.Actions[r.Power.Scheme] = 3; r.Poll();
        Check("scheme change restores old scheme before journaling new action", r.Power.Actions[original] == 2
            && r.Power.Action == 0 && r.Config.SavedCritAction == 3 && r.Config.SavedCritScheme == r.Power.Scheme.ToString());
        r.Engine.Shutdown("test shutdown");
        Check("scheme change recovery restores new original action", r.Power.Action == 3 && r.Config.SavedCritAction == -1);
        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Power.Actions[r.Power.Scheme] = 0; r.Poll(); r.Engine.Shutdown("test shutdown");
        Check("original Do nothing action is preserved through release", r.Power.Action == 0 && r.Config.SavedCritAction == -1);

        r = new Rig(); r.Hardware.Sample.Percent = 40; r.Poll(); r.Power.FailRestore = true;
        original = r.Power.Scheme; r.Power.Scheme = Guid.NewGuid(); r.Power.Actions[r.Power.Scheme] = 3; r.Poll();
        Check("failed old scheme restore keeps original backup and new scheme protection", r.Config.SavedCritScheme == original.ToString()
            && r.Config.SavedCritAction == 2 && r.Power.Action == 3 && !r.Guards.CriticalProtected);
        r.Power.FailRestore = false; r.Poll();
        Check("old scheme recovery retries before guarding new scheme", r.Power.Actions[original] == 2 && r.Power.Action == 0 && r.Config.SavedCritAction == 3);
    }

    static void JournalAndWarningTests()
    {
        var config = new Config { SavedCritAction = 2, SavedCritScheme = Guid.NewGuid().ToString() }; config.Save();
        string problem;
        var loaded = Config.LoadStrict(out problem);
        Check("critical journal roundtrip and strict validation", loaded != null && loaded.SavedCritAction == 2
            && loaded.SavedCritScheme == config.SavedCritScheme && Config.Load().SavedCritAction == 2);
        Check("uninstall policy rejects pending critical restore", Refused(delegate {
            Installer.CheckNothingPending(false, delegate { return State(true, false); }, delegate { return null; }); }));
        Check("forced uninstall accepts pending journal", !Refused(delegate {
            Installer.CheckNothingPending(true, delegate { return State(true, false); }, delegate { return null; }); }));
        Check("forced uninstall still refuses reported firmware calibration", Refused(delegate {
            Installer.CheckNothingPending(true, delegate { return State(true, true); }, delegate { return null; }); }));
        Check("uninstall refuses unreadable firmware without force", Refused(delegate {
            Installer.CheckNothingPending(false, delegate { throw new AcerException("Simulated read error"); }, delegate { return null; }); }));
        Check("forced uninstall retains existing unknown firmware override", !Refused(delegate {
            Installer.CheckNothingPending(true, delegate { throw new AcerException("Simulated read error"); }, delegate { return null; }); }));
        string legacy = "calibrating=0\nsaved_lid_action=-1\nsaved_lid_scheme=\n";
        File.WriteAllText(Paths.ConfigFile, legacy, Encoding.UTF8);
        Check("legacy journal without critical keys remains compatible", Config.LoadStrict(out problem) != null);
        foreach (string invalid in new string[] {
            "saved_crit_action=2\n", "saved_crit_scheme=\n", "saved_crit_action=4\nsaved_crit_scheme=\n",
            "saved_crit_action=2\nsaved_crit_scheme=invalid\n", "saved_crit_action=-1\nsaved_crit_scheme=invalid\n",
            "saved_crit_action=-1\nsaved_crit_action=2\nsaved_crit_scheme=\n" })
        {
            File.WriteAllText(Paths.ConfigFile, legacy + invalid, Encoding.UTF8);
            Check("incomplete or corrupt critical journal refused: " + invalid.Replace('\n', ' '), Config.LoadStrict(out problem) == null);
        }
        new Config().Save();
        var p = new Plan();
        string warning = CalibrationSafety.Warning(p);
        Check("shared warning reads current action and level without writes", warning.Contains("currently: Hibernate at 5%")
            && warning.Contains("100%") && warning.Contains("3%") && warning.Contains("80%")
            && warning.Contains("Do not unplug") && p.DisableWrites == 0 && p.RestoreWrites == 0);
        p.FailLevel = true;
        Check("warning explains unreadable settings without blocking display", CalibrationSafety.Warning(p).Contains("currently: unavailable"));
        Check("dry-run blocks critical action writes", Refused(delegate { Power.WriteAcCriticalAction(Guid.Empty, 0); }));
    }

    public static int Run()
    {
        FloorTests();
        TelemetryTests();
        RecoveryTests();
        JournalAndWarningTests();
        return fails;
    }
}
