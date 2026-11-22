-- QuestGearScout core: learns quest chains as you play.
--
-- The game API never says where a chain ends, so we learn it:
--   * QUEST_DETAIL  -> who offered the quest (NPC or object GUID), where, and
--                      its rewards' quality
--   * QUEST_TURNED_IN then a quest accepted soon after -> link the new quest
--                      to the one before it (follow-ups are offered on turn-in)
--   * QUEST_COMPLETE -> the final reward quality
-- Any quest with an Uncommon+ reward is walked back along those links to the
-- chain's first quest, and that quest's giver becomes a "starter" we alert on.
-- Data is account-wide, so what one character learns helps every alt.

local ADDON, ns = ...
local QGS = {}
ns.QGS = QGS
_G.QuestGearScout = QGS

local DEFAULTS = {
    settings = {
        enabled    = true,
        minQuality = 2,     -- 2 Uncommon, 3 Rare, 4 Epic
        radius     = 60,    -- yards for proximity alerts
        sound      = true,
        raidWarn   = true,  -- big center-screen text
        cooldown   = 600,   -- seconds before the same starter alerts again
        chainNotify = true, -- notify when you start / advance a quest chain
        minimap     = true, -- show the minimap button
        openOnLogin = true, -- open the window automatically when you log in
        hotkey      = true, -- Ctrl+Q toggles the window
        dungeons    = true, -- also track dungeon quests and chains leading to them
        minimapAngle = 200, -- degrees around the minimap
    },
    quests   = {},  -- [questID] = { title, giver, prev, quality, item, dungeon, dungeonName }
    imported = {},  -- [starterKey] = starter (from /qgs import)
}

QGS.starters = {}   -- computed: [key] = starter

---------------------------------------------------------------------------
-- Utilities
---------------------------------------------------------------------------
local function copyDefaults(src, dst)
    for k, v in pairs(src) do
        if type(v) == "table" then
            if type(dst[k]) ~= "table" then dst[k] = {} end
            copyDefaults(v, dst[k])
        elseif dst[k] == nil then
            dst[k] = v
        end
    end
end

function QGS.Print(msg)
    DEFAULT_CHAT_FRAME:AddMessage("|cffffb000QuestGearScout|r: " .. tostring(msg))
end

local function isSecret(v)
    return issecretvalue and issecretvalue(v) or false
end

-- "Creature-0-1-2-3-<npcID>-..." / "GameObject-0-1-2-3-<objectID>-..."
function QGS.ParseGUID(guid)
    if type(guid) ~= "string" or isSecret(guid) then return end
    local kind, _, _, _, _, id = strsplit("-", guid)
    id = tonumber(id)
    if not id then return end
    if kind == "Creature" or kind == "Vehicle" then return "C", id end
    if kind == "GameObject" then return "O", id end
end

function QGS.StarterKey(kind, id) return kind .. ":" .. id end

function QGS.PlayerPos()
    if not (C_Map and C_Map.GetBestMapForUnit) then return end
    local map = C_Map.GetBestMapForUnit("player")
    if not map then return end
    local pos = C_Map.GetPlayerMapPosition(map, "player")
    if not pos then return end -- nil inside instances
    local x, y = pos:GetXY()
    return map, x, y
end

function QGS.IsQuestDone(qid)
    if C_QuestLog and C_QuestLog.IsQuestFlaggedCompleted then
        return C_QuestLog.IsQuestFlaggedCompleted(qid)
    end
    return IsQuestFlaggedCompleted and IsQuestFlaggedCompleted(qid) or false
end

function QGS.IsOnQuest(qid)
    if C_QuestLog and C_QuestLog.IsOnQuest then return C_QuestLog.IsOnQuest(qid) end
    if GetQuestLogIndexByID then return (GetQuestLogIndexByID(qid) or 0) > 0 end
    return false
end

-- Best reward quality currently shown in the quest frame (detail or complete).
local function scanRewards()
    local best, bestLink = -1, nil
    local function check(kind, n)
        for i = 1, n do
            local _, _, _, quality = GetQuestItemInfo(kind, i)
            local link = GetQuestItemLink and GetQuestItemLink(kind, i)
            if (not quality or quality < 0) and link then
                quality = select(3, GetItemInfo(link))
            end
            if quality and quality > best then best, bestLink = quality, link end
        end
    end
    check("choice", GetNumQuestChoices and GetNumQuestChoices() or 0)
    check("reward", GetNumQuestRewards and GetNumQuestRewards() or 0)
    return best, bestLink
