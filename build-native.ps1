[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (![Environment]::Is64BitProcess -or [Environment]::OSVersion.Platform -ne 'Win32NT') {
        throw 'Use a Windows x64 build host for this release.'
    }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-home'
    $env:DOTNET_NOLOGO = '1'
    function Invoke-NativeDotnet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
    }
    Invoke-NativeDotnet run --project tests/ReviewTests -c Release
    Invoke-NativeDotnet run --project tests/NativeInteropTests -c Release
    # Requires Visual Studio Build Tools: Desktop development with C++ and Windows SDK.
    # Do not distribute the managed DLL from bin/ if publish fails.
    Invoke-NativeDotnet publish src/IntegrityGuard.Native/IntegrityGuard.Native.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/native/win-x64
    $nativeDll = Join-Path $PSScriptRoot 'artifacts/native/win-x64/IntegrityGuard.Native.dll'
    $importLibrary = Join-Path $PSScriptRoot 'src/IntegrityGuard.Native/bin/Release/net8.0/win-x64/native/IntegrityGuard.Native.lib'
    if (!(Test-Path -LiteralPath $importLibrary)) { throw 'Native import library was not generated.' }
    Copy-Item -LiteralPath $importLibrary -Destination artifacts/native/win-x64
    Invoke-NativeDotnet run --project tests/NativeInteropTests -c Release -- $nativeDll
    Invoke-NativeDotnet build tools/ManifestSigner -c Release
    & cmake -S samples/native-c -B artifacts/native-c -A x64 "-DINTEGRITY_GUARD_LIBRARY=$importLibrary"
    if ($LASTEXITCODE -ne 0) { throw 'C consumer configuration failed.' }
    & cmake --build artifacts/native-c --config Release
    if ($LASTEXITCODE -ne 0) { throw 'C consumer build failed.' }
    Copy-Item -LiteralPath $nativeDll -Destination artifacts/native-c/Release
    & python tests/native_smoke.py $nativeDll (Join-Path $PSScriptRoot 'tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll') (Join-Path $PSScriptRoot 'artifacts/native-c/Release/integrity_guard_sample.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Python native consumer tests failed.' }
    Copy-Item -LiteralPath native/include/integrity_guard.h -Destination artifacts/native/win-x64
    Copy-Item -LiteralPath docs/NATIVE.md -Destination artifacts/native/win-x64
    Write-Host 'Native DLL passed ABI and Python consumer tests. Output: artifacts/native/win-x64'
}
finally { Pop-Location }
