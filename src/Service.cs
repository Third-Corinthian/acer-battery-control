using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace AcerBatteryControl
{
    // Windows service (LocalSystem). Check schedule:
    //  startup: after 20 s, 2 min, 5 min, 10 min (catch limit resets after sign-in)
    //  resume from sleep/hibernation: after 20 s
    //  power source change: after 5 s
    //  calibration: 60 s, 15 s at low/unknown charge; firmware not ready: 60 s
    //  steady state: every 30 min
    class BatteryService : ServiceBase
    {
        static readonly int[] BootOffsets = { 20, 100, 180, 300 };
        static readonly string[] BootLabels = { "startup", "startup+2m", "startup+5m", "startup+10m" };

        Engine engine;
        PipeServer pipe;
        Timer timer;
        readonly object schedLock = new object();
        DateTime plannedAt = DateTime.MaxValue;
        string plannedOrigin = "startup";
        int bootStep;
        volatile bool stopping;
        int timerBusy, powerBusy;

        public BatteryService()
        {
            ServiceName = Paths.ServiceName;
            CanHandlePowerEvent = true;
            CanShutdown = true;
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            stopping = false;
            bootStep = 0;
            plannedAt = DateTime.MaxValue;
            // the data folder must be trusted before reading config or writing logs.
            // Create it securely if missing; if a user controls it, the service neither starts nor writes to it.
            string dataNote = null;
            if (!DryRun.Active)
            {
                string problem = null;
                try { dataNote = DataDirTrust.Secure(Paths.DataDir); }
                catch (Exception ex) { problem = ex.Message; }
                if (problem != null)
                {
                    Log.Disable();
                    ExitCode = 1;
                    throw new InvalidOperationException("Untrusted data folder: " + problem);
                }
            }
            if (dataNote != null) Log.Write("[service] WARNING: " + dataNote);
            engine = new Engine();
            try
            {
                engine.Startup();
                timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                engine.WorkRequested += delegate { ScheduleIfEarlier(engine.CalibrationPollSeconds, "command"); };
                pipe = new PipeServer(engine);
                pipe.Start();
                ScheduleIfEarlier(BootOffsets[0], BootLabels[0]);
            }
            catch (Exception ex)
            {
                Log.Write("[service] startup failed: " + ex.Message);
                StopAll("startup failed");
                throw; // SCM must know that startup failed.
            }
        }

        protected override void OnStop()
        {
            StopAll("service stop");
        }

        protected override void OnShutdown()
        {
            StopAll("Windows shutdown");
        }

        void StopAll(string why)
        {
            lock (schedLock)
            {
                stopping = true;
                AcerWmi.BeginStop();   // power setting restores (powrprof) remain allowed
                if (engine != null) engine.BeginStop();
                plannedAt = DateTime.MaxValue;
                try { if (timer != null) timer.Dispose(); } catch { }
            }
            try { if (pipe != null) pipe.Stop(); } catch { }
            try { if (engine != null) engine.Shutdown(why); } catch { }
        }

        protected override bool OnPowerEvent(PowerBroadcastStatus status)
        {
            try
            {
            if (stopping) return true;
            if (status == PowerBroadcastStatus.ResumeAutomatic || status == PowerBroadcastStatus.ResumeSuspend)
            {
                ScheduleIfEarlier(20, "resume");
            }
            else if (status == PowerBroadcastStatus.PowerStatusChange)
            {
                if (Interlocked.CompareExchange(ref powerBusy, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { if (!stopping) engine.PowerChanged(); }
                        catch (Exception ex) { Log.Write("[power] " + ex.Message); }
                        finally { Interlocked.Exchange(ref powerBusy, 0); }
                    });
                ScheduleIfEarlier(5, "power");
            }
            }
            catch (Exception ex) { Log.Write("[power] " + ex.Message); }
            return true;
        }

        void ScheduleIfEarlier(int seconds, string origin)
        {
            lock (schedLock)
            {
                if (stopping || timer == null) return;
                DateTime due = DateTime.UtcNow.AddSeconds(seconds);
                if (due >= plannedAt) return;
                plannedAt = due;
                plannedOrigin = origin;
                try { timer.Change(seconds * 1000, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        void OnTimer(object state)
        {
            if (stopping) return;
            if (Interlocked.CompareExchange(ref timerBusy, 1, 0) != 0)
            {
                lock (schedLock) plannedAt = DateTime.MaxValue;
                ScheduleIfEarlier(60, "pending check");
                return;
            }
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Write("[timer] " + ex.Message);
                ScheduleIfEarlier(60, "timer error");
            }
            finally { Interlocked.Exchange(ref timerBusy, 0); }
        }

        void Tick()
        {
            string origin;
            lock (schedLock)
            {
                origin = plannedOrigin;
                plannedAt = DateTime.MaxValue;
            }

            bool retry = engine.Enforce(origin);

            int next;
            string label;
            if (retry || engine.IsCalibrating)
            {
                next = engine.IsCalibrating ? engine.CalibrationPollSeconds : CalibrationSafety.PollSeconds;
                label = retry ? "retry" : "calibration";
            }
            else if (bootStep < BootOffsets.Length - 1)
            {
                bootStep++;
                next = BootOffsets[bootStep];
                label = BootLabels[bootStep];
            }
            else
            {
                next = 1800;
                label = "periodic";
            }
            ScheduleIfEarlier(next, label);
        }
    }

    // Local named pipe: access for SYSTEM, Administrators and interactive users; denied from the network.
    class PipeServer
    {
        readonly Engine engine;
        readonly string name;
        readonly int requestTimeout;
        readonly PipeSecurity permissions;
        readonly object clientsLock = new object();
        readonly System.Collections.Generic.List<NamedPipeServerStream> clients = new System.Collections.Generic.List<NamedPipeServerStream>();
        readonly Thread[] workers = new Thread[4];
        volatile bool running;
        int errors;

        public PipeServer(Engine engine) : this(engine, Paths.PipeName, 2000, null) { }
        internal PipeServer(Engine engine, string pipeName, int timeout, PipeSecurity testSecurity)
        {
            if (testSecurity != null && (!DryRun.Active || !pipeName.StartsWith("acerbat-test-")))
                throw new AcerException("Test ACL allowed only in dry-run on isolated pipes.");
            this.engine = engine; name = pipeName; requestTimeout = timeout;
            permissions = testSecurity ?? Security();
        }

        public void Start()
        {
            running = true;
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(Loop);
                workers[i].IsBackground = true;
                workers[i].Name = "pipe " + i;
                workers[i].Start();
            }
        }

        public void Stop()
        {
            lock (clientsLock)
            {
                running = false;
                foreach (NamedPipeServerStream client in clients)
                    try { client.Dispose(); } catch { }
            }
            var deadline = new Deadline(2000);
            foreach (Thread worker in workers)
                if (worker != null && worker != Thread.CurrentThread) worker.Join(deadline.Remaining);
        }

        internal bool IsStopped
        {
            get
            {
                foreach (Thread worker in workers) if (worker != null && worker.IsAlive) return false;
                return true;
            }
        }

        static PipeSecurity Security()
        {
            PipeSecurity ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return ps;
        }

        void Loop()
        {
            while (running)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    lock (clientsLock)
                    {
                        if (!running) return;
                        stream = new NamedPipeServerStream(name, PipeDirection.InOut, 4,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, permissions);
                        clients.Add(stream);
                    }
                    // Closing in Stop cancels the overlapped wait; End is always consumed.
                    var waiting = new object();
                    bool connected = false;
                    Exception connectionError = null;
                    NamedPipeServerStream client = stream;
                    stream.BeginWaitForConnection(delegate(IAsyncResult ar)
                    {
                        try { client.EndWaitForConnection(ar); }
                        catch (Exception ex) { connectionError = ex; }
                        finally { lock (waiting) { connected = true; Monitor.PulseAll(waiting); } }
                    }, null);
                    lock (waiting)
                        while (!connected && running) Monitor.Wait(waiting, 200);
                    if (!running) return;
                    if (connectionError != null) throw connectionError;
                    string req = Ipc.ReadLine(stream, 256, new Deadline(requestTimeout));
                    if (!running) return;
                    byte[] response = Encoding.UTF8.GetBytes(Handle(req));
                    if (!running) return;
                    PipeIo.Transfer(stream, response, response.Length, true, new Deadline(5000));
                }
                catch (Exception ex)
                {
                    if (running)
                    {
                        int n = Interlocked.Increment(ref errors);
                        if (n == 1 || n % 10 == 0) Log.Write("[pipe] error (" + n + "): " + ex.Message);
                        Thread.Sleep(100);
                    }
                }
                finally
                {
                    if (stream != null)
                    {
                        lock (clientsLock) clients.Remove(stream);
                        try { stream.Dispose(); } catch { }
                    }
                }
            }
        }

        string Handle(string req)
        {
            try
            {
                string r = (req ?? "").Trim().ToUpperInvariant();
                if (r == "STATUS") return "OK\n" + engine.Snapshot();
                if (r == "LIMIT ON") return "OK\n" + engine.SetLimit(true);
                if (r == "LIMIT OFF") return "OK\n" + engine.SetLimit(false);
                if (r == "CALIBRATE START") return "OK\n" + engine.StartCalibration();
                if (r == "CALIBRATE STOP") return "OK\n" + engine.StopCalibration();
                if (r == "ENFORCE")
                {
                    return "OK\n" + engine.EnforceCommand();
                }
                if (r == "LOG" || r.StartsWith("LOG "))
                {
                    int n = 100;
                    if (r.Length > 4) int.TryParse(r.Substring(4), out n);
                    return "OK\n" + Log.Tail(Math.Max(1, Math.Min(n, 2000)));
                }
                return "ERR Unknown command: " + r;
            }
            catch (Exception ex)
            {
                return "ERR " + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
        }
    }
}
