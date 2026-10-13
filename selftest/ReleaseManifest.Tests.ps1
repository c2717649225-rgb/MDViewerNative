param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\release-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$publish = Join-Path $fixture 'publish'
[void][IO.Directory]::CreateDirectory($publish)
$source = Join-Path $root 'engine\bin\Release\net10.0-windows'
$package = Join-Path $fixture 'candidate.zip'
$writer = Join-Path $root 'scripts\WriteReleaseManifest.ps1'
$count = 0
function Assert-Release([string]$Name, [bool]$Pass) {
    if (-not $Pass) { throw "FAIL release/$Name" }
    $script:count++
    Write-Output "PASS release/$Name"
}
try {
    Copy-Item -LiteralPath (Join-Path $source 'MDViewerNative.exe') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $publish
    Compress-Archive -LiteralPath (Join-Path $publish 'MDViewerNative.exe'), (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -DestinationPath $package
    & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish
    $manifest = Get-Content -LiteralPath ($package + '.manifest.json') -Raw | ConvertFrom-Json
    Assert-Release 'unknown checks are not claimed passed' ($manifest.checks.selftest -eq 'not-run' -and $manifest.checks.ui -eq 'not-run' -and $manifest.checks.desktopInput -eq 'not-run' -and $manifest.checks.packageContent -eq 'passed')
    Assert-Release 'package digest matches' ($manifest.package.sha256 -eq (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant())
    Assert-Release 'manifest records runtime files and notices' ($manifest.files.Count -eq 2 -and $manifest.sourceCommit -match '^[0-9a-f]{40}$')
    Assert-Release 'checksum sidecar matches' ((Get-Content -LiteralPath ($package + '.sha256') -Raw).Trim() -eq ($manifest.package.sha256 + '  candidate.zip'))
    & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish -Checks @{ desktopInput = 'passed' }
    $inputManifest = Get-Content -LiteralPath ($package + '.manifest.json') -Raw | ConvertFrom-Json
    Assert-Release 'explicit desktop acceptance is recorded separately' ($inputManifest.checks.desktopInput -eq 'passed' -and $inputManifest.checks.ui -eq 'not-run')
    $badStatus = $false
    try { & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish -Checks @{ desktopInput = 'unknown' } }
    catch { if ($_.Exception.Message -notlike 'Invalid release check status:*') { throw }; $badStatus = $true }
    Assert-Release 'unknown desktop acceptance status is rejected' $badStatus
    [IO.File]::WriteAllText((Join-Path $publish 'THIRD-PARTY-NOTICES.txt'), 'different bytes')
    $rejected = $false
    try { & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish }
    catch { if ($_.Exception.Message -notlike 'ZIP content mismatch:*') { throw }; $rejected = $true }
    Assert-Release 'mismatched ZIP payload rejected' $rejected
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $publish -Force
    $extra = Join-Path $fixture 'unexpected.txt'
    [IO.File]::WriteAllText($extra, 'unexpected')
    Compress-Archive -LiteralPath $extra -DestinationPath $package -Update
    $rejected = $false
    try { & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish }
    catch { if ($_.Exception.Message -notlike 'Release ZIP must contain*') { throw }; $rejected = $true }
    Assert-Release 'extra ZIP entry rejected' $rejected
    Remove-Item -LiteralPath $package -Force
    Compress-Archive -LiteralPath (Join-Path $publish 'MDViewerNative.exe') -DestinationPath $package
    $rejected = $false
    try { & $writer -ProjectRoot $root -Package $package -PublishDirectory $publish }
    catch { if ($_.Exception.Message -notlike 'Release ZIP must contain*') { throw }; $rejected = $true }
    Assert-Release 'ZIP without third-party notices rejected' $rejected
    Write-Output "Release manifest checks: $script:count passed"
} finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
