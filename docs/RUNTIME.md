# Runtime integrity checks

[Overview](../README.md) · [Native ABI](NATIVE.md) · [Deployment](DEPLOYMENT.md)

## Purpose and trust boundary

`RuntimeIntegritySession` helps an application detect conventional inline hooks in selected native APIs and decline its own sensitive operation. It is separate from signed-file verification. An unchanged disk file does not establish that the loaded function is unchanged.

This is a reference implementation. Broader coverage, stronger integration, and different detection methods can be developed. The current result is local telemetry and a workflow decision, not remote attestation.

**Verified target:** Windows x64. Other platforms return `UnsupportedRuntimePlatform`.

## Default coverage

The first **32 bytes** of each configured entry are checked:

| DLL | Export | Relevant boundary |
| --- | --- | --- |
| `crypt32.dll` | `CryptProtectData` | Plaintext on protection entry |
| `crypt32.dll` | `CryptUnprotectData` | Plaintext after successful unprotection |
| `bcrypt.dll` | `BCryptFinishHash` | Hash finalization |
| `ncrypt.dll` | `NCryptVerifySignature` | Signature verification |

This is not a complete scan of process memory, managed methods, every API, or every TLS provider.

## Reference bytes

The check does not snapshot current memory as a trusted startup baseline. A hook installed before startup could already be present in such a baseline.

Instead, it loads the named system DLL from its full System32 path, restricting dependency search to System32. It resolves the export and identifies the module that actually owns the resulting address. A forwarded export is compared with its real owner's PE file.

The code reads the reference entry window from the executable PE section at the resolved RVA, then reads current process bytes with `ReadProcessMemory`. A mismatch returns `RuntimeNativeEntryChanged`. References are reread from disk on each check; new memory bytes are not automatically trusted.

If a base relocation intersects the window, the check returns `RuntimeRelocationUnsupported`. It does not silently ignore the affected bytes. Unreadable entries, missing exports, unexpected owners outside System32, and unsupported images fail verification.

The backing OS files and the local mechanisms used to resolve addresses/read bytes are trust assumptions. Windows updates, hotpatching, and legitimate security products can produce differences requiring compatibility testing.

## Debugger state and sessions

The default policy separately checks `Debugger.IsAttached`, `IsDebuggerPresent`, and `CheckRemoteDebuggerPresent`. These do not establish that instrumentation is absent. Entry comparison is an additional check.

Keep one session for the application's workflow:

```csharp
using IntegrityGuard;

var runtime = new RuntimeIntegritySession();
if (!runtime.Check(IntegrityCheckpoint.Startup).Success)
    return;

// Later, immediately before the sensitive call:
var decision = runtime.Check(IntegrityCheckpoint.BeforeOperation);
if (!decision.Success)
    return;

// The host performs its own sensitive operation here.
```

The first failure latches the session. Later calls return `RuntimeSessionBlocked`, including after a hook is removed or entry bytes are repaired. Recreating a session on each operation discards this history. Provide a trusted restart/recovery workflow.

`RuntimeIntegrityPolicy` accepts 1–32 distinct system DLL/export pairs and copies the supplied list. A custom policy may set `detectDebugger: false`; that policy must not be described as providing debugger detection. Selecting a different export does not expand the platform or PE-layout support.

The host owns timing and scheduling. There is no hidden timer.

## Results

| Managed code | Meaning | Native status |
| --- | --- | --- |
| `RuntimeVerified` | Checks passed | 0 |
| `RuntimeDebuggerDetected` | Debugger state was positive | 200 |
| `RuntimeNativeEntryChanged` | Entry window differs from the backing file | 201 |
| `RuntimeInspectionError` and detailed inspection failures | Entry could not be verified | 202 |
| `UnsupportedRuntimePlatform` | Host is not Windows x64 | 203 |
| `RuntimeSessionBlocked` | An earlier failure latched the gate | 204 |
| `InvalidRuntimeConfiguration` | Invalid checkpoint | 205 |

Detailed managed inspection failures include unavailable exports/owners, unexpected module locations, unsupported PE images or relocation overlaps, and unreadable entries. A non-success result must not start the protected operation.

