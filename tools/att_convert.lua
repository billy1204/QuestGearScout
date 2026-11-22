-- Builds QuestGearScout/Data.lua from AllTheThings' WoW Forever source data
-- (MIT licence, https://github.com/ATTWoWAddon/AllTheThings).
--
--   lua5.1 tools/att_convert.lua <att_data folder> <output Data.lua>
--
-- The data folder needs: zones/**, "dungeons & raids"/*, ItemDB.lua and
-- const_maps.lua. The ATT files are read as text and parsed here; none of
-- their code is ever run.

local DATA, OUT = arg[1], arg[2]
assert(DATA and OUT, "usage: att_convert.lua <att_data folder> <output Data.lua>")

---------------------------------------------------------------------------
-- File helpers
---------------------------------------------------------------------------
local function readFile(path)
    local f = assert(io.open(path, "rb"), "can't open " .. path)
    local s = f:read("*a"); f:close()
    return s
end

local function listLua(dir)
    local out = {}
    local p = io.popen('dir /b /s /a-d "' .. dir .. '\\*.lua" 2>nul')
    for line in p:lines() do out[#out + 1] = line end
    p:close()
    table.sort(out)
    return out
end

---------------------------------------------------------------------------
-- Tokenizer (Lua syntax). Line comments are kept per line, because ATT
-- writes names there: q(166, {	-- The Defias Brotherhood (7/7)
---------------------------------------------------------------------------
local function tokenize(src)
    local toks, comments = {}, {}
    local i, n, line = 1, #src, 1
    local function push(t, v) toks[#toks + 1] = { t = t, v = v, line = line } end
    local function longBracket(at)       -- returns level or nil for [[ / [==[
        local eq = src:match("^%[(=*)%[", at)
        return eq and #eq or nil
    end
    while i <= n do
        local c = src:sub(i, i)
        if c == "\n" then line = line + 1; i = i + 1
        elseif c:match("%s") then i = i + 1
        elseif src:sub(i, i + 1) == "--" then
            local lvl = longBracket(i + 2)
            if lvl then
                local close = "]" .. string.rep("=", lvl) .. "]"
                local e = src:find(close, i, true) or n
                for _ in src:sub(i, e):gmatch("\n") do line = line + 1 end
                i = e + #close
            else
                local e = src:find("\n", i, true) or (n + 1)
                if not comments[line] then comments[line] = src:sub(i + 2, e - 1) end
                i = e
            end
        elseif c == '"' or c == "'" then
            local j, buf = i + 1, {}
            while j <= n do
                local d = src:sub(j, j)
                if d == "\\" then buf[#buf + 1] = src:sub(j + 1, j + 1); j = j + 2
                elseif d == c then break
                else if d == "\n" then line = line + 1 end; buf[#buf + 1] = d; j = j + 1 end
            end
            push("str", table.concat(buf)); i = j + 1
        elseif c == "[" and longBracket(i) then
            local lvl = longBracket(i)
            local close = "]" .. string.rep("=", lvl) .. "]"
            local s = i + lvl + 2
            local e = src:find(close, s, true) or n
            local body = src:sub(s, e - 1)
            push("str", body)
            for _ in body:gmatch("\n") do line = line + 1 end
            i = e + #close
        elseif c:match("%d") or (c == "." and src:sub(i + 1, i + 1):match("%d")) then
            local num = src:match("^0[xX]%x+", i) or src:match("^%d*%.?%d+[eE][%+%-]?%d+", i)
                or src:match("^%d+%.?%d*", i) or src:match("^%.%d+", i)
            push("num", tonumber(num)); i = i + #num
        elseif c:match("[%a_]") then
            local w = src:match("^[%w_]+", i)
            push("name", w); i = i + #w
        else
            local three, two = src:sub(i, i + 2), src:sub(i, i + 1)
            if three == "..." then push("op", three); i = i + 3
            elseif two == ".." or two == "==" or two == "~=" or two == "<=" or two == ">=" then push("op", two); i = i + 2
            else push("op", c); i = i + 1 end
        end
    end
    return toks, comments
end

---------------------------------------------------------------------------
-- Expression parser: enough of Lua to read ATT data tables.
-- Nodes: num, str, name (dotted), call {name,args}, table {arr,hash}, other.
---------------------------------------------------------------------------
local KEYWORD_VALUES = { ["true"] = true, ["false"] = false, ["nil"] = "nil" }
local BINOPS = { ["+"] = 1, ["-"] = 1, ["*"] = 1, ["/"] = 1, ["%"] = 1, ["^"] = 1, [".."] = 1, ["=="] = 1,
                 ["~="] = 1, ["<"] = 1, [">"] = 1, ["<="] = 1, [">="] = 1, ["and"] = 1, ["or"] = 1 }

local function parser(toks)
    local P = { pos = 1 }
    local function peek(o) return toks[P.pos + (o or 0)] end
    local function nextTok() local t = toks[P.pos]; P.pos = P.pos + 1; return t end
    local function isOp(t, v) return t and t.t == "op" and t.v == v end

    local parseExpr, parseTable

    local function skipFunction()          -- after 'function': skip to matching 'end'
        local depth = 1
        while depth > 0 and peek() do
            local t = nextTok()
            if t.t == "name" then
                if t.v == "function" or t.v == "if" or t.v == "do" then depth = depth + 1
                elseif t.v == "repeat" then depth = depth + 1
                elseif t.v == "end" or t.v == "until" then depth = depth - 1 end
            end
        end
        return { k = "other" }
    end

    local function parseArgs()
        local t = peek()
        if isOp(t, "(") then
            nextTok()
            local args = {}
            while peek() and not isOp(peek(), ")") do
                args[#args + 1] = parseExpr()
                if isOp(peek(), ",") then nextTok() end
            end
            nextTok()
            return args
        elseif isOp(t, "{") then
            return { parseTable() }
        elseif t and t.t == "str" then
            nextTok(); return { { k = "str", v = t.v } }
        end
    end

    local function parsePrimary()
        local t = peek()
        if not t then return { k = "other" } end
        if t.t == "num" then nextTok(); return { k = "num", v = t.v } end
        if t.t == "str" then nextTok(); return { k = "str", v = t.v } end
        if isOp(t, "{") then return parseTable() end
        if isOp(t, "-") then nextTok(); local e = parsePrimary(); if e.k == "num" then e.v = -e.v end; return e end
        if isOp(t, "(") then nextTok(); local e = parseExpr(); if isOp(peek(), ")") then nextTok() end; return e end
        if isOp(t, "...") then nextTok(); return { k = "other" } end
        if t.t == "name" then
            if t.v == "function" then nextTok(); return skipFunction() end
            if t.v == "not" then nextTok(); parsePrimary(); return { k = "other" } end
            if KEYWORD_VALUES[t.v] ~= nil then nextTok(); return { k = "bool", v = KEYWORD_VALUES[t.v] } end
            nextTok()
            local name, line = t.v, t.line
            local node = { k = "name", v = name, line = line }
            while true do
                local p = peek()
                if isOp(p, ".") and peek(1) and peek(1).t == "name" then
                    nextTok(); name = name .. "." .. nextTok().v; node = { k = "name", v = name, line = line }
                elseif isOp(p, "[") then
                    nextTok(); parseExpr(); if isOp(peek(), "]") then nextTok() end
                    node = { k = "other" }
                elseif isOp(p, ":") and peek(1) and peek(1).t == "name" then
                    nextTok(); nextTok(); parseArgs(); node = { k = "other" }
                elseif isOp(p, "(") or isOp(p, "{") or (p and p.t == "str") then
                    node = { k = "call", name = node.k == "name" and node.v or "?", args = parseArgs() or {}, line = line }
                else
                    break
                end
            end
            return node
        end
        nextTok()
        return { k = "other" }
    end

    function parseExpr()
        local left = parsePrimary()
        while true do
            local t = peek()
            if t and ((t.t == "op" and BINOPS[t.v]) or (t.t == "name" and BINOPS[t.v])) then
                nextTok(); parsePrimary(); left = { k = "other" }
            else
                return left
            end
        end
    end

    function parseTable()
        local open = nextTok()             -- '{'
        local node = { k = "table", arr = {}, hash = {}, line = open.line, lines = {} }
        while peek() and not isOp(peek(), "}") do
            local t, t2 = peek(), peek(1)
            if t.t == "name" and isOp(t2, "=") then
                nextTok(); nextTok()
                node.hash[t.v] = parseExpr()
                node.lines[t.v] = t.line
            elseif isOp(t, "[") then
                nextTok(); parseExpr(); if isOp(peek(), "]") then nextTok() end
                if isOp(peek(), "=") then nextTok() end
                parseExpr()
            else
                local line = t.line
                local v = parseExpr()
                v.line = v.line or line
                node.arr[#node.arr + 1] = v
            end
            if isOp(peek(), ",") or isOp(peek(), ";") then nextTok() end
        end
        nextTok()                           -- '}'
        return node
    end

    -- Parse every top-level call in the file (maproot, instanceroot, root ...).
    function P.all()
        local out = {}
        while peek() do
            local t, t2 = peek(), peek(1)
            if t.t == "name" and (isOp(t2, "(") or isOp(t2, "{")) and t.v ~= "function" then
                out[#out + 1] = parseExpr()
            elseif t.t == "name" and t.v == "function" then
                nextTok(); skipFunction()
            else
                nextTok()
            end
        end
        return out
    end
    return P
end

---------------------------------------------------------------------------
-- Constants
---------------------------------------------------------------------------
local MAPS = {}
for name, id in readFile(DATA .. "\\const_maps.lua"):gmatch("([%u_%d]+)%s*=%s*(%d+)%s*;") do
    MAPS[name] = tonumber(id)
end

local ITEMS = {}
for id, body in readFile(DATA .. "\\ItemDB.lua"):gmatch("%[(%d+)%]=(%b{})") do
    ITEMS[tonumber(id)] = {
        q = tonumber(body:match("[{,]q=(%d+)")),
        equip = body:match("equippable=1") ~= nil,
        name = body:match('name="(.-)"[,}]'),
    }
end

local ALLIANCE_RACES = { HUMAN = true, DWARF = true, NIGHTELF = true, GNOME = true, SKYBORNE_ALLIANCE = true }
local HORDE_RACES = { ORC = true, UNDEAD = true, TAUREN = true, TROLL = true, SKYBORNE_HORDE = true }
local QUALITY_HEX = { [2] = "ff1eff00", [3] = "ff0070dd", [4] = "ffa335ee", [5] = "ffff8000" }

---------------------------------------------------------------------------
-- Pull quests out of the parsed trees
---------------------------------------------------------------------------
local quests = {}       -- [id] = { title, givers = {{kind,id,name,map,x,y}}, prev, faction, classes, best = {q,item}, dungeon }
local stats = { files = 0, qcalls = 0, removed = 0 }

local function cleanName(s)
    if not s then return nil end
    s = s:gsub("^%s+", ""):gsub("%s+$", "")
    s = s:gsub("%s*<.->%s*$", "")
    s = s:gsub("%s*%(.-!%)%s*$", "")           -- "(QI!)" style tags
    if s == "" then return nil end
    return s
end

local function mapOf(node)
    if not node or node.k ~= "name" then return nil end
    return MAPS[(node.v:gsub("^MAP%.", ""))]
end

local function coordOf(node)
    if not node or node.k ~= "table" then return end
    local a = node.arr
    if a[1] and a[1].k == "num" and a[2] and a[2].k == "num" then
        local map = mapOf(a[3])
        if map then return map, a[1].v / 100, a[2].v / 100 end
    end
end

local function factionOf(node)
    if not node then return nil end
    if node.k == "name" then
        if node.v:find("^ALLIANCE") then return "A" end
        if node.v:find("^HORDE") then return "H" end
        return nil
    end
    if node.k == "table" then
        local a, h = 0, 0
        for _, r in ipairs(node.arr) do
            if r.k == "name" then
                if ALLIANCE_RACES[r.v] then a = a + 1 elseif HORDE_RACES[r.v] then h = h + 1 end
            end
        end
        if a > 0 and h == 0 then return "A" end
        if h > 0 and a == 0 then return "H" end
    end
end

local function classesOf(node)
    if not node or node.k ~= "table" then return nil end
    local out = {}
    for _, c in ipairs(node.arr) do if c.k == "name" then out[#out + 1] = c.v end end
    return #out > 0 and table.concat(out, ",") or nil
end

local function removed(node)
    if not node or node.k ~= "table" then return false end
    local last = node.arr[#node.arr]
    return last and last.k == "name" and last.v:find("REMOVED") ~= nil
end

-- Reward items: every i(...) under the quest, not descending into other quests.
local function collectItems(node, out)
    if type(node) ~= "table" then return end
    if node.k == "call" then
        if node.name == "q" then return end
        if node.name == "i" and node.args[1] and node.args[1].k == "num" then out[#out + 1] = node.args[1].v end
        for _, a in ipairs(node.args) do collectItems(a, out) end
    elseif node.k == "table" then
        for _, v in ipairs(node.arr) do collectItems(v, out) end
        for k, v in pairs(node.hash) do if k == "groups" then collectItems(v, out) end end
    end
end

local function bestReward(groups)
    local ids, best = {}, nil
    collectItems(groups, ids)
    for _, id in ipairs(ids) do
        local it = ITEMS[id]
        if it and it.equip and it.q and it.q >= 2 and it.name and (not best or it.q > best.q) then
            best = { q = it.q, item = string.format("|c%s|Hitem:%d::::::::|h[%s]|h|r", QUALITY_HEX[it.q] or "ff1eff00", id, it.name) }
        end
    end
    return best
end

local function giversOf(t, comments)
    local ids = {}
    local h = t.hash
    if h.qg and h.qg.k == "num" then
        ids[#ids + 1] = { kind = "C", id = h.qg.v, name = cleanName(comments[t.lines.qg]) }
    end
    if h.qgs and h.qgs.k == "table" then
        for _, v in ipairs(h.qgs.arr) do
            if v.k == "num" then ids[#ids + 1] = { kind = "C", id = v.v, name = cleanName(comments[v.line]) } end
        end
    end
    local function prov(p, line)
        if p and p.k == "table" and p.arr[1] and p.arr[1].k == "str" and p.arr[2] and p.arr[2].k == "num" then
            local kind = (p.arr[1].v == "n" and "C") or (p.arr[1].v == "o" and "O") or nil
            if kind then ids[#ids + 1] = { kind = kind, id = p.arr[2].v, name = cleanName(comments[line or p.line]) } end
        end
    end
    prov(h.provider, t.lines.provider)
    if h.providers and h.providers.k == "table" then
        for _, p in ipairs(h.providers.arr) do prov(p, p.line) end
    end

    local coords = {}
    local m, x, y = coordOf(h.coord)
    if m then coords[1] = { m, x, y } end
    if h.coords and h.coords.k == "table" then
        for _, c in ipairs(h.coords.arr) do
            local cm, cx, cy = coordOf(c)
            if cm then coords[#coords + 1] = { cm, cx, cy } end
        end
    end

    local out = {}
    if #ids > 0 and #coords > 0 then
        local pairUp = (#ids == #coords)
        for i, g in ipairs(ids) do
            local c = pairUp and coords[i] or (i == 1 and coords[1])
            if c then
                out[#out + 1] = { kind = g.kind, id = g.id, name = g.name, map = c[1], x = c[2], y = c[3] }
            end
        end
    end
    return out
end

local function prevOf(h)
    if h.sourceQuest and h.sourceQuest.k == "num" then return h.sourceQuest.v end
    if h.sourceQuests and h.sourceQuests.k == "table" then
        for _, v in ipairs(h.sourceQuests.arr) do if v.k == "num" then return v.v end end
    end
end

local function mergeHash(base, over)
    local t = { k = "table", arr = base.arr, hash = {}, lines = {}, line = base.line }
    for k, v in pairs(base.hash) do t.hash[k] = v; t.lines[k] = base.lines[k] end
    for k, v in pairs(over.hash) do t.hash[k] = v; t.lines[k] = over.lines[k] end
    return t
end

local function addQuest(id, title, t, comments, dungeon, factionOverride)
    local h = t.hash
    if removed(h.timeline) then stats.removed = stats.removed + 1; return end
    if h.repeatable and h.repeatable.k == "bool" and h.repeatable.v == true then return end
    local q = quests[id]
    if not q then q = { id = id, givers = {} }; quests[id] = q end
    q.title = q.title or title
    for _, g in ipairs(giversOf(t, comments)) do q.givers[#q.givers + 1] = g end
    q.prev = q.prev or prevOf(h)
    q.faction = factionOverride or q.faction or factionOf(h.races)
    q.classes = q.classes or classesOf(h.classes)
    local b = bestReward(h.groups)
    if b and (not q.best or b.q > q.best.q) then q.best = b end
    if dungeon then q.dungeon = q.dungeon or dungeon end
end

local function walk(node, comments, dungeon)
    if type(node) ~= "table" then return end
    if node.k == "call" then
        if node.name == "q" and node.args[1] and node.args[1].k == "num" then
            stats.qcalls = stats.qcalls + 1
            local id = node.args[1].v
            local t = node.args[2]
            if t and t.k == "table" then
                local title = cleanName(comments[node.line])
                local aq, hq = t.hash.allianceQuestData, t.hash.hordeQuestData
                if (aq and aq.k == "table") or (hq and hq.k == "table") then
                    if aq and aq.k == "table" then
                        local id2 = (aq.hash.questID and aq.hash.questID.k == "num") and aq.hash.questID.v or id
                        addQuest(id2, title, mergeHash(t, aq), comments, dungeon, "A")
                    end
                    if hq and hq.k == "table" then
                        local id2 = (hq.hash.questID and hq.hash.questID.k == "num") and hq.hash.questID.v or id
                        addQuest(id2, title, mergeHash(t, hq), comments, dungeon, "H")
                    end
                else
                    addQuest(id, title, t, comments, dungeon)
                end
            end
        end
        for _, a in ipairs(node.args) do walk(a, comments, dungeon) end
    elseif node.k == "table" then
        for _, v in ipairs(node.arr) do walk(v, comments, dungeon) end
        for _, v in pairs(node.hash) do walk(v, comments, dungeon) end
    end
end

local function titleCase(s)
    return (s:gsub("(%a)([%w']*)", function(a, b) return a:upper() .. b end))
end

local SKIP_FILES = { ["0.5 - dungeon sets.lua"] = true, ["0.6 - world bosses.lua"] = true }

for _, path in ipairs(listLua(DATA)) do
    local base = path:match("([^\\]+)$")
    local isZone = path:find("\\zones\\", 1, true)
    local isDungeon = path:find("\\dungeons & raids\\", 1, true)
    if (isZone or isDungeon) and not SKIP_FILES[base] then
        stats.files = stats.files + 1
        local src = readFile(path)
        local toks, comments = tokenize(src)
        local dungeon
        if isDungeon then
            dungeon = src:match("instanceroot%(%d+,%s*{%s*%-%-%s*([^\r\n]+)")
            dungeon = cleanName(dungeon) or titleCase((base:gsub("%.lua$", ""):gsub("^[%d%.]+ %- ", "")))
        end
        local before = stats.qcalls
        for _, tree in ipairs(parser(toks).all()) do walk(tree, comments, dungeon) end
        if arg[3] == "debug" then
            local raw = 0
            for i = 1, #toks - 1 do
                if toks[i].t == "name" and toks[i].v == "q" and toks[i + 1].t == "op" and toks[i + 1].v == "(" then raw = raw + 1 end
            end
            if raw ~= stats.qcalls - before then
                print(string.format("  %s: %d q( tokens, %d parsed", base, raw, stats.qcalls - before))
            end
        end
    end
end

---------------------------------------------------------------------------
-- Chains -> starters
---------------------------------------------------------------------------
-- Path from the chain's first quest down to qid.
local function pathTo(qid)
    local path, seen, cur = {}, {}, qid
    while cur and quests[cur] and not seen[cur] and #path < 40 do
        table.insert(path, 1, cur)
        seen[cur] = true
        cur = quests[cur].prev
    end
    return path
end

-- The earliest quest on the path whose giver has a known location.
local function starterOn(path)
    for idx, id in ipairs(path) do
        local q = quests[id]
        if q.givers[1] then return idx, q end
    end
end

local starters, chainQuests = {}, {}
local counts = { gear = 0, dungeon = 0 }

local function sameOrNil(a, b) if a == b then return a end return nil end

local function offer(path, idx, root, fields)
    local steps = #path - idx + 1
    for i = idx, #path do chainQuests[path[i]] = true end
    for _, g in ipairs(root.givers) do
        local key = g.kind .. ":" .. g.id
        local s = starters[key]
        if not s then
            s = { kind = g.kind, id = g.id, name = g.name, map = g.map, x = g.x, y = g.y,
                  faction = root.faction, classes = root.classes }
            starters[key] = s
        else
            s.faction = sameOrNil(s.faction, root.faction)
            s.classes = sameOrNil(s.classes, root.classes)
            s.name = s.name or g.name
        end
        if fields.quality then
            if not s.quality or fields.quality > s.quality or (fields.quality == s.quality and steps < s.steps) then
                s.quality, s.item, s.finalTitle, s.steps = fields.quality, fields.item, fields.finalTitle, steps
                s.rootQuest, s.rootTitle = root.id, root.title
            end
        else
            if not s.dungeonSteps or steps < s.dungeonSteps then
                s.dungeonSteps, s.dungeonTitle, s.dungeonName = steps, fields.dungeonTitle, fields.dungeonName
                s.dRoot, s.dRootTitle = root.id, root.title
            end
        end
    end
end

for id, q in pairs(quests) do
    if q.best or q.dungeon then
        local path = pathTo(id)
        local idx, root = starterOn(path)
        if idx then
            if q.best then
                counts.gear = counts.gear + 1
                offer(path, idx, root, { quality = q.best.q, item = q.best.item, finalTitle = q.title })
            end
            if q.dungeon then
                counts.dungeon = counts.dungeon + 1
                offer(path, idx, root, { dungeonTitle = q.title, dungeonName = q.dungeon })
            end
        end
    end
end

---------------------------------------------------------------------------
-- Output (same text format as /qgs export, see Share.lua)
---------------------------------------------------------------------------
local FIELDS = { "kind", "id", "map", "x", "y", "rootQuest", "quality", "steps",
                 "name", "rootTitle", "finalTitle", "item",
                 "dRoot", "dungeonSteps", "dungeonName", "dungeonTitle", "dRootTitle",
                 "faction", "classes" }

local function esc(v)
    if v == nil then return "" end
    if type(v) == "number" then
        return (v == math.floor(v)) and tostring(v) or string.format("%.4f", v)
    end
    return (tostring(v):gsub("[%%|\t\n\r]", function(c) return string.format("%%%02X", c:byte()) end))
end

local keys = {}
for k in pairs(starters) do keys[#keys + 1] = k end
table.sort(keys)
local lines = { "QGS1" }
for _, k in ipairs(keys) do
    local s, row = starters[k], {}
    for i, f in ipairs(FIELDS) do row[i] = esc(s[f]) end
    lines[#lines + 1] = table.concat(row, "\t")
end
local export = table.concat(lines, "\n")
assert(not export:find("]=====]", 1, true))

local qids = {}
for id in pairs(chainQuests) do qids[#qids + 1] = id end
for id, q in pairs(quests) do if q.dungeon and not chainQuests[id] then qids[#qids + 1] = id end end
table.sort(qids)
local qlines = {}
for _, id in ipairs(qids) do
    local q = quests[id]
    qlines[#qlines + 1] = string.format("[%d]={%s,%s,%s},", id,
        -- Only link to quests on a kept chain, so chains start at their located starter.
        (q.prev and chainQuests[q.prev]) and tostring(q.prev) or "false",
        string.format("%q", q.title or ""):gsub("\\\n", "\\n"),
        q.dungeon and string.format("%q", q.dungeon) or "false")
end

local f = assert(io.open(OUT, "wb"))
f:write([[
-- QuestGearScout built-in data. GENERATED by tools/att_convert.lua - don't edit by hand.
--
-- Quest data from AllTheThings (https://github.com/ATTWoWAddon/AllTheThings),
-- WoW Forever patch 1.60.1, used under the MIT License:
-- Copyright (c) 2026 AllTheThings WoW Addon. See LICENSE-AllTheThings.txt.
local _, ns = ...

-- Chain starters, in the /qgs export format.
ns.BUNDLED_EXPORT = [=====[
]])
f:write(export)
f:write("\n]=====]\n\n")
f:write("-- [questID] = { previous quest in the chain, title, dungeon name }\nns.BUNDLED_QUESTS = {\n")
f:write(table.concat(qlines, "\n"))
f:write("\n}\n")
f:close()

local nq = 0 for _ in pairs(quests) do nq = nq + 1 end
local withGiver = 0 for _, q in pairs(quests) do if q.givers[1] then withGiver = withGiver + 1 end end
print(string.format("files %d, q() calls %d, quests %d (%d with a located giver), removed skipped %d",
    stats.files, stats.qcalls, nq, withGiver, stats.removed))
print(string.format("gear quests %d, dungeon quests %d, starters %d, chain quests %d, items known %d",
    counts.gear, counts.dungeon, #keys, #qids, (function() local c = 0 for _ in pairs(ITEMS) do c = c + 1 end return c end)()))
