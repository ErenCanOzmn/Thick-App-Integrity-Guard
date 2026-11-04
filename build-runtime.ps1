[CmdletBinding()]
param([string]$SafiyeScript, [string]$NativeDll)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    $env:DOTNET_NOLOGO = '1'
    & dotnet run --project tests/RuntimeTests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Runtime regression tests failed.' }
    & dotnet build samples/RuntimeProbe -c Release -o artifacts/runtime-probe
    if ($LASTEXITCODE -ne 0) { throw 'Runtime laboratory build failed.' }
    if ($SafiyeScript) {
        $hookPath = (Resolve-Path -LiteralPath $SafiyeScript).Path
        & python tests/runtime_instrumentation_tests.py --safiye-script $hookPath
        if ($LASTEXITCODE -ne 0) { throw 'Managed Safiye comparisons failed.' }
        if ($NativeDll) {
            $libraryPath = (Resolve-Path -LiteralPath $NativeDll).Path
            & python tests/runtime_instrumentation_tests.py --safiye-script $hookPath --native-dll $libraryPath
            if ($LASTEXITCODE -ne 0) { throw 'Native DLL Safiye comparisons failed.' }
        }
    }
    elseif ($NativeDll) { throw 'Native instrumentation comparison also requires -SafiyeScript.' }
    else { Write-Host 'Runtime sample built. Safiye/Frida comparisons were not requested; pass -SafiyeScript to run them.' }
}
finally { Pop-Location }
