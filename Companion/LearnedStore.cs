// QuestGearScout Companion - keeps every chain the addon has learned in
// Documents\QuestGearScout\Data\Learned\<account>, updated automatically each time the game saves:
//   all-chains.txt     every chain found so far, one per line (a giver's several chains and each
//                      faction's copy); only added or updated, never dropped, so they survive the
//                      game's data being reset
//   chains.txt         the same for /qgs import (one chain per giver, faction and gear/dungeon)
//   chains-list.txt    every chain, one readable line each
//   QuestGearScout.lua a copy of the game's saved data as it was last read
// C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QGSCompanion
{
    public class LearnedSaveResult
    {
        public int Total, Added, Updated;
        public string Folder;
        public bool Changed { get { return Added + Updated > 0; } }
    }

    public static class LearnedStore
    {
        public static string Dir { get { return Path.Combine(RestorePoints.Root, "Learned"); } }

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string FolderFor(string account)
        {
            var sb = new StringBuilder();
            foreach (char c in account ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            string name = sb.ToString().Trim(' ', '.');
            return Path.Combine(Dir, name == "" ? "account" : name);
        }

        // ---- all-chains.txt: the archive, one line per chain ----------------------------------
        // "QGS-ARCHIVE 1", then lines "<id> TAB <B or -> TAB <one QGS1 line>". The id is the giver,
        // gear or dungeon, the first quest and the faction ("C:123#G:456|H"; B = both factions have
        // seen it, N = not known), so a giver's several chains and each faction's copy are all kept.
        const string ArchiveHeader = "QGS-ARCHIVE 1";

        static string Prefix(string id) { int bar = id.LastIndexOf('|'); return bar < 0 ? id : id.Substring(0, bar + 1); }

        class Entry { public string Id; public ChainRow Row; public string Line; }

        static List<Entry> ReadArchive(string path)
        {
            var list = new List<Entry>();
            if (!File.Exists(path)) return list;
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Split(new[] { '\t' }, 3);
                if (p.Length < 3 || p[0] == "" || raw.StartsWith(ArchiveHeader)) continue;
                if (p[0].IndexOf('|') < 0) continue;   // a line from a test build before ids had a faction
                var s = Starter.Decode("QGS1\n" + p[2]).Values.FirstOrDefault();
                if (s == null) continue;
                bool dun = p[0].Contains("#D:");
                if (!s.Holds(dun)) continue;
                list.Add(new Entry { Id = p[0], Line = p[2], Row = new ChainRow { Starter = s, IsDungeon = dun, Faction = Starter.Tag(s), Both = p[1] == "B" } });
            }
            return list;
        }

        // A chain as one QGS1 line holding only its own half (gear or dungeon).
        static string LineOf(ChainRow r)
        {
            var s = new Starter();
            foreach (string f in Starter.BaseFields) if (r.Starter.F.ContainsKey(f)) s.F[f] = r.Starter.F[f];
            foreach (string f in r.IsDungeon ? Starter.DungeonFields : Starter.GearFields) if (r.Starter.F.ContainsKey(f)) s.F[f] = r.Starter.F[f];
            if (r.Faction != null) s.F["faction"] = r.Faction; else s.F.Remove("faction");
            return s.ToLine();
        }

        // Reads the game's saved data (`svText`, from `svPath`) and adds every chain it has learned to
        // the account's folder. `addon` supplies the built-in chain links, as in the main list.
        public static LearnedSaveResult Save(string account, string svPath, string svText, AddonData addon)
        {
            var res = new LearnedSaveResult { Folder = FolderFor(account) };
            // Every gear chain (Uncommon or better) and dungeon chain, whatever the list's filter.
            var learned = Learned.FromSavedVariables(svText, addon ?? new AddonData(), 2);

            Directory.CreateDirectory(res.Folder);
            string archivePath = Path.Combine(res.Folder, "all-chains.txt");
            string chainsPath = Path.Combine(res.Folder, "chains.txt");
            string listPath = Path.Combine(res.Folder, "chains-list.txt");
            var entries = ReadArchive(archivePath);
            bool changed = false;
            var fresh = learned.AllChains();
            // A chain both factions have seen (any of its reward quests) is saved once, as everyone's.
            var bothPrefixes = new HashSet<string>(fresh.Where(kv => kv.Value.Both).Select(kv => Prefix(kv.Key))
                .Concat(entries.Where(e => e.Row.Both).Select(e => Prefix(e.Id))));
            foreach (var kv in fresh)
            {
                var row = kv.Value;
                if (!row.Both && bothPrefixes.Contains(Prefix(kv.Key))) continue;
                string line = LineOf(row);
                string flag = row.Both ? "B" : "-";
                // Seen by both factions: it replaces its one-faction (or faction-unknown) copies.
                int removed = 0;
                if (row.Both)
                    removed = entries.RemoveAll(e => e.Id != kv.Key && Prefix(e.Id) == Prefix(kv.Key));
                int at = entries.FindIndex(e => e.Id == kv.Key);
                if (at >= 0 && entries[at].Line == line && entries[at].Row.Both == row.Both)
                {
                    if (removed > 0) { res.Updated++; changed = true; }
                    continue;
                }
                if (at >= 0) entries.RemoveAt(at);
                // Newest last, so it's the one chains.txt keeps where only one chain per giver fits.
                entries.Add(new Entry { Id = kv.Key, Line = line, Row = new ChainRow { Starter = row.Starter, IsDungeon = row.IsDungeon, Faction = row.Faction, Both = row.Both } });
                if (at >= 0 || removed > 0) res.Updated++; else res.Added++;
                changed = true;
            }
            res.Total = entries.Count;
            if (changed || !File.Exists(archivePath))
            {
                var sb = new StringBuilder(ArchiveHeader).Append('\n');
                foreach (var e in entries) sb.Append(e.Id).Append('\t').Append(e.Row.Both ? "B" : "-").Append('\t').Append(e.Line).Append('\n');
                WriteAtomic(archivePath, sb.ToString());
            }
            // The other files are written whenever they differ from what they should be (so a write that
            // failed last time is made up for). For /qgs import, which holds one chain per giver, faction
            // and gear/dungeon: the newest. A chain whose faction isn't known never goes under a tag.
            var packed = ChainRow.Pack(entries.Select(e => new ChainRow { Starter = Starter.Decode("QGS1\n" + e.Line).Values.First(),
                IsDungeon = e.Row.IsDungeon, Faction = e.Row.Faction, Both = e.Row.Both, KeepApart = e.Row.Faction == null }), null).Values;
            WriteIfDifferent(chainsPath, Starter.Encode(packed) + "\n");
            string list = ListText(account, entries.Select(e => e.Row).ToList());
            if (changed || !File.Exists(listPath) || !SameIgnoringFirstLines(File.ReadAllText(listPath), list, 2)) WriteAtomic(listPath, list);
            if (svText != null)
            {
                string copy = Path.Combine(res.Folder, "QuestGearScout.lua");
                if (!File.Exists(copy) || File.ReadAllText(copy) != svText) WriteAtomic(copy, svText);
            }
            string readme = Path.Combine(Dir, "README.txt");
            if (!File.Exists(readme))
                WriteAtomic(readme, "Chains the QuestGearScout addon has learned, saved by the QuestGearScout Companion app each time the game saves\r\n" +
                    "(when you log out or type /reload). One folder per WoW account:\r\n\r\n" +
                    "  all-chains.txt      every chain found so far, one per line. Chains are only ever added or updated here,\r\n" +
                    "                      so they stay even if the game's saved data is reset.\r\n" +
                    "  chains.txt          the same chains for /qgs import in game (it takes one chain per quest giver and\r\n" +
                    "                      faction, so where a giver has several, the newest is used)\r\n" +
                    "  chains-list.txt     every chain, one readable line each\r\n" +
                    "  QuestGearScout.lua  a copy of the game's saved data as it was last read\r\n");
            return res;
        }
        static string ListText(string account, List<ChainRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("QuestGearScout - chains learned on account ").Append(account).Append(" (").Append(rows.Count).Append(")\r\n");
            sb.Append("Updated ").Append(DateTime.Now.ToString("g", CultureInfo.CurrentCulture)).Append("\r\n\r\n");
            foreach (var r in rows.OrderBy(x => Zones.Name(x.Starter.Num("map")), StringComparer.CurrentCultureIgnoreCase)
                                  .ThenBy(x => x.Starter.Get("name") ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.IsDungeon))
            {
                var s = r.Starter;
                double x = s.Num("x") ?? 0, y = s.Num("y") ?? 0;
                Func<string, string> steps = k => { string n = s.Get(k) ?? "?"; return n + (n == "1" ? " step" : " steps"); };
                string what = r.IsDungeon
                    ? "Dungeon chain, " + steps("dungeonSteps") + " to " + (s.Get("dungeonTitle") ?? "?") + (s.Get("dungeonName") != null ? " (" + s.Get("dungeonName") + ")" : "")
                    : "Gear chain, " + steps("steps") + " to " + TextUtil.Plain(s.Get("item") ?? s.Get("finalTitle") ?? "a reward");
                sb.Append(s.Get("name") ?? "?").Append(" - ").Append(Zones.Name(s.Num("map")))
                  .Append(string.Format(CultureInfo.InvariantCulture, " ({0:0.0}, {1:0.0})", x * 100, y * 100))
                  .Append(" - ").Append(what)
                  .Append(" - ").Append(r.Faction == "A" ? "Alliance" : r.Faction == "H" ? "Horde" : r.Both ? "both factions" : "faction not known (recorded before 1.9.0)").Append("\r\n");
            }
            return sb.ToString();
        }

        static void WriteIfDifferent(string path, string text)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != text) WriteAtomic(path, text);
        }

        // The list's first lines say when it was written; the rest is what matters.
        static bool SameIgnoringFirstLines(string a, string b, int skip)
        {
            Func<string, string> body = s => string.Join("\n", s.Replace("\r", "").Split('\n').Skip(skip).ToArray());
            return body(a) == body(b);
        }

        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    public partial class MainForm
    {
        // Saved data already stored, by file: its time when it was stored.
        readonly Dictionary<string, DateTime> learnedStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Saved data that couldn't be stored: when it failed, so it's tried again only after a pause
        // (or as soon as the game writes it again).
        readonly Dictionary<string, Tuple<DateTime, DateTime>> learnedFailed = new Dictionary<string, Tuple<DateTime, DateTime>>(StringComparer.OrdinalIgnoreCase);

        // Stores what the game's saved data at `path` has learned, once per time the game saves it.
        // Returns null when nothing was done (already stored, or it couldn't be).
        LearnedSaveResult StoreLearned(string account, string path, string text, DateTime stamp)
        {
            DateTime done;
            if (learnedStamps.TryGetValue(path, out done) && done == stamp) return null;
            try
            {
                var res = LearnedStore.Save(account, path, text, addon);
                learnedStamps[path] = stamp;
                learnedFailed.Remove(path);
                return res;
            }
            catch (Exception ex)
            {
                bool first = !learnedFailed.ContainsKey(path);
                learnedFailed[path] = Tuple.Create(stamp, DateTime.Now);
                if (first) Msg("Couldn't save the learned chains of account " + account + " in " + LearnedStore.Dir + ": " + ex.Message, true);
                return null;
            }
        }

        // Every account in the game folder (not just the one shown): stored when the game saves it.
        void StoreAllAccounts()
        {
            if (gameDir == "") return;
            foreach (var a in ScanAccounts())
            {
                string p = Path.Combine(gameDir, "WTF", "Account", a.Name, "SavedVariables", "QuestGearScout.lua");
                DateTime done;
                if (learnedStamps.TryGetValue(p, out done) && done == a.Saved) continue;
                Tuple<DateTime, DateTime> failed;
                if (learnedFailed.TryGetValue(p, out failed) && failed.Item1 == a.Saved && (DateTime.Now - failed.Item2).TotalMinutes < 2) continue;
                string text;
                try { text = ReadShared(p); } catch { continue; }   // the game may be writing it: next time
                var res = StoreLearned(a.Name, p, text, a.Saved);
                var cur = accountBox.SelectedItem as AccountItem;
                if (res != null && res.Added > 0 && (cur == null || cur.Name != a.Name))
                    Msg("Saved " + Plural(res.Added, "new chain") + " from account " + a.Name + " in Documents\\QuestGearScout\\Data\\Learned.", false);
            }
        }

        internal string TestLearnedStore()
        {
            var sb = new StringBuilder();
            if (!Directory.Exists(LearnedStore.Dir)) return "  no Learned folder\n";
            foreach (string d in Directory.GetDirectories(LearnedStore.Dir))
            {
                string all = Path.Combine(d, "all-chains.txt");
                int n = File.Exists(all) ? File.ReadAllLines(all).Count(l => l.Contains("\t")) : -1;
                sb.Append("  ").Append(Path.GetFileName(d)).Append(": ").Append(n).Append(" chains saved; files: ")
                  .Append(string.Join(", ", Directory.GetFiles(d).Select(Path.GetFileName).OrderBy(x => x).ToArray())).Append('\n');
            }
            return sb.ToString();
        }
    }
}
