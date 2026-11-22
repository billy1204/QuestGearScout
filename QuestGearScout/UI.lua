-- QuestGearScout window: Nearby / My chains / Stats / Settings tabs,
-- a chain-started banner and a minimap button.
-- Nothing here refreshes during combat.
local _, ns = ...
local QGS = ns.QGS

local W, H = 380, 430
local ROW_H = 40
local QUALITY_NAMES = { [2] = "Uncommon", [3] = "Rare", [4] = "Epic", [5] = "Legendary" }
local ICON_NPC = "Interface\\GossipFrame\\AvailableQuestIcon"
local ICON_OBJ = "Interface\\Icons\\INV_Box_01"
local BAR_TEX  = "Interface\\TargetingFrame\\UI-StatusBar"

local frame, child, bodyText, footLeft, footRight, actionBar, settingsPanel
local tabs, rows = {}, {}
local currentTab = "nearby"

local function plural(n, word) return string.format("%d %s%s", n, word, n == 1 and "" or "s") end

local function qualityHex(q)
    local c = ITEM_QUALITY_COLORS and ITEM_QUALITY_COLORS[q]
    return c and c.hex or "|cff1eff00"
end

local ICON_DUN = "Interface\\Icons\\INV_Misc_Bone_HumanSkull_01"

local function rewardText(s)
    return s.item or (qualityHex(s.quality or 2) .. (QUALITY_NAMES[s.quality] or "gear") .. " reward|r")
end

