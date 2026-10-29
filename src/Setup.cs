using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;

namespace MonitorLuxSetup
{
    public class SetupForm : Form
    {
        private const string AppName = "MonitorLux";

        private static bool Zh =
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

        private static string L(string zh, string en) { return Zh ? zh : en; }

        public static void ForceEnglish() { Zh = false; }
        private const string Version = "1.0.0";
        private const string PayloadName = "MonitorLux.exe";

        private float k = 1f;
        private CheckBox chkAuto;
        private CheckBox chkMenu;
        private Label lblStatus;
        private Button btnInstall;
        private Button btnCancel;
        private bool busy = false;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    "MonitorLux");
            }
        }

        public static float ScreenScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    if (s > 0.5f && s < 4f) return s;
                }
            }
            catch { }
            return 1f;
        }

        private int P(float v) { return (int)Math.Round(v * k); }
        public float ScaleFactor { get { return k; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int caption = 0x00FFFFFF;
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                int text = 0x0018181C;
                DwmSetWindowAttribute(Handle, 36, ref text, 4);
                int light = 0;
                DwmSetWindowAttribute(Handle, 20, ref light, 4);
            }
            catch { }
        }

        public SetupForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = AppName + L(" \u5B89\u88C5", " Setup");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            DoubleBuffered = true;

            IntPtr force = Handle;              // create handle so per-monitor DPI is known
            k = 1f;
            try
            {
                uint dpi = GetDpiForWindow(force);
                if (dpi > 48 && dpi < 400) k = dpi / 96f;
            }
            catch { k = ScreenScale(); }
            if (k <= 0.5f || k > 4f) k = 1f;
            ClientSize = new Size(P(470), P(336));
            Font = new Font("Microsoft YaHei UI", 9F);

            chkAuto = new CheckBox();
            chkAuto.Text = L("\u5F00\u673A\u81EA\u52A8\u542F\u52A8\uFF08\u9759\u9ED8\uFF0C\u4E0D\u5F39\u7A97\uFF09",
                "Start with Windows (silent, no popup)");
            chkAuto.Location = new Point(P(30), P(178));
            chkAuto.Size = new Size(P(340), P(24));
            chkAuto.Checked = true;
            Controls.Add(chkAuto);

            chkMenu = new CheckBox();
            chkMenu.Text = L("\u521B\u5EFA\u5F00\u59CB\u83DC\u5355\u5FEB\u6377\u65B9\u5F0F",
                "Create Start-menu shortcut");
            chkMenu.Location = new Point(P(30), P(206));
            chkMenu.Size = new Size(P(340), P(24));
            chkMenu.Checked = true;
            Controls.Add(chkMenu);

            lblStatus = new Label();
            lblStatus.Location = new Point(P(30), P(248));
            lblStatus.Size = new Size(P(410), P(24));
            lblStatus.ForeColor = Color.FromArgb(96, 96, 102);
            lblStatus.Text = "";
            Controls.Add(lblStatus);

            btnCancel = new Button();
            btnCancel.Text = L("\u53D6\u6D88", "Cancel");
            btnCancel.Location = new Point(P(268), P(288));
            btnCancel.Size = new Size(P(80), P(32));
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(212, 212, 218);
            btnCancel.BackColor = Color.White;
            btnCancel.ForeColor = Color.FromArgb(40, 40, 46);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            btnInstall = new Button();
            btnInstall.Text = L("\u5B89\u88C5", "Install");
            btnInstall.Location = new Point(P(358), P(288));
            btnInstall.Size = new Size(P(80), P(32));
            btnInstall.FlatStyle = FlatStyle.Flat;
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.BackColor = Color.FromArgb(0, 103, 192);
            btnInstall.ForeColor = Color.White;
            btnInstall.Click += delegate { DoInstall(); };
            Controls.Add(btnInstall);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            try
            {
                using (Stream st = LoadIconStream())
                {
                    if (st != null)
                    {
                        using (Icon ic = new Icon(st, P(48), P(48)))
                            g.DrawIcon(ic, new Rectangle(P(28), P(26), P(48), P(48)));
                    }
                }
            }
            catch { }

            using (Font f1 = new Font("Microsoft YaHei UI", 14.5F))
            using (SolidBrush b1 = new SolidBrush(Color.FromArgb(22, 22, 26)))
                g.DrawString(AppName, f1, b1, P(92), P(22));

            using (Font f2 = new Font("Microsoft YaHei UI", 9F))
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(114, 114, 122)))
                g.DrawString(L("Ctrl+Alt+\u6EDA\u8F6E\u3001\u6258\u76D8\u6EDA\u8F6E\u3001Ctrl+Alt+\u2191\u2193 \u968F\u65F6\u8C03\u4EAE\u5EA6",
                        "Ctrl+Alt+wheel \u00B7 tray wheel \u00B7 Ctrl+Alt+\u2191\u2193   adjust anytime"),
                    f2, b2, P(93), P(58));

            using (Pen p = new Pen(Color.FromArgb(238, 238, 242), Math.Max(1f, k)))
                g.DrawLine(p, P(30), P(98), P(440), P(98));

            using (Font f3 = new Font("Microsoft YaHei UI", 9F))
            using (SolidBrush b3 = new SolidBrush(Color.FromArgb(124, 124, 132)))
                g.DrawString(L("\u5B89\u88C5\u4F4D\u7F6E", "Install location"), f3, b3, P(30), P(116));

            using (Font f4 = new Font("Microsoft YaHei UI", 9.5F))
            using (SolidBrush b4 = new SolidBrush(Color.FromArgb(30, 30, 34)))
                g.DrawString(InstallDir, f4, b4, P(30), P(140));
        }

        private static Stream LoadIconStream()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(n);
            return null;
        }

        private void SetStatus(string s)
        {
            lblStatus.Text = s;
            Application.DoEvents();
        }

        private void DoInstall()
        {
            if (busy) return;
            busy = true;
            btnInstall.Enabled = false;
            btnCancel.Enabled = false;

            try
            {
                SetStatus(L("\u6B63\u5728\u505C\u6B62\u5DF2\u8FD0\u884C\u7684\u7A0B\u5E8F\u2026", "Stopping the running instance\u2026"));
                foreach (Process p in Process.GetProcessesByName("MonitorLux"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }

                SetStatus(L("\u6B63\u5728\u5199\u5165\u6587\u4EF6\u2026", "Writing files\u2026"));
                Directory.CreateDirectory(InstallDir);
                string target = Path.Combine(InstallDir, PayloadName);
                byte[] data = ReadPayload();
                if (data == null || data.Length < 1024)
                    throw new Exception(L("\u5B89\u88C5\u5305\u5185\u90E8\u6570\u636E\u7F3A\u5931\u3002", "Installer payload missing."));
                File.WriteAllBytes(target, data);

                if (chkMenu.Checked)
                {
                    SetStatus(L("\u6B63\u5728\u521B\u5EFA\u5FEB\u6377\u65B9\u5F0F\u2026", "Creating shortcuts\u2026"));
                    try
                    {
                        string menuDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
                        Directory.CreateDirectory(menuDir);
                        MakeShortcut(Path.Combine(menuDir, AppName + ".lnk"), target, InstallDir, target + ",0", AppName);
                        MakeShortcut(Path.Combine(menuDir, L("\u5378\u8F7D ", "Uninstall ") + AppName + ".lnk"),
                            target, InstallDir, target + ",0", L("\u5378\u8F7D ", "Uninstall ") + AppName);
                    }
                    catch { }
                }

                SetStatus(L("\u6B63\u5728\u6CE8\u518C\u5B89\u88C5\u4FE1\u606F\u2026", "Registering install info\u2026"));
                WriteUninstallEntry(target);

                SetStatus(L("\u6B63\u5728\u8BBE\u7F6E\u5F00\u673A\u81EA\u542F\u2026", "Enabling autostart\u2026"));
                SetAutostart(chkAuto.Checked, target);

                SetStatus(L("\u5B8C\u6210\uFF0C\u6B63\u5728\u542F\u52A8\u2026", "Done. Launching\u2026"));
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(target);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = InstallDir;
                    Process.Start(psi);
                }
                catch { }

                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 700;
                t.Tick += delegate { t.Stop(); Close(); };
                t.Start();
            }
            catch (Exception ex)
            {
                SetStatus(L("\u5B89\u88C5\u5931\u8D25\uFF1A", "Install failed: ") + ex.Message);
                btnInstall.Enabled = true;
                btnCancel.Enabled = true;
                busy = false;
            }
        }

        private static byte[] ReadPayload()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string res = null;
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("payload.exe", StringComparison.OrdinalIgnoreCase)) { res = n; break; }
            if (res == null) return null;
            using (Stream st = asm.GetManifestResourceStream(res))
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[81920];
                int r;
                while ((r = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, r);
                return ms.ToArray();
            }
        }

        private static void WriteUninstallEntry(string exe)
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorLux";
            using (Microsoft.Win32.RegistryKey kk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
            {
                if (kk == null) return;
                kk.SetValue("DisplayName", AppName);
                kk.SetValue("DisplayVersion", Version);
                kk.SetValue("Publisher", AppName);
                kk.SetValue("DisplayIcon", exe + ",0");
                kk.SetValue("InstallLocation", InstallDir);
                kk.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                kk.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall");
                kk.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                kk.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                long size = 0;
                try { size = new FileInfo(exe).Length / 1024; } catch { }
                kk.SetValue("EstimatedSize", (int)size, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        private static void SetAutostart(bool on, string exe)
        {
            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
            try
            {
                using (Microsoft.Win32.RegistryKey kk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(run, true))
                {
                    if (kk == null) return;
                    if (on) kk.SetValue("MonitorLux", "\"" + exe + "\"");
                    else kk.DeleteValue("MonitorLux", false);
                }
            }
            catch { }
        }

        private static void MakeShortcut(string lnk, string target, string workDir, string icon, string desc)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, new object[] { });
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--verify") { Verify(); return; }
            if (args.Length > 0 && args[0] == "--preview") { Preview(); return; }
            if (args.Length > 0 && args[0] == "--preview-en") { SetupForm.ForceEnglish(); Preview(); return; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        private static void Preview()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            try
            {
                SetupForm f = new SetupForm();
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(250);
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(Path.Combine(dir, "_setup_ui.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                File.WriteAllText(Path.Combine(dir, "_setup_prev.txt"),
                    "client=" + f.ClientSize.Width + "x" + f.ClientSize.Height + " k=" + f.ScaleFactor);
                f.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "_setup_prev.txt"), "EX: " + ex.ToString());
            }
        }

        private static void Verify()
        {
            string outFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "_setup_verify.txt");
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string res = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith("payload.exe", StringComparison.OrdinalIgnoreCase)) { res = n; break; }

                System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
                lines.Add("resources=" + string.Join(",", asm.GetManifestResourceNames()));
                lines.Add("payload=" + (res == null ? "MISSING" : res));
                if (res != null)
                {
                    using (Stream st = asm.GetManifestResourceStream(res))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[81920];
                        int r;
                        while ((r = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, r);
                        byte[] d = ms.ToArray();
                        lines.Add("size=" + d.Length);
                        bool isPe = d.Length > 0x40 && d[0] == 'M' && d[1] == 'Z';
                        lines.Add("isPE=" + isPe);
                        if (isPe)
                        {
                            int pe = BitConverter.ToInt32(d, 0x3C);
                            int sub = BitConverter.ToInt16(d, pe + 24 + 68);
                            lines.Add("subsystem=" + sub + " (2=GUI,3=console)");
                        }
                    }
                }
                lines.Add("installDir=" + SetupForm.InstallDir);
                lines.Add("scale=" + SetupForm.ScreenScale());
                File.WriteAllLines(outFile, lines.ToArray());
            }
            catch (Exception ex)
            {
                File.WriteAllText(outFile, "EXCEPTION: " + ex.ToString());
            }
        }
    }
}
