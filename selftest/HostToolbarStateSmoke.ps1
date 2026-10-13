param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'An existing reader must be left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
Add-Type -AssemblyName System.Windows.Forms
if ([Windows.Forms.SystemInformation]::HighContrast) { throw 'Normal-theme marker capture requires a separately coordinated non-HC session; do not change the system setting.' }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-toolbar-state-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0
$app=$null
function Marker-Components([string]$Name, [bool]$Dark) {
    $image=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name+'.png')))
    try {
        $wanted=if ($Dark) { 0xDEA243 } else { 0xB87318 }
        $height=[Math]::Min($image.Height,[int][Math]::Ceiling(36.0*$script:scale))
        # Count separated X runs of the actual Warn pixels in this toolbar-only band.
        $groups=0; $inside=$false
        for ($x=0; $x -lt $image.Width; $x++) {
            $has=$false
            for ($y=0; $y -lt $height; $y++) {
                if (($image.GetPixel($x,$y).ToArgb() -band 0xFFFFFF) -eq $wanted) { $has=$true; break }
            }
            if ($has -and -not $inside) { $groups++ }
            $inside=$has
        }
        return $groups
    } finally { $image.Dispose() }
}
function Body-Equal([string]$First, [string]$Second) {
    # 工具栏带以下的正文像素逐点比较(步长 3):沉浸切换不得移动正文。
    $before=$null; $after=$null
    try {
        $before=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($First+'.png')))
        $after=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Second+'.png')))
        if ($before.Width -ne $after.Width -or $before.Height -ne $after.Height) { return $false }
        for ($row=[int][Math]::Ceiling(37.0*$script:scale); $row -lt $before.Height; $row+=3) { for ($col=0; $col -lt $before.Width; $col+=3) { if ($before.GetPixel($col,$row).ToArgb() -ne $after.GetPixel($col,$row).ToArgb()) { return $false } } }
        return $true
    } finally { if ($before) { $before.Dispose() }; if ($after) { $after.Dispose() } }
}
function Move-Pointer([int]$X, [int]$Y) { [void](Send $script:window 0x0200 0 (($Y -shl 16) -bor $X)) }
function Config-Has([string]$Line) { ([IO.File]::ReadAllLines((Join-Path $env:MDV_CONFIG_DIR 'config.txt'))) -contains $Line }
try {
    $alpha=Join-Path $fixture 'alpha & literal.md'; $bravo=Join-Path $fixture 'bravo.md'
    $source="# Alpha saved`n`nOriginal text.`n"
    [IO.File]::WriteAllText($alpha,$source)
    [IO.File]::WriteAllText($bravo,"# Bravo`n`nSecond source.`n")
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,800,0`ntabs=$alpha|$bravo`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$alpha+'"') -PassThru
    Wait-Until { if ($app.HasExited) {throw 'Toolbar host exited.'}; [HostNative]::Window([uint32]$app.Id,'MorningDesk') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('alpha & literal.md') }
    # An explicit startup file overrides session restoration. Add the second fixture
    # through the actual owned single-instance handoff, not a presumed tabs config.
    $handoff=Start-Process -FilePath $ExePath -ArgumentList ('"'+$bravo+'"') -PassThru -WindowStyle Hidden
    if (-not $handoff.WaitForExit(8000)) { throw 'Second fixture handoff did not exit.' }
    $handoff.Dispose()
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') }
    Command 1010
    Wait-Until { (Text-Of $script:window).Contains('alpha & literal.md') }
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("TOOLBAR_DPI="+[HostNative]::GetDpiForWindow($script:window))
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](1100*$script:scale),800,4)
    [void](Send $script:window 0x0200 0 -1)
    Capture 'b1-clean'
    Assert-Host 'clean toolbar has no unsaved marker' ((Marker-Components 'b1-clean' $false) -eq 0)
    Command 1002
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    Edit-Text "# Alpha draft`r`n`r`nUnsaved but isolated.`r`n"
    Command 1002
    Capture 'b1-dirty-light'
    Assert-Host 'active dirty source has save and tab markers' ((Marker-Components 'b1-dirty-light' $false) -eq 2)
    Assert-Host 'marking dirty did not write the source file' ([IO.File]::ReadAllText($alpha) -eq $source -and (Text-Of $script:window).StartsWith('*'))
    Command 1004
    Capture 'b1-dirty-dark'
    Assert-Host 'dark mode retains both unsaved markers' ((Marker-Components 'b1-dirty-dark' $true) -eq 2)
    # 阶段 B 沉浸:隐藏只停绘工具栏(两个标记随之消失),正文像素不动;指针到顶边召回;设置持久化。
    # 真实光标若停在工具栏带,系统补发的鼠标移动会按其位置召回工具栏:不动光标,只挪窗口使光标落在正文区或窗口之外。
    $cursor=[Windows.Forms.Cursor]::Position; $area=[Windows.Forms.Screen]::FromPoint($cursor).WorkingArea
    $top=if ($cursor.Y - 300 -ge $area.Top) { $cursor.Y - 300 } else { $cursor.Y + 40 }
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,$top,[int](1100*$script:scale),800,4)
    $bodyY=[int](400*$script:scale)
    Move-Pointer 200 $bodyY
    Capture 'zen-before'
    Command 2251
    Capture 'zen-hidden'
    Assert-Host 'zen hides toolbar drawing including unsaved markers' ((Marker-Components 'zen-hidden' $true) -eq 0)
    Assert-Host 'zen keeps body pixels in place' (Body-Equal 'zen-before' 'zen-hidden')
    Assert-Host 'zen preference is persisted' (Config-Has 'zen=1')
    Move-Pointer 200 ([int](10*$script:scale))
    Capture 'zen-recalled'
    Assert-Host 'pointer at top edge recalls toolbar' ((Marker-Components 'zen-recalled' $true) -eq 2)
    Move-Pointer 200 $bodyY
    Capture 'zen-rehidden'
    Assert-Host 'leaving the toolbar band hides it again' ((Marker-Components 'zen-rehidden' $true) -eq 0)
    Command 2251
    Capture 'zen-off'
    Assert-Host 'leaving zen restores toolbar and preference' ((Marker-Components 'zen-off' $true) -eq 2 -and (Config-Has 'zen=0'))
    Command 1009
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') }
    Capture 'b1-inactive-dirty'
    Assert-Host 'inactive dirty tab keeps its own marker' ((Marker-Components 'b1-inactive-dirty' $true) -eq 1)
    Command 1008
    Wait-Until { (Text-Of $script:window).Contains('alpha & literal.md') }
    Guarded-Command 1008 2
    Assert-Host 'cancel close retains dirty source and identity' ((Text-Of $script:window).StartsWith('*alpha & literal.md') -and [IO.File]::ReadAllText($alpha) -eq $source)
    Guarded-Command 2097 6
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) {throw 'Toolbar state host did not exit.'}
    Assert-Host 'toolbar state host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST TOOLBAR STATE SMOKE: $script:passed PASS (isolated documents; no system setting changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)); Capture 'b1-failure' }
    throw
} finally {
    if ($app) {if (-not $app.HasExited) {$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    $env:MDV_CONFIG_DIR=$oldConfig
    if (Test-Path -LiteralPath $fixture) {Remove-Item -LiteralPath $fixture -Recurse -Force}
}
