-- QuestGearScout sharing (export/import strings) and slash commands.
local _, ns = ...
local QGS = ns.QGS

---------------------------------------------------------------------------
-- Text format: "QGS1" header, then one tab-separated starter per line.
-- % | tab and newline are %XX-escaped so item links survive copy/paste.
---------------------------------------------------------------------------
-- Dungeon fields were added at the end in 1.6.0, so older exports still
-- import, and older versions simply ignore the extra columns.
local FIELDS = { "kind", "id", "map", "x", "y", "rootQuest", "quality", "steps",
                 "name", "rootTitle", "finalTitle", "item",
                 "dRoot", "dungeonSteps", "dungeonName", "dungeonTitle", "dRootTitle",
                 "faction", "classes" }   -- "A"/"H"; "MAGE,PRIEST" (built-in data only)
local NUMERIC = { id = true, map = true, x = true, y = true, rootQuest = true, quality = true, steps = true,
                  dRoot = true, dungeonSteps = true }

local function esc(v)
    if v == nil then return "" end
    if type(v) == "number" then
        return (v == math.floor(v)) and tostring(v) or string.format("%.4f", v)
    end
    return (tostring(v):gsub("[%%|\t\n\r]", function(c) return string.format("%%%02X", c:byte()) end))
end

local function unesc(s)
    return (s:gsub("%%(%x%x)", function(h) return string.char(tonumber(h, 16)) end))
end

