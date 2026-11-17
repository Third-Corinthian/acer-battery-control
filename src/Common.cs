using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AcerBatteryControl
{
    static class Paths
    {
        public const string AppName = "Acer Battery Control";
        public const string ServiceName = "AcerBatteryControl";
        public const string PipeName = "AcerBatteryControl";
        public const string GuiExeName = "AcerBatteryControl.exe";
        public const string CliExeName = "acerbat.exe";
        public const string Version = "2.1.0";

        // ACERBAT_DATADIR redirects config and log (tests only: the service does not receive it from the user).
        public static readonly string DataDir = DataDirFromEnv();
        public static readonly string ConfigFile = Path.Combine(DataDir, "config.ini");
        public static readonly string LogFile = Path.Combine(DataDir, "log.txt");
        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AcerBatteryControl");

        static string DataDirFromEnv()
        {
            string d = Environment.GetEnvironmentVariable("ACERBAT_DATADIR");
            if (!string.IsNullOrEmpty(d)) return Path.GetFullPath(d);
            if (DryRun.Active) return Path.Combine(Path.GetTempPath(), "AcerBatteryControl-dryrun");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AcerBatteryControl");
        }
    }

    // ACERBAT_DRYRUN=1: no writes to firmware, power plan or system. Read on every call.
    static class DryRun
    {
        public static bool Active
        {
            get { return Environment.GetEnvironmentVariable("ACERBAT_DRYRUN") == "1"; }
        }

        public static void Guard(string what)
        {
            if (!Active) return;
            string msg = "[dry-run] write blocked: " + what;
            try { Console.Error.WriteLine(msg); } catch { }
            throw new AcerException(msg);
        }
    }

    class AcerException : Exception
    {
        public AcerException(string message) : base(message) { }
    }

    static class Util
    {
        public static readonly CultureInfo It = CultureInfo.CurrentCulture;
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        public static bool IsAdmin()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // Relaunch the specified executable with UAC. false if the user cancels.
        public static bool RunElevated(string file, string args, bool wait)
        {
            DryRun.Guard("elevated launch: " + args);
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            try
            {
                using (Process p = Process.Start(psi))
                    if (wait && p != null && !p.WaitForExit(10000))
                        throw new AcerException("Service still starting: check its status in the panel.");
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        // like RunElevated, but waits and returns the actual exit code; null if the user cancels UAC.
        public static int? RunElevatedExitCode(string file, string args, int timeoutMs)
        {
            DryRun.Guard("elevated launch: " + args);
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    if (!p.WaitForExit(timeoutMs)) throw new AcerException("Command still running: check its status in the panel.");
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public static int RunHidden(string file, string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                output = o.Trim();
                return p.ExitCode;
            }
        }

        public static string OnOff(bool v)
        {
            return v ? "on" : "off";
        }
    }

    // Shared log: written only by the service (SYSTEM) and administrative tools.
    static class Log
    {
        static readonly object gate = new object();
        const long MaxBytes = 512 * 1024;
        static volatile bool disabled;

        // the service writes nothing in an untrusted data folder, not even the log
        // (a planted link would turn SYSTEM's append into an arbitrary write).
        public static void Disable() { disabled = true; }

        public static void Write(string message)
        {
            if (disabled) return;
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    string text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
                    if (text.Length > 4096) text = text.Substring(0, 4096) + "…";
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Util.Inv) + "  " + text + Environment.NewLine;
                    if (File.Exists(Paths.LogFile) && new FileInfo(Paths.LogFile).Length + Encoding.UTF8.GetByteCount(line) > MaxBytes)
                    {
                        string tail = Tail(1000);
                        File.WriteAllText(Paths.LogFile, tail + Environment.NewLine, Encoding.UTF8);
                    }
                    File.AppendAllText(Paths.LogFile, line, Encoding.UTF8);
                }
                catch
                {
                    // Logging must never block the service.
                }
            }
        }

        public static string Tail(int lines)
        {
            try
            {
                List<string> all = new List<string>();
                using (FileStream fs = new FileStream(Paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bool partial = fs.Length > 128 * 1024;
                    if (partial) fs.Seek(-128 * 1024, SeekOrigin.End);
                    using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                    {
                    if (partial) r.ReadLine();
                    string l;
                    while ((l = r.ReadLine()) != null) all.Add(l);
                    }
                }
                int start = Math.Max(0, all.Count - lines);
                return string.Join("\n", all.GetRange(start, all.Count - start).ToArray());
            }
            catch (Exception ex)
            {
                return "(cannot read log: " + ex.Message + ")";
            }
        }
    }

    // Desired and persistent service state. Written atomically.
    class Config
    {
        public bool LimitDesired = true;
        public bool Calibrating = false;
        public DateTime CalibStartedUtc = DateTime.MinValue;
        public int SavedLidAction = -1;     // -1 = no saved value
        public string SavedLidScheme = "";
        // the journal was lost or unreadable. Persists until an explicit uninstall
        // (--force) accepts it; no service startup can turn it into "no recovery needed".
        public bool RecoveryUnknown = false;

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(Paths.ConfigFile)) return c;
                foreach (string raw in File.ReadAllLines(Paths.ConfigFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "limit": c.LimitDesired = v.ToLowerInvariant() != "off"; break;
                        case "calibrating": c.Calibrating = v == "1"; break;
                        case "calib_started_utc":
                            DateTime d;
                            if (DateTime.TryParse(v, Util.Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                                c.CalibStartedUtc = d;
                            break;
                        case "saved_lid_action":
                            int n;
                            if (int.TryParse(v, NumberStyles.Integer, Util.Inv, out n)) c.SavedLidAction = n;
                            break;
                        case "saved_lid_scheme": c.SavedLidScheme = v; break;
                        case "recovery_unknown": c.RecoveryUnknown = v != "0"; break;
                    }
                }
            }
            catch (Exception ex)
            {
                c.RecoveryUnknown = true;
                Log.Write("[config] read failed, using defaults: " + ex.Message);
            }
            return c;
        }

        // read for irreversible decisions (uninstall). Return null, with the reason, if the file
        // is missing, unreadable, or a recovery field (calibrating, saved_lid_*) is invalid.
        public static Config LoadStrict(out string problem)
        {
            problem = null;
            Config c = new Config();
            bool hasCal = false, hasLid = false;
            try
            {
                if (!File.Exists(Paths.ConfigFile)) { problem = "config.ini missing"; return null; }
                foreach (string raw in File.ReadAllLines(Paths.ConfigFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) { problem = "invalid line: " + line; return null; }
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "calibrating")
                    {
                        if (v != "0" && v != "1") { problem = "invalid calibrating value"; return null; }
                        c.Calibrating = v == "1"; hasCal = true;
                    }
                    else if (k == "saved_lid_action")
                    {
                        int n;
                        if (!int.TryParse(v, NumberStyles.Integer, Util.Inv, out n) || n < -1) { problem = "invalid saved_lid_action value"; return null; }
                        c.SavedLidAction = n; hasLid = true;
                    }
                    else if (k == "saved_lid_scheme") c.SavedLidScheme = v;
                    else if (k == "recovery_unknown" && v != "0") { problem = "journal previously lost: recovery state unknown"; return null; }
                }
            }
            catch (Exception ex) { problem = "cannot read config.ini: " + ex.Message; return null; }
            if (!hasCal || !hasLid) { problem = "config.ini incomplete"; return null; }
            Guid g;
            if (c.SavedLidAction >= 0 && !Guid.TryParse(c.SavedLidScheme, out g)) { problem = "invalid saved_lid_scheme value"; return null; }
            return c;
        }

        public virtual void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Acer Battery Control - managed by the service, edit with the app or acerbat.exe");
            sb.AppendLine("limit=" + Util.OnOff(LimitDesired));
            sb.AppendLine("calibrating=" + (Calibrating ? "1" : "0"));
            sb.AppendLine("calib_started_utc=" + (CalibStartedUtc == DateTime.MinValue ? "" : CalibStartedUtc.ToString("o", Util.Inv)));
            sb.AppendLine("saved_lid_action=" + SavedLidAction.ToString(Util.Inv));
            sb.AppendLine("saved_lid_scheme=" + SavedLidScheme);
            if (RecoveryUnknown) sb.AppendLine("recovery_unknown=1");
            Directory.CreateDirectory(Paths.DataDir);
            string tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(Paths.ConfigFile)) File.Replace(tmp, Paths.ConfigFile, null);
            else File.Move(tmp, Paths.ConfigFile);
        }
    }

    class IpcResult
    {
        public bool Ok;
        public bool Unreachable;
        public string Message = "";
        public string Body = "";
        public Dictionary<string, string> Data = new Dictionary<string, string>();

        public string Get(string key)
        {
            string v;
            return Data.TryGetValue(key, out v) ? v : "";
        }
    }

    // Protocol: one command line from the client, response "OK"/"ERR <msg>" + body, then close.
    static class Ipc
    {
        public static string ReadLine(Stream s, int max)
        {
            return ReadLine(delegate { return s.ReadByte(); }, max);
        }

        public static string ReadLine(Stream s, int max, Deadline deadline)
        {
            byte[] one = new byte[1];
            return ReadLine(delegate { return PipeIo.Transfer(s, one, 1, false, deadline) == 0 ? -1 : one[0]; }, max);
        }

        static string ReadLine(Func<int> read, int max)
        {
            List<byte> buf = new List<byte>();
            for (int count = 0; count < max; count++)
            {
                int b = read();
                if (b < 0) throw new IOException("Incomplete IPC request.");
                if (b == '\n') return new UTF8Encoding(false, true).GetString(buf.ToArray()).TrimEnd('\r');
                if (b < 32 && b != '\r') throw new IOException("Invalid IPC character.");
                buf.Add((byte)b);
            }
            throw new IOException("IPC request too long.");
        }

        public static IpcResult Call(string command, int connectTimeoutMs)
        {
            return Call(command, connectTimeoutMs, Paths.PipeName, 30000, ServiceProcessId);
        }

        // Tests only: the expected server is this process itself.
        internal static IpcResult Call(string command, int connectTimeoutMs, string pipeName, int operationTimeoutMs)
        {
            return Call(command, connectTimeoutMs, pipeName, operationTimeoutMs,
                delegate { return (uint?)(uint)Process.GetCurrentProcess().Id; });
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatusEx(IntPtr service, int level, byte[] buffer, int size, out int needed);
        [DllImport("advapi32.dll")]
        static extern bool CloseServiceHandle(IntPtr handle);

        // Service PID according to SCM, null if not running. Readable by a standard user.
        internal static uint? ServiceProcessId()
        {
            IntPtr scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
            if (scm == IntPtr.Zero) return null;
            try
            {
                IntPtr svc = OpenService(scm, Paths.ServiceName, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc == IntPtr.Zero) return null;
                try
                {
                    byte[] buf = new byte[36]; // SERVICE_STATUS_PROCESS
                    int needed;
                    if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, buf.Length, out needed)) return null;
                    uint state = BitConverter.ToUInt32(buf, 4);
                    uint pid = BitConverter.ToUInt32(buf, 28);
                    if (state != 4 /* SERVICE_RUNNING */ || pid == 0) return null;
                    return pid;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        // send the command only if the process that opened the pipe is the service.
        internal static IpcResult Call(string command, int connectTimeoutMs, string pipeName, int operationTimeoutMs, Func<uint?> expectedServer)
        {
            IpcResult res = new IpcResult();
            string raw;
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    c.Connect(connectTimeoutMs);
                    uint server;
                    if (!GetNamedPipeServerProcessId(c.SafePipeHandle, out server))
                        throw new IOException("cannot identify the pipe's process");
                    uint? expected = expectedServer();
                    if (!expected.HasValue || expected.Value != server)
                        throw new IOException("the pipe is open by a process other than the service (PID " + server + "); command not sent");
                    byte[] req = Encoding.UTF8.GetBytes(command + "\n");
                    var deadline = new Deadline(operationTimeoutMs);
                    PipeIo.Transfer(c, req, req.Length, true, deadline);
                    raw = PipeIo.ReadResponse(c, deadline);
                }
            }
            catch (Exception ex)
            {
                res.Unreachable = true;
                res.Message = "Service unreachable (" + ex.Message + ")";
                return res;
            }

            raw = raw.Replace("\r", "");
            int nl = raw.IndexOf('\n');
            if (nl < 0)
            {
                res.Message = "Incomplete IPC response: check the status before repeating the command.";
                return res;
            }
            string head = nl < 0 ? raw : raw.Substring(0, nl);
            res.Body = nl < 0 ? "" : raw.Substring(nl + 1);
            if (head == "OK")
            {
                res.Ok = true;
            }
            else
            {
                res.Message = head.StartsWith("ERR ") ? head.Substring(4) : head;
                return res;
            }
            foreach (string line in res.Body.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) res.Data[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            res.Message = res.Body.Trim();
            return res;
        }
    }
}
