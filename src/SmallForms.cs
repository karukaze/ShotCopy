// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 小さな画面：タイマーの残り秒数、コピー後の案内
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
    // ================= コピー後の案内 =================
    // タイマー撮影の残り秒数（右下の丸い表示）。クリックはすり抜け、ほかの窓のメニューを閉じないよう前面にも出ない
    class CountdownForm : Form
    {
        int number;
        readonly Font numFont = new Font("Yu Gothic UI", 20f, FontStyle.Bold);
        readonly Font subFont = new Font("Yu Gothic UI", 11f);
        readonly Font keyFont = new Font("Yu Gothic UI", 9f);

        public CountdownForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(32, 32, 36);
            DoubleBuffered = true;
            Opacity = 0.94;
            // 横長の角丸：左に大きな数字（青い丸の中）、まん中に「秒後に撮ります」、右に「× やめる」
            int w = Ui.S(300), h = Ui.S(64);
            Size = new Size(w, h);
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - w - Ui.S(24), wa.Bottom - h - Ui.S(24));
            using (var p = Ui.Round(new RectangleF(0, 0, w, h), h / 2f)) Region = new Region(p);
            cancelRect = new Rectangle(w - Ui.S(104), Ui.S(13), Ui.S(90), h - Ui.S(26));
        }

        readonly Rectangle cancelRect;
        // 「× やめる」の画面上の位置（ここだけクリックを受け取る窓を重ねる）
        public Rectangle CancelScreenRect { get { return new Rectangle(Left + cancelRect.X, Top + cancelRect.Y, cancelRect.Width, cancelRect.Height); } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // クリック透過・ツール窓（タスクバーに出ない）・前面に出ない・常に手前
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        public void SetNumber(int n) { number = n; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Setup(g);
            int m = Ui.S(9), d = Height - m * 2;
            var circle = new Rectangle(m, m, d, d);
            using (var b = new SolidBrush(Color.FromArgb(74, 144, 226))) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, number.ToString(), numFont, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 「秒後に撮ります」と、その下に小さく「Ctrl ですぐ撮る」
            int tx = circle.Right + Ui.S(12), tw = cancelRect.Left - circle.Right - Ui.S(16);
            TextRenderer.DrawText(g, "秒後に撮ります", subFont, new Rectangle(tx, Ui.S(8), tw, Height / 2 - Ui.S(6)), Color.FromArgb(235, 235, 240), TextFormatFlags.Left | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, "Ctrl ですぐ撮る", keyFont, new Rectangle(tx, Height / 2 + Ui.S(2), tw, Height / 2 - Ui.S(8)), Color.FromArgb(175, 175, 185), TextFormatFlags.Left | TextFormatFlags.Top);
            // × やめる（範囲を選ぶ画面の「× やめる」と同じ見た目）
            using (var b = new SolidBrush(Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, cancelRect, Ui.S(8));
            int xs = Ui.S(13);
            var xr = new Rectangle(cancelRect.Left + Ui.S(12), cancelRect.Top + (cancelRect.Height - xs) / 2, xs, xs);
            Icons.Close(g, xr, Color.White);
            var xt = new Rectangle(xr.Right + Ui.S(4), cancelRect.Top, cancelRect.Right - xr.Right - Ui.S(8), cancelRect.Height);
            TextRenderer.DrawText(g, "やめる", subFont, xt, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { numFont.Dispose(); subFont.Dispose(); keyFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // 決まった場所だけクリックを受け取る、見えない小さな窓（前面に出ず、ほかの窓のメニューを閉じない）
    class ClickSpotForm : Form
    {
        public event Action Clicked;

        public ClickSpotForm(Rectangle screenRect)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.01;   // ほぼ透明（クリックは受け取れる）
            Bounds = screenRect;
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;   // ツール窓・前面に出ない・常に手前
                return cp;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && Clicked != null) Clicked();
        }
    }

    class ToastForm : Form
    {
        readonly Bitmap source;   // 保存用（閉じるまで ShotFlow が持っている）
        readonly Bitmap thumb;
        readonly bool ok;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Font titleFont = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        readonly Font subFont = new Font("Yu Gothic UI", 10f);
        readonly Font btnFont = new Font("Yu Gothic UI", 9.5f);
        public string SavedPath;
        string error;
        Rectangle btnRect, reRect;
        bool hoverBtn, hoverRe;
        public event Action ReEdit;      // 「書き直す」を押したとき
        readonly bool canReEdit;

        public ToastForm(Bitmap img, bool copied, Point near) : this(img, copied, near, false) { }

        // 色（Windows のライト／ダークに合わせる）
        readonly bool dark = Native.SystemDark;
        Color Bg { get { return dark ? Color.FromArgb(44, 44, 46) : Color.White; } }
        Color Ink { get { return dark ? Color.FromArgb(240, 240, 242) : Color.FromArgb(25, 25, 25); } }
        Color SubInk { get { return dark ? Color.FromArgb(185, 185, 190) : Color.FromArgb(70, 70, 70); } }
        Color Line { get { return dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(200, 200, 205); } }
        Color BtnHover { get { return dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(240, 240, 240); } }

        public ToastForm(Bitmap img, bool copied, Point near, bool reEditable)
        {
            source = img;
            ok = copied;
            canReEdit = reEditable && copied;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            // Windows 11 では角を丸くする（影も角に沿う）
            HandleCreated += delegate { Native.RoundCorners(Handle); };
            DoubleBuffered = true;
            Size = new Size(Ui.S(canReEdit ? 560 : 450), Ui.S(80));
            Rectangle wa = Screen.FromPoint(near).WorkingArea;
            Location = new Point(wa.Right - Width - Ui.S(16), wa.Bottom - Height - Ui.S(16));
            btnRect = new Rectangle(Width - Ui.S(16) - Ui.S(112), (Height - Ui.S(34)) / 2, Ui.S(112), Ui.S(34));
            reRect = canReEdit ? new Rectangle(btnRect.Left - Ui.S(8) - Ui.S(96), btnRect.Top, Ui.S(96), btnRect.Height) : Rectangle.Empty;

            int tw = Ui.S(64), th = Ui.S(46);
            float k = Math.Min((float)tw / img.Width, (float)th / img.Height);
            thumb = new Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(0, 0, thumb.Width, thumb.Height));
            }

            timer.Interval = Math.Max(1, Settings.ToastSeconds) * 1000;
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start();
            // マウスを乗せている間は消えない
            MouseEnter += delegate { timer.Stop(); };
            MouseLeave += delegate { hoverBtn = false; hoverRe = false; Invalidate(); timer.Interval = 2500; timer.Start(); };
        }

        // 設定の保存先に PNG で保存
        // 選んだ場所へ保存（「名前を付けて保存...」）
        public void SaveAs(string path)
        {
            if (SavedPath != null) return;
            try { ImageSaver.SaveTo(source, path); SavedPath = path; error = null; }
            catch (Exception ex) { error = ex.Message; }
            timer.Stop(); timer.Interval = 5000; timer.Start();
            Invalidate();
        }

        public void Save()
        {
            if (SavedPath != null) return;
            try { SavedPath = ImageSaver.Save(source); error = null; }
            catch (Exception ex) { error = ex.Message; }
            timer.Stop(); timer.Interval = 5000; timer.Start();
            Invalidate();
        }

        void OpenFolder()
        {
            ImageSaver.ShowInFolder(SavedPath);
            Close();
        }

        // 保存先を短く表示（標準なら「ピクチャ ＞ ShotCopy」）
        static string FolderLabel(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (string.Equals(dir, Settings.DefaultSaveFolder, StringComparison.OrdinalIgnoreCase)) return "ピクチャ ＞ ShotCopy";
            string name = Path.GetFileName(dir), parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
            return string.IsNullOrEmpty(parent) ? name : parent + " ＞ " + name;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = btnRect.Contains(e.Location), hr = reRect.Contains(e.Location);
            if (h != hoverBtn || hr != hoverRe) { hoverBtn = h; hoverRe = hr; Cursor = h || hr ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (btnRect.Contains(e.Location) && ok)
            {
                if (SavedPath == null) Save(); else OpenFolder();
            }
            else if (reRect.Contains(e.Location) && ReEdit != null) { Close(); ReEdit(); }
            else Close();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // NOACTIVATE | TOOLWINDOW | TOPMOST
                cp.ClassStyle |= 0x00020000;                          // CS_DROPSHADOW：周りに影を付けて画面から浮かせる
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Setup(g);
            using (var p = new Pen(Line, 1)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            // 左端の色の帯（成功は青、失敗は赤）
            bool failed = !ok || error != null;
            using (var b = new SolidBrush(failed ? Color.FromArgb(220, 60, 60) : Ui.Accent)) g.FillRectangle(b, 0, 0, Ui.S(5), Height);
            int tx = Ui.S(18), ty = (Height - thumb.Height) / 2;
            int boxX = tx + (Ui.S(64) - thumb.Width) / 2;
            g.DrawImage(thumb, new Rectangle(boxX, ty, thumb.Width, thumb.Height));
            using (var p = new Pen(Color.FromArgb(55, 138, 221), 1)) g.DrawRectangle(p, boxX, ty, thumb.Width - 1, thumb.Height - 1);
            int x = tx + Ui.S(64) + Ui.S(16);
            // 見出しの前の丸いマーク（成功は緑のチェック、失敗は赤の ×）
            int ms = Ui.S(18);
            var mark = new Rectangle(x, Ui.S(17), ms, ms);
            using (var b = new SolidBrush(failed ? Color.FromArgb(220, 60, 60) : Color.FromArgb(34, 160, 84))) g.FillEllipse(b, mark);
            using (var p = new Pen(Color.White, Ui.S(2.2f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (failed)
                {
                    g.DrawLine(p, mark.X + ms * 0.32f, mark.Y + ms * 0.32f, mark.X + ms * 0.68f, mark.Y + ms * 0.68f);
                    g.DrawLine(p, mark.X + ms * 0.68f, mark.Y + ms * 0.32f, mark.X + ms * 0.32f, mark.Y + ms * 0.68f);
                }
                else g.DrawLines(p, new[] { new PointF(mark.X + ms * 0.27f, mark.Y + ms * 0.52f), new PointF(mark.X + ms * 0.44f, mark.Y + ms * 0.68f), new PointF(mark.X + ms * 0.74f, mark.Y + ms * 0.34f) });
            }
            int titleX = mark.Right + Ui.S(7);
            string title, sub;
            if (!ok) { title = "コピーできませんでした"; sub = "もう一度撮ってください"; }
            else if (error != null) { title = "保存できませんでした"; sub = "コピーはできています"; }
            else if (SavedPath != null) { title = "保存しました"; sub = FolderLabel(SavedPath); }
            else { title = "コピーしました"; sub = "貼り付けたい場所で Ctrl+V"; }
            // 文字はボタンの手前までに収める（ボタンとの間はしっかり空ける）
            int textRight = (ok ? (canReEdit ? reRect.Left : btnRect.Left) - Ui.S(20) : Width - Ui.S(16));
            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(titleX, Ui.S(14), textRight - titleX, Ui.S(24)), Ink, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sub, subFont, new Rectangle(x, Ui.S(42), textRight - x, Ui.S(24)), SubInk, flags);

            if (!ok) return;
            // 「書き直す」：最後の画像を、書き込みを直せる状態で開き直す
            if (canReEdit)
            {
                using (var b = new SolidBrush(hoverRe ? BtnHover : Bg)) Ui.FillRound(g, b, reRect, Ui.S(6));
                using (var p = new Pen(Line, 1)) Ui.DrawRound(g, p, reRect, Ui.S(6));
                int rsz = Ui.S(15);
                var rir = new Rectangle(reRect.Left + Ui.S(12), reRect.Top + (reRect.Height - rsz) / 2, rsz, rsz);
                Icons.Tool(g, Tool.Pen, rir, SubInk, btnFont);
                TextRenderer.DrawText(g, "書き直す", btnFont, new Rectangle(rir.Right + Ui.S(4), reRect.Top, reRect.Right - rir.Right - Ui.S(8), reRect.Height),
                    Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            // 右側のボタン：保存 → 保存後は「フォルダを開く」
            bool saved = SavedPath != null;
            string label = saved ? "フォルダを開く" : "保存";
            if (saved)
            {
                if (hoverBtn) using (var b = new SolidBrush(dark ? Color.FromArgb(30, 50, 80) : Ui.AccentLight)) Ui.FillRound(g, b, btnRect, Ui.S(6));
                TextRenderer.DrawText(g, label, btnFont, btnRect, dark ? Color.FromArgb(110, 168, 255) : Ui.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                using (var b = new SolidBrush(hoverBtn ? BtnHover : Bg)) Ui.FillRound(g, b, btnRect, Ui.S(6));
                using (var p = new Pen(Line, 1)) Ui.DrawRound(g, p, btnRect, Ui.S(6));
                int isz = Ui.S(15);
                var ir = new Rectangle(btnRect.Left + Ui.S(26), btnRect.Top + (btnRect.Height - isz) / 2, isz, isz);
                Icons.Download(g, ir, SubInk);
                TextRenderer.DrawText(g, label, btnFont, new Rectangle(ir.Right + Ui.S(5), btnRect.Top, btnRect.Right - ir.Right - Ui.S(10), btnRect.Height),
                    Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); thumb.Dispose(); titleFont.Dispose(); subFont.Dispose(); btnFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