function ns.Encode(starters)
    local lines = { "QGS1" }
    for _, s in pairs(starters) do
        local row = {}
        for i, k in ipairs(FIELDS) do row[i] = esc(s[k]) end
        lines[#lines + 1] = table.concat(row, "\t")
    end
    return table.concat(lines, "\n")
end

function ns.Decode(text)
    local out = {}
    if type(text) ~= "string" then return out, 0 end
    text = text:gsub("\r", "")
    local count = 0
    for line in text:gmatch("[^\n]+") do
        line = strtrim(line)
        if line ~= "" and line ~= "QGS1" then
            local parts, i = {}, 1
            for field in (line .. "\t"):gmatch("([^\t]*)\t") do parts[i] = field; i = i + 1 end
            local s = {}
            for j, k in ipairs(FIELDS) do
                local v = parts[j]
                if v and v ~= "" then
                    v = unesc(v)
                    s[k] = NUMERIC[k] and tonumber(v) or v
                end
            end
            if (s.kind == "C" or s.kind == "O") and s.id and s.map and s.x and s.y
                    and (s.quality or s.dungeonSteps) then
                -- The same giver can have a Horde entry and an Alliance entry: keep both.
                local key = QGS.StarterKey(s.kind, s.id)
                if out[key] and out[key].faction ~= s.faction then key = key .. "|" .. (s.faction or "N") end
                out[key] = s
                count = count + 1
            end
        end
    end
    return out, count
end

-- A giver whose gear chain and dungeon chain are for different factions (one Horde-only, one for
-- everyone, say) is shared as two lines, each with its own faction, so neither chain reaches the
-- wrong faction. Otherwise the starter as it is.
local BASE = { "kind", "id", "map", "x", "y", "name", "classes" }
local GEAR = { "rootQuest", "quality", "steps", "rootTitle", "finalTitle", "item" }
local DUN  = { "dRoot", "dungeonSteps", "dungeonName", "dungeonTitle", "dRootTitle" }
function QGS.SplitByFaction(s)
    local gf, df = s.gearFaction, s.dunFaction
    if not (s.quality and s.dungeonSteps) or gf == nil or df == nil or gf == df then return { s } end
    local function part(fields, f)
        local p = {}
        for _, k in ipairs(BASE) do p[k] = s[k] end
        for _, k in ipairs(fields) do p[k] = s[k] end
        p.faction = f or nil
        return p
    end
    return { part(GEAR, gf), part(DUN, df) }
end

---------------------------------------------------------------------------
-- Copy/paste window
---------------------------------------------------------------------------
local win
local function getWindow()
    if win then return win end
    local ok, f = pcall(CreateFrame, "Frame", "QuestGearScoutShare", UIParent, "BasicFrameTemplateWithInset")
    if not ok then
        f = CreateFrame("Frame", "QuestGearScoutShare", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
        if f.SetBackdrop then
            f:SetBackdrop({ bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background",
                edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border",
                tile = true, tileSize = 32, edgeSize = 32,
                insets = { left = 8, right = 8, top = 8, bottom = 8 } })
        end
        CreateFrame("Button", nil, f, "UIPanelCloseButton"):SetPoint("TOPRIGHT", -4, -4)
    end
    f:SetSize(520, 360)
    f:SetPoint("CENTER")
    f:SetFrameStrata("DIALOG")
    f:SetMovable(true); f:EnableMouse(true); f:RegisterForDrag("LeftButton")
    f:SetScript("OnDragStart", f.StartMoving); f:SetScript("OnDragStop", f.StopMovingOrSizing)
    tinsert(UISpecialFrames, "QuestGearScoutShare")

    f.title = f:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    f.title:SetPoint("TOP", 0, -6)

    local scroll = CreateFrame("ScrollFrame", nil, f, "UIPanelScrollFrameTemplate")
    scroll:SetPoint("TOPLEFT", 14, -32)
    scroll:SetPoint("BOTTOMRIGHT", -34, 44)
    local edit = CreateFrame("EditBox", nil, scroll)
    edit:SetMultiLine(true)
    edit:SetAutoFocus(false)
    edit:SetMaxLetters(0)
    edit:SetFontObject(ChatFontNormal)
    edit:SetWidth(460)
    edit:SetScript("OnEscapePressed", function() f:Hide() end)
    scroll:SetScrollChild(edit)
    f.edit = edit

    local btn = CreateFrame("Button", nil, f, "UIPanelButtonTemplate")
    btn:SetSize(120, 24)
    btn:SetPoint("BOTTOMRIGHT", -14, 12)
    btn:SetText("Import")
    btn:SetScript("OnClick", function()
        local data, n = ns.Decode(edit:GetText())
        for k, s in pairs(data) do QuestGearScoutDB.imported[k] = s end
        QGS.Rebuild()
        QGS.Print(string.format("imported %d starter%s.", n, n == 1 and "" or "s"))
        f:Hide()
    end)
    f.importBtn = btn
    f:Hide()
    win = f
    return f
end

local function showExport()
    local f = getWindow()
    f.title:SetText("QuestGearScout - Export (Ctrl+A, Ctrl+C)")
    f.importBtn:Hide()
    -- Everyone already has the built-in chains, so share only what you've added.
    local mine = {}
    for key, s in pairs(QGS.starters) do
        if (QGS.sources and QGS.sources[key]) ~= "bundled" then
            for i, part in ipairs(QGS.SplitByFaction(s)) do mine[key .. "#" .. i] = part end
        end
    end
    f.edit:SetText(ns.Encode(mine))
    f:Show()
    f.edit:SetFocus()
    f.edit:HighlightText()
end

local function showImport()
    local f = getWindow()
    f.title:SetText("QuestGearScout - Paste an export, then Import")
    f.importBtn:Show()
    f.edit:SetText("")
    f:Show()
    f.edit:SetFocus()
end

---------------------------------------------------------------------------
-- Slash commands
---------------------------------------------------------------------------
local function count(t) local n = 0 for _ in pairs(t) do n = n + 1 end return n end

local function list()
    local map = QGS.PlayerPos()
    local shown = 0
    for _, s in pairs(QGS.starters) do
        if not map or s.map == map then
            shown = shown + 1
            local state = QGS.Available(s) and "|cff40ff40available|r" or "|cff888888done/in log|r"
            print(string.format("  %s %s (%.1f, %.1f) - %s  %s",
                s.kind == "O" and "[Obj]" or "[NPC]", s.name or "?", (s.x or 0) * 100, (s.y or 0) * 100,
                QGS.DescribeStarter(s), state))
        end
    end
    QGS.Print(string.format("%d starter%s in this zone. %s.",
        shown, shown == 1 and "" or "s", QGS.StatsText()))
end

local function stats(post)
    local st = QGS.stats or {}
    local avail = 0
    for _, s in pairs(QGS.starters) do if QGS.Available(s) then avail = avail + 1 end end
    if post then
        local channel = (IsInRaid and IsInRaid()) and "RAID" or (IsInGroup and IsInGroup()) and "PARTY" or nil
        if not channel then QGS.Print("join a group to share your stats."); return end
        SendChatMessage(string.format("[QuestGearScout] %s, %d quest chain%s recorded.",
            QGS.StatsText(), st.chains or 0, (st.chains or 0) == 1 and "" or "s"), channel)
        return
    end
    QGS.Print("stats:")
    print(string.format("  Gear chains known: |cff40ff40%d|r", st.gear or 0))
    print(string.format("    learned by you: %d", st.learned or 0))
    print(string.format("    imported: %d", st.imported or 0))
    if (st.bundled or 0) > 0 then print(string.format("    built-in: %d", st.bundled)) end
    print(string.format("  Dungeon chains known: |cff66ccff%d|r", st.dungeon or 0))
    print(string.format("  Dungeon quests seen: %d", st.dungeonQuests or 0))
    print(string.format("  Still to do on this character: %d", avail))
    print(string.format("  Quest chains recorded (any reward): %d", st.chains or 0))
    print(string.format("  Longest chain: %d step%s", st.longest or 0, (st.longest or 0) == 1 and "" or "s"))
    print(string.format("  Quests seen: %d", count(QuestGearScoutDB.quests)))
end

-- Chains you are on right now, with "step X of N".
local function activeChains()
    local out = {}
    for qid, q in pairs(QuestGearScoutDB.quests) do
        if QGS.IsOnQuest(qid) then
            local rootID, root, step = QGS.ChainPos(qid)
            local total, complete = QGS.ChainTotal(rootID)
            local gear = QGS.StarterForQuest(rootID)
            local wantDun = QuestGearScoutDB.settings.dungeons
            local dun = wantDun and QGS.DungeonForRoot(rootID)
            local isDungeon = wantDun and q.dungeon
            if step > 1 or gear or total > 1 or dun or isDungeon then
                out[#out + 1] = {
                    title = q.title or "?", rootTitle = root.title or "?",
                    step = step, total = total, complete = complete,
                    reward = gear and (gear.item or "gear") or nil,
                    isDungeon = isDungeon or nil, dungeonName = isDungeon and q.dungeonName or nil,
                    dunStep = dun and dun.dungeonSteps or nil,
                    dunTitle = dun and dun.dungeonTitle or nil,
                    dunName = dun and dun.dungeonName or nil,
                }
            end
        end
    end
    table.sort(out, function(a, b) return a.title < b.title end)
    return out
end

local function chains()
    local list = activeChains()
    if #list == 0 then
        QGS.Print("you're not on any known quest chain right now.")
        return
    end
    QGS.Print("your quest chains:")
    for _, c in ipairs(list) do
        print(string.format("  |cffffff00%s|r - %s%s%s", c.title, QGS.StepText(c.step, c.total, c.complete),
            c.reward and (" -> " .. c.reward) or "", QGS.ChainDungeonText(c)))
    end
    print("  |cff888888'+' means the chain may be longer; nobody has finished it yet.|r")
end

-- Post your chain progress to group chat so others can see it.
local function tell()
    local list = activeChains()
    if #list == 0 then QGS.Print("no quest chains to share."); return end
    local channel = (IsInRaid and IsInRaid()) and "RAID" or (IsInGroup and IsInGroup()) and "PARTY" or nil
    if not channel then QGS.Print("join a group to share your chains."); return end
    for i = 1, math.min(3, #list) do
        local c = list[i]
        SendChatMessage(string.format("[QuestGearScout] %s: %s%s%s", c.title,
            QGS.StepText(c.step, c.total, c.complete), c.reward and (", ends in " .. c.reward) or "",
            QGS.ChainDungeonText(c, true)), channel)
    end
end

-- " - dungeon quest (The Deadmines)" / " - dungeon quest at step 4", or "".
-- plain = no colour codes (for chat messages to other players).
function QGS.ChainDungeonText(c, plain)
    local text
    if c.isDungeon then
        text = "dungeon quest" .. QGS.DungeonWhere(c.dungeonName)
    elseif c.dunStep and c.dunStep > c.step then
        text = string.format("dungeon quest at step %d%s", c.dunStep, QGS.DungeonWhere(c.dunName))
    end
    if not text then return "" end
    return plain and (", " .. text) or (" - |cff66ccff" .. text .. "|r")
end

-- Shared with UI.lua
QGS.ActiveChains = activeChains
QGS.TellChains   = tell
QGS.TellStats    = function() stats(true) end
QGS.ShowExport   = showExport
QGS.ShowImport   = showImport
QGS.CountQuests  = function() return count(QuestGearScoutDB.quests) end

local function help()
    local st = QuestGearScoutDB.settings
    QGS.Print("commands:")
    print("  /qgs - open the QuestGearScout window (/qgs minimap toggles its button)")
    print("  /qgs stats - how many chains you've learned (/qgs stats tell posts it to group)")
    print("  /qgs chains - quest chains you're on, with step X of N")
    print("  /qgs tell - post your chain progress to party/raid chat")
    print("  /qgs chain on|off - chain started/next step notices (now " .. (st.chainNotify and "on" or "off") .. ")")
    print("  /qgs dungeon on|off - dungeon quests and chains leading to them (now " .. (st.dungeons and "on" or "off") .. ")")
    print("  /qgs list - gear and dungeon chain starters in this zone")
    print("  /qgs near - nearest available starter + waypoint")
    print("  /qgs on|off - alerts (now " .. (st.enabled and "on" or "off") .. ")")
    print("  /qgs quality 2|3|4 - minimum reward: Uncommon/Rare/Epic (now " .. st.minQuality .. ")")
    print("  /qgs radius <yards> - proximity distance (now " .. st.radius .. ")")
    print("  /qgs sound on|off, /qgs warn on|off - sound / center-screen text")
    print("  /qgs export | import - share starters with other players")
    print("  /qgs test - show a sample alert")
end

SLASH_QUESTGEARSCOUT1 = "/qgs"
SLASH_QUESTGEARSCOUT2 = "/gearscout"
SlashCmdList.QUESTGEARSCOUT = function(msg)
    local cmd, rest = strtrim(msg or ""):match("^(%S*)%s*(.-)$")
    cmd, rest = (cmd or ""):lower(), (rest or ""):lower()
    local st = QuestGearScoutDB.settings

    if cmd == "" or cmd == "show" then
        if QGS.ToggleUI then QGS.ToggleUI() else list() end
    elseif cmd == "minimap" then
        st.minimap = not st.minimap
        if QGS.UpdateMinimapButton then QGS.UpdateMinimapButton() end
        QGS.Print("minimap button " .. (st.minimap and "shown" or "hidden"))
    elseif cmd == "stats" then
        stats(rest == "tell")
    elseif cmd == "chains" then
        chains()
    elseif cmd == "tell" then
        tell()
    elseif cmd == "chain" then
        st.chainNotify = rest ~= "off"
        QGS.Print("chain notices " .. (st.chainNotify and "on" or "off"))
    elseif cmd == "dungeon" or cmd == "dungeons" then
        st.dungeons = rest ~= "off"
        QGS.Rebuild()
        QGS.Print("dungeon quests and chains " .. (st.dungeons and "on" or "off"))
    elseif cmd == "list" then
        list()
    elseif cmd == "near" then
        local s, d = QGS.Nearest()
        if s then
            QGS.Print(string.format("nearest: %s, %d yards.", s.name or "?", d))
            QGS.SetWaypoint(s)
        else
            QGS.Print("no available starters known in this zone.")
        end
    elseif cmd == "on" or cmd == "off" then
        st.enabled = (cmd == "on")
        QGS.Print("alerts " .. cmd)
    elseif cmd == "quality" and tonumber(rest) then
        st.minQuality = math.max(2, math.min(5, tonumber(rest)))
        QGS.Rebuild()
        QGS.Print("minimum reward quality " .. st.minQuality)
    elseif cmd == "radius" and tonumber(rest) then
        st.radius = math.max(10, tonumber(rest))
        QGS.Print("radius " .. st.radius .. " yards")
    elseif cmd == "sound" then
        st.sound = rest ~= "off"; QGS.Print("sound " .. (st.sound and "on" or "off"))
    elseif cmd == "warn" then
        st.raidWarn = rest ~= "off"; QGS.Print("center text " .. (st.raidWarn and "on" or "off"))
    elseif cmd == "export" then
        showExport()
    elseif cmd == "import" then
        showImport()
    elseif cmd == "test" then
        local map, x, y = QGS.PlayerPos()
        QGS.Alert({ kind = "C", id = 0, name = "Sample Quest Giver", map = map, x = x or 0.5, y = y or 0.5,
                    steps = 3, quality = 2 }, "test", true)
    else
        help()
    end
end