end

local function currentGiver()
    local kind, id = QGS.ParseGUID(UnitGUID("npc"))
    if not kind then return end
    local map, x, y = QGS.PlayerPos()
    local name = UnitName("npc")
    if isSecret(name) then name = nil end
    return { kind = kind, id = id, name = name, map = map, x = x, y = y }
end

---------------------------------------------------------------------------
-- Dungeon quests
--   * The quest's tag says Dungeon/Raid (when the client exposes tags), or
--   * you make progress on it, accept it or turn it in inside a dungeon.
---------------------------------------------------------------------------
local DUNGEON_TAG_IDS = { [62] = true, [81] = true, [85] = true, [88] = true, [89] = true }
local DUNGEON_TAG_NAMES = { Dungeon = true, Raid = true, Heroic = true }

function QGS.QuestTagIsDungeon(qid)
    local tagID, tagName
    if C_QuestLog and C_QuestLog.GetQuestTagInfo then
        local ok, info = pcall(C_QuestLog.GetQuestTagInfo, qid)
        if ok and type(info) == "table" then tagID, tagName = info.tagID, info.tagName end
    end
    if not tagID and GetQuestTagInfo then
        local ok, a, b = pcall(GetQuestTagInfo, qid)
        if ok then tagID, tagName = a, b end
    end
    if isSecret(tagID) or isSecret(tagName) then return false end
    return (tagID and DUNGEON_TAG_IDS[tagID]) or (tagName and DUNGEON_TAG_NAMES[tagName]) or false
end

-- Name of the dungeon/raid you're in, "" if inside one we can't name, nil outside.
function QGS.CurrentInstance()
    if not IsInInstance then return end
    local ok, inInst, kind = pcall(IsInInstance)
    if not ok or not inInst or isSecret(kind) or (kind ~= "party" and kind ~= "raid") then return end
    local name = GetInstanceInfo and GetInstanceInfo()
    if type(name) ~= "string" or isSecret(name) then name = "" end
    return name
end

---------------------------------------------------------------------------
-- Building the starter list
-- A starter is the giver of a chain's first quest. It can lead to gear
-- (quality, item, steps), to a dungeon quest (dungeonSteps, dungeonName), or both.
---------------------------------------------------------------------------
local GEAR_FIELDS = { "rootQuest", "rootTitle", "finalTitle", "quality", "item", "steps" }
local DUN_FIELDS  = { "dRoot", "dRootTitle", "dungeonTitle", "dungeonName", "dungeonSteps" }

