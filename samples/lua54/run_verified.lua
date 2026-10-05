-- Run in a host that preloads its trusted C module from an absolute application path.
local guard = require("integrity_guard")
-- config_json comes from trusted host configuration, including a pinned public key.
local function run_verified(config_json)
    local session, code = guard.open(config_json)
    assert(session, "IntegrityGuard open failed: " .. tostring(code))
    local owned_session <close> = session
    local source, status = session:read("protected.lua", guard.BEFORE_OPERATION)
    assert(source, "IntegrityGuard check failed: " .. tostring(status))
    -- Text-only loading. This environment is an example, not a security sandbox.
    local chunk, err = load(source, "@verified/protected.lua", "t", { assert = assert })
    assert(chunk, err)
    return chunk()
end
return run_verified
