# Changelog

## 0.4.0

- Added Windows x64 `RuntimeIntegritySession`: debugger state and the first 32 bytes of four native crypto entries are compared with backing System32 PE files.
- Resolve forwarded exports to their actual module owners. Unreadable entries and relocation overlaps fail closed. The first failure latches the session.
- Added `ig_runtime_check` to native ABI v1 with a process-lifetime gate independent of signed-file sessions.
- Added guarded/unguarded synthetic DPAPI examples and a ctypes host for the actual NativeAOT DLL. Plaintext is not logged.
- Seven runtime core tests and thirteen ABI tests passed in managed and native hosts.
- Six runtime-instrumentation scenarios passed for the managed lab and six for the native DLL/Python host: exact unguarded capture, guarded rejection, persistent block after detach, clean restart, passive attachment, and selected entry hooks.
- Tested `BCryptFinishHash` and `NCryptVerifySignature` entry hooks separately.
- Added English project documentation, source screenshots, and reproducible release packaging.

Results are local Windows development observations, not process isolation, remote attestation, production certification, or resistance to every instrumentation/bypass method.

## 0.3.1

- Require `Version`, `Files`, and `Release` in signed manifests; reject missing/wrong-type fields with `InvalidManifest`.
- Validate signer inventories, self-inclusion, and the total 64 MiB limit before replacing existing manifest/signature files.
- Added malformed-schema and partial-verification regressions: 27 passed.
- Eleven signer process scenarios, seven application scenarios, and six GUI controls passed.
- Built the actual Windows x64 NativeAOT DLL; twelve ABI tests and five external C/Python consumer tests passed. Lua runtime tests were not included.

## 0.3.0

Initial customer-oriented source distribution:

- .NET 8 signed manifests and verified file snapshots.
- Immutable policies, session latches, release floors, and local observations.
- WinForms, console, and customer-integration examples.
- NativeAOT ABI v1 source, C header, and Lua/Python binding source.
- Regression, entry-point, and application tests plus deployment guides.

This version did not implement anti-debug or native runtime-entry checks. Actual native DLL and Lua runtime validation were incomplete at that version.
