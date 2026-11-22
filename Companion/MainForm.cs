// QuestGearScout Companion - the "Scout's Notebook" window.
// C# 5 / .NET Framework 4.x WinForms, built with the csc.exe that ships with Windows.
//
// Layout, top to bottom: header (title, "watching" pill, account, Folder...),
// next-step banner, toolbar (search, filter pills, quality, view switch, undo,
// Publish), body (chain list + detail pane, or the Table view), status strip.
// A first-run panel replaces banner/toolbar/body until saved data exists.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QGSCompanion
{
    class ChainList : ListView
    {
        public ChainList() { DoubleBuffered = true; }
    }

    class AccountItem
    {
        public string Name;
        public DateTime Saved;
        public bool ShowTime;
        public override string ToString() { return "Account: " + Name + (ShowTime ? "  (saved " + MainForm.Ago(Saved) + ")" : ""); }
    }

    class Step
    {
        public string Num, Title, Sub;
        public Color Color;
    }

    public partial class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EM_SETCUEBANNER = 0x1501;

        float scale = 1f;
        int S(int px) { return (int)Math.Round(px * scale); }

        Font fBody, fBold, fSmall, fSmallBold, fHead, fTitle, fBig, fMono;
        Bitmap warnIcon;
        string detailFor = "", savedGameDir = "";
        Action publishRefresh;     // set while the Publish dialog is open, so it can follow live changes
        IWin32Window dialogOwner;  // the Publish dialog while it's open, so confirmations sit on top of it
        bool busy;                 // an add / remove / undo is running

        BufferedPanel header, bannerHost, toolbar, splitHost, firstRun, footer;
        ScrollPanel detail;
        CardPanel banner, searchHost, listCard, detailCard, firstCard;
        Panel mainArea;
        ComboBox accountBox, qualityBox;
        FlatBtn folderBtn, bannerMain, bannerAlt, clearSearch, undoBtn, publishBtn, footPrimary, footSecondary, firstBtn, firstAlt;
        TextBox searchBox;
        PillToggle pAll, pNew, pGear, pDun, vNotebook, vTable;
        SplitContainer split;
        ListBox nb;
        ChainList table;
        LinkLabel emptyLink;
        StatusStrip strip;
        ToolStripStatusLabel lblLeft, lblMsg, lblShowing, lblLock;
        ContextMenuStrip rowMenu, publishMenu;
        ToolTip tips = new ToolTip();

        static readonly string[] ColumnNames = { "Status", "Type", "Quest giver", "Zone", "Location", "Steps", "Rewards or leads to", "First quest", "Faction" };
        static readonly int[] ColumnWidths = { 110, 70, 190, 130, 80, 64, 250, 170, 80 };

        System.Windows.Forms.Timer debounce, poll, searchTimer, freshTimer;
        FileSystemWatcher watcher;
        DateTime lastWrite;
        AddonData addon = new AddonData();
        Learned learned = new Learned();
        List<ChainRow> rows = new List<ChainRow>();
        HashSet<string> prevKeys, appeared = new HashSet<string>(), pendingKeys;
        string gameDir = "", prevPath, loadedGameDir, lastBackup, lastBackupDir;
        DateTime lastBackupTime;
        bool hasData, suppress, filling, detailQueued, uiTest, autoSelect = true, authorOpen;
        string filter = "all", view = "notebook", emptyReason = "";
        int setupStep;            // 1 folder, 2 addon, 3 saved data, 4 all fine
        int sortCol = -1, lastNbIndex;
        bool sortDesc;
        Dictionary<string, string> settings = new Dictionary<string, string>();
        Rectangle linkCopyLoc, linkAuthor, linkCopyIds;

        public MainForm() : this(false) { }

        public MainForm(bool testMode)
        {
            uiTest = testMode;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Text = "QuestGearScout Companion";
            fBody = new Font("Segoe UI", 9f);
            fBold = new Font("Segoe UI Semibold", 9f);
            fSmall = new Font("Segoe UI", 8.25f);
            fSmallBold = new Font("Segoe UI Semibold", 8.25f);
            fHead = new Font("Segoe UI Semibold", 11f);
            fTitle = new Font("Palatino Linotype", 12.5f, FontStyle.Bold);
            fBig = new Font("Palatino Linotype", 15f, FontStyle.Bold);
            fMono = new Font("Consolas", 8.5f);
            Font = fBody;
            warnIcon = Draw.IconBitmap("warn", (int)Math.Round(14 * scale), Pal.Warn);
            BackColor = Pal.Window;
            ForeColor = Pal.Text;
            Theme.DarkTitle(this);
            Theme.Tip(tips);
            Size = new Size(S(1040), S(680));
            MinimumSize = new Size(S(900), S(460));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            Icon = MakeAppIcon();

            BuildHeader();
            BuildBanner();
            BuildToolbar();
            BuildBody();
            BuildPlayers();
            BuildFilters();
            BuildFirstRun();
            BuildStatus();
            BuildMenus();

            mainArea = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Window };
            mainArea.Controls.Add(splitHost);
            mainArea.Controls.Add(toolbar);
            mainArea.Controls.Add(bannerHost);
            Controls.Add(mainArea);
            Controls.Add(firstRun);
            Controls.Add(header);
            Controls.Add(strip);
            Theme.Tree(this);
            Theme.Strip(rowMenu);
            Theme.Strip(publishMenu);
            Theme.Strip(filtersMenu);

            debounce = new System.Windows.Forms.Timer { Interval = 1200 };
            debounce.Tick += (s, e) => { debounce.Stop(); Reload(); };
            searchTimer = new System.Windows.Forms.Timer { Interval = 250 };
            searchTimer.Tick += (s, e) => { searchTimer.Stop(); Fill(); };
            poll = new System.Windows.Forms.Timer { Interval = 5000 };
            poll.Tick += (s, e) => PollTick();
            freshTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            freshTimer.Tick += (s, e) => { freshTimer.Stop(); appeared.Clear(); Fill(); };

            LoadSettings();
            savedGameDir = settings.ContainsKey("gameDir") ? settings["gameDir"] : "";
            suppress = true;
            RestoreFilters();
            gameDir = FindGameFolder();
            if (savedGameDir == "") savedGameDir = gameDir;   // first run: adopt what was found
            loadedGameDir = gameDir;
            FillAccounts();
            suppress = false;
            RestoreWindow();

            KeyDown += OnFormKey;
            FormClosing += OnClosing;
            Resize += (s, e) => LayoutBanner();
            Reload();
            InitSharing();
            poll.Start();
        }

        // ======================================================================
        // Building the window
        // ======================================================================
        void BuildHeader()
        {
            header = new BufferedPanel { Dock = DockStyle.Top, Height = S(48), BackColor = Pal.FooterBg };
            header.Paint += (s, e) =>
            {
                var g = e.Graphics;
                using (var p = new Pen(Pal.Trim, Math.Max(1, S(2)))) g.DrawLine(p, 0, header.Height - S(1), header.Width, header.Height - S(1));
                Draw.Icon(g, "compass", new RectangleF(S(16), S(12), S(24), S(24)), Pal.Accent);
                int x = S(48);
                Draw.Text(g, "QuestGearScout Companion", fTitle, new Rectangle(x, 0, S(300), header.Height), Pal.Trim, TextFormatFlags.VerticalCenter);
                x += Draw.Width(g, "QuestGearScout Companion", fTitle) + S(12);
                int maxX = (accountBox.Visible ? accountBox.Left : folderBtn.Left) - S(12);

                // Addon pill: installed version and health. Click it for the addon check.
                Color aFill, aBorder, aFore; string aIcon;
                string apill = AddonPillText(out aFill, out aBorder, out aFore, out aIcon);
                int aw = Math.Min(maxX - x, Draw.Width(g, apill, fSmallBold) + S(36));
                addonPillRect = Rectangle.Empty;
                if (aw >= S(80))
                {
                    var ar = new Rectangle(x, S(12), aw, S(24));
                    Draw.Card(g, ar, aFill, aBorder, ar.Height / 2f);
                    Draw.Icon(g, aIcon, new RectangleF(ar.X + S(8), ar.Y + S(5), S(14), S(14)), aFore);
                    Draw.Text(g, apill, fSmallBold, new Rectangle(ar.X + S(26), ar.Y, aw - S(30), ar.Height), aFore, TextFormatFlags.VerticalCenter);
                    addonPillRect = ar;
                    x = ar.Right + S(8);
                }

                // Sharing pill (click: Sharing settings). Drawn before the "watching" pill so it keeps its place.
                bool shareOn;
                string spill = SharingPillText(out shareOn);
                int sw = Draw.Width(g, spill, fSmall) + S(30);
                sharingPillRect = Rectangle.Empty;
                if (maxX - x >= sw)
                {
                    var sr = new Rectangle(x, S(12), sw, S(24));
                    Draw.Card(g, sr, shareOn ? SharedBg : Pal.Card, shareOn ? SharedBorder : Pal.CardBorder, sr.Height / 2f);
                    using (var b = new SolidBrush(shareOn ? SharedC : Pal.Muted)) g.FillEllipse(b, sr.X + S(10), sr.Y + S(8), S(8), S(8));
                    Draw.Text(g, spill, fSmall, new Rectangle(sr.X + S(22), sr.Y, sw - S(26), sr.Height), shareOn ? SharedC : Pal.Sub, TextFormatFlags.VerticalCenter);
                    sharingPillRect = sr;
                    x = sr.Right + S(8);
                }

                string pill = WatchText();
                bool ok = hasData && setupStep == 4;
                int w = Math.Min(maxX - x, Draw.Width(g, pill, fSmall) + S(30));
                if (w < S(60)) return;
                var r = new Rectangle(x, S(12), w, S(24));
                Draw.Card(g, r, ok ? Pal.SuccessBg : Pal.WarnBg, ok ? Pal.SuccessBorder : Pal.WarnBorder, r.Height / 2f);
                using (var b = new SolidBrush(ok ? Pal.SuccessDot : Pal.Warn)) g.FillEllipse(b, r.X + S(10), r.Y + S(8), S(8), S(8));
                Draw.Text(g, pill, fSmall, new Rectangle(r.X + S(22), r.Y, w - S(26), r.Height), Pal.Text, TextFormatFlags.VerticalCenter);
            };
            header.MouseMove += (s, e) =>
            {
                bool over = addonPillRect.Contains(e.Location), overShare = sharingPillRect.Contains(e.Location);
                header.Cursor = over || overShare ? Cursors.Hand : Cursors.Default;
                string tip = over ? "Check the addon: where it's installed, its version, and whether anything is wrong."
                           : overShare ? "Share chains and stats with players on your local network."
                                  : "The list updates by itself a moment after you log out, type /reload or exit the game.";
                if (tips.GetToolTip(header) != tip) tips.SetToolTip(header, tip);
            };
            header.MouseClick += (s, e) =>
            {
                if (addonPillRect.Contains(e.Location)) Run("Addon check", () => ShowChecker(false));
                else if (sharingPillRect.Contains(e.Location)) Run("Sharing", ShowSharing);
            };

            accountBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = S(240), Font = fBody };
            accountBox.SelectedIndexChanged += (s, e) => { if (suppress) return; Watch(); Reload(); SaveSettings(); };
            folderBtn = new FlatBtn { Kind = "ghost", Text = "Folder...", IconName = "folder", Height = S(30), Font = fBody };
            folderBtn.FitWidth(S(12), S(90));
            folderBtn.Click += (s, e) => Run("Folder", ShowFolderDialog);
            tips.SetToolTip(folderBtn, "Game folder, account and Refresh (F5)");
            header.Controls.Add(accountBox);
            header.Controls.Add(folderBtn);
            header.Resize += (s, e) =>
            {
                folderBtn.Location = new Point(header.Width - S(16) - folderBtn.Width, (header.Height - folderBtn.Height) / 2);
                accountBox.Location = new Point(folderBtn.Left - S(8) - accountBox.Width, (header.Height - accountBox.Height) / 2);
                header.Invalidate();
            };
        }

        // ======================================================================
        // Addon checker
        // ======================================================================
        AddonStatus check = new AddonStatus();
        List<FoundInstall> scanResults;
        FoundInstall suggestion;
        bool quickScanning;
        string scannedFor;
        Rectangle addonPillRect;

        string AddonPillText(out Color fill, out Color border, out Color fore, out string icon)
        {
            if (check.Installed && check.Healthy && !check.Items.Any(i => i.State == "warn" || i.State == "bad"))
            {
                fill = Pal.SuccessBg; border = Pal.SuccessBorder; fore = Pal.Success; icon = "check";
                return "QuestGearScout v" + (check.Version == "" ? "?" : check.Version);
            }
            if (check.Installed)
            {
                fill = Pal.WarnBg; border = Pal.WarnBorder; fore = Pal.Warn; icon = "warn";
                return "QuestGearScout v" + (check.Version == "" ? "?" : check.Version) + " - check";
            }
            fill = Pal.ErrorBg; border = Pal.ErrorBorder; fore = Pal.Error; icon = "warn";
            return check.MisplacedToc != null ? "QuestGearScout in the wrong folder" : "QuestGearScout not installed";
        }

        void RunCheck()
        {
            try { check = AddonCheck.Run(gameDir); } catch { check = new AddonStatus { GameDir = gameDir }; }
            if (check.Installed) { suggestion = null; return; }
            // Results from an earlier scan still answer for a new folder; scan again only if there are none.
            if (scanResults != null && !quickScanning) { suggestion = InstallScanner.Suggest(scanResults, gameDir); if (suggestion != null) return; }
            StartQuickScan();
        }

        // When the addon isn't in the current folder, look for it elsewhere in the background.
        void StartQuickScan()
        {
            if (quickScanning || noScan || string.Equals(scannedFor, gameDir, StringComparison.OrdinalIgnoreCase)) return;
            quickScanning = true;
            scannedFor = gameDir;
            string forDir = gameDir;
            var t = new System.Threading.Thread(() =>
            {
                List<FoundInstall> r;
                try { r = InstallScanner.Scan(15, () => IsDisposed); } catch { r = new List<FoundInstall>(); }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        quickScanning = false;
                        scanResults = r;
                        // The scan covers the whole PC, so it answers for whatever folder is current now.
                        scannedFor = gameDir;
                        suggestion = !check.Installed ? InstallScanner.Suggest(r, gameDir) : null;
                        UpdateBanner();
                        if (firstRun.Visible) { UpdateFirstButton(); PlaceFirstRun(); firstCard.Invalidate(); }
                    }));
                }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            t.Start();
        }
        internal bool noScan = false;   // set true to turn the background scan off (e.g. for debugging)

        void ShowChecker(bool autoScan)
        {
            using (var f = new CheckerForm(scale, () => gameDir, dir => ApplyGameDir(dir), OpenFolder, Confirm, scanResults) { testMode = uiTest })
            {
                if (autoScan && scanResults == null) f.Shown += (s, e) => f.StartScan();
                dialogOwner = f;
                try { f.ShowDialog(this); }
                finally { dialogOwner = null; }
                if (f.Results != null) scanResults = f.Results;
            }
            Reload();
        }

        void UseSuggestion()
        {
            if (suggestion == null) return;
            string p = suggestion.Path;
            suggestion = null;
            ApplyGameDir(p);
            Msg("Switched to " + p + ".", false);
        }

        string WatchText()
        {
            if (setupStep == 1) return "Game folder not set";
            if (!hasData) return "Waiting for the game to save";
            if (!addon.Found) return "Addon not found in this folder";
            return "Watching your game - last saved " + Ago(lastWrite);
        }

        string bState = "", bHead = "", bBody = "", bIcon = "sparkle";
        int bStep = 1;
        Color bFill = Pal.AccentBg, bBorder = Pal.AccentBorder, bTint = Pal.Accent;
        bool compact;

        void BuildBanner()
        {
            bannerHost = new BufferedPanel { Dock = DockStyle.Top, Height = S(80), BackColor = Pal.Window };
            banner = new CardPanel();
            bannerHost.Resize += (s, e) => LayoutBanner();
            banner.Paint += PaintBanner;
            bannerHost.Controls.Add(banner);
            bannerMain = new FlatBtn { Kind = "primary", Height = S(32), Font = fBold };
            bannerAlt = new FlatBtn { Kind = "secondary", Height = S(32), Font = fBody };
            bannerMain.Click += (s, e) => Run(bannerMain.Text, BannerMainClick);
            bannerAlt.Click += (s, e) => Run(bannerAlt.Text, BannerAltClick);
            tips.SetToolTip(bannerAlt, "Copies text a friend can paste in game after typing /qgs import.");
            banner.Controls.Add(bannerMain);
            banner.Controls.Add(bannerAlt);
        }

        bool wantMain, wantAlt, layingBanner;

        void LayoutBanner()
        {
            if (banner == null || bannerMain == null || layingBanner) return;
            layingBanner = true;
            try { LayoutBannerCore(); } finally { layingBanner = false; }
        }

        void LayoutBannerCore()
        {
            bool c = ClientSize.Height < S(560);
            if (c != compact)
            {
                compact = c;
                bannerHost.Height = compact ? S(48) : S(80);
            }
            // The card is placed by hand (not docked) so it always matches the host's current height.
            banner.Bounds = new Rectangle(S(16), S(8), Math.Max(S(100), bannerHost.Width - S(32)), Math.Max(S(24), bannerHost.Height - S(16)));
            int h = compact ? S(26) : S(32);
            bannerMain.Height = bannerAlt.Height = h;
            bannerMain.Visible = wantMain;
            bannerAlt.Visible = wantAlt && (!compact || !wantMain);
            int x = banner.Width - S(16), y = (banner.Height - h) / 2;
            if (bannerAlt.Visible) { bannerAlt.FitWidth(S(14), S(110)); x -= bannerAlt.Width; bannerAlt.Location = new Point(x, y); x -= S(8); }
            if (bannerMain.Visible) { bannerMain.FitWidth(S(16), S(120)); x -= bannerMain.Width; bannerMain.Location = new Point(x, y); }
            banner.Invalidate();
        }

        void PaintBanner(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int left = S(16);
            int right = (bannerMain.Visible ? bannerMain.Left : bannerAlt.Visible ? bannerAlt.Left : banner.Width) - S(12);
            if (compact)
            {
                Draw.Icon(g, bIcon, new RectangleF(left, (banner.Height - S(16)) / 2f, S(16), S(16)), bTint);
                Draw.Text(g, bHead, fBold, new Rectangle(left + S(24), 0, Math.Max(0, right - left - S(24)), banner.Height), Pal.Text, TextFormatFlags.VerticalCenter);
                return;
            }
            Draw.Icon(g, bIcon, new RectangleF(left, (banner.Height - S(28)) / 2f, S(28), S(28)), bTint);
            int x = left + S(42);
            int w = Math.Max(S(50), right - x);
            Draw.Text(g, bHead, fHead, new Rectangle(x, S(6), w, S(20)), Pal.Text, TextFormatFlags.Top);
            Draw.Text(g, bBody, fBody, new Rectangle(x, S(26), w, S(16)), Pal.Sub, TextFormatFlags.Top);
            if (bState == "new" || bState == "added")
            {
                string[] steps = { "1  Add to addon", "2  /reload in game", "3  Publish to CurseForge" };
                int tx = x;
                for (int i = 0; i < steps.Length && tx < right; i++)
                {
                    bool cur = i + 1 == bStep, done = i + 1 < bStep;
                    var f = cur ? fSmallBold : fSmall;
                    string t = (done ? "\u2713 " : "") + steps[i];
                    Draw.Text(g, t, f, new Rectangle(tx, S(44), Math.Max(0, right - tx), S(14)), cur ? bTint : Pal.Sub, TextFormatFlags.Top);
                    tx += Draw.Width(g, t, f);
                    if (i < steps.Length - 1)
                    {
                        Draw.Text(g, "   \u203A   ", fSmall, new Rectangle(tx, S(44), S(40), S(14)), Pal.Muted, TextFormatFlags.Top);
                        tx += Draw.Width(g, "   \u203A   ", fSmall);
                    }
                }
            }
        }

        void BuildToolbar()
        {
            toolbar = new BufferedPanel { Dock = DockStyle.Top, Height = S(40), BackColor = Pal.Window };
            searchHost = new CardPanel { Size = new Size(S(200), S(28)), Radius = 5f, BackColor = Pal.Input, Fill = Pal.Input, Border = Pal.InputBorder };
            searchHost.Paint += (s, e) => Draw.Icon(e.Graphics, "search", new RectangleF(S(8), S(7), S(14), S(14)), Pal.Muted);
            searchBox = new TextBox { BorderStyle = BorderStyle.None, Font = fBody, Location = new Point(S(28), S(6)), Width = S(146), BackColor = Pal.Input, ForeColor = Pal.Text };
            searchBox.HandleCreated += (s, e) => SendMessage(searchBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Search chains");
            tips.SetToolTip(searchBox, "Search givers, zones, quests or items (Ctrl+F). Esc clears it.");
            searchBox.TextChanged += (s, e) => { clearSearch.Visible = searchBox.Text.Length > 0; searchTimer.Stop(); searchTimer.Start(); };
            searchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; searchBox.Clear(); } };
            clearSearch = new FlatBtn { Kind = "ghost", Text = "\u00D7", Size = new Size(S(22), S(22)), Location = new Point(S(174), S(3)), Visible = false, Font = fBody, TabStop = false };
            clearSearch.Click += (s, e) => { searchBox.Clear(); searchBox.Focus(); };
            searchHost.Controls.Add(searchBox);
            searchHost.Controls.Add(clearSearch);
            toolbar.Controls.Add(searchHost);

            pAll = MakePill("All", "all"); pNew = MakePill("New", "new"); pNew.Dot = true;
            pGear = MakePill("Gear", "gear"); pDun = MakePill("Dungeons", "dun");
            foreach (var p in new[] { pAll, pNew, pGear, pDun }) toolbar.Controls.Add(p);

            qualityBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = S(130), Font = fBody };
            qualityBox.Items.AddRange(new object[] { "Uncommon+ gear", "Rare+ gear", "Epic+ gear" });
            tips.SetToolTip(qualityBox, "Only count gear chains whose reward is at least this good.");
            qualityBox.SelectedIndex = 0;
            qualityBox.SelectedIndexChanged += (s, e) => { if (suppress) return; Reload(); SaveSettings(); };
            toolbar.Controls.Add(qualityBox);

            vNotebook = new PillToggle { Text = "Notebook", Height = S(26), Font = fBody, Checked = true };
            vTable = new PillToggle { Text = "Table", Height = S(26), Font = fBody };
            vPlayers = new PillToggle { Text = "Players", Height = S(26), Font = fBody };
            vNotebook.Picked += (s, e) => SetView("notebook");
            vTable.Picked += (s, e) => SetView("table");
            vPlayers.Picked += (s, e) => SetView("players");
            tips.SetToolTip(vNotebook, "Chains grouped by zone, with the selected chain's steps on the right.");
            tips.SetToolTip(vTable, "One line per chain with sortable columns - handy for long lists.");
            tips.SetToolTip(vPlayers, "Leaderboard, ranks and achievements for you and the players you share with.");
            toolbar.Controls.Add(vPlayers);
            undoBtn = new FlatBtn { Kind = "ghost", Text = "", IconName = "undo", Size = new Size(S(30), S(28)), Enabled = false };
            undoBtn.Click += (s, e) => Run("Undo", UndoLast);
            tips.SetToolTip(undoBtn, "Nothing to undo yet");
            publishBtn = new FlatBtn { Kind = "secondary", Text = "Publish \u25BE", Height = S(28), Font = fBody };
            publishBtn.FitWidth(S(14), S(90));
            publishBtn.Click += (s, e) => publishMenu.Show(publishBtn, new Point(0, publishBtn.Height));
            foreach (Control c in new Control[] { vNotebook, vTable, undoBtn, publishBtn }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();
        }

        PillToggle MakePill(string text, string key)
        {
            var p = new PillToggle { Text = text, Height = S(26), Font = fBody, Tag = key };
            p.Picked += (s, e) => { filter = key; UpdatePills(); Fill(); SaveSettings(); };
            return p;
        }

        // Pill labels: full ("All 13") and short ("All") for narrow windows.
        string[] pillFull = { "All", "New", "Gear", "Dungeons" }, pillCounts = { "", "", "", "" };

        void LayoutToolbar()
        {
            if (toolbar == null || vNotebook == null) return;
            int y = (toolbar.Height - S(28)) / 2;
            // Right side first: Publish, Undo, view switch.
            int r = toolbar.Width - S(16);
            publishBtn.Location = new Point(r - publishBtn.Width, y); r = publishBtn.Left - S(8);
            undoBtn.Location = new Point(r - undoBtn.Width, y); r = undoBtn.Left - S(12);
            vTable.FitWidth(); vNotebook.FitWidth(); vPlayers.FitWidth();
            vPlayers.Location = new Point(r - vPlayers.Width, y + S(1)); r = vPlayers.Left - S(4);
            vTable.Location = new Point(r - vTable.Width, y + S(1)); r = vTable.Left - S(4);
            vNotebook.Location = new Point(r - vNotebook.Width, y + S(1));
            int limit = vNotebook.Left - S(10);
            qualityBox.Visible = false;   // the quality choice lives in the Filters menu now

            // Then the left side, shrinking step by step until it fits.
            var pills = new[] { pAll, pNew, pGear, pDun };
            // Try, in order: full layout, shorter search, pills without counts; the Filters button goes last.
            foreach (bool quality in new[] { true, false })
                foreach (int searchW in new[] { 200, 150, 120 })
                    foreach (bool counts in new[] { true, false })
                    {
                        int x = S(16);
                        searchHost.Width = S(searchW);
                        searchBox.Width = searchHost.Width - S(54);
                        clearSearch.Left = searchHost.Width - S(26);
                        searchHost.Location = new Point(x, y); x += searchHost.Width + S(12);
                        for (int i = 0; i < pills.Length; i++)
                        {
                            string t = pillFull[i] + (counts && pillCounts[i] != "" ? " " + pillCounts[i] : "");
                            if (pills[i].Text != t) pills[i].Text = t;
                            pills[i].FitWidth();
                            tips.SetToolTip(pills[i], pillFull[i] + (pillCounts[i] != "" ? ": " + pillCounts[i] : ""));
                            pills[i].Location = new Point(x, y + S(1)); x += pills[i].Width + S(6);
                        }
                        x += S(4);
                        if (filtersBtn != null) filtersBtn.Location = new Point(x, y);
                        bool fits = (quality && filtersBtn != null ? filtersBtn.Right : x) <= limit;
                        if (fits || (searchW == 120 && !counts && !quality))
                        {
                            if (filtersBtn != null) filtersBtn.Visible = quality && fits;
                            return;
                        }
                    }
        }

        void BuildBody()
        {
            splitHost = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Pal.Window, Padding = new Padding(S(16), 0, S(16), S(12)) };
            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = S(8), BackColor = Pal.Window };
            split.Panel1.BackColor = Pal.Window; split.Panel2.BackColor = Pal.Window;
            splitHost.Controls.Add(split);
            splitHost.HandleCreated += (s, e) => { try { split.Panel1MinSize = S(260); split.Panel2MinSize = S(300); split.SplitterDistance = S(380); } catch { } };

            listCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            nb = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawVariable, SelectionMode = SelectionMode.MultiExtended,
                IntegralHeight = false, BackColor = Pal.Card, ForeColor = Pal.Text, Font = fBody };
            nb.MeasureItem += (s, e) => { e.ItemHeight = nb.Items[e.Index] is GroupHeader ? S(28) : S(46); };
            nb.DrawItem += DrawNotebookItem;
            nb.SelectedIndexChanged += (s, e) => OnSelectionChanged();
            nb.KeyDown += OnListKey;
            nb.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = nb.IndexFromPoint(e.Location);
                if (i >= 0 && !nb.GetSelected(i) && nb.Items[i] is ChainRow) { nb.ClearSelected(); nb.SetSelected(i, true); }
            };
            nb.Resize += (s, e) => nb.Invalidate();
            table = new ChainList { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None,
                ShowItemToolTips = true, Visible = false, Font = fBody };
            for (int i = 0; i < ColumnNames.Length; i++)
            {
                var col = table.Columns.Add(ColumnNames[i], S(ColumnWidths[i]));
                if (i == 5) col.TextAlign = HorizontalAlignment.Right;
            }
            Theme.DarkList(table, Pal.Card);
            table.ColumnClick += (s, e) => SortBy(e.Column);
            table.SelectedIndexChanged += (s, e) => OnSelectionChanged();
            table.KeyDown += OnListKey;
            emptyLink = new LinkLabel { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = Pal.Card, ForeColor = Pal.Sub,
                Font = new Font("Segoe UI", 10f), Visible = false, LinkColor = Pal.Accent, ActiveLinkColor = Pal.Accent };
            emptyLink.LinkClicked += (s, e) => { if (emptyReason == "search") searchBox.Clear(); else { filter = "all"; UpdatePills(); Fill(); SaveSettings(); } };
            listCard.Controls.Add(emptyLink);
            listCard.Controls.Add(nb);
            listCard.Controls.Add(table);
            listCard.Resize += (s, e) => PlaceEmpty();
            split.Panel1.Controls.Add(listCard);

            detailCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            detail = new ScrollPanel { Dock = DockStyle.Fill, BackColor = Pal.Card, ForeColor = Pal.Text, AutoScroll = true };
            detail.Paint += PaintDetail;
            detail.MouseClick += DetailClick;
            detail.Scroll += (s, e) => detail.Invalidate();
            detail.MouseWheel += (s, e) => detail.Invalidate();
            detail.MouseEnter += (s, e) => { if (!(ActiveControl is TextBox) && !(ActiveControl is ComboBox)) detail.Focus(); };
            detail.MouseMove += (s, e) =>
            {
                var p = DetailPoint(e.Location);
                bool over = linkCopyLoc.Contains(p) || linkAuthor.Contains(p) || linkCopyIds.Contains(p);
                detail.Cursor = over ? Cursors.Hand : Cursors.Default;
            };
            footer = new BufferedPanel { Dock = DockStyle.Bottom, Height = S(56), BackColor = Pal.FooterBg };
            footer.Paint += PaintFooter;
            footPrimary = new FlatBtn { Kind = "primary", Height = S(32), Font = fBold, Visible = false };
            footSecondary = new FlatBtn { Kind = "secondary", Height = S(32), Font = fBody, Visible = false };
            footPrimary.Click += (s, e) => Run(footPrimary.Text, () => FooterAction(footPrimary));
            footSecondary.Click += (s, e) => Run(footSecondary.Text, () => FooterAction(footSecondary));
            footer.Controls.Add(footPrimary);
            footer.Controls.Add(footSecondary);
            footer.Resize += (s, e) => LayoutFooter();
            detailCard.Controls.Add(detail);
            detailCard.Controls.Add(footer);
            split.Panel2.Controls.Add(detailCard);
        }

        void BuildFirstRun()
        {
            firstRun = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Pal.Window, Visible = false };
            firstCard = new CardPanel { Size = new Size(S(500), S(300)), Radius = 8f };
            firstCard.Paint += PaintFirstRun;
            firstBtn = new FlatBtn { Kind = "primary", Text = "Choose game folder...", Height = S(34), Font = fBold };
            firstBtn.FitWidth(S(18), S(180));
            firstBtn.Click += (s, e) => Run(firstBtn.Text, () =>
            {
                if (!check.Installed && check.MisplacedToc == null && suggestion != null) UseSuggestion();
                else if (setupStep != 1 && !check.Installed) ShowChecker(check.MisplacedToc == null);
                else BrowseGame();
            });
            firstAlt = new FlatBtn { Kind = "secondary", Height = S(34), Font = fBody };
            firstAlt.Click += (s, e) => Run(firstAlt.Text, () =>
            {
                if (setupStep == 1 && suggestion != null) BrowseGame();
                else ShowChecker(suggestion == null);
            });
            firstCard.Controls.Add(firstBtn);
            firstCard.Controls.Add(firstAlt);
            firstRun.Controls.Add(firstCard);
            firstRun.Resize += (s, e) => PlaceFirstRun();
        }

        void PlaceFirstRun()
        {
            firstCard.Height = setupStep == 1 ? S(240) : S(300);
            firstCard.Location = new Point(Math.Max(S(16), (firstRun.Width - firstCard.Width) / 2), Math.Max(S(16), (firstRun.Height - firstCard.Height) / 2 - S(20)));
            firstBtn.Location = new Point(S(32), firstCard.Height - firstBtn.Height - S(28));
            firstAlt.Location = new Point(firstBtn.Right + S(8), firstBtn.Top);
        }

        void BuildStatus()
        {
            strip = new StatusStrip { SizingGrip = true, BackColor = Pal.FooterBg, ShowItemToolTips = true, Font = fSmall };
            Theme.Strip(strip);
            lblLeft = new ToolStripStatusLabel { ForeColor = Pal.Sub };
            lblLeft.Click += (s, e) => { if (lblLeft.IsLink) Run("Addon check", () => ShowChecker(true)); };
            lblMsg = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Pal.Text, ImageAlign = ContentAlignment.MiddleLeft };
            lblShowing = new ToolStripStatusLabel { ForeColor = Pal.Sub };
            lblLock = new ToolStripStatusLabel { ForeColor = Pal.Sub, Text = "Everything stays on your PC", Image = Draw.IconBitmap("lock", S(14), Pal.Sub),
                ToolTipText = "The app only reads and writes files on this PC. It never touches the running game or the internet." };
            strip.Items.AddRange(new ToolStripItem[] { lblLeft, lblMsg, lblShowing, lblLock });
        }

        void BuildMenus()
        {
            rowMenu = new ContextMenuStrip();
            var mAdd = Item(rowMenu, "Add to addon", "Ctrl+D", AddToAddon);
            var mRemove = Item(rowMenu, "Remove from addon", "Del", RemoveFromDiscoveries);
            var mCopy = Item(rowMenu, "Copy for /qgs import", "Ctrl+C", CopyExport);
            var mLoc = Item(rowMenu, "Copy location", "", CopyGiver);
            var mIds = Item(rowMenu, "Copy quest IDs", "", CopyQuestIds);
            rowMenu.Items.Add(new ToolStripSeparator());
            var mNew = Item(rowMenu, "Select all new", "Ctrl+N", SelectNew);
            var mAll = Item(rowMenu, "Select all", "Ctrl+A", SelectAll);
            rowMenu.Opening += (s, e) =>
            {
                List<ChainRow> adds, ups;
                ChosenForAdd(out adds, out ups);
                var sel = SelectedRows();
                mAdd.Enabled = addon.Found && adds.Count + ups.Count > 0;
                mRemove.Enabled = addon.Found && sel.Any(r => r.Status == ChainRow.InDiscoveries);
                mCopy.Enabled = ShownRows().Count > 0;
                mLoc.Enabled = mIds.Enabled = sel.Count > 0;
                mNew.Enabled = rows.Any(r => r.Status == ChainRow.New);
                mAll.Enabled = ShownRows().Count > 0;
            };
            nb.ContextMenuStrip = rowMenu;
            table.ContextMenuStrip = rowMenu;

            publishMenu = new ContextMenuStrip();
            Item(publishMenu, "Publish a new version...", "", ShowPublish);
            Item(publishMenu, "Check addon...", "", () => ShowChecker(false));
            Item(publishMenu, "Local sharing...", "", ShowSharing);
            Item(publishMenu, "Save share file...", "", SaveExport);
            publishMenu.Items.Add(new ToolStripSeparator());
            var oAddon = Item(publishMenu, "Open addon folder", "", () => OpenFolder(AddonDir));
            var oSaved = Item(publishMenu, "Open saved game data folder", "", () => { string p = SavedVarsPath(); OpenFolder(p == null ? null : Path.GetDirectoryName(p)); });
            var oBack = Item(publishMenu, "Open backups folder", "", () => OpenFolder(AppPaths.Backups));
            publishMenu.Opening += (s, e) =>
            {
                oAddon.Enabled = SafeDirExists(AddonDir);
                string p = SavedVarsPath();
                oSaved.Enabled = p != null && SafeDirExists(Path.GetDirectoryName(p));
                oBack.Enabled = SafeDirExists(AppPaths.Backups);
            };
        }

        ToolStripMenuItem Item(ContextMenuStrip m, string text, string keys, Action a)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            string what = text.TrimEnd('.');
            it.Click += (s, e) => Run(what, a);
            m.Items.Add(it);
            return it;
        }

        static bool SafeDirExists(string d)
        {
            try { return !string.IsNullOrEmpty(d) && Directory.Exists(d); } catch { return false; }
        }

        Icon MakeAppIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                Draw.Card(g, new RectangleF(1, 1, 30, 30), Pal.FooterBg, Pal.Trim, 7f);
                Draw.Icon(g, "compass", new RectangleF(3, 3, 26, 26), Pal.Accent);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ======================================================================
        // Paths, accounts and settings
        // ======================================================================
        string AddonDir { get { return gameDir == "" ? "" : Path.Combine(gameDir, "Interface", "AddOns", "QuestGearScout"); } }

        string SavedVarsPath()
        {
            var a = accountBox.SelectedItem as AccountItem;
            if (a == null || gameDir == "") return null;
            return Path.Combine(gameDir, "WTF", "Account", a.Name, "SavedVariables", "QuestGearScout.lua");
        }

        static string NormalizeGameDir(string dir)
        {
            dir = (dir ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            if (dir == "") return "";
            try
            {
                if (Directory.Exists(Path.Combine(dir, "_forever_"))) return Path.Combine(dir, "_forever_");
                string[] tails = { @"\Interface\AddOns\QuestGearScout", @"\Interface\AddOns", @"\Interface", @"\WTF\Account", @"\WTF" };
                foreach (string t in tails)
                    if (dir.EndsWith(t, StringComparison.OrdinalIgnoreCase)) return dir.Substring(0, dir.Length - t.Length);
            }
            catch { }
            return dir;
        }

        string FindGameFolder()
        {
            var tries = new List<string>();
            if (settings.ContainsKey("gameDir") && settings["gameDir"] != "") tries.Add(settings["gameDir"]);
            foreach (string drive in new[] { "C", "D", "E", "F" })
            {
                tries.Add(drive + @":\Program Files (x86)\World of Warcraft\_forever_");
                tries.Add(drive + @":\Program Files\World of Warcraft\_forever_");
                tries.Add(drive + @":\World of Warcraft\_forever_");
                tries.Add(drive + @":\Games\World of Warcraft\_forever_");
            }
            foreach (string t in tries)
            {
                try { if (Directory.Exists(Path.Combine(t, "WTF")) || Directory.Exists(Path.Combine(t, "Interface"))) return t; }
                catch { }
            }
            return "";
        }

        void ApplyGameDir(string text)
        {
            string dir = NormalizeGameDir(text);
            savedGameDir = dir;
            if (string.Equals(dir, loadedGameDir, StringComparison.OrdinalIgnoreCase)) { gameDir = dir; RefreshAll(); SaveSettings(); return; }
            gameDir = dir;
            loadedGameDir = dir;
            lastBackup = null; lastBackupDir = null;
            appeared = new HashSet<string>();
            prevKeys = null; prevPath = null;
            autoSelect = true;
            FillAccounts();
            Reload();
            SaveSettings();
        }

        void BrowseGame()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick your World of Warcraft: Forever folder (the one named _forever_). Interface, AddOns or WTF inside it work too." })
            {
                if (SafeDirExists(gameDir)) dlg.SelectedPath = gameDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ApplyGameDir(dlg.SelectedPath);
            }
        }

        void ShowFolderDialog()
        {
            using (var f = new Form { Text = "Game folder", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(520), S(190)), BackColor = Pal.Card, Font = fBody, ShowInTaskbar = false })
            {
                var title = new Label { Text = "World of Warcraft: Forever folder", Font = fHead, AutoSize = true, Location = new Point(S(20), S(16)) };
                var help = new Label { Text = "The folder named _forever_. Interface, AddOns or WTF inside it work too.", ForeColor = Pal.Sub, AutoSize = true, Location = new Point(S(20), S(42)) };
                var box = new TextBox { Text = gameDir, Location = new Point(S(20), S(72)), Width = S(380) };
                box.HandleCreated += (s, e) => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, @"C:\Program Files (x86)\World of Warcraft\_forever_");
                var browse = new FlatBtn { Text = "Browse...", Location = new Point(S(410), S(68)), Size = new Size(S(90), S(28)) };
                browse.Click += (s, e) =>
                {
                    using (var dlg = new FolderBrowserDialog { Description = "Pick your World of Warcraft: Forever folder (the one named _forever_)." })
                    {
                        if (SafeDirExists(box.Text)) dlg.SelectedPath = box.Text;
                        if (dlg.ShowDialog(f) == DialogResult.OK) box.Text = NormalizeGameDir(dlg.SelectedPath);
                    }
                };
                var refresh = new FlatBtn { Kind = "ghost", Text = "Refresh now (F5)", Location = new Point(S(14), S(140)), Size = new Size(S(140), S(30)) };
                refresh.Click += (s, e) => { f.DialogResult = DialogResult.Retry; f.Close(); };
                var ok = new FlatBtn { Kind = "primary", Text = "Use this folder", Location = new Point(S(290), S(140)), Size = new Size(S(130), S(30)), Font = fBold };
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                var cancel = new FlatBtn { Text = "Cancel", Location = new Point(S(428), S(140)), Size = new Size(S(72), S(30)) };
                cancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
                f.Controls.AddRange(new Control[] { title, help, box, browse, refresh, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.Form(f);
                var res = f.ShowDialog(this);
                if (res == DialogResult.OK) ApplyGameDir(box.Text);
                else if (res == DialogResult.Retry) RefreshAll();
            }
        }

        List<AccountItem> ScanAccounts()
        {
            var outp = new List<AccountItem>();
            try
            {
                string acc = Path.Combine(gameDir, "WTF", "Account");
                if (gameDir == "" || !Directory.Exists(acc)) return outp;
                foreach (string d in Directory.GetDirectories(acc))
                {
                    string f = Path.Combine(d, "SavedVariables", "QuestGearScout.lua");
                    if (File.Exists(f)) outp.Add(new AccountItem { Name = Path.GetFileName(d), Saved = File.GetLastWriteTime(f) });
                }
            }
            catch { }
            return outp;
        }

        void FillAccounts()
        {
            var found = ScanAccounts();
            var cur = accountBox.SelectedItem as AccountItem;
            string keep = cur != null ? cur.Name : (settings.ContainsKey("account") ? settings["account"] : null);
            bool was = suppress;
            suppress = true;
            accountBox.Items.Clear();
            foreach (var a in found) { a.ShowTime = found.Count > 1; accountBox.Items.Add(a); }
            if (found.Count > 0)
            {
                int idx = keep == null ? -1 : found.FindIndex(a => string.Equals(a.Name, keep, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = found.IndexOf(found.OrderByDescending(a => a.Saved).First());
                accountBox.SelectedIndex = idx;
            }
            accountBox.Visible = found.Count > 0;
            int widest = S(160);
            foreach (var a in found) widest = Math.Max(widest, TextRenderer.MeasureText(a.ToString(), accountBox.Font).Width + S(34));
            accountBox.Width = Math.Min(S(360), widest);
            if (header.Width > 0) { folderBtn.Left = header.Width - S(16) - folderBtn.Width; accountBox.Left = folderBtn.Left - S(8) - accountBox.Width; header.Invalidate(); }
            suppress = was;
            Watch();
        }

        void RefreshAll()
        {
            FillAccounts();
            Reload();
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(AppPaths.Settings)) return;
                foreach (string line in File.ReadAllLines(AppPaths.Settings))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) settings[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (suppress) return;
            // Only a folder the user chose replaces the saved one (an unplugged drive mustn't erase it).
            settings["gameDir"] = savedGameDir;
            var a = accountBox.SelectedItem as AccountItem;
            if (a != null) settings["account"] = a.Name;
            settings["minQuality"] = (qualityBox.SelectedIndex + 2).ToString(CultureInfo.InvariantCulture);
            settings["filter"] = filter;
            settings["view"] = view;
            settings["sort"] = sortCol + "," + (sortDesc ? 1 : 0);
            settings["factionView"] = factionView;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllLines(AppPaths.Settings, settings.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value).ToArray());
            }
            catch { }
        }

        void RestoreFilters()
        {
            string v;
            if (settings.TryGetValue("filter", out v) && (v == "all" || v == "new" || v == "gear" || v == "dun")) filter = v;
            int q;
            if (settings.TryGetValue("minQuality", out v) && int.TryParse(v, out q) && q >= 2 && q <= 4) qualityBox.SelectedIndex = q - 2;
            if (settings.TryGetValue("sort", out v))
            {
                string[] p = v.Split(',');
                int c;
                if (p.Length == 2 && int.TryParse(p[0], out c) && c >= -1 && c < ColumnNames.Length) { sortCol = c; sortDesc = p[1] == "1"; }
            }
            if (settings.TryGetValue("view", out v) && (v == "table" || v == "players")) SetView(v, false);
            if (settings.TryGetValue("factionView", out v) && (v == "A" || v == "H" || v == "all")) factionView = v;
            UpdatePills();
            UpdateHeaders();
        }

        void RestoreWindow()
        {
            string v;
            if (settings.TryGetValue("bounds", out v))
            {
                string[] p = v.Split(',');
                int x, y, w, h;
                if (p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h)
                    && w >= MinimumSize.Width && h >= MinimumSize.Height)
                {
                    var r = new Rectangle(x, y, w, h);
                    if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) { StartPosition = FormStartPosition.Manual; Bounds = r; }
                }
            }
            if (settings.TryGetValue("max", out v) && v == "1") WindowState = FormWindowState.Maximized;
            if (settings.TryGetValue("cols", out v))
            {
                string[] p = v.Split(',');
                for (int i = 0; i < p.Length && i < table.Columns.Count; i++)
                {
                    int w;
                    if (int.TryParse(p[i], out w) && w >= 20 && w <= 1000) Theme.SetDesignedWidth(table.Columns[i], S(w));
                }
            }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings["bounds"] = b.X + "," + b.Y + "," + b.Width + "," + b.Height;
            settings["max"] = WindowState == FormWindowState.Maximized ? "1" : "0";
            settings["cols"] = string.Join(",", table.Columns.Cast<ColumnHeader>()
                .Select(c => ((int)Math.Round(Theme.DesignedWidth(c) / scale)).ToString(CultureInfo.InvariantCulture)).ToArray());
            if (!uiTest) SaveSettings();
            poll.Stop(); debounce.Stop(); searchTimer.Stop(); freshTimer.Stop();
            StopSharing();
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
        }

        // ======================================================================
        // Watching and loading
        // ======================================================================
        void Watch()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            string p = SavedVarsPath();
            try
            {
                if (p == null || !Directory.Exists(Path.GetDirectoryName(p))) return;
                watcher = new FileSystemWatcher(Path.GetDirectoryName(p), "QuestGearScout.lua*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                watcher.Changed += (s, e) => Kick();
                watcher.Created += (s, e) => Kick();
                watcher.Renamed += (s, e) => Kick();
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }
        }

        void Kick()
        {
            if (!IsHandleCreated || IsDisposed || Disposing) return;
            try { BeginInvoke((Action)(() => { debounce.Stop(); debounce.Start(); })); }
            catch (InvalidOperationException) { }
        }

        void PollTick()
        {
            try
            {
                // The saved folder came back (e.g. an external drive was plugged in): switch to it.
                if (savedGameDir != "" && !string.Equals(savedGameDir, gameDir, StringComparison.OrdinalIgnoreCase) && SafeDirExists(savedGameDir))
                {
                    ApplyGameDir(savedGameDir);
                    Msg("Found your game folder again.", false);
                    return;
                }
                if (accountBox.Items.Count == 0)
                {
                    var found = ScanAccounts();
                    if (found.Count > 0)
                    {
                        FillAccounts();
                        Reload();
                        SaveSettings();
                        Msg("Found saved data for account " + found[0].Name + ".", false);
                        return;
                    }
                }
                // The addon was just installed (or fixed) while the app is open: pick it up.
                if (!addon.Found && AddonDir != "" && File.Exists(Path.Combine(AddonDir, AddonCheck.Name + ".toc"))) { Reload(); Msg("QuestGearScout is installed now.", false); return; }
                string p = SavedVarsPath();
                if (p != null && File.Exists(p) && File.GetLastWriteTime(p) != lastWrite) { debounce.Stop(); Reload(); }
                else
                {
                    header.Invalidate();
                    // Online dots and "seen 3 min ago" change over time.
                    if (view == "players") { playersList.Invalidate(); detail.Invalidate(); footer.Invalidate(); lblShowing.Text = PlayersCountText(); }
                }
            }
            catch { }
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
        }

        void Reload()
        {
            try { addon = AddonData.Load(AddonDir); }
            catch (Exception ex) { Msg("Couldn't read the addon files: " + ex.Message, true); }

            string p = SavedVarsPath();
            if (p == null || !File.Exists(p))
            {
                learned = new Learned(); rows = new List<ChainRow>(); hasData = false;
                lastWrite = DateTime.MinValue;
                appeared = new HashSet<string>();
            }
            else
            {
                try
                {
                    var stamp = File.GetLastWriteTime(p);
                    var l = Learned.FromSavedVariables(ReadShared(p), addon, qualityBox.SelectedIndex + 2);
                    var r = l.Rows(addon);
                    var keys = new HashSet<string>(r.Select(x => x.Key));
                    bool changed = prevPath != p || stamp != lastWrite;
                    if (changed)
                    {
                        appeared = new HashSet<string>();
                        if (prevKeys != null && prevPath == p)
                            foreach (string k in keys) if (!prevKeys.Contains(k)) appeared.Add(k);
                    }
                    else appeared.IntersectWith(keys);
                    prevKeys = keys; prevPath = p;
                    learned = l; rows = WithShared(r); hasData = true; lastWrite = stamp;
                    if (changed && appeared.Count > 0)
                    {
                        Msg(Plural(appeared.Count, "chain") + " appeared after your last /reload.", false);
                        freshTimer.Stop(); freshTimer.Start();
                    }
                }
                catch (Exception ex)
                {
                    Msg("Couldn't read the saved data yet (the game may still be writing it): " + ex.Message, true);
                }
            }
            UpdateSetup();
            UpdateFactionPill();
            Fill();
            AfterReloadSharing();
            if (view == "players") { FillPlayers(); detail.Invalidate(); }
        }

        void UpdateSetup()
        {
            RunCheck();
            string sv = SavedVarsPath();
            if (!SafeDirExists(gameDir)) setupStep = 1;
            else if (!addon.Found) setupStep = 2;
            else if (sv == null || !File.Exists(sv)) setupStep = 3;
            else setupStep = 4;

            bool first = !hasData;
            firstRun.Visible = first;
            mainArea.Visible = !first;
            if (first) { UpdateFirstButton(); PlaceFirstRun(); firstCard.Invalidate(); }

            if (setupStep == 1 || setupStep == 2)
            {
                lblLeft.Text = setupStep == 1 ? "Game folder not found. Check..." : "Addon not installed in this folder. Check...";
                lblLeft.IsLink = true; lblLeft.LinkColor = lblLeft.ActiveLinkColor = lblLeft.VisitedLinkColor = Pal.Warn; lblLeft.Image = warnIcon;
            }
            else
            {
                lblLeft.IsLink = false; lblLeft.Image = null; lblLeft.ForeColor = Pal.Sub;
                lblLeft.Text = "QuestGearScout v" + addon.Version + (check.GameVersion != "" ? " for game " + string.Join(".", check.GameVersion.Split('.').Take(3).ToArray()) : "") +
                    " - " + learned.QuestsSeen + " quests seen - " + addon.BuiltIn.Count + " chains built in";
            }
            header.Invalidate();
            UpdateUndo();
        }

        public static string Ago(DateTime when)
        {
            var d = DateTime.Now - when;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            return when.ToString("d MMM", CultureInfo.CurrentCulture);
        }

        void Msg(string text, bool warn)
        {
            lblMsg.Text = text.Length > 120 ? text.Substring(0, 117) + "..." : text;
            lblMsg.ToolTipText = text;
            lblMsg.ForeColor = warn ? Pal.Warn : Pal.Text;
            lblMsg.Image = warn ? warnIcon : null;
        }

        static string Plural(int n, string word) { return n + " " + word + (n == 1 ? "" : "s"); }

        // ======================================================================
        // Words and values for a chain
        // ======================================================================
        static string StatusWord(ChainRow r)
        {
            if (r.Status == ChainRow.New) return "New";
            if (r.Status == ChainRow.InDiscoveries) return "Added by you";
            if (r.Status == ChainRow.BuiltIn) return "In the addon";
            if (r.Status == ChainRow.Shared) return "From " + r.From;
            return "Can't check";
        }

        static string StatusHelp(ChainRow r)
        {
            if (r.Status == ChainRow.Shared) return "Shared by " + r.From + " on your network - it's in your game too (after /reload).";
            if (r.Status == ChainRow.New) return "New - not in the addon yet.";
            if (r.Status == ChainRow.InDiscoveries) return "Added by you - in this addon's Discoveries.lua.";
            if (r.Status == ChainRow.BuiltIn) return "In the addon - already part of its built-in data.";
            return "Can't check - the addon wasn't found, so this can't be compared.";
        }

        static string Giver(Starter s) { return s.Get("name") ?? "?"; }
        static string Zone(Starter s) { return Zones.Name(s.Num("map")); }
        static int Steps(ChainRow r) { return (int)(r.Starter.Num(r.IsDungeon ? "dungeonSteps" : "steps") ?? 1); }
        static int Quality(ChainRow r) { return (int)(r.Starter.Num("quality") ?? 2); }
        static string FirstTitle(ChainRow r) { return r.Starter.Get(r.IsDungeon ? "dRootTitle" : "rootTitle") ?? "?"; }
        static string RootId(ChainRow r) { return r.Starter.Get(r.IsDungeon ? "dRoot" : "rootQuest"); }

        static string LocText(Starter s)
        {
            double? x = s.Num("x"), y = s.Num("y");
            if (!x.HasValue || !y.HasValue) return "";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0}", x.Value * 100, y.Value * 100);
        }

        static string Reward(ChainRow r)
        {
            var s = r.Starter;
            if (r.IsDungeon) return string.IsNullOrEmpty(s.Get("dungeonName")) ? "Dungeon quest" : s.Get("dungeonName");
            return TextUtil.Plain(s.Get("item") ?? s.Get("finalTitle") ?? "Gear reward");
        }

        static Color RewardColor(ChainRow r) { return r.IsDungeon ? Pal.Dungeon : Pal.Quality(Quality(r)); }

        static string QualityWord(int q) { return q >= 0 && q < TextUtil.Quality.Length ? TextUtil.Quality[q] : "Gear"; }

        static string Line2(ChainRow r) { return (r.IsDungeon ? "Dungeon" : "Gear") + " - " + Plural(Steps(r), "step") + " - "; }

        static string IconFor(ChainRow r) { return r.IsDungeon ? "castle" : r.Starter.Get("kind") == "O" ? "scroll" : "sword"; }

        static string Haystack(ChainRow r)
        {
            var s = r.Starter;
            return string.Join("\n", new[] { s.Get("name"), Zone(s), Reward(r), FirstTitle(r), s.Get("finalTitle"), s.Get("dungeonTitle"),
                RootId(r), s.Get("id"), StatusWord(r), r.IsDungeon ? "dungeon" : "gear", FactionWord(r.Faction), r.From }.Where(x => x != null).ToArray());
        }

        // ======================================================================
        // Filter, sort and fill
        // ======================================================================
        string[] Terms() { return searchBox.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); }

        bool Passes(ChainRow r, string[] terms)
        {
            if (!FactionOk(r)) return false;
            if (filter == "new" && r.Status != ChainRow.New) return false;
            if (filter == "gear" && r.IsDungeon) return false;
            if (filter == "dun" && !r.IsDungeon) return false;
            if (terms.Length > 0)
            {
                string hay = Haystack(r);
                foreach (string t in terms) if (hay.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        IEnumerable<ChainRow> TableOrder(IEnumerable<ChainRow> src)
        {
            if (sortCol < 0) return src.OrderBy(r => r.StatusRank).ThenBy(r => Zone(r.Starter)).ThenBy(r => Giver(r.Starter)).ThenBy(r => r.IsDungeon);
            Func<ChainRow, IComparable> key;
            switch (sortCol)
            {
                case 0: key = r => r.StatusRank; break;
                case 1: key = r => r.IsDungeon ? 1 : 0; break;
                case 2: key = r => Giver(r.Starter); break;
                case 3: key = r => Zone(r.Starter); break;
                case 4: key = r => (r.Starter.Num("x") ?? 0) * 1000 + (r.Starter.Num("y") ?? 0); break;
                case 5: key = r => Steps(r); break;
                case 6: key = r => Reward(r); break;
                case 8: key = r => r.Faction ?? "B"; break;
                default: key = r => FirstTitle(r); break;
            }
            var ordered = sortDesc ? src.OrderByDescending(key) : src.OrderBy(key);
            return ordered.ThenBy(r => r.StatusRank).ThenBy(r => Giver(r.Starter));
        }

        void SortBy(int col)
        {
            if (col == sortCol) { if (!sortDesc) sortDesc = true; else { sortCol = -1; sortDesc = false; } }
            else { sortCol = col; sortDesc = false; }
            UpdateHeaders();
            Fill();
            SaveSettings();
        }

        void UpdateHeaders()
        {
            for (int i = 0; i < table.Columns.Count; i++)
                table.Columns[i].Text = ColumnNames[i] + (i == sortCol ? (sortDesc ? " \u25BC" : " \u25B2") : "");
        }

        void UpdatePills()
        {
            foreach (var p in new[] { pAll, pNew, pGear, pDun }) { p.Checked = (string)p.Tag == filter; p.Invalidate(); }
        }

        void SetView(string v) { SetView(v, true); }

        void SetView(string v, bool save)
        {
            var keep = SelectedKeys();
            view = v;
            vNotebook.Checked = v == "notebook"; vTable.Checked = v == "table"; vPlayers.Checked = v == "players";
            vNotebook.Invalidate(); vTable.Invalidate(); vPlayers.Invalidate();
            nb.Visible = v == "notebook";
            table.Visible = v == "table";
            playersList.Visible = v == "players";
            split.Panel2Collapsed = v == "table";
            if (v == "players") { emptyLink.Visible = false; FillPlayers(); }
            if (save) { pendingKeys = keep; Fill(); SaveSettings(); }
            UpdateDetail();
        }

        HashSet<string> SelectedKeys() { return new HashSet<string>(SelectedRows().Select(r => r.Key)); }

        void Fill()
        {
            filling = true;
            var selKeys = pendingKeys ?? SelectedKeys();
            pendingKeys = null;
            var terms = Terms();
            var shown = rows.Where(r => Passes(r, terms)).ToList();

            if (view == "notebook")
            {
                int top = nb.TopIndex;
                nb.BeginUpdate();
                nb.Items.Clear();
                var fresh = shown.Where(r => r.Status == ChainRow.New).OrderBy(r => Zone(r.Starter)).ThenBy(r => Giver(r.Starter)).ToList();
                if (fresh.Count > 0)
                {
                    nb.Items.Add(new GroupHeader { Text = "New finds (" + fresh.Count + ")", IsNew = true });
                    foreach (var r in fresh) nb.Items.Add(r);
                }
                foreach (var grp in shown.Where(r => r.Status != ChainRow.New).GroupBy(r => Zone(r.Starter)).OrderBy(gp => gp.Key))
                {
                    nb.Items.Add(new GroupHeader { Text = grp.Key + " (" + grp.Count() + ")" });
                    foreach (var r in grp.OrderBy(r => Giver(r.Starter)).ThenBy(r => r.IsDungeon)) nb.Items.Add(r);
                }
                int firstSel = -1;
                for (int i = 0; i < nb.Items.Count; i++)
                {
                    var r = nb.Items[i] as ChainRow;
                    if (r != null && selKeys.Contains(r.Key)) { nb.SetSelected(i, true); if (firstSel < 0) firstSel = i; }
                }
                if (firstSel < 0 && autoSelect && shown.Count > 0)
                {
                    for (int i = 0; i < nb.Items.Count; i++) if (nb.Items[i] is ChainRow) { nb.SetSelected(i, true); break; }
                    autoSelect = false;
                }
                nb.EndUpdate();
                try { if (top < nb.Items.Count) nb.TopIndex = top; } catch { }
                if (shown.Count > 0) autoSelect = false;   // only ever auto-pick once per folder
            }
            else if (view == "table")
            {
                string topKey = table.TopItem != null ? ((ChainRow)table.TopItem.Tag).Key : null;
                table.BeginUpdate();
                table.Items.Clear();
                ListViewItem topItem = null;
                foreach (var r in TableOrder(shown))
                {
                    var s = r.Starter;
                    var item = new ListViewItem(new[] { StatusWord(r), r.IsDungeon ? "Dungeon" : "Gear", Giver(s) + (s.Get("kind") == "O" ? " (object)" : ""),
                        Zone(s), LocText(s), Steps(r).ToString(CultureInfo.InvariantCulture),
                        r.IsDungeon ? Reward(r) + ": " + (s.Get("dungeonTitle") ?? "?") : QualityWord(Quality(r)) + ": " + Reward(r), FirstTitle(r),
                        r.Faction == null ? "Both" : FactionWord(r.Faction) });
                    item.UseItemStyleForSubItems = false;
                    item.SubItems[0].ForeColor = r.Status == ChainRow.New ? Pal.Accent : r.Status == ChainRow.InDiscoveries ? Pal.Added
                        : r.Status == ChainRow.Shared ? SharedC : Pal.Sub;
                    item.SubItems[8].ForeColor = r.Faction == "A" ? Alliance : r.Faction == "H" ? Horde : Pal.Sub;
                    if (r.Status == ChainRow.New) item.SubItems[0].Font = fBold;
                    item.SubItems[6].ForeColor = RewardColor(r);
                    if (appeared.Contains(r.Key)) foreach (ListViewItem.ListViewSubItem si in item.SubItems) si.BackColor = Pal.Fresh;
                    item.ToolTipText = StatusHelp(r) + "\nFirst quest: " + FirstTitle(r) + (RootId(r) != null ? " (" + RootId(r) + ")" : "");
                    item.Tag = r;
                    table.Items.Add(item);
                    if (selKeys.Contains(r.Key)) item.Selected = true;
                    if (r.Key == topKey) topItem = item;
                }
                table.EndUpdate();
                try { if (topItem != null) table.TopItem = topItem; } catch { }
                autoSelect = false;
            }
            filling = false;

            // Counts follow the faction filter (chains for factions you don't play aren't counted).
            var fr = rows.Where(FactionOk).ToList();
            int nNew = fr.Count(r => r.Status == ChainRow.New);
            pillCounts = new[] { fr.Count.ToString(CultureInfo.InvariantCulture), nNew.ToString(CultureInfo.InvariantCulture),
                fr.Count(r => !r.IsDungeon).ToString(CultureInfo.InvariantCulture), fr.Count(r => r.IsDungeon).ToString(CultureInfo.InvariantCulture) };
            LayoutToolbar();
            int nShared = fr.Count(r => r.Status == ChainRow.Shared);
            lblShowing.Text = "Showing " + shown.Count + " of " + fr.Count + (nShared > 0 ? " (" + nShared + " shared)" : "") +
                (filtersBtn != null && !filtersBtn.Visible && (qualityBox.SelectedIndex > 0 || factionView != "all") ? " - filters on" : "");
            if (view == "players") FillPlayers();
            UpdateBanner();
            UpdateEmpty(shown.Count);
            UpdateDetail();
            if (publishRefresh != null) publishRefresh();
        }

        void UpdateEmpty(int shown)
        {
            emptyReason = "";
            string text = null;
            if (shown == 0 && hasData)
            {
                if (rows.Count == 0) text = "No chains learned yet. Do quests with the addon on, then type /reload.";
                else if (Terms().Length > 0) { emptyReason = "search"; text = "No chains match \"" + searchBox.Text + "\"\nClear search"; }
                else if (filter == "new") { emptyReason = "filter"; text = "Nothing new - everything you've found is already in the addon.\nShow all chains"; }
                else { emptyReason = "filter"; text = "No chains of this kind yet.\nShow all chains"; }
            }
            emptyLink.Text = text ?? "";
            emptyLink.Links.Clear();
            if (emptyReason != "" && text != null)
            {
                int nl = text.LastIndexOf('\n');
                emptyLink.Links.Add(nl + 1, text.Length - nl - 1);
            }
            emptyLink.Visible = text != null;
            if (text != null) emptyLink.BringToFront();
            PlaceEmpty();
        }

        void PlaceEmpty()
        {
            if (emptyLink == null) return;
            int head = view == "table" ? S(26) : 0;
            emptyLink.Bounds = new Rectangle(1, 1 + head, Math.Max(0, listCard.Width - 2), Math.Max(0, listCard.Height - 2 - head));
        }

        // ======================================================================
        // Notebook list drawing
        // ======================================================================
        void DrawNotebookItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= nb.Items.Count) return;
            var g = e.Graphics;
            var b = e.Bounds;
            object it = nb.Items[e.Index];
            var head = it as GroupHeader;
            if (head != null)
            {
                using (var hb = new SolidBrush(Pal.Card)) g.FillRectangle(hb, b);
                Draw.Text(g, head.Text, fSmallBold, new Rectangle(b.X + S(12), b.Y + S(10), b.Width - S(24), S(16)), head.IsNew ? Pal.Accent : Pal.Trim, TextFormatFlags.Top);
                return;
            }
            var r = (ChainRow)it;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Pal.AccentBg : appeared.Contains(r.Key) ? Pal.Fresh : Pal.Card)) g.FillRectangle(bg, b);
            if (sel) using (var bar = new SolidBrush(Pal.Accent)) g.FillRectangle(bar, b.X, b.Y, S(3), b.Height);
            using (var line = new Pen(Pal.CardBorder)) g.DrawLine(line, b.X + S(12), b.Bottom - 1, b.Right - S(8), b.Bottom - 1);

            int x = b.X + S(14), right = b.Right - S(10);
            int tagW;
            var tagAt = new Point(right, b.Y + S(6));
            if (r.Status == ChainRow.New) tagW = Draw.Pill(g, "New", fSmallBold, tagAt, Pal.Accent, Pal.Accent, Pal.OnAccent, S(8), S(18), true);
            else if (r.Status == ChainRow.InDiscoveries) tagW = Draw.Pill(g, "Added by you", fSmall, tagAt, Pal.AddedBg, Pal.AddedBorder, Pal.Added, S(8), S(18), true);
            else if (r.Status == ChainRow.Unknown) tagW = Draw.Pill(g, "Can't check", fSmall, tagAt, Pal.Card, Pal.CardBorder, Pal.Sub, S(8), S(18), true);
            else if (r.Status == ChainRow.Shared) tagW = Draw.Pill(g, "From " + r.From, fSmall, tagAt, SharedBg, SharedBorder, SharedC, S(8), S(18), true);
            else { Draw.Icon(g, "check", new RectangleF(right - S(14), b.Y + S(8), S(14), S(14)), Pal.Muted); tagW = S(14); }

            var nameFont = r.Status == ChainRow.New ? fBold : fBody;
            Draw.Text(g, Giver(r.Starter), nameFont, new Rectangle(x, b.Y + S(6), Math.Max(0, right - tagW - S(8) - x), S(18)), Pal.Text, TextFormatFlags.Top);
            Draw.Icon(g, IconFor(r), new RectangleF(x, b.Y + S(27), S(12), S(12)), Pal.Sub);
            string l2 = Line2(r);
            int lx = x + S(16);
            Draw.Text(g, l2, fSmall, new Rectangle(lx, b.Y + S(25), Math.Max(0, right - lx), S(16)), Pal.Sub, TextFormatFlags.Top);
            lx += Draw.Width(g, l2, fSmall);
            // Faction label at the right of line 2 (neutral chains have none).
            int facW = 0;
            if (r.Faction != null)
            {
                var ff = fSmall;
                facW = Draw.Width(g, FactionWord(r.Faction), ff) + S(12);
                DrawFaction(g, r.Faction, right - facW, b.Y + S(25), S(16), ff);
            }
            Draw.Text(g, Reward(r), fSmall, new Rectangle(lx, b.Y + S(25), Math.Max(0, right - lx - facW - S(4)), S(16)), RewardColor(r), TextFormatFlags.Top);
        }

        // ======================================================================
        // Selection
        // ======================================================================
        List<ChainRow> SelectedRows()
        {
            if (view == "players") return new List<ChainRow>();
            if (view == "table") return table.SelectedItems.Cast<ListViewItem>().Select(i => (ChainRow)i.Tag).ToList();
            var outp = new List<ChainRow>();
            foreach (int i in nb.SelectedIndices) { var r = nb.Items[i] as ChainRow; if (r != null) outp.Add(r); }
            return outp;
        }

        List<ChainRow> ShownRows()
        {
            if (view == "players") return new List<ChainRow>();
            if (view == "table") return table.Items.Cast<ListViewItem>().Select(i => (ChainRow)i.Tag).ToList();
            return nb.Items.OfType<ChainRow>().ToList();
        }

        List<ChainRow> Chosen() { var s = SelectedRows(); return s.Count > 0 ? s : ShownRows(); }

        void ChosenForAdd(out List<ChainRow> adds, out List<ChainRow> updates)
        {
            var sel = SelectedRows();
            if (sel.Count > 0)
            {
                // Selected shared chains can be added too (to publish a friend's find in your release).
                adds = sel.Where(r => r.Status == ChainRow.New || r.Status == ChainRow.Shared).ToList();
                updates = sel.Where(r => r.Status == ChainRow.InDiscoveries).ToList();
            }
            else
            {
                adds = ShownRows().Where(r => r.Status == ChainRow.New).ToList();
                updates = new List<ChainRow>();
            }
        }

        void OnSelectionChanged()
        {
            if (filling) return;
            if (view == "notebook")
            {
                // Group headers can't be selected. Landing on one alone (arrow keys, Home, a click)
                // moves on to the nearest chain in the direction of travel.
                if (nb.SelectedIndices.Count == 1 && nb.Items[nb.SelectedIndices[0]] is GroupHeader)
                {
                    int idx = nb.SelectedIndices[0];
                    int dir = idx >= lastNbIndex ? 1 : -1;
                    int target = -1;
                    for (int i = idx + dir; i >= 0 && i < nb.Items.Count; i += dir) if (nb.Items[i] is ChainRow) { target = i; break; }
                    if (target < 0) for (int i = idx - dir; i >= 0 && i < nb.Items.Count; i -= dir) if (nb.Items[i] is ChainRow) { target = i; break; }
                    filling = true;
                    nb.ClearSelected();
                    if (target >= 0) nb.SetSelected(target, true);
                    filling = false;
                }
                for (int i = nb.SelectedIndices.Count - 1; i >= 0; i--)
                {
                    int idx = nb.SelectedIndices[i];
                    if (nb.Items[idx] is GroupHeader) { filling = true; nb.SetSelected(idx, false); filling = false; }
                }
                if (nb.SelectedIndices.Count == 1) lastNbIndex = nb.SelectedIndices[0];
            }
            if (!IsHandleCreated) { UpdateDetail(); return; }
            if (detailQueued) return;
            detailQueued = true;
            BeginInvoke((Action)(() => { detailQueued = false; authorOpen = false; UpdateDetail(); }));
        }

        void SelectAll()
        {
            filling = true;
            if (view == "table") foreach (ListViewItem it in table.Items) it.Selected = true;
            else for (int i = 0; i < nb.Items.Count; i++) nb.SetSelected(i, nb.Items[i] is ChainRow);
            filling = false;
            UpdateDetail();
        }

        void SelectNew()
        {
            filling = true;
            if (view == "table") foreach (ListViewItem it in table.Items) it.Selected = ((ChainRow)it.Tag).Status == ChainRow.New;
            else for (int i = 0; i < nb.Items.Count; i++) { var r = nb.Items[i] as ChainRow; nb.SetSelected(i, r != null && r.Status == ChainRow.New); }
            filling = false;
            UpdateDetail();
        }

        // ======================================================================
        // Banner
        // ======================================================================
        bool Unpublished()
        {
            if (!addon.Found || !rows.Any(r => r.Status == ChainRow.InDiscoveries)) return false;
            string disc = Path.Combine(AddonDir, "Discoveries.lua");
            long ticks;
            string t;
            if (!settings.TryGetValue(ZipKey("lastZipTime"), out t) || !long.TryParse(t, out ticks)) return true;
            try { return File.Exists(disc) && File.GetLastWriteTime(disc).Ticks > ticks; } catch { return false; }
        }

        bool CanUndoHere() { return lastBackup != null && string.Equals(lastBackupDir, AddonDir, StringComparison.OrdinalIgnoreCase); }

        void UpdateBanner()
        {
            var fresh = rows.Where(r => r.Status == ChainRow.New && FactionOk(r)).ToList();
            bannerAlt.Tint = Pal.Accent;
            if (!addon.Found)
            {
                bState = "problem"; bIcon = "warn"; bFill = Pal.WarnBg; bBorder = Pal.WarnBorder; bTint = Pal.Warn;
                if (check.MisplacedToc != null)
                {
                    bHead = "QuestGearScout is in the wrong folder";
                    bBody = check.MisplacedProblem;
                    bMainAct = "check"; SetBannerButtons("Fix it...", null);
                }
                else if (suggestion != null)
                {
                    bHead = "Found QuestGearScout in another game folder";
                    bBody = suggestion.Path + " has " + suggestion.Status.Summary + ".";
                    bMainAct = "use"; SetBannerButtons("Use that folder", "Check addon...");
                }
                else
                {
                    bHead = SafeDirExists(gameDir) ? "QuestGearScout isn't installed in this folder" : "Your game folder wasn't found";
                    bBody = quickScanning ? "Looking for it on this PC..." :
                        "Install it from CurseForge, or pick the World of Warcraft: Forever folder where it's installed. Until then, chains show \"Can't check\".";
                    bMainAct = "check"; SetBannerButtons("Check addon...", "Choose folder...");
                }
            }
            else if (fresh.Count == 0 && check.Items.Any(i => i.State == "bad"))
            {
                // Serious (e.g. files missing): this comes before the Publish step.
                SetAttention(check.Items.First(i => i.State == "bad"), false);
            }
            else if (fresh.Count > 0)
            {
                bState = "new"; bIcon = "sparkle"; bFill = Pal.AccentBg; bBorder = Pal.AccentBorder; bTint = Pal.Accent; bStep = 1;
                bHead = "You found " + Plural(fresh.Count, "new chain");
                var f = fresh.OrderBy(r => Zone(r.Starter)).ThenBy(r => Giver(r.Starter)).First();
                bBody = fresh.Count == 1 ? Giver(f.Starter) + " in " + Zone(f.Starter) + " isn't in the addon yet."
                                         : Giver(f.Starter) + " and " + (fresh.Count - 1) + " more aren't in the addon yet.";
                SetBannerButtons("Add " + fresh.Count + " to addon", "Share with friends");
            }
            else if (Unpublished())
            {
                int n = rows.Count(r => r.Status == ChainRow.InDiscoveries);
                bState = "added"; bIcon = "check"; bFill = Pal.SuccessBg; bBorder = Pal.SuccessBorder; bTint = Pal.Success; bStep = 2;
                bHead = "Added " + Plural(n, "chain") + " to the addon";
                bBody = "Type /reload in game to check " + (n == 1 ? "it" : "them") + ". " + (n == 1 ? "It goes" : "They go") + " out with your next release.";
                SetBannerButtons("Publish...", CanUndoHere() ? "Undo" : null);
                bannerAlt.Tint = Pal.Success;
            }
            else if (check.Items.Any(i => i.State == "warn"))
            {
                // Minor (an alt with the addon off, an older interface number): only when there's nothing else to do.
                SetAttention(check.Items.First(i => i.State == "warn"), true);
            }
            else
            {
                bState = "nothing"; bIcon = "map"; bFill = Pal.Card; bBorder = Pal.CardBorder; bTint = Pal.Sub;
                bHead = "Nothing new yet";
                bBody = "Play as usual. When you log out or type /reload, new finds show up here by themselves.";
                SetBannerButtons(null, rows.Count > 0 ? "Share all " + rows.Count + " chains" : null);
            }
            banner.Fill = bFill; banner.Border = bBorder;
            banner.BackColor = bFill;   // the buttons' rounded corners blend into the card colour
            tips.SetToolTip(bannerAlt, bState == "added" ? "Put Discoveries.lua back the way it was before your last change." :
                "Copies text a friend can paste in game after typing /qgs import.");
            tips.SetToolTip(bannerMain, bState == "new" ? "Writes the new chains into the addon's Discoveries.lua. A backup is kept." :
                bState == "added" ? "Bump the version and build the CurseForge zip." :
                bMainAct == "use" && bState == "problem" ? "Switch the app to that game folder." :
                bState == "problem" || bState == "attention" ? "See what's installed where, and fix it." : "");
            if (bState == "problem") tips.SetToolTip(bannerAlt, bMainAct == "use" ? "See every game folder on this PC." : "Pick the game folder yourself.");
            LayoutBanner();
        }

        void SetAttention(CheckItem item, bool offerShare)
        {
            bState = "attention"; bIcon = "warn"; bFill = Pal.WarnBg; bBorder = Pal.WarnBorder; bTint = Pal.Warn;
            bHead = "QuestGearScout needs attention: " + item.Title.Substring(0, 1).ToLowerInvariant() + item.Title.Substring(1);
            bBody = item.Detail ?? "";
            bMainAct = "check";
            SetBannerButtons("Check addon...", offerShare && rows.Count > 0 ? "Share all " + rows.Count + " chains" : null);
        }

        void SetBannerButtons(string main, string alt)
        {
            wantMain = main != null; if (main != null) bannerMain.Text = main;
            wantAlt = alt != null; if (alt != null) bannerAlt.Text = alt;
            bannerMain.Invalidate(); bannerAlt.Invalidate();
        }

        string bMainAct = "";

        void BannerMainClick()
        {
            if (bState == "problem" && bMainAct == "use") UseSuggestion();
            else if (bState == "problem" || bState == "attention") ShowChecker(bMainAct == "check" && check.MisplacedToc == null);
            else if (bState == "new") AddRows(rows.Where(r => r.Status == ChainRow.New && FactionOk(r)).ToList(), new List<ChainRow>());
            else if (bState == "added") ShowPublish();
        }

        void BannerAltClick()
        {
            if (bState == "added") { UndoLast(); return; }
            if (bState == "problem") { if (bMainAct == "use") ShowChecker(false); else ShowFolderDialog(); return; }
            // Only what the banner counted: rows for the faction(s) being shown.
            var list = rows.Where(r => FactionOk(r) && (bState != "new" || r.Status == ChainRow.New)).ToList();
            CopyRows(list);
            bBody = "Copied. Your friend can paste it in game with /qgs import.";
            banner.Invalidate();
        }

        void UpdateUndo()
        {
            bool can = CanUndoHere();
            undoBtn.Enabled = can;
            undoBtn.Invalidate();
            tips.SetToolTip(undoBtn, can ? "Undo last change (" + lastBackupTime.ToString("t", CultureInfo.CurrentCulture) + ") - Ctrl+Z" : "Nothing to undo yet");
        }

        // ======================================================================
        // Detail pane
        // ======================================================================
        void UpdateDetail()
        {
            linkCopyLoc = linkAuthor = linkCopyIds = Rectangle.Empty;
            var nowSel = string.Join("|", SelectedRows().Select(r => r.Key).ToArray());
            if (nowSel != detailFor) { detailFor = nowSel; detail.AutoScrollPosition = Point.Empty; }
            detail.Invalidate();
            var sel = SelectedRows();
            footPrimary.Visible = footSecondary.Visible = false;
            footPrimary.Tag = footSecondary.Tag = null;
            if (sel.Count == 1)
            {
                var r = sel[0];
                if ((r.Status == ChainRow.New || r.Status == ChainRow.Shared) && addon.Found) { FootBtn(footPrimary, "Add to addon", "add", r.Status == ChainRow.New ? "primary" : "secondary", Pal.Accent); FootBtn(footSecondary, "Copy for /qgs import", "copy", "secondary", Pal.Accent); }
                else if (r.Status == ChainRow.InDiscoveries) { FootBtn(footPrimary, "Remove from addon", "remove", "secondary", Pal.Added); FootBtn(footSecondary, "Copy for /qgs import", "copy", "secondary", Pal.Accent); }
                else FootBtn(footSecondary, "Copy for /qgs import", "copy", "secondary", Pal.Accent);
            }
            else if (sel.Count > 1)
            {
                List<ChainRow> adds, ups;
                ChosenForAdd(out adds, out ups);
                if (adds.Count + ups.Count > 0 && addon.Found)
                    FootBtn(footPrimary, adds.Count > 0 && ups.Count > 0 ? "Add " + adds.Count + " new + update " + ups.Count
                                       : ups.Count > 0 ? "Update " + ups.Count + " in addon" : "Add " + adds.Count + " new to addon", "add", "primary", Pal.Accent);
                FootBtn(footSecondary, "Copy " + sel.Count + " for /qgs import", "copy", "secondary", Pal.Accent);
            }
            // Players view: the footer is about sharing.
            sharingBtnFoot.Visible = view == "players";
            if (view == "players")
            {
                sharingBtnFoot.Text = lan == null ? "Turn on sharing..." : "Sharing settings...";
                sharingBtnFoot.Kind = lan == null ? "primary" : "secondary";
                sharingBtnFoot.FitWidth(S(14), S(120));
                sharingBtnFoot.Location = new Point(footer.Width - S(14) - sharingBtnFoot.Width, (footer.Height - S(32)) / 2);
                sharingBtnFoot.Invalidate();
            }
            LayoutFooter();
            footer.Invalidate();
        }

        void FootBtn(FlatBtn b, string text, string action, string kind, Color tint)
        {
            b.Text = text; b.Tag = action; b.Visible = true; b.Kind = kind; b.Tint = tint;
            footFull[b] = text;
            tips.SetToolTip(b, text);
            b.Invalidate();
        }

        // Full and short labels per footer button (Tag holds the action; text set by FootBtn).
        Dictionary<FlatBtn, string> footFull = new Dictionary<FlatBtn, string>();

        void LayoutFooter()
        {
            int y = (footer.Height - S(32)) / 2;
            var btns = new[] { footSecondary, footPrimary }.Where(b => b.Visible).ToList();
            // Full labels when there's room for them and some text; otherwise short ones.
            foreach (bool full in new[] { true, false })
            {
                int total = 0;
                foreach (var b in btns)
                {
                    string label;
                    footFull.TryGetValue(b, out label);
                    b.Text = full ? (label ?? b.Text) : ShortLabel(label ?? b.Text);
                    b.FitWidth(S(14), full ? S(100) : S(64));
                    total += b.Width + S(8);
                }
                if (!full || total <= footer.Width - S(28) - S(150)) break;
            }
            int x = footer.Width - S(14);
            foreach (var b in btns) { x -= b.Width; b.Location = new Point(Math.Max(S(8), x), y); x -= S(8); }
            footer.Invalidate();
        }

        static string ShortLabel(string full)
        {
            var m = Regex.Match(full, @"\d+");
            string n = m.Success ? " " + m.Value : "";
            if (full.StartsWith("Copy")) return "Copy" + n;
            if (full.StartsWith("Remove")) return "Remove";
            if (full.StartsWith("Update")) return "Update" + n;
            if (full.StartsWith("Add")) return "Add" + n;
            return full;
        }

        void FooterAction(FlatBtn b)
        {
            string a = b.Tag as string;
            if (a == "add") AddToAddon();
            else if (a == "remove") RemoveFromDiscoveries();
            else if (a == "copy") CopyExport();
        }

        string FooterText(out string icon, out Color c)
        {
            var sel = SelectedRows();
            icon = "info"; c = Pal.Accent;
            if (view == "players") { c = lan != null ? Pal.Success : Pal.Muted; icon = lan != null ? "check" : "info"; return PlayersFooterText(); }
            if (sel.Count == 0) { c = Pal.Muted; return "Pick a chain to see its steps and share or add it."; }
            if (sel.Count > 1) return sel.Count + " chains selected";
            var r = sel[0];
            if (r.Status == ChainRow.Shared) { c = SharedC; return "Shared by " + r.From + ". It's in your game too (after /reload)."; }
            if (r.Status == ChainRow.New) return "This chain isn't in the addon yet.";
            if (r.Status == ChainRow.InDiscoveries) { c = Pal.Added; return "You added this one. It goes out with your next release."; }
            if (r.Status == ChainRow.BuiltIn) { icon = "check"; c = Pal.Success; return "Already in the addon. Nothing to do."; }
            icon = "warn"; c = Pal.Warn; return "The addon wasn't found, so this can't be compared.";
        }

        void PaintFooter(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var p = new Pen(Pal.CardBorder)) g.DrawLine(p, 0, 0, footer.Width, 0);
            string icon; Color c;
            string t = FooterText(out icon, out c);
            int right = (sharingBtnFoot.Visible ? sharingBtnFoot.Left : footPrimary.Visible ? footPrimary.Left : footSecondary.Visible ? footSecondary.Left : footer.Width) - S(10);
            if (right < S(40)) return;   // buttons take the whole width: no icon or text
            Draw.Icon(g, icon, new RectangleF(S(16), (footer.Height - S(16)) / 2f, S(16), S(16)), c);
            Draw.Text(g, t, fBody, new Rectangle(S(40), 0, Math.Max(0, right - S(40)), footer.Height), Pal.Sub, TextFormatFlags.VerticalCenter);
        }

        List<Step> StepsFor(ChainRow row)
        {
            var s = row.Starter;
            int n = Steps(row);
            string talk = s.Get("kind") == "O" ? "Start here. Use the " + Giver(s) + " in " + Zone(s) + "." : "Start here. Talk to " + Giver(s) + " in " + Zone(s) + ".";
            string last = row.IsDungeon ? "Dungeon quest: " + (s.Get("dungeonTitle") ?? "?") : "Final quest: " + (s.Get("finalTitle") ?? "?");
            string lastSub = row.IsDungeon ? "Take it into " + Reward(row) + "." : "Rewards " + Reward(row);
            Color end = row.IsDungeon ? Pal.Dungeon : Pal.Success;
            var steps = new List<Step>();
            if (n <= 1) steps.Add(new Step { Num = "1", Title = FirstTitle(row), Sub = talk + " " + lastSub, Color = end });
            else
            {
                steps.Add(new Step { Num = "1", Title = FirstTitle(row), Sub = talk, Color = Pal.Accent });
                if (n > 2) steps.Add(new Step { Num = "...", Title = Plural(n - 2, "more quest") + " in this chain", Sub = "", Color = Pal.Muted });
                steps.Add(new Step { Num = n.ToString(CultureInfo.InvariantCulture), Title = last, Sub = lastSub, Color = end });
            }
            return steps;
        }

        void SetDetailHeight(int h)
        {
            var want = new Size(0, h);
            if (detail.AutoScrollMinSize != want) detail.AutoScrollMinSize = want;
        }

        void PaintDetail(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            // Rebuild the clickable link areas on every paint, so a link that isn't drawn can't be clicked.
            linkCopyLoc = linkAuthor = linkCopyIds = Rectangle.Empty;
            if (view == "players") { PaintPlayerDetail(g); return; }
            var sel = SelectedRows();
            int pad = S(20), w = detail.ClientSize.Width - pad * 2;
            if (sel.Count != 1) SetDetailHeight(0);
            if (sel.Count == 0)
            {
                Draw.Icon(g, "map", new RectangleF((detail.Width - S(56)) / 2f, detail.Height / 2f - S(60), S(56), S(56)), Pal.CardBorder);
                Draw.Text(g, "Pick a chain on the left to see its steps.", fBody, new Rectangle(0, detail.Height / 2 + S(6), detail.Width, S(20)), Pal.Sub, TextFormatFlags.HorizontalCenter);
                return;
            }
            if (sel.Count > 1)
            {
                Draw.Text(g, sel.Count + " chains selected", fBig, new Rectangle(pad, pad, w, S(30)), Pal.Text, TextFormatFlags.Top);
                int a = sel.Count(r => r.Status == ChainRow.New), b = sel.Count(r => r.Status == ChainRow.InDiscoveries), c = sel.Count(r => r.Status == ChainRow.BuiltIn);
                var parts = new List<string>();
                if (a > 0) parts.Add(a + " new");
                if (b > 0) parts.Add(b + " added by you");
                if (c > 0) parts.Add(c + " in the addon");
                Draw.Text(g, string.Join(", ", parts.ToArray()), fBody, new Rectangle(pad, pad + S(34), w, S(18)), Pal.Sub, TextFormatFlags.Top);
                int yy = pad + S(66);
                // as many as fit, then "and N more"
                int fit = Math.Max(1, (detail.Height - yy - S(30)) / S(22));
                int showN = sel.Count > fit ? fit - 1 : sel.Count;
                foreach (var r in sel.Take(Math.Max(0, showN)))
                {
                    Draw.Icon(g, IconFor(r), new RectangleF(pad, yy + S(2), S(12), S(12)), Pal.Sub);
                    Draw.Text(g, Giver(r.Starter) + " - " + Zone(r.Starter) + " - " + Reward(r), fBody, new Rectangle(pad + S(18), yy, w - S(18), S(18)), Pal.Text, TextFormatFlags.Top);
                    yy += S(22);
                }
                if (sel.Count > showN) Draw.Text(g, "and " + (sel.Count - showN) + " more", fSmall, new Rectangle(pad + S(18), yy, w, S(16)), Pal.Sub, TextFormatFlags.Top);
                return;
            }

            var row = sel[0];
            var st = row.Starter;
            // Content scrolls when it's taller than the pane; link rectangles are in content coordinates.
            g.TranslateTransform(detail.AutoScrollPosition.X, detail.AutoScrollPosition.Y);
            int y = pad;
            int px = pad;
            if (row.Status == ChainRow.New) px += Draw.Pill(g, "New", fSmallBold, new Point(px, y), Pal.Accent, Pal.Accent, Pal.OnAccent, S(8), S(20), false);
            else if (row.Status == ChainRow.InDiscoveries) px += Draw.Pill(g, "Added by you", fSmall, new Point(px, y), Pal.AddedBg, Pal.AddedBorder, Pal.Added, S(8), S(20), false);
            else if (row.Status == ChainRow.BuiltIn) px += Draw.Pill(g, "\u2713 In the addon", fSmall, new Point(px, y), Pal.Card, Pal.CardBorder, Pal.Sub, S(8), S(20), false);
            else if (row.Status == ChainRow.Shared) px += Draw.Pill(g, "From " + row.From, fSmall, new Point(px, y), SharedBg, SharedBorder, SharedC, S(8), S(20), false);
            else px += Draw.Pill(g, "Can't check", fSmall, new Point(px, y), Pal.Card, Pal.CardBorder, Pal.Sub, S(8), S(20), false);
            px += S(6);
            int fw = DrawFaction(g, row.Faction, px, y, S(20), fSmall);
            if (fw > 0) px += fw;
            px += S(10);
            Draw.Icon(g, IconFor(row), new RectangleF(px, y + S(4), S(13), S(13)), Pal.Sub);
            Draw.Text(g, row.IsDungeon ? "Dungeon chain" : "Gear chain", fSmall, new Rectangle(px + S(18), y, S(200), S(20)), Pal.Sub, TextFormatFlags.VerticalCenter);
            y += S(30);
            Draw.Text(g, Giver(st), fBig, new Rectangle(pad, y, w, S(30)), Pal.Text, TextFormatFlags.Top);
            y += S(32);
            Draw.Icon(g, "pin", new RectangleF(pad, y + S(2), S(13), S(13)), Pal.Sub);
            string sub = (st.Get("kind") == "O" ? "Object in " : "Quest giver in ") + Zone(st) + (LocText(st) != "" ? " - " + LocText(st) : "") + "    ";
            Draw.Text(g, sub, fBody, new Rectangle(pad + S(18), y, w - S(18), S(18)), Pal.Sub, TextFormatFlags.Top);
            int lx = pad + S(18) + Draw.Width(g, sub, fBody);
            int lw = Draw.Width(g, "Copy location", fBody);
            if (lx + lw > pad + w) { y += S(20); lx = pad + S(18); }   // no room: put the link on its own line
            linkCopyLoc = new Rectangle(lx, y, lw, S(18));
            Draw.Text(g, "Copy location", fBody, linkCopyLoc, Pal.Accent, TextFormatFlags.Top);
            y += S(30);

            var card = new Rectangle(pad, y, Math.Min(w, S(460)), S(58));
            Color rc = RewardColor(row);
            if (row.IsDungeon)
            {
                Draw.Card(g, card, Pal.DungeonBg, Pal.DungeonBorder, 6f);
                Draw.Icon(g, "castle", new RectangleF(card.X + S(12), card.Y + S(13), S(32), S(32)), Pal.Dungeon);
                Draw.Text(g, "Leads to " + Reward(row), fBold, new Rectangle(card.X + S(56), card.Y + S(10), card.Width - S(64), S(18)), Pal.Dungeon, TextFormatFlags.Top);
                Draw.Text(g, "Dungeon quest: " + (st.Get("dungeonTitle") ?? "?"), fBody, new Rectangle(card.X + S(56), card.Y + S(30), card.Width - S(64), S(18)), Pal.Sub, TextFormatFlags.Top);
            }
            else
            {
                Draw.Card(g, card, Pal.Window, Pal.CardBorder, 6f);
                var sq = new Rectangle(card.X + S(10), card.Y + S(9), S(40), S(40));
                Draw.Card(g, sq, Pal.Card, rc, 5f);
                using (var p2 = new Pen(rc, 2f)) g.DrawRectangle(p2, sq.X + 1, sq.Y + 1, sq.Width - 3, sq.Height - 3);
                Draw.Icon(g, "sword", new RectangleF(sq.X + S(8), sq.Y + S(8), S(24), S(24)), rc);
                Draw.Text(g, Reward(row), fBold, new Rectangle(card.X + S(60), card.Y + S(10), card.Width - S(68), S(18)), rc, TextFormatFlags.Top);
                int n0 = Steps(row);
                Draw.Text(g, QualityWord(Quality(row)) + " reward " + (n0 > 1 ? "at the end of a " + n0 + "-quest chain" : "for this quest"), fBody,
                    new Rectangle(card.X + S(60), card.Y + S(30), card.Width - S(68), S(18)), Pal.Sub, TextFormatFlags.Top);
            }
            y = card.Bottom + S(18);
            Draw.Text(g, "How to get it", fBold, new Rectangle(pad, y, w, S(18)), Pal.Text, TextFormatFlags.Top);
            y += S(26);
            var steps = StepsFor(row);
            int cx = pad + S(11);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (int i = 0; i < steps.Count; i++)
            {
                var sp = steps[i];
                int rowH = sp.Sub == "" ? S(30) : S(42);
                if (i < steps.Count - 1) using (var lp = new Pen(Pal.CardBorder, S(2))) g.DrawLine(lp, cx, y + S(22), cx, y + rowH);
                var circ = new Rectangle(pad, y, S(22), S(22));
                if (sp.Num == "...")
                    using (var dp = new Pen(Pal.Muted, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }) { using (var eb = new SolidBrush(Pal.Card)) g.FillEllipse(eb, circ); g.DrawEllipse(dp, circ); }
                else
                {
                    using (var cb = new SolidBrush(sp.Color)) g.FillEllipse(cb, circ);
                    Draw.Text(g, sp.Num, fSmallBold, circ, Pal.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                Draw.Text(g, sp.Title, sp.Sub == "" ? fBody : fBold, new Rectangle(pad + S(34), y + S(2), w - S(34), S(18)), sp.Sub == "" ? Pal.Sub : Pal.Text, TextFormatFlags.Top);
                if (sp.Sub != "") Draw.Text(g, sp.Sub, fBody, new Rectangle(pad + S(34), y + S(20), w - S(34), S(18)), Pal.Sub, TextFormatFlags.Top);
                y += rowH;
            }
            y += S(4);
            string at = "Details for the addon author " + (authorOpen ? "\u25BE" : "\u25B8");
            linkAuthor = new Rectangle(pad, y, Draw.Width(g, at, fSmall), S(16));
            Draw.Text(g, at, fSmall, linkAuthor, Pal.Sub, TextFormatFlags.Top);
            y += S(20);
            if (authorOpen)
            {
                string ids = (st.Get("kind") == "O" ? "Object " : "NPC ") + st.Get("id") + "   First quest ID " + (RootId(row) ?? "?") + "   Key " + st.Key;
                var box = new Rectangle(pad, y, Math.Min(w, S(460)), S(26));
                Draw.Card(g, box, Pal.Input, Pal.InputBorder, 4f);
                Draw.Text(g, ids, fMono, new Rectangle(box.X + S(8), box.Y, box.Width - S(16), box.Height), Pal.Text, TextFormatFlags.VerticalCenter);
                y = box.Bottom + S(4);
                linkCopyIds = new Rectangle(pad, y, Draw.Width(g, "Copy quest IDs", fSmall), S(16));
                Draw.Text(g, "Copy quest IDs", fSmall, linkCopyIds, Pal.Accent, TextFormatFlags.Top);
                y += S(16);
            }
            SetDetailHeight(y + pad);
        }

        // Mouse position in content coordinates (the detail pane can scroll).
        Point DetailPoint(Point p) { return new Point(p.X - detail.AutoScrollPosition.X, p.Y - detail.AutoScrollPosition.Y); }

        void DetailClick(object sender, MouseEventArgs e)
        {
            var p = DetailPoint(e.Location);
            if (linkCopyLoc.Contains(p)) Run("Copy location", CopyGiver);
            else if (linkAuthor.Contains(p)) { authorOpen = !authorOpen; detail.Invalidate(); }
            else if (linkCopyIds.Contains(p)) Run("Copy quest IDs", CopyQuestIds);
        }

        // ======================================================================
        // First-run panel
        // ======================================================================
        void PaintFirstRun(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int pad = S(32), w = firstCard.Width - pad * 2, y = S(28);
            Draw.Icon(g, "map", new RectangleF(pad, y, S(40), S(40)), Pal.Accent);
            y += S(52);
            bool noFolder = setupStep == 1;
            string title = noFolder ? (suggestion != null ? "Found your game" : "Let's find your game") : "Almost there";
            Draw.Text(g, title, fBig, new Rectangle(pad, y, w, S(30)), Pal.Text, TextFormatFlags.Top);
            y += S(36);
            if (noFolder)
            {
                string text = suggestion != null
                    ? suggestion.Path + "\nQuestGearScout " + suggestion.Status.Summary + (suggestion.Status.GameVersion != "" ? " - game " + suggestion.Status.GameVersion : "") + "."
                    : quickScanning ? "Looking for World of Warcraft: Forever on this PC..."
                    : "Pick your World of Warcraft: Forever folder (the one named _forever_). Interface, AddOns or WTF inside it work too.";
                Draw.Text(g, text, fBody, new Rectangle(pad, y, w, S(40)), Pal.Sub, TextFormatFlags.WordBreak);
            }
            else
            {
                var items = new[] {
                    Tuple.Create(true, "Game folder found" + (check.GameVersion != "" ? " (game " + check.GameVersion + ")" : "")),
                    Tuple.Create(addon.Found, addon.Found ? "QuestGearScout v" + addon.Version + " installed"
                        : check.MisplacedToc != null ? "QuestGearScout is in the wrong folder"
                        : suggestion != null ? "QuestGearScout isn't here, but it's in " + suggestion.Path
                        : quickScanning ? "QuestGearScout isn't here - looking for it on this PC..."
                        : "QuestGearScout isn't installed in this folder"),
                    Tuple.Create(false, "Waiting for the game to save") };
                for (int i = 0; i < items.Length; i++)
                {
                    bool ok = items[i].Item1, pending = i == 2;
                    var r = new RectangleF(pad, y + S(1), S(16), S(16));
                    if (ok) Draw.Icon(g, "check", r, Pal.Success);
                    else if (pending) using (var p = new Pen(Pal.Muted, 1.5f)) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.DrawEllipse(p, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4); }
                    else Draw.Icon(g, "warn", r, Pal.Warn);
                    Draw.Text(g, items[i].Item2, fBody, new Rectangle(pad + S(24), y, w - S(24), S(18)), ok ? Pal.Text : Pal.Sub, TextFormatFlags.Top);
                    y += S(24);
                }
                y += S(4);
                Draw.Text(g, "Play, then log out or type /reload. Your finds show up here by themselves.", fBody, new Rectangle(pad, y, w, S(36)), Pal.Sub, TextFormatFlags.WordBreak);
            }
        }

        void UpdateFirstButton()
        {
            if (setupStep == 1)
            {
                firstBtn.Text = suggestion != null ? "Use this folder" : "Choose game folder...";
                firstAlt.Text = suggestion != null ? "Choose a different folder..." : "Scan this PC";
                firstBtn.Kind = "primary";
            }
            else if (!check.Installed)
            {
                firstBtn.Text = check.MisplacedToc != null ? "Fix it..." : suggestion != null ? "Use that folder" : "Check addon...";
                firstAlt.Text = suggestion != null ? "Check addon..." : "Scan this PC";
                firstBtn.Kind = "primary";
            }
            else
            {
                firstBtn.Text = "Choose a different folder...";
                firstAlt.Text = "Check addon...";
                firstBtn.Kind = "secondary";
            }
            firstBtn.FitWidth(S(18), S(160));
            firstAlt.FitWidth(S(18), S(120));
            firstBtn.Invalidate(); firstAlt.Invalidate();
            if (firstAlt.Parent != null) firstAlt.Location = new Point(firstBtn.Right + S(8), firstBtn.Top);
        }

        // ======================================================================
        // Keyboard
        // ======================================================================
        void OnFormKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; Run("Refresh", RefreshAll); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = e.SuppressKeyPress = true; searchBox.Focus(); searchBox.SelectAll(); }
            // Undo and Add work from anywhere except while typing (where Ctrl+Z undoes the typing).
            else if (e.Control && (e.KeyCode == Keys.Z || e.KeyCode == Keys.D) && !(ActiveControl is TextBox) && !(ActiveControl is ComboBox)
                     && ActiveControl != nb && ActiveControl != table)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Z) Run("Undo", UndoLast); else Run("Add to addon", AddToAddon);
            }
        }

        void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; Run("Copy", CopyExport); }
            else if (e.Control && e.KeyCode == Keys.A) { e.Handled = e.SuppressKeyPress = true; SelectAll(); }
            else if (e.Control && e.KeyCode == Keys.N) { e.Handled = e.SuppressKeyPress = true; SelectNew(); }
            else if (e.Control && e.KeyCode == Keys.D) { e.Handled = e.SuppressKeyPress = true; Run("Add to addon", AddToAddon); }
            else if (e.Control && e.KeyCode == Keys.Z) { e.Handled = e.SuppressKeyPress = true; Run("Undo", UndoLast); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; Run("Remove from addon", RemoveFromDiscoveries); }
        }

        // ======================================================================
        // Actions
        // ======================================================================
        void Run(string what, Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                Theme.Box(this, what + " didn't work.\n\n" + Friendly(ex) + "\n\nDetails: " + ex.Message,
                    "QuestGearScout Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Msg(what + " didn't work: " + ex.Message, true);
            }
        }

        static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException)
                return "Windows won't let this app write there. This usually happens when WoW is under Program Files.\n" +
                       "Close the app, right-click it and choose \"Run as administrator\", or give your user account write access to the WoW folder.";
            if (ex is ExternalException) return "The clipboard is busy (another program is using it). Try again in a moment.";
            if (ex is IOException) return "A file is in use by another program (an editor, or the game), or the path isn't allowed. Close it and try again.";
            return "Something unexpected went wrong.";
        }

        static void SetClipboard(string text)
        {
            for (int i = 0; ; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch (ExternalException) { if (i >= 2) throw; System.Threading.Thread.Sleep(100); }
            }
        }

        // Dialogs open on top of whatever is in front: the Publish dialog while it's open, else this window.
        IWin32Window DialogParent { get { return dialogOwner ?? this; } }

        bool Confirm(string body)
        {
            if (uiTest) { testConfirms.Add(body.Replace("\n", " / ")); return true; }
            return Theme.Box(DialogParent, body, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }
        internal List<string> testConfirms = new List<string>();

        bool AddonReady()
        {
            if (addon.Found) return true;
            Theme.Box(DialogParent, "QuestGearScout isn't installed in this folder:\n" + AddonDir + "\n\nUse Folder... to pick the right one.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Runs a change to Discoveries.lua unless one is already running (no double adds).
        void Guarded(Action a)
        {
            if (busy) return;
            busy = true;
            try { a(); } finally { busy = false; }
        }

        string Describe(ChainRow r) { return Giver(r.Starter) + " - " + Zone(r.Starter) + " - " + (r.IsDungeon ? "leads to " : "") + Reward(r); }

        static string ListLines(List<ChainRow> a, string headA, List<ChainRow> b, string headB, Func<ChainRow, string> line)
        {
            var sb = new StringBuilder();
            int shown = 0, total = a.Count + b.Count;
            foreach (var part in new[] { new { Head = headA, Rows = a }, new { Head = headB, Rows = b } })
            {
                if (part.Rows.Count == 0 || shown >= 15) continue;
                sb.Append(part.Head).Append('\n');
                foreach (var r in part.Rows)
                {
                    if (shown >= 15) break;
                    sb.Append("   ").Append(line(r)).Append('\n');
                    shown++;
                }
            }
            if (total > shown) sb.Append("   (and ").Append(total - shown).Append(" more)\n");
            return sb.ToString();
        }

        void CopyRows(List<ChainRow> chosen)
        {
            if (chosen.Count == 0) { Msg("There are no chains to share.", true); return; }
            SetClipboard(Starter.Encode(ChainRow.Pack(chosen, null).Values));
            Msg("Copied " + Plural(chosen.Count, "chain") + ". Your friend types /qgs import in game and pastes with Ctrl+V.", false);
        }

        void CopyExport() { CopyRows(Chosen()); }

        void SaveExport() { SaveRows(Chosen()); }

        void SaveRows(List<ChainRow> chosen)
        {
            if (chosen.Count == 0) { Msg("There are no chains to share.", true); return; }
            using (var dlg = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "QuestGearScout-share-" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt" })
            {
                if (dlg.ShowDialog(DialogParent) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, Starter.Encode(ChainRow.Pack(chosen, null).Values), new UTF8Encoding(false));
                Msg("Saved " + Plural(chosen.Count, "chain") + " to " + dlg.FileName, false);
            }
        }

        void WriteWithBackup(IEnumerable<Starter> starters)
        {
            string b = AddonData.Backup(AddonDir);
            AddonData.WriteDiscoveries(AddonDir, starters);
            lastBackup = b;
            lastBackupDir = AddonDir;
            lastBackupTime = DateTime.Now;
        }

        Dictionary<string, Starter> FreshDiscovered() { return AddonData.Load(AddonDir).Discovered; }

        void AddToAddon()
        {
            List<ChainRow> adds, ups;
            ChosenForAdd(out adds, out ups);
            AddRows(adds, ups);
        }

        void AddRows(List<ChainRow> adds, List<ChainRow> ups) { Guarded(() => AddRowsCore(adds, ups)); }

        void AddRowsCore(List<ChainRow> adds, List<ChainRow> ups)
        {
            if (!AddonReady()) return;
            if (adds.Count + ups.Count == 0) { Msg(SelectedRows().Count > 0 ? "Nothing selected is new - it's already in the addon." : "Nothing new to add.", false); return; }
            string body = "Add these to the addon?\n\n" + ListLines(adds, "New:", ups, "Update (added before):", Describe) +
                "\nThey're written to Discoveries.lua, load in game after /reload, and go out with your next release. A backup is kept, so you can undo.";
            if (!Confirm(body)) return;
            WriteWithBackup(ChainRow.Pack(adds.Concat(ups), FreshDiscovered()).Values);
            Reload();
            Msg("Added " + Plural(adds.Count, "chain") + (ups.Count > 0 ? ", updated " + ups.Count : "") + ". Type /reload in game to check.", false);
        }

        void RemoveFromDiscoveries() { Guarded(RemoveCore); }

        void RemoveCore()
        {
            if (!AddonReady()) return;
            var chosen = SelectedRows().Where(r => r.Status == ChainRow.InDiscoveries).ToList();
            if (chosen.Count == 0) { Msg("Select chains marked \"Added by you\" to remove them.", false); return; }
            string body = "Remove these from the addon?\n\n" + ListLines(chosen, "Remove:", new List<ChainRow>(), "",
                r => Giver(r.Starter) + " - " + (r.IsDungeon ? "dungeon chain" : "gear chain")) + "\nA backup is kept, so you can undo.";
            if (!Confirm(body)) return;
            WriteWithBackup(ChainRow.Unpack(chosen, FreshDiscovered()).Values);
            Reload();
            Msg("Removed " + Plural(chosen.Count, "chain") + " from the addon.", false);
        }

        void UndoLast() { Guarded(UndoCore); }

        void UndoCore()
        {
            if (lastBackup == null || !File.Exists(lastBackup)) { lastBackup = null; UpdateUndo(); Msg("Nothing to undo.", false); return; }
            if (!AddonReady()) return;
            if (!CanUndoHere())
            {
                lastBackup = null; UpdateUndo();
                Msg("The last change was made in a different game folder, so there's nothing to undo here.", false);
                return;
            }
            if (!Confirm("Undo your last change to the addon (made at " + lastBackupTime.ToString("t", CultureInfo.CurrentCulture) + ")?")) return;
            string restore = lastBackup;
            AddonData.Backup(lastBackupDir);
            string target = Path.Combine(lastBackupDir, "Discoveries.lua");
            File.Copy(restore, target, true);
            // A copy keeps the backup's old time; mark it changed now so the banner and Publish see it.
            try { File.SetLastWriteTime(target, DateTime.Now); } catch { }
            lastBackup = null;
            Reload();
            Msg("Undone. Type /reload in game to load it.", false);
        }

        void CopyGiver()
        {
            var sel = SelectedRows();
            if (sel.Count == 0) return;
            var lines = sel.GroupBy(r => r.Starter.Key).Select(gp => gp.First().Starter)
                .Select(s => Giver(s) + " - " + Zone(s) + " " + LocText(s)).ToArray();
            SetClipboard(string.Join("\r\n", lines));
            Msg("Copied " + Plural(lines.Length, "location") + ".", false);
        }

        void CopyQuestIds()
        {
            var ids = SelectedRows().Select(r => RootId(r)).Where(x => x != null).Distinct().ToArray();
            if (ids.Length == 0) return;
            SetClipboard(string.Join(", ", ids));
            Msg("Copied " + Plural(ids.Length, "quest ID") + ".", false);
        }

        void OpenFolder(string dir)
        {
            if (!SafeDirExists(dir)) { Msg("That folder doesn't exist yet.", true); return; }
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        // ======================================================================
        // Publish dialog
        // ======================================================================
        static string NextVersion(string v)
        {
            var m = Regex.Match(v ?? "", @"^(.*?)(\d+)([^\d]*)$");
            long n;
            if (!m.Success || !long.TryParse(m.Groups[2].Value, out n)) return null;
            return m.Groups[1].Value + (n + 1) + m.Groups[3].Value;
        }

        // Build history is kept per addon folder.
        string ZipKey(string what)
        {
            string dir;
            try { dir = Path.GetFullPath(AddonDir).TrimEnd('\\').ToLowerInvariant(); } catch { dir = AddonDir.ToLowerInvariant(); }
            return what + "|" + dir;
        }

        string LastZipVersion()
        {
            string v;
            return settings.TryGetValue(ZipKey("lastZipVersion"), out v) ? v : null;
        }

        bool AlreadyBuilt()
        {
            string v = LastZipVersion();
            return v != null && v == addon.Version && addon.Version != "";
        }

        internal Form BuildPublishDialog()
        {
            var f = new Form { Text = "Publish a new version", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(560), S(380)), BackColor = Pal.Card, Font = fBody, ShowInTaskbar = false };
            var title = new Label { Text = "Publish a new version", Font = fBig, AutoSize = true, Location = new Point(S(24), S(18)) };
            var intro = new Label { Text = "Three steps to get your finds to everyone who uses the addon.", ForeColor = Pal.Sub, AutoSize = true, Location = new Point(S(24), S(52)) };
            f.Controls.Add(title); f.Controls.Add(intro);

            var circles = new BufferedPanel[3];
            var heads = new Label[3];
            var subs = new Label[3];
            var btns = new FlatBtn[3];
            var done = new bool[3];
            var shownBtn = new bool[3];   // what refresh() asked for (Visible reads false until the dialog is shown)
            for (int i = 0; i < 3; i++)
            {
                int y = S(88) + i * S(76);
                int idx = i;
                circles[i] = new BufferedPanel { Location = new Point(S(24), y), Size = new Size(S(28), S(28)), BackColor = Pal.Card };
                circles[i].Paint += (s, e) =>
                {
                    var g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(done[idx] ? Pal.Success : Pal.Accent)) g.FillEllipse(b, 0, 0, S(27), S(27));
                    if (done[idx]) Draw.Icon(g, "check", new RectangleF(S(6), S(6), S(16), S(16)), Pal.OnAccent);
                    else Draw.Text(g, (idx + 1).ToString(CultureInfo.InvariantCulture), fBold, new Rectangle(0, 0, S(28), S(28)), Pal.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
                heads[i] = new Label { Font = fHead, AutoSize = true, Location = new Point(S(64), y) };
                subs[i] = new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(S(64), y + S(24)), Size = new Size(S(290), S(36)) };
                btns[i] = new FlatBtn { Height = S(32), Font = fBody };
                f.Controls.AddRange(new Control[] { circles[i], heads[i], subs[i], btns[i] });
            }
            heads[0].Text = "Add your finds"; heads[1].Text = "Version number"; heads[2].Text = "Build the zip";

            var foot = new Label { Text = "Sharing with a friend instead?", ForeColor = Pal.Sub, AutoSize = true, Location = new Point(S(24), S(340)) };
            var copyLink = new LinkLabel { Text = "Copy for /qgs import", AutoSize = true, LinkColor = Pal.Accent, Location = new Point(S(214), S(340)) };
            var saveLink = new LinkLabel { Text = "Save share file...", AutoSize = true, LinkColor = Pal.Accent, Location = new Point(S(354), S(340)) };
            var close = new FlatBtn { Text = "Close", Size = new Size(S(80), S(30)), Location = new Point(S(456), S(334)) };
            close.Click += (s, e) => f.Close();
            // Both links share every chain, so they always match each other.
            copyLink.LinkClicked += (s, e) => Run("Copy", () => CopyRows(rows.ToList()));
            saveLink.LinkClicked += (s, e) => Run("Save share file", () => SaveRows(rows.ToList()));
            f.Controls.AddRange(new Control[] { foot, copyLink, saveLink, close });
            f.CancelButton = close;
            Theme.Form(f);

            Action refresh = () =>
            {
                int nNew = rows.Count(r => r.Status == ChainRow.New);
                done[0] = nNew == 0;
                subs[0].Text = done[0] ? "All finds added." : Plural(nNew, "new chain") + (nNew == 1 ? " isn't" : " aren't") + " in the addon yet.";
                btns[0].Visible = shownBtn[0] = !done[0]; btns[0].Text = "Add " + (nNew == 1 ? "it" : "them") + " now"; btns[0].Kind = "secondary";

                string next = NextVersion(addon.Version);
                string last = LastZipVersion();
                // Done only when we know the number changed since the last zip built from this folder.
                done[1] = last != null && last != addon.Version;
                subs[1].Text = done[1] ? "v" + addon.Version + " - ready for a new upload."
                             : last != null ? "Currently v" + addon.Version + ". CurseForge needs a new number for each upload."
                             : "Currently v" + addon.Version + ". Make sure it's newer than your last CurseForge upload.";
                btns[1].Visible = shownBtn[1] = !done[1] && next != null; btns[1].Text = "Bump to v" + next; btns[1].Kind = "secondary";

                done[2] = false;
                subs[2].Text = "Junk like .git and backup files is left out.";
                btns[2].Visible = shownBtn[2] = true; btns[2].Text = "Build CurseForge zip..."; btns[2].Kind = done[0] && done[1] ? "primary" : "secondary";
                btns[2].Font = btns[2].Kind == "primary" ? fBold : fBody;
                for (int i = 0; i < 3; i++)
                {
                    btns[i].FitWidth(S(14), S(120));
                    btns[i].Location = new Point(f.ClientSize.Width - S(24) - btns[i].Width, S(88) + i * S(76));
                    btns[i].Invalidate(); circles[i].Invalidate();
                }
            };
            btns[0].Click += (s, e) => Run("Add", () => { AddRows(rows.Where(r => r.Status == ChainRow.New).ToList(), new List<ChainRow>()); refresh(); });
            btns[1].Click += (s, e) => Run("Bump version", () =>
            {
                string nv = AddonData.BumpVersion(AddonDir);
                if (nv == null) { Theme.Box(f, "Couldn't find a '## Version:' line ending in a number in QuestGearScout.toc. Edit it by hand.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Reload(); Msg("Version bumped to " + nv + ".", false); refresh();
            });
            btns[2].Click += (s, e) => Run("Build zip", () => { if (ZipNow(f)) f.Close(); });
            refresh();
            publishDialogRefresh = refresh;
            f.Tag = new Func<string>(() =>
                string.Join(" | ", Enumerable.Range(0, 3).Select(i => (done[i] ? "[done] " : "[" + (i + 1) + "] ") + heads[i].Text + ": " + subs[i].Text +
                    (shownBtn[i] ? " <" + btns[i].Text + ">" : "")).ToArray()));
            return f;
        }

        Action publishDialogRefresh;   // the newest dialog's refresh (set by BuildPublishDialog)

        void ShowPublish()
        {
            if (!AddonReady()) return;
            using (var f = BuildPublishDialog())
            {
                // While it's open: follow live changes (a game save can add new chains) and own the confirmations.
                publishRefresh = publishDialogRefresh;
                dialogOwner = f;
                try { f.ShowDialog(this); }
                finally { publishRefresh = null; dialogOwner = null; }
            }
        }

        bool ZipNow(IWin32Window owner)
        {
            if (!AddonReady()) return false;
            string dir;
            if (!settings.TryGetValue("lastZipDir", out dir) || !SafeDirExists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            using (var dlg = new SaveFileDialog { Filter = "Zip file (*.zip)|*.zip", InitialDirectory = dir,
                FileName = "QuestGearScout-" + (addon.Version == "" ? "build" : addon.Version) + ".zip" })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                string full = Path.GetFullPath(dlg.FileName);
                string root = Path.GetFullPath(AddonDir).TrimEnd('\\') + "\\";
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Theme.Box(owner, "Save the zip outside the addon folder, or it would include itself.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (File.Exists(full)) File.Delete(full);
                var skipped = new List<string>();
                int count = Zipper.Zip(AddonDir, full, skipped);
                settings[ZipKey("lastZipVersion")] = addon.Version;
                settings[ZipKey("lastZipTime")] = DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
                settings["lastZipDir"] = Path.GetDirectoryName(full);
                SaveSettings();
                UpdateBanner();
                Msg("Built " + Path.GetFileName(full) + " (" + count + " files).", false);
                if (Theme.Box(owner, "Built " + Path.GetFileName(full) + " (" + count + " files).\n\nUpload it on CurseForge: your project > Files > Upload File.\n\nOpen the folder?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("explorer.exe", "/select,\"" + full + "\"");
                return true;
            }
        }

        // ======================================================================
        // Self-test hooks (QuestGearScoutCompanion.exe --uitest)
        // ======================================================================
        internal string TestDump()
        {
            var sb = new StringBuilder();
            Color c1, c2, c3; string ic;
            sb.Append("header: [").Append(AddonPillText(out c1, out c2, out c3, out ic)).Append("] ").Append(WatchText()).Append(accountBox.Visible && accountBox.SelectedItem != null ? " | " + accountBox.SelectedItem : "").Append('\n');
            if (firstRun.Visible)
            {
                UpdateFirstButton();
                sb.Append("FIRST-RUN (step ").Append(setupStep).Append("): ").Append(setupStep == 1 ? (suggestion != null ? "Found your game: " + suggestion.Path : "Let's find your game")
                    : "Almost there" + (!check.Installed && suggestion != null ? " - addon is in " + suggestion.Path : ""))
                  .Append(" <").Append(firstBtn.Text).Append("> <").Append(firstAlt.Text).Append(">\n");
            }
            else
            {
                sb.Append("banner [").Append(bState).Append(compact ? ", compact" : "").Append("]: ").Append(bHead).Append(" / ").Append(bBody)
                  .Append(bannerMain.Visible ? " <" + bannerMain.Text + ">" : "").Append(bannerAlt.Visible ? " <" + bannerAlt.Text + ">" : "").Append('\n');
                sb.Append("pills: ").Append(string.Join(" ", new[] { pAll, pNew, pGear, pDun }.Select(p => (p.Checked ? "*" : "") + p.Text).ToArray()))
                  .Append(" | view ").Append(view).Append(" | undo ").Append(undoBtn.Enabled ? "on" : "off").Append(" | quality box ").Append(qualityBox.Visible ? "shown" : "hidden").Append('\n');
                if (view == "notebook")
                {
                    for (int i = 0; i < nb.Items.Count; i++)
                    {
                        var h = nb.Items[i] as GroupHeader;
                        if (h != null) { sb.Append("  # ").Append(h.Text).Append('\n'); continue; }
                        var r = (ChainRow)nb.Items[i];
                        sb.Append("    ").Append(Giver(r.Starter)).Append(" [").Append(StatusWord(r)).Append("] ").Append(Line2(r)).Append(Reward(r))
                          .Append(r.Faction != null ? " {" + FactionWord(r.Faction) + "}" : "")
                          .Append(nb.GetSelected(i) ? "  <selected>" : "").Append('\n');
                    }
                    var sel = SelectedRows();
                    if (sel.Count == 1)
                    {
                        var r = sel[0];
                        sb.Append("  detail: ").Append(Giver(r.Starter)).Append(" | ").Append(Zone(r.Starter)).Append(" ").Append(LocText(r.Starter)).Append(" | ")
                          .Append(r.IsDungeon ? "Leads to " + Reward(r) : Reward(r) + " (" + QualityWord(Quality(r)) + ")").Append(" | steps: ")
                          .Append(string.Join(" > ", StepsFor(r).Select(x => x.Num + " " + x.Title + (x.Sub != "" ? " (" + x.Sub + ")" : "")).ToArray())).Append('\n');
                    }
                }
                else
                    foreach (ListViewItem it in table.Items)
                        sb.Append("    ").Append(string.Join(" | ", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text).ToArray()))
                          .Append(it.Selected ? "  <selected>" : "").Append('\n');
                if (emptyLink.Visible) sb.Append("  (empty) ").Append(emptyLink.Text.Replace("\n", " / ")).Append('\n');
                string icon; Color c;
                sb.Append("footer: ").Append(FooterText(out icon, out c)).Append(footPrimary.Visible ? " <" + footPrimary.Text + ">" : "")
                  .Append(footSecondary.Visible ? " <" + footSecondary.Text + ">" : "").Append('\n');
            }
            sb.Append("status: ").Append(lblLeft.Text).Append(" || ").Append(lblMsg.Text).Append(" || ").Append(lblShowing.Text).Append('\n');
            return sb.ToString();
        }

        internal void TestSearch(string text) { searchBox.Text = text; searchTimer.Stop(); Fill(); }
        internal void TestFilter(string f) { filter = f; UpdatePills(); Fill(); }
        internal void TestView(string v) { SetView(v); }
        internal void TestSort(int col) { SortBy(col); }
        internal void TestSetGame(string dir) { ApplyGameDir(dir); UpdateFirstButton(); }
        internal void TestBannerMain() { Run("banner", BannerMainClick); }
        internal void TestBannerAlt() { Run("banner", BannerAltClick); }
        internal void TestSelectStatus(string status)
        {
            filling = true;
            if (view == "table") foreach (ListViewItem it in table.Items) it.Selected = ((ChainRow)it.Tag).Status == status;
            else for (int i = 0; i < nb.Items.Count; i++) { var r = nb.Items[i] as ChainRow; nb.SetSelected(i, r != null && r.Status == status); }
            filling = false;
            UpdateDetail();
        }
        internal void TestSelectGiver(string name)
        {
            filling = true;
            nb.ClearSelected();
            for (int i = 0; i < nb.Items.Count; i++) { var r = nb.Items[i] as ChainRow; if (r != null && Giver(r.Starter) == name) { nb.SetSelected(i, true); break; } }
            filling = false;
            UpdateDetail();
        }
        internal string TestPublish() { using (var f = BuildPublishDialog()) return ((Func<string>)f.Tag)(); }
        internal void TestWaitScan()
        {
            var until = DateTime.UtcNow.AddSeconds(40);
            while (quickScanning && DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            Application.DoEvents();
        }
        internal string TestChecker(string shotPath)
        {
            using (var f = new CheckerForm(scale, () => gameDir, dir => ApplyGameDir(dir), OpenFolder, Confirm, scanResults) { testMode = true })
            {
                if (shotPath != null)
                {
                    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-20000, -20000);
                    f.Show(); Application.DoEvents();
                    using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png); }
                }
                return f.TestText();
            }
        }
        // Shows a dialog off-screen and saves a picture of it (for the --uitest screenshots).
        internal void TestDialogShot(Form f, string shotPath)
        {
            using (f)
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-20000, -20000);
                f.Show(); Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png); }
                f.Close();
            }
        }
        void PaintDetailNow()
        {
            // The test window is off-screen and never gets WM_PAINT, so paint into a bitmap.
            using (var bmp = new Bitmap(Math.Max(1, detail.Width), Math.Max(1, detail.Height))) detail.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        }

        internal string TestAuthorToggle()
        {
            var sb = new StringBuilder();
            PaintDetailNow();
            sb.Append("collapsed: ids link ").Append(linkCopyIds.IsEmpty ? "none" : "SHOWN");
            authorOpen = true; PaintDetailNow();
            sb.Append(" | open: ids link ").Append(linkCopyIds.IsEmpty ? "MISSING" : "shown").Append(", content height ").Append(detail.AutoScrollMinSize.Height)
              .Append(" vs pane ").Append(detail.ClientSize.Height).Append(detail.VerticalScroll.Visible ? " (scrollbar on)" : " (no scrollbar)");
            authorOpen = false; PaintDetailNow();
            sb.Append(" | collapsed again: ids link ").Append(linkCopyIds.IsEmpty ? "none" : "STILL CLICKABLE");
            return sb.ToString();
        }
        internal string TestToolbar()
        {
            var parts = new[] { pAll, pNew, pGear, pDun }.Select(p => p.Text + "@" + p.Left + "-" + p.Right).ToList();
            return string.Join(", ", parts.ToArray()) + " | quality " + (qualityBox.Visible ? qualityBox.Left + "-" + qualityBox.Right : "hidden") +
                " | view switch starts " + vNotebook.Left + " | search " + searchHost.Width + " | overlap " +
                (new[] { pAll, pNew, pGear, pDun }.Any(p => p.Right > vNotebook.Left) || (qualityBox.Visible && qualityBox.Right > vNotebook.Left) ? "YES" : "no");
        }
        internal string TestFooter()
        {
            return string.Join(" | ", new[] { footPrimary, footSecondary }.Where(b => b.Visible).Select(b => b.Text + "@" + b.Left).ToArray()) + " (footer " + footer.Width + ")";
        }
        internal void TestSelectNewAndBuiltIn()
        {
            filling = true;
            nb.ClearSelected();
            int added = 0;
            for (int i = 0; i < nb.Items.Count && added < 5; i++) { if (nb.Items[i] is ChainRow) { nb.SetSelected(i, true); added++; } }
            filling = false;
            UpdateDetail();
        }
        internal void TestSize(int w, int h) { ClientSize = new Size(S(w), S(h)); Application.DoEvents(); }
        internal Bitmap TestShot()
        {
            var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            return bmp;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            // Command-line modes (used for testing):
            //   --report  <SavedVariables file> <addon folder> <output file>
            //   --add-new <SavedVariables file> <addon folder>
            //   --bump    <addon folder>
            //   --zip     <addon folder> <zip file>
            //   --uitest  <WoW Forever folder> <output file> <app data folder>
            if (args.Length == 4 && args[0] == "--report")
            {
                var addon = AddonData.Load(args[2]);
                var learned = Learned.FromSavedVariables(File.ReadAllText(args[1]), addon, 2);
                var rows = learned.Rows(addon);
                var sb = new StringBuilder();
                sb.AppendFormat("addon v{0}: {1} built-in starters, {2} discoveries, {3} bundled quests\n",
                    addon.Version, addon.BuiltIn.Count, addon.Discovered.Count, addon.BundledTitle.Count);
                sb.AppendFormat("quests seen {0}, dungeon quests {1}, chains {2}, new {3}\n",
                    learned.QuestsSeen, learned.DungeonQuests, rows.Count, rows.Count(r => r.Status == ChainRow.New));
                foreach (var r in rows.OrderBy(r => r.Status).ThenBy(r => r.Starter.Key))
                    sb.AppendFormat("{0}\t{1}\t{2}\t{3}\t{4}\n", r.Status, r.IsDungeon ? "Dungeon" : "Gear", r.Starter.Key,
                        r.Starter.Get("name"), Zones.Name(r.Starter.Num("map")));
                sb.Append("--- export ---\n").Append(Starter.Encode(learned.Starters.Values)).Append('\n');
                File.WriteAllText(args[3], sb.ToString(), new UTF8Encoding(false));
                return 0;
            }
            if (args.Length == 3 && args[0] == "--add-new")
            {
                var addon = AddonData.Load(args[2]);
                var learned = Learned.FromSavedVariables(File.ReadAllText(args[1]), addon, 2);
                var chosen = learned.Rows(addon).Where(r => r.Status == ChainRow.New).ToList();
                AddonData.Backup(args[2]);
                AddonData.WriteDiscoveries(args[2], ChainRow.Pack(chosen, addon.Discovered).Values);
                return chosen.Count;
            }
            if (args.Length == 3 && args[0] == "--check")
            {
                var st = AddonCheck.Run(args[1]);
                File.WriteAllText(args[2], "summary: " + st.Summary + " | game " + st.GameVersion + " (" + st.GameInterface + ") | addon interface " + st.Interface + "\n" +
                    string.Join("\n", st.Items.Select(i => i.ToString() + (i.Fix != null ? "  {" + i.Fix + "}" : "")).ToArray()) + "\n", new UTF8Encoding(false));
                return 0;
            }
            if (args.Length == 3 && args[0] == "--fix")
            {
                var st = AddonCheck.Run(args[1]);
                string msg;
                try { msg = AddonCheck.FixLayout(st); } catch (Exception ex) { msg = "error: " + ex.Message; }
                var after = AddonCheck.Run(args[1]);
                File.WriteAllText(args[2], msg + "\nafter: " + after.Summary + "\n", new UTF8Encoding(false));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--scan")
            {
                var t0 = DateTime.UtcNow;
                var found = InstallScanner.Scan(20, null);
                File.WriteAllText(args[1], "took " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms\n" +
                    string.Join("\n", found.Select(x => x + " | product " + x.Status.Product + " | game " + x.Status.GameVersion).ToArray()) + "\n", new UTF8Encoding(false));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--lantest")
            {
                File.WriteAllText(args[1], LanTest.Run(), new UTF8Encoding(false));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--bump")
            {
                string nv = AddonData.BumpVersion(args[1]);
                File.WriteAllText(Path.Combine(args[1], "bump-result.txt"), nv ?? "(null)");
                return 0;
            }
            if (args.Length == 3 && args[0] == "--zip")
            {
                var skipped = new List<string>();
                int n = Zipper.Zip(args[1], args[2], skipped);
                File.WriteAllText(args[2] + ".txt", n + " files; skipped: " + string.Join(", ", skipped.ToArray()));
                return 0;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nThe app will keep running.",
                    "QuestGearScout Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (args.Length == 4 && args[0] == "--uitest")
            {
                AppPaths.Root = args[3];
                return UiTest(args[1], args[2]);
            }
            Application.Run(new MainForm());
            return 0;
        }

        static void Shot(MainForm f, string path)
        {
            Application.DoEvents();
            using (var b = f.TestShot()) b.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        static int UiTest(string gameDir, string outFile)
        {
            var log = new StringBuilder();
            string shots = Path.GetDirectoryName(Path.GetFullPath(outFile));
            var f = new MainForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            f.Show();
            f.TestSize(1024, 640);
            f.TestSetGame(gameDir); Application.DoEvents();
            log.Append("== loaded\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-1-new.png"));
            log.Append("== author details toggle: ").Append(f.TestAuthorToggle()).Append('\n');
            log.Append("== toolbar at 1024: ").Append(f.TestToolbar()).Append('\n');
            log.Append("== footer at 1024: ").Append(f.TestFooter()).Append('\n');
            f.TestSize(900, 600); Application.DoEvents();
            log.Append("== toolbar at 900 (minimum): ").Append(f.TestToolbar()).Append('\n');
            log.Append("== footer at 900: ").Append(f.TestFooter()).Append('\n');
            f.TestSelectNewAndBuiltIn(); Application.DoEvents();
            log.Append("== footer at 900, 5 selected incl. new: ").Append(f.TestFooter()).Append('\n');
            Shot(f, Path.Combine(shots, "shot-0-narrow.png"));
            f.TestSize(1024, 640); Application.DoEvents();
            f.TestSelectGiver("Remy \"Two Times\"");
            f.TestSelectGiver("Wilder Thistlenettle");
            f.TestFilter("dun"); Application.DoEvents();
            log.Append("== Dungeons pill\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-2-dungeon.png"));
            f.TestFilter("all");
            f.TestSearch("pocket"); Application.DoEvents();
            log.Append("== search 'pocket'\n").Append(f.TestDump());
            f.TestSearch("zzzz"); Application.DoEvents();
            log.Append("== search with no match\n").Append(f.TestDump());
            f.TestSearch("");
            log.Append("== publish dialog before adding: ").Append(f.TestPublish()).Append('\n');
            f.TestBannerMain(); Application.DoEvents();
            log.Append("== banner: Add 1 to addon (confirm: " + string.Join(" || ", f.testConfirms.ToArray()) + ")\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-3-added.png"));
            f.testConfirms.Clear();
            log.Append("== publish dialog after adding: ").Append(f.TestPublish()).Append('\n');
            f.TestBannerAlt(); Application.DoEvents();
            log.Append("== banner: Undo (confirm: " + string.Join(" || ", f.testConfirms.ToArray()) + ")\n").Append(f.TestDump());
            f.testConfirms.Clear();
            f.TestSelectStatus(ChainRow.BuiltIn); Application.DoEvents();
            log.Append("== many selected\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-4-multi.png"));
            f.TestView("table"); Application.DoEvents();
            f.TestSort(5); Application.DoEvents();
            log.Append("== table view sorted by Steps\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-5-table.png"));
            f.TestSort(5); f.TestSort(5);
            f.TestView("notebook");
            f.TestSize(900, 520); Application.DoEvents();
            log.Append("== small window (compact banner)\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-6-compact.png"));
            f.TestSize(1024, 640);
            log.Append("== addon check (good install)\n").Append(f.TestChecker(Path.Combine(shots, "shot-9-checker-good.png"))).Append('\n');

            // ---- local sharing: two other players' data arrives (as if over the network) ----
            Func<string, int, string, string, int, string> row = (kind, id, name, faction, quality) =>
            {
                var s = new Starter();
                s.F["kind"] = kind; s.F["id"] = (double)id; s.F["map"] = 1429.0; s.F["x"] = 0.3; s.F["y"] = 0.4;
                s.F["name"] = name; s.F["rootQuest"] = (double)(id + 1); s.F["rootTitle"] = "First of " + name; s.F["finalTitle"] = "Last of " + name;
                s.F["quality"] = (double)quality; s.F["steps"] = 3.0;
                if (faction != null) s.F["faction"] = faction;
                return Starter.Encode(new[] { s }).Split('\n')[1];
            };
            Func<string, string, PlayerStats, string[], string> payload = (id, nick, st, rs) =>
                Profile.Build(id, nick, st, Starter.Decode("QGS1\n" + string.Join("\n", rs)).Values);
            f.TestPeer(payload("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Hordie", new PlayerStats { Gear = 30, Dungeon = 6, Discoveries = 4, QuestsSeen = 400, Longest = 7, Zones = 12, Factions = "H", Known = 40, Possible = 330 },
                new[] { row("C", 90001, "Horde Quartermaster", "H", 3), row("C", 90002, "Neutral Hermit", null, 2) }));
            f.TestPeer(payload("cccccccccccccccccccccccccccccccc", "Allyson", new PlayerStats { Gear = 3, Factions = "A", Known = 3, Possible = 320 },
                new[] { row("C", 90003, "Alliance Captain", "A", 2) }));
            Application.DoEvents();
            log.Append("== two players shared (you play Alliance? factions known: " + f.TestFactions() + ")\n").Append(f.TestDump());
            log.Append("== Shared.lua for the game: ").Append(f.TestSharedFile()).Append('\n');
            Shot(f, Path.Combine(shots, "shot-11-shared.png"));
            log.Append("== players view\n").Append(f.TestPlayers());
            Shot(f, Path.Combine(shots, "shot-12-players.png"));
            f.TestDialogShot(f.BuildSharingDialog(), Path.Combine(shots, "shot-13-sharing.png"));
            f.TestDialogShot(f.BuildPublishDialog(), Path.Combine(shots, "shot-14-publish.png"));
            f.TestDialogShot(Theme.BuildBox("Built QuestGearScout-1.9.1.zip (11 files).\n\nUpload it on CurseForge: your project > Files > Upload File.\n\nOpen the folder?",
                "QuestGearScout Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Information), Path.Combine(shots, "shot-15-message.png"));
            f.TestView("notebook");
            f.TestFaction("H"); Application.DoEvents();
            log.Append("== faction filter: Horde only\n").Append(f.TestDump());
            f.TestFaction("all");
            f.TestSetGame(Path.Combine(gameDir, "does-not-exist")); Application.DoEvents();
            log.Append("== missing folder (scan running)\n").Append(f.TestDump());
            f.TestWaitScan();
            log.Append("== missing folder (after background scan)\n").Append(f.TestDump());
            Shot(f, Path.Combine(shots, "shot-7-firstrun.png"));
            string noAddon = Path.Combine(Path.GetDirectoryName(gameDir), "_noaddon_");
            if (Directory.Exists(noAddon))
            {
                f.TestSetGame(noAddon); Application.DoEvents();
                f.TestWaitScan();
                log.Append("== game folder without the addon (after background scan)\n").Append(f.TestDump());
                log.Append("== addon check there\n").Append(f.TestChecker(Path.Combine(shots, "shot-10-checker-noaddon.png"))).Append('\n');
                Shot(f, Path.Combine(shots, "shot-8-noaddon.png"));
            }
            f.TestSetGame(Path.Combine(gameDir, "Interface", "AddOns")); Application.DoEvents();
            log.Append("== picked Interface\\AddOns\n").Append(f.TestDump());
            f.Close();
            File.WriteAllText(outFile, log.ToString(), new UTF8Encoding(false));
            return 0;
        }
    }
}
