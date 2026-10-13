param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$FixturePath, [switch]$IncludeBottom)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MDViewerNative -ErrorAction SilentlyContinue) { throw 'Close MDViewerNative before this owned-window capture.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-container-' + [guid]::NewGuid().ToString('N'))
$previousConfig = $env:MDV_CONFIG_DIR
$app = $null
try {
    $env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
    [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,960,800,0")
    $path = Join-Path $fixture 'container-review.md'
    if (-not $FixturePath) { $FixturePath = Join-Path $PSScriptRoot '../docs/render-fixtures/containers.md' }
    Copy-Item $FixturePath $path
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Capture host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MDViewerNative') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MDViewerNative')
    [void][HostNative]::ShowWindow($script:window, 5)
    Capture 'containers-wide'
    if ($IncludeBottom) {
        [void][HostNative]::PostMessage($script:window, 0x020A, [IntPtr](-3000 -shl 16), [IntPtr]::Zero)
        Capture 'containers-wide-bottom'
        [void][HostNative]::PostMessage($script:window, 0x020A, [IntPtr](3000 -shl 16), [IntPtr]::Zero)
    }
    $dpi = [HostNative]::GetDpiForWindow($script:window)
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, [int](540 * $dpi / 96), [int](800 * $dpi / 96), 4)
    Capture 'containers-narrow'
    if ($IncludeBottom) {
        [void][HostNative]::PostMessage($script:window, 0x020A, [IntPtr](-3000 -shl 16), [IntPtr]::Zero)
        Capture 'containers-narrow-bottom'
    }
    $info = [ordered]@{ dpi=$dpi; executableSha256=(Get-FileHash $ExePath -Algorithm SHA256).Hash.ToLowerInvariant(); fixtureSha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant(); purpose='Owned-window captures for manual comparison, not a complete visual pass' }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'capture.json'), ($info | ConvertTo-Json))
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) { throw 'Capture host failed to close.' }
    Write-Output 'Container captures completed (manual review required).'
} finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    $env:MDV_CONFIG_DIR = $previousConfig
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
