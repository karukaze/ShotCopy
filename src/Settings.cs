// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 設定（保存・読み込み）、ショートカットの組み合わせ、PrintScreen キーまわり
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
    // ================= 設定 =================
    // ショートカットキー（なしも可）。MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4
    class HotkeySpec
    {
        public uint Mods; public Keys Key;
        public HotkeySpec(uint m, Keys k) { Mods = m; Key = k; }
        public static HotkeySpec None { get { return new HotkeySpec(0, Keys.None); } }
        public bool IsNone { get { return Key == Keys.None; } }
        public override string ToString()
        {
            if (IsNone) return "なし";
            var parts = new List<string>();
            if ((Mods & 2) != 0) parts.Add("Ctrl");
            if ((Mods & 1) != 0) parts.Add("Alt");
            if ((Mods & 4) != 0) parts.Add("Shift");
            string k = Key.ToString();
            if (Key >= Keys.D0 && Key <= Keys.D9) k = ((int)(Key - Keys.D0)).ToString();
            if (Key == Keys.PrintScreen) k = "PrintScreen";
            parts.Add(k);
            return string.Join("+", parts.ToArray());
        }
        public string Serialize() { return IsNone ? "none" : Mods + "," + (int)Key; }
        public static HotkeySpec Parse(string s, HotkeySpec fallback)
        {
            if (s == "none") return None;
            var p = s.Split(',');
            uint m; int k;
            if (p.Length == 2 && uint.TryParse(p[0], out m) && int.TryParse(p[1], out k)) return new HotkeySpec(m, (Keys)k);
            return fallback;
        }
        public bool SameAs(HotkeySpec o) { return o != null && o.Mods == Mods && o.Key == Key; }
    }

    enum CaptureMode { Select, Full, Window, Last, Timer }

    static class Settings
    {
        // 撮り方ごとのショートカット
        // 初めて使うときは「範囲を選んで撮る」だけ Ctrl + PrintScreen（2 = MOD_CONTROL）。
        // 撮影画面の中でウィンドウ・画面全体・タイマーも選べるので、ほかは「なし」でよい
        public static HotkeySpec HotkeySelect = Recommended;
        public static HotkeySpec Recommended { get { return new HotkeySpec(2, Keys.PrintScreen); } }
        public static HotkeySpec HotkeyFull = HotkeySpec.None;
        public static HotkeySpec HotkeyWindow = HotkeySpec.None;
        public static HotkeySpec HotkeyTimer = HotkeySpec.None;
        public static HotkeySpec HotkeyLast = HotkeySpec.None;    // 前回と同じ範囲で撮る
        // タイマー撮影：待つ秒数（1〜10）と、マウスポインターも写すか
        public static int TimerSeconds = 5;
        public static bool TimerCursor = true;
        public static readonly int[] TimerChoices = { 3, 5, 10 };
        // コピーと同時にファイルにも保存する
        public static bool AutoSave = false;
        // 設定ファイルの形式の版（古い版の設定を読んだときに、新しい標準へ切り替えるため）
        const int SettingsVersion = 2;

        // 秒数を 3・5・10 のどれかにそろえる（いちばん近いもの）
        public static int NearestTimer(int s)
        {
            int best = TimerChoices[0];
            foreach (int c in TimerChoices) if (Math.Abs(c - s) < Math.Abs(best - s)) best = c;
            return best;
        }
        public static string SaveFolder = DefaultSaveFolder;
        // コピー後の案内を出す秒数（0 = 出さない、1〜10）
        public static int ToastSeconds = 4;
        // 書き込みメニュー（色ボタン）で最後に選んだ色（パレットの番号）と太さ・大きさの段階
        public static int LastColor = 0;
        public static int LastSize = 1;
        static bool loaded;   // 設定ファイルを読んだあとだけ保存する（読む前の初期値で上書きしないため）

        public static void SaveIfLoaded() { if (loaded) Save(); }

        static HotkeySpec FixPlainPrintScreen(HotkeySpec h)
        {
            return h.Key == Keys.PrintScreen && h.Mods == 0 ? new HotkeySpec(PrintScreenKey.CtrlMods, Keys.PrintScreen) : h;
        }

        public static string DefaultSaveFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ShotCopy"); }
        }

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotCopy"); } }
        public static string FilePath { get { return Path.Combine(Dir, "settings.txt"); } }

        public static void Load()
        {
            loaded = true;
            int fileVersion = 0;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "hotkey.select") HotkeySelect = HotkeySpec.Parse(v, HotkeySelect);
                    else if (k == "hotkey.full") HotkeyFull = HotkeySpec.Parse(v, HotkeyFull);
                    else if (k == "hotkey.window") HotkeyWindow = HotkeySpec.Parse(v, HotkeyWindow);
                    else if (k == "hotkey.timer") HotkeyTimer = HotkeySpec.Parse(v, HotkeyTimer);
                    else if (k == "hotkey.last") HotkeyLast = HotkeySpec.Parse(v, HotkeyLast);
                    else if (k == "timer.seconds") { int n; if (int.TryParse(v, out n)) TimerSeconds = NearestTimer(n); }
                    else if (k == "autosave") AutoSave = v == "1";
                    else if (k == "settings.version") { int n; if (int.TryParse(v, out n)) fileVersion = n; }
                    else if (k == "timer.cursor") TimerCursor = v == "1";
                    else if (k == "savefolder" && v.Length > 0) SaveFolder = v;
                    else if (k == "toast.seconds")
                    {
                        int n;
                        if (int.TryParse(v, out n)) ToastSeconds = Math.Max(0, Math.Min(10, n));
                    }
                    else if (k == "draw.color") { int n; if (int.TryParse(v, out n)) LastColor = n; }
                    else if (k == "draw.size") { int n; if (int.TryParse(v, out n)) LastSize = n; }
                }
                // PrintScreen 単独は Windows が先に受け取って届かないため、Ctrl + PrintScreen に置き換える
                HotkeySelect = FixPlainPrintScreen(HotkeySelect);
                HotkeyFull = FixPlainPrintScreen(HotkeyFull);
                HotkeyWindow = FixPlainPrintScreen(HotkeyWindow);
                HotkeyTimer = FixPlainPrintScreen(HotkeyTimer);
                HotkeyLast = FixPlainPrintScreen(HotkeyLast);
                // 古い版の設定：タイマーは新しい標準（5 秒・ポインターも写す）に切り替える
                if (fileVersion < 2) { TimerSeconds = 5; TimerCursor = true; }
            }
            catch { }
        }

        // 保存できなかったときの理由（できたら null）
        public static string LastSaveError;

        // 保存できたら true。できなかったら false で、理由は LastSaveError
        // （色や太さなど、操作のついでに覚えるだけのときは結果を見ない。設定画面の「OK」では知らせる）
        public static bool Save()
        {
            LastSaveError = null;
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "hotkey.select=" + HotkeySelect.Serialize(),
                    "hotkey.full=" + HotkeyFull.Serialize(),
                    "hotkey.window=" + HotkeyWindow.Serialize(),
                    "hotkey.timer=" + HotkeyTimer.Serialize(),
                    "hotkey.last=" + HotkeyLast.Serialize(),
                    "timer.seconds=" + TimerSeconds,
                    "timer.cursor=" + (TimerCursor ? "1" : "0"),
                    "autosave=" + (AutoSave ? "1" : "0"),
                    "settings.version=" + SettingsVersion,
                    "savefolder=" + SaveFolder,
                    "toast.seconds=" + ToastSeconds,
                    "draw.color=" + LastColor,
                    "draw.size=" + LastSize });
                return true;
            }
            catch (Exception ex) { LastSaveError = ex.Message; return false; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool AutoStart
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ShotCopy") != null; }
                catch { return false; }
            }
            set
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (value) k.SetValue("ShotCopy", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("ShotCopy") != null) k.DeleteValue("ShotCopy");
                }
            }
        }
    }

    // PrintScreen キーまわり
    // PrintScreen を単独で押すと Windows が先に受け取り、登録したアプリには届かない（Windows 11 で確認）。
    // そのため ShotCopy では Ctrl または Shift との組み合わせだけを使う。組み合わせなら Windows の切り取りツールとも重ならない
    static class PrintScreenKey
    {
        public const uint CtrlMods = 2;

        // PrintScreen を使うことがあるアプリ（表示名, プロセス名）。どのアプリがキーを使っているかは Windows では調べられないため、起動中のものを候補として出す
        static readonly string[,] KnownApps = {
            { "ShareX", "ShareX" }, { "Greenshot", "Greenshot" }, { "Lightshot", "Lightshot" },
            { "PicPick", "picpick" }, { "Snagit", "Snagit32" }, { "Snagit", "SnagitCapture" },
            { "Gyazo", "Gyazo" }, { "Gyazo", "GyStation" }, { "Dropbox", "Dropbox" }, { "OneDrive", "OneDrive" } };

        public static string[] RunningCandidates()
        {
            var found = new List<string>();
            for (int i = 0; i < KnownApps.GetLength(0); i++)
            {
                string name = KnownApps[i, 0];
                if (found.Contains(name)) continue;
                try
                {
                    var ps = Process.GetProcessesByName(KnownApps[i, 1]);
                    if (ps.Length > 0) found.Add(name);
                    foreach (var p in ps) p.Dispose();
                }
                catch { }
            }
            return found.ToArray();
        }
    }

    // ================= 設定画面 =================
}