function QGS.Rebuild()
    local db = QuestGearScoutDB
    local minQ = db.settings.minQuality
    local wantDungeons = db.settings.dungeons
    local out, source = {}, {}

    -- Built-in data says which faction / classes a chain is for.
    local myFaction = UnitFactionGroup and UnitFactionGroup("player")
    myFaction = (myFaction == "Alliance" and "A") or (myFaction == "Horde" and "H") or nil
    local myClass = UnitClass and select(2, UnitClass("player"))
    if isSecret(myClass) then myClass = nil end

    local function offer(s, src)
        if not s then return end
        if s.faction and myFaction and s.faction ~= myFaction then return end
        if s.classes and myClass and not ("," .. s.classes .. ","):find("," .. myClass .. ",", 1, true) then return end
        local gearOK = s.quality and s.quality >= minQ
        local dunOK = wantDungeons and s.dungeonSteps
        if not gearOK and not dunOK then return end
        local key = QGS.StarterKey(s.kind, s.id)
        local cur = out[key]
        if not cur then
            cur = { kind = s.kind, id = s.id, name = s.name, map = s.map, x = s.x, y = s.y,
                    faction = s.faction, classes = s.classes }
            out[key] = cur
        end
        local changed = false
        if gearOK and (not cur.quality or s.quality > cur.quality
                or (s.quality == cur.quality and (s.steps or 1) < (cur.steps or 1))) then
            for _, k in ipairs(GEAR_FIELDS) do cur[k] = s[k] end
            changed = true
        end
        if dunOK and (not cur.dungeonSteps or s.dungeonSteps < cur.dungeonSteps) then
            for _, k in ipairs(DUN_FIELDS) do cur[k] = s[k] end
            changed = true
        end
        if changed then
            cur.name = s.name or cur.name
            cur.map, cur.x, cur.y = s.map, s.x, s.y
            source[key] = src
        end
    end

    -- Bundled, shared (other players on your network, via the Companion app) and imported first;
    -- your own learned data overrides them.
    for _, s in pairs(ns.bundled or {}) do offer(s, "bundled") end
    for _, s in pairs(ns.shared or {}) do offer(s, "shared") end
    for _, s in pairs(db.imported) do offer(s, "imported") end

    -- Which faction a learned chain belongs to: the faction of the characters that did it.
    -- "B" (done on both factions) or unknown (data from before 1.9.0) means everyone sees it.
    local function chainFaction(root, q)
        local f = root.faction or q.faction
        if f == "A" or f == "H" then return f end
        return nil
    end

    local lengths, dungeonQuests = {}, 0
    for qid, q in pairs(db.quests) do
        local rootID, root, steps = QGS.ChainPos(qid)
        if steps > (lengths[rootID] or 0) then lengths[rootID] = steps end
        if q.dungeon then dungeonQuests = dungeonQuests + 1 end
        local g = root.giver
        if g and g.map then
            if (q.quality or -1) >= minQ then
                offer({
                    kind = g.kind, id = g.id, name = g.name,
                    map = g.map, x = g.x, y = g.y,
                    rootQuest = rootID, rootTitle = root.title,
                    finalTitle = q.title, quality = q.quality, item = q.item,
                    steps = steps, faction = chainFaction(root, q),
                }, "learned")
            end
            if q.dungeon then
                offer({
                    kind = g.kind, id = g.id, name = g.name,
                    map = g.map, x = g.x, y = g.y,
                    dRoot = rootID, dRootTitle = root.title,
                    dungeonTitle = q.title, dungeonName = q.dungeonName,
                    dungeonSteps = steps, faction = chainFaction(root, q),
                }, "learned")
            end
        end
    end
    QGS.starters = out
    QGS.sources = source
    QGS.chainLen = lengths

    -- Counts for /qgs stats and messages.
    local st = { gear = 0, dungeon = 0, learned = 0, imported = 0, bundled = 0, shared = 0, chains = 0, longest = 0,
                 dungeonQuests = dungeonQuests }
    for key, s in pairs(out) do
        if s.quality then st.gear = st.gear + 1 end
        if s.dungeonSteps then st.dungeon = st.dungeon + 1 end
        st[source[key]] = st[source[key]] + 1
    end
    for _, len in pairs(lengths) do
        if len >= 2 then st.chains = st.chains + 1 end   -- any chain of 2+ quests you've done
        if len > st.longest then st.longest = len end
    end
    QGS.stats = st
end

