$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$entry = Join-Path $projectRoot 'build.ps1'
$sdk = Split-Path (Get-Command dotnet -ErrorAction Stop).Source -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-sdk-entry-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$names = @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_NOLOGO')
$before = @{}
foreach ($name in $names) { $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$globalHash = (Get-FileHash (Join-Path $projectRoot 'global.json') -Algorithm SHA256).Hash
$script:passed = 0
function Check-Sdk([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL sdk/$Name" }
    $script:passed++
    Write-Output "PASS sdk/$Name"
}
function Same-Environment {
    foreach ($name in $names) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne $before[$name]) { return $false }
    }
    return $true
}
Push-Location $fixture
try {
    $caught = $false
    try { & $entry -Task Build -SdkDirectory (Join-Path $fixture 'missing') -OutputDirectory (Join-Path $fixture 'missing-output') }
    catch { $caught = $true }
    Check-Sdk 'missing SDK is rejected without installation' $caught
    Check-Sdk 'early failure leaves environment unchanged' (Same-Environment)
    Check-Sdk 'early failure leaves caller directory unchanged' ((Get-Location).Path -eq $fixture)

    $caught = $false
    try { & $entry -Task Build -SdkDirectory $fixture -OutputDirectory (Join-Path $fixture 'invalid-output') }
    catch { $caught = $_.Exception.Message.Contains('does not contain dotnet.exe') }
    Check-Sdk 'directory without dotnet is rejected' $caught

    # Resolve an actual SDK, then deliberately fail before native compilation or publish.
    $caught = $false
    try { & $entry -Task Publish -SkipNative -SdkDirectory $sdk -OutputDirectory (Join-Path $fixture 'failed-publish') }
    catch { $caught = $_.Exception.Message.Contains('Publish requires rebuilding the native shim') }
    Check-Sdk 'selected SDK reaches the existing publish guard' $caught
    Check-Sdk 'inner failure restores every process setting' (Same-Environment)
    Check-Sdk 'inner failure restores the caller directory' ((Get-Location).Path -eq $fixture)
    Check-Sdk 'SDK selection never rewrites global.json' ((Get-FileHash (Join-Path $projectRoot 'global.json') -Algorithm SHA256).Hash -eq $globalHash)
    Write-Output "Project SDK checks: $script:passed passed"
} finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
