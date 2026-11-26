// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 撮影の流れ（撮る → 書き込む → コピー・保存 → 案内）と、画像の保存・コピー
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
    static class ImageSaver
    {
        // 設定の保存先に PNG で保存して、そのパスを返す（失敗したら例外）
        public static string Save(Bitmap img)
        {
            string dir = Settings.SaveFolder;
            Directory.CreateDirectory(dir);
            string baseName = "ShotCopy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, baseName + ".png");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, baseName + "_" + i + ".png");
            img.Save(path, ImageFormat.Png);
            return path;
        }
        // 決まった場所へ PNG で保存（「名前を付けて保存...」用）
        public static void SaveTo(Bitmap img, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            img.Save(path, ImageFormat.Png);
        }
        public static void ShowInFolder(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    static class ShotFlow
    {
        // captureDone: 撮影画面が閉じたとき / allDone: 案内表示まで含めてすべて終わったとき / onResult: 画像ができたとき
        public static void Begin(int delayMs, CaptureMode mode, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            if (mode == CaptureMode.Timer) { BeginTimer(Settings.TimerSeconds, captureDone, allDone, onResult); return; }
            if (delayMs <= 0) { Run(mode, captureDone, allDone, onResult); return; }
            var t = new System.Windows.Forms.Timer();
            t.Interval = delayMs;
            t.Tick += delegate { t.Stop(); t.Dispose(); Run(mode, captureDone, allDone, onResult); };
            t.Start();
        }

        // ---------- タイマー撮影 ----------
        // 右下に残りの秒数を出して待ち、時間になったら（表示を消してから）画面を止めて撮る
        static System.Windows.Forms.Timer countdownTimer;
        static CountdownForm countdownForm;
        static ClickSpotForm cancelHit;
        static EscKey escKey;
        static Action pendingCaptureDone, pendingAllDone;

        public static bool TimerActive { get { return countdownTimer != null; } }

        public static void BeginTimer(int seconds, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            CancelTimerQuiet();
            seconds = Math.Max(1, Math.Min(30, seconds));
            pendingCaptureDone = captureDone; pendingAllDone = allDone;
            int left = seconds;
            countdownForm = new CountdownForm();
            countdownForm.SetNumber(left);
            countdownForm.Show();
            // 「× やめる」の部分だけはクリックできるようにする（ほかはクリックがすり抜ける）
            cancelHit = new ClickSpotForm(countdownForm.CancelScreenRect);
            cancelHit.Clicked += delegate { CancelTimer(); };
            cancelHit.Show();
            // Esc でもやめられるようにする（数字の表示はキーを受け取らないので、待っている間だけ Esc を受け取る）
            escKey = new EscKey();
            escKey.Pressed += delegate { CancelTimer(); };
            Action fire = delegate
            {
                CancelTimerQuiet();
                // 表示が画面から消えるのを少し待ってから撮る
                var t2 = new System.Windows.Forms.Timer();
                t2.Interval = 180;
                t2.Tick += delegate { t2.Stop(); t2.Dispose(); Run(CaptureMode.Select, captureDone, allDone, onResult, Settings.TimerCursor, false, true); };
                t2.Start();
            };
            countdownTimer = new System.Windows.Forms.Timer();
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate
            {
                left--;
                if (left > 0) { countdownForm.SetNumber(left); return; }
                fire();
            };
            countdownTimer.Start();

            // 待っている間に Ctrl を 1 回だけ押して離したら、すぐ撮る（メニューは Ctrl だけでは閉じないことが多い）。
            // キーボードフックは使わず、数字が出ている間だけ「Ctrl が押されたか」を見る。
            // Ctrl＋クリック・Ctrl＋C などのときは撮らないよう、ほかのキーやマウスのボタンも一緒に押されていないかだけ確かめる（何のキーかは覚えない）
            bool waitRelease = Native.KeyDown(0x11);   // 始めたときに Ctrl を押していたら、いったん離すまで待つ
            bool down = false, clean = false;
            DateTime downAt = DateTime.MinValue;
            Point downCursor = Point.Empty;
            ctrlWatch = new System.Windows.Forms.Timer();
            ctrlWatch.Interval = 30;
            ctrlWatch.Tick += delegate
            {
                bool ctrl = Native.KeyDown(0x11);
                if (waitRelease) { if (!ctrl) waitRelease = false; return; }
                if (ctrl && !down) { down = true; clean = !Native.OtherKeyDown(); downAt = DateTime.Now; downCursor = Cursor.Position; return; }
                if (ctrl && down) { if (clean && Native.OtherKeyDown()) clean = false; return; }
                if (!ctrl && down)
                {
                    down = false;
                    // 短く 1 回押しただけのときだけ（Ctrl＋ホイールでの拡大や、Ctrl を押したままの操作では撮らない）
                    Point c = Cursor.Position;
                    bool still = Math.Abs(c.X - downCursor.X) + Math.Abs(c.Y - downCursor.Y) < Ui.S(6);
                    if (clean && still && (DateTime.Now - downAt).TotalMilliseconds < 500) fire();
                }
            };
            ctrlWatch.Start();
        }

        static System.Windows.Forms.Timer ctrlWatch;

        // 待っている途中でやめる
        public static void CancelTimer()
        {
            Action cd = pendingCaptureDone, ad = pendingAllDone;
            bool was = TimerActive;
            CancelTimerQuiet();
            if (!was) return;
            if (cd != null) cd();
            if (ad != null) ad();
        }

        static void CancelTimerQuiet()
        {
            if (countdownTimer != null) { countdownTimer.Stop(); countdownTimer.Dispose(); countdownTimer = null; }
            if (ctrlWatch != null) { ctrlWatch.Stop(); ctrlWatch.Dispose(); ctrlWatch = null; }
            if (countdownForm != null) { countdownForm.Close(); countdownForm.Dispose(); countdownForm = null; }
            if (cancelHit != null) { cancelHit.Close(); cancelHit.Dispose(); cancelHit = null; }
            if (escKey != null) { escKey.Dispose(); escKey = null; }
            pendingCaptureDone = null; pendingAllDone = null;
        }

        // 「書き直す」：トレイ側でこれを受けて BeginReEdit を呼ぶ（つながっていなければ、案内にボタンを出さない）
        public static event Action ReEditRequested;
        public static bool CanReEdit { get { return ReEditRequested != null && LastSession.Available; } }

        // 最後の画像を、書き込みを直せる状態で開き直す
        public static void BeginReEdit(Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            if (!LastSession.Available) { if (captureDone != null) captureDone(); if (allDone != null) allDone(); return; }
            Run(CaptureMode.Select, captureDone, allDone, onResult, false, true);
        }

        // fromTimer：タイマーで撮った（範囲を選ぶ画面に「タイマーで撮りました」と出す）
        static void Run(CaptureMode mode, Action captureDone, Action allDone, Action<Bitmap> onResult, bool withCursor = false, bool reEdit = false, bool fromTimer = false)
        {
            // 撮影を始める直前に使っていた窓（閉じたらそこへ戻して、すぐ Ctrl+V で貼れるようにする）
            IntPtr prevWindow = Native.ReturnWindow();
            CaptureOverlay ov;
            try
            {
                ov = reEdit ? new CaptureOverlay(LastSession.Take()) : new CaptureOverlay(mode, withCursor);
                if (fromTimer) ov.MarkFromTimer();
            }
            catch (Exception ex)
            {
                MessageBox.Show("画面を取得できませんでした。\n" + ex.Message, "ShotCopy");
                if (captureDone != null) captureDone();
                if (allDone != null) allDone();
                return;
            }
            ov.FormClosed += delegate
            {
                // 撮影画面の「つなげて撮る」ボタンで部分を選んだ：そのままつなげて撮る
                if (!ov.PickedArea.IsEmpty)
                {
                    Rectangle area = ov.PickedArea;
                    ov.Dispose();
                    StartScrollSession(area, prevWindow, captureDone, allDone, onResult);
                    return;
                }
                // 撮影画面の「タイマー」ボタン：閉じてから数秒待って撮り直す
                if (ov.TimerRequest > 0)
                {
                    int sec = ov.TimerRequest;
                    ov.Dispose();
                    BeginTimer(sec, captureDone, allDone, onResult);
                    return;
                }
                Bitmap result = ov.Result;
                Point where = ov.ResultScreenCenter;
                bool saveNow = ov.SaveRequested || (result != null && Settings.AutoSave);   // 設定で「コピーと同時に保存」
                string saveAs = ov.SaveAsPath;   // 「名前を付けて保存...」で選んだ場所
                ov.Dispose();
                if (captureDone != null) captureDone();
                Deliver(result, where, saveNow, saveAs, prevWindow, CanReEdit, allDone, onResult);
            };
            ov.Show();
        }

        // できた画像をコピーして（必要なら保存して）、案内を出す。result が null なら何もしない
        static void Deliver(Bitmap result, Point where, bool saveNow, string saveAs, IntPtr prevWindow, bool canReEdit, Action allDone, Action<Bitmap> onResult)
        {
            if (result == null) { if (allDone != null) allDone(); return; }
            if (onResult != null) onResult(result);
            bool ok = ClipboardHelper.CopyImage(result);
            // 元の窓に戻す（案内は前に出ない窓なので、このあとも元の窓が前のまま）
            if (prevWindow != IntPtr.Zero) Native.SetForegroundWindow(prevWindow);
            if (ok && Settings.ToastSeconds <= 0)
            {
                // 案内なし：Ctrl+S のときだけ黙って保存（失敗したときは知らせる）
                if (saveNow || saveAs != null)
                {
                    try { if (saveAs != null) ImageSaver.SaveTo(result, saveAs); else ImageSaver.Save(result); }
                    catch (Exception ex) { MessageBox.Show("画像を保存できませんでした。\n" + ex.Message, "ShotCopy"); }
                }
                result.Dispose();
                if (allDone != null) allDone();
                return;
            }
            var toast = new ToastForm(result, ok, where, canReEdit);
            toast.ReEdit += delegate { if (ReEditRequested != null) ReEditRequested(); };
            toast.FormClosed += delegate { result.Dispose(); if (allDone != null) allDone(); };
            toast.Show();
            if (saveAs != null) toast.SaveAs(saveAs);
            else if (saveNow) toast.Save();
        }

        // ---------- つなげて撮る ----------
        // 範囲を選んでもらい、その範囲を自分でスクロールしている間に、重なりを見つけて縦につなぐ
        public static void BeginScroll(int delayMs, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = Math.Max(1, delayMs);
            t.Tick += delegate { t.Stop(); t.Dispose(); RunScroll(captureDone, allDone, onResult); };
            t.Start();
        }

        static void RunScroll(Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            IntPtr prevWindow = Native.ReturnWindow();
            CaptureOverlay ov;
            try { ov = CaptureOverlay.ForPick(); }
            catch (Exception ex)
            {
                MessageBox.Show("画面を取得できませんでした。\n" + ex.Message, "ShotCopy");
                if (captureDone != null) captureDone();
                if (allDone != null) allDone();
                return;
            }
            ov.FormClosed += delegate
            {
                Rectangle area = ov.PickedArea;
                ov.Dispose();
                if (area.IsEmpty) { if (captureDone != null) captureDone(); if (allDone != null) allDone(); return; }
                StartScrollSession(area, prevWindow, captureDone, allDone, onResult);
            };
            ov.Show();
        }

        // 選んだ部分（画面の座標）で、つなげて撮るを始める。終わったら確認画面を出して、選んだとおりにコピーする
        static void StartScrollSession(Rectangle area, IntPtr prevWindow, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            // 選んだ部分の窓を前に出しておく（キーでもスクロールできるように）
            IntPtr target = Native.TopWindowAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
            if (target != IntPtr.Zero) Native.ForceForeground(target);
            ScrollCapture sc;
            try { sc = new ScrollCapture(area); }
            catch (Exception ex)
            {
                MessageBox.Show("つなげて撮ることができませんでした。\n" + ex.Message, "ShotCopy");
                if (captureDone != null) captureDone();
                if (allDone != null) allDone();
                return;
            }
            sc.Finished += delegate(Bitmap result)
            {
                Point where = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                IntPtr back = target != IntPtr.Zero ? target : prevWindow;
                if (result == null) { if (captureDone != null) captureDone(); if (allDone != null) allDone(); return; }
                ShowScrollPreview(result, area, where, back, captureDone, allDone, onResult);
            };
            sc.Start();
        }

        // コピーする前に、つないだ画像を確かめてもらう（「書き込む」なら書き込みの画面へ）
        static void ShowScrollPreview(Bitmap result, Rectangle area, Point where, IntPtr back, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            var pv = new ScrollPreviewForm(result, area);
            pv.FormClosed += delegate
            {
                var choice = pv.Chosen;
                pv.Dispose();
                if (choice == ScrollPreviewForm.Choice.Edit) { EditLongImage(result, area, where, back, captureDone, allDone, onResult); return; }
                if (captureDone != null) captureDone();
                if (choice == ScrollPreviewForm.Choice.Cancel) { result.Dispose(); if (allDone != null) allDone(); return; }
                Deliver(result, where, choice == ScrollPreviewForm.Choice.CopyAndSave || Settings.AutoSave, null, back, false, allDone, onResult);
            };
            pv.Show();
        }

        // つないだ画像に、いつもの道具で書き込む。やめたら確認画面に戻る（つないだ画像は失わない）
        static void EditLongImage(Bitmap result, Rectangle area, Point where, IntPtr back, Action captureDone, Action allDone, Action<Bitmap> onResult)
        {
            CaptureOverlay ov;
            try { ov = new CaptureOverlay(result, Screen.FromRectangle(area).Bounds); }
            catch (Exception ex)
            {
                MessageBox.Show("書き込みの画面を開けませんでした。\n" + ex.Message, "ShotCopy");
                ShowScrollPreview(result, area, where, back, captureDone, allDone, onResult);
                return;
            }
            ov.FormClosed += delegate
            {
                Bitmap edited = ov.Result;
                bool saveNow = ov.SaveRequested || (edited != null && Settings.AutoSave);
                string saveAs = ov.SaveAsPath;
                ov.Dispose();
                if (edited == null) { ShowScrollPreview(result, area, where, back, captureDone, allDone, onResult); return; }
                result.Dispose();
                if (captureDone != null) captureDone();
                Deliver(edited, where, saveNow, saveAs, back, false, allDone, onResult);
            };
            ov.Show();
        }
    }

    // 最後にコピーした撮影（「書き直す」用）。画面の画像・範囲・書き込みを 1 回分だけ、終了するまで覚える
    class LastSession
    {
        public Bitmap Screen;
        public Rectangle VirtualScreen, Sel;
        public List<Shape> Shapes;
        // 「元に戻す」の履歴（書き直したあとも、それまでの書き込みを戻せるように）
        public List<KeyValuePair<List<Shape>, Rectangle>> Undo = new List<KeyValuePair<List<Shape>, Rectangle>>();

        static LastSession current;
        public static bool Available { get { return current != null; } }

        public static void Store(Bitmap screen, Rectangle vs, Rectangle sel, List<Shape> shapes, List<KeyValuePair<List<Shape>, Rectangle>> undo)
        {
            if (current != null && current.Screen != screen) current.Screen.Dispose();
            current = new LastSession { Screen = screen, VirtualScreen = vs, Sel = sel, Shapes = shapes, Undo = CloneUndo(undo) };
        }

        static List<KeyValuePair<List<Shape>, Rectangle>> CloneUndo(List<KeyValuePair<List<Shape>, Rectangle>> src)
        {
            var r = new List<KeyValuePair<List<Shape>, Rectangle>>();
            foreach (var u in src) r.Add(new KeyValuePair<List<Shape>, Rectangle>(CaptureOverlay.CloneShapes(u.Key), u.Value));
            return r;
        }

        // 開き直す用の写し（書き込みは写しを渡すので、やめても覚えている内容は変わらない）
        public static LastSession Take()
        {
            if (current == null) return null;
            return new LastSession { Screen = current.Screen, VirtualScreen = current.VirtualScreen, Sel = current.Sel, Shapes = CaptureOverlay.CloneShapes(current.Shapes), Undo = CloneUndo(current.Undo) };
        }
    }

    static class ClipboardHelper
    {
        public static bool CopyImage(Bitmap img)
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, new Bitmap(img));
                var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                data.SetData("PNG", false, ms);
                Clipboard.SetDataObject(data, true, 10, 100);
                return true;
            }
            catch { return false; }
        }
    }

    static class AppIcon
    {
        public const string Version = "1.0.0";
        public const string Author = "karukaze";
        public const string RepoUrl = "https://github.com/karukaze/ShotCopy";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string SponsorUrl = "";   // GitHub Sponsors を始めたら URL を入れる（空なら表示しない）

        // 使い方のページ（exe に入っている）を設定フォルダに書き出して開く。ネットにつながっていなくても見られる
        public static void OpenHelp()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotCopy");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "help.html");
                using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("help.html"))
                {
                    if (s == null) { OpenUrl(RepoUrl); return; }
                    using (var fs = File.Create(path)) s.CopyTo(fs);
                }
                Process.Start(path);
            }
            catch (Exception ex) { MessageBox.Show("使い方のページを開けませんでした。\n" + ex.Message, "ShotCopy"); }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { MessageBox.Show("ページを開けませんでした。\n" + ex.Message, "ShotCopy"); }
        }

        // 埋め込んだ ShotCopy.ico から、指定サイズのアイコンを取り出す（なければ描画版）
        public static Icon Load(Size size)
        {
            try
            {
                using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("ShotCopy.ico"))
                    if (s != null) return new Icon(s, size);
            }
            catch { }
            return Create();
        }
        public static Icon Small { get { return Load(SystemInformation.SmallIconSize); } }
        public static Icon Large { get { return Load(SystemInformation.IconSize); } }

        public static Icon Create()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    // ShotCopy.ico と同じ形：青い角丸の四角＋白い四隅のカギ
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(74, 144, 226))) Ui.FillRound(g, b, new RectangleF(1, 1, 30, 30), 7);
                    using (var p = new Pen(Color.White, 3.4f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                        g.DrawLines(p, new[] { new PointF(8, 13), new PointF(8, 8), new PointF(13, 8) });
                        g.DrawLines(p, new[] { new PointF(19, 8), new PointF(24, 8), new PointF(24, 13) });
                        g.DrawLines(p, new[] { new PointF(24, 19), new PointF(24, 24), new PointF(19, 24) });
                        g.DrawLines(p, new[] { new PointF(13, 24), new PointF(8, 24), new PointF(8, 19) });
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
