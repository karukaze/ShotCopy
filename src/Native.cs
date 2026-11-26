// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// Windows の機能の呼び出しと、見た目の共通部品（拡大率・色・角丸）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShotCopy
{
    // ================= 共通 =================
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);

        // 画面に見えているウィンドウの範囲を、手前にあるものから順に返す（座標は仮想画面の左上が 0,0）
        public static List<Rectangle> VisibleWindowRects(Rectangle vs) { return VisibleWindowRects(vs, null); }

        // handles に、それぞれの範囲の窓のハンドルを同じ順番で入れる（null なら入れない）
        public static List<Rectangle> VisibleWindowRects(Rectangle vs, List<IntPtr> handles)
        {
            var list = new List<Rectangle>();
            uint myPid = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if (pid == myPid) return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // 非表示の仮想デスクトップなど
                    if ((GetWindowLong(h, -20) & 0x20) != 0) return true; // WS_EX_TRANSPARENT（クリック透過の飾り窓）
                    var sb = new System.Text.StringBuilder(64);
                    GetClassName(h, sb, 64);
                    string cls = sb.ToString();
                    if (cls == "Progman" || cls == "WorkerW")
                    {
                        // デスクトップ（アイコンが並ぶ窓）より奥の窓は見えていない。
                        // Win+D でデスクトップを表示したとき、最小化できない窓（設定画面など）はデスクトップの裏に残るため、ここで打ち切る
                        if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return false;
                        return true; // デスクトップは「画面全体」扱い
                    }
                    RECT r;
                    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r); // 影を除いた実際の枠
                    var rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    if (rc.Width < 20 || rc.Height < 20) return true;
                    // 最大化ウィンドウははみ出すので、そのモニターに収める
                    rc.Intersect(Screen.FromRectangle(rc).Bounds);
                    rc.Offset(-vs.X, -vs.Y);
                    if (rc.Width > 0 && rc.Height > 0) { list.Add(rc); if (handles != null) handles.Add(h); }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        // そのキー（またはマウスのボタン）が、いま押されているか
        public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // Ctrl 以外のキーかマウスのボタンが、どれか押されているか（何が押されたかは覚えない）
        public static bool OtherKeyDown()
        {
            for (int vk = 0x01; vk <= 0xFE; vk++)
            {
                if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) continue;   // Ctrl（左右とも）
                if (vk == 0x07 || (vk >= 0x0A && vk <= 0x0B) || vk == 0xFF) continue;
                // 日本語入力まわりのキー（半角/全角・かな・変換など）と CapsLock は数えない。
                // 半角/全角キーは「離した」知らせが来ず、押されたままに見えることがあるため
                if ((vk >= 0x14 && vk <= 0x19) || (vk >= 0x1C && vk <= 0x1F) || vk == 0xE5 || (vk >= 0xF0 && vk <= 0xF6)) continue;
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            }
            return false;
        }
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

        // この窓を、画面の取り込み（スクリーンショット）に写らないようにする（Windows 10 2004 以降。だめなら何もしない）
        public static void ExcludeFromCapture(IntPtr h) { try { SetWindowDisplayAffinity(h, 0x11); } catch { } }

        // 画面のその位置にある、いちばん外側の窓
        public static IntPtr TopWindowAt(Point screenPt)
        {
            IntPtr h = WindowFromPoint(screenPt);
            return h == IntPtr.Zero ? h : GetAncestor(h, 2);
        }
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        // 窓を確実に一番前（キーを受け取る状態）にする。
        // Windows は、しばらく操作されていないアプリが前に出るのを止めることがある（タイマー撮影のあとなど）。
        // いま前にいる窓の入力と一時的につなぐと、前に出られる。前に出られたら true
        public static bool ForceForeground(IntPtr h)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == h) return true;
            uint pid;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            return GetForegroundWindow() == h;
        }

        // 撮影のあとに戻る窓。トレイのアイコンから始めたときはタスクバーが前に出ているので、その下にある普通のアプリの窓を探す
        public static IntPtr ReturnWindow()
        {
            uint myPid = (uint)Process.GetCurrentProcess().Id;
            IntPtr fg = GetForegroundWindow();
            if (IsAppWindow(fg, myPid)) return fg;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsAppWindow(h, myPid)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static bool IsAppWindow(IntPtr h, uint myPid)
        {
            try
            {
                if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return false;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return false;
                if ((GetWindowLong(h, -20) & 0x80) != 0) return false;   // ツール窓
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
                var sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, 64);
                string cls = sb.ToString();
                return cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd" && cls != "NotifyIconOverflowWindow"
                    && cls != "Progman" && cls != "WorkerW" && cls != "TopLevelWindowForOverflowXamlIsland";
            }
            catch { return false; }
        }

        // ---------- マウスポインターを画像に描く（タイマー撮影の「ポインターも写す」） ----------
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int w, int hh, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        // 今のマウスポインターを、仮想画面の左上を (0,0) とする画像に描く
        public static void DrawCursor(Graphics g, Rectangle vs)
        {
            try
            {
                var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;   // 表示されていない
                ICONINFO ii;
                int hx = 0, hy = 0;
                if (GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                IntPtr hdc = g.GetHdc();
                try { DrawIconEx(hdc, ci.pt.X - vs.X - hx, ci.pt.Y - vs.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
                finally { g.ReleaseHdc(hdc); }
            }
            catch { }
        }
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        // Windows 11 なら窓の角を丸くする（それ以前の Windows では何もしない）
        public static void RoundCorners(IntPtr hWnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hWnd, 33, ref round, 4); } catch { }
        }

        // Windows のメニューなどがダークモードか
        public static bool SystemDark
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                        return v is int && (int)v == 0;
                    }
                }
                catch { return false; }
            }
        }

        // 前面のウィンドウの範囲（デスクトップやタスクバーなら空）
        public static Rectangle ForegroundWindowRect(Rectangle vs)
        {
            IntPtr h;
            return ForegroundWindowRect(vs, out h);
        }

        public static Rectangle ForegroundWindowRect(Rectangle vs, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            try
            {
                IntPtr h = GetForegroundWindow();
                handle = h;
                if (h == IntPtr.Zero || IsIconic(h)) return Rectangle.Empty;
                var sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, 64);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return Rectangle.Empty;
                RECT r;
                if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
                var rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                rc.Intersect(Screen.FromRectangle(rc).Bounds);
                rc.Offset(-vs.X, -vs.Y);
                return rc.Width > 10 && rc.Height > 10 ? rc : Rectangle.Empty;
            }
            catch { return Rectangle.Empty; }
        }

        // ---------- ウィンドウだけの中身を取り込む（前に重なっている窓を写さない） ----------
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

        // ウィンドウの見えている枠の範囲（画面の座標）の画像を返す。取り込めない・真っ黒のときは null
        public static Bitmap CaptureWindow(IntPtr h, out Rectangle frame, out bool roundCorners)
        {
            frame = Rectangle.Empty; roundCorners = false;
            try
            {
                if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return null;
                RECT wr; if (!GetWindowRect(h, out wr)) return null;
                RECT fr; if (DwmGetWindowAttribute(h, 9, out fr, Marshal.SizeOf(typeof(RECT))) != 0) fr = wr;
                var whole = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
                frame = Rectangle.FromLTRB(fr.Left, fr.Top, fr.Right, fr.Bottom);
                if (whole.Width < 10 || whole.Height < 10 || frame.Width < 10 || frame.Height < 10) return null;
                using (var all = new Bitmap(whole.Width, whole.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(all))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok;
                        try { ok = PrintWindow(h, hdc, 2); }   // PW_RENDERFULLCONTENT：今どきのアプリの中身も描く
                        finally { g.ReleaseHdc(hdc); }
                        if (!ok) return null;
                    }
                    var crop = new Rectangle(frame.X - whole.X, frame.Y - whole.Y, frame.Width, frame.Height);
                    crop.Intersect(new Rectangle(0, 0, all.Width, all.Height));
                    if (crop.Width < 10 || crop.Height < 10) return null;
                    var img = all.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    if (LooksBlank(img)) { img.Dispose(); return null; }
                    frame = new Rectangle(frame.X, frame.Y, crop.Width, crop.Height);
                    // Windows 11 の角が丸い窓（最大化していないとき）
                    roundCorners = Environment.OSVersion.Version.Build >= 22000 && !IsZoomed(h);
                    return img;
                }
            }
            catch { return null; }
        }

        // 真っ黒・真っ白一色など、取り込めていない画像か（ゲームや動画など対応していない窓）
        static bool LooksBlank(Bitmap b)
        {
            int n = 0, black = 0; Color first = b.GetPixel(b.Width / 2, b.Height / 2); bool same = true;
            for (int yi = 1; yi <= 9; yi++)
                for (int xi = 1; xi <= 9; xi++)
                {
                    Color c = b.GetPixel(b.Width * xi / 10, b.Height * yi / 10);
                    n++;
                    if (c.R + c.G + c.B < 12) black++;
                    if (c.ToArgb() != first.ToArgb()) same = false;
                }
            return black == n || (same && first.R + first.G + first.B < 12);
        }

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }

    static class Ui
    {
        public static readonly float Scale = GetScale();
        public static readonly FontFamily TextFamily = PickFamily();
        public static readonly Color Accent = Color.FromArgb(24, 95, 165);
        public static readonly Color AccentLight = Color.FromArgb(230, 241, 251);
        public static readonly Color[] Palette = {
            Color.FromArgb(226, 60, 58), Color.FromArgb(40, 120, 220), Color.FromArgb(70, 150, 40),
            Color.FromArgb(240, 170, 30), Color.FromArgb(40, 40, 40), Color.White };
        public static readonly string[] PaletteNames = { "赤", "青", "緑", "黄", "黒", "白" };

        static float GetScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }
        static FontFamily PickFamily()
        {
            foreach (string n in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS UI Gothic" })
            {
                try { return new FontFamily(n); } catch { }
            }
            return FontFamily.GenericSansSerif;
        }
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void FillRound(Graphics g, Brush b, RectangleF r, float rad) { using (var p = Round(r, rad)) g.FillPath(b, p); }
        public static void DrawRound(Graphics g, Pen pen, RectangleF r, float rad) { using (var p = Round(r, rad)) g.DrawPath(pen, p); }
        public static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
    }
}
