#include <stdint.h>
#include <lua.h>
#include <lauxlib.h>
#include "integrity_guard.h"
#if defined(_WIN32)
#include <windows.h>
#define LUA_EXPORT __declspec(dllexport)
#else
#define LUA_EXPORT
#endif

#define SESSION_MT "IntegrityGuard.Session.v1"
typedef struct { ig_handle handle; } session;

static int failure(lua_State *L, int32_t code) {
    lua_pushnil(L); lua_pushinteger(L, code); return 2;
}
static session *get_session(lua_State *L) {
    return (session *)luaL_checkudata(L, 1, SESSION_MT);
}
static int close_session(lua_State *L) {
    session *s = get_session(L);
    int32_t code = s->handle ? ig_close(s->handle) : IG_OK;
    s->handle = 0;
    lua_pushboolean(L, code == IG_OK); lua_pushinteger(L, code); return 2;
}
static int check_session(lua_State *L) {
    session *s = get_session(L);
    lua_Integer phase = luaL_optinteger(L, 2, IG_BEFORE_OPERATION);
    luaL_argcheck(L, phase >= 0 && phase <= 2, 2, "invalid checkpoint");
    int32_t code = ig_check(s->handle, (int32_t)phase);
    lua_pushboolean(L, code == IG_OK); lua_pushinteger(L, code); return 2;
}
static int read_file(lua_State *L) {
    session *s = get_session(L);
    size_t name_size;
    const char *name = luaL_checklstring(L, 2, &name_size);
    luaL_argcheck(L, name_size > 0 && name_size <= 128, 2, "invalid filename");
    lua_Integer phase = luaL_optinteger(L, 3, IG_BEFORE_OPERATION);
    luaL_argcheck(L, phase >= 0 && phase <= 2, 3, "invalid checkpoint");
    int32_t code = ig_check(s->handle, (int32_t)phase);
    if (code != IG_OK) return failure(L, code);
    uint32_t required = 0;
    code = ig_copy_file(s->handle, (const uint8_t *)name, (uint32_t)name_size, NULL, 0, &required);
    if (code != IG_OK && code != IG_BUFFER_TOO_SMALL) return failure(L, code);
    if (required > 33554432) return failure(L, IG_INTERNAL_ERROR);
    luaL_Buffer buffer;
    char *destination = luaL_buffinitsize(L, &buffer, required);
    uint32_t copied = 0;
    code = ig_copy_file(s->handle, (const uint8_t *)name, (uint32_t)name_size,
        (uint8_t *)destination, required, &copied);
    if (code != IG_OK) { luaL_pushresultsize(&buffer, 0); lua_pop(L, 1); return failure(L, code); }
    luaL_pushresultsize(&buffer, copied);
    lua_pushinteger(L, IG_OK); return 2;
}
static int open_session(lua_State *L) {
    size_t length;
    const char *config = luaL_checklstring(L, 1, &length);
    luaL_argcheck(L, length > 0 && length <= 131072, 1, "config must be at most 128 KiB");
    session *s = (session *)lua_newuserdatauv(L, sizeof(session), 0);
    s->handle = 0;
    luaL_setmetatable(L, SESSION_MT); /* set finalizer before acquiring native resource */
    int32_t code = ig_open((const uint8_t *)config, (uint32_t)length, &s->handle);
    if (code != IG_OK) { lua_pop(L, 1); return failure(L, code); }
    lua_pushinteger(L, IG_OK); return 2;
}
LUA_EXPORT int luaopen_integrity_guard(lua_State *L) {
    if (ig_abi_version() != 1) return luaL_error(L, "unsupported IntegrityGuard ABI");
#if defined(_WIN32)
    /* NativeAOT cannot be unloaded when an embedded Lua state closes. Pin the DLL. */
    HMODULE pinned;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            (LPCWSTR)(uintptr_t)&ig_abi_version, &pinned))
        return luaL_error(L, "could not pin IntegrityGuard native library");
#endif
    static const luaL_Reg methods[] = {
        {"check", check_session}, {"read", read_file}, {"close", close_session},
        {"__gc", close_session}, {"__close", close_session}, {NULL, NULL}
    };
    luaL_newmetatable(L, SESSION_MT);
    luaL_setfuncs(L, methods, 0);
    lua_pushvalue(L, -1); lua_setfield(L, -2, "__index");
    lua_pop(L, 1);
    lua_newtable(L);
    lua_pushcfunction(L, open_session); lua_setfield(L, -2, "open");
    lua_pushinteger(L, IG_STARTUP); lua_setfield(L, -2, "STARTUP");
    lua_pushinteger(L, IG_PERIODIC); lua_setfield(L, -2, "PERIODIC");
    lua_pushinteger(L, IG_BEFORE_OPERATION); lua_setfield(L, -2, "BEFORE_OPERATION");
    return 1;
}
