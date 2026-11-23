// QuestGearScout Companion - data side: reads the addon's SavedVariables and
// Data.lua / Discoveries.lua, works out the chains you've learned, and writes
// exports in the same "QGS1" format as /qgs export.
// Builds with the C# compiler that ships with Windows (.NET Framework 4.x, C# 5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QGSCompanion
{
    // ---------------------------------------------------------------------
    // Minimal Lua value reader for SavedVariables files.
    // Tables become Dictionary<object, object>; numbers are double; keys that
    // are whole numbers become long so quest IDs compare cleanly.
    // ---------------------------------------------------------------------
    public class LuaReader
    {
        readonly string s;
        int i;

        LuaReader(string text) { s = text; }

        public static Dictionary<string, object> ReadFile(string text)
        {
            var r = new LuaReader(text);
            var globals = new Dictionary<string, object>();
            while (true)
            {
                r.Skip();
                if (r.i >= r.s.Length) break;
                string name = r.Name();
                if (name == null) { r.i++; continue; }
                r.Skip();
                if (r.i < r.s.Length && r.s[r.i] == '=')
                {
                    r.i++;
                    globals[name] = r.Value();
                }
            }
            return globals;
        }

        void Skip()
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    if (i + 3 < s.Length && s[i + 2] == '[' && s[i + 3] == '[')
                    {
                        int e = s.IndexOf("]]", i + 4, StringComparison.Ordinal);
                        i = e < 0 ? s.Length : e + 2;
                    }
                    else
                    {
                        int e = s.IndexOf('\n', i);
                        i = e < 0 ? s.Length : e + 1;
                    }
                    continue;
                }
                break;
            }
        }

        string Name()
        {
            int st = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return i > st ? s.Substring(st, i - st) : null;
        }

        object Value()
        {
            Skip();
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return Table();
            if (c == '"' || c == '\'') return Str();
            if (c == '-' || char.IsDigit(c) || c == '.') return Num();
            string w = Name();
            if (w == "true") return true;
            if (w == "false") return false;
            return null; // nil or anything unexpected
        }

        Dictionary<object, object> Table()
        {
            var t = new Dictionary<object, object>();
            i++; // {
            long n = 1;
            while (true)
            {
                Skip();
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                object key = null;
                if (s[i] == '[')
                {
                    i++;
                    key = Normal(Value());
                    Skip(); if (i < s.Length && s[i] == ']') i++;
                    Skip(); if (i < s.Length && s[i] == '=') i++;
                }
                else
                {
                    int save = i;
                    string nm = Name();
                    Skip();
                    if (nm != null && i < s.Length && s[i] == '=' && (i + 1 >= s.Length || s[i + 1] != '='))
                    {
                        i++; key = nm;
                    }
                    else { i = save; key = n++; }
                }
                object v = Value();
                if (key != null && v != null) t[key] = v;
                Skip();
                if (i < s.Length && (s[i] == ',' || s[i] == ';')) i++;
            }
            return t;
        }

        static object Normal(object k)
        {
            if (k is double)
            {
                double d = (double)k;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
            }
            return k;
        }

        string Str()
        {
            char q = s[i++];
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != q)
            {
                char c = s[i++];
                if (c != '\\' || i >= s.Length) { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\n': sb.Append('\n'); break;
                    default:
                        if (char.IsDigit(e))
                        {
                            int code = e - '0', k = 0;
                            while (k < 2 && i < s.Length && char.IsDigit(s[i])) { code = code * 10 + (s[i++] - '0'); k++; }
                            sb.Append((char)code);
                        }
                        else sb.Append(e);
                        break;
                }
            }
            i++; // closing quote
            return sb.ToString();
        }

        double Num()
        {
            int st = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' ||
                   ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')))) i++;
            string tok = s.Substring(st, i - st);
            double d;
            if (tok.StartsWith("0x") || tok.StartsWith("-0x"))
            {
                bool neg = tok[0] == '-';
                d = Convert.ToInt64(tok.Substring(neg ? 3 : 2), 16) * (neg ? -1 : 1);
            }
            else double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }
    }

    // ---------------------------------------------------------------------
    // A chain starter (one row of the QGS1 format).
    // ---------------------------------------------------------------------
    public class Starter
    {
        public static readonly string[] Fields = {
            "kind", "id", "map", "x", "y", "rootQuest", "quality", "steps",
            "name", "rootTitle", "finalTitle", "item",
            "dRoot", "dungeonSteps", "dungeonName", "dungeonTitle", "dRootTitle",
            "faction", "classes" };
        static readonly HashSet<string> Numeric = new HashSet<string> {
            "id", "map", "x", "y", "rootQuest", "quality", "steps", "dRoot", "dungeonSteps" };

        public Dictionary<string, object> F = new Dictionary<string, object>();

        public string Key { get { return Get("kind") + ":" + Get("id"); } }
        public string Get(string k) { object v; return F.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
        public double? Num(string k) { object v; if (F.TryGetValue(k, out v) && v is double) return (double)v; return null; }
        public bool HasGear { get { return Num("quality").HasValue; } }
        public bool HasDungeon { get { return Num("dungeonSteps").HasValue; } }

        static string Esc(object v)
        {
            if (v == null) return "";
            if (v is double)
            {
                double d = (double)v;
                return d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture)
                                          : d.ToString("0.0000", CultureInfo.InvariantCulture);
            }
            var sb = new StringBuilder();
            foreach (char c in Convert.ToString(v, CultureInfo.InvariantCulture))
            {
                // '[' and ']' are encoded too, so no text can ever close the Lua long string
                // ([=====[ ... ]=====]) these lines are written inside in Shared.lua / Discoveries.lua.
                if (c == '%' || c == '|' || c == '\t' || c == '\n' || c == '\r' || c == '[' || c == ']') sb.Append('%').Append(((int)c).ToString("X2"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Unesc(string s)
        {
            return Regex.Replace(s, "%([0-9A-Fa-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        }

        public string ToLine()
        {
            return string.Join("\t", Fields.Select(f => { object v; F.TryGetValue(f, out v); return Esc(v); }).ToArray());
        }

        public static string Encode(IEnumerable<Starter> list)
        {
            var sb = new StringBuilder("QGS1");
            foreach (var s in list.OrderBy(x => x.Key)) sb.Append('\n').Append(s.ToLine());
            return sb.ToString();
        }

        public static Dictionary<string, Starter> Decode(string text)
        {
            var outp = new Dictionary<string, Starter>();
            if (text == null) return outp;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "" || line == "QGS1") continue;
                // Leading spaces (text pasted from chat or forums) are never data.
                string[] parts = raw.TrimStart(' ').TrimEnd(' ', '\r').Split('\t');
                var st = new Starter();
                for (int j = 0; j < Fields.Length && j < parts.Length; j++)
                {
                    if (parts[j] == "") continue;
                    string v = Unesc(parts[j]);
                    if (Numeric.Contains(Fields[j]))
                    {
                        double d;
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) st.F[Fields[j]] = d;
                    }
                    else st.F[Fields[j]] = v;
                }
                string kind = st.Get("kind");
                if ((kind == "C" || kind == "O") && st.Num("id").HasValue && st.Num("map").HasValue &&
                    st.Num("x").HasValue && st.Num("y").HasValue && (st.HasGear || st.HasDungeon))
                {
                    // The same giver can have a Horde entry and an Alliance entry: keep both.
                    string k = st.Key;
                    Starter prev;
                    if (outp.TryGetValue(k, out prev) && prev.Get("faction") != st.Get("faction")) k = AltKey(k, st.Get("faction"));
                    outp[k] = st;
                }
            }
            return outp;
        }

        // Dictionary key for a second entry of the same giver that belongs to another faction.
        public static string AltKey(string key, string faction) { return key + "|" + (faction ?? "N"); }

        // Could these two faction tags be the same chain? Untagged (neutral / unknown) matches anything.
        public static bool SameSide(string a, string b) { return a == null || b == null || a == b; }

        // The entry for this giver and faction in 'd' (the plain key, or the other-faction key).
        public static Starter Find(Dictionary<string, Starter> d, string key, string faction)
        {
            Starter s;
            if (d.TryGetValue(key, out s) && SameSide(s.Get("faction"), faction)) return s;
            if (d.TryGetValue(AltKey(key, faction), out s)) return s;
            return null;
        }

        // A giver can have up to four entries: the plain key, and key|A, key|H, key|N for further
        // entries of other factions. The tag that matters is the entry's own "faction" field.
        public static string Tag(Starter s) { string f = s.Get("faction"); return f == "A" || f == "H" ? f : null; }
        // "B" only travels between Companion apps (a player's chain that both factions have seen:
        // for everyone, for sure); it's never written to the addon's files. Untagged = for everyone,
        // or recorded before factions were saved.
        public static bool IsBoth(Starter s) { return s.Get("faction") == "B"; }
        public static string FactionKey(Starter s) { return IsBoth(s) ? "B" : Tag(s); }
        public bool Holds(bool dungeon) { return dungeon ? HasDungeon : HasGear; }

        public static List<KeyValuePair<string, Starter>> Entries(Dictionary<string, Starter> d, string key)
        {
            var list = new List<KeyValuePair<string, Starter>>();
            foreach (string k in new[] { key, AltKey(key, "A"), AltKey(key, "H"), AltKey(key, null) })
            {
                Starter s;
                if (d.TryGetValue(k, out s)) list.Add(new KeyValuePair<string, Starter>(k, s));
            }
            // Entries Pack added this session under a numbered key (see ChainRow.Target).
            foreach (var kv in d) if (kv.Key.StartsWith(key + "|#", StringComparison.Ordinal)) list.Add(kv);
            return list;
        }

        // The entry holding this giver's gear (or dungeon) chain for `faction`: one tagged for that
        // faction, else an untagged (neutral) one. For an untagged faction: a neutral one, else any.
        // `k` is its key. Reading, Pack and Unpack all use this, so they always agree.
        // For an untagged faction, after a neutral entry: one tagged for a faction in `prefer` (the
        // ones you play), then - only when `anySide` - any entry holding the chain.
        public static Starter Locate(Dictionary<string, Starter> d, string key, string faction, bool dungeon, out string k, ICollection<string> prefer, bool anySide)
        {
            var holders = Entries(d, key).Where(e => e.Value.Holds(dungeon)).ToList();
            var hit = holders.FirstOrDefault(e => Tag(e.Value) == faction);
            if (hit.Value == null && faction != null) hit = holders.FirstOrDefault(e => Tag(e.Value) == null);
            if (hit.Value == null && faction == null && prefer != null) hit = holders.FirstOrDefault(e => prefer.Contains(Tag(e.Value)));
            if (hit.Value == null && faction == null && anySide) hit = holders.FirstOrDefault();
            k = hit.Key;
            return hit.Value;
        }

        public static Starter Locate(Dictionary<string, Starter> d, string key, string faction, bool dungeon, out string k)
        {
            return Locate(d, key, faction, dungeon, out k, null, true);
        }

        public static Starter Locate(Dictionary<string, Starter> d, string key, string faction, bool dungeon)
        {
            string k;
            return Locate(d, key, faction, dungeon, out k, null, true);
        }

        // Is the chain built in (or in `d`) for a player of `played`? A chain without a faction
        // counts only when an entry for one of those factions (or a neutral one) holds it.
        public static Starter LocateFor(Dictionary<string, Starter> d, string key, string faction, bool dungeon, ICollection<string> played)
        {
            string k;
            bool any = played == null || played.Count == 0;
            return Locate(d, key, faction, dungeon, out k, any ? null : played, any);
        }

        public Starter Clone()
        {
            return new Starter { F = new Dictionary<string, object>(F) };
        }

        // Deep copy, so merging into the result never changes the original.
        public static Dictionary<string, Starter> CloneAll(Dictionary<string, Starter> src)
        {
            var outp = new Dictionary<string, Starter>();
            foreach (var kv in src) outp[kv.Key] = kv.Value.Clone();
            return outp;
        }

        public static readonly string[] BaseFields = { "kind", "id", "map", "x", "y", "name", "faction", "classes" };
        public static readonly string[] GearFields = { "rootQuest", "quality", "steps", "rootTitle", "finalTitle", "item" };
        public static readonly string[] DungeonFields = { "dRoot", "dungeonSteps", "dungeonName", "dungeonTitle", "dRootTitle" };
    }

    // One line in the app's list: a gear chain or a dungeon chain from a starter.
    public class ChainRow
    {
        public const string New = "New", InDiscoveries = "In Discoveries", BuiltIn = "Built-in", Unknown = "?", Shared = "Shared";

        public Starter Starter;
        public bool IsDungeon;
        public string Status;   // one of the constants above
        public string Faction;  // "A", "H", or null for both / unknown
        public string From;     // for Shared rows: the nickname of the player who shared it
        public bool Both;       // seen by both factions: for everyone for sure (Faction is null); a copy
                                // tagged for one faction is replaced when it's added
        // Unique per chain: a giver's Horde and Alliance (or neutral) versions are different rows.
        public string Key { get { return Starter.Key + (IsDungeon ? "#D" : "#G") + (Faction != null ? "|" + Faction : ""); } }

        public int StatusRank { get { return Status == New ? 0 : Status == Shared ? 1 : Status == InDiscoveries ? 2 : Status == Unknown ? 3 : 4; } }

        // Is this row for a player of these factions? Neutral chains are for everyone.
        public bool ForFactions(ICollection<string> factions)
        {
            return Faction == null || factions == null || factions.Count == 0 || factions.Contains(Faction);
        }

        // Rows -> starters holding only the chosen chains (a gear row doesn't
        // drag the same giver's dungeon chain along). Merges into a copy of 'into'.
        // `played`: the factions you play (kept for callers; a neutral chain is only re-tagged when
        // the other faction has its own copy, see Target).
        public static Dictionary<string, Starter> Pack(IEnumerable<ChainRow> chosen, Dictionary<string, Starter> into, ICollection<string> played = null)
        {
            var outp = into == null ? new Dictionary<string, Starter>() : Starter.CloneAll(into);
            foreach (var r in chosen)
            {
                if (r.Both && r.Faction == null)
                {
                    // Seen by both factions: a copy tagged for one faction is taken out, and the chain
                    // goes into a neutral entry (that faction's other chain, if any, stays tagged).
                    foreach (var e in Starter.Entries(outp, r.Starter.Key))
                        if (Starter.Tag(e.Value) != null && e.Value.Holds(r.IsDungeon) && SameChain(e.Value, r.Starter, r.IsDungeon))
                        {
                            foreach (string f in r.IsDungeon ? Starter.DungeonFields : Starter.GearFields) e.Value.F.Remove(f);
                            if (!e.Value.HasGear && !e.Value.HasDungeon) outp.Remove(e.Key);
                        }
                }
                string k;
                Starter s = Target(outp, r.Starter.Key, r.Faction, r.IsDungeon, out k, played);
                // Seen by both factions: never into an entry tagged for one faction (that's another chain).
                if (r.Both && r.Faction == null && s != null && Starter.Tag(s) != null) { s = null; k = FreeKey(outp, r.Starter.Key, null); }
                bool made = s == null;
                if (made)
                {
                    s = new Starter();
                    foreach (string f in Starter.BaseFields) if (r.Starter.F.ContainsKey(f)) s.F[f] = r.Starter.F[f];
                    outp[k] = s;
                }
                foreach (string f in r.IsDungeon ? Starter.DungeonFields : Starter.GearFields)
                {
                    if (r.Starter.F.ContainsKey(f)) s.F[f] = r.Starter.F[f]; else s.F.Remove(f);
                }
                // The row's faction wins (also tags entries written before factions were known). A row
                // without one never takes a tag away: a faction's chain never becomes everyone's.
                if (r.Faction != null) s.F["faction"] = r.Faction;
                else if (made) s.F.Remove("faction");
            }
            return outp;
        }

        // The entry in 'into' that Pack writes a row (giver `key`, `faction`, gear or dungeon) to, or
        // null if it makes a new one under key `k`. Never two entries of one faction for a giver (the
        // file keeps only one of them), and never a chain under another faction's tag:
        //  - for a faction: the entry already tagged for it, else a neutral entry holding just this
        //    chain when the other faction has its own copy (it gets the tag), else a new entry;
        //  - without a faction: the neutral entry holding this chain, else a tagged one holding it
        //    (its tag is kept), else any neutral entry, else a new one.
        public static Starter Target(Dictionary<string, Starter> into, string key, string faction, bool dungeon, out string k, ICollection<string> played = null)
        {
            var es = Starter.Entries(into, key);
            KeyValuePair<string, Starter> hit;
            if (faction != null)
            {
                hit = es.FirstOrDefault(e => Starter.Tag(e.Value) == faction);
                // A neutral entry is only re-tagged when the other faction already has its own copy;
                // otherwise players of the other faction may still need it, so a new entry is made.
                string other = faction == "A" ? "H" : "A";
                bool otherCovered = es.Any(e => Starter.Tag(e.Value) == other && e.Value.Holds(dungeon));
                if (hit.Value == null && otherCovered)
                    hit = es.FirstOrDefault(e => Starter.Tag(e.Value) == null && e.Value.Holds(dungeon) && !e.Value.Holds(!dungeon));
            }
            else
            {
                hit = es.FirstOrDefault(e => Starter.Tag(e.Value) == null && e.Value.Holds(dungeon));
                if (hit.Value == null) hit = es.FirstOrDefault(e => e.Value.Holds(dungeon));
                if (hit.Value == null) hit = es.FirstOrDefault(e => Starter.Tag(e.Value) == null);
            }
            if (hit.Value != null) { k = hit.Key; return hit.Value; }
            k = FreeKey(into, key, faction);
            return null;
        }

        // A key for a new entry of this giver that doesn't write over another entry.
        static string FreeKey(Dictionary<string, Starter> into, string key, string faction)
        {
            string k = !into.ContainsKey(key) ? key : Starter.AltKey(key, faction);
            for (int n = 2; into.ContainsKey(k); n++) k = key + "|#" + n;
            return k;
        }

        // Is it the same chain (same first quest)? A giver can give one faction a different chain.
        // Not known (no first quest recorded) counts as different, so nothing is replaced by mistake.
        public static bool SameChain(Starter a, Starter b, bool dungeon)
        {
            string f = dungeon ? "dRoot" : "rootQuest";
            double? x = a.Num(f), y = b.Num(f);
            return x.HasValue && y.HasValue && x.Value == y.Value;
        }

        // Removes the chosen halves (gear or dungeon) from a copy of 'from'.
        public static Dictionary<string, Starter> Unpack(IEnumerable<ChainRow> chosen, Dictionary<string, Starter> from)
        {
            var outp = Starter.CloneAll(from);
            foreach (var r in chosen)
            {
                // The same entry the list shows the row from (see Starter.Locate).
                string k;
                Starter s = Starter.Locate(outp, r.Starter.Key, r.Faction, r.IsDungeon, out k);
                if (s == null) continue;
                foreach (string f in r.IsDungeon ? Starter.DungeonFields : Starter.GearFields) s.F.Remove(f);
                if (!s.HasGear && !s.HasDungeon) outp.Remove(k);
            }
            return outp;
        }
    }

    // Where the app keeps its own files (settings, Discoveries.lua backups).
    public static class AppPaths
    {
        public static string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuestGearScoutCompanion");
        public static string Settings { get { return Path.Combine(Root, "settings.txt"); } }
        public static string Backups { get { return Path.Combine(Root, "backups"); } }
    }

    // ---------------------------------------------------------------------
    // Reads the addon's files and works out what you've learned.
    // ---------------------------------------------------------------------
    public class AddonData
    {
        public Dictionary<string, Starter> BuiltIn = new Dictionary<string, Starter>();
        public Dictionary<string, Starter> Discovered = new Dictionary<string, Starter>();
        public Dictionary<long, long> BundledPrev = new Dictionary<long, long>();
        public Dictionary<long, string> BundledTitle = new Dictionary<long, string>();
        public Dictionary<long, string> BundledDungeon = new Dictionary<long, string>();
        public string Version = "";

        static readonly Regex LongString = new Regex(@"=\s*\[(=*)\[\r?\n?(.*?)\]\1\]", RegexOptions.Singleline);

        // Undo Lua %q escaping: \" \\ \n \ddd
        static string Unq(string s)
        {
            return Regex.Replace(s, @"\\(\d{1,3}|.)", m =>
            {
                string e = m.Groups[1].Value;
                if (char.IsDigit(e[0])) return ((char)int.Parse(e)).ToString();
                return e == "n" ? "\n" : e;
            });
        }

        static string ExportIn(string lua, string var)
        {
            int at = lua.IndexOf(var, StringComparison.Ordinal);
            if (at < 0) return "";
            var m = LongString.Match(lua, at);
            return m.Success ? m.Groups[2].Value : "";
        }

        public static AddonData Load(string addonDir)
        {
            var d = new AddonData();
            if (string.IsNullOrEmpty(addonDir) || !Directory.Exists(addonDir)) return d;
            string dataPath = Path.Combine(addonDir, "Data.lua");
            if (File.Exists(dataPath))
            {
                string lua = File.ReadAllText(dataPath);
                d.BuiltIn = Starter.Decode(ExportIn(lua, "BUNDLED_EXPORT"));
                var q = new Regex("\\[(\\d+)\\]=\\{(false|\\d+),\"((?:[^\"\\\\]|\\\\.)*)\",(false|\"((?:[^\"\\\\]|\\\\.)*)\")\\}");
                foreach (Match m in q.Matches(lua))
                {
                    long id, prev;
                    if (!long.TryParse(m.Groups[1].Value, out id)) continue;
                    if (m.Groups[2].Value != "false" && long.TryParse(m.Groups[2].Value, out prev)) d.BundledPrev[id] = prev;
                    d.BundledTitle[id] = Unq(m.Groups[3].Value);
                    if (m.Groups[4].Value != "false") d.BundledDungeon[id] = Unq(m.Groups[5].Value);
                }
            }
            string discPath = Path.Combine(addonDir, "Discoveries.lua");
            if (File.Exists(discPath)) d.Discovered = Starter.Decode(ExportIn(File.ReadAllText(discPath), "DISCOVERED_EXPORT"));
            string toc = Path.Combine(addonDir, "QuestGearScout.toc");
            if (File.Exists(toc))
            {
                d.Found = true;
                var m = Regex.Match(File.ReadAllText(toc), @"^## Version:[ \t]*(\S+)", RegexOptions.Multiline);
                if (m.Success) d.Version = m.Groups[1].Value;
            }
            return d;
        }

        // True when QuestGearScout.toc exists in the folder.
        public bool Found;

        // Belt and braces: the encoded body must never contain the long-string terminator.
        static string SafeBody(IEnumerable<Starter> starters)
        {
            string body = Starter.Encode(starters);
            if (body.Contains("]=") || body.Contains("]]")) throw new InvalidDataException("Refusing to write chain data that could break out of the Lua string.");
            return body;
        }

        // The chains in the text of a Discoveries.lua file (empty if it isn't one).
        public static Dictionary<string, Starter> DiscoveriesIn(string lua)
        {
            return Starter.Decode(ExportIn(lua ?? "", "DISCOVERED_EXPORT"));
        }

        public static string DiscoveriesText(IEnumerable<Starter> starters)
        {
            var sb = new StringBuilder();
            sb.Append("-- QuestGearScout discoveries: chains players found that aren't in the\n");
            sb.Append("-- built-in data yet. Written by the QuestGearScout Companion desktop app\n");
            sb.Append("-- (\"Add to addon\"); same text format as /qgs export.\n");
            sb.Append("local _, ns = ...\n\nns.DISCOVERED_EXPORT = [=====[\n");
            sb.Append(SafeBody(starters));
            sb.Append("\n]=====]\n");
            return sb.ToString();
        }

        // Writes Discoveries.lua with the given starters (merged per giver).
        public static void WriteDiscoveries(string addonDir, IEnumerable<Starter> starters)
        {
            File.WriteAllText(Path.Combine(addonDir, "Discoveries.lua"), DiscoveriesText(starters), new UTF8Encoding(false));
        }

        public static string SharedText(IEnumerable<Starter> starters)
        {
            var sb = new StringBuilder();
            sb.Append("-- QuestGearScout shared chains: chains other players on your local network found.\n");
            sb.Append("-- Written by the QuestGearScout Companion desktop app (local sharing); same text\n");
            sb.Append("-- format as /qgs export. Ships empty; each player's app fills in their own copy.\n");
            sb.Append("local _, ns = ...\n\nns.SHARED_EXPORT = [=====[\n");
            if (starters != null) sb.Append(SafeBody(starters)).Append('\n');
            sb.Append("]=====]\n");
            return sb.ToString();
        }

        // Writes Shared.lua only when its content changes. Returns true if it was written.
        public static bool WriteShared(string addonDir, IEnumerable<Starter> starters)
        {
            string path = Path.Combine(addonDir, "Shared.lua");
            string text = SharedText(starters);
            try { if (File.Exists(path) && File.ReadAllText(path) == text) return false; } catch { }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }

        // Copies Discoveries.lua to the app's backups folder (outside the addon,
        // so it's never zipped) and keeps the newest 20. Returns the backup path.
        public static string Backup(string addonDir)
        {
            Directory.CreateDirectory(AppPaths.Backups);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(AppPaths.Backups, "Discoveries-" + stamp + ".lua");
            for (int n = 1; File.Exists(path); n++)
                path = Path.Combine(AppPaths.Backups, "Discoveries-" + stamp + "-" + n + ".lua");
            string src = Path.Combine(addonDir, "Discoveries.lua");
            if (File.Exists(src)) File.Copy(src, path);
            else File.WriteAllText(path, DiscoveriesText(new Starter[0]), new UTF8Encoding(false));
            foreach (string old in Directory.GetFiles(AppPaths.Backups, "Discoveries-*.lua").OrderByDescending(f => f).Skip(20))
            {
                try { File.Delete(old); } catch { }
            }
            return path;
        }

        // "## Version: 1.8.0" -> 1.8.1 (last number + 1). Keeps the file's
        // encoding and line endings. Returns the new version, or null.
        public static string BumpVersion(string addonDir)
        {
            string toc = Path.Combine(addonDir, "QuestGearScout.toc");
            byte[] bytes = File.ReadAllBytes(toc);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            string result = null;
            // (?=\r?$): in Multiline mode $ only matches before \n, so allow (and keep) a CR.
            string updated = Regex.Replace(text, @"^(## Version:[ \t]*)(\S*?)(\d+)([^\s\d]*)([ \t]*)(?=\r?$)", m =>
            {
                if (result != null) return m.Value;
                long n;
                if (!long.TryParse(m.Groups[3].Value, out n)) return m.Value;
                result = m.Groups[2].Value + (n + 1) + m.Groups[4].Value;
                return m.Groups[1].Value + result + m.Groups[5].Value;
            }, RegexOptions.Multiline);
            if (result == null) return null;
            var outBytes = new List<byte>();
            if (bom) outBytes.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
            outBytes.AddRange(new UTF8Encoding(false).GetBytes(updated));
            File.WriteAllBytes(toc, outBytes.ToArray());
            return result;
        }
    }

    public static class Zipper
    {
        static readonly string[] SkipDirs = { ".git", ".svn", ".vscode", ".idea" };
        static readonly string[] SkipFiles = { "*.bak", "*.zip", "*.tmp", "*.orig", "*~", "Thumbs.db", "desktop.ini", ".DS_Store" };

        static bool Like(string name, string pattern)
        {
            if (pattern.StartsWith("*")) return name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        // Zips the addon folder with entries under "QuestGearScout/" (forward
        // slashes, as CurseForge expects), leaving out editor/OS junk.
        public static int Zip(string addonDir, string zipPath, List<string> skipped)
        {
            string rootFull = Path.GetFullPath(addonDir).TrimEnd('\\') + "\\";
            string zipFull = Path.GetFullPath(zipPath);
            if (zipFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Save the zip outside the addon folder, or it would include itself.");
            int n = 0;
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string f in Directory.GetFiles(addonDir, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = Path.GetFullPath(f).Substring(rootFull.Length);
                    string[] parts = rel.Split('\\');
                    bool skip = false;
                    for (int i = 0; i < parts.Length - 1 && !skip; i++)
                        foreach (string d in SkipDirs) if (string.Equals(parts[i], d, StringComparison.OrdinalIgnoreCase)) skip = true;
                    foreach (string p in SkipFiles) if (Like(parts[parts.Length - 1], p)) skip = true;
                    if (skip) { if (skipped != null) skipped.Add(rel); continue; }
                    if (string.Equals(rel, "Shared.lua", StringComparison.OrdinalIgnoreCase))
                    {
                        // Other players' chains are yours to see, not to publish: the release ships it empty.
                        var e = z.CreateEntry("QuestGearScout/Shared.lua");
                        using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(AddonData.SharedText(null));
                        n++;
                        continue;
                    }
                    z.CreateEntryFromFile(f, "QuestGearScout/" + rel.Replace('\\', '/'));
                    n++;
                }
            }
            return n;
        }
    }

    public class CharacterInfo
    {
        public string Name, Faction, Class;
        public int Level;
    }

    public class Learned
    {
        public Dictionary<string, Starter> Starters = new Dictionary<string, Starter>();
        public int QuestsSeen, DungeonQuests;
        public int MinQuality = 2;
        public List<CharacterInfo> Characters = new List<CharacterInfo>();   // from QuestGearScoutDB.chars (addon 1.9.0+)

        // Factions you play: from your characters, else from the quests you've taken.
        public HashSet<string> Factions = new HashSet<string>();

        // "A"/"H" for a single faction, null for both/unknown ("B" in saved data means both factions took it).
        static string OneFaction(object v)
        {
            string f = v as string;
            return f == "A" || f == "H" ? f : null;
        }

        // A learned chain's faction, the same rule as the addon (QGS.ChainFaction): learned by one
        // faction, that faction's; once the other faction has seen it (its first quest or its
        // reward quest is "B"), neutral; not known (recorded before 1.9.0), everyone's. "X" when
        // the first quest is one faction's and the reward the other's (no faction can do the
        // whole chain): such a chain is skipped.
        // Marks (not written to any file) on a learned starter whose gear / dungeon chain both
        // factions have seen.
        public const string BothGear = "_bothG", BothDungeon = "_bothD";

        public static string ChainFaction(object rootFaction, object questFaction)
        {
            if (rootFaction as string == "B" || questFaction as string == "B") return null;
            string rf = OneFaction(rootFaction), qf = OneFaction(questFaction);
            if (rf != null && qf != null && rf != qf) return "X";
            return qf;   // a reward recorded before factions were saved stays everyone's
        }

        static Dictionary<object, object> T(Dictionary<object, object> t, object k)
        {
            object v; return t != null && t.TryGetValue(k, out v) ? v as Dictionary<object, object> : null;
        }
        static object V(Dictionary<object, object> t, object k) { object v; return t != null && t.TryGetValue(k, out v) ? v : null; }
        static long? L(object o) { if (o is double) return (long)(double)o; if (o is long) return (long)o; return null; }

        // Same rules as QGS.Rebuild in the addon, using only what was learned in game
        // (plus the built-in links so learned quests join the right chain).
        public static Learned FromSavedVariables(string text, AddonData data, int minQuality)
        {
            var res = new Learned { MinQuality = minQuality };
            var g = LuaReader.ReadFile(text);
            object dbo; g.TryGetValue("QuestGearScoutDB", out dbo);
            var db = dbo as Dictionary<object, object>;
            var quests = T(db, "quests") ?? new Dictionary<object, object>();
            res.QuestsSeen = quests.Count;

            var chars = T(db, "chars");
            if (chars != null)
                foreach (var kv in chars)
                {
                    var c = kv.Value as Dictionary<object, object>;
                    if (c == null || !(kv.Key is string)) continue;
                    var ci = new CharacterInfo { Name = (string)kv.Key, Faction = OneFaction(V(c, "faction")), Class = V(c, "class") as string,
                        Level = (int)(L(V(c, "level")) ?? 0) };
                    res.Characters.Add(ci);
                    if (ci.Faction != null) res.Factions.Add(ci.Faction);
                }
            foreach (var kv in quests)
            {
                var q = kv.Value as Dictionary<object, object>;
                string f = q == null ? null : V(q, "faction") as string;
                if (f == "A" || f == "H") res.Factions.Add(f);
                else if (f == "B") { res.Factions.Add("A"); res.Factions.Add("H"); }
            }

            Func<long, Dictionary<object, object>> rec = id => T(quests, id);
            Func<long, long?> prevOf = id =>
            {
                long? p = L(V(rec(id), "prev"));
                if (!p.HasValue && data.BundledPrev.ContainsKey(id)) p = data.BundledPrev[id];
                if (p.HasValue && (rec(p.Value) != null || data.BundledTitle.ContainsKey(p.Value))) return p;
                return null;
            };
            Func<long, string> titleOf = id =>
            {
                string t = V(rec(id), "title") as string;
                if (t == null && data.BundledTitle.ContainsKey(id)) t = data.BundledTitle[id];
                return t;
            };

            foreach (var kv in quests)
            {
                long? qidN = L(kv.Key);
                var q = kv.Value as Dictionary<object, object>;
                if (!qidN.HasValue || q == null) continue;
                long qid = qidN.Value;

                long root = qid; int step = 1, guard = 0;
                long? p = prevOf(qid);
                while (p.HasValue && guard < 50) { root = p.Value; step++; guard++; p = prevOf(root); }

                bool isDungeon = V(q, "dungeon") is bool && (bool)V(q, "dungeon");
                string dName = V(q, "dungeonName") as string;
                if (data.BundledDungeon.ContainsKey(qid)) { isDungeon = true; if (dName == null) dName = data.BundledDungeon[qid]; }
                if (isDungeon) res.DungeonQuests++;

                var giver = T(rec(root), "giver");
                if (giver == null || V(giver, "map") == null) continue;
                double quality = V(q, "quality") is double ? (double)V(q, "quality") : -1;
                bool gear = quality >= minQuality;
                if (!gear && !isDungeon) continue;

                string key = (V(giver, "kind") as string) + ":" + L(V(giver, "id"));
                string faction = ChainFaction(V(rec(root), "faction"), V(q, "faction"));
                if (faction == "X") continue;   // no single faction can do it (a wrong link)
                // Seen by both factions: for everyone for sure (not just "faction unknown").
                bool both = V(rec(root), "faction") as string == "B" || V(q, "faction") as string == "B";
                // One starter per giver and faction, so each chain keeps its own faction (a giver's
                // Horde gear chain and its neutral dungeon chain stay apart).
                string sk = key;
                Starter s;
                if (res.Starters.TryGetValue(sk, out s) && Starter.Tag(s) != faction)
                {
                    sk = Starter.AltKey(key, faction);
                    res.Starters.TryGetValue(sk, out s);
                }
                if (s == null)
                {
                    s = new Starter();
                    s.F["kind"] = V(giver, "kind"); s.F["id"] = (double)L(V(giver, "id")).Value;
                    s.F["map"] = V(giver, "map"); s.F["x"] = V(giver, "x"); s.F["y"] = V(giver, "y");
                    s.F["name"] = V(giver, "name");
                    if (faction != null) s.F["faction"] = faction;
                    res.Starters[sk] = s;
                }
                if (gear)
                {
                    double? cq = s.Num("quality"), cs = s.Num("steps");
                    if (!cq.HasValue || quality > cq.Value || (quality == cq.Value && step < cs.Value))
                    {
                        s.F["quality"] = quality; s.F["steps"] = (double)step;
                        s.F["item"] = V(q, "item"); s.F["finalTitle"] = V(q, "title");
                        s.F["rootQuest"] = (double)root; s.F["rootTitle"] = titleOf(root);
                        if (both) s.F[BothGear] = true; else s.F.Remove(BothGear);
                    }
                }
                if (isDungeon)
                {
                    double? ds = s.Num("dungeonSteps");
                    if (!ds.HasValue || step < ds.Value)
                    {
                        s.F["dungeonSteps"] = (double)step; s.F["dungeonName"] = dName;
                        s.F["dungeonTitle"] = V(q, "title");
                        s.F["dRoot"] = (double)root; s.F["dRootTitle"] = titleOf(root);
                        if (both) s.F[BothDungeon] = true; else s.F.Remove(BothDungeon);
                    }
                }
            }
            return res;
        }

        // One row per chain, with whether it's new compared to the addon's data.
        public List<ChainRow> Rows(AddonData data)
        {
            var rows = new List<ChainRow>();
            foreach (var s in Starters.Values)
            {
                foreach (bool dun in new[] { false, true })
                {
                    if (dun ? !s.HasDungeon : !s.HasGear) continue;
                    string mine = Starter.Tag(s);
                    bool both = s.F.ContainsKey(dun ? BothDungeon : BothGear);
                    string kb, kd;
                    // Untagged (older records): prefer the copy for a faction you play. Seen by both
                    // factions: only a copy for everyone counts (one tagged for a single faction is
                    // out of date, so the chain shows as New and Add to addon makes it everyone's).
                    Starter b = Starter.Locate(data.BuiltIn, s.Key, mine, dun, out kb, both ? null : Factions, !both),
                            dsc = Starter.Locate(data.Discovered, s.Key, mine, dun, out kd, both ? null : Factions, !both);
                    // Without the addon we can't tell what's new, so don't claim it is.
                    string status = data.Found ? ChainRow.New : ChainRow.Unknown;
                    if (b != null && (dun ? b.HasDungeon : b.HasGear)) status = ChainRow.BuiltIn;
                    else if (dsc != null && (dun ? dsc.HasDungeon : dsc.HasGear)) status = ChainRow.InDiscoveries;
                    // Faction: what you recorded, else what Discoveries.lua or the built-in data says.
                    string f = both ? null : mine ?? (dsc != null ? dsc.Get("faction") : null) ?? (b != null ? b.Get("faction") : null);
                    rows.Add(new ChainRow { Starter = s, IsDungeon = dun, Status = status, Faction = f == "A" || f == "H" ? f : null, Both = both });
                }
            }
            return rows;
        }
    }

    public static class Zones
    {
        static readonly Dictionary<long, string> names = new Dictionary<long, string>();
        static Zones()
        {
            const string list =
                "1414=Kalimdor;1440=Ashenvale;1447=Azshara;1439=Darkshore;1457=Darnassus;1443=Desolace;1411=Durotar;" +
                "1445=Dustwallow Marsh;1448=Felwood;1444=Feralas;1450=Moonglade;1412=Mulgore;1454=Orgrimmar;1451=Silithus;" +
                "1442=Stonetalon Mountains;1446=Tanaris;1438=Teldrassil;1413=The Barrens;1441=Thousand Needles;" +
                "1456=Thunder Bluff;1449=Un'Goro Crater;1452=Winterspring;1415=Eastern Kingdoms;1416=Alterac Mountains;" +
                "1417=Arathi Highlands;1418=Badlands;33=Blackrock Mountain;1419=Blasted Lands;1428=Burning Steppes;" +
                "1430=Deadwind Pass;1426=Dun Morogh;427=Coldridge Valley;1431=Duskwood;1423=Eastern Plaguelands;" +
                "1429=Elwynn Forest;425=Northshire Valley;1424=Hillsbrad Foothills;1455=Ironforge;1432=Loch Modan;" +
                "1433=Redridge Mountains;1427=Searing Gorge;1421=Silverpine Forest;1453=Stormwind City;" +
                "1434=Stranglethorn Vale;1435=Swamp of Sorrows;1425=The Hinterlands;1420=Tirisfal Glades;1458=Undercity;" +
                "1422=Western Plaguelands;1436=Westfall;1437=Wetlands;2521=Zephras Isle;2482=Mount Hyjal;" +
                "2548=Riverglades;2652=Shen'dralas";
            foreach (string pair in list.Split(';'))
            {
                string[] kv = pair.Split('=');
                names[long.Parse(kv[0])] = kv[1];
            }
        }
        public static string Name(double? map)
        {
            if (!map.HasValue) return "";
            string n;
            return names.TryGetValue((long)map.Value, out n) ? n : ("Map " + (long)map.Value);
        }
    }

    public static class TextUtil
    {
        // "|cff...|Hitem:...|h[Tunic of Westfall]|h|r" -> "Tunic of Westfall"
        public static string Plain(string link)
        {
            if (string.IsNullOrEmpty(link)) return "";
            var m = Regex.Match(link, @"\[(.+?)\]");
            if (m.Success) return m.Groups[1].Value;
            return Regex.Replace(link, @"\|c\w{8}|\|c[^:|]*:|\|r|\|H[^|]*\|h|\|h", "");
        }
        public static readonly string[] Quality = { "Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary" };
    }
}
