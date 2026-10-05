-- Usage for Lua 5.4: lua tests/lua_consumer.lua lua54 config.json
-- Usage for LuaJIT: luajit tests/lua_consumer.lua luajit config.json ABSOLUTE_NATIVE_DLL
-- Supply a fixture whose signed protected.lua contains exactly: return 42
local file = assert(io.open(assert(arg[2]), "rb"))
local config = file:read("*a")
file:close()
local guard = require("integrity_guard")
local session, code
if arg[1] == "luajit" then session, code = guard.open(assert(arg[3]), config)
else session, code = guard.open(config) end
assert(session, tostring(code))
local ok, err = pcall(function()
    assert(session:check(guard.STARTUP))
    local source, status = session:read("protected.lua", guard.BEFORE_OPERATION)
    assert(source, tostring(status))
    local chunk = assert(load(source, "@verified/protected.lua", "t", {}))
    assert(chunk() == 42)
    local missing, missing_code = session:read("not-in-manifest.lua")
    assert(missing == nil and missing_code == 5)
end)
session:close()
assert(ok, err)
local closed, closed_code = session:check(guard.STARTUP)
assert(not closed and closed_code == 2)
print("PASS Lua native consumer")
