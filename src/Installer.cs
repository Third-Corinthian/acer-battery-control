using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AcerBatteryControl
{
    // the data folder is trusted only if no non-administrator owns it or can
    // write to it. The service (LocalSystem) reads config.ini from it and writes to the power plan.
    static class DataDirTrust
    {
        const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        static bool Trusted(IdentityReference id)
        {
            SecurityIdentifier sid = id as SecurityIdentifier;
            if (sid == null) return false;
            return sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || sid.Value == TrustedInstallerSid;
        }

        static string Name(IdentityReference id)
        {
            try { return id.Translate(typeof(NTAccount)).Value; } catch { return id.Value; }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] info);

        // number of file names (hardlinks), read without following reparse points.
        internal static int Links(string path)
        {
            using (SafeFileHandle h = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, IntPtr.Zero,
                3 /* OPEN_EXISTING */, 0x00200000 | 0x02000000 /* OPEN_REPARSE_POINT | BACKUP_SEMANTICS */, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new IOException("cannot open " + path);
                byte[] info = new byte[52]; // BY_HANDLE_FILE_INFORMATION
                if (!GetFileInformationByHandle(h, info)) throw new IOException("cannot read " + path);
                return BitConverter.ToInt32(info, 40); // nNumberOfLinks
            }
        }

        static string Check(FileSystemInfo item)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "link: " + item.FullName;
            bool isDir = item is DirectoryInfo;
            if (!isDir && Links(item.FullName) > 1) return "file with multiple names (hardlinks): " + item.FullName;
            FileSystemSecurity sec = isDir
                ? (FileSystemSecurity)((DirectoryInfo)item).GetAccessControl()
                : ((FileInfo)item).GetAccessControl();
            IdentityReference owner = sec.GetOwner(typeof(SecurityIdentifier));
            if (!Trusted(owner)) return "owner " + Name(owner) + ": " + item.FullName;
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow || Trusted(r.IdentityReference)) continue;
                if (r.PropagationFlags == PropagationFlags.InheritOnly) continue;   // e.g. CREATOR OWNER
                if ((r.FileSystemRights & WriteRights) != 0) return "writable by " + Name(r.IdentityReference) + ": " + item.FullName;
            }
            return null;
        }

        // Manual traversal: never enters a reparse point (junction, symlink), unlike AllDirectories.
        static string Walk(DirectoryInfo dir)
        {
            string p = Check(dir);
            if (p != null) return p;
            foreach (FileSystemInfo item in dir.GetFileSystemInfos())
            {
                DirectoryInfo sub = item as DirectoryInfo;
                p = sub != null ? Walk(sub) : Check(item);
                if (p != null) return p;
            }
            return null;
        }

        // null if the folder is missing or safe: trusted owners, no links or hardlinks,
        // no write access for non-administrators, at every level.
        public static string Problem(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            try { return Walk(new DirectoryInfo(dir)); }
            catch (Exception ex) { return "cannot verify: " + ex.Message; }
        }

        static DirectorySecurity SecureAcl()
        {
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetOwner(admins);
            ds.SetAccessRuleProtection(true, false);
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(admins,
                FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            return ds;
        }

        // use an already secure folder as is; do NOT repair an insecure one in place (a handle
        // opened earlier survives a DACL change): move it aside and create a new one, protected
        // from creation. Refuse if the move fails (open folder handle, permissions).
        // Requires SYSTEM or administrator. Returns a log note, or null.
        public static string Secure(string dir)
        {
            string note = null;
            if (Directory.Exists(dir))
            {
                string p = Problem(dir);
                if (p == null) return null;
                string aside = dir + ".untrusted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Util.Inv);
                try { Directory.Move(dir, aside); }
                catch (Exception ex)
                {
                    throw new AcerException("The folder " + dir + " is untrusted (" + p + ") and cannot be moved ("
                        + ex.Message + "). Delete it and retry installation.");
                }
                note = "untrusted data folder moved to " + aside + " (" + p + "); default configuration";
            }
            Directory.CreateDirectory(dir, SecureAcl());
            // CreateDirectory succeeds if someone recreated it between move and creation: check again.
            string left = Problem(dir);
            if (left != null) throw new AcerException("Failed to secure the data folder: " + left);
            return note;
        }
    }

    static class Installer
    {
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AcerBatteryControl";

        public static bool ServiceExists()
        {
            foreach (ServiceController s in ServiceController.GetServices())
            {
                using (s)
                {
                    if (s.ServiceName.Equals(Paths.ServiceName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        static void Sc(string args, StringBuilder report, bool mustSucceed)
        {
            string output;
            int rc = Util.RunHidden("sc.exe", args, out output);
            if (rc != 0 && mustSucceed) throw new AcerException("sc.exe " + args + " -> " + rc + ": " + output);
            if (rc != 0 && report != null) report.AppendLine("Warning: sc.exe " + args.Split(' ')[0] + " -> " + rc);
        }

        static void StopService()
        {
            using (ServiceController sc = new ServiceController(Paths.ServiceName))
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }

        static void KillOtherInstances()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (string name in new string[] { "AcerBatteryControl", "acerbat" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        if (p.Id == me) continue;
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                    }
                }
            }
        }

        static void SecureDataDir(StringBuilder report)
        {
            string note = DataDirTrust.Secure(Paths.DataDir);
            if (note != null) report.AppendLine("Warning: " + note + ".");
        }

        static RegistryKey Hklm()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        }

        static RegistryKey OpenRun(bool writable)
        {
            using (RegistryKey hklm = Hklm()) return hklm.OpenSubKey(RunKey, writable);
        }

        // Entry in Settings > Apps > Installed apps: uninstall runs "AcerBatteryControl.exe uninstall"
        // (self-elevates through UAC). No modify or repair entry.
        static void RegisterUninstaller(string dir, string gui)
        {
            long bytes = 0;
            foreach (string f in Directory.GetFiles(dir)) bytes += new FileInfo(f).Length;
            using (RegistryKey hklm = Hklm())
            using (RegistryKey k = hklm.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Paths.AppName, RegistryValueKind.String);
                k.SetValue("DisplayVersion", Paths.Version, RegistryValueKind.String);
                k.SetValue("Publisher", "Independent project (not affiliated with Acer)", RegistryValueKind.String);
                k.SetValue("DisplayIcon", "\"" + gui + "\",0", RegistryValueKind.String);
                k.SetValue("InstallLocation", dir, RegistryValueKind.String);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", Util.Inv), RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + gui + "\" uninstall", RegistryValueKind.String);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
            }
        }

        static void UnregisterUninstaller()
        {
            using (RegistryKey hklm = Hklm()) hklm.DeleteSubKeyTree(UninstallKey, false);
        }

        // uninstall only if the journal proves nothing needs recovery. A missing or unreadable
        // journal, or one in an untrusted folder, means "unknown state": keep everything and refuse.
        // With --force the user accepts unknown journal/firmware state after checking both AC actions,
        // but never a calibration reported active by the service or firmware.
        static void CheckNothingPending(bool force)
        {
            CheckNothingPending(force, AcerWmi.GetStatus, delegate { return DataDirTrust.Problem(Paths.DataDir); });
        }

        internal static void CheckNothingPending(bool force, Func<HealthStatus> readStatus, Func<string> trustProblem)
        {
            CheckFirmwareNotCalibrating(force, readStatus);
            if (force) return;
            string why = trustProblem();
            Config saved = why == null ? Config.LoadStrict(out why) : null;
            if (saved == null)
                throw new AcerException("Cannot verify calibration and power restore state (" + why + "): data retained, uninstall cancelled.\n"
                    + "Check the lid close and critical battery actions when plugged in under Control Panel > Power Options, "
                    + "then run from an administrator command prompt: AcerBatteryControl.exe uninstall --force");
            if (saved.Calibrating || saved.SavedLidAction >= 0 || saved.SavedCritAction >= 0)
                throw new AcerException("Power setting restore or calibration still pending: data retained, uninstall cancelled. "
                    + "Start the service: it will complete recovery automatically.");
        }

        static void CheckFirmwareNotCalibrating(bool force, Func<HealthStatus> readStatus)
        {
            HealthStatus h = null;
            try { h = readStatus(); }
            catch (Exception ex) { if (!force) throw new AcerException("Cannot read firmware state (" + ex.Message + "): uninstall cancelled."); }
            if (h != null && h.Valid && h.CalibOn)
                throw new AcerException("The firmware reports calibration in progress: wait for it to finish or stop it, then uninstall.");
            if ((h == null || !h.Valid) && !force)
                throw new AcerException("Cannot read firmware state: uninstall cancelled. Try again shortly.");
        }

        static bool ServiceRunning()
        {
            try
            {
                using (ServiceController sc = new ServiceController(Paths.ServiceName))
                    return sc.Status != ServiceControllerStatus.Stopped;
            }
            catch (InvalidOperationException) { return false; }
        }


        public static string Install()
        {
            DryRun.Guard("installation");
            if (!Util.IsAdmin()) throw new AcerException("Administrator privileges required.");
            AcerWmi.EnsurePresent(); // No installation if the firmware does not expose the interface.
            StringBuilder report = new StringBuilder();

            bool exists = ServiceExists();
            if (exists) StopService();
            KillOtherInstances();

            string srcDir = Path.GetDirectoryName(Util.ExePath);
            string dst = Paths.InstallDir;
            Directory.CreateDirectory(dst);
            foreach (string f in new string[] { Paths.GuiExeName, Paths.GuiExeName + ".config", Paths.CliExeName, "README.md" })
            {
                string from = Path.Combine(srcDir, f);
                string to = Path.Combine(dst, f);
                if (File.Exists(from) && !string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                    File.Copy(from, to, true);
            }
            string gui = Path.Combine(dst, Paths.GuiExeName);
            if (!File.Exists(gui)) throw new AcerException("Copy failed: " + gui);

            // A scheduled charge-limit task would compete with the service.
            string o;
            if (Util.RunHidden("schtasks.exe", "/Query /TN AcerChargeLimit", out o) == 0)
            {
                Util.RunHidden("schtasks.exe", "/Delete /TN AcerChargeLimit /F", out o);
                report.AppendLine("Removed the competing scheduled task \"AcerChargeLimit\".");
            }

            SecureDataDir(report);
            if (!File.Exists(Paths.ConfigFile)) new Config().Save();   // Default: 80% limit enabled

            string bin = "\\\"" + gui + "\\\" --service";
            if (exists)
                Sc("config " + Paths.ServiceName + " binPath= \"" + bin + "\" start= auto", null, true);
            else
                Sc("create " + Paths.ServiceName + " binPath= \"" + bin + "\" start= auto DisplayName= \"" + Paths.AppName + "\"", null, true);
            Sc("description " + Paths.ServiceName + " \"Maintains the 80% charge limit and manages battery calibration on Acer laptops, without Acer Care Center.\"", report, false);
            Sc("failure " + Paths.ServiceName + " reset= 86400 actions= restart/60000/restart/60000//0", report, false);

            using (ServiceController sc = new ServiceController(Paths.ServiceName))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }

            using (RegistryKey run = OpenRun(true))
                run.SetValue(Paths.ServiceName, "\"" + gui + "\"", RegistryValueKind.String);
            RegisterUninstaller(dst, gui);

            Log.Write("[install] v" + Paths.Version + " installed in " + dst);
            // Start the tray icon as a normal, non-elevated user.
            try { Process.Start("explorer.exe", "\"" + gui + "\""); } catch { }

            report.Insert(0, "Installed in " + dst + ".\n" +
                "Service \"" + Paths.AppName + "\" started (automatic startup, system account).\n" +
                "The notification area icon starts at every sign-in.\n" +
                "To uninstall: Settings > Apps > Installed apps > " + Paths.AppName + ".\n");
            return report.ToString().Trim();
        }

        public static string Uninstall(bool force)
        {
            DryRun.Guard("uninstall");
            if (!Util.IsAdmin()) throw new AcerException("Administrator privileges required.");
            string fwState = "";
            IpcResult st = Ipc.Call("STATUS", 1500);
            if (st.Ok) fwState = StatusView.LimitLine(st);
            if (st.Ok && (st.Get("calibrating") == "1" || st.Get("calib_on") == "1"))
                throw new AcerException("Calibration in progress: stop it before uninstalling.");

            if (ServiceExists())
            {
                // Service stopped: read pending state from config.ini after stopping (below).
                // Service running but unresponsive: calibration state is unknown, so refuse.
                if (!st.Ok && ServiceRunning())
                    throw new AcerException("The service is running but not responding: restart it before uninstalling.");
                bool wasRunning = st.Ok;
                StopService();
                try { CheckNothingPending(force); }
                catch
                {
                    // Refusal: restore the service's previous state so the limit remains maintained.
                    if (wasRunning) try { using (ServiceController sc = new ServiceController(Paths.ServiceName)) sc.Start(); } catch { }
                    throw;
                }
                Sc("delete " + Paths.ServiceName, null, true);
            }
            else if (Directory.Exists(Paths.DataDir))
                CheckNothingPending(force);
            using (RegistryKey run = OpenRun(true))
                if (run != null && run.GetValue(Paths.ServiceName) != null) run.DeleteValue(Paths.ServiceName);
            UnregisterUninstaller();
            KillOtherInstances();

            try { if (Directory.Exists(Paths.DataDir)) Directory.Delete(Paths.DataDir, true); } catch { }

            string myDir = Path.GetDirectoryName(Util.ExePath);
            if (Directory.Exists(Paths.InstallDir))
            {
                if (string.Equals(Path.GetFullPath(myDir).TrimEnd('\\'), Path.GetFullPath(Paths.InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                        "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                else
                {
                    try { Directory.Delete(Paths.InstallDir, true); } catch { }
                }
            }
            return "Uninstalled. Firmware state was not changed"
                   + (fwState.Length > 0 ? " (80% limit: " + fwState + ")." : ".");
        }
    }
}
