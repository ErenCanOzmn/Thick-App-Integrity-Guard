# Example applications and observed behavior

[Overview](../README.md) · [Runtime checks](RUNTIME.md) · [Native ABI](NATIVE.md)

All examples use synthetic local data. The runtime DPAPI lab and the signed-file demos exercise different APIs.

## Runtime DPAPI lab

Build/run:

```powershell
.\build-runtime.ps1
.\artifacts\runtime-probe\RuntimeProbe.exe
```

Use `check`, `use`, and `exit`. `use` performs the runtime gate before unprotecting synthetic data. It returns JSON Lines containing the decision and a comparison hash, not plaintext. The startup check runs before generating/protecting the sample data.

`--unguarded` selects the comparison mode. It is a lab-only option.

See [RUNTIME.md](RUNTIME.md) for post-startup instrumentation comparisons and the actual-native-DLL Python test host.

## Signed-file WinForms demo

Executable: `artifacts/demo/DemoApp.exe`. Keep its DLLs, runtimeconfig/deps files, rules, manifest, and signature together. It requires the Windows .NET 8 Desktop Runtime.

The application calculates a synthetic quote from signed rules. It checks startup, every five seconds, and before the operation. The first failure disables the quote button for that session.

The manifest covers six distribution files. This happens after application code is loaded and does not establish a trusted boot/loading boundary.

Six local GUI checks passed:

| Check | Observed behavior |
| --- | --- |
| Clean startup | `DOGRULANDI`, button enabled |
| Actual quote button | Modal dialog shows `Demo hizmet: 125,00 TL` |
| Rules changed while running | `FileMismatch`, button disabled |
| Original file restored | Button still disabled after six seconds |
| Restart after repair | Verification succeeds, button enabled |
| Tampered startup | `FileMismatch`, button remains disabled |

The demo retains its original UI labels. Tests observed real Windows controls, sent the actual button-click message, and read the modal dialog. No mode was added to fabricate GUI results.

Report: `artifacts/application-tests/gui-<run>/gui-report.json`.

## Signed-file console demo

Executable: `artifacts/apps/QuoteConsole/QuoteConsole.exe`. Keep its distribution folder intact; it requires the .NET 8 Runtime.

```text
quote 2
check
exit
```

Responses are JSON Lines. Two units total `251.00` in the clean example. It uses `ProtectedRules` directly and signs only `rules.json`, not its EXE/DLL. Required business fields and ranges are validated after file verification.

Seven process scenarios passed:

- Clean quote calculation and negative quantity rejection.
- Live tampering produces `FileMismatch`; repair keeps `SessionBlocked`; restart recovers.
- Changed startup file returns `FileMismatch`, exit 1.
- Missing startup file returns `VerificationError`, exit 1.
- Invalid signature returns `InvalidSignature`, exit 1.
- Validly signed data missing its price returns `InvalidBusinessRules`.
- WinForms `--verify` returns 0 for clean files and 1 after tampering.

Report: `artifacts/application-tests/<run>/process-report.json`.

## Build and test the signed-file apps

```powershell
.\build-example-apps.ps1
```

The script creates development keys if needed, builds/signs the apps, runs library and signer regressions, and exercises real processes/controls. GUI tests require a usable Windows desktop session and can fail in a headless service session.

Tests modify unique copies under `artifacts`, close only processes they launched, and retain their reports. Development signing keys are not distribution assets.

These demos do not automatically use the runtime gate. Their successful file/GUI results are not runtime-instrumentation resistance evidence.

The native DLL has separate ABI and C/Python consumer tests. Lua runtime validation remains incomplete.
