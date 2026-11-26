// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// つなげて撮る（スクロールしながら長い 1 枚に）と、その確認画面
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
    // ================= つなげて撮る =================
    // 選んだ範囲を少しずつ撮り、画面が止まったところで前の画像との重なりを探して、下へつないでいく。
    // スクロールは利用者がする（ほかのアプリは操作しない）。範囲の上下に止まったままの帯（固定のヘッダーなど）は1回だけ写す
    class ScrollCapture
    {
        public event Action<Bitmap> Finished;   // できた画像（やめたときは null）
        readonly Rectangle area;
        readonly int W, H, maxH;
        ScrollFrameForm frame;
        ScrollPanelForm panel;
        EscKey escKey, enterKey;
        System.Windows.Forms.Timer timer;
        // つないだ画像は「上の帯」＋「スクロールする部分（canvas）」＋「下の帯」でできている。
        // canvas の中身は canvas の off 行目から contentH 行（上にも足せるように、上にも空きを持つ）
        Bitmap canvas; int off, contentH;
        Bitmap header, footer;   // 上下の止まった帯（なければ null）
        int T = -1, B;           // 帯の高さ（T が -1 のあいだは、まだ決まっていない）
        int pos;                 // いまの画面のスクロールする部分が、中身の何行目にあるか
        ulong[] lastRows;     // 最後につないだ画面の、行ごとの値
        // 止まっている間に変わったます目（動画・GIF アニメの場所）。重なりを比べるときに使わない
        bool[] viewAnim, lastAnim;
        ulong[] sampleRows;   // ひとつ前に撮った画面（スクロールが止まったかを見る）
        ulong[] failedRows;   // つなげなかった画面（同じ画面で何度も探さない）
        bool done;

        public ScrollCapture(Rectangle area)
        {
            this.area = area;
            W = area.Width; H = area.Height;
            // 長さの上限：2万ピクセル、または大きすぎる画像にならない長さ（約 4000 万ピクセル）
            maxH = Math.Max(H, Math.Min(20000, 40000000 / Math.Max(1, W)));
        }

        public void Start()
        {
            frame = new ScrollFrameForm(area);
            frame.Show();
            panel = new ScrollPanelForm(area);
            // ボタンの処理の中でパネルを閉じないよう、処理が終わってから呼ぶ
            panel.DoneClicked += delegate { panel.BeginInvoke(new Action(Finish)); };
            panel.CancelClicked += delegate { panel.BeginInvoke(new Action(Cancel)); };
            panel.Show();
            // つなげて撮っている間だけ、Esc（やめる）と Enter（ここまで）を受け取る
            escKey = new EscKey(Keys.Escape); escKey.Pressed += Cancel;
            enterKey = new EscKey(Keys.Enter); enterKey.Pressed += Finish;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 100;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            ShowStatus(0);
        }

        void Tick()
        {
            if (done) return;
            try
            {
                using (Bitmap cur = Grab())
                {
                    ulong[] rows = RowHashes(cur);
                    // 動画や GIF アニメが動いていても、まわりの大部分が止まっていれば「スクロールが止まった」とみなす
                    bool settled = sampleRows != null && MostlySame(rows, sampleRows);
                    // 止まっている間に変わったます目を覚えていく（スクロールしたら覚え直す）
                    viewAnim = settled ? AddAnim(viewAnim, rows, sampleRows) : null;
                    sampleRows = rows;
                    if (!settled) return;   // スクロールの途中は待つ
                    if (canvas == null) { First(cur, rows, viewAnim); ShowStatus(1); return; }
                    if (MostlySame(rows, lastRows)) { lastAnim = AddAnim(lastAnim, rows, lastRows); return; }
                    if (failedRows != null && MostlySame(rows, failedRows)) return;
                    ShowStatus(Append(cur, rows, viewAnim));
                }
            }
            catch (Exception ex)
            {
                End(null);
                MessageBox.Show("つなげて撮ることができませんでした。\n" + ex.Message, "ShotCopy");
            }
        }

        int TotalH { get { return contentH + Math.Max(0, T) + B; } }

        static bool[] AddAnim(bool[] acc, ulong[] a, ulong[] b)
        {
            if (acc == null || acc.Length != a.Length) acc = new bool[a.Length];
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) acc[i] = true;
            return acc;
        }

        void First(Bitmap cur, ulong[] rows, bool[] anim)
        {
            off = H * 2;
            canvas = new Bitmap(W, off + H * 3, PixelFormat.Format32bppRgb);
            CopyRows(cur, 0, off, H);
            contentH = H;
            pos = 0;
            lastRows = rows;
            lastAnim = anim;
        }

        // 1 = つないだ（またはもう撮った部分に戻った） ／ -1 = 重なりが見つからない ／ 2 = これ以上は長くできない
        int Append(Bitmap cur, ulong[] rows, bool[] anim)
        {
            // 横の区切りのうち、ほとんどの行で同じ位置のまま変わらない部分（横に並んだ動かない欄や、何もない余白）は比べない
            var use = new bool[SEG];
            int moving = 0;
            for (int s = 0; s < SEG; s++)
            {
                int same = 0;
                for (int y = 0; y < H; y++) if (rows[y * SEG + s] == lastRows[y * SEG + s]) same++;
                use[s] = same < H * 9 / 10;
                if (use[s]) moving++;
            }
            if (moving == 0) return 1;   // 変わっていない
            int top, bottom;
            if (T >= 0) { top = T; bottom = B; }
            else
            {
                // 最初にスクロールしたとき、上下の止まった帯の高さを決める
                int tol = Math.Min(SegTolerance, moving / 4);
                top = 0;
                while (top < H && RowEq(rows, top, lastRows, top, use, tol)) top++;
                if (top >= H) return 1;
                bottom = 0;
                while (bottom < H - top && RowEq(rows, H - 1 - bottom, lastRows, H - 1 - bottom, use, tol)) bottom++;
            }
            int mid = H - top - bottom;
            if (mid < 16) { failedRows = rows; return -1; }
            int minOverlap = Math.Min(16, Math.Max(1, mid / 4));
            // 重なる位置を探す（ずれの小さいほうから、下へ・上へ両方。少しの違いは許す）。dy が正なら下へ、負なら上へスクロールした
            // 合ったます目が多く、合わない割合が小さい位置を選ぶ（狭い重なりがたまたま合うより、広い重なりを信じる）
            int best = 0; double bestScore = 0;
            for (int d = 1; d <= mid - minOverlap; d++)
            {
                int n = mid - d;
                // 合わない割合は重く見る（1 行ずれただけの似た文章が、広い重なりで選ばれないように）
                double down = Overlaps(rows, top, lastRows, top + d, n, use, mid, anim, lastAnim);
                if (down >= 0) { double sc = n * Math.Pow(1 - down, 8); if (sc > bestScore) { best = d; bestScore = sc; } }
                double up = Overlaps(rows, top + d, lastRows, top, n, use, mid, anim, lastAnim);
                if (up >= 0) { double sc = n * Math.Pow(1 - up, 8); if (sc > bestScore) { best = -d; bestScore = sc; } }
                if (n <= bestScore) break;   // これより先は重なりが狭くなるだけなので、もっとよい位置はない
            }
            // 自信のない位置ではつながない（まちがってつなぐより、「少し戻して」と頼むほうがよい）
            if (best == 0 || bestScore < mid * 0.1) { failedRows = rows; return -1; }

            if (T < 0)
            {
                // 帯が決まったので、最初の画面から上下の帯を切り離す（中身はスクロールする部分だけにする）
                T = top; B = bottom;
                if (T > 0) header = CopyOut(canvas, off, T);
                if (B > 0) footer = CopyOut(canvas, off + H - B, B);
                off += T;
                contentH = mid;
                pos = 0;
            }
            int newPos = pos + best;
            if (newPos < 0)
            {
                // 上へ：まだ撮っていない部分が見えたので、上に足す
                int k = -newPos;
                if (TotalH + k > maxH) return 2;
                Grow(k, 0);
                off -= k;
                contentH += k;
                CopyRows(cur, top, off, k);
                newPos = 0;
            }
            else if (newPos + mid > contentH)
            {
                // 下へ：まだ撮っていない部分が見えたので、下に足す
                int k = newPos + mid - contentH;
                if (TotalH + k > maxH) return 2;
                Grow(0, k);
                CopyRows(cur, top + mid - k, off + contentH, k);
                contentH += k;
            }
            pos = newPos;
            lastRows = rows;
            lastAnim = anim;
            failedRows = null;
            return 1;
        }

        Bitmap CopyOut(Bitmap src, int y, int n)
        {
            var b = new Bitmap(W, n, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, W, n), new Rectangle(0, y, W, n), GraphicsUnit.Pixel);
            }
            return b;
        }

        // 1 行を横に SEG 個に分けて比べる。点滅するカーソルや小さな動く飾りがあっても、ほかの部分が同じなら同じ行とみなす
        const int SEG = 16, SegTolerance = 2;
        static readonly bool[] AllSeg = { true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true };

        // use の区切りだけを比べ、違いが tol 個までなら同じ行
        static bool RowEq(ulong[] a, int ra, ulong[] b, int rb, bool[] use, int tol)
        {
            int diff = 0, ia = ra * SEG, ib = rb * SEG;
            for (int s = 0; s < SEG; s++) if (use[s] && a[ia + s] != b[ib + s] && ++diff > tol) return false;
            return true;
        }

        static bool RowVaries(ulong[] a, int ra, bool[] use)
        {
            int i = ra * SEG, j = (ra - 1) * SEG;
            for (int s = 0; s < SEG; s++) if (use[s] && a[i + s] != a[j + s]) return true;
            return false;
        }

        // a の ai 行目からと b の bi 行目からの n 行を、行 × 横の区切りの小さなます目ごとに比べる。
        // 重なっていれば合わなかったます目の割合、重なっていなければ -1。動画や GIF アニメの部分は合わないので、ある程度の違いは許す
        // （重なりが広いときは 4 分の 1 まで、狭いときは 1 割まで）。全部が同じ色の行（何も書いていない部分）では位置を決められないので、重なっていないことにする
        // aa・ab は、それぞれの画面で動いていたます目（null なら、なし）。動いていたます目は比べない
        static double Overlaps(ulong[] a, int ai, ulong[] b, int bi, int n, bool[] use, int mid, bool[] aa, bool[] ab)
        {
            int segs = 0;
            for (int s = 0; s < SEG; s++) if (use[s]) segs++;
            long all = (long)n * segs;
            if (all == 0) return -1;
            bool wide = n * 4 >= mid;
            long maxMiss = wide ? all / 4 : all / 10;
            long miss = 0, total = 0;
            bool varied = false;
            for (int i = 0; i < n; i++)
            {
                int ia = (ai + i) * SEG, ib = (bi + i) * SEG;
                for (int s = 0; s < SEG; s++)
                {
                    if (!use[s] || (aa != null && aa[ia + s]) || (ab != null && ab[ib + s])) continue;
                    total++;
                    if (a[ia + s] != b[ib + s] && ++miss > maxMiss) return -1;
                }
                if (i > 0 && !varied && RowVaries(a, ai + i, use)) varied = true;
            }
            // 比べられたます目が少なすぎる（ほとんど動いている）なら、位置を決められない
            if (!varied || total * 2 < all) return -1;
            if (miss > (wide ? total / 4 : total / 10)) return -1;
            return (double)miss / total;
        }

        // 中身の上に addTop 行、下に addBottom 行を書ける空きを作る（足りなければ、余裕をもって大きくし直す）
        void Grow(int addTop, int addBottom)
        {
            if (off >= addTop && off + contentH + addBottom <= canvas.Height) return;
            int slack = Math.Max(H * 2, contentH / 2);
            int newOff = off >= addTop ? off : addTop + slack;
            int space = canvas.Height - off - contentH;
            int newHeight = newOff + contentH + (space >= addBottom ? space : addBottom + slack);
            var bigger = new Bitmap(W, newHeight, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bigger))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(canvas, new Rectangle(0, newOff, W, contentH), new Rectangle(0, off, W, contentH), GraphicsUnit.Pixel);
            }
            canvas.Dispose();
            canvas = bigger;
            off = newOff;
        }

        void CopyRows(Bitmap src, int srcY, int dstY, int n)
        {
            if (n <= 0) return;
            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, dstY, W, n), new Rectangle(0, srcY, W, n), GraphicsUnit.Pixel);
            }
        }

        Bitmap Grab()
        {
            var b = new Bitmap(W, H, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        // 行ごと・横の区切りごとの値（その部分の色の並びから作る。同じ並びなら同じ値）。y 行目の s 番目は [y * SEG + s]
        static ulong[] RowHashes(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            int segW = (w + SEG - 1) / SEG;
            var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var px = new int[w];
                var res = new ulong[h * SEG];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), px, 0, w);
                    for (int s = 0; s < SEG; s++)
                    {
                        ulong v = 14695981039346656037UL;
                        int x1 = Math.Min(w, (s + 1) * segW);
                        for (int x = s * segW; x < x1; x++) { v ^= (uint)(px[x] & 0xFFFFFF); v *= 1099511628211UL; }
                        res[y * SEG + s] = v;
                    }
                }
                return res;
            }
            finally { b.UnlockBits(data); }
        }

        // 2 つの画面が、動いている部分（動画・GIF アニメなど）を除いて同じか。
        // 何か書いてある行（すぐ上の行と違う行）のうち 6 割以上が同じ位置のままなら、スクロールしていないとみなす
        static bool MostlySame(ulong[] a, ulong[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int h = a.Length / SEG, info = 0, same = 0;
            for (int y = 1; y < h; y++)
            {
                if (!RowVaries(a, y, AllSeg)) continue;
                info++;
                if (RowEq(a, y, b, y, AllSeg, SegTolerance)) same++;
            }
            if (info < 8) return Same(a, b);   // ほとんど何も書いていない画面
            return same * 10 >= info * 6;
        }

        // 2 つの画面が（少しの違いを除いて）同じか
        static bool Same(ulong[] a, ulong[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int h = a.Length / SEG;
            for (int y = 0; y < h; y++) if (!RowEq(a, y, b, y, AllSeg, SegTolerance)) return false;
            return true;
        }

        void ShowStatus(int state)
        {
            if (panel == null || panel.IsDisposed) return;
            string len = canvas == null ? "" : "つないだ長さ：" + TotalH.ToString("N0") + " px";
            if (state == -1) panel.SetStatus("つなげません。少し戻してください", true, len);
            else if (state == 2) panel.SetStatus("これ以上は長くできません。「ここまで」を押してください", true, len);
            else panel.SetStatus("上か下へ、ゆっくりスクロールしてください", false, len);
        }

        void Finish()
        {
            if (done) return;
            try
            {
                // 最後のスクロールがまだつながっていなければ、いまの画面でもう一度つなぐ
                using (Bitmap cur = Grab())
                {
                    ulong[] rows = RowHashes(cur);
                    if (canvas == null) First(cur, rows, null);
                    else if (!MostlySame(rows, lastRows)) Append(cur, rows, MostlySame(rows, sampleRows) ? viewAnim : null);
                }
                End(Compose());
            }
            catch (Exception ex)
            {
                End(null);
                MessageBox.Show("つなげた画像を作れませんでした。\n" + ex.Message, "ShotCopy");
            }
        }

        // できあがりの画像：上の帯 ＋ 中身 ＋ 下の帯
        Bitmap Compose()
        {
            var result = new Bitmap(W, TotalH, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                int y = 0;
                if (header != null) { g.DrawImage(header, new Rectangle(0, 0, W, header.Height), new Rectangle(0, 0, W, header.Height), GraphicsUnit.Pixel); y = header.Height; }
                g.DrawImage(canvas, new Rectangle(0, y, W, contentH), new Rectangle(0, off, W, contentH), GraphicsUnit.Pixel);
                y += contentH;
                if (footer != null) g.DrawImage(footer, new Rectangle(0, y, W, footer.Height), new Rectangle(0, 0, W, footer.Height), GraphicsUnit.Pixel);
            }
            return result;
        }

        void Cancel() { End(null); }

        void End(Bitmap result)
        {
            if (done) { if (result != null) result.Dispose(); return; }
            done = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            if (escKey != null) { escKey.Dispose(); escKey = null; }
            if (enterKey != null) { enterKey.Dispose(); enterKey = null; }
            if (panel != null) { panel.Close(); panel.Dispose(); panel = null; }
            if (frame != null) { frame.Close(); frame.Dispose(); frame = null; }
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (header != null) { header.Dispose(); header = null; }
            if (footer != null) { footer.Dispose(); footer = null; }
            if (Finished != null) Finished(result);
        }
    }

    // つないだ画像を、コピーする前に確かめる画面。コピー後の案内やコピーの一覧と同じ見た目（角丸・影・ライト／ダーク）。
    // ホイール・ドラッグ・キーで全体を見られる。ボタンの名前とキーは「コピーして閉じる ▾」の一覧と同じ
    class ScrollPreviewForm : Form
    {
        public enum Choice { Cancel, Copy, CopyAndSave, Edit }
        public Choice Chosen = Choice.Cancel;

        readonly Bitmap img;
        readonly double scale;
        readonly int imgW, imgH;
        readonly Rectangle viewRect, closeRect;
        readonly Rectangle[] btnRects = new Rectangle[3];
        static readonly string[] BtnNames = { "コピーして閉じる", "コピーして、保存もする", "やめる" };
        static readonly string[] BtnKeys = { "Enter", "Ctrl+S", "Esc" };
        static readonly Choice[] BtnChoice = { Choice.Copy, Choice.CopyAndSave, Choice.Cancel };
        readonly Font titleFont = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
        readonly Font subFont = new Font("Yu Gothic UI", 9.5f);
        readonly Font btnFont = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
        readonly Font keyFont = new Font("Yu Gothic UI", 9f);
        int scroll, hover = -1;   // hover: 0..2 = ボタン、3 = ×、4 = つまみ
        bool dragThumb, dragWindow;
        int dragStartY, dragStartScroll;
        Point dragOffset;

        // 色（Windows のライト／ダークに合わせる。コピー後の案内と同じ）
        readonly bool dark = Native.SystemDark;
        Color Bg { get { return dark ? Color.FromArgb(44, 44, 46) : Color.White; } }
        Color Ink { get { return dark ? Color.FromArgb(240, 240, 242) : Color.FromArgb(25, 25, 25); } }
        Color SubInk { get { return dark ? Color.FromArgb(185, 185, 190) : Color.FromArgb(70, 70, 70); } }
        Color Line { get { return dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(200, 200, 205); } }
        Color BtnHover { get { return dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(240, 240, 240); } }
        Color ViewBg { get { return dark ? Color.FromArgb(30, 30, 32) : Color.FromArgb(240, 240, 243); } }

        public ScrollPreviewForm(Bitmap img, Rectangle near)
        {
            this.img = img;
            Text = "つないだ画像の確認 - ShotCopy";
            Icon = AppIcon.Small;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            BackColor = Bg;
            DoubleBuffered = true;
            HandleCreated += delegate { Native.RoundCorners(Handle); };

            Rectangle wa = Screen.FromRectangle(near).WorkingArea;
            int pad = Ui.S(16), headH = Ui.S(64), footH = Ui.S(68), barW = Ui.S(14);
            // 横幅は画面に収まる大きさまで（それより広い画像は縮めて見せる）
            int viewW = Math.Min(img.Width, Math.Min(Ui.S(900), wa.Width - Ui.S(120)));
            scale = Math.Min(1.0, (double)viewW / img.Width);
            imgW = Math.Max(1, (int)Math.Round(img.Width * scale));
            imgH = Math.Max(1, (int)Math.Round(img.Height * scale));
            int viewH = Math.Max(Ui.S(180), Math.Min(imgH, wa.Height - Ui.S(120) - headH - footH));

            // ボタンの幅（名前＋キー）
            int bh = Ui.S(40), gap = Ui.S(8);
            var bw = new int[3];
            int btnTotal = 0;
            for (int i = 0; i < 3; i++)
            {
                bw[i] = TextRenderer.MeasureText(BtnNames[i], btnFont).Width + TextRenderer.MeasureText(BtnKeys[i], keyFont).Width + Ui.S(40);
                btnTotal += bw[i] + (i > 0 ? gap : 0);
            }
            // 左端の「書き込む」（いつもの書き込みの道具で、矢印や番号を足せる）
            int editW = TextRenderer.MeasureText(EditText, btnFont).Width + Ui.S(52);
            int innerW = Math.Max(viewW + barW, editW + Ui.S(24) + btnTotal);
            ClientSize = new Size(pad * 2 + innerW, headH + viewH + footH);
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);

            viewRect = new Rectangle(pad, headH, innerW, viewH);
            closeRect = new Rectangle(ClientSize.Width - Ui.S(12) - Ui.S(32), Ui.S(14), Ui.S(32), Ui.S(32));
            int x = ClientSize.Width - pad, by = headH + viewH + (footH - bh) / 2;
            for (int i = 2; i >= 0; i--) { x -= bw[i]; btnRects[i] = new Rectangle(x, by, bw[i], bh); x -= gap; }
            editRect = new Rectangle(pad, by, editW, bh);
        }

        const string EditText = "書き込む";
        readonly Rectangle editRect;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW：周りに影を付けて画面から浮かせる
                return cp;
            }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

        int MaxScroll { get { return Math.Max(0, imgH - viewRect.Height); } }
        void ScrollTo(int y) { y = Math.Max(0, Math.Min(MaxScroll, y)); if (y != scroll) { scroll = y; Invalidate(); } }

        Rectangle ThumbRect
        {
            get
            {
                if (MaxScroll <= 0) return Rectangle.Empty;
                int track = viewRect.Height - Ui.S(8);
                int th = Math.Max(Ui.S(36), track * viewRect.Height / imgH);
                int ty = viewRect.Top + Ui.S(4) + (int)((long)(track - th) * scroll / MaxScroll);
                return new Rectangle(viewRect.Right - Ui.S(10), ty, Ui.S(6), th);
            }
        }

        void Choose(Choice c) { Chosen = c; Close(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Enter: Choose(Choice.Copy); return true;
                case Keys.Control | Keys.S: Choose(Choice.CopyAndSave); return true;
                case Keys.Escape: Choose(Choice.Cancel); return true;
                case Keys.Down: ScrollTo(scroll + Ui.S(40)); return true;
                case Keys.Up: ScrollTo(scroll - Ui.S(40)); return true;
                case Keys.PageDown: case Keys.Space: ScrollTo(scroll + viewRect.Height * 9 / 10); return true;
                case Keys.PageUp: ScrollTo(scroll - viewRect.Height * 9 / 10); return true;
                case Keys.Home: ScrollTo(0); return true;
                case Keys.End: ScrollTo(MaxScroll); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scroll - e.Delta * Ui.S(100) / 120);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < 3; i++) if (btnRects[i].Contains(p)) return i;
            if (editRect.Contains(p)) return 5;
            if (closeRect.Contains(p)) return 3;
            Rectangle t = ThumbRect;
            if (!t.IsEmpty && new Rectangle(t.X - Ui.S(6), viewRect.Top, t.Width + Ui.S(12), viewRect.Height).Contains(p)) return 4;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == 4)
            {
                // つまみの外（溝）を押したら、そこへ飛ぶ
                Rectangle t = ThumbRect;
                if (!t.Contains(e.Location)) ScrollTo((int)((long)(e.Y - viewRect.Top - t.Height / 2) * MaxScroll / Math.Max(1, viewRect.Height - t.Height)));
                dragThumb = true; dragStartY = e.Y; dragStartScroll = scroll; Capture = true;
            }
            else if (h < 0 && e.Y < viewRect.Top) { dragWindow = true; dragOffset = e.Location; Capture = true; }   // 上の帯をつかんで窓を動かす
            else if (h < 0 && viewRect.Contains(e.Location)) { dragThumb = false; dragStartY = e.Y; dragStartScroll = scroll; Capture = true; }   // 画像をつかんで動かす
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && dragWindow) { Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y); return; }
            if (Capture && dragThumb)
            {
                Rectangle t = ThumbRect;
                int track = viewRect.Height - Ui.S(8) - t.Height;
                ScrollTo(dragStartScroll + (int)((long)(e.Y - dragStartY) * MaxScroll / Math.Max(1, track)));
                return;
            }
            if (Capture && viewRect.Contains(new Point(viewRect.Left + 1, e.Y))) { ScrollTo(dragStartScroll - (e.Y - dragStartY)); return; }
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = (h >= 0 && h <= 3) || h == 5 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = Capture;
            Capture = false; dragThumb = false; dragWindow = false;
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h >= 0 && h <= 2) Choose(BtnChoice[h]);
            else if (h == 5) Choose(Choice.Edit);
            else if (h == 3) Choose(Choice.Cancel);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Setup(g);
            using (var p = new Pen(Line, 1)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            // 見出し：つなげて撮るのしるし＋「つないだ画像」＋大きさと使い方
            int pad = viewRect.Left, ms = Ui.S(32);
            var mark = new Rectangle(pad, Ui.S(16), ms, ms);
            using (var b = new SolidBrush(dark ? Color.FromArgb(30, 50, 80) : Ui.AccentLight)) Ui.FillRound(g, b, mark, Ui.S(8));
            Icons.Scroll(g, Rectangle.Inflate(mark, -Ui.S(6), -Ui.S(6)), dark ? Color.FromArgb(110, 168, 255) : Ui.Accent);
            int tx = mark.Right + Ui.S(12), tw = closeRect.Left - Ui.S(12) - tx;
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, "つないだ画像", titleFont, new Rectangle(tx, Ui.S(12), tw, Ui.S(24)), Ink, flags | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, img.Width + " × " + img.Height + " px ／ ホイールで全体を確かめられます", subFont, new Rectangle(tx, Ui.S(36), tw, Ui.S(20)), SubInk, flags);

            // × （やめる）
            if (hover == 3) using (var b = new SolidBrush(BtnHover)) Ui.FillRound(g, b, closeRect, Ui.S(8));
            int xs = Ui.S(14);
            Icons.Close(g, new Rectangle(closeRect.Left + (closeRect.Width - xs) / 2, closeRect.Top + (closeRect.Height - xs) / 2, xs, xs), SubInk);

            // 画像（見えている部分だけを元の画像から切り出して描く）
            using (var path = Ui.Round(viewRect, Ui.S(10)))
            {
                using (var b = new SolidBrush(ViewBg)) g.FillPath(b, path);
                var oldClip = g.Clip;
                g.SetClip(path);
                g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                int vh = Math.Min(viewRect.Height, imgH - scroll);
                int x = viewRect.Left + Math.Max(0, (viewRect.Width - Ui.S(14) - imgW) / 2);
                if (vh > 0)
                {
                    var src = new RectangleF(0, (float)(scroll / scale), img.Width, (float)(vh / scale));
                    g.DrawImage(img, new RectangleF(x, viewRect.Top, imgW, vh), src, GraphicsUnit.Pixel);
                }
                g.Clip = oldClip;
                oldClip.Dispose();
                using (var p = new Pen(Line, 1)) g.DrawPath(p, path);
            }
            // 細いスクロールのつまみ
            Rectangle t = ThumbRect;
            if (!t.IsEmpty)
                using (var b = new SolidBrush(hover == 4 || dragThumb ? (dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(120, 120, 126)) : (dark ? Color.FromArgb(110, 110, 116) : Color.FromArgb(175, 175, 180))))
                    Ui.FillRound(g, b, t, t.Width / 2);

            // 「書き込む」（左端。ペンの絵＋名前）
            using (var b = new SolidBrush(hover == 5 ? BtnHover : Bg)) Ui.FillRound(g, b, editRect, Ui.S(8));
            using (var p = new Pen(Line, 1)) Ui.DrawRound(g, p, editRect, Ui.S(8));
            int eis = Ui.S(18);
            var eir = new Rectangle(editRect.Left + Ui.S(14), editRect.Top + (editRect.Height - eis) / 2, eis, eis);
            Icons.Tool(g, Tool.Pen, eir, Ink, btnFont);
            TextRenderer.DrawText(g, EditText, btnFont, new Rectangle(eir.Right + Ui.S(6), editRect.Top, editRect.Right - eir.Right - Ui.S(12), editRect.Height), Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // ボタン（左が「コピーして閉じる」。名前の右に、うすくキーを書く）
            for (int i = 0; i < 3; i++)
            {
                var r = btnRects[i];
                bool primary = i == 0;
                Color fill = primary ? (hover == i ? Color.FromArgb(18, 78, 140) : Ui.Accent) : (hover == i ? BtnHover : Bg);
                using (var b = new SolidBrush(fill)) Ui.FillRound(g, b, r, Ui.S(8));
                if (!primary) using (var p = new Pen(Line, 1)) Ui.DrawRound(g, p, r, Ui.S(8));
                Size ns = TextRenderer.MeasureText(BtnNames[i], btnFont), ks = TextRenderer.MeasureText(BtnKeys[i], keyFont);
                int cx = r.Left + (r.Width - ns.Width - Ui.S(8) - ks.Width) / 2;
                TextRenderer.DrawText(g, BtnNames[i], btnFont, new Rectangle(cx, r.Top, ns.Width, r.Height), primary ? Color.White : Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, BtnKeys[i], keyFont, new Rectangle(cx + ns.Width + Ui.S(8), r.Top, ks.Width, r.Height), primary ? Color.FromArgb(215, 230, 250) : SubInk, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); subFont.Dispose(); btnFont.Dispose(); keyFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // つなげて撮る範囲のまわりの青い枠（クリックはすり抜ける。画面の取り込みには写らない）
    class ScrollFrameForm : Form
    {
        readonly int t = Ui.S(3);

        public ScrollFrameForm(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 0, 255);
            TransparencyKey = BackColor;
            Bounds = Rectangle.Inflate(area, t, t);   // 枠は範囲の外側に描く
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000 | 0x8 | 0x80000;   // クリック透過・ツール窓・前面に出ない・常に手前・重ね合わせ
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.ExcludeFromCapture(Handle); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Ui.Accent))
            {
                e.Graphics.FillRectangle(b, 0, 0, Width, t);
                e.Graphics.FillRectangle(b, 0, Height - t, Width, t);
                e.Graphics.FillRectangle(b, 0, 0, t, Height);
                e.Graphics.FillRectangle(b, Width - t, 0, t, Height);
            }
        }
    }

    // つなげて撮っている間の操作パネル（案内・いまの長さ・「ここまで」「やめる」）。前面に出ず、画面の取り込みには写らない
    class ScrollPanelForm : Form
    {
        public event Action DoneClicked, CancelClicked;
        readonly Font bold = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        readonly Font font = new Font("Yu Gothic UI", 10.5f);
        readonly Font sub = new Font("Yu Gothic UI", 9.5f);
        string status = "", length = "";
        bool warn;
        readonly Rectangle doneRect, cancelRect;
        int hover = -1;   // 0=ここまで 1=やめる
        const string DoneText = "ここまで (Enter)", CancelText = "やめる (Esc)";

        public ScrollPanelForm(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(32, 32, 36);
            DoubleBuffered = true;
            Opacity = 0.96;
            int h = Ui.S(68);
            int doneW = TextRenderer.MeasureText(DoneText, bold).Width + Ui.S(32);
            int cancelW = TextRenderer.MeasureText(CancelText, font).Width + Ui.S(46);
            int textW = TextRenderer.MeasureText("これ以上は長くできません。「ここまで」を押してください", bold).Width;
            int w = Ui.S(18) + textW + Ui.S(16) + doneW + Ui.S(8) + cancelW + Ui.S(12);
            Size = new Size(w, h);
            // 範囲にかからない場所に置く：下 → 上 → 右 → 左。どこにも入らなければ範囲の中の右下
            // （画面の取り込みには写らない。じゃまならドラッグで動かせる）
            Rectangle wa = Screen.FromRectangle(area).WorkingArea;
            int gap = Ui.S(12);
            int cx = Math.Max(wa.Left + gap, Math.Min(area.Left + (area.Width - w) / 2, wa.Right - w - gap));
            int cy = Math.Max(wa.Top + gap, Math.Min(area.Top + (area.Height - h) / 2, wa.Bottom - h - gap));
            Point loc;
            if (area.Bottom + gap + h <= wa.Bottom) loc = new Point(cx, area.Bottom + gap);
            else if (area.Top - gap - h >= wa.Top) loc = new Point(cx, area.Top - gap - h);
            else if (area.Right + gap + w <= wa.Right) loc = new Point(area.Right + gap, cy);
            else if (area.Left - gap - w >= wa.Left) loc = new Point(area.Left - gap - w, cy);
            else loc = new Point(Math.Min(area.Right, wa.Right) - w - gap, Math.Min(area.Bottom, wa.Bottom) - h - gap);
            Location = loc;
            using (var p = Ui.Round(new RectangleF(0, 0, w, h), Ui.S(14))) Region = new Region(p);
            int bh = h - Ui.S(24);
            cancelRect = new Rectangle(w - Ui.S(12) - cancelW, (h - bh) / 2, cancelW, bh);
            doneRect = new Rectangle(cancelRect.Left - Ui.S(8) - doneW, (h - bh) / 2, doneW, bh);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;   // ツール窓・前面に出ない（スクロールしている窓のまま）・常に手前
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.ExcludeFromCapture(Handle); }

        public void SetStatus(string s, bool isWarn, string len)
        {
            if (s == status && isWarn == warn && len == length) return;
            status = s; warn = isWarn; length = len;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Setup(g);
            int left = Ui.S(18), tw = doneRect.Left - Ui.S(16) - left;
            var r1 = new Rectangle(left, Ui.S(10), tw, Height / 2 - Ui.S(10));
            var r2 = new Rectangle(left, Height / 2, tw, Height / 2 - Ui.S(10));
            TextRenderer.DrawText(g, status, bold, r1, warn ? Color.FromArgb(255, 184, 77) : Color.White, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, length.Length > 0 ? length : "範囲の中をスクロールすると、自動でつなぎます", sub, r2, Color.FromArgb(200, 200, 206), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            using (var b = new SolidBrush(hover == 0 ? Color.FromArgb(18, 78, 140) : Ui.Accent)) Ui.FillRound(g, b, doneRect, Ui.S(8));
            TextRenderer.DrawText(g, DoneText, bold, doneRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using (var b = new SolidBrush(hover == 1 ? Color.FromArgb(95, 95, 102) : Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, cancelRect, Ui.S(8));
            int xs = Ui.S(13);
            var xr = new Rectangle(cancelRect.Left + Ui.S(12), cancelRect.Top + (cancelRect.Height - xs) / 2, xs, xs);
            Icons.Close(g, xr, Color.White);
            var xt = new Rectangle(xr.Right + Ui.S(4), cancelRect.Top, cancelRect.Right - xr.Right - Ui.S(10), cancelRect.Height);
            TextRenderer.DrawText(g, CancelText, font, xt, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int HitButton(Point p) { return doneRect.Contains(p) ? 0 : cancelRect.Contains(p) ? 1 : -1; }

        // ボタン以外のところをつかむと、パネルを動かせる
        bool dragging;
        Point dragOffset;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && HitButton(e.Location) < 0) { dragging = true; dragOffset = e.Location; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y); return; }
            int h = HitButton(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.SizeAll; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragging) { dragging = false; Capture = false; return; }
            int h = HitButton(e.Location);
            if (h == 0 && DoneClicked != null) DoneClicked();
            else if (h == 1 && CancelClicked != null) CancelClicked();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { bold.Dispose(); font.Dispose(); sub.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
