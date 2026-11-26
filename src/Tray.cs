// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// タスクトレイのアイコンとメニュー、ショートカットの受け取り
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
    // 複数のショートカットをまとめて登録する見えない窓（id = CaptureMode の番号 + 1）
    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<CaptureMode> Pressed;
        readonly List<int> registered = new List<int>();
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        public bool Register(CaptureMode mode, HotkeySpec h)
        {
            int id = (int)mode + 1;
            Unregister(id);
            if (h.IsNone) return true;
            bool ok = Native.RegisterHotKey(Handle, id, h.Mods | 0x4000, (uint)h.Key);
            if (ok) registered.Add(id);
            return ok;
        }
        void Unregister(int id) { if (registered.Remove(id)) Native.UnregisterHotKey(Handle, id); }
        public void UnregisterAll() { foreach (int id in registered.ToArray()) Unregister(id); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed((CaptureMode)((int)m.WParam - 1));
            base.WndProc(ref m);
        }
        public void Dispose() { UnregisterAll(); DestroyHandle(); }
    }

    // ShotCopy の窓がキーを受け取れないあいだだけ、Esc をどこで押しても受け取る見えない窓
    // （タイマーの数字が出ている間など。使い終わったらすぐ Dispose して、Esc をほかのアプリに返す）
    class EscKey : NativeWindow, IDisposable
    {
        public event Action Pressed;
        bool registered;
        public EscKey() : this(Keys.Escape) { }
        // ほかのキー（Enter など）を、同じように少しの間だけ受け取る
        public EscKey(Keys key)
        {
            CreateHandle(new CreateParams());
            registered = Native.RegisterHotKey(Handle, 1, 0x4000, (uint)key);   // 0x4000 = 押しっぱなしで何度も来ない
        }
        protected override void WndProc(ref Message m)
        {
            bool hit = m.Msg == 0x0312;
            base.WndProc(ref m);
            // 受け取った側でこの窓を消すことがあるので、窓の処理を済ませてから知らせる
            if (hit && Pressed != null) Pressed();
        }
        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (registered) { Native.UnregisterHotKey(Handle, 1); registered = false; }
            DestroyHandle();
        }
    }

    class TrayContext : ApplicationContext
    {
        NotifyIcon icon;
        HotkeyWindow hotkey;
        ToolStripMenuItem shotItem, fullItem, sameItem, timerItem, reEditItem, lastItem;
        SettingsForm settingsForm;
        bool capturing;
        Bitmap lastImage;          // 「最後の画像を保存」用
        string balloonPath;        // 通知をクリックしたときに開くファイル

        public TrayContext()
        {
            icon = new NotifyIcon();
            icon.Icon = AppIcon.Small;
            menu = new ContextMenuStrip();
            menu.Font = new Font("Yu Gothic UI", 9.5f);
            menu.ShowImageMargin = true;
            menu.ImageScalingSize = new Size(Ui.S(16), Ui.S(16));
            menu.Padding = new Padding(Ui.S(4), Ui.S(6), Ui.S(4), Ui.S(6));
            menu.HandleCreated += delegate { Native.RoundCorners(menu.Handle); };
            menu.Opening += delegate
            {
                ApplyMenuTheme();
                sameItem.Enabled = !CaptureOverlay.LastArea.IsEmpty;
                reEditItem.Enabled = LastSession.Available;
                timerItem.Text = "タイマーで撮る（" + Settings.TimerSeconds + "秒後）";
                foreach (ToolStripItem it in timerItem.DropDownItems)
                {
                    int s = (int)it.Tag;
                    it.Text = s + "秒後に撮る" + (s == Settings.TimerSeconds ? "（いまの設定）" : "");
                }
            };
            shotItem = MenuItem("範囲を選んで撮る", MenuIcon.Region, delegate { StartShot(300, CaptureMode.Select); });
            fullItem = MenuItem("画面全体を撮る", MenuIcon.Monitor, delegate { StartShot(300, CaptureMode.Full); });
            sameItem = MenuItem("前回と同じ範囲で撮る", MenuIcon.Same, delegate { StartShot(300, CaptureMode.Last); });
            sameItem.Enabled = false;
            // タイマーで撮る：右に出る一覧から秒数を選ぶと、その秒数で開始（次からもその秒数）
            timerItem = MenuItem("", MenuIcon.Timer, null);
            foreach (int sec in Settings.TimerChoices)
            {
                int s = sec;
                var it = new ToolStripMenuItem(s + "秒後に撮る", null, delegate { Settings.TimerSeconds = s; Settings.Save(); StartTimer(s); });
                it.Padding = new Padding(Ui.S(4), Ui.S(5), Ui.S(4), Ui.S(5));
                it.Tag = s;
                timerItem.DropDownItems.Add(it);
            }
            timerItem.DropDown.HandleCreated += delegate { Native.RoundCorners(timerItem.DropDown.Handle); };
            var scrollItem = MenuItem("つなげて撮る（スクロール）", MenuIcon.Scroll, delegate { StartScroll(); });
            reEditItem = MenuItem("最後の画像を書き直す", MenuIcon.ReEdit, delegate { StartReEdit(); });
            lastItem = MenuItem("最後の画像を保存", MenuIcon.Save, delegate { SaveLast(); });
            lastItem.Enabled = false;
            menu.Items.Add(shotItem);
            menu.Items.Add(fullItem);
            menu.Items.Add(sameItem);
            menu.Items.Add(timerItem);
            menu.Items.Add(scrollItem);
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(reEditItem);
            menu.Items.Add(lastItem);
            menu.Items.Add(MenuItem("設定...", MenuIcon.Gear, delegate { ShowSettings(); }));
            menu.Items.Add(MenuSeparator());
            menu.Items.Add(MenuItem("終了", MenuIcon.Exit, delegate { ExitThread(); }));
            ApplyMenuTheme();
            icon.ContextMenuStrip = menu;
            icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (ShotFlow.TimerActive) { ShotFlow.CancelTimer(); return; }   // タイマーで待っている間に押したら、やめる
                StartShot(200, CaptureMode.Select);
            };
            icon.BalloonTipClicked += delegate { if (balloonPath != null) ImageSaver.ShowInFolder(balloonPath); };
            icon.Visible = true;

            ShotFlow.ReEditRequested += StartReEdit;
            hotkey = new HotkeyWindow();
            hotkey.Pressed += delegate(CaptureMode m)
            {
                if (m == CaptureMode.Timer && ShotFlow.TimerActive) { ShotFlow.CancelTimer(); return; }
                StartShot(0, m);
            };
            string failed = ApplyHotkeys();
            if (failed.Length > 0)
            {
                string apps = failed.Contains("PrintScreen") ? string.Join("・", PrintScreenKey.RunningCandidates()) : "";
                Balloon(failed + " は他のアプリが使用中です" + (apps.Length > 0 ? "（" + apps + " の可能性）" : "") +
                    "。トレイのアイコンをクリックすればスクリーンショットを撮れます。右クリック →「設定...」でキーを変えられます。", ToolTipIcon.Warning, null);
            }
            else if (!Settings.HotkeySelect.IsNone)
                Balloon(Settings.HotkeySelect + " でスクリーンショットを撮れます（トレイのアイコンをクリックしても撮れます）。", ToolTipIcon.Info, null);
            else
                Balloon("トレイのアイコンをクリックすると、スクリーンショットを撮れます。", ToolTipIcon.Info, null);
        }

        ContextMenuStrip menu;
        readonly Dictionary<ToolStripMenuItem, MenuIcon> menuIcons = new Dictionary<ToolStripMenuItem, MenuIcon>();
        bool? menuDark;

        // 仕切り線（高さを奇数にして、線がちょうどまん中に来るようにする）
        static ToolStripSeparator MenuSeparator()
        {
            var s = new ToolStripSeparator();
            s.AutoSize = false;
            s.Height = Ui.S(9) | 1;
            s.Margin = Padding.Empty;
            return s;
        }

        ToolStripMenuItem MenuItem(string text, MenuIcon ic, EventHandler click)
        {
            var it = new ToolStripMenuItem(text, null, click);
            it.Padding = new Padding(Ui.S(4), Ui.S(5), Ui.S(4), Ui.S(5));
            it.ShowShortcutKeys = true;
            menuIcons[it] = ic;
            return it;
        }

        // Windows のライト／ダークに合わせて、メニューの色とアイコンを作り直す
        void ApplyMenuTheme()
        {
            bool dark = Native.SystemDark;
            if (menuDark.HasValue && menuDark.Value == dark) return;
            menuDark = dark;
            menu.Renderer = new ModernMenuRenderer(dark);
            timerItem.DropDown.Renderer = menu.Renderer;
            timerItem.DropDown.Padding = menu.Padding;
            timerItem.DropDown.Font = menu.Font;
            foreach (var kv in menuIcons)
            {
                if (kv.Key.Image != null) kv.Key.Image.Dispose();
                kv.Key.Image = ModernMenuRenderer.MakeIcon(kv.Value, Ui.S(16), dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(40, 40, 40));
            }
        }

        void Balloon(string text, ToolTipIcon kind, string path)
        {
            balloonPath = path;
            icon.ShowBalloonTip(4000, "ShotCopy", text, kind);
        }

        // 登録できなかったショートカットの名前を返す（すべて成功なら空）
        public string ApplyHotkeys()
        {
            var failed = new List<string>();
            if (!hotkey.Register(CaptureMode.Select, Settings.HotkeySelect)) failed.Add(Settings.HotkeySelect.ToString());
            if (!hotkey.Register(CaptureMode.Full, Settings.HotkeyFull)) failed.Add(Settings.HotkeyFull.ToString());
            if (!hotkey.Register(CaptureMode.Window, Settings.HotkeyWindow)) failed.Add(Settings.HotkeyWindow.ToString());
            if (!hotkey.Register(CaptureMode.Timer, Settings.HotkeyTimer)) failed.Add(Settings.HotkeyTimer.ToString());
            if (!hotkey.Register(CaptureMode.Last, Settings.HotkeyLast)) failed.Add(Settings.HotkeyLast.ToString());
            sameItem.ShortcutKeyDisplayString = Settings.HotkeyLast.IsNone ? "" : Settings.HotkeyLast.ToString();
            timerItem.ShortcutKeyDisplayString = Settings.HotkeyTimer.IsNone ? "" : Settings.HotkeyTimer.ToString();
            icon.Text = Settings.HotkeySelect.IsNone ? "ShotCopy" : "ShotCopy（" + Settings.HotkeySelect + " でスクリーンショット）";
            // 登録済みのショートカットはメニューの右側に表示
            shotItem.ShortcutKeyDisplayString = Settings.HotkeySelect.IsNone ? "" : Settings.HotkeySelect.ToString();
            fullItem.ShortcutKeyDisplayString = Settings.HotkeyFull.IsNone ? "" : Settings.HotkeyFull.ToString();
            return string.Join("、", failed.ToArray());
        }

        // 設定画面でキーを入力している間は、登録済みのショートカットを止めておく
        public void SuspendHotkeys() { hotkey.UnregisterAll(); }

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            SuspendHotkeys();
            settingsForm = new SettingsForm(this);
            settingsForm.FormClosed += delegate { ApplyHotkeys(); };
            settingsForm.Show();
            settingsForm.Activate();
        }

        void StartShot(int delayMs, CaptureMode mode)
        {
            if (capturing) return;
            capturing = true;
            ShotFlow.Begin(delayMs, mode, delegate { capturing = false; }, Tidy, KeepLast);
        }

        // 最後の画像を、書き込みを直せる状態で開き直す（コピー後の案内の「書き直す」からも呼ばれる）
        void StartReEdit()
        {
            if (capturing || ShotFlow.TimerActive || !LastSession.Available) return;
            capturing = true;
            ShotFlow.BeginReEdit(delegate { capturing = false; }, Tidy, KeepLast);
        }

        void StartTimer(int seconds)
        {
            if (ShotFlow.TimerActive) { ShotFlow.CancelTimer(); return; }
            if (capturing) return;
            capturing = true;
            ShotFlow.BeginTimer(seconds, delegate { capturing = false; }, Tidy, KeepLast);
        }

        // つなげて撮る：範囲を選んで、自分でスクロールしている間につなぐ
        void StartScroll()
        {
            if (capturing || ShotFlow.TimerActive) return;
            capturing = true;
            ShotFlow.BeginScroll(300, delegate { capturing = false; }, Tidy, KeepLast);
        }

        // 1 回の撮影が案内まで含めて終わったら、使い終わった画像（撮った画面の写し・モザイク・コピー用の写しなど）を片付ける。
        // 画像は Windows の外側のメモリーを使うので、.NET の自動の片付けを待つと、常駐している間に少しずつ増えることがある。
        // 「元に戻す」などで同じ画像を共有しているため、1 つずつ消すのではなく、使われなくなったものをまとめて片付けてもらう
        static void Tidy()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        void KeepLast(Bitmap img)
        {
            if (lastImage != null) lastImage.Dispose();
            lastImage = new Bitmap(img);
            lastItem.Enabled = true;
        }

        void SaveLast()
        {
            if (lastImage == null) return;
            try
            {
                string path = ImageSaver.Save(lastImage);
                Balloon("保存しました（クリックでフォルダを開く）\n" + path, ToolTipIcon.Info, path);
            }
            catch (Exception ex) { Balloon("保存できませんでした。\n" + ex.Message, ToolTipIcon.Warning, null); }
        }

        protected override void ExitThreadCore()
        {
            hotkey.Dispose();
            icon.Visible = false;
            icon.Dispose();
            if (lastImage != null) lastImage.Dispose();
            base.ExitThreadCore();
        }
    }

    enum MenuIcon { Region, Monitor, Same, Timer, Scroll, ReEdit, Save, Gear, Exit }

    // トレイの右クリックメニューを Windows 11 風に描く（角丸・広めの余白・ライト／ダーク対応）
    class ModernMenuRenderer : ToolStripRenderer
    {
        readonly Color bg, fg, sub, hover, sep, border;
        public ModernMenuRenderer(bool dark)
        {
            bg = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(28, 28, 28);
            sub = dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110);
            hover = dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(234, 234, 234);
            sep = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(224, 224, 224);
            border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(214, 214, 214);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        // 「タイマーで撮る ▸」の右の小さな三角を、文字と同じ色にする
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = sub;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Ui.Setup(e.Graphics);
            var r = new Rectangle(Ui.S(2), 1, e.Item.Width - Ui.S(4), e.Item.Height - 2);
            using (var b = new SolidBrush(hover)) Ui.FillRound(e.Graphics, b, r, Ui.S(5));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var mi = e.Item as ToolStripMenuItem;
            bool isShortcut = mi != null && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
            Color c = !e.Item.Enabled ? sub : isShortcut ? sub : fg;
            // 文字の行の高さではなく、項目の高さの上下まん中に置く（アイコンと高さをそろえる）
            var r = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
            var flags = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, c, flags);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // 上下の項目のちょうど中間に、左右の余白をそろえて引く
            int y = (e.Item.Height + 1) / 2;   // 文字の下の空きがわずかに小さいので、見た目のまん中に合わせて 1px 下げる
            using (var p = new Pen(sep)) e.Graphics.DrawLine(p, Ui.S(12), y, e.Item.Width - Ui.S(12) - 1, y);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            // アイコンも、項目の高さの上下まん中に置く
            var ir = e.ImageRectangle;
            var r = new Rectangle(ir.X, (e.Item.Height - e.Image.Height) / 2, e.Image.Width, e.Image.Height);
            if (e.Item.Enabled) e.Graphics.DrawImage(e.Image, r);
            else ControlPaint.DrawImageDisabled(e.Graphics, e.Image, r.X, r.Y, bg);
        }

        // メニュー用の小さな線画アイコン
        public static Bitmap MakeIcon(MenuIcon k, int size, Color c)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                Ui.Setup(g);
                var r = new Rectangle(0, 0, size, size);
                switch (k)
                {
                    case MenuIcon.Region: Icons.Region(g, r, c); break;
                    case MenuIcon.Monitor: Icons.Monitor(g, r, c); break;
                    case MenuIcon.Timer: Icons.Timer(g, r, c); break;
                    case MenuIcon.Scroll: Icons.Scroll(g, r, c); break;
                    case MenuIcon.ReEdit: using (var f = new Font("Yu Gothic UI", 9f, FontStyle.Bold)) Icons.Tool(g, Tool.Pen, r, c, f); break;
                    case MenuIcon.Same:
                        // 四隅のカギ＋中に小さな四角（同じ範囲）
                        Icons.Region(g, r, c);
                        using (var b = new SolidBrush(c)) g.FillRectangle(b, r.X + r.Width * 9f / 24f, r.Y + r.Height * 9f / 24f, r.Width * 6f / 24f, r.Height * 6f / 24f);
                        break;
                    case MenuIcon.Save: Icons.Download(g, r, c); break;
                    case MenuIcon.Gear: Icons.Gear(g, r, c); break;
                    case MenuIcon.Exit: Icons.Close(g, r, c); break;
                }
            }
            return bmp;
        }
    }
}
