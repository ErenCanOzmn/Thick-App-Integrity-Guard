#ifndef INTEGRITY_GUARD_H
#define INTEGRITY_GUARD_H
#include <stdint.h>
#if defined(_WIN32)
#define IG_API __declspec(dllimport)
#define IG_CALL __cdecl
#else
#define IG_API
#define IG_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef uint64_t ig_handle;
enum ig_status {
    IG_OK = 0, IG_INVALID_ARGUMENT = 1, IG_INVALID_HANDLE = 2,
    IG_BUFFER_TOO_SMALL = 3, IG_NOT_VERIFIED = 4, IG_FILE_NOT_FOUND = 5,
    IG_RESOURCE_LIMIT = 6, IG_INTERNAL_ERROR = 7,
    IG_INVALID_CONFIGURATION = 100, IG_INVALID_SIGNATURE = 101,
    IG_INVALID_MANIFEST = 102, IG_INVALID_DIGEST = 103,
    IG_ROLLBACK_REJECTED = 104, IG_FILE_MISMATCH = 105,
    IG_MISSING_REQUIRED_FILE = 106, IG_VERIFICATION_ERROR = 107,
    IG_SESSION_BLOCKED = 108,
    IG_RUNTIME_DEBUGGER_DETECTED = 200, IG_RUNTIME_NATIVE_ENTRY_CHANGED = 201,
    IG_RUNTIME_INSPECTION_ERROR = 202, IG_UNSUPPORTED_RUNTIME_PLATFORM = 203,
    IG_RUNTIME_SESSION_BLOCKED = 204, IG_INVALID_RUNTIME_CONFIGURATION = 205
};
enum ig_checkpoint { IG_STARTUP = 0, IG_PERIODIC = 1, IG_BEFORE_OPERATION = 2 };

IG_API uint32_t IG_CALL ig_abi_version(void);
/* Additive v1 extension, Windows x64: checks debugger state and four crypto API
   entrypoints against backing system PE files. Call before sensitive operations.
   First failure latches for process lifetime; no open/close/reset clears it.
   Nonzero means do not start the operation. Does not prevent attachment or
   attest the process. Existing ig_check still checks signed files only. */
IG_API int32_t IG_CALL ig_runtime_check(int32_t checkpoint);
/* UTF-8 JSON, explicit byte length; no terminating NUL needed. Handle is 0 on failure. */
IG_API int32_t IG_CALL ig_open(const uint8_t *config_json, uint32_t length, ig_handle *handle);
/* Replaces the saved snapshot. A failed check latches the session closed to operations. */
IG_API int32_t IG_CALL ig_check(ig_handle handle, int32_t checkpoint);
/* Copies the last verified snapshot, never reopens the source file.
   NULL/0 queries size: BUFFER_TOO_SMALL for nonempty, OK for empty.
   written is required size on BUFFER_TOO_SMALL, bytes copied on OK, otherwise 0.
   Caller owns destination and frees it with its own allocator; no cross-CRT free. */
IG_API int32_t IG_CALL ig_copy_file(ig_handle handle, const uint8_t *name,
    uint32_t name_length, uint8_t *destination, uint32_t capacity, uint32_t *written);
/* Releases session and saved snapshots. Double-close returns INVALID_HANDLE.
   Do not unload the NativeAOT library (FreeLibrary/dlclose is unsupported). */
IG_API int32_t IG_CALL ig_close(ig_handle handle);
#ifdef __cplusplus
}
#endif
#endif
