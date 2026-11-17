using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AcerBatteryControl
{
    static class DesktopStyle
    {
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string text);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static bool Dark
        {
            get { return ReadTheme("AppsUseLightTheme"); }
        }

        public static bool TaskbarDark { get { return ReadTheme("SystemUsesLightTheme"); } }

        static bool ReadTheme(string value)
        {
                if (SystemInformation.HighContrast) return false;
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return key != null && Convert.ToInt32(key.GetValue(value, 1)) == 0;
                }
                catch { return false; }
        }

        public static int IconSize()
        {
            try
            {
                uint dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
                int size = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi);
                if (size >= 16 && size <= 128) return size;
            }
            catch { }
            return Math.Max(16, SystemInformation.SmallIconSize.Width);
        }

        public static Font Font()
        {
            var font = new Font("Segoe UI Variable Text", 9f);
            if (font.Name == "Segoe UI Variable Text") return font;
            font.Dispose();
            return new Font("Segoe UI", 9f);
        }

        public static void Apply(Control control)
        {
            bool dark = Dark;
            ApplyColors(control, dark);
            var menu = control as ToolStrip;
            if (menu != null)
                menu.Renderer = SystemInformation.HighContrast ? (ToolStripRenderer)new ToolStripSystemRenderer()
                    : new ToolStripProfessionalRenderer(new MenuColors(dark));
            var form = control as Form;
            if (form == null || !form.IsHandleCreated) return;
            try
            {
                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref value, 4);
                value = SystemInformation.HighContrast ? 1 : 2;
                DwmSetWindowAttribute(form.Handle, 33, ref value, 4);
                value = SystemInformation.HighContrast ? 1 : 2; // DWMSBT_MAINWINDOW, if supported
                DwmSetWindowAttribute(form.Handle, 38, ref value, 4);
            }
            catch { } // Windows 10 and systems without DWM: standard controls
        }

        static void ApplyColors(Control control, bool dark)
        {
            control.BackColor = SystemInformation.HighContrast ? SystemColors.Window
                : dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(248, 248, 248);
            control.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText
                : dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(25, 25, 25);
            foreach (Control child in control.Controls) ApplyColors(child, dark);
        }

        sealed class MenuColors : ProfessionalColorTable
        {
            readonly bool dark;
            public MenuColors(bool useDark) { dark = useDark; UseSystemColors = false; }
            Color Background { get { return dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(248, 248, 248); } }
            public override Color ToolStripDropDownBackground { get { return Background; } }
            public override Color ImageMarginGradientBegin { get { return Background; } }
            public override Color ImageMarginGradientMiddle { get { return Background; } }
            public override Color ImageMarginGradientEnd { get { return Background; } }
            public override Color MenuBorder { get { return dark ? Color.FromArgb(70, 70, 70) : Color.Silver; } }
            public override Color MenuItemSelected { get { return dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(225, 225, 225); } }
            public override Color MenuItemBorder { get { return MenuItemSelected; } }
        }
    }

    // The same barrier covers worker queuing and subsequent execution on the UI thread.
    static class UiDispatch
    {
        public static bool Post(Action<Action> enqueue, Func<bool> closed, Action action)
        {
            if (closed()) return false;
            try
            {
                enqueue(delegate
                {
                    if (closed()) return;
                    try { action(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("UI: " + ex.Message); }
                });
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
    }
}
