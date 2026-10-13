param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'A viewer is already running; test will not touch it.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OverlayMenuProbe {
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuStringW(IntPtr menu, uint position, StringBuilder text, int length, uint flags);
}
'@
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-overlay-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$app = $null; $script:passed = 0
function Metrics {
    $r = New-Object HostNative+RECT
    [void][HostNative]::GetWindowRect($script:window, [ref]$r)
    return "$($r.R-$r.L),$($r.B-$r.T),$(Scroll-Query 1),$(Scroll-Query 2),$(Scroll-Query 0)"
}
function Body-Hash([string]$Name, [int]$LeftDip = 240) {
    Capture $Name
    $image = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name + '.png')))
    $stream = New-Object IO.MemoryStream
    $sha = [Security.Cryptography.SHA256]::Create(); $crop = $null
    try {
        # Compare only uncovered body pixels, excluding toolbar, both overlays and scrollbar (A3 D4: the two-row narrow find strip ends at 116 DIP).
        $x = [int]($LeftDip * $scale); $y = [int](120 * $scale)
        $rect = New-Object Drawing.Rectangle $x, $y, ($image.Width-$x-[int](24*$scale)), ($image.Height-$y-8)
        $crop = $image.Clone($rect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $crop.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return [Convert]::ToBase64String($sha.ComputeHash($stream.ToArray()))
    } finally { if ($crop) { $crop.Dispose() }; $sha.Dispose(); $stream.Dispose(); $image.Dispose() }
}
function Resize-Dip([int]$Width) {
    [void](Send $script:window 0x0231)
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, [int]($Width*$scale), [int](600*$scale), 4)
    [void](Send $script:window 0x0232)
    [void](Send $script:window 0x0100 0x24)
}
function Menu-Paths([IntPtr]$Menu, [string]$Prefix) {
    for ($i=0; $i -lt [OverlayMenuProbe]::GetMenuItemCount($Menu); $i++) {
        $text = New-Object Text.StringBuilder 256
        [void][OverlayMenuProbe]::GetMenuStringW($Menu, $i, $text, 256, 0x400)
        $path = $Prefix + '/' + $text.ToString()
        $child = [OverlayMenuProbe]::GetSubMenu($Menu, $i)
        if ($child -ne [IntPtr]::Zero) { Menu-Paths $child $path }
        else {
            $id = [OverlayMenuProbe]::GetMenuItemID($Menu, $i)
            if ($id -ne [uint32]::MaxValue) { $script:menuPaths[[int]$id] = $path }
        }
    }
}
try {
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,820,600,0")
    $path = Join-Path $fixture 'overlay.md'
    $source = "# Start`n`n" + ("Reading text with enough words to wrap consistently across multiple lines. 正文位置应当保持稳定。`n`n" * 35) + "# Destination`n`nEnd.`n"
    [IO.File]::WriteAllText($path, $source)
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"'+$path+'"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Overlay host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    # A test process started hidden has its first ShowWindow replaced by SW_HIDE (STARTUPINFO); retry until visible.
    Wait-Until { [void][HostNative]::ShowWindow($script:window, 4); [HostNative]::IsWindowVisible($script:window) }
    $scale = [HostNative]::GetDpiForWindow($script:window)/96.0
    # S81: a pinned directory follows the window width. It folds away when the window
    # becomes too narrow to pin it (no overlay left over the body), comes back when the
    # window is wide again, and is restored at startup, because toc=1 means "pinned when
    # wide". Runs first: no jump has happened yet, so no timed return hint is on screen.
    Resize-Dip 700
    $narrow = Body-Hash 'pin-700' 0
    Resize-Dip 1200
    $wide = Body-Hash 'pin-1200-closed' 0
    Command 1006
    $pinned = Body-Hash 'pin-1200-open' 0
    Assert-Host 'wide window pins the directory beside the body' ($pinned -ne $wide)
    Resize-Dip 700
    Assert-Host 'narrowing folds the pinned directory away' ((Body-Hash 'pin-700-after' 0) -eq $narrow)
    Resize-Dip 1200
    Assert-Host 'widening again restores the pinned directory' ((Body-Hash 'pin-1200-again' 0) -eq $pinned)
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(5000)) { throw 'Overlay host did not exit before restart.' }
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"'+$path+'"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Overlay host exited during restart.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    Wait-Until { [void][HostNative]::ShowWindow($script:window, 4); [HostNative]::IsWindowVisible($script:window) }
    Assert-Host 'startup restores the pinned directory' ((Body-Hash 'pin-restart' 0) -eq $pinned)
    foreach ($width in @(820, 540)) {
        [void](Send $script:window 0x0231)
        [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, [int]($width*$scale), [int](600*$scale), 4)
        [void](Send $script:window 0x0232)
        [void](Send $script:window 0x0100 0x22)
        $before = Body-Hash "$width-before"; $metrics = Metrics
        Command 1005
        $find = [HostNative]::GetDlgItem($script:window, 3011)
        Assert-Host "$width find is visible" ([HostNative]::IsWindowVisible($find))
        Assert-Host "$width find preserves body pixels" ((Body-Hash "$width-find") -eq $before)
        Assert-Host "$width find preserves viewport and scroll" ((Metrics) -eq $metrics)
        Command 1006
        Assert-Host "$width combined overlays preserve body pixels" ((Body-Hash "$width-both") -eq $before)
        Assert-Host "$width directory preserves geometry and scroll" ((Metrics) -eq $metrics)
        [void](Send $script:window 0x0111 3005)
        Assert-Host "$width closing find preserves body pixels" ((Body-Hash "$width-toc") -eq $before)
        # The second directory entry must jump even in a manually narrowed window.
        $xy = ([int](78*$scale) -shl 16) -bor [int](30*$scale)
        [void](Send-Physical $script:window 0x0201 1 $xy)
        [void](Send-Physical $script:window 0x0202 0 $xy)
        Assert-Host "$width directory entry remains clickable" ((Metrics) -ne $metrics)
        Command 1006
        [void](Send $script:window 0x0100 0x1B)
        $afterEscape = Body-Hash "$width-escape"
        Command 1006
        Assert-Host "$width reopening directory leaves body unchanged" ((Body-Hash "$width-reopened") -eq $afterEscape)
        Command 1006
        [void](Send $script:window 0x0100 0x24)
    }
    Command 1005
    $setResult = [IntPtr]::Zero
    if ([HostNative]::SendText($find, 0x000C, [IntPtr]::Zero, 'Start', 2, 4000, [ref]$setResult) -eq [IntPtr]::Zero) { throw 'Setting the search query timed out.' }
    Assert-Host 'first-heading search selects a real match' ((Text-Of $find) -eq 'Start' -and (Text-Of ([HostNative]::GetDlgItem($script:window, 3012))) -eq '1/1')
    Capture 'first-heading-search'
    $next = [HostNative]::GetDlgItem($script:window, 3004)
    $controlRect = New-Object HostNative+RECT; $origin = New-Object HostNative+POINT
    [void][HostNative]::GetWindowRect($next, [ref]$controlRect)
    [void][HostNative]::ClientToScreen($script:window, [ref]$origin)
    Assert-Host 'search controls leave the first heading visible at scroll origin' (($controlRect.B-$origin.Y) -lt [int](74*$scale))
    [void](Send $script:window 0x0111 3005)
    [void][HostNative]::PostPhysical($script:window, 0x007B, $script:window, [IntPtr](-1))
    Wait-Until { [HostNative]::Window([uint32]$app.Id, '#32768') -ne [IntPtr]::Zero }
    $popup = [HostNative]::Window([uint32]$app.Id, '#32768')
    $script:menuPaths = @{}
    Menu-Paths ([IntPtr](Send $popup 0x01E1)) ''
    Assert-Host 'cache tools are under maintenance' ($script:menuPaths[2243] -eq '/设置与维护/恢复、历史与缓存/图片缓存…')
    Assert-Host 'tab actions remain reachable together' ($script:menuPaths[2202] -eq '/文档操作/标签/关闭其他标签')
    Assert-Host 'offline export remains reachable with printing' ($script:menuPaths[2231] -eq '/文档操作/导出与打印/导出离线 HTML…')
    [void][HostNative]::PostMessage($script:window, 0x001F, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, '#32768') -eq [IntPtr]::Zero }
    Assert-Host 'overlay navigation does not modify source' ([IO.File]::ReadAllText($path) -ceq $source -and -not (Text-Of $script:window).StartsWith('*'))
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(5000)) { throw 'Overlay host did not exit.' }
    Assert-Host 'overlay host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST OVERLAY SMOKE: $script:passed PASS"
} finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(4000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
