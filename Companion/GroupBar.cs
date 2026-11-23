// QuestGearScout Companion - the "Local group" sidebar on the right of the main window.
//
// Not in a group: the groups players on your network have made (click one and enter its code
// to join), "Join with a code..." and "Create group". In a group: its name, who's online (click a
// player to see and get the chains they shared: PlayerChains.cs) and the group's settings. Looking for groups only starts after you ask for it once (so Windows'
// firewall question never pops up on its own), and is remembered after that.
// When the window is too narrow the sidebar shrinks to a slim strip with a menu.
// C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace QGSCompanion
{
    public partial class MainForm
    {
        CardPanel groupBar;
        Panel groupGap;
        FlatBtn gbCreate, gbJoinCode, gbFind, gbSettings, gbLeave;
        ContextMenuStrip groupMenu;
        LanShare scout;                                        // listen-only: finds groups while you're not in one
        readonly GroupDirectory nearby = new GroupDirectory();
        List<NearbyGroup> gbRows = new List<NearbyGroup>();    // groups as last drawn, with where they were drawn
        List<Rectangle> gbRowRects = new List<Rectangle>();
        List<PeerData> gbPeers = new List<PeerData>();          // players in your group as last drawn (click one for their chains)
        List<Rectangle> gbPeerRects = new List<Rectangle>();
        int gbPeerHover = -1;
        Rectangle gbMoreRect;                                    // the "+N more" line under the players
        List<PeerData> gbMorePeers = new List<PeerData>();
        bool gbMoreHover;
        List<string> gbDump = new List<string>();              // what was drawn, for the self-test
        Rectangle gbToggleRect;
        int gbHover = -1;
        bool gbForcedRail, gbShort, gbScanFailed;

        const int GroupBarWide = 216, GroupBarRail = 44, GroupBarGap = 12, BodyMinWidth = 700, GroupBarMinHeight = 330;

        string GroupName { get { string v; return settings.TryGetValue("groupName", out v) ? Profile.CleanGroupName(v) : ""; } }
        bool InGroup { get { return SharingOn && Nick != "" && GroupCode.Trim().Length >= 4; } }
        bool ScanOn { get { string v; return settings.TryGetValue("findGroups", out v) && v == "1"; } }
        bool GroupBarOpen { get { string v; return !settings.TryGetValue("groupBar", out v) || v != "0"; } }
        // Your group's name; until its players confirm one (signed), the name it shows in the list
        // nearby. That one is only displayed: it's never saved or announced.
        string GroupTitle
        {
            get
            {
                if (GroupName != "") return GroupName;
                string mine = GroupCode.Trim() == "" ? null : Profile.GroupHash(GroupCode);
                var seen = mine == null ? null : nearby.Snapshot(DateTime.UtcNow).FirstOrDefault(x => x.Hash == mine && x.Name != null);
                return seen != null ? seen.Name : "Your group";
            }
        }
        // When the group's name was chosen (unix time); a newer name signed by a member replaces it.
        long GroupNamedAt { get { string v; long t; return settings.TryGetValue("groupNamedAt", out v) && long.TryParse(v, out t) && t > 0 ? t : 0; } }

        void SetGroupNameSetting(string name, long namedAt)
        {
            settings["groupName"] = Profile.CleanGroupName(name);
            settings["groupNamedAt"] = Math.Max(0, namedAt).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Another group nearby (one with a different code) already shows this name.
        bool NameTakenNearby(string name, string code)
        {
            return nearby.Snapshot(DateTime.UtcNow).Any(x => x.Name != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                && !Profile.CodeMatches(code, x.Hash));
        }
        bool GroupBarIsRail { get { return groupBar != null && groupBar.Width < S(GroupBarWide) / 2; } }

        static string GroupLabel(NearbyGroup g) { return g.Name ?? "Unnamed group"; }

        // ------------------------------------------------------------------
        // Building and layout
        // ------------------------------------------------------------------
        void BuildGroupBar()
        {
            groupBar = new CardPanel { Dock = DockStyle.Right, Width = S(GroupBarWide), Padding = new Padding(1) };
            groupBar.Paint += PaintGroupBar;
            groupBar.MouseMove += GroupBarMouseMove;
            groupBar.MouseLeave += (s, e) => { if (gbHover != -1 || gbPeerHover != -1) { gbHover = -1; gbPeerHover = -1; groupBar.Invalidate(); } };
            groupBar.MouseClick += GroupBarClick;
            groupBar.Resize += (s, e) => PlaceGroupButtons();
            groupGap = new Panel { Dock = DockStyle.Right, Width = S(GroupBarGap), BackColor = Pal.Window };

            gbCreate = new FlatBtn { Kind = "primary", Text = "Create group", Height = S(32), Font = fBold };
            gbCreate.Click += (s, e) => Run("Create group", ShowCreateGroup);
            tips.SetToolTip(gbCreate, "Start a group for players on your network. You pick its name and code.");
            gbJoinCode = new FlatBtn { Kind = "ghost", Text = "Join with a code...", Height = S(30), Font = fBody };
            gbJoinCode.Click += (s, e) => Run("Join group", () => ShowJoinGroup(null));
            tips.SetToolTip(gbJoinCode, "Join a group that isn't in the list (for example when nobody in it is online).");
            gbFind = new FlatBtn { Kind = "secondary", Text = "Look for groups", IconName = "search", Height = S(30), Font = fBody };
            gbFind.Click += (s, e) => Run("Look for groups", FindGroups);
            tips.SetToolTip(gbFind, "Listen on your network for groups other players made. Nothing is sent.");
            gbSettings = new FlatBtn { Kind = "secondary", Text = "Group settings...", Height = S(30), Font = fBody };
            gbSettings.Click += (s, e) => Run("Sharing", ShowSharing);
            tips.SetToolTip(gbSettings, "Nickname, code, forgetting players and Sync now");
            gbLeave = new FlatBtn { Kind = "ghost", Text = "Leave group", Height = S(30), Font = fBody };
            gbLeave.Click += (s, e) => Run("Leave group", LeaveGroup);
            groupBar.Controls.AddRange(new Control[] { gbCreate, gbJoinCode, gbFind, gbSettings, gbLeave });

            groupMenu = new ContextMenuStrip();
            Theme.Strip(groupMenu);

            // Docked right of the chain list and detail pane (the split fills what's left).
            splitHost.Controls.Add(groupGap);
            splitHost.Controls.Add(groupBar);
            // The width is set in the host's Layout event, which runs before docking places the
            // controls; changing it anywhere else would leave the panel at its old position.
            splitHost.Layout += (s, e) => SizeGroupBar();
            LayoutGroupBar();
        }

        void RefreshGroupBar() { if (groupBar != null) LayoutGroupBar(); }

        // Full width, or a slim strip when tucked away or when the window is too narrow
        // (the chain list and detail pane keep at least BodyMinWidth).
        void SizeGroupBar()
        {
            int avail = splitHost.ClientSize.Width - splitHost.Padding.Horizontal;
            int availH = splitHost.ClientSize.Height - splitHost.Padding.Vertical;
            // Too narrow for the chain list next to it, or too short for the list above the buttons.
            gbForcedRail = (avail > 0 && avail - S(GroupBarWide) - S(GroupBarGap) < S(BodyMinWidth)) || (availH > 0 && availH < S(GroupBarMinHeight));
            int w = GroupBarOpen && !gbForcedRail ? S(GroupBarWide) : S(GroupBarRail);
            if (groupBar.Width != w) groupBar.Width = w;
        }

        // After something changed (joined, left, tucked away...): size, re-dock, place the buttons.
        void LayoutGroupBar()
        {
            if (groupBar == null) return;
            int before = groupBar.Width;
            SizeGroupBar();
            if (groupBar.Width != before) splitHost.PerformLayout();
            PlaceGroupButtons();
        }

        void PlaceGroupButtons()
        {
            bool open = !GroupBarIsRail;
            int w = groupBar.Width;
            int pad = S(14), bw = w - pad * 2, bottom = groupBar.ClientSize.Height - pad;
            gbShort = groupBar.ClientSize.Height < S(330);
            bool inGroup = InGroup;
            SetShown(gbCreate, open && !inGroup);
            SetShown(gbJoinCode, open && !inGroup);
            SetShown(gbFind, open && !inGroup && (!ScanOn || gbScanFailed));
            string findText = gbScanFailed ? "Try again" : "Look for groups";
            if (gbFind.Text != findText) gbFind.Text = findText;
            SetShown(gbSettings, open && inGroup);
            SetShown(gbLeave, open && inGroup);
            if (open)
            {
                foreach (var b in new[] { gbCreate, gbJoinCode, gbFind, gbSettings, gbLeave }) if (b.Width != bw) b.Width = bw;
                gbCreate.Location = new Point(pad, bottom - gbCreate.Height);
                gbJoinCode.Location = new Point(pad, gbCreate.Top - S(6) - gbJoinCode.Height);
                gbLeave.Location = new Point(pad, bottom - gbLeave.Height);
                gbSettings.Location = new Point(pad, gbLeave.Top - S(6) - gbSettings.Height);
                // Under the "Groups nearby" heading (and its explanation when there's room for it).
                gbFind.Location = new Point(pad, gbShort ? S(96) : S(148));
            }
            groupBar.Invalidate();
        }

        static void SetShown(Control c, bool visible) { if (c.Visible != visible) c.Visible = visible; }

        void SetGroupBarOpen(bool open)
        {
            settings["groupBar"] = open ? "1" : "0";
            SaveSettings();
            LayoutGroupBar();
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------
        void PaintGroupBar(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            gbDump.Clear();
            gbRows = new List<NearbyGroup>();
            gbRowRects = new List<Rectangle>();
            gbPeers = new List<PeerData>();
            gbPeerRects = new List<Rectangle>();
            gbMoreRect = Rectangle.Empty;
            gbToggleRect = Rectangle.Empty;
            int w = groupBar.Width, pad = S(14);
            bool inGroup = InGroup;

            if (GroupBarIsRail)
            {
                Draw.Icon(g, "group", new RectangleF((w - S(20)) / 2f, S(14), S(20), S(20)), Pal.Accent);
                Color dot = inGroup ? (lan != null && lan.OnlineCount > 0 ? Pal.SuccessDot : Pal.Accent)
                                    : (scout != null && nearby.Snapshot(DateTime.UtcNow).Count > 0 ? Pal.Trim : Pal.Muted);
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, (w - S(8)) / 2f, S(42), S(8), S(8));
                if (!gbForcedRail) Draw.Icon(g, "chevron-left", new RectangleF((w - S(16)) / 2f, S(58), S(16), S(16)), Pal.Sub);
                gbDump.Add("strip: " + (inGroup ? "in " + GroupTitle : "not in a group"));
                return;
            }

            // Heading, with the arrow that tucks the panel away.
            Draw.Icon(g, "group", new RectangleF(pad, S(14), S(18), S(18)), Pal.Accent);
            Draw.Text(g, "Local group", fHead, new Rectangle(pad + S(24), S(11), w - pad * 2 - S(46), S(24)), Pal.Text, TextFormatFlags.VerticalCenter);
            gbToggleRect = new Rectangle(w - pad - S(20), S(13), S(20), S(20));
            Draw.Icon(g, "chevron-right", gbToggleRect, Pal.Sub);
            string sub = inGroup ? (lan != null ? "Sharing on your network" : "Not connected right now") : "You're not in a group yet";
            Draw.Text(g, sub, fSmall, new Rectangle(pad, S(38), w - pad * 2, S(16)), Pal.Sub, TextFormatFlags.Top);
            gbDump.Add("heading: Local group / " + sub);
            using (var line = new Pen(Pal.CardBorder)) g.DrawLine(line, pad, S(62), w - pad, S(62));
            int y = S(72);

            if (inGroup) PaintMyGroup(g, w, pad, y);
            else PaintNearby(g, w, pad, y);
        }

        void PaintNearby(Graphics g, int w, int pad, int y)
        {
            Draw.Text(g, "Groups nearby", fSmallBold, new Rectangle(pad, y, w - pad * 2, S(18)), Pal.Sub, TextFormatFlags.Top);
            y += S(22);
            int limit = gbJoinCode.Top - S(10);
            g.SetClip(new Rectangle(0, 0, w, Math.Max(0, limit)));   // never under the buttons
            if (!ScanOn || gbScanFailed)
            {
                string text = gbScanFailed ? "Couldn't listen for groups on your network (another program may be using it)."
                                           : "See the groups players on your network have made. Click one and enter its code to join.";
                if (!gbShort || gbScanFailed)
                    Draw.Text(g, text, fSmall, new Rectangle(pad, y, w - pad * 2, S(50)), gbScanFailed ? Pal.Warn : Pal.Muted, TextFormatFlags.WordBreak | TextFormatFlags.Top);
                gbDump.Add("nearby: " + (gbScanFailed ? "couldn't listen" : "not looking yet") + " <" + gbFind.Text + ">");
                return;
            }
            var list = nearby.Snapshot(DateTime.UtcNow);
            if (list.Count == 0)
            {
                Draw.Text(g, "Looking... No groups on your network right now. Create one, or ask a friend to.", fSmall,
                    new Rectangle(pad, y, w - pad * 2, Math.Max(S(18), Math.Min(S(50), limit - y))), Pal.Muted, TextFormatFlags.WordBreak | TextFormatFlags.Top);
                gbDump.Add("nearby: none yet");
                return;
            }
            int rowH = S(46), step = rowH + S(6);
            int show = RowsThatFit(list.Count, limit - y, step, S(6), S(18));
            for (int i = 0; i < list.Count; i++)
            {
                var grp = list[i];
                if (i >= show)
                {
                    int more = list.Count - i;
                    if (y + S(18) <= limit)
                        Draw.Text(g, "+" + more + " more (make the window taller)", fSmall, new Rectangle(pad, y, w - pad * 2, S(18)), Pal.Muted, TextFormatFlags.Top);
                    gbDump.Add("  +" + more + " more");
                    break;
                }
                var r = new Rectangle(pad - S(4), y, w - pad * 2 + S(8), rowH);
                bool hover = gbHover == gbRows.Count;
                Draw.Card(g, r, hover ? Pal.Hover : Pal.Window, hover ? Pal.AccentBorder : Pal.CardBorder, S(6));
                Draw.Icon(g, "lock", new RectangleF(r.X + S(10), r.Y + (rowH - S(16)) / 2f, S(16), S(16)), Pal.Trim);
                int tx = r.X + S(34), tw = r.Width - S(34) - S(26);
                Draw.Text(g, GroupLabel(grp), grp.Name != null ? fBold : fBody, new Rectangle(tx, r.Y + S(6), tw, S(18)), grp.Name != null ? Pal.Text : Pal.Sub, TextFormatFlags.Top);
                using (var b = new SolidBrush(Pal.SuccessDot)) g.FillEllipse(b, tx, r.Y + S(29), S(6), S(6));
                Draw.Text(g, Plural(grp.Online, "player") + " online", fSmall, new Rectangle(tx + S(11), r.Y + S(25), tw - S(11), S(16)), Pal.Sub, TextFormatFlags.Top);
                Draw.Icon(g, "chevron-right", new RectangleF(r.Right - S(22), r.Y + (rowH - S(14)) / 2f, S(14), S(14)), hover ? Pal.Accent : Pal.Muted);
                gbRows.Add(grp);
                gbRowRects.Add(r);
                gbDump.Add("  [" + GroupLabel(grp) + "] " + Plural(grp.Online, "player") + " online");
                y += rowH + S(6);
            }
        }

        // How many rows to draw in `room` pixels: all of them if they fit, otherwise as many as fit
        // above a "+N more" line (at least one, if one fits at all).
        static int RowsThatFit(int count, int room, int step, int gapAfterLast, int moreLine)
        {
            int all = room <= 0 ? 0 : (room + gapAfterLast) / step;
            if (count <= all) return count;
            int withMore = room - moreLine <= 0 ? 0 : (room - moreLine) / step;
            return Math.Max(Math.Min(1, all), withMore);
        }

        void PaintMyGroup(Graphics g, int w, int pad, int y)
        {
            // The group card: name, who's online, your nickname.
            var card = new Rectangle(pad - S(4), y, w - pad * 2 + S(8), S(70));
            Draw.Card(g, card, Pal.AccentBg, Pal.AccentBorder, S(6));
            Draw.Text(g, GroupTitle, fHead, new Rectangle(card.X + S(12), card.Y + S(7), card.Width - S(24), S(22)), Pal.Trim, TextFormatFlags.Top);
            int online = lan != null ? lan.OnlineCount : 0;
            string status = lan == null ? "Not connected" : online == 0 ? "Waiting for others" : Plural(online, "other player") + " online";
            Color dot = lan != null && online > 0 ? Pal.SuccessDot : lan != null ? Pal.Accent : Pal.Muted;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, card.X + S(12), card.Y + S(36), S(7), S(7));
            Draw.Text(g, status, fSmall, new Rectangle(card.X + S(24), card.Y + S(32), card.Width - S(36), S(16)), Pal.Text, TextFormatFlags.Top);
            Draw.Text(g, "You: " + Nick, fSmall, new Rectangle(card.X + S(12), card.Y + S(50), card.Width - S(24), S(16)), Pal.Sub, TextFormatFlags.Top);
            gbDump.Add("group: " + GroupTitle + " | " + status + " | You: " + Nick);
            y = card.Bottom + S(12);

            int limit = gbSettings.Top - S(10);
            g.SetClip(new Rectangle(0, 0, w, Math.Max(0, limit)));   // never under the buttons
            Draw.Text(g, "Players", fSmallBold, new Rectangle(pad, y, w - pad * 2, S(18)), Pal.Sub, TextFormatFlags.Top);
            if (peers.Values.Any(p => p.Group == Profile.GroupHash(GroupCode)))
                Draw.Text(g, "click for chains", fSmall, new Rectangle(pad, y, w - pad * 2, S(18)), Pal.Muted, TextFormatFlags.Top | TextFormatFlags.Right);
            y += S(22);
            // Only players heard in this group (earlier groups' players are still in the Players view).
            string mine = Profile.GroupHash(GroupCode);
            var others = peers.Values.Where(p => p.Group == mine).Select(p => new { P = p, On = PeerOnline(p.Id), Seen = PeerSeen(p) })
                // Online players by name (a steady order, so a row doesn't move under the mouse), then the rest by when last seen.
                .OrderByDescending(x => x.On).ThenByDescending(x => x.On ? DateTime.MinValue : x.Seen)
                .ThenBy(x => x.P.Nick, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.P.Id, StringComparer.Ordinal).ToList();
            if (others.Count == 0)
            {
                Draw.Text(g, "Nobody else yet. Give friends on your network the group's code so they can join.", fSmall,
                    new Rectangle(pad, y, w - pad * 2, Math.Max(S(18), Math.Min(S(50), limit - y))), Pal.Muted, TextFormatFlags.WordBreak | TextFormatFlags.Top);
                gbDump.Add("players: none yet");
                return;
            }
            // Each player is a row you can click to see (and get) the chains they shared.
            int rowH = S(26);
            int show = RowsThatFit(others.Count, limit - y, rowH, 0, S(18));
            for (int i = 0; i < others.Count; i++)
            {
                var o = others[i];
                if (i >= show)
                {
                    int more = others.Count - i;
                    if (y + S(18) <= limit)
                    {
                        // Click for a menu of the players that didn't fit.
                        gbMoreRect = new Rectangle(pad - S(6), y, w - pad * 2 + S(12), S(18));
                        gbMorePeers = others.Skip(i).Select(x => x.P).ToList();
                        Draw.Text(g, "+" + more + " more (click)", fSmall, new Rectangle(pad, y, w - pad * 2, S(18)), gbMoreHover ? Pal.Accent : Pal.Muted, TextFormatFlags.Top);
                    }
                    gbDump.Add("  +" + more + " more");
                    break;
                }
                var r = new Rectangle(pad - S(6), y, w - pad * 2 + S(12), rowH - S(2));
                bool hover = gbPeerHover == gbPeers.Count;
                if (hover) Draw.Card(g, r, Pal.Hover, Pal.AccentBorder, S(5));
                int ty = r.Y + (r.Height - S(18)) / 2;
                using (var b = new SolidBrush(o.On ? Pal.SuccessDot : Pal.Muted)) g.FillEllipse(b, pad, ty + S(6), S(7), S(7));
                int n = OfferCount(o.P);
                string when = o.On ? "online" : Ago(o.Seen);
                int rightEdge = w - pad;
                int tagW;
                if (n > 0)
                    tagW = Draw.Pill(g, OfferWord(n), fSmall, new Point(rightEdge, ty + S(1)), Pal.AccentBg, Pal.AccentBorder, Pal.Accent, S(6), S(16), true);
                else
                {
                    tagW = Draw.Width(g, when, fSmall);
                    Draw.Text(g, when, fSmall, new Rectangle(rightEdge - tagW, ty + S(1), tagW + S(2), S(18)), Pal.Muted, TextFormatFlags.Top);
                }
                Draw.Text(g, o.P.Nick, fBody, new Rectangle(pad + S(14), ty, rightEdge - tagW - S(6) - pad - S(14), S(18)), o.On ? Pal.Text : Pal.Sub, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
                gbPeers.Add(o.P);
                gbPeerRects.Add(r);
                gbDump.Add("  " + o.P.Nick + " - " + when + (n > 0 ? " - " + OfferWord(n) : ""));
                y += rowH;
            }
        }

        // ------------------------------------------------------------------
        // Mouse
        // ------------------------------------------------------------------
        void GroupBarMouseMove(object sender, MouseEventArgs e)
        {
            int hov = -1;
            for (int i = 0; i < gbRowRects.Count; i++) if (gbRowRects[i].Contains(e.Location)) { hov = i; break; }
            int phov = -1;
            for (int i = 0; i < gbPeerRects.Count; i++) if (gbPeerRects[i].Contains(e.Location)) { phov = i; break; }
            bool rail = GroupBarIsRail, overToggle = gbToggleRect.Contains(e.Location), overMore = gbMoreRect.Contains(e.Location);
            groupBar.Cursor = rail || hov >= 0 || phov >= 0 || overToggle || overMore ? Cursors.Hand : Cursors.Default;
            if (hov != gbHover || phov != gbPeerHover || overMore != gbMoreHover) { gbHover = hov; gbPeerHover = phov; gbMoreHover = overMore; groupBar.Invalidate(); }
            string tip = rail ? (gbForcedRail ? "Local group (click for options; make the window bigger to see the panel)" : "Show the Local group panel")
                : overToggle ? "Tuck this panel away"
                : hov >= 0 ? "Join " + GroupLabel(gbRows[hov]) + " - you'll need its code"
                : phov >= 0 && phov < gbPeers.Count ? PeerTip(gbPeers[phov]) : "";
            if (tips.GetToolTip(groupBar) != tip) tips.SetToolTip(groupBar, tip);
        }

        void GroupBarClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (GroupBarIsRail)
            {
                if (gbForcedRail) ShowGroupMenu(e.Location);
                else SetGroupBarOpen(true);
                return;
            }
            if (gbToggleRect.Contains(e.Location)) { SetGroupBarOpen(false); return; }
            for (int i = 0; i < gbRowRects.Count && i < gbRows.Count; i++)
                if (gbRowRects[i].Contains(e.Location))
                {
                    var grp = gbRows[i];
                    Run("Join group", () => ShowJoinGroup(grp));
                    return;
                }
            for (int i = 0; i < gbPeerRects.Count && i < gbPeers.Count; i++)
                if (gbPeerRects[i].Contains(e.Location))
                {
                    var p = gbPeers[i];
                    Run("Chains from " + p.Nick, () => ShowPlayerChains(p));
                    return;
                }
            if (gbMoreRect.Contains(e.Location))
            {
                groupMenu.Items.Clear();
                AddPlayerItems(gbMorePeers);
                groupMenu.Show(groupBar, e.Location);
            }
        }

        // Menu items opening each player's chains.
        void AddPlayerItems(IEnumerable<PeerData> list)
        {
            foreach (var p in list)
            {
                var p2 = p;
                int n = OfferCount(p2);
                string text = p2.Nick + "'s chains" + (n > 0 ? " (" + OfferWord(n) + ")" : "") + "...";
                var it = new ToolStripMenuItem(text);
                it.Click += (s, e) => Run("Chains from " + p2.Nick, () => ShowPlayerChains(p2));
                groupMenu.Items.Add(it);
            }
        }

        string PeerTip(PeerData p)
        {
            int n = OfferCount(p);
            return n > 0 ? "See " + p.Nick + "'s chains - " + n + " you don't have yet (click to get them)" : "See the chains " + p.Nick + " shared";
        }

        // The slim strip's menu (when the window is too narrow for the panel).
        void ShowGroupMenu(Point at)
        {
            groupMenu.Items.Clear();
            Action<string, Action> add = (text, act) =>
            {
                var it = new ToolStripMenuItem(text);
                it.Click += (s, e) => Run(text.TrimEnd('.'), act);
                groupMenu.Items.Add(it);
            };
            if (InGroup)
            {
                groupMenu.Items.Add(new ToolStripLabel(GroupTitle) { Font = fSmallBold, ForeColor = Pal.Sub });
                string mine = Profile.GroupHash(GroupCode);
                AddPlayerItems(peers.Values.Where(x => x.Group == mine).OrderBy(x => x.Nick, StringComparer.CurrentCultureIgnoreCase).ToList());
                groupMenu.Items.Add(new ToolStripSeparator());
                add("Group settings...", ShowSharing);
                add("Restore points...", ShowRestorePoints);
                add("Leave group", LeaveGroup);
            }
            else
            {
                add("Create group...", ShowCreateGroup);
                var list = scout != null || uiTest ? nearby.Snapshot(DateTime.UtcNow) : new List<NearbyGroup>();
                if (list.Count > 0)
                {
                    groupMenu.Items.Add(new ToolStripSeparator());
                    groupMenu.Items.Add(new ToolStripLabel("Groups nearby") { Font = fSmallBold, ForeColor = Pal.Sub });
                    foreach (var grp in list.Take(12))
                    {
                        var g2 = grp;
                        add("Join " + GroupLabel(g2) + " (" + g2.Online + " online)...", () => ShowJoinGroup(g2));
                    }
                }
                groupMenu.Items.Add(new ToolStripSeparator());
                add("Join with a code...", () => ShowJoinGroup(null));
                if (!ScanOn || gbScanFailed) add(gbScanFailed ? "Look for groups again" : "Look for groups", FindGroups);
            }
            groupMenu.Show(groupBar, at);
        }

        // ------------------------------------------------------------------
        // Looking for groups (listen only)
        // ------------------------------------------------------------------
        void FindGroups()
        {
            settings["findGroups"] = "1";
            SaveSettings();
            StartScout();
            if (!gbScanFailed) Msg("Looking for groups on your network. If Windows asks, allow the app on private networks.", false);
            LayoutGroupBar();
        }

        void StartScout()
        {
            StopScout();
            gbScanFailed = false;
            if (uiTest || InGroup || !ScanOn) return;
            try
            {
                scout = new LanShare(PeerId);
                scout.GroupHeard += OnGroupHeard;
                scout.Start();
            }
            catch (Exception ex)
            {
                // Shown in the panel with a "Try again" button.
                StopScout();
                gbScanFailed = true;
                Msg("Couldn't look for groups on your network: " + ex.Message, true);
            }
        }

        void StopScout()
        {
            if (scout != null) { try { scout.Dispose(); } catch { } scout = null; }
        }

        // Background thread: a beacon or group name was heard.
        void OnGroupHeard(string hash, string member, string name, long namedAt, bool signed)
        {
            bool news = nearby.Heard(hash, member, name, namedAt, DateTime.UtcNow);
            if (signed && name != null) OnUi(() => AdoptGroupName(hash, name, namedAt));
            else if (news) OnUi(() => { if (groupBar != null) groupBar.Invalidate(); });
        }

        // A name signed with your group's code (so it's from a member): take it if you have none, or
        // if it's newer than yours (someone renamed the group). Ties go to the same name everywhere.
        void AdoptGroupName(string hash, string name, long namedAt)
        {
            name = Profile.CleanGroupName(name);
            if (groupBar != null) groupBar.Invalidate();
            if (lan == null || lan.Group != hash || !InGroup || name == "") return;
            long mine = GroupNamedAt;
            if (name == GroupName)
            {
                // Same name, chosen again later (renamed away and back): keep the newer time, quietly,
                // so an older rename heard later can't win over it.
                if (namedAt > mine) { SetGroupNameSetting(name, namedAt); SaveSettings(); lan.SetGroupName(GroupName, GroupNamedAt); }
                return;
            }
            bool take = GroupName == "" || namedAt > mine || (namedAt == mine && string.CompareOrdinal(name, GroupName) > 0);
            if (!take) return;
            bool had = GroupName != "";
            SetGroupNameSetting(name, namedAt);
            SaveSettings();
            lan.SetGroupName(GroupName, GroupNamedAt);
            Msg(had ? "Your group is now called " + GroupName + "." : "Your group is called " + GroupName + ".", false);
        }

        // ------------------------------------------------------------------
        // Join, create, leave
        // ------------------------------------------------------------------
        void ShowJoinGroup(NearbyGroup grp)
        {
            using (var f = BuildJoinDialog(grp))
            {
                dialogOwner = f;
                try { f.ShowDialog(this); } finally { dialogOwner = null; }
            }
            if (view == "players") FillPlayers();
        }

        void ShowCreateGroup()
        {
            using (var f = BuildCreateDialog())
            {
                dialogOwner = f;
                try { f.ShowDialog(this); } finally { dialogOwner = null; }
            }
            if (view == "players") FillPlayers();
        }

        internal Form BuildJoinDialog(NearbyGroup grp)
        {
            string label = grp == null ? "" : GroupLabel(grp);
            string title = grp == null ? "Join a group with its code" : "Join " + label;
            string intro = grp == null
                ? "Type the group's code (ask someone in the group for it). Its players show up once you're both online."
                : Plural(grp.Online, "player") + " online in this group. Enter the group's code to join - ask one of them if you don't have it.";
            return GroupDialog(grp == null ? "Join a group" : "Join group", title, intro, false, "Join", (name, nick, code) =>
            {
                if (nick == "") return "Pick a nickname first (it's what the others see).";
                if (code.Length < 4) return "Group codes have at least 4 characters.";
                if (grp != null && !Profile.CodeMatches(code, grp.Hash))
                    return "That code doesn't match " + (grp.Name != null ? grp.Name : "this group") + ". Check it with someone in the group and try again.";
                // The name shown in the list isn't signed, so it isn't kept: the group's real name
                // arrives from its players (signed with the code) a few seconds after joining.
                JoinGroup("", 0, nick, code);
                Msg("You joined " + (grp != null ? GroupLabel(grp) : "the group") + " as " + Nick + ". Looking for its players on your network...", false);
                return null;
            });
        }

        internal Form BuildCreateDialog()
        {
            return GroupDialog("Create group", "Create a local group",
                "Players nearby see the name; only people you give the code to can join. A strong code is filled in for you.",
                true, "Create", (name, nick, code) =>
                {
                    if (name == "") return "Give your group a name.";
                    if (nick == "") return "Pick a nickname first (it's what the others see).";
                    string weak = Profile.WeakCode(code);
                    if (weak != null) return weak + " Someone on your network could guess a short code.";
                    var list = nearby.Snapshot(DateTime.UtcNow);
                    var clash = list.FirstOrDefault(x => Profile.CodeMatches(code, x.Hash));
                    if (clash != null)
                        return "A group nearby" + (clash.Name != null ? " (" + clash.Name + ")" : "") + " already uses that code. Pick another code, or join that group instead.";
                    if (NameTakenNearby(name, code))
                        return "There's already a group called " + name + " on your network. Pick another name.";
                    JoinGroup(name, LanShare.UnixNow(), nick, code);
                    Msg("Created " + GroupTitle + ". Give your friends the code so they can join.", false);
                    return null;
                });
        }

        // A small dialog: optional group name, nickname and code. apply(name, nick, code) returns an
        // error to show, or null when it's done. Tag holds the same check for the self-test.
        Form GroupDialog(string caption, string title, string intro, bool askName, string okText, Func<string, string, string, string> apply)
        {
            var f = new Form { Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, BackColor = Pal.Card, ForeColor = Pal.Text, Font = fBody, ShowInTaskbar = false };
            int x0 = S(24), fx = S(156), width = S(560), y = S(16);
            var titleL = new Label { Text = title, Font = fBig, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(width - x0 * 2, S(30)) };
            y += S(36);
            var introL = new Label { Text = intro, ForeColor = Pal.Sub, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(width - x0 * 2, S(40)) };
            y += S(50);
            var controls = new List<Control> { titleL, introL };
            TextBox nameB = null;
            if (askName)
            {
                controls.Add(new Label { Text = "Group name:", AutoSize = true, Location = new Point(x0, y + S(3)) });
                nameB = new TextBox { Location = new Point(fx, y), Width = width - fx - x0, MaxLength = 24 };
                controls.Add(nameB);
                y += S(34);
            }
            controls.Add(new Label { Text = "Your nickname:", AutoSize = true, Location = new Point(x0, y + S(3)) });
            var nickB = new TextBox { Text = Nick, Location = new Point(fx, y), Width = width - fx - x0, MaxLength = 24 };
            controls.Add(nickB);
            y += S(34);
            controls.Add(new Label { Text = "Group code:", AutoSize = true, Location = new Point(x0, y + S(3)) });
            // Creating a group: a strong random code is filled in (four words and a number).
            var codeB = new TextBox { Location = new Point(fx, y), Width = width - fx - x0 - S(70), MaxLength = 60, UseSystemPasswordChar = !askName,
                Text = askName ? Profile.SuggestCode() : "" };
            var show = new CheckBox { Text = "Show", AutoSize = true, Checked = askName, Location = new Point(codeB.Right + S(10), y + S(2)) };
            show.CheckedChanged += (s, e) => codeB.UseSystemPasswordChar = !show.Checked;
            controls.Add(codeB); controls.Add(show);
            y += S(36);
            var err = new Label { ForeColor = Pal.Error, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(width - x0 * 2, S(36)) };
            controls.Add(err);
            y += S(38);
            controls.Add(new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(x0, y), Size = new Size(width - x0 * 2, S(34)),
                Text = "Windows may ask to let this app through its firewall the first time: allow it for private networks." });
            y += S(44);
            var ok = new FlatBtn { Kind = "primary", Text = okText, Size = new Size(S(100), S(30)), Font = fBold };
            var cancel = new FlatBtn { Text = "Cancel", Size = new Size(S(90), S(30)) };
            cancel.Location = new Point(width - x0 - cancel.Width, y);
            ok.Location = new Point(cancel.Left - S(8) - ok.Width, y);
            controls.Add(ok); controls.Add(cancel);
            f.ClientSize = new Size(width, y + S(30) + S(20));

            Func<string, string, string, string> attempt = (name, nick, code) =>
            {
                string problem = apply(Profile.CleanGroupName(name ?? ""), Profile.CleanNick(nick ?? ""), (code ?? "").Trim());
                err.Text = problem ?? "";
                return problem;
            };
            ok.Click += (s, e) =>
            {
                string problem = attempt(nameB != null ? nameB.Text : "", nickB.Text, codeB.Text);
                if (problem == null) { f.DialogResult = DialogResult.OK; f.Close(); }
                else if (problem.Contains("code")) { codeB.Focus(); codeB.SelectAll(); }
            };
            cancel.Click += (s, e) => f.Close();
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.AddRange(controls.ToArray());
            f.Tag = attempt;
            Theme.Form(f);
            f.Shown += (s, e) => { if (nameB != null) nameB.Focus(); else if (nickB.Text == "") nickB.Focus(); else codeB.Focus(); };
            return f;
        }

        // name/namedAt: the name you gave a new group, or "" and 0 when joining (the group's name then
        // comes from its players, signed with the code: see AdoptGroupName).
        void JoinGroup(string name, long namedAt, string nick, string code)
        {
            settings["shareOn"] = "1";
            settings["nick"] = Profile.CleanNick(nick);
            settings["groupCode"] = code.Trim();
            settings["codeSince"] = "1.10.2";   // set with this version (see StartSharing)
            SetGroupNameSetting(name, name == "" ? 0 : namedAt);
            SaveSettings();
            StartSharing();
            header.Invalidate();
        }

        void LeaveGroup()
        {
            if (!InGroup) return;
            if (!Confirm("Leave " + GroupTitle + "?\n\nYou'll stop sharing with its players. Chains and stats they already shared stay in the app. " +
                         "To remove them, open Publish > Local sharing... and use Forget player.")) return;
            string was = GroupTitle;
            settings["shareOn"] = "0";
            settings["groupCode"] = "";
            SetGroupNameSetting("", 0);
            SaveSettings();
            StartSharing();
            header.Invalidate();
            Msg("You left " + was + ".", false);
        }

        // ------------------------------------------------------------------
        // Self-test hooks
        // ------------------------------------------------------------------
        internal string TestGroupBar()
        {
            LayoutGroupBar();
            using (var bmp = new Bitmap(Math.Max(1, groupBar.Width), Math.Max(1, groupBar.Height)))
                groupBar.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            var sb = new System.Text.StringBuilder();
            sb.Append("  panel: ").Append(GroupBarIsRail ? (gbForcedRail ? "slim strip (window too small)" : "slim strip (tucked away)") : "open");
            // Docked at the right edge, with the chain list and detail pane to its left (not under it).
            bool placed = groupBar.Right == splitHost.ClientSize.Width - splitHost.Padding.Right && split.Right <= groupBar.Left && groupBar.Left - split.Right == groupGap.Width;
            sb.Append(placed ? " | placed right of the list" : " | MISPLACED (bar " + groupBar.Left + "-" + groupBar.Right + ", list ends " + split.Right + ", host " + splitHost.ClientSize.Width + ")").Append('\n');
            foreach (string l in gbDump) sb.Append("  ").Append(l).Append('\n');
            var btns = new[] { gbFind, gbJoinCode, gbCreate, gbSettings, gbLeave }.Where(b => b.Visible).ToList();
            sb.Append("  buttons: ").Append(btns.Count == 0 ? "none" : string.Join(", ", btns.Select(b => b.Text + "@" + b.Top).ToArray()));
            bool overlap = btns.Any(a => btns.Any(b => a != b && a.Bounds.IntersectsWith(b.Bounds)));
            sb.Append(overlap ? " | OVERLAP" : "").Append('\n');
            return sb.ToString();
        }

        // Pretends `players` apps of a group were heard on the network.
        internal void TestNearby(string code, string name, int players)
        {
            string hash = Profile.GroupHash(code);
            for (int i = 0; i < players; i++) nearby.Heard(hash, hash.Substring(0, 30) + i.ToString("x2"), name, 100, DateTime.UtcNow);
        }

        // What the panel shows when listening couldn't start (e.g. another program holds the port).
        internal string TestScanFailed()
        {
            gbScanFailed = true;
            string s = TestGroupBar();
            gbScanFailed = false;
            LayoutGroupBar();
            return s;
        }

        internal void TestFindGroups() { FindGroups(); }
        internal NearbyGroup TestNearbyGroup(string name) { return nearby.Snapshot(DateTime.UtcNow).FirstOrDefault(x => x.Name == name); }

        internal string TestDialog(Form f, string name, string nick, string code)
        {
            using (f)
            {
                string problem = ((Func<string, string, string, string>)f.Tag)(name, nick, code);
                return problem == null ? "done" : "refused: " + problem;
            }
        }

        internal string TestLeaveGroup()
        {
            testConfirms.Clear();
            LeaveGroup();
            string asked = string.Join(" || ", testConfirms.ToArray());
            testConfirms.Clear();
            return "asked: " + asked + " | now " + (InGroup ? "STILL IN " + GroupTitle : "not in a group");
        }
    }
}
