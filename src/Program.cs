using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Acer Battery Control")]
[assembly: AssemblyProduct("Acer Battery Control")]
[assembly: AssemblyDescription("80% charge limit and battery calibration for Acer laptops, without Acer Care Center")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace AcerBatteryControl
{
#if CLI
    static class Program
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            return Cli.Run(args);
        }
    }
#else
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (cmd == "--service")
            {
                // Last resort: the process still exits and SCM restarts it, but the cause remains in the log.
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("[service] unhandled exception, the service will restart: " + e.ExceptionObject);
                };
                ServiceBase.Run(new BatteryService());
                return 0;
            }

            // a UI thread exception must not open an error dialog or leave the icon in an
            // unknown state: log it (if possible) and shut down the icon cleanly.
            // The service is unaffected; the icon restarts at the next sign-in.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { Log.Write("[tray] unexpected error, tray icon closed: " + e.Exception); } catch { }
                try { Application.Exit(); } catch { }
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (cmd == "install" || cmd == "uninstall")
                return RunSetup(cmd, args.Length > 1 && args[1].ToLowerInvariant() == "--force");

            bool created;
            using (Mutex m = new Mutex(true, "Local\\AcerBatteryControlTray", out created))
            {
                if (!created) return 0;
                if (!Installer.ServiceExists())
                {
                    DialogResult a = MessageBox.Show(
                        "The Acer Battery Control service is not installed.\n\n" +
                        "Install it now? It will be copied to " + Paths.InstallDir + ", registered as a system service, " +
                        "and the tray icon will start at every sign-in. Administrator confirmation is required.",
                        Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (a == DialogResult.Yes)
                    {
                        m.ReleaseMutex();
                        Util.RunElevated(Util.ExePath, "install", false);
                    }
                    return 0;
                }
                TrayContext tray = new TrayContext();
                try { Application.Run(tray); }
                finally { try { tray.Dispose(); } catch { } }   // The icon disappears from the notification area.
            }
            return 0;
        }

        static int RunSetup(string cmd, bool force)
        {
            if (!Util.IsAdmin())
                return Util.RunElevated(Util.ExePath, cmd + (force ? " --force" : ""), false) ? 0 : 1;
            try
            {
                if (force && MessageBox.Show("Uninstall without checking the calibration and power setting journal?\n\n"
                        + "First check the lid close and critical battery actions when plugged in under Power Options. "
                        + "A reported firmware calibration still blocks uninstall.",
                        Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return 1;
                string msg = cmd == "install" ? Installer.Install() : Installer.Uninstall(force);
                MessageBox.Show(msg, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Operation failed:\n" + ex.Message, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
#endif

    static class Cli
    {
        const string Usage =
            "acerbat - Acer Battery Control " + Paths.Version + "\n\n" +
            "  acerbat status                 full status\n" +
            "  acerbat limit on|off           80% charge limit\n" +
            "  acerbat calibrate start|stop   calibration (AC adapter connected)\n" +
            "  acerbat enforce                check the limit immediately\n" +
            "  acerbat log [lines]            latest log lines\n" +
            "  acerbat probe                  direct firmware test, without service (admin)\n" +
            "  acerbat install|uninstall      install/remove service and tray icon (admin)\n" +
            "  acerbat uninstall --force      remove even with a lost recovery journal (admin)\n";

        public static int Run(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string arg = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            try
            {
                switch (cmd)
                {
                    case "status": return Status();
                    case "limit":
                        if (arg == "on") return Simple("LIMIT ON");
                        if (arg == "off") return Simple("LIMIT OFF");
                        break;
                    case "calibrate":
                        if (arg == "start")
                        {
                            Console.WriteLine(CalibrationSafety.Warning(new PowerAccess()));
                            return Simple("CALIBRATE START");
                        }
                        if (arg == "stop") return Simple("CALIBRATE STOP");
                        break;
                    case "enforce": return Simple("ENFORCE");
                    case "log": return Simple("LOG " + (arg.Length > 0 ? arg : "50"));
                    case "probe": return Probe();
                    case "install": Console.WriteLine(Installer.Install()); return 0;
                    case "uninstall": Console.WriteLine(Installer.Uninstall(arg == "--force")); return 0;
                }
                Console.Write(Usage);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static int Simple(string command)
        {
            IpcResult r = Ipc.Call(command, 3000);
            if (!r.Ok)
            {
                Console.Error.WriteLine(r.Message);
                return 1;
            }
            Console.WriteLine(r.Body.TrimEnd());
            return 0;
        }

        static int Status()
        {
            IpcResult r = Ipc.Call("STATUS", 3000);
            if (!r.Ok)
            {
                Console.Error.WriteLine(r.Message);
                return 1;
            }
            string[] v = StatusView.Values(r);
            int w = 0;
            foreach (string l in StatusView.Labels) w = Math.Max(w, l.Length);
            for (int i = 0; i < v.Length; i++)
                Console.WriteLine("  " + StatusView.Labels[i].PadRight(w) + "  " + v[i]);
            Console.WriteLine("  " + "Firmware (raw)".PadRight(w) + "  " + r.Get("fw_raw"));
            return 0;
        }

        static int Probe()
        {
            if (!Util.IsAdmin())
            {
                Console.Error.WriteLine("probe requires an administrator command prompt.");
                return 1;
            }
            HealthStatus h = AcerWmi.GetStatus();
            Console.WriteLine("BatteryControl found. " + h.Raw);
            Console.WriteLine("  Valid: " + h.Valid);
            Console.WriteLine("  80% limit   supported: " + h.HealthSupported + "  enabled: " + h.HealthOn);
            Console.WriteLine("  Calibration supported: " + h.CalibSupported + "  active: " + h.CalibOn);
            Telemetry t = AcerWmi.ReadStandard();
            AcerWmi.ReadFirmwareSensors(t);
            Console.WriteLine("  Charge: " + (t.Percent.HasValue ? t.Percent + "%" : "?")
                              + "  AC: " + t.OnAC + "  Temp: " + (t.TempC.HasValue ? t.TempC.Value.ToString("0.0", Util.It) + " °C" : "?")
                              + "  Voltage: " + t.VoltageMv + " mV  Current: " + t.CurrentMa + " mA");
            Console.WriteLine("  Capacity: " + t.FullMwh + " / " + t.DesignMwh + " mWh  Cycles: " + t.Cycles + "  " + t.Model);
            return h.Valid ? 0 : 3;
        }
    }
}
