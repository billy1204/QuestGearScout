// QuestGearScout Companion - the "Addon check" window: what's installed where, what version,
// what's wrong, and other game folders on this PC. C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QGSCompanion
{
    public class CheckerForm : Form
    {
        readonly Func<float, int> S;
        readonly Func<string> currentDir;
        readonly Action<string> useFolder;
        readonly Action<string> openFolder;
        readonly Func<string, bool> confirm;
        readonly Font fBody, fBold, fHead, fBig;

        Label subtitle, scanNote;
        Panel checks;
        FlatBtn scanBtn, useBtn, openBtn;
        ListView found;
        List<FoundInstall> results;
        bool scanning, closed;
        internal AddonStatus status;
        internal string lastMessage = "";
        internal bool testMode;

        public CheckerForm(float scale, Func<string> currentDir, Action<string> useFolder, Action<string> openFolder, Func<string, bool> confirm,
                           List<FoundInstall> cached)
        {
            S = px => (int)Math.Round(px * scale);
            this.currentDir = currentDir; this.useFolder = useFolder; this.openFolder = openFolder; this.confirm = confirm;
            results = cached;
            fBody = new Font("Segoe UI", 9f); fBold = new Font("Segoe UI Semibold", 9f);
            fHead = new Font("Segoe UI Semibold", 11f); fBig = new Font("Palatino Linotype", 15f, FontStyle.Bold);
            Text = "Addon check";
            Font = fBody;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Pal.Card;
            ForeColor = Pal.Text;
            ClientSize = new Size(S(700), S(580));

            var title = new Label { Text = "Addon check", Font = fBig, AutoSize = true, Location = new Point(S(24), S(16)) };
            subtitle = new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(S(24), S(48)), Size = new Size(S(650), S(18)), AutoEllipsis = true };
            checks = new Panel { Location = new Point(S(24), S(76)), Size = new Size(S(652), S(250)), AutoScroll = true, BackColor = Pal.Card };

            var sep = new Label { AutoSize = false, BackColor = Pal.CardBorder, Location = new Point(S(24), S(336)), Size = new Size(S(652), 1) };
            var otherHead = new Label { Text = "Game folders on this PC", Font = fHead, AutoSize = true, Location = new Point(S(24), S(346)) };
            scanBtn = new FlatBtn { Kind = "secondary", Text = "Scan this PC", Height = S(30), Font = fBody };
            scanBtn.FitWidth(S(14), S(120));
            scanBtn.Location = new Point(ClientSize.Width - S(24) - scanBtn.Width, S(342));
            scanBtn.Click += (s, e) => StartScan();
            scanNote = new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(S(24), S(374)), Size = new Size(S(652), S(18)),
                Text = "Looks where World of Warcraft is usually installed, then a few folders deep on each drive. Takes a few seconds." };
            found = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Location = new Point(S(24), S(396)),
                Size = new Size(S(652), S(132)), BorderStyle = BorderStyle.FixedSingle };
            found.Columns.Add("Folder", S(330));
            found.Columns.Add("Game", S(110));
            found.Columns.Add("QuestGearScout", S(120));
            found.Columns.Add("Saved data", S(80));
            found.SelectedIndexChanged += (s, e) => UpdateFoundButtons();
            found.DoubleClick += (s, e) => UseSelected();
            useBtn = new FlatBtn { Kind = "primary", Text = "Use this folder", Height = S(30), Font = fBold, Enabled = false };
            useBtn.FitWidth(S(14), S(120));
            useBtn.Location = new Point(S(24), S(536));
            useBtn.Click += (s, e) => UseSelected();
            openBtn = new FlatBtn { Kind = "secondary", Text = "Open folder", Height = S(30), Font = fBody, Enabled = false };
            openBtn.FitWidth(S(14), S(100));
            openBtn.Location = new Point(useBtn.Right + S(8), S(536));
            openBtn.Click += (s, e) => { var f = Selected(); if (f != null) openFolder(f.Path); };
            var again = new FlatBtn { Kind = "ghost", Text = "Check again", Height = S(30), Font = fBody };
            again.FitWidth(S(14), S(100));
            var close = new FlatBtn { Kind = "secondary", Text = "Close", Height = S(30), Font = fBody, Width = S(80) };
            close.Location = new Point(ClientSize.Width - S(24) - close.Width, S(536));
            again.Location = new Point(close.Left - S(8) - again.Width, S(536));
            again.Click += (s, e) => Recheck();
            close.Click += (s, e) => Close();
            CancelButton = close;
            Controls.AddRange(new Control[] { title, subtitle, checks, sep, otherHead, scanBtn, scanNote, found, useBtn, openBtn, again, close });
            FormClosed += (s, e) => closed = true;
            Theme.Form(this);
            Theme.DarkScroll(checks);

            Recheck();
            if (results != null) ShowResults();
        }

        // ------------------------------------------------------------------
        // The checklist for the current folder
        // ------------------------------------------------------------------
        internal void Recheck()
        {
            status = AddonCheck.Run(currentDir());
            subtitle.Text = status.GameDir == "" ? "No game folder picked yet." : "Checking " + status.GameDir;
            checks.SuspendLayout();
            foreach (Control c in checks.Controls.Cast<Control>().ToList()) c.Dispose();
            checks.Controls.Clear();
            int y = 0, w = checks.Width - S(20);
            bool scanOffered = false;
            foreach (var item in status.Items)
            {
                // Only one "Find it on this PC" button, on the most important row (the bad one).
                if (item.Fix == "scan" && (scanOffered || (item.State != "bad" && status.Items.Any(i => i.Fix == "scan" && i.State == "bad"))))
                    item.Fix = null;
                if (item.Fix == "scan") scanOffered = true;
                var row = MakeRow(item, w);
                row.Location = new Point(0, y);
                checks.Controls.Add(row);
                y += row.Height + S(4);
            }
            checks.ResumeLayout();
            UpdateFoundButtons();
        }

        Panel MakeRow(CheckItem item, int w)
        {
            var row = new Panel { Width = w, BackColor = Pal.Card };
            Color c = item.State == "ok" ? Pal.Success : item.State == "warn" ? Pal.Warn : item.State == "bad" ? Pal.Error : Pal.Accent;
            string iconName = item.State == "ok" ? "check" : item.State == "info" ? "info" : "warn";
            var icon = new PictureBox { Image = Draw.IconBitmap(iconName, S(16), c), Size = new Size(S(16), S(16)), Location = new Point(0, S(2)) };
            FlatBtn btn = null;
            string action = item.Fix;
            if (action == "fix-layout") btn = new FlatBtn { Kind = "primary", Text = "Fix it", Font = fBold };
            else if (action == "scan" && !scanning) btn = new FlatBtn { Kind = "secondary", Text = "Find it on this PC", Font = fBody };
            else if (action == "open-addons") btn = new FlatBtn { Kind = "ghost", Text = "Open folder", Font = fBody };
            int textW = w - S(28) - (btn != null ? S(150) : 0);
            var t = new Label { Text = item.Title, Font = fBold, AutoSize = true, MaximumSize = new Size(textW, 0), Location = new Point(S(26), 0),
                ForeColor = item.State == "bad" ? c : Pal.Text };
            var d = new Label { Text = item.Detail ?? "", ForeColor = Pal.Sub, AutoSize = true, MaximumSize = new Size(textW, 0), Location = new Point(S(26), S(18)) };
            row.Controls.Add(icon); row.Controls.Add(t); row.Controls.Add(d);
            int h = Math.Max(S(20), d.Text == "" ? S(20) : d.Location.Y + d.PreferredHeight);
            if (btn != null)
            {
                btn.Height = S(28);
                btn.FitWidth(S(12), S(90));
                btn.Location = new Point(w - btn.Width, 0);
                btn.Click += (s, e) => RowAction(action);
                row.Controls.Add(btn);
                h = Math.Max(h, btn.Height);
            }
            row.Height = h + S(6);
            return row;
        }

        void RowAction(string action)
        {
            try
            {
                if (action == "scan") StartScan();
                else if (action == "open-addons") openFolder(Path.GetDirectoryName(status.AddonDir));
                else if (action == "fix-layout")
                {
                    string from = Path.GetDirectoryName(status.MisplacedToc);
                    if (!confirm("Move the addon into place?\n\n" + (status.MisplacedVersion != "" ? "Version:  v" + status.MisplacedVersion + "\n" : "") +
                                 "From:  " + from + "\nTo:      " + Path.Combine(status.GameDir, "Interface", "AddOns", AddonCheck.Name) +
                                 "\n\nNothing is deleted except a folder the move leaves empty. If something is in the way, it's kept with \".old\" added to its name. " +
                                 "Close the game first, or restart it afterwards.")) return;
                    lastMessage = AddonCheck.FixLayout(status);
                    if (!testMode) Theme.Box(this, lastMessage + "\n\nRestart the game (or /reload) to load it.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    useFolder(status.GameDir);   // reload the main window
                    Recheck();
                }
            }
            catch (Exception ex)
            {
                lastMessage = "error: " + ex.Message;
                if (!testMode)
                    Theme.Box(this, "That didn't work, so nothing was changed (any step already done was undone).\n\n" + (ex is UnauthorizedAccessException || ex is IOException
                        ? "Windows wouldn't let this app move the folder. Usually something has a file or window open in it: close the game, any File Explorer " +
                          "or editor windows showing that folder, then try again. If it still fails, run this app as administrator, or move the folder yourself in File Explorer."
                        : "") + "\n\nDetails: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------------------
        // Scanning for other game folders
        // ------------------------------------------------------------------
        internal void StartScan()
        {
            if (scanning) return;
            scanning = true;
            scanBtn.Enabled = false; scanBtn.Text = "Scanning..."; scanBtn.Invalidate();
            scanNote.Text = "Scanning... this takes a few seconds.";
            Recheck();
            var t = new System.Threading.Thread(() =>
            {
                List<FoundInstall> r;
                try { r = InstallScanner.Scan(20, () => closed); } catch { r = new List<FoundInstall>(); }
                if (closed || !IsHandleCreated) return;
                try { BeginInvoke((Action)(() => { results = r; scanning = false; ShowResults(); Recheck(); })); } catch (InvalidOperationException) { }
            }) { IsBackground = true };
            t.Start();
        }

        internal bool Scanning { get { return scanning; } }

        void ShowResults()
        {
            scanBtn.Enabled = true; scanBtn.Text = "Scan again"; scanBtn.FitWidth(S(14), S(120));
            scanBtn.Left = ClientSize.Width - S(24) - scanBtn.Width; scanBtn.Invalidate();
            found.BeginUpdate();
            found.Items.Clear();
            string cur = (currentDir() ?? "").TrimEnd('\\');
            foreach (var f in results)
            {
                var st = f.Status;
                bool isCur = string.Equals(f.Path, cur, StringComparison.OrdinalIgnoreCase);
                string game = (st.Product == "wowf" || Path.GetFileName(f.Path).Equals("_forever_", StringComparison.OrdinalIgnoreCase) ? "Forever" : Path.GetFileName(f.Path).Trim('_'))
                              + (st.GameVersion != "" ? " " + st.GameVersion.Split('.').Take(3).Aggregate((a, b) => a + "." + b) : "");
                var it = new ListViewItem(new[] { f.Path + (isCur ? "  (current)" : ""), game, st.Summary, st.AccountsWithData.Count > 0 ? "Yes" : "-" }) { Tag = f };
                it.UseItemStyleForSubItems = false;
                it.SubItems[2].ForeColor = st.Installed ? (st.Healthy ? Pal.Success : Pal.Warn) : st.MisplacedToc != null ? Pal.Warn : Pal.Sub;
                if (isCur) it.Font = fBold;
                found.Items.Add(it);
            }
            found.EndUpdate();
            int withAddon = results.Count(r => r.Status.Installed);
            scanNote.Text = results.Count == 0 ? "No World of Warcraft folders found. Use Folder... in the main window to pick it yourself."
                : results.Count + (results.Count == 1 ? " game folder" : " game folders") + " found, " + withAddon + " with QuestGearScout. Pick one and click Use this folder.";
            var best = InstallScanner.Suggest(results, cur);
            if (best != null)
                foreach (ListViewItem it in found.Items) if (it.Tag == best) { it.Selected = true; it.EnsureVisible(); break; }
            UpdateFoundButtons();
        }

        FoundInstall Selected() { return found.SelectedItems.Count == 1 ? (FoundInstall)found.SelectedItems[0].Tag : null; }

        void UpdateFoundButtons()
        {
            var f = Selected();
            bool isCur = f != null && string.Equals(f.Path, (currentDir() ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            useBtn.Enabled = f != null && !isCur;
            openBtn.Enabled = f != null;
            useBtn.Invalidate(); openBtn.Invalidate();
        }

        void UseSelected()
        {
            var f = Selected();
            if (f == null) return;
            useFolder(f.Path);
            Recheck();
            if (results != null) ShowResults();
        }

        internal List<FoundInstall> Results { get { return results; } }

        internal string TestText()
        {
            var lines = status.Items.Select(i => "    " + i).ToList();
            if (results != null) lines.Add("    scan: " + string.Join(" | ", results.Select(r => r.ToString()).ToArray()));
            return string.Join("\n", lines.ToArray());
        }
    }
}
