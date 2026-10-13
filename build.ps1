param(
    [ValidateSet('Build', 'Verify', 'Publish')][string]$Task = 'Verify',
    [switch]$Ui,
    [switch]$Performance,
    [switch]$DesktopInput,
    [switch]$SkipNative,
    [string]$SdkDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)
$ErrorActionPreference = 'Stop'
if ($DesktopInput -and -not $Ui) { throw '-DesktopInput requires -Ui and an idle owned test desktop.' }
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $args" }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($Task -eq 'Publish') {
    # A failed attempt must never leave a previous candidate looking current.
    foreach ($name in @('MDViewerNative-win-x64.zip', 'MDViewerNative-win-x64.zip.sha256', 'MDViewerNative-win-x64.zip.manifest.json')) {
        $candidate = Join-Path $output $name
        if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
    }
}
$savedEnvironment = @{}
# PowerShell 7 drops a hashtable key when $null is assigned to it, so the names
# to restore are tracked separately: variables that were unset before the run
# must still be unset afterwards.
$savedNames = New-Object System.Collections.Generic.List[string]
$locationPushed = $false
try {
    if ($SdkDirectory) {
        $sdkRoot = if ([IO.Path]::IsPathRooted($SdkDirectory)) { $SdkDirectory } else { Join-Path $PSScriptRoot $SdkDirectory }
        $sdkRoot = (Resolve-Path -LiteralPath $sdkRoot).Path
        if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'dotnet.exe') -PathType Leaf)) {
            throw "The selected SDK directory does not contain dotnet.exe: $sdkRoot"
        }
        # Only process-scoped settings, restored even when the build fails.
        $environment = @{
            DOTNET_ROOT = $sdkRoot
            DOTNET_ROOT_X64 = $sdkRoot
            PATH = "$sdkRoot;$env:PATH"
            DOTNET_CLI_HOME = (Join-Path $PSScriptRoot 'work/maturity/dotnet-home')
            NUGET_PACKAGES = (Join-Path $PSScriptRoot 'work/maturity/nuget10')
            DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
            DOTNET_NOLOGO = 'true'
        }
        foreach ($name in $environment.Keys) {
            [void]$savedNames.Add($name)
            $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
        }
    }
    Push-Location $PSScriptRoot
    $locationPushed = $true
    try { Invoke-Dotnet --version }
    catch { throw "Cannot resolve the SDK required by global.json. Select an existing SDK with -SdkDirectory (for example work/maturity/sdk10). $($_.Exception.Message)" }
    if ($SkipNative) {
        if ($Task -eq 'Publish') { throw 'Publish requires rebuilding the native shim; omit -SkipNative.' }
        $shim = Join-Path $PSScriptRoot 'engine\MDNative.dll'
    } else {
        . (Join-Path $PSScriptRoot 'scripts\BuildNative.ps1') -OutputDirectory (Join-Path $output 'native')
        $shim = Join-Path $output 'native\MDNative.dll'
    }
    Invoke-Dotnet build engine/MDViewerNative.csproj -c Release --nologo "-p:NativeShimPath=$shim"
    Invoke-Dotnet build selftest/SelfTest.csproj -c Release --nologo "-p:NativeShimPath=$shim"
    if ($Task -ne 'Build') {
        Invoke-Dotnet format whitespace engine/MDViewerNative.csproj --verify-no-changes --no-restore
        Invoke-Dotnet format whitespace selftest/SelfTest.csproj --verify-no-changes --no-restore
        & ./selftest/ProjectSdk.Tests.ps1
        & ./selftest/ReleaseManifest.Tests.ps1
        Push-Location selftest
        try {
            Invoke-Dotnet run -c Release --no-build
            Invoke-Dotnet run -c Release --no-build -- --spec-verify
            if ($Performance) { Invoke-Dotnet run -c Release --no-build -- --perf }
        } finally { Pop-Location }
        & powershell -NoProfile -ExecutionPolicy Bypass -File ./selftest/CorpusGolden.ps1 -Verify
        if ($LASTEXITCODE -ne 0) { throw 'Corpus regression verification failed.' }
    }
    $exe = Join-Path $PSScriptRoot 'engine\bin\Release\net10.0-windows\MDViewerNative.exe'
    if ($Task -eq 'Publish') {
        $publish = Join-Path $output 'publish\win-x64'
        # Start clean so a leftover MDNative.dll from an older layout can never ship.
        if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
        # Single-file: the shim is linked statically (see engine/MDViewerNative.csproj ShimStatic).
        Invoke-Dotnet publish engine/MDViewerNative.csproj -c Release -r win-x64 '-p:PublishAot=true' '-p:IlcUseEnvironmentalTools=true' "-p:NativeShimPath=$shim" "-p:NativeShimLib=$shimLib" -o $publish
        if (Test-Path -LiteralPath (Join-Path $publish 'MDNative.dll')) { throw 'Publish must not ship MDNative.dll; the shim is linked into the exe.' }
        $exe = Join-Path $publish 'MDViewerNative.exe'
    }
    if ($Ui) {
        & ./selftest/HostSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostFileSmoke.ps1 -ExePath $exe
        & ./selftest/HostRecoverySmoke.ps1 -ExePath $exe
        & ./selftest/HostUiSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostHandoffSmoke.ps1 -ExePath $exe
        & ./selftest/HostNavigationSmoke.ps1 -ExePath $exe
        & ./selftest/HostRefreshSmoke.ps1 -ExePath $exe
        & ./selftest/HostImageSmoke.ps1 -ExePath $exe
        & ./selftest/HostReadingSmoke.ps1 -ExePath $exe
        & ./selftest/HostDraftPreviewSmoke.ps1 -ExePath $exe
        & ./selftest/HostProductDocumentsSmoke.ps1 -ExePath $exe
        & ./selftest/HostMathDiagramSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostWideContentSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostSourceColorSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostToolbarStateSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostTabNamesSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostToolbarFoldSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostA2MatrixSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui') -Theme light
        & ./selftest/HostA2MatrixSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui') -Theme dark
        & ./selftest/HostOverlaySmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostOverlayHitSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
        & ./selftest/HostCodeCopySmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui')
    }
    if ($DesktopInput) {
        & ./selftest/HostReadingToolsSmoke.ps1 -ExePath $exe
        & ./selftest/HostImeSmoke.ps1 -ExePath $exe -AllowInput
        & ./selftest/HostKeyboardSmoke.ps1 -ExePath $exe -AllowInput
        & ./selftest/HostToolbarInteractionSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui') -AllowInput
        & ./selftest/HostTouchSmoke.ps1 -ExePath $exe -OutputDirectory (Join-Path $output 'ui') -AllowInput
    }
    if ($Task -eq 'Publish') {
        $package = Join-Path $output 'MDViewerNative-win-x64.zip'
        if (-not (Test-Path -LiteralPath $exe)) {
            $contents = (Get-ChildItem -LiteralPath $publish -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ', '
            throw "Publish output is missing: $exe. Publish directory contents: $contents"
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $publish -Force
        Compress-Archive -LiteralPath $exe, (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -DestinationPath $package -Force
        $checks = @{ desktopInput = $(if ($DesktopInput) { 'passed' } else { 'not-run' }); build = 'passed'; formatting = 'passed'; selftest = 'passed'; specification = 'passed'; corpus = 'passed'; ui = $(if ($Ui) { 'passed' } else { 'not-run' }); performance = $(if ($Performance) { 'passed' } else { 'not-run' }) }
        try {
            & (Join-Path $PSScriptRoot 'scripts/WriteReleaseManifest.ps1') -ProjectRoot $PSScriptRoot -Package $package -PublishDirectory $publish -Checks $checks
        } catch {
            foreach ($candidate in @($package, ($package + '.sha256'), ($package + '.manifest.json'))) {
                if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
            }
            throw
        }
        Write-Host "Package: $package"
    }
    Write-Host "$Task completed."
} finally {
    if ($locationPushed) { Pop-Location }
    # $savedNames, not $savedEnvironment.Keys: a saved $null may have dropped its key.
    foreach ($name in $savedNames) {
        $saved = $savedEnvironment[$name]
        if ([string]::IsNullOrEmpty($saved)) {
            # Was unset before this run. On PowerShell 7's .NET, SetEnvironmentVariable
            # with a null value leaves an empty variable instead of deleting it, which
            # still leaks a setting; the provider delete is reliable on both.
            Remove-Item "env:$name" -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($name, $saved, 'Process')
        }
    }
}
