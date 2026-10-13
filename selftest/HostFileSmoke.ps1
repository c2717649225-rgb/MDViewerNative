param([string]$ExePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MorningDesk.exe' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before running this isolated host test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-file-host-' + [guid]::NewGuid().ToString('N'))
$configDirectory = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($configDirectory)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = $configDirectory
$app = $null
$script:passed = 0

function Post-Command([int]$Id) {
    [void][HostNative]::PostMessage($script:window, 0x0111, [IntPtr]((1 -shl 16) -bor $Id), [IntPtr]::Zero)
}
function Wait-Dialog {
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
    return [HostNative]::Dialog([uint32]$app.Id)
}
function Dialog-Finished {
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
    [void](Send $script:window 0)
}
function Handoff([string]$Path) {
    $other = Start-Process -FilePath $ExePath -ArgumentList ('"' + $Path + '"') -PassThru -WindowStyle Hidden
    try { if (-not $other.WaitForExit(8000)) { throw 'Handoff timed out.' } }
    finally { if (-not $other.HasExited) { Stop-Process -Id $other.Id -Force } }
    [void](Send $script:window 0)
}
function Set-ControlText([IntPtr]$Control, [string]$Value) {
    $result = [IntPtr]::Zero
    if ([HostNative]::SendText($Control, 0x000C, [IntPtr]::Zero, $Value, 2, 4000, [ref]$result) -eq [IntPtr]::Zero) { throw 'Set control text timed out.' }
}
function Pick-File([IntPtr]$Dialog, [string]$Path) {
    Wait-Until { [HostNative]::Control($Dialog, 1001, 'Edit') -ne [IntPtr]::Zero -or [HostNative]::Control($Dialog, 1148, 'Edit') -ne [IntPtr]::Zero }
    $name = [HostNative]::Control($Dialog, 1001, 'Edit') # Save dialog input, not the address toolbar.
    if ($name -eq [IntPtr]::Zero) { $name = [HostNative]::Control($Dialog, 1148, 'Edit') } # Open dialog input.
    Set-ControlText $name $Path
    $ok = [HostNative]::Control($Dialog, 1)
    if ($ok -eq [IntPtr]::Zero) { throw 'Missing file picker confirmation button.' }
    [void][HostNative]::PostMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
}
function Other-Edit([IntPtr[]]$Exclude) {
    $cursor = [IntPtr]::Zero
    do {
        $cursor = [HostNative]::FindWindowEx($script:window, $cursor, 'EDIT', $null)
        if ($cursor -ne [IntPtr]::Zero -and $cursor -notin $Exclude) { return $cursor }
    } while ($cursor -ne [IntPtr]::Zero)
    throw 'Missing editor child.'
}

