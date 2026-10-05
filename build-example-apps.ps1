$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Builds/signs the real WinForms executable and verifies existing library tests.
    & ./build-demo.ps1
    if (!$?) { throw 'WinForms demo build failed.' }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    & dotnet build samples/QuoteConsole/QuoteConsole.csproj -c Release -o artifacts/apps/QuoteConsole
    if ($LASTEXITCODE -ne 0) { throw 'QuoteConsole build failed.' }
    & dotnet tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll sign .demo-keys/private.pem artifacts/apps/QuoteConsole 1 rules.json
    if ($LASTEXITCODE -ne 0) { throw 'QuoteConsole signing failed.' }
    & python tests/application_process_tests.py
    if ($LASTEXITCODE -ne 0) { throw 'Application process tests failed.' }
    & ./tests/verify-gui.ps1
    if (!$?) { throw 'GUI tests failed.' }
}
finally { Pop-Location }
