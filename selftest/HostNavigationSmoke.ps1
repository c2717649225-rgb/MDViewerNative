param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before navigation tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\navigation-' + [guid]::NewGuid().ToString('N'))
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'work\maturity\reader-links-host-images' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
function Position {
    return (Scroll-Query 0)
}
function Click-FirstLink {
    $scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
    $client = New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window, [ref]$client)
    $width = $client.R / $scale
    $x = (Column-Left $width) + 8.0
    $px = [int][Math]::Round($x * $scale)
    $py = [int][Math]::Round(96.0 * $scale)   # first line: top padding is 48 DIPs (S80)
    Write-Output "Click first link: width=$width x=$x scale=$scale physical=$px,$py"
    if (-not [HostNative]::ClickPhysicalClient($script:window, $px, $py)) { throw 'Cannot post mouse release.' }
}
function Open-Test([string]$Name, [string]$Source) {
    $path = Join-Path $fixture $Name
    [IO.File]::WriteAllText($path, $Source)
    if ($script:app) { Stop-Test }
    $script:app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru
    Wait-Until { if ($app.HasExited) { throw 'Navigation host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    # Wait for the actual source, not only window creation during startup.
    Wait-Until { (Text-Of $script:window).Contains($Name) }
    [void](Send $script:window 0)
    Capture $Name
}
function Stop-Test {
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $script:app.WaitForExit(6000)) { throw 'Navigation host did not exit.' }
    Assert-Host 'navigation host exits cleanly' ($script:app.ExitCode -eq 0)
    $script:app.Dispose()
    $script:app = $null
}
try {
    $env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
    [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,960,800,0")
    $filler = "body paragraph.`n`n" * 70
    Open-Test 'same.md' ("[Jump](#quick-start-1)`n`n# Quick Start`n`n" + $filler + "# Quick Start`n")
    Click-FirstLink
    Wait-Until { (Position) -gt 1000 }
    Assert-Host 'native click jumps to duplicate heading suffix' ((Position) -gt 1000)
    # 「返回原处」标签:跳转后在正文列底部居中出现,点击等同后退
    $scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
    $client = New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window, [ref]$client)
    Capture 'return-hint'
    if (-not [HostNative]::ClickPhysicalClient($script:window, [int]($client.R / 2), [int]($client.B - 28 * $scale))) { throw 'Cannot post mouse release.' }
    Wait-Until { (Position) -lt 100 }
    Assert-Host 'return hint click goes back to the reading position' ((Position) -lt 100)
    Stop-Test
    [IO.File]::WriteAllText((Join-Path $fixture 'target file.md'), "# Top`n`n" + $filler + "# Install Guide`n")
    Open-Test 'cross.md' "[Other](target%20file.md#install-guide)`n"
    Click-FirstLink
    Wait-Until { (Text-Of $script:window).Contains('target file.md') -and (Position) -gt 1000 }
    Assert-Host 'encoded local file and fragment reach target document' ((Text-Of $script:window).Contains('target file.md'))
    Assert-Host 'cross-document jump scrolls after loading' ((Position) -gt 1000)
    Stop-Test
    foreach ($link in @('#missing-heading','not-found.md','javascript:alert(1)')) {
        Open-Test 'invalid.md' ("[Invalid](" + $link + ")`n")
        Click-FirstLink
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
        $dialog = [HostNative]::Dialog([uint32]$app.Id)
        Assert-Host ('invalid destination gives feedback: ' + $link) ((Text-Of $dialog).Length -gt 0)
        Choose-Dialog $dialog 1
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
        Assert-Host 'failed navigation retains current document' ((Text-Of $script:window).Contains('invalid.md'))
        Stop-Test
    }
    # E11:可执行类文件用警告,普通文件沿用原提问;两者默认都是“否”,测试只选“否”,不会启动任何程序。
    [IO.File]::WriteAllText((Join-Path $fixture 'setup.bat'), "@exit /b 0`r`n")
    [IO.File]::WriteAllText((Join-Path $fixture 'report.pdf'), "%PDF-1.4`n")
    foreach ($case in @(@('setup.bat', '运行程序？'), @('report.pdf', '打开外部文件'))) {
        Open-Test 'external.md' ("[File](" + $case[0] + ")`n")
        Click-FirstLink
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
        $dialog = [HostNative]::Dialog([uint32]$app.Id)
        Assert-Host ('external file asks before launch: ' + $case[0]) ((Text-Of $dialog) -eq $case[1])
        Assert-Host ('external file question defaults to No: ' + $case[0]) (((Send $dialog 0x0400) -band 0xFFFF) -eq 7)
        Choose-Dialog $dialog 7
        Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
        Stop-Test
    }
    Write-Output "HOST NAVIGATION SMOKE: $script:passed PASS (owned messages; no external app or network launched)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)); Capture 'failure' }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
