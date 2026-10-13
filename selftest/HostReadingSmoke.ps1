param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before reading-state tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\reading-host-' + [guid]::NewGuid().ToString('N'))
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
function Position {
    return (Scroll-Query 0)
}
function Open-Reader([string]$Expected, [string]$File = '') {
    if ($File.Length -gt 0) { $script:app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $File + '"') -PassThru -WindowStyle Hidden }
    else { $script:app = Start-Process -FilePath $ExePath -PassThru -WindowStyle Hidden }
    Wait-Until { if ($app.HasExited) { throw 'Reading host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    Wait-Until { (Text-Of $script:window).Contains($Expected) -and (Scroll-Extent) -gt 3000 }
    [void](Send $script:window 0)
    Start-Sleep -Milliseconds 300
    Wait-Until { (Text-Of $script:window).Contains($Expected) }
}
function Stop-Reader {
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $script:app.WaitForExit(6000)) { throw 'Reading host did not exit.' }
    Assert-Host 'reading host exits cleanly' ($script:app.ExitCode -eq 0)
    $script:app.Dispose(); $script:app = $null
}
try {
    $env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
    [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
    $a = Join-Path $fixture 'alpha.md'
    $b = Join-Path $fixture 'bravo.md'
    $body = [string]::Join("`n`n", (0..160 | ForEach-Object { "Paragraph $_ with enough words to identify this particular block." }))
    [IO.File]::WriteAllText($a, "# Alpha`n`n" + $body)
    [IO.File]::WriteAllText($b, "# Bravo`n`n" + $body)
    $config = Join-Path $env:MDV_CONFIG_DIR 'config.txt'
    $state = Join-Path $env:MDV_CONFIG_DIR 'reading-state.tsv'
    [IO.File]::WriteAllText($config, "theme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,960,800,0`nrecent1=$a`nrecent2=$b`ntabs=$a|$b`n")
    $encodedA = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($a))
    [IO.File]::WriteAllText($state, "reading-v1`nactive`t$encodedA`n")
    Open-Reader 'alpha.md'
    Assert-Host 'saved active tab wins over last opened tab' ((Text-Of $script:window).Contains('alpha.md'))
    1..6 | ForEach-Object { [void](Send $script:window 0x0115 3) }
    $positionA = Position
    Assert-Host 'first document scrolls' ($positionA -gt 1000)
    Command 1009
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') }
    Assert-Host 'recent-file overlap does not drop restored tabs' ((Text-Of $script:window).Contains('bravo.md'))
    1..2 | ForEach-Object { [void](Send $script:window 0x0115 3) }
    $positionB = Position
    Command 1010
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    Stop-Reader
    Assert-Host 'versioned reading state persisted' ([IO.File]::ReadAllText($state).StartsWith('reading-v1') -and [IO.File]::ReadAllLines($state).Count -eq 4)
    Open-Reader 'alpha.md'
    Assert-Host 'active document position survives process restart' ([Math]::Abs((Position) - $positionA) -lt 12)
    Command 1009
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') }
    Assert-Host 'inactive tab position survives process restart' ([Math]::Abs((Position) - $positionB) -lt 12)
    Stop-Reader
    Open-Reader 'bravo.md'
    Assert-Host 'changed active tab persists' ((Text-Of $script:window).Contains('bravo.md'))
    Stop-Reader
    Open-Reader 'alpha.md' $a
    Assert-Host 'explicit command-line document overrides saved active tab' ((Text-Of $script:window).Contains('alpha.md'))
    Stop-Reader
    [IO.File]::WriteAllText($a, ("# Inserted front matter`n`n" + ("New introduction.`n`n" * 20) + "# Alpha`n`n" + $body))
    Open-Reader 'alpha.md' $a
    Assert-Host 'inserting content relocates saved block instead of stale pixels' ((Position) -gt $positionA + 150)
    Stop-Reader
    [IO.File]::Delete($a)
    Open-Reader 'bravo.md'
    Assert-Host 'missing active path falls back to available recent document' ((Text-Of $script:window).Contains('bravo.md'))
    Stop-Reader
    [IO.File]::WriteAllText($state, 'invalid state')
    Open-Reader 'bravo.md'
    Assert-Host 'corrupt reading state falls back to top' ((Position) -eq 0)
    Stop-Reader
    $locked = [IO.File]::Open($state, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Open-Reader 'bravo.md'
        Assert-Host 'unreadable reading state does not break session restoration' ((Text-Of $script:window).Contains('bravo.md'))
    } finally { $locked.Dispose() }
    Stop-Reader
    Assert-Host 'reading restoration never writes document backups' (-not [IO.File]::Exists($b + '.bak'))
    Write-Output "HOST READING SMOKE: $script:passed PASS (owned messages and isolated state; no real keyboard or settings changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
