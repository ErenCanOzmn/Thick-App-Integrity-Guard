# Deployment and release acceptance

[Overview](../README.md) · [Runtime checks](RUNTIME.md) · [Signed files](INTEGRATION.md)

## Plan the integration

Define the protected operations, inventory, OS/runtime, offline behavior, failure presentation, and trusted repair process. The sample's assumptions are not a customer's requirements.

For runtime checks, test the target Windows x64 build with its EDR/antivirus and hotpatch environment. A passing signed-file test does not demonstrate that the runtime gate works. Check before preparing or consuming sensitive data; do not add a production fallback that disables the gate after failure.

This is a reference implementation. Adapt it to the application and test the final deployed artifact.

## Signing keys

Use a product-specific key lifecycle. Keep the production private key in a protected signing environment; never include it in source control, a NuGet package, or an installation. Pin the public key in the trusted application distribution. Public keys are not secrets.

The development signer reads a local PEM file. HSM/KMS integration, access controls, role separation, and signing audit logs are not implemented here.

## Release order

1. Build with the trusted public key and inventory.
2. Apply obfuscation and final transformations.
3. Apply Authenticode signing or other steps that change protected file bytes.
4. Generate and sign the final manifest with an increasing positive release number.
5. Verify the final files and real application workflows.
6. Distribute through a trusted installer/updater with the manifest and signature.

Obfuscation must preserve required APIs, native exports, JSON fields, and reflection contracts. No obfuscator is included. Testing an unobfuscated development build is not evidence for an altered production artifact.

## Development signer

```powershell
dotnet build tools/ManifestSigner -c Release

# Development only. Existing key files are not overwritten.
dotnet tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll keygen .demo-keys

# Example paths; the private key stays in the protected signing environment.
dotnet tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll sign C:/SecureSigning/private.pem C:/Release/MyApp 2 rules.json
```

This protects only `rules.json`. Include the exact same inventory in the signer and `RequiredFiles` policy. A nonzero signer exit means the output must not be released.

Invalid inventories, duplicates, self-inclusion of the manifest/signature, and size-limit failures are rejected before replacing the existing signed pair. Limits are 32 files, 32 MiB per file, and 64 MiB total. Later disk/write failures have no two-file atomic update guarantee: use a separate staging directory.

`tests/signer_process_tests.py` checks preservation of an existing pair and is included in `build-example-apps.ps1`.

## Updates and rollback

A minimum accepted release must come from a trusted host/service or application distribution. A value beside replaceable files can be rolled back with them. Do not silently lower the floor when a service is unreachable.

Stage and verify a complete release separately. Stop the application before switching its files through a trusted updater. Installer/updater security is outside this library.

One pinned public key is supported. Automatic key rings, revocation, and rotation services are not included. Key changes require trusted application updates. A leaked signing key requires a new trusted key/verifier and an updated release policy.

## Acceptance checklist

- Clean installation, update, repair, and restart work on the target platform.
- Tampered/missing files and invalid signatures block the operation.
- Older signed releases fail below the trusted minimum.
- Repair does not reopen a blocked session.
- Business code consumes verified copies and validates its own schema.
- Selected entry changes are detected before the sensitive call.
- Legitimate security software and OS updates have a compatibility/recovery path.
- The final obfuscated/signed build preserves API, export, and JSON contracts.
- Signing private keys are absent from source and the package.
- Startup, periodic, and operation latency are measured with real file sizes.
- Log limits, failure presentation, and support procedures are defined.
- Server authorization does not trust the client's success result as evidence.

The runtime check is local defense in depth, not process isolation or a replacement for a server trust boundary.
