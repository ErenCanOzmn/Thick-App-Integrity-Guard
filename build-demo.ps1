$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_NOLOGO = '1'
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
}
Invoke-Dotnet build tools/ManifestSigner/ManifestSigner.csproj -c Release
$signer = 'tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll'
if (!(Test-Path '.demo-keys/private.pem')) {
    Invoke-Dotnet $signer keygen .demo-keys
}
Invoke-Dotnet build src/DemoApp/DemoApp.csproj -c Release -o artifacts/demo
Invoke-Dotnet $signer sign .demo-keys/private.pem artifacts/demo 1 DemoApp.exe DemoApp.dll DemoApp.deps.json DemoApp.runtimeconfig.json IntegrityGuard.dll demo-rules.json
Invoke-Dotnet artifacts/demo/DemoApp.dll --verify
Invoke-Dotnet run --project tests/ReviewTests/ReviewTests.csproj -c Release
Invoke-Dotnet build samples/CustomerIntegration/CustomerIntegration.csproj -c Release
Write-Host 'Demo built and startup verification passed: artifacts/demo/DemoApp.exe'
