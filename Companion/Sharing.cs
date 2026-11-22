// QuestGearScout Companion - local sharing in the window: the Sharing dialog, shared chains in the
// list, faction labels and filter, writing Shared.lua for the addon, and the Players view
// (leaderboard, ranks, progress, achievements). C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QGSCompanion
{
    public partial class MainForm
    {
        LanShare lan;
        Dictionary<string, PeerData> peers = new Dictionary<string, PeerData>();
        PlayerStats myStats = new PlayerStats { IsMe = true };
        string lastProfile;
        ListBox playersList;
        FlatBtn sharingBtnFoot;
        PillToggle vPlayers;
        string factionView = "all";     // "all", "A", "H"
        Rectangle sharingPillRect;

        static readonly Color Alliance = Pal.Hex("#1F5FBF"), AllianceBg = Pal.Hex("#EAF1FC"), Horde = Pal.Hex("#B3261E"), HordeBg = Pal.Hex("#FCEDEC");
        static readonly Color SharedC = Pal.Hex("#7A4CC2"), SharedBg = Pal.Hex("#F3EEFB"), SharedBorder = Pal.Hex("#DCCDF3");

        bool SharingOn { get { string v; return settings.TryGetValue("shareOn", out v) && v == "1"; } }
        string Nick { get { string v; return settings.TryGetValue("nick", out v) ? Profile.CleanNick(v) : ""; } }
        string GroupCode { get { string v; return settings.TryGetValue("groupCode", out v) ? v : ""; } }

        string PeerId
        {
            get
            {
                string v;
                if (!settings.TryGetValue("peerId", out v) || !Profile.IsId(v)) { v = Guid.NewGuid().ToString("N"); settings["peerId"] = v; }
                return v;
            }
        }

        // ------------------------------------------------------------------
        // Start / stop
        // ------------------------------------------------------------------
        void InitSharing()
        {
            var fg = Forgotten();
            foreach (var p in SharedStore.LoadAll()) if (p.Id != PeerId && !fg.Contains(p.Id)) peers[p.Id] = p;
            if (peers.Count > 0) Reload();
            if (!uiTest) StartSharing();
        }

        void StartSharing()
        {
            StopSharing();
            if (!SharingOn || Nick == "" || GroupCode.Trim().Length < 4) { header.Invalidate(); return; }
            try
            {
                lan = new LanShare(PeerId, GroupCode);
                foreach (string id in Forgotten()) lan.Ignore(id);
                lan.PeerData += (p, raw) => OnUi(() => OnPeer(p, raw));
                lan.Problem += msg => OnUi(() => { if (!quietProblems) Msg(msg, true); });
                lastProfile = null;
                UpdateMyProfile();
                lan.Start();
                Msg("Sharing on as " + Nick + ". Looking for players on your network...", false);
            }
            catch (Exception ex)
            {
                StopSharing();
                Msg("Sharing couldn't start: " + ex.Message, true);
            }
            header.Invalidate();
        }

        bool quietProblems = true;   // one-off fetch errors (a player closing their app) aren't worth a warning

        void StopSharing()
        {
            if (lan != null) { try { lan.Dispose(); } catch { } lan = null; }
        }

        void OnUi(Action a)
        {
            if (!IsHandleCreated || IsDisposed || Disposing) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void OnPeer(PeerData p, string raw)
        {
            if (p.Id == PeerId || Forgotten().Contains(p.Id)) return;
            try { SharedStore.Save(p.Id, raw); } catch { }
            PeerData old;
            peers.TryGetValue(p.Id, out old);
            int before = old == null ? 0 : old.Chains.Count;
            peers[p.Id] = p;
            Reload();
            int added = p.Chains.Count - before;
            Msg(old == null ? p.Nick + " joined - " + Plural(p.Chains.Count, "chain") + " shared."
                : added > 0 ? p.Nick + " shared " + Plural(added, "new chain") + "." : p.Nick + "'s stats updated.", false);
        }

        // ------------------------------------------------------------------
        // Rows, factions, Shared.lua, your profile
        // ------------------------------------------------------------------
        // Your rows plus chains other players shared that you don't have and the addon doesn't ship.
        List<ChainRow> WithShared(List<ChainRow> mine)
        {
            var outp = new List<ChainRow>(mine);
            var have = new FactionSet();
            foreach (var r in mine) have.Add(r.Key, r.Faction);
            foreach (var p in peers.Values.OrderByDescending(x => x.LastSeen))
                foreach (var c in p.Chains.Values)
                    foreach (bool dun in new[] { false, true })
                    {
                        if (dun ? !c.HasDungeon : !c.HasGear) continue;
                        string key = c.Key + (dun ? "#D" : "#G"), f = ChainFaction(c);
                        // A Horde chain and an Alliance chain from the same giver are two different rows.
                        if (have.Covers(key, f)) continue;
                        Starter b = Starter.Find(addon.BuiltIn, c.Key, f), d = Starter.Find(addon.Discovered, c.Key, f);
                        if (b != null && (dun ? b.HasDungeon : b.HasGear)) continue;
                        bool added = d != null && (dun ? d.HasDungeon : d.HasGear);
                        have.Add(key, f);
                        outp.Add(new ChainRow { Starter = c, IsDungeon = dun, Status = added ? ChainRow.InDiscoveries : ChainRow.Shared, From = p.Nick, Faction = f });
                    }
            return outp;
        }

        static string ChainFaction(Starter c) { string f = c.Get("faction"); return f == "A" || f == "H" ? f : null; }

        // Which chain keys we already have, per faction ("untagged" covers every faction).
        class FactionSet
        {
            readonly Dictionary<string, List<string>> d = new Dictionary<string, List<string>>();
            public bool Covers(string key, string f)
            {
                List<string> l;
                return d.TryGetValue(key, out l) && l.Any(x => Starter.SameSide(x, f));
            }
            public void Add(string key, string f)
            {
                List<string> l;
                if (!d.TryGetValue(key, out l)) d[key] = l = new List<string>();
                l.Add(f);
            }
        }

        // Every chain any player shared that the addon doesn't ship, for the addon's Shared.lua.
        List<ChainRow> AllSharedRows()
        {
            var list = new List<ChainRow>();
            var have = new FactionSet();
            foreach (var p in peers.Values.OrderByDescending(x => x.LastSeen))
                foreach (var c in p.Chains.Values)
                    foreach (bool dun in new[] { false, true })
                    {
                        if (dun ? !c.HasDungeon : !c.HasGear) continue;
                        string key = c.Key + (dun ? "#D" : "#G"), f = ChainFaction(c);
                        Starter b = Starter.Find(addon.BuiltIn, c.Key, f);
                        if (b != null && (dun ? b.HasDungeon : b.HasGear)) continue;
                        if (have.Covers(key, f)) continue;
                        have.Add(key, f);
                        list.Add(new ChainRow { Starter = c, IsDungeon = dun, Faction = f });
                    }
            return list;
        }

        // After each reload: refresh Shared.lua for the game, and what we tell other players.
        void AfterReloadSharing()
        {
            if (addon.Found && AddonDir != "" && (peers.Count > 0 || File.Exists(Path.Combine(AddonDir, "Shared.lua"))))
            {
                try
                {
                    var shared = ChainRow.Pack(AllSharedRows(), null).Values;
                    if (AddonData.WriteShared(AddonDir, shared) && peers.Count > 0)
                        Msg(Msg_Shared(shared.Count()), false);
                }
                catch (Exception ex) { Msg("Couldn't update the shared chains for the game: " + ex.Message, true); }
            }
            UpdateMyProfile();
        }

        string Msg_Shared(int n) { return Plural(n, "shared chain") + " ready for the game - type /reload in game to load " + (n == 1 ? "it." : "them."); }

        List<ChainRow> MyRows() { return rows.Where(r => r.Status != ChainRow.Shared && r.From == null).ToList(); }

        void UpdateMyProfile()
        {
            myStats = PlayerStats.Compute(learned, addon, MyRows(), lastWrite, peers.Count);
            myStats.Id = PeerId; myStats.Nick = Nick == "" ? "You" : Nick;
            if (lan == null) return;
            // Chains learned before 1.9.0 have no faction. If you only play one faction they are
            // that faction's, so tag them before sharing (otherwise the other faction would see them).
            string only = learned.Factions != null && learned.Factions.Count == 1 ? learned.Factions.First() : null;
            var mine = MyRows().Where(r => r.Status == ChainRow.New || r.Status == ChainRow.InDiscoveries || r.Status == ChainRow.Unknown)
                .Select(r => r.Faction != null || only == null ? r
                    : new ChainRow { Starter = r.Starter, IsDungeon = r.IsDungeon, Status = r.Status, From = r.From, Faction = only })
                .ToList();
            string payload = Profile.Build(PeerId, Nick, myStats, ChainRow.Pack(mine, null).Values);
            if (payload == lastProfile) return;
            lastProfile = payload;
            lan.SetProfile(Nick, payload);
        }

        // Base rule: only chains for factions you play (neutral ones always). The pill narrows it further.
        bool FactionOk(ChainRow r)
        {
            if (!r.ForFactions(learned.Factions)) return false;
            if (factionView == "A") return r.Faction != "H";
            if (factionView == "H") return r.Faction != "A";
            return true;
        }

        static string FactionWord(string f) { return f == "A" ? "Alliance" : f == "H" ? "Horde" : ""; }

        // "Filters" button: gear quality and faction in one menu (keeps the toolbar uncluttered).
        FlatBtn filtersBtn;
        ContextMenuStrip filtersMenu;

        void BuildFilters()
        {
            filtersBtn = new FlatBtn { Kind = "secondary", Text = "Filters ▾", Height = S(28), Font = fBody };
            filtersBtn.Click += (s, e) => { BuildFiltersMenu(); filtersMenu.Show(filtersBtn, new Point(0, filtersBtn.Height)); };
            tips.SetToolTip(filtersBtn, "Gear quality and Horde / Alliance");
            filtersMenu = new ContextMenuStrip();
            toolbar.Controls.Add(filtersBtn);
            UpdateFactionPill();
        }

        void BuildFiltersMenu()
        {
            filtersMenu.Items.Clear();
            filtersMenu.Items.Add(new ToolStripLabel("Gear rewards") { Font = fSmallBold, ForeColor = Pal.Sub });
            string[] q = { "Uncommon and better", "Rare and better", "Epic and better" };
            for (int i = 0; i < q.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(q[i]) { Checked = qualityBox.SelectedIndex == i };
                it.Click += (s, e) => { qualityBox.SelectedIndex = idx; UpdateFactionPill(); };
                filtersMenu.Items.Add(it);
            }
            filtersMenu.Items.Add(new ToolStripSeparator());
            filtersMenu.Items.Add(new ToolStripLabel("Factions") { Font = fSmallBold, ForeColor = Pal.Sub });
            bool both = learned.Factions.Count != 1;
            foreach (var opt in new[] { new[] { "all", "Both factions" }, new[] { "A", "Alliance only" }, new[] { "H", "Horde only" } })
            {
                string key = opt[0];
                var it = new ToolStripMenuItem(opt[1]) { Checked = factionView == key, Enabled = both };
                it.Click += (s, e) => { factionView = key; UpdateFactionPill(); Fill(); SaveSettings(); };
                filtersMenu.Items.Add(it);
            }
            if (!both)
                filtersMenu.Items.Add(new ToolStripLabel("You only play " + FactionWord(learned.Factions.First()) + ", so only its chains are shown.")
                    { ForeColor = Pal.Sub, Font = fSmall });
        }

        void UpdateFactionPill()
        {
            if (filtersBtn == null) return;
            if (learned.Factions.Count == 1 && factionView != "all") factionView = "all";
            int active = (qualityBox.SelectedIndex > 0 ? 1 : 0) + (factionView != "all" ? 1 : 0);
            string t = active > 0 ? "Filters (" + active + ") ▾" : "Filters ▾";
            if (filtersBtn.Text != t) { filtersBtn.Text = t; filtersBtn.FitWidth(S(12), S(90)); }
            filtersBtn.Kind = active > 0 ? "primary" : "secondary";
            filtersBtn.Invalidate();
        }

        // Small faction label drawn at x; returns its width (0 for neutral).
        int DrawFaction(Graphics g, string f, int x, int y, int h, Font font)
        {
            if (f != "A" && f != "H") return 0;
            return Draw.Pill(g, FactionWord(f), font, new Point(x, y), f == "A" ? AllianceBg : HordeBg, f == "A" ? AllianceBg : HordeBg,
                f == "A" ? Alliance : Horde, S(6), h, false);
        }

        // ------------------------------------------------------------------
        // Players view
        // ------------------------------------------------------------------
        void BuildPlayers()
        {
            playersList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(58),
                IntegralHeight = false, BackColor = Color.White, Visible = false, Font = fBody };
            playersList.DrawItem += DrawPlayer;
            playersList.SelectedIndexChanged += (s, e) =>
            {
                // Back to the top only when a different player is picked, not when the list refreshes.
                var st = SelectedPlayer();
                string id = st == null ? null : st.Id;
                if (id != playersDetailFor) { playersDetailFor = id; detail.AutoScrollPosition = Point.Empty; }
                detail.Invalidate();
            };
            listCard.Controls.Add(playersList);
            sharingBtnFoot = new FlatBtn { Kind = "secondary", Height = S(32), Font = fBody, Visible = false };
            sharingBtnFoot.Click += (s, e) => Run("Sharing", ShowSharing);
            footer.Controls.Add(sharingBtnFoot);
        }

        List<PlayerStats> Leaderboard()
        {
            var list = new List<PlayerStats> { myStats };
            foreach (var p in peers.Values)
            {
                var st = p.Stats;
                st.Id = p.Id; st.Nick = p.Nick; st.LastSeen = p.LastSeen;
                list.Add(st);
            }
            return list.OrderByDescending(x => x.Points).ThenBy(x => x.Nick).ToList();
        }

        string playersDetailFor;

        // Worked out when drawn, so "online" and "seen 5 min ago" never go stale.
        bool PeerOnline(string id) { return lan != null && lan.IsOnline(id); }
        DateTime PeerSeen(PlayerStats st)
        {
            DateTime heard = lan != null ? lan.LastHeard(st.Id) : DateTime.MinValue;
            return heard > st.LastSeen ? heard : st.LastSeen;
        }
        DateTime PeerSeen(PeerData p)
        {
            DateTime heard = lan != null ? lan.LastHeard(p.Id) : DateTime.MinValue;
            return heard > p.LastSeen ? heard : p.LastSeen;
        }

        string PlayersCountText()
        {
            return Plural(playersList.Items.Count, "player") + (lan != null ? " - " + lan.OnlineCount + " online" : "");
        }

        // Players the user chose to forget (kept in settings so they stay forgotten).
        HashSet<string> Forgotten()
        {
            string v;
            settings.TryGetValue("forgotten", out v);
            return new HashSet<string>((v ?? "").Split(',').Where(Profile.IsId));
        }

        void SetForgotten(HashSet<string> ids)
        {
            settings["forgotten"] = string.Join(",", ids.ToArray());
            SaveSettings();
        }

        void FillPlayers()
        {
            string keep = playersList.SelectedItem is PlayerStats ? ((PlayerStats)playersList.SelectedItem).Id : PeerId;
            playersList.BeginUpdate();
            playersList.Items.Clear();
            foreach (var st in Leaderboard()) playersList.Items.Add(st);
            for (int i = 0; i < playersList.Items.Count; i++) if (((PlayerStats)playersList.Items[i]).Id == keep) { playersList.SelectedIndex = i; break; }
            if (playersList.SelectedIndex < 0 && playersList.Items.Count > 0) playersList.SelectedIndex = 0;
            playersList.EndUpdate();
            lblShowing.Text = PlayersCountText();
        }

        static Color RankColor(int level)
        {
            switch (level)
            {
                case 1: return Pal.Hex("#8A94A3");
                case 2: return Pal.Hex("#2DA44E");
                case 3: return Pal.Hex("#1F7AB8");
                case 4: return Pal.Hex("#6B4FBB");
                case 5: return Pal.Hex("#C9971C");
                case 6: return Pal.Hex("#C25E00");
                default: return Pal.Hex("#B3261E");
            }
        }

        void DrawRankBadge(Graphics g, Rank rank, Rectangle r, Font f)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(RankColor(rank.Level))) g.FillEllipse(b, r);
            Draw.Text(g, rank.Level.ToString(CultureInfo.InvariantCulture), f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void DrawPlayer(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= playersList.Items.Count) return;
            var g = e.Graphics; var b = e.Bounds;
            var st = (PlayerStats)playersList.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Pal.AccentBg : Color.White)) g.FillRectangle(bg, b);
            if (sel) using (var bar = new SolidBrush(Pal.Accent)) g.FillRectangle(bar, b.X, b.Y, S(3), b.Height);
            using (var line = new Pen(Pal.Window)) g.DrawLine(line, b.X + S(12), b.Bottom - 1, b.Right - S(8), b.Bottom - 1);
            int x = b.X + S(12);
            Draw.Text(g, "#" + (e.Index + 1), fSmallBold, new Rectangle(x, b.Y, S(26), b.Height), Pal.Sub, TextFormatFlags.VerticalCenter);
            x += S(26);
            var rank = st.Rank;
            DrawRankBadge(g, rank, new Rectangle(x, b.Y + (b.Height - S(30)) / 2, S(30), S(30)), fBold);
            x += S(40);
            string name = st.IsMe && Nick == "" ? "You" : st.Nick + (st.IsMe ? " (you)" : "");
            Draw.Text(g, name, st.IsMe ? fBold : fBody, new Rectangle(x, b.Y + S(8), b.Right - x - S(90), S(18)), Pal.Text, TextFormatFlags.Top);
            int fx = x + Math.Min(Draw.Width(g, name, st.IsMe ? fBold : fBody), b.Right - x - S(160)) + S(6);
            foreach (char f in st.Factions) fx += DrawFaction(g, f.ToString(), fx, b.Y + S(9), S(16), fSmall) + S(4);
            Draw.Text(g, rank.Name + " - " + st.Points + " points - " + st.Gear + " gear, " + st.Dungeon + " dungeon" + (st.Discoveries > 0 ? ", " + st.Discoveries + " new finds" : ""),
                fSmall, new Rectangle(x, b.Y + S(30), b.Right - x - S(16), S(16)), Pal.Sub, TextFormatFlags.Top);
            // online dot / last seen
            bool online = !st.IsMe && PeerOnline(st.Id);
            string seen = st.IsMe ? (lan != null ? "sharing" : "") : online ? "online" : "seen " + Ago(PeerSeen(st));
            int sw = Draw.Width(g, seen, fSmall);
            if (seen != "")
            {
                Draw.Text(g, seen, fSmall, new Rectangle(b.Right - S(12) - sw, b.Y + S(9), sw + S(2), S(16)), Pal.Sub, TextFormatFlags.Top);
                bool on = st.IsMe ? lan != null : online;
                using (var dot = new SolidBrush(on ? Pal.SuccessDot : Pal.Muted)) g.FillEllipse(dot, b.Right - S(22) - sw, b.Y + S(13), S(7), S(7));
            }
        }

        PlayerStats SelectedPlayer() { return playersList.SelectedItem as PlayerStats; }

        void PaintPlayerDetail(Graphics g)
        {
            var st = SelectedPlayer();
            int pad = S(20), w = detail.ClientSize.Width - pad * 2;
            if (st == null)
            {
                Draw.Text(g, "Pick a player on the left.", fBody, new Rectangle(0, detail.Height / 2, detail.Width, S(20)), Pal.Sub, TextFormatFlags.HorizontalCenter);
                SetDetailHeight(0);
                return;
            }
            g.TranslateTransform(detail.AutoScrollPosition.X, detail.AutoScrollPosition.Y);
            int y = pad;
            var rank = st.Rank;
            DrawRankBadge(g, rank, new Rectangle(pad, y, S(44), S(44)), fHead);
            Draw.Text(g, st.IsMe && Nick == "" ? "You" : st.Nick + (st.IsMe ? " (you)" : ""), fBig, new Rectangle(pad + S(56), y, w - S(56), S(28)), Pal.Text, TextFormatFlags.Top);
            int fx = pad + S(56);
            Draw.Text(g, "Rank " + rank.Level + ": " + rank.Name + "  -  " + st.Points + " points", fBody, new Rectangle(fx, y + S(28), w - S(56), S(18)), RankColor(rank.Level), TextFormatFlags.Top);
            y += S(56);
            int px = pad;
            foreach (char f in st.Factions) px += DrawFaction(g, f.ToString(), px, y, S(20), fSmall) + S(6);
            string when = st.IsMe ? (lan != null ? "Sharing as " + Nick : "Sharing is off") : (PeerOnline(st.Id) ? "Online now" : "Last seen " + Ago(PeerSeen(st)));
            Draw.Text(g, when + (st.Updated > DateTime.MinValue ? "  -  game saved " + Ago(st.Updated) : ""), fSmall, new Rectangle(px + S(4), y, w, S(20)), Pal.Sub, TextFormatFlags.VerticalCenter);
            y += S(30);

            // Progress to the next rank, and how much of the addon's chains they've done.
            Action<string, int, int, Color> bar = (label, val, max, col) =>
            {
                Draw.Text(g, label, fSmall, new Rectangle(pad, y, w, S(16)), Pal.Sub, TextFormatFlags.Top);
                y += S(18);
                var track = new Rectangle(pad, y, Math.Min(w, S(460)), S(8));
                Draw.Card(g, track, Pal.Window, Pal.CardBorder, S(4));
                if (max > 0 && val > 0)
                {
                    int fw = Math.Max(S(8), (int)(track.Width * Math.Min(1.0, (double)val / max)));
                    Draw.Card(g, new Rectangle(track.X, track.Y, fw, track.Height), col, col, S(4));
                }
                y += S(18);
            };
            if (rank.Next > 0) bar("Next rank: " + Ranks.For(rank.Next).Name + " at " + rank.Next + " points (" + (rank.Next - st.Points) + " to go)", st.Points - rank.Min, rank.Next - rank.Min, RankColor(rank.Level + 1));
            else bar("Top rank reached", 1, 1, RankColor(rank.Level));
            bar("Chains done: " + st.Known + " of " + st.Possible + " built into the addon for their factions (" + st.Percent + "%)", st.Known, st.Possible, Pal.Success);

            // Stats
            y += S(4);
            Draw.Text(g, "Stats", fBold, new Rectangle(pad, y, w, S(18)), Pal.Text, TextFormatFlags.Top);
            y += S(22);
            var lines = new[] {
                Tuple.Create("Gear chains", st.Gear.ToString(CultureInfo.InvariantCulture) + (st.Rare + st.Epic > 0 ? "  (" + st.Rare + " rare, " + st.Epic + " epic)" : "")),
                Tuple.Create("Dungeon chains", st.Dungeon.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create("New finds", st.Discoveries + " the addon didn't know"),
                Tuple.Create("Quests seen", st.QuestsSeen + "  (" + st.DungeonQuests + " dungeon quests)"),
                Tuple.Create("Longest chain", Plural(st.Longest, "step")),
                Tuple.Create("Zones", st.Zones.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create("Characters", st.Characters.ToString(CultureInfo.InvariantCulture)) };
            foreach (var l in lines)
            {
                Draw.Text(g, l.Item1, fBody, new Rectangle(pad, y, S(140), S(18)), Pal.Sub, TextFormatFlags.Top);
                Draw.Text(g, l.Item2, fBody, new Rectangle(pad + S(140), y, w - S(140), S(18)), Pal.Text, TextFormatFlags.Top);
                y += S(20);
            }

            // Achievements
            y += S(8);
            int earned = Achievements.Count(st);
            Draw.Text(g, "Achievements  " + earned + " of " + Achievements.All.Count, fBold, new Rectangle(pad, y, w, S(18)), Pal.Text, TextFormatFlags.Top);
            y += S(24);
            foreach (var a in Achievements.All.OrderByDescending(a => a.Earned(st)))
            {
                bool ok = a.Earned(st);
                var medal = new Rectangle(pad, y + S(1), S(18), S(18));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var mb = new SolidBrush(ok ? Pal.Hex("#C9971C") : Pal.Window)) g.FillEllipse(mb, medal);
                using (var mp = new Pen(ok ? Pal.Hex("#9A7212") : Pal.CardBorder, 1.5f)) g.DrawEllipse(mp, medal);
                if (ok) Draw.Icon(g, "check", new RectangleF(medal.X + S(3), medal.Y + S(3), S(12), S(12)), Color.White);
                Draw.Text(g, a.Name, ok ? fBold : fBody, new Rectangle(pad + S(28), y, S(170), S(18)), ok ? Pal.Text : Pal.Sub, TextFormatFlags.Top);
                string prog = ok ? "" : "  (" + a.Progress(st) + "/" + a.Target + ")";
                Draw.Text(g, a.Text + prog, fSmall, new Rectangle(pad + S(200), y + S(1), w - S(200), S(18)), Pal.Sub, TextFormatFlags.Top);
                y += S(24);
            }
            SetDetailHeight(y + pad);
        }

        string PlayersFooterText()
        {
            if (lan == null) return SharingOn ? "Sharing is on, but needs a nickname and group code." : "Sharing is off. Turn it on to see other players on your network.";
            return "Sharing as " + Nick + " - " + Plural(lan.OnlineCount, "player") + " online now";
        }

        // ------------------------------------------------------------------
        // Sharing dialog
        // ------------------------------------------------------------------
        void ShowSharing()
        {
            using (var f = new Form { Text = "Local sharing", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(580), S(520)), BackColor = Color.White, Font = fBody, ShowInTaskbar = false })
            {
                var title = new Label { Text = "Share with players on your network", Font = fBig, AutoSize = true, Location = new Point(S(24), S(16)) };
                var intro = new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(S(24), S(50)), Size = new Size(S(532), S(52)),
                    Text = "Companion apps on the same home network with the same group code swap the chains they've found and their stats. " +
                           "Shared chains show up in your game after /reload. Horde chains are only shown to Horde characters, Alliance to Alliance." };
                var on = new CheckBox { Text = "Share with players on my local network", Checked = SharingOn, AutoSize = true, Location = new Point(S(24), S(108)), Font = fBold };
                var nickL = new Label { Text = "Your nickname (what others see):", AutoSize = true, Location = new Point(S(24), S(142)) };
                var nickB = new TextBox { Text = Nick, Location = new Point(S(250), S(139)), Width = S(200), MaxLength = 24 };
                var codeL = new Label { Text = "Group code (agree it with your friends):", AutoSize = true, Location = new Point(S(24), S(176)) };
                var codeB = new TextBox { Text = GroupCode, Location = new Point(S(250), S(173)), Width = S(200), MaxLength = 40, UseSystemPasswordChar = true };
                var show = new CheckBox { Text = "Show", AutoSize = true, Location = new Point(S(460), S(175)) };
                show.CheckedChanged += (s, e) => codeB.UseSystemPasswordChar = !show.Checked;
                var note = new Label { ForeColor = Pal.Sub, AutoSize = false, Location = new Point(S(24), S(206)), Size = new Size(S(532), S(46)),
                    Text = "Only apps with the same code can join or send you chains. Windows may ask to let this app through its firewall the first time: allow it for private networks." };
                var err = new Label { ForeColor = Pal.Hex("#B42318"), AutoSize = false, Location = new Point(S(24), S(252)), Size = new Size(S(532), S(18)) };
                var listL = new Label { Text = "Players", Font = fHead, AutoSize = true, Location = new Point(S(24), S(274)) };
                var lv = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Location = new Point(S(24), S(300)),
                    Size = new Size(S(532), S(160)), BorderStyle = BorderStyle.FixedSingle };
                lv.Columns.Add("Player", S(150)); lv.Columns.Add("Faction", S(110)); lv.Columns.Add("Rank", S(110)); lv.Columns.Add("Chains", S(60)); lv.Columns.Add("Seen", S(90));
                Action fillList = () =>
                {
                    lv.Items.Clear();
                    foreach (var p in peers.Values.OrderBy(x => x.Nick))
                    {
                        bool online = PeerOnline(p.Id);
                        var it = new ListViewItem(new[] { p.Nick, string.Join(", ", p.Stats.Factions.Select(c => FactionWord(c.ToString())).ToArray()),
                            p.Stats.Rank.Name, p.Chains.Count.ToString(CultureInfo.InvariantCulture), online ? "online" : Ago(PeerSeen(p)) }) { Tag = p.Id };
                        lv.Items.Add(it);
                    }
                    if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { "Nobody yet", "", "", "", "" }) { ForeColor = Pal.Sub });
                };
                fillList();
                var forget = new FlatBtn { Text = "Forget player", Height = S(30), Font = fBody };
                forget.FitWidth(S(12), S(110)); forget.Location = new Point(S(24), S(470));
                var unforget = new FlatBtn { Kind = "ghost", Height = S(30), Font = fBody };
                forget.Click += (s, e) =>
                {
                    if (lv.SelectedItems.Count == 0 || !(lv.SelectedItems[0].Tag is string)) return;
                    string id = (string)lv.SelectedItems[0].Tag;
                    // Remembered, so they don't come straight back with their next broadcast.
                    var fg = Forgotten(); fg.Add(id); SetForgotten(fg);
                    if (lan != null) lan.Ignore(id);
                    SharedStore.Forget(id);
                    peers.Remove(id);
                    Reload();
                    fillList();
                    unforget.Enabled = true;
                };
                unforget.Text = "Un-forget all";
                unforget.FitWidth(S(12), S(100));
                unforget.Enabled = Forgotten().Count > 0;
                unforget.Click += (s, e) =>
                {
                    SetForgotten(new HashSet<string>());
                    unforget.Enabled = false;
                    StartSharing();   // fresh engine: hears everyone again
                    Msg("Forgotten players will be picked up again next time they're online.", false);
                };
                var sync = new FlatBtn { Kind = "ghost", Text = "Sync now", Height = S(30), Font = fBody };
                sync.FitWidth(S(12), S(90)); sync.Location = new Point(forget.Right + S(8), S(470));
                unforget.Location = new Point(sync.Right + S(4), S(470));
                sync.Click += (s, e) => { if (lan != null) { lan.ForgetVersions(); Msg("Asking everyone for their latest chains...", false); } };
                var save = new FlatBtn { Kind = "primary", Text = "Save", Size = new Size(S(90), S(30)), Location = new Point(S(370), S(470)), Font = fBold };
                var cancel = new FlatBtn { Text = "Cancel", Size = new Size(S(90), S(30)), Location = new Point(S(466), S(470)) };
                cancel.Click += (s, e) => f.Close();
                save.Click += (s, e) =>
                {
                    string n = Profile.CleanNick(nickB.Text), c = codeB.Text.Trim();
                    if (on.Checked && n == "") { err.Text = "Pick a nickname first."; return; }
                    if (on.Checked && c.Length < 4) { err.Text = "Use a group code of at least 4 characters."; return; }
                    settings["shareOn"] = on.Checked ? "1" : "0";
                    settings["nick"] = n;
                    settings["groupCode"] = c;
                    SaveSettings();
                    StartSharing();
                    f.Close();
                };
                f.AcceptButton = save; f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { title, intro, on, nickL, nickB, codeL, codeB, show, note, err, listL, lv, forget, sync, unforget, save, cancel });
                dialogOwner = f;
                try { f.ShowDialog(this); } finally { dialogOwner = null; }
            }
            if (view == "players") FillPlayers();
            UpdateDetail();
            header.Invalidate();
        }

        string SharingPillText(out bool on)
        {
            on = lan != null;
            if (lan == null) return "Sharing off";
            int n = lan.OnlineCount;
            return "Sharing: " + (n == 0 ? "nobody online" : Plural(n, "player") + " online");
        }

        // ------------------------------------------------------------------
        // Self-test hooks
        // ------------------------------------------------------------------
        internal void TestPeer(string payload)
        {
            var p = Profile.Parse(payload);
            if (p != null) { p.LastSeen = DateTime.Now; OnPeer(p, payload); }
        }
        internal void TestFaction(string f) { factionView = f; UpdateFactionPill(); Fill(); }
        internal string TestFactions() { return learned.Factions.Count == 0 ? "none recorded (shows everything)" : string.Join("+", learned.Factions.ToArray()); }
        internal string TestPlayers()
        {
            SetView("players");
            var sb = new System.Text.StringBuilder();
            foreach (var st in Leaderboard())
                sb.Append("    ").Append(st.Nick).Append(st.IsMe ? " (you)" : "").Append(" | ").Append(st.Factions).Append(" | ").Append(st.Rank.Name)
                  .Append(" | ").Append(st.Points).Append(" pts | gear ").Append(st.Gear).Append(" dungeon ").Append(st.Dungeon).Append(" new ").Append(st.Discoveries)
                  .Append(" | ").Append(Achievements.Count(st)).Append(" achievements | ").Append(st.Known).Append("/").Append(st.Possible).Append('\n');
            return sb.ToString();
        }
        internal string TestSharedFile()
        {
            try
            {
                string t = File.ReadAllText(Path.Combine(AddonDir, "Shared.lua"));
                int a = t.IndexOf("[=====[", StringComparison.Ordinal), b = t.IndexOf("]=====]", StringComparison.Ordinal);
                var rowsIn = Starter.Decode(t.Substring(a + 7, b - a - 7));
                return rowsIn.Count + " chains: " + string.Join(", ", rowsIn.Values.Select(s => s.Get("name") + "[" + (s.Get("faction") ?? "both") + "]").ToArray());
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }
    }
}
