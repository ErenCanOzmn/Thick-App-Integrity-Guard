local guard = require("integrity_guard")
local function run_verified(absolute_dll_path, config_json)
    local session, code = guard.open(absolute_dll_path, config_json)
    assert(session, "IntegrityGuard open failed: " .. tostring(code))
    local ok, result = pcall(function()
        local source, status = session:read("protected.lua", guard.BEFORE_OPERATION)
        assert(source, "IntegrityGuard check failed: " .. tostring(status))
        -- LuaJIT 2.1 supports load's text-only mode. No dofile/loadfile disk reread.
        local chunk, err = load(source, "@verified/protected.lua", "t", { assert = assert })
        assert(chunk, err)
        return chunk()
    end)
    session:close()
    if not ok then error(result, 0) end
    return result
end
return run_verified
