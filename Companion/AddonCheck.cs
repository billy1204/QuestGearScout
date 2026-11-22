// QuestGearScout Companion - the addon checker.
//   AddonCheck.Run(gameDir)  -> is QuestGearScout installed and healthy in this game folder?
//   InstallScanner.Scan()    -> every World of Warcraft game folder on this PC, with the addon status in each.
//   AddonCheck.FixLayout()   -> moves a mis-unzipped addon folder into the right place.
// Only reads the disk, except FixLayout (which moves one folder, after the user confirms).
// C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QGSCompanion
{
    public class CheckItem
    {
        public string State;      // "ok", "warn", "bad", "info"
        public string Title;
        public string Detail;
        public string Fix;        // optional action key the UI can offer ("fix-layout", "scan", "open-addons")
        public override string ToString() { return "[" + State + "] " + Title + (string.IsNullOrEmpty(Detail) ? "" : " - " + Detail); }
    }

    public class AddonStatus
    {
        public string GameDir = "";
        public bool GameDirExists;
        public string Flavor = "";            // "_forever_", "_retail_"...
        public string Product = "";           // from .flavor.info, e.g. "wowf"
        public string GameVersion = "";       // e.g. "1.60.1.71037" from .build.info
        public int GameInterface;             // 16001 for 1.60.1
        public bool Installed;                // AddOns\QuestGearScout\QuestGearScout.toc exists
        public string AddonDir = "";
        public string Version = "";           // ## Version
        public int Interface;                 // ## Interface
        public List<string> MissingFiles = new List<string>();
        public string MisplacedToc;           // a QuestGearScout.toc found in the wrong place
        public string MisplacedProblem;       // human description of what's wrong with it
        public string MisplacedVersion = "";  // ## Version of that misplaced copy
        public List<string> DisabledFor = new List<string>();   // "Account/Realm/Character"
        public List<string> AccountsWithData = new List<string>();
        public List<CheckItem> Items = new List<CheckItem>();

        public bool InterfaceOutdated
        {
            get
            {
                bool forever = Product == "wowf" || Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase);
                return forever && Installed && GameInterface > 0 && Interface > 0 && Interface < GameInterface;
            }
        }
        public bool Healthy { get { return Installed && MissingFiles.Count == 0 && !InterfaceOutdated; } }

        // "v1.8.0", "not installed", "v1.8.0 (needs attention)"
        public string Summary
        {
            get
            {
                if (!Installed) return MisplacedToc != null ? "installed in the wrong folder" : "not installed";
                return "v" + (Version == "" ? "?" : Version) + (Healthy ? "" : " (needs attention)");
            }
        }
    }

    public static class AddonCheck
    {
        public const string Name = "QuestGearScout";

        static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        static bool DirExists(string d) { try { return !string.IsNullOrEmpty(d) && Directory.Exists(d); } catch { return false; } }
        static bool FileExists(string f) { try { return !string.IsNullOrEmpty(f) && File.Exists(f); } catch { return false; } }

        // "1.60.1.71037" -> 16001 (major*10000 + minor*100 + patch), the same number a .toc's ## Interface uses.
        public static int InterfaceFor(string version)
        {
            var m = Regex.Match(version ?? "", @"^(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return 0;
            int a, b, c;
            if (!int.TryParse(m.Groups[1].Value, out a) || !int.TryParse(m.Groups[2].Value, out b) || !int.TryParse(m.Groups[3].Value, out c)) return 0;
            if (b > 99 || c > 99) return 0;
            return a * 10000 + b * 100 + c;
        }

        static string Toc(string text, string key)
        {
            var m = Regex.Match(text, @"^##\s*" + Regex.Escape(key) + @":[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Files the .toc loads (lines that aren't comments or ## tags).
        static List<string> TocFiles(string text)
        {
            var outp = new List<string>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "" || line.StartsWith("#")) continue;
                outp.Add(line.Replace('/', '\\'));
            }
            return outp;
        }

        // Product and version from the launcher files: <WoW>\_forever_\.flavor.info and <WoW>\.build.info.
        static void ReadGameVersion(AddonStatus s)
        {
            string flavorInfo = Path.Combine(s.GameDir, ".flavor.info");
            if (FileExists(flavorInfo))
            {
                var lines = (Safe(() => File.ReadAllText(flavorInfo)) ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim() != "").ToList();
                if (lines.Count >= 2) s.Product = lines[1].Trim();
            }
            string root = Safe(() => Path.GetDirectoryName(s.GameDir.TrimEnd('\\')));
            string build = root == null ? null : Path.Combine(root, ".build.info");
            if (!FileExists(build)) return;
            var rows = (Safe(() => File.ReadAllText(build)) ?? "").TrimStart('﻿').Replace("\r", "").Split('\n').Where(l => l.Trim() != "").ToList();
            if (rows.Count < 2) return;
            var head = rows[0].Split('|').Select(h => h.Split('!')[0].Trim()).ToList();
            int vi = head.IndexOf("Version"), pi = head.IndexOf("Product"), ai = head.IndexOf("Active");
            if (vi < 0) return;
            // Active rows first (the launcher keeps inactive regions/branches in the same file).
            var ordered = rows.Skip(1).OrderByDescending(r => { var c = r.Split('|'); return ai >= 0 && c.Length > ai && c[ai].Trim() == "1"; }).ToList();
            foreach (string r in ordered)
            {
                var cols = r.Split('|');
                if (cols.Length <= vi) continue;
                string product = pi >= 0 && cols.Length > pi ? cols[pi].Trim() : "";
                // Match this folder's product ("wowf" for Forever); with no .flavor.info, take a Forever row.
                bool isForeverFolder = s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase);
                bool match = s.Product != "" ? string.Equals(product, s.Product, StringComparison.OrdinalIgnoreCase)
                                             : isForeverFolder && (product.Equals("wowf", StringComparison.OrdinalIgnoreCase) || rows.Count == 2);
                if (match) { s.GameVersion = cols[vi].Trim(); break; }
            }
            s.GameInterface = InterfaceFor(s.GameVersion);
        }

        static Version ParseVersion(string v)
        {
            var m = Regex.Match(v ?? "", @"\d+(\.\d+){0,3}");
            Version outp;
            if (!m.Success) return new Version(0, 0);
            string t = m.Value.Contains(".") ? m.Value : m.Value + ".0";
            return Version.TryParse(t, out outp) ? outp : new Version(0, 0);
        }

        // Looks for QuestGearScout.toc files that aren't at AddOns\QuestGearScout\QuestGearScout.toc:
        //   AddOns\QuestGearScout-1.8.0\QuestGearScout.toc          (folder renamed)
        //   AddOns\<x>\QuestGearScout\QuestGearScout.toc             (unzipped one folder too deep)
        //   AddOns\QuestGearScout\<x>\QuestGearScout.toc             (right outer name, wrong inner folder)
        // When there are several, the newest version (then the newest files) is chosen.
        static void FindMisplaced(AddonStatus s, string addons)
        {
            if (!DirExists(addons)) return;
            string[] subs;
            try { subs = Directory.GetDirectories(addons); } catch { return; }
            var cands = new List<KeyValuePair<string, string>>();   // toc path -> problem text
            foreach (string d in subs)
            {
                string name = Path.GetFileName(d);
                bool isTarget = string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);
                if (!isTarget && FileExists(Path.Combine(d, Name + ".toc")))
                    cands.Add(new KeyValuePair<string, string>(Path.Combine(d, Name + ".toc"),
                        "Its folder is called \"" + name + "\". WoW only loads it from a folder named exactly \"" + Name + "\"."));
                string[] inner;
                try { inner = Directory.GetDirectories(d); } catch { continue; }
                foreach (string sub in inner)
                {
                    string toc = Path.Combine(sub, Name + ".toc");
                    if (FileExists(toc))
                        cands.Add(new KeyValuePair<string, string>(toc,
                            "It was unzipped one folder too deep: AddOns\\" + name + "\\" + Path.GetFileName(sub) + ". WoW only looks one level down."));
                }
            }
            if (cands.Count == 0) return;
            var best = cands.OrderByDescending(c => ParseVersion(Toc(Safe(() => File.ReadAllText(c.Key)) ?? "", "Version")))
                            .ThenByDescending(c => { try { return File.GetLastWriteTimeUtc(c.Key); } catch { return DateTime.MinValue; } })
                            .First();
            s.MisplacedToc = best.Key;
            s.MisplacedVersion = Toc(Safe(() => File.ReadAllText(best.Key)) ?? "", "Version");
            s.MisplacedProblem = best.Value + (s.MisplacedVersion != "" ? " (v" + s.MisplacedVersion + ")" : "") +
                (cands.Count > 1 ? " " + cands.Count + " copies were found; the newest one will be used." : "");
        }

        public static AddonStatus Run(string gameDir)
        {
            var s = new AddonStatus { GameDir = (gameDir ?? "").TrimEnd('\\') };
            s.GameDirExists = DirExists(s.GameDir);
            s.Flavor = s.GameDir == "" ? "" : Path.GetFileName(s.GameDir);
            if (!s.GameDirExists)
            {
                s.Items.Add(new CheckItem { State = "bad", Title = "Game folder not found", Detail = s.GameDir == "" ? "No folder picked yet." : s.GameDir, Fix = "scan" });
                return s;
            }
            ReadGameVersion(s);
            string flavorWord = s.Product == "wowf" || s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase) ? "WoW Forever" : "WoW (" + s.Flavor + ")";
            s.Items.Add(new CheckItem
            {
                State = s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase) || s.Product == "wowf" ? "ok" : "warn",
                Title = "Game folder: " + flavorWord,
                Detail = s.GameDir + (s.GameVersion != "" ? "  -  game version " + s.GameVersion : ""),
                Fix = s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase) || s.Product == "wowf" ? null : "scan"
            });
            if (!(s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase) || s.Product == "wowf"))
                s.Items[0].Detail += "  -  QuestGearScout is made for WoW Forever (the _forever_ folder).";

            string addons = Path.Combine(s.GameDir, "Interface", "AddOns");
            s.AddonDir = Path.Combine(addons, Name);
            string toc = Path.Combine(s.AddonDir, Name + ".toc");
            s.Installed = FileExists(toc);
            if (!s.Installed)
            {
                FindMisplaced(s, addons);
                if (s.MisplacedToc != null)
                    s.Items.Add(new CheckItem { State = "bad", Title = "QuestGearScout is in the wrong folder", Detail = s.MisplacedProblem, Fix = "fix-layout" });
                else
                    s.Items.Add(new CheckItem { State = "bad", Title = "QuestGearScout isn't installed here",
                        Detail = (DirExists(s.AddonDir) ? "There's a " + Name + " folder in AddOns, but no " + Name + ".toc inside it. Reinstall the addon from its zip."
                                                        : "No " + Name + " folder in " + addons + ". It may be installed in another game folder."), Fix = "scan" });
            }
            else
            {
                string text = Safe(() => File.ReadAllText(toc)) ?? "";
                s.Version = Toc(text, "Version");
                int iface;
                if (int.TryParse(Toc(text, "Interface").Split(',')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out iface)) s.Interface = iface;
                s.Items.Add(new CheckItem { State = "ok", Title = "QuestGearScout v" + (s.Version == "" ? "?" : s.Version) + " installed", Detail = s.AddonDir, Fix = "open-addons" });

                foreach (string f in TocFiles(text))
                    if (!FileExists(Path.Combine(s.AddonDir, f))) s.MissingFiles.Add(f);
                if (s.MissingFiles.Count > 0)
                    s.Items.Add(new CheckItem { State = "bad", Title = "Files missing", Detail = string.Join(", ", s.MissingFiles.ToArray()) + ". Reinstall the addon from its zip." });
                else
                    s.Items.Add(new CheckItem { State = "ok", Title = "All addon files present", Detail = TocFiles(text).Count + " files listed in " + Name + ".toc" });

                bool forever = s.Product == "wowf" || s.Flavor.Equals("_forever_", StringComparison.OrdinalIgnoreCase);
                if (!forever) { }   // a different game: the folder warning above already says so; versions aren't comparable
                else if (s.Interface > 0 && s.GameInterface > 0)
                {
                    if (s.Interface < s.GameInterface)
                        s.Items.Add(new CheckItem { State = "warn", Title = "Made for an older game version",
                            Detail = "The addon says " + s.Interface + ", the game is " + s.GameInterface + " (" + s.GameVersion + "). WoW lists it as out of date; tick \"Load out of date AddOns\" in the game's AddOns menu, or update the addon." });
                    else
                        s.Items.Add(new CheckItem { State = "ok", Title = "Up to date for this game version", Detail = "Addon " + s.Interface + ", game " + s.GameInterface + " (" + s.GameVersion + ")" });
                }
                else if (s.Interface > 0)
                    s.Items.Add(new CheckItem { State = "info", Title = "Built for game interface " + s.Interface, Detail = "The game's version couldn't be read, so this wasn't compared." });
            }

            // Per-character "disabled" settings and saved data, from WTF.
            string wtf = Path.Combine(s.GameDir, "WTF", "Account");
            if (DirExists(wtf))
            {
                string[] accounts;
                try { accounts = Directory.GetDirectories(wtf); } catch { accounts = new string[0]; }
                foreach (string acc in accounts)
                {
                    if (FileExists(Path.Combine(acc, "SavedVariables", Name + ".lua"))) s.AccountsWithData.Add(Path.GetFileName(acc));
                    string[] lists;
                    try { lists = Directory.GetFiles(acc, "AddOns.txt", SearchOption.AllDirectories); } catch { continue; }
                    foreach (string list in lists.Take(200))
                    {
                        string txt = Safe(() => File.ReadAllText(list)) ?? "";
                        if (Regex.IsMatch(txt, @"^" + Name + @":\s*disabled\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                        {
                            string rel = list.Substring(wtf.Length).Trim('\\');
                            rel = rel.Substring(0, Math.Max(0, rel.Length - "AddOns.txt".Length)).Trim('\\').Replace('\\', '/');
                            s.DisabledFor.Add(rel == "" ? Path.GetFileName(acc) : rel);
                        }
                    }
                }
            }
            if (s.Installed)
            {
                if (s.DisabledFor.Count > 0)
                    s.Items.Add(new CheckItem { State = "warn", Title = "Turned off for " + s.DisabledFor.Count + (s.DisabledFor.Count == 1 ? " character" : " characters"),
                        Detail = string.Join(", ", s.DisabledFor.Take(6).ToArray()) + (s.DisabledFor.Count > 6 ? ", ..." : "") + ". Turn it on in the AddOns menu on the character select screen." });
                s.Items.Add(s.AccountsWithData.Count > 0
                    ? new CheckItem { State = "ok", Title = "Saved data found", Detail = "Account " + string.Join(", ", s.AccountsWithData.ToArray()) }
                    : new CheckItem { State = "info", Title = "No saved data yet", Detail = "Log in with the addon on, then type /reload or log out." });
            }
            return s;
        }

        static string FreeName(string basePath)
        {
            string p = basePath;
            for (int n = 2; Directory.Exists(p) || File.Exists(p); n++) p = basePath + "-" + n;
            return p;
        }

        // Moves a mis-placed addon folder to AddOns\QuestGearScout. Returns a description of what was done.
        // Nothing is deleted except a folder the move leaves completely empty. If any step fails, the
        // steps already done are undone (best effort) before the error is passed on, so the addon is
        // never left half-moved under a temporary name.
        public static string FixLayout(AddonStatus s)
        {
            if (s.MisplacedToc == null) throw new InvalidOperationException("Nothing to fix.");
            string addons = Path.Combine(s.GameDir, "Interface", "AddOns").TrimEnd('\\');
            string src = Path.GetDirectoryName(s.MisplacedToc).TrimEnd('\\');     // the folder holding QuestGearScout.toc
            string target = Path.Combine(addons, Name);
            string parent = Path.GetDirectoryName(src).TrimEnd('\\');
            bool deep = !string.Equals(parent, addons, StringComparison.OrdinalIgnoreCase);
            bool parentIsTarget = string.Equals(parent, target, StringComparison.OrdinalIgnoreCase);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            // Steps done so far, so they can be undone: (from, to) pairs of completed moves.
            var done = new List<KeyValuePair<string, string>>();
            Action<string, string> move = (from, to) => { Directory.Move(from, to); done.Add(new KeyValuePair<string, string>(from, to)); };
            string deletedEmpty = null;
            string note = "";
            try
            {
                if (!deep)
                {
                    // Renamed folder: AddOns\QuestGearScout-1.8.0 -> AddOns\QuestGearScout.
                    // A QuestGearScout folder already there has no .toc (else the addon would be installed): keep it aside.
                    if (Directory.Exists(target))
                    {
                        string old = FreeName(target + ".old-" + stamp);
                        move(target, old);
                        note = " The old, incomplete \"" + Name + "\" folder was kept as \"" + Path.GetFileName(old) + "\".";
                    }
                    move(src, target);
                    return "Renamed \"" + Path.GetFileName(src) + "\" to \"" + Name + "\"." + note;
                }

                // One level too deep: AddOns\<X>\<inner> -> AddOns\QuestGearScout
                string temp = FreeName(Path.Combine(addons, Name + ".moving-" + stamp));
                move(src, temp);
                bool parentEmpty = !Directory.EnumerateFileSystemEntries(parent).Any();
                if (parentIsTarget)
                {
                    if (parentEmpty) { Directory.Delete(parent); deletedEmpty = parent; }
                    else
                    {
                        string old = FreeName(target + ".old-" + stamp);
                        move(target, old);
                        note = " The other files that were in \"" + Name + "\" were kept in \"" + Path.GetFileName(old) + "\".";
                    }
                }
                else
                {
                    if (Directory.Exists(target))
                    {
                        string old = FreeName(target + ".old-" + stamp);
                        move(target, old);
                        note = " The old, incomplete \"" + Name + "\" folder was kept as \"" + Path.GetFileName(old) + "\".";
                    }
                    if (parentEmpty) { try { Directory.Delete(parent); } catch { } }
                }
                move(temp, target);
                return "Moved the addon to AddOns\\" + Name + "." + note;
            }
            catch
            {
                // Undo in reverse order. A folder deleted because it was empty is recreated first.
                if (deletedEmpty != null) { try { Directory.CreateDirectory(deletedEmpty); } catch { } }
                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        string back = done[i].Key;
                        string parentOfBack = Path.GetDirectoryName(back);
                        if (!Directory.Exists(parentOfBack)) Directory.CreateDirectory(parentOfBack);
                        if (!Directory.Exists(back)) Directory.Move(done[i].Value, back);
                    }
                    catch { }
                }
                throw;
            }
        }
    }

    // A game folder found on this PC.
    public class FoundInstall
    {
        public string Path;
        public AddonStatus Status;
        public override string ToString() { return Path + "  -  " + Status.Summary; }
    }

    public static class InstallScanner
    {
        static readonly string[] SkipNames = { "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "PerfLogs",
            "AppData", "node_modules", ".git", "Microsoft", "WindowsApps", "Temp" };

        static bool IsGameFolder(string d)
        {
            try { return Directory.Exists(System.IO.Path.Combine(d, "Interface")) || Directory.Exists(System.IO.Path.Combine(d, "WTF")); } catch { return false; }
        }

        // Flavor folders (_forever_, _retail_...) under a World of Warcraft folder, or the folder itself.
        static IEnumerable<string> FlavorsIn(string root)
        {
            var outp = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    string n = System.IO.Path.GetFileName(d);
                    if (n.StartsWith("_") && n.EndsWith("_") && IsGameFolder(d)) outp.Add(d);
                }
                if (outp.Count == 0 && IsGameFolder(root)) outp.Add(root);
            }
            catch { }
            return outp;
        }

        // Finds World of Warcraft game folders: the Blizzard registry entry, the usual install
        // places on every fixed drive, then a shallow search (3 levels) for "World of Warcraft" or
        // "_forever_" folders. Stops after about maxSeconds.
        public static List<FoundInstall> Scan(int maxSeconds, Func<bool> cancelled)
        {
            var started = DateTime.UtcNow;
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<bool> timeUp = () => (cancelled != null && cancelled()) || (DateTime.UtcNow - started).TotalSeconds > maxSeconds;

            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                    {
                        string p = k == null ? null : k.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(p))
                        {
                            p = p.TrimEnd('\\');
                            string n = System.IO.Path.GetFileName(p);
                            roots.Add(n.StartsWith("_") && n.EndsWith("_") ? System.IO.Path.GetDirectoryName(p) : p);
                        }
                    }
                }
                catch { }
            }

            var drives = new List<string>();
            try { drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); } catch { }
            foreach (string dr in drives)
                foreach (string rel in new[] { "World of Warcraft", @"Program Files (x86)\World of Warcraft", @"Program Files\World of Warcraft",
                                               @"Games\World of Warcraft", @"Blizzard\World of Warcraft", @"Battle.net\World of Warcraft", @"Blizzard Games\World of Warcraft" })
                {
                    string p = System.IO.Path.Combine(dr, rel);
                    try { if (Directory.Exists(p)) roots.Add(p); } catch { }
                }

            // Shallow search for less usual places (e.g. D:\Stuff\Games\World of Warcraft).
            foreach (string dr in drives)
            {
                var queue = new Queue<KeyValuePair<string, int>>();
                queue.Enqueue(new KeyValuePair<string, int>(dr, 0));
                int visited = 0;
                while (queue.Count > 0 && !timeUp() && visited < 6000)
                {
                    var cur = queue.Dequeue();
                    visited++;
                    string[] subs;
                    try { subs = Directory.GetDirectories(cur.Key); } catch { continue; }
                    foreach (string d in subs)
                    {
                        string n = System.IO.Path.GetFileName(d);
                        if (SkipNames.Contains(n, StringComparer.OrdinalIgnoreCase) || n.StartsWith(".")) continue;
                        if (n.Equals("World of Warcraft", StringComparison.OrdinalIgnoreCase)) { roots.Add(d); continue; }
                        if (n.Equals("_forever_", StringComparison.OrdinalIgnoreCase)) { roots.Add(System.IO.Path.GetDirectoryName(d)); continue; }
                        if (cur.Value < 3) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
                }
            }

            var found = new List<FoundInstall>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
                foreach (string f in FlavorsIn(r))
                    if (seen.Add(f.TrimEnd('\\'))) found.Add(new FoundInstall { Path = f.TrimEnd('\\'), Status = AddonCheck.Run(f) });
            // Forever folders first, then those with the addon, then the rest.
            return found.OrderByDescending(f => f.Status.Product == "wowf" || System.IO.Path.GetFileName(f.Path).Equals("_forever_", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(f => f.Status.Installed).ThenBy(f => f.Path).ToList();
        }

        // The other folder to suggest when the current one has no addon: a WoW Forever folder with it
        // installed. Never a retail/classic folder - the addon is made for Forever.
        public static FoundInstall Suggest(List<FoundInstall> found, string current)
        {
            return found.FirstOrDefault(f => f.Status.Installed && !string.Equals(f.Path, (current ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                             && (f.Status.Product == "wowf" || System.IO.Path.GetFileName(f.Path).Equals("_forever_", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
