param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'An existing reader must be left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
# A2-B4 and the 36b570b double-click fix in the real host: posted toolbar clicks
# (single/double/triple), the hidden-dirty document list entry at the reachable
# minimum width, and a native read of the More popup compared with menu_golden.
# Owned window messages only; no real keyboard or mouse, isolated config and docs.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class MenuProbe {
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
    [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr m, uint id, uint flags);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr m, int pos);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr m, uint id, StringBuilder s, int n, uint flags);
}
'@
function U([string]$s) { return [regex]::Unescape($s) }
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-toolbar-fold-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0
$app=$null
$toolbarPrefix='['+(U '\u5DE5\u5177\u680F\uFF1A')
$descPattern=[regex]::Escape($toolbarPrefix)+'(?<d>[^\]]+)\]'
$tabLabel=U '\u6807\u7B7E\uFF1A'
$unsaved=U '\u672A\u4FDD\u5B58'
$docsHidden=(U '\u6587\u6863\u5217\u8868')+(U '\uFF08\u542B\u672A\u4FDD\u5B58\u7684\u9690\u85CF\u6587\u6863\uFF09')
$docsLabel=U '\u6587\u6863\u5217\u8868'
$moreLabel=U '\u66F4\u591A'
function Handoff([string]$Path) {
    $p=Start-Process -FilePath $ExePath -ArgumentList ('"'+$Path+'"') -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(8000)) { throw 'Fixture handoff did not exit.' }
    $p.Dispose()
    $leaf=[IO.Path]::GetFileName($Path)
    Wait-Until { (Text-Of $script:window).StartsWith($leaf) } { 'title=' + (Text-Of $script:window) }
}
function Post-Pointer([uint32]$Message,[int]$X,[int]$Y) {
    $w=if ($Message -eq 0x0202) {0} else {1}
    $l=($Y -shl 16) -bor ($X -band 0xFFFF)
    if (-not [HostNative]::PostPhysical($script:window,$Message,[IntPtr]$w,[IntPtr]$l)) { throw 'Pointer post failed.' }
}
function Click-Edit([uint32[]]$Messages) { foreach ($m in $Messages) { Post-Pointer $m $script:editX $script:editY } }
function Editor-Visible {
    $e=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    return ($e -ne [IntPtr]::Zero -and [HostNative]::IsWindowVisible($e))
}
function Focus-Descriptions {
    # F6 is routed by the message loop, so it must be posted; arrows are handled by WndProc.
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x75,[IntPtr]::Zero)
    Wait-Until { (Text-Of $script:window).Contains($toolbarPrefix) } { 'title=' + (Text-Of $script:window) }
    $found=New-Object System.Collections.Generic.List[string]
    for ($i=0; $i -lt 30; $i++) {
        if ((Text-Of $script:window) -match $descPattern -and -not $found.Contains($Matches['d'])) { $found.Add($Matches['d']) }
        [void](Send $script:window 0x0100 0x27)
    }
    [void](Send $script:window 0x0100 0x1B)
    Wait-Until { -not (Text-Of $script:window).Contains($toolbarPrefix) }
    return ,@($found.ToArray())
}
function Menu-Window {
    $h=[IntPtr]::Zero
    while ($true) {
        $h=[HostNative]::FindWindowEx([IntPtr]::Zero,$h,'#32768',$null)
        if ($h -eq [IntPtr]::Zero) { return $h }
        $procId=[uint32]0
        [void][HostNative]::GetWindowThreadProcessId($h,[ref]$procId)
        if ($procId -eq $app.Id -and [HostNative]::IsWindowVisible($h)) { return $h }
    }
}
function Read-MoreMenu {
    # The popup can be dismissed on its own when the reader is not the foreground
    # window; retry a bounded number of times and report how many were needed.
    for ($attempt=1; $attempt -le 3; $attempt++) {
        $items=Try-ReadMoreMenu
        if ($null -ne $items) { $script:moreAttempts=$attempt; return ,$items }
        Start-Sleep -Milliseconds 500
    }
    throw 'More popup could not be read in 3 attempts.'
}
function Try-ReadMoreMenu {
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x75,[IntPtr]::Zero)
    Wait-Until { (Text-Of $script:window).Contains($toolbarPrefix) }
    for ($i=0; $i -lt 30 -and -not (Text-Of $script:window).Contains($toolbarPrefix+$moreLabel+']'); $i++) { [void](Send $script:window 0x0100 0x27) }
    if (-not (Text-Of $script:window).Contains($toolbarPrefix+$moreLabel+']')) { throw ('More never took focus: ' + (Text-Of $script:window)) }
    # The popup is modal inside the reader, so Enter must be posted.
    [void][HostNative]::PostMessage($script:window,0x0100,[IntPtr]0x0D,[IntPtr]::Zero)
    Wait-Until { (Menu-Window) -ne [IntPtr]::Zero }
    $menuWindow=Menu-Window
    $raw=[IntPtr]::Zero
    $ok=[HostNative]::Send($menuWindow,0x01E1,[IntPtr]::Zero,[IntPtr]::Zero,2,2000,[ref]$raw) -ne [IntPtr]::Zero   # MN_GETHMENU
    if (-not $ok -or $raw -eq [IntPtr]::Zero) {
        if ((Menu-Window) -ne [IntPtr]::Zero) { [void][HostNative]::PostMessage((Menu-Window),0x0100,[IntPtr]0x1B,[IntPtr]::Zero) }
        Wait-Until { (Menu-Window) -eq [IntPtr]::Zero }
        if ((Text-Of $script:window).Contains($toolbarPrefix)) { [void](Send $script:window 0x0100 0x1B) }
        Wait-Until { -not (Text-Of $script:window).Contains($toolbarPrefix) }
        return $null
    }
    $hm=$raw
    $items=@()
    $count=[MenuProbe]::GetMenuItemCount($hm)
    for ($i=0; $i -lt $count; $i++) {
        $st=[MenuProbe]::GetMenuState($hm,[uint32]$i,0x400)
        $sb=New-Object Text.StringBuilder 256
        [void][MenuProbe]::GetMenuString($hm,[uint32]$i,$sb,256,0x400)
        # For a submenu GetMenuState returns (child count << 8) | flags, so a submenu
        # with eight or more children also has bit 0x800 set: test MF_POPUP first.
        if ($st -band 0x10) { $items+=('>'+$sb.ToString()); continue }
        if ($st -band 0x800) { $items+='---'; continue }
        $items+=([string][MenuProbe]::GetMenuItemID($hm,$i))+'|'+$sb.ToString()
    }
    [void][HostNative]::PostMessage($menuWindow,0x0100,[IntPtr]0x1B,[IntPtr]::Zero)
    Wait-Until { (Menu-Window) -eq [IntPtr]::Zero }
    if ((Text-Of $script:window).Contains($toolbarPrefix)) { [void](Send $script:window 0x0100 0x1B) }
    Wait-Until { -not (Text-Of $script:window).Contains($toolbarPrefix) }
    return ,$items
}
function Golden-More {
    $lines=[IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'menu_golden.txt'),[Text.Encoding]::UTF8)
    $inMore=$false; $expected=@()
    foreach ($line in $lines) {
        $t=$line.Trim()
        if ($t -eq '[More]') { $inMore=$true; continue }
        if (-not $inMore) { continue }
        if ($t.StartsWith('[')) { break }
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        if ($t -eq '---') { $expected+='---'; continue }
        $pair=$t.Split('|',2)
        $parts=[regex]::Split($pair[1].Trim(),'\t+\s*|\s{2,}')
        $label=$parts[0]
        if ($pair[0] -eq '1004') { $label=U '\u5207\u6362\u5230\u6DF1\u8272\u4E3B\u9898' }   # light theme
        if ($parts.Count -gt 1) { $label+="`t"+$parts[1] }
        $expected+=($pair[0]+'|'+$label)
    }
    return ,$expected
}
try {
    $alpha=Join-Path $fixture 'alpha.md'; $bravo=Join-Path $fixture 'bravo.md'
    $files=@($alpha,$bravo); $sources=@{}
    foreach ($f in $files) { $s="# "+[IO.Path]::GetFileName($f)+"`n`nFixture body.`n"; [IO.File]::WriteAllText($f,$s); $sources[$f]=$s }
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,800,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$alpha+'"') -PassThru
    Wait-Until { if ($app.HasExited) {throw 'Fold host exited.'}; [HostNative]::Window([uint32]$app.Id,'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MDViewerNative')
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') }
    Handoff $bravo
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("FOLD_DPI="+[HostNative]::GetDpiForWindow($script:window))
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](1100*$script:scale),[int](800*$script:scale),4)
    [void](Send $script:window 0x0200 0 -1)
    # Edit sits after Open: both labels are two CJK glyphs, so 84 DIP is inside Edit
    # for any plausible font; the first single click below validates the coordinate.
    $script:editX=[int](84*$script:scale); $script:editY=[int](18*$script:scale)

    Assert-Host 'starts in preview' (-not (Editor-Visible))
    Click-Edit @(0x0201,0x0202)
    Wait-Until { Editor-Visible }
    Assert-Host 'posted single click on Edit enters edit mode (coordinate check)' (Editor-Visible)
    Click-Edit @(0x0201,0x0202)
    Wait-Until { -not (Editor-Visible) }
    Assert-Host 'second single click returns to preview' (-not (Editor-Visible))
    # Real rapid clicks arrive as DOWN UP DBLCLK UP DOWN UP (CS_DBLCLKS).
    Click-Edit @(0x0201,0x0202,0x0203,0x0202,0x0201,0x0202)
    Start-Sleep -Milliseconds 900
    Assert-Host 'rapid triple click toggles three times (DBLCLK is not dropped)' (Editor-Visible)
    Click-Edit @(0x0201,0x0202,0x0203,0x0202)
    Start-Sleep -Milliseconds 900
    Assert-Host 'rapid double click toggles twice' (Editor-Visible)
    Click-Edit @(0x0201,0x0202)
    Wait-Until { -not (Editor-Visible) }

    Command 1010
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') } { 'title=' + (Text-Of $script:window) }
    Command 1002
    Wait-Until { Editor-Visible }
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    Edit-Text "# alpha changed`r`n"
    Command 1002
    Wait-Until { -not (Editor-Visible) }
    Command 1009
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') } { 'title=' + (Text-Of $script:window) }
    [void](Send $script:window 0x0200 0 -1)
    Capture 'b4-wide'
    $wide=Focus-Descriptions
    Write-Output ('WIDE_FOCUS='+($wide -join ' | '))
    Assert-Host 'wide: the inactive dirty tab is visible and says unsaved' (@($wide | Where-Object { $_.StartsWith($tabLabel+'alpha') -and $_.Contains($unsaved) }).Count -eq 1)
    Assert-Host 'wide: no document list entry is needed' (@($wide | Where-Object { $_.StartsWith($docsLabel) }).Count -eq 0)

    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](480*$script:scale),[int](800*$script:scale),4)
    [void](Send $script:window 0x0200 0 -1)
    $cr=New-Object HostNative+RECT
    [void][HostNative]::GetClientRect($script:window,[ref]$cr)
    Write-Output ('NARROW_CLIENT_DIP='+[Math]::Round(($cr.R-$cr.L)/$script:scale,1))
    Capture 'b4-narrow'
    $narrow=Focus-Descriptions
    Write-Output ('NARROW_FOCUS='+($narrow -join ' | '))
    Assert-Host 'narrow: document list entry reports the hidden unsaved document' ($narrow -contains $docsHidden)
    Assert-Host 'narrow: hidden dirty tab left the focus sequence' (@($narrow | Where-Object { $_.StartsWith($tabLabel+'alpha') }).Count -eq 0)
    Assert-Host 'narrow: the active tab stays visible' (@($narrow | Where-Object { $_.StartsWith($tabLabel+'bravo') }).Count -eq 1)
    Assert-Host 'narrow: More carries no unsaved marker for a clean active document' ($narrow -contains $moreLabel)

    $items=Read-MoreMenu
    Write-Output ('MORE_READ_ATTEMPTS='+$script:moreAttempts)
    Write-Output ('MORE_ITEMS='+(($items | ForEach-Object { $_.Replace("`t",' <TAB> ') }) -join ' | '))
    $expected=Golden-More
    $head=@($items | Select-Object -First $expected.Count)
    $diff=@(for ($i=0; $i -lt $expected.Count; $i++) { if ($head[$i] -ne $expected[$i]) { "#$i expected=[$($expected[$i])] actual=[$($head[$i])]" } })
    if ($diff.Count) { Write-Output ('MORE_DIFF='+($diff -join ' ; ')) }
    Assert-Host 'More menu starts exactly like menu_golden (nothing folded at the reachable minimum width)' ($expected.Count -gt 10 -and $diff.Count -eq 0)

    Command 1010
    # A dirty document's window title is prefixed with '*'.
    Wait-Until { (Text-Of $script:window).StartsWith('*alpha.md') } { 'title=' + (Text-Of $script:window) }
    Assert-Host 'window title marks the hidden draft as unsaved once it is active' ((Text-Of $script:window).StartsWith('*alpha.md'))
    Guarded-Command 1008 1001
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') } { 'title=' + (Text-Of $script:window) }
    Assert-Host 'no fixture was written' (@($files | Where-Object { [IO.File]::ReadAllText($_) -ne $sources[$_] }).Count -eq 0)
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) {throw 'Fold host did not exit.'}
    Assert-Host 'fold host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST TOOLBAR FOLD SMOKE: $script:passed PASS (owned messages; isolated documents; no real keyboard, mouse or settings changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ('title=' + (Text-Of $script:window)); Capture 'b4-failure' }
    throw
} finally {
    if ($app) {if (-not $app.HasExited) {$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    $env:MDV_CONFIG_DIR=$oldConfig
    if (Test-Path -LiteralPath $fixture) {Remove-Item -LiteralPath $fixture -Recurse -Force}
}
