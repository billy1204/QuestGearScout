// QuestGearScout Companion - getting chains from a player in your group, and restore points.
//
// Click a player in the Local group panel: a window lists the chains they shared that you don't
// have yet (or that they have a newer version of), for your factions only. "Update" copies the
// ticked ones into the addon's Discoveries.lua.
//
// Every change this app makes to Discoveries.lua (getting chains, Add to addon, Remove, Undo,
// restoring) saves a restore point in Documents\QuestGearScout\Data\Restore points: a copy of
// the file plus a short note of what happened. Chains you got from a player are also kept in
// Documents\QuestGearScout\Data\Received chains (same text as /qgs export). The Restore points
// window puts any saved state back. C# 5 / .NET Framework 4.x WinForms.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace QGSCompanion
{
    // One saved state of Discoveries.lua.
    public class RestorePoint
    {
        public string Dir = "", Label = "", Folder = "";
        public DateTime When;
        public int Chains;
        public string FilePath { get { return Path.Combine(Dir, "Discoveries.lua"); } }
    }

    // Restore points and received chains, kept in Documents\QuestGearScout\Data.
    // Nothing here is ever deleted by the app (the files are small).
    public static class RestorePoints
    {
        public static string Root = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuestGearScout"), "Data");
        public static string PointsDir { get { return Path.Combine(Root, "Restore points"); } }
        public static string ReceivedDir { get { return Path.Combine(Root, "Received chains"); } }

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // Discoveries.lua as it is now (an empty one when the addon has none yet).
        public static string CurrentText(string addonDir)
        {
            string p = Path.Combine(addonDir, "Discoveries.lua");
            return File.Exists(p) ? File.ReadAllText(p) : AddonData.DiscoveriesText(new Starter[0]);
        }

        // The chains in a Discoveries.lua text, in one fixed order: two files with the same chains
        // compare equal even if they were written differently.
        public static string Canon(string lua) { return Starter.Encode(AddonData.DiscoveriesIn(lua).Values); }

        // Newest first.
        public static List<RestorePoint> All()
        {
            var list = new List<RestorePoint>();
            if (!Directory.Exists(PointsDir)) return list;
            foreach (string d in Directory.GetDirectories(PointsDir))
            {
                if (d.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;   // never finished saving
                var rp = Read(d);
                if (rp != null) list.Add(rp);
            }
            return list.OrderByDescending(x => x.When).ThenByDescending(x => Path.GetFileName(x.Dir), StringComparer.Ordinal).ToList();
        }

        static RestorePoint Read(string d)
        {
            try
            {
                var rp = new RestorePoint { Dir = d, Chains = -1 };   // -1: not known (no note saved)
                if (!File.Exists(rp.FilePath)) return null;
                rp.When = Directory.GetCreationTime(d);
                string info = Path.Combine(d, "info.txt");
                if (File.Exists(info))
                    foreach (string line in File.ReadAllLines(info))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        DateTime t; int n;
                        if (k == "label") rp.Label = v;
                        else if (k == "folder") rp.Folder = v;
                        else if (k == "time" && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
                            rp.When = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
                        else if (k == "chains" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) rp.Chains = n;
                    }
                return rp;
            }
            catch { return null; }
        }

        // Why a restore point's file can't be used (null if it's fine): it must be a Discoveries.lua
        // with a complete chain list holding as many chains as were saved.
        public static string Problem(RestorePoint rp, string text)
        {
            if (text.IndexOf("DISCOVERED_EXPORT", StringComparison.Ordinal) < 0 || text.IndexOf("]=====]", StringComparison.Ordinal) < 0)
                return "That restore point's file is damaged, so it can't be used.";
            int got = AddonData.DiscoveriesIn(text).Count;
            if (rp.Chains >= 0 && got != rp.Chains)
                return "That restore point's file is damaged (it should hold " + rp.Chains + " chains but " + got + " could be read), so it can't be used.";
            return null;
        }

        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }

        public static bool SameFolder(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // Saves Discoveries.lua as it is now. Written to a ".partial" folder first and renamed when
        // complete, so a half-saved point is never offered.
        public static RestorePoint Save(string addonDir, string label)
        {
            Directory.CreateDirectory(PointsDir);
            DateTime now = DateTime.Now;
            string stamp = now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            string final = Path.Combine(PointsDir, stamp);
            for (int n = 2; Directory.Exists(final) || Directory.Exists(final + ".partial"); n++)
                final = Path.Combine(PointsDir, stamp + " (" + n + ")");
            string tmp = final + ".partial";
            Directory.CreateDirectory(tmp);
            string text = CurrentText(addonDir);
            File.WriteAllText(Path.Combine(tmp, "Discoveries.lua"), text, Utf8);
            File.WriteAllText(Path.Combine(tmp, "info.txt"),
                "label=" + OneLine(label) + "\ntime=" + now.ToString("o", CultureInfo.InvariantCulture) + "\nfolder=" + OneLine(addonDir) +
                "\nchains=" + AddonData.DiscoveriesIn(text).Count.ToString(CultureInfo.InvariantCulture) + "\n", Utf8);
            Directory.Move(tmp, final);
            return Read(final);
        }

        // Saves the current state unless the newest point for this folder already holds it.
        public static RestorePoint SaveIfChanged(string addonDir, string label)
        {
            // Compared as written (not just the chains), so a hand edit is kept too.
            string now = CurrentText(addonDir).Replace("\r", "");
            var last = All().FirstOrDefault(p => SameFolder(p.Folder, addonDir));
            if (last != null)
            {
                try { if (File.ReadAllText(last.FilePath).Replace("\r", "") == now) return last; } catch { }
            }
            return Save(addonDir, label);
        }

        // Keeps a copy of chains received from a player (the same text /qgs import takes).
        public static string SaveReceived(string nick, IEnumerable<Starter> chains)
        {
            Directory.CreateDirectory(ReceivedDir);
            var sb = new StringBuilder();
            foreach (char c in nick ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            string who = sb.ToString().Trim(' ', '.');
            if (who == "") who = "a player";
            string baseName = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + " from " + who;
            string path = Path.Combine(ReceivedDir, baseName + ".txt");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(ReceivedDir, baseName + " (" + n + ").txt");
            File.WriteAllText(path, Starter.Encode(chains) + "\n", Utf8);
            return path;
        }
    }

    public partial class MainForm
    {
        // A chain a player shared that you could take: new to you, or a different version of one you have.
        class ChainOffer
        {
            public ChainRow Row;
            public Starter Source;   // the player's own entry (Row.Starter may have your missing fields filled in)
            public bool IsUpdate;
            public bool Picked = true;
        }

        // What an update changes: the gear or dungeon chain's fields (Pack never rewrites a giver's
        // name or place in an entry you have; the faction is compared separately).
        static string ChainSig(Starter s, bool dun)
        {
            var sb = new StringBuilder();
            foreach (string f in dun ? Starter.DungeonFields : Starter.GearFields) sb.Append(s.Get(f) ?? "").Append('\u0001');
            return sb.ToString();
        }

        // The chains a player shared that you could take, compared with `disc` (your Discoveries.lua).
        // Only for factions you play (neutral ones always), never ones the addon already ships, and
        // not ones you already have the same. Each is checked against the exact entry Update writes to.
        List<ChainOffer> OffersFrom(PeerData p, Dictionary<string, Starter> disc, out int have)
        {
            var list = new List<ChainOffer>();
            have = 0;
            disc = disc ?? new Dictionary<string, Starter>();
            var seenOffers = new FactionSet();   // one offer per giver, chain and side (a player can send two)
            foreach (var c in p.Chains.Values.OrderBy(x => x.Get("name") ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal))
                foreach (bool dun in new[] { false, true })
                {
                    if (dun ? !c.HasDungeon : !c.HasGear) continue;
                    // Shared without a faction: the tag your copy of this chain has (never neutral then).
                    string f = KeepTag(c, dun, disc);
                    bool both = Starter.IsBoth(c);
                    var row = new ChainRow { Starter = c, IsDungeon = dun, Faction = f, From = p.Nick, Status = ChainRow.Shared, Both = both };
                    if (!row.ForFactions(learned.Factions)) continue;
                    string sideKey = c.Key + (dun ? "#D" : "#G");
                    if (both ? seenOffers.HasExact(sideKey, null) : seenOffers.Covers(sideKey, f)) continue;
                    seenOffers.Add(sideKey, f);
                    if (both)
                    {
                        // Seen by both factions: it's for everyone. Your copy counts only if it's for
                        // everyone too; one tagged for a single faction is offered as an update.
                        string kb, kn;
                        if (Starter.Locate(addon.BuiltIn, c.Key, null, dun, out kb, null, false) != null) { have++; continue; }
                        Starter n = Starter.Locate(disc, c.Key, null, dun, out kn, null, false);
                        bool tagged = Starter.Entries(disc, c.Key).Any(e => Starter.Tag(e.Value) != null && e.Value.Holds(dun) && ChainRow.SameChain(e.Value, c, dun));
                        if (n != null && !tagged && ChainSig(n, dun) == ChainSig(c, dun)) { have++; continue; }
                        list.Add(new ChainOffer { Row = row, Source = c, IsUpdate = n != null || tagged });
                        continue;
                    }
                    if (Starter.LocateFor(addon.BuiltIn, c.Key, f, dun, learned.Factions) != null) { have++; continue; }
                    // The entry Update would write to: an update when it already holds this chain.
                    string k;
                    Starter d = ChainRow.Target(disc, c.Key, f, dun, out k, learned.Factions);
                    bool inDb = d != null && d.Holds(dun);
                    // Theirs has no faction and differs from the copy you have for a faction: it can't be
                    // told which side it's for, so it isn't offered (it never replaces a faction's chain).
                    if (inDb && ChainFaction(c) == null && f != null && ChainSig(d, dun) != ChainSig(c, dun)) { have++; continue; }
                    if (!inDb)
                    {
                        // You have it as everyone's (neutral) and theirs is the same chain: nothing new.
                        Starter seen = Starter.Locate(disc, c.Key, f, dun);
                        if (seen != null && ChainSig(seen, dun) == ChainSig(c, dun)) { have++; continue; }
                    }
                    if (inDb)
                    {
                        // Fields their copy lacks (an item link a player's app left out) keep your values.
                        var merged = c.Clone();
                        foreach (string fl in dun ? Starter.DungeonFields : Starter.GearFields)
                            if (!merged.F.ContainsKey(fl) && d.F.ContainsKey(fl)) merged.F[fl] = d.F[fl];
                        row.Starter = merged;
                        if (ChainSig(d, dun) == ChainSig(merged, dun) && (f == null || ChainFaction(d) == f)) { have++; continue; }
                    }
                    list.Add(new ChainOffer { Row = row, Source = c, IsUpdate = inDb });
                }
            return list;
        }

        List<ChainOffer> OffersFrom(PeerData p, out int have) { return OffersFrom(p, addon.Discovered, out have); }

        // How many chains a player has for you (for the sidebar), worked out again only when their
        // data, the addon's files or your characters change.
        readonly Dictionary<string, Tuple<PeerData, AddonData, Learned, int>> offerCounts = new Dictionary<string, Tuple<PeerData, AddonData, Learned, int>>();

        int OfferCount(PeerData p)
        {
            Tuple<PeerData, AddonData, Learned, int> c;
            if (offerCounts.TryGetValue(p.Id, out c) && c.Item1 == p && c.Item2 == addon && c.Item3 == learned) return c.Item4;
            int have;
            int n = OffersFrom(p, out have).Count;
            offerCounts[p.Id] = Tuple.Create(p, addon, learned, n);
            return n;
        }

        static string OfferWord(int n) { return n + " new"; }

        // ------------------------------------------------------------------
        // Getting chains
        // ------------------------------------------------------------------
        void ShowPlayerChains(PeerData p)
        {
            // Opened again after a restore made from inside it, so its list matches the restored file.
            DialogResult res;
            do
            {
                PeerData now;
                if (peers.TryGetValue(p.Id, out now)) p = now;   // their latest data
                using (var f = BuildPlayerChainsDialog(p))
                {
                    dialogOwner = f;
                    try { res = f.ShowDialog(this); } finally { dialogOwner = null; }
                }
            } while (res == DialogResult.Retry);
            if (groupBar != null) groupBar.Invalidate();
        }

        // Copies the picked chains into Discoveries.lua (restore point first) and keeps a copy of
        // them in Documents\QuestGearScout\Data\Received chains. True when it was done.
        bool GetChains(PeerData p, List<ChainOffer> picked)
        {
            bool done = false;
            Guarded(() =>
            {
                if (!AddonReady()) return;
                if (picked.Count == 0) { Msg("Tick at least one chain to get.", false); return; }
                // Worked out again against the file as it is now (it may have changed since the window
                // opened), so each chain goes to the right entry with the right faction.
                var fresh = FreshDiscovered();
                int had;
                picked = OffersFrom(p, fresh, out had).Where(n => picked.Any(o => o.Source == n.Source && o.Row.IsDungeon == n.Row.IsDungeon)).ToList();
                if (picked.Count == 0) { Msg("You already have those chains from " + p.Nick + ".", false); return; }
                int ups = picked.Count(o => o.IsUpdate), adds = picked.Count - ups;
                string did = adds == 0 ? "Got " + p.Nick + "'s updates to " + Plural(ups, "chain")
                    : "Got " + Plural(picked.Count, "chain") + " from " + p.Nick + (ups > 0 ? " (" + adds + " new, " + ups + " updated)" : "");
                var chosen = picked.Select(o => o.Row).ToList();
                if (!WriteWithBackup(ChainRow.Pack(chosen, fresh, learned.Factions).Values, did)) return;
                string keep = null;
                try { keep = RestorePoints.SaveReceived(p.Nick, ChainRow.Pack(chosen, null).Values); }
                catch (Exception ex) { Msg("Got the chains, but couldn't keep a copy in " + RestorePoints.ReceivedDir + ": " + ex.Message, true); }
                Reload();
                if (keep != null) Msg(did + ". Type /reload in game to load them.", false);
                done = true;
            });
            return done;
        }

        internal Form BuildPlayerChainsDialog(PeerData p)
        {
            int have;
            var offers = OffersFrom(p, out have);
            var f = new Form { Text = "Chains from " + p.Nick, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, BackColor = Pal.Card, ForeColor = Pal.Text, Font = fBody, ShowInTaskbar = false };
            int x0 = S(24), width = S(620), inner = width - x0 * 2, y = S(16);
            var controls = new List<Control>();
            controls.Add(new Label { Text = "Chains from " + p.Nick, Font = fBig, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(30)) });
            y += S(34);
            int ups = offers.Count(o => o.IsUpdate), news = offers.Count - ups;
            string intro;
            if (offers.Count == 0)
                intro = have > 0 ? "You already have every chain " + p.Nick + " shared for your factions (" + have + ")." : p.Nick + " hasn't shared any chains for your factions yet.";
            else
                intro = (news > 0 ? Plural(news, "chain") + " new to you" : "") + (news > 0 && ups > 0 ? " and " : "") +
                        (ups > 0 ? Plural(ups, "update") + " to chains you have" : "") + "." +
                        (have > 0 ? " You already have " + Plural(have, "other chain") + " of theirs." : "") +
                        " Only chains for the factions you play are listed.";
            controls.Add(new Label { Text = intro, ForeColor = Pal.Sub, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(36)) });
            y += S(42);

            var list = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(46), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Pal.Input, ForeColor = Pal.Text, Font = fBody, Location = new Point(x0, y), Size = new Size(inner, S(46) * Math.Max(3, Math.Min(6, offers.Count)) + S(2)) };
            foreach (var o in offers) list.Items.Add(o);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.Visible = offers.Count > 0;
            controls.Add(list);
            y += list.Visible ? list.Height + S(8) : 0;

            var all = new FlatBtn { Kind = "ghost", Text = "Tick all", Height = S(28), Font = fBody, Visible = offers.Count > 1 };
            var none = new FlatBtn { Kind = "ghost", Text = "Untick all", Height = S(28), Font = fBody, Visible = offers.Count > 1 };
            all.FitWidth(S(12), S(80)); none.FitWidth(S(12), S(80));
            all.Location = new Point(x0, y); none.Location = new Point(all.Right + S(6), y);
            controls.Add(all); controls.Add(none);
            if (offers.Count > 1) y += S(36);

            controls.Add(new Label { ForeColor = Pal.Sub, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(52)),
                Text = "Update copies the ticked chains into the addon (type /reload in game to see them). First a restore point is saved in " +
                       "Documents\\QuestGearScout\\Data, so Restore points... can always put things back." });
            y += S(56);
            var err = new Label { ForeColor = Pal.Error, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(36)) };
            if (!addon.Found) err.Text = "QuestGearScout isn't installed in the game folder this app is using, so chains can't be copied yet.";
            controls.Add(err);
            y += S(40);

            var restore = new FlatBtn { Kind = "ghost", Text = "Restore points...", Height = S(30), Font = fBody };
            restore.FitWidth(S(12), S(110)); restore.Location = new Point(x0, y);
            var close = new FlatBtn { Text = "Close", Size = new Size(S(90), S(30)) };
            close.Location = new Point(width - x0 - close.Width, y);
            var update = new FlatBtn { Kind = "primary", Height = S(30), Font = fBold };
            controls.Add(restore); controls.Add(update); controls.Add(close);
            f.ClientSize = new Size(width, y + S(30) + S(20));

            Action refresh = () =>
            {
                int n = offers.Count(o => o.Picked);
                update.Text = n > 0 ? "Update (" + n + ")" : "Update";
                update.FitWidth(S(14), S(100));
                update.Location = new Point(close.Left - S(8) - update.Width, y);
                update.Enabled = n > 0 && addon.Found;
                update.Invalidate();
                list.Invalidate();
            };
            refresh();

            list.DrawItem += (s, e) => DrawOffer(list, e);
            Action<int> toggle = i =>
            {
                if (i < 0 || i >= list.Items.Count) return;
                var o = (ChainOffer)list.Items[i];
                o.Picked = !o.Picked;
                refresh();
            };
            list.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) toggle(list.IndexFromPoint(e.Location)); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { toggle(list.SelectedIndex); e.Handled = e.SuppressKeyPress = true; } };
            all.Click += (s, e) => { foreach (var o in offers) o.Picked = true; refresh(); };
            none.Click += (s, e) => { foreach (var o in offers) o.Picked = false; refresh(); };
            restore.Click += (s, e) =>
            {
                var before = addon;
                Run("Restore points", ShowRestorePoints);
                // The addon's chains changed: this list is out of date, so the window opens again.
                if (addon != before) { f.DialogResult = DialogResult.Retry; f.Close(); }
            };
            close.Click += (s, e) => f.Close();
            update.Click += (s, e) =>
            {
                err.Text = "";
                Run("Update", () => { if (GetChains(p, offers.Where(o => o.Picked).ToList())) { f.DialogResult = DialogResult.OK; f.Close(); } });
            };
            f.AcceptButton = update; f.CancelButton = close;
            f.Controls.AddRange(controls.ToArray());
            // For the self-test: tick everything and update, as if clicked.
            f.Tag = (Func<bool>)(() => GetChains(p, offers.Where(o => o.Picked).ToList()));
            Theme.Form(f);
            return f;
        }

        void DrawOffer(ListBox list, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var g = e.Graphics; var b = e.Bounds;
            var o = (ChainOffer)list.Items[e.Index];
            var r = o.Row;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Pal.Hover : Pal.Input)) g.FillRectangle(bg, b);
            using (var line = new Pen(Pal.CardBorder)) g.DrawLine(line, b.X + S(8), b.Bottom - 1, b.Right - S(8), b.Bottom - 1);
            // The tick box.
            var box = new Rectangle(b.X + S(12), b.Y + (b.Height - S(18)) / 2, S(18), S(18));
            Draw.Card(g, box, o.Picked ? Pal.Accent : Pal.Window, o.Picked ? Pal.Accent : Pal.InputBorder, S(4));
            if (o.Picked) Draw.Icon(g, "check", new RectangleF(box.X + S(3), box.Y + S(3), S(12), S(12)), Pal.OnAccent);
            int x = box.Right + S(12), right = b.Right - S(10);
            // Line 1: who gives it, then what it is to you.
            string name = Giver(r.Starter);
            int tagW = Draw.Pill(g, o.IsUpdate ? "Update" : "New to you", fSmall, new Point(right, b.Y + S(6)),
                o.IsUpdate ? Pal.AddedBg : Pal.AccentBg, o.IsUpdate ? Pal.AddedBorder : Pal.AccentBorder, o.IsUpdate ? Pal.Added : Pal.Accent, S(7), S(18), true);
            int fw = r.Faction == "A" || r.Faction == "H" ? Draw.Width(g, FactionWord(r.Faction), fSmall) + S(12) + S(6) : 0;
            int nameRoom = right - tagW - S(8) - fw - x;
            Draw.Text(g, name, fBold, new Rectangle(x, b.Y + S(6), Math.Max(S(20), nameRoom), S(18)), o.Picked ? Pal.Text : Pal.Sub, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            if (fw > 0) DrawFaction(g, r.Faction, x + Math.Min(Draw.Width(g, name, fBold), nameRoom) + S(6), b.Y + S(7), S(16), fSmall);
            // Line 2: gear or dungeon, where, and the reward.
            string what = (r.IsDungeon ? "Dungeon chain" : "Gear chain") + " - " + Zone(r.Starter) + " - " + Reward(r) +
                          (r.Both ? " - both factions can do it" : "") + (o.IsUpdate ? " - replaces your copy" : "");
            Draw.Text(g, what, fSmall, new Rectangle(x, b.Y + S(25), right - x, S(16)), r.IsDungeon ? Pal.Dungeon : Pal.Sub, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }

        // ------------------------------------------------------------------
        // Restore points
        // ------------------------------------------------------------------
        // Before a change: keep the state as it is now (unless the newest point already has it).
        // When that can't be saved nothing is changed.
        bool RestorePointBefore(string did)
        {
            try { RestorePoints.SaveIfChanged(AddonDir, "How it was before: " + did); return true; }
            catch (Exception ex)
            {
                string body = "Couldn't save a restore point in\n" + RestorePoints.Root + "\n\nso nothing was changed.\n\nDetails: " + ex.Message;
                if (uiTest) testConfirms.Add(body.Replace("\n", " / "));
                else Theme.Box(DialogParent, body, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Msg("Nothing was changed: couldn't save a restore point (" + ex.Message + ")", true);
                return false;
            }
        }

        // After a change: keep the new state too, so it can be come back to.
        void RestorePointAfter(string did)
        {
            try { RestorePoints.Save(AddonDir, did); }
            catch (Exception ex)
            {
                string body = "The change was made, but its restore point couldn't be saved in\n" + RestorePoints.Root + "\n\nDetails: " + ex.Message;
                if (uiTest) testConfirms.Add(body.Replace("\n", " / "));
                else Theme.Box(DialogParent, body, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ShowRestorePoints()
        {
            using (var f = BuildRestoreDialog())
            {
                var prev = dialogOwner;
                dialogOwner = f;
                try { f.ShowDialog(prev ?? this); } finally { dialogOwner = prev; }
            }
        }

        static string WhenText(DateTime t) { return t.ToString("ddd d MMM", CultureInfo.CurrentCulture) + ", " + t.ToString("T", CultureInfo.CurrentCulture); }

        // Puts Discoveries.lua back to a restore point (the state now is saved first).
        bool RestoreTo(RestorePoint rp)
        {
            bool done = false;
            Guarded(() =>
            {
                if (!AddonReady()) return;
                string text;
                try { text = File.ReadAllText(rp.FilePath); }
                catch (Exception ex) { Msg("Couldn't read that restore point: " + ex.Message, true); return; }
                string bad = RestorePoints.Problem(rp, text);
                if (bad != null) { Msg(bad, true); return; }
                string did = "Went back to " + WhenText(rp.When) + (rp.Label != "" ? " (" + rp.Label + ")" : "");
                // Written fresh from the chains it holds (the same way the app always writes the file).
                if (!WriteWithBackup(AddonData.DiscoveriesIn(text).Values.ToList(), did)) return;
                Reload();
                Msg("The addon's chains are back to how they were " + WhenText(rp.When) + ". Type /reload in game to load them.", false);
                done = true;
            });
            return done;
        }

        internal Form BuildRestoreDialog()
        {
            var f = new Form { Text = "Restore points", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, BackColor = Pal.Card, ForeColor = Pal.Text, Font = fBody, ShowInTaskbar = false };
            int x0 = S(24), width = S(660), inner = width - x0 * 2, y = S(16);
            var controls = new List<Control>();
            controls.Add(new Label { Text = "Restore points", Font = fBig, AutoSize = true, Location = new Point(x0, y) });
            y += S(36);
            controls.Add(new Label { ForeColor = Pal.Sub, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(52)),
                Text = "A restore point is saved every time this app changes the addon's chains. Pick one and click Restore to put the chains back " +
                       "the way they were then - the way they are now is saved first, so you can always come back. They're kept in " +
                       (RestorePoints.Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
                           ? "Documents\\QuestGearScout\\Data" : RestorePoints.Root) + " (Open folder)." });
            y += S(58);
            var lv = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Location = new Point(x0, y),
                Size = new Size(inner, S(250)), BorderStyle = BorderStyle.FixedSingle };
            lv.Columns.Add("When", S(150)); lv.Columns.Add("What happened", S(370)); lv.Columns.Add("Chains", S(70));
            controls.Add(lv);
            y += lv.Height + S(8);
            var err = new Label { ForeColor = Pal.Error, AutoSize = false, UseMnemonic = false, Location = new Point(x0, y), Size = new Size(inner, S(36)) };
            controls.Add(err);
            y += S(40);
            var open = new FlatBtn { Kind = "ghost", Text = "Open folder", Height = S(30), Font = fBody };
            open.FitWidth(S(12), S(100)); open.Location = new Point(x0, y);
            var close = new FlatBtn { Text = "Close", Size = new Size(S(90), S(30)) };
            close.Location = new Point(width - x0 - close.Width, y);
            var restore = new FlatBtn { Kind = "primary", Text = "Restore", Size = new Size(S(100), S(30)), Font = fBold };
            restore.Location = new Point(close.Left - S(8) - restore.Width, y);
            controls.Add(open); controls.Add(restore); controls.Add(close);
            f.ClientSize = new Size(width, y + S(30) + S(20));

            string nowCanon = null;
            Action fill = () =>
            {
                lv.Items.Clear();
                try { nowCanon = addon.Found ? RestorePoints.Canon(RestorePoints.CurrentText(AddonDir)) : null; } catch { nowCanon = null; }
                List<RestorePoint> points;
                try { points = RestorePoints.All(); } catch (Exception ex) { points = new List<RestorePoint>(); err.Text = "Couldn't read the restore points: " + ex.Message; }
                bool markedNow = false;
                foreach (var rp in points)
                {
                    bool isNow = false;
                    if (!markedNow && nowCanon != null && RestorePoints.SameFolder(rp.Folder, AddonDir))
                    {
                        try { isNow = RestorePoints.Canon(File.ReadAllText(rp.FilePath)) == nowCanon; } catch { }
                        markedNow = true;   // only the newest point for this folder can be "now"
                    }
                    string what = (rp.Label == "" ? "Saved" : rp.Label) + (isNow ? "  (how it is now)" : "") +
                                  (rp.Folder != "" && addon.Found && !RestorePoints.SameFolder(rp.Folder, AddonDir) ? "  (another game folder)" : "");
                    var it = new ListViewItem(new[] { WhenText(rp.When), what, rp.Chains >= 0 ? rp.Chains.ToString(CultureInfo.InvariantCulture) : "?" }) { Tag = rp };
                    if (isNow) it.ForeColor = Pal.Sub;
                    lv.Items.Add(it);
                }
                if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { "None yet", "One is saved the first time this app changes the addon's chains.", "" }) { ForeColor = Pal.Sub });
            };
            fill();

            open.Click += (s, e) => OpenFolder(Directory.Exists(RestorePoints.PointsDir) ? RestorePoints.PointsDir : RestorePoints.Root);
            close.Click += (s, e) => f.Close();
            Func<RestorePoint, string> attempt = rp =>
            {
                if (rp == null) return "Pick a restore point first.";
                if (!addon.Found) return "QuestGearScout isn't installed in the game folder this app is using.";
                string text;
                try { text = File.ReadAllText(rp.FilePath); }
                catch (Exception ex) { return "Couldn't read that restore point: " + ex.Message; }
                string bad = RestorePoints.Problem(rp, text);
                if (bad != null) return bad;
                if (nowCanon != null && RestorePoints.Canon(text) == nowCanon) return "The addon's chains are already like that.";
                if (!Confirm("Put the addon's chains back to how they were " + WhenText(rp.When) + "?\n\n" + (rp.Label == "" ? "" : rp.Label + "\n") + Plural(AddonData.DiscoveriesIn(text).Count, "chain") +
                             " then.\n\nThe way they are now is saved as a restore point first, so you can come back to it.")) return "";
                return RestoreTo(rp) ? null : "It wasn't restored (see the message at the bottom of the main window).";
            };
            restore.Click += (s, e) =>
            {
                var rp = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as RestorePoint : null;
                string problem = "It wasn't restored.";
                Run("Restore", () => { problem = attempt(rp); });
                err.Text = problem ?? "";
                if (problem == null) fill();
            };
            lv.DoubleClick += (s, e) => restore.PerformClick();
            f.AcceptButton = restore; f.CancelButton = close;
            f.Controls.AddRange(controls.ToArray());
            f.Tag = attempt;
            Theme.Form(f);
            return f;
        }

        // ------------------------------------------------------------------
        // Self-test hooks
        // ------------------------------------------------------------------
        PeerData TestPeerNamed(string nick) { return peers.Values.FirstOrDefault(x => x.Nick == nick); }

        internal string TestOffers(string nick)
        {
            var p = TestPeerNamed(nick);
            if (p == null) return "no player called " + nick;
            int have;
            var offers = OffersFrom(p, out have);
            return offers.Count + " to get, " + have + " already had" + (offers.Count > 0 ? ": " : "") +
                string.Join(", ", offers.Select(o => Giver(o.Row.Starter) + (o.Row.IsDungeon ? " (dungeon)" : " (gear)") + "[" + (o.Row.Faction ?? "both") + "]" + (o.IsUpdate ? " UPDATE" : " NEW")).ToArray());
        }

        internal Form TestPlayerDialog(string nick) { var p = TestPeerNamed(nick); return p == null ? null : BuildPlayerChainsDialog(p); }

        // Clicks Update in the player's window with everything ticked.
        internal string TestGetChains(string nick)
        {
            var p = TestPeerNamed(nick);
            if (p == null) return "no player called " + nick;
            using (var f = BuildPlayerChainsDialog(p))
            {
                bool ok = ((Func<bool>)f.Tag)();
                return (ok ? "updated" : "not updated") + " | " + lblMsg.Text;
            }
        }

        internal string TestRestorePoints()
        {
            var sb = new StringBuilder();
            var all = RestorePoints.All();
            sb.Append("  ").Append(Plural(all.Count, "restore point")).Append(" in Documents\\QuestGearScout\\Data\n");
            foreach (var rp in all) sb.Append("    ").Append(rp.Label).Append(" | ").Append(Plural(rp.Chains, "chain")).Append('\n');
            int received = Directory.Exists(RestorePoints.ReceivedDir) ? Directory.GetFiles(RestorePoints.ReceivedDir, "*.txt").Length : 0;
            sb.Append("  received-chain files: ").Append(received).Append('\n');
            return sb.ToString();
        }

        internal int TestRestoreCount() { return RestorePoints.All().Count; }
        internal void TestUndo() { Run("Undo", UndoLast); }

        // Picks the restore point `index` (0 = newest) in the Restore points window and clicks Restore.
        internal string TestRestore(int index)
        {
            testConfirms.Clear();
            using (var f = BuildRestoreDialog())
            {
                var all = RestorePoints.All();
                var rp = index >= 0 && index < all.Count ? all[index] : null;
                string problem = ((Func<RestorePoint, string>)f.Tag)(rp);
                string asked = string.Join(" || ", testConfirms.ToArray());
                testConfirms.Clear();
                return (problem == null ? "restored" : "refused: " + problem) + (asked != "" ? " | asked: " + asked : "") + " | " + lblMsg.Text;
            }
        }
    }
}
