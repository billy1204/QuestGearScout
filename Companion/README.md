# QuestGearScout Companion

A small Windows app that shows what QuestGearScout learned in game, and helps you share it and publish it.

## Getting started
Run `QuestGearScoutCompanion.exe`. It finds your WoW Forever folder by itself. If it can't, it asks you to **Choose game folder...** (the `_forever_` folder; `Interface`, `AddOns` or `WTF` inside it work too). Then play: WoW saves the addon's data when you **log out, type /reload, or exit**, and the app updates by itself a moment later.

## The window
The app uses a dark "Ember Forge" look: dark iron panels, ember-orange buttons and gold trim. Windows 10 and 11 also get a dark title bar.

- **The banner at the top** tells you what's going on and gives you the one button to press:
  - *You found 1 new chain* -> **Add 1 to addon** (or **Share with friends**)
  - *Added 1 chain to the addon* -> **Publish...** (or **Undo**)
  - *Nothing new yet* -> keep playing
  - A warning if the addon isn't installed in the folder you picked
  The step trail (1 Add to addon > 2 /reload in game > 3 Publish to CurseForge) shows where you are.
- **The list** has new finds at the top, then every chain grouped by zone. Each chain says *New*, *Added by you*, *In the addon* (a grey check) or *Can't check*. Reward names are in their quality colour; dungeons are purple. Chains that just appeared are highlighted in amber for a few seconds.
- **The right side** shows the selected chain step by step: who gives it and where (with **Copy location**), the reward, how to get it, and the buttons that fit that chain. Select several chains to act on all of them.
- **Search, filters and views**: the search box (Ctrl+F), the All / New / Gear / Dungeons pills, the gear quality filter, and **Notebook / Table**. Table shows one line per chain with sortable columns.
- **Publish menu**: *Publish a new version...* walks you through adding your finds, bumping the version number and building the CurseForge zip (junk like `.git` and backup files is left out). It also has *Save share file...* and links to the addon, saved-data and backups folders.
- **Folder...** (top right) changes the game folder or refreshes (F5).

Every change to `Discoveries.lua` makes a backup first, in `%APPDATA%\QuestGearScoutCompanion\backups` (newest 20 kept), and **Undo** (the arrow button, or Ctrl+Z) puts it back.

## Addon check
The pill at the top left shows the installed version, for example **QuestGearScout v1.8.0** in green. It turns amber when something needs attention and red when the addon isn't installed. Click it (or **Publish ▾ > Check addon...**) to see:
- which game folder is used and its game version (read from the launcher's own files, e.g. 1.60.1)
- whether QuestGearScout is installed, which version, and whether all its files are there
- whether it's up to date for the game version (the addon's interface number vs the game's)
- characters that have it turned off, and accounts with saved data
- every World of Warcraft game folder on this PC (Forever, retail, classic...), with the addon version in each. Pick one and click **Use this folder**.

If the addon isn't installed in the current folder, the app looks for it on its own and offers the right folder. If it was unzipped into the wrong place (a folder called `QuestGearScout-1.8.0`, or one folder too deep), **Fix it** moves it into place. It asks first, keeps anything in the way with `.old` added to its name, and puts everything back if a step fails.

## Local sharing (players on your network)
Friends on the same home network can swap the chains they've found and compare progress. The easiest way in is the **Local group** panel on the right of the window:

- **Not in a group yet?** Click **Look for groups** once and the panel lists the groups other players on your network have made, with how many of them are online. Click a group, pick a **nickname** and type the group's **code** (ask someone in it) to join. A wrong code is turned away. **Join with a code...** joins a group that isn't listed (for example when nobody in it is online), and **Create group** starts your own: give it a name and a code, then tell your friends the code.
- **In a group**, the panel shows its name, who's online and the players you share with, plus **Group settings...** and **Leave group**.
- The arrow at the top tucks the panel away into a slim strip (click the strip to bring it back). On a narrow window it becomes that strip by itself; click it for a menu with the same choices.

Everyone on your network can see a group's name, but only apps with its code can see you or send you chains, so use a code that's hard to guess (a few words is good). The app only listens for groups after you click **Look for groups**. The first time, Windows may ask to let the app through its firewall: allow it for **private networks**.

You can also set things up the older way: **Publish ▾ > Local sharing...** (or click the *Sharing* pill at the top), tick **Share with players on my local network**, and type a nickname, an optional group name and the code. Groups set up on older versions of the app still work together with this one; they show as *Unnamed group* to others until someone in the group types a name there.

- **Chains**: every few seconds the apps find each other and fetch what changed. Chains you don't have appear in the list marked *From <nickname>*. They're written to the addon's `Shared.lua` (sharing on its own never touches your `Discoveries.lua`), so they show up in game after `/reload`.
- **Getting a player's chains**: click a player in the **Local group** panel (a badge like *2 new* shows when they have chains you don't). A window lists the chains they shared that are new to you, plus *Update* for chains you have that they have a different version of. Only chains for the factions you play are listed. Untick any you don't want and click **Update**: the chains are copied into your `Discoveries.lua` (so they're yours, and go out with your next release), and a copy of them is kept in `Documents\QuestGearScout\Data\Received chains` (the same text `/qgs import` takes).

