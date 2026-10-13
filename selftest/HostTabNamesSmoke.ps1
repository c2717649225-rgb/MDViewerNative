param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'An existing reader must be left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
# A2-B3 display names in the real host: toolbar focus descriptions, the native
# locate list and screenshots. Owned window messages only; no real keyboard,
# no system setting changes, isolated config and temporary documents.
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-tab-names-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'one'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'two'))
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0
$app=$null
$sep=' '+[char]0x203A+' '
$prefix='\[\u5DE5\u5177\u680F\uFF1A'
$tabPattern=$prefix+'\u6807\u7B7E\uFF1A(?<name>[^\]]+?)(\uFF08\u672A\u4FDD\u5B58\uFF09)?\]'
function Form-Window { [HostNative]::Window([uint32]$app.Id,'MDVNativeForm') }
function Click-Form([int]$Id) { $f=Form-Window; [void][HostNative]::PostMessage([HostNative]::GetDlgItem($f,$Id),0x00F5,[IntPtr]::Zero,[IntPtr]::Zero) }
function Handoff([string]$Path) {
    $p=Start-Process -FilePath $ExePath -ArgumentList ('"'+$Path+'"') -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(8000)) { throw 'Fixture handoff did not exit.' }
    $p.Dispose()
    $leaf=[IO.Path]::GetFileName($Path)
    Wait-Until { (Text-Of $script:window).StartsWith($leaf) } { 'title=' + (Text-Of $script:window) }
}
function Toolbar-TabNames {
    # F6 is routed by the message loop, so it must be posted; arrows are handled by WndProc.
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x75,[IntPtr]::Zero)
    Wait-Until { (Text-Of $script:window) -match $prefix } { 'title=' + (Text-Of $script:window) }
    $names=New-Object System.Collections.Generic.List[string]
    $titles=New-Object System.Collections.Generic.List[string]
    for ($i=0; $i -lt 40; $i++) {
        $t=Text-Of $script:window
        $titles.Add($t)
        if ($t -match $tabPattern -and -not $names.Contains($Matches['name'])) { $names.Add($Matches['name']) }
        [void](Send $script:window 0x0100 0x27)
    }
    [void](Send $script:window 0x0100 0x1B)
    Wait-Until { (Text-Of $script:window) -notmatch $prefix }
    return ,@($names.ToArray())
}
function Scroll-Position { Scroll-Query 0 }
function Focus-Part { if ((Text-Of $script:window) -match ($prefix+'[^\]]*\]')) { $Matches[0] } else { '' } }
function Locate-Items {
    [void][HostNative]::PostMessage($script:window,0x0111,[IntPtr]2213,[IntPtr]::Zero)
    Wait-Until { (Form-Window) -ne [IntPtr]::Zero }
    Click-Form 4000
    Wait-Until { $f=Form-Window; $f -ne [IntPtr]::Zero -and [HostNative]::GetDlgItem($f,3000) -ne [IntPtr]::Zero }
    $list=[HostNative]::GetDlgItem((Form-Window),3000)
    $count=Send $list 0x018B
    $items=@()
    for ($i=0; $i -lt $count; $i++) {
        $len=Send $list 0x018A $i
        $sb=New-Object Text.StringBuilder ([int]$len+2)
        $r=[IntPtr]::Zero
        if ([HostNative]::ReadBuffer($list,0x0189,[IntPtr]$i,$sb,2,4000,[ref]$r) -eq [IntPtr]::Zero) { throw 'LB_GETTEXT timed out.' }
        $items+=$sb.ToString()
    }
    Click-Form 4001
    Wait-Until { (Form-Window) -eq [IntPtr]::Zero }
    return ,$items
}
try {
    $readme1=Join-Path $fixture 'one\README.md'; $readme2=Join-Path $fixture 'two\README.md'
    $notes=Join-Path $fixture 'notes.MD'; $amp=Join-Path $fixture 'A&B.md'
    $files=@($readme1,$readme2,$notes,$amp)
    $sources=@{}
    foreach ($f in $files) { $s="# "+[IO.Path]::GetFileName($f)+"`n`nFixture body.`n"; [IO.File]::WriteAllText($f,$s); $sources[$f]=$s }
    # A long active document so PageDown has somewhere to go (review #4).
    $long="# A&B.md`n`n"+((1..200 | ForEach-Object { "Paragraph $_ of the fixture body." }) -join "`n`n")+"`n"
    [IO.File]::WriteAllText($amp,$long); $sources[$amp]=$long
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,800,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$readme1+'"') -PassThru
    Wait-Until { if ($app.HasExited) {throw 'Tab-name host exited.'}; [HostNative]::Window([uint32]$app.Id,'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MDViewerNative')
    Wait-Until { (Text-Of $script:window).StartsWith('README.md') }
    Handoff $readme2; Handoff $notes; Handoff $amp
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("TAB_NAMES_DPI="+[HostNative]::GetDpiForWindow($script:window))
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](1100*$script:scale),[int](800*$script:scale),4)
    [void](Send $script:window 0x0200 0 -1)
    Capture 'b3-wide'

    Assert-Host 'window title keeps the original file name' ((Text-Of $script:window).StartsWith('A&B.md'))
    $names=Toolbar-TabNames
    Write-Output ('TOOLBAR_TAB_NAMES='+($names -join ' | '))
    $expected=@(('one'+$sep+'README'),('two'+$sep+'README'),'notes','A&B')
    Assert-Host 'toolbar exposes all four tabs by display name' ($names.Count -eq 4 -and @($expected | Where-Object { $names -notcontains $_ }).Count -eq 0)
    Assert-Host 'display names hide .md case-insensitively' (@($names | Where-Object { $_ -match '\.md$' }).Count -eq 0)
    Assert-Host 'same-name files are qualified by their own parent directory' ($names -contains ('one'+$sep+'README') -and $names -contains ('two'+$sep+'README'))

    # Review #4: the focused toolbar owns Home/End but leaves reading keys to the document.
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x75,[IntPtr]::Zero)
    Wait-Until { (Text-Of $script:window) -match $prefix } { 'title=' + (Text-Of $script:window) }
    [void](Send $script:window 0x0100 0x24); $homeFocus=Focus-Part
    [void](Send $script:window 0x0100 0x23); $endFocus=Focus-Part
    [void](Send $script:window 0x0100 0x27); $wrappedFocus=Focus-Part
    Write-Output ('TOOLBAR_HOME='+$homeFocus+' END='+$endFocus)
    Assert-Host 'Home and End reach the first and last toolbar target' ($homeFocus -ne '' -and $endFocus -ne '' -and $endFocus -ne $homeFocus -and $wrappedFocus -eq $homeFocus)
    $before=Scroll-Position
    [void](Send $script:window 0x0100 0x22)
    Wait-Until { (Scroll-Position) -gt $before } { 'pos=' + (Scroll-Position) + ' before=' + $before }
    Assert-Host 'PageDown still scrolls the document while the toolbar keeps focus' ((Focus-Part) -eq $homeFocus)
    [void](Send $script:window 0x0100 0x2E)
    Assert-Host 'Delete on the toolbar does not close the document' ((Focus-Part) -eq $homeFocus -and (Text-Of $script:window).StartsWith('A&B.md'))
    [void](Send $script:window 0x0100 0x1B)
    Wait-Until { (Text-Of $script:window) -notmatch $prefix }

    $items=Locate-Items
    Write-Output ('LOCATE_ITEMS='+($items -join ' | '))
    $pairs=@(@(('one'+$sep+'README'),$readme1),@(('two'+$sep+'README'),$readme2),@('notes',$notes),@('A&B',$amp))
    $missing=@($pairs | Where-Object { $items -notcontains ($_[0]+' '+[char]0x2014+' '+$_[1]) })
    Assert-Host 'locate list shows display name with the full original path' ($items.Count -eq 4 -and $missing.Count -eq 0)
    Assert-Host 'locate cancel keeps the active document' ((Text-Of $script:window).StartsWith('A&B.md'))

    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](760*$script:scale),[int](800*$script:scale),4)
    [void](Send $script:window 0x0200 0 -1)
    Capture 'b3-narrow'
    Command 1010
    Wait-Until { (Text-Of $script:window).StartsWith('notes.MD') }
    Capture 'b3-switched'
    Assert-Host 'switching tabs keeps original title and identity' ((Text-Of $script:window).StartsWith('notes.MD'))

    Assert-Host 'no fixture was written' (@($files | Where-Object { [IO.File]::ReadAllText($_) -ne $sources[$_] }).Count -eq 0)
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) {throw 'Tab-name host did not exit.'}
    Assert-Host 'tab-name host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST TAB NAMES SMOKE: $script:passed PASS (owned messages; isolated documents; no real keyboard or settings changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ('title=' + (Text-Of $script:window)); Capture 'b3-failure' }
    throw
} finally {
    if ($app) {if (-not $app.HasExited) {$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    $env:MDV_CONFIG_DIR=$oldConfig
    if (Test-Path -LiteralPath $fixture) {Remove-Item -LiteralPath $fixture -Recurse -Force}
}
