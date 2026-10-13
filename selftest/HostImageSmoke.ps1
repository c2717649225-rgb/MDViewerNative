param([Parameter(Mandatory=$true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before image tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\image-host-' + [guid]::NewGuid().ToString('N'))
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
$OutputDirectory = Join-Path $root 'work\maturity\reader-images-host-images'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
try {
    $env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
    [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "theme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,960,800,0")
    $bitmap = New-Object Drawing.Bitmap 120,60
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.Clear([Drawing.Color]::Magenta); $bitmap.Save((Join-Path $fixture 'test image.png')) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    $path = Join-Path $fixture 'rich.md'
    [IO.File]::WriteAllText($path, "- # Nested content`n`n  ![local](test%20image.png)`n`n  | key | value |`n  | --- | --- |`n  | alpha | bravo |`n`n![remote](https://example.invalid/never-requested.png)`n")
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Image host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('rich.md') }
    [void][HostNative]::ShowWindow($script:window, 4)
    Capture 'nested-image-table'
    $render = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'nested-image-table.png'))
    $point = $null
    try {
        for ($y=0; $y -lt $render.Height -and $null -eq $point; $y+=3) {
            for ($x=0; $x -lt $render.Width; $x+=3) {
                $color = $render.GetPixel($x,$y)
                if ($color.R -gt 240 -and $color.B -gt 240 -and $color.G -lt 20) { $point=@($x,$y); break }
            }
        }
    } finally { $render.Dispose() }
    Assert-Host 'nested encoded image is actually painted' ($null -ne $point)
    Assert-Host 'opening remote placeholder creates no download cache' (-not (Test-Path (Join-Path $env:MDV_CONFIG_DIR 'image-cache')))
    $click = (([int]$point[1] + 6) -shl 16) -bor ([int]$point[0] + 6)
    [void](Send-Physical $script:window 0x0201 1 $click)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -ne [IntPtr]::Zero }
    $lightbox = [HostNative]::Window([uint32]$app.Id, 'MDLightbox')
    Assert-Host 'nested image opens owned lightbox' ($lightbox -ne [IntPtr]::Zero)
    [void][HostNative]::PostMessage($lightbox, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MDLightbox') -eq [IntPtr]::Zero }
    Assert-Host 'lightbox closes without losing document' ((Text-Of $script:window).Contains('rich.md'))
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    Assert-Host 'rich native layout does not alter Markdown source' ((Text-Of $script:editor).Contains('test%20image.png') -and -not (Text-Of $script:window).StartsWith('*'))
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) { throw 'Image host did not exit.' }
    Assert-Host 'image host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST IMAGE SMOKE: $script:passed PASS (owned messages; remote placeholder not clicked; no network request)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