## Restore points
Every time the app changes `Discoveries.lua` (getting a player's chains, **Add to addon**, **Remove**, **Undo**, restoring) it first saves a **restore point** in `Documents\QuestGearScout\Data\Restore points`, a copy of the file and a note of what happened. If the change can't be saved there, nothing is changed. The new state is saved too, so you can always get back to any of them. Open **Publish ▾ > Restore points...** (also in a player's chains window), pick one and click **Restore**. The way things are now is saved first, so a restore can be undone the same way (or with Ctrl+Z). The app never deletes restore points; they're small text files you can remove yourself.
- **Horde and Alliance**: each chain carries its faction. In game a character only gets chains for its own faction plus neutral ones; a Horde chain never shows for an Alliance character, and the other way round. In the app you see the factions you play, each labelled; if you play both, **Filters ▾** can narrow it to one.
- **Players view**: a leaderboard of everyone in your group with their rank, points, factions, gear and dungeon chains, new finds, progress through the addon's chains and 15 achievements. Ranks go Wanderer, Scout, Pathfinder, Explorer, Cartographer, Loremaster, Legend.
- **Forget player** removes someone's chains and stats and keeps them out until you press **Un-forget all**. **Sync now** asks everyone for their latest data.

What's sent: your nickname, stats and the chains you found that aren't built into the addon. Nothing else from your PC or game account, and it never leaves your local network.

## Shortcuts
| Key | Does |
|---|---|
| Ctrl+C | Copy the selected chains for /qgs import |
| Ctrl+D | Add to addon |
| Del | Remove the selected chains from the addon |
| Ctrl+Z | Undo your last change |
| Ctrl+A / Ctrl+N | Select all / select all new |
| Ctrl+F | Search |
| F5 | Refresh |

Right-click a chain for the same actions plus *Copy location* and *Copy quest IDs*.

The app only reads and writes files on your PC. It never touches the running game and never sends anything over the internet (local sharing, when you turn it on, stays on your home network). It remembers its folder, account, filters, view and window size in `%APPDATA%\QuestGearScoutCompanion\settings.txt`.

## Building it
It uses the C# compiler that comes with Windows, so nothing needs installing:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:bin\QuestGearScoutCompanion.exe /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll Core.cs Ui.cs AddonCheck.cs CheckerForm.cs Progress.cs Lan.cs LanTest.cs Sharing.cs GroupBar.cs PlayerChains.cs MainForm.cs
```

`QuestGearScoutCompanion.exe --lantest <output.txt>` runs the sharing self-test on 127.0.0.1 only (handshake, wrong group code, replayed requests, fake players, groups nearby and forged group names, Horde/Alliance merging, ranks).

To check the window without clicking through it, `QuestGearScoutCompanion.exe --uitest <_forever_ folder> <output.txt> <scratch folder>` opens it off-screen, drives it (filters, search, add, undo, views, small sizes, Publish steps, the Local group panel with join and create, getting a player's chains, restore points), writes what it showed and saves screenshots next to the output file. Point it at a **copy** of your WoW folder, because it really writes `Discoveries.lua`.
