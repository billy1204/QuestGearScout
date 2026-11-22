-- Checks that a Discoveries.lua written by the Companion app loads in the addon.
--   lua5.1 tests/discoveries_check.lua <addon folder> <expected count>
local dir, want = arg[1], tonumber(arg[2])
function strtrim(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
local ns = { QGS = { StarterKey = function(k, id) return k .. ":" .. id end } }
assert(loadfile(dir .. "/Discoveries.lua"))("QuestGearScout", ns)
-- Borrow the addon's real decoder from Share.lua without loading its UI code.
local src = assert(io.open("QuestGearScout/Share.lua")):read("*a")
local decoder = src:match("(local FIELDS.-\nend\n\nfunction ns%.Decode.-\nend\n)")
assert(loadstring(decoder))
local env = setmetatable({ ns = ns, QGS = ns.QGS }, { __index = _G })
local chunk = loadstring("local ns = ...\n" .. decoder)
setfenv(chunk, env)
chunk(ns)
local starters, n = ns.Decode(ns.DISCOVERED_EXPORT)
print(string.format("Discoveries.lua decodes %d starter(s), expected %d", n, want))
for k, s in pairs(starters) do print("  " .. k, s.name, s.steps, s.item) end
os.exit(n == want and 0 or 1)
