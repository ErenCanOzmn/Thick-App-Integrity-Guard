$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    $env:DOTNET_NOLOGO = '1'
    function Invoke-PackageDotnet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
    }
    Invoke-PackageDotnet run --project tests/ReviewTests/ReviewTests.csproj -c Release
    Invoke-PackageDotnet run --project tests/RuntimeTests/RuntimeTests.csproj -c Release
    Invoke-PackageDotnet run --project tests/NativeInteropTests/NativeInteropTests.csproj -c Release
    Invoke-PackageDotnet build samples/CustomerIntegration/CustomerIntegration.csproj -c Release
    Invoke-PackageDotnet pack src/IntegrityGuard/IntegrityGuard.csproj -c Release -o artifacts/packages
    Write-Host 'Local package created in artifacts/packages. No package was published.'
}
finally { Pop-Location }
