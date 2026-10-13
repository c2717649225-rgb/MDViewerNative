param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory)
# A4 E5: code block copy button. Owned window messages only (no real keyboard or mouse, no focus steal).
# The clipboard is snapshotted first and restored afterwards (a newer external clipboard is never overwritten).
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'A viewer is already running; test will not touch it.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'ClipboardTestSupport.ps1')
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'work\code-copy-smoke' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-copy-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$previousConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$doc = Join-Path $fixture 'copy-alpha.md'
$fence = '```'
$source = "# Copy`n`nIntro paragraph.`n`n${fence}python`nprint(1)`n$fence`n`nAfter the code.`n"
[IO.File]::WriteAllText($doc, $source)
$script:passed = 0; $app = $null; $snapshot = $null; $touched = $false; $lastSequence = 0
function Xy([int]$X, [int]$Y) { return ($Y -shl 16) -bor ($X -band 0xffff) }
function Move-Px([int]$X, [int]$Y) { [void](Send-Physical $script:window 0x0200 0 (Xy $X $Y)); Start-Sleep -Milliseconds 120 }
function Click-Px([int]$X, [int]$Y) { $xy = Xy $X $Y; [void](Send-Physical $script:window 0x0200 0 $xy); [void](Send-Physical $script:window 0x0201 1 $xy); [void](Send-Physical $script:window 0x0202 0 $xy) }
function Shot([string]$Name) {
    Capture $Name
    $path = Join-Path $OutputDirectory ($Name + '.png')
    $bytes = [IO.File]::ReadAllBytes($path)
    $stream = New-Object IO.MemoryStream (, $bytes)
    return New-Object Drawing.Bitmap $stream
}
$surface = [Drawing.Color]::FromArgb(255, 0xEF, 0xEC, 0xE5)
function Is-Surface($c) { return $c.R -eq $surface.R -and $c.G -eq $surface.G -and $c.B -eq $surface.B }
function Ink([Drawing.Bitmap]$Bitmap) {
    $n = 0
    for ($y = $script:chip.T + 2; $y -lt $script:chip.B - 2; $y++) { for ($x = $script:chip.L + 2; $x -lt $script:chip.R - 2; $x++) { if (-not (Is-Surface $Bitmap.GetPixel($x, $y))) { $n++ } } }
    return $n
}
function Chip-Diff([Drawing.Bitmap]$A, [Drawing.Bitmap]$B) {
    $n = 0
    for ($y = $script:chip.T + 2; $y -lt $script:chip.B - 2; $y++) { for ($x = $script:chip.L + 2; $x -lt $script:chip.R - 2; $x++) { if ($A.GetPixel($x, $y).ToArgb() -ne $B.GetPixel($x, $y).ToArgb()) { $n++ } } }
    return $n
}
try {
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=100,100,1000,700,0`n")
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $doc + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    Wait-Until { [void][HostNative]::ShowWindow($script:window, 4); [HostNative]::IsWindowVisible($script:window) }
    Wait-Until { (Text-Of $script:window).Contains('copy-alpha.md') }
    $script:scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 100, 100, [int](1000 * $script:scale), [int](700 * $script:scale), 0x14)
    Start-Sleep -Milliseconds 300

    # Locate the code panel from pixels: a Surface run down the client centre, then its right edge.
    # Copy chip is a 22x18 DIP icon button, 6 DIP in from the panel's right edge (DocView.CodeChipW).
    $rest = Shot 'copy-0-rest'
    $cx = [int]($rest.Width / 2); $top = -1; $bottom = -1
    for ($y = [int](40 * $script:scale); $y -lt $rest.Height; $y++) { if (Is-Surface $rest.GetPixel($cx, $y)) { if ($top -lt 0) { $top = $y }; $bottom = $y } elseif ($top -ge 0) { break } }
    $right = -1
    if ($top -ge 0) { for ($x = $rest.Width - 1; $x -gt $cx; $x--) { if (Is-Surface $rest.GetPixel($x, $top + [int](10 * $script:scale))) { $right = $x; break } } }
    Assert-Host 'code panel located in the reading view' ($top -gt 0 -and $bottom -gt $top + 20 -and $right -gt $cx)
    $script:chip = @{ L = [int]($right + 1 - 28 * $script:scale); R = [int]($right + 1 - 6 * $script:scale); T = [int]($top + 2 * $script:scale); B = [int]($top + 20 * $script:scale) }
    $chipX = [int](($script:chip.L + $script:chip.R) / 2); $chipY = [int](($script:chip.T + $script:chip.B) / 2)
    Write-Output ("panel top={0} bottom={1} right={2} chip={3},{4}-{5},{6}" -f $top, $bottom, $right, $script:chip.L, $script:chip.T, $script:chip.R, $script:chip.B)
    Assert-Host 'copy button is hidden at rest' ((Ink $rest) -eq 0)

    $panelY = [int](($top + $bottom) / 2)
    Move-Px $cx $panelY
    $hover = Shot 'copy-1-hover'
    Assert-Host 'hovering the code block shows the copy button' ((Ink $hover) -gt 30)

    $snapshot = [ClipboardSnapshot]::Capture($script:window)
    $touched = $true
    try { Click-Px $chipX $chipY; Start-Sleep -Milliseconds 200 } finally { $lastSequence = [ClipboardSnapshot]::GetClipboardSequenceNumber() }
    $text = [ClipboardSnapshot]::Text($script:window)
    Assert-Host 'clicking the button copies the block source' ($null -ne $text -and $text.TrimEnd("`n") -ceq 'print(1)')
    $copied = Shot 'copy-2-copied'
    Write-Output ("copied ink={0} diff={1}" -f (Ink $copied), (Chip-Diff $hover $copied))
    Assert-Host 'the button then reads as copied' ((Ink $copied) -gt 30 -and (Chip-Diff $hover $copied) -gt 10)
    Assert-Host 'copy leaves the file and dirty state untouched' ([IO.File]::ReadAllText($doc) -ceq $source -and -not (Text-Of $script:window).StartsWith('*'))

    Move-Px ([int](10 * $script:scale)) ([int](300 * $script:scale))
    $left = Shot 'copy-3-left'
    Assert-Host 'leaving the block hides the button' ((Ink $left) -eq 0)
    Move-Px $cx $panelY
    $again = Shot 'copy-4-again'
    Assert-Host 'hovering again shows the plain copy label' ((Chip-Diff $hover $again) -le 4)

    $restored = $snapshot.Restore($script:window, $lastSequence); $touched = $false
    Write-Output ("Clipboard snapshot restored=" + $restored + " (a newer external clipboard is never overwritten).")
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(5000)) { throw 'Host did not exit.' }
    Write-Output "HOST CODE COPY SMOKE: $script:passed PASS (owned messages; isolated documents; clipboard restored; no real keyboard, mouse or settings changes)"
} finally {
    try {
        if ($snapshot -and $touched) {
            $owner = if ($app -and -not $app.HasExited) { $script:window } else { [IntPtr]::Zero }
            $restored = $snapshot.Restore($owner, $lastSequence)
            Write-Output ("Clipboard snapshot restored=" + $restored + " (a newer external clipboard is never overwritten).")
        }
    } finally {
        if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; [void]$app.WaitForExit(4000) }
        $env:MDV_CONFIG_DIR = $previousConfig
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
