local ffi = require("ffi")
assert(ffi.os == "Windows" and ffi.arch == "x64", "this package targets Windows x64")
ffi.cdef[[
uint32_t ig_abi_version(void);
int32_t ig_open(const uint8_t *, uint32_t, uint64_t *);
int32_t ig_check(uint64_t, int32_t);
int32_t ig_copy_file(uint64_t, const uint8_t *, uint32_t, uint8_t *, uint32_t, uint32_t *);
int32_t ig_close(uint64_t);
int __stdcall GetModuleHandleExW(uint32_t, const uint16_t *, void **);
]]

local M = { STARTUP = 0, PERIODIC = 1, BEFORE_OPERATION = 2 }
-- Keep the DLL loaded for the lifetime of the Lua state. Do not unload NativeAOT.
local libraries = {}

function M.open(absolute_dll_path, config_json)
    assert(type(absolute_dll_path) == "string" and
        (absolute_dll_path:match("^%a:[/\\]") or absolute_dll_path:match("^\\\\")),
        "provide an absolute Windows DLL path")
    assert(type(config_json) == "string" and #config_json > 0 and #config_json <= 131072,
        "config must be UTF-8 JSON, at most 128 KiB")
    local lib = libraries[absolute_dll_path]
    if not lib then
        lib = ffi.load(absolute_dll_path)
        local pinned = ffi.new("void *[1]")
        assert(ffi.C.GetModuleHandleExW(5, ffi.cast("const uint16_t *", lib.ig_abi_version), pinned) ~= 0,
            "could not pin IntegrityGuard native library")
        libraries[absolute_dll_path] = lib
    end
    assert(lib.ig_abi_version() == 1, "unsupported IntegrityGuard ABI")
    local handle = ffi.new("uint64_t[1]")
    local rc = tonumber(lib.ig_open(config_json, #config_json, handle))
    if rc ~= 0 then return nil, rc end
    -- Keep handles as uint64_t cdata: Lua numbers cannot represent all 64-bit IDs.
    ffi.gc(handle, function(h) if h[0] ~= 0 then lib.ig_close(h[0]) end end)
    local session = {}
    function session:check(checkpoint)
        if handle[0] == 0 then return false, 2 end
        checkpoint = checkpoint or M.BEFORE_OPERATION
        assert(checkpoint == 0 or checkpoint == 1 or checkpoint == 2, "invalid checkpoint")
        local code = tonumber(lib.ig_check(handle[0], checkpoint))
        return code == 0, code
    end
    function session:read(name, checkpoint)
        assert(type(name) == "string" and #name > 0 and #name <= 128, "invalid filename")
        local ok, code = self:check(checkpoint)
        if not ok then return nil, code end
        local length = ffi.new("uint32_t[1]")
        code = tonumber(lib.ig_copy_file(handle[0], name, #name, nil, 0, length))
        if code ~= 0 and code ~= 3 then return nil, code end
        local n = tonumber(length[0])
        if n > 33554432 then return nil, 7 end
        local buffer = ffi.new("uint8_t[?]", math.max(n, 1))
        code = tonumber(lib.ig_copy_file(handle[0], name, #name, buffer, n, length))
        if code ~= 0 then return nil, code end
        return ffi.string(buffer, tonumber(length[0])), 0
    end
    function session:close()
        if handle[0] == 0 then return true, 0 end
        local code = tonumber(lib.ig_close(handle[0]))
        handle[0] = 0
        ffi.gc(handle, nil)
        return code == 0, code
    end
    return session, 0
end
return M
