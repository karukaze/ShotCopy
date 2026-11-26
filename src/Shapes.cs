// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 書き込みの図形（ペン・矢印・図形・スタンプ・文字・モザイク）
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
    // ================= 書き込みの図形 =================
    static class Geo
    {
        public static float DistToSegment(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len2 = dx * dx + dy * dy;
            float t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }
        public static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    // 日本語入力（IME）で変換中の文字を知らせてくれる入力欄
    class ImeTextBox : TextBox
    {
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionStringW(IntPtr hIMC, int index, byte[] buf, int len);
        const int WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F, GCS_COMPSTR = 0x0008;

        public string Composition = "";
        public event EventHandler CompositionChanged;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_IME_COMPOSITION) ReadComposition();
            else if (m.Msg == WM_IME_ENDCOMPOSITION) SetComposition("");
        }

        void ReadComposition()
        {
            IntPtr ctx = ImmGetContext(Handle);
            if (ctx == IntPtr.Zero) return;
            try
            {
                int len = ImmGetCompositionStringW(ctx, GCS_COMPSTR, null, 0);
                string s = "";
                if (len > 0)
                {
                    var buf = new byte[len];
                    ImmGetCompositionStringW(ctx, GCS_COMPSTR, buf, len);
                    s = System.Text.Encoding.Unicode.GetString(buf);
                }
                SetComposition(s);
            }
            finally { ImmReleaseContext(Handle, ctx); }
        }

        public void SetComposition(string s)
        {
            Composition = s ?? "";
            if (CompositionChanged != null) CompositionChanged(this, EventArgs.Empty);
        }
    }

    abstract class Shape
    {
        public Color Color;
        public abstract void Draw(Graphics g);
        public abstract bool HitTest(Point p);
        public abstract Rectangle Bounds { get; }
        public abstract void Offset(int dx, int dy);
        public abstract Shape Clone();
        public virtual bool HasColor { get { return true; } }
        protected static float Tol { get { return Ui.S(6); } }
    }

    class StrokeShape : Shape
    {
        public List<Point> Points = new List<Point>();
        public float Width;
        public bool Marker;
        // Shift でまっすぐ引いた部分の端の点（なめらかにするときに動かさない）
        public List<int> Corners = new List<int>();

        // 手描きの部分を少しなめらかにする（ガタガタを抑える。端と直線の部分は動かさない）
        public void Smooth()
        {
            if (Points.Count < 5) return;
            var fixedIdx = new HashSet<int>(Corners);
            for (int pass = 0; pass < 2; pass++)
            {
                var src = new List<Point>(Points);
                for (int i = 1; i < src.Count - 1; i++)
                {
                    if (fixedIdx.Contains(i)) continue;
                    Points[i] = new Point((src[i - 1].X + src[i].X * 2 + src[i + 1].X + 2) / 4, (src[i - 1].Y + src[i].Y * 2 + src[i + 1].Y + 2) / 4);
                }
            }
        }
        Color DrawColor { get { return Marker ? Color.FromArgb(110, Color) : Color; } }

        public override void Draw(Graphics g)
        {
            if (Points.Count == 0) return;
            if (Points.Count == 1)
            {
                using (var b = new SolidBrush(DrawColor)) g.FillEllipse(b, Points[0].X - Width / 2, Points[0].Y - Width / 2, Width, Width);
                return;
            }
            using (var p = new Pen(DrawColor, Width))
            using (var path = new GraphicsPath())
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                path.AddLines(Points.ToArray());
                g.DrawPath(p, path);
            }
        }
        public override bool HitTest(Point pt)
        {
            float tol = Width / 2 + Tol;
            if (Points.Count == 1) return Geo.DistToSegment(pt, Points[0], Points[0]) <= tol;
            for (int i = 1; i < Points.Count; i++)
                if (Geo.DistToSegment(pt, Points[i - 1], Points[i]) <= tol) return true;
            return false;
        }
        public override Rectangle Bounds
        {
            get
            {
                int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
                foreach (var p in Points) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
                var rc = Rectangle.FromLTRB(l, t, r, b);
                rc.Inflate((int)(Width / 2) + 1, (int)(Width / 2) + 1);
                return rc;
            }
        }
        public override void Offset(int dx, int dy)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
        }
        public override Shape Clone()
        {
            var s = (StrokeShape)MemberwiseClone();
            s.Points = new List<Point>(Points);
            s.Corners = new List<int>(Corners);
            return s;
        }
    }

    enum ArrowKind { Arrow, Line, Double }

    // 矢印・直線・両矢印
    class ArrowShape : Shape
    {
        public Point From, To; public float Width; public ArrowKind Kind;
        // 端がくっついている図形とその点の番号（くっついていなければ null）
        public Shape FromAnchor, ToAnchor; public int FromIdx, ToIdx;
        public override void Draw(Graphics g)
        {
            using (var p = new Pen(Color, Width))
            using (var cap = new AdjustableArrowCap(3.2f, 3.2f, true))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                if (Kind != ArrowKind.Line) p.CustomEndCap = cap;
                if (Kind == ArrowKind.Double) p.CustomStartCap = cap;
                g.DrawLine(p, From, To);
            }
        }
        public override bool HitTest(Point pt) { return Geo.DistToSegment(pt, From, To) <= Width / 2 + Tol; }
        public override Rectangle Bounds
        {
            get { var r = Geo.Norm(From, To); r.Inflate((int)(Width * 2), (int)(Width * 2)); return r; }
        }
        public override void Offset(int dx, int dy) { From.Offset(dx, dy); To.Offset(dx, dy); }
        public override Shape Clone() { return (Shape)MemberwiseClone(); }
    }

    // Number / Letter は図形ではなく、クリックで置くスタンプ（①②… / Ⓐ Ⓑ…）
    enum BoxKind { Rounded, Square, Ellipse, Callout, Number, Letter }

    // 番号・記号スタンプ：塗った丸の中に白い文字
    class StampShape : Shape
    {
        public Point Center; public float Diameter; public string Label; public bool Letter;
        // 図形のくっつく点に付いているとき、その図形と点の番号（付いていなければ null）
        public Shape Anchor; public int AnchorIdx;

        public static string LabelOf(int n, bool letter)
        {
            if (!letter) return n.ToString();
            string s = "";
            while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
            return s;
        }

        public static int IndexOf(string label, bool letter)
        {
            int n;
            if (!letter) return int.TryParse(label, out n) ? n : 0;
            n = 0;
            foreach (char c in label) { if (c < 'A' || c > 'Z') return 0; n = n * 26 + (c - 'A' + 1); }
            return n;
        }

        RectangleF Circle { get { return new RectangleF(Center.X - Diameter / 2, Center.Y - Diameter / 2, Diameter, Diameter); } }

        public override void Draw(Graphics g)
        {
            var r = Circle;
            using (var b = new SolidBrush(Color)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.White, Math.Max(1.5f, Diameter / 14f))) g.DrawEllipse(p, r);
            Color tc = Color.GetBrightness() > 0.8f ? Color.FromArgb(40, 40, 40) : Color.White;
            float fs = Diameter * (Label.Length >= 2 ? 0.46f : 0.58f);
            using (var f = new Font(Ui.TextFamily, fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(tc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Label, f, b, new RectangleF(r.X, r.Y + Diameter * 0.03f, r.Width, r.Height), sf);
        }
        public override bool HitTest(Point pt)
        {
            double dx = pt.X - Center.X, dy = pt.Y - Center.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= Diameter / 2 + Tol;
        }
        public override Rectangle Bounds { get { var r = Rectangle.Round(Circle); r.Inflate(2, 2); return r; } }
        public override void Offset(int dx, int dy) { Center.Offset(dx, dy); }
        public override Shape Clone() { return (Shape)MemberwiseClone(); }
    }
    enum FillMode { None, Light, Solid }

    // 図形（角丸・四角・丸）。枠の色とは別に塗りの色を持てる
    class BoxShape : Shape
    {
        public Rectangle Rect; public float Width; public BoxKind Kind;
        public FillMode Fill; public Color FillColor;
        // 吹き出しだけ：しっぽの先（画面の座標）と、中の文字
        public Point Tail; public string Text; public float TextSize;
        public bool HideText;   // 文字を入力している間は、本体の文字を描かない
        public bool TailMoved;  // しっぽを自分で動かしたか（動かしていなければ、本体の大きさに合わせて付け直す）

        public void SyncTail() { if (IsCallout && !TailMoved) Tail = DefaultTail(Rect); }

        public bool IsCallout { get { return Kind == BoxKind.Callout; } }
        // 中に文字を書ける形（角丸・四角・丸・吹き出し）
        public bool CanHoldText { get { return Kind == BoxKind.Rounded || Kind == BoxKind.Square || Kind == BoxKind.Ellipse || Kind == BoxKind.Callout; } }
        public bool HasText { get { return !string.IsNullOrEmpty(Text); } }

        // 中の文字の色：ふだんは線と同じ色。塗りが濃い色なら白（読めるように）
        public Color TextColor
        {
            get
            {
                if (Fill == FillMode.Solid && Luma(FillColor) < 150) return Color.White;
                return Color;
            }
        }
        public static double Luma(Color c) { return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }
        // 枠と文字の間の余白（角が丸い分、左右を多めに）
        public static int PadX { get { return Ui.S(16); } }
        public static int PadY { get { return Ui.S(12); } }

        GraphicsPath MakePath()
        {
            Rectangle r = Rect;
            if (r.Width < 1) r.Width = 1;
            if (r.Height < 1) r.Height = 1;
            if (Kind == BoxKind.Rounded)
                return Ui.Round(r, Math.Min(Ui.S(10) + Width, Math.Min(r.Width, r.Height) / 2f));
            if (Kind == BoxKind.Callout) return CalloutPath(r, Tail, Width);
            var p = new GraphicsPath();
            if (Kind == BoxKind.Ellipse) p.AddEllipse(r); else p.AddRectangle(r);
            return p;
        }

        // 新しく描いた吹き出しのしっぽの先（本体の左下の少し下）
        public static Point DefaultTail(Rectangle body)
        {
            return new Point(body.X + (int)(body.Width * 0.18f), body.Bottom + (int)Math.Max(Ui.S(14), Math.Min(body.Height * 0.45f, Ui.S(30))));
        }

        // 吹き出し：角丸の本体＋しっぽ。しっぽは先のある方向の辺から出す
        public static GraphicsPath CalloutPath(RectangleF b, PointF tip, float width)
        {
            float rad = Math.Min(Ui.S(10) + width, Math.Min(b.Width, b.Height) / 2f);
            float d = Math.Max(1f, Math.Min(rad * 2, Math.Min(b.Width, b.Height)));
            float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            float nx = (tip.X - cx) / Math.Max(1f, b.Width / 2), ny = (tip.Y - cy) / Math.Max(1f, b.Height / 2);
            bool inside = b.Contains(tip);
            // 0=上 1=右 2=下 3=左
            int side = inside ? -1 : Math.Abs(ny) >= Math.Abs(nx) ? (ny > 0 ? 2 : 0) : (nx > 0 ? 1 : 3);
            bool horiz = side == 0 || side == 2;
            float len = horiz ? b.Width : b.Height;
            float bw = Math.Max(4f, Math.Min(len * 0.14f, Ui.S(13)));
            float lo = (horiz ? b.X : b.Y) + d / 2 + bw, hi = (horiz ? b.Right : b.Bottom) - d / 2 - bw;
            float c = horiz ? tip.X : tip.Y;
            c = lo <= hi ? Math.Max(lo, Math.Min(hi, c)) : (horiz ? cx : cy);

            var p = new GraphicsPath();
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            if (side == 0) { p.AddLine(c - bw, b.Y, tip.X, tip.Y); p.AddLine(tip.X, tip.Y, c + bw, b.Y); }
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            if (side == 1) { p.AddLine(b.Right, c - bw, tip.X, tip.Y); p.AddLine(tip.X, tip.Y, b.Right, c + bw); }
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            if (side == 2) { p.AddLine(c + bw, b.Bottom, tip.X, tip.Y); p.AddLine(tip.X, tip.Y, c - bw, b.Bottom); }
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            if (side == 3) { p.AddLine(b.X, c + bw, tip.X, tip.Y); p.AddLine(tip.X, tip.Y, b.X, c - bw); }
            p.CloseFigure();
            return p;
        }

        // 中の文字の形（太字）。入力欄の位置は本体の左上から余白の分だけ内側
        GraphicsPath TextPath(Point at)
        {
            var path = new GraphicsPath();
            path.AddString(Text, Ui.TextFamily, (int)FontStyle.Bold, TextSize, at, StringFormat.GenericTypographic);
            return path;
        }

        public Point TextOrigin { get { return new Point(Rect.X + PadX, Rect.Y + PadY); } }

        // 文字が収まる本体の大きさ（文字の形そのものの幅・高さ＋上下左右の余白。文字がなければ 0）
        public Size TextNeed
        {
            get
            {
                if (string.IsNullOrEmpty(Text) || TextSize <= 0) return Size.Empty;
                using (var path = TextPath(Point.Empty))
                {
                    RectangleF tb = path.GetBounds();
                    int w = (int)Math.Ceiling(tb.Width) + PadX * 2, h = (int)Math.Ceiling(tb.Height) + PadY * 2;
                    // 丸は、内側に収まる四角に文字が入る大きさ（約 1.42 倍）
                    if (Kind == BoxKind.Ellipse) { w = (int)Math.Ceiling(w * 1.42); h = (int)Math.Ceiling(h * 1.42); }
                    return new Size(w, h);
                }
            }
        }

        // 本体を文字にぴったり合わせる（左上は動かさない）
        public void FitText()
        {
            Size need = TextNeed;
            if (need.IsEmpty) return;
            Rect.Width = need.Width;
            Rect.Height = need.Height;
            SyncTail();
        }

        // 「文字に合わせる」：中心を動かさずに、文字にぴったりの大きさにする
        public void FitTextCentered()
        {
            Size need = TextNeed;
            if (need.IsEmpty) return;
            int cx = Rect.X + Rect.Width / 2, cy = Rect.Y + Rect.Height / 2;
            Rect = new Rectangle(cx - need.Width / 2, cy - need.Height / 2, need.Width, need.Height);
            SyncTail();
        }

        // 文字が入りきらないときだけ、中心を動かさずに広げる（囲んだ範囲は変えない）
        public void GrowToText()
        {
            Size need = TextNeed;
            if (need.IsEmpty || (Rect.Width >= need.Width && Rect.Height >= need.Height)) return;
            int w = Math.Max(Rect.Width, need.Width), h = Math.Max(Rect.Height, need.Height);
            int cx = Rect.X + Rect.Width / 2, cy = Rect.Y + Rect.Height / 2;
            Rect = new Rectangle(cx - w / 2, cy - h / 2, w, h);
            SyncTail();
        }

        // 文字を入れたあとの大きさ合わせ：吹き出しはぴったり、ほかの形は入りきらないときだけ広げる
        public void AdjustToText() { if (IsCallout) FitText(); else GrowToText(); }

        public override void Draw(Graphics g)
        {
            using (var path = MakePath())
            {
                if (Fill != FillMode.None)
                    using (var b = new SolidBrush(Fill == FillMode.Light ? Color.FromArgb(70, FillColor) : FillColor)) g.FillPath(b, path);
                using (var p = new Pen(Color, Width)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
            }
            if (CanHoldText && !HideText && !string.IsNullOrEmpty(Text))
                using (var tp = TextPath(Point.Empty))
                using (var b = new SolidBrush(TextColor))
                using (var m = new Matrix())
                {
                    // 文字の形そのものを、本体の上下左右のまん中に置く（フォントの上の余白で下寄りに見えないように）
                    RectangleF tb = tp.GetBounds();
                    m.Translate(Rect.X + (Rect.Width - tb.Width) / 2f - tb.X, Rect.Y + (Rect.Height - tb.Height) / 2f - tb.Y);
                    tp.Transform(m);
                    g.FillPath(b, tp);
                }
        }
        // 塗りがあり、点を覆い隠しているか（くっつく点を隠すかどうかの判定）
        public bool Covers(Point pt)
        {
            if (Fill == FillMode.None) return false;
            using (var path = MakePath()) return path.IsVisible(pt);
        }

        public override bool HitTest(Point pt)
        {
            using (var path = MakePath())
            {
                if (Fill != FillMode.None && path.IsVisible(pt)) return true;
                using (var p = new Pen(Color.Black, Width + Tol * 2)) return path.IsOutlineVisible(pt, p);
            }
        }
        public override Rectangle Bounds
        {
            get
            {
                var r = Rect;
                if (IsCallout) r = Rectangle.Union(r, new Rectangle(Tail, new Size(1, 1)));
                r.Inflate((int)Width + 1, (int)Width + 1);
                return r;
            }
        }
        public override void Offset(int dx, int dy) { Rect.Offset(dx, dy); Tail.Offset(dx, dy); }
        public override Shape Clone() { return (Shape)MemberwiseClone(); }
    }

    class TextShape : Shape
    {
        public string Text; public Point Location; public float Size;
        GraphicsPath MakePath()
        {
            var path = new GraphicsPath();
            path.AddString(Text, Ui.TextFamily, (int)FontStyle.Bold, Size, Location, StringFormat.GenericTypographic);
            return path;
        }
        public override void Draw(Graphics g)
        {
            using (var path = MakePath())
            {
                Color outline = Color.GetBrightness() > 0.8f ? Color.FromArgb(40, 40, 40) : Color.White;
                using (var p = new Pen(outline, Math.Max(3f, Size / 5f))) { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
                using (var b = new SolidBrush(Color)) g.FillPath(b, path);
            }
        }
        public override bool HitTest(Point pt) { var r = Bounds; r.Inflate(Ui.S(4), Ui.S(4)); return r.Contains(pt); }
        public override Rectangle Bounds
        {
            get
            {
                using (var path = MakePath())
                {
                    RectangleF b = path.GetBounds();
                    if (b.Width <= 0) b = new RectangleF(Location.X, Location.Y, Size, Size);
                    var r = Rectangle.Round(b);
                    r.Inflate(Ui.S(3), Ui.S(3));
                    return r;
                }
            }
        }
        public override void Offset(int dx, int dy) { Location.Offset(dx, dy); }
        public override Shape Clone() { return (Shape)MemberwiseClone(); }
    }

    class MosaicShape : Shape
    {
        public Rectangle Rect; public Bitmap Pixels; public int Block;
        // 塗りつぶし：モザイクではなく黒く塗って、元の文字や絵を確実に隠す（パスワードなど）
        public bool Solid;
        public Color SolidColor = Color.FromArgb(24, 24, 24);   // 塗りつぶしの色（黒・グレー・白）
        public override bool HasColor { get { return false; } }
        public override void Draw(Graphics g)
        {
            if (Solid) { using (var b = new SolidBrush(SolidColor)) g.FillRectangle(b, Rect); return; }
            if (Pixels != null) g.DrawImage(Pixels, Rect);
        }
        public override bool HitTest(Point pt) { return Rect.Contains(pt); }
        public override Rectangle Bounds { get { return Rect; } }
        public override void Offset(int dx, int dy) { Rect.Offset(dx, dy); }
        public override Shape Clone() { return (Shape)MemberwiseClone(); }

        public void Refresh(Bitmap src)
        {
            if (Solid) { Pixels = null; return; }   // 塗りつぶしは元の画像を使わない
            Rectangle r = Rect;
            r.Intersect(new Rectangle(Point.Empty, src.Size));
            if (r.Width <= 0 || r.Height <= 0) { Pixels = null; return; }
            int sw = Math.Max(1, (r.Width + Block - 1) / Block), sh = Math.Max(1, (r.Height + Block - 1) / Block);
            using (var small = new Bitmap(sw, sh))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                }
                var outBmp = new Bitmap(Rect.Width, Rect.Height);
                using (var g = Graphics.FromImage(outBmp))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(small, new Rectangle(r.X - Rect.X, r.Y - Rect.Y, sw * Block, sh * Block));
                }
                Pixels = outBmp;
            }
        }
    }
}
