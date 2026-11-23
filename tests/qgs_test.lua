-- QuestGearScout logic tests. Run from the folder that holds QuestGearScout/:
--   lua5.1 tests/qgs_test.lua
-- Fakes just enough of the WoW API to load the real addon files and drive
-- quest events. It can't test frames, visuals or real game behaviour.

---------------------------------------------------------------------------
-- Fake WoW environment
---------------------------------------------------------------------------
local handlers = {}           -- event -> list of { frame, fn }
local chat, sent = {}, {}

local newFrame
newFrame = function()
    local f = { _events = {}, _scripts = {} }
    return setmetatable(f, { __index = function(t, k)
        if k == "RegisterEvent" then
            return function(self, ev) self._events[ev] = true end
        elseif k == "SetScript" or k == "HookScript" then
            return function(self, name, fn) self._scripts[name] = fn end
        elseif type(k) == "string" and k:find("^Create") then
            return function() return newFrame() end  -- CreateTexture, CreateFontString...
        elseif k == "GetStringHeight" or k == "GetWidth" then
            return function() return 0 end
        elseif k == "IsShown" then
            return function() return true end
        end
        return function() end   -- every other method is a no-op
    end })
end

function CreateFrame() local f = newFrame(); handlers[#handlers + 1] = f; return f end
UIParent, Minimap, GameTooltip = newFrame(), newFrame(), newFrame()
UISpecialFrames, SlashCmdList = {}, {}
DEFAULT_CHAT_FRAME = { AddMessage = function(_, m) chat[#chat + 1] = m end }
print = function(...) chat[#chat + 1] = table.concat({ ... }, " ") end
C_Timer = { After = function() end, NewTicker = function() end }
SOUNDKIT = {}
function PlaySound() end
function RaidNotice_AddMessage() end
RaidWarningFrame = {}
function InCombatLockdown() return false end
function tinsert(t, v) t[#t + 1] = v end
function wipe(t) for k in pairs(t) do t[k] = nil end return t end
function strtrim(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
function strsplit(sep, s)
    local out = {}
    for piece in (s .. sep):gmatch("(.-)" .. sep:gsub("%p", "%%%0")) do out[#out + 1] = piece end
    return unpack(out)
end

local now = 1000
function GetTime() return now end
local clock = 1700000000
function time() return clock end

-- Player position / maps
local player = { map = 1425, x = 0.50, y = 0.50 }
C_Map = {
    GetBestMapForUnit = function() return player.map end,
    GetPlayerMapPosition = function()
        return { GetXY = function() return player.x, player.y end }
    end,
    GetWorldPosFromMapPos = function(map, v)       -- 1 map unit = 1000 yards
        return 1, { GetXY = function() return v.x * 1000, v.y * 1000 end }
    end,
}
function CreateVector2D(x, y) return { x = x, y = y } end

-- Quest state
local done, inLog = {}, {}
function IsQuestFlaggedCompleted(id) return done[id] or false end
function GetQuestLogIndexByID(id) return inLog[id] and 1 or 0 end

-- Current quest dialog
local dialog = { npc = nil, npcName = nil, qid = 0, title = "", rewards = {} }
function UnitGUID(unit) if unit == "npc" then return dialog.npc end end
function UnitName(unit) if unit == "npc" then return dialog.npcName end return "Tester" end
function GetQuestID() return dialog.qid end
function GetTitleText() return dialog.title end
function GetNumQuestChoices() return 0 end
function GetNumQuestRewards() return #dialog.rewards end
function GetQuestItemInfo(_, i) local r = dialog.rewards[i]; return r.name, nil, 1, r.quality end
function GetQuestItemLink(_, i) return dialog.rewards[i].link end
function GetItemInfo() end

function IsInGroup() return true end
function IsInRaid() return false end
function SendChatMessage(msg, ch) sent[#sent + 1] = ch .. ": " .. msg end

local function fire(event, ...)
    for _, f in ipairs(handlers) do
        if f._events[event] and f._scripts.OnEvent then f._scripts.OnEvent(f, event, ...) end
    end
end

---------------------------------------------------------------------------
-- Load the real addon files
---------------------------------------------------------------------------
local ns = {}
assert(loadfile("QuestGearScout/Data.lua"))("QuestGearScout", ns)
-- Most tests run without the built-in data so they only see what they set up.
local REAL_EXPORT, REAL_QUESTS = ns.BUNDLED_EXPORT, ns.BUNDLED_QUESTS
ns.BUNDLED_EXPORT, ns.BUNDLED_QUESTS = "", {}
for _, file in ipairs({ "Core.lua", "Alerts.lua", "Share.lua", "UI.lua" }) do
    local chunk = assert(loadfile("QuestGearScout/" .. file))
    chunk("QuestGearScout", ns)
end
fire("ADDON_LOADED", "QuestGearScout")
local QGS = ns.QGS

---------------------------------------------------------------------------
-- Tiny test runner
---------------------------------------------------------------------------
local passed, failed = 0, 0
local function check(name, cond, detail)
    if cond then passed = passed + 1; io.write("  ok    ", name, "\n")
    else failed = failed + 1; io.write("  FAIL  ", name, detail and ("  (" .. tostring(detail) .. ")") or "", "\n") end
end
local function chatHas(pattern)
    for _, m in ipairs(chat) do if m:find(pattern, 1, true) then return true end end
    return false
end
local function clearChat() for k in pairs(chat) do chat[k] = nil end end

local GLOVES = "|cff1eff00|Hitem:5001::::::::|h[Digger's Gloves]|h|r"

-- Helpers that act out talking to an NPC
local function offer(qid, title, npcID, npcName, rewards)
    dialog.npc = "Creature-0-1-2-3-" .. npcID .. "-0000"
    dialog.npcName, dialog.qid, dialog.title, dialog.rewards = npcName, qid, title, rewards or {}
    fire("QUEST_DETAIL")
end
local function accept(qid, classicArgs)
    inLog[qid] = true
    if classicArgs then fire("QUEST_ACCEPTED", 5, qid) else fire("QUEST_ACCEPTED", qid) end
end
local function turnIn(qid, npcID, rewards)
    dialog.npc = "Creature-0-1-2-3-" .. npcID .. "-0000"
    dialog.qid, dialog.rewards = qid, rewards or {}
    fire("QUEST_COMPLETE")
    inLog[qid], done[qid] = nil, true
    fire("QUEST_TURNED_IN", qid, 100, 0)
end

---------------------------------------------------------------------------
print = function(...) io.write(table.concat({ ... }, " "), "\n") end
local capture = function(...) chat[#chat + 1] = table.concat({ ... }, " ") end

io.write("Setup\n")
check("defaults created", QuestGearScoutDB and QuestGearScoutDB.settings.minQuality == 2)
check("minimap setting default", QuestGearScoutDB.settings.minimap == true)
print = capture

io.write("Learning a 3-step chain that ends in green gloves\n")
offer(1, "Ironband's Excavation", 100, "Explorer Fellhew")
accept(1)
check("step 1 alone gives no notice", not chatHas("Quest chain"))
now = now + 30
turnIn(1, 200)
now = now + 3
offer(2, "Digging Deeper", 200, "Prospector Ironband")
accept(2)
check("quest 2 linked to quest 1", QuestGearScoutDB.quests[2].prev == 1)
check("'Quest chain started!' notice", chatHas("Quest chain started!"), chat[#chat])
clearChat()
now = now + 60
turnIn(2, 200)
now = now + 100   -- slower than 20s, but same NPC within 3 minutes
offer(3, "The Last Dig", 200, "Prospector Ironband")
accept(3, true)   -- Classic-style event arguments
check("quest 3 linked via same-NPC window", QuestGearScoutDB.quests[3].prev == 2)
check("step 3 notice shows 3 of 3+", chatHas("step 3 of 3+"), chat[#chat])
clearChat()
turnIn(3, 200, { { name = "Digger's Gloves", quality = 2, link = GLOVES } })

local s = QGS.starters["C:100"]
check("starter recorded for first NPC", s ~= nil)
check("starter has 3 steps", s and s.steps == 3, s and s.steps)
check("starter remembers the gloves", s and s.item == GLOVES)
check("'Recorded a gear chain' message with count", chatHas("You've learned 1 gear chain."), chat[#chat])

io.write("Chain maths\n")
local rootID, _, step = QGS.ChainPos(3)
check("ChainPos(3) -> root 1, step 3", rootID == 1 and step == 3, tostring(rootID) .. "/" .. tostring(step))
local total, complete = QGS.ChainTotal(1)
check("ChainTotal(1) = 3, complete", total == 3 and complete == true)
check("StepText complete", QGS.StepText(2, 3, true) == "step 2 of 3")
check("StepText learning", QGS.StepText(4, 3, false) == "step 4 of 4+")

io.write("Stats\n")
check("1 gear chain learned", QGS.stats.learned == 1 and QGS.stats.gear == 1)
check("1 quest chain recorded", QGS.stats.chains == 1)
check("longest chain 3", QGS.stats.longest == 3)
check("StatsText", QGS.StatsText() == "1 gear chain known (1 learned)", QGS.StatsText())

io.write("No link when the next quest comes much later from someone else\n")
now = now + 600
offer(10, "Unrelated Errand", 300, "Farmer Ted")
accept(10)
check("quest 10 not linked", QuestGearScoutDB.quests[10].prev == nil)

io.write("Re-offering a known chain on an alt\n")
done[1], done[2], done[3] = nil, nil, nil
clearChat()
offer(1, "Ironband's Excavation", 100, "Explorer Fellhew")
check("quest window says it starts a 3-step chain", chatHas("starts a 3-step chain"), chat[#chat])
accept(1)
check("'Gear quest chain started!' on accept", chatHas("Gear quest chain started!"), chat[#chat])
inLog[1] = nil

io.write("Export / import\n")
local text = ns.Encode(QGS.starters)
local back, n = ns.Decode(text)
check("round trip keeps 1 starter", n == 1)
check("item link survives (pipes escaped)", back["C:100"] and back["C:100"].item == GLOVES)
check("coordinates survive", back["C:100"] and math.abs(back["C:100"].x - 0.5) < 1e-3)
local tricky = { ["O:9"] = { kind = "O", id = 9, map = 1, x = 0.1, y = 0.2, quality = 3, steps = 2,
                             name = "Odd\tName|100%", item = "x\ny" } }
local b2 = ns.Decode(ns.Encode(tricky))
check("tabs, pipes, % and newlines survive", b2["O:9"] and b2["O:9"].name == "Odd\tName|100%" and b2["O:9"].item == "x\ny")
local _, junk = ns.Decode("QGS1\nnot\ta\tvalid\tline\n\n")
check("junk lines ignored", junk == 0)
QuestGearScoutDB.imported["O:9"] = b2["O:9"]
QGS.Rebuild()
check("imported counted separately", QGS.stats.imported == 1 and QGS.stats.learned == 1 and QGS.stats.gear == 2)

io.write("Proximity and availability\n")
player.x, player.y = 0.52, 0.50   -- 20 yards away
local d = QGS.DistanceTo(QGS.starters["C:100"])
check("distance about 20 yards", d and math.abs(d - 20) < 0.5, d)
check("available before doing it", QGS.Available(QGS.starters["C:100"]))
done[1] = true
check("not available once done", not QGS.Available(QGS.starters["C:100"]))
done[1] = nil

io.write("Alert cooldown\n")
clearChat()
QGS.Alert(QGS.starters["C:100"], "test")
QGS.Alert(QGS.starters["C:100"], "test")
local alerts = 0
for _, m in ipairs(chat) do if m:find("starts a 3-step chain", 1, true) then alerts = alerts + 1 end end
check("same starter alerts once per cooldown", alerts == 1, alerts)

io.write("Quality filter\n")
QuestGearScoutDB.settings.minQuality = 3
QGS.Rebuild()
check("green chain hidden at Rare+", QGS.starters["C:100"] == nil)
check("rare imported chain still shown", QGS.starters["O:9"] ~= nil)
QuestGearScoutDB.settings.minQuality = 2
QGS.Rebuild()

io.write("Chat commands\n")
inLog[2] = true
local chains = QGS.ActiveChains()
check("ActiveChains finds quest 2 as step 2 of 3", #chains == 1 and chains[1].step == 2 and chains[1].total == 3)
QGS.TellChains()
check("/qgs tell posts to party", sent[#sent] and sent[#sent]:find("PARTY: [QuestGearScout] Digging Deeper: step 2 of 3", 1, true), sent[#sent])
QGS.TellStats()
check("/qgs stats tell posts totals", sent[#sent] and sent[#sent]:find("2 gear chains known", 1, true), sent[#sent])
for _, cmd in ipairs({ "list", "stats", "chains", "near", "help", "quality 9", "radius 5", "minimap", "minimap" }) do
    local ok, err = pcall(SlashCmdList.QUESTGEARSCOUT, cmd)
    check("/qgs " .. cmd .. " runs without error", ok, err)
end
check("quality clamped to 5", QuestGearScoutDB.settings.minQuality == 5)
check("radius clamped to 10", QuestGearScoutDB.settings.radius == 10)

io.write("Dungeon quests\n")
QuestGearScoutDB.settings.minQuality = 2
QGS.Rebuild()
-- Fake quest log, instance state and quest tags
local logList, instance, tags = {}, nil, {}
function GetNumQuestLogEntries() return #logList end
function GetQuestLogTitle(i)
    local e = logList[i]
    return e.title, 10, 0, false, false, nil, 1, e.qid
end
function GetNumQuestLeaderBoards(i) return #logList[i].obj end
function GetQuestLogLeaderBoard(j, i) local o = logList[i].obj[j]; return o[1], "monster", o[2] end
function IsInInstance() if instance then return true, "party" end return false, "none" end
function GetInstanceInfo() return instance end
function GetQuestTagInfo(qid) if tags[qid] then return tags[qid], "Dungeon" end end
local function logAdd(qid, title, text) logList[#logList + 1] = { qid = qid, title = title, obj = { { text, false } } } end
local function logFind(qid) for _, e in ipairs(logList) do if e.qid == qid then return e end end end

now = now + 600
offer(20, "Report to Gryan", 400, "Scout Riell")
accept(20)
now = now + 30
turnIn(20, 401)
now = now + 3
offer(21, "The Defias Brotherhood", 401, "Gryan Stoutmantle")
accept(21)
check("dungeon quest linked to the quest before it", QuestGearScoutDB.quests[21].prev == 20)
logAdd(21, "The Defias Brotherhood", "Edwin VanCleef slain: 0/1")
logAdd(10, "Unrelated Errand", "Wool: 0/5")
fire("QUEST_LOG_UPDATE")
logFind(10).obj[1][1] = "Wool: 1/5"
fire("QUEST_LOG_UPDATE")
check("progress outside a dungeon doesn't flag a quest", not QuestGearScoutDB.quests[10].dungeon)
instance = "The Deadmines"
fire("PLAYER_ENTERING_WORLD")
check("just entering a dungeon flags nothing", not QuestGearScoutDB.quests[21].dungeon)
clearChat()
logFind(21).obj[1] = { "Edwin VanCleef slain: 1/1", true }
fire("QUEST_LOG_UPDATE")
local q21 = QuestGearScoutDB.quests[21]
check("progress inside a dungeon flags the quest", q21.dungeon == true and q21.dungeonName == "The Deadmines")
check("'Recorded a dungeon quest' message", chatHas("Recorded a dungeon quest:") and chatHas("step 2"), chat[#chat])
instance = nil
local ds = QGS.starters["C:400"]
check("chain's first NPC becomes a dungeon starter", ds and ds.dungeonSteps == 2 and ds.dRoot == 20 and not ds.quality)
check("starter knows the dungeon", ds and ds.dungeonName == "The Deadmines" and ds.dungeonTitle == "The Defias Brotherhood")
check("stats count dungeon chains", QGS.stats.dungeon == 1 and QGS.stats.dungeonQuests == 1)
check("StatsText mentions dungeon chains", QGS.StatsText():find("1 dungeon chain", 1, true), QGS.StatsText())
local dc = QGS.ActiveChains()
local found
for _, c in ipairs(dc) do if c.title == "The Defias Brotherhood" then found = c end end
check("My chains shows the dungeon quest", found and found.isDungeon and found.dungeonName == "The Deadmines")

clearChat()
QGS.Alert(ds, "test", true)
check("alert says it leads to a dungeon quest", chatHas("2-step chain to a dungeon quest") and chatHas("The Deadmines"), chat[#chat])

io.write("Dungeon chain on an alt\n")
done[20], done[21], inLog[21] = nil, nil, nil
logList = {}
clearChat()
now = now + 600
offer(20, "Report to Gryan", 400, "Scout Riell")
check("quest window says it starts a chain to a dungeon quest", chatHas("2-step chain to a dungeon quest"), chat[#chat])
accept(20)
check("'Dungeon quest chain started!' on accept", chatHas("Dungeon quest chain started!"), chat[#chat])
inLog[20] = nil

io.write("Dungeon tag from the quest itself\n")
tags[30] = 81
clearChat()
now = now + 600
offer(30, "The Scarlet Crusade", 500, "Varimathras")
check("tagged quest flagged as dungeon", QuestGearScoutDB.quests[30].dungeon == true)
check("quest window says 'This is a dungeon quest'", chatHas("This is a dungeon quest"), chat[#chat])
accept(30)
check("'Dungeon quest accepted!' on accept", chatHas("Dungeon quest accepted!"), chat[#chat])
check("single dungeon quest giver is a starter", QGS.starters["C:500"] and QGS.starters["C:500"].dungeonSteps == 1)
inLog[30] = nil

io.write("Turn-in inside a dungeon\n")
now = now + 600
offer(40, "Lost Ring", 600, "Old Miner")
accept(40)
instance = "The Deadmines"
turnIn(40, 601)
instance = nil
check("turning in inside a dungeon flags the quest", QuestGearScoutDB.quests[40].dungeon == true)

io.write("Dungeon export / import\n")
local dback, dn = ns.Decode(ns.Encode({ ["C:400"] = QGS.starters["C:400"] }))
local d4 = dback["C:400"]
check("dungeon-only starter survives export", dn == 1 and d4 and d4.dungeonSteps == 2 and d4.dungeonName == "The Deadmines")
local _, oldN = ns.Decode("QGS1\nC\t7\t1425\t0.5\t0.5\t1\t2\t3\tOld\tA\tB\tC")
check("pre-1.6 exports still import", oldN == 1)

io.write("Dungeon setting\n")
SlashCmdList.QUESTGEARSCOUT("dungeon off")
check("/qgs dungeon off hides dungeon starters", QGS.starters["C:400"] == nil and QGS.starters["C:100"] ~= nil)
SlashCmdList.QUESTGEARSCOUT("dungeon on")
check("/qgs dungeon on brings them back", QGS.starters["C:400"] ~= nil)
QuestGearScoutDB.settings.minQuality = 5
QGS.Rebuild()
check("dungeon starters ignore the reward-quality filter", QGS.starters["C:400"] ~= nil and QGS.starters["C:500"] ~= nil)
check("no-reward dungeon quest still alerts", QGS.starters["C:400"].quality == nil)
QuestGearScoutDB.settings.minQuality = 2
QGS.Rebuild()

io.write("Window (smoke test: each tab draws without a Lua error)\n")
QuestGearScoutDB.settings.minQuality = 2
QGS.Rebuild()
-- ToggleUI reports errors in chat instead of raising them, so check chat too.
local function drew(tab)
    clearChat()
    local ok, err = pcall(QGS.ToggleUI, tab)
    if not ok then return false, err end
    for _, m in ipairs(chat) do if m:find("error", 1, true) then return false, m end end
    return true
end
for _, tab in ipairs({ "nearby", "chains", "stats", "settings" }) do
    check("tab '" .. tab .. "' draws", drew(tab))
end
player.map = nil
check("Nearby tab copes with no position (dungeon)", drew("nearby"))
local ok, err
player.map = 1425
ok, err = pcall(QGS.ShowToast, "Quest chain started!", "detail")
check("banner shows without error", ok, err)
ok, err = pcall(QGS.UpdateMinimapButton)
check("minimap button builds without error", ok, err)

-- Error reporting: a broken window must say so in chat, not fail silently.
clearChat()
local realRefresh = QGS.RefreshUI
QGS.RefreshUI = function() error("boom") end
QGS.ToggleUI("stats")
QGS.RefreshUI = realRefresh
check("window errors are reported in chat", chatHas("error opening the window"), chat[1])

clearChat()
function GetAddOnMetadata(_, field) if field == "Version" then return "1.4.0" end end
local delayed = 0
C_Timer.After = function(_, fn) delayed = delayed + 1; fn() end   -- run timers immediately
check("open-at-login defaults on", QuestGearScoutDB.settings.openOnLogin == true)
fire("PLAYER_LOGIN")
check("login says which version loaded", chatHas("v1.4.0 loaded"), chat[1])
check("window opened at login without error", delayed > 0 and not chatHas("error"), chat[#chat])
QuestGearScoutDB.settings.openOnLogin = false
delayed = 0
fire("PLAYER_LOGIN")
check("no auto-open when the setting is off", delayed == 0)

io.write("Ctrl+Q shortcut\n")
local overrides, userBinds, combat = {}, {}, false
function SetOverrideBinding(_, _, key, action) overrides[key] = action end
function ClearOverrideBindings() for k in pairs(overrides) do overrides[k] = nil end end
function GetBindingAction(key) return userBinds[key] or "" end
InCombatLockdown = function() return combat end
QuestGearScoutDB.settings.hotkey = true
QGS.UpdateHotkey()
check("Ctrl+Q bound to the toggle action", overrides["CTRL-Q"] == "QUESTGEARSCOUT_TOGGLE")
QuestGearScoutDB.settings.hotkey = false
QGS.UpdateHotkey()
check("turning it off removes the binding", overrides["CTRL-Q"] == nil)
QuestGearScoutDB.settings.hotkey = true
userBinds["CTRL-Q"] = "TOGGLEWORLDMAP"
clearChat()
QGS.UpdateHotkey()
check("doesn't steal Ctrl+Q from the player's own binding", overrides["CTRL-Q"] == nil and chatHas("already used"))
userBinds["CTRL-Q"] = nil
combat = true
QGS.UpdateHotkey()
check("waits while in combat", overrides["CTRL-Q"] == nil)
combat = false
fire("PLAYER_REGEN_ENABLED")
check("binds once combat ends", overrides["CTRL-Q"] == "QUESTGEARSCOUT_TOGGLE")
check("binding labels set", BINDING_NAME_QUESTGEARSCOUT_TOGGLE ~= nil and BINDING_HEADER_QUESTGEARSCOUT == "QuestGearScout")
local okBind = pcall(loadstring("QuestGearScout.ToggleUI()"))
check("Bindings.xml code runs", okBind and not chatHas("error"))

io.write("Built-in AllTheThings data\n")
local realStarters, realN = ns.Decode(REAL_EXPORT)
check("built-in data decodes (600+ starters)", realN > 600, realN)
ns.bundled, ns.BUNDLED_QUESTS = realStarters, REAL_QUESTS
QGS.Rebuild()
local gryan = QGS.starters["C:234"]
check("Gryan Stoutmantle starts the Defias chain", gryan and gryan.dungeonName == "Deadmines" and gryan.dungeonSteps == 7, gryan and gryan.dungeonSteps)
check("...ending in Rare gear", gryan and gryan.quality == 3 and gryan.item:find("Chausses of Westfall", 1, true))
local r166, root166, step166 = QGS.ChainPos(166)
check("ChainPos uses built-in links (166 is step 7 of 65)", r166 == 65 and step166 == 7 and root166.title == "The Defias Brotherhood (1/7)",
    tostring(r166) .. "/" .. tostring(step166))
check("built-in counted as built-in", (QGS.stats.bundled or 0) > 600)
clearChat()
inLog[166], done[166] = nil, nil
offer(166, "The Defias Brotherhood", 234, "Gryan Stoutmantle")
check("built-in dungeon quest flagged on first sight", QuestGearScoutDB.quests[166].dungeon == true and chatHas("This is a dungeon quest (Deadmines)"), chat[#chat])
function UnitFactionGroup() return "Horde" end
QGS.Rebuild()
check("Alliance chain hidden for a Horde character", QGS.starters["C:234"] == nil)
function UnitFactionGroup() return "Alliance" end
function UnitClass() return "Mage", "MAGE" end
QGS.Rebuild()
check("Alliance chain shown for an Alliance character", QGS.starters["C:234"] ~= nil)
local classOnly
for k, s in pairs(realStarters) do if s.classes and not s.classes:find("MAGE") then classOnly = k; break end end
check("other classes' chains hidden", classOnly and QGS.starters[classOnly] == nil, classOnly)
local exported = select(2, ns.Decode((function() local m = {} for k, s in pairs(QGS.starters) do if QGS.sources[k] ~= "bundled" then m[k] = s end end return ns.Encode(m) end)()))
check("export leaves out built-in chains", exported < 50, exported)
check("tabs draw with the full data", drew("nearby") and drew("stats"))

io.write("Factions (1.9.0)\n")
ns.bundled, ns.BUNDLED_QUESTS = {}, {}
UnitClass = function() return "Warrior", "WARRIOR" end
local HAXE = "|cff1eff00|Hitem:9001::::::::|h[Orcish Cleaver]|h|r"
function UnitFactionGroup() return "Horde" end
now = now + 900
offer(900, "Blood and Iron", 950, "Grunt Gorrak", { { name = "Orcish Cleaver", quality = 2, link = HAXE } })
accept(900)
check("quest remembers it was taken by a Horde character", QuestGearScoutDB.quests[900].faction == "H", QuestGearScoutDB.quests[900].faction)
turnIn(900, 950, { { name = "Orcish Cleaver", quality = 2, link = HAXE } })
QGS.Rebuild()
check("Horde character sees its own chain", QGS.starters["C:950"] ~= nil and QGS.starters["C:950"].faction == "H")
function UnitFactionGroup() return "Alliance" end
QGS.Rebuild()
check("Alliance character doesn't see the Horde chain", QGS.starters["C:950"] == nil)
done[900] = nil
offer(900, "Blood and Iron", 950, "Grunt Gorrak", { { name = "Orcish Cleaver", quality = 2, link = HAXE } })
check("quest taken by both factions becomes neutral", QuestGearScoutDB.quests[900].faction == "B", QuestGearScoutDB.quests[900].faction)
QGS.Rebuild()
check("a chain both factions did is shown to everyone", QGS.starters["C:950"] ~= nil and QGS.starters["C:950"].faction == nil)
check("old data without a faction is still shown", QGS.starters["C:100"] ~= nil)

io.write("Shared chains from other players (1.9.0)\n")
local sharedText = "QGS1\n" ..
    "C\t960\t1429\t0.5\t0.5\t961\t3\t2\tHorde Scout\tA\tB\t\t\t\t\t\t\tH\t\n" ..
    "C\t970\t1429\t0.6\t0.6\t971\t2\t1\tAlliance Scout\tA\tB\t\t\t\t\t\t\tA\t\n" ..
    "C\t980\t1429\t0.7\t0.7\t981\t2\t1\tNeutral Scout\tA\tB\t\t\t\t\t\t\t\t\n"
ns.shared = {}
for _, s in pairs((ns.Decode(sharedText))) do ns.shared[#ns.shared + 1] = s end
check("shared chains decode with their faction", #ns.shared == 3)
QGS.Rebuild()
check("Alliance sees the shared Alliance chain", QGS.starters["C:970"] ~= nil)
check("Alliance sees the shared neutral chain", QGS.starters["C:980"] ~= nil)
check("Alliance doesn't see the shared Horde chain", QGS.starters["C:960"] == nil)
check("shared chains counted as shared", (QGS.stats.shared or 0) == 2, QGS.stats.shared)
check("StatsText mentions shared", QGS.StatsText():find("shared", 1, true) ~= nil, QGS.StatsText())
function UnitFactionGroup() return "Horde" end
QGS.Rebuild()
check("Horde sees the shared Horde chain, not the Alliance one", QGS.starters["C:960"] ~= nil and QGS.starters["C:970"] == nil)
local shippedNs = {}
assert(loadfile("QuestGearScout/Shared.lua"))("QuestGearScout", shippedNs)
local _, shippedCount = ns.Decode(shippedNs.SHARED_EXPORT)
check("shipped Shared.lua is empty", shippedNs.SHARED_EXPORT ~= nil and shippedCount == 0, shippedCount)
function UnitFactionGroup() return "Alliance" end
-- One giver with a Horde chain and an Alliance chain: both survive decoding, each faction sees its own.
local twoText = "QGS1\n" ..
    "C\t990\t1429\t0.5\t0.5\t991\t4\t2\tTwo%5BFaced%5D Giver\tHorde root\tB\t\t\t\t\t\t\tH\t\n" ..
    "C\t990\t1429\t0.5\t0.5\t995\t2\t1\tTwo%5BFaced%5D Giver\tAlliance root\tB\t\t\t\t\t\t\tA\t\n"
local two, twoCount = ns.Decode(twoText)
local kept = 0
for _ in pairs(two) do kept = kept + 1 end
check("same giver, two factions: both entries kept", twoCount == 2 and kept == 2, kept)
check("%5B / %5D decode to [ and ]", two["C:990"] and two["C:990"].name == "Two[Faced] Giver", two["C:990"] and two["C:990"].name)
ns.shared = {}
for _, s in pairs(two) do ns.shared[#ns.shared + 1] = s end
QGS.Rebuild()
check("Alliance gets the Alliance chain of a two-faction giver", QGS.starters["C:990"] ~= nil and QGS.starters["C:990"].rootQuest == 995,
    QGS.starters["C:990"] and QGS.starters["C:990"].rootQuest)
function UnitFactionGroup() return "Horde" end
QGS.Rebuild()
check("Horde gets the Horde chain of a two-faction giver", QGS.starters["C:990"] ~= nil and QGS.starters["C:990"].rootQuest == 991,
    QGS.starters["C:990"] and QGS.starters["C:990"].rootQuest)
function UnitFactionGroup() return "Alliance" end
ns.shared = {}
QGS.Rebuild()

io.write("Faction saved with every learned chain\n")
local GIVER = function(id, name) return { kind = "C", id = id, name = name, map = 1429, x = 0.45, y = 0.45 } end
function UnitFactionGroup() return "Horde" end
-- Picked up without the quest window (auto-accepted): turning it in still records the faction.
dialog.npc, dialog.qid, dialog.title, dialog.rewards = "Creature-0-1-2-3-1260-0000", 1210, "Quick Errand", {}
QuestGearScoutDB.quests[1210] = { title = "Quick Errand", giver = GIVER(1260, "Errand Giver") }
turnIn(1210, 1260, { { name = "Orcish Cleaver", quality = 2, link = HAXE } })
check("turning in a quest records the character's faction", QuestGearScoutDB.quests[1210].faction == "H", QuestGearScoutDB.quests[1210].faction)
-- Recorded before factions were saved: filled in at login if this character has done it.
QuestGearScoutDB.quests[1200] = { title = "Old Quest", giver = GIVER(1250, "Old Giver"), quality = 2 }
QuestGearScoutDB.quests[1201] = { title = "Someone Else's", giver = GIVER(1251, "Other Giver"), quality = 2 }
done[1200] = true
fire("PLAYER_LOGIN")
check("login fills in the faction of quests this character has done", QuestGearScoutDB.quests[1200].faction == "H", QuestGearScoutDB.quests[1200].faction)
check("quests this character hasn't done stay without one", QuestGearScoutDB.quests[1201].faction == nil, QuestGearScoutDB.quests[1201].faction)
check("the learned chain gets that faction", QGS.starters["C:1250"] ~= nil and QGS.starters["C:1250"].faction == "H")
-- A quest in the log (taken some other way) counts too.
QuestGearScoutDB.quests[1202] = { title = "In My Log", giver = GIVER(1252, "Log Giver"), quality = 2 }
inLog[1202] = true
QGS.BackfillFactions()
check("a quest in this character's log gets its faction", QuestGearScoutDB.quests[1202].faction == "H", QuestGearScoutDB.quests[1202].faction)
inLog[1202] = nil
-- A Horde character learns a chain: it's listed as Horde.
local SWORD = "|cff0070dd|Hitem:9100::::::::|h[Herald's Blade]|h|r"
done[1220], done[1221], inLog[1220], inLog[1221] = nil, nil, nil, nil
now = now + 900
offer(1220, "Shared Start", 1270, "Crossroads Herald")
accept(1220)
turnIn(1220, 1270)
offer(1221, "Herald's Reward", 1270, "Crossroads Herald", { { name = "Herald's Blade", quality = 3, link = SWORD } })
accept(1221)
turnIn(1221, 1270, { { name = "Herald's Blade", quality = 3, link = SWORD } })
QGS.Rebuild()
check("a chain learned by a Horde character is listed as Horde", QGS.starters["C:1270"] ~= nil and QGS.starters["C:1270"].faction == "H",
    QGS.starters["C:1270"] and tostring(QGS.starters["C:1270"].faction))
function UnitFactionGroup() return "Alliance" end
QGS.Rebuild()
check("an Alliance character isn't shown it yet", QGS.starters["C:1270"] == nil)
-- An Alliance character sees the chain in game (opens its first quest): now it's for everyone.
done[1220] = nil
offer(1220, "Shared Start", 1270, "Crossroads Herald")
check("the quest notes that both factions have seen it", QuestGearScoutDB.quests[1220].faction == "B", QuestGearScoutDB.quests[1220].faction)
check("so the chain becomes neutral and the Alliance character sees it",
    QGS.starters["C:1270"] ~= nil and QGS.starters["C:1270"].faction == nil, QGS.starters["C:1270"] and tostring(QGS.starters["C:1270"].faction))
function UnitFactionGroup() return "Horde" end
QGS.Rebuild()
check("and the Horde character still sees it", QGS.starters["C:1270"] ~= nil and QGS.starters["C:1270"].faction == nil)
-- The same the other way round: an Alliance chain becomes neutral once a Horde character sees its reward quest.
function UnitFactionGroup() return "Alliance" end
QuestGearScoutDB.quests[1225] = { title = "Alliance Start", giver = GIVER(1275, "Wayfarer"), faction = "A" }
QuestGearScoutDB.quests[1226] = { title = "Alliance Reward", prev = 1225, quality = 3, faction = "A" }
QGS.Rebuild()
check("a chain learned by an Alliance character is listed as Alliance", QGS.starters["C:1275"] ~= nil and QGS.starters["C:1275"].faction == "A")
function UnitFactionGroup() return "Horde" end
QGS.Rebuild()
check("a Horde character isn't shown it yet", QGS.starters["C:1275"] == nil)
offer(1226, "Alliance Reward", 1275, "Wayfarer")
check("once a Horde character sees it, it's neutral", QuestGearScoutDB.quests[1226].faction == "B" and QGS.starters["C:1275"] ~= nil and QGS.starters["C:1275"].faction == nil)
-- A Horde chain whose quest turns up in an Alliance character's quest log (taken some other way).
function UnitFactionGroup() return "Horde" end
QuestGearScoutDB.quests[1227] = { title = "Horde Errand", giver = GIVER(1277, "Runner"), quality = 3, faction = "H" }
QGS.Rebuild()
function UnitFactionGroup() return "Alliance" end
QGS.Rebuild()
check("(an Alliance character isn't shown the Horde errand)", QGS.starters["C:1277"] == nil)
logList = {}
logAdd(1227, "Horde Errand", "0/1")
fire("QUEST_LOG_UPDATE")
check("a quest in an Alliance character's log makes the chain neutral right away",
    QuestGearScoutDB.quests[1227].faction == "B" and QGS.starters["C:1277"] ~= nil and QGS.starters["C:1277"].faction == nil)
logList = {}
function UnitFactionGroup() return "Horde" end
check("ChainFaction: Alliance start and Horde reward is a wrong link, never shown",
    QGS.ChainFaction({ faction = "A" }, { faction = "H" }) == "X")
check("ChainFaction: a reward recorded before factions were saved leaves the chain for everyone",
    QGS.ChainFaction({ faction = "A" }, {}) == nil and QGS.ChainFaction({}, { faction = "H" }) == "H")
QuestGearScoutDB.quests[1240] = { title = "Alliance Start", giver = GIVER(1290, "Mislinked"), faction = "A" }
QuestGearScoutDB.quests[1241] = { title = "Horde Reward", prev = 1240, quality = 3, faction = "H" }
QGS.Rebuild()
check("a chain no single faction can do isn't shown", QGS.starters["C:1290"] == nil)
-- One giver: a Horde gear chain and a dungeon chain both factions did.
QuestGearScoutDB.settings.dungeons = true
QuestGearScoutDB.quests[1230] = { title = "Horde Gear", giver = GIVER(1280, "Two Paths"), quality = 3, faction = "H" }
QuestGearScoutDB.quests[1231] = { title = "Into the Depths", giver = GIVER(1280, "Two Paths"), dungeon = true, dungeonName = "Ragefire Chasm", faction = "B" }
QGS.Rebuild()
local tp = QGS.starters["C:1280"]
check("a giver with a Horde gear chain and a neutral dungeon chain keeps each one's faction",
    tp ~= nil and tp.gearFaction == "H" and tp.dunFaction == false and tp.faction == nil,
    tp and (tostring(tp.gearFaction) .. "/" .. tostring(tp.dunFaction) .. "/" .. tostring(tp.faction)))
local parts = QGS.SplitByFaction(tp)
check("export shares them as two lines, each with its own faction",
    #parts == 2 and parts[1].faction == "H" and parts[1].quality and not parts[1].dungeonSteps and parts[2].faction == nil and parts[2].dungeonSteps and not parts[2].quality)
local back = ns.Decode(ns.Encode(parts))
local hordeGear, neutralDun = 0, 0
for _, s in pairs(back) do
    if s.faction == "H" and s.quality then hordeGear = hordeGear + 1 end
    if s.faction == nil and s.dungeonSteps then neutralDun = neutralDun + 1 end
end
check("...and both come back on import", hordeGear == 1 and neutralDun == 1, hordeGear .. "/" .. neutralDun)
check("a giver whose chains share a faction exports as one line", #QGS.SplitByFaction(QGS.starters["C:1250"]) == 1)
function UnitFactionGroup() return "Alliance" end
QGS.Rebuild()
check("Alliance sees only the neutral dungeon chain of that giver",
    QGS.starters["C:1280"] ~= nil and QGS.starters["C:1280"].dungeonSteps ~= nil and QGS.starters["C:1280"].quality == nil)

io.write(string.format("\n%d passed, %d failed\n", passed, failed))
os.exit(failed == 0 and 0 or 1)
