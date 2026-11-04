[CmdletBinding()]
param([string]$AnalysisScript)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Native build already runs file/runtime regressions and both ABI hosts.
    & ./build-native.ps1
    if (!$?) { throw 'Native build or verification failed.' }
    & dotnet build samples/RuntimeProbe -c Release -o artifacts/runtime-probe
    if ($LASTEXITCODE -ne 0) { throw 'Runtime example build failed.' }
    & dotnet pack src/IntegrityGuard -c Release -o artifacts/packages
    if ($LASTEXITCODE -ne 0) { throw 'Managed package build failed.' }

    $nativeDll = Join-Path $PSScriptRoot 'artifacts/native/win-x64/IntegrityGuard.Native.dll'
    if ($AnalysisScript) {
        $scriptPath = (Resolve-Path -LiteralPath $AnalysisScript).Path
        & python tests/runtime_instrumentation_tests.py --safiye-script $scriptPath
        if ($LASTEXITCODE -ne 0) { throw 'Managed instrumentation scenarios failed.' }
        & python tests/runtime_instrumentation_tests.py --safiye-script $scriptPath --native-dll $nativeDll
        if ($LASTEXITCODE -ne 0) { throw 'Native instrumentation scenarios failed.' }
    }

    [xml]$project = Get-Content -LiteralPath src/IntegrityGuard/IntegrityGuard.csproj -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
    $nativeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($nativeDll).FileVersion
    if ($nativeVersion -ne "$version.0") { throw 'Native binary version does not match source.' }
    $releaseDirectory = Join-Path $PSScriptRoot "artifacts/releases/$version"
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $zipPath = Join-Path $releaseDirectory "IntegrityGuard-$version-win-x64.zip"

    $entries = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    # Use Git's public source inventory; ignored development keys/build caches stay out.
    $inventory = & git -c core.quotePath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source inventory.' }
    foreach ($name in $inventory) {
        if ($name -match '(^|/)(\.git|\.demo-keys|\.dotnet-home|artifacts|bin|obj|__pycache__)(/|$)' -or
            $name -match '\.(pem|pfx|p12|snk|pyc)$') { throw "Unexpected release source: $name" }
        $entries[$name] = Join-Path $PSScriptRoot $name
    }
    $entries['lib/win-x64/IntegrityGuard.Native.dll'] = $nativeDll
    $entries['lib/win-x64/IntegrityGuard.Native.lib'] = Join-Path $PSScriptRoot 'artifacts/native/win-x64/IntegrityGuard.Native.lib'
    $entries["packages/IntegrityGuard.$version.nupkg"] = Join-Path $PSScriptRoot "artifacts/packages/IntegrityGuard.$version.nupkg"
    Get-ChildItem -LiteralPath artifacts/runtime-probe -File | Where-Object Extension -NE '.pdb' | ForEach-Object {
        $entries['examples/RuntimeProbe/' + $_.Name] = $_.FullName
    }

    if ($AnalysisScript) {
        $identities = @{
            'managed RuntimeProbe.exe' = @{ Hash=(Get-FileHash -LiteralPath artifacts/runtime-probe/RuntimeProbe.exe -Algorithm SHA256).Hash; Name='validation/runtime-managed.json' }
            'Python ctypes host for actual NativeAOT DLL' = @{ Hash=(Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash; Name='validation/runtime-native.json' }
        }
        $reports = Get-ChildItem -Path artifacts/runtime-tests/*/runtime-report.json -File | Sort-Object LastWriteTime -Descending
        foreach ($identity in $identities.Keys) {
            $matched = $false
            foreach ($report in $reports) {
                $data = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
                if ($data.target_kind -eq $identity -and $data.target_sha256 -eq $identities[$identity].Hash -and
                    @($data.results).Count -eq 6 -and @($data.results | Where-Object { !$_.passed }).Count -eq 0) {
                    $entries[$identities[$identity].Name] = $report.FullName
                    $matched = $true
                    break
                }
            }
            if (!$matched) { throw "No passing instrumentation report matches $identity." }
        }
    }

    $sourceCommit = & git rev-parse HEAD
    $sourceDirty = [bool](& git status --porcelain)
    $manifest = [ordered]@{
        version=$version; platform='Windows x64'; source_commit=$sourceCommit
        source_tree_dirty=$sourceDirty; dotnet_sdk=(& dotnet --version)
        build_checks=@('signed-file regressions', 'runtime core', 'managed ABI', 'native ABI', 'C/Python native consumers')
        instrumentation_comparisons=[bool]$AnalysisScript
        lua_runtime_validated=$false
    } | ConvertTo-Json -Depth 5

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fileStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create)
    $hashLines = [Collections.Generic.List[string]]::new()
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            if (!(Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Missing release input: $($entry.Key)" }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key) | Out-Null
            $hashLines.Add((Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $entry.Key)
        }
        $manifestEntry = $archive.CreateEntry('release-manifest.json')
        $writer = [IO.StreamWriter]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine($manifest) } finally { $writer.Dispose() }
        $hashEntry = $archive.CreateEntry('SHA256SUMS.txt')
        $writer = [IO.StreamWriter]::new($hashEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine(($hashLines -join "`n")) } finally { $writer.Dispose() }
    }
    finally { $archive.Dispose(); $fileStream.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash + '  ' + [IO.Path]::GetFileName($zipPath) | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ASCII
    Write-Host "Release ZIP: $zipPath"
    Write-Host "SHA256: $zipHash"
}
finally { Pop-Location }
