// Self-test for local sharing (QuestGearScoutCompanion.exe --lantest <output file>).
// Runs several sharing engines inside this one process on 127.0.0.1 only (no firewall prompt,
// nothing leaves the PC) and checks who gets whose data.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QGSCompanion
{
    static class LanTest
    {
        static string Row(string kind, int id, string name, string faction, int quality, string item)
        {
            var s = new Starter();
            s.F["kind"] = kind; s.F["id"] = (double)id; s.F["map"] = 1429.0; s.F["x"] = 0.5; s.F["y"] = 0.5;
            s.F["name"] = name; s.F["rootQuest"] = (double)(id + 1); s.F["quality"] = (double)quality; s.F["steps"] = 3.0;
            if (item != null) s.F["item"] = item;
            if (faction != null) s.F["faction"] = faction;
            return Starter.Encode(new[] { s }).Split('\n')[1];
        }

        static string Payload(string id, string nick, PlayerStats st, params string[] rows)
        {
            var chains = Starter.Decode("QGS1\n" + string.Join("\n", rows)).Values;
            return Profile.Build(id, nick, st, chains);
        }

        public static string Run()
        {
            var log = new StringBuilder();
            Action<string, bool> check = (name, ok) => log.Append(ok ? "  ok    " : "  FAIL  ").Append(name).Append('\n');

            // ---- parsing and cleaning untrusted data ----
            log.Append("Untrusted data\n");
            string goodLink = "|cff1eff00|Hitem:2041::::::::|h[Tunic of Westfall]|h|r";
            string evil = Row("C", 500, "Evil|cffff0000 NPC", "A", 2, "|cffff0000|Hurl:http://x|h[Click me]|h|r");
            string p1 = Payload("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Bob|=;\u0001Builder", new PlayerStats { Gear = 3 },
                Row("C", 100, "Good NPC", "A", 3, goodLink), evil, Row("X", 1, "bad kind", null, 2, null), Row("C", 0, "zero id", null, 2, null));
            var pd = Profile.Parse(p1);
            check("profile parses", pd != null);
            check("nickname cleaned (no separators/control chars)", pd != null && pd.Nick == "BobBuilder");
            check("good chain kept with its item link and faction", pd != null && pd.Chains.ContainsKey("C:100") && pd.Chains["C:100"].Get("item") == goodLink && pd.Chains["C:100"].Get("faction") == "A");
            check("non-item link dropped, '|' stripped from names", pd != null && pd.Chains.ContainsKey("C:500") && pd.Chains["C:500"].Get("item") == null && !pd.Chains["C:500"].Get("name").Contains("|"));
            check("invalid chains (bad kind, zero id) dropped", pd != null && pd.Chains.Count == 2);
            check("stats numbers clamped", PlayerStats.Parse("gear=999999999;longest=500;factions=AHX;zones=-5").Gear == 100000 &&
                                          PlayerStats.Parse("longest=500").Longest == 60 && PlayerStats.Parse("factions=AHXA").Factions == "AH" && PlayerStats.Parse("zones=-5").Zones == 0);
            check("junk payload rejected", Profile.Parse("hello") == null && Profile.Parse("QGSC-PROFILE 1\nid=notanid\nchains\n") == null);
            check("oversized payload rejected", Profile.Parse("QGSC-PROFILE 1\n" + new string('x', Profile.MaxPayload)) == null);

            // ---- three engines on loopback: A and B share a code, C uses another ----
            log.Append("Sharing on 127.0.0.1\n");
            int pa = 47950, pb = 47951, pc = 47952;
            var A = new LanShare("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "gnomes42") { UdpPort = pa, Bind = IPAddress.Loopback, BroadcastBeacons = false, BeaconMs = 500 };
            var B = new LanShare("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Gnomes42 ") { UdpPort = pb, Bind = IPAddress.Loopback, BroadcastBeacons = false, BeaconMs = 500 };
            var C = new LanShare("cccccccccccccccccccccccccccccccc", "orcs99") { UdpPort = pc, Bind = IPAddress.Loopback, BroadcastBeacons = false, BeaconMs = 500 };
            foreach (var x in new[] { A, B, C })
                foreach (int port in new[] { pa, pb, pc }) if (port != x.UdpPort) x.ExtraTargets.Add(new IPEndPoint(IPAddress.Loopback, port));
            A.SetProfile("Ally", Payload(A.Id, "Ally", new PlayerStats { Gear = 2, Factions = "A" }, Row("C", 700, "Alliance Giver", "A", 2, null)));
            B.SetProfile("Hordie", Payload(B.Id, "Hordie", new PlayerStats { Gear = 5, Dungeon = 1, Factions = "H" }, Row("C", 800, "Horde Giver", "H", 3, null), Row("C", 801, "Neutral Giver", null, 2, null)));
            C.SetProfile("Stranger", Payload(C.Id, "Stranger", new PlayerStats { Gear = 9 }, Row("C", 900, "Stranger Giver", "A", 2, null)));
            var got = new Dictionary<string, List<PeerData>>();
            var problems = new List<string>();
            foreach (var x in new[] { A, B, C })
            {
                var me = x;
                got[me.Id] = new List<PeerData>();
                me.PeerData += (p, raw) => { lock (got) got[me.Id].Add(p); };
                me.Problem += msg => { lock (problems) problems.Add(msg); };
            }
            try
            {
                A.Start(); B.Start(); C.Start();
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(100);
                    lock (got) if (got[A.Id].Count > 0 && got[B.Id].Count > 0 && i > 25) break;
                }
                lock (got)
                {
                    var aGot = got[A.Id]; var bGot = got[B.Id]; var cGot = got[C.Id];
                    check("A received B's profile (same code, typed differently)", aGot.Any(p => p.Id == B.Id && p.Nick == "Hordie" && p.Chains.ContainsKey("C:800")));
                    check("B received A's profile", bGot.Any(p => p.Id == A.Id && p.Chains.ContainsKey("C:700")));
                    check("B's chains keep their factions (H and neutral)", aGot.Any(p => p.Id == B.Id && p.Chains["C:800"].Get("faction") == "H" && p.Chains["C:801"].Get("faction") == null));
                    check("stats arrive", aGot.Any(p => p.Id == B.Id && p.Stats.Gear == 5 && p.Stats.Dungeon == 1 && p.Stats.Factions == "H"));
                    check("nobody received C (different code)", !aGot.Any(p => p.Id == C.Id) && !bGot.Any(p => p.Id == C.Id));
                    check("C received nobody", cGot.Count == 0);
                    check("each profile fetched once while unchanged", aGot.Count(p => p.Id == B.Id) == 1);
                }
                // B changes its data: A should fetch again.
                B.SetProfile("Hordie", Payload(B.Id, "Hordie", new PlayerStats { Gear = 6, Factions = "H" }, Row("C", 800, "Horde Giver", "H", 3, null), Row("C", 802, "New Horde Giver", "H", 2, null)));
                for (int i = 0; i < 40; i++) { Thread.Sleep(100); lock (got) if (got[A.Id].Count(p => p.Id == B.Id) >= 2) break; }
                lock (got) check("changed data is fetched again", got[A.Id].Count(p => p.Id == B.Id) >= 2 && got[A.Id].Last(p => p.Id == B.Id).Chains.ContainsKey("C:802"));

                // A stranger who heard B's beacon (so knows the group's public id) but not the code:
                // asking B for data must get nothing.
                log.Append("Strangers without the code\n");
                byte[] gk = Profile.GroupKey("gnomes42");
                // Talks to B: returns (hello line, everything after sending getLine). getLine may use {sn}.
                Func<Func<string, string>, string[]> talk = makeGet =>
                {
                    string helloLine = "", rest = "";
                    try
                    {
                        using (var cl = new TcpClient())
                        {
                            cl.Connect(IPAddress.Loopback, B.TcpPort);
                            cl.ReceiveTimeout = 2000;
                            var s = cl.GetStream();
                            var hb = new List<byte>(); int b;
                            while ((b = s.ReadByte()) >= 0 && b != '\n' && hb.Count < 300) hb.Add((byte)b);
                            helloLine = Encoding.UTF8.GetString(hb.ToArray());
                            string[] hp = helloLine.Split(' ');
                            byte[] req = Encoding.UTF8.GetBytes(makeGet(hp.Length > 2 ? hp[2] : "") + "\n");
                            s.Write(req, 0, req.Length);
                            var ms = new System.IO.MemoryStream(); var buf = new byte[4096]; int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                            rest = Encoding.UTF8.GetString(ms.ToArray());
                        }
                    }
                    catch { }
                    return new[] { helloLine, rest };
                };
                var r1 = talk(sn => "QGSC2 GET " + new string('0', 32) + " " + new string('f', 64));
                check("the other app opens with a fresh challenge naming itself", r1[0].StartsWith("QGSC2 HELLO ") && r1[0].EndsWith(" " + B.Id));
                check("a request without the code gets no data", !r1[1].Contains("QGSC-PROFILE"));
                // Someone with the code answers correctly once: they get data.
                string cn = new string('1', 32), captured = null;
                var r2 = talk(sn => captured = "QGSC2 GET " + cn + " " + Profile.Sign(gk, "get|" + sn + "|" + cn + "|" + B.Id));
                check("a correctly signed request gets the data", r2[1].StartsWith("QGSC2 OK ") && r2[1].Contains("QGSC-PROFILE"));
                // An eavesdropper replays that exact request on a new connection: the challenge changed, so nothing.
                var r3 = talk(sn => captured);
                check("a captured request can't be replayed", !r3[1].Contains("QGSC-PROFILE"));

                // Beacons: unsigned, wrongly signed or old ones are ignored (we never connect to them).
                string fakeId = "dddddddddddddddddddddddddddddddd";
                var fake = new TcpListener(IPAddress.Loopback, 0);
                fake.Start();
                int fakePort = ((IPEndPoint)fake.LocalEndpoint).Port;
                int fakeHits = 0;
                Func<long, bool, byte[]> beaconFor = (ts, goodSig) =>
                {
                    string body = Profile.GroupHash("gnomes42") + "|" + fakeId + "|" + fakePort + "|0123abcd|" + ts + "|Faker";
                    return Encoding.UTF8.GetBytes("QGSC2|" + body + "|" + (goodSig ? Profile.Sign(gk, "beacon|" + body) : new string('a', 64)));
                };
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                var fakeThread = new Thread(() =>
                {
                    try
                    {
                        for (int k = 0; k < 6; k++)
                            using (var c = fake.AcceptTcpClient())
                            {
                                Interlocked.Increment(ref fakeHits);
                                var s = c.GetStream();
                                // Plays along with the handshake but can't sign the data (doesn't know the code).
                                byte[] hello = Encoding.UTF8.GetBytes("QGSC2 HELLO " + new string('2', 32) + " " + fakeId + "\n");
                                s.Write(hello, 0, hello.Length);
                                var buf = new byte[512]; s.Read(buf, 0, buf.Length);
                                byte[] resp = Encoding.UTF8.GetBytes("QGSC2 OK " + new string('a', 64) + "\n" +
                                    Payload(fakeId, "Faker", new PlayerStats { Gear = 99999 }, Row("C", 666, "Fake Giver", "A", 4, null)));
                                s.Write(resp, 0, resp.Length);
                            }
                    }
                    catch { }
                }) { IsBackground = true };
                fakeThread.Start();
                using (var u = new UdpClient())
                {
                    foreach (var bc in new[] { beaconFor(now, false), beaconFor(now - 3600, true),
                        Encoding.UTF8.GetBytes("QGSC1|" + Profile.GroupHash("gnomes42") + "|" + fakeId + "|" + fakePort + "|0123abcd|Faker") })
                        for (int k = 0; k < 2; k++) { u.Send(bc, bc.Length, new IPEndPoint(IPAddress.Loopback, pa)); Thread.Sleep(200); }
                    Thread.Sleep(800);
                    check("unsigned, wrongly signed and hour-old beacons are ignored", fakeHits == 0 && !A.IsOnline(fakeId));
                    // A beacon signed with the code (an insider) whose server sends unsigned data.
                    byte[] good = beaconFor(now, true);
                    for (int k = 0; k < 3; k++) { u.Send(good, good.Length, new IPEndPoint(IPAddress.Loopback, pa)); Thread.Sleep(400); }
                }
                Thread.Sleep(1500);
                lock (got) check("unsigned data is rejected", fakeHits > 0 && !got[A.Id].Any(p => p.Id == fakeId));
                lock (problems) check("...and reported as not signed with the code", problems.Any(p => p.Contains("signed")));
                fake.Stop();

                // Forgetting a player: no more fetches from them, even when their data changes.
                log.Append("Forgetting\n");
                check("last heard time is known for an online player", (DateTime.Now - A.LastHeard(B.Id)).TotalSeconds < 10);
                A.Ignore(B.Id);
                int before; lock (got) before = got[A.Id].Count(p => p.Id == B.Id);
                B.SetProfile("Hordie", Payload(B.Id, "Hordie", new PlayerStats { Gear = 7, Factions = "H" }, Row("C", 803, "Another", "H", 2, null)));
                Thread.Sleep(2000);
                lock (got) check("a forgotten player is not fetched again", got[A.Id].Count(p => p.Id == B.Id) == before);
                check("a never-heard player has no last heard time", A.LastHeard("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee") == DateTime.MinValue);
            }
            finally { A.Dispose(); B.Dispose(); C.Dispose(); }

            // ---- Horde and Alliance kept apart when merging ----
            log.Append("Horde / Alliance merging\n");
            var two = Starter.Decode("QGS1\n" + Row("C", 990, "Two[Faced]", "H", 4, null) + "\n" + Row("C", 990, "Two[Faced]", "A", 2, null));
            check("same giver, two factions: both entries decode", two.Count == 2 && two.Values.Select(s => s.Get("faction")).OrderBy(x => x).SequenceEqual(new[] { "A", "H" }));
            check("[ and ] survive encoding", two.Values.All(s => s.Get("name") == "Two[Faced]") && !Starter.Encode(two.Values).Contains("["));
            var hs = Starter.Decode("QGS1\n" + Row("C", 991, "Giver", "H", 3, null)).Values.First();
            var ns = Starter.Decode("QGS1\n" + Row("C", 991, "Giver", null, 3, null)).Values.First();
            ns.F["dungeonSteps"] = 2.0; ns.F["dungeonName"] = "Deadmines"; ns.F["dRoot"] = 5.0;
            var packed = ChainRow.Pack(new[] { new ChainRow { Starter = hs, IsDungeon = false, Faction = "H" },
                                               new ChainRow { Starter = ns, IsDungeon = true, Faction = null } }, null);
            check("a Horde gear chain and a neutral dungeon chain of one giver stay separate",
                packed.Count == 2 && packed.Values.Any(s => s.Get("faction") == "H" && s.HasGear && !s.HasDungeon) &&
                packed.Values.Any(s => s.Get("faction") == null && s.HasDungeon && !s.HasGear));
            check("...and both survive a round trip", Starter.Decode(Starter.Encode(packed.Values)).Count == 2);
            var oldEntry = Starter.Decode("QGS1\n" + Row("C", 992, "Old", null, 2, null));
            var upd = ChainRow.Pack(new[] { new ChainRow { Starter = oldEntry.Values.First(), IsDungeon = false, Faction = "A" } }, oldEntry);
            check("an existing untagged entry gets its faction", upd.Count == 1 && upd.Values.First().Get("faction") == "A");
            var unp = ChainRow.Unpack(new[] { new ChainRow { Starter = hs, IsDungeon = false, Faction = "H" } }, packed);
            check("removing the Horde chain leaves the neutral one", unp.Count == 1 && unp.Values.First().HasDungeon);

            // ---- ranks and achievements ----
            log.Append("Ranks and achievements\n");
            var st = new PlayerStats { Gear = 12, Rare = 1, Dungeon = 5, Discoveries = 2, QuestsSeen = 104, Longest = 6, Zones = 4, Factions = "AH", SharedWith = 1 };
            check("points add up (12*10 + 5 + 5*8 + 2*25 + 104 + 12 + 5 = 336)", st.Points == 336);
            check("336 points is Explorer", st.Rank.Name == "Explorer" && st.Rank.Next == 600);
            check("earned: First find, Gear collector, Rare taste, Dungeon delver, Trailblazer, Marathon, Double agent, Sharing is caring",
                new[] { "First find", "Gear collector", "Rare taste", "Dungeon delver", "Trailblazer", "Marathon", "Double agent", "Sharing is caring" }
                    .All(n => Achievements.All.First(a => a.Name == n).Earned(st)));
            check("not yet: Hoarder, Epic hunter, Guild spirit", !new[] { "Hoarder", "Epic hunter", "Guild spirit" }.Any(n => Achievements.All.First(a => a.Name == n).Earned(st)));
            check("stats survive the wire format", PlayerStats.Parse(st.Serialize()).Points == st.Points);

            int fails = log.ToString().Split('\n').Count(l => l.StartsWith("  FAIL"));
            int oks = log.ToString().Split('\n').Count(l => l.StartsWith("  ok"));
            log.Append('\n').Append(oks).Append(" passed, ").Append(fails).Append(" failed\n");
            return log.ToString();
        }
    }
}
