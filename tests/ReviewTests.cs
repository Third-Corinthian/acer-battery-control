using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AcerBatteryControl;

static class ReviewTests
{
    static int fails;
    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) fails++;
    }
    sealed class FakeConfig : Config
    {
        public bool FailSave;
        public override void Save() { if (FailSave) throw new IOException("simulated save failed"); }
    }
    sealed class FakeGuards : CalibrationGuards
    {
        public int Releases;
        public override void Acquire(Config cfg) { }
        public override void Release(Config cfg) { Releases++; }
        public override void RestoreLid(Config cfg) { }
        public override void RestoreCritical(Config cfg) { }
        public override void UpdateCritical(Config cfg, bool allowOverride) { }
    }
    sealed class FakeHardware : IBatteryHardware
    {
        public readonly Queue<HealthStatus> Reads = new Queue<HealthStatus>();
        public readonly List<byte> Writes = new List<byte>();
        public int ReadCount, ReadsAtWrite;
        public HealthStatus Current = Status(true, false);
        public Func<HealthStatus> Read;
        public HealthStatus GetStatus()
        {
            ReadCount++;
            if (Read != null) return Read();
            if (Reads.Count > 0) Current = Reads.Dequeue();
            return Current;
        }
        public int SetMode(byte mask, bool on)
        {
            Writes.Add(mask); ReadsAtWrite = ReadCount;
            Current = Status(mask == 1 ? on : Current.HealthOn, mask == 2 ? on : Current.CalibOn);
            return 0;
        }
        public Telemetry ReadStandard() { return new Telemetry { OnAC = true }; }
        public void ReadFirmwareSensors(Telemetry t) { }
    }
    static HealthStatus Status(bool health, bool calib)
    {
        return new HealthStatus { Valid = true, HealthSupported = true, CalibSupported = true, HealthOn = health, CalibOn = calib };
    }
    static Engine Make(FakeHardware h, FakeConfig c, FakeGuards g)
    {
        return new Engine(c, h, g, delegate { });
    }
    static void Attempt(Action action) { try { action(); } catch (AcerException) { } }

    static void FirmwareTests()
    {
        Check("parser: no mode, missing or short arrays are invalid",
            !AcerWmi.ParseStatus(0, new byte[5], new byte[2]).Valid && !AcerWmi.ParseStatus(3, null, null).Valid
            && !AcerWmi.ParseStatus(3, new byte[1], null).Valid);
        foreach (bool desired in new bool[] { false, true })
        {
            var h = new FakeHardware { Current = Status(!desired, true) };
            var c = new FakeConfig();
            Make(h, c, new FakeGuards()).SetLimit(desired);
            Check("external calibration: no limit write " + desired, h.Writes.Count == 0 && c.Calibrating);
            h = new FakeHardware { Current = Status(!desired, false) };
            Make(h, new FakeConfig { Calibrating = true }, new FakeGuards()).SetLimit(desired);
            Check("saved calibration: limit deferred " + desired, h.Writes.Count == 0);
        }
        for (int command = 0; command < 4; command++)
        {
            var h = new FakeHardware { Current = Status(false, command == 3) };
            var e = Make(h, new FakeConfig { Calibrating = command == 3 }, new FakeGuards());
            if (command == 0) e.Enforce("test");
            else if (command == 1) e.SetLimit(true);
            else if (command == 2) e.StartCalibration();
            else e.StopCalibration();
            Check("command " + command + ": confirmation and readback", h.Writes.Count > 0 && h.ReadsAtWrite >= 2 && h.ReadCount > h.ReadsAtWrite);
            for (int invalid = 0; invalid < 3; invalid++)
            {
                h = new FakeHardware();
                h.Reads.Enqueue(invalid == 0 ? new HealthStatus() : Status(false, command == 3));
                h.Reads.Enqueue(invalid == 1 ? new HealthStatus() : Status(true, command != 3));
                e = Make(h, new FakeConfig { Calibrating = command == 3 }, new FakeGuards());
                Attempt(delegate
                {
                    if (command == 0) e.Enforce("test");
                    else if (command == 1) e.SetLimit(true);
                    else if (command == 2) e.StartCalibration();
                    else e.StopCalibration();
                });
                Check("command " + command + ": reject sequence " + invalid, h.Writes.Count == 0);
            }
        }
        var stopHw = new FakeHardware();
        stopHw.Reads.Enqueue(Status(false, true)); stopHw.Reads.Enqueue(Status(false, true));
        stopHw.Reads.Enqueue(new HealthStatus());
        var stopCfg = new FakeConfig { Calibrating = true };
        var stopGuards = new FakeGuards();
        Attempt(delegate { Make(stopHw, stopCfg, stopGuards).StopCalibration(); });
        Check("uncertain stop preserves calibration and guards", stopHw.Writes.Count == 1 && stopHw.Writes[0] == 2 && stopCfg.Calibrating && stopGuards.Releases == 0);

        var cap = new FakeHardware();
        cap.Reads.Enqueue(Status(false, false));
        var different = Status(false, false); different.CalibSupported = false;
        cap.Reads.Enqueue(different);
        Make(cap, new FakeConfig(), new FakeGuards()).Enforce("support test");
        Check("inconsistent firmware capabilities prevent writes", cap.Writes.Count == 0);
        var hSave = new FakeHardware();
        bool failed = false;
        try { Make(hSave, new FakeConfig { FailSave = true }, new FakeGuards()).StartCalibration(); }
        catch (IOException) { failed = true; }
        Check("intent save failed: no firmware write", failed && hSave.Writes.Count == 0);
        var pendingHw = new FakeHardware();
        var pending = Make(pendingHw, new FakeConfig(), new FakeGuards());
        int requests = 0; pending.WorkRequested += delegate { requests++; };
        pending.StartCalibration();
        Check("manual calibration requests a prompt check", requests == 1);
        pending.BeginStop();
        int writes = pendingHw.Writes.Count;
        Attempt(delegate { pending.SetLimit(false); }); Attempt(delegate { pending.StartCalibration(); });
        Attempt(delegate { pending.StopCalibration(); }); pending.Enforce("stop test");
        Check("no command writes after stopping", writes == pendingHw.Writes.Count);
    }

    sealed class FakePower : PowerAccess
    {
        public bool FailWrite;
        public int Writes;
        public uint Lid = 1;
        public override Guid ActiveScheme() { return Guid.Empty; }
        public override uint ReadAcLid(Guid scheme) { return Lid; }
        public override uint ReadAcCriticalAction(Guid scheme) { return 0; }
        public override uint ReadAcCriticalLevel(Guid scheme) { return 5; }
        public override void WriteAcCriticalAction(Guid scheme, uint value) { }
        public override void WriteAcLid(Guid scheme, uint value)
        {
            Writes++;
            if (FailWrite) throw new IOException("simulated power plan unavailable");
            Lid = value;
        }
        public override IntPtr KeepAwake() { return new IntPtr(42); }
        public override void Release(IntPtr request) { }
    }

    static void RecoveryTests()
    {
        var cfg = new FakeConfig { SavedLidAction = 1, SavedLidScheme = Guid.Empty.ToString() };
        var power = new FakePower { FailWrite = true, Lid = 0 };
        var protection = new CalibrationGuards(power);
        protection.RestoreLid(cfg);
        Check("failed restore preserves journal", cfg.SavedLidAction == 1 && cfg.SavedLidScheme == Guid.Empty.ToString());
        power.FailWrite = false; cfg.FailSave = true;
        protection.RestoreLid(cfg);
        Check("failed restore save preserves journal", cfg.SavedLidAction == 1 && power.Lid == 1);
        cfg.FailSave = false; protection.RestoreLid(cfg);
        Check("restore retried and completed", cfg.SavedLidAction == -1 && power.Writes == 3);

        var fakeGuards = new FakeGuards();
        var e = Make(new FakeHardware(), new FakeConfig { Calibrating = true, FailSave = true }, fakeGuards);
        e.Enforce("test");
        Check("config error does not skip release", fakeGuards.Releases == 1);
        cfg = new FakeConfig { Calibrating = true, SavedLidAction = 2, SavedLidScheme = Guid.Empty.ToString() };
        power = new FakePower { Lid = 0 };
        new Engine(cfg, new FakeHardware(), new CalibrationGuards(power), delegate { }).Startup();
        Check("startup after crash recovers journal even with calibrating=1", power.Lid == 2 && cfg.SavedLidAction == -1);

        cfg = new FakeConfig { FailSave = true };
        power = new FakePower(); protection = new CalibrationGuards(power);
        protection.Acquire(cfg); protection.Acquire(cfg);
        Check("unsaved journal prevents all lid attempts", power.Writes == 0);
        cfg.FailSave = false; protection.Acquire(cfg);
        Check("acquisition retried after config error", power.Writes == 1 && power.Lid == 0);
        protection.Release(cfg);
        Check("full release", power.Lid == 1 && cfg.SavedLidAction == -1);
    }

    static void TimeoutTests()
    {
        var expired = new CallPermit(Stopwatch.StartNew(), 1);
        Thread.Sleep(5);
        bool clockBlocked = false;
        try { expired.Check(); } catch (AcerException) { clockBlocked = true; }
        Check("permission expires even before caller notification", clockBlocked);
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            var bounded = new BoundedCall(100);
            Thread native = null;
            bool lateWrite = false;
            var hw = new FakeHardware();
            hw.Read = delegate
            {
                return bounded.Run<HealthStatus>(delegate(CallPermit permit)
                {
                    native = Thread.CurrentThread; entered.Set(); release.WaitOne();
                    permit.Check(); lateWrite = true; return Status(false, false);
                });
            };
            var e = Make(hw, new FakeConfig(), new FakeGuards());
            var worker = new Thread(delegate() { e.Enforce("test timeout"); }); worker.IsBackground = true;
            var shutdown = new Thread(delegate() { e.Shutdown("test timeout"); }); shutdown.IsBackground = true;
            try
            {
                worker.Start(); Check("simulated driver entered", entered.WaitOne(1000));
                shutdown.Start();
                Check("shutdown completes even with driver still blocked", shutdown.Join(1500) && worker.Join(1500));
                bool submitted = false;
                for (int i = 0; i < 100; i++) Attempt(delegate { bounded.Run<int>(delegate { submitted = true; return 1; }); });
                Check("timeout does not accumulate new workers", !submitted);
            }
            finally
            {
                release.Set();
                // generous margins, the test machine may be busy
                Check("simulated native worker exited", native != null && native.Join(10000));
                Check("no late write continuation", !lateWrite);
                int reopened = 0;
                try { reopened = bounded.Run<int>(delegate { return 5; }); } catch (AcerException) { }
                Check("channel reopened when driver responds", !bounded.Stuck && reopened == 5);
                worker.Join(1500); if (shutdown.ThreadState != System.Threading.ThreadState.Unstarted) shutdown.Join(1500);
            }
        }
        var call = new BoundedCall(1000);
        bool caught = false;
        try { call.Run<int>(delegate { throw new InvalidOperationException("simulated provider exception"); }); }
        catch (AcerException) { caught = true; }
        Check("worker exception contained and next call possible", caught && call.Run<int>(delegate { return 7; }) == 7);
    }

    static void PipeTests()
    {
        foreach (string malformed in new string[] { "LIMIT ON", "LIMIT ON" + new string(' ', 248) + "INVALID\n", new string('\r', 256) + "LIMIT ON\n", "LIMIT\0 ON\n" })
        {
            bool rejected = false;
            try { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(malformed))) Ipc.ReadLine(stream, 256); }
            catch (IOException) { rejected = true; }
            Check("incomplete/long/malformed request rejected", rejected);
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("STATUS\r\n")))
            Check("valid CRLF request accepted", Ipc.ReadLine(stream, 256) == "STATUS");
        string name = "acerbat-test-" + Guid.NewGuid().ToString("N");
        var hw = new FakeHardware { Current = Status(false, false) };
        var security = new PipeSecurity();
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        var server = new PipeServer(Make(hw, new FakeConfig(), new FakeGuards()), name, 150, security);
        server.Start();
        try
        {
            var response = Ipc.Call("LIMIT ON", 2000, name, 3000);
            if (!response.Ok) Console.WriteLine("Isolated pipe diagnostics: " + response.Message + " | " + Log.Tail(3));
            Check("real isolated pipe: simulated command and response", response.Ok && hw.Writes.Count == 1);
            var idle = new List<NamedPipeClientStream>();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    idle.Add(client); client.Connect(2000);
                }
                Thread.Sleep(400);
                response = Ipc.Call("LIMIT ON", 2000, name, 3000);
                Check("four silent clients do not exhaust the pipe", response.Ok && hw.Writes.Count == 1);
            }
            finally { foreach (var client in idle) client.Dispose(); }
        }
        finally { server.Stop(); }
        Check("all pipe workers exited", server.IsStopped);
        server = new PipeServer(Make(new FakeHardware(), new FakeConfig(), new FakeGuards()), name, 150, security);
        server.Start();
        try { Check("reconnection after server restart", Ipc.Call("LIMIT ON", 2000, name, 3000).Ok); }
        finally { server.Stop(); }
        Check("all second pipe workers exited", server.IsStopped);
        Check("service absent: error returned", Ipc.Call("STATUS", 80, name, 100).Unreachable);

        using (var silent = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var release = new ManualResetEvent(false))
        {
            var thread = new Thread(delegate() { try { silent.WaitForConnection(); release.WaitOne(); } catch (IOException) { } });
            thread.IsBackground = true; thread.Start();
            try
            {
                var clock = Stopwatch.StartNew();
                var response = Ipc.Call("STATUS", 1000, name, 150);
                Check("client connected but server silent: end-to-end timeout", response.Unreachable && clock.ElapsedMilliseconds < 1500);
            }
            finally { release.Set(); silent.Dispose(); Check("simulated server thread exited", thread.Join(2000)); }
        }
        using (var stream = new MemoryStream(new byte[256 * 1024 + 1]))
        {
            bool rejected = false;
            try { PipeIo.ReadResponse(stream, new Deadline(2000)); } catch (IOException) { rejected = true; }
            Check("oversized response rejected", rejected);
        }
    }

    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool CreateHardLink(string link, string existing, IntPtr security);
    static void PresentationTests()
    {
        Action queued = null;
        bool closed = false, applied = false;
        Check("failed BeginInvoke does not escape the worker", !UiDispatch.Post(delegate { throw new InvalidOperationException(); }, delegate { return false; }, delegate { }));
        UiDispatch.Post(delegate(Action work) { queued = work; }, delegate { return closed; }, delegate { applied = true; });
        closed = true; queued();
        Check("queued callback ignored after exit", !applied);
        UiDispatch.Post(delegate(Action work) { queued = work; }, delegate { return false; }, delegate { throw new ArgumentException("simulated data"); });
        queued(); Check("UI callback exception contained", true);
        using (Process process = Process.GetCurrentProcess())
        {
            using (Icon warmup = IconRenderer.Render(16, TrayState.Unknown, 0)) { }
            int before = GetGuiResources(process.Handle, 1);
            for (int i = 0; i < 500; i++)
                using (Icon icon = IconRenderer.Render(16 + (i % 5) * 8, (TrayState)(i % 5), i % 101, i % 2 == 0)) { }
            int after = GetGuiResources(process.Handle, 1);
            Check("500 redraws: no increase in USER icon handles", after <= before + 2);
        }
        Check("Framework runtime handles TaskbarCreated", typeof(System.Windows.Forms.NotifyIcon).GetMethod("WmTaskbarCreated", BindingFlags.Instance | BindingFlags.NonPublic) != null);
    }

    static void LogTests()
    {
        for (int i = 0; i < 1100; i++) Log.Write(new string('\u20AC', 1024));
        Check("log stays within 512 KiB after multiple rotations", new FileInfo(Paths.LogFile).Length <= 512 * 1024);
        Check("IPC log tail within 256 KiB", Encoding.UTF8.GetByteCount(Log.Tail(2000)) < 256 * 1024);
        string before = File.ReadAllText(Paths.LogFile);
        var h = new FakeHardware();
        h.Read = delegate { var a = Status(false, false); a.HealthOn = h.ReadCount % 2 == 0; return a; };
        var e = Make(h, new FakeConfig(), new FakeGuards());
        for (int i = 0; i < 11; i++) e.Enforce("inconsistent");
        string delta = File.ReadAllText(Paths.LogFile).Substring(before.Length);
        Check("inconsistent reads logged on first and tenth event", delta.Split(new string[] { "inconsistent firmware reads" }, StringSplitOptions.None).Length == 3);
    }

    sealed class SlowHardware : IBatteryHardware
    {
        public readonly ManualResetEvent Entered = new ManualResetEvent(false);
        public readonly ManualResetEvent Release = new ManualResetEvent(false);
        public int SensorReads;
        public HealthStatus GetStatus() { return Status(true, false); }
        public int SetMode(byte mask, bool on) { return 0; }
        public Telemetry ReadStandard() { Entered.Set(); Release.WaitOne(5000); return new Telemetry { OnAC = true }; }
        public void ReadFirmwareSensors(Telemetry t) { SensorReads++; }
    }

    // Data folder, impostor pipe, stop during snapshot.
    static void HardeningTests()
    {
        string dir = Path.Combine(Paths.DataDir, "precreated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.ini"), "saved_lid_action=3\n");
        if (Util.IsAdmin())
            Console.WriteLine("SKIP user-created folder: test running as administrator");
        else
            Check("folder pre-created by a user: rejected", DataDirTrust.Problem(dir) != null);
        Check("missing folder: no problem", DataDirTrust.Problem(Path.Combine(dir, "missing")) == null);
        Directory.Delete(dir, true);

        // hardlinks and junctions detected (the owner here is the user: check the specific reason)
        string links = Path.Combine(Paths.DataDir, "link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(links);
        string target = Path.Combine(links, "target.txt");
        File.WriteAllText(target, "x");
        bool hard = CreateHardLink(Path.Combine(links, "log.txt"), target, IntPtr.Zero);
        Check("hardlink created for the test", hard);
        Check("hardlink detected (2 names) without following links", hard && DataDirTrust.Links(Path.Combine(links, "log.txt")) == 2
            && DataDirTrust.Links(Path.Combine(links, "target.txt")) == 2);
        File.Delete(Path.Combine(links, "log.txt"));
        string outside = Path.Combine(Paths.DataDir, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string junction = Path.Combine(links, "junction");
        Process mk = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mk.StandardOutput.ReadToEnd(); mk.WaitForExit(10000);
        bool isJunction = Directory.Exists(junction) && (new DirectoryInfo(junction).Attributes & FileAttributes.ReparsePoint) != 0;
        Check("junction detected as a link", isJunction && DataDirTrust.Problem(junction) != null && DataDirTrust.Problem(junction).StartsWith("link"));
        if (isJunction) Directory.Delete(junction);
        Directory.Delete(outside, true);
        Directory.Delete(links, true);

        // a missing, corrupt or incomplete journal does not mean "no recovery needed"
        string probl;
        if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
        Check("missing journal rejected", Config.LoadStrict(out probl) == null);
        File.WriteAllText(Paths.ConfigFile, "calibrating=?\nsaved_lid_action=-1\n", Encoding.UTF8);
        Check("corrupt journal rejected", Config.LoadStrict(out probl) == null);
        File.WriteAllText(Paths.ConfigFile, "limit=on\n", Encoding.UTF8);
        Check("incomplete journal rejected", Config.LoadStrict(out probl) == null);
        File.WriteAllText(Paths.ConfigFile, "calibrating=0\nsaved_lid_action=2\nsaved_lid_scheme=\n", Encoding.UTF8);
        Check("journal with invalid scheme rejected", Config.LoadStrict(out probl) == null);
        new Config().Save();
        Config strict = Config.LoadStrict(out probl);
        Check("journal written by the service accepted", strict != null && !strict.Calibrating && strict.SavedLidAction == -1);

        // service startup with a lost journal does not turn it into "no recovery needed"
        File.Delete(Paths.ConfigFile);
        var lost = Config.Load();
        new Engine(lost, new FakeHardware { Current = Status(true, true) }, new FakeGuards(), delegate { }).Startup();
        Check("startup with lost journal: state remains unknown", Config.LoadStrict(out probl) == null && Config.Load().RecoveryUnknown);
        lost.LimitDesired = false; lost.Save();
        Check("unknown state survives subsequent saves", Config.LoadStrict(out probl) == null);
        new Config().Save();

        string name = "acerbat-test-squat-" + Guid.NewGuid().ToString("N");
        var hw = new FakeHardware { Current = Status(false, false) };
        var security = new PipeSecurity();
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        var impostor = new PipeServer(Make(hw, new FakeConfig(), new FakeGuards()), name, 3000, security);
        impostor.Start();
        try
        {
            var r = Ipc.Call("LIMIT ON", 5000, name, 10000, delegate { return (uint?)0; });
            Check("server differs from service: command not sent", !r.Ok && r.Unreachable && hw.Writes.Count == 0);
            r = Ipc.Call("LIMIT ON", 5000, name, 10000, delegate { return (uint?)null; });
            Check("service not running: command not sent", !r.Ok && hw.Writes.Count == 0);
            r = Ipc.Call("LIMIT ON", 5000, name, 10000);
            if (!r.Ok) Console.WriteLine("diagnostics: " + r.Message);
            Check("expected server: command sent", r.Ok && hw.Writes.Count == 1);
        }
        finally { impostor.Stop(); }

        var slow = new SlowHardware();
        var engine = new Engine(new FakeConfig(), slow, new FakeGuards(), delegate { });
        var snap = new Thread(delegate() { try { engine.Snapshot(); } catch (AcerException) { } });
        snap.IsBackground = true; snap.Start();
        bool entered = slow.Entered.WaitOne(2000);
        engine.BeginStop(); slow.Release.Set();
        Check("snapshot started before stopping: no subsequent sensor read", entered && snap.Join(3000) && slow.SensorReads == 0);
    }

    public static int Run()
    {
        HardeningTests();
        FirmwareTests();
        RecoveryTests();
        fails += CalibrationTests.Run();
        TimeoutTests();
        try { PipeTests(); }
        catch (Exception ex) { Check("pipe infrastructure: " + ex.Message, false); }
        PresentationTests();
        LogTests();
        File.WriteAllText(Paths.ConfigFile, "limit=corrupted-value\ncalibrating=?\n", Encoding.UTF8);
        var fallback = Config.Load();
        Check("unreadable values: default limit on, calibration not started", fallback.LimitDesired && !fallback.Calibrating);
        var saved = new Config { LimitDesired = false }; saved.Save(); saved.LimitDesired = true; saved.Save();
        Check("complete config replacement without leftover tmp files", Config.Load().LimitDesired && !File.Exists(Paths.ConfigFile + ".tmp"));

        return fails;
    }
}
