param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before refresh tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\refresh-host-' + [guid]::NewGuid().ToString('N'))
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
function Wait-Dialog {
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
    return [HostNative]::Dialog([uint32]$app.Id)
}
function Finish-Dialog([IntPtr]$Dialog, [int]$Choice) {
    Choose-Dialog $Dialog $Choice
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
    [void](Send $script:window 0)
}
function Post-Refresh { [void][HostNative]::PostMessage($script:window, 0x0111, [IntPtr]2097, [IntPtr]::Zero) }
function Draft-Files { return @(Get-ChildItem -LiteralPath (Join-Path $env:MDV_CONFIG_DIR 'drafts') -File -ErrorAction SilentlyContinue) }
try {
    $env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
    [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "theme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,960,800,0")
    $path = Join-Path $fixture 'refresh.md'
    [IO.File]::WriteAllText($path, "# Original`n")
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Refresh host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('refresh.md') }
    [void][HostNative]::ShowWindow($script:window, 4) # SW_SHOWNOACTIVATE
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    $draft = "# Unsaved draft`r`n"
    Edit-Text $draft
    [void](Send $script:editor 0x00B1 3 8)
    $selection = Send $script:editor 0x00B0
    [void](Send $script:window 0x0113 8)
    $draftFiles = @(Draft-Files)
    Assert-Host 'draft checkpoint exists before refresh' ($draftFiles.Count -eq 1)
    $checkpoint = [IO.File]::ReadAllBytes($draftFiles[0].FullName)
    [IO.File]::WriteAllText($path, "# External`n")
    [void](Send $script:window 0x0006 1)
    $changedNotice = -join ([char[]](0x78C1, 0x76D8, 0x5DF2, 0x66F4, 0x6539))
    Wait-Until { (Text-Of $script:window).Contains($changedNotice) }
    Assert-Host 'external change is nonmodal and retains live draft' ((Text-Of $script:editor) -eq $draft -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Post-Refresh
    $dialog = Wait-Dialog
    Assert-Host 'discard confirmation defaults to No' (((Send $dialog 0x0400) -band 0xFFFF) -eq 7)
    Finish-Dialog $dialog 7
    Assert-Host 'cancel preserves editor text selection and dirty flag' ((Text-Of $script:editor) -eq $draft -and (Send $script:editor 0x00B0) -eq $selection -and (Text-Of $script:window).StartsWith('*'))
    Assert-Host 'cancel preserves undo history' ((Send $script:editor 0x00C6) -ne 0)
    Assert-Host 'cancel retains recovery bytes' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($draftFiles[0].FullName)) -eq [Convert]::ToBase64String($checkpoint))
    [IO.File]::Delete($path)
    Post-Refresh
    Finish-Dialog (Wait-Dialog) 1
    Assert-Host 'missing file failure preserves live draft and selection' ((Text-Of $script:editor) -eq $draft -and (Send $script:editor 0x00B0) -eq $selection -and (Text-Of $script:window).StartsWith('*'))
    Assert-Host 'failed refresh retains recovery checkpoint' ([IO.File]::Exists($draftFiles[0].FullName))
    [IO.File]::WriteAllText($path, "# Recreated`n")
    Post-Refresh
    $dialog = Wait-Dialog
    [IO.File]::WriteAllText($path, "# Changed during confirmation`n")
    Choose-Dialog $dialog 6
    Wait-Until { $next = [HostNative]::Dialog([uint32]$app.Id); $next -ne [IntPtr]::Zero -and (Text-Of $script:window).StartsWith('*') -and ([HostNative]::Control($next, 1) -ne [IntPtr]::Zero -or [HostNative]::Control($next, 2) -ne [IntPtr]::Zero) }
    Finish-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 1
    Assert-Host 'disk race rejects candidate without losing draft' ((Text-Of $script:editor) -eq $draft -and (Send $script:editor 0x00B0) -eq $selection)
    Post-Refresh
    Finish-Dialog (Wait-Dialog) 6
    Assert-Host 'confirmed refresh shows latest text and clears dirty flag' ((Text-Of $script:editor) -eq "# Changed during confirmation`r`n" -and -not (Text-Of $script:window).StartsWith('*'))
    Assert-Host 'refresh preserves editor mode' ([HostNative]::IsWindowVisible($script:editor))
    Assert-Host 'recovery removed only after successful commit' (-not [IO.File]::Exists($draftFiles[0].FullName))
    Assert-Host 'refresh does not create a disk backup' (-not [IO.File]::Exists($path + '.bak'))
    [IO.File]::WriteAllText($path, "# Clean reload`n")
    Command 2097
    Assert-Host 'clean refresh needs no discard dialog' ((Text-Of $script:editor) -eq "# Clean reload`r`n" -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Command 1002
    [IO.File]::WriteAllText($path, ("# Longer preview`n`n" + ("paragraph`n`n" * 80)))
    Command 2097
    Assert-Host 'preview refresh reflows the native document' ((Scroll-Extent) -gt 1000 -and -not [HostNative]::IsWindowVisible($script:editor))
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) { throw 'Refresh host did not exit.' }
    Assert-Host 'refresh host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST REFRESH SMOKE: $script:passed PASS (owned messages; no real keyboard, external application or network)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
