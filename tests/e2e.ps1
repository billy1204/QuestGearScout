# End-to-end check: the addon's real code records quests (tests/qgs_test.lua acts out questing),
# its data is written the way the game writes SavedVariables, and the Companion app reads it back.
# Run from the repo folder:  powershell -File tests\e2e.ps1 [-Exe <companion exe>] [-Lua <lua5.1.exe>]
param(
    [string]$Exe = "Companion\bin\QGSC-dev.exe",
    [string]$Lua = "$env:LOCALAPPDATA\Programs\Lua51\lua5.1.exe"
)
$ErrorActionPreference = "Stop"
$work = Join-Path ([IO.Path]::GetTempPath()) ("qgs-e2e-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
$sv = Join-Path $work "QuestGearScout.lua"
$report = Join-Path $work "report.txt"

$env:QGS_SAVE = $sv
& $Lua tests\qgs_test.lua | Select-String "^wrote|passed" | ForEach-Object { "addon: " + $_.Line.Trim() }
Remove-Item Env:\QGS_SAVE
if (-not (Test-Path $sv)) { "FAIL: the addon test didn't write saved data"; exit 1 }

# The test ends with the addon's built-in quest data switched off, so the app gets the same: an
# addon folder whose Data.lua is empty (otherwise built-in links put test quests into other chains).
$addon = Join-Path $work "QuestGearScout"
New-Item -ItemType Directory -Force $addon | Out-Null
Copy-Item QuestGearScout\QuestGearScout.toc $addon
[IO.File]::WriteAllText((Join-Path $addon "Data.lua"), "local _, ns = ...`nns.BUNDLED_EXPORT = [=====[`nQGS1`n]=====]`nns.BUNDLED_QUESTS = {}`n", (New-Object Text.UTF8Encoding($false)))
$p = Start-Process $Exe -ArgumentList "--report", "`"$sv`"", "`"$addon`"", "`"$report`"" -Wait -PassThru
if (-not (Test-Path $report)) { "FAIL: the Companion app didn't write its report"; exit 1 }

$expected = Get-Content "$sv.expected"
$wantQuests = [int]($expected[0] -replace "^quests ", "")
$want = $expected | Select-Object -Skip 1 | Where-Object { $_ -ne "" }
$lines = Get-Content $report
$gotQuests = [int](([regex]::Match($lines[1], "quests seen (\d+)")).Groups[1].Value)
$got = @{}
foreach ($l in $lines) {
    $c = $l -split "`t"
    if ($c.Count -ge 3 -and ($c[1] -eq "Gear" -or $c[1] -eq "Dungeon")) { $got[$c[2] + "`t" + $c[1]] = $true }
}
$fails = 0
if ($gotQuests -eq $wantQuests) { "  ok    the app sees all $wantQuests quests the addon saved" }
else { "  FAIL  the addon saved $wantQuests quests, the app sees $gotQuests"; $fails++ }
$missing = @($want | Where-Object { -not $got.ContainsKey($_) })
if ($missing.Count -eq 0) { "  ok    every chain the addon learned ($($want.Count)) shows up in the app" }
else { "  FAIL  chains the addon learned that the app doesn't show: " + ($missing -join ", "); $fails++ }
Remove-Item -Recurse -Force $work -Confirm:$false
if ($fails -eq 0) { "end-to-end: passed" } else { "end-to-end: $fails failed"; exit 1 }
