using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;

namespace AcerBatteryControl
{
    class HealthStatus
    {
        public bool Valid;              // false = the firmware reports no mode (transient state)
        public bool HealthSupported;
        public bool CalibSupported;
        public bool HealthOn;
        public bool CalibOn;
        public string Raw = "";
    }

    class Telemetry
    {
        public int? Percent;
        public bool? OnAC;
        public bool? Charging;
        public bool? Discharging;
        public int? RateMw;             // >0 charging, <0 discharging
        public int? VoltageMv;
        public int? CurrentMa;          // >0 charging, <0 discharging
        public double? TempC;
        public int? RemainingMwh;
        public int? FullMwh;
        public int? DesignMwh;
        public int? Cycles;
        public string Model = "";
    }

    // Acer firmware interface: root\WMI BatteryControl {79772EC5-04B1-4BFD-843C-61E7F77B6CC9}
    // 19 GetBattInfoInterface, 20 GetBatteryHealthControlStatus, 21 SetBatteryHealthControl.
    // Concurrent firmware calls can return corrupted data: everything goes through Gate.
    static class AcerWmi
    {
        public const byte ModeHealth = 0x01;
        public const byte ModeCalibration = 0x02;
        const string Ns = @"root\wmi";
        public static readonly object Gate = new object();
        // Separate channels: slow Windows telemetry does not block the firmware, and vice versa.
        // 8 s cover the first connection to root\wmi after Windows starts; 12 s = 6 queries of 2 s.
        static readonly BoundedCall calls = new BoundedCall(8000);
        static readonly BoundedCall telemetryCalls = new BoundedCall(12000);

        // set when the service stops; no new WMI calls start after that.
        static volatile bool stopRequested;
        public static void BeginStop() { stopRequested = true; }
        static void CheckNotStopping()
        {
            if (stopRequested) throw new AcerException("Service stopping: no new WMI calls.");
        }

        static ManagementObject Instance(CallPermit permit)
        {
            permit.Check();
            CheckNotStopping(); // every query, not just method entry
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(Ns, "SELECT * FROM BatteryControl"))
            {
                s.Options.Timeout = TimeSpan.FromSeconds(3);
                using (ManagementObjectCollection rows = s.Get())
                    foreach (ManagementObject o in rows) return o;
            }
            throw new AcerException("WMI class BatteryControl not found: this notebook's firmware does not expose Acer battery control.");
        }

        static T Invoke<T>(string method, Dictionary<string, object> args, Func<ManagementBaseObject, T> read)
        {
            // Reads are also excluded from tests: no WMI methods on the real host.
            DryRun.Guard("WMI BatteryControl." + method);
            CheckNotStopping();
            return calls.Run<T>(delegate(CallPermit permit)
            {
                lock (Gate)
                {
                    using (ManagementObject o = Instance(permit))
                    {
                        permit.Check();
                        using (ManagementBaseObject inp = o.GetMethodParameters(method))
                        {
                            foreach (KeyValuePair<string, object> kv in args) inp[kv.Key] = kv.Value;
                            permit.Check(); // Do not write if the instance lookup exceeded the deadline.
                            CheckNotStopping();
                            using (ManagementBaseObject result = o.InvokeMethod(method, inp, null))
                                return read(result);
                        }
                    }
                }
            });
        }

        public static void EnsurePresent()
        {
            DryRun.Guard("WMI BatteryControl lookup");
            calls.Run<bool>(delegate(CallPermit permit)
            {
                lock (Gate) { using (ManagementObject o = Instance(permit)) return true; }
            });
        }

        public static HealthStatus GetStatus()
        {
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["uBatteryNo"] = (byte)1;
            a["uFunctionQuery"] = (byte)1;
            a["uReserved"] = new byte[] { 0, 0 };
            return Invoke<HealthStatus>("GetBatteryHealthControlStatus", a, delegate(ManagementBaseObject r)
            {
                return ParseStatus(Convert.ToByte(r["uFunctionList"]), r["uFunctionStatus"] as byte[], r["uReturn"] as byte[]);
            });
        }

