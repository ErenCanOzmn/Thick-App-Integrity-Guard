# .NET signed-file integration

[Overview](../README.md) · [Runtime checks](RUNTIME.md) · [Deployment](DEPLOYMENT.md)

This guide covers signed file snapshots. `RuntimeIntegritySession` is a separate API; file verification does not automatically compare native entry bytes.

## Add the library

Reference `src/IntegrityGuard/IntegrityGuard.csproj`, or run `build-package.ps1` and place `IntegrityGuard.0.4.0.nupkg` in a trusted local/private package feed. The project does not publish to NuGet.org; do not assume that a similarly named public package belongs to this repository.

The managed target is .NET 8. Verified use is Windows; this is not a .NET Framework 4.x package. WPF, services, and other hosts manage scheduling and failure presentation themselves.

## Construct a trusted policy

```csharp
using IntegrityGuard;

var session = new IntegritySession(new IntegrityPolicy(
    AppContext.BaseDirectory,
    pinnedPublicKeyPem,
    new[] { "rules.json" },
    minimumRelease: trustedMinimumRelease));

if (!session.Check(IntegrityCheckpoint.Startup).Success)
    return;

var result = session.Check(IntegrityCheckpoint.BeforeOperation);
if (!result.Success)
    return;

byte[] rules = result.Files["rules.json"];
// Parse this copy and validate the application's schema and business rules.
```

`IntegrityPolicy` copies the inventory. Invalid basic configuration throws `ArgumentException` or a derived exception; handle it before enabling the feature. The PEM key is parsed during verification and an invalid key fails verification.

The root, public key, inventory, and minimum release must come from trusted application configuration. A user-editable configuration cannot establish its own trust.

## Keep one session

| Checkpoint | Host responsibility |
| --- | --- |
| `Startup` | Check before enabling the protected feature |
| `Periodic` | Schedule optional checks and cancellation |
| `BeforeOperation` | Check immediately before every protected operation |

All three perform full verification. The names describe the observation stage, not different strength. The library creates no timer or network service. Keep disk I/O off the UI thread where appropriate.

The first failure latches the session. Subsequent calls return `SessionBlocked`, even after file repair. Recreating a session on every click discards that history. Provide a trusted repair/restart workflow.

## Consume the verified copy

`result.Files["rules.json"]` is the snapshot that passed verification. Do not reopen the file afterward. Validate required values, ranges, and business meaning separately: a valid signature does not make invalid business data valid.

Checks are serialized, but verification plus the business operation is not atomic. The host owns workflow serialization. Returned `byte[]` values are caller-mutable; avoid a shared mutable global cache. Each successful check returns independent snapshots.

[ProtectedRules.cs](../samples/CustomerIntegration/ProtectedRules.cs) is a compilable example. The lower-level `IntegrityVerifier.Verify` is stateless and supplies no session latch itself.

## Observations

An optional `Action<IntegrityObservation>` receives `SchemaVersion`, `CheckId`, `TimestampUtc`, `Checkpoint`, `Code`, `Allowed`, and `DurationMilliseconds`. It contains no file body, signing key, username, or local path.

The observer should enqueue quickly to a bounded log queue and must not reenter the same session. There is no callback timeout. Observer exceptions increment `ObserverFailureCount` and do not change the decision. Concurrent event delivery is not ordered.

These events are local telemetry, not authorization or attestation evidence.

## Result codes

| Code | Meaning |
| --- | --- |
| `Verified` | Snapshot verified; business schema still needs validation |
| `InvalidConfiguration` | Invalid host policy |
| `InvalidSignature` | Signature does not verify with the pinned key |
| `InvalidManifest`, `InvalidDigest`, `MissingRequiredFile` | Manifest/inventory problem |
| `FileMismatch` | A file does not match its signed digest |
| `RollbackRejected` | Release below the supplied trusted minimum |
| `VerificationError` | File access, missing file, size, parsing, or cryptographic failure |
| `SessionBlocked` | An earlier failure latched this session |

A failure is not attribution to an attacker. Interrupted updates and access errors can produce failures too. Do not continue the protected operation after a failed result.

## Format and limits

- 1–32 flat filenames using ASCII letters, digits, dots, hyphens, and underscores.
- 32 MiB per file, 64 MiB total; manifest limit 64 KiB.
- The signed inventory must exactly match the host's required inventory.
- ECDSA P-256 with SHA-256 signs the exact manifest bytes; signatures use P1363 encoding.
- `Version`, `Files`, and `Release` are required. Missing/wrong-type fields return `InvalidManifest`; an omitted release is not silently treated as release 1.
- File snapshots do not verify already-loaded code, memory, or debugger state.
- Native bindings have an additional [ABI contract](NATIVE.md).
