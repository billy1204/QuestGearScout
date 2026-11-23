# QuestGearScout

A World of Warcraft: Forever addon that alerts you near NPCs and objects that start quest chains ending in Uncommon-or-better gear, or leading to dungeon quests. It also comes with a Windows companion app.

| Folder | What's in it |
|---|---|
| [`QuestGearScout/`](QuestGearScout) | The addon (Lua). Copy this folder into `World of Warcraft\_forever_\Interface\AddOns`. See its [README](QuestGearScout/README.md). |
| [`Companion/`](Companion) | **QuestGearScout Companion**, a Windows app (C#, WinForms) that shows what the addon learned, adds new finds to the addon, shares chains with friends on your home network, and builds the CurseForge zip. See its [README](Companion/README.md). |
| [`tests/`](tests) | Addon tests, run with Lua 5.1: `lua5.1 tests/qgs_test.lua` (from this folder). |
| [`tools/`](tools) | `att_convert.lua`, which builds `QuestGearScout/Data.lua` from AllTheThings data. |

## Building the Companion app
No install needed; it uses the C# compiler that ships with Windows. From this folder:

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:Companion\bin\QuestGearScoutCompanion.exe /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll Companion\Core.cs Companion\Ui.cs Companion\AddonCheck.cs Companion\CheckerForm.cs Companion\Progress.cs Companion\Lan.cs Companion\LanTest.cs Companion\Sharing.cs Companion\GroupBar.cs Companion\PlayerChains.cs Companion\MainForm.cs
```

Self-tests: `QuestGearScoutCompanion.exe --lantest out.txt` (local sharing) and `--uitest` (the window; see the Companion README).

## Licence
MIT, see [`LICENSE`](LICENSE).

## Credits
The built-in chain data comes from [AllTheThings](https://github.com/ATTWoWAddon/AllTheThings) (MIT licence, see [`QuestGearScout/LICENSE-AllTheThings.txt`](QuestGearScout/LICENSE-AllTheThings.txt)).