        internal static HealthStatus ParseStatus(byte list, byte[] st, byte[] ret)
        {
            HealthStatus h = new HealthStatus();
            h.Raw = string.Format("list=0x{0:X2} status={1} ret={2}", list,
                st == null ? "-" : BitConverter.ToString(st), ret == null ? "-" : BitConverter.ToString(ret));
            if (st == null || st.Length < 2 || list == 0) return h;   // Valid=false
            h.Valid = true;
            h.HealthSupported = (list & ModeHealth) != 0;
            h.CalibSupported = (list & ModeCalibration) != 0;
            h.HealthOn = h.HealthSupported && st[0] != 0;
            h.CalibOn = h.CalibSupported && st[1] != 0;
            return h;
        }

        public static int SetMode(byte mask, bool on)
        {
            if (mask != ModeHealth && mask != ModeCalibration) throw new AcerException("Firmware mode not allowed.");
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["uBatteryNo"] = (byte)1;
            a["uFunctionMask"] = mask;
            a["uFunctionStatus"] = (byte)(on ? 1 : 0);
            a["uReservedIn"] = new byte[5];
            return Invoke<int>("SetBatteryHealthControl", a, delegate(ManagementBaseObject r) { return Convert.ToInt32(r["uReturn"]); });
        }

        // Smart Battery indices: 0x08 temperature (0.1 K), 0x09 voltage (mV), 0x0A current (mA, signed).
        // Unsupported indices return 1.
        static uint? BattInfo(uint index)
        {
            try
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["uBatteryInfoIndex"] = index;
                a["uBatteryNo"] = (uint)1;
                return Invoke<uint>("GetBattInfoInterface", a, delegate(ManagementBaseObject r) { return Convert.ToUInt32(r["uReturn"]); });
            }
            catch
            {
                return null;
            }
        }

        public static void ReadFirmwareSensors(Telemetry t)
        {
            uint? temp = BattInfo(0x08);
            if (temp.HasValue && temp.Value > 1 && temp.Value <= 0xFFFF)
            {
                double c = (temp.Value - 2731) / 10.0;
                if (c > -20 && c < 100) t.TempC = c;
            }
            uint? mv = BattInfo(0x09);
            if (mv.HasValue && mv.Value > 1000 && mv.Value < 30000) t.VoltageMv = (int)mv.Value;
            uint? ma = BattInfo(0x0A);
            if (ma.HasValue && ma.Value <= 0xFFFF) t.CurrentMa = (short)(ushort)ma.Value;
        }

        // ---------------------------------------------------------- standard Windows telemetry

        static ManagementBaseObject First(string ns, string cls, CallPermit permit)
        {
            try
            {
                permit.Check();
                if (stopRequested) return null; // no new queries after stopping
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(ns, "SELECT * FROM " + cls))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(2);
                    using (ManagementObjectCollection rows = s.Get())
                        foreach (ManagementBaseObject o in rows) return o;
                }
            }
            catch { }
            return null;
        }

        static int? I(ManagementBaseObject o, string p)
        {
            if (o == null) return null;
            try
            {
                object v = o[p];
                if (v == null) return null;
                return Convert.ToInt32(v);
            }
            catch { return null; }
        }

        static bool? B(ManagementBaseObject o, string p)
        {
            if (o == null) return null;
            try
            {
                object v = o[p];
                if (v == null) return null;
                return Convert.ToBoolean(v);
            }
            catch { return null; }
        }

        static string S(ManagementBaseObject o, string p)
        {
            if (o == null) return "";
            try
            {
                object v = o[p];
                return v == null ? "" : v.ToString().Trim();
            }
            catch { return ""; }
        }

        public static Telemetry ReadStandard()
        {
            DryRun.Guard("WMI telemetry");
            CheckNotStopping();
            return telemetryCalls.Run<Telemetry>(ReadStandardCore);
        }

        static Telemetry ReadStandardCore(CallPermit permit)
        {
            Telemetry t = new Telemetry();
            using (ManagementBaseObject st = First(Ns, "BatteryStatus", permit))
            using (ManagementBaseObject full = First(Ns, "BatteryFullChargedCapacity", permit))
            using (ManagementBaseObject stat = First(Ns, "BatteryStaticData", permit))
            using (ManagementBaseObject cyc = First(Ns, "BatteryCycleCount", permit))
            {

            t.OnAC = B(st, "PowerOnline");
            t.Charging = B(st, "Charging");
            t.Discharging = B(st, "Discharging");
            t.RemainingMwh = I(st, "RemainingCapacity");
            t.VoltageMv = I(st, "Voltage");
            int? cr = I(st, "ChargeRate");
            int? dr = I(st, "DischargeRate");
            if (t.Charging == true && cr.HasValue) t.RateMw = Math.Abs(cr.Value);
            else if (t.Discharging == true && dr.HasValue) t.RateMw = -Math.Abs(dr.Value);
            else if (st != null) t.RateMw = 0;

            t.FullMwh = I(full, "FullChargedCapacity");
            t.DesignMwh = I(stat, "DesignedCapacity");
            t.Cycles = I(cyc, "CycleCount");
            if (t.Cycles.HasValue && t.Cycles.Value <= 0) t.Cycles = null;
            string model = (S(stat, "ManufactureName") + " " + S(stat, "DeviceName")).Trim();
            t.Model = model;

            if (t.RemainingMwh.HasValue && t.FullMwh.HasValue && t.FullMwh.Value > 0
                && t.RemainingMwh.Value >= 0 && t.RemainingMwh.Value <= t.FullMwh.Value)
                t.Percent = (int)Math.Round(t.RemainingMwh.Value * 100.0 / t.FullMwh.Value);
            if (!t.Percent.HasValue)
                using (ManagementBaseObject fallback = First(@"root\cimv2", "Win32_Battery", permit))
                    t.Percent = I(fallback, "EstimatedChargeRemaining");
            if (!t.OnAC.HasValue)
            {
                using (ManagementBaseObject fallback = First(@"root\cimv2", "Win32_Battery", permit))
                {
                    int? bs = I(fallback, "BatteryStatus");
                    if (bs.HasValue) t.OnAC = bs.Value == 2 || (bs.Value >= 6 && bs.Value <= 9);
                }
            }
            return t;
            }
        }
    }

    // Power plan: AC-only calibration guards and system wake request. Never write DC settings.
    static class Power
    {
        static Guid SubButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        static Guid LidAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
        static Guid SubBattery = new Guid("e73a048d-bf27-4f12-9731-8b2076e8891f");
        static Guid CriticalAction = new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546");
        static Guid CriticalLevel = new Guid("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469");

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")]
        static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint acValueIndex);
        [DllImport("powrprof.dll")]
        static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint acValueIndex);
        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct REASON_CONTEXT
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PowerSetRequest(IntPtr h, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool PowerClearRequest(IntPtr h, int requestType);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        const int PowerRequestSystemRequired = 1;

        static void Check(uint rc, string what)
        {
            if (rc != 0) throw new Win32Exception((int)rc, what);
        }

        public static Guid ActiveScheme()
        {
            IntPtr p;
            Check(PowerGetActiveScheme(IntPtr.Zero, out p), "PowerGetActiveScheme");
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
        }

        public static uint ReadAcLid(Guid scheme)
        {
            uint v;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, out v), "PowerReadACValueIndex");
            return v;
        }

        public static void WriteAcLid(Guid scheme, uint value)
        {
            DryRun.Guard("lid action (AC) = " + value);
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, value), "PowerWriteACValueIndex");
            Guid active = ActiveScheme();
            if (active == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref active), "PowerSetActiveScheme");
        }

        public static IntPtr KeepAwake(string reason)
        {
            DryRun.Guard("system wake request");
            REASON_CONTEXT ctx = new REASON_CONTEXT();
            ctx.Version = 0;
            ctx.Flags = 1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
            ctx.SimpleReasonString = reason;
            IntPtr h = PowerCreateRequest(ref ctx);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerCreateRequest");
            if (!PowerSetRequest(h, PowerRequestSystemRequired))
            {
                int err = Marshal.GetLastWin32Error();
                CloseHandle(h);
                throw new Win32Exception(err, "PowerSetRequest");
            }
            return h;
        }

        public static uint ReadAcCriticalAction(Guid scheme)
        {
            uint v;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubBattery, ref CriticalAction, out v), "PowerReadACValueIndex");
            if (v > 3) throw new AcerException("Invalid critical battery action.");
            return v;
        }

        public static uint ReadAcCriticalLevel(Guid scheme)
        {
            uint v;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubBattery, ref CriticalLevel, out v), "PowerReadACValueIndex");
            if (v > CalibrationSafety.MaximumPercent) throw new AcerException("Invalid critical battery level.");
            return v;
        }

        public static void WriteAcCriticalAction(Guid scheme, uint value)
        {
            DryRun.Guard("critical battery action (AC) = " + value);
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubBattery, ref CriticalAction, value), "PowerWriteACValueIndex");
            Guid active = ActiveScheme();
            if (active == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref active), "PowerSetActiveScheme");
        }

        public static void Release(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            DryRun.Guard("release system wake request");
            PowerClearRequest(h, PowerRequestSystemRequired);
            CloseHandle(h);
        }
    }

    class PowerAccess
    {
        public virtual Guid ActiveScheme() { return Power.ActiveScheme(); }
        public virtual uint ReadAcLid(Guid scheme) { return Power.ReadAcLid(scheme); }
        public virtual void WriteAcLid(Guid scheme, uint value) { Power.WriteAcLid(scheme, value); }
        public virtual uint ReadAcCriticalAction(Guid scheme) { return Power.ReadAcCriticalAction(scheme); }
        public virtual uint ReadAcCriticalLevel(Guid scheme) { return Power.ReadAcCriticalLevel(scheme); }
        public virtual void WriteAcCriticalAction(Guid scheme, uint value) { Power.WriteAcCriticalAction(scheme, value); }
        public virtual IntPtr KeepAwake() { return Power.KeepAwake("Acer battery calibration in progress"); }
        public virtual void Release(IntPtr request) { Power.Release(request); }
    }

    // During calibration the PC must stay on and connected to AC power:
    // block automatic sleep and set lid close to Do nothing (AC only), then restore.
    class CalibrationGuards
    {
        IntPtr request = IntPtr.Zero;
        readonly PowerAccess power;
        int awakeErrors, lidErrors, restoreErrors;
        int criticalErrors, criticalRestoreErrors;
        public bool CriticalProtected { get; private set; }
        public int? CriticalLevel { get; private set; }

        public CalibrationGuards() : this(new PowerAccess()) { }
        internal CalibrationGuards(PowerAccess access) { power = access; }

        static void Error(ref int count, string message)
        {
            count++;
            if (count == 1 || count % 10 == 0) Log.Write(message + " (" + count + ")");
        }

        public virtual void Acquire(Config cfg)
        {
            if (request == IntPtr.Zero)
            {
                try
                {
                    request = power.KeepAwake();
                    awakeErrors = 0;
                    Log.Write("[calibration] automatic sleep blocked");
                }
                catch (Exception ex)
                {
                    Error(ref awakeErrors, "[calibration] cannot block sleep: " + ex.Message);
                }
            }
            try
            {
                if (cfg.SavedLidAction < 0)
                {
                    Guid scheme = power.ActiveScheme();
                    uint cur = power.ReadAcLid(scheme);
                    if (cur != 0)
                    {
                        cfg.SavedLidAction = (int)cur;
                        cfg.SavedLidScheme = scheme.ToString();
                    }
                }
                if (cfg.SavedLidAction >= 0)
                {
                    // Even after a Save error, persist the journal before any attempt.
                    cfg.Save();
                    Guid scheme = new Guid(cfg.SavedLidScheme);
                    if (power.ReadAcLid(scheme) != 0)
                    {
                        power.WriteAcLid(scheme, 0);
                        Log.Write("[calibration] lid close (plugged in): Do nothing");
                    }
                }
                lidErrors = 0;
            }
            catch (Exception ex)
            {
                Error(ref lidErrors, "[calibration] cannot change lid action: " + ex.Message);
            }
        }

        public virtual void Release(Config cfg)
        {
            if (request != IntPtr.Zero)
            {
                try { power.Release(request); } catch { }
                request = IntPtr.Zero;
                Log.Write("[calibration] automatic sleep allowed again");
            }
            try { RestoreCritical(cfg); }
            finally { RestoreLid(cfg); }
        }

        public virtual void UpdateCritical(Config cfg, bool allowOverride)
        {
            bool previouslyProtected = CriticalProtected;
            CriticalProtected = false;
            CriticalLevel = null;
            try
            {
                Guid scheme = power.ActiveScheme();
                CriticalLevel = (int)power.ReadAcCriticalLevel(scheme);
                // Restore the old scheme before journaling another; a failed restore keeps its identity.
                if (cfg.SavedCritAction >= 0 && cfg.SavedCritScheme != scheme.ToString()) RestoreCritical(cfg);
                if (!allowOverride)
                {
                    RestoreCritical(cfg);
                    return;
                }
                if (cfg.SavedCritAction >= 0 && cfg.SavedCritScheme != scheme.ToString())
                    throw new AcerException("Previous power scheme still needs critical-action recovery.");
                bool journalNew = false;
                if (cfg.SavedCritAction < 0)
                {
                    cfg.SavedCritAction = (int)power.ReadAcCriticalAction(scheme);
                    cfg.SavedCritScheme = scheme.ToString();
                    journalNew = true;
                }
                bool changed = power.ReadAcCriticalAction(scheme) != 0;
                bool write = changed || !previouslyProtected;
                // The backup must reach disk before every write attempt (also after a failed save);
                // steady polls with nothing to write do not rewrite the journal.
                if (journalNew || write) cfg.Save();
                if (write)
                {
                    power.WriteAcCriticalAction(scheme, 0);
                }
                if (power.ReadAcCriticalAction(scheme) != 0 || power.ActiveScheme() != scheme)
                    throw new AcerException("Critical battery action was not applied to the active scheme.");
                if (changed) Log.Write("[calibration] critical battery action (plugged in): Do nothing");
                CriticalProtected = true;
                criticalErrors = 0;
            }
            catch (Exception ex)
            {
                Error(ref criticalErrors, "[calibration] cannot guard critical battery action: " + ex.Message);
                RestoreCritical(cfg); // A write may have succeeded before apply/readback failed.
            }
        }

        public virtual void RestoreCritical(Config cfg)
        {
            CriticalProtected = false;
            if (cfg.SavedCritAction < 0) return;
            int saved = cfg.SavedCritAction;
            string scheme = cfg.SavedCritScheme;
            try
            {
                if (saved > 3) throw new AcerException("Invalid saved critical battery action.");
                power.WriteAcCriticalAction(new Guid(scheme), (uint)saved);
                if (power.ReadAcCriticalAction(new Guid(scheme)) != (uint)saved)
                    throw new AcerException("Critical battery action restore could not be verified.");
                cfg.SavedCritAction = -1;
                cfg.SavedCritScheme = "";
                cfg.Save();
                criticalRestoreErrors = 0;
                Log.Write("[calibration] critical battery action (plugged in) restored to " + CalibrationSafety.ActionName((uint)saved));
            }
            catch (Exception ex)
            {
                cfg.SavedCritAction = saved;
                cfg.SavedCritScheme = scheme;
                Error(ref criticalRestoreErrors, "[calibration] critical battery action restore failed, backup retained: " + ex.Message);
            }
        }

        public virtual void RestoreLid(Config cfg)
        {
            if (cfg.SavedLidAction < 0) return;
            int saved = cfg.SavedLidAction;
            string scheme = cfg.SavedLidScheme;
            try
            {
                power.WriteAcLid(new Guid(scheme), (uint)saved);
                cfg.SavedLidAction = -1;
                cfg.SavedLidScheme = "";
                cfg.Save();
                restoreErrors = 0;
                Log.Write("[calibration] lid close (plugged in) restored to " + saved);
            }
            catch (Exception ex)
            {
                cfg.SavedLidAction = saved;
                cfg.SavedLidScheme = scheme;
                Error(ref restoreErrors, "[calibration] lid action restore failed, backup retained: " + ex.Message);
            }
        }
    }
}
