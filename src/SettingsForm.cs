// ShotCopy - スクリーンショットにその場で書き込み、コピーして貼り付けるツール
// Copyright (c) 2026 karukaze
// SPDX-License-Identifier: MIT
// Released under the MIT License. See the LICENSE file for details.
//
// 設定画面
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
    // クリックしてキーを押すと、その組み合わせを登録する入力欄
    class HotkeyBox : TextBox
    {
        public HotkeySpec Value = HotkeySpec.None;
        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            Width = Ui.S(210);
            ShowValue();
        }
        public void SetValue(HotkeySpec v) { Value = v; ShowValue(); }
        void ShowValue() { Text = Value.ToString(); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); BackColor = Color.FromArgb(255, 252, 225); Text = "キーを押してください"; }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); BackColor = Color.White; errorTip.Hide(this); ShowValue(); }

        static uint ModsOf(Keys mods)
        {
            uint m = 0;
            if ((mods & Keys.Control) != 0) m |= 2;
            if ((mods & Keys.Alt) != 0) m |= 1;
            if ((mods & Keys.Shift) != 0) m |= 4;
            return m;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if ((key == Keys.Tab || key == Keys.Escape || key == Keys.Enter) && mods == Keys.None) return base.ProcessCmdKey(ref msg, keyData);
            if ((key == Keys.Back || key == Keys.Delete) && mods == Keys.None) { SetValue(HotkeySpec.None); return true; }
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                Text = new HotkeySpec(ModsOf(mods), Keys.None).IsNone && mods != Keys.None
                    ? string.Join("+", PartsOf(mods)) + "+..." : "キーを押してください";
                return true;
            }
            TrySet(new HotkeySpec(ModsOf(mods), key));
            return true;
        }

        // 登録してよい組み合わせか（だめなら理由を返す）
        public static string Check(HotkeySpec h)
        {
            if (h.IsNone) return null;
            int mods = ((h.Mods & 1) != 0 ? 1 : 0) + ((h.Mods & 2) != 0 ? 1 : 0) + ((h.Mods & 4) != 0 ? 1 : 0);
            bool fkey = h.Key >= Keys.F1 && h.Key <= Keys.F24;
            bool prt = h.Key == Keys.PrintScreen;
            if (Blocked(h)) return h + " は Windows やよく使うアプリの操作と重なるため使えません。別の組み合わせにしてください。";
            if (prt) return mods == 0 ? "PrintScreen は単独では使えません（Windows が先に受け取るため）。一覧の「Ctrl+PrintScreen」か「Shift+PrintScreen」を選んでください。" : null;
            if (fkey && mods == 0) return "F キーは Ctrl・Alt・Shift のどれかと組み合わせてください。";
            if (!fkey && mods < 2) return "Ctrl・Alt・Shift のうち2つ以上と組み合わせてください。";
            return null;
        }

        // Windows やよく使うアプリのショートカット（どこでも効かなくなると困るもの）
        static bool Blocked(HotkeySpec h)
        {
            uint C = 2, A = 1, S = 4;
            var list = new[] {
                new HotkeySpec(C | A, Keys.Delete), new HotkeySpec(C | S, Keys.Escape), new HotkeySpec(A, Keys.F4), new HotkeySpec(C, Keys.F4),
                new HotkeySpec(S, Keys.F10), new HotkeySpec(A, Keys.PrintScreen), new HotkeySpec(C | A, Keys.Tab), new HotkeySpec(C | S, Keys.Tab),
                new HotkeySpec(C | S, Keys.Z), new HotkeySpec(C | S, Keys.V), new HotkeySpec(C | S, Keys.C), new HotkeySpec(C | S, Keys.T),
                new HotkeySpec(C | S, Keys.N), new HotkeySpec(C | S, Keys.S), new HotkeySpec(C | S, Keys.I), new HotkeySpec(C | S, Keys.Delete),
                new HotkeySpec(C | S, Keys.Left), new HotkeySpec(C | S, Keys.Right), new HotkeySpec(C | S, Keys.Up), new HotkeySpec(C | S, Keys.Down),
                new HotkeySpec(C | A, Keys.Left), new HotkeySpec(C | A, Keys.Right), new HotkeySpec(C | A, Keys.Up), new HotkeySpec(C | A, Keys.Down) };
            foreach (var b in list) if (b.SameAs(h)) return true;
            return false;
        }

        public string LastError;
        readonly ToolTip errorTip = new ToolTip();
        void TrySet(HotkeySpec h)
        {
            string err = Check(h);
            LastError = err;
            if (err != null)
            {
                // 欄には短く出し、理由は欄の下の吹き出しで最後まで読めるようにする
                Text = "× 使えない組み合わせです";
                BackColor = Color.FromArgb(255, 232, 232);
                errorTip.Show(err, this, 0, Height + Ui.S(2), 5000);
                return;
            }
            errorTip.Hide(this);
            BackColor = Focused ? Color.FromArgb(255, 252, 225) : Color.White;
            SetValue(h);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) errorTip.Dispose();
            base.Dispose(disposing);
        }

        static string[] PartsOf(Keys mods)
        {
            var p = new List<string>();
            if ((mods & Keys.Control) != 0) p.Add("Ctrl");
            if ((mods & Keys.Alt) != 0) p.Add("Alt");
            if ((mods & Keys.Shift) != 0) p.Add("Shift");
            return p.ToArray();
        }

        // PrintScreen は押したときではなく、離したときにしか届かない
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PrintScreen) { TrySet(new HotkeySpec(ModsOf(e.Modifiers), Keys.PrintScreen)); e.Handled = true; }
            base.OnKeyUp(e);
        }
    }

    // ショートカットを一覧から選ぶ欄。PrintScreen は一覧から選ぶ（欄で PrintScreen を押すと Windows の切り取りツールが先に開くため）。
    // 「ほかのキーにする...」を選んだときだけ、キーを押して登録する入力欄を出す
    class ShortcutPicker : FlowLayoutPanel
    {
        readonly ComboBox list = new ComboBox();
        readonly HotkeyBox box = new HotkeyBox();
        static readonly HotkeySpec CtrlPrt = new HotkeySpec(2, Keys.PrintScreen), ShiftPrt = new HotkeySpec(4, Keys.PrintScreen);
        const int IdxCtrl = 0, IdxShift = 1, IdxOther = 2, IdxNone = 3;
        bool updating;
        // 設定画面の一覧（▾）はすべてこの幅にそろえる
        public static int ListWidth { get { return Ui.S(215); } }

        public ShortcutPicker(bool recommended)
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; WrapContents = false; Margin = new Padding(0);
            list.DropDownStyle = ComboBoxStyle.DropDownList;
            // 一覧（▾）は文字の大きさが決まったあとで高くなる。そのとき入れ物の高さも合わせないと、
            // 一覧の下のふちが切れて見える（GitHub の Issue #1）
            list.SizeChanged += delegate { MinimumSize = new Size(0, list.Margin.Top + list.Height + list.Margin.Bottom); };
            list.Items.Add("Ctrl+PrintScreen" + (recommended ? "（おすすめ）" : ""));
            list.Items.Add("Shift+PrintScreen");
            list.Items.Add("ほかのキーにする...");
            list.Items.Add("なし");
            list.Width = ListWidth;
            list.Margin = new Padding(3, Ui.S(4), 3, 3);
            box.Width = Ui.S(165);
            box.Margin = new Padding(Ui.S(6), Ui.S(4), 3, 3);
            box.Visible = false;
            list.SelectedIndexChanged += delegate
            {
                if (updating) return;
                int i = list.SelectedIndex;
                box.Visible = i == IdxOther;
                if (i == IdxCtrl) box.SetValue(CtrlPrt);
                else if (i == IdxShift) box.SetValue(ShiftPrt);
                else if (i == IdxNone) box.SetValue(HotkeySpec.None);
                else { box.SetValue(HotkeySpec.None); box.Focus(); }
            };
            Controls.Add(list);
            Controls.Add(box);
        }

        public HotkeySpec Value { get { return box.Value; } }

        public void SetValue(HotkeySpec v)
        {
            updating = true;
            box.SetValue(v);
            int i = v.IsNone ? IdxNone : v.SameAs(CtrlPrt) ? IdxCtrl : v.SameAs(ShiftPrt) ? IdxShift : IdxOther;
            list.SelectedIndex = i;
            box.Visible = i == IdxOther;
            updating = false;
        }
    }

    class SettingsForm : Form
    {
        readonly TrayContext tray;
        readonly ShortcutPicker keySelect = new ShortcutPicker(true), keyFull = new ShortcutPicker(false),
            keyWindow = new ShortcutPicker(false), keyTimer = new ShortcutPicker(false), keyLast = new ShortcutPicker(false);
        readonly CheckBox autoStart = new CheckBox();
        readonly CheckBox autoSave = new CheckBox();
        readonly ComboBox toastBox = new ComboBox();
        readonly ComboBox timerBox = new ComboBox();
        readonly CheckBox timerCursor = new CheckBox();
        readonly Label folderLabel = new Label();
        string folder;
        readonly Font headFont = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        readonly Font noteFont = new Font("Yu Gothic UI", 9.5f);
        readonly Font subFont = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold);

        public SettingsForm(TrayContext owner)
        {
            tray = owner;
            Text = "ShotCopy の設定";
            Icon = AppIcon.Large;
            Font = new Font("Yu Gothic UI", 10f);
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(Ui.S(18), Ui.S(8), Ui.S(18), Ui.S(14));

            var t = new TableLayoutPanel();
            t.AutoSize = true; t.ColumnCount = 3; t.Dock = DockStyle.Fill;
            for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;

            keySelect.SetValue(Settings.HotkeySelect);
            keyFull.SetValue(Settings.HotkeyFull);
            keyWindow.SetValue(Settings.HotkeyWindow);
            keyTimer.SetValue(Settings.HotkeyTimer);
            keyLast.SetValue(Settings.HotkeyLast);

            // --- スクリーンショットを撮る（始め方：ショートカットかトレイのアイコン） ---
            AddHead(t, ref row, "スクリーンショットを撮る");
            AddKeyRow(t, ref row, "ショートカット", keySelect);
            AddNote(t, ref row, "ノートパソコンで動かないときは、Fn キーを押したまま、このショートカットを押してください。");
            AddTip(t, ref row, "トレイのアイコンをクリックしても撮れます。\n" +
                               "アイコンが見えないときは、タスクバー右下の「^」を押して、ShotCopy のアイコンを\n" +
                               "タスクバーへドラッグすると、いつでも表示されます。");
            AddNote(t, ref row, "撮り始めると、ドラッグで範囲、クリックでウィンドウを選べます。\n" +
                                "画面全体やタイマーは、画面の上に出るボタンから選べます。");

            // --- 起動 ---
            AddHead(t, ref row, "起動");
            autoStart.Text = "パソコンを起動したら、ShotCopy も自動で起動する";
            autoStart.AutoSize = true;
            autoStart.Checked = Settings.AutoStart;
            autoStart.Margin = new Padding(3, Ui.S(4), 3, 0);
            t.Controls.Add(autoStart, 0, row);
            t.SetColumnSpan(autoStart, 3);
            row++;
            AddNote(t, ref row, "オフのときは、使う前にスタートメニューから ShotCopy を起動してください。");

            // --- 保存 ---
            AddHead(t, ref row, "画像の保存");
            folder = Settings.SaveFolder;
            folderLabel.AutoSize = false;
            folderLabel.AutoEllipsis = true;
            folderLabel.Size = new Size(Ui.S(330), Ui.S(26));
            folderLabel.TextAlign = ContentAlignment.MiddleLeft;
            folderLabel.Margin = new Padding(3, Ui.S(6), Ui.S(8), 3);
            folderLabel.Text = folder;
            t.Controls.Add(folderLabel, 0, row);
            t.SetColumnSpan(folderLabel, 2);
            var folderButtons = new FlowLayoutPanel();
            folderButtons.AutoSize = true; folderButtons.WrapContents = false; folderButtons.Margin = new Padding(0, Ui.S(3), 0, 0);
            var change = new Button(); change.Text = "変更..."; change.AutoSize = true;
            var open = new Button(); open.Text = "開く"; open.AutoSize = true;
            change.Click += delegate { PickFolder(); };
            open.Click += delegate
            {
                try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
            };
            var reset = new Button(); reset.Text = "標準に戻す"; reset.AutoSize = true;
            reset.Click += delegate { folder = Settings.DefaultSaveFolder; folderLabel.Text = folder; };
            folderButtons.Controls.Add(change);
            folderButtons.Controls.Add(open);
            folderButtons.Controls.Add(reset);
            t.Controls.Add(folderButtons, 2, row);
            row++;
            autoSave.Text = "コピーと同時に保存する";
            autoSave.AutoSize = true;
            autoSave.Checked = Settings.AutoSave;
            autoSave.Margin = new Padding(3, Ui.S(4), 3, 3);
            t.Controls.Add(autoSave, 0, row);
            t.SetColumnSpan(autoSave, 3);
            row++;

            // --- 詳しい設定（変える人が少ないもの。最初は閉じておく） ---
            var detToggle = new LinkLabel();
            detToggle.Text = "▸ 詳しい設定";
            detToggle.AutoSize = true; detToggle.Font = headFont;
            detToggle.LinkBehavior = LinkBehavior.HoverUnderline;
            detToggle.LinkColor = Color.FromArgb(45, 45, 45); detToggle.ActiveLinkColor = detToggle.LinkColor;
            detToggle.Margin = new Padding(0, Ui.S(18), 0, Ui.S(2));
            t.Controls.Add(detToggle, 0, row);
            t.SetColumnSpan(detToggle, 3);
            row++;

            var d = new TableLayoutPanel();
            d.AutoSize = true; d.ColumnCount = 3; d.Dock = DockStyle.Fill; d.Visible = false;
            d.Margin = new Padding(0);
            for (int i = 0; i < 3; i++) d.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int drow = 0;

            // 撮り方ごとのショートカット（ふだんは「なし」のままでよい）
            AddSubHead(d, ref drow, "撮り方ごとのショートカット（必要なときだけ）");
            AddKeyRow(d, ref drow, "画面全体を撮る", keyFull);
            AddKeyRow(d, ref drow, "いま使っているウィンドウを撮る", keyWindow);
            AddKeyRow(d, ref drow, "タイマーで撮る", keyTimer);
            AddKeyRow(d, ref drow, "前回と同じ範囲で撮る", keyLast);

            // コピー後の案内
            AddSubHead(d, ref drow, "コピーしたあとの案内");
            var toastLabel = new Label();
            toastLabel.Text = "表示する時間"; toastLabel.AutoSize = true; toastLabel.Anchor = AnchorStyles.Left;
            toastLabel.Margin = new Padding(3, Ui.S(6), Ui.S(14), 3); toastLabel.MinimumSize = new Size(LabelWidth, 0);
            d.Controls.Add(toastLabel, 0, drow);
            toastBox.DropDownStyle = ComboBoxStyle.DropDownList;
            toastBox.Items.Add("なし（出さない）");
            for (int s = 1; s <= 10; s++) toastBox.Items.Add(s + "秒");
            toastBox.SelectedIndex = Math.Max(0, Math.Min(10, Settings.ToastSeconds));
            toastBox.Width = ShortcutPicker.ListWidth;
            toastBox.Margin = new Padding(3, Ui.S(4), 3, 3);
            d.Controls.Add(toastBox, 1, drow);
            d.SetColumnSpan(toastBox, 2);
            drow++;
            AddNote(d, ref drow, "「なし」にしても、トレイのメニューの「最後の画像を保存」から\n保存できます。");

            // タイマーで撮る
            AddSubHead(d, ref drow, "タイマーで撮る");
            var timerLabel = new Label();
            timerLabel.Text = "待つ時間"; timerLabel.AutoSize = true; timerLabel.Anchor = AnchorStyles.Left;
            timerLabel.Margin = new Padding(3, Ui.S(6), Ui.S(14), 3); timerLabel.MinimumSize = new Size(LabelWidth, 0);
            d.Controls.Add(timerLabel, 0, drow);
            timerBox.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int s in Settings.TimerChoices) timerBox.Items.Add(s + "秒");
            timerBox.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.TimerChoices, Settings.NearestTimer(Settings.TimerSeconds)));
            timerBox.Width = ShortcutPicker.ListWidth;
            timerBox.Margin = new Padding(3, Ui.S(4), 3, 3);
            d.Controls.Add(timerBox, 1, drow);
            d.SetColumnSpan(timerBox, 2);
            drow++;
            timerCursor.Text = "マウスの矢印も写す";
            timerCursor.AutoSize = true;
            timerCursor.Checked = Settings.TimerCursor;
            timerCursor.Margin = new Padding(3, Ui.S(6), 3, 3);
            d.Controls.Add(timerCursor, 0, drow);
            d.SetColumnSpan(timerCursor, 3);
            drow++;
            AddNote(d, ref drow, "クリックすると消えるメニューなどを撮るときに使います。\n" +
                                 "待っている間に、撮りたいメニューを開いてください。");

            t.Controls.Add(d, 0, row);
            t.SetColumnSpan(d, 3);
            row++;
            detToggle.LinkClicked += delegate
            {
                d.Visible = !d.Visible;
                detToggle.Text = (d.Visible ? "▾ " : "▸ ") + "詳しい設定";
                FitToScreen();
            };

            // --- 下：バージョンとボタン ---
            var bottom = new TableLayoutPanel();
            bottom.AutoSize = true; bottom.ColumnCount = 2; bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0, Ui.S(18), 0, 0);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // バージョン・作成者・ライセンスと、使い方・GitHub のリンク
            var ver = new FlowLayoutPanel();
            ver.AutoSize = true; ver.FlowDirection = FlowDirection.TopDown; ver.WrapContents = false;
            ver.Anchor = AnchorStyles.Left; ver.Margin = new Padding(0, Ui.S(4), 0, 0);
            var info = new Label();
            info.Text = "ShotCopy " + AppIcon.Version + " ・ 作成：" + AppIcon.Author + " ・ MIT ライセンス";
            info.AutoSize = true; info.Font = noteFont; info.ForeColor = Color.FromArgb(80, 80, 80);
            info.Margin = new Padding(3, 0, 3, Ui.S(2));
            ver.Controls.Add(info);
            var links = new FlowLayoutPanel();
            links.AutoSize = true; links.WrapContents = false; links.Margin = new Padding(0);
            links.Controls.Add(MakeLink("使い方", delegate { AppIcon.OpenHelp(); }));
            links.Controls.Add(MakeLink("GitHub のページ", delegate { AppIcon.OpenUrl(AppIcon.RepoUrl); }));
            links.Controls.Add(MakeLink("新しい版を見る", delegate { AppIcon.OpenUrl(AppIcon.ReleasesUrl); }));
            if (AppIcon.SponsorUrl.Length > 0) links.Controls.Add(MakeLink("応援する", delegate { AppIcon.OpenUrl(AppIcon.SponsorUrl); }));
            ver.Controls.Add(links);
            var buttons = new FlowLayoutPanel();
            buttons.AutoSize = true; buttons.WrapContents = false; buttons.Anchor = AnchorStyles.Right;
            var okBtn = new Button(); okBtn.Text = "保存"; okBtn.AutoSize = true; okBtn.MinimumSize = new Size(Ui.S(84), 0);
            var cancel = new Button(); cancel.Text = "キャンセル"; cancel.AutoSize = true; cancel.MinimumSize = new Size(Ui.S(84), 0);
            okBtn.Click += delegate { SaveAndClose(); };
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(okBtn);
            buttons.Controls.Add(cancel);
            bottom.Controls.Add(ver, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);
            t.Controls.Add(bottom, 0, row);
            t.SetColumnSpan(bottom, 3);

            Controls.Add(t);
            CancelButton = cancel;
            Shown += delegate { okBtn.Focus(); };
            table = t;
            Load += delegate
            {
                // 青い案内の枠は、画面の中身の幅いっぱいにして右端をそろえる
                int w = 0;
                foreach (int cw in t.GetColumnWidths()) w += cw;
                foreach (Label tip in tips) { tip.MinimumSize = new Size(w - tip.Margin.Horizontal, 0); tip.MaximumSize = tip.MinimumSize; }
                FitToScreen();
            };
        }

        TableLayoutPanel table;

        // 画面に収まらないときは、高さを画面に合わせてスクロールできるようにする（詳しい設定を開いたときも）
        void FitToScreen()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (!AutoSize) { AutoScroll = false; table.Dock = DockStyle.Fill; AutoSize = true; }
            PerformLayout();
            if (Height > wa.Height - Ui.S(20))
            {
                AutoSize = false;
                table.Dock = DockStyle.None; table.AutoSize = true;
                AutoScroll = true;
                Size = new Size(Width + SystemInformation.VerticalScrollBarWidth, wa.Height - Ui.S(20));
            }
            if (Bottom > wa.Bottom) Location = new Point(Location.X, Math.Max(wa.Top + Ui.S(10), wa.Bottom - Height - Ui.S(10)));
        }

        LinkLabel MakeLink(string text, Action click)
        {
            var l = new LinkLabel();
            l.Text = text; l.AutoSize = true; l.Font = noteFont;
            l.Margin = new Padding(3, 0, Ui.S(10), 0);
            l.LinkClicked += delegate { click(); };
            return l;
        }

        void AddHead(TableLayoutPanel t, ref int row, string text)
        {
            var l = new Label();
            l.Text = text; l.AutoSize = true; l.Font = headFont;
            l.Margin = new Padding(0, Ui.S(row == 0 ? 6 : 18), 0, Ui.S(2));
            t.Controls.Add(l, 0, row);
            t.SetColumnSpan(l, 3);
            row++;
        }

        // 目立たせたい案内（薄い青の枠）
        readonly List<Label> tips = new List<Label>();

        void AddTip(TableLayoutPanel t, ref int row, string text)
        {
            var l = new Label();
            l.Text = text; l.AutoSize = true; l.Font = noteFont;
            l.MaximumSize = new Size(Ui.S(470), 0);
            l.BackColor = Color.FromArgb(232, 240, 253);
            l.ForeColor = Color.FromArgb(24, 64, 140);
            l.Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), Ui.S(6));
            l.Margin = new Padding(3, Ui.S(10), 3, 0);
            t.Controls.Add(l, 0, row);
            t.SetColumnSpan(l, 3);
            tips.Add(l);
            row++;
        }

        // 見出しの中の小さな見出し（同じまとまりの続き）
        void AddSubHead(TableLayoutPanel t, ref int row, string text)
        {
            var l = new Label();
            l.Text = text; l.AutoSize = true; l.Font = subFont;
            l.ForeColor = Color.FromArgb(55, 55, 55);
            l.Margin = new Padding(3, Ui.S(row == 0 ? 8 : 16), 0, Ui.S(2));
            t.Controls.Add(l, 0, row);
            t.SetColumnSpan(l, 3);
            row++;
        }

        void AddNote(TableLayoutPanel t, ref int row, string text, bool muted = true)
        {
            var l = new Label();
            l.Text = text; l.AutoSize = true; l.Font = noteFont;
            l.MaximumSize = new Size(Ui.S(470), 0);
            l.ForeColor = muted ? Color.FromArgb(80, 80, 80) : Color.FromArgb(45, 45, 45);
            l.Margin = new Padding(3, Ui.S(6), 3, 0);
            t.Controls.Add(l, 0, row);
            t.SetColumnSpan(l, 3);
            row++;
        }

        // 項目名の幅（一覧の左端を、どの見出しの中でもそろえるため）
        static int LabelWidth { get { return Ui.S(165); } }

        void AddKeyRow(TableLayoutPanel t, ref int row, string label, ShortcutPicker picker)
        {
            var l = new Label();
            l.Text = label; l.AutoSize = true; l.Anchor = AnchorStyles.Left;
            l.MinimumSize = new Size(LabelWidth, 0);
            l.Margin = new Padding(3, Ui.S(6), Ui.S(14), 3);
            t.Controls.Add(l, 0, row);
            t.Controls.Add(picker, 1, row);
            t.SetColumnSpan(picker, 2);
            row++;
        }

        void PickFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "画像の保存先を選んでください";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(folder)) dlg.SelectedPath = folder;
                if (dlg.ShowDialog(this) == DialogResult.OK) { folder = dlg.SelectedPath; folderLabel.Text = folder; }
            }
        }

        void SaveAndClose()
        {
            // 同じキーを2つの撮り方に使っていないか（念のため、使えない組み合わせも再確認）
            var specs = new[] { keySelect.Value, keyFull.Value, keyWindow.Value, keyTimer.Value, keyLast.Value };
            foreach (var s in specs)
            {
                string err = HotkeyBox.Check(s);
                if (err != null) { MessageBox.Show(this, err, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            for (int i = 0; i < specs.Length; i++)
                for (int j = i + 1; j < specs.Length; j++)
                    if (!specs[i].IsNone && specs[i].SameAs(specs[j]))
                    {
                        MessageBox.Show(this, specs[i] + " が2か所で使われています。別の組み合わせにしてください。", "ShotCopy",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

            var old = new[] { Settings.HotkeySelect, Settings.HotkeyFull, Settings.HotkeyWindow, Settings.HotkeyTimer, Settings.HotkeyLast };
            Settings.HotkeySelect = specs[0]; Settings.HotkeyFull = specs[1]; Settings.HotkeyWindow = specs[2]; Settings.HotkeyTimer = specs[3]; Settings.HotkeyLast = specs[4];
            if (tray != null)
            {
                string failed = tray.ApplyHotkeys();
                tray.SuspendHotkeys();   // 画面を閉じるまでは止めておく（閉じたら改めて登録）
                if (failed.Length > 0)
                {
                    string text = failed + " は他のアプリが使用中のため設定できませんでした。別の組み合わせにしてください。";
                    if (failed.Contains("PrintScreen"))
                    {
                        string[] apps = PrintScreenKey.RunningCandidates();
                        if (apps.Length > 0)
                            text += "\n\nPrintScreen を使うことがある次のアプリが起動しています。このアプリの設定で PrintScreen を使わないようにすると、ShotCopy で使えるようになる可能性があります。\n・" +
                                string.Join("\n・", apps);
                    }
                    MessageBox.Show(this, text, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Settings.HotkeySelect = old[0]; Settings.HotkeyFull = old[1]; Settings.HotkeyWindow = old[2]; Settings.HotkeyTimer = old[3]; Settings.HotkeyLast = old[4];
                    return;
                }
            }
            Settings.SaveFolder = folder;
            Settings.AutoSave = autoSave.Checked;
            Settings.ToastSeconds = Math.Max(0, toastBox.SelectedIndex);
            Settings.TimerSeconds = Settings.TimerChoices[Math.Max(0, timerBox.SelectedIndex)];
            Settings.TimerCursor = timerCursor.Checked;
            if (!Settings.Save())
                MessageBox.Show(this, "設定をファイルに保存できませんでした。\n" +
                    "いまの設定は ShotCopy を終了するまで使えますが、次に起動したときは前の設定に戻ります。\n\n" +
                    "保存する場所：" + Settings.FilePath + "\n理由：" + Settings.LastSaveError, "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            try { Settings.AutoStart = autoStart.Checked; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "「パソコンを起動したら、ShotCopy も自動で起動する」を設定できませんでした。\n" +
                    "職場などのパソコンでは、管理の設定で止められていることがあります。ほかの設定は保存しています。\n\n理由：" + ex.Message,
                    "ShotCopy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { headFont.Dispose(); noteFont.Dispose(); subFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