## Native DLL integration

`ig_runtime_check(int32_t checkpoint)` exposes the same default policy through the NativeAOT DLL. Checkpoints: 0 startup, 1 periodic, 2 before operation.

The native gate lives for the process lifetime. Opening/closing signed-file sessions with `ig_open`/`ig_close` cannot reset it. Existing `ig_check` still checks signed files only. A host requiring both checks must enforce both results.

The export is an additive extension to ABI v1; an older v1 DLL may lack it. Resolve the export from the 0.4.0 artifact and treat a missing export as an integration failure.

## Synthetic laboratory

```powershell
.\build-runtime.ps1
.\artifacts\runtime-probe\RuntimeProbe.exe

# Comparison only:
.\artifacts\runtime-probe\RuntimeProbe.exe --unguarded
```

The demo preloads the monitored DLLs. In guarded mode, startup verification happens **before** generating or protecting synthetic data. After a successful check, a 60-byte mutable buffer containing a `LAB_RUNTIME_` prefix and random bytes is protected with current-user DPAPI. Owned plaintext buffers are cleared.

Commands: `check`, `use`, and `exit`. A guarded `use` calls `CryptUnprotectData` only after a successful operation check. Output contains the decision, byte count, and `plaintextSha256`, never the plaintext. The hash is a comparison aid, not an authorization token.

`--unguarded` is a laboratory comparison mode, not a production fallback. No real credential, database, or network endpoint is involved.

`tests/native_runtime_probe.py <absolute-DLL> [--unguarded]` implements the same protocol in a Python ctypes host for the real native DLL. It does not start instrumentation itself.

## Reproduce the instrumentation comparison

The runner attaches only to synthetic lab processes it launches itself. The external analysis script is not included in this repository.

```powershell
.\build-runtime.ps1 -SafiyeScript C:/tools/analysis/hooks/safiye_frida_script.js `
    -NativeDll C:/project/artifacts/native/win-x64/IntegrityGuard.Native.dll
```

Paths are examples. Python and the Frida Python package must already be installed. The retained `-SafiyeScript` flag identifies the tested external script; it is not the name of a detection rule.

The local 0.4.0 run used Frida 16.6.4 and the unchanged script previously used in the author's runtime investigations. Script SHA-256:

```text
1ee82cdfe87c7772c33acde1d82b62c0475c757b5e3a25adec393488a78423cc
```

Six scenarios passed for the managed lab and six for the actual native DLL/Python host:

1. An unguarded DPAPI capture matches the consumed synthetic data's hash.
2. The guarded operation is rejected; detaching does not reopen its session.
3. A fresh, uninstrumented process succeeds.
4. Passive attachment alone remains accepted.
5. A `BCryptFinishHash` entry hook is detected.
6. An `NCryptVerifySignature` entry hook is detected.

Reports are under `artifacts/runtime-tests/<run>/runtime-report.json`. They retain counts and hashes, not raw plaintext. Coverage is **post-startup attachment**; all spawn/early-injection variants and bypass techniques have not been tested.

## Core tests

```powershell
dotnet run --project tests/RuntimeTests -c Release
```

Seven tests cover clean entries, missing exports and latching, invalid checkpoints, copied/invalid policies, controlled entry-byte modification and repair, and serialized parallel checks.

The byte-modification fixture changes one byte in its own test process, never calls that modified function, and restores both the byte and page permissions in `finally`. It does not replace a real-tool compatibility test.

## Limitations

- Only selected entry windows are measured. Mid-function changes, IAT hooks, managed methods, and unmonitored providers can be outside this check.
- EDR, antivirus, profilers, or hotpatching can change entries legitimately; a mismatch is not attacker attribution.
- The check and sensitive call are not atomic. A later hook can target the gap.
- Previously captured plaintext cannot be recalled.
- Address resolution, module queries, file/memory reads, the check itself, and its result can be manipulated in the same process.
- The library does not kill other processes, remove hooks, disable security products, or conceal the machine.
- Server authorization, trusted deployment, and removing shared secrets from the client remain separate architectural requirements.
