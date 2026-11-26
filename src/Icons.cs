// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// アイコン（画像ファイルを使わず描く）
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
    // ================= アイコン（画像ファイルを使わず描画） =================
    static class Icons
    {
        static PointF P(Rectangle r, float x, float y) { return new PointF(r.X + x / 24f * r.Width, r.Y + y / 24f * r.Height); }
        static Pen MakePen(Color c, Rectangle r, float w)
        {
            var p = new Pen(c, Math.Max(1.2f, w * r.Width / 24f));
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            return p;
        }

        public static void Tool(Graphics g, ShotCopy.Tool t, Rectangle r, Color c, Font bold)
        {
            switch (t)
            {
                case ShotCopy.Tool.Pen:
                    using (var p = MakePen(c, r, 1.8f))
                    {
                        g.DrawPolygon(p, new[] { P(r, 4, 20), P(r, 5, 15.5f), P(r, 15.5f, 5), P(r, 19, 8.5f), P(r, 8.5f, 19) });
                        g.DrawLine(p, P(r, 13, 7.5f), P(r, 16.5f, 11));
                    }
                    break;
                case ShotCopy.Tool.Marker:
                    using (var b = new SolidBrush(Color.FromArgb(110, c)))
                        g.FillRectangle(b, new RectangleF(P(r, 3, 18.5f), new SizeF(18f / 24f * r.Width, 3.5f / 24f * r.Height)));
                    using (var p = MakePen(c, r, 1.8f))
                        g.DrawPolygon(p, new[] { P(r, 7, 15), P(r, 7, 11.5f), P(r, 14.5f, 4), P(r, 19, 8.5f), P(r, 11.5f, 16), P(r, 8, 16) });
                    break;
                case ShotCopy.Tool.Arrow:
                    ArrowOf(g, ArrowKind.Arrow, r, c);
                    break;
                case ShotCopy.Tool.Shape:
                    Box(g, BoxKind.Rounded, r, c);
                    break;
                case ShotCopy.Tool.Text:
                    using (var f = new Font(Ui.TextFamily, r.Height * 0.78f, FontStyle.Bold, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, "A", f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    break;
                case ShotCopy.Tool.Mosaic:
                {
                    float cell = r.Width * 5.2f / 24f, gap = r.Width * 0.9f / 24f;
                    float start = r.X + (r.Width - (cell * 3 + gap * 2)) / 2, top = r.Y + (r.Height - (cell * 3 + gap * 2)) / 2;
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            using (var b = new SolidBrush((i + j) % 2 == 0 ? c : Color.FromArgb(95, c)))
                                g.FillRectangle(b, start + i * (cell + gap), top + j * (cell + gap), cell, cell);
                    break;
                }
            }
        }

        // 四隅のカギ（範囲を選ぶ）
        public static void Region(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 2f))
            {
                g.DrawLines(p, new[] { P(r, 4, 9), P(r, 4, 4), P(r, 9, 4) });
                g.DrawLines(p, new[] { P(r, 15, 4), P(r, 20, 4), P(r, 20, 9) });
                g.DrawLines(p, new[] { P(r, 20, 15), P(r, 20, 20), P(r, 15, 20) });
                g.DrawLines(p, new[] { P(r, 9, 20), P(r, 4, 20), P(r, 4, 15) });
            }
        }

        // 歯車（設定）
        public static void Gear(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.9f))
            {
                var cpt = P(r, 12, 12);
                float ro = r.Width * 8.5f / 24f, ri = r.Width * 6f / 24f, rc = r.Width * 2.6f / 24f;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(p, (float)(cpt.X + Math.Cos(a) * ri), (float)(cpt.Y + Math.Sin(a) * ri),
                                  (float)(cpt.X + Math.Cos(a) * ro), (float)(cpt.Y + Math.Sin(a) * ro));
                }
                g.DrawEllipse(p, cpt.X - ri, cpt.Y - ri, ri * 2, ri * 2);
                g.DrawEllipse(p, cpt.X - rc, cpt.Y - rc, rc * 2, rc * 2);
            }
        }

        public static void ArrowOf(Graphics g, ArrowKind k, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.9f))
            {
                g.DrawLine(p, P(r, 5, 19), P(r, 19, 5));
                if (k != ArrowKind.Line) g.DrawLines(p, new[] { P(r, 11, 5), P(r, 19, 5), P(r, 19, 13) });
                if (k == ArrowKind.Double) g.DrawLines(p, new[] { P(r, 5, 11), P(r, 5, 19), P(r, 13, 19) });
            }
        }

        // 文字の形そのものの大きさを測って、枠のちょうど真ん中に描く（文字の前後の余白で左右にずれないように）
        static void DrawGlyphCentered(Graphics g, string s, float emPx, Brush b, RectangleF box)
        {
            using (var path = new GraphicsPath())
            {
                path.AddString(s, Ui.TextFamily, (int)FontStyle.Bold, emPx, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF gb = path.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate(box.X + (box.Width - gb.Width) / 2f - gb.X, box.Y + (box.Height - gb.Height) / 2f - gb.Y);
                    path.Transform(m);
                }
                g.FillPath(b, path);
            }
        }

        public static void Box(Graphics g, BoxKind k, Rectangle r, Color c)
        {
            if (k == BoxKind.Number || k == BoxKind.Letter)
            {
                // ほかの形と同じ太さの線の丸に、同じ色の「1」または「A」
                var cr = new RectangleF(P(r, 4, 4), new SizeF(16f / 24f * r.Width, 16f / 24f * r.Height));
                using (var p = MakePen(c, r, 1.9f)) g.DrawEllipse(p, cr);
                using (var b = new SolidBrush(c))
                    DrawGlyphCentered(g, k == BoxKind.Number ? "1" : "A", cr.Height * 0.56f, b, cr);
                return;
            }
            if (k == BoxKind.Callout)
            {
                using (var p = MakePen(c, r, 1.9f))
                using (var path = BoxShape.CalloutPath(new RectangleF(P(r, 3.5f, 4.5f), new SizeF(17f / 24f * r.Width, 11.5f / 24f * r.Height)), P(r, 6.5f, 20.5f), 0))
                { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
                return;
            }
            using (var p = MakePen(c, r, 1.9f))
            {
                var rr = new RectangleF(P(r, 4, 5.5f), new SizeF(16f / 24f * r.Width, 13f / 24f * r.Height));
                if (k == BoxKind.Ellipse) g.DrawEllipse(p, rr);
                else if (k == BoxKind.Square) g.DrawRectangle(p, rr.X, rr.Y, rr.Width, rr.Height);
                else Ui.DrawRound(g, p, rr, r.Width / 7f);
            }
        }

        public static void Undo(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.9f))
            {
                g.DrawBezier(p, P(r, 19, 18), P(r, 19.5f, 10), P(r, 13, 8.5f), P(r, 5.5f, 9.5f));
                g.DrawLines(p, new[] { P(r, 9.5f, 5.5f), P(r, 5.5f, 9.5f), P(r, 9.5f, 13.5f) });
            }
        }

        // ストップウォッチ（タイマー撮影）
        public static void Timer(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.9f))
            {
                g.DrawEllipse(p, new RectangleF(P(r, 4.5f, 6), new SizeF(15f / 24f * r.Width, 15f / 24f * r.Height)));
                g.DrawLine(p, P(r, 10, 3), P(r, 14, 3));
                g.DrawLine(p, P(r, 12, 3), P(r, 12, 6));
                g.DrawLine(p, P(r, 12, 13.5f), P(r, 12, 9.5f));
                g.DrawLine(p, P(r, 12, 13.5f), P(r, 15, 15));
            }
        }

        public static void Monitor(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.9f))
            {
                Ui.DrawRound(g, p, new RectangleF(P(r, 3, 4), new SizeF(18f / 24f * r.Width, 12.5f / 24f * r.Height)), r.Width / 14f);
                g.DrawLine(p, P(r, 12, 16.5f), P(r, 12, 20));
                g.DrawLine(p, P(r, 8, 20), P(r, 16, 20));
            }
        }

        public static void Close(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 2f))
            {
                g.DrawLine(p, P(r, 6.5f, 6.5f), P(r, 17.5f, 17.5f));
                g.DrawLine(p, P(r, 17.5f, 6.5f), P(r, 6.5f, 17.5f));
            }
        }

        public static void Copy(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.8f))
            {
                Ui.DrawRound(g, p, new RectangleF(P(r, 8, 8), new SizeF(12f / 24f * r.Width, 13f / 24f * r.Height)), r.Width / 12f);
                g.DrawLines(p, new[] { P(r, 15.5f, 5), P(r, 15.5f, 4), P(r, 4, 4), P(r, 4, 17), P(r, 5, 17) });
            }
        }

        // 番号を増やす（上向きの三角）・減らす（下向きの三角）
        public static void UpDown(Graphics g, Rectangle r, Color c, bool up)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, w = r.Width * 0.30f, h = r.Height * 0.20f;
            var pts = up
                ? new[] { new PointF(cx - w, cy + h), new PointF(cx + w, cy + h), new PointF(cx, cy - h) }
                : new[] { new PointF(cx - w, cy - h), new PointF(cx + w, cy - h), new PointF(cx, cy + h) };
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        // 塗りつぶし（確実に隠す）：モザイクの絵と同じ大きさの、塗った四角。fill の色で塗り、ふちは line の色
        // （白で塗るときも、白い背景で見えるように、ふちを描く）
        public static void SolidFill(Graphics g, Rectangle r, Color fill, Color line)
        {
            float s = r.Width * 17.4f / 24f;
            var sq = new RectangleF(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s);
            float rad = r.Width * 2.5f / 24f;
            using (var b = new SolidBrush(fill)) Ui.FillRound(g, b, sq, rad);
            using (var p = new Pen(line, Math.Max(1f, r.Width / 16f))) Ui.DrawRound(g, p, sq, rad);
        }

        public static void Download(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 2.2f))
            {
                g.DrawLine(p, P(r, 12, 3), P(r, 12, 15));
                g.DrawLines(p, new[] { P(r, 7, 10.5f), P(r, 12, 15.5f), P(r, 17, 10.5f) });
                g.DrawLines(p, new[] { P(r, 4, 16), P(r, 4, 20.5f), P(r, 20, 20.5f), P(r, 20, 16) });
            }
        }

        // 文字に合わせる：「A」を両側から矢印ではさむ
        public static void FitText(Graphics g, Rectangle r, Color c)
        {
            using (var f = new Font(Ui.TextFamily, r.Height * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("A", f, b, new RectangleF(P(r, 7, 5), new SizeF(10f / 24f * r.Width, 14f / 24f * r.Height)), sf);
            using (var p = MakePen(c, r, 1.7f))
            {
                g.DrawLine(p, P(r, 1.5f, 12), P(r, 6, 12)); g.DrawLines(p, new[] { P(r, 4, 9.5f), P(r, 6, 12), P(r, 4, 14.5f) });
                g.DrawLine(p, P(r, 22.5f, 12), P(r, 18, 12)); g.DrawLines(p, new[] { P(r, 20, 9.5f), P(r, 18, 12), P(r, 20, 14.5f) });
            }
        }

        // 囲む：角丸の枠の中に「A」
        public static void WrapText(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 1.7f)) Ui.DrawRound(g, p, new RectangleF(P(r, 3, 5), new SizeF(18f / 24f * r.Width, 14f / 24f * r.Height)), r.Width / 6f);
            using (var f = new Font(Ui.TextFamily, r.Height * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("A", f, b, new RectangleF(P(r, 3, 5.5f), new SizeF(18f / 24f * r.Width, 14f / 24f * r.Height)), sf);
        }

        public static void Trash(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 2f))
            {
                g.DrawLine(p, P(r, 3, 6), P(r, 21, 6));
                g.DrawLines(p, new[] { P(r, 9, 6), P(r, 9, 3), P(r, 15, 3), P(r, 15, 6) });
                g.DrawPolygon(p, new[] { P(r, 5.5f, 6), P(r, 7, 21), P(r, 17, 21), P(r, 18.5f, 6) });
            }
        }

        public static void Dot(Graphics g, Rectangle r, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(c.GetBrightness() > 0.85f ? 150 : 50, 0, 0, 0), 1)) g.DrawEllipse(p, r);
        }

        // つなげて撮る：2 枚の画面を縦につないだ、長い 1 枚（つなぎ目を点線で）。
        // （紙＋下向きの矢印だと「ダウンロード」に見えるため、この形にした）
        public static void Scroll(Graphics g, Rectangle r, Color c)
        {
            using (var p = MakePen(c, r, 2f))
            {
                p.LineJoin = LineJoin.Round;
                using (var path = Ui.Round(new RectangleF(P(r, 6, 2).X, P(r, 6, 2).Y, r.Width * 12f / 24f, r.Height * 20f / 24f), r.Width * 2f / 24f))
                    g.DrawPath(p, path);
                // 上の画面と下の画面の中身（横線）
                g.DrawLine(p, P(r, 9, 6.5f), P(r, 15, 6.5f));
                g.DrawLine(p, P(r, 9, 17.5f), P(r, 15, 17.5f));
            }
            // つなぎ目（点線）
            using (var p = MakePen(c, r, 1.6f))
            {
                p.DashStyle = DashStyle.Dash;
                p.StartCap = LineCap.Flat; p.EndCap = LineCap.Flat;
                g.DrawLine(p, P(r, 3, 12), P(r, 21, 12));
            }
        }

        public static void Chevron(Graphics g, Rectangle r, Color c)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = r.Width * 0.28f;
            using (var p = new Pen(c, Math.Max(1.2f, r.Width / 8f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLines(p, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
            }
        }
    }
}
