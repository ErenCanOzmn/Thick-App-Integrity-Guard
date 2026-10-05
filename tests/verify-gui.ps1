$ErrorActionPreference = 'Stop'
if (-not ('GuiWindowProbe' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'GuiWindowProbe.cs')
}
$projectRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path $projectRoot ('artifacts/application-tests/gui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$fixture = Join-Path $runDirectory 'app'
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts/demo') -Destination $fixture -Recurse
$rulePath = Join-Path $fixture 'demo-rules.json'
$originalRules = [IO.File]::ReadAllBytes($rulePath)
$parsedRules = [Text.Encoding]::UTF8.GetString($originalRules) | ConvertFrom-Json
$events = [Collections.Generic.List[object]]::new()
$process = $null

function Wait-Gui($action, [string]$description, [int]$seconds = 12) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $value = & $action
        if ($value) { return $value }
        if ($process -and $process.HasExited) { throw "Application exited during: $description" }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timeout: $description"
}
function Find-Window([string]$title) {
    foreach ($candidate in [GuiWindowProbe]::Windows($process.Id)) {
        if ([GuiWindowProbe]::Text($candidate) -eq $title) { return $candidate }
    }
    return [IntPtr]::Zero
}
function Read-State {
    $main = Find-Window 'IntegrityGuard - Dosya butunlugu demosu'
    if ($main -eq [IntPtr]::Zero) { return $null }
    $status = ''
    $button = [IntPtr]::Zero
    foreach ($child in [GuiWindowProbe]::Children($main)) {
        $text = [GuiWindowProbe]::Text($child)
        if ($text.StartsWith('DOGRULANDI') -or $text.StartsWith('ENGELLENDI')) { $status = $text }
        if ($text -eq 'Demo teklif hesapla') { $button = $child }
    }
    if (!$status -or $button -eq [IntPtr]::Zero) { return $null }
    return [PSCustomObject]@{ Status=$status; Enabled=[GuiWindowProbe]::IsWindowEnabled($button); Button=$button }
}
function Save-State([string]$name, $state) {
    $events.Add([PSCustomObject]@{ Scenario=$name; Status=$state.Status; ButtonEnabled=$state.Enabled })
    Write-Output "PASS $name : $($state.Status) ; Enabled=$($state.Enabled)"
}
function Stop-OwnedApp {
    if ($process -and !$process.HasExited) {
        foreach ($window in [GuiWindowProbe]::Windows($process.Id)) {
            [GuiWindowProbe]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id }
    }
}

$passed = $false
$failure = $null
try {
    $process = Start-Process -FilePath (Join-Path $fixture 'DemoApp.exe') -WindowStyle Hidden -PassThru
    $clean = Wait-Gui { $s = Read-State; if ($s -and $s.Status.StartsWith('DOGRULANDI') -and $s.Enabled) { $s } } 'clean GUI startup'
    Save-State 'clean startup enables quote button' $clean

    # Exercise the actual native button and modal dialog in the running GUI.
    [GuiWindowProbe]::PostMessage($clean.Button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $dialog = Wait-Gui { $w = Find-Window 'Demo teklif'; if ($w -ne [IntPtr]::Zero) { $w } } 'quote button dialog'
    $dialogText = @([GuiWindowProbe]::Children($dialog) | ForEach-Object { [GuiWindowProbe]::Text($_) }) -join ' '
    if (!$dialogText.Contains($parsedRules.Product)) { throw "Unexpected quote dialog: $dialogText" }
    $events.Add([PSCustomObject]@{ Scenario='quote button computes real dialog'; Text=$dialogText })
    Write-Output "PASS quote button computes real dialog : $dialogText"
    [GuiWindowProbe]::PostMessage($dialog, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Wait-Gui { $s = Read-State; if ((Find-Window 'Demo teklif') -eq [IntPtr]::Zero -and $s.Enabled) { $true } } 'dialog closes' | Out-Null

    [IO.File]::WriteAllText($rulePath, '{"Product":"Changed","UnitPrice":999}', [Text.UTF8Encoding]::new($false))
    $blocked = Wait-Gui { $s = Read-State; if ($s -and $s.Status.Contains('FileMismatch') -and !$s.Enabled) { $s } } 'periodic check rejects changed rules'
    Save-State 'live modification disables quote button' $blocked

    [IO.File]::WriteAllBytes($rulePath, $originalRules)
    Start-Sleep -Seconds 6
    $latched = Read-State
    if (!$latched -or $latched.Enabled -or !$latched.Status.StartsWith('ENGELLENDI')) { throw 'Repair unlocked existing GUI session.' }
    Save-State 'repair does not unlock current GUI session' $latched
    Stop-OwnedApp

    $process = Start-Process -FilePath (Join-Path $fixture 'DemoApp.exe') -WindowStyle Hidden -PassThru
    $recovered = Wait-Gui { $s = Read-State; if ($s -and $s.Status.StartsWith('DOGRULANDI') -and $s.Enabled) { $s } } 'restart after repair'
    Save-State 'restart after repair enables quote button' $recovered
    Stop-OwnedApp

    [IO.File]::WriteAllText($rulePath, '{"Product":"Changed","UnitPrice":999}', [Text.UTF8Encoding]::new($false))
    $process = Start-Process -FilePath (Join-Path $fixture 'DemoApp.exe') -WindowStyle Hidden -PassThru
    $initialBlock = Wait-Gui { $s = Read-State; if ($s -and $s.Status.Contains('FileMismatch') -and !$s.Enabled) { $s } } 'modified startup'
    Save-State 'modified startup keeps quote button disabled' $initialBlock
    $passed = $true
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    Stop-OwnedApp
    # Restore only this isolated fixture; leave the signed main demo untouched.
    [IO.File]::WriteAllBytes($rulePath, $originalRules)
    $report = [PSCustomObject]@{ Passed=$passed; Failure=$failure; RunDirectory=$runDirectory; Events=$events }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'gui-report.json') -Encoding UTF8
    Write-Output "GUI_REPORT=$(Join-Path $runDirectory 'gui-report.json')"
}
if (!$passed) { throw "GUI application tests failed: $failure" }