-- "3 steps -> [Gloves] + dungeon quest at step 2 (The Deadmines)"
local function starterText(s)
    local parts = {}
    if s.quality then parts[#parts + 1] = plural(s.steps or 1, "step") .. " -> " .. rewardText(s) end
    if s.dungeonSteps then
        local where = QGS.DungeonWhere(s.dungeonName)
        parts[#parts + 1] = "|cff66ccff" .. (s.dungeonSteps <= 1 and "Dungeon quest"
            or ("Dungeon quest at step " .. s.dungeonSteps)) .. "|r" .. where
    end
    return table.concat(parts, " + ")
end

local function makeFrame(name, parent, template)
    local ok, f = pcall(CreateFrame, "Frame", name, parent, template)
    if ok then return f, true end
    f = CreateFrame("Frame", name, parent, BackdropTemplateMixin and "BackdropTemplate" or nil)
    if f.SetBackdrop then
        f:SetBackdrop({ bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background",
            edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border",
            tile = true, tileSize = 32, edgeSize = 32,
            insets = { left = 8, right = 8, top = 8, bottom = 8 } })
    end
    return f, false
end

local function button(parent, text, w, h)
    local b = CreateFrame("Button", nil, parent, "UIPanelButtonTemplate")
    b:SetSize(w or 80, h or 22)
    b:SetText(text)
    return b
end

---------------------------------------------------------------------------
-- Row pool (used by Nearby and My chains)
---------------------------------------------------------------------------
local function getRow(i)
    local r = rows[i]
    if r then return r end
    r = CreateFrame("Frame", nil, child)
    r:SetSize(W - 56, ROW_H)
    r:SetPoint("TOPLEFT", 0, -(i - 1) * ROW_H)

    r.line = r:CreateTexture(nil, "BACKGROUND")
    r.line:SetColorTexture(1, 0.82, 0, 0.12)
    r.line:SetPoint("BOTTOMLEFT"); r.line:SetPoint("BOTTOMRIGHT"); r.line:SetHeight(1)

    r.icon = r:CreateTexture(nil, "ARTWORK")
    r.icon:SetSize(24, 24)
    r.icon:SetPoint("LEFT", 2, 0)

    r.name = r:CreateFontString(nil, "OVERLAY", "GameFontNormal")
    r.name:SetPoint("TOPLEFT", 32, -4)
    r.name:SetPoint("RIGHT", -80, 0)
    r.name:SetJustifyH("LEFT")
    r.name:SetWordWrap(false)

    r.sub = r:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    r.sub:SetPoint("TOPLEFT", r.name, "BOTTOMLEFT", 0, -3)
    r.sub:SetPoint("RIGHT", -80, 0)
    r.sub:SetJustifyH("LEFT")
    r.sub:SetWordWrap(false)

    r.right = r:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    r.right:SetPoint("TOPRIGHT", -4, -5)

    r.btn = button(r, "Waypoint", 72, 20)
    r.btn:SetPoint("RIGHT", -2, 0)

    r.bar = CreateFrame("StatusBar", nil, r)
    r.bar:SetStatusBarTexture(BAR_TEX)
    r.bar:SetHeight(6)
    r.bar:SetPoint("BOTTOMLEFT", 4, 5)
    r.bar:SetPoint("BOTTOMRIGHT", -4, 5)
    r.bar.bg = r.bar:CreateTexture(nil, "BACKGROUND")
    r.bar.bg:SetAllPoints()
    r.bar.bg:SetColorTexture(0, 0, 0, 0.5)

    rows[i] = r
    return r
end

local function hideRows(from)
    for i = from, #rows do rows[i]:Hide() end
end

---------------------------------------------------------------------------
-- Tab renderers
---------------------------------------------------------------------------
local function renderNearby()
    local map = QGS.PlayerPos()
    if not map then
        bodyText:SetText("|cff888888Your position isn't available here (for example inside a dungeon).|r")
        hideRows(1)
        return 0
    end
    local list = {}
    for _, s in pairs(QGS.starters) do
        if s.map == map then
            list[#list + 1] = { s = s, d = QGS.DistanceTo(s), avail = QGS.Available(s) }
        end
    end
    table.sort(list, function(a, b)
        if a.avail ~= b.avail then return a.avail end
        return (a.d or 1e9) < (b.d or 1e9)
    end)

    if #list == 0 then
        bodyText:SetText("|cff888888No gear or dungeon chain starters known in this zone yet.\n\n" ..
            "Do quests here and QuestGearScout will learn them, or import a list from a friend.|r")
    else
        bodyText:SetText("")
    end

    for i, e in ipairs(list) do
        local r, s = getRow(i), e.s
        r.icon:SetTexture(s.kind == "O" and ICON_OBJ or (not s.quality and ICON_DUN) or ICON_NPC)
        r.icon:SetDesaturated(not e.avail)
        r.name:SetText(e.avail and (s.name or "?") or ("|cff888888" .. (s.name or "?") .. "|r"))
        local where = e.avail and (e.d and string.format("%d yd", e.d) or "") or "Done on this character"
        r.sub:SetText(string.format("%s%s%s", where, where ~= "" and " - " or "", starterText(s)))
        r.right:SetText("")
        r.bar:Hide()
        r.btn:SetShown(e.avail)
        r.btn:SetScript("OnClick", function() QGS.SetWaypoint(s) end)
        r:Show()
    end
    hideRows(#list + 1)
    footLeft:SetText(plural(#list, "starter") .. " in this zone")
    return #list
end

local function renderChains()
    local list = QGS.ActiveChains()
    bodyText:SetText(#list == 0 and
        "|cff888888You're not on any known quest chain.\n\nAccept a follow-up quest and it will show up here.|r" or "")
    for i, c in ipairs(list) do
        local r = getRow(i)
        r.icon:SetTexture(c.isDungeon and ICON_DUN or ICON_NPC)
        r.icon:SetDesaturated(false)
        r.name:SetText(c.title)
        local sub = c.reward and ("Ends in " .. c.reward)
            or (c.complete and ("Began with " .. c.rootTitle) or "Still learning how long this chain is")
        local dun = QGS.ChainDungeonText(c)
        if dun ~= "" then
            sub = c.reward and (sub .. dun) or dun:gsub("^ %- ", "")
        end
        r.sub:SetText(sub)
        local total = math.max(c.total, c.step)
        r.right:SetText(string.format("%d of %d%s", c.step, total, c.complete and "" or "+"))
        r.bar:SetMinMaxValues(0, total)
        r.bar:SetValue(c.step)
        if c.complete then r.bar:SetStatusBarColor(0.12, 1, 0) else r.bar:SetStatusBarColor(1, 0.82, 0) end
        r.bar:Show()
        r.btn:Hide()
        r:Show()
    end
    hideRows(#list + 1)
    footLeft:SetText(plural(#list, "chain") .. " in progress")
    return #list
end

local function renderStats()
    hideRows(1)
    local st = QGS.stats or {}
    local avail = 0
    for _, s in pairs(QGS.starters) do if QGS.Available(s) then avail = avail + 1 end end
    local lines = {
        "|cffffd100Gear chains known|r   " .. (st.gear or 0),
        "    Learned by you   " .. (st.learned or 0),
        "    Imported   " .. (st.imported or 0),
    }
    if (st.bundled or 0) > 0 then lines[#lines + 1] = "    Built-in   " .. st.bundled end
    lines[#lines + 1] = "|cff66ccffDungeon chains known|r   " .. (st.dungeon or 0)
    lines[#lines + 1] = "    Dungeon quests seen   " .. (st.dungeonQuests or 0)
    lines[#lines + 1] = ""
    lines[#lines + 1] = "|cffffd100Still to do on this character|r   " .. avail
    lines[#lines + 1] = "|cffffd100Quest chains recorded|r   " .. (st.chains or 0)
    lines[#lines + 1] = "|cffffd100Longest chain|r   " .. plural(st.longest or 0, "step")
    lines[#lines + 1] = "|cffffd100Quests seen|r   " .. QGS.CountQuests()
    bodyText:SetText(table.concat(lines, "\n"))
    footLeft:SetText("")
    return 0
end

---------------------------------------------------------------------------
-- Settings panel
---------------------------------------------------------------------------
local function buildSettings()
    local p = CreateFrame("Frame", nil, frame)
    p:SetPoint("TOPLEFT", 16, -64)
    p:SetPoint("BOTTOMRIGHT", -16, 36)
    local y = 0

    local function check(label, key, after)
        local cb = CreateFrame("CheckButton", nil, p, "UICheckButtonTemplate")
        cb:SetSize(24, 24)
        cb:SetPoint("TOPLEFT", 0, y)
        local t = p:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
        t:SetPoint("LEFT", cb, "RIGHT", 4, 0)
        t:SetText(label)
        cb:SetScript("OnShow", function(self) self:SetChecked(QuestGearScoutDB.settings[key]) end)
        cb:SetScript("OnClick", function(self)
            QuestGearScoutDB.settings[key] = self:GetChecked() and true or false
            if after then after() end
        end)
        y = y - 28
    end

    local function stepper(getText, onMinus, onPlus)
        local minus = button(p, "-", 24, 22)
        minus:SetPoint("TOPLEFT", 2, y)
        local plus = button(p, "+", 24, 22)
        plus:SetPoint("LEFT", minus, "RIGHT", 4, 0)
        local t = p:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
        t:SetPoint("LEFT", plus, "RIGHT", 8, 0)
        local function upd() t:SetText(getText()) end
        minus:SetScript("OnClick", function() onMinus(); upd() end)
        plus:SetScript("OnClick", function() onPlus(); upd() end)
        p:HookScript("OnShow", upd)
        y = y - 30
    end

    check("Alerts near chain starters", "enabled")
    check("Dungeon quests and chains that lead to them", "dungeons", function() QGS.Rebuild() end)
    check("Chain started / next step notices", "chainNotify")
    check("Sound", "sound")
    check("Banner and center-screen text", "raidWarn")
    check("Minimap button", "minimap", function() QGS.UpdateMinimapButton() end)
    check("Open this window when you log in", "openOnLogin")
    check("Ctrl+Q opens and closes this window", "hotkey", function() QGS.UpdateHotkey(true) end)
    y = y - 8

    local st = function() return QuestGearScoutDB.settings end
    stepper(function()
        local q = st().minQuality
        return "Minimum reward: " .. qualityHex(q) .. (QUALITY_NAMES[q] or q) .. "|r"
    end, function() st().minQuality = math.max(2, st().minQuality - 1); QGS.Rebuild() end,
       function() st().minQuality = math.min(5, st().minQuality + 1); QGS.Rebuild() end)

    stepper(function() return "Alert distance: " .. st().radius .. " yards" end,
        function() st().radius = math.max(10, st().radius - 10) end,
        function() st().radius = math.min(300, st().radius + 10) end)

    local test = button(p, "Test alert", 100, 22)
    test:SetPoint("TOPLEFT", 2, y - 6)
    test:SetScript("OnClick", function() SlashCmdList.QUESTGEARSCOUT("test") end)

    p:Hide()
    settingsPanel = p
end

---------------------------------------------------------------------------
-- Main window
---------------------------------------------------------------------------
function QGS.RefreshUI()
    if not frame or not frame:IsShown() or InCombatLockdown() then return end
    for key, t in pairs(tabs) do
        if key == currentTab then t:LockHighlight() else t:UnlockHighlight() end
    end
    local isSettings = currentTab == "settings"
    settingsPanel:SetShown(isSettings)
    frame.scroll:SetShown(not isSettings)
    actionBar:SetShown(currentTab == "chains" or currentTab == "stats")
    footRight:SetText(QGS.StatsText and QGS.StatsText() or "")
    if isSettings then footLeft:SetText(""); return end

    local n = 0
    if currentTab == "nearby" then n = renderNearby()
    elseif currentTab == "chains" then n = renderChains()
    else n = renderStats() end
    child:SetHeight(math.max(n * ROW_H, bodyText:GetStringHeight()) + 10)
end

local function createWindow()
    local f, templated = makeFrame("QuestGearScoutFrame", UIParent, "BasicFrameTemplateWithInset")
    if not templated then
        CreateFrame("Button", nil, f, "UIPanelCloseButton"):SetPoint("TOPRIGHT", -4, -4)
    end
    f:SetSize(W, H)
    f:SetPoint("CENTER", 220, 40)
    f:SetFrameStrata("DIALOG")
    f:SetMovable(true); f:EnableMouse(true); f:RegisterForDrag("LeftButton")
    f:SetScript("OnDragStart", f.StartMoving)
    f:SetScript("OnDragStop", f.StopMovingOrSizing)
    f:SetClampedToScreen(true)
    tinsert(UISpecialFrames, "QuestGearScoutFrame")
    frame = f

    local title = f:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    title:SetPoint("TOP", 0, -6)
    title:SetText("QuestGearScout")

    -- Tabs
    local defs = { { "nearby", "Nearby" }, { "chains", "My chains" }, { "stats", "Stats" }, { "settings", "Settings" } }
    local prev
    for _, d in ipairs(defs) do
        local b = button(f, d[2], 84, 22)
        if prev then b:SetPoint("LEFT", prev, "RIGHT", 2, 0) else b:SetPoint("TOPLEFT", 14, -30) end
        b:SetScript("OnClick", function() currentTab = d[1]; QGS.RefreshUI() end)
        tabs[d[1]] = b
        prev = b
    end

    -- Scrolling body
    local scroll = CreateFrame("ScrollFrame", nil, f, "UIPanelScrollFrameTemplate")
    scroll:SetPoint("TOPLEFT", 16, -60)
    scroll:SetPoint("BOTTOMRIGHT", -34, 64)
    child = CreateFrame("Frame", nil, scroll)
    child:SetSize(W - 56, 10)
    scroll:SetScrollChild(child)
    f.scroll = scroll

    bodyText = child:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    bodyText:SetPoint("TOPLEFT", 4, -6)
    bodyText:SetWidth(W - 64)
    bodyText:SetJustifyH("LEFT")
    bodyText:SetSpacing(4)

    -- Buttons for My chains / Stats
    actionBar = CreateFrame("Frame", nil, f)
    actionBar:SetPoint("BOTTOMLEFT", 14, 34)
    actionBar:SetSize(W - 28, 24)
    actionBar.tell = button(actionBar, "Tell group", 100, 22)
    actionBar.tell:SetPoint("LEFT")
    actionBar.tell:SetScript("OnClick", function()
        if currentTab == "stats" then QGS.TellStats() else QGS.TellChains() end
    end)
    local exp = button(actionBar, "Export", 80, 22)
    exp:SetPoint("LEFT", actionBar.tell, "RIGHT", 4, 0)
    exp:SetScript("OnClick", function() QGS.ShowExport() end)
    local imp = button(actionBar, "Import", 80, 22)
    imp:SetPoint("LEFT", exp, "RIGHT", 4, 0)
    imp:SetScript("OnClick", function() QGS.ShowImport() end)

    -- Footer
    footLeft = f:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
    footLeft:SetPoint("BOTTOMLEFT", 16, 14)
    footRight = f:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
    footRight:SetPoint("BOTTOMRIGHT", -16, 14)

    buildSettings()

    -- Live distances while open
    local acc = 0
    f:SetScript("OnUpdate", function(_, elapsed)
        acc = acc + elapsed
        if acc >= 1 then
            acc = 0
            if (currentTab == "nearby" or currentTab == "chains") and not f.broken then
                if not safe("refreshing the window", QGS.RefreshUI) then f.broken = true end -- report once
            end
        end
    end)
    f:SetScript("OnShow", QGS.RefreshUI)
    f:Hide()
end

-- Runs fn and reports any Lua error in chat (the game hides them by default).
local function safe(what, fn, ...)
    local ok, err = pcall(fn, ...)
    if not ok then
        QGS.Print("|cffff4040error " .. what .. ":|r " .. tostring(err))
        QGS.Print("please send this message to the addon author.")
    end
    return ok
end
QGS.Safe = safe

function QGS.ToggleUI(tab)
    safe("opening the window", function()
        if not frame then createWindow() end
        if tab then currentTab = tab end
        if frame:IsShown() and not tab then frame:Hide() else frame:Show(); QGS.RefreshUI() end
    end)
end

---------------------------------------------------------------------------
-- Chain-started banner
---------------------------------------------------------------------------
local toast
function QGS.ShowToast(title, detail)
    if not toast then
        toast = makeFrame("QuestGearScoutToast", UIParent, nil)
        toast:SetSize(340, 52)
        toast:SetPoint("TOP", 0, -150)
        toast:SetFrameStrata("HIGH")
        if not toast.SetBackdrop then
            local bg = toast:CreateTexture(nil, "BACKGROUND")
            bg:SetAllPoints(); bg:SetColorTexture(0, 0, 0, 0.75)
        end
        toast.icon = toast:CreateTexture(nil, "ARTWORK")
        toast.icon:SetSize(28, 28)
        toast.icon:SetPoint("LEFT", 14, 0)
        toast.icon:SetTexture(ICON_NPC)
        toast.title = toast:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
        toast.title:SetPoint("TOPLEFT", 50, -10)
        toast.title:SetTextColor(0.25, 1, 0.25)
        toast.detail = toast:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
        toast.detail:SetPoint("TOPLEFT", toast.title, "BOTTOMLEFT", 0, -3)
        toast.detail:SetPoint("RIGHT", -12, 0)
        toast.detail:SetJustifyH("LEFT")
        toast.detail:SetWordWrap(false)
        toast:EnableMouse(true)
        toast:SetScript("OnMouseUp", function() toast:Hide(); QGS.ToggleUI("chains") end)
        toast:SetScript("OnUpdate", function(self)
            local left = (self.hideAt or 0) - GetTime()
            if left <= 0 then self:Hide()
            elseif left < 1 then self:SetAlpha(left) end
        end)
    end
    toast.title:SetText(title)
    toast.detail:SetText(detail or "")
    toast:SetAlpha(1)
    toast.hideAt = GetTime() + 6
    toast:Show()
end

---------------------------------------------------------------------------
-- Minimap button
---------------------------------------------------------------------------
local mm
local function placeMinimapButton()
    local angle = math.rad(QuestGearScoutDB.settings.minimapAngle or 200)
    local r = (Minimap:GetWidth() / 2) + 10
    mm:ClearAllPoints()
    mm:SetPoint("CENTER", Minimap, "CENTER", math.cos(angle) * r, math.sin(angle) * r)
end

function QGS.UpdateMinimapButton()
    if not Minimap then return end
    if not mm then
        mm = CreateFrame("Button", "QuestGearScoutMinimapButton", Minimap)
        mm:SetSize(31, 31)
        mm:SetFrameStrata("MEDIUM")
        mm:SetFrameLevel(8)
        mm:RegisterForClicks("LeftButtonUp", "RightButtonUp")
        mm:RegisterForDrag("LeftButton")
        mm:SetHighlightTexture("Interface\\Minimap\\UI-Minimap-ZoomButton-Highlight")

        local bg = mm:CreateTexture(nil, "BACKGROUND")
        bg:SetTexture("Interface\\Minimap\\UI-Minimap-Background")
        bg:SetSize(20, 20); bg:SetPoint("TOPLEFT", 7, -5)
        local icon = mm:CreateTexture(nil, "ARTWORK")
        icon:SetTexture("Interface\\Icons\\INV_Misc_Map_01")
        icon:SetSize(17, 17); icon:SetPoint("TOPLEFT", 7, -6)
        local border = mm:CreateTexture(nil, "OVERLAY")
        border:SetTexture("Interface\\Minimap\\MiniMap-TrackingBorder")
        border:SetSize(53, 53); border:SetPoint("TOPLEFT")

        mm:SetScript("OnClick", function(_, btn)
            if btn == "RightButton" then QGS.ToggleUI("settings") else QGS.ToggleUI() end
        end)
        mm:SetScript("OnDragStart", function(self)
            self:SetScript("OnUpdate", function()
                local mx, my = Minimap:GetCenter()
                local cx, cy = GetCursorPosition()
                local scale = Minimap:GetEffectiveScale()
                QuestGearScoutDB.settings.minimapAngle = math.deg(math.atan2(cy / scale - my, cx / scale - mx))
                placeMinimapButton()
            end)
        end)
        mm:SetScript("OnDragStop", function(self) self:SetScript("OnUpdate", nil) end)
        mm:SetScript("OnEnter", function(self)
            GameTooltip:SetOwner(self, "ANCHOR_LEFT")
            GameTooltip:AddLine("QuestGearScout")
            GameTooltip:AddLine(QGS.StatsText(), 1, 1, 1)
            GameTooltip:AddLine("Left-click: open   Right-click: settings   Drag: move", 0.6, 0.6, 0.6)
            GameTooltip:Show()
        end)
        mm:SetScript("OnLeave", function() GameTooltip:Hide() end)
    end
    placeMinimapButton()
    mm:SetShown(QuestGearScoutDB.settings.minimap)
end

---------------------------------------------------------------------------
-- Ctrl+Q hotkey
-- Uses an override binding: it never changes your saved key bindings, and it
-- steps aside if you've already bound Ctrl+Q to something else. You can also
-- pick any key under Options > Keybindings > AddOns > QuestGearScout.
---------------------------------------------------------------------------
BINDING_HEADER_QUESTGEARSCOUT = "QuestGearScout"
BINDING_NAME_QUESTGEARSCOUT_TOGGLE = "Open or close the window"

local HOTKEY, ACTION = "CTRL-Q", "QUESTGEARSCOUT_TOGGLE"
local bindOwner = CreateFrame("Frame")
local hotkeyPending = false

function QGS.UpdateHotkey(announce)
    if not SetOverrideBinding then return end
    if InCombatLockdown() then hotkeyPending = true; return end -- bindings can't change in combat
    hotkeyPending = false
    ClearOverrideBindings(bindOwner)
    if not QuestGearScoutDB.settings.hotkey then
        if announce then QGS.Print("Ctrl+Q shortcut off.") end
        return
    end
    local existing = GetBindingAction and GetBindingAction(HOTKEY) or ""
    if existing ~= "" and existing ~= ACTION then
        QGS.Print("Ctrl+Q is already used by another action (" .. existing .. "), so it's left alone. " ..
            "Pick a key under Options > Keybindings > AddOns > QuestGearScout.")
        return
    end
    SetOverrideBinding(bindOwner, false, HOTKEY, ACTION)
    if announce then QGS.Print("Ctrl+Q now opens and closes the window.") end
end

local ev = CreateFrame("Frame")
ev:RegisterEvent("PLAYER_LOGIN")
ev:RegisterEvent("PLAYER_REGEN_ENABLED")
ev:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_REGEN_ENABLED" and hotkeyPending then
        safe("setting the Ctrl+Q shortcut", QGS.UpdateHotkey)
    end
    if event == "PLAYER_LOGIN" then
        safe("creating the minimap button", QGS.UpdateMinimapButton)
        safe("setting the Ctrl+Q shortcut", QGS.UpdateHotkey)
        local getMeta = (C_AddOns and C_AddOns.GetAddOnMetadata) or GetAddOnMetadata
        local version = getMeta and getMeta("QuestGearScout", "Version") or "?"
        QGS.Print(string.format("v%s loaded. Press |cffffff00Ctrl+Q|r, type |cffffff00/qgs|r or click the minimap button to open or close the window.", version))
        if QuestGearScoutDB.settings.openOnLogin then
            -- Short delay so the rest of the UI has finished loading.
            C_Timer.After(2, function()
                safe("opening the window", function()
                    if not frame then createWindow() end
                    frame:Show()
                    QGS.RefreshUI()
                end)
            end)
        end
    else
        safe("refreshing the window", QGS.RefreshUI)
    end
end)