try {
    [IO.File]::WriteAllText((Join-Path $configDirectory 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,766,900,0")
    $a = Join-Path $fixture 'alpha.md'
    $b = Join-Path $fixture 'bravo.md'
    $c = Join-Path $fixture 'charlie.md'
    $bad = Join-Path $fixture 'invalid.md'
    $draft = "# Visible draft`r`n"
    [IO.File]::WriteAllText($a, "# Original`n")
    [IO.File]::WriteAllText($c, "Existing Save As target")
    [IO.File]::WriteAllBytes($bad, [byte[]](0xEF, 0xBB, 0xBF, 0xC3, 0x28))
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $a + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    [void][HostNative]::ShowWindow($script:window, 5)
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    Edit-Text $draft
    [void](Send $script:editor 0x00B1 3 8)
    $selection = Send $script:editor 0x00B0
    Post-Command 1003
    $picker = Wait-Dialog
    Assert-Host 'open chooses a file before asking to discard' ((Text-Of $picker) -ne 'alpha.md')
    [void][HostNative]::PostMessage($picker, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Dialog-Finished
    Assert-Host 'cancel open preserves draft, dirty flag and selection' ((Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*') -and (Send $script:editor 0x00B0) -eq $selection)

    Post-Command 1003
    $picker = Wait-Dialog
    Pick-File $picker $c
    Wait-Until { $next = [HostNative]::Dialog([uint32]$app.Id); $next -ne [IntPtr]::Zero -and $next -ne $picker }
    Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 2
    Dialog-Finished
    Assert-Host 'cancel after selecting a file preserves the original session' ((Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*alpha.md') -and (Send $script:editor 0x00B0) -eq $selection)

    Handoff $a
    Assert-Host 'reopening active file preserves the live draft' ((Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*') -and [HostNative]::IsWindowVisible($script:editor))
    Handoff $bad
    $errorDialog = Wait-Dialog
    Assert-Host 'invalid UTF8 reports a read failure before dirty guard' ((Text-Of $errorDialog) -eq 'MorningDesk')
    Choose-Dialog $errorDialog 1
    Dialog-Finished
    Assert-Host 'read failure preserves editor draft and selection' ((Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*') -and (Send $script:editor 0x00B0) -eq $selection)

    [IO.File]::WriteAllText($a, "# External edit`n")
    Guarded-Command 1001 2
    Assert-Host 'cancel conflict preserves disk, draft and selection' ([IO.File]::ReadAllText($a) -eq "# External edit`n" -and (Text-Of $script:editor) -eq $draft -and (Text-Of $script:window).StartsWith('*') -and (Send $script:editor 0x00B0) -eq $selection)
    Post-Command 1001
    $conflict = Wait-Dialog
    Choose-Dialog $conflict 1000
    Wait-Until { $next = [HostNative]::Dialog([uint32]$app.Id); $next -ne [IntPtr]::Zero -and $next -ne $conflict }
    Pick-File ([HostNative]::Dialog([uint32]$app.Id)) $b
    Wait-Until { [IO.File]::Exists($b) -and (Text-Of $script:window).StartsWith('bravo.md') }
    Dialog-Finished
    Assert-Host 'conflict Save As writes a copy and preserves original' ([IO.File]::ReadAllText($b) -eq "# Visible draft`n" -and [IO.File]::ReadAllText($a) -eq "# External edit`n" -and (Text-Of $script:window).StartsWith('bravo.md') -and (Send $script:editor 0x00B0) -eq $selection)

    [IO.File]::WriteAllText($b, "External Bravo")
    Guarded-Command 1001 1001
    Assert-Host 'explicit conflict overwrite retains external backup' ([IO.File]::ReadAllText($b) -eq "# Visible draft`n" -and [IO.File]::ReadAllText($b + '.bak') -eq 'External Bravo')
    Post-Command 1012
    $picker = Wait-Dialog
    Pick-File $picker $c
    Wait-Until { $next = [HostNative]::Dialog([uint32]$app.Id); $next -ne [IntPtr]::Zero -and $next -ne $picker }
    Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 7
    Dialog-Finished
    Assert-Host 'cancel Save As overwrite preserves target and identity' ([IO.File]::ReadAllText($c) -eq 'Existing Save As target' -and (Text-Of $script:window).StartsWith('bravo.md'))
    Post-Command 1012
    $picker = Wait-Dialog
    Pick-File $picker $c
    Wait-Until { $next = [HostNative]::Dialog([uint32]$app.Id); $next -ne [IntPtr]::Zero -and $next -ne $picker }
    Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 6
    Dialog-Finished
    Assert-Host 'confirmed Save As preserves replaced target backup' ([IO.File]::ReadAllText($c + '.bak') -eq 'Existing Save As target' -and (Text-Of $script:window).StartsWith('charlie.md'))

    Edit-Text 'Aa aa'
    Command 1005
    $find = Other-Edit @($script:editor)
    Set-ControlText $find 'aa'
    Command 1013
    $replacement = Other-Edit @($script:editor, $find)
    Set-ControlText $replacement '$1\path'
    [void](Send $script:window 0x0111 3002)
    Assert-Host 'replace all uses literal replacement text' ((Text-Of $script:editor) -eq '$1\path $1\path')
    [void](Send $script:editor 0x00C7)
    Assert-Host 'replace all can be undone' ((Text-Of $script:editor) -eq 'Aa aa')
    [void](Send $script:editor 0x00B1 0 0)
    Set-ControlText $find 'aa'
    [void](Send $find 0x0100 13)
    Assert-Host 'find input Enter advances through the instance callback' ((Send $script:editor 0x00B0) -eq ((5 -shl 16) -bor 3))
    [void](Send $find 0x0100 13)
    Assert-Host 'find input Enter wraps to the first result' ((Send $script:editor 0x00B0) -eq (2 -shl 16))
    [void](Send $find 0x0100 27)
    Assert-Host 'find input Escape closes the controls' (-not [HostNative]::IsWindowVisible($find))
    Command 1005
    Assert-Host 'reopening find retains its query' ((Text-Of $find) -eq 'aa' -and [HostNative]::IsWindowVisible($find))
    [void](Send $find 0x0100 27)
    Discard-ToPreview
    Assert-Host 'discard synchronizes editor with saved snapshot' ((Text-Of $script:editor) -eq $draft -and -not (Text-Of $script:window).StartsWith('*'))
    Command 1002

    Edit-Text "# Latest export draft`r`n"
    $html = [IO.Path]::ChangeExtension($c, '.html')
    [IO.File]::WriteAllText($html, 'Hand-maintained HTML')
    Post-Command 1011
    Choose-Dialog (Wait-Dialog) 7
    Dialog-Finished
    Assert-Host 'cancel export overwrite preserves existing HTML' ([IO.File]::ReadAllText($html) -eq 'Hand-maintained HTML' -and (Text-Of $script:window).StartsWith('*'))
    Post-Command 1011
    $overwrite = Wait-Dialog
    Choose-Dialog $overwrite 6
    Wait-Until { [IO.File]::ReadAllText($html).Contains('Latest export draft') }
    Assert-Host 'export uses live draft and backs up existing HTML' ([IO.File]::ReadAllText($html + '.bak') -eq 'Hand-maintained HTML' -and [IO.File]::ReadAllText($c) -eq "# Visible draft`n" -and (Text-Of $script:window).StartsWith('*'))
    # Leave the success dialog open until fixture cleanup; dismissing it launches the user's browser.
    Write-Output "HOST FILE SMOKE: $script:passed PASS"
}
catch { Write-Output $_.ScriptStackTrace; Write-Output ([HostNative]::Snapshot([uint32]$app.Id)); throw }
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(8000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the test temporary directory.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
