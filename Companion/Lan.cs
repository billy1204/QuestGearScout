// QuestGearScout Companion - sharing chains and stats with other players on the local network.
//
// How it works:
//   * Every few seconds each app broadcasts a small UDP "beacon" on the local network:
//       QGSC1|<group hash>|<player id>|<tcp port>|<data version>|<nickname>
//     The group hash comes from the group code, so apps with a different code ignore each other.
//   * When an app sees a beacon from the same group with a data version it hasn't got yet, it
//     connects to that player's TCP port and asks for their profile: nickname, stats and the
//     chains they learned themselves (with Horde/Alliance marked).
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
    }

    public static class Profile
    {
        public const int MaxPayload = 2 * 1024 * 1024;
        public const int MaxChains = 5000;

        public static string GroupHash(string code)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("qgsc-group|" + (code ?? "").Trim().ToLowerInvariant()));
                return string.Concat(h.Take(16).Select(b => b.ToString("x2")).ToArray());
            }
        }

        // Secret key from the group code. It never goes over the network; it signs requests and replies.
        public static byte[] GroupKey(string code)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes("qgsc-key|" + (code ?? "").Trim().ToLowerInvariant()));
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
            if (f == "A" || f == "H") o.F["faction"] = f;
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
                if (clean != null) p.Chains[clean.Key] = clean;
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

        public static void Save(string id, string payload)
        {
            if (!Profile.IsId(id)) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, id + ".txt"), payload, new UTF8Encoding(false));
        }

        public static List<PeerData> LoadAll()
        {
            var outp = new List<PeerData>();
            try
            {
                if (!Directory.Exists(Dir)) return outp;
                foreach (string f in Directory.GetFiles(Dir, "*.txt"))
                {
                    try
                    {
                        if (new FileInfo(f).Length > Profile.MaxPayload) continue;
                        var p = Profile.Parse(File.ReadAllText(f));
                        if (p == null || p.Id + ".txt" != Path.GetFileName(f)) continue;
                        p.LastSeen = File.GetLastWriteTime(f);
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
        }
    }

    public class LanShare : IDisposable
    {
        public int UdpPort = 47923;
        public IPAddress Bind = IPAddress.Any;           // IPAddress.Loopback for tests
        public List<IPEndPoint> ExtraTargets = new List<IPEndPoint>();   // tests: send beacons straight to these
        public int BeaconMs = 5000;
        public bool BroadcastBeacons = true;

        public readonly string Id, Group;
        readonly byte[] key;
        string nick = "", payload = "", version = "";
        readonly object gate = new object();
        UdpClient udp;
        TcpListener tcp;
        int tcpPort;
        volatile bool stopping;
        readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();       // id -> last beacon
        readonly Dictionary<string, string> fetchedVersion = new Dictionary<string, string>(); // id -> version we have
        readonly HashSet<string> fetching = new HashSet<string>();

        // Raised on a background thread when a player's data arrives (raw payload, already parsed).
        public event Action<PeerData, string> PeerData;
        // Raised on a background thread when a player's beacon is heard (id, nick, address).
        public event Action<string, string, string> PeerSeen;
        public event Action<string> Problem;

        public LanShare(string id, string groupCode)
        {
            Id = id; Group = Profile.GroupHash(groupCode); key = Profile.GroupKey(groupCode);
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
            tcp = new TcpListener(Bind, 0);
            tcp.Start();
            tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(Bind, UdpPort));
            new Thread(ReceiveLoop) { IsBackground = true, Name = "qgsc-udp" }.Start();
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

        // When we last heard this player's beacon (local time), or DateTime.MinValue.
        public DateTime LastHeard(string id)
        {
            lock (gate) { DateTime t; return seen.TryGetValue(id, out t) ? t.ToLocalTime() : DateTime.MinValue; }
        }

        // Players the user chose to forget: never fetched from again.
        readonly HashSet<string> ignored = new HashSet<string>();
        public void Ignore(string id) { lock (gate) { ignored.Add(id); fetchedVersion.Remove(id); } }

        static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

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
                string body;
                lock (gate) body = Group + "|" + Id + "|" + tcpPort + "|" + version + "|" + UnixNow() + "|" + nick;
                string msg = "QGSC2|" + body + "|" + Profile.Sign(key, "beacon|" + body);
                byte[] data = Encoding.UTF8.GetBytes(msg);
                foreach (var t in Targets())
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
                // QGSC2|group|id|port|version|unix time|nick|signature
                string[] p = Encoding.UTF8.GetString(data).Split('|');
                if (p.Length != 8 || p[0] != "QGSC2" || p[1] != Group || !Profile.IsId(p[2]) || p[2] == Id) continue;
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
                    seen[peer] = DateTime.UtcNow;
                    if (seen.Count > 500)   // never grows without bound
                        foreach (var old in seen.Where(kv => (DateTime.UtcNow - kv.Value).TotalMinutes > 10).Select(kv => kv.Key).ToList()) seen.Remove(old);
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

        // ------------------------------------------------------------------
        // Profile exchange over TCP
        // ------------------------------------------------------------------
        void Fetch(string peer, IPAddress addr, int port, string ver)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(addr, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new IOException("timed out connecting");
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
                    var s = c.GetStream();
                    // 1. The other app speaks first with a fresh challenge: "QGSC2 HELLO <server nonce> <its id>".
                    string hello = ReadLine(s, 200);
                    string[] h1 = hello == null ? new string[0] : hello.Split(' ');
                    if (h1.Length != 4 || h1[0] != "QGSC2" || h1[1] != "HELLO" || !Regex.IsMatch(h1[2], "^[0-9a-f]{32}$")) throw new IOException("not a QuestGearScout Companion");
                    if (h1[3] != peer) throw new IOException("a different player answered");
                    // 2. We answer with our own nonce, signed over both nonces and its id: the request only
                    //    works on this one connection, so it can't be captured and replayed.
                    string sn = h1[2], cn = Nonce();
                    byte[] req = Encoding.UTF8.GetBytes("QGSC2 GET " + cn + " " + Profile.Sign(key, "get|" + sn + "|" + cn + "|" + peer) + "\n");
                    s.Write(req, 0, req.Length);
                    // 3. Reply: "QGSC2 OK <signature>\n" + profile, signed over both nonces and the data,
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
                    if (nl < 0 || !all.StartsWith("QGSC2 OK ")) throw new IOException("the other app refused (different group code?)");
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
                    }
                    var h = PeerData;
                    if (h != null) h(pd, text);
                }
            }
            catch (Exception ex) { Report("Couldn't get data from a player at " + addr + ": " + ex.Message); }
            finally { lock (gate) fetching.Remove(peer); }
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
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 3000; c.SendTimeout = 5000;
                    var s = c.GetStream();
                    // Fresh challenge for this connection only.
                    string sn = Nonce();
                    byte[] hello = Encoding.UTF8.GetBytes("QGSC2 HELLO " + sn + " " + Id + "\n");
                    s.Write(hello, 0, hello.Length);
                    string line = ReadLine(s, 200);
                    string[] req = line == null ? new string[0] : line.Split(' ');
                    // "QGSC2 GET <client nonce> <signature over both nonces and our id>":
                    // answer only someone who knows the group code, on this connection.
                    if (req.Length != 4 || req[0] != "QGSC2" || req[1] != "GET" || !Regex.IsMatch(req[2], "^[0-9a-f]{32}$")) return;
                    if (!Profile.SameSig(req[3], Profile.Sign(key, "get|" + sn + "|" + req[2] + "|" + Id))) return;
                    string p;
                    lock (gate) p = payload;
                    byte[] data = Encoding.UTF8.GetBytes("QGSC2 OK " + Profile.Sign(key, "resp|" + sn + "|" + req[2] + "|" + p) + "\n" + p);
                    s.Write(data, 0, data.Length);
                }
                catch { }
            }
        }
    }
}
