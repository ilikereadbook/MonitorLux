using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Security.Principal;

namespace MonitorLux
{
    // ------------------------------------------------------------------
    // DDC/CI
    // ------------------------------------------------------------------
    public static class Ddc
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left; public int top; public int right; public int bottom; }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [Out] PHYSICAL_MONITOR[] arr);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorBrightness(IntPtr h, ref uint min, ref uint cur, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorBrightness(IntPtr h, uint val);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitor(IntPtr h);

        public static List<MonitorInfo> Enumerate()
        {
            List<MonitorInfo> found = new List<MonitorInfo>();
            MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d)
            {
                uint count = 0;
                if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref count) && count > 0)
                {
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    {
                        foreach (PHYSICAL_MONITOR pm in arr)
                        {
                            MonitorInfo mi = new MonitorInfo();
                            mi.Handle = pm.hPhysicalMonitor;
                            mi.Name = pm.szPhysicalMonitorDescription;
                            uint mn = 0, cu = 0, mx = 0;
                            if (GetMonitorBrightness(pm.hPhysicalMonitor, ref mn, ref cu, ref mx))
                            {
                                mi.Ok = true;
                                mi.Current = cu;
                                mi.Max = (mx > 0) ? mx : 100;
                                if (mx > 0 && mi.Current > mx) mi.Current = mx;
                            }
                            found.Add(mi);
                        }
                    }
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            return found;
        }

        public static void ReleaseAll(List<MonitorInfo> list)
        {
            if (list == null) return;
            foreach (MonitorInfo m in list)
            {
                if (m.Handle != IntPtr.Zero)
                {
                    try { DestroyPhysicalMonitor(m.Handle); } catch { }
                    m.Handle = IntPtr.Zero;
                }
            }
        }
    }

    public class MonitorInfo
    {
        public IntPtr Handle = IntPtr.Zero;
        public string Name = "";
        public uint Current = 50;
        public uint Max = 100;
        public bool Ok = false;
    }

    // ------------------------------------------------------------------
    // Brightness engine: cached handles, DDC/CI retry, failure reporting
    // ------------------------------------------------------------------
    public static class Brightness
    {
        private static List<MonitorInfo> cache = new List<MonitorInfo>();
        private static readonly object gate = new object();
        private static bool loaded = false;
        private static bool warned = false;

        public static int Value = 50;
        public static event Action<int> Changed;
        public static event Action Failed;

        public static void Reload()
        {
            lock (gate)
            {
                try { if (cache.Count > 0) Ddc.ReleaseAll(cache); } catch { }
                cache = Ddc.Enumerate();
                loaded = true;
                MonitorInfo r = Ref();
                if (r != null) Value = (int)r.Current;
            }
        }

        private static MonitorInfo Ref()
        {
            for (int i = 0; i < cache.Count; i++) if (cache[i].Ok) return cache[i];
            return null;
        }

        public static bool HasControl
        {
            get { lock (gate) { if (!loaded) Reload(); return Ref() != null; } }
        }

        public static List<MonitorInfo> Monitors
        {
            get { lock (gate) { if (!loaded) Reload(); return cache; } }
        }

        private static bool Write(uint v)
        {
            bool any = false;
            for (int i = 0; i < cache.Count; i++)
            {
                MonitorInfo m = cache[i];
                if (!m.Ok || m.Handle == IntPtr.Zero) continue;
                bool ok = false;
                for (int t = 0; t < 3 && !ok; t++)
                {
                    ok = Ddc.SetMonitorBrightness(m.Handle, v);
                    if (!ok) System.Threading.Thread.Sleep(35);
                }
                if (ok) { any = true; m.Current = v; }
            }
            return any;
        }

        public static bool Apply(int v)
        {
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            bool ok;
            lock (gate)
            {
                if (!loaded || cache.Count == 0) Reload();
                ok = Write((uint)v);
                if (!ok)
                {
                    Reload();
                    ok = Write((uint)v);
                }
                if (ok) Value = v;
            }
            if (ok)
            {
                if (Changed != null) Changed(Value);
            }
            else if (!warned)
            {
                warned = true;
                if (Failed != null) Failed();
            }
            return ok;
        }

        public static void Adjust(int delta) { Apply(Value + delta); }
    }

    // ------------------------------------------------------------------
    // On-screen display: layered capsule, click-through, auto fade out
    // ------------------------------------------------------------------
    public class Osd : Form
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp; public byte BlendFlags;
            public byte SourceConstantAlpha; public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dx, out uint dy);

        private const int FADE_IN = 110;
        private const int HOLD = 1150;
        private const int FADE_OUT = 230;

        private static Osd live;

        private int value = 50;
        private float scale = 1f;
        private int baseX, baseY, width, height;
        private long startTick;
        private System.Windows.Forms.Timer timer;
        private Bitmap buffer;
        private Bitmap backdrop;

        private Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(1, 1);
            Location = new Point(-20000, -20000);
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        private static long Now() { return DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; }

        public static void ShowValue(int v)
        {
            try
            {
                if (live == null || live.IsDisposed)
                {
                    live = new Osd();
                    live.Start(v);
                }
                else
                {
                    live.value = v;
                    live.startTick = Now() - FADE_IN;
                    live.Tick();
                }
            }
            catch { }
        }

        private void Start(int v)
        {
            value = v;
            Screen scr = null;
            try { scr = Screen.FromPoint(Cursor.Position); } catch { }
            if (scr == null) scr = Screen.PrimaryScreen;
            scale = ScaleOf(scr);

            width = (int)Math.Round(224 * scale);
            height = (int)Math.Round(64 * scale);
            Rectangle wa = scr.WorkingArea;
            baseX = wa.Right - width - (int)Math.Round(22 * scale);
            baseY = wa.Bottom - height - (int)Math.Round(22 * scale);

            buffer = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            CaptureBackdrop();
            Show();
            startTick = Now();
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 15;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        private void CaptureBackdrop()
        {
            try
            {
                Bitmap raw = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(raw))
                    g.CopyFromScreen(baseX, baseY, 0, 0, new Size(width, height));

                int sw = Math.Max(2, width / 9);
                int sh = Math.Max(2, height / 9);
                using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(raw, new Rectangle(0, 0, sw, sh));
                    }
                    raw.Dispose();
                    backdrop = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(backdrop))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(small, new Rectangle(0, 0, width, height));
                    }
                }
            }
            catch { backdrop = null; }
        }

        private static float ScaleOf(Screen scr)
        {
            try
            {
                Rectangle b = scr.Bounds;
                IntPtr hm = MonitorFromPoint(new POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, 2);
                if (hm != IntPtr.Zero)
                {
                    uint dx = 0, dy = 0;
                    if (GetDpiForMonitor(hm, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
                }
            }
            catch { }
            return 1f;
        }

        private void Tick()
        {
            long el = Now() - startTick;
            double alpha;
            int off = 0;
            if (el < FADE_IN)
            {
                double t = (double)el / FADE_IN;
                alpha = 1.0 - Math.Pow(1.0 - t, 3.0);
                off = (int)Math.Round((1.0 - alpha) * 7 * scale);
            }
            else if (el < FADE_IN + HOLD)
            {
                alpha = 1.0;
            }
            else if (el < FADE_IN + HOLD + FADE_OUT)
            {
                double t = (double)(el - FADE_IN - HOLD) / FADE_OUT;
                alpha = 1.0 - t;
            }
            else
            {
                Close2();
                return;
            }
            Render(alpha, off);
        }

        private void Render(double alpha, int offsetY)
        {
            if (buffer == null) return;
            Color fg = Color.FromArgb(243, 255, 255, 255);
            using (Graphics g = Graphics.FromImage(buffer))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                int r = (int)Math.Round(18 * scale);
                using (GraphicsPath p = Ui.Rounded(new Rectangle(0, 0, width - 1, height - 1), r))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(p, CombineMode.Replace);
                    if (backdrop != null) g.DrawImageUnscaled(backdrop, 0, 0);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(218, 14, 14, 18)))
                        g.FillPath(b, p);
                    g.Restore(st);
                    using (Pen pen = new Pen(Color.FromArgb(54, 255, 255, 255), Math.Max(1f, scale)))
                        g.DrawPath(pen, p);
                }

                int cx = (int)Math.Round(29 * scale);
                int cy = height / 2;
                float cr = 5.4f * scale;
                using (SolidBrush b = new SolidBrush(fg))
                    g.FillEllipse(b, cx - cr, cy - cr, cr * 2, cr * 2);
                using (Pen pen = new Pen(fg, Math.Max(1.1f, 1.5f * scale)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI / 4.0 * i;
                        double r1 = 8.2 * scale, r2 = 11.4 * scale;
                        g.DrawLine(pen,
                            (float)(cx + Math.Cos(a) * r1), (float)(cy + Math.Sin(a) * r1),
                            (float)(cx + Math.Cos(a) * r2), (float)(cy + Math.Sin(a) * r2));
                    }
                }

                int tx = (int)Math.Round(52 * scale);
                int tw = (int)Math.Round(108 * scale);
                int th = Math.Max(3, (int)Math.Round(5 * scale));
                int ty = cy - th / 2;
                using (GraphicsPath tp = Ui.Rounded(new Rectangle(tx, ty, tw, th), th / 2))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(52, 255, 255, 255)))
                    g.FillPath(b, tp);
                int fw = (int)Math.Round(tw * (value / 100.0));
                if (fw > 0)
                {
                    if (fw < th) fw = th;
                    using (GraphicsPath fp = Ui.Rounded(new Rectangle(tx, ty, fw, th), th / 2))
                    using (SolidBrush b = new SolidBrush(fg))
                        g.FillPath(b, fp);
                }

                string s = value + "%";
                using (Font f = new Font("Segoe UI", 17f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(246, 255, 255, 255)))
                {
                    SizeF sz = g.MeasureString(s, f);
                    float x = width - (float)(16 * scale) - sz.Width;
                    float y = (height - sz.Height) / 2f;
                    g.DrawString(s, f, b, x, y);
                }
            }
            Push(alpha, offsetY);
        }

        private void Push(double alpha, int offsetY)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBmp = buffer.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                SIZE size = new SIZE();
                size.cx = width;
                size.cy = height;
                POINT src = new POINT();
                src.X = 0;
                src.Y = 0;
                POINT dst = new POINT();
                dst.X = baseX;
                dst.Y = baseY + offsetY;
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = 0;
                bf.BlendFlags = 0;
                int a = (int)Math.Round(alpha * 255.0);
                if (a < 0) a = 0;
                if (a > 255) a = 255;
                bf.SourceConstantAlpha = (byte)a;
                bf.AlphaFormat = 1;   // AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                if (hBmp != IntPtr.Zero)
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBmp);
                }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void Close2()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; } } catch { }
            live = null;
            try { Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; } } catch { }
            try { if (buffer != null) { buffer.Dispose(); buffer = null; } } catch { }
            try { if (backdrop != null) { backdrop.Dispose(); backdrop = null; } } catch { }
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------
    // Global input: Ctrl+Alt+wheel anywhere, or wheel over the tray icon
    // ------------------------------------------------------------------
    public static class WinHook
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int X;
            public int Y;
            public int mouseData;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static IntPtr hook = IntPtr.Zero;
        private static HookProc proc;

        public static bool Installed { get { return hook != IntPtr.Zero; } }

        public static bool Install()
        {
            if (hook != IntPtr.Zero) return true;
            try
            {
                proc = new HookProc(Callback);
                IntPtr mod = GetModuleHandle(null);
                hook = SetWindowsHookEx(WH_MOUSE_LL, proc, mod, 0);
                if (hook == IntPtr.Zero)
                {
                    uint thread = GetWindowThreadId();
                    hook = SetWindowsHookEx(WH_MOUSE_LL, proc, IntPtr.Zero, thread);
                }
            }
            catch { }
            return hook != IntPtr.Zero;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        private static uint GetWindowThreadId() { return GetCurrentThreadId(); }

        public static void Uninstall()
        {
            try { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); } catch { }
            hook = IntPtr.Zero;
        }

        private static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
                {
                    MSLLHOOKSTRUCT d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    int raw = (d.mouseData >> 16) & 0xFFFF;
                    int notches = raw > 0 ? 1 : -1;
                    bool ctrlAlt = Down(VK_CONTROL) && Down(VK_MENU);
                    bool overTray = false;
                    if (!ctrlAlt)
                    {
                        try { overTray = TrayHit.Hit(new Point(d.X, d.Y)); } catch { }
                    }
                    if (ctrlAlt || overTray)
                    {
                        ScrollPump.Add(notches);
                        return (IntPtr)1;
                    }
                }
            }
            catch { }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    // coalesce rapid wheel events into one DDC write
    public static class ScrollPump
    {
        private const int STEP = 5;
        private static int pending = 0;
        private static System.Windows.Forms.Timer timer;

        public static void Add(int n) { Interlocked.Add(ref pending, n); }

        public static void Start()
        {
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 45;
            timer.Tick += delegate
            {
                int v = Interlocked.Exchange(ref pending, 0);
                if (v != 0) Brightness.Adjust(v * STEP);
            };
            timer.Start();
        }

        public static void Stop()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; } } catch { }
        }
    }

    // is the cursor on our tray icon?
    public static class TrayHit
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NID
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref NID id, out RECT r);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        private static IntPtr hwnd = IntPtr.Zero;
        private static uint uid = 0;

        public static bool Bound { get { return hwnd != IntPtr.Zero; } }

        public static void Bind(NotifyIcon ni)
        {
            try
            {
                BindingFlags f = BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo wf = typeof(NotifyIcon).GetField("window", f);
                if (wf != null)
                {
                    NativeWindow nw = wf.GetValue(ni) as NativeWindow;
                    if (nw != null) hwnd = nw.Handle;
                }
                FieldInfo idf = typeof(NotifyIcon).GetField("id", f);
                if (idf != null) uid = (uint)idf.GetValue(ni);
            }
            catch { }
        }

        private static Rectangle cached = Rectangle.Empty;
        private static long cachedAt = 0;

        private static bool TryGetRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                NID nid = new NID();
                nid.cbSize = Marshal.SizeOf(typeof(NID));
                nid.hWnd = hwnd;
                nid.uID = uid;
                RECT r;
                if (Shell_NotifyIconGetRect(ref nid, out r) == 0)
                {
                    rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                    rect.Inflate(4, 4);
                    return rect.Width > 0 && rect.Height > 0;
                }
            }
            catch { }
            return false;
        }

        public static bool Hit(Point p)
        {
            // the wheel hook runs on the input thread; cache the icon rect so we
            // never call into the shell on every single wheel event
            long now = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            if (cached.IsEmpty || now - cachedAt > 600)
            {
                cachedAt = now;
                Rectangle r;
                cached = TryGetRect(out r) ? r : Rectangle.Empty;
            }
            if (!cached.IsEmpty) return cached.Contains(p);
            return OnTaskbar(p);
        }

        private static bool OnTaskbar(Point p)
        {
            try
            {
                IntPtr h = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
                if (h == IntPtr.Zero) return false;
                for (int i = 0; i < 6 && h != IntPtr.Zero; i++)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                    GetClassName(h, sb, 256);
                    string c = sb.ToString();
                    if (c == "Shell_TrayWnd" || c == "NotifyIconOverflowWindow" ||
                        c == "TopLevelWindowForOverflowXamlIsland" || c == "TrayNotifyWnd" ||
                        c == "XamlExplorerHostIslandWindow")
                        return true;
                    h = GetAncestor(h, 1);   // GA_PARENT
                }
            }
            catch { }
            return false;
        }
    }

    // hidden window that owns the global hotkeys
    public class HotkeyWindow : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 1;
        private const uint MOD_CONTROL = 2;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int STEP = 5;
        public static bool UpOk = false;
        public static bool DownOk = false;

        public HotkeyWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
            Location = new Point(-20000, -20000);
            Opacity = 0.01;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
            UpOk = RegisterHotKey(Handle, 10, mods, VK_UP);
            DownOk = RegisterHotKey(Handle, 11, mods, VK_DOWN);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 10) Brightness.Adjust(STEP);
                else if (id == 11) Brightness.Adjust(-STEP);
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { UnregisterHotKey(Handle, 10); UnregisterHotKey(Handle, 11); } catch { }
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------
    // Theme (follows Windows light/dark)
    // ------------------------------------------------------------------
    public static class Theme
    {
        public static bool Dark = false;
        public static bool ForceDark = false;

        public static Color GlassTint;
        public static Color CardFill;
        public static Color CardBorder;
        public static Color CardShadow;
        public static Color TextPrimary;
        public static Color TextSecondary;
        public static Color Accent;
        public static Color AccentLight;
        public static Color TrackRest;
        public static Color HoverFill;
        public static Color ShadowColor;
        public static Color SepLine;

        public static void Update()
        {
            Dark = ForceDark || ReadSystemDark();
            if (Dark)
            {
                GlassTint     = Color.FromArgb(138, 20, 20, 24);
                CardFill      = Color.FromArgb(38, 255, 255, 255);
                CardBorder    = Color.FromArgb(34, 255, 255, 255);
                CardShadow    = Color.FromArgb(40, 0, 0, 0);
                TextPrimary   = Color.FromArgb(246, 246, 248);
                TextSecondary = Color.FromArgb(150, 150, 158);
                Accent        = Color.FromArgb(82, 196, 255);
                AccentLight   = Color.FromArgb(130, 215, 255);
                TrackRest     = Color.FromArgb(64, 255, 255, 255);
                HoverFill     = Color.FromArgb(30, 255, 255, 255);
                ShadowColor   = Color.FromArgb(150, 0, 0, 0);
                SepLine       = Color.FromArgb(26, 255, 255, 255);
            }
            else
            {
                GlassTint     = Color.FromArgb(104, 250, 250, 253);
                CardFill      = Color.FromArgb(122, 255, 255, 255);
                CardBorder    = Color.FromArgb(140, 255, 255, 255);
                CardShadow    = Color.FromArgb(22, 0, 0, 0);
                TextPrimary   = Color.FromArgb(26, 26, 30);
                TextSecondary = Color.FromArgb(108, 108, 116);
                Accent        = Color.FromArgb(0, 103, 192);
                AccentLight   = Color.FromArgb(64, 156, 232);
                TrackRest     = Color.FromArgb(52, 0, 0, 0);
                HoverFill     = Color.FromArgb(26, 0, 0, 0);
                ShadowColor   = Color.FromArgb(70, 0, 0, 0);
                SepLine       = Color.FromArgb(22, 0, 0, 0);
            }
        }

        private static bool ReadSystemDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }
    }

    public static class Fonts
    {
        public static Font Title = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        public static Font CardName = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        public static Font CardValue = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        public static Font Body = new Font("Microsoft YaHei UI", 9F);
        public static Font Small = new Font("Microsoft YaHei UI", 8.25F);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    public static class Ui
    {
        public static float S = 1f;

        public static int Px(float v) { return (int)Math.Round(v * S); }
        public static float Pxf(float v) { return v * S; }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void DrawSun(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = area.X + area.Width / 2f;
            float cy = area.Y + area.Height / 2f;
            float unit = Math.Min(area.Width, area.Height) / 2f;
            float sunR = unit * 0.42f;
            using (SolidBrush b = new SolidBrush(color))
                g.FillEllipse(b, cx - sunR, cy - sunR, sunR * 2f, sunR * 2f);
            using (Pen p = new Pen(color, Math.Max(1f, unit * 0.15f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 4.0 * i;
                    float x1 = cx + (float)(unit * 0.70f * Math.Cos(a));
                    float y1 = cy + (float)(unit * 0.70f * Math.Sin(a));
                    float x2 = cx + (float)(unit * 1.0f * Math.Cos(a));
                    float y2 = cy + (float)(unit * 1.0f * Math.Sin(a));
                    g.DrawLine(p, x1, y1, x2, y2);
                }
            }
        }

        public static void DrawRefreshGlyph(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = area.Width * 0.28f;
            RectangleF r = new RectangleF(area.X + pad, area.Y + pad, area.Width - pad * 2, area.Height - pad * 2);
            using (Pen p = new Pen(color, Math.Max(1.4f, area.Width * 0.085f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawArc(p, r, 40, 280);
            }
            // arrow head
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            double ang = 40 * Math.PI / 180.0;
            float hx = cx + (float)(r.Width / 2f * Math.Cos(ang));
            float hy = cy + (float)(r.Height / 2f * Math.Sin(ang));
            float hs = area.Width * 0.2f;
            using (SolidBrush b = new SolidBrush(color))
            {
                PointF[] pts = new PointF[] {
                    new PointF(hx - hs * 0.7f, hy - hs * 1.1f),
                    new PointF(hx + hs * 0.75f, hy - hs * 0.15f),
                    new PointF(hx - hs * 0.35f, hy + hs * 0.95f)
                };
                g.FillPolygon(b, pts);
            }
        }

        public static void DrawShadow(Graphics g, Rectangle panelRect, int radius)
        {
            int spread = Ui.Px(20);
            Rectangle outer = new Rectangle(panelRect.X - spread, panelRect.Y - spread,
                                            panelRect.Width + spread * 2, panelRect.Height + spread * 2);
            using (GraphicsPath op = Rounded(outer, radius + spread))
            using (GraphicsPath ip = Rounded(panelRect, radius))
            using (Region reg = new Region(op))
            {
                reg.Exclude(ip);
                GraphicsState st = g.Save();
                g.SetClip(reg, CombineMode.Replace);
                using (PathGradientBrush pgb = new PathGradientBrush(op))
                {
                    pgb.CenterPoint = new PointF(outer.X + outer.Width / 2f, outer.Y + outer.Height * 0.62f);
                    pgb.CenterColor = Theme.ShadowColor;
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, Theme.ShadowColor) };
                    g.FillRectangle(pgb, outer);
                }
                g.Restore(st);
            }
        }
    }

    // ------------------------------------------------------------------
    // Backdrop capture + blur (liquid glass)
    // ------------------------------------------------------------------
    public static class Glass
    {
        public static Bitmap Capture(Rectangle screenRect)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, screenRect.Width), Math.Max(1, screenRect.Height), PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(screenRect.X, screenRect.Y, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
                }
            }
            catch { }
            return bmp;
        }

        public static Bitmap Blur(Bitmap src, int radius)
        {
            int dw = Math.Max(1, src.Width / 4);
            int dh = Math.Max(1, src.Height / 4);
            Bitmap small = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, dw, dh));
            }
            BoxBlur(small, Math.Max(1, radius / 4));
            Vibrance(small, 1.42f);
            Bitmap outp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(outp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height));
            }
            small.Dispose();
            return outp;
        }

        // boost saturation/luminosity of the blurred backdrop (Apple-style vibrancy)
        private static void Vibrance(Bitmap bmp, float amount)
        {
            int w = bmp.Width, h = bmp.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int[] px = new int[w * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i++)
            {
                int v = px[i];
                int bb = v & 0xff, gg = (v >> 8) & 0xff, rr = (v >> 16) & 0xff, aa = (v >> 24) & 0xff;
                float lum = 0.299f * rr + 0.587f * gg + 0.114f * bb;
                int nr = (int)(lum + (rr - lum) * amount);
                int ng = (int)(lum + (gg - lum) * amount);
                int nb = (int)(lum + (bb - lum) * amount);
                nr = nr < 0 ? 0 : (nr > 255 ? 255 : nr);
                ng = ng < 0 ? 0 : (ng > 255 ? 255 : ng);
                nb = nb < 0 ? 0 : (nb > 255 ? 255 : nb);
                px[i] = (aa << 24) | (nr << 16) | (ng << 8) | nb;
            }
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
        }

        private static void BoxBlur(Bitmap bmp, int r)
        {
            int w = bmp.Width, h = bmp.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int[] px = new int[w * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            int[] tmp = new int[w * h];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = x + k;
                        if (xx < 0) xx = 0;
                        if (xx >= w) xx = w - 1;
                        int v = px[row + xx];
                        a += (v >> 24) & 0xff; rr += (v >> 16) & 0xff; gg += (v >> 8) & 0xff; bb += v & 0xff; c++;
                    }
                    tmp[row + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                }
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int a = 0, rr = 0, gg = 0, bb = 0, c = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = y + k;
                        if (yy < 0) yy = 0;
                        if (yy >= h) yy = h - 1;
                        int v = tmp[yy * w + x];
                        a += (v >> 24) & 0xff; rr += (v >> 16) & 0xff; gg += (v >> 8) & 0xff; bb += v & 0xff; c++;
                    }
                    px[y * w + x] = ((a / c) << 24) | ((rr / c) << 16) | ((gg / c) << 8) | (bb / c);
                }
            }
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
        }
    }

    // ------------------------------------------------------------------
    // The glass popup panel (fully owner drawn)
    // ------------------------------------------------------------------
    public class GlassPopup : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

        public static bool StaticPreview = false;

        private static Screen TargetScreen()
        {
            try { return StaticPreview ? Screen.PrimaryScreen : Screen.FromPoint(Cursor.Position); }
            catch { return Screen.PrimaryScreen; }
        }

        private static float DpiScaleFor(Point p)
        {
            try
            {
                IntPtr hmon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2);
                if (hmon != IntPtr.Zero)
                {
                    uint dx = 0, dy = 0;
                    if (GetDpiForMonitor(hmon, 0, out dx, out dy) == 0 && dx > 0)
                        return dx / 96f;
                }
            }
            catch { }
            return 1f;
        }

        private class Slot
        {
            public MonitorInfo Mon;
            public int Value;
            public Rectangle Track;      // exact track band
            public Rectangle Card;       // card rect
            public bool Dragging;
            public bool Hover;
        }

        private List<MonitorInfo> monitors = new List<MonitorInfo>();
        private List<Slot> slots = new List<Slot>();
        private Bitmap backdropRaw;
        private Bitmap backdropBlur;
        private Rectangle panelRect;
        private Rectangle refreshRect;
        private bool refreshHover;
        private int margin = 18;   // set in BuildLayout (DPI scaled)
        private float fade = 0f;
        private System.Windows.Forms.Timer anim;

        public bool PreviewMode = false;

        public GlassPopup()
        {
            Theme.Update();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = AppIcon.Small(); } catch { }

            // determine target monitor scale first, so size & position are consistent
            Rectangle wa0 = TargetScreen().WorkingArea;
            float sc = DpiScaleFor(new Point(wa0.Right - 12, wa0.Bottom - 12));
            if (sc <= 0f) sc = 1f;
            Ui.S = sc;
            BuildLayout();
        }

        // ---------------- layout ----------------
        private void BuildLayout()
        {
            LoadMonitors();

            margin = Ui.Px(20);
            int panelW = Ui.Px(338);
            int padX = Ui.Px(14);
            int cardH = Ui.Px(86);
            int gap = Ui.Px(10);
            int headerH = Ui.Px(34);
            int footerH = Ui.Px(24);

            int count = Math.Max(1, slots.Count);
            int panelH = headerH + Ui.Px(6) + count * (cardH + gap) - gap + footerH + Ui.Px(10);

            Size = new Size(panelW + margin * 2, panelH + margin * 2);
            panelRect = new Rectangle(margin, margin, panelW, panelH);

            slots.Clear();
            int y = panelRect.Y + headerH + Ui.Px(6);
            for (int i = 0; i < monitors.Count; i++)
            {
                Slot s = new Slot();
                s.Mon = monitors[i];
                s.Value = (int)monitors[i].Current;
                if (s.Value < 0) s.Value = 0;
                if (s.Value > 100) s.Value = 100;
                s.Card = new Rectangle(panelRect.X + padX, y, panelW - padX * 2, cardH);
                s.Track = new Rectangle(s.Card.X + Ui.Px(16), s.Card.Y + Ui.Px(44),
                                        s.Card.Width - Ui.Px(32), Ui.Px(30));
                slots.Add(s);
                y += cardH + gap;
            }
            refreshRect = new Rectangle(panelRect.Right - Ui.Px(12) - Ui.Px(30), panelRect.Y + Ui.Px(8), Ui.Px(30), Ui.Px(30));
        }

        private void LoadMonitors()
        {
            Ddc.ReleaseAll(monitors);
            monitors = Ddc.Enumerate();
            if (PreviewMode && monitors.Count == 0)
            {
                MonitorInfo a = new MonitorInfo();
                a.Name = "ViewSonic VX2780-2K-Pro"; a.Ok = true; a.Current = 62;
                MonitorInfo b = new MonitorInfo();
                b.Name = "内置显示器 (CMN1602)"; b.Ok = true; b.Current = 88;
                MonitorInfo c = new MonitorInfo();
                c.Name = "外接显示器 3"; c.Ok = false; c.Current = 50;
                monitors.Add(a); monitors.Add(b); monitors.Add(c);
            }
        }

        public void Prepare()
        {
            try
            {
                if (backdropRaw != null) backdropRaw.Dispose();
                if (backdropBlur != null) backdropBlur.Dispose();
                Rectangle r = new Rectangle(Location, Size);
                backdropRaw = Glass.Capture(r);
                backdropBlur = Glass.Blur(backdropRaw, Ui.Px(22));
            }
            catch { }
        }

        public void ShowNearTray()
        {
            Screen scr = TargetScreen();
            Rectangle wa = scr.WorkingArea;
            Point loc = new Point(wa.Right - Width - Ui.Px(10), wa.Bottom - Height - Ui.Px(10));
            if (loc.X < wa.Left) loc.X = wa.Left;
            if (loc.Y < wa.Top) loc.Y = wa.Top;
            Location = loc;
            Prepare();
            fade = 0.35f;
            Opacity = fade;
            Show();
            Activate();
            anim = new System.Windows.Forms.Timer();
            anim.Interval = 16;
            anim.Tick += delegate
            {
                fade += 0.14f;
                if (fade >= 1f)
                {
                    fade = 1f;
                    anim.Stop();
                    anim.Dispose();
                    anim = null;
                }
                Opacity = fade;
            };
            anim.Start();
        }

        // ---------------- painting ----------------
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 1) raw backdrop fills the whole window (keeps outer rim invisible)
            if (backdropRaw != null)
                g.DrawImage(backdropRaw, new Rectangle(0, 0, Width, Height));
            else
                g.Clear(Theme.Dark ? Color.FromArgb(32, 32, 34) : Color.FromArgb(243, 243, 245));

            int radius = Ui.Px(16);

            // 2) soft shadow around panel
            Ui.DrawShadow(g, panelRect, radius);

            // 3) glass body: clipped blurred backdrop + tint
            GraphicsState st = g.Save();
            using (GraphicsPath path = Ui.Rounded(panelRect, radius))
            {
                g.SetClip(path, CombineMode.Replace);
                if (backdropBlur != null)
                    g.DrawImage(backdropBlur, new Rectangle(0, 0, Width, Height));
                using (SolidBrush tint = new SolidBrush(Theme.GlassTint))
                    g.FillRectangle(tint, panelRect);

                // top specular highlight (liquid glass)
                Rectangle spec = new Rectangle(panelRect.X, panelRect.Y, panelRect.Width, Ui.Px(52));
                using (LinearGradientBrush lg = new LinearGradientBrush(spec,
                    Color.FromArgb(Theme.Dark ? 34 : 96, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, spec);

                // bottom subtle darkening for depth
                Rectangle bot = new Rectangle(panelRect.X, panelRect.Bottom - Ui.Px(60), panelRect.Width, Ui.Px(60));
                using (LinearGradientBrush lg = new LinearGradientBrush(bot,
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(Theme.Dark ? 42 : 20, 0, 0, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, bot);
            }
            g.Restore(st);

            // 4) border: crisp inner rim + outer rim
            using (GraphicsPath path = Ui.Rounded(panelRect, radius))
            {
                using (Pen p = new Pen(Color.FromArgb(Theme.Dark ? 60 : 150, 255, 255, 255), Ui.Pxf(1.1f)))
                    g.DrawPath(p, path);
                using (GraphicsPath inner = Ui.Rounded(new Rectangle(panelRect.X + 1, panelRect.Y + 1, panelRect.Width - 2, panelRect.Height - 2), radius - 1))
                using (Pen p = new Pen(Theme.Dark ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(22, 0, 0, 0), Ui.Pxf(1f)))
                    g.DrawPath(p, inner);
            }

            DrawHeader(g);
            foreach (Slot s in slots) DrawCard(g, s);
            DrawFooter(g);
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle sun = new Rectangle(panelRect.X + Ui.Px(16), panelRect.Y + Ui.Px(9), Ui.Px(20), Ui.Px(20));
            Ui.DrawSun(g, sun, Theme.Accent);

            Rectangle titleRect = new Rectangle(sun.Right + Ui.Px(9), panelRect.Y + Ui.Px(6), panelRect.Width - Ui.Px(90), Ui.Px(26));
            TextRenderer.DrawText(g, "\u5C4F\u5E55\u4EAE\u5EA6", Fonts.Title, titleRect, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            // refresh button
            if (refreshHover)
            {
                using (GraphicsPath p = Ui.Rounded(refreshRect, Ui.Px(8)))
                using (SolidBrush b = new SolidBrush(Theme.HoverFill))
                    g.FillPath(b, p);
            }
            Rectangle glyph = new Rectangle(refreshRect.X + Ui.Px(6), refreshRect.Y + Ui.Px(6), refreshRect.Width - Ui.Px(12), refreshRect.Height - Ui.Px(12));
            Ui.DrawRefreshGlyph(g, glyph, Theme.TextSecondary);
        }

        private void DrawCard(Graphics g, Slot s)
        {
            Rectangle c = s.Card;
            bool enabled = s.Mon != null && s.Mon.Ok;

            // card shadow
            using (GraphicsPath p = Ui.Rounded(new Rectangle(c.X, c.Y + Ui.Px(1), c.Width, c.Height), Ui.Px(10)))
            using (SolidBrush b = new SolidBrush(Theme.CardShadow))
                g.FillPath(b, p);

            using (GraphicsPath path = Ui.Rounded(c, Ui.Px(10)))
            {
                using (SolidBrush b = new SolidBrush(Theme.CardFill))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Theme.CardBorder, Ui.Pxf(1f)))
                    g.DrawPath(p, path);
            }

            // name
            Color nameColor = enabled ? Theme.TextPrimary : Theme.TextSecondary;
            Rectangle nameRect = new Rectangle(c.X + Ui.Px(16), c.Y + Ui.Px(11), c.Width - Ui.Px(90), Ui.Px(24));
            TextRenderer.DrawText(g, s.Mon.Name, Fonts.CardName, nameRect, nameColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // value
            Rectangle valRect = new Rectangle(c.Right - Ui.Px(74), c.Y + Ui.Px(9), Ui.Px(58), Ui.Px(26));
            string valText = enabled ? (s.Value + "%") : "--";
            TextRenderer.DrawText(g, valText, Fonts.CardValue, valRect,
                enabled ? Theme.Accent : Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            DrawTrack(g, s, enabled);
        }

        private void DrawTrack(Graphics g, Slot s, bool enabled)
        {
            Rectangle t = s.Track;
            int thick = Ui.Px(6);
            int thumbR = Ui.Px((s.Hover || s.Dragging) ? 10 : 9);
            int cy = t.Y + t.Height / 2;
            int left = t.X + thumbR;
            int right = t.Right - thumbR;
            float frac = s.Value / 100f;
            float tx = left + (right - left) * frac;

            // rest track
            using (Pen p = new Pen(enabled ? Theme.TrackRest : Color.FromArgb(Theme.Dark ? 40 : 26, 128, 128, 128), thick))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, left, cy, right, cy);
            }
            // filled portion
            if (enabled && tx > left + 0.5f)
            {
                using (Pen p = new Pen(Theme.Accent, thick))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, left, cy, tx, cy);
                }
                using (Pen p = new Pen(Color.FromArgb(70, Theme.AccentLight), Ui.Pxf(1.4f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, left, cy - Ui.Pxf(1.2f), tx, cy - Ui.Pxf(1.2f));
                }
            }

            // thumb
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 46, 0, 0, 0)))
                g.FillEllipse(b, tx - thumbR - Ui.Pxf(0.5f), cy - thumbR + Ui.Pxf(1.6f), thumbR * 2 + Ui.Pxf(1f), thumbR * 2 + Ui.Pxf(1f));

            Color face = Theme.Dark ? Color.FromArgb(250, 250, 252) : Color.White;
            using (SolidBrush b = new SolidBrush(enabled ? face : Color.FromArgb(210, 210, 214)))
                g.FillEllipse(b, tx - thumbR, cy - thumbR, thumbR * 2, thumbR * 2);

            if (enabled)
            {
                using (Pen p = new Pen(Theme.Accent, Ui.Pxf(2.2f)))
                    g.DrawEllipse(p, tx - thumbR + Ui.Pxf(1.1f), cy - thumbR + Ui.Pxf(1.1f), thumbR * 2 - Ui.Pxf(2.2f), thumbR * 2 - Ui.Pxf(2.2f));
            }
            else
            {
                using (Pen p = new Pen(Color.FromArgb(80, 128, 128, 128), Ui.Pxf(1.6f)))
                    g.DrawEllipse(p, tx - thumbR + Ui.Pxf(0.8f), cy - thumbR + Ui.Pxf(0.8f), thumbR * 2 - Ui.Pxf(1.6f), thumbR * 2 - Ui.Pxf(1.6f));
            }

            // hover ring
            if ((s.Hover || s.Dragging) && enabled)
            {
                using (Pen p = new Pen(Color.FromArgb(52, Theme.Accent), Ui.Pxf(1.6f)))
                    g.DrawEllipse(p, tx - thumbR - Ui.Pxf(3), cy - thumbR - Ui.Pxf(3), thumbR * 2 + Ui.Pxf(6), thumbR * 2 + Ui.Pxf(6));
            }
        }

        private void DrawFooter(Graphics g)
        {
            Rectangle line = new Rectangle(panelRect.X + Ui.Px(16), panelRect.Bottom - Ui.Px(30), panelRect.Width - Ui.Px(32), 1);
            using (SolidBrush b = new SolidBrush(Theme.SepLine))
                g.FillRectangle(b, line);

            string left = "\u5171 " + slots.Count + " \u53F0\u663E\u793A\u5668";  // 共 N 台显示器
            Rectangle lr = new Rectangle(panelRect.X + Ui.Px(16), panelRect.Bottom - Ui.Px(28), Ui.Px(160), Ui.Px(22));
            TextRenderer.DrawText(g, left, Fonts.Small, lr, Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            string right = "Esc \u5173\u95ED";  // Esc 关闭
            Rectangle rr = new Rectangle(panelRect.Right - Ui.Px(140), panelRect.Bottom - Ui.Px(28), Ui.Px(124), Ui.Px(22));
            TextRenderer.DrawText(g, right, Fonts.Small, rr, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        // ---------------- interaction ----------------
        private Slot SlotAt(Point p)
        {
            foreach (Slot s in slots)
            {
                Rectangle hit = s.Track;
                hit.Inflate(0, Ui.Px(12));
                if (hit.Contains(p)) return s;
            }
            return null;
        }

        private void SetFromMouse(Slot s, int x)
        {
            Rectangle t = s.Track;
            int thumbR = Ui.Px(9);
            int left = t.X + thumbR;
            int right = t.Right - thumbR;
            if (right <= left) return;
            float frac = (x - left) / (float)(right - left);
            if (frac < 0f) frac = 0f;
            if (frac > 1f) frac = 1f;
            int v = (int)Math.Round(frac * 100f);
            if (v == s.Value) return;
            s.Value = v;
            if (s.Mon != null && s.Mon.Ok)
            {
                Ddc.SetMonitorBrightness(s.Mon.Handle, (uint)v);
                s.Mon.Current = (uint)v;
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (refreshRect.Contains(e.Location))
            {
                BuildLayout();
                Prepare();
                Invalidate();
                return;
            }
            Slot s = SlotAt(e.Location);
            if (s != null && s.Mon != null && s.Mon.Ok)
            {
                s.Dragging = true;
                SetFromMouse(s, e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool needPaint = false;

            bool rh = refreshRect.Contains(e.Location);
            if (rh != refreshHover) { refreshHover = rh; needPaint = true; }

            Slot at = SlotAt(e.Location);
            foreach (Slot s in slots)
            {
                bool h = (s == at) && s.Mon != null && s.Mon.Ok;
                if (h != s.Hover) { s.Hover = h; needPaint = true; }
                if (s.Dragging) SetFromMouse(s, e.X);
            }
            Cursor = at != null || refreshHover ? Cursors.Hand : Cursors.Default;
            if (needPaint) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            foreach (Slot s in slots) s.Dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (slots.Count == 0) return;
            Slot s = SlotAt(PointToClient(Cursor.Position));
            if (s == null || s.Mon == null || !s.Mon.Ok) s = slots[0];
            if (s.Mon == null || !s.Mon.Ok) return;
            int nv = s.Value + (e.Delta > 0 ? 5 : -5);
            if (nv < 0) nv = 0;
            if (nv > 100) nv = 100;
            s.Value = nv;
            Ddc.SetMonitorBrightness(s.Mon.Handle, (uint)nv);
            s.Mon.Current = (uint)nv;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (slots.Count == 0) return;
            Slot s = SlotAt(PointToClient(Cursor.Position));
            if (s == null) s = slots[0];
            if (s.Mon == null || !s.Mon.Ok) return;
            int step = 0;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) step = -5;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) step = 5;
            if (step == 0) return;
            int nv = s.Value + step;
            if (nv < 0) nv = 0;
            if (nv > 100) nv = 100;
            s.Value = nv;
            Ddc.SetMonitorBrightness(s.Mon.Handle, (uint)nv);
            s.Mon.Current = (uint)nv;
            Invalidate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (anim != null) { anim.Stop(); anim.Dispose(); anim = null; }
            Ddc.ReleaseAll(monitors);
            if (backdropRaw != null) { backdropRaw.Dispose(); backdropRaw = null; }
            if (backdropBlur != null) { backdropBlur.Dispose(); backdropBlur = null; }
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------
    // Icon loading (multi-size, DPI aware)
    // ------------------------------------------------------------------
    public static class AppIcon
    {
        private static Icon small;
        private static Icon big;

        public static Icon Small()
        {
            if (small != null) return small;
            small = Load(SystemInformation.SmallIconSize);
            return small;
        }

        public static Icon Big()
        {
            if (big != null) return big;
            big = Load(new Size(32, 32));
            return big;
        }

        private static Icon Load(Size size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream st = asm.GetManifestResourceStream(name))
                        {
                            return new Icon(st, size);
                        }
                    }
                }
            }
            catch { }
            try { return new Icon(Icon.ExtractAssociatedIcon(Application.ExecutablePath), size); }
            catch { return SystemIcons.Application; }
        }
    }

    // ------------------------------------------------------------------
    // Autostart (scheduled task at logon -> silent, no UAC)
    // ------------------------------------------------------------------
    public static class Autostart
    {
        private const string TaskName = "MonitorLuxApp";

        private static int Run(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
            catch { return -1; }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MonitorLux";

        public static bool IsEnabled()
        {
            if (RunKeyExists()) return true;
            return Run("/query /tn " + TaskName) == 0;
        }

        public static bool Apply(bool on)
        {
            string exe = Application.ExecutablePath;
            if (on)
            {
                // preferred: plain HKCU Run entry - instant, no subprocess, no AV noise
                if (SetRunKey("\"" + exe + "\"", true)) return true;
                // fallback: logon scheduled task
                return Run("/create /tn " + TaskName + " /tr \"\\\"" + exe + "\\\"\" /sc onlogon /f") == 0;
            }
            SetRunKey(null, false);
            Run("/delete /tn " + TaskName + " /f");
            return true;
        }

        private static bool RunKeyExists()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        private static bool SetRunKey(string value, bool add)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (add) k.SetValue(RunValue, value);
                    else k.DeleteValue(RunValue, false);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------
    // Uninstall (called as MonitorLux.exe --uninstall)
    // ------------------------------------------------------------------
    public static class Uninstaller
    {
        public const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MonitorLux";

        public static void Run()
        {
            try { Autostart.Apply(false); } catch { }
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            }
            catch { }

            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string parent = Path.GetDirectoryName(dir);
            // if we live in ...\Programs\MonitorLux, remove that folder; otherwise keep it
            string target = dir;
            try
            {
                if (parent != null &&
                    string.Equals(Path.GetFileName(dir), "MonitorLux", StringComparison.OrdinalIgnoreCase))
                    target = dir;
            }
            catch { }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + target + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Tray application context
    // ------------------------------------------------------------------
    public class TrayContext : ApplicationContext
    {
        private NotifyIcon tray;
        private GlassPopup popup;
        private HotkeyWindow hotkeys;
        private long lastClose = 0;
        private bool balloonShown = false;

        public TrayContext()
        {
            tray = new NotifyIcon();
            try { tray.Icon = AppIcon.Small(); } catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "\u5C4F\u5E55\u4EAE\u5EA6";   // 屏幕亮度
            tray.Visible = true;

            Brightness.Changed += delegate(int v)
            {
                Osd.ShowValue(v);
                try { tray.Text = "\u5C4F\u5E55\u4EAE\u5EA6 " + v + "%"; } catch { }
            };
            Brightness.Failed += delegate
            {
                if (balloonShown) return;
                balloonShown = true;
                try
                {
                    tray.BalloonTipTitle = "\u5C4F\u5E55\u4EAE\u5EA6";
                    tray.BalloonTipText = "\u65E0\u6CD5\u63A7\u5236\u5C4F\u5E55\u4EAE\u5EA6\u3002\u8BD5\u8BD5\u53F3\u952E\u83DC\u5355\u91CC\u7684\u201C\u4EE5\u7BA1\u7406\u5458\u6743\u9650\u91CD\u542F\u201D\u3002";
                    tray.ShowBalloonTip(4000);
                }
                catch { }
            };

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem hint = new ToolStripMenuItem("Ctrl+Alt+\u6EDA\u8F6E / Ctrl+Alt+\u2191\u2193 \u8C03\u4EAE\u5EA6");
            hint.Enabled = false;
            menu.Items.Add(hint);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("\u6253\u5F00\u8C03\u8282\u9762\u677F", null, delegate { ShowPopup(true); });   // 打开调节面板
            menu.Items.Add("\u5237\u65B0\u663E\u793A\u5668", null, delegate { ShowPopup(true); });         // 刷新显示器
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem("\u5F00\u673A\u81EA\u542F");                    // 开机自启
            auto.Checked = Autostart.IsEnabled();
            auto.Click += delegate
            {
                bool target = !auto.Checked;
                if (Autostart.Apply(target)) auto.Checked = target;
            };
            menu.Items.Add(auto);

            menu.Items.Add("\u4EE5\u7BA1\u7406\u5458\u6743\u9650\u91CD\u542F", null, delegate   // 以管理员权限重启
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    tray.Visible = false;
                    Application.Exit();
                }
                catch { }
            });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("\u9000\u51FA", null, delegate {   // 退出
                tray.Visible = false;
                Application.Exit();
            });
            tray.ContextMenuStrip = menu;

            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePopup();
            };

            // read current brightness first so hotkeys/wheel have a sane starting point
            try { Brightness.Reload(); } catch { }

            // wheel over the tray icon + Ctrl+Alt+wheel anywhere
            TrayHit.Bind(tray);
            WinHook.Install();
            ScrollPump.Start();

            // global hotkeys
            try
            {
                hotkeys = new HotkeyWindow();
                hotkeys.Show();
            }
            catch { }
        }

        private void TogglePopup()
        {
            long now = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            if (popup != null && !popup.IsDisposed)
            {
                lastClose = now;
                popup.Close();
                popup = null;
                return;
            }
            if (now - lastClose < 260) return;   // eat the click that closed it
            ShowPopup(false);
        }

        private void ShowPopup(bool rebuild)
        {
            if (popup != null && !popup.IsDisposed)
            {
                popup.Close();
                popup = null;
                if (!rebuild) return;
            }
            GlassPopup p = new GlassPopup();
            p.FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                lastClose = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                if (popup == p) popup = null;
            };
            popup = p;
            p.ShowNearTray();
        }
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------
    public static class Program
    {
        private static Mutex single;

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest(); return; }
            if (args.Length > 0 && args[0] == "--preview") { Preview(false); return; }
            if (args.Length > 0 && args[0] == "--preview-dark") { Theme.ForceDark = true; Preview(true); return; }
            if (args.Length > 0 && args[0] == "--traytest") { TrayTest(); return; }
            if (args.Length > 0 && args[0] == "--osdtest") { OsdTest(); return; }
            if (args.Length > 0 && args[0] == "--uninstall") { Uninstaller.Run(); return; }
            if (args.Length > 0 && args[0] == "--autostart-on") { Autostart.Apply(true); return; }
            if (args.Length > 0 && args[0] == "--autostart-off") { Autostart.Apply(false); return; }

            // CLI: --set 20 / --step -5   (runs without the tray app being resident)
            if (args.Length >= 2 && (args[0] == "--set" || args[0] == "--step"))
            {
                int n;
                if (int.TryParse(args[1], out n))
                {
                    try { Brightness.Reload(); } catch { }
                    if (args[0] == "--set") Brightness.Apply(n);
                    else Brightness.Adjust(n);
                }
                return;
            }

            bool createdNew;
            single = new Mutex(true, "MonitorLuxApp_SingleInstance", out createdNew);
            if (!createdNew) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }

        // renders the OSD once and screenshots it, used to verify visuals
        private static void OsdTest()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Update();
            try { Brightness.Reload(); } catch { }

            Osd.ShowValue(20);

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 700;
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    Screen scr = Screen.FromPoint(Cursor.Position);
                    Rectangle wa = scr.WorkingArea;
                    int cw = 340, ch = 150;
                    Rectangle r = new Rectangle(wa.Right - cw, wa.Bottom - ch, cw, ch);
                    using (Bitmap bmp = new Bitmap(cw, ch))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(cw, ch));
                        bmp.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "_osd.png"),
                            ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "_osd_err.txt"),
                        ex.ToString());
                }
                Application.Exit();
            };
            t.Start();
            Application.Run();
        }

        private static bool IsAdmin()
        {
            try
            {
                WindowsIdentity wi = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // shows the panel, screenshots it, exits - used to verify visuals
        private static void Preview(bool dark)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // colorful test backdrop so the glass blur is visible
            Form bg = new Form();
            bg.FormBorderStyle = FormBorderStyle.None;
            bg.StartPosition = FormStartPosition.Manual;
            bg.ShowInTaskbar = false;
            int bw = Ui.Px(520), bh = Ui.Px(430);
            Rectangle wa0 = Screen.PrimaryScreen.WorkingArea;
            bg.Size = new Size(bw, bh);
            bg.Location = new Point(wa0.Right - bw, wa0.Bottom - bh);
            bg.Paint += delegate(object s2, PaintEventArgs e2)
            {
                Graphics g2 = e2.Graphics;
                Color tc1 = dark ? Color.FromArgb(255, 22, 40, 78) : Color.FromArgb(255, 255, 92, 138);
                Color tc2 = dark ? Color.FromArgb(255, 8, 12, 28) : Color.FromArgb(255, 22, 96, 220);
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, bw, bh), tc1, tc2, 35f))
                    g2.FillRectangle(lg, new Rectangle(0, 0, bw, bh));
                using (SolidBrush b1 = new SolidBrush(dark ? Color.FromArgb(150, 200, 140, 40) : Color.FromArgb(190, 255, 214, 92)))
                    g2.FillEllipse(b1, Ui.Px(40), Ui.Px(60), Ui.Px(210), Ui.Px(210));
                using (SolidBrush b2 = new SolidBrush(Color.FromArgb(170, 64, 232, 190)))
                    g2.FillEllipse(b2, Ui.Px(250), Ui.Px(20), Ui.Px(230), Ui.Px(230));
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g2.FillEllipse(b3, Ui.Px(150), Ui.Px(230), Ui.Px(300), Ui.Px(180));
                using (Font f2 = new Font("Segoe UI", 22F, FontStyle.Bold))
                using (SolidBrush b4 = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
                    g2.DrawString("GLASS TEST", f2, b4, Ui.Px(60), Ui.Px(300));
            };
            bg.Show();
            bg.Update();
            Application.DoEvents();
            System.Threading.Thread.Sleep(120);
            Application.DoEvents();

            GlassPopup.StaticPreview = true;
            GlassPopup p = new GlassPopup();
            p.PreviewMode = true;
            p.ShowNearTray();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1600;
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    Rectangle r = new Rectangle(p.Location, p.Size);
                    Rectangle scr0 = Screen.FromRectangle(r).Bounds;
                    r.Intersect(scr0);
                    System.IO.File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "_geom.txt"),
                        new string[] {
                            "FormBounds=" + p.Bounds,
                            "FormSize=" + p.Size,
                            "UiScale=" + Ui.S,
                            "Primary=" + Screen.PrimaryScreen.Bounds,
                            "CaptureRect=" + r
                        });
                    using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                        bmp.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
                            dark ? "_ui_dark.png" : "_ui_light.png"), ImageFormat.Png);
                    }
                }
                catch { }
                p.Close();
                Application.Exit();
            };
            t.Start();
            Application.Run();
        }

        // create the tray context, verify it builds, then exit
        private static void TrayTest()
        {
            List<string> lines = new List<string>();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Theme.Update();
                lines.Add("Theme ok, dark=" + Theme.Dark);
                Icon ic = AppIcon.Small();
                lines.Add("TrayIcon size=" + ic.Width + "x" + ic.Height);
                TrayContext ctx = new TrayContext();
                lines.Add("TrayContext created ok");
                lines.Add("Brightness.HasControl=" + Brightness.HasControl + " value=" + Brightness.Value);
                lines.Add("WinHook.Installed=" + WinHook.Installed);
                lines.Add("TrayHit.Bound=" + TrayHit.Bound);
                lines.Add("HotkeyUp=" + HotkeyWindow.UpOk + " HotkeyDown=" + HotkeyWindow.DownOk);
                lines.Add("AutostartEnabled=" + Autostart.IsEnabled());
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate { t.Stop(); Application.Exit(); };
                t.Start();
                Application.Run();
                lines.Add("Message loop exited cleanly");
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION: " + ex.ToString());
            }
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "_traytest.txt"), lines.ToArray());
        }

        public static void SelfTest()
        {
            List<string> lines = new List<string>();
            List<MonitorInfo> ms = Ddc.Enumerate();
            foreach (MonitorInfo m in ms)
                lines.Add(string.Format("Monitor=[{0}] Ok={1} cur={2}", m.Name, m.Ok, m.Current));
            Ddc.ReleaseAll(ms);
            if (lines.Count == 0) lines.Add("No monitors found.");
            lines.Add("DpiScale=" + Ui.S);
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            File.WriteAllLines(Path.Combine(dir, "_selftest.txt"), lines.ToArray());
        }
    }
}