-- "12 gear chains known (8 learned, 4 imported)"
-- "12 gear chains, 3 dungeon chains known (13 learned, 2 imported)"
function QGS.StatsText()
    local st = QGS.stats or {}
    local parts = { string.format("%d learned", st.learned or 0) }
    if (st.imported or 0) > 0 then parts[#parts + 1] = string.format("%d imported", st.imported) end
    if (st.bundled or 0) > 0 then parts[#parts + 1] = string.format("%d built-in", st.bundled) end
    if (st.shared or 0) > 0 then parts[#parts + 1] = string.format("%d shared", st.shared) end
    local what = string.format("%d gear chain%s", st.gear or 0, (st.gear or 0) == 1 and "" or "s")
    if (st.dungeon or 0) > 0 then
        what = what .. string.format(", %d dungeon chain%s", st.dungeon, st.dungeon == 1 and "" or "s")
    end
    return string.format("%s known (%s)", what, table.concat(parts, ", "))
end

function QGS.StarterForQuest(qid)
    for _, s in pairs(QGS.starters) do
        if s.rootQuest == qid then return s end
    end
end

-- Starter whose chain (starting with quest qid) leads to a dungeon quest.
function QGS.DungeonForRoot(qid)
    for _, s in pairs(QGS.starters) do
        if s.dRoot == qid then return s end
    end
end

-- " (The Deadmines)" or ""
function QGS.DungeonWhere(name)
    return (name and name ~= "") and (" (" .. name .. ")") or ""
end

-- Walks back to the chain's first quest. Returns rootID, rootRecord, step
-- (1 = the first quest itself).
-- Uses what you've learned first, then the built-in chain data.
function QGS.ChainPos(qid)
    local quests, bq = QuestGearScoutDB.quests, ns.BUNDLED_QUESTS or {}
    local function prevOf(id)
        local q, b = quests[id], bq[id]
        local p = (q and q.prev) or (b and b[1]) or nil
        if p and (quests[p] or bq[p]) then return p end
    end
    local rootID, step, guard = qid, 1, 0
    local p = prevOf(qid)
    while p and guard < 50 do
        rootID, step, guard = p, step + 1, guard + 1
        p = prevOf(p)
    end
    local root = quests[rootID]
    if not (root and root.title) then
        local b = bq[rootID]
        root = { title = (root and root.title) or (b and b[2] ~= "" and b[2]) or nil, giver = root and root.giver }
    end
    return rootID, root, step
end

-- Built-in: is this quest a dungeon quest? Returns the dungeon name.
local function bundledDungeon(qid)
    local b = ns.BUNDLED_QUESTS and ns.BUNDLED_QUESTS[qid]
    return b and b[3] or nil
end

-- Total steps for the chain starting at rootID.
-- Returns total, isComplete. isComplete is true when the full chain (up to
-- its gear reward) is known; false means "at least this many, still learning".
function QGS.ChainTotal(rootID)
    local s = QGS.StarterForQuest(rootID)
    if s and s.steps then return s.steps, true end
    return (QGS.chainLen and QGS.chainLen[rootID]) or 1, false
end

-- "step 2 of 5" / "step 2 of 3+" (3+ = still learning how long the chain is)
function QGS.StepText(step, total, complete)
    if step > total then total = step end
    return string.format("step %d of %d%s", step, total, complete and "" or "+")
end

---------------------------------------------------------------------------
-- Learning events
---------------------------------------------------------------------------
local pendingDetail      -- { qid, giver }
local lastTurnIn         -- { qid, time, giverKey }
local LINK_ANY_WINDOW  = 20   -- s: next quest from anyone right after a turn-in
local LINK_SAME_WINDOW = 180  -- s: next quest from the same NPC/object

local function questRecord(qid)
    local q = QuestGearScoutDB.quests[qid]
    if not q then q = {}; QuestGearScoutDB.quests[qid] = q end
    return q
end

-- "A" / "H" for the current character, nil if unknown (e.g. Pandaren before choosing).
function QGS.PlayerFaction()
    local f = UnitFactionGroup and UnitFactionGroup("player")
    if isSecret(f) then return nil end
    return (f == "Alliance" and "A") or (f == "Horde" and "H") or nil
end

-- Remembers which faction took a quest: "A", "H", or "B" once both factions have taken it.
local function noteFaction(q)
    local f = QGS.PlayerFaction()
    if not f then return end
    if not q.faction then q.faction = f
    elseif q.faction ~= f and q.faction ~= "B" then q.faction = "B" end
end

-- Per-character info for the Companion app's stats (faction, class, level).
local function noteCharacter()
    local db = QuestGearScoutDB
    local name = UnitName and UnitName("player")
    local realm = GetRealmName and GetRealmName()
    if not name or isSecret(name) or isSecret(realm) then return end
    local _, class = UnitClass("player")
    db.chars = db.chars or {}
    db.chars[name .. "-" .. (realm or "?")] = {
        faction = QGS.PlayerFaction(), class = (not isSecret(class)) and class or nil,
        level = UnitLevel and UnitLevel("player") or nil, seen = time and time() or nil,
    }
end

-- Flags a quest as a dungeon quest. Returns true the first time.
local function setDungeon(q, name)
    local new = not q.dungeon
    q.dungeon = true
    if name and name ~= "" and not q.dungeonName then q.dungeonName = name end
    return new
end

-- Learned from progress / turn-in inside a dungeon: record it and say so.
function QGS.MarkDungeon(qid, name)
    local q = questRecord(qid)
    if not setDungeon(q, name) then return false end
    QGS.Rebuild()
    if not QuestGearScoutDB.settings.dungeons then return true end
    local _, root, step = QGS.ChainPos(qid)
    if step > 1 then
        QGS.Print(string.format("|cff66ccffRecorded a dungeon quest:|r %s%s - step %d of the chain that starts with %s.",
            q.title or "?", QGS.DungeonWhere(q.dungeonName), step, root.title or "?"))
    else
        QGS.Print(string.format("|cff66ccffRecorded a dungeon quest:|r %s%s.",
            q.title or "?", QGS.DungeonWhere(q.dungeonName)))
    end
    return true
end

-- Prints what the quest window's quest means for dungeons.
local function dungeonDetailLine(qid, q)
    if not QuestGearScoutDB.settings.dungeons then return end
    local d = QGS.DungeonForRoot(qid)
    if q.dungeon then
        QGS.Print(string.format("|cff66ccffThis is a dungeon quest%s|r", QGS.DungeonWhere(q.dungeonName)))
    elseif d and d.dungeonSteps > 1 then
        QGS.Print(string.format("|cff66ccffThis quest starts a %d-step chain to a dungeon quest: %s%s|r",
            d.dungeonSteps, d.dungeonTitle or "?", QGS.DungeonWhere(d.dungeonName)))
    elseif q.prev then
        local rootID, _, step = QGS.ChainPos(qid)
        d = QGS.DungeonForRoot(rootID)
        if d and d.dungeonSteps > step then
            QGS.Print(string.format("|cff66ccffThis chain leads to a dungeon quest at step %d: %s%s|r",
                d.dungeonSteps, d.dungeonTitle or "?", QGS.DungeonWhere(d.dungeonName)))
        end
    end
end

local function onDetail()
    local qid = GetQuestID and GetQuestID()
    if not qid or qid == 0 then return end
    local q = questRecord(qid)
    q.title = GetTitleText and GetTitleText() or q.title
    noteFaction(q)
    local giver = currentGiver()
    if giver and not q.giver then q.giver = giver end
    local quality, link = scanRewards()
    if quality > (q.quality or -1) then q.quality, q.item = quality, link end
    if QGS.QuestTagIsDungeon(qid) then setDungeon(q) end
    if bundledDungeon(qid) then setDungeon(q, bundledDungeon(qid)) end
    local inst = QGS.CurrentInstance()
    if inst then setDungeon(q, inst) end
    pendingDetail = { qid = qid, giver = giver }

    QGS.Rebuild()
    dungeonDetailLine(qid, q)
    local s = QGS.StarterForQuest(qid)
    if s and s.steps and s.steps > 1 then
        QGS.Print(string.format("|cff40ff40This quest starts a %d-step chain ending in %s|r",
            s.steps, s.item or ("a quality " .. s.quality .. " reward")))
    elseif q.prev then
        local rootID, root, step = QGS.ChainPos(qid)
        local total, complete = QGS.ChainTotal(rootID)
        QGS.Print(string.format("|cff40ff40This quest is %s in the chain that starts with %s|r",
            QGS.StepText(step, total, complete), root.title or "?"))
    end
end

local function onAccepted(arg1, arg2)
    local qid = arg2 or arg1           -- Classic: (logIndex, questID); modern: (questID)
    if type(qid) ~= "number" then return end
    local q = questRecord(qid)
    if not q.title and C_QuestLog and C_QuestLog.GetTitleForQuestID then
        q.title = C_QuestLog.GetTitleForQuestID(qid)
    end
    local giver = pendingDetail and pendingDetail.qid == qid and pendingDetail.giver
    if giver and not q.giver then q.giver = giver end
    noteFaction(q)
    if QGS.QuestTagIsDungeon(qid) then setDungeon(q) end
    if bundledDungeon(qid) then setDungeon(q, bundledDungeon(qid)) end
    local inst = QGS.CurrentInstance()
    if inst then setDungeon(q, inst) end

    local newLink = false
    if lastTurnIn and lastTurnIn.qid ~= qid and not q.prev then
        local age = GetTime() - lastTurnIn.time
        local sameGiver = giver and lastTurnIn.giverKey == QGS.StarterKey(giver.kind, giver.id)
        if age <= LINK_ANY_WINDOW or (sameGiver and age <= LINK_SAME_WINDOW) then
            q.prev = lastTurnIn.qid
            newLink = true
        end
    end
    pendingDetail = nil
    QGS.Rebuild()

    -- Notify: a chain has started (or moved on to its next step).
    local rootID, root, step = QGS.ChainPos(qid)
    local total, complete = QGS.ChainTotal(rootID)
    local gear = QGS.StarterForQuest(rootID)
    local reward = gear and (gear.item or ("a quality " .. (gear.quality or 2) .. " reward"))
    local wantDun = QuestGearScoutDB.settings.dungeons
    local dun = wantDun and QGS.DungeonForRoot(rootID)

    local title, detail
    if step == 1 and gear then
        -- First quest of a chain we already know ends in gear.
        title = "Gear quest chain started!"
        detail = string.format("%s - %d steps, ending in %s", q.title or "?", total, reward)
        if dun and dun.dungeonSteps > 1 then
            detail = detail .. string.format(" - dungeon quest at step %d", dun.dungeonSteps)
        end
    elseif step == 1 and dun and dun.dungeonSteps > 1 then
        -- First quest of a chain we know leads to a dungeon quest.
        title = "Dungeon quest chain started!"
        detail = string.format("%s - %d steps to a dungeon quest: %s%s", q.title or "?",
            dun.dungeonSteps, dun.dungeonTitle or "?", QGS.DungeonWhere(dun.dungeonName))
    elseif step > 1 and (newLink or q.prev) then
        title = (step == 2 and newLink) and "Quest chain started!"
                or string.format("Quest chain: %s", QGS.StepText(step, total, complete))
        detail = string.format("%s (%s, began with %s)", q.title or "?",
            QGS.StepText(step, total, complete), root.title or "?")
        if reward then detail = detail .. " - ends in " .. reward end
        if dun and dun.dungeonSteps > step then
            detail = detail .. string.format(" - dungeon quest at step %d", dun.dungeonSteps)
        end
    end
    if wantDun and q.dungeon then
        title = "Dungeon quest accepted!"
        detail = (detail or (q.title or "?")) .. QGS.DungeonWhere(q.dungeonName)
    end
    if title then QGS.NotifyChain(title, detail) end
end

local turnInGiverKey
local function onComplete()
    local qid = GetQuestID and GetQuestID()
    if not qid or qid == 0 then return end
    local q = questRecord(qid)
    q.title = q.title or (GetTitleText and GetTitleText())
    local quality, link = scanRewards()
    if quality > (q.quality or -1) then q.quality, q.item = quality, link end
    local giver = currentGiver()
    turnInGiverKey = giver and QGS.StarterKey(giver.kind, giver.id) or nil
end

local function onTurnedIn(qid)
    lastTurnIn = { qid = qid, time = GetTime(), giverKey = turnInGiverKey }
    turnInGiverKey = nil
    local inst = QGS.CurrentInstance()
    if inst then QGS.MarkDungeon(qid, inst) end
    local q = QuestGearScoutDB.quests[qid]
    if q and (q.quality or -1) >= QuestGearScoutDB.settings.minQuality then
        QGS.Rebuild()
        local s = QGS.StarterForQuest((QGS.ChainPos(qid)))
        if s then
            QGS.Print(string.format("Recorded a gear chain: %d step%s from %s to %s. |cff40ff40You've learned %d gear chain%s.|r",
                s.steps, s.steps == 1 and "" or "s", s.name or "?", s.item or s.finalTitle or "?",
                QGS.stats.learned, QGS.stats.learned == 1 and "" or "s"))
        end
    end
end

---------------------------------------------------------------------------
-- Objective progress inside a dungeon -> that quest is a dungeon quest.
-- We keep a snapshot of every quest's objective text; when one changes while
-- you're inside a dungeon or raid, the quest gets flagged.
---------------------------------------------------------------------------
local progress = {}

local function eachLogQuest(fn)
    if C_QuestLog and C_QuestLog.GetNumQuestLogEntries and C_QuestLog.GetInfo then
        for i = 1, C_QuestLog.GetNumQuestLogEntries() or 0 do
            local info = C_QuestLog.GetInfo(i)
            if info and not info.isHeader and info.questID and info.questID > 0 then fn(info.questID, i) end
        end
    elseif GetNumQuestLogEntries and GetQuestLogTitle then
        for i = 1, (GetNumQuestLogEntries()) or 0 do
            local _, _, _, isHeader, _, isComplete, _, qid = GetQuestLogTitle(i)
            if not isHeader and qid and qid > 0 then fn(qid, i, isComplete) end
        end
    end
end

local function progressOf(qid, index, isComplete)
    local parts = { tostring(isComplete or "") }
    if C_QuestLog and C_QuestLog.GetQuestObjectives then
        local objs = C_QuestLog.GetQuestObjectives(qid)
        if type(objs) == "table" then
            for _, o in ipairs(objs) do
                parts[#parts + 1] = tostring(o.numFulfilled or o.text or "") .. (o.finished and "y" or "n")
            end
            return table.concat(parts, ";")
        end
    end
    if GetNumQuestLeaderBoards and GetQuestLogLeaderBoard then
        for j = 1, GetNumQuestLeaderBoards(index) or 0 do
            local text, _, finished = GetQuestLogLeaderBoard(j, index)
            parts[#parts + 1] = tostring(text or "") .. (finished and "y" or "n")
        end
    end
    return table.concat(parts, ";")
end

local function scanProgress()
    local inst = QGS.CurrentInstance()
    local now = {}
    eachLogQuest(function(qid, index, isComplete)
        local p = progressOf(qid, index, isComplete)
        if inst and progress[qid] and progress[qid] ~= p then QGS.MarkDungeon(qid, inst) end
        now[qid] = p
    end)
    progress = now
end
QGS.ScanProgress = scanProgress

local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:RegisterEvent("QUEST_DETAIL")
f:RegisterEvent("QUEST_ACCEPTED")
f:RegisterEvent("QUEST_COMPLETE")
f:RegisterEvent("QUEST_TURNED_IN")
f:RegisterEvent("QUEST_LOG_UPDATE")
f:RegisterEvent("PLAYER_ENTERING_WORLD")
f:RegisterEvent("PLAYER_LOGIN")
f:SetScript("OnEvent", function(_, event, a1, a2)
    if event == "PLAYER_LOGIN" then
        -- Faction and class are certain now; filter the built-in and shared chains by them.
        if QuestGearScoutDB then
            pcall(noteCharacter)
            QGS.Rebuild()
        end
        return
    end
    if event == "QUEST_LOG_UPDATE" or event == "PLAYER_ENTERING_WORLD" then
        if QuestGearScoutDB and QuestGearScoutDB.quests then
            local ok, err = pcall(scanProgress)
            if not ok and not QGS.scanErrorShown then
                QGS.scanErrorShown = true
                QGS.Print("dungeon quest check hit an error: " .. tostring(err))
            end
        end
        return
    end
    if event == "ADDON_LOADED" then
        if a1 ~= ADDON then return end
        QuestGearScoutDB = QuestGearScoutDB or {}
        copyDefaults(DEFAULTS, QuestGearScoutDB)
        -- Built-in chains plus discoveries added with the Companion app.
        -- A list, so a giver can appear in both and offer() merges them.
        ns.bundled, ns.shared = {}, {}
        if ns.Decode then
            for _, text in ipairs({ ns.BUNDLED_EXPORT or "", ns.DISCOVERED_EXPORT or "" }) do
                for _, s in pairs((ns.Decode(text))) do ns.bundled[#ns.bundled + 1] = s end
            end
            -- Chains other players on your network found (written by the Companion app).
            for _, s in pairs((ns.Decode(ns.SHARED_EXPORT or ""))) do ns.shared[#ns.shared + 1] = s end
        end
        QGS.Rebuild()
        if QGS.OnReady then QGS.OnReady() end
        -- One-line summary a few seconds after login.
        C_Timer.After(4, function()
            local st = QGS.stats
            if st and (st.gear > 0 or st.chains > 0) then
                QGS.Print(QGS.StatsText() .. string.format(", %d quest chain%s recorded. /qgs stats for more.",
                    st.chains, st.chains == 1 and "" or "s"))
            end
        end)
    elseif event == "QUEST_DETAIL" then
        onDetail()
    elseif event == "QUEST_ACCEPTED" then
        onAccepted(a1, a2)
    elseif event == "QUEST_COMPLETE" then
        onComplete()
    elseif event == "QUEST_TURNED_IN" then
        onTurnedIn(a1)
    end
end)
