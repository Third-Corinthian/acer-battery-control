using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AcerBatteryControl
{
    static class IconRenderer
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        public static Icon Render(int size, TrayState state, int percent)
        {
            return Render(size, state, percent, false);
        }

        public static Icon Render(int size, TrayState state, int percent, bool darkTheme)
        {
            Color outline = darkTheme ? Color.White : Color.FromArgb(45, 45, 45);
            Color fill;
            switch (state)
            {
                case TrayState.LimitOn: fill = Color.FromArgb(46, 170, 70); break;
                case TrayState.LimitOff: fill = Color.FromArgb(235, 150, 30); break;
                case TrayState.Calibrating: fill = Color.FromArgb(50, 130, 230); break;
                case TrayState.Stopped: fill = Color.FromArgb(215, 45, 45); break;
                default: fill = Color.FromArgb(150, 150, 150); break;
            }
            // Service stopped: full red battery, recognizable even at 16 px
            if (state == TrayState.Stopped) percent = 100;
            using (Bitmap bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);
                float u = size / 16f;
                int bx = (int)Math.Round(1 * u), by = (int)Math.Round(3 * u);
                int bw = (int)Math.Round(12 * u), bh = (int)Math.Round(10 * u);
                int tipW = Math.Max(1, (int)Math.Round(2 * u)), tipH = (int)Math.Round(4 * u);
                int border = Math.Max(1, (int)Math.Round(u));

                using (SolidBrush dark = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dark, bx, by, bw, bh);
                int inner = bw - 2 * border - 2;
                int fw = Math.Max(0, Math.Min(inner, (int)Math.Round(inner * Math.Max(0, Math.Min(100, percent)) / 100.0)));
                if (percent > 0 && fw == 0) fw = 1;
                using (SolidBrush b = new SolidBrush(fill))
                    g.FillRectangle(b, bx + border + 1, by + border + 1, fw, bh - 2 * border - 2);
                using (Pen p = new Pen(outline, border))
                {
                    p.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(p, bx, by, bw - 1, bh - 1);
                }
                using (SolidBrush w = new SolidBrush(outline))
                    g.FillRectangle(w, bx + bw, by + (bh - tipH) / 2, tipW, tipH);

                if (state == TrayState.Error)
                {
                    using (Pen x = new Pen(Color.FromArgb(220, 40, 40), Math.Max(2, 2 * u)))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawLine(x, 3 * u, 3 * u, 13 * u, 13 * u);
                        g.DrawLine(x, 13 * u, 3 * u, 3 * u, 13 * u);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon notify;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miCharge, miLimitState, miLimit, miCalStart, miCalStop, miPanel, miLog, miService, miInfo, miExit;
        string service = "";
        readonly System.Windows.Forms.Timer timer;
        readonly Control invoker;
        MainForm form;
        IpcResult last;
        Icon currentIcon;
        string iconKey = "";
        long lastSeq = -1;
        bool wasReachable = true;
        int failures;
        int polling;
        int commanding;
        volatile bool exiting;

        public TrayContext()
        {
            invoker = new Control();
            IntPtr force = invoker.Handle; // Create the handle for BeginInvoke.

            menu = new ContextMenuStrip();
            miCharge = new ToolStripMenuItem("…") { Enabled = false };
            miCharge.Font = new Font(menu.Font, FontStyle.Bold);
            miLimitState = new ToolStripMenuItem("…") { Enabled = false };
            miLimit = new ToolStripMenuItem("80% charge limit");
            miLimit.Click += delegate { ToggleLimit(); };
            ToolStripMenuItem miCal = new ToolStripMenuItem("Calibration");
            miCalStart = new ToolStripMenuItem("Start calibration…");
            miCalStart.Click += delegate { StartCalibration(); };
            miCalStop = new ToolStripMenuItem("Stop calibration");
            miCalStop.Click += delegate { StopCalibration(); };
            miCal.DropDownItems.Add(miCalStart);
            miCal.DropDownItems.Add(miCalStop);
            miPanel = new ToolStripMenuItem("Status panel");
            miPanel.Font = new Font(menu.Font, FontStyle.Bold);
            miPanel.Click += delegate { ShowPanel(); };
            miLog = new ToolStripMenuItem("Open log");
            miLog.Click += delegate { OpenLog(); };
            miService = new ToolStripMenuItem("Stop service…");
            miService.Click += delegate { ToggleService(); };
            miInfo = new ToolStripMenuItem("About and credits");
            miInfo.Click += delegate { ShowInfo(); };
            miExit = new ToolStripMenuItem("Exit tray icon (service keeps running)");
            miExit.Click += delegate { Quit(); };

            menu.Items.AddRange(new ToolStripItem[] {
                miCharge, miLimitState, new ToolStripSeparator(),
                miLimit, miCal, new ToolStripSeparator(),
                miPanel, miLog, new ToolStripSeparator(),
                miService, miInfo, new ToolStripSeparator(), miExit });

            notify = new NotifyIcon();
            notify.ContextMenuStrip = menu;
            notify.Text = Paths.AppName;
            SetIcon(TrayState.Unknown, 0);
            notify.Visible = true;
            notify.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowPanel(); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 15000;
            timer.Tick += delegate { Poll(); };
            timer.Start();
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            DesktopStyle.Apply(menu);
            Poll();
        }

        // ------------------------------------------------------------------ polling

        public void Poll()
        {
            if (exiting) return;
            if (Interlocked.Exchange(ref polling, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    IpcResult r = Ipc.Call("STATUS", 2000);
                    string svc = ServiceInfo.State();
                    Post(delegate
                    {
                        service = svc;
                        Apply(r);
                    });
                }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
                finally { Interlocked.Exchange(ref polling, 0); }
            });
        }

        bool Post(Action action)
        {
            return UiDispatch.Post(delegate(Action work) { invoker.BeginInvoke(work); }, delegate { return exiting; }, action);
        }

        void PreferencesChanged(object sender, UserPreferenceChangedEventArgs args) { RefreshStyle(); }
        void DisplayChanged(object sender, EventArgs args) { RefreshStyle(); }
        void RefreshStyle()
        {
            Post(delegate
            {
                iconKey = "";
                DesktopStyle.Apply(menu);
                if (form != null && !form.IsDisposed) DesktopStyle.Apply(form);
                if (last != null) Apply(last);
            });
        }

        void Apply(IpcResult r)
        {
            last = r;
            TrayState st = StatusView.State(r);
            bool stopped = !r.Ok && service == ServiceInfo.Stopped;
            if (stopped) st = TrayState.Stopped;
            int pct = 0;
            int.TryParse(r.Get("percent"), out pct);
            SetIcon(st, pct);

            // action depends only on SCM state (same rule as the panel); disabled during transitions.
            string action = ServiceInfo.Action(service);
            miService.Text = action == ServiceInfo.ActionStart ? "Start service…" : "Stop service…";
            miService.Visible = service != ServiceInfo.Absent;
            miService.Enabled = action != null && !(action == ServiceInfo.ActionStop && StopBlocked(r));

            if (!r.Ok)
            {
                miCharge.Text = stopped ? "Service stopped" : "Service unreachable";
                string detail = stopped ? "The 80% limit is not being maintained." : r.Message;
                miLimitState.Text = detail.Length > 70 ? detail.Substring(0, 70) + "…" : detail;
                miLimit.Enabled = miCalStart.Enabled = miCalStop.Enabled = false;
                SetTip(stopped ? "Acer Battery: service stopped" : "Acer Battery: service unreachable");
                failures++;
                if (failures == 2 && wasReachable)
                {
                    Balloon(stopped ? "Service stopped" : "Service unreachable",
                        "The charge limit is not under control. Use \"Start service\" from the menu.", ToolTipIcon.Warning);
                    wasReachable = false;
                }
            }
            else
            {
                failures = 0;
                if (!wasReachable) Balloon("Service reachable again", StatusView.LimitLine(r), ToolTipIcon.Info);
                wasReachable = true;

                bool calibrating = r.Get("calibrating") == "1";
                miCharge.Text = "Battery " + StatusView.ChargeLine(r);
                miLimitState.Text = "80% limit: " + StatusView.LimitLine(r);
                miLimit.Enabled = true;
                miLimit.Checked = r.Get("limit_desired") == "on";
                miCalStart.Enabled = !calibrating && r.Get("calib_supported") != "0";
                miCalStop.Enabled = calibrating;
                SetTip("Battery " + StatusView.ChargeLine(r) + " · limit " + StatusView.LimitLine(r));

                long seq;
                if (long.TryParse(r.Get("event_seq"), out seq))
                {
                    if (lastSeq >= 0 && seq > lastSeq && r.Get("event_text").Length > 0)
                    {
                        bool warn = r.Get("event_text").StartsWith("WARNING");
                        Balloon(Paths.AppName, r.Get("event_text"), warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    }
                    lastSeq = seq;
                }
            }
            if (commanding != 0) miLimit.Enabled = miCalStart.Enabled = miCalStop.Enabled = miService.Enabled = false;
            if (form != null && !form.IsDisposed && form.Visible) form.UpdateData(r, service, currentIcon);
        }

        void SetTip(string t)
        {
            notify.Text = t.Length > 63 ? t.Substring(0, 62) + "…" : t;
        }

        void SetIcon(TrayState st, int pct)
        {
            int size = DesktopStyle.IconSize();
            bool dark = DesktopStyle.TaskbarDark;
            string key = st + "|" + (pct / 5) + "|" + size + "|" + dark;
            if (key == iconKey) return;
            Icon old = currentIcon;
            currentIcon = IconRenderer.Render(size, st, pct, dark);
            notify.Icon = currentIcon;
            if (form != null && !form.IsDisposed) form.Icon = currentIcon;
            iconKey = key;
            if (old != null) old.Dispose();
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            try { notify.ShowBalloonTip(6000, title, text, icon); } catch { }
        }

        // ------------------------------------------------------------------ commands

        void Run(string command, bool confirmResult)
        {
            RunWork(delegate { return Ipc.Call(command, 3000); }, confirmResult);
        }

        void RunWork(Func<IpcResult> work, bool confirmResult)
        {
            if (exiting || Interlocked.CompareExchange(ref commanding, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                IpcResult r;
                try { r = work(); }
                catch (Exception ex) { r = new IpcResult { Message = ex.Message }; }
                Post(delegate
                {
                    if (!r.Ok)
                        MessageBox.Show(r.Message, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (confirmResult)
                        Balloon(Paths.AppName, r.Message, ToolTipIcon.Info);
                    Poll();
                });
                }
                finally { Interlocked.Exchange(ref commanding, 0); }
            });
        }

        public void ToggleLimit()
        {
            bool on = last != null && last.Get("limit_desired") == "on";
            Run(on ? "LIMIT OFF" : "LIMIT ON", true);
        }

        public void StartCalibration()
        {
            string msg = CalibrationSafety.Warning(new PowerAccess()) + "\n\nStart calibration?";
            if (MessageBox.Show(msg, "Battery calibration", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Run("CALIBRATE START", true);
        }

        public void StopCalibration()
        {
            if (MessageBox.Show("Stop the current calibration? The 80% limit will be reapplied.", "Battery calibration",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Run("CALIBRATE STOP", true);
        }

        public void OpenLog()
        {
            if (!File.Exists(Paths.LogFile))
            {
                MessageBox.Show("The log does not exist yet.", Paths.AppName);
                return;
            }
            try { Process.Start("notepad.exe", "\"" + Paths.LogFile + "\""); } catch { }
        }

        public void ToggleService()
        {
            string action = ServiceInfo.Action(service);
            if (action == ServiceInfo.ActionStop) StopService();
            else if (action == ServiceInfo.ActionStart) StartService();
        }

        // Start and stop use elevated sc.exe (UAC): a standard user lacks these service permissions.
        // read the sc.exe exit code and wait for the SCM state before reporting the outcome.
        static IpcResult ScCommand(string verb, string expected, string done)
        {
            int? code = Util.RunElevatedExitCode("sc.exe", verb + " " + Paths.ServiceName, 15000);
            if (!code.HasValue) return new IpcResult { Message = "Operation cancelled." };
            if (code.Value != 0) return new IpcResult { Message = "sc.exe " + verb + " failed (code " + code.Value + "). State: " + ServiceInfo.State() + "." };
            if (!ServiceInfo.WaitFor(expected, 30000)) return new IpcResult { Message = "Command accepted, but the service is still " + ServiceInfo.State() + "." };
            return new IpcResult { Ok = true, Message = done };
        }

        public void StartService()
        {
            RunWork(delegate { return ScCommand("start", ServiceInfo.Running, "Service started."); }, true);
        }

        public void StopService()
        {
            string msg = "Stop the Acer Battery Control service?\n\n"
                + "The firmware keeps the current limit setting, but it will no longer be maintained: "
                + "if another program (such as Acer Care Center) changes it, it will not be restored. "
                + "The icon turns red until you restart the service from the menu.\n\n"
                + "Stopping is refused during calibration or while a power setting restore is pending.";
            if (MessageBox.Show(msg, Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            RunWork(delegate
            {
                // fresh journal and firmware state immediately before stopping.
                IpcResult now = Ipc.Call("STATUS", 3000);
                if (!now.Ok) return new IpcResult { Message = "Cannot read service state: service not stopped (" + now.Message + ")." };
                if (StopBlocked(now))
                    return new IpcResult { Message = "Calibration or power setting restore pending: service not stopped. Stop calibration and let recovery finish first." };
                return ScCommand("stop", ServiceInfo.Stopped, "Service stopped.");
            }, true);
        }

        public void ShowInfo()
        {
            string text = Paths.AppName + " " + Paths.Version + "\n\n"
                + "80% charge limit and battery calibration for Acer laptops, without Acer Care Center.\n"
                + "Independent project, not affiliated with Acer.\n\n"
                + "Credits\n"
                + "• acerctl (github.com/FnnDK/acerctl): WMI interface documentation and calibration behavior.\n"
                + "• acer-wmi-battery (github.com/frederik-h/acer-wmi-battery) and Linux kernel documentation: "
                + "BatteryControl methods and parameters.\n"
                + "The code is written from scratch for Windows.\n\n"
                + "Service: " + (service.Length > 0 ? service : ServiceInfo.Unknown) + "\n"
                + "Data and log: " + Paths.DataDir;
            MessageBox.Show(text, "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static bool StopBlocked(IpcResult status)
        {
            return status.Get("calibrating") == "1" || status.Get("calib_on") == "1"
                || status.Get("power_restore_pending") == "1";
        }

        void ShowPanel()
        {
            if (form == null || form.IsDisposed)
            {
                form = new MainForm(this);
                form.Icon = currentIcon;
            }
            if (last != null) form.UpdateData(last, service, currentIcon);
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            Poll();
        }

        void Quit()
        {
            Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
            {
                exiting = true;
                SystemEvents.UserPreferenceChanged -= PreferencesChanged;
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                timer.Dispose();
                notify.Visible = false;
                notify.Dispose();
                if (form != null) form.Dispose();
                miCharge.Font.Dispose();
                miPanel.Font.Dispose();
                menu.Dispose();
                invoker.Dispose();
                if (currentIcon != null) currentIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    class MainForm : Form
    {
        readonly Label[] values = new Label[StatusView.Labels.Length];
        readonly Button bLimit, bCal, bLog, bRefresh, bService;
        readonly Label footer, svcValue, svcWarn;
        readonly System.Windows.Forms.Timer fast;
        IpcResult last;

        public MainForm(TrayContext tray)
        {
            Text = Paths.AppName;
            Font = DesktopStyle.Font();
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);
            ShowInTaskbar = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.Dock = DockStyle.Fill;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            grid.AutoSize = true;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            for (int i = 0; i < StatusView.Labels.Length; i++)
            {
                Label k = new Label();
                k.Text = StatusView.Labels[i];
                k.AutoSize = true;
                k.ForeColor = SystemColors.GrayText;
                k.Margin = new Padding(0, 4, 16, 4);
                Label v = new Label();
                v.Text = "—";
                v.AutoSize = true;
                v.MaximumSize = new Size(380, 0);
                v.Margin = new Padding(0, 4, 0, 4);
                values[i] = v;
                grid.Controls.Add(k, 0, i);
                grid.Controls.Add(v, 1, i);
            }
            values[8].Font = new Font(Font, FontStyle.Bold);

            // Services: Windows state, Stop/Start command, warning if stopped.
            TableLayoutPanel svc = new TableLayoutPanel();
            svc.ColumnCount = 3;
            svc.AutoSize = true;
            svc.Margin = new Padding(0, 12, 0, 0);
            Label svcHead = new Label();
            svcHead.Text = "Services";
            svcHead.AutoSize = true;
            svcHead.Font = values[8].Font;
            svcHead.Margin = new Padding(0, 0, 0, 4);
            svc.Controls.Add(svcHead, 0, 0);
            svc.SetColumnSpan(svcHead, 3);
            Label svcName = new Label();
            svcName.Text = Paths.AppName;
            svcName.AutoSize = true;
            svcName.Anchor = AnchorStyles.Left;
            svcName.Margin = new Padding(0, 4, 16, 4);
            svcValue = new Label();
            svcValue.Text = "—";
            svcValue.AutoSize = true;
            svcValue.Anchor = AnchorStyles.Left;
            svcValue.Margin = new Padding(0, 4, 16, 4);
            bService = MakeButton("Stop service…");
            bService.Anchor = AnchorStyles.Left;
            bService.Click += delegate { tray.ToggleService(); };
            svc.Controls.Add(svcName, 0, 1);
            svc.Controls.Add(svcValue, 1, 1);
            svc.Controls.Add(bService, 2, 1);
            svcWarn = new Label();
            svcWarn.AutoSize = true;
            svcWarn.MaximumSize = new Size(560, 0);
            svcWarn.Margin = new Padding(0, 6, 0, 0);
            svcWarn.Visible = false;
            svc.Controls.Add(svcWarn, 0, 2);
            svc.SetColumnSpan(svcWarn, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Margin = new Padding(0, 12, 0, 0);
            bLimit = MakeButton("80% limit");
            bLimit.Click += delegate { tray.ToggleLimit(); };
            bCal = MakeButton("Calibration…");
            bCal.Click += delegate
            {
                if (last != null && last.Get("calibrating") == "1") tray.StopCalibration();
                else tray.StartCalibration();
            };
            bLog = MakeButton("Open log");
            bLog.Click += delegate { tray.OpenLog(); };
            bRefresh = MakeButton("Refresh");
            bRefresh.Click += delegate { tray.Poll(); };
            Button bInfo = MakeButton("About");
            bInfo.Click += delegate { tray.ShowInfo(); };
            buttons.Controls.AddRange(new Control[] { bLimit, bCal, bLog, bRefresh, bInfo });

            footer = new Label();
            footer.AutoSize = true;
            footer.ForeColor = SystemColors.GrayText;
            footer.Margin = new Padding(0, 10, 0, 0);

            root.Controls.Add(grid, 0, 0);
            root.Controls.Add(svc, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);

            fast = new System.Windows.Forms.Timer();
            fast.Interval = 4000;
            fast.Tick += delegate { if (Visible) tray.Poll(); };
            VisibleChanged += delegate { if (Visible) fast.Start(); else fast.Stop(); };
            HandleCreated += delegate { DesktopStyle.Apply(this); };
        }

        static Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }

        public void UpdateData(IpcResult r, string service, Icon icon)
        {
            last = r;
            if (icon != null) Icon = icon;
            bool running = r.Ok;
            bool calibrating = r.Ok && (r.Get("calibrating") == "1" || r.Get("calib_on") == "1");
            svcValue.Text = r.Ok ? ServiceInfo.Running : (service == ServiceInfo.Running ? "running, not responding" : service);
            Color bad = DesktopStyle.Dark ? Color.FromArgb(255, 120, 110) : Color.FromArgb(196, 43, 28);
            svcValue.ForeColor = running ? ForeColor : bad;
            svcValue.Font = running ? Font : values[8].Font;
            string action = ServiceInfo.Action(service);   // same rule as the menu
            bService.Text = action == ServiceInfo.ActionStart ? "Start service…" : "Stop service…";
            bService.Enabled = action != null && !(action == ServiceInfo.ActionStop && TrayContext.StopBlocked(r));
            svcWarn.Visible = !running;
            svcWarn.ForeColor = bad;
            svcWarn.Text = service == ServiceInfo.Absent
                ? "The service is not installed: the charge limit is not being maintained. Reinstall the application."
                : "Services stopped or unreachable: " + Paths.AppName + ". The charge limit is not being maintained.";
            if (!r.Ok)
            {
                for (int i = 0; i < values.Length; i++) values[i].Text = "—";
                values[12].Text = r.Message;
                bLimit.Enabled = bCal.Enabled = false;
                footer.Text = service == ServiceInfo.Stopped ? "Service stopped." : "Service unreachable.";
                return;
            }
            string[] v = StatusView.Values(r);
            for (int i = 0; i < values.Length; i++) values[i].Text = v[i];
            bLimit.Enabled = true;
            bLimit.Text = r.Get("limit_desired") == "on" ? "Disable 80% limit" : "Enable 80% limit";
            bCal.Enabled = calibrating || r.Get("calib_supported") != "0";
            bCal.Text = calibrating ? "Stop calibration" : "Start calibration…";
            footer.Text = "Service v" + r.Get("version") + " · updated at " + DateTime.Now.ToString("HH:mm:ss", Util.It)
                          + " · firmware: " + r.Get("fw_raw");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                fast.Dispose();
                values[8].Font.Dispose();
                Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
