param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [hashtable]$Checks = @{}
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$names = @('MDViewerNative.exe', 'THIRD-PARTY-NOTICES.txt')
$archive = [IO.Compression.ZipFile]::OpenRead($Package)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    if (($entries -join '|') -ne (($names | Sort-Object) -join '|')) {
        throw 'Release ZIP must contain exactly the single-file executable and third-party notices.'
    }
    foreach ($entry in $archive.Entries) {
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $PublishDirectory $entry.FullName) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $sourceHash) { throw "ZIP content mismatch: $($entry.FullName)" }
    }
} finally { $archive.Dispose() }
$commit = $null
$dirty = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $revision = & git -C $ProjectRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $commit = [string]$revision
        $status = & git -C $ProjectRoot status --porcelain --untracked-files=normal
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source worktree state.' }
        $dirty = -not [string]::IsNullOrWhiteSpace(($status -join "`n"))
    }
}
$packageHash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
$sdk = & dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Cannot read SDK version.' }
$verified = [ordered]@{}
foreach ($key in @('build', 'formatting', 'selftest', 'specification', 'corpus', 'ui', 'performance', 'desktopInput')) {
    $value = $Checks[$key]
    if (-not $value) { $value = 'not-run' }
    if ($value -notin @('passed', 'not-run')) { throw "Invalid release check status: $key=$value" }
    $verified[$key] = $value
}
$verified['packageContent'] = 'passed'
$manifest = [ordered]@{
    schema = 1
    createdUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $commit
    sourceDirty = $dirty
    sdk = [string]$sdk
    productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDirectory 'MDViewerNative.exe')).ProductVersion
    runtime = 'win-x64'
    checks = $verified
    package = [ordered]@{ name = [IO.Path]::GetFileName($Package); bytes = (Get-Item -LiteralPath $Package).Length; sha256 = $packageHash }
    files = @($names | ForEach-Object {
        $path = Join-Path $PublishDirectory $_
        [ordered]@{ name = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$encoding = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText(($Package + '.manifest.json'), (($manifest | ConvertTo-Json -Depth 6) + "`n"), $encoding)
[IO.File]::WriteAllText(($Package + '.sha256'), ($packageHash + '  ' + [IO.Path]::GetFileName($Package) + "`n"), $encoding)
Write-Host "Release manifest: $Package.manifest.json (sourceDirty=$dirty)"
