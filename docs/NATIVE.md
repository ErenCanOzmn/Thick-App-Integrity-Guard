# Native C ABI v1 — Windows x64

[Overview](../README.md) · [Runtime checks](RUNTIME.md) · [Signed-file integration](INTEGRATION.md)

## Artifacts and validation

The 0.4.0 Windows x64 NativeAOT DLL was built and tested locally. Thirteen ABI tests passed in a managed test host and again against the actual native DLL. Five external file-consumer tests exercised Python ctypes and an MSVC C executable. Six additional runtime-instrumentation scenarios passed in a Python host using the actual DLL.

Build environment: .NET SDK 10.0.403 targeting net8.0, Visual Studio Build Tools 2026, MSVC 19.51, Windows SDK 10.0.26100.0. Lua 5.4/LuaJIT runtime validation is not complete. These results do not replace application-specific acceptance tests.

## Build

Requirements: Windows x64, .NET 8 or a compatible newer SDK, Visual Studio C++ Build Tools, Windows SDK, CMake, and Python 3.

```powershell
.\build-native.ps1
```

The script runs managed regressions, runtime tests, and ABI checks; publishes NativeAOT; reruns ABI checks against the real DLL; builds a C consumer; and runs Python/C consumer tests. Any failed step stops the build.

Output: `artifacts/native/win-x64/IntegrityGuard.Native.dll`, its import library, and the header. Do not distribute a normal managed DLL from `bin/` as the native artifact.

