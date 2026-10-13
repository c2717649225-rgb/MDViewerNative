param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory)
# A3 acceptance 2: overlays must not let clicks through. Owned window messages only (no real input, no focus steal).
# Every "blocked" click is followed by a control click at the same point once the overlay is gone, which must
# follow the link underneath; this proves the point really covers link text.
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'A viewer is already running; test will not touch it.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'work\overlay-hit-smoke' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-hit-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$a = Join-Path $fixture 'hit-alpha.md'; $b = Join-Path $fixture 'hit-bravo.md'
[IO.File]::WriteAllText($a, '[' + (('follow this link ' * 160).Trim()) + '](hit-bravo.md)' + "`n")
[IO.File]::WriteAllText($b, "# Bravo`n`nArrived.`n")
$script:passed = 0; $app = $null
function Launch {
    if ($script:app -and -not $script:app.HasExited) { Stop-Process -Id $script:app.Id -Force; [void]$script:app.WaitForExit(4000) }
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=100,100,900,700,0`n")
    $script:app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $a + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($script:app.HasExited) { throw 'Host exited during startup.' }; [HostNative]::Window([uint32]$script:app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$script:app.Id, 'MDViewerNative')
    # A test process started hidden has its first ShowWindow replaced by SW_HIDE (STARTUPINFO); retry until visible.
    Wait-Until { [void][HostNative]::ShowWindow($script:window, 4); [HostNative]::IsWindowVisible($script:window) }
    Wait-Until { (Text-Of $script:window).Contains('hit-alpha.md') }
    $script:scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
    # The saved window rectangle is not honoured for this size; set the client explicitly (panel needs V >= 720, a transient directory needs colX < 256).
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 100, 100, [int](800 * $script:scale), [int](700 * $script:scale), 0x14)   # 800 DIP: floating find panel (>= 720) and a transient directory (< 816, S80)
    $cr = New-Object HostNative+RECT; [void][HostNative]::GetClientRect($script:window, [ref]$cr); $script:viewDip = $cr.R / $script:scale
    Start-Sleep -Milliseconds 300
}
function Click-Px([int]$X, [int]$Y) { $xy = ($Y -shl 16) -bor ($X -band 0xffff); [void](Send-Physical $script:window 0x0200 0 $xy); [void](Send-Physical $script:window 0x0201 1 $xy); [void](Send-Physical $script:window 0x0202 0 $xy) }
function Stayed { Start-Sleep -Milliseconds 700; (Text-Of $script:window).Contains('hit-alpha.md') }
function Followed { try { Wait-Until { (Text-Of $script:window).Contains('hit-bravo.md') }; $true } catch { $false } }
function Client-Of([IntPtr]$Control) {
    $r = New-Object HostNative+RECT; [void][HostNative]::GetWindowRect($Control, [ref]$r)
    $o = New-Object HostNative+POINT; [void][HostNative]::ClientToScreen($script:window, [ref]$o)
    $r.L -= $o.X; $r.R -= $o.X; $r.T -= $o.Y; $r.B -= $o.Y; return $r
}
try {
    # 1. The gap between two panel buttons.
    Launch
    # Scroll so link text runs under the panel (the document top is padding).
    [void](Send $script:window 0x0100 0x22); [void](Send $script:window 0x0101 0x22); Start-Sleep -Milliseconds 300
    Command 1005
    $query = [HostNative]::GetDlgItem($script:window, 3011)
    $r0 = [IntPtr]::Zero; [void][HostNative]::SendText($query, 0x000C, [IntPtr]::Zero, 'zzzz', 2, 4000, [ref]$r0)
    Start-Sleep -Milliseconds 300
    $prev = Client-Of ([HostNative]::GetDlgItem($script:window, 3003)); $next = Client-Of ([HostNative]::GetDlgItem($script:window, 3004))
    Assert-Host 'wide window shows the floating panel with a gap between buttons' ($script:viewDip -ge 720 -and $next.L - $prev.R -ge 2 -and $prev.T -eq $next.T -and $prev.T -eq [int](48 * $script:scale))
    $gx = [int](($prev.R + $next.L) / 2); $gy = [int](($prev.T + $prev.B) / 2)
    Capture 'hit-1-panel'
    Click-Px $gx $gy
    Assert-Host 'click in a find panel gap does not follow the link underneath' (Stayed)
    Assert-Host 'click in a find panel gap leaves find open' ([HostNative]::IsWindowVisible($query))
    [void](Send $script:window 0x0111 3005)
    Start-Sleep -Milliseconds 300
    Click-Px $gx $gy
    Assert-Host 'control: the same point follows the link once find is closed' (Followed)

    # 2. Inside the transient directory, over body text.
    Launch
    Command 1006
    Start-Sleep -Milliseconds 300
    $tx = [int](200 * $script:scale); $ty = [int](300 * $script:scale)
    Capture 'hit-2-toc'
    Click-Px $tx $ty
    Assert-Host 'click inside the transient directory does not follow the link underneath' (Stayed)
    Command 1006
    Start-Sleep -Milliseconds 300
    Click-Px $tx $ty
    Assert-Host 'control: the same point follows the link once the directory is closed' (Followed)

    # 3. Outside the transient directory: the click only closes it.
    Launch
    Command 1006
    Start-Sleep -Milliseconds 300
    $ox = [int](500 * $script:scale); $oy = [int](300 * $script:scale)
    Click-Px $ox $oy
    Assert-Host 'outside click closes the transient directory without following the link' (Stayed)
    Capture 'hit-3-after-outside'
    Click-Px $ox $oy
    Assert-Host 'control: the next click at the same point follows the link' (Followed)

    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $script:app.WaitForExit(5000)) { throw 'Host did not exit.' }
    Write-Output "HOST OVERLAY HIT SMOKE: $script:passed PASS (owned messages; isolated documents; no real keyboard, mouse or settings changes)"
} finally {
    if ($script:app -and -not $script:app.HasExited) { Stop-Process -Id $script:app.Id -Force; [void]$script:app.WaitForExit(4000) }
    $env:MDV_CONFIG_DIR = $previousConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
