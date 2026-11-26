// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 起動・想定外のエラーの知らせ
// Windows 標準の .NET Framework 4.x の csc.exe でビルドできるよう C# 5 の文法で書いています。
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

[assembly: System.Reflection.AssemblyTitle("ShotCopy")]
[assembly: System.Reflection.AssemblyProduct("ShotCopy")]
[assembly: System.Reflection.AssemblyDescription("Screenshot, annotate and copy")]
[assembly: System.Reflection.AssemblyCompany("karukaze")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 karukaze. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace ShotCopy
{
    // ================= 起動・常駐 =================
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.EnableDpiAwareness();
            // 想定外のエラーは .NET の英語の画面ではなく、日本語のお知らせにする（内容はファイルに記録）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ErrorReport.Show(e.Exception, false); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ErrorReport.Show(e.ExceptionObject as Exception, true); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            bool once = args.Length > 0 && args[0].ToLowerInvariant() == "/once";
            if (once)
            {
                var ctx = new ApplicationContext();
                ShotFlow.Begin(0, CaptureMode.Select, null, delegate { ctx.ExitThread(); }, null);
                Application.Run(ctx);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "ShotCopy_TrayInstance", out created))
            {
                if (!created)
                {
                    string how = Settings.HotkeySelect.IsNone ? "トレイのアイコンをクリックすると" : Settings.HotkeySelect + " で";
                    MessageBox.Show("ShotCopy はすでに起動しています。\n" + how + "スクリーンショットを撮れます。",
                        "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new TrayContext());
            }
        }
    }

    // 想定外のエラーを記録して、わかりやすく知らせる
    static class ErrorReport
    {
        static int showing;   // お知らせを重ねて出さない

        public static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotCopy", "error.log"); }
        }

        public static void Show(Exception ex, bool fatal)
        {
            string log = Write(ex);
            if (Interlocked.Exchange(ref showing, 1) == 1) return;
            try
            {
                string msg = fatal
                    ? "予期しない問題が起きたため、ShotCopy を終了します。\nもう一度起動すれば、そのまま使えます。"
                    : "予期しない問題が起きました。\nShotCopy はこのまま使えますが、うまく動かないときは、トレイのアイコンを右クリックして「終了」し、起動し直してください。";
                if (log != null) msg += "\n\n詳しい内容は次のファイルに記録しました。問い合わせるときに添えてください。\n" + log;
                // 撮影画面（最前面）の裏に隠れないように、最前面の見えない窓を親にして出す
                using (var owner = new Form())
                {
                    owner.TopMost = true;
                    owner.ShowInTaskbar = false;
                    owner.FormBorderStyle = FormBorderStyle.None;
                    owner.StartPosition = FormStartPosition.CenterScreen;
                    owner.Size = new Size(1, 1);
                    owner.Opacity = 0;
                    owner.Show();
                    MessageBox.Show(owner, msg, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref showing, 0); }
        }

        // 記録したファイルの場所を返す（書けなければ null）
        static string Write(Exception ex)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 大きくなりすぎたら作り直す
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  ShotCopy " + AppIcon.Version +
                    "  " + Environment.OSVersion + "\r\n" + (ex != null ? ex.ToString() : "(詳細なし)") + "\r\n\r\n");
                return path;
            }
            catch { return null; }
        }
    }
}
