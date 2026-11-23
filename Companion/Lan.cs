// QuestGearScout Companion - sharing chains and stats with other players on the local network.
//
// How it works:
//   * Every few seconds each app broadcasts a small, signed UDP "beacon" on the local network:
//       QGSC3|<group id>|<player id>|<tcp port>|<data version>|<time>|<nickname>|<sig>
//     The group id and the signing key come from the group code through PBKDF2 (200,000 rounds),
//     so guessing a code from what's heard on the network is very slow. Apps with a different
//     code ignore each other. (QGSC2, before 1.10.2, used a fast hash: those apps are only
//     noticed, so the user can be told to ask that player to update.)
//   * When an app sees a beacon from the same group with a data version it hasn't got yet, it
//     connects to that player's TCP port and asks for their profile: nickname, stats and the
//     chains they learned themselves (with Horde/Alliance marked). A player id stays with the
//     computer it was first heard from, one computer can only bring a few ids, and every
//     download and incoming connection has a time limit.
//   * A group with a name also announces it ("QGSC3G|<group id>|<player id>|<time>|<named at>|<name>|<sig>")
//     so players who aren't in a group yet can see it in the "Local group" sidebar. They still need
//     the code to join: the app checks the code they type against the group's hash. Only members
//     can check the signature; they take a newer signed name, so a rename reaches everyone.
//     A listen-only engine (no group) only hears these; it sends nothing.
//   * Everything received is treated as untrusted: sizes are capped and every field is checked.
//   * What each player last sent is kept in %APPDATA%\QuestGearScoutCompanion\shared, so the
//     leaderboard and shared chains are still there when they're offline.
// Nothing ever leaves the local network, and the app only answers apps that know the group code.
// C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace QGSCompanion
{
    public class PeerData
    {
        public string Id = "", Nick = "";
        public PlayerStats Stats = new PlayerStats();
        public Dictionary<string, Starter> Chains = new Dictionary<string, Starter>();
        public DateTime LastSeen;
        public string Address = "";
        public string Group = "";   // hash of the group they were heard in ("" if not known, e.g. saved by an older version)
    }

    public static class Profile
    {
        public const int MaxPayload = 2 * 1024 * 1024;
        public const int MaxChains = 5000;

        // The group code goes through PBKDF2 with many rounds, so testing guesses against what's heard
        // on the network (the group id in every beacon) is very slow. The id that's sent and the
        // secret key both come from that one result; the key never goes over the network.
        public const int KdfRounds = 200000;
        static readonly Dictionary<string, byte[]> kdfCache = new Dictionary<string, byte[]>();

        static string NormCode(string code) { return (code ?? "").Trim().ToLowerInvariant(); }

        static byte[] Kdf(string code)
        {
            string c = NormCode(code);
            lock (kdfCache)
            {
                byte[] b;
                if (kdfCache.TryGetValue(c, out b)) return b;
                using (var d = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes("qgsc3-group-code"), KdfRounds))
                    b = d.GetBytes(20);
                if (kdfCache.Count > 32) kdfCache.Clear();
                kdfCache[c] = b;
                return b;
            }
        }

        static byte[] Sha(string label, byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(label).Concat(data).ToArray());
        }

        public static string GroupHash(string code)
        {
            return string.Concat(Sha("qgsc3-id|", Kdf(code)).Take(16).Select(b => b.ToString("x2")).ToArray());
        }

        // Secret key from the group code. It never goes over the network; it signs requests and replies.
        public static byte[] GroupKey(string code) { return Sha("qgsc3-key|", Kdf(code)); }

        // The group id an app from before 1.10.2 sends for this code (a fast hash): only used to
        // notice that a player in your group still has an older app, never sent.
        public static string LegacyGroupHash(string code)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("qgsc-group|" + NormCode(code))).Take(16).Select(b => b.ToString("x2")).ToArray());
        }

        // How easy a group code is to guess: null if it's fine, else what's wrong.
        public static string WeakCode(string code)
        {
            string c = NormCode(code);
            if (c.Length < 12) return "Use a longer code: at least 12 characters (a few words is easiest).";
            if (c.Distinct().Count() < 6) return "Use a code with more different letters.";
            return null;
        }

        // A code that's hard to guess: five random words and a number (about 40 bits), e.g. "amber-falcon-river-hollow-moss-27".
        static readonly string[] CodeWords = {
            "amber", "anvil", "arrow", "ash", "badger", "banner", "barrow", "beacon", "birch", "bramble", "bridge", "brook", "candle", "canyon",
            "cedar", "cinder", "cliff", "clover", "cobalt", "comet", "copper", "coral", "crane", "crest", "crow", "dagger", "dawn", "delta",
            "drum", "dune", "eagle", "ember", "falcon", "fern", "flint", "forge", "fox", "frost", "gale", "garnet", "glade", "granite",
            "griffin", "grove", "harbor", "hawk", "hazel", "heron", "hollow", "horizon", "iron", "ivy", "jade", "juniper", "kestrel", "lantern",
            "lark", "ledger", "lichen", "lily", "lynx", "maple", "marsh", "meadow", "mesa", "mist", "moon", "moss", "nettle", "oak",
            "onyx", "orchid", "otter", "owl", "pebble", "pine", "plume", "quarry", "quartz", "raven", "reed", "ridge", "river", "rook",
            "rowan", "saber", "sage", "shale", "shield", "silver", "slate", "sparrow", "spruce", "stag", "stone", "storm", "summit", "tallow",
            "thistle", "thorn", "thunder", "timber", "torch", "tundra", "valley", "vale", "willow", "wolf", "wren", "yarrow", "zephyr" };

        public static string SuggestCode()
        {
            var b = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var words = Enumerable.Range(0, 5).Select(i => CodeWords[((b[i * 2] << 8) | b[i * 2 + 1]) % CodeWords.Length]).ToArray();
            return string.Join("-", words) + "-" + (10 + (((b[10] << 8) | b[11]) % 90)).ToString(CultureInfo.InvariantCulture);
        }

        public static string Sign(byte[] key, string text)
        {
            using (var h = new HMACSHA256(key))
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")).ToArray());
        }

        // Compares two signatures without leaking how much matched.
        public static bool SameSig(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        public static bool IsId(string s) { return s != null && Regex.IsMatch(s, "^[0-9a-f]{32}$"); }

        // Whether a code belongs to the group with this public hash (as heard in its beacons).
        public static bool CodeMatches(string code, string groupHash)
        {
            return groupHash != null && (code ?? "").Trim().Length > 0 && GroupHash(code) == groupHash;
        }

        // Group names follow the nickname rules (they're shown to everyone on the network).
        public static string CleanGroupName(string s) { return CleanNick(s); }

        // Nicknames: printable, no separators, at most 24 characters.
        public static string CleanNick(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c) && c != '|' && c != '=' && c != ';') sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > 24 ? t.Substring(0, 24) : t;
        }

        static readonly Regex ItemLink = new Regex(@"^\|c[0-9A-Za-z:]{1,12}\|Hitem:[0-9:\-]{1,120}\|h\[[^\]\|\r\n]{1,80}\]\|h\|r$");

        // Makes a chain received from another player safe to store, show and hand to the addon.
        // Returns null if it isn't usable.
        public static Starter Sanitize(Starter s)
        {
            var o = new Starter();
            string kind = s.Get("kind");
            if (kind != "C" && kind != "O") return null;
            double? id = s.Num("id"), map = s.Num("map"), x = s.Num("x"), y = s.Num("y");
            if (!id.HasValue || id < 1 || id > 1e9 || !map.HasValue || map < 1 || map > 1e6) return null;
            if (!x.HasValue || !y.HasValue || x < 0 || x > 1 || y < 0 || y > 1) return null;
            o.F["kind"] = kind; o.F["id"] = Math.Floor(id.Value); o.F["map"] = Math.Floor(map.Value); o.F["x"] = x.Value; o.F["y"] = y.Value;
            Action<string, int> text = (k, max) =>
            {
                string v = s.Get(k);
                if (string.IsNullOrEmpty(v)) return;
                var sb = new StringBuilder();
                foreach (char c in v) if (!char.IsControl(c) && c != '|') sb.Append(c);
                string t = sb.ToString().Trim();
                if (t.Length > max) t = t.Substring(0, max);
                if (t != "") o.F[k] = t;
            };
            Action<string, double, double> num = (k, lo, hi) =>
            {
                double? v = s.Num(k);
                if (v.HasValue && v >= lo && v <= hi) o.F[k] = Math.Floor(v.Value);
            };
            text("name", 80);
            num("quality", 0, 7); num("steps", 1, 60); num("rootQuest", 1, 1e9);
            text("rootTitle", 120); text("finalTitle", 120);
            string item = s.Get("item");
            if (item != null && ItemLink.IsMatch(item)) o.F["item"] = item;   // only real item links
            num("dRoot", 1, 1e9); num("dungeonSteps", 1, 60);
            text("dungeonName", 80); text("dungeonTitle", 120); text("dRootTitle", 120);
            string f = s.Get("faction");
            if (f == "A" || f == "H" || f == "B") o.F["faction"] = f;   // "B": seen by both factions (for everyone)
            string cls = s.Get("classes");
            if (cls != null && Regex.IsMatch(cls, "^[A-Z]{2,15}(,[A-Z]{2,15}){0,12}$")) o.F["classes"] = cls;
            if (!o.HasGear && !o.HasDungeon) return null;
            if (o.HasGear && !o.F.ContainsKey("steps")) o.F["steps"] = 1.0;
            return o;
        }

        public static string Build(string id, string nick, PlayerStats stats, IEnumerable<Starter> chains)
        {
            var sb = new StringBuilder();
            sb.Append("QGSC-PROFILE 1\n");
            sb.Append("id=").Append(id).Append('\n');
            sb.Append("nick=").Append(CleanNick(nick)).Append('\n');
            sb.Append("stats=").Append(stats.Serialize()).Append('\n');
            sb.Append("chains\n");
            sb.Append(Starter.Encode(chains.Take(MaxChains)));
            sb.Append('\n');
            return sb.ToString();
        }

        public static PeerData Parse(string payload)
        {
            if (payload == null || payload.Length > MaxPayload) return null;
            payload = payload.Replace("\r", "");
            if (!payload.StartsWith("QGSC-PROFILE 1\n")) return null;
            int at = payload.IndexOf("\nchains\n", StringComparison.Ordinal);
            if (at < 0) return null;
            var p = new PeerData();
            foreach (string line in payload.Substring(0, at).Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k == "id") p.Id = v.Trim();
                else if (k == "nick") p.Nick = CleanNick(v);
                else if (k == "stats") p.Stats = PlayerStats.Parse(v);
            }
            if (!IsId(p.Id)) return null;
            if (p.Nick == "") p.Nick = "Player " + p.Id.Substring(0, 4);
            int n = 0;
            foreach (var kv in Starter.Decode(payload.Substring(at + "\nchains\n".Length)))
            {
                if (++n > MaxChains) break;
                var clean = Sanitize(kv.Value);
                // One entry per giver and faction (as cleaned): a giver's Horde entry and Alliance entry
                // both stay; a second one for the same faction is dropped.
                if (clean == null) continue;
                string ck = clean.Key;
                Starter first;
                if (p.Chains.TryGetValue(ck, out first))
                {
                    if (Starter.FactionKey(first) == Starter.FactionKey(clean)) continue;
                    ck = Starter.AltKey(clean.Key, Starter.FactionKey(clean));
                    if (p.Chains.ContainsKey(ck)) continue;
                }
                p.Chains[ck] = clean;
            }
            p.Stats.Id = p.Id; p.Stats.Nick = p.Nick;
            return p;
        }

        public static string Version(string payload)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(payload ?? "")).Take(8).Select(b => b.ToString("x2")).ToArray());
        }
    }

    // What each player last sent, kept on disk.
    public static class SharedStore
    {
        public static string Dir { get { return Path.Combine(AppPaths.Root, "shared"); } }

        // At most this many players are kept (the ones heard longest ago go first).
        public const int MaxStored = 200;

        // Stores a player's data as the app understood it (cleaned and rebuilt, so padding or junk in
        // what they sent isn't kept). group: the group they were heard in, kept next to it in <id>.grp.
        public static void Save(PeerData p, string group)
        {
            if (p == null) return;
            Save(p.Id, Profile.Build(p.Id, p.Nick, p.Stats, p.Chains.Values), group);
        }

        public static void Save(string id, string payload, string group)
        {
            if (!Profile.IsId(id)) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, id + ".txt"), payload, new UTF8Encoding(false));
            if (Profile.IsId(group)) File.WriteAllText(Path.Combine(Dir, id + ".grp"), group, new UTF8Encoding(false));
        }

        public static List<PeerData> LoadAll()
        {
            var outp = new List<PeerData>();
            try
            {
                if (!Directory.Exists(Dir)) return outp;
                // The most recently heard players only (a flood of made-up players can't slow startup).
                foreach (string f in Directory.GetFiles(Dir, "*.txt").OrderByDescending(x => File.GetLastWriteTimeUtc(x)).Take(MaxStored))
                {
                    try
                    {
                        if (new FileInfo(f).Length > Profile.MaxPayload) continue;
                        var p = Profile.Parse(File.ReadAllText(f));
                        if (p == null || p.Id + ".txt" != Path.GetFileName(f)) continue;
                        p.LastSeen = File.GetLastWriteTime(f);
                        string gf = Path.Combine(Dir, p.Id + ".grp");
                        if (File.Exists(gf) && new FileInfo(gf).Length < 100)
                        {
                            string grp = File.ReadAllText(gf).Trim();
                            if (Profile.IsId(grp)) p.Group = grp;
                        }
                        outp.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            return outp;
        }

        public static void Forget(string id)
        {
            if (!Profile.IsId(id)) return;
            try { File.Delete(Path.Combine(Dir, id + ".txt")); } catch { }
            try { File.Delete(Path.Combine(Dir, id + ".grp")); } catch { }
        }
    }

    // A group someone on the network is sharing in, as seen from outside it.
    public class NearbyGroup
    {
        public string Hash = "", Name;   // Name is null for groups from apps that don't announce one
        public int Online;
    }

    // Groups heard on the local network: their public hash, the names their players announce and
    // who's in them. Nothing here is signed (we don't know their codes), so it's only a label for
    // the list: joining needs the code, and a joiner never takes the name from here (it waits for a
    // name signed with the code). Each player counts once; the name most of a group's online
    // players announce is shown (newest rename on a tie), so one player can't make it flicker.
    // Thread-safe.
    public class GroupDirectory
    {
        public const int MaxGroups = 64, MaxMembers = 64;
        public int OnlineSeconds = 30;

        class Said { public string Name; public long NamedAt; }
        class Entry
        {
            public readonly Dictionary<string, DateTime> Members = new Dictionary<string, DateTime>();
            public readonly Dictionary<string, Said> Names = new Dictionary<string, Said>();   // per player
        }
        readonly Dictionary<string, Entry> groups = new Dictionary<string, Entry>();
        readonly object gate = new object();

        // Records a beacon (name null) or a name announcement. Returns true when it's worth
        // redrawing for (a new group, a new player in a group, or a player's name changed).
        public bool Heard(string hash, string member, string name, long namedAt, DateTime nowUtc)
        {
            if (!Profile.IsId(hash) || !Profile.IsId(member)) return false;
            if (name != null) { name = Profile.CleanGroupName(name); if (name == "") name = null; }
            lock (gate)
            {
                Prune(nowUtc);
                Entry e;
                bool news = false;
                if (!groups.TryGetValue(hash, out e))
                {
                    if (groups.Count >= MaxGroups) return false;
                    groups[hash] = e = new Entry();
                    news = true;
                }
                if (!e.Members.ContainsKey(member))
                {
                    if (e.Members.Count >= MaxMembers) return news;
                    news = true;
                }
                e.Members[member] = nowUtc;
                if (name != null)
                {
                    Said old;
                    if (!e.Names.TryGetValue(member, out old) || old.Name != name) news = true;
                    e.Names[member] = new Said { Name = name, NamedAt = namedAt };
                }
                return news;
            }
        }

        void Prune(DateTime nowUtc)
        {
            foreach (var g in groups.ToList())
            {
                foreach (var m in g.Value.Members.Where(kv => (nowUtc - kv.Value).TotalSeconds > OnlineSeconds * 2).Select(kv => kv.Key).ToList())
                {
                    g.Value.Members.Remove(m);
                    g.Value.Names.Remove(m);
                }
                if (g.Value.Members.Count == 0) groups.Remove(g.Key);
            }
        }

        string ShownName(Entry e, DateTime nowUtc)
        {
            var best = e.Names.Where(kv => (nowUtc - e.Members[kv.Key]).TotalSeconds <= OnlineSeconds)
                .GroupBy(kv => kv.Value.Name)
                .Select(gr => new { Name = gr.Key, Count = gr.Count(), Newest = gr.Max(kv => kv.Value.NamedAt) })
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.Newest).ThenBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            return best == null ? null : best.Name;
        }

        // Groups with someone online right now, busiest first.
        public List<NearbyGroup> Snapshot(DateTime nowUtc)
        {
            lock (gate)
            {
                Prune(nowUtc);
                return groups.Select(kv => new NearbyGroup { Hash = kv.Key, Name = ShownName(kv.Value, nowUtc),
                        Online = kv.Value.Members.Count(m => (nowUtc - m.Value).TotalSeconds <= OnlineSeconds) })
                    .Where(g => g.Online > 0)
                    .OrderByDescending(g => g.Online).ThenBy(g => g.Name ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Hash, StringComparer.Ordinal)
                    .ToList();
            }
        }

        public void Clear() { lock (gate) groups.Clear(); }
    }

    public class LanShare : IDisposable
    {
        public int UdpPort = 47923;
        public IPAddress Bind = IPAddress.Any;           // IPAddress.Loopback for tests
        public List<IPEndPoint> ExtraTargets = new List<IPEndPoint>();   // tests: send beacons straight to these
        public int BeaconMs = 5000;
        public bool BroadcastBeacons = true;

        public readonly string Id, Group;   // Group is null for a listen-only engine
        readonly string legacyGroup;        // what an app from before 1.10.2 sends for our code
        readonly byte[] key;
        string nick = "", payload = "", version = "", groupName = "";
        long groupNamedAt;
        readonly object gate = new object();
        UdpClient udp;
        TcpListener tcp;
        int tcpPort;
        volatile bool stopping;
        readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();       // id -> last beacon
        readonly Dictionary<string, string> fetchedVersion = new Dictionary<string, string>(); // id -> version we have
        readonly HashSet<string> fetching = new HashSet<string>();
        // Each player id stays with the computer (address) it's heard from, so a beacon copied and
        // sent again from another machine is ignored while the real one is around; and one
        // computer can only bring a few ids (no inventing thousands of players).
        public const int MaxIdsPerAddress = 3;
        readonly Dictionary<string, Tuple<string, DateTime>> idHome = new Dictionary<string, Tuple<string, DateTime>>();   // id -> (address its data came from, last heard there)
        readonly Dictionary<string, DateTime> cooldown = new Dictionary<string, DateTime>();   // "id|address" whose fetch failed -> ignored until
        readonly Dictionary<string, Dictionary<string, DateTime>> addressIds = new Dictionary<string, Dictionary<string, DateTime>>();
        readonly HashSet<string> olderApps = new HashSet<string>();
        int serving;   // incoming connections being answered right now (inside the lock)
        readonly Dictionary<string, int> servingFrom = new Dictionary<string, int>();   // address -> how many of them
        public const int MaxServing = 16, MaxServingPerAddress = 2;
        // Time limits (shorter in tests): a whole download; proving you have the code; sending the data.
        public int FetchSeconds = 30, AuthSeconds = 3, ServeSeconds = 25;

        // Raised on a background thread when a player's data arrives (raw payload, already parsed).
        public event Action<PeerData, string> PeerData;
        // Raised on a background thread when a player's beacon is heard (id, nick, address).
        public event Action<string, string, string> PeerSeen;
        public event Action<string> Problem;
        // Raised on a background thread for every beacon or group-name announcement heard, from any
        // group: (group hash, player id, announced group name or null, when that name was chosen
        // (unix time), signed with our own code). Only "signed" can be trusted; the rest is just
        // for showing which groups are nearby.
        public event Action<string, string, string, long, bool> GroupHeard;
        // Raised (once per player) when someone in our group is heard with an app from before 1.10.2,
        // which can't share with this one: (player id, nickname).
        public event Action<string, string> OlderAppHeard;

        public LanShare(string id, string groupCode)
        {
            Id = id; Group = Profile.GroupHash(groupCode); key = Profile.GroupKey(groupCode);
            legacyGroup = Profile.LegacyGroupHash(groupCode);
        }

        // Listen-only: hears which groups are on the network, sends nothing and shares nothing.
        public LanShare(string id)
        {
            Id = id; Group = null; key = null;
        }

        public bool ListenOnly { get { return Group == null; } }

        // The group's name, announced to the network next to the beacons ("" announces nothing),
        // and when it was chosen (unix time): members who hear a newer signed name take it.
        public string GroupName { get { lock (gate) return groupName; } }
        public long GroupNamedAt { get { lock (gate) return groupNamedAt; } }
        public void SetGroupName(string name, long namedAt)
        {
            lock (gate) { groupName = Profile.CleanGroupName(name); groupNamedAt = Math.Max(0, namedAt); }
        }

        static string Nonce()
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return string.Concat(b.Select(x => x.ToString("x2")).ToArray());
        }

        public int TcpPort { get { return tcpPort; } }

        public void SetProfile(string nickname, string profilePayload)
        {
            lock (gate) { nick = Profile.CleanNick(nickname); payload = profilePayload ?? ""; version = Profile.Version(payload); }
        }

        public void Start()
        {
            if (!ListenOnly)
            {
                tcp = new TcpListener(Bind, 0);
                tcp.Start();
                tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            }
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(Bind, UdpPort));
            new Thread(ReceiveLoop) { IsBackground = true, Name = "qgsc-udp" }.Start();
            if (ListenOnly) return;
            new Thread(BeaconLoop) { IsBackground = true, Name = "qgsc-beacon" }.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "qgsc-tcp" }.Start();
        }

        public void Dispose()
        {
            stopping = true;
            try { if (udp != null) udp.Close(); } catch { }
            try { if (tcp != null) tcp.Stop(); } catch { }
        }

        public int OnlineCount
        {
            get { lock (gate) return seen.Count(kv => (DateTime.UtcNow - kv.Value).TotalSeconds < 20); }
        }

        public bool IsOnline(string id)
        {
            lock (gate) { DateTime t; return seen.TryGetValue(id, out t) && (DateTime.UtcNow - t).TotalSeconds < 20; }
        }

        // Ask everyone again (e.g. after the user clicks Sync now).
        public void ForgetVersions() { lock (gate) fetchedVersion.Clear(); }
        // A player's data was dropped (to make room): fetch it again next time they're heard.
        public void ForgetVersion(string id) { lock (gate) fetchedVersion.Remove(id); }

        // When we last heard this player's beacon (local time), or DateTime.MinValue.
        public DateTime LastHeard(string id)
        {
            lock (gate) { DateTime t; return seen.TryGetValue(id, out t) ? t.ToLocalTime() : DateTime.MinValue; }
        }

        // Players the user chose to forget: never fetched from again.
        readonly HashSet<string> ignored = new HashSet<string>();
        public void Ignore(string id) { lock (gate) { ignored.Add(id); fetchedVersion.Remove(id); } }

        public static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // Reads one line (up to max bytes) or returns null.
        static string ReadLine(Stream s, int max)
        {
            var line = new List<byte>();
            int b;
            while (line.Count < max && (b = s.ReadByte()) >= 0 && b != '\n') line.Add((byte)b);
            return line.Count >= max ? null : Encoding.UTF8.GetString(line.ToArray()).Trim();
        }

        void Report(string msg) { var h = Problem; if (h != null) try { h(msg); } catch { } }

        // ------------------------------------------------------------------
        // Beacons
        // ------------------------------------------------------------------
        IEnumerable<IPEndPoint> Targets()
        {
            var list = new List<IPEndPoint>(ExtraTargets);
            if (!BroadcastBeacons) return list;
            list.Add(new IPEndPoint(IPAddress.Broadcast, UdpPort));
            try
            {
                // Directed broadcast on each active IPv4 interface (255.255.255.255 only uses one of them).
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] a = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes();
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(a[i] | ~m[i]);
                        list.Add(new IPEndPoint(new IPAddress(bc), UdpPort));
                    }
                }
            }
            catch { }
            return list;
        }

        void BeaconLoop()
        {
            while (!stopping)
            {
                // Signed with the group key and timestamped: apps without the code can't fake or
                // (after a few minutes) replay a beacon to make us connect to them.
                string body, gname;
                long gat;
                lock (gate) { body = Group + "|" + Id + "|" + tcpPort + "|" + version + "|" + UnixNow() + "|" + nick; gname = groupName; gat = groupNamedAt; }
                string msg = "QGSC3|" + body + "|" + Profile.Sign(key, "beacon|" + body);
                var packets = new List<byte[]> { Encoding.UTF8.GetBytes(msg) };
                // The group's name, for players who aren't in it yet and for members who don't have
                // the newest name: "QGSC3G|group|id|unix time|named at|name|signature".
                // Older apps ignore it.
                if (gname != "")
                {
                    string gbody = Group + "|" + Id + "|" + UnixNow() + "|" + gat + "|" + gname;
                    packets.Add(Encoding.UTF8.GetBytes("QGSC3G|" + gbody + "|" + Profile.Sign(key, "group|" + gbody)));
                }
                foreach (var t in Targets())
                    foreach (byte[] data in packets)
                    {
                        try { udp.Send(data, data.Length, t); } catch { }
                    }
                for (int i = 0; i < BeaconMs / 100 && !stopping; i++) Thread.Sleep(100);
            }
        }

        void ReceiveLoop()
        {
            while (!stopping)
            {
                IPEndPoint from = null;
                byte[] data;
                try { data = udp.Receive(ref from); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (stopping) return; continue; }
                if (data.Length > 700) continue;
                string[] p = Encoding.UTF8.GetString(data).Split('|');
                if (p.Length == 7 && p[0] == "QGSC3G") { HeardName(p); continue; }
                // An app from before 1.10.2 in our group (it sends the old, fast group hash): it can't
                // share with this one, so the user is told once to ask that player to update.
                if (p.Length == 8 && p[0] == "QGSC2" && legacyGroup != null && p[1] == legacyGroup && Profile.IsId(p[2]) && p[2] != Id)
                {
                    bool first;
                    lock (gate) first = olderApps.Count < 64 && olderApps.Add(p[2]);
                    var ho = OlderAppHeard;
                    if (first && ho != null) try { ho(p[2], Profile.CleanNick(p[6])); } catch { }
                    continue;
                }
                // QGSC3|group|id|port|version|unix time|nick|signature
                if (p.Length != 8 || p[0] != "QGSC3" || !Profile.IsId(p[1]) || !Profile.IsId(p[2]) || p[2] == Id) continue;
                long when;
                if (long.TryParse(p[5], out when) && Math.Abs(UnixNow() - when) <= 300) RaiseGroupHeard(p[1], p[2], null, 0, false);
                if (p[1] != Group) continue;
                int port;
                if (!int.TryParse(p[3], out port) || port < 1 || port > 65535) continue;
                string ver = p[4];
                if (!Regex.IsMatch(ver, "^[0-9a-f]{0,64}$")) continue;
                long ts;
                if (!long.TryParse(p[5], out ts) || Math.Abs(UnixNow() - ts) > 300) continue;     // stale or far-off clock
                string body = string.Join("|", p.Skip(1).Take(6).ToArray());
                if (!Profile.SameSig(p[7], Profile.Sign(key, "beacon|" + body))) continue;     // not from someone with the code
                string peer = p[2], peerNick = Profile.CleanNick(p[6]);
                bool fetch;
                lock (gate)
                {
                    if (ignored.Contains(peer)) continue;
                    if (!FromHome(peer, from.Address.ToString())) continue;
                    seen[peer] = DateTime.UtcNow;
                    if (seen.Count > 500)   // never grows without bound
                        foreach (var old in seen.Where(kv => (DateTime.UtcNow - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList()) seen.Remove(old);
                    if (fetchedVersion.Count > 1000)
                        foreach (var old in fetchedVersion.Keys.Where(k => !seen.ContainsKey(k)).ToList()) fetchedVersion.Remove(old);
                    string have;
                    fetchedVersion.TryGetValue(peer, out have);
                    fetch = ver != "" && have != ver && !fetching.Contains(peer) && fetching.Count < 16;
                    if (fetch) fetching.Add(peer);
                }
                var hs = PeerSeen;
                if (hs != null) try { hs(peer, peerNick, from.Address.ToString()); } catch { }
                if (fetch)
                {
                    var addr = from.Address;
                    ThreadPool.QueueUserWorkItem(_ => Fetch(peer, addr, port, ver));
                }
            }
        }

        // (Call inside the lock.) Is this beacon for `peer` worth acting on from this address?
        //  - A player's home is the address their data was last fetched from, signed (a beacon alone
        //    proves nothing about where it came from: it can be copied). While the home is heard
        //    (in the last minute), the same id from any other address is ignored.
        //  - An address whose fetch for that id failed is ignored for that id for 2 minutes, so a
        //    copied beacon can't keep winning the race against the real one.
        //  - A new id is only accepted from an address that brought fewer than MaxIdsPerAddress ids
        //    in the last hour.
        bool FromHome(string peer, string addr)
        {
            var now = DateTime.UtcNow;
            DateTime until;
            if (cooldown.TryGetValue(peer + "|" + addr, out until) && now < until) return false;
            Tuple<string, DateTime> home;
            if (idHome.TryGetValue(peer, out home))
            {
                if (home.Item1 == addr) idHome[peer] = Tuple.Create(addr, now);
                else if ((now - home.Item2).TotalSeconds < 60) return false;   // its own computer is around
            }
            Dictionary<string, DateTime> ids;
            if (!addressIds.TryGetValue(addr, out ids)) addressIds[addr] = ids = new Dictionary<string, DateTime>();
            foreach (var old in ids.Where(kv => (now - kv.Value).TotalHours > 1).Select(kv => kv.Key).ToList()) ids.Remove(old);
            if (!ids.ContainsKey(peer) && ids.Count >= MaxIdsPerAddress) return false;
            ids[peer] = now;
            if (idHome.Count > 1000)
                foreach (var old in idHome.Where(kv => (now - kv.Value.Item2).TotalHours > 1).Select(kv => kv.Key).ToList()) idHome.Remove(old);
            if (addressIds.Count > 1000)
                foreach (var old in addressIds.Where(kv => kv.Value.All(x => (now - x.Value).TotalHours > 1)).Select(kv => kv.Key).ToList()) addressIds.Remove(old);
            if (cooldown.Count > 1000)
                foreach (var old in cooldown.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) cooldown.Remove(old);
            return true;
        }

        void RaiseGroupHeard(string group, string peer, string name, long namedAt, bool signed)
        {
            var h = GroupHeard;
            if (h != null) try { h(group, peer, name, namedAt, signed); } catch { }
        }

        // "QGSC3G|group|id|unix time|named at|name|signature": a group announcing its name.
        void HeardName(string[] p)
        {
            if (!Profile.IsId(p[1]) || !Profile.IsId(p[2]) || p[2] == Id) return;
            long ts, at;
            long now = UnixNow();
            if (!long.TryParse(p[3], out ts) || Math.Abs(now - ts) > 300) return;
            // When the name was chosen: never in the future (so nobody can pin a name forever).
            if (!long.TryParse(p[4], out at) || at < 0 || at > now + 300) return;
            string name = Profile.CleanGroupName(p[5]);
            if (name == "" || name != p[5]) return;
            // Only members (who have the code) can check the signature; for everyone else it's just a label.
            bool signed = Group != null && p[1] == Group &&
                Profile.SameSig(p[6], Profile.Sign(key, "group|" + p[1] + "|" + p[2] + "|" + p[3] + "|" + p[4] + "|" + p[5]));
            lock (gate) if (ignored.Contains(p[2])) return;
            RaiseGroupHeard(p[1], p[2], name, at, signed);
        }

        // ------------------------------------------------------------------
        // Profile exchange over TCP
        // ------------------------------------------------------------------
        void Fetch(string peer, IPAddress addr, int port, string ver)
        {
            bool ok = false;
            try
            {
                using (var c = new TcpClient())
                // The whole exchange has a time limit: a sender trickling a byte every few seconds can't
                // hold the download open (closing the connection makes the waiting read fail).
                using (new System.Threading.Timer(_ => { try { c.Close(); } catch { } }, null, FetchSeconds * 1000, Timeout.Infinite))
                {
                    var ar = c.BeginConnect(addr, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new IOException("timed out connecting");
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
                    var s = c.GetStream();
                    // 1. The other app speaks first with a fresh challenge: "QGSC3 HELLO <server nonce> <its id>".
                    string hello = ReadLine(s, 200);
                    string[] h1 = hello == null ? new string[0] : hello.Split(' ');
                    if (h1.Length != 4 || h1[0] != "QGSC3" || h1[1] != "HELLO" || !Regex.IsMatch(h1[2], "^[0-9a-f]{32}$")) throw new IOException("not a QuestGearScout Companion");
                    if (h1[3] != peer) throw new IOException("a different player answered");
                    // 2. We answer with our own nonce, signed over both nonces and its id: the request only
                    //    works on this one connection, so it can't be captured and replayed.
                    string sn = h1[2], cn = Nonce();
                    byte[] req = Encoding.UTF8.GetBytes("QGSC3 GET " + cn + " " + Profile.Sign(key, "get|" + sn + "|" + cn + "|" + peer) + "\n");
                    s.Write(req, 0, req.Length);
                    // 3. Reply: "QGSC3 OK <signature>\n" + profile, signed over both nonces and the data,
                    //    so someone without the code can't send us chains.
                    var ms = new MemoryStream();
                    var buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > Profile.MaxPayload + 200) throw new IOException("too much data");
                    }
                    string all = Encoding.UTF8.GetString(ms.ToArray());
                    int nl = all.IndexOf('\n');
                    if (nl < 0 || !all.StartsWith("QGSC3 OK ")) throw new IOException("the other app refused (different group code?)");
                    string sig = all.Substring(9, nl - 9).Trim();
                    string text = all.Substring(nl + 1);
                    if (!Profile.SameSig(sig, Profile.Sign(key, "resp|" + sn + "|" + cn + "|" + text))) throw new IOException("the data wasn't signed with your group code");
                    var pd = Profile.Parse(text);
                    if (pd == null || pd.Id != peer) throw new IOException("not a valid profile");
                    pd.Address = addr.ToString();
                    pd.LastSeen = DateTime.Now;
                    lock (gate)
                    {
                        if (ignored.Contains(peer)) return;   // forgotten while we were fetching
                        fetchedVersion[peer] = ver;
                        // Signed data came from here: this is the player's computer now.
                        idHome[peer] = Tuple.Create(addr.ToString(), DateTime.UtcNow);
                        cooldown.Remove(peer + "|" + addr);
                    }
                    ok = true;
                    var h = PeerData;
                    if (h != null) h(pd, text);
                }
            }
            catch (Exception ex) { Report("Couldn't get data from a player at " + addr + ": " + ex.Message); }
            finally
            {
                lock (gate)
                {
                    fetching.Remove(peer);
                    // Fetching from this address failed: it's ignored for this player for a while (not
                    // their own computer's address, which a passing hiccup shouldn't lock out).
                    Tuple<string, DateTime> home;
                    bool isHome = idHome.TryGetValue(peer, out home) && home.Item1 == addr.ToString();
                    if (!ok && !isHome) cooldown[peer + "|" + addr] = DateTime.UtcNow.AddMinutes(2);
                }
            }
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = tcp.AcceptTcpClient(); }
                catch { if (stopping) return; Thread.Sleep(200); continue; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        void Serve(TcpClient c)
        {
            // Only a few at a time and at most MaxServingPerAddress from one computer, so one machine
            // can't take every slot. Whoever doesn't prove they have the code within AuthSeconds is cut
            // off; a member then gets ServeSeconds for the data.
            string from;
            try { from = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); } catch { try { c.Close(); } catch { } return; }
            bool room;
            lock (gate)
            {
                int mine;
                servingFrom.TryGetValue(from, out mine);
                room = serving < MaxServing && mine < MaxServingPerAddress;
                if (room) { serving++; servingFrom[from] = mine + 1; }
            }
            if (!room) { try { c.Close(); } catch { } return; }
            using (c)
            using (var limit = new System.Threading.Timer(_ => { try { c.Close(); } catch { } }, null, AuthSeconds * 1000, Timeout.Infinite))
            {
                try
                {
                    c.ReceiveTimeout = 3000; c.SendTimeout = 5000;
                    var s = c.GetStream();
                    // Fresh challenge for this connection only.
                    string sn = Nonce();
                    byte[] hello = Encoding.UTF8.GetBytes("QGSC3 HELLO " + sn + " " + Id + "\n");
                    s.Write(hello, 0, hello.Length);
                    string line = ReadLine(s, 200);
                    string[] req = line == null ? new string[0] : line.Split(' ');
                    // "QGSC3 GET <client nonce> <signature over both nonces and our id>":
                    // answer only someone who knows the group code, on this connection.
                    if (req.Length != 4 || req[0] != "QGSC3" || req[1] != "GET" || !Regex.IsMatch(req[2], "^[0-9a-f]{32}$")) return;
                    if (!Profile.SameSig(req[3], Profile.Sign(key, "get|" + sn + "|" + req[2] + "|" + Id))) return;
                    limit.Change(ServeSeconds * 1000, Timeout.Infinite);   // a member: time to send the data
                    string p;
                    lock (gate) p = payload;
                    byte[] data = Encoding.UTF8.GetBytes("QGSC3 OK " + Profile.Sign(key, "resp|" + sn + "|" + req[2] + "|" + p) + "\n" + p);
                    s.Write(data, 0, data.Length);
                }
                catch { }
                finally
                {
                    lock (gate)
                    {
                        serving--;
                        int mine;
                        if (servingFrom.TryGetValue(from, out mine)) { if (mine <= 1) servingFrom.Remove(from); else servingFrom[from] = mine - 1; }
                    }
                }
            }
        }
    }
}
