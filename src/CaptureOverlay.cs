// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 範囲を選ぶ画面と、書き込みの画面（全画面）
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
    // ================= 撮影・書き込み画面（全画面オーバーレイ） =================
    enum Tool { Pen, Marker, Arrow, Shape, Text, Mosaic }
    enum ItemKind { Grip, Tool, Color, Undo, Cancel, Copy, CopyMenu, MenuColor, MenuFill, MenuEdit, MenuFit, MenuWrap, MenuDelete, MenuUp, MenuDown }
    enum PopupKind { None, ColorSize, Fill, BoxKind, ArrowKind, PenKind, WrapKind, CopyKind, MosaicKind }

    // 一覧の中の押せる場所（Type: 0=色 1=太さ 2=塗り方 3=塗りの色 4=形 5=矢印の種類 6=ペンの種類）
    class PopHit
    {
        public Rectangle R; public int Type; public int Index;
        public PopHit(Rectangle r, int type, int index) { R = r; Type = type; Index = index; }
    }
    enum DragMode { None, Select, PendingDraw, Draw, Move, Resize, MoveToolbar, ResizeShape }

    class UiItem
    {
        public Rectangle R; public ItemKind Kind; public Tool Tool; public string Label;
        public UiItem(ItemKind k, Tool t, string label) { Kind = k; Tool = t; Label = label; }
    }

    class CaptureOverlay : Form
    {
        public Bitmap Result;
        public Point ResultScreenCenter;
        public static Rectangle LastArea = Rectangle.Empty;   // 前回コピーした範囲（アプリを閉じるまで覚える）

        Bitmap screen, dimmed;
        readonly Rectangle vs;

        // ---------- 長い画像に書き込む（つなげて撮った画像） ----------
        // 画面より縦に長い画像は、見えている部分だけを screen・dimmed に描き、ホイールで上下に動かす。
        // 動かすときは、範囲（sel）と書き込み（元に戻すの記録も）を一緒に動かすので、ほかの処理は画面の座標のまま使える。
        // 画像の点 c は、画面では c + (imgOffX, imgOffY) に見える
        readonly bool imageMode;
        readonly Bitmap image;          // 長い画像そのもの（ShotFlow が持っている。ここでは破棄しない）
        int imgOffX, imgOffY;
        Rectangle ImageRect { get { return new Rectangle(imgOffX, imgOffY, image.Width, image.Height); } }
        // 範囲を広げられる限界（ふだんは画面全体、長い画像では画像の大きさ）
        Rectangle SelLimit { get { return imageMode ? ImageRect : new Rectangle(0, 0, vs.Width, vs.Height); } }
        bool editing;                 // false: 範囲選択中 / true: 書き込み中
        Rectangle sel = Rectangle.Empty;

        List<Shape> shapes = new List<Shape>();
        readonly List<KeyValuePair<List<Shape>, Rectangle>> undo = new List<KeyValuePair<List<Shape>, Rectangle>>();
        Shape selected;
        Tool tool = Tool.Shape;
        Color color = Ui.Palette[0];
        BoxKind boxKind = BoxKind.Rounded;   // 最初は角丸（やさしい印象）
        ArrowKind arrowKind = ArrowKind.Arrow;

        // サブメニュー（形・矢印の種類）を持つ道具
        static bool HasSubmenu(Tool t) { return t == Tool.Shape || t == Tool.Arrow || t == Tool.Pen || t == Tool.Mosaic; }
        static PopupKind SubmenuOf(Tool t) { return t == Tool.Shape ? PopupKind.BoxKind : t == Tool.Arrow ? PopupKind.ArrowKind : t == Tool.Mosaic ? PopupKind.MosaicKind : PopupKind.PenKind; }
        int mosaicKind;    // モザイクの道具の種類：0 モザイク ／ 1 黒 ／ 2 グレー ／ 3 白で塗りつぶす
        bool mosaicFill { get { return mosaicKind > 0; } }
        static readonly Color[] FillColors = { Color.Empty, Color.FromArgb(24, 24, 24), Color.FromArgb(128, 128, 132), Color.White };
        // ペンと蛍光ペンは 1 つのボタン（Tool.Pen）にまとめる。最後に使った方を覚えておく
        Tool penTool = Tool.Pen;
        static bool IsPenGroup(Tool t) { return t == Tool.Pen || t == Tool.Marker; }
        int sizeLevel = 1;                   // 0:細/小 1:中 2:太/大

        DragMode drag = DragMode.None;
        Point downPt, lastPt;
        Rectangle selAtDown;
        int resizeHandle = -1;
        bool moved;
        Shape drawing;
        Rectangle mosaicPreview = Rectangle.Empty;

        TextBox textBox;
        TextShape editingText;
        Point textPt;

        readonly List<UiItem> toolbar = new List<UiItem>();
        readonly List<UiItem> menu = new List<UiItem>();
        Rectangle toolbarRect, menuRect, popupRect;
        PopupKind popup = PopupKind.None;
        bool popupForShape;
        readonly List<PopHit> popHits = new List<PopHit>();
        bool popupOpen
        {
            get { return popup != PopupKind.None; }
            set { if (!value) popup = PopupKind.None; }
        }
        UiItem hover;
        Rectangle lastUi = Rectangle.Empty;
        bool toolbarMoved;
        Point toolbarPos, toolbarAtDown;

        readonly Font uiFont = new Font("Yu Gothic UI", 10f);
        readonly Font uiBold = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
        readonly Font hintFont = new Font("Yu Gothic UI", 12f, FontStyle.Bold);

        // 太さ・大きさは3段階
        static readonly float[] StrokeLv = { 2.5f, 4f, 7f };
        static readonly float[] MarkerLv = { 12f, 18f, 26f };
        static readonly float[] TextLv = { 16f, 22f, 32f };
        static readonly float[] MosaicLv = { 8f, 12f, 18f };
        static readonly float[] StampLv = { 22f, 30f, 42f };
        float StampD { get { return Ui.S(StampLv[sizeLevel]); } }
        bool StampMode { get { return tool == Tool.Shape && (boxKind == BoxKind.Number || boxKind == BoxKind.Letter); } }
        // 文字を入れられる図形（角丸・四角・丸・吹き出し）を選んでいれば、その図形
        BoxShape SelectedCallout { get { var b = selected as BoxShape; return b != null && b.CanHoldText ? b : null; } }
        bool SizeIsStamp { get { return popupForShape ? selected is StampShape : StampMode; } }

        // 番号スタンプ・記号スタンプの最初の番号（1 なら 1, 2, 3… ／ A, B, C…。5 なら 5, 6, 7… ／ E, F, G…）
        readonly int[] stampStart = { 1, 1 };
        int StartOf(bool letter) { return stampStart[letter ? 1 : 0]; }

        // 次に置くスタンプの文字（今ある同じ種類のいちばん大きい番号の次。まだなければ最初の番号）
        string NextStampLabel(bool letter)
        {
            int max = StartOf(letter) - 1;
            foreach (var s in shapes)
            {
                var st = s as StampShape;
                if (st != null && st.Letter == letter) max = Math.Max(max, StampShape.IndexOf(st.Label, letter));
            }
            return StampShape.LabelOf(max + 1, letter);
        }

        // 同じ種類（数字／記号）のスタンプを、番号の順に並べた一覧
        List<StampShape> StampsInOrder(bool letter)
        {
            var list = new List<StampShape>();
            foreach (var s in shapes) { var st = s as StampShape; if (st != null && st.Letter == letter) list.Add(st); }
            // 番号順（同じ番号なら、先に置いた方が前）
            var order = new List<StampShape>(list);
            list.Sort(delegate(StampShape a, StampShape b)
            {
                int c = StampShape.IndexOf(a.Label, letter).CompareTo(StampShape.IndexOf(b.Label, letter));
                return c != 0 ? c : order.IndexOf(a).CompareTo(order.IndexOf(b));
            });
            return list;
        }

        // 並び順どおりに、最初の番号から 1 つずつふり直す（抜けをつめる）
        void Relabel(List<StampShape> list, bool letter)
        {
            int s = StartOf(letter);
            for (int i = 0; i < list.Count; i++) list[i].Label = StampShape.LabelOf(s + i, letter);
        }

        // 最初の番号を変える（もう置いてあるスタンプも、その番号から並べ直す）。元に戻す 1 回分
        void ChangeStampStart(bool letter, int delta)
        {
            int k = letter ? 1 : 0;
            int n = Math.Max(1, Math.Min(letter ? 26 : 999, stampStart[k] + delta));
            if (n == stampStart[k]) return;
            var list = StampsInOrder(letter);
            if (list.Count > 0) PushUndo();
            stampStart[k] = n;
            Relabel(list, letter);
        }

        void RenumberStamps(bool letter) { Relabel(StampsInOrder(letter), letter); }

        // スタンプの番号を n にする（その位置に割り込ませて、ほかをずらす）。元に戻す 1 回分
        public void SetStampNumber(StampShape st, int n)
        {
            var list = StampsInOrder(st.Letter);
            int cur = list.IndexOf(st);
            if (cur < 0) return;
            int to = Math.Max(0, Math.Min(list.Count - 1, n - StartOf(st.Letter)));
            if (to == cur) return;
            PushUndo();
            list.RemoveAt(cur);
            list.Insert(to, st);
            Relabel(list, st.Letter);
        }

        // ↑／↓：番号を 1 つ後ろ（増える）／前（減る）と入れ替える
        public void MoveStampNumber(StampShape st, int delta)
        {
            var list = StampsInOrder(st.Letter);
            int cur = list.IndexOf(st);
            if (cur < 0) return;
            int to = cur + delta;
            // 1 つしかないときは、最初の番号を変えて、そのスタンプの番号を増やす・減らす
            if (to < 0 || to >= list.Count)
            {
                if (list.Count == 1) { ChangeStampStart(st.Letter, delta); RelayoutUi(); Redraw(); }
                return;
            }
            PushUndo();
            list[cur] = list[to]; list[to] = st;
            Relabel(list, st.Letter);
            RelayoutUi(); Redraw();
        }

        // 選んでいる文字を、選んだ形でぴったり囲む（文字はその図形の中の文字になる）。元に戻す 1 回分
        void WrapText(BoxKind kind)
        {
            var t = selected as TextShape;
            popup = PopupKind.None;
            if (t == null) { RelayoutUi(); Redraw(); return; }
            PushUndo();
            var b = new BoxShape();
            b.Kind = kind; b.Color = t.Color; b.Width = Ui.S(StrokeLv[sizeLevel]);
            b.Fill = kind == BoxKind.Callout ? FillMode.Solid : FillMode.None;
            b.FillColor = kind == BoxKind.Callout ? Color.White : t.Color;
            b.Text = t.Text; b.TextSize = t.Size;
            Size need = b.TextNeed;
            Rectangle tb = t.Bounds;
            int cx = tb.X + tb.Width / 2, cy = tb.Y + tb.Height / 2;
            b.Rect = new Rectangle(cx - need.Width / 2, cy - need.Height / 2, need.Width, need.Height);
            b.SyncTail();
            int idx = shapes.IndexOf(t);
            shapes[idx] = b;
            selected = b;
            RelayoutUi(); Redraw();
        }

        void PlaceStamp(Point p)
        {
            var st = new StampShape();
            st.Letter = boxKind == BoxKind.Letter;
            st.Label = NextStampLabel(st.Letter);
            st.Color = color; st.Diameter = StampD;
            PushUndo();
            SetStampCenter(st, p);   // 図形のくっつく点の近くなら、そこにくっつける
            shapes.Add(st);
        }
        float StrokeW { get { return Ui.S(StrokeLv[sizeLevel]); } }
        float MarkerW { get { return Ui.S(MarkerLv[sizeLevel]); } }
        float TextPx { get { return Ui.S(TextLv[sizeLevel]); } }
        int MosaicBlock { get { return Ui.S(MosaicLv[sizeLevel]); } }

        static int NearestLevel(float[] table, float px)
        {
            int best = 0;
            for (int i = 1; i < table.Length; i++)
                if (Math.Abs(Ui.S(table[i]) - px) < Math.Abs(Ui.S(table[best]) - px)) best = i;
            return best;
        }

        // 選択中の図形（なければこれから書くもの）の太さ・大きさの段階
        int CurrentLevel()
        {
            Shape s = popupForShape ? selected : null;
            if (s is TextShape) return NearestLevel(TextLv, ((TextShape)s).Size);
            if (s is StrokeShape) { var st = (StrokeShape)s; return NearestLevel(st.Marker ? MarkerLv : StrokeLv, st.Width); }
            if (s is ArrowShape) return NearestLevel(StrokeLv, ((ArrowShape)s).Width);
            if (s is BoxShape) return NearestLevel(StrokeLv, ((BoxShape)s).Width);
            if (s is StampShape) return NearestLevel(StampLv, ((StampShape)s).Diameter);
            return sizeLevel;
        }

        bool SizeIsText { get { return popupForShape ? selected is TextShape : tool == Tool.Text; } }

        // ---------- Shift（正円・正方形、比率を保つ、矢印は45度ごと） ----------
#pragma warning disable 649
        bool testShift;   // 自動テスト用
#pragma warning restore 649
        bool ShiftDown { get { return testShift || (Control.ModifierKeys & Keys.Shift) != 0; } }

        // ---------- 矢印の端を図形にくっつける（スナップ） ----------
#pragma warning disable 649
        bool testAlt;     // 自動テスト用
#pragma warning restore 649
        bool AltDown { get { return testAlt || (Control.ModifierKeys & Keys.Alt) != 0; } }
        bool snapActive;          // いま動かしている端がくっついているか
        Point snapPt;
        int SnapDist { get { return Ui.S(12); } }

        // くっつけられる図形：四角・角丸・丸と、番号スタンプ
        static bool Attachable(Shape s)
        {
            var b = s as BoxShape;
            if (b != null) return b.Kind == BoxKind.Rounded || b.Kind == BoxKind.Square || b.Kind == BoxKind.Ellipse;
            return s is StampShape;
        }

        static readonly int[] EllipseDeg = { 225, 270, 315, 0, 45, 90, 135, 180 };

        // くっつく点（0..7 = 左上・上・右上・右・右下・下・左下・左）。スタンプは toward の方向のふちの点
        static Point AnchorPoint(Shape s, int i, Point toward)
        {
            var st = s as StampShape;
            if (st != null)
            {
                double dx = toward.X - st.Center.X, dy = toward.Y - st.Center.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) { dx = 1; dy = 0; len = 1; }
                return new Point(st.Center.X + (int)Math.Round(dx / len * st.Diameter / 2), st.Center.Y + (int)Math.Round(dy / len * st.Diameter / 2));
            }
            var b = (BoxShape)s;
            Rectangle r = b.Rect;
            double cx = r.Left + r.Width / 2.0, cy = r.Top + r.Height / 2.0;
            if (b.Kind == BoxKind.Ellipse)
            {
                double ang = EllipseDeg[i] * Math.PI / 180;
                return new Point((int)Math.Round(cx + r.Width / 2.0 * Math.Cos(ang)), (int)Math.Round(cy + r.Height / 2.0 * Math.Sin(ang)));
            }
            int mx = (int)Math.Round(cx), my = (int)Math.Round(cy);
            Point[] pts = { new Point(r.Left, r.Top), new Point(mx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, my),
                            new Point(r.Right, r.Bottom), new Point(mx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, my) };
            Point p = pts[i];
            if (b.Kind == BoxKind.Rounded && i % 2 == 0)
            {
                // 角丸の角は、丸みの線の上（45度の位置）
                float rad = Math.Min(Ui.S(10) + b.Width, Math.Min(r.Width, r.Height) / 2f);
                float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
                int k = (int)Math.Round(d / 2f * (1 - 0.70710678));
                p.X += (i == 0 || i == 6) ? k : -k;
                p.Y += (i == 0 || i == 2) ? k : -k;
            }
            return p;
        }

        // 点 q が、n 番目より上（手前）にある塗りのある図形に隠れているか（ignore は数えない）
        bool PointHidden(Point q, int n, Shape ignore)
        {
            for (int k = n + 1; k < shapes.Count; k++)
            {
                Shape s = shapes[k];
                if (s == ignore) continue;
                var b = s as BoxShape;
                if (b != null) { if (b.Covers(q)) return true; continue; }
                var st = s as StampShape;
                if (st != null)
                {
                    double dx = q.X - st.Center.X, dy = q.Y - st.Center.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) < st.Diameter / 2 - 1) return true;
                    continue;
                }
                if (s is MosaicShape && ((MosaicShape)s).Rect.Contains(q)) return true;
            }
            return false;
        }

        // p の近くのくっつく点を探す（toward はスタンプのふちの向きを決めるためのもう一方の端）
        // boxesOnly: スタンプを置くとき（四角・角丸・丸だけ）。隠れている点は選ばない
        bool FindSnap(Point p, Point toward, out Shape hit, out int idx, out Point pt, bool boxesOnly = false, Shape ignore = null)
        {
            hit = null; idx = -1; pt = p;
            double best = SnapDist + 0.5;
            for (int n = shapes.Count - 1; n >= 0; n--)
            {
                Shape s = shapes[n];
                if (s == ignore || !Attachable(s)) continue;
                var st = s as StampShape;
                if (st != null)
                {
                    if (boxesOnly) continue;
                    double dx = p.X - st.Center.X, dy = p.Y - st.Center.Y;
                    double d = Math.Max(0, Math.Sqrt(dx * dx + dy * dy) - st.Diameter / 2);
                    if (d < best)
                    {
                        Point q = AnchorPoint(s, 0, toward == p ? p : toward);
                        if (!PointHidden(q, n, ignore)) { best = d; hit = s; idx = 0; pt = q; }
                    }
                    continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    Point q = AnchorPoint(s, i, toward);
                    double dx = p.X - q.X, dy = p.Y - q.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < best && !PointHidden(q, n, ignore)) { best = d; hit = s; idx = i; pt = q; }
                }
            }
            return hit != null;
        }

        // スタンプの中心を p に置く。近くに図形のくっつく点があればくっつける（Alt なら自由）
        void SetStampCenter(StampShape st, Point p)
        {
            Shape hit = null; int idx = -1; Point sp = p;
            bool snapped = !AltDown && FindSnap(p, p, out hit, out idx, out sp, true, st);
            st.Center = snapped ? sp : p;
            st.Anchor = snapped ? hit : null;
            st.AnchorIdx = idx;
            snapActive = snapped; snapPt = st.Center;
        }

        // 矢印の片方の端を p に置く。近くにくっつく点があればくっつける（Alt を押していればくっつけない）
        void SetArrowEnd(ArrowShape a, bool isFrom, Point p, Point other)
        {
            Shape hit = null; int idx = -1; Point sp = p;
            bool snapped = !AltDown && FindSnap(p, other, out hit, out idx, out sp);
            if (!snapped) sp = ShiftDown && other != p ? Snap45(other, p) : p;
            if (isFrom) { a.From = sp; a.FromAnchor = snapped ? hit : null; a.FromIdx = idx; }
            else { a.To = sp; a.ToAnchor = snapped ? hit : null; a.ToIdx = idx; }
            snapActive = snapped; snapPt = sp;
        }

        // 図形を動かした・大きさを変えた・消したあと、くっついている矢印の端を合わせる
        void UpdateAttachments()
        {
            // 先にスタンプ（スタンプにくっついた矢印も正しく追従させるため）
            foreach (var s in shapes)
            {
                var st = s as StampShape;
                if (st == null || st.Anchor == null) continue;
                if (!shapes.Contains(st.Anchor)) { st.Anchor = null; continue; }
                st.Center = AnchorPoint(st.Anchor, st.AnchorIdx, st.Center);
            }
            foreach (var s in shapes)
            {
                var a = s as ArrowShape;
                if (a == null) continue;
                if (a.FromAnchor != null && !shapes.Contains(a.FromAnchor)) a.FromAnchor = null;
                if (a.ToAnchor != null && !shapes.Contains(a.ToAnchor)) a.ToAnchor = null;
                if (a.FromAnchor != null) a.From = AnchorPoint(a.FromAnchor, a.FromIdx, a.To);
                if (a.ToAnchor != null) a.To = AnchorPoint(a.ToAnchor, a.ToIdx, a.From);
            }
        }

        bool ArrowEndDragging
        {
            get { return (drag == DragMode.Draw && drawing is ArrowShape) || (drag == DragMode.ResizeShape && selected is ArrowShape); }
        }
        Point moveDownPt, stampCenterAtDown;
        bool StampDragging { get { return drag == DragMode.Move && selected is StampShape; } }
        // スタンプの道具で、まだ押していないときも点を見せる
        bool StampHovering { get { return editing && drag == DragMode.None && StampMode && textBox == null && sel.Contains(curPt); } }

        // くっつく点を出すとき：マウスにいちばん近い、見えている点を持つ図形の点だけを小さな青い点で出す。
        // くっついた点は輪で強調（画像には写らない）
        void DrawSnapPoints(Graphics g)
        {
            bool stamp = StampDragging || StampHovering;
            if (!ArrowEndDragging && !stamp) return;
            Shape ignore = StampDragging ? selected : null;
            Ui.Setup(g);
            float dot = Ui.S(5);
            // いちばん近い図形を探す
            Shape near = null; int nearIdx = -1; double nd = Ui.S(70);
            for (int n = shapes.Count - 1; n >= 0; n--)
            {
                Shape s = shapes[n];
                if (s == ignore || !Attachable(s) || s is StampShape) continue;
                for (int i = 0; i < 8; i++)
                {
                    Point q = AnchorPoint(s, i, curPt);
                    double dx = curPt.X - q.X, dy = curPt.Y - q.Y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < nd && !PointHidden(q, n, ignore)) { nd = d; near = s; nearIdx = n; }
                }
            }
            if (near != null)
                using (var fill = new SolidBrush(Color.FromArgb(220, 55, 138, 221)))
                using (var edge = new Pen(Color.White, Ui.S(1)))
                    for (int i = 0; i < 8; i++)
                    {
                        Point q = AnchorPoint(near, i, curPt);
                        if (PointHidden(q, nearIdx, ignore)) continue;
                        g.FillEllipse(fill, q.X - dot / 2, q.Y - dot / 2, dot, dot);
                        g.DrawEllipse(edge, q.X - dot / 2, q.Y - dot / 2, dot, dot);
                    }
            if (StampHovering)
            {
                // まだ置いていないとき：吸い寄せられる点があれば輪で示す
                Shape h; int hi; Point sp;
                if (!AltDown && FindSnap(curPt, curPt, out h, out hi, out sp, true))
                {
                    float ring = Ui.S(14);
                    using (var p = new Pen(Ui.Accent, Ui.S(2))) g.DrawEllipse(p, sp.X - ring / 2, sp.Y - ring / 2, ring, ring);
                }
                return;
            }
            if (snapActive)
            {
                float ring = Ui.S(14);
                using (var p = new Pen(Ui.Accent, Ui.S(2))) g.DrawEllipse(p, snapPt.X - ring / 2, snapPt.Y - ring / 2, ring, ring);
            }
        }

        // 描いている最中・つまみで変えている最中に、マウスの近くへ Shift の使い方を出す（画像には写らない）
        Point curPt;
        Rectangle shiftHintRect;
        int straightAnchor = -1;   // ペン・蛍光ペンで Shift を押している間の、直線の始まりの点（番号）

        string ShiftHintText()
        {
            bool on = ShiftDown;
            if (drag == DragMode.Draw)
            {
                if (drawing is BoxShape)
                {
                    // 円のときは「正円」、四角・吹き出しのときは「正方形」と、描いている形に合わせて出す
                    string form = ((BoxShape)drawing).Kind == BoxKind.Ellipse ? "正円" : "正方形";
                    return on ? form + "で描いています" : "Shift で" + form;
                }
                if (drawing is StrokeShape) return on ? "まっすぐ引いています" : "Shift でまっすぐ";
                if (drawing is ArrowShape)
                    return snapActive ? "図形にくっつけています（Alt で自由な位置）"
                         : on ? "45度ごとに描いています" : "Shift で45度ごと ／ Alt で自由な位置";
                if (tool == Tool.Mosaic) return on ? "正方形で描いています" : "Shift で正方形";
            }
            else if (StampDragging) return snapActive ? "図形にくっつけています（Alt で自由な位置）" : null;
            else if (drag == DragMode.ResizeShape && selected != null)
            {
                if (selected is ArrowShape)
                    return snapActive ? "図形にくっつけています（Alt で自由な位置）"
                         : on ? "45度ごとに動かしています" : "Shift で45度ごと ／ Alt で自由な位置";
                bool corner = shapeHandle == 0 || shapeHandle == 2 || shapeHandle == 4 || shapeHandle == 6;
                if ((selected is BoxShape || selected is MosaicShape) && corner) return on ? "比率を保っています" : "Shift で比率を保つ";
            }
            return null;
        }

        void DrawShiftHint(Graphics g)
        {
            string t = ShiftHintText();
            if (t == null) { shiftHintRect = Rectangle.Empty; return; }
            Ui.Setup(g);
            var sz = TextRenderer.MeasureText(t, uiFont);
            int w = sz.Width + Ui.S(16), h = sz.Height + Ui.S(8);
            Rectangle mon = MonitorOf(new Rectangle(curPt, new Size(1, 1)));
            int x = curPt.X + Ui.S(18), y = curPt.Y + Ui.S(22);
            if (x + w > mon.Right - 4) x = curPt.X - w - Ui.S(12);
            if (y + h > mon.Bottom - 4) y = curPt.Y - h - Ui.S(14);
            var r = new Rectangle(x, y, w, h);
            shiftHintRect = r;
            bool active = ShiftDown || ((ArrowEndDragging || StampDragging) && snapActive);
            using (var b = new SolidBrush(active ? Ui.Accent : Color.FromArgb(230, 40, 40, 40))) Ui.FillRound(g, b, r, h / 2f);
            TextRenderer.DrawText(g, t, uiFont, r, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        // Shift を押した・離した瞬間に、マウスを動かさなくても形と案内を更新する
        void ShiftChanged()
        {
            if (StampMode && drag == DragMode.None) Redraw();
            if (drag != DragMode.Draw && drag != DragMode.ResizeShape && !StampDragging) return;
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, curPt.X, curPt.Y, 0));
            Redraw();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) { ShiftChanged(); e.Handled = true; }
        }

        static Rectangle SquareFrom(Point a, Point b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            int s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return Geo.Norm(a, new Point(a.X + (dx < 0 ? -s : s), a.Y + (dy < 0 ? -s : s)));
        }

        static Point Snap45(Point from, Point to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return new Point(from.X + (int)Math.Round(Math.Cos(ang) * len), from.Y + (int)Math.Round(Math.Sin(ang) * len));
        }

        // ---------- 置いた図形の大きさ変更（白い丸のつまみ） ----------
        int shapeHandle = -1;
        Rectangle shapeRectAtDown;
        Point arrowFromAtDown, arrowToAtDown;
        float textSizeAtDown;
        Point textLocAtDown;

        static Rectangle ShapeRect(Shape s)
        {
            if (s is BoxShape) return ((BoxShape)s).Rect;
            if (s is MosaicShape) return ((MosaicShape)s).Rect;
            if (s is TextShape) return s.Bounds;
            return Rectangle.Empty;
        }

        // 文字は角のつまみで大きさ（文字サイズ）を変える。反対側の角は動かさず、縦横の比率はそのまま
        void ResizeText(TextShape t, int h, int dx, int dy)
        {
            Rectangle r0 = shapeRectAtDown;
            if (r0.Width <= 0) return;
            Rectangle nr = ResizeKeep(r0, h, dx, dy, true);
            double sc = nr.Width / (double)r0.Width;
            float size = (float)Math.Max(Ui.S(8), Math.Min(Ui.S(240), textSizeAtDown * sc));
            sc = size / textSizeAtDown;
            // 固定する角は、余白を除いた文字の輪郭の角（余白は大きさで変わらないため）
            Rectangle g0 = Rectangle.Inflate(r0, -Ui.S(3), -Ui.S(3));
            Point anchor = h == 0 ? new Point(g0.Right, g0.Bottom) : h == 2 ? new Point(g0.Left, g0.Bottom)
                         : h == 4 ? new Point(g0.Left, g0.Top) : new Point(g0.Right, g0.Top);
            t.Size = size;
            t.Location = new Point(anchor.X + (int)Math.Round((textLocAtDown.X - anchor.X) * sc),
                                   anchor.Y + (int)Math.Round((textLocAtDown.Y - anchor.Y) * sc));
        }

        static void SetShapeRect(Shape s, Rectangle r)
        {
            if (s is BoxShape) ((BoxShape)s).Rect = r;
            else if (s is MosaicShape) ((MosaicShape)s).Rect = r;
        }

        // 選択中の図形のつまみ（四角・丸・モザイクは8か所、矢印は両端）
        Rectangle[] ShapeHandles()
        {
            if (selected == null || editingText != null || editingCallout != null || editingStamp != null) return new Rectangle[0];
            int s = Ui.S(11), hs = s / 2;
            var pts = new List<Point>();
            if (selected is ArrowShape) { var a = (ArrowShape)selected; pts.Add(a.From); pts.Add(a.To); }
            else
            {
                Rectangle r = ShapeRect(selected);
                if (r.IsEmpty) return new Rectangle[0];
                int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                pts.AddRange(new[] {
                    new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
                    new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy) });
                // 吹き出しは、しっぽの先にもつまみ（9 番目）
                var cb = selected as BoxShape;
                if (cb != null && cb.IsCallout) pts.Add(cb.Tail);
            }
            var res = new Rectangle[pts.Count];
            for (int i = 0; i < pts.Count; i++) res[i] = new Rectangle(pts[i].X - hs, pts[i].Y - hs, s, s);
            // 文字は角の4つだけ（辺のつまみは使わない）
            if (selected is TextShape) for (int i = 1; i < res.Length; i += 2) res[i] = Rectangle.Empty;
            return res;
        }

        int HitShapeHandle(Point p)
        {
            var hs = ShapeHandles();
            for (int i = 0; i < hs.Length; i++)
            {
                if (hs[i].IsEmpty) continue;
                var r = hs[i]; r.Inflate(Ui.S(4), Ui.S(4));
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        // 角のつまみ＋Shift なら、つかんだときの縦横の比率を保つ
        static Rectangle ResizeKeep(Rectangle r0, int h, int dx, int dy, bool keepAspect)
        {
            int l = r0.Left, t = r0.Top, rt = r0.Right, b = r0.Bottom;
            if (h == 0 || h == 6 || h == 7) l += dx;
            if (h == 2 || h == 3 || h == 4) rt += dx;
            if (h == 0 || h == 1 || h == 2) t += dy;
            if (h == 4 || h == 5 || h == 6) b += dy;
            bool corner = h == 0 || h == 2 || h == 4 || h == 6;
            if (keepAspect && corner && r0.Width > 0 && r0.Height > 0)
            {
                double sx = Math.Abs(rt - l) / (double)r0.Width, sy = Math.Abs(b - t) / (double)r0.Height;
                double sc = Math.Max(sx, sy);
                int w = (int)Math.Round(r0.Width * sc), hh = (int)Math.Round(r0.Height * sc);
                // つかんでいない反対側の角を固定する
                if (h == 0) { l = r0.Right - w; t = r0.Bottom - hh; rt = r0.Right; b = r0.Bottom; }
                if (h == 2) { rt = r0.Left + w; t = r0.Bottom - hh; l = r0.Left; b = r0.Bottom; }
                if (h == 4) { rt = r0.Left + w; b = r0.Top + hh; l = r0.Left; t = r0.Top; }
                if (h == 6) { l = r0.Right - w; b = r0.Top + hh; rt = r0.Right; t = r0.Top; }
            }
            var n = Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b));
            if (n.Width < 6) n.Width = 6;
            if (n.Height < 6) n.Height = 6;
            return n;
        }

        void ApplyLevel(Shape s, int lv)
        {
            if (s is TextShape) ((TextShape)s).Size = Ui.S(TextLv[lv]);
            else if (s is StrokeShape) { var st = (StrokeShape)s; st.Width = Ui.S((st.Marker ? MarkerLv : StrokeLv)[lv]); }
            else if (s is ArrowShape) ((ArrowShape)s).Width = Ui.S(StrokeLv[lv]);
            else if (s is BoxShape)
            {
                var b = (BoxShape)s;
                b.Width = Ui.S(StrokeLv[lv]);
                // 吹き出しの中の文字も同じ段階の大きさにする
                // 吹き出しの文字の大きさは、一覧の 3 段目で別に変える
            }
            else if (s is StampShape) ((StampShape)s).Diameter = Ui.S(StampLv[lv]);
        }

        readonly List<Rectangle> windows;
        Rectangle hoverWin = Rectangle.Empty;

        // 最初の案内の帯と「画面全体」ボタン（撮影開始時にマウスがあった画面に出す）
        Rectangle startMon, hintRect, fullBtnRect;
        const string HintText = "ドラッグで範囲を選ぶ ／ クリックで青枠のウィンドウ";
        const string PickHintText = "つなげて撮る：スクロールする部分をドラッグで選ぶ";
        const string CloseText = "やめる (Esc)";
        string CurHintText { get { return PickOnly ? PickHintText : HintText; } }
        // タイマーで撮ったときは、帯は短いまま、その下に別の小さな案内を出す
        const string TimerNoticeText = "タイマーで撮りました。範囲を選んでください";
        Rectangle TimerNoticeRect
        {
            get
            {
                if (!fromTimer || PickOnly) return Rectangle.Empty;
                Size s = TextRenderer.MeasureText(TimerNoticeText, hintFont);
                int w = s.Width + Ui.S(32), h = s.Height + Ui.S(14);
                return new Rectangle(hintRect.Left + (hintRect.Width - w) / 2, hintRect.Bottom + Ui.S(10), w, h);
            }
        }

        // タイマーで撮ったとき：時間になって画面が止まったあと、範囲を選べることがわかるように案内を変える
        bool fromTimer;
        public void MarkFromTimer() { fromTimer = true; LayoutHint(); }

        // つなげて撮る用：範囲を選ぶだけで閉じる（書き込みには進まない）。選んだ範囲は PickedArea（画面の座標）
        public bool PickOnly { get; private set; }
        public Rectangle PickedArea { get; private set; }

        public static CaptureOverlay ForPick()
        {
            var ov = new CaptureOverlay(CaptureMode.Select, false);
            ov.PickOnly = true;
            ov.LayoutHint();
            return ov;
        }

        void FinishPick(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
            if (r.Width < 20 || r.Height < 20) { sel = Rectangle.Empty; Invalidate(); return; }   // 小さすぎる範囲は選び直し
            PickedArea = new Rectangle(r.X + vs.X, r.Y + vs.Y, r.Width, r.Height);
            Result = null;
            Close();
        }

        // 案内の帯の「タイマー ○秒」ボタンと、秒数を選ぶ ▾（押すと 1・2・3・5・10 秒の一覧）、右端の「× やめる」（Esc と同じ）
        Rectangle timerBtnRect, timerDropRect, timerListRect, closeBtnRect;
        Rectangle scrollBtnRect;       // 「つなげて撮る」：押すと、この画面のまま、つなげたい部分を選ぶ状態になる
        const string ScrollBtnText = "つなげて撮る";
        Rectangle[] timerListRects = new Rectangle[0];
        bool timerListOpen;
        int hoverHint = -1;            // 0=画面全体 1=タイマー 2=▾ 3=やめる 4=つなげて撮る 10+i=一覧の項目

        // 帯の「つなげて撮る」：書き込みには進まず、選んだ部分をつなげて撮る（閉じたあと ShotFlow が続ける）
        void EnterPick()
        {
            PickOnly = true;
            timerListOpen = false;
            hoverHint = -1;
            LayoutHint();
            Invalidate();
        }
        public int TimerRequest;       // 0 より大きければ、閉じたあとにその秒数でタイマー撮影
        static string TimerLabel(int s) { return "タイマー " + s + "秒"; }

        void LayoutHint()
        {
            Size ts = TextRenderer.MeasureText(CurHintText, hintFont);
            Size bs = TextRenderer.MeasureText("画面全体", hintFont);
            Size tms = TextRenderer.MeasureText(TimerLabel(10), hintFont);
            int pad = Ui.S(16), btnW = bs.Width + Ui.S(46), btnH = ts.Height + Ui.S(12);
            int timerW = tms.Width + Ui.S(42), dropW = Ui.S(28);
            int closeW = TextRenderer.MeasureText(CloseText, hintFont).Width + Ui.S(40);
            int scrollW = TextRenderer.MeasureText(ScrollBtnText, hintFont).Width + Ui.S(46);
            // 「前回の範囲」は、前に撮った範囲があるときだけ出す
            bool hasLast = !LastArea.IsEmpty && !imageMode;
            int sameW = hasLast ? TextRenderer.MeasureText(SameBtnText, hintFont).Width + Ui.S(46) : 0;
            int w = pad + ts.Width + Ui.S(14) + btnW + Ui.S(8) + (hasLast ? sameW + Ui.S(8) : 0) + timerW + dropW + Ui.S(8) + scrollW + Ui.S(8) + closeW + Ui.S(8), h = btnH + Ui.S(16);
            if (PickOnly) w = pad + ts.Width + Ui.S(14) + closeW + Ui.S(8);   // つなげて撮る：案内と「やめる」だけ
            hintRect = new Rectangle(startMon.Left + (startMon.Width - w) / 2, startMon.Top + Ui.S(28), w, h);
            int by = hintRect.Top + (h - btnH) / 2;
            closeBtnRect = new Rectangle(hintRect.Right - Ui.S(8) - closeW, by, closeW, btnH);
            if (PickOnly)
            {
                fullBtnRect = timerBtnRect = timerDropRect = timerListRect = scrollBtnRect = sameBtnRect = Rectangle.Empty;
                fullBtnRect.X = closeBtnRect.Left - Ui.S(14);   // 案内の文字の右端（DrawHint が使う）
                timerListRects = new Rectangle[0];
                return;
            }
            scrollBtnRect = new Rectangle(closeBtnRect.Left - Ui.S(8) - scrollW, by, scrollW, btnH);
            timerDropRect = new Rectangle(scrollBtnRect.Left - Ui.S(8) - dropW, by, dropW, btnH);
            timerBtnRect = new Rectangle(timerDropRect.Left - timerW, by, timerW, btnH);
            sameBtnRect = hasLast ? new Rectangle(timerBtnRect.Left - Ui.S(8) - sameW, by, sameW, btnH) : Rectangle.Empty;
            fullBtnRect = new Rectangle((hasLast ? sameBtnRect.Left : timerBtnRect.Left) - Ui.S(8) - btnW, by, btnW, btnH);
            int itemH = Ui.S(34), lw = timerW + dropW, n = Settings.TimerChoices.Length;
            timerListRect = new Rectangle(timerBtnRect.Left, hintRect.Bottom + Ui.S(6), lw, itemH * n + Ui.S(8));
            timerListRects = new Rectangle[n];
            for (int i = 0; i < n; i++) timerListRects[i] = new Rectangle(timerListRect.Left + Ui.S(4), timerListRect.Top + Ui.S(4) + i * itemH, lw - Ui.S(8), itemH);
        }

        Rectangle HintArea
        {
            get
            {
                Rectangle a = PickOnly ? hintRect : Rectangle.Union(hintRect, timerListRect);
                Rectangle n = TimerNoticeRect;
                if (!n.IsEmpty) a = Rectangle.Union(a, n);
                // ボタンの名前の吹き出しが出る所（どのボタンでも入るよう、帯の下を広めに）
                a = Rectangle.Union(a, new Rectangle(hintRect.Left - Ui.S(120), hintRect.Bottom, hintRect.Width + Ui.S(240), Ui.S(48)));
                return Rectangle.Inflate(a, Ui.S(4), Ui.S(4));
            }
        }

        // 帯のボタンの名前とキー（マウスを乗せたときに出す。キーで押せることがわかるように）
        string HintTipText(int h)
        {
            switch (h)
            {
                case 0: return "画面全体を撮る（Enter）";
                case 1: return "タイマーで撮る（T）";
                case 2: return "待つ秒数を選ぶ";
                case 3: return "やめる（Esc）";
                case 4: return "つなげて撮る：長いページを 1 枚に（S）";
                case 5: return "前回と同じ範囲で撮る（L）";
            }
            return null;
        }

        Rectangle HintTipRect
        {
            get
            {
                if (editing || timerListOpen || hoverHint < 0 || hoverHint > 5) return Rectangle.Empty;
                string s = HintTipText(hoverHint);
                Rectangle btn = hoverHint == 0 ? fullBtnRect : hoverHint == 1 ? timerBtnRect : hoverHint == 2 ? timerDropRect
                              : hoverHint == 3 ? closeBtnRect : hoverHint == 4 ? scrollBtnRect : sameBtnRect;
                if (s == null || btn.IsEmpty) return Rectangle.Empty;
                Size sz = TextRenderer.MeasureText(s, uiFont);
                int w = sz.Width + Ui.S(16), h = sz.Height + Ui.S(10);
                int x = Math.Max(startMon.Left + 4, Math.Min(btn.Left + (btn.Width - w) / 2, startMon.Right - w - 4));
                return new Rectangle(x, hintRect.Bottom + Ui.S(6), w, h);
            }
        }

        int HintHit(Point p)
        {
            if (timerListOpen) for (int i = 0; i < timerListRects.Length; i++) if (timerListRects[i].Contains(p)) return 10 + i;
            if (fullBtnRect.Contains(p)) return 0;
            if (timerBtnRect.Contains(p)) return 1;
            if (timerDropRect.Contains(p)) return 2;
            if (closeBtnRect.Contains(p)) return 3;
            if (scrollBtnRect.Contains(p)) return 4;
            if (sameBtnRect.Contains(p)) return 5;
            return -1;
        }

        // 帯の「前回の範囲」：前にコピーした範囲を、すぐ選ぶ
        Rectangle sameBtnRect;
        const string SameBtnText = "前回の範囲";
        void SelectLastArea()
        {
            if (LastArea.IsEmpty) return;
            SelectFullScreen(new Rectangle(LastArea.X - vs.X, LastArea.Y - vs.Y, LastArea.Width, LastArea.Height));
        }

        // 撮影画面を閉じて、seconds 秒後にタイマー撮影（remember なら次からの標準の秒数にする）
        void RequestTimer(int seconds, bool remember)
        {
            if (remember) { Settings.TimerSeconds = seconds; Settings.SaveIfLoaded(); }
            TimerRequest = seconds;
            Canceled = true;
            Result = null;
            Close();
        }

        void SelectFullScreen(Rectangle mon)
        {
            if (PickOnly) { FinishPick(mon); return; }
            sel = mon;
            sel.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
            hoverWin = Rectangle.Empty;
            drag = DragMode.None;
            editing = true;
            RelayoutUi();
            Invalidate();
        }

        Rectangle WindowAt(Point p)
        {
            foreach (var w in windows) if (w.Contains(p)) return w;
            return Rectangle.Empty;
        }

        readonly List<IntPtr> windowHandles = new List<IntPtr>();
        public bool WindowOnly;   // ウィンドウだけの中身に差し替えたか（自動テスト用にも）

        // クリックしたウィンドウだけの中身を取り込んで、止めた画面のその部分に重ねる。
        // 前に重なっていた窓が写らなくなる。取り込めないときは何もしない（画面の見た目のまま）
        bool UseWindowOnly(IntPtr h)
        {
            Rectangle frame; bool round;
            Bitmap img = Native.CaptureWindow(h, out frame, out round);
            if (img == null) return false;
            var local = new Rectangle(frame.X - vs.X, frame.Y - vs.Y, frame.Width, frame.Height);
            using (img)
            {
                foreach (var target in new[] { screen, dimmed })
                    using (var g = Graphics.FromImage(target))
                    {
                        // 角が丸い窓は、角の外側に後ろの画面が見えるようにする
                        if (round) using (var path = Ui.Round(local, Ui.S(8))) g.SetClip(path);
                        g.DrawImage(img, local);
                        if (target == dimmed) using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, local);
                    }
            }
            WindowOnly = true;
            return true;
        }

        public CaptureOverlay() : this(CaptureMode.Select) { }

        public CaptureOverlay(CaptureMode mode) : this(mode, false) { }

        // withCursor: マウスポインターも画像に写す（タイマー撮影で設定がオンのとき）
        public CaptureOverlay(CaptureMode mode, bool withCursor)
        {
            vs = SystemInformation.VirtualScreen;
            IntPtr fgHandle = IntPtr.Zero;
            Rectangle fg = mode == CaptureMode.Window ? Native.ForegroundWindowRect(vs, out fgHandle) : Rectangle.Empty;
            try { windows = Native.VisibleWindowRects(vs, windowHandles); } catch { windows = new List<Rectangle>(); windowHandles.Clear(); }
            // 透明度を持たない形式で保持する（画面キャプチャは透明度 0 で取り込まれるため）
            screen = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(screen))
            {
                g.CopyFromScreen(vs.X, vs.Y, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
                if (withCursor) Native.DrawCursor(g, vs);
            }
            dimmed = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(dimmed))
            {
                g.DrawImage(screen, new Rectangle(0, 0, vs.Width, vs.Height));
                using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, 0, 0, vs.Width, vs.Height);
            }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            Point cp = Cursor.Position;
            startMon = MonitorOf(new Rectangle(cp.X - vs.X, cp.Y - vs.Y, 1, 1));
            LayoutHint();

            // ショートカットで「すぐ撮る」ときは、範囲選択を飛ばして書き込みから始める
            if (mode == CaptureMode.Full) SelectFullScreen(startMon);
            else if (mode == CaptureMode.Window)
            {
                if (!fg.IsEmpty) UseWindowOnly(fgHandle);   // 前に重なっている窓は写さない
                SelectFullScreen(fg.IsEmpty ? startMon : fg);
            }
            else if (mode == CaptureMode.Last && !LastArea.IsEmpty)
                SelectFullScreen(new Rectangle(LastArea.X - vs.X, LastArea.Y - vs.Y, LastArea.Width, LastArea.Height));

            // メインメニューで最後に選んだ色と太さから始める
            color = Ui.Palette[Math.Max(0, Math.Min(Ui.Palette.Length - 1, Settings.LastColor))];
            sizeLevel = Math.Max(0, Math.Min(2, Settings.LastSize));
        }

        // 「書き直す」：最後に撮った画面・範囲・書き込みから、書き込み中の状態で始める
        bool keepScreen;   // この画面の画像は LastSession が持っているので、閉じても破棄しない

        public CaptureOverlay(LastSession s)
        {
            vs = s.VirtualScreen;
            windows = new List<Rectangle>();
            screen = s.Screen;
            keepScreen = true;
            dimmed = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(dimmed))
            {
                g.DrawImage(screen, new Rectangle(0, 0, vs.Width, vs.Height));
                using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, 0, 0, vs.Width, vs.Height);
            }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            startMon = MonitorOf(s.Sel);
            LayoutHint();
            color = Ui.Palette[Math.Max(0, Math.Min(Ui.Palette.Length - 1, Settings.LastColor))];
            sizeLevel = Math.Max(0, Math.Min(2, Settings.LastSize));
            shapes = s.Shapes;
            undo.AddRange(s.Undo);   // 書き直しても、前の書き込みを「元に戻す」で戻せる
            // 最初の番号は、置いてあるスタンプのいちばん小さい番号から（書き直しても番号がずれないように）
            InferStampStart();
            SelectFullScreen(s.Sel);
        }

        // 長い画像（つなげて撮った画像）に書き込む：monitor の画面いっぱいに開き、書き込み中の状態で始める
        public CaptureOverlay(Bitmap longImage, Rectangle monitor)
        {
            imageMode = true;
            image = longImage;
            // 画面いっぱいにはせず、画面のまん中の窓にする（全画面だと不安に感じるため）
            Rectangle wa = Screen.FromRectangle(monitor).WorkingArea;
            int w = Math.Min(wa.Width - Ui.S(60), Math.Max(Ui.S(780), image.Width + Ui.S(80)));
            int h = wa.Height - Ui.S(40);
            vs = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
            windows = new List<Rectangle>();
            keepScreen = true;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            HandleCreated += delegate { Native.RoundCorners(Handle); };
            Size bs = TextRenderer.MeasureText(BackText, uiFont);
            backRect = new Rectangle(vs.Width - Ui.S(16) - (bs.Width + Ui.S(28)), (HeaderH - Ui.S(34)) / 2, bs.Width + Ui.S(28), Ui.S(34));
            // 横はまん中、縦は上から
            imgOffX = ScrollLeft;
            imgOffY = ScrollTop;
            if (image.Height <= ScrollBottom - ScrollTop) imgOffY = Math.Max(ScrollTop, (ScrollBottom - image.Height) / 2);
            RenderView();
            startMon = new Rectangle(0, 0, vs.Width, vs.Height);
            LayoutHint();
            color = Ui.Palette[Math.Max(0, Math.Min(Ui.Palette.Length - 1, Settings.LastColor))];
            sizeLevel = Math.Max(0, Math.Min(2, Settings.LastSize));
            sel = ImageRect;
            editing = true;
            RelayoutUi();
        }

        // 書き込みを、くっつき先も含めて丸ごと写す（元に戻す・書き直す用）
        public static List<Shape> CloneShapes(List<Shape> src)
        {
            var copy = new List<Shape>();
            var map = new Dictionary<Shape, Shape>();
            foreach (var s in src) { var c = s.Clone(); map[s] = c; copy.Add(c); }
            Shape t;
            foreach (var c in copy)
            {
                var a = c as ArrowShape;
                if (a != null)
                {
                    a.FromAnchor = a.FromAnchor != null && map.TryGetValue(a.FromAnchor, out t) ? t : null;
                    a.ToAnchor = a.ToAnchor != null && map.TryGetValue(a.ToAnchor, out t) ? t : null;
                }
                var st = c as StampShape;
                if (st != null) st.Anchor = st.Anchor != null && map.TryGetValue(st.Anchor, out t) ? t : null;
            }
            return copy;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Activate();
            // タイマー撮影のあとなどは、Windows に止められて前に出られず、キーがほかのアプリへ行ってしまうことがある。
            // まず確実に前に出す。それでもだめなら、この画面がキーを受け取れるようになるまで Esc だけは受け取る
            if (!Native.ForceForeground(Handle))
            {
                escUntilActive = new EscKey();
                escUntilActive.Pressed += delegate
                {
                    if (IsDisposed) return;
                    EscapePressed();
                    if (!IsDisposed && Visible) Native.ForceForeground(Handle);   // キーを押した直後は前に出られる
                };
            }
        }

        EscKey escUntilActive;
        void StopEscUntilActive() { if (escUntilActive != null) { escUntilActive.Dispose(); escUntilActive = null; } }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); StopEscUntilActive(); }   // キーを受け取れるようになったら、Esc をほかのアプリに返す
        protected override void OnFormClosed(FormClosedEventArgs e) { StopEscUntilActive(); base.OnFormClosed(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        Rectangle MonitorOf(Rectangle r)
        {
            if (imageMode) return new Rectangle(0, HeaderH, vs.Width, vs.Height - HeaderH);   // 長い画像：窓の中（見出しの下）
            Rectangle m = Screen.FromRectangle(new Rectangle(r.X + vs.X, r.Y + vs.Y, Math.Max(1, r.Width), Math.Max(1, r.Height))).Bounds;
            m.Offset(-vs.X, -vs.Y);
            return m;
        }

        // ---------- レイアウト ----------
        void RelayoutUi()
        {
            toolbar.Clear();
            toolbar.Add(new UiItem(ItemKind.Grip, Tool.Pen, "ドラッグでメニューを移動"));
            toolbar.Add(new UiItem(ItemKind.Tool, Tool.Pen, "ペン・蛍光ペン (P)"));
            toolbar.Add(new UiItem(ItemKind.Tool, Tool.Arrow, "矢印・直線 (A)"));
            toolbar.Add(new UiItem(ItemKind.Tool, Tool.Shape, "図形 (S)"));
            toolbar.Add(new UiItem(ItemKind.Tool, Tool.Text, "文字 (T)"));
            toolbar.Add(new UiItem(ItemKind.Tool, Tool.Mosaic, "モザイク (M)"));
            toolbar.Add(new UiItem(ItemKind.Color, Tool.Pen, tool == Tool.Text ? "色・文字の大きさ" : StampMode ? "色・大きさ" : "色・太さ"));
            toolbar.Add(new UiItem(ItemKind.Undo, Tool.Pen, "元に戻す (Ctrl+Z)"));
            toolbar.Add(new UiItem(ItemKind.Cancel, Tool.Pen, imageMode ? "確認画面に戻る (Esc)" : "やめる (Esc)"));
            toolbar.Add(new UiItem(ItemKind.Copy, Tool.Pen, "コピーして閉じる (Enter)"));
            toolbar.Add(new UiItem(ItemKind.CopyMenu, Tool.Pen, "保存のしかたを選ぶ"));

            int b = Ui.S(36), pad = Ui.S(6), sep = Ui.S(13), h = b + pad * 2;
            int copyW = TextRenderer.MeasureText("コピーして閉じる", uiBold).Width + Ui.S(44);
            int x = pad;
            foreach (var it in toolbar)
            {
                if (it.Kind == ItemKind.Color || it.Kind == ItemKind.Cancel) x += sep;
                if (it.Kind == ItemKind.Copy) x += Ui.S(4);
                if (it.Kind == ItemKind.CopyMenu) x -= Ui.S(2);   // 「コピーして閉じる」とくっつけて1つのボタンに見せる
                int w = it.Kind == ItemKind.Color ? Ui.S(46) : it.Kind == ItemKind.Copy ? copyW : it.Kind == ItemKind.CopyMenu ? Ui.S(28)
                    : it.Kind == ItemKind.Grip ? Ui.S(16) : b;
                it.R = new Rectangle(x, pad, w, b);
                x += w + Ui.S(2);
            }
            int totalW = x - Ui.S(2) + pad;

            int tx, ty;
            if (toolbarMoved)
            {
                // 自分で動かした位置を優先（画面の外に出ないようにだけ調整）
                Rectangle m = MonitorOf(new Rectangle(toolbarPos, new Size(totalW, h)));
                tx = Math.Max(m.Left, Math.Min(toolbarPos.X, m.Right - totalW));
                ty = Math.Max(m.Top, Math.Min(toolbarPos.Y, m.Bottom - h));
            }
            else if (imageMode)
            {
                // 長い画像：画面の下のまん中に固定（画像を動かしても、メニューは動かない）
                tx = Math.Max(Ui.S(4), Math.Min(sel.Left + (sel.Width - totalW) / 2, vs.Width - totalW - Ui.S(4)));
                ty = vs.Height - h - Ui.S(20);
            }
            else
            {
                Rectangle mon = MonitorOf(sel);
                tx = sel.Left + (sel.Width - totalW) / 2;
                ty = sel.Bottom + Ui.S(10);
                if (ty + h > mon.Bottom - Ui.S(4)) ty = sel.Top - h - Ui.S(10);
                if (ty < mon.Top + Ui.S(4)) ty = sel.Bottom - h - Ui.S(10);
                tx = Math.Max(mon.Left + Ui.S(4), Math.Min(tx, mon.Right - totalW - Ui.S(4)));
            }
            toolbarRect = new Rectangle(tx, ty, totalW, h);
            foreach (var it in toolbar) it.R.Offset(tx, ty);

            LayoutMenu();
            if (popupOpen) LayoutPopup();
        }

        void LayoutMenu()
        {
            menu.Clear();
            menuRect = Rectangle.Empty;
            if (selected == null || textBox != null) return;
            if (selected.HasColor) menu.Add(new UiItem(ItemKind.MenuColor, Tool.Pen, selected is TextShape || selected is StampShape ? "色・大きさ" : "色・太さ"));
            if (selected is BoxShape) menu.Add(new UiItem(ItemKind.MenuFill, Tool.Pen, "塗り"));
            var mb = selected as BoxShape;
            if (selected is TextShape || (mb != null && (mb.IsCallout || (mb.CanHoldText && mb.HasText))))
                menu.Add(new UiItem(ItemKind.MenuEdit, Tool.Pen, "文字を直す（ダブルクリックでも）"));
            if (mb != null && mb.CanHoldText && mb.HasText) menu.Add(new UiItem(ItemKind.MenuFit, Tool.Pen, "文字に合わせる"));
            if (selected is TextShape) menu.Add(new UiItem(ItemKind.MenuWrap, Tool.Pen, "囲む"));
            if (selected is StampShape)
            {
                menu.Add(new UiItem(ItemKind.MenuEdit, Tool.Pen, "番号を入れて変える (Enter)"));
                menu.Add(new UiItem(ItemKind.MenuUp, Tool.Pen, "番号を増やす (↑)"));
                menu.Add(new UiItem(ItemKind.MenuDown, Tool.Pen, "番号を減らす (↓)"));
            }
            menu.Add(new UiItem(ItemKind.MenuDelete, Tool.Pen, "削除 (Delete)"));

            // アイコンだけの四角いボタン。削除は間違えないよう少し離す
            int h = Ui.S(32), pad = Ui.S(3), x = pad;
            foreach (var it in menu)
            {
                if (it.Kind == ItemKind.MenuDelete && menu.Count > 1) x += Ui.S(8);
                it.R = new Rectangle(x, pad, h, h);
                x += h;
            }
            int totalW = x + pad, totalH = h + pad * 2;
            Rectangle bnd = selected.Bounds;
            Rectangle mon = MonitorOf(sel);
            int mx = Math.Max(mon.Left + 4, Math.Min(bnd.Left, mon.Right - totalW - 4));
            int my = bnd.Top - totalH - Ui.S(8);
            if (my < mon.Top + 4) my = bnd.Bottom + Ui.S(8);
            menuRect = new Rectangle(mx, my, totalW, totalH);
            foreach (var it in menu) it.R.Offset(mx, my);
        }

        void LayoutPopup()
        {
            popHits.Clear();
            hoverPop = null;
            Rectangle anchor = Rectangle.Empty;
            foreach (var it in popupForShape ? menu : toolbar)
            {
                if (popup == PopupKind.ColorSize && (it.Kind == ItemKind.Color || it.Kind == ItemKind.MenuColor)) anchor = it.R;
                if (popup == PopupKind.Fill && it.Kind == ItemKind.MenuFill) anchor = it.R;
                if (popup == PopupKind.BoxKind && it.Kind == ItemKind.Tool && it.Tool == Tool.Shape) anchor = it.R;
                if (popup == PopupKind.ArrowKind && it.Kind == ItemKind.Tool && it.Tool == Tool.Arrow) anchor = it.R;
                if (popup == PopupKind.PenKind && it.Kind == ItemKind.Tool && it.Tool == Tool.Pen) anchor = it.R;
                if (popup == PopupKind.MosaicKind && it.Kind == ItemKind.Tool && it.Tool == Tool.Mosaic) anchor = it.R;
                if (popup == PopupKind.WrapKind && it.Kind == ItemKind.MenuWrap) anchor = it.R;
                if (popup == PopupKind.CopyKind && it.Kind == ItemKind.CopyMenu) anchor = it.R;
            }
            if (anchor.IsEmpty) { popup = PopupKind.None; return; }

            int d = Ui.S(22), gap = Ui.S(10), pad = Ui.S(10), optH = Ui.S(36), rowGap = Ui.S(13);
            // 種類の一覧は、書き込みメニューと同じ大きさ（ボタン 36、すき間 2、ふち 6 → 高さ 48）にそろえる
            int kPad = Ui.S(6), kGap = Ui.S(2), kw = Ui.S(36);
            int colorsW = Ui.Palette.Length * d + (Ui.Palette.Length - 1) * gap;
            int w, h;
            bool kinds = popup == PopupKind.BoxKind || popup == PopupKind.ArrowKind || popup == PopupKind.PenKind || popup == PopupKind.WrapKind || popup == PopupKind.MosaicKind;
            bool copyKinds = popup == PopupKind.CopyKind;
            int kindCount = popup == PopupKind.BoxKind ? BoxNames.Length : popup == PopupKind.PenKind ? 2 : popup == PopupKind.MosaicKind ? 4 : popup == PopupKind.WrapKind ? 4 : 3;
            if (copyKinds)
            {
                // 「コピーして閉じる」の ▾：文字の一覧（左に絵、右に操作キー）
                int textW = 0, keyW = 0;
                for (int i = 0; i < CopyCount; i++)
                {
                    textW = Math.Max(textW, TextRenderer.MeasureText(CopyNameAt(i), uiBold).Width);
                    keyW = Math.Max(keyW, TextRenderer.MeasureText(CopyKeyAt(i), uiFont).Width);
                }
                w = kPad * 2 + Ui.S(12) + Ui.S(18) + Ui.S(10) + textW + Ui.S(24) + keyW + Ui.S(12);
                h = kPad * 2 + CopyCount * Ui.S(40) + (CopyCount > 3 ? Ui.S(9) : 0);
            }
            else if (kinds) { w = kPad * 2 + kindCount * kw + (kindCount - 1) * kGap; h = kPad * 2 + optH; }
            else { w = pad * 2 + colorsW; h = pad * 2 + d + rowGap + optH; }
            // 選んだ吹き出しの「色・太さ」には、3 段目に文字の大きさ
            bool calloutText = popup == PopupKind.ColorSize && popupForShape && SelectedCallout != null && (SelectedCallout.IsCallout || SelectedCallout.HasText);
            if (calloutText) h += rowGap + optH;
            // 番号・記号スタンプの「大きさ」には、3 段目に最初の番号（− 1 ＋）
            bool stampStartRow = popup == PopupKind.ColorSize && !calloutText && SizeIsStamp;
            if (stampStartRow) h += rowGap + optH;

            Rectangle mon = MonitorOf(sel);
            int px = Math.Max(mon.Left + 4, Math.Min((copyKinds ? anchor.Right - w + kPad : kinds ? anchor.Left - kPad : anchor.Left - pad), mon.Right - w - 4));
            int py = anchor.Bottom + Ui.S(8);
            if (py + h > mon.Bottom - 4) py = anchor.Top - h - Ui.S(8);
            if (copyKinds && !copyMenuAt.IsEmpty)
            {
                // 右クリック（範囲の外のクリック）で開いたとき：クリックした所のすぐ右下に（入らなければ左・上へ）
                Rectangle cm = MonitorOf(new Rectangle(copyMenuAt, new Size(1, 1)));
                px = copyMenuAt.X + Ui.S(4); py = copyMenuAt.Y + Ui.S(4);
                if (px + w > cm.Right - 4) px = copyMenuAt.X - w - Ui.S(4);
                if (py + h > cm.Bottom - 4) py = copyMenuAt.Y - h - Ui.S(4);
                px = Math.Max(cm.Left + 4, Math.Min(px, cm.Right - w - 4));
                py = Math.Max(cm.Top + 4, Math.Min(py, cm.Bottom - h - 4));
            }
            if (popupForShape && selected != null)
            {
                // 図形用の一覧は、図形にかぶらない位置に出す（小メニューの上 → 図形の下 → 図形の右）
                Rectangle sb = selected.Bounds; sb.Inflate(Ui.S(6), Ui.S(6));
                int above = menuRect.Top - h - Ui.S(6), below = sb.Bottom + Ui.S(8);
                if (above >= mon.Top + 4 && !new Rectangle(px, above, w, h).IntersectsWith(sb)) py = above;
                else if (below + h <= mon.Bottom - 4) py = below;
                else { py = Math.Max(mon.Top + 4, Math.Min(sb.Top, mon.Bottom - h - 4)); px = Math.Min(sb.Right + Ui.S(8), mon.Right - w - 4); }
            }
            popupRect = new Rectangle(px, py, w, h);

            int x0 = px + pad, y0 = py + pad;
            px = popupRect.X; py = popupRect.Y;
            if (copyKinds)
            {
                for (int i = 0; i < CopyCount; i++)
                    popHits.Add(new PopHit(new Rectangle(px + kPad, py + kPad + i * Ui.S(40) + (i == 3 ? Ui.S(9) : 0), w - kPad * 2, Ui.S(40)), 9, i));
                return;
            }
            if (kinds)
            {
                int type = popup == PopupKind.BoxKind ? 4 : popup == PopupKind.ArrowKind ? 5 : popup == PopupKind.WrapKind ? 8 : popup == PopupKind.MosaicKind ? 10 : 6;
                for (int i = 0; i < kindCount; i++) popHits.Add(new PopHit(new Rectangle(px + kPad + i * (kw + kGap), py + kPad, kw, optH), type, i));
                return;
            }
            int optW = (w - pad * 2 - 2 * gap) / 3;
            // 色・太さ：上に色、下に太さ ／ 塗り：上に塗り方、下に塗りの色
            bool fill = popup == PopupKind.Fill;
            int colorsY = fill ? y0 + optH + rowGap : y0;
            int optY = fill ? y0 : y0 + d + rowGap;
            for (int i = 0; i < Ui.Palette.Length; i++)
                popHits.Add(new PopHit(new Rectangle(x0 + i * (d + gap), colorsY, d, d), fill ? 3 : 0, i));
            for (int i = 0; i < 3; i++)
                popHits.Add(new PopHit(new Rectangle(x0 + i * (optW + gap), optY, optW, optH), fill ? 2 : 1, i));
            if (calloutText)
                for (int i = 0; i < 3; i++)
                    popHits.Add(new PopHit(new Rectangle(x0 + i * (optW + gap), optY + optH + rowGap, optW, optH), 7, i));
            if (stampStartRow)
            {
                // 左に「最初の番号」と書き、右に［−］［数字］［＋］
                int sy = optY + optH + rowGap, cw = Ui.S(34), nw = Ui.S(40);
                int right = popupRect.Right - pad;
                popHits.Add(new PopHit(new Rectangle(right - cw, sy, cw, optH), 11, 2));
                popHits.Add(new PopHit(new Rectangle(right - cw - nw, sy, nw, optH), 11, 1));
                popHits.Add(new PopHit(new Rectangle(right - cw * 2 - nw, sy, cw, optH), 11, 0));
                startLabelRect = new Rectangle(x0, sy, right - cw * 2 - nw - x0 - Ui.S(4), optH);
            }
        }

        Rectangle startLabelRect;   // 「最初の番号」の文字の場所

        // 一覧の「最初の番号」が、数字と記号のどちらのスタンプのものか
        bool StartRowLetter
        {
            get
            {
                var st = popupForShape ? selected as StampShape : null;
                return st != null ? st.Letter : boxKind == BoxKind.Letter;
            }
        }

        static bool TogglesPopup(UiItem it, PopupKind closed)
        {
            return (it.Kind == ItemKind.Color && closed == PopupKind.ColorSize)
                || (it.Kind == ItemKind.MenuColor && closed == PopupKind.ColorSize)
                || (it.Kind == ItemKind.MenuFill && closed == PopupKind.Fill)
                || (it.Kind == ItemKind.Tool && it.Tool == Tool.Shape && closed == PopupKind.BoxKind)
                || (it.Kind == ItemKind.Tool && it.Tool == Tool.Arrow && closed == PopupKind.ArrowKind)
                || (it.Kind == ItemKind.Tool && it.Tool == Tool.Pen && closed == PopupKind.PenKind)
                || (it.Kind == ItemKind.Tool && it.Tool == Tool.Mosaic && closed == PopupKind.MosaicKind)
                || (it.Kind == ItemKind.MenuWrap && closed == PopupKind.WrapKind)
                || (it.Kind == ItemKind.CopyMenu && closed == PopupKind.CopyKind);
        }

        Rectangle[] Handles()
        {
            int s = Ui.S(10), hs = s / 2;
            int cx = sel.Left + sel.Width / 2, cy = sel.Top + sel.Height / 2;
            Point[] pts = {
                new Point(sel.Left, sel.Top), new Point(cx, sel.Top), new Point(sel.Right, sel.Top), new Point(sel.Right, cy),
                new Point(sel.Right, sel.Bottom), new Point(cx, sel.Bottom), new Point(sel.Left, sel.Bottom), new Point(sel.Left, cy) };
            var r = new Rectangle[8];
            for (int i = 0; i < 8; i++) r[i] = new Rectangle(pts[i].X - hs, pts[i].Y - hs, s, s);
            return r;
        }

        int HitHandle(Point p)
        {
            var hs = Handles();
            for (int i = 0; i < hs.Length; i++) { var r = hs[i]; r.Inflate(Ui.S(4), Ui.S(4)); if (r.Contains(p)) return i; }
            return -1;
        }

        // 範囲の外でも、ふちから SafeZone 以内なら「その方向のつまみ」として扱う
        int SafeZone { get { return Ui.S(20); } }
        bool closedByThisClick;         // このクリックで一覧を閉じた（ドラッグしなければ何もしない）

        int EdgeZoneHandle(Point p)
        {
            if (sel.IsEmpty || sel.Contains(p) || !Rectangle.Inflate(sel, SafeZone, SafeZone).Contains(p)) return -1;
            int hx = p.X < sel.Left ? -1 : (p.X >= sel.Right ? 1 : 0);
            int vy = p.Y < sel.Top ? -1 : (p.Y >= sel.Bottom ? 1 : 0);
            if (vy < 0) return hx < 0 ? 0 : (hx == 0 ? 1 : 2);
            if (vy > 0) return hx < 0 ? 6 : (hx == 0 ? 5 : 4);
            return hx > 0 ? 3 : 7;
        }

        bool NearUi(Point p)
        {
            int z = SafeZone;
            if (!toolbarRect.IsEmpty && Rectangle.Inflate(toolbarRect, z, z).Contains(p)) return true;
            if (!menuRect.IsEmpty && Rectangle.Inflate(menuRect, z, z).Contains(p)) return true;
            if (!popupRect.IsEmpty && popupOpen && Rectangle.Inflate(popupRect, z, z).Contains(p)) return true;
            return false;
        }

        UiItem HitItem(Point p)
        {
            foreach (var it in toolbar) if (it.R.Contains(p)) return it;
            foreach (var it in menu) if (it.R.Contains(p)) return it;
            return null;
        }

        Shape HitShape(Point p)
        {
            for (int i = shapes.Count - 1; i >= 0; i--) if (shapes[i].HitTest(p)) return shapes[i];
            return null;
        }

        // ---------- 再描画（変わった部分だけ） ----------
        Rectangle UiArea()
        {
            Rectangle a = sel;
            a.Inflate(Ui.S(60), Ui.S(60));
            if (editing)
            {
                // 左右も広めに：端のボタンの名前の表示は、メニューの外にはみ出すことがある（1回目から全部描くため）
                a = Rectangle.Union(a, Rectangle.Inflate(toolbarRect, Ui.S(160), Ui.S(50)));
                if (!lastTipRect.IsEmpty) a = Rectangle.Union(a, Rectangle.Inflate(lastTipRect, 4, 4));
                if (!shiftHintRect.IsEmpty) a = Rectangle.Union(a, Rectangle.Inflate(shiftHintRect, 4, 4));
                if (drag == DragMode.Draw || drag == DragMode.ResizeShape || drag == DragMode.Move || StampMode)
                    a = Rectangle.Union(a, new Rectangle(curPt.X - Ui.S(320), curPt.Y - Ui.S(80), Ui.S(640), Ui.S(160)));
                if (!textFrameRect.IsEmpty) a = Rectangle.Union(a, Rectangle.Inflate(textFrameRect, Ui.S(8), Ui.S(40)));
                if (!enterHintRect.IsEmpty) a = Rectangle.Union(a, Rectangle.Inflate(enterHintRect, 4, 4));
                if (!menuRect.IsEmpty) a = Rectangle.Union(a, Rectangle.Inflate(menuRect, 4, 4));
                if (popupOpen) a = Rectangle.Union(a, Rectangle.Inflate(popupRect, Ui.S(60), Ui.S(44)));
                if (selected != null) a = Rectangle.Union(a, Rectangle.Inflate(selected.Bounds, Ui.S(10), Ui.S(10)));
            }
            return a;
        }

        void Redraw()
        {
            UpdateAttachments();
            Rectangle now = UiArea();
            Invalidate(lastUi.IsEmpty ? now : Rectangle.Union(lastUi, now));
            lastUi = now;
        }

        // ---------- 描画 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);

            if (sel.Width > 0 && sel.Height > 0)
            {
                Rectangle sc = Rectangle.Intersect(sel, clip);
                if (!sc.IsEmpty) g.DrawImage(screen, sc, sc, GraphicsUnit.Pixel);
                Ui.Setup(g);
                g.SetClip(sel);
                foreach (var s in shapes) if (s != editingText) s.Draw(g);
                if (drawing != null) drawing.Draw(g);
                if (!mosaicPreview.IsEmpty)
                {
                    if (mosaicFill) using (var b = new SolidBrush(FillColors[mosaicKind])) g.FillRectangle(b, mosaicPreview);
                    using (var p = new Pen(Color.White, 2)) g.DrawRectangle(p, mosaicPreview);
                    using (var p = new Pen(Color.Black, 2)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, mosaicPreview); }
                }
                g.ResetClip();

                using (var p = new Pen(Color.FromArgb(55, 138, 221), Ui.S(2))) g.DrawRectangle(p, sel);
                if (editing)
                {
                    foreach (var h in Handles())
                    {
                        g.FillRectangle(Brushes.White, h);
                        using (var p = new Pen(Color.FromArgb(55, 138, 221), Ui.S(1.5f))) g.DrawRectangle(p, h);
                    }
                }
                if (!editing || drag == DragMode.Resize) DrawSizeLabel(g);

                if (selected != null && selected != editingText)
                {
                    var hs = ShapeHandles();
                    if (hs.Length == 0 || selected is ArrowShape)
                    {
                        Rectangle b = selected.Bounds;
                        b.Inflate(Ui.S(4), Ui.S(4));
                        using (var p = new Pen(Ui.Accent, Ui.S(1.5f))) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, b); }
                    }
                    else
                    {
                        using (var p = new Pen(Ui.Accent, Ui.S(1.2f))) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, ShapeRect(selected)); }
                    }
                    // つまみは白い丸（範囲の四角いつまみと区別する）
                    var sa = selected as ArrowShape;
                    for (int hi = 0; hi < hs.Length; hi++)
                    {
                        var h = hs[hi];
                        if (h.IsEmpty) continue;
                        // 図形にくっついている矢印の端は、青い丸（つながっていることがわかるように）
                        bool att = sa != null && ((hi == 0 && sa.FromAnchor != null) || (hi == 1 && sa.ToAnchor != null));
                        using (var hb = new SolidBrush(att ? Ui.Accent : Color.White)) g.FillEllipse(hb, h);
                        using (var p = new Pen(att ? Color.White : Ui.Accent, Ui.S(1.6f))) g.DrawEllipse(p, h);
                    }
                }
            }
            else if (!editing)
            {
                if (!hoverWin.IsEmpty && drag == DragMode.None)
                {
                    Rectangle hc = Rectangle.Intersect(hoverWin, clip);
                    if (!hc.IsEmpty) g.DrawImage(screen, hc, hc, GraphicsUnit.Pixel);
                    var hr = hoverWin; hr.Inflate(-Ui.S(1), -Ui.S(1));
                    using (var p = new Pen(Color.FromArgb(55, 138, 221), Ui.S(3))) g.DrawRectangle(p, hr);
                }
                DrawHint(g);
            }

            if (editing)
            {
                if (imageMode) DrawImageScrollbar(g);
                DrawToolbar(g);
                if (!menuRect.IsEmpty) DrawMenu(g);
                if (popupOpen) DrawPopup(g);
                if (hover != null && drag == DragMode.None) DrawTooltip(g, hover);
                DrawSnapPoints(g);
                DrawShiftHint(g);
                DrawTextFrameHint(g);
                if (enterGuardHint) DrawEnterHint(g);
            }
            if (imageMode) DrawImageHeader(g);   // 見出しは一番上に（書き込みがはみ出しても隠れる）
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (imageMode) cp.ClassStyle |= 0x00020000;   // 長い画像の窓：影を付けて、ほかの画面と同じように浮かせる
                return cp;
            }
        }

        void DrawSizeLabel(Graphics g)
        {
            string s = sel.Width + " × " + sel.Height;
            var sz = g.MeasureString(s, uiFont);
            float ly = sel.Top - sz.Height - Ui.S(6); if (ly < 0) ly = sel.Top + Ui.S(4);
            using (var b = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) Ui.FillRound(g, b, new RectangleF(sel.Left, ly, sz.Width + Ui.S(10), sz.Height + Ui.S(2)), Ui.S(4));
            g.DrawString(s, uiFont, Brushes.White, sel.Left + Ui.S(5), ly + Ui.S(1));
        }

        // × やめる（Esc と同じ。Esc を知らない人でも閉じられるように）
        void DrawCloseButton(Graphics g)
        {
            using (var b = new SolidBrush(hoverHint == 3 ? Color.FromArgb(95, 95, 102) : Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, closeBtnRect, Ui.S(8));
            int xs = Ui.S(14);
            var xr = new Rectangle(closeBtnRect.Left + Ui.S(12), closeBtnRect.Top + (closeBtnRect.Height - xs) / 2, xs, xs);
            Icons.Close(g, xr, Color.White);
            var xtr = new Rectangle(xr.Right + Ui.S(4), closeBtnRect.Top, closeBtnRect.Right - xr.Right - Ui.S(10), closeBtnRect.Height);
            TextRenderer.DrawText(g, CloseText, hintFont, xtr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        void DrawHint(Graphics g)
        {
            Ui.Setup(g);
            using (var b = new SolidBrush(Color.FromArgb(225, 30, 30, 30))) Ui.FillRound(g, b, hintRect, Ui.S(12));
            var tr = new Rectangle(hintRect.Left + Ui.S(16), hintRect.Top, fullBtnRect.Left - hintRect.Left - Ui.S(16), hintRect.Height);
            TextRenderer.DrawText(g, CurHintText, hintFont, tr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (PickOnly) { DrawCloseButton(g); return; }

            using (var b = new SolidBrush(hoverHint == 0 ? Color.FromArgb(18, 78, 140) : Ui.Accent)) Ui.FillRound(g, b, fullBtnRect, Ui.S(8));
            int isz = Ui.S(18);
            var ir = new Rectangle(fullBtnRect.Left + Ui.S(12), fullBtnRect.Top + (fullBtnRect.Height - isz) / 2, isz, isz);
            Icons.Monitor(g, ir, Color.White);
            var br = new Rectangle(ir.Right + Ui.S(4), fullBtnRect.Top, fullBtnRect.Right - ir.Right - Ui.S(10), fullBtnRect.Height);
            TextRenderer.DrawText(g, "画面全体", hintFont, br, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);

            // タイマー ○秒 ｜ ▾
            var tall = Rectangle.Union(timerBtnRect, timerDropRect);
            using (var b = new SolidBrush(Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, tall, Ui.S(8));
            if (hoverHint == 1 || hoverHint == 2)
            {
                var hr = hoverHint == 1 ? timerBtnRect : timerDropRect;
                using (var b = new SolidBrush(Color.FromArgb(95, 95, 102))) Ui.FillRound(g, b, hr, Ui.S(8));
            }
            var tir = new Rectangle(timerBtnRect.Left + Ui.S(12), timerBtnRect.Top + (timerBtnRect.Height - isz) / 2, isz, isz);
            Icons.Timer(g, tir, Color.White);
            var tbr = new Rectangle(tir.Right + Ui.S(4), timerBtnRect.Top, timerBtnRect.Right - tir.Right - Ui.S(8), timerBtnRect.Height);
            TextRenderer.DrawText(g, TimerLabel(Settings.TimerSeconds), hintFont, tbr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            using (var p = new Pen(Color.FromArgb(120, 120, 128), 1))
                g.DrawLine(p, timerDropRect.Left, timerDropRect.Top + Ui.S(7), timerDropRect.Left, timerDropRect.Bottom - Ui.S(7));
            int cs = Ui.S(12);
            Icons.Chevron(g, new Rectangle(timerDropRect.Left + (timerDropRect.Width - cs) / 2, timerDropRect.Top + (timerDropRect.Height - cs) / 2, cs, cs), Color.White);

            // 前回の範囲（前にコピーした範囲があるときだけ）
            if (!sameBtnRect.IsEmpty)
            {
                using (var b = new SolidBrush(hoverHint == 5 ? Color.FromArgb(95, 95, 102) : Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, sameBtnRect, Ui.S(8));
                var mir = new Rectangle(sameBtnRect.Left + Ui.S(12), sameBtnRect.Top + (sameBtnRect.Height - isz) / 2, isz, isz);
                Icons.Region(g, mir, Color.White);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, mir.X + mir.Width * 9f / 24f, mir.Y + mir.Height * 9f / 24f, mir.Width * 6f / 24f, mir.Height * 6f / 24f);
                var mtr = new Rectangle(mir.Right + Ui.S(4), sameBtnRect.Top, sameBtnRect.Right - mir.Right - Ui.S(10), sameBtnRect.Height);
                TextRenderer.DrawText(g, SameBtnText, hintFont, mtr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }

            // つなげて撮る
            using (var b = new SolidBrush(hoverHint == 4 ? Color.FromArgb(95, 95, 102) : Color.FromArgb(70, 70, 76))) Ui.FillRound(g, b, scrollBtnRect, Ui.S(8));
            var sir = new Rectangle(scrollBtnRect.Left + Ui.S(12), scrollBtnRect.Top + (scrollBtnRect.Height - isz) / 2, isz, isz);
            Icons.Scroll(g, sir, Color.White);
            var sbr = new Rectangle(sir.Right + Ui.S(4), scrollBtnRect.Top, scrollBtnRect.Right - sir.Right - Ui.S(10), scrollBtnRect.Height);
            TextRenderer.DrawText(g, ScrollBtnText, hintFont, sbr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);

            DrawCloseButton(g);

            // タイマーで撮ったときの案内（帯のすぐ下に、青い小さな札で）
            Rectangle tn = TimerNoticeRect;
            if (!tn.IsEmpty && !timerListOpen)
            {
                using (var b = new SolidBrush(Ui.Accent)) Ui.FillRound(g, b, tn, tn.Height / 2);
                TextRenderer.DrawText(g, TimerNoticeText, hintFont, tn, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }

            // マウスを乗せたボタンの名前と、キー（書き込みのメニューと同じ見た目の小さな吹き出しで、ボタンのすぐ下に）
            Rectangle tip = HintTipRect;
            if (!tip.IsEmpty)
            {
                using (var b = new SolidBrush(Color.FromArgb(240, 40, 40, 40))) Ui.FillRound(g, b, tip, Ui.S(6));
                TextRenderer.DrawText(g, HintTipText(hoverHint), uiFont, tip, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }

            if (timerListOpen)
            {
                using (var b = new SolidBrush(Color.FromArgb(240, 36, 36, 40))) Ui.FillRound(g, b, timerListRect, Ui.S(10));
                for (int i = 0; i < timerListRects.Length; i++)
                {
                    int sec = Settings.TimerChoices[i];
                    var r = timerListRects[i];
                    bool cur = sec == Settings.TimerSeconds;
                    if (cur || hoverHint == 10 + i)
                        using (var b = new SolidBrush(cur ? Ui.Accent : Color.FromArgb(80, 80, 88))) Ui.FillRound(g, b, r, Ui.S(7));
                    TextRenderer.DrawText(g, sec + "秒後に撮る", uiFont, r, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }
            }
        }

        void DrawToolbar(Graphics g)
        {
            Ui.Setup(g);
            using (var b = new SolidBrush(Color.White)) Ui.FillRound(g, b, toolbarRect, Ui.S(12));
            using (var p = new Pen(Color.FromArgb(200, 200, 200), 1)) Ui.DrawRound(g, p, toolbarRect, Ui.S(12));

            UiItem prev = null;
            foreach (var it in toolbar)
            {
                if (prev != null && (it.Kind == ItemKind.Color || it.Kind == ItemKind.Cancel))
                {
                    int sx = (prev.R.Right + it.R.Left) / 2;
                    using (var p = new Pen(Color.FromArgb(215, 215, 215), 1))
                        g.DrawLine(p, sx, it.R.Top + Ui.S(8), sx, it.R.Bottom - Ui.S(8));
                }
                prev = it;

                if (it.Kind == ItemKind.Grip)
                {
                    // 縦に並んだ 2×3 の点：つかんで動かせる印
                    Color gc = (it == hover || drag == DragMode.MoveToolbar) ? Ui.Accent : Color.FromArgb(125, 125, 125);
                    float d = Ui.S(3), gx = it.R.Left + (it.R.Width - d * 3) / 2f, gy = it.R.Top + (it.R.Height - d * 5) / 2f;
                    using (var b = new SolidBrush(gc))
                        for (int i = 0; i < 2; i++)
                            for (int j = 0; j < 3; j++)
                                g.FillEllipse(b, gx + i * d * 2, gy + j * d * 2, d, d);
                    continue;
                }

                if (it.Kind == ItemKind.CopyMenu)
                {
                    // 「コピーして閉じる」の右の ▾（保存のしかたを選ぶ）
                    if (it == hover) using (var b = new SolidBrush(Color.FromArgb(18, 78, 140))) Ui.FillRound(g, b, it.R, Ui.S(8));
                    using (var p = new Pen(Color.FromArgb(150, 255, 255, 255), 1))
                        g.DrawLine(p, it.R.Left, it.R.Top + Ui.S(8), it.R.Left, it.R.Bottom - Ui.S(8));
                    int cs = Ui.S(12);
                    Icons.Chevron(g, new Rectangle(it.R.Left + (it.R.Width - cs) / 2, it.R.Top + (it.R.Height - cs) / 2, cs, cs), Color.White);
                    continue;
                }

                if (it.Kind == ItemKind.Copy)
                {
                    // ▾ と合わせて1つの青いボタンに見せる
                    UiItem menuItem = null;
                    foreach (var x2 in toolbar) if (x2.Kind == ItemKind.CopyMenu) menuItem = x2;
                    Rectangle whole = menuItem != null ? Rectangle.Union(it.R, menuItem.R) : it.R;
                    using (var b = new SolidBrush(Ui.Accent)) Ui.FillRound(g, b, whole, Ui.S(8));
                    if (it == hover) using (var b = new SolidBrush(Color.FromArgb(18, 78, 140))) Ui.FillRound(g, b, it.R, Ui.S(8));
                    var ir = new Rectangle(it.R.Left + Ui.S(10), it.R.Top + (it.R.Height - Ui.S(18)) / 2, Ui.S(18), Ui.S(18));
                    Icons.Copy(g, ir, Color.White);
                    var tr = new Rectangle(ir.Right + Ui.S(4), it.R.Top, it.R.Right - ir.Right - Ui.S(8), it.R.Height);
                    TextRenderer.DrawText(g, "コピーして閉じる", uiBold, tr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                    continue;
                }

                bool on = (it.Kind == ItemKind.Tool && (it.Tool == tool || (it.Tool == Tool.Pen && IsPenGroup(tool)))) ||(it.Kind == ItemKind.Color && popup == PopupKind.ColorSize && !popupForShape);
                if (on) using (var b = new SolidBrush(Ui.AccentLight)) Ui.FillRound(g, b, it.R, Ui.S(8));
                else if (it == hover) using (var b = new SolidBrush(Color.FromArgb(240, 240, 240))) Ui.FillRound(g, b, it.R, Ui.S(8));
                Color ic = on ? Ui.Accent : Color.FromArgb(70, 70, 70);
                int isz = Ui.S(22);
                var icon = new Rectangle(it.R.Left + (it.R.Width - isz) / 2, it.R.Top + (it.R.Height - isz) / 2, isz, isz);

                if (it.Kind == ItemKind.Tool && HasSubmenu(it.Tool))
                {
                    // 今の種類のアイコン＋右下の小さな三角（種類が選べることを示す目印。押す場所ではない）
                    if (it.Tool == Tool.Shape) Icons.Box(g, boxKind, icon, ic);
                    else if (it.Tool == Tool.Arrow) Icons.ArrowOf(g, arrowKind, icon, ic);
                    else if (it.Tool == Tool.Mosaic) { if (mosaicFill) Icons.SolidFill(g, icon, FillColors[mosaicKind], ic); else Icons.Tool(g, Tool.Mosaic, icon, ic, uiBold); }
                    else Icons.Tool(g, penTool, icon, ic, uiBold);
                    float t = Ui.S(5);
                    using (var b = new SolidBrush(Color.FromArgb(150, ic)))
                        g.FillPolygon(b, new[] { new PointF(it.R.Right - Ui.S(3), it.R.Bottom - Ui.S(3) - t), new PointF(it.R.Right - Ui.S(3), it.R.Bottom - Ui.S(3)), new PointF(it.R.Right - Ui.S(3) - t, it.R.Bottom - Ui.S(3)) });
                }
                else if (it.Kind == ItemKind.Tool) Icons.Tool(g, it.Tool, icon, ic, uiBold);
                else if (it.Kind == ItemKind.Undo) Icons.Undo(g, icon, ic);
                else if (it.Kind == ItemKind.Cancel) Icons.Close(g, icon, ic);
                else if (it.Kind == ItemKind.Color)
                {
                    // 丸の色＝いまの色、丸の大きさ＝いまの太さ（細・中・太）。押さなくても見てわかるように
                    int box = Ui.S(18), d = Ui.S(new[] { 10, 14, 18 }[Math.Max(0, Math.Min(2, sizeLevel))]);
                    var area = new Rectangle(it.R.Left + Ui.S(7), it.R.Top + (it.R.Height - box) / 2, box, box);
                    var dot = new Rectangle(area.Left + (box - d) / 2, area.Top + (box - d) / 2, d, d);
                    Icons.Dot(g, dot, color);
                    Icons.Chevron(g, new Rectangle(area.Right + Ui.S(3), area.Top, Ui.S(12), box), Color.FromArgb(90, 90, 90));
                }
            }
        }

        void DrawMenu(Graphics g)
        {
            Ui.Setup(g);
            using (var b = new SolidBrush(Color.White)) Ui.FillRound(g, b, menuRect, Ui.S(8));
            using (var p = new Pen(Color.FromArgb(200, 200, 200), 1)) Ui.DrawRound(g, p, menuRect, Ui.S(8));
            foreach (var it in menu)
            {
                bool del = it.Kind == ItemKind.MenuDelete;
                if (it == hover)
                    using (var b = new SolidBrush(del ? Color.FromArgb(253, 232, 232) : Color.FromArgb(240, 240, 240))) Ui.FillRound(g, b, it.R, Ui.S(6));
                Color tc = del ? Color.FromArgb(190, 40, 40) : Color.FromArgb(60, 60, 60);
                bool small = it.Kind == ItemKind.MenuColor || it.Kind == ItemKind.MenuFill;
                int isz = Ui.S(small ? 16 : 20);
                var ir = new Rectangle(it.R.Left + (it.R.Width - isz) / 2, it.R.Top + (it.R.Height - isz) / 2, isz, isz);
                if (it.Kind == ItemKind.MenuColor) Icons.Dot(g, ir, selected != null ? selected.Color : color);
                else if (del) Icons.Trash(g, ir, tc);
                else if (it.Kind == ItemKind.MenuEdit) Icons.Tool(g, Tool.Pen, ir, tc, uiBold);
                else if (it.Kind == ItemKind.MenuUp || it.Kind == ItemKind.MenuDown)
                    // ▲▼ だと「重なりの順」とも読めるので、数字が増える・減ることがわかる「＋1」「−1」
                    TextRenderer.DrawText(g, it.Kind == ItemKind.MenuUp ? "+1" : "−1", uiBold, it.R, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                else if (it.Kind == ItemKind.MenuFit) Icons.FitText(g, ir, tc);
                else if (it.Kind == ItemKind.MenuWrap) Icons.WrapText(g, ir, tc);
                else if (it.Kind == ItemKind.MenuFill)
                {
                    var bx = selected as BoxShape;
                    Color fc = bx != null && bx.Fill != FillMode.None ? bx.FillColor : Color.White;
                    using (var b = new SolidBrush(fc)) Ui.FillRound(g, b, ir, Ui.S(3));
                    using (var p = new Pen(Color.FromArgb(120, 120, 120), 1)) Ui.DrawRound(g, p, ir, Ui.S(3));                }
            }
        }

        static readonly string[] SizeNames = { "細", "中", "太" };
        static readonly string[] TextSizeNames = { "小", "中", "大" };
        static readonly string[] FillNames = { "なし", "うすく", "塗る" };
        static readonly string[] BoxNames = { "角丸", "四角", "丸", "吹き出し", "番号スタンプ (1, 2, 3…)", "記号スタンプ (A, B, C…)" };
        static readonly string[] PenNames = { "ペン (P)", "蛍光ペン (H)" };
        static readonly string[] MosaicNames = { "モザイク (M)", "黒で塗りつぶす（確実に隠す）", "グレーで塗りつぶす", "白で塗りつぶす（白い背景に）" };
        static readonly string[] ArrowNames = { "矢印", "直線", "両矢印" };

        void DrawPopup(Graphics g)
        {
            Ui.Setup(g);
            // 書き込みメニューと同じ見た目（白・角の丸み 12・薄い灰色のふち）
            using (var b = new SolidBrush(Color.White)) Ui.FillRound(g, b, popupRect, Ui.S(12));
            using (var p = new Pen(Color.FromArgb(200, 200, 200), 1)) Ui.DrawRound(g, p, popupRect, Ui.S(12));

            Color cur = popupForShape && selected != null ? selected.Color : color;
            var box = selected as BoxShape;
            int level = CurrentLevel();
            Color ink = Color.FromArgb(60, 60, 60);
            Rectangle colorsRow = Rectangle.Empty, optRow = Rectangle.Empty, textRow = Rectangle.Empty;

            foreach (var ph in popHits)
            {
                Rectangle r = ph.R;
                if (ph.Type == 9)
                {
                    // コピー・保存のしかた：左に絵、まん中に名前、右に操作キー
                    if (ph == hoverPop) using (var b = new SolidBrush(Color.FromArgb(242, 242, 242))) Ui.FillRound(g, b, r, Ui.S(8));
                    int isz = Ui.S(18);
                    var ir = new Rectangle(r.Left + Ui.S(12), r.Top + (r.Height - isz) / 2, isz, isz);
                    // 4 つ目の「やめる」は、削除と同じく赤で、上に区切りの線
                    bool quit = ph.Index == 3;
                    Color qc = quit ? Color.FromArgb(190, 40, 40) : Color.FromArgb(25, 25, 25);
                    if (quit) using (var lp = new Pen(Color.FromArgb(225, 225, 225), 1)) g.DrawLine(lp, r.Left + Ui.S(6), r.Top - Ui.S(5), r.Right - Ui.S(6), r.Top - Ui.S(5));
                    if (ph.Index == 0) Icons.Copy(g, ir, qc); else if (quit) Icons.Close(g, Rectangle.Inflate(ir, -Ui.S(2), -Ui.S(2)), qc); else Icons.Download(g, ir, qc);
                    var tr = new Rectangle(ir.Right + Ui.S(10), r.Top, r.Right - ir.Right - Ui.S(22), r.Height);
                    TextRenderer.DrawText(g, CopyNameAt(ph.Index), uiBold, tr, qc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    TextRenderer.DrawText(g, CopyKeyAt(ph.Index), uiFont, new Rectangle(r.Left, r.Top, r.Width - Ui.S(12), r.Height),
                        Color.FromArgb(90, 90, 90), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                    continue;
                }
                if (ph.Type == 0 || ph.Type == 3)
                {
                    colorsRow = colorsRow.IsEmpty ? r : Rectangle.Union(colorsRow, r);
                    Icons.Dot(g, r, Ui.Palette[ph.Index]);
                    bool on = ph.Type == 0 ? Ui.Palette[ph.Index].ToArgb() == cur.ToArgb()
                        : box != null && box.Fill != FillMode.None && Ui.Palette[ph.Index].ToArgb() == box.FillColor.ToArgb();
                    if (on)
                    {
                        var rr = r; rr.Inflate(Ui.S(4), Ui.S(4));
                        using (var p = new Pen(Color.FromArgb(55, 138, 221), Ui.S(2))) g.DrawEllipse(p, rr);
                    }
                    continue;
                }

                if (ph.Type == 7 || ph.Type == 11) textRow = textRow.IsEmpty ? r : Rectangle.Union(textRow, r);
                else optRow = optRow.IsEmpty ? r : Rectangle.Union(optRow, r);
                if (ph.Type == 11)
                {
                    // 最初の番号：「最初の番号」［−］［5］［＋］
                    if (ph.Index == 1)
                        TextRenderer.DrawText(g, "最初の番号", uiFont, startLabelRect, ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (ph == hoverPop && ph.Index != 1) using (var b = new SolidBrush(Color.FromArgb(242, 242, 242))) Ui.FillRound(g, b, r, Ui.S(8));
                    else if (ph.Index != 1) using (var p2 = new Pen(Color.FromArgb(150, 150, 156), 1)) Ui.DrawRound(g, p2, Rectangle.Inflate(r, -Ui.S(2), -Ui.S(4)), Ui.S(7));   // ボタンのふちは背景と 3:1 以上の差
                    string label = ph.Index == 0 ? "−" : ph.Index == 2 ? "＋" : StampShape.LabelOf(StartOf(StartRowLetter), StartRowLetter);
                    TextRenderer.DrawText(g, label, uiBold, r, Color.FromArgb(25, 25, 25),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    continue;
                }
                var scb = SelectedCallout;
                bool sel2 = (ph.Type == 1 && level == ph.Index)
                    || (ph.Type == 7 && scb != null && NearestLevel(TextLv, scb.TextSize) == ph.Index)
                    || (ph.Type == 2 && box != null && (int)box.Fill == ph.Index)
                    || (ph.Type == 4 && (int)boxKind == ph.Index)
                    || (ph.Type == 5 && (int)arrowKind == ph.Index)
                    || (ph.Type == 6 && (penTool == Tool.Marker ? 1 : 0) == ph.Index)
                    || (ph.Type == 10 && mosaicKind == ph.Index);
                if (sel2) using (var b = new SolidBrush(Ui.AccentLight)) Ui.FillRound(g, b, r, Ui.S(8));
                Color tc = sel2 ? Ui.Accent : ink;
                // 文字は出さず、絵だけ（名前はマウスを乗せると出る）
                if (ph == hoverPop && !sel2) using (var b = new SolidBrush(Color.FromArgb(242, 242, 242))) Ui.FillRound(g, b, r, Ui.S(8));
                var icon = new Rectangle(r.Left + (r.Width - Ui.S(26)) / 2, r.Top + (r.Height - Ui.S(20)) / 2, Ui.S(26), Ui.S(20));

                if (ph.Type == 7)
                {
                    // 吹き出しの文字の大きさ（小・中・大）
                    var ta = new Rectangle(r.Left, r.Top, r.Width, r.Height - Ui.S(5));
                    using (var f = new Font(Ui.TextFamily, Ui.S(new[] { 12f, 18f, 26f }[ph.Index]), FontStyle.Bold, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, "A", f, ta, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
                }
                else if (ph.Type == 1)
                {
                    if (SizeIsStamp)
                    {
                        float dd = Ui.S(new[] { 10f, 14f, 19f }[ph.Index]);
                        using (var b = new SolidBrush(tc)) g.FillEllipse(b, r.Left + (r.Width - dd) / 2f, r.Top + (r.Height - dd) / 2f, dd, dd);
                    }
                    else if (SizeIsText)
                    {
                        var ta = new Rectangle(r.Left, r.Top, r.Width, r.Height - Ui.S(5));
                        using (var f = new Font(Ui.TextFamily, Ui.S(new[] { 12f, 18f, 26f }[ph.Index]), FontStyle.Bold, GraphicsUnit.Pixel))
                            TextRenderer.DrawText(g, "A", f, ta, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
                    }
                    else
                    {
                        float th = Ui.S(new[] { 1.5f, 3f, 5.5f }[ph.Index]);
                        using (var p = new Pen(tc, th)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, icon.Left + 2, icon.Top + icon.Height / 2, icon.Right - 2, icon.Top + icon.Height / 2); }
                    }
                }
                else if (ph.Type == 2)
                {
                    Color bc = box != null ? box.Color : color;
                    Color fc = box != null && box.Fill != FillMode.None ? box.FillColor : bc;
                    var sr = new RectangleF(icon.Left + Ui.S(2), icon.Top + Ui.S(3), icon.Width - Ui.S(4), icon.Height - Ui.S(6));
                    if (ph.Index == 1) using (var b = new SolidBrush(Color.FromArgb(70, fc))) Ui.FillRound(g, b, sr, Ui.S(4));
                    if (ph.Index == 2) using (var b = new SolidBrush(fc)) Ui.FillRound(g, b, sr, Ui.S(4));
                    using (var p = new Pen(bc, Ui.S(2))) Ui.DrawRound(g, p, sr, Ui.S(4));
                }
                else if (ph.Type == 4)
                {
                    int bs = Ui.S(22);   // 書き込みメニューの絵と同じ大きさ
                    Icons.Box(g, (BoxKind)ph.Index, new Rectangle(r.Left + (r.Width - bs) / 2, r.Top + (r.Height - bs) / 2, bs, bs), tc);
                }
                else if (ph.Type == 5)
                {
                    int bs = Ui.S(22);   // 書き込みメニューの絵と同じ大きさ
                    Icons.ArrowOf(g, (ArrowKind)ph.Index, new Rectangle(r.Left + (r.Width - bs) / 2, r.Top + (r.Height - bs) / 2, bs, bs), tc);
                }
                else if (ph.Type == 8)
                {
                    int bs = Ui.S(22);   // 書き込みメニューの絵と同じ大きさ
                    Icons.Box(g, (BoxKind)ph.Index, new Rectangle(r.Left + (r.Width - bs) / 2, r.Top + (r.Height - bs) / 2, bs, bs), tc);
                }
                else if (ph.Type == 6)
                {
                    int bs = Ui.S(22);   // 書き込みメニューの絵と同じ大きさ
                    Icons.Tool(g, ph.Index == 0 ? Tool.Pen : Tool.Marker, new Rectangle(r.Left + (r.Width - bs) / 2, r.Top + (r.Height - bs) / 2, bs, bs), tc, uiBold);
                }
                else if (ph.Type == 10)
                {
                    int bs = Ui.S(22);   // 書き込みメニューの絵と同じ大きさ
                    var br = new Rectangle(r.Left + (r.Width - bs) / 2, r.Top + (r.Height - bs) / 2, bs, bs);
                    if (ph.Index == 0) Icons.Tool(g, Tool.Mosaic, br, tc, uiBold); else Icons.SolidFill(g, br, FillColors[ph.Index], tc);
                }
            }

            // 2段あるときは間に薄い線
            if (!colorsRow.IsEmpty && !optRow.IsEmpty)
            {
                int y = colorsRow.Top > optRow.Top ? (optRow.Bottom + colorsRow.Top) / 2 : (colorsRow.Bottom + optRow.Top) / 2;
                using (var p = new Pen(Color.FromArgb(225, 225, 225), 1))
                    g.DrawLine(p, popupRect.Left + Ui.S(10), y, popupRect.Right - Ui.S(10), y);
            }
            if (!textRow.IsEmpty && !optRow.IsEmpty)
            {
                int y = (optRow.Bottom + textRow.Top) / 2;
                using (var p = new Pen(Color.FromArgb(225, 225, 225), 1))
                    g.DrawLine(p, popupRect.Left + Ui.S(10), y, popupRect.Right - Ui.S(10), y);
            }

            // マウスを乗せている項目の名前（文字の一覧は名前が見えているので出さない）
            if (hoverPop != null && hoverPop.Type != 9) DrawTip(g, PopLabel(hoverPop), hoverPop.R, popupRect);
        }

        static readonly string[] CopyNames = { "コピーして閉じる", "コピーして、保存もする", "コピーして、名前を付けて保存..." };
        static readonly string[] CopyKeys = { "Enter", "Ctrl+S", "" };
        // 右クリック（範囲の外のクリック）で開いたときだけ、4 つ目に「やめる」（長い画像では「確認画面に戻る」）
        int CopyCount { get { return copyMenuAt.IsEmpty ? CopyNames.Length : CopyNames.Length + 1; } }
        string CopyNameAt(int i) { return i < CopyNames.Length ? CopyNames[i] : imageMode ? "確認画面に戻る" : "やめる（書き込みも消える）"; }
        string CopyKeyAt(int i) { return i < CopyKeys.Length ? CopyKeys[i] : "Esc"; }

        PopHit hoverPop;

        string PopLabel(PopHit ph)
        {
            switch (ph.Type)
            {
                case 0: return Ui.PaletteNames[ph.Index];
                case 1: return SizeIsText ? "文字：" + TextSizeNames[ph.Index] : SizeIsStamp ? "大きさ：" + TextSizeNames[ph.Index] : "太さ：" + SizeNames[ph.Index];
                case 2: return "塗り：" + FillNames[ph.Index];
                case 3: return "塗りの色：" + Ui.PaletteNames[ph.Index];
                case 4: return BoxNames[ph.Index];
                case 6: return PenNames[ph.Index];
                case 10: return MosaicNames[ph.Index];
                case 7: return "文字の大きさ：" + TextSizeNames[ph.Index];
                case 11: return ph.Index == 0 ? "最初の番号を減らす" : ph.Index == 2 ? "最初の番号を増やす" : "最初の番号（ここから順に増える）";
                case 8: return BoxNames[ph.Index] + "で囲む";
                default: return ArrowNames[ph.Index];
            }
        }

        // 小さな吹き出し（一覧の下に出す。入らなければ上）
        // 小さな吹き出し。押せるもの（書き込みメニュー・小メニュー・一覧）に重ならない場所を順に探す：
        // 対象の帯（avoid）の下 → 上 → 一覧なら右 → 左
        public Rectangle lastTipRect;   // 自動テスト用
        void DrawTip(Graphics g, string text, Rectangle target, Rectangle avoid)
        {
            Ui.Setup(g);
            var sz = TextRenderer.MeasureText(text, uiFont);
            int w = sz.Width + Ui.S(14), h = sz.Height + Ui.S(8), gap = Ui.S(6);
            Rectangle mon = MonitorOf(sel);
            int cx = Math.Max(mon.Left + 2, Math.Min(target.Left + (target.Width - w) / 2, mon.Right - w - 2));
            int cy = Math.Max(mon.Top + 2, Math.Min(target.Top + (target.Height - h) / 2, mon.Bottom - h - 2));
            var tries = new[] {
                new Rectangle(cx, avoid.Bottom + gap, w, h),
                new Rectangle(cx, avoid.Top - h - gap, w, h),
                new Rectangle(avoid.Right + gap, cy, w, h),
                new Rectangle(avoid.Left - w - gap, cy, w, h) };
            var blockers = new List<Rectangle> { toolbarRect };
            if (!menuRect.IsEmpty) blockers.Add(menuRect);
            if (popupOpen) blockers.Add(popupRect);
            Rectangle r = tries[0];
            foreach (var t in tries)
            {
                if (t.Left < mon.Left || t.Top < mon.Top || t.Right > mon.Right || t.Bottom > mon.Bottom) continue;
                bool hit = false;
                foreach (var b in blockers) if (Rectangle.Inflate(b, 2, 2).IntersectsWith(t)) { hit = true; break; }
                if (!hit) { r = t; break; }
            }
            lastTipRect = r;
            using (var b = new SolidBrush(Color.FromArgb(235, 40, 40, 40))) Ui.FillRound(g, b, r, Ui.S(6));
            TextRenderer.DrawText(g, text, uiFont, r, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        void DrawTooltip(Graphics g, UiItem it)
        {
            bool inMenu = it.Kind == ItemKind.MenuColor || it.Kind == ItemKind.MenuFill || it.Kind == ItemKind.MenuEdit || it.Kind == ItemKind.MenuFit || it.Kind == ItemKind.MenuWrap || it.Kind == ItemKind.MenuDelete
                || it.Kind == ItemKind.MenuUp || it.Kind == ItemKind.MenuDown;
            // 一覧が開いているときは、その一覧のボタンの名前は出さない（一覧に重ねない）
            if (inMenu && popupOpen && popupForShape) return;
            DrawTip(g, it.Kind == ItemKind.Color ? ColorButtonTip() : it.Label, it.R, inMenu ? menuRect : toolbarRect);
        }

        // 色・太さのボタンの説明：いまの色と太さと、次に撮るときもこの色と太さから始まること
        string ColorButtonTip()
        {
            int ci = Array.FindIndex(Ui.Palette, c => c.ToArgb() == color.ToArgb());
            string cname = ci >= 0 ? Ui.PaletteNames[ci] : "";
            string[] names = tool == Tool.Text || StampMode ? TextSizeNames : SizeNames;
            string what = tool == Tool.Text ? "色・文字の大きさ" : StampMode ? "色・大きさ" : "色・太さ";
            return what + "：" + cname + "・" + names[Math.Max(0, Math.Min(2, sizeLevel))] + "（次に撮るときも、この色と" + (tool == Tool.Text || StampMode ? "大きさ" : "太さ") + "から始まります）";
        }

        // ---------- マウス ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Point p = e.Location;
            if (imageMode && p.Y < HeaderH)
            {
                // 長い画像の窓の見出し：「確認画面に戻る」か、窓を動かす
                if (e.Button != MouseButtons.Left) return;
                if (textBox != null) CommitText();
                if (backRect.Contains(p)) { Cancel(); return; }
                headerDrag = true; headerDragOffset = p; Capture = true;
                return;
            }
            ClearEnterGuard();
            if (!editing)
            {
                if (e.Button == MouseButtons.Right) { Cancel(); return; }
                if (e.Button != MouseButtons.Left) return;
                int hh = HintHit(p);
                if (timerListOpen)
                {
                    // 秒数を選んだら、その秒数でタイマー撮影（次からもその秒数）。一覧の外なら閉じるだけ
                    if (hh >= 10) { RequestTimer(Settings.TimerChoices[hh - 10], true); return; }
                    timerListOpen = false;
                    Invalidate(HintArea);
                    return;
                }
                if (hh == 3) { Cancel(); return; }
                if (hh == 4) { EnterPick(); return; }
                if (hh == 5) { SelectLastArea(); return; }
                if (hh == 1) { RequestTimer(Settings.TimerSeconds, false); return; }
                if (hh == 2) { timerListOpen = true; Invalidate(HintArea); return; }
                if (hh == 0) { SelectFullScreen(startMon); return; }
                drag = DragMode.Select;
                downPt = p;
                sel = Rectangle.Empty;
                Invalidate();
                return;
            }
            // 書き込み中の右クリック：すぐには閉じず、コピー・保存のしかたの一覧を、クリックした所の近くに出す（入力中の文字は確定してから）
            if (e.Button == MouseButtons.Right)
            {
                if (textBox != null) CommitText();
                OpenCopyMenuAt(p);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 文字の入力中：確定する。書き込みメニューのボタンを押したときは、そのままそのボタンも効かせる（道具をすぐ切り替える）
            if (textBox != null) { CommitText(); Redraw(); if (HitItem(p) == null) return; }

            PopupKind closed = PopupKind.None;
            if (popupOpen)
            {
                foreach (var ph in popHits)
                {
                    var r = ph.R; r.Inflate(Ui.S(4), Ui.S(4));
                    if (r.Contains(p)) { PickPopup(ph); return; }
                }
                closed = popup;
                bool wasForShape = popupForShape;
                popupOpen = false;
                Redraw();
                // 一覧の外をクリックしたとき：メニューのボタンはそのまま押せる。
                // 道具の一覧なら、範囲の中ではそのまま描き始められる。それ以外は「閉じる」だけ
                if (HitItem(p) == null && (wasForShape || !sel.Contains(p))) return;
                // クリックだけなら閉じるだけ、ドラッグしたら描く
                closedByThisClick = HitItem(p) == null;
            }
            else closedByThisClick = false;

            UiItem item = HitItem(p);
            // 開いていた一覧のボタンをもう一度押したら、閉じるだけ
            if (item != null && TogglesPopup(item, closed)) { RelayoutUi(); Redraw(); return; }
            // つかむ所、またはボタン以外の余白をつかんだらメニューを移動
            if ((item != null && item.Kind == ItemKind.Grip) || (item == null && toolbarRect.Contains(p)))
            {
                drag = DragMode.MoveToolbar; downPt = p; toolbarAtDown = toolbarRect.Location;
                Cursor = Cursors.SizeAll;
                Redraw();
                return;
            }
            if (item != null) { DoItem(item); return; }

            // 選んだ図形のつまみ（範囲のつまみより優先）
            int sh = HitShapeHandle(p);
            if (sh >= 0)
            {
                PushUndo();
                drag = DragMode.ResizeShape; shapeHandle = sh; downPt = p; moved = false;
                shapeRectAtDown = ShapeRect(selected);
                if (selected is ArrowShape) { arrowFromAtDown = ((ArrowShape)selected).From; arrowToAtDown = ((ArrowShape)selected).To; }
                if (selected is TextShape) { textSizeAtDown = ((TextShape)selected).Size; textLocAtDown = ((TextShape)selected).Location; }
                return;
            }

            int h = HitHandle(p);
            if (h >= 0)
            {
                PushUndo();
                drag = DragMode.Resize; resizeHandle = h; downPt = p; selAtDown = sel; moved = false;
                return;
            }

            if (selected != null && selected.HitTest(p))
            {
                PushUndo();
                drag = DragMode.Move; lastPt = p; moved = false;
                moveDownPt = p;
                if (selected is StampShape) stampCenterAtDown = ((StampShape)selected).Center;
                return;
            }

            if (sel.Contains(p)) { drag = DragMode.PendingDraw; downPt = p; return; }

            // 安全域①：範囲のふちの近く → やめずに、ドラッグなら範囲の調整
            int eh = EdgeZoneHandle(p);
            if (eh >= 0)
            {
                PushUndo();
                drag = DragMode.Resize; resizeHandle = eh; downPt = p; selAtDown = sel; moved = false;
                return;
            }
            // 安全域②：メニュー・一覧の周り → 何もしない（選択中なら選択を外すだけ）
            if (NearUi(p))
            {
                if (selected != null) { selected = null; RelayoutUi(); Redraw(); }
                return;
            }

            // 範囲の外をクリック：選択中なら選択を外すだけ。そうでなければ、右クリックと同じく
            // コピー・保存のしかたの一覧を出す（うっかり押して、書き込みが消えてしまわないように。やめるのは × か Esc）
            if (selected != null) { selected = null; RelayoutUi(); Redraw(); }
            else OpenCopyMenuAt(p);
        }

        // コピー・保存のしかたの一覧を、p の近くに出す（もう出ていれば閉じる）
        Point copyMenuAt = Point.Empty;   // 空なら「コピーして閉じる ▾」の下に出す
        void OpenCopyMenuAt(Point p)
        {
            selected = null;
            popupForShape = false;
            // 右クリックで開いた一覧がもう出ていれば閉じる。「▾」から開いた一覧なら、クリックした所へ出し直す
            if (popup == PopupKind.CopyKind && !copyMenuAt.IsEmpty) popup = PopupKind.None;
            else { popup = PopupKind.CopyKind; copyMenuAt = p; }
            RelayoutUi(); Redraw();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.Location;
            if (imageMode)
            {
                if (headerDrag) { Location = new Point(Cursor.Position.X - headerDragOffset.X, Cursor.Position.Y - headerDragOffset.Y); return; }
                if (drag == DragMode.None && p.Y < HeaderH)
                {
                    bool hb = backRect.Contains(p);
                    if (hb != hoverBack) { hoverBack = hb; Invalidate(new Rectangle(0, 0, vs.Width, HeaderH)); }
                    Cursor = hb ? Cursors.Hand : Cursors.Default;
                    return;
                }
                if (hoverBack) { hoverBack = false; Invalidate(new Rectangle(0, 0, vs.Width, HeaderH)); }
            }
            curPt = p;
            switch (drag)
            {
                case DragMode.Select:
                {
                    Rectangle old = sel;
                    sel = Geo.Norm(downPt, p);
                    Rectangle inv = old.IsEmpty ? new Rectangle(0, 0, vs.Width, vs.Height) : Rectangle.Union(old, sel);
                    inv.Inflate(Ui.S(10), Ui.S(40));
                    Invalidate(inv);
                    return;
                }
                case DragMode.MoveToolbar:
                    toolbarMoved = true;
                    toolbarPos = new Point(toolbarAtDown.X + p.X - downPt.X, toolbarAtDown.Y + p.Y - downPt.Y);
                    RelayoutUi(); Redraw();
                    return;
                case DragMode.Resize:
                    sel = ResizeSel(selAtDown, resizeHandle, p.X - downPt.X, p.Y - downPt.Y);
                    moved = true;
                    RelayoutUi(); Redraw();
                    return;
                case DragMode.ResizeShape:
                    if (selected is ArrowShape)
                    {
                        var a = (ArrowShape)selected;
                        if (shapeHandle == 0) SetArrowEnd(a, true, p, a.To);
                        else SetArrowEnd(a, false, p, a.From);
                    }
                    else if (selected is TextShape) ResizeText((TextShape)selected, shapeHandle, p.X - downPt.X, p.Y - downPt.Y);
                    else if (selected is BoxShape && ((BoxShape)selected).IsCallout && shapeHandle == 8)
                    { ((BoxShape)selected).Tail = p; ((BoxShape)selected).TailMoved = true; }   // しっぽの先を動かす
                    else
                    {
                        SetShapeRect(selected, ResizeKeep(shapeRectAtDown, shapeHandle, p.X - downPt.X, p.Y - downPt.Y, ShiftDown));
                        // 吹き出しは、中の文字がはみ出す大きさにはしない
                        var cb = selected as BoxShape;
                        if (cb != null && cb.CanHoldText)
                        {
                            Size need = cb.TextNeed;
                            if (!need.IsEmpty)
                            {
                                Rectangle r = cb.Rect;
                                bool leftMoves = shapeHandle == 0 || shapeHandle == 6 || shapeHandle == 7;
                                bool topMoves = shapeHandle == 0 || shapeHandle == 1 || shapeHandle == 2;
                                if (r.Width < need.Width) { if (leftMoves) r.X = r.Right - need.Width; r.Width = need.Width; }
                                if (r.Height < need.Height) { if (topMoves) r.Y = r.Bottom - need.Height; r.Height = need.Height; }
                                cb.Rect = r;
                            }
                            cb.SyncTail();
                        }
                    }
                    moved = true;
                    RelayoutUi(); Redraw();
                    return;
                case DragMode.Move:
                    if (p != lastPt)
                    {
                        // 矢印そのものを動かしたら、くっつきは外す
                        var ma = selected as ArrowShape;
                        if (ma != null) { ma.FromAnchor = null; ma.ToAnchor = null; }
                        var mst = selected as StampShape;
                        if (mst != null)
                            // スタンプは動かしながら図形の点にくっつく（離れたら外れる）
                            SetStampCenter(mst, new Point(stampCenterAtDown.X + p.X - moveDownPt.X, stampCenterAtDown.Y + p.Y - moveDownPt.Y));
                        else selected.Offset(p.X - lastPt.X, p.Y - lastPt.Y);
                        lastPt = p; moved = true;
                        RelayoutUi(); Redraw();
                    }
                    return;
                case DragMode.PendingDraw:
                    if (Math.Abs(p.X - downPt.X) + Math.Abs(p.Y - downPt.Y) > Ui.S(3))
                    {
                        if (tool == Tool.Text || StampMode)
                        {
                            // 文字・スタンプはクリックで置く。ただし置いてある同じ種類のものをつかんでドラッグしたら、それを動かす
                            Shape grabbed = HitShape(downPt);
                            bool same = StampMode ? grabbed is StampShape : grabbed is TextShape;
                            if (same)
                            {
                                selected = grabbed;
                                PushUndo();
                                drag = DragMode.Move; lastPt = downPt; moved = false;
                                moveDownPt = downPt;
                                if (selected is StampShape) stampCenterAtDown = ((StampShape)selected).Center;
                                RelayoutUi(); Redraw();
                            }
                            return;
                        }
                        StartDrawing();
                        drag = DragMode.Draw;
                        UpdateDrawing(p);
                        Redraw();
                    }
                    return;
                case DragMode.Draw:
                    UpdateDrawing(p);
                    Redraw();
                    return;
            }

            if (!editing)
            {
                int hh = HintHit(p);
                if (hh != hoverHint) { hoverHint = hh; Invalidate(HintArea); }
                Cursor want = hh >= 0 ? Cursors.Hand : Cursors.Cross;
                if (Cursor != want) Cursor = want;

                // ウィンドウの上にマウスがあれば、その枠を明るく表示する（案内の帯の上では出さない）
                Rectangle w = hintRect.Contains(p) ? Rectangle.Empty : WindowAt(p);
                if (w.IsEmpty && !hintRect.Contains(p)) w = MonitorOf(new Rectangle(p, new Size(1, 1)));
                if (w != hoverWin)
                {
                    Rectangle inv = hoverWin.IsEmpty ? w : w.IsEmpty ? hoverWin : Rectangle.Union(hoverWin, w);
                    inv.Inflate(Ui.S(6), Ui.S(6));
                    hoverWin = w;
                    Invalidate(inv);
                }
                return;
            }
            UiItem h = HitItem(p);
            if (h != hover) { hover = h; Redraw(); }
            else if (StampMode && textBox == null) Redraw();   // スタンプの道具：くっつく点の表示をマウスに合わせて更新
            PopHit hp = null;
            if (popupOpen) foreach (var ph in popHits) if (ph.R.Contains(p)) hp = ph;
            if (hp != hoverPop) { hoverPop = hp; Redraw(); }
            UpdateCursor(p);
        }

        // ダブルクリック：文字や吹き出しなら、すぐ文字の入力に入る
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!editing || e.Button != MouseButtons.Left || textBox != null) return;
            Shape hit = HitShape(e.Location);
            if (hit is TextShape) { drag = DragMode.None; BeginText(((TextShape)hit).Location, (TextShape)hit); Redraw(); }
            else if (hit is StampShape) { drag = DragMode.None; selected = hit; BeginEditSelected(); Redraw(); }
            else if (hit is BoxShape && ((BoxShape)hit).CanHoldText) { drag = DragMode.None; BeginCalloutText((BoxShape)hit); Redraw(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (headerDrag) { headerDrag = false; Capture = false; return; }
            if (e.Button != MouseButtons.Left) return;
            Point p = e.Location;
            DragMode d = drag;
            drag = DragMode.None;

            switch (d)
            {
                case DragMode.Select:
                {
                    Rectangle r = sel;
                    if (PickOnly)
                    {
                        // つなげて撮る：クリックだけならその位置のウィンドウ（なければ画面全体）
                        if (r.Width < 4 || r.Height < 4) { r = WindowAt(p); if (r.IsEmpty) r = MonitorOf(new Rectangle(p, new Size(1, 1))); }
                        hoverWin = Rectangle.Empty;
                        FinishPick(r);
                        return;
                    }
                    if (r.Width < 4 || r.Height < 4)
                    {
                        // クリックだけ：その位置のウィンドウ（なければ画面全体）。ウィンドウは、その窓だけの中身にする
                        r = WindowAt(p);
                        int wi = r.IsEmpty ? -1 : windows.IndexOf(r);
                        if (wi >= 0 && wi < windowHandles.Count) UseWindowOnly(windowHandles[wi]);
                        if (r.IsEmpty) r = MonitorOf(new Rectangle(p, new Size(1, 1)));
                    }
                    hoverWin = Rectangle.Empty;
                    r.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
                    sel = r;
                    editing = true;
                    RelayoutUi();
                    Invalidate();
                    UpdateCursor(p);
                    return;
                }
                case DragMode.MoveToolbar:
                    UpdateCursor(p); Redraw();
                    return;
                case DragMode.ResizeShape:
                    if (!moved)
                    {
                        undo.RemoveAt(undo.Count - 1);
                        // 選んでいる図形のつまみ（＝くっつく点）をスタンプの道具でクリックしただけなら、そこにスタンプを置く
                        if (StampMode && selected is BoxShape && Attachable(selected)) { selected = null; PlaceStamp(p); RelayoutUi(); Redraw(); return; }
                    }
                    else if (selected is MosaicShape) RefreshMosaic((MosaicShape)selected);
                    RelayoutUi(); Invalidate();
                    return;
                case DragMode.Resize:
                    if (!moved || sel == selAtDown) undo.RemoveAt(undo.Count - 1);
                    RelayoutUi(); Invalidate();
                    return;
                case DragMode.Move:
                    if (!moved)
                    {
                        undo.RemoveAt(undo.Count - 1);
                        // 選んでいる図形の中をクリックしただけ：文字・スタンプの道具なら、そこに置く
                        // スタンプの道具で、選んでいる図形のくっつく点の近くをクリックしただけなら、スタンプを置く
                        Shape sh0; int si0; Point sp0;
                        if (StampMode && !(selected is StampShape) && !AltDown && FindSnap(p, p, out sh0, out si0, out sp0, true))
                        { selected = null; PlaceStamp(p); RelayoutUi(); Redraw(); return; }
                    }
                    else if (selected is MosaicShape) RefreshMosaic((MosaicShape)selected);
                    RelayoutUi(); Redraw();
                    return;
                case DragMode.PendingDraw:
                {
                    if (closedByThisClick) { closedByThisClick = false; RelayoutUi(); Redraw(); return; }
                    Shape hit = HitShape(p);
                    // 文字・スタンプの道具では、同じ種類のものだけクリックで選ぶ（吹き出しや図形の中にも置けるように）
                    // どの道具でも、置いてあるものをクリックすれば選ぶ。
                    // ただしスタンプの道具で、図形のくっつく点の近くなら、スタンプを置く方を優先
                    Shape sh1; int si1; Point sp1;
                    if (StampMode && !(hit is StampShape) && !AltDown && FindSnap(p, p, out sh1, out si1, out sp1, true))
                    { selected = null; PlaceStamp(downPt); RelayoutUi(); Redraw(); return; }
                    if (hit != null) selected = hit;
                    else
                    {
                        selected = null;
                        if (tool == Tool.Text) { RelayoutUi(); BeginText(p, null); Redraw(); return; }
                        if (StampMode) { PlaceStamp(downPt); RelayoutUi(); Redraw(); return; }
                    }
                    RelayoutUi(); Redraw();
                    return;
                }
                case DragMode.Draw:
                    FinishDrawing();
                    RelayoutUi(); Redraw();
                    return;
            }
        }

        void UpdateCursor(Point p)
        {
            Cursor c = Cursors.Cross;
            UiItem hi = HitItem(p);
            if ((hi != null && hi.Kind == ItemKind.Grip) || (hi == null && toolbarRect.Contains(p))) c = Cursors.SizeAll;
            else if (hi != null || (popupOpen && popupRect.Contains(p))) c = Cursors.Hand;
            else if (HitShapeHandle(p) >= 0)
            {
                int sh = HitShapeHandle(p);
                if (selected is ArrowShape) c = Cursors.SizeAll;
                else if (sh == 8) c = Cursors.SizeAll;   // 吹き出しのしっぽ
                else if (sh == 0 || sh == 4) c = Cursors.SizeNWSE;
                else if (sh == 2 || sh == 6) c = Cursors.SizeNESW;
                else if (sh == 1 || sh == 5) c = Cursors.SizeNS;
                else c = Cursors.SizeWE;
            }
            else
            {
                int h = HitHandle(p);
                if (h < 0) h = EdgeZoneHandle(p);
                if (h == 0 || h == 4) c = Cursors.SizeNWSE;
                else if (h == 2 || h == 6) c = Cursors.SizeNESW;
                else if (h == 1 || h == 5) c = Cursors.SizeNS;
                else if (h == 3 || h == 7) c = Cursors.SizeWE;
                else if (selected != null && selected.HitTest(p)) c = Cursors.SizeAll;
                else if (HitShape(p) != null) c = Cursors.Hand;
                else if (!sel.Contains(p)) c = Cursors.Default;
                else if (tool == Tool.Text) c = Cursors.IBeam;
            }
            if (Cursor != c) Cursor = c;
        }

        Rectangle ResizeSel(Rectangle r, int h, int dx, int dy)
        {
            int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            if (h == 0 || h == 6 || h == 7) l += dx;
            if (h == 2 || h == 3 || h == 4) rt += dx;
            if (h == 0 || h == 1 || h == 2) t += dy;
            if (h == 4 || h == 5 || h == 6) b += dy;
            var n = Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b));
            n.Intersect(SelLimit);
            if (n.Width < 8) n.Width = 8;
            if (n.Height < 8) n.Height = 8;
            return n;
        }

        // ---------- 書き込み ----------
        void StartDrawing()
        {
            selected = null;
            switch (tool)
            {
                case Tool.Pen:
                case Tool.Marker:
                    var s = new StrokeShape();
                    s.Color = color; s.Marker = tool == Tool.Marker; s.Width = s.Marker ? MarkerW : StrokeW;
                    s.Points.Add(downPt);
                    straightAnchor = -1;
                    drawing = s; break;
                case Tool.Arrow:
                    var a = new ArrowShape(); a.Color = color; a.Width = StrokeW; a.Kind = arrowKind; a.From = a.To = downPt;
                    // 描き始めの点が図形の近くなら、そこにくっつける
                    SetArrowEnd(a, true, downPt, downPt);
                    drawing = a; break;
                case Tool.Shape:
                    var r = new BoxShape();
                    r.Color = color; r.Width = StrokeW; r.Kind = boxKind; r.Fill = FillMode.None; r.FillColor = color;
                    // 吹き出しは中に文字を書きやすいよう、白く塗っておく（塗りはあとから変えられる）
                    if (boxKind == BoxKind.Callout) { r.Fill = FillMode.Solid; r.FillColor = Color.White; r.TextSize = TextPx; r.Tail = downPt; }
                    r.Rect = new Rectangle(downPt, Size.Empty);
                    drawing = r; break;
                case Tool.Mosaic:
                    drawing = null; mosaicPreview = new Rectangle(downPt, Size.Empty); break;
            }
        }

        void UpdateDrawing(Point p)
        {
            if (drawing is StrokeShape)
            {
                var s = (StrokeShape)drawing;
                if (ShiftDown)
                {
                    // Shift を押している間：押し始めた点から今の位置まで、45 度ごとにそろえた直線
                    if (straightAnchor < 0)
                    {
                        straightAnchor = s.Points.Count - 1;
                        if (!s.Corners.Contains(straightAnchor)) s.Corners.Add(straightAnchor);
                    }
                    Point a0 = s.Points[straightAnchor];
                    Point q = Snap45(a0, p);
                    s.Points.RemoveRange(straightAnchor + 1, s.Points.Count - straightAnchor - 1);
                    s.Corners.RemoveAll(delegate(int c) { return c > straightAnchor; });
                    if (q != a0) { s.Points.Add(q); s.Corners.Add(s.Points.Count - 1); }
                }
                else
                {
                    // 離したら、直線の終わりから手描きに戻る
                    straightAnchor = -1;
                    Point last = s.Points[s.Points.Count - 1];
                    if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 2) s.Points.Add(p);
                }
            }
            else if (drawing is ArrowShape)
            {
                var a = (ArrowShape)drawing;
                // 描き始めの点がスタンプなら、向きに合わせてふちの点を選び直す
                a.From = a.FromAnchor != null ? AnchorPoint(a.FromAnchor, a.FromIdx, p) : downPt;
                SetArrowEnd(a, false, p, a.From);
                if (a.FromAnchor != null) a.From = AnchorPoint(a.FromAnchor, a.FromIdx, a.To);
            }
            else if (drawing is BoxShape)
            {
                var bx = (BoxShape)drawing;
                bx.Rect = ShiftDown ? SquareFrom(downPt, p) : Geo.Norm(downPt, p);
                if (bx.IsCallout) bx.Tail = BoxShape.DefaultTail(bx.Rect);
            }
            else if (tool == Tool.Mosaic) mosaicPreview = ShiftDown ? SquareFrom(downPt, p) : Geo.Norm(downPt, p);
        }

        void FinishDrawing()
        {
            Shape done = null;
            if (drawing is StrokeShape) { done = drawing; ((StrokeShape)drawing).Smooth(); }
            else if (drawing is ArrowShape)
            {
                var a = (ArrowShape)drawing;
                if (Math.Abs(a.From.X - a.To.X) + Math.Abs(a.From.Y - a.To.Y) > Ui.S(6)) done = a;
            }
            else if (drawing is BoxShape)
            {
                var r = (BoxShape)drawing;
                if (r.Rect.Width > 3 && r.Rect.Height > 3) done = r;
            }
            else if (tool == Tool.Mosaic && mosaicPreview.Width > 3 && mosaicPreview.Height > 3)
            {
                var m = new MosaicShape();
                m.Rect = mosaicPreview; m.Block = MosaicBlock; m.Color = Color.Gray; m.Solid = mosaicFill; m.SolidColor = FillColors[mosaicKind];
                RefreshMosaic(m);
                done = m;
            }
            drawing = null;
            mosaicPreview = Rectangle.Empty;
            if (done == null) return;
            PushUndo();
            if (done is MosaicShape)
            {
                // モザイクは他の書き込みより下に入れる（書き込みが隠れないように）
                int idx = 0;
                while (idx < shapes.Count && shapes[idx] is MosaicShape) idx++;
                shapes.Insert(idx, done);
            }
            else shapes.Add(done);
            // 吹き出しは、描いたらすぐ中に文字を書ける
            var cdone = done as BoxShape;
            if (cdone != null && cdone.IsCallout) BeginCalloutText(cdone);
        }

        // ---------- ボタン・メニュー ----------
        void DoItem(UiItem it)
        {
            switch (it.Kind)
            {
                case ItemKind.Tool:
                    // 図形・矢印は、色と同じく 1 回押すと道具を選んで種類の一覧も開く
                    tool = it.Tool == Tool.Pen ? penTool : it.Tool;
                    if (HasSubmenu(it.Tool)) { popupForShape = false; popup = SubmenuOf(it.Tool); }
                    else popup = PopupKind.None;
                    selected = null; break;
                case ItemKind.Color:
                    popupForShape = false; popup = PopupKind.ColorSize; break;
                case ItemKind.MenuFill:
                    popupForShape = true; popup = PopupKind.Fill; break;
                case ItemKind.Undo:
                    Undo(); return;
                case ItemKind.Copy:
                    CopyAndClose(); return;
                case ItemKind.CopyMenu:
                    popupForShape = false; popup = PopupKind.CopyKind; copyMenuAt = Point.Empty; break;
                case ItemKind.Cancel:
                    Cancel(); return;
                case ItemKind.MenuColor:
                    popupForShape = true; popup = PopupKind.ColorSize; break;
                case ItemKind.MenuEdit:
                    BeginEditSelected();
                    break;
                case ItemKind.MenuUp:
                case ItemKind.MenuDown:
                    if (selected is StampShape) MoveStampNumber((StampShape)selected, it.Kind == ItemKind.MenuUp ? 1 : -1);
                    return;
                case ItemKind.MenuFit:
                {
                    // 文字に合わせる：中心を保って、文字にぴったりの大きさに
                    var fb = SelectedCallout;
                    if (fb != null && fb.HasText && fb.Rect.Size != fb.TextNeed) { PushUndo(); fb.FitTextCentered(); }
                    break;
                }
                case ItemKind.MenuWrap:
                    popupForShape = true; popup = PopupKind.WrapKind; break;
                case ItemKind.MenuDelete:
                    DeleteSelected(); return;
            }
            RelayoutUi();
            Redraw();
        }

        void PickPopup(PopHit ph)
        {
            Shape target = popupForShape ? selected : null;
            var box = target as BoxShape;
            switch (ph.Type)
            {
                case 0: // 色
                {
                    Color c = Ui.Palette[ph.Index];
                    if (target != null) { if (target.Color.ToArgb() != c.ToArgb()) { PushUndo(); target.Color = c; } }
                    else { color = c; Settings.LastColor = ph.Index; Settings.SaveIfLoaded(); }   // メインメニューで選んだ色は次回も使う
                    break;
                }
                case 1: // 太さ・文字の大きさ
                    if (target != null) { if (CurrentLevel() != ph.Index) { PushUndo(); ApplyLevel(target, ph.Index); } }
                    else { sizeLevel = ph.Index; Settings.LastSize = ph.Index; Settings.SaveIfLoaded(); }
                    break;
                case 2: // 塗り方
                    if (box != null && (int)box.Fill != ph.Index)
                    {
                        PushUndo();
                        if (box.Fill == FillMode.None) box.FillColor = box.Color;
                        box.Fill = (FillMode)ph.Index;
                    }
                    break;
                case 3: // 塗りの色（塗りなしなら「うすく」にする）
                    if (box != null)
                    {
                        PushUndo();
                        box.FillColor = Ui.Palette[ph.Index];
                        if (box.Fill == FillMode.None) box.Fill = FillMode.Light;
                    }
                    break;
                case 4: // 形
                    boxKind = (BoxKind)ph.Index;
                    popup = PopupKind.None;
                    break;
                case 5: // 矢印の種類
                    arrowKind = (ArrowKind)ph.Index;
                    popup = PopupKind.None;
                    break;
                case 8: // 文字を選んだ形で囲む
                    WrapText((BoxKind)ph.Index);
                    return;
                case 9: // コピー・保存のしかた
                    popup = PopupKind.None;
                    if (ph.Index == 3) { Cancel(); return; }
                    if (ph.Index == 1) SaveRequested = true;
                    if (ph.Index == 2) SaveAsAndClose(); else CopyAndClose();
                    return;
                case 11: // 最初の番号（− ／ ＋。まん中は表示だけ）
                    if (ph.Index != 1) ChangeStampStart(StartRowLetter, ph.Index == 0 ? -1 : 1);
                    break;
                case 7: // 吹き出しの文字の大きさ
                {
                    var cb = SelectedCallout;
                    if (cb != null && NearestLevel(TextLv, cb.TextSize) != ph.Index)
                    {
                        PushUndo();
                        cb.TextSize = Ui.S(TextLv[ph.Index]);
                        cb.AdjustToText();   // 吹き出しはぴったり、ほかの形は入りきらないときだけ広げる
                    }
                    break;
                }
                case 6: // ペン ／ 蛍光ペン
                    penTool = ph.Index == 0 ? Tool.Pen : Tool.Marker;
                    tool = penTool;
                    popup = PopupKind.None;
                    break;
                case 10: // モザイク ／ 塗りつぶし
                    mosaicKind = ph.Index;
                    tool = Tool.Mosaic;
                    popup = PopupKind.None;
                    break;
            }
            RelayoutUi();
            Redraw();
        }

        void DeleteSelected()
        {
            if (selected == null) return;
            PushUndo();
            shapes.Remove(selected);
            // 番号スタンプを消したら、後ろの番号をつめる（同じ元に戻す 1 回分）
            var dst = selected as StampShape;
            if (dst != null) RenumberStamps(dst.Letter);
            selected = null;
            popupOpen = false;
            RelayoutUi();
            Redraw();
        }

        // ---------- 元に戻す ----------
        void PushUndo()
        {
            // くっついている先も、写した側の図形を指すように付け替えて写す
            var copy = CloneShapes(shapes);
            undo.Add(new KeyValuePair<List<Shape>, Rectangle>(copy, sel));
            if (undo.Count > 100) undo.RemoveAt(0);
        }

        void Undo()
        {
            if (textBox != null) { CancelText(); Redraw(); return; }
            if (undo.Count == 0) return;
            var st = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            shapes = st.Key;
            sel = st.Value;
            selected = null;
            popupOpen = false;
            InferStampStart();   // 最初の番号を変えたのを戻したときも、番号の並びと合わせる
            RelayoutUi();
            Invalidate();
        }

        // 最初の番号を、置いてあるスタンプのいちばん小さい番号に合わせる（スタンプがない種類は、そのまま）
        void InferStampStart()
        {
            foreach (bool letter in new[] { false, true })
            {
                var list = StampsInOrder(letter);
                if (list.Count > 0) stampStart[letter ? 1 : 0] = Math.Max(1, StampShape.IndexOf(list[0].Label, letter));
            }
        }

        // ---------- 文字 ----------
        // 入力欄のまわりの角丸の枠と、その下の「Enter で確定 ／ Shift+Enter で改行」（どちらも画像には写らない）
        Rectangle textFrameRect, textHintRect;
        const string TextHintText = "Enter で確定 ／ Shift+Enter で改行";
        bool TextOnDark { get { return textBox != null && textBox.BackColor.GetBrightness() < 0.5f; } }

        void DrawTextFrameHint(Graphics g)
        {
            if (textBox == null || textFrameRect.IsEmpty) return;
            Ui.Setup(g);
            var fr = textFrameRect;
            float rad = Ui.S(8);
            if (editingCallout == null)
            {
                // やわらかい影
                for (int i = 3; i >= 1; i--)
                {
                    var sr = new RectangleF(fr.X, fr.Y + i * 1.5f, fr.Width, fr.Height);
                    sr.Inflate(i, i);
                    using (var b = new SolidBrush(Color.FromArgb(22, 0, 0, 0))) Ui.FillRound(g, b, sr, rad + i);
                }
                using (var b = new SolidBrush(textBox.BackColor)) Ui.FillRound(g, b, fr, rad);
                using (var p = new Pen(Ui.Accent, Ui.S(1.6f))) Ui.DrawRound(g, p, fr, rad);
            }

            var sz = TextRenderer.MeasureText(TextHintText, uiFont);
            var hr = new Rectangle(fr.Left, fr.Bottom + Ui.S(6), sz.Width + Ui.S(16), sz.Height + Ui.S(6));
            Rectangle mon = MonitorOf(fr);
            if (hr.Bottom > mon.Bottom - 4) hr.Y = fr.Top - hr.Height - Ui.S(6);
            textHintRect = hr;
            using (var b = new SolidBrush(Color.FromArgb(230, 40, 40, 40))) Ui.FillRound(g, b, hr, hr.Height / 2f);
            TextRenderer.DrawText(g, TextHintText, uiFont, hr, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        // 文字を Enter で確定した直後の Enter は 1 回だけ止めて、「もう一度 Enter で閉じる」と案内する（うっかり閉じるのを防ぐ）
        bool enterGuard, enterGuardHint;
        Rectangle enterHintRect;
        const string EnterHintText = "もう一度 Enter でコピーして閉じます";

        void DrawEnterHint(Graphics g)
        {
            UiItem copy = null;
            foreach (var it in toolbar) if (it.Kind == ItemKind.Copy) copy = it;
            if (copy == null) return;
            DrawTip(g, EnterHintText, copy.R, toolbarRect);
            enterHintRect = lastTipRect;
        }

        void ClearEnterGuard()
        {
            if (!enterGuard && !enterGuardHint) return;
            enterGuard = false;
            if (enterGuardHint) { enterGuardHint = false; Redraw(); enterHintRect = Rectangle.Empty; }
        }

        void BeginText(Point p, TextShape existing)
        {
            editingText = existing;
            Color c = existing != null ? existing.Color : color;
            // 白・黄色など明るい色の文字は、暗い背景で見せる
            Color back = c.GetBrightness() > 0.75f ? Color.FromArgb(48, 50, 56) : Color.White;
            BeginTextBox(existing != null ? existing.Location : new Point(p.X + 3, p.Y + 3),
                existing != null ? existing.Text : "", existing != null ? existing.Size : TextPx, c, back);
        }

        // 吹き出しの中の文字を入力する（文字は吹き出しの一部として持つ）
        BoxShape editingCallout;
        Rectangle calloutRectAtEdit;

        void BeginCalloutText(BoxShape b)
        {
            editingCallout = b;
            calloutRectAtEdit = b.Rect;
            b.HideText = true;
            if (b.TextSize <= 0) b.TextSize = TextPx;
            Color back = b.Fill == FillMode.Solid ? b.FillColor : Color.White;
            Color fore = b.TextColor;
            if (BoxShape.Luma(fore) > 200 && BoxShape.Luma(back) > 200) back = Color.FromArgb(48, 50, 56);
            BeginTextBox(b.TextOrigin, b.Text ?? "", b.TextSize, fore, back);
        }

        void BeginTextBox(Point at, string text, float size, Color c, Color back)
        {
            textPt = at;
            selected = null;
            popupOpen = false;
            var ime = new ImeTextBox();
            textBox = ime;
            textBox.Multiline = true;
            textBox.WordWrap = false;          // 変換中の文字も含め、勝手に折り返さない（改行は Shift+Enter だけ）
            textBox.ScrollBars = ScrollBars.None;
            textBox.AcceptsReturn = true;
            // 枠なしの入力欄を、角丸の枠（OnPaint で描く）の中に置く。文字は確定後と同じ色・大きさで見せる
            textBox.BorderStyle = BorderStyle.None;
            textBox.Font = new Font(Ui.TextFamily, size, FontStyle.Bold, GraphicsUnit.Pixel);
            textBox.ForeColor = c;
            textBox.BackColor = back;
            textBox.Location = new Point(textPt.X - 3, textPt.Y - 3);
            if (!string.IsNullOrEmpty(text)) textBox.Text = text.Replace("\n", "\r\n");
            ResizeTextBox();
            textBox.TextChanged += delegate { ResizeTextBox(); };
            ime.CompositionChanged += delegate { ResizeTextBox(); };
            textBox.KeyDown += TextBox_KeyDown;
            Controls.Add(textBox);
            ResizeTextBox();
            textBox.Focus();
            textBox.SelectionStart = textBox.Text.Length;
            RelayoutUi();
        }

        void ResizeTextBox()
        {
            if (textBox == null) return;
            // 変換中の文字（まだ Text に入っていない）も含めて幅を計算する
            string t = textBox.Text;
            var ime = textBox as ImeTextBox;
            if (ime != null && ime.Composition.Length > 0)
            {
                int at = Math.Max(0, Math.Min(textBox.SelectionStart, t.Length));
                t = t.Insert(at, ime.Composition);
            }
            if (t.Length == 0) t = "あ";
            if (t.EndsWith("\n")) t += "あ";
            bool typed = textBox.Text.Length > 0 || (ime != null && ime.Composition.Length > 0);
            Size sz = TextRenderer.MeasureText(t + (editingCallout != null ? "あ" : "ああ"), textBox.Font);
            textBox.Size = new Size(Math.Max(sz.Width + 10, Ui.S(editingCallout != null || editingStamp != null ? 24 : 80)), sz.Height + 4);
            Rectangle old = Rectangle.Union(textFrameRect, textHintRect);
            if (editingCallout != null)
            {
                // 吹き出しは、入力している文字の大きさにその場で合わせる（まだ何も書いていなければ描いた大きさのまま）
                var b = editingCallout;
                Rectangle before = b.Bounds;
                Rectangle r = calloutRectAtEdit;
                if (b.IsCallout)
                {
                    if (typed)
                    {
                        r.Width = textBox.Right + 3 + BoxShape.PadX - r.X;
                        r.Height = textBox.Bottom + BoxShape.PadY - r.Y;
                    }
                }
                else
                {
                    // 角丸・四角・丸：入力欄を図形のまん中に置き、入りきらないときだけ中心を保って広げる
                    double k = b.Kind == BoxKind.Ellipse ? 1.42 : 1.0;
                    int needW = (int)((textBox.Width + BoxShape.PadX * 2) * k), needH = (int)((textBox.Height + BoxShape.PadY * 2) * k);
                    if (typed && (r.Width < needW || r.Height < needH))
                    {
                        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                        int w = Math.Max(r.Width, needW), h = Math.Max(r.Height, needH);
                        r = new Rectangle(cx - w / 2, cy - h / 2, w, h);
                    }
                    textBox.Location = new Point(r.X + (r.Width - textBox.Width) / 2, r.Y + (r.Height - textBox.Height) / 2);
                }
                b.Rect = r;
                b.SyncTail();
                old = old.IsEmpty ? before : Rectangle.Union(old, before);
                old = Rectangle.Union(old, b.Bounds);
                textFrameRect = b.Bounds;   // 枠は描かない（吹き出しが枠の代わり）。案内の位置の基準にだけ使う
            }
            else textFrameRect = Rectangle.Inflate(textBox.Bounds, Ui.S(8), Ui.S(6));
            Rectangle now = Rectangle.Inflate(textFrameRect, Ui.S(8), Ui.S(8));
            now.Height += Ui.S(48);
            now.Y -= Ui.S(40); now.Height += Ui.S(40);
            Invalidate(old.IsEmpty ? now : Rectangle.Union(Rectangle.Inflate(old, Ui.S(8), Ui.S(8)), now));
        }

        void RemoveTextHint()
        {
            Rectangle old = Rectangle.Union(textFrameRect, textHintRect);
            textFrameRect = Rectangle.Empty;
            textHintRect = Rectangle.Empty;
            if (!old.IsEmpty) Invalidate(Rectangle.Inflate(old, Ui.S(10), Ui.S(10)));
        }

        void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; e.Handled = true; CommitText(); enterGuard = true; Focus(); Redraw(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; e.Handled = true; CancelText(); Focus(); Redraw(); }
        }

        void CommitText()
        {
            if (textBox == null) return;
            TextBox tb = textBox;
            textBox = null;
            string t = tb.Text.Replace("\r\n", "\n").TrimEnd('\n');
            bool empty = t.Trim().Length == 0;
            // スタンプの番号：決まった番号の位置に割り込ませて、ほかをふり直す
            if (editingStamp != null)
            {
                var st = editingStamp;
                editingStamp = null;
                int n = StampShape.IndexOf(ToHalfWidth(t.Trim()).ToUpperInvariant(), st.Letter);
                if (n > 0) SetStampNumber(st, n);
            }
            else if (editingCallout != null)
            {
                // 吹き出しの文字：入力前の大きさに戻してから、文字に合わせて広げる（元に戻すで 1 回分）
                var b = editingCallout;
                editingCallout = null;
                b.HideText = false;
                b.Rect = calloutRectAtEdit;
                b.SyncTail();
                string nt = empty ? null : t;
                if (nt != b.Text) { PushUndo(); b.Text = nt; b.AdjustToText(); }
            }
            else if (editingText != null)
            {
                if (empty) { PushUndo(); shapes.Remove(editingText); }
                else if (t != editingText.Text) { PushUndo(); editingText.Text = t; }
            }
            else if (!empty)
            {
                var s = new TextShape();
                s.Text = t; s.Color = color; s.Size = TextPx; s.Location = textPt;
                PushUndo();
                shapes.Add(s);
            }
            editingText = null;
            Controls.Remove(tb);
            tb.Dispose();
            RemoveTextHint();
            RelayoutUi();
            Invalidate();
        }

        void CancelText()
        {
            if (textBox == null) return;
            TextBox tb = textBox;
            textBox = null;
            editingText = null;
            if (editingCallout != null) { editingCallout.HideText = false; editingCallout.Rect = calloutRectAtEdit; editingCallout.SyncTail(); editingCallout = null; }
            editingStamp = null;
            RemoveTextHint();
            Controls.Remove(tb);
            tb.Dispose();
            RelayoutUi();
            Invalidate();
        }

        // ---------- キー ----------
        // Esc：開いている一覧を閉じる → 選んでいる書き込みを外す → 撮影をやめる（の順に1つずつ）
        void EscapePressed()
        {
            if (popupOpen) { popupOpen = false; Redraw(); }
            else if (selected != null) { selected = null; RelayoutUi(); Redraw(); }
            else Cancel();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (textBox != null) return;
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) { ShiftChanged(); e.Handled = true; return; }
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.Menu) ClearEnterGuard();
            if (e.KeyCode == Keys.Escape) { EscapePressed(); e.Handled = true; return; }
            if (imageMode && editing && (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp || ((e.KeyCode == Keys.Home || e.KeyCode == Keys.End) && e.Control)))
            {
                // 長い画像を動かす：PageUp・PageDown（Ctrl+Home・Ctrl+End で先頭・最後）
                int page = vs.Height * 3 / 4;
                if (e.KeyCode == Keys.PageDown) ScrollImage(page);
                else if (e.KeyCode == Keys.PageUp) ScrollImage(-page);
                else if (e.KeyCode == Keys.Home) ScrollImage(-image.Height);
                else ScrollImage(image.Height);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (!editing)
            {
                // Enter：マウスがある画面の全体
                if (e.KeyCode == Keys.Enter)
                {
                    Point cp = PointToClient(Cursor.Position);
                    SelectFullScreen(MonitorOf(new Rectangle(cp, new Size(1, 1))));
                    e.SuppressKeyPress = true;
                }
                // 帯のボタンをキーでも：T タイマー ／ L 前回の範囲 ／ S つなげて撮る（マウスが使いにくい人のため）
                else if (!PickOnly && !e.Control && !e.Alt)
                {
                    if (e.KeyCode == Keys.T) { e.SuppressKeyPress = true; RequestTimer(Settings.TimerSeconds, false); }
                    else if (e.KeyCode == Keys.L && !sameBtnRect.IsEmpty) { e.SuppressKeyPress = true; SelectLastArea(); }
                    else if (e.KeyCode == Keys.S) { e.SuppressKeyPress = true; EnterPick(); }
                }
                return;
            }
            // 吹き出し・文字を選んでいるとき：Enter で文字を直す。吹き出しは、文字のキーを押せばそのまま入力が始まる
            var scb = SelectedCallout;
            if ((scb != null || selected is TextShape || selected is StampShape) && !e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; ClearEnterGuard(); BeginEditSelected(); Redraw(); return; }
                if (scb != null && e.KeyCode == Keys.ProcessKey) { BeginEditSelected(); Redraw(); return; }   // 日本語入力：入力欄を先に開いて受け取る
                if (scb != null && IsTypingKey(e.KeyCode)) return;   // 文字は OnKeyPress で入れる（道具の切り替えはしない）
                if (selected is StampShape && IsTypingKey(e.KeyCode)) return;   // スタンプ：数字（記号は英字）を打つと番号の入力が始まる
            }
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) { DeleteSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !e.Control && enterGuard)
            {
                // 文字の確定直後の Enter：閉じずに案内だけ出す
                e.SuppressKeyPress = true;
                enterGuard = false; enterGuardHint = true;
                Redraw();
            }
            else if (e.KeyCode == Keys.Enter || (e.Control && e.KeyCode == Keys.C)) { e.SuppressKeyPress = true; CopyAndClose(); }
            else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveRequested = true; CopyAndClose(); }
            else if (!e.Control && !e.Alt)
            {
                // 道具の切り替え：P ペン・蛍光ペン / H 蛍光ペン / A 矢印 / S 図形 / T 文字 / M モザイク（数字の1〜5でも可）
                Tool? t = KeyTool(e.KeyCode);
                if (t.HasValue)
                {
                    Tool nt = t.Value;
                    if (nt == Tool.Shape && tool == Tool.Shape) boxKind = (BoxKind)(((int)boxKind + 1) % BoxNames.Length);   // もう一度押すと形を切り替え
                    if (nt == Tool.Arrow && tool == Tool.Arrow) arrowKind = (ArrowKind)(((int)arrowKind + 1) % 3);   // 矢印 → 直線 → 両矢印
                    if (nt == Tool.Mosaic && tool == Tool.Mosaic) mosaicKind = (mosaicKind + 1) % 4;   // もう一度押すとモザイク → 黒 → グレー → 白
                    if (nt == Tool.Pen) nt = IsPenGroup(tool) ? (tool == Tool.Pen ? Tool.Marker : Tool.Pen) : penTool;   // もう一度押すとペン ⇔ 蛍光ペン
                    if (IsPenGroup(nt)) penTool = nt;
                    tool = nt;
                    selected = null;
                    // 種類のある道具（ペン・矢印・図形）は、ボタンを押したときと同じく種類の一覧も出す
                    // （同じキーを続けて押したとき、今どの種類になったかが一覧の青い印でわかる）
                    Tool group = IsPenGroup(nt) ? Tool.Pen : nt;
                    if (HasSubmenu(group)) { popupForShape = false; popup = SubmenuOf(group); }
                    else popup = PopupKind.None;
                    RelayoutUi(); Redraw();
                    UpdateCursor(PointToClient(Cursor.Position));
                    e.Handled = true; e.SuppressKeyPress = true;
                }
            }
        }

        // 全角の英数字を半角にする（番号の入力用）
        static string ToHalfWidth(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= (char)0xFF01 && c <= (char)0xFF5E ? (char)(c - 0xFEE0) : c);
            return sb.ToString();
        }

        static bool IsTypingKey(Keys k)
        {
            return (k >= Keys.A && k <= Keys.Z) || (k >= Keys.D0 && k <= Keys.D9) || (k >= Keys.NumPad0 && k <= Keys.Divide)
                || k == Keys.Space || (k >= Keys.Oem1 && k <= Keys.Oem102);
        }

        void BeginEditSelected()
        {
            var cb = SelectedCallout;
            if (cb != null) BeginCalloutText(cb);
            else if (selected is TextShape) { var t = (TextShape)selected; BeginText(t.Location, t); }
            else if (selected is StampShape) BeginStampText((StampShape)selected);
        }

        // スタンプの番号を入力する（入力欄はスタンプの上に重ねる。日本語入力は使わない）
        StampShape editingStamp;

        void BeginStampText(StampShape st)
        {
            editingStamp = st;
            float size = Math.Max(Ui.S(14), st.Diameter * 0.55f);
            var at = new Point(st.Center.X - (int)(st.Diameter / 2) + Ui.S(4), st.Center.Y - (int)(size * 0.62f));
            BeginTextBox(at, st.Label, size, st.Color, Color.White);
            textBox.ImeMode = ImeMode.Disable;
            textBox.SelectAll();
        }

        // ↑／↓：選んでいるスタンプの番号を前後と入れ替える（矢印キーは先にここで受け取る）
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var st = selected as StampShape;
            if (editing && textBox == null && st != null && (keyData == Keys.Up || keyData == Keys.Down))
            {
                MoveStampNumber(st, keyData == Keys.Up ? 1 : -1);   // ↑ で番号が増える
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!editing || char.IsControl(e.KeyChar)) return;
            if (textBox != null)
            {
                // 入力欄の外に届いた文字（日本語入力で確定した最初の文字など）も、入力欄に入れる
                if (!textBox.Focused) { textBox.SelectedText = e.KeyChar.ToString(); e.Handled = true; }
                return;
            }
            if (SelectedCallout != null || selected is StampShape)
            {
                BeginEditSelected();
                textBox.SelectedText = e.KeyChar.ToString();
                e.Handled = true;
                Redraw();
            }
        }

        static Tool? KeyTool(Keys k)
        {
            switch (k)
            {
                // 数字はボタンの並び順（ペン・蛍光ペン / 矢印 / 図形 / 文字 / モザイク）
                case Keys.P: case Keys.D1: case Keys.NumPad1: return Tool.Pen;
                case Keys.H: return Tool.Marker;
                case Keys.A: case Keys.D2: case Keys.NumPad2: return Tool.Arrow;
                case Keys.S: case Keys.D3: case Keys.NumPad3: return Tool.Shape;
                case Keys.T: case Keys.D4: case Keys.NumPad4: return Tool.Text;
                case Keys.M: case Keys.D5: case Keys.NumPad5: return Tool.Mosaic;
            }
            return null;
        }

        // モザイクを、元の画像から作り直す（長い画像では、見えていない部分も含めて画像そのものから作る）
        void RefreshMosaic(MosaicShape m)
        {
            if (!imageMode) { m.Refresh(screen); return; }
            m.Rect.Offset(-imgOffX, -imgOffY);
            m.Refresh(image);
            m.Rect.Offset(imgOffX, imgOffY);
        }

        // ---------- 長い画像を上下に動かす ----------
        // 長い画像の窓の見出し（題名・使い方・「確認画面に戻る」）
        int HeaderH { get { return imageMode ? Ui.S(56) : 0; } }
        Rectangle backRect;
        bool hoverBack, headerDrag;
        Point headerDragOffset;
        const string BackText = "確認画面に戻る (Esc)";

        void DrawImageHeader(Graphics g)
        {
            Ui.Setup(g);
            var hr = new Rectangle(0, 0, vs.Width, HeaderH);
            g.FillRectangle(Brushes.White, hr);
            using (var p = new Pen(Color.FromArgb(220, 220, 224))) g.DrawLine(p, 0, hr.Bottom - 1, vs.Width, hr.Bottom - 1);
            int pad = Ui.S(16), ms = Ui.S(28);
            var mark = new Rectangle(pad, (HeaderH - ms) / 2, ms, ms);
            using (var b = new SolidBrush(Ui.AccentLight)) Ui.FillRound(g, b, mark, Ui.S(7));
            Icons.Tool(g, Tool.Pen, Rectangle.Inflate(mark, -Ui.S(5), -Ui.S(5)), Ui.Accent, uiBold);
            int tx = mark.Right + Ui.S(10), tw = backRect.Left - Ui.S(12) - tx;
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, "つないだ画像に書き込む", uiBold, new Rectangle(tx, Ui.S(6), tw, Ui.S(24)), Color.FromArgb(25, 25, 25), flags);
            bool canV = ScrollTop - MinOffY > 0, canH = ScrollLeft - MinOffX > 0;
            string sub = (canV ? "ホイールで上下に動かせます" + (canH ? "（Shift＋ホイールで左右）" : "") + "。"
                        : canH ? "ホイールで左右に動かせます。" : "")
                       + "書き終わったら、下の「コピーして閉じる」";
            TextRenderer.DrawText(g, sub, uiFont, new Rectangle(tx, Ui.S(28), tw, Ui.S(22)), Color.FromArgb(80, 80, 80), flags);
            using (var b = new SolidBrush(hoverBack ? Color.FromArgb(240, 240, 240) : Color.White)) Ui.FillRound(g, b, backRect, Ui.S(8));
            using (var p = new Pen(Color.FromArgb(200, 200, 205), 1)) Ui.DrawRound(g, p, backRect, Ui.S(8));
            TextRenderer.DrawText(g, BackText, uiFont, backRect, Color.FromArgb(25, 25, 25), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int ScrollTop { get { return HeaderH + Ui.S(16); } }          // 画像の上端を、この位置より下には下げない
        int ScrollBottom { get { return vs.Height - Ui.S(96); } }     // 画像の下端を、この位置（メニューの上）より上には上げない
        int MinOffY { get { return Math.Min(ScrollTop, ScrollBottom - image.Height); } }

        // 画像を dy だけ上へ動かす（負なら下へ）。範囲・書き込み・元に戻すの記録を一緒に動かす
        void ScrollImage(int dy) { ScrollImage(0, dy); }

        // 横にも動かす（画面の幅いっぱいの画像は、窓に入りきらないことがあるため）
        int ScrollLeft { get { return Math.Max(Ui.S(8), (vs.Width - image.Width) / 2); } }
        int MinOffX { get { return Math.Min(ScrollLeft, vs.Width - Ui.S(24) - image.Width); } }

        void ScrollImage(int dx, int dy)
        {
            if (!imageMode || drag != DragMode.None) return;
            int newY = Math.Max(MinOffY, Math.Min(ScrollTop, imgOffY - dy));
            int newX = Math.Max(MinOffX, Math.Min(ScrollLeft, imgOffX - dx));
            int d = newY - imgOffY, ddx = newX - imgOffX;
            if (d == 0 && ddx == 0) return;
            if (textBox != null) CommitText();
            imgOffY = newY; imgOffX = newX;
            sel.Offset(ddx, d);
            foreach (var s in shapes) s.Offset(ddx, d);
            for (int i = 0; i < undo.Count; i++)
            {
                foreach (var s in undo[i].Key) s.Offset(ddx, d);
                Rectangle r = undo[i].Value; r.Offset(ddx, d);
                undo[i] = new KeyValuePair<List<Shape>, Rectangle>(undo[i].Key, r);
            }
            UpdateAttachments();
            RenderView();
            RelayoutUi();
            Invalidate();
        }

        // 見えている部分を、screen（そのまま）と dimmed（範囲の外の暗い色）に描く
        void RenderView()
        {
            var bg = Color.FromArgb(228, 228, 232);   // 窓の中の、画像のまわりのうすい灰色
            var view = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            var dim = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(view))
            {
                g.Clear(bg);
                g.DrawImage(image, new Rectangle(imgOffX, imgOffY, image.Width, image.Height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            }
            using (var g = Graphics.FromImage(dim))
            {
                g.DrawImage(view, 0, 0, vs.Width, vs.Height);
                using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(b, ImageRect);
            }
            if (screen != null) screen.Dispose();
            if (dimmed != null) dimmed.Dispose();
            screen = view; dimmed = dim;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!imageMode) return;
            int amount = -e.Delta * Ui.S(120) / 120;
            // Shift＋ホイール（または縦に動かす余地がないとき）は横に動かす
            bool sideways = (Control.ModifierKeys & Keys.Shift) != 0 || (ScrollTop - MinOffY <= 0 && ScrollLeft - MinOffX > 0);
            if (sideways) ScrollImage(amount, 0); else ScrollImage(0, amount);
        }

        // 右端の細いつまみ（今どのあたりを見ているか）と、使い方の小さな案内
        void DrawImageScrollbar(Graphics g)
        {
            Ui.Setup(g);
            int range = ScrollTop - MinOffY;
            if (range > 0)
            {
                int trackTop = HeaderH + Ui.S(8), trackH = vs.Height - HeaderH - Ui.S(16);
                int viewH = vs.Height - HeaderH;
                int th = Math.Max(Ui.S(40), (int)((long)trackH * viewH / (image.Height + Ui.S(120))));
                int ty = trackTop + (int)((long)(trackH - th) * (ScrollTop - imgOffY) / range);
                var tr = new Rectangle(vs.Width - Ui.S(12), ty, Ui.S(6), th);
                using (var b = new SolidBrush(Color.FromArgb(110, 110, 118))) Ui.FillRound(g, b, tr, tr.Width / 2);   // 背景と 3:1 以上の差で見えるように
            }
        }

        // ---------- 出力 ----------
        public Bitmap Render()
        {
            UpdateAttachments();
            var bmp = new Bitmap(sel.Width, sel.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (imageMode)   // 長い画像は、見えていない部分も含めて画像そのものから
                    g.DrawImage(image, new Rectangle(0, 0, sel.Width, sel.Height), new Rectangle(sel.X - imgOffX, sel.Y - imgOffY, sel.Width, sel.Height), GraphicsUnit.Pixel);
                else
                    g.DrawImage(screen, new Rectangle(0, 0, sel.Width, sel.Height), sel, GraphicsUnit.Pixel);
                g.TranslateTransform(-sel.X, -sel.Y);
                Ui.Setup(g);
                foreach (var s in shapes) s.Draw(g);
            }
            return bmp;
        }

        public bool Canceled;
        public bool SaveRequested;   // Ctrl+S：コピーに加えてファイルにも保存する
        void Cancel()
        {
            Canceled = true;
            Result = null;
            Close();
        }

        public string SaveAsPath;   // 「名前を付けて保存...」で選んだ保存先（コピーに加えてここへ保存する）

        void SaveAsAndClose()
        {
            CommitText();
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "名前を付けて保存";
                dlg.Filter = "PNG 画像 (*.png)|*.png";
                dlg.DefaultExt = "png";
                dlg.AddExtension = true;
                dlg.FileName = "ShotCopy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                try { Directory.CreateDirectory(Settings.SaveFolder); dlg.InitialDirectory = Settings.SaveFolder; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) { Focus(); return; }
                SaveAsPath = dlg.FileName;
            }
            CopyAndClose();
        }

        void CopyAndClose()
        {
            CommitText();
            if (sel.Width <= 0 || sel.Height <= 0) return;
            Result = Render();
            if (imageMode)
            {
                // 長い画像は「前回と同じ範囲」や「書き直す」の対象にしない
                ResultScreenCenter = new Point(vs.X + vs.Width / 2, vs.Y + vs.Height / 2);
                Close();
                return;
            }
            ResultScreenCenter = new Point(sel.X + vs.X + sel.Width / 2, sel.Y + vs.Y + sel.Height / 2);
            LastArea = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);   // 「前回と同じ範囲で撮る」用（画面の座標）
            // 「書き直す」用に、画面・範囲・書き込みを覚えておく（画面の画像は破棄せずに渡す）
            LastSession.Store(screen, vs, sel, CloneShapes(shapes), undo);
            keepScreen = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!keepScreen || imageMode) screen.Dispose();   // 長い画像のときの screen は、見えている部分の写し（画像そのものは ShotFlow が持つ）
                dimmed.Dispose();
                uiFont.Dispose(); uiBold.Dispose(); hintFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
