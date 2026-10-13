param([string]$ExePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MorningDesk.exe' }
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before running handoff tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HandoffProbe {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr process);
}
'@
$fixture = Join-Path $projectRoot ('work\handoff-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
[IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "hints=0`n")
$app = $null
$children = @()
$script:passed = 0
try {
    $first = Join-Path $fixture 'initial.md'
    [IO.File]::WriteAllText($first, '# initial')
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $first + '"') -PassThru
    Wait-Until { if ($app.HasExited) { throw 'Host exited at startup.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    [void](Send $script:window 0)
    $expected = @('initial.md')
    # Stall only the host created by this test, so all requests arrive before consumption.
    if ([HandoffProbe]::NtSuspendProcess($app.Handle) -ne 0) { throw 'Could not suspend test-owned host.' }
    try {
    1..24 | ForEach-Object {
        $name = 'concurrent-' + $_.ToString('00') + '.md'
        $expected += $name
        $file = Join-Path $fixture $name
        [IO.File]::WriteAllText($file, ('# ' + $name))
        $children += Start-Process -FilePath $ExePath -ArgumentList ('"' + $file + '"') -PassThru
    }
    Start-Sleep -Milliseconds 300
    } finally {
        if ([HandoffProbe]::NtResumeProcess($app.Handle) -ne 0) { throw 'Could not resume test-owned host.' }
    }
    foreach ($child in $children) {
        if (-not $child.WaitForExit(15000)) { throw 'Forwarding process did not exit.' }
        Assert-Host ('forwarder exits successfully ' + $child.Id) ($child.ExitCode -eq 0)
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $title = Text-Of $script:window
        foreach ($name in $expected) { if ($title.Contains($name)) { [void]$seen.Add($name) } }
        Command 1009
        Start-Sleep -Milliseconds 40
    } while ($seen.Count -lt $expected.Count -and [DateTime]::UtcNow -lt $deadline)
    $missing = @($expected | Where-Object { -not $seen.Contains($_) })
    Assert-Host ('all concurrent paths reach tabs; missing=' + ($missing -join ',')) ($missing.Count -eq 0)
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(8000)) { throw 'Host did not close normally.' }
    Assert-Host 'host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "Handoff checks: $script:passed passed"
} finally {
    foreach ($child in $children) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
