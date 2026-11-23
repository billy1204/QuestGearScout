# QuestGearScout

QuestGearScout tells you when you're near an NPC or object that starts a quest chain ending in Uncommon-or-better gear, for World of Warcraft: Forever.

## Built-in chains
QuestGearScout comes with **about 650 WoW Forever chain starters** built in, so alerts work from your first login. They cover gear chains and dungeon chains in every zone, including Riverglades, Mount Hyjal, Shen'dralas and Zephras Isle. You only see the chains for your faction and class. The data comes from AllTheThings (see Credits) and matches WoW Forever patch 1.60.1. Anything that has changed since then, the addon learns as you play.

## How it works
WoW doesn't tell addons where a quest chain ends, so on top of the built-in list, QuestGearScout **learns chains as you quest**:
1. It records who gave each quest (NPC or object) and where.
2. When you accept a follow-up right after a turn-in, it links the two quests into a chain.
3. When a quest rewards Uncommon+ gear, it traces back to the chain's first quest and saves that quest's giver as a **starter**.

What it learns is saved for your whole account, so your alts get alerts for chains your main has already done. Players can also share what they've learned with export/import strings.

## Horde and Alliance
Since 1.9.0 every chain QuestGearScout learns remembers which faction did it. A Horde character only gets alerts for Horde chains and neutral ones, and an Alliance character only for Alliance and neutral ones. A chain done on both factions counts as neutral.

The faction is saved with each quest whenever your character sees it: in the quest window, on accepting, on turning in, and while it's in your quest log. At login, quests recorded before factions were saved get the faction of each character that has done them. A chain learned by a Horde character is listed as Horde; as soon as an Alliance character sees it in game (its first quest or its reward quest), it becomes neutral and shows for both factions. The same works the other way round. A quest giver's gear chain and dungeon chain each keep their own faction, also in `/qgs export` (it then shares them as two lines). The Companion app reads the same information, so its faction filter and local sharing match the game.

## Sharing on your home network
With the **QuestGearScout Companion** app, friends on the same local network with the same group code share the chains they've found and compare ranks and achievements. Shared chains are kept in `Shared.lua`, apart from your own data, and load in game after `/reload`. Each character still only sees its own faction's chains.

## Dungeon quests
QuestGearScout also learns **dungeon quests and the chains that lead to them**. A quest counts as a dungeon quest when:
- the game tags it as a Dungeon or Raid quest, or
- you make progress on it, pick it up or turn it in inside a dungeon or raid.

Then the giver of the chain's first quest becomes a dungeon starter. You get alerts for it ("starts a 3-step chain to a dungeon quest (The Deadmines)"), and its tooltip shows a blue "Dungeon chain" line. Accepting a quest tells you when a chain leads to a dungeon quest, and at which step. You can turn dungeon tracking off in Settings or with `/qgs dungeon off`.

## Alerts
- **Proximity**: you come within 60 yards of a starter (works for NPCs and objects).
- **Nameplate**: a starter NPC's nameplate appears (turn on friendly NPC nameplates).
- **Mouseover**: you mouse over a starter NPC or object.
- **Quest window**: a message when the quest you're reading starts a known gear chain, or what step it is in a known chain.
- **Chain started**: when you accept a quest that starts or continues a chain, you get a green notice showing **step X of N** and the gear it ends in. If N has a "+" after it, nobody has finished that chain yet, so it may be longer.
- **Tooltips**: mousing over a starter NPC or object shows how many steps its chain has and what gear it ends in.

## Window
The window opens when you log in. You can turn that off in Settings. Press **Ctrl+Q**, type `/qgs`, or click the minimap button to open or close it. If you've already bound Ctrl+Q to something else, QuestGearScout leaves it alone. You can pick any other key under Options > Keybindings > AddOns > QuestGearScout. Right-click the minimap button to go straight to Settings.
- **Nearby**: gear-chain starters in your zone, closest first, with distance, step count, reward and a Waypoint button. Chains you've already done on this character are greyed out.
- **My chains**: a progress bar for each chain you're on, showing step X of N. The bar is gold while the chain's full length isn't known yet. Buttons: Tell group, Export, Import.
- **Stats**: chains learned, imported and recorded, plus the longest chain.
- **Settings**: turn alerts, chain notices, sound, banner and the minimap button on or off, and set the minimum reward quality and alert distance.

When you start or continue a chain, a green banner appears near the top of the screen. Click it to open My chains.

## Chains learned
QuestGearScout keeps count of what it knows: gear chains you learned yourself, gear chains you imported, every quest chain you've recorded (whatever the reward), and the longest one. You see a one-line summary when you log in, and the total goes up each time you finish a new gear chain. Type `/qgs stats` for the full breakdown.

## Step counts
QuestGearScout records how many steps each chain has. Those counts are included when you `/qgs export`, so the people you share with can see how long each chain is before they start it.

Each alert shows in chat and as center-screen text, with a sound. It stays quiet if your character has already done the chain or has its first quest in the log. Alerts never fire in combat, and the addon reads no combat data.

## Commands
| Command | What it does |
|---|---|
| `/qgs` | Open or close the window |
| `/qgs minimap` | Show or hide the minimap button |
| `/qgs stats` | How many chains you've learned (`/qgs stats tell` posts it to group chat) |
| `/qgs chains` | Chains you're on now, with step X of N |
| `/qgs tell` | Post your chain progress to party/raid chat |
| `/qgs chain on\|off` | Turn chain-started notices on or off |
| `/qgs dungeon on\|off` | Track dungeon quests and the chains that lead to them |
| `/qgs list` | Starters in your current zone |
| `/qgs near` | Nearest available starter, plus a map waypoint |
| `/qgs on` / `off` | Turn alerts on or off |
| `/qgs quality 2\|3\|4` | Minimum reward: Uncommon / Rare / Epic |
| `/qgs radius 60` | Proximity distance in yards |
| `/qgs sound on\|off`, `/qgs warn on\|off` | Sound and center-screen text |
| `/qgs export` / `import` | Share starters with friends or your guild |
| `/qgs test` | Show a sample alert |

`/qgs export` shares only the chains you learned or imported, since everyone already has the built-in ones.

## Credits
Built-in quest data comes from [AllTheThings](https://github.com/ATTWoWAddon/AllTheThings), used under the MIT License. Copyright (c) 2026 AllTheThings WoW Addon. The full licence is in `LICENSE-AllTheThings.txt`.

## For the addon author
`Data.lua` is generated by `tools/att_convert.lua` from the AllTheThings Forever data. Don't edit it by hand.

`Discoveries.lua` holds chains players found that aren't built in yet. The **QuestGearScout Companion** desktop app writes it: it reads your SavedVariables, lists what you've learned, marks what's new, and can add new chains to `Discoveries.lua` and build the CurseForge zip.
