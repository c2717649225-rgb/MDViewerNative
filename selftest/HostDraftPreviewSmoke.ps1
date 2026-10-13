param([string]$ExePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) { $ExePath = Join-Path $projectRoot 'engine\bin\Release\net10.0-windows\MorningDesk.exe' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before running this isolated host test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-draft-preview-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($config)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = $config
$script:passed = 0
$app = $null
$handoff = $null
function Draft-Files { @(Get-ChildItem (Join-Path $config 'drafts') -Filter '*.json' -ErrorAction SilentlyContinue) }
# Reported only when a wait times out, so a future failure says what it actually saw
# instead of leaving a bare "timed out" in the log.
function Host-State {
    $lines = @()
    if ((Get-Variable -Name app -Scope Script -ErrorAction SilentlyContinue) -and $app -and -not $app.HasExited) {
        $lines += 'dialog=' + [HostNative]::Dialog([uint32]$app.Id)
    }
    if ((Get-Variable -Name window -Scope Script -ErrorAction SilentlyContinue) -and $script:window -ne [IntPtr]::Zero) {
        $lines += 'windowTitle=' + (Text-Of $script:window)
    }
    if ((Get-Variable -Name editor -Scope Script -ErrorAction SilentlyContinue) -and $script:editor -ne [IntPtr]::Zero) {
        $text = Text-Of $script:editor
        $head = $text.Substring(0, [Math]::Min(24, $text.Length)) -replace "`r", '\r' -replace "`n", '\n'
        $lines += 'editorVisible=' + [HostNative]::IsWindowVisible($script:editor)
        $lines += 'editorChars=' + $text.Length
        $lines += 'editorHead=' + $head
        $lines += 'editorSelection=' + (Send $script:editor 0x00B0)
        $lines += 'editorFirstLine=' + (Send $script:editor 0x00BB (Send $script:editor 0x00CE))
    } else { $lines += 'editor=absent' }
    $lines += 'draftFiles=' + (Draft-Files).Count
    return $lines
}
function Open-Another([string]$Path) {
    $script:handoff = Start-Process -FilePath $ExePath -ArgumentList ('"' + $Path + '"') -PassThru -WindowStyle Hidden
    if (-not $script:handoff.WaitForExit(8000)) { throw 'Owned handoff did not finish.' }
    $script:handoff.Dispose()
    $script:handoff = $null
}
try {
    [IO.File]::WriteAllText((Join-Path $config 'config.txt'), "theme=light`ntoc=0`nfont=16`nhints=0`nwin=120,90,800,700,0")
    $a = Join-Path $fixture 'alpha.md'
    $b = Join-Path $fixture 'bravo.md'
    $original = "# Original`n`nSaved source.`n"
    [IO.File]::WriteAllText($a, $original)
    [IO.File]::WriteAllText($b, "# Bravo`n")
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $a + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw "Host exited: $($app.ExitCode)" }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero } { Host-State }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    [void][HostNative]::ShowWindow($script:window, 5)
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') } { Host-State }
    [void](Send $script:window 0)
    $savedHeight = Scroll-Extent
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    Assert-Host 'source editor opens with saved text' ($script:editor -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($script:editor) -and (Text-Of $script:editor) -eq $original.Replace("`n", "`r`n"))
    $draft = "# Visible draft`r`n`r`n" + ("Paragraph in the unsaved draft.`r`n`r`n" * 65)
    Edit-Text $draft
    [void](Send $script:editor 0x00B1 4 11)
    [void](Send $script:editor 0x00B6 0 8)
    $selection = Send $script:editor 0x00B0
    $first = Send $script:editor 0x00BB (Send $script:editor 0x00CE)
    Command 1002
    Assert-Host 'preview opens without a save dialog' (-not [HostNative]::IsWindowVisible($script:editor) -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Assert-Host 'preview remains visibly dirty' ((Text-Of $script:window).StartsWith('*alpha.md'))
    Assert-Host 'preview renders the new draft rather than saved text' ((Scroll-Extent) -gt ($savedHeight + 500))
    Assert-Host 'preview leaves source bytes and backup untouched' ([IO.File]::ReadAllText($a) -eq $original -and -not (Test-Path ($a + '.bak')))
    Wait-Until { (Draft-Files).Count -eq 1 } { Host-State; 'expected draftFiles=1' }
    $snapshot = [IO.File]::ReadAllText((Draft-Files)[0].FullName) | ConvertFrom-Json
    Assert-Host 'preview draft remains recoverable' ($snapshot.Text -eq $draft.Replace("`r`n", "`n") -and $snapshot.SavedText -eq $original)
    Command 1002
    Assert-Host 'mode round trip preserves source selection' ((Text-Of $script:editor) -eq $draft -and (Send $script:editor 0x00B0) -eq $selection)
    # RichEdit may apply the queued syntax-color pass after the window is shown;
    # wait for the restored first line instead of sampling during that redraw.
    Wait-Until { (Send $script:editor 0x00BB (Send $script:editor 0x00CE)) -eq $first } { Host-State; "expected firstLine=$first selection=$selection" }
    Assert-Host 'mode round trip preserves a source viewport anchor' ((Send $script:editor 0x00BB (Send $script:editor 0x00CE)) -eq $first)
    Assert-Host 'mode round trip preserves native undo' ((Send $script:editor 0x00C6) -ne 0)
    [void](Send $script:editor 0x00C7)
    Assert-Host 'native undo still restores the saved text' ((Text-Of $script:editor) -eq $original.Replace("`n", "`r`n"))
    Command 1002
    Wait-Until { (Draft-Files).Count -eq 0 } { Host-State; 'expected draftFiles=0' }
    Assert-Host 'undo to saved text clears dirty and obsolete recovery' (-not (Text-Of $script:window).StartsWith('*') -and [IO.File]::ReadAllText($a) -eq $original)

    Command 1002
    $alphaDraft = "# Alpha unsaved`r`n`r`nKeep this while visiting another tab.`r`n"
    Edit-Text $alphaDraft
    [void](Send $script:editor 0x00B1 2 8)
    $alphaSelection = Send $script:editor 0x00B0
    Open-Another $b
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') } { Host-State }
    Assert-Host 'new-tab handoff does not discard or save the source draft' ([IO.File]::ReadAllText($a) -eq $original -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Command 1002
    Edit-Text "# Bravo unsaved`r`n"
    Command 1002
    Next-Document
    Command 1002
    Wait-Until { (Text-Of $script:editor) -eq $alphaDraft -and (Send $script:editor 0x00B0) -eq $alphaSelection } { Host-State; "expected selection=$alphaSelection draftChars=$($alphaDraft.Length)" }
    Assert-Host 'returning to another tab restores its own draft and selection' ((Text-Of $script:editor) -eq $alphaDraft -and (Send $script:editor 0x00B0) -eq $alphaSelection)
    Command 1002
    Command 1001
    Assert-Host 'explicit save also works from draft preview' ([IO.File]::ReadAllText($a) -eq $alphaDraft.Replace("`r`n", "`n") -and -not (Text-Of $script:window).StartsWith('*'))
    Assert-Host 'saving preview keeps the usual backup' ([IO.File]::ReadAllText($a + '.bak') -eq $original)
    Next-Document
    [IO.File]::WriteAllText($b, "# External bravo`n")
    Guarded-Command 1001 2
    Assert-Host 'preview save still respects external conflicts' ((Text-Of $script:window).StartsWith('*bravo.md') -and [IO.File]::ReadAllText($b) -eq "# External bravo`n")
    Guarded-Command 1008 2
    Assert-Host 'cancel close still retains a dirty preview' ((Text-Of $script:window).StartsWith('*bravo.md'))
    Guarded-Command 1008 1001
    Assert-Host 'explicit discard close preserves the external file' ((Text-Of $script:window).Contains('alpha.md') -and [IO.File]::ReadAllText($b) -eq "# External bravo`n")
    Close-LastTab
    Command 1002
    $unnamed = "# Unnamed draft`r`n`r`nNo Save As required."
    Edit-Text $unnamed
    Command 1002
    Assert-Host 'unnamed draft previews without a path dialog' ((Text-Of $script:window).StartsWith('*') -and -not [HostNative]::IsWindowVisible($script:editor) -and [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero)
    Open-Another $a
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') } { Host-State }
    Next-Document
    Command 1002
    Assert-Host 'opening a new tab preserves an unnamed draft' ((Text-Of $script:editor) -eq $unnamed)
    Assert-Host 'preview never invents a source file for an unnamed draft' (@(Get-ChildItem $fixture -Filter '*.md').Count -eq 2)
    Guarded-Command 1008 1001
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { $app.HasExited } { Host-State; 'expected host to exit' }
    Assert-Host 'owned host closes normally' ($app.ExitCode -eq 0)
    Write-Output "Draft preview host smoke: $script:passed passed"
} catch {
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    foreach ($process in @($handoff, $app)) {
        if ($process) {
            if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(4000) }
            $process.Dispose()
        }
    }
    $env:MDV_CONFIG_DIR = $previousConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