The native library is self-contained and needs no installed .NET runtime in the consuming process. It must remain loaded until process exit: NativeAOT `FreeLibrary`/`dlclose` unloading is unsupported. Closing a session does not unload the library. See [Microsoft's native library contract](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries).

## Calling convention and ownership

Use [integrity_guard.h](../native/include/integrity_guard.h). The ABI uses cdecl and fixed-width integer types. A session handle is `uint64_t`, not a pointer or Lua floating-point number.

String arguments are explicit-length UTF-8 bytes; no NUL terminator is required. Caller pointers must be valid for the declared length, and input/output regions must not overlap. Invalid addresses can crash the process. The ABI is not isolation from untrusted in-process code.

| Function | Purpose |
| --- | --- |
| `ig_abi_version()` | Returns 1 |
| `ig_runtime_check(phase)` | Process-lifetime debugger/native-entry gate |
| `ig_open(json, length, &handle)` | Copies a signed-file policy; does not verify files yet |
| `ig_check(handle, phase)` | Replaces the saved file snapshots after full verification |
| `ig_copy_file(...)` | Copies bytes from the last successful snapshot, never reopens disk |
| `ig_close(handle)` | Releases the file session and snapshot references |

`ig_runtime_check` is additive to ABI v1 in 0.4.0. An older v1 DLL may lack that export; the ABI version alone does not establish its presence. Existing file functions do not invoke the runtime gate automatically.

## Runtime gate

Call `ig_runtime_check` at startup and immediately before sensitive operations. Phases: 0 startup, 1 periodic, 2 before operation. The first failure latches the gate for the process lifetime; file-session open/close cannot reset it.

| Status | Meaning |
| --- | --- |
| 0 | Runtime check succeeded |
| 200 | Debugger detected |
| 201 | Selected entry bytes changed |
| 202 | Inspection, PE, relocation, export, or read failure |
| 203 | Unsupported platform |
| 204 | Runtime gate already blocked |
| 205 | Invalid checkpoint |

The [runtime guide](RUNTIME.md) explains the four default targets, comparison window, disk trust, false positives, and check/call race.

## Signed-file policy

All configuration fields are required. Unknown/duplicate members are rejected. JSON is limited to 128 KiB and the root must be absolute.

```json
{
  "root": "C:\\MyApplication",
  "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
  "requiredFiles": ["protected.lua"],
  "minimumRelease": 2
}
```

This is a format example, not a working key. Construct it from trusted host settings. Do not accept a replaceable file's key/floor as authoritative. Never pass the signing private key.

The signed manifest must exactly match the required inventory. Limits are 32 files, 32 MiB per file, and 64 MiB total. There are at most 16 native file sessions per process. Closed handles are not reused.

Every `ig_check` clears the old snapshot first. Failure blocks the session; later checks return 108 and copies return 4. A stale successful snapshot is not exposed after a failed recheck.

## Buffers and concurrency

Query size with `destination=NULL, capacity=0`: a nonempty file returns 3; an empty file returns 0. Allocate the destination and call again. A short buffer receives no bytes. `written` contains required size on status 3, copied size on 0, and zero on other statuses.

Embedded NUL bytes are preserved and no string terminator is appended. The caller allocates/frees its own buffers; no cross-runtime allocator is involved.

`ig_close` drops references, but GC reclamation time and zeroization are not guaranteed. Bytes already copied to the caller remain the caller's responsibility.

Calls are individually locked. Check, size query, copy, and the business operation are not one atomic transaction. Use one serialized workflow owner per handle. Separate sessions may be used concurrently.

## File API statuses

```text
0    success
1    invalid ABI argument
2    invalid/closed handle
3    buffer too small
4    no verified snapshot
5    requested filename absent
6    session/resource limit
7    unexpected internal error
100  invalid policy
101  invalid signature
102  invalid manifest
103  invalid digest
104  rejected release rollback
105  file mismatch
106  missing manifest entry
107  file/cryptographic/access verification error
108  file session blocked
```

Managed exception objects do not cross the ABI. Fatal faults such as bad pointers or stack overflow have no recovery guarantee.

## Python and C/C++

`bindings/python/integrity_guard.py` supplies the signed-file API with standard-library `ctypes`. Use a context manager or `close()`; `read(name)` returns verified `bytes`, and `IntegrityError.code` carries native failures.

The runtime test host is `tests/native_runtime_probe.py`. It explicitly binds `ig_runtime_check`; the existing file-binding `Session.check()` remains a file check.

The C consumer is under `samples/native-c`. Link with the native import library and load the DLL from a trusted directory.

## LuaJIT 2.1

Add `bindings/luajit` to a trusted `package.path`:

```lua
local guard = require("integrity_guard")
local session, code = guard.open(native_dll_absolute_path, trusted_config_json)
assert(session, tostring(code))
local source, status = session:read("protected.lua", guard.BEFORE_OPERATION)
if not source then session:close(); error(tostring(status)) end
local chunk, err = load(source, "@verified/protected.lua", "t", {})
session:close()
assert(chunk, err)
return chunk()
```

`samples/luajit/run_verified.lua` handles close on errors. Handles remain FFI `uint64_t`. Buffers belong to LuaJIT; GC finalization is a fallback, not a substitute for explicit close.

## Lua 5.4

`bindings/lua54` is a distinct Lua C API module, not the LuaJIT binding. It needs matching x64 Lua 5.4 headers/import library:

```powershell
cmake -S bindings/lua54 -B artifacts/lua54 -A x64 `
  -DLUA_INCLUDE_DIR=C:/Lua54/include -DLUA_LIBRARY=C:/Lua54/lib/lua54.lib `
  -DINTEGRITY_GUARD_LIBRARY=C:/project/artifacts/native/win-x64/IntegrityGuard.Native.lib
cmake --build artifacts/lua54 --config Release
```

Paths are examples. Place the module and dependencies in a trusted application directory and restrict DLL search. Load/register the module from an application-controlled absolute path. The example is `samples/lua54/run_verified.lua`; the consumer test is `tests/lua_consumer.lua`.

Pass verified bytes to Lua `load` in text mode rather than reopening the file. A signature does not prove that a script is functionally safe or that the supplied environment is a sandbox.

The bundled Lua bindings expose signed-file workflows; adding the native runtime export to a Lua host requires explicit integration and tests. Their runtime support is not claimed as verified.

## Scope

Windows x64 is the verified target. Linux, macOS, ARM64, .NET Framework, and Lua 5.1/5.2/5.3 C modules are not supported release artifacts.

Native compilation does not change the trust boundary: local results can be manipulated, and process-memory confidentiality is not guaranteed.
