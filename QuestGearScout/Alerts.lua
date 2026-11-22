-- QuestGearScout alerts. There are three ways to spot a starter, all used
-- only out of combat:
--   1. Proximity: your position vs. the starter's saved coordinates (finds
--      NPCs and objects, even ones out of sight).
--   2. Nameplates: a starter NPC's nameplate appears (needs friendly
--      nameplates turned on).
--   3. Tooltip: you mouse over a starter NPC or a starter object by name.
local _, ns = ...
local QGS = ns.QGS

local QUALITY_NAMES = { [2] = "Uncommon", [3] = "Rare", [4] = "Epic", [5] = "Legendary" }
local lastAlert = {}

local function rootOpen(qid)
    return not QGS.IsQuestDone(qid) and not QGS.IsOnQuest(qid)
end

-- A starter is available while any of its chains (gear or dungeon) hasn't
-- been started on this character.
local function available(s)
    if not s.rootQuest and not s.dRoot then return true end
    return (s.rootQuest and rootOpen(s.rootQuest)) or (s.dRoot and rootOpen(s.dRoot)) or false
end

local function qualityColor(q)
    local c = ITEM_QUALITY_COLORS and ITEM_QUALITY_COLORS[q]
    return c and c.hex or "|cff1eff00"
end

-- "a 3-step chain ending in [Gloves] and a dungeon quest (The Deadmines)"
function QGS.DescribeStarter(s)
    local parts = {}
    if s.quality then
        local reward = s.item or (qualityColor(s.quality) .. (QUALITY_NAMES[s.quality] or "gear") .. " reward|r")
        parts[#parts + 1] = string.format("a %d-step chain ending in %s", s.steps or 1, reward)
    end
    if s.dungeonSteps then
        local where = QGS.DungeonWhere(s.dungeonName)
        if s.dungeonSteps <= 1 then
            parts[#parts + 1] = "|cff66ccffa dungeon quest|r" .. where
        else
            parts[#parts + 1] = string.format("|cff66ccffa %d-step chain to a dungeon quest|r%s", s.dungeonSteps, where)
        end
    end
    return table.concat(parts, " and ")
end

function QGS.Alert(s, how, force)
    local db = QuestGearScoutDB
    if not db.settings.enabled and not force then return end
    if InCombatLockdown() then return end
    local key = QGS.StarterKey(s.kind, s.id)
    local now = GetTime()
    if not force and lastAlert[key] and now - lastAlert[key] < db.settings.cooldown then return end
    lastAlert[key] = now

    local where = (s.x and s.y) and string.format(" (%.1f, %.1f)", s.x * 100, s.y * 100) or ""
    local what = s.kind == "O" and "Object" or "NPC"
    local msg = string.format("%s |cffffff00%s|r%s starts %s", what, s.name or "?", where, QGS.DescribeStarter(s))

    QGS.Print(msg .. (how and (" |cff888888[" .. how .. "]|r") or ""))
    if db.settings.raidWarn and RaidNotice_AddMessage and RaidWarningFrame then
        RaidNotice_AddMessage(RaidWarningFrame,
            (s.quality and "Gear quest nearby: " or "Dungeon quest nearby: ") .. (s.name or "?"),
            ChatTypeInfo and ChatTypeInfo["RAID_WARNING"] or { r = 1, g = 0.8, b = 0 })
    end
    if db.settings.sound then
        PlaySound(SOUNDKIT and SOUNDKIT.RAID_WARNING or 8959, "Master")
    end
    QGS.lastAlerted = s
end

-- Chain-started / next-step notification (fires when you accept a quest).
function QGS.NotifyChain(title, detail)
    local db = QuestGearScoutDB
    if not db.settings.chainNotify then return end
    QGS.Print("|cff40ff40" .. title .. "|r " .. (detail or ""))
    if db.settings.raidWarn then
        if QGS.ShowToast then
            QGS.ShowToast(title, detail)          -- banner from UI.lua
        elseif RaidNotice_AddMessage and RaidWarningFrame then
            RaidNotice_AddMessage(RaidWarningFrame, title, { r = 0.25, g = 1, b = 0.25 })
        end
    end
    if db.settings.sound then
        PlaySound(SOUNDKIT and SOUNDKIT.READY_CHECK or 8960, "Master")
    end
end

---------------------------------------------------------------------------
-- 1. Proximity
---------------------------------------------------------------------------
local function worldPos(map, x, y)
    if not (C_Map and C_Map.GetWorldPosFromMapPos and CreateVector2D) then return end
    local ok, cont, pos = pcall(C_Map.GetWorldPosFromMapPos, map, CreateVector2D(x, y))
    if ok and cont and pos then return cont, pos:GetXY() end
end

local function distanceYards(map, px, py, s)
    if s.map == map then
        local c1, x1, y1 = worldPos(map, px, py)
        local c2, x2, y2 = worldPos(s.map, s.x, s.y)
        if c1 and c2 and c1 == c2 then
            return math.sqrt((x1 - x2) ^ 2 + (y1 - y2) ^ 2)
        end
        -- Fallback: rough map-units guess (a zone is ~1000-2000 yards wide).
        return math.sqrt((px - s.x) ^ 2 + (py - s.y) ^ 2) * 1500
    end
end

local function proximityTick()
    local db = QuestGearScoutDB
    if not db or not db.settings.enabled or InCombatLockdown() then return end
    local map, px, py = QGS.PlayerPos()
    if not map then return end
    for _, s in pairs(QGS.starters) do
        if s.map == map and s.x and available(s) then
            local d = distanceYards(map, px, py, s)
            if d and d <= db.settings.radius then QGS.Alert(s, string.format("%d yd", d)) end
        end
    end
end

---------------------------------------------------------------------------
-- 2. Nameplates and 3. tooltips
---------------------------------------------------------------------------
local function checkUnit(unit, how)
    if InCombatLockdown() then return end
    local ok, guid = pcall(UnitGUID, unit)
    if not ok then return end
    local kind, id = QGS.ParseGUID(guid)
    if not kind then return end
    local s = QGS.starters[QGS.StarterKey(kind, id)]
    if s and available(s) then QGS.Alert(s, how) end
    return s
end

-- Adds "Gear chain: 4 steps -> [item]" to a starter's tooltip.
local function addChainLine(tt, s)
    if not s or tt.qgsLine then return end
    tt.qgsLine = true
    local done = not available(s)
    local doneText = done and " |cff888888(done)|r" or ""
    if s.quality then
        tt:AddLine(string.format("Gear chain: %d step%s -> %s%s", s.steps or 1, (s.steps or 1) == 1 and "" or "s",
            s.item or "gear", doneText), 0.25, 1, 0.25)
    end
    if s.dungeonSteps then
        local where = QGS.DungeonWhere(s.dungeonName)
        if s.dungeonSteps <= 1 then
            tt:AddLine("Dungeon quest: " .. (s.dungeonTitle or "?") .. where .. doneText, 0.4, 0.8, 1)
        else
            tt:AddLine(string.format("Dungeon chain: %d steps -> %s%s%s", s.dungeonSteps,
                s.dungeonTitle or "?", where, doneText), 0.4, 0.8, 1)
        end
    end
    tt:Show() -- resize to fit the new lines
end

local function onUnitTooltip(tt)
    if InCombatLockdown() then return end
    local ok, guid = pcall(UnitGUID, "mouseover")
    if not ok then return end
    local kind, id = QGS.ParseGUID(guid)
    if kind then addChainLine(tt, QGS.starters[QGS.StarterKey(kind, id)]) end
end

local function checkObjectTooltip()
    if InCombatLockdown() or not GameTooltip or GameTooltip:GetUnit() then return end
    local line = _G.GameTooltipTextLeft1
    local name = line and line:GetText()
    if not name or (issecretvalue and issecretvalue(name)) then return end
    local map = QGS.PlayerPos()
    for _, s in pairs(QGS.starters) do
        if s.kind == "O" and s.name == name and (not map or s.map == map) then
            addChainLine(GameTooltip, s)
            if available(s) then QGS.Alert(s, "mouseover") end
            return
        end
    end
end

local ev = CreateFrame("Frame")
ev:RegisterEvent("NAME_PLATE_UNIT_ADDED")
ev:RegisterEvent("UPDATE_MOUSEOVER_UNIT")
ev:SetScript("OnEvent", function(_, event, unit)
    if not QuestGearScoutDB or not QuestGearScoutDB.settings.enabled then return end
    if event == "NAME_PLATE_UNIT_ADDED" then
        checkUnit(unit, "nameplate")
    else
        checkUnit("mouseover", "mouseover")
    end
end)

function QGS.OnReady()
    if C_Timer and C_Timer.NewTicker then C_Timer.NewTicker(2, proximityTick) end
    if not GameTooltip then return end
    GameTooltip:HookScript("OnShow", checkObjectTooltip)
    GameTooltip:HookScript("OnTooltipCleared", function(tt) tt.qgsLine = nil end)
    if TooltipDataProcessor and TooltipDataProcessor.AddTooltipPostCall and Enum and Enum.TooltipDataType then
        TooltipDataProcessor.AddTooltipPostCall(Enum.TooltipDataType.Unit, function(tt)
            if tt == GameTooltip then onUnitTooltip(tt) end
        end)
    elseif GameTooltip:HasScript("OnTooltipSetUnit") then
        GameTooltip:HookScript("OnTooltipSetUnit", onUnitTooltip)
    end
end

---------------------------------------------------------------------------
-- Waypoint to the last (or nearest) starter
---------------------------------------------------------------------------
function QGS.SetWaypoint(s)
    if not s or not s.map then QGS.Print("no starter to mark."); return end
    if C_Map and C_Map.SetUserWaypoint and UiMapPoint then
        local ok = pcall(C_Map.SetUserWaypoint, UiMapPoint.CreateFromCoordinates(s.map, s.x, s.y))
        if ok and C_SuperTrack and C_SuperTrack.SetSuperTrackedUserWaypoint then
            C_SuperTrack.SetSuperTrackedUserWaypoint(true)
        end
        if ok then QGS.Print("waypoint set to " .. (s.name or "?")); return end
    end
    QGS.Print(string.format("%s is at %.1f, %.1f (this client has no map pins).",
        s.name or "?", s.x * 100, s.y * 100))
end

function QGS.Nearest()
    local map, px, py = QGS.PlayerPos()
    if not map then return end
    local best, bestD
    for _, s in pairs(QGS.starters) do
        if s.map == map and s.x and available(s) then
            local d = distanceYards(map, px, py, s)
            if d and (not bestD or d < bestD) then best, bestD = s, d end
        end
    end
    return best, bestD
end

QGS.Available = available

-- Yards from the player to a starter (nil if not on the same map / unknown).
function QGS.DistanceTo(s)
    local map, px, py = QGS.PlayerPos()
    if not map or not s.x then return end
    return distanceYards(map, px, py, s)
end
