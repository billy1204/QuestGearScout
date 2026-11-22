// QuestGearScout Companion - player stats, points, ranks and achievements.
// The same numbers are worked out for you and sent to other players on your network.
// C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QGSCompanion
{
    public class PlayerStats
    {
        public string Id = "", Nick = "";
        public int Gear, Dungeon, Discoveries, QuestsSeen, DungeonQuests, Longest, Zones, Rare, Epic, Characters, Known, Possible, SharedWith;
        public string Factions = "";          // "A", "H", "AH" or ""
        public DateTime Updated;              // when the game last saved (their clock)
        public DateTime LastSeen;             // when we last heard from them on the network
        public bool Online, IsMe;

        // Points: what a find is worth. Discoveries (chains the addon didn't know) count most.
        public int Points
        {
            get
            {
                return Gear * 10 + Rare * 5 + Epic * 15 + Dungeon * 8 + Discoveries * 25 + Math.Min(QuestsSeen, 500) + Longest * 2 + SharedWith * 5;
            }
        }

        public Rank Rank { get { return Ranks.For(Points); } }

        public int Percent { get { return Possible <= 0 ? 0 : Math.Min(100, (int)Math.Round(100.0 * Known / Possible)); } }

        // ---- wire format: "gear=3;dungeon=1;..." (numbers only, clamped on read) ----
        public string Serialize()
        {
            var parts = new[] {
                "gear=" + Gear, "dungeon=" + Dungeon, "disc=" + Discoveries, "quests=" + QuestsSeen, "dquests=" + DungeonQuests,
                "longest=" + Longest, "zones=" + Zones, "rare=" + Rare, "epic=" + Epic, "chars=" + Characters,
                "known=" + Known, "possible=" + Possible, "shared=" + SharedWith, "factions=" + Factions,
                "updated=" + Updated.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) };
            return string.Join(";", parts);
        }

        public static PlayerStats Parse(string s)
        {
            var p = new PlayerStats();
            foreach (string part in (s ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim(), v = part.Substring(eq + 1).Trim();
                long n;
                bool num = long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                int c = num ? (int)Math.Max(0, Math.Min(100000, n)) : 0;   // never trust sizes from the network
                switch (k)
                {
                    case "gear": p.Gear = c; break;
                    case "dungeon": p.Dungeon = c; break;
                    case "disc": p.Discoveries = c; break;
                    case "quests": p.QuestsSeen = c; break;
                    case "dquests": p.DungeonQuests = c; break;
                    case "longest": p.Longest = Math.Min(c, 60); break;
                    case "zones": p.Zones = Math.Min(c, 200); break;
                    case "rare": p.Rare = c; break;
                    case "epic": p.Epic = c; break;
                    case "chars": p.Characters = Math.Min(c, 500); break;
                    case "known": p.Known = c; break;
                    case "possible": p.Possible = c; break;
                    case "shared": p.SharedWith = Math.Min(c, 100); break;
                    case "factions": p.Factions = new string(v.Where(ch => ch == 'A' || ch == 'H').Distinct().ToArray()); break;
                    case "updated":
                        if (num && n > 0 && n < DateTime.MaxValue.Ticks) p.Updated = new DateTime(n, DateTimeKind.Utc).ToLocalTime();
                        break;
                }
            }
            if (p.Known > p.Possible) p.Known = p.Possible;
            return p;
        }

        // Your own stats, from what the app has read.
        public static PlayerStats Compute(Learned learned, AddonData addon, List<ChainRow> myRows, DateTime saved, int sharedWith)
        {
            var mine = myRows.Where(r => r.Status != ChainRow.Shared).ToList();
            var st = new PlayerStats
            {
                IsMe = true,
                Gear = mine.Count(r => !r.IsDungeon),
                Dungeon = mine.Count(r => r.IsDungeon),
                Discoveries = mine.Count(r => r.Status == ChainRow.New || r.Status == ChainRow.InDiscoveries),
                QuestsSeen = learned.QuestsSeen,
                DungeonQuests = learned.DungeonQuests,
                Longest = mine.Select(r => (int)(r.Starter.Num(r.IsDungeon ? "dungeonSteps" : "steps") ?? 1)).DefaultIfEmpty(0).Max(),
                Zones = mine.Select(r => (long)(r.Starter.Num("map") ?? 0)).Distinct().Count(),
                Rare = mine.Count(r => !r.IsDungeon && (r.Starter.Num("quality") ?? 0) == 3),
                Epic = mine.Count(r => !r.IsDungeon && (r.Starter.Num("quality") ?? 0) >= 4),
                Characters = learned.Characters.Count,
                Factions = string.Concat(new[] { "A", "H" }.Where(f => learned.Factions.Contains(f)).ToArray()),
                Updated = saved,
                SharedWith = sharedWith,
            };
            // Progress: how many of the addon's built-in chains (for your factions) you've done yourself.
            var facs = learned.Factions;
            Func<Starter, bool> forMe = s => { string f = s.Get("faction"); return f == null || facs.Count == 0 || facs.Contains(f); };
            st.Possible = addon.BuiltIn.Values.Where(forMe).Sum(s => (s.HasGear ? 1 : 0) + (s.HasDungeon ? 1 : 0));
            st.Known = Math.Min(st.Possible, mine.Count(r => r.Status == ChainRow.BuiltIn));
            return st;
        }
    }

    public class Rank
    {
        public int Level, Min, Next;
        public string Name;
    }

    public static class Ranks
    {
        static readonly string[] Names = { "Wanderer", "Scout", "Pathfinder", "Explorer", "Cartographer", "Loremaster", "Legend" };
        static readonly int[] Mins = { 0, 50, 150, 300, 600, 1000, 2000 };

        public static Rank For(int points)
        {
            int i = Mins.Length - 1;
            while (i > 0 && points < Mins[i]) i--;
            return new Rank { Level = i + 1, Name = Names[i], Min = Mins[i], Next = i + 1 < Mins.Length ? Mins[i + 1] : 0 };
        }
    }

    public class Achievement
    {
        public string Name, Text;
        public Func<PlayerStats, int> Value;
        public int Target;
        public bool Earned(PlayerStats s) { return Value(s) >= Target; }
        public int Progress(PlayerStats s) { return Math.Min(Target, Value(s)); }
    }

    public static class Achievements
    {
        public static readonly List<Achievement> All = new List<Achievement>
        {
            new Achievement { Name = "First find", Text = "Learn your first gear chain", Value = s => s.Gear, Target = 1 },
            new Achievement { Name = "Gear collector", Text = "Learn 10 gear chains", Value = s => s.Gear, Target = 10 },
            new Achievement { Name = "Hoarder", Text = "Learn 25 gear chains", Value = s => s.Gear, Target = 25 },
            new Achievement { Name = "Rare taste", Text = "Learn a chain with a Rare reward", Value = s => s.Rare + s.Epic, Target = 1 },
            new Achievement { Name = "Epic hunter", Text = "Learn a chain with an Epic reward", Value = s => s.Epic, Target = 1 },
            new Achievement { Name = "Dungeon delver", Text = "Learn 5 dungeon chains", Value = s => s.Dungeon, Target = 5 },
            new Achievement { Name = "Dungeon master", Text = "Learn 15 dungeon chains", Value = s => s.Dungeon, Target = 15 },
            new Achievement { Name = "Trailblazer", Text = "Find a chain the addon didn't know", Value = s => s.Discoveries, Target = 1 },
            new Achievement { Name = "Pioneer", Text = "Find 5 chains the addon didn't know", Value = s => s.Discoveries, Target = 5 },
            new Achievement { Name = "Marathon", Text = "Learn a chain of 6 or more steps", Value = s => s.Longest, Target = 6 },
            new Achievement { Name = "Globetrotter", Text = "Learn chains in 10 zones", Value = s => s.Zones, Target = 10 },
            new Achievement { Name = "Bookworm", Text = "See 250 quests", Value = s => s.QuestsSeen, Target = 250 },
            new Achievement { Name = "Double agent", Text = "Play both Horde and Alliance", Value = s => s.Factions.Length, Target = 2 },
            new Achievement { Name = "Sharing is caring", Text = "Share with another player on your network", Value = s => s.SharedWith, Target = 1 },
            new Achievement { Name = "Guild spirit", Text = "Share with 3 players on your network", Value = s => s.SharedWith, Target = 3 },
        };

        public static int Count(PlayerStats s) { return All.Count(a => a.Earned(s)); }
    }
}
