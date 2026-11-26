// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// ShotCopy のインストーラー（ShotCopy-Setup.exe）。Windows に最初から入っている .NET Framework だけで作る。
// ・このパソコンの「自分のアカウント」に入れる（%LOCALAPPDATA%\Programs\ShotCopy）。管理者の権限はいらない
// ・スタートメニューに登録し、Windows の「設定」→「アプリ」から削除できるようにする
// ・同じ exe を uninstall.exe として置き、/uninstall で起動すると削除する（設定のフォルダも消す。保存した画像は消さない）
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ShotCopy セットアップ")]
[assembly: AssemblyProduct("ShotCopy")]
[assembly: AssemblyDescription("ShotCopy installer")]
[assembly: AssemblyCompany("karukaze")]
[assembly: AssemblyCopyright("Copyright (c) 2026 karukaze. MIT License.")]

namespace ShotCopySetup
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool uninstall = args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase);
            bool quiet = Array.Exists(args, a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
            try
            {
                if (quiet)
                {
                    // 試験や自動の入れ替え用：画面を出さずに（入れる：自動起動あり・起動しない）
                    if (uninstall) Installer.Uninstall();
                    else Installer.Install(true, false, false);
                    return 0;
                }
                Application.Run(new SetupForm(uninstall));
                return 0;
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show((uninstall ? "アンインストールできませんでした。" : "インストールできませんでした。") + "\n\n" + ex.Message, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            finally
            {
                // uninstall.exe 自身は、このプログラムが終わってから消す（動いている間は消せないため）
                Installer.DeleteSelfAfterExit();
            }
        }
    }

    // 入れる・消すの中身
    static class Installer
    {
        public const string AppName = "ShotCopy";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShotCopy";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        public static string ExePath { get { return Path.Combine(InstallDir, "ShotCopy.exe"); } }
        public static string UninstallerPath { get { return Path.Combine(InstallDir, "uninstall.exe"); } }
        static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
        static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName); } }

        public static bool IsInstalled { get { return File.Exists(ExePath); } }

        // 起動中の ShotCopy があるか（入れ替えるときに、いったん終了する）
        public static bool AnyRunning { get { return Process.GetProcessesByName("ShotCopy").Length > 0; } }

        static void StopRunning()
        {
            foreach (var p in Process.GetProcessesByName("ShotCopy"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                finally { p.Dispose(); }
            }
        }

        // 入れる（上書きで新しい版にもなる）
        public static void Install(bool autoStart, bool desktopIcon, bool launch)
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShotCopy.exe"))
            {
                if (s == null) throw new InvalidOperationException("インストーラーの中に ShotCopy.exe が見つかりません。");
                using (var f = File.Create(ExePath)) s.CopyTo(f);
            }
            // 自分（このインストーラー）を、削除用として置く
            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallerPath, true);

            CreateShortcut(StartMenuLink, ExePath, "スクリーンショットに書き込んで、すぐ貼れる");
            if (desktopIcon) CreateShortcut(DesktopLink, ExePath, "スクリーンショットに書き込んで、すぐ貼れる");
            else if (File.Exists(DesktopLink)) TryDelete(DesktopLink);

            // 自動起動（ShotCopy の設定画面と同じ書き方）
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autoStart) k.SetValue(AppName, "\"" + ExePath + "\"");
                else if (k.GetValue(AppName) != null) k.DeleteValue(AppName);
            }

            // Windows の「設定」→「アプリ」に出す
            string version = "";
            try { var v = FileVersionInfo.GetVersionInfo(ExePath); version = v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart; } catch { }
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "karukaze");
                k.SetValue("DisplayIcon", ExePath);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallerPath + "\" /uninstall /quiet");
                k.SetValue("URLInfoAbout", "https://github.com/karukaze/ShotCopy");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try { k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(ExePath).Length / 1024), RegistryValueKind.DWord); } catch { }
            }

            if (launch) Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
        }

        // 画像を保存していたフォルダ（設定の保存先。中にファイルがあるときだけ。なければ null）。アンインストールの前に調べる
        public static string SavedImagesFolder()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);
            try
            {
                string settings = Path.Combine(SettingsDir, "settings.txt");
                if (File.Exists(settings))
                    foreach (string line in File.ReadAllLines(settings))
                        if (line.StartsWith("savefolder=") && line.Length > 11) folder = line.Substring(11);
                if (Directory.Exists(folder) && Directory.GetFiles(folder, "*.png").Length > 0) return folder;
            }
            catch { }
            return null;
        }

        static bool selfDeletePending;

        // 消す：ShotCopy・ショートカット・自動起動・「アプリ」の登録・設定のフォルダ。保存した画像は消さない
        public static void Uninstall()
        {
            StopRunning();
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                // 自動起動は、このインストール先を指しているときだけ消す（置くだけの版の自動起動は残す）
                var v = k == null ? null : k.GetValue(AppName) as string;
                if (v != null && v.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(AppName);
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey); } catch { }
            TryDelete(ExePath);
            try { if (Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true); } catch { }

            // 自分（uninstall.exe）は動いている間は消せないので、このプログラムが終わってから消す（DeleteSelfAfterExit）
            string self = Assembly.GetExecutingAssembly().Location;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                selfDeletePending = true;
            else
            {
                TryDelete(UninstallerPath);
                try { if (Directory.Exists(InstallDir) && Directory.GetFileSystemEntries(InstallDir).Length == 0) Directory.Delete(InstallDir); } catch { }
            }
        }

        // このプログラムが終わったあとに、uninstall.exe とインストール先のフォルダを消す。
        // 終わるまで少し時間がかかることがあるので、消せるまで 1 秒ごとに（最大 30 回）やり直す
        public static void DeleteSelfAfterExit()
        {
            if (!selfDeletePending) return;
            selfDeletePending = false;
            string f = UninstallerPath, d = InstallDir;
            string cmd = "/d /c for /l %i in (1,1,30) do (ping 127.0.0.1 -n 2 >nul & del /f /q \"" + f + "\" 2>nul & if not exist \"" + f + "\" (rmdir \"" + d + "\" 2>nul & exit))";
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.WorkingDirectory = Path.GetTempPath();   // インストール先を作業場所にすると、フォルダを消せないため
            try { Process.Start(psi); } catch { }
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        // ショートカット（.lnk）を作る。Windows に最初から入っている WScript.Shell を使う
        static void CreateShortcut(string lnk, string target, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }

    // 入れる・消すの画面（ShotCopy のほかの画面と同じ、白い背景・青いボタン）
    class SetupForm : Form
    {
        static readonly Color Accent = Color.FromArgb(24, 95, 165);
        readonly bool uninstall;
        readonly Font titleFont = new Font("Yu Gothic UI", 14f, FontStyle.Bold);
        readonly Font bodyFont = new Font("Yu Gothic UI", 10.5f);
        readonly Font noteFont = new Font("Yu Gothic UI", 9.5f);
        readonly Label title = new Label(), body = new Label(), note = new Label();
        readonly CheckBox launch = new CheckBox(), autoStart = new CheckBox(), desktop = new CheckBox();
        readonly Button primary = new Button(), secondary = new Button();
        bool done;
        string imagesFolder;   // 画像を保存していたフォルダ（アンインストールのとき。なければ null）

        public SetupForm(bool uninstall)
        {
            this.uninstall = uninstall;
            Text = uninstall ? "ShotCopy の削除" : "ShotCopy のインストール";
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = bodyFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(28, 24, 28, 20);

            var t = new TableLayoutPanel();
            t.AutoSize = true; t.ColumnCount = 1; t.Dock = DockStyle.Fill;
            Controls.Add(t);

            title.AutoSize = true; title.Font = titleFont; title.ForeColor = Color.FromArgb(25, 25, 25); title.Margin = new Padding(0, 0, 0, 10);
            body.AutoSize = true; body.MaximumSize = new Size(460, 0); body.ForeColor = Color.FromArgb(40, 40, 40); body.Margin = new Padding(0, 0, 0, 14);
            note.AutoSize = true; note.MaximumSize = new Size(460, 0); note.Font = noteFont; note.ForeColor = Color.FromArgb(80, 80, 80); note.Margin = new Padding(0, 6, 0, 0);
            t.Controls.Add(title);
            t.Controls.Add(body);

            // 文言は、ほかのソフトのインストーラーと同じ言い方にそろえる（見慣れていて安心できるように）
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            string ver = v.Major + "." + v.Minor + "." + v.Build;
            if (uninstall)
            {
                Text = "ShotCopy アンインストール";
                title.Text = "ShotCopy のアンインストール";
                body.Text = "ShotCopy と、その設定をこのパソコンから削除します。";
                imagesFolder = Installer.SavedImagesFolder();
                note.Text = "スタートメニュー・デスクトップのショートカットと、自動起動の設定も削除されます。"
                          + (imagesFolder != null ? "\nShotCopy で保存した画像（" + imagesFolder + "）は削除されません。" : "")
                          + (Installer.AnyRunning ? "\n起動中の ShotCopy は終了します。" : "");
                t.Controls.Add(note);
                primary.Text = "アンインストール";
            }
            else
            {
                bool again = Installer.IsInstalled;
                Text = "ShotCopy " + ver + " セットアップ";
                title.Text = again ? "ShotCopy の更新" : "ShotCopy のインストール";
                body.Text = again
                    ? "インストールされている ShotCopy を、バージョン " + ver + " に更新します。設定はそのまま引き継がれます。"
                    : "ShotCopy " + ver + " をこのパソコンにインストールします。";
                foreach (var c in new[] { launch, autoStart, desktop }) { c.AutoSize = true; c.Margin = new Padding(0, 2, 0, 4); t.Controls.Add(c); }
                launch.Text = "インストール完了後に ShotCopy を起動する"; launch.Checked = true;
                autoStart.Text = "Windows の起動時に ShotCopy を自動で起動する"; autoStart.Checked = true;
                desktop.Text = "デスクトップにショートカットを作成する"; desktop.Checked = false;
                note.Text = "インストール先：" + Installer.InstallDir + "\n管理者の権限は必要ありません。"
                          + (Installer.AnyRunning ? "\n起動中の ShotCopy は終了します。" : "");
                t.Controls.Add(note);
                primary.Text = again ? "更新" : "インストール";
            }

            var buttons = new FlowLayoutPanel();
            buttons.AutoSize = true; buttons.FlowDirection = FlowDirection.RightToLeft; buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 20, 0, 0);
            StyleButton(primary, true);
            StyleButton(secondary, false);
            secondary.Text = "キャンセル";
            buttons.Controls.Add(secondary);
            buttons.Controls.Add(primary);
            t.Controls.Add(buttons);
            primary.Click += delegate { Run(); };
            secondary.Click += delegate { Close(); };
            AcceptButton = primary;
            CancelButton = secondary;
        }

        static void StyleButton(Button b, bool main)
        {
            b.AutoSize = true;
            b.MinimumSize = new Size(110, 38);
            b.Padding = new Padding(10, 2, 10, 2);
            b.Margin = new Padding(8, 0, 0, 0);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = main ? Accent : Color.FromArgb(200, 200, 205);
            b.BackColor = main ? Accent : Color.White;
            b.ForeColor = main ? Color.White : Color.FromArgb(25, 25, 25);
            if (main) b.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        }

        void Run()
        {
            if (done) { Close(); return; }
            Cursor = Cursors.WaitCursor;
            primary.Enabled = false; secondary.Enabled = false;
            try
            {
                if (uninstall)
                {
                    Installer.Uninstall();
                    title.Text = "アンインストールが完了しました";
                    body.Text = "ShotCopy をご利用いただき、ありがとうございました。";
                    // 画像を保存していたときだけ、残っていることを伝える（保存していなければ何も書かない）
                    note.Text = imagesFolder != null ? "ShotCopy で保存した画像は、次のフォルダに残っています。不要な場合は手動で削除してください。\n" + imagesFolder : "";
                    note.Visible = imagesFolder != null;
                }
                else
                {
                    Installer.Install(autoStart.Checked, desktop.Checked, launch.Checked);
                    foreach (var c in new[] { launch, autoStart, desktop }) c.Visible = false;
                    title.Text = "インストールが完了しました";
                    body.Text = "ShotCopy は、画面右下の通知領域（時計の近く）にアイコンで常駐します。Ctrl+PrintScreen を押すか、アイコンをクリックするとスクリーンショットを撮れます。";
                    note.Text = "アイコンが見当たらないときは、タスクバー右端の「^」の中に隠れていることがあります。アイコンをタスクバーへドラッグして移動すると、いつでも表示されるようになります。\n"
                              + "スタートメニューの「ShotCopy」からも起動できます。アンインストールは、Windows の「設定」→「アプリ」から行えます。";
                }
                done = true;
                primary.Text = "完了";
                primary.Enabled = true;
                secondary.Visible = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, (uninstall ? "アンインストールできませんでした。" : "インストールできませんでした。") + "\n\n" + ex.Message, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                primary.Enabled = true; secondary.Enabled = true;
            }
            finally { Cursor = Cursors.Default; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); noteFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
