param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'

# Import the installed x64 C++ toolchain for both the shim and NativeAOT.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = $env:MDV_VCVARS_PATH
if (-not $vcvars -and (Test-Path -LiteralPath $vswhere)) {
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -eq 0 -and $vs) { $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat' }
}
# Some offline developer images contain a usable toolchain without installer registration.
if (-not $vcvars) {
    $candidates = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | ForEach-Object {
        Get-ChildItem -Path (Join-Path $_ 'Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat') -ErrorAction SilentlyContinue
    }
    $vcvars = $candidates | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $vcvars -or -not (Test-Path -LiteralPath $vcvars)) { throw 'Install Visual Studio x64 C++ tools and Windows SDK, or set MDV_VCVARS_PATH to vcvars64.bat.' }
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $env:ComSpec
$start.Arguments = '/u /d /c "call "%MDV_BUILD_VCVARS%" >nul && set"'
$start.EnvironmentVariables['MDV_BUILD_VCVARS'] = $vcvars
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.StandardOutputEncoding = [System.Text.Encoding]::Unicode
$process = [System.Diagnostics.Process]::Start($start)
try {
    $environment = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'C++ toolchain initialization failed.' }
} finally { $process.Dispose() }
foreach ($entry in ($environment -split "`r?`n")) {
    if ($entry -match '^([^=]+)=(.*)$' -and $matches[1] -ne 'MDV_BUILD_VCVARS') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
    }
}
# vcvars sets Platform=x64, which otherwise silently redirects managed bin/obj paths.
[Environment]::SetEnvironmentVariable('Platform', $null, 'Process')
$nativeOutput = [IO.Path]::GetFullPath($OutputDirectory)
$nativeSource = Join-Path $PSScriptRoot '..\shim\mdnative.cpp'
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
Push-Location $nativeOutput
try {
    # /Brepro on both compiler and linker drops the PE timestamp, so identical sources and
    # toolset give byte-identical output and a Publish leaves the worktree clean.
    & cl /nologo /std:c++17 /utf-8 /O2 /MT /EHsc /LD /Brepro $nativeSource /Fe:MDNative.dll /link /Brepro d2d1.lib d3d11.lib dxgi.lib windowscodecs.lib shlwapi.lib ole32.lib
    if ($LASTEXITCODE -ne 0) { throw "Native shim build failed ($LASTEXITCODE)." }
    # Static flavour for NativeAOT publish: MD_STATIC drops the exports, /Zl leaves the CRT
    # choice to the host link, so the published exe carries the shim and needs no MDNative.dll.
    $staticOutput = Join-Path $nativeOutput 'static'
    New-Item -ItemType Directory -Path $staticOutput -Force | Out-Null
    & cl /nologo /c /std:c++17 /utf-8 /O2 /MT /EHsc /Zl /Brepro /DMD_STATIC $nativeSource "/Fo:$staticOutput\mdnative.obj"
    if ($LASTEXITCODE -ne 0) { throw "Native static shim build failed ($LASTEXITCODE)." }
    & lib /nologo /Brepro "$staticOutput\mdnative.obj" "/OUT:$staticOutput\MDNative.lib"
    if ($LASTEXITCODE -ne 0) { throw "Native static shim archive failed ($LASTEXITCODE)." }
} finally { Pop-Location }
$shimLib = Join-Path $nativeOutput 'static\MDNative.lib'
# engine/MDViewerNative.csproj defaults NativeShimPath to engine\MDNative.dll, so both a
# plain `dotnet build` and `build.ps1 -SkipNative` link the checked-in copy. Refresh it from
# this build: a stale copy silently links an older shim and the host dies with
# EntryPointNotFoundException the first time a document has an inline image.
# engine\MDNative.dll shows as modified only when shim\mdnative.cpp or the C++ toolset
# changed; commit the refreshed copy together with the source change.
$builtShim = Join-Path $nativeOutput 'MDNative.dll'
$checkedInShim = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\engine\MDNative.dll'))
if ($checkedInShim -ne $builtShim) { Copy-Item -LiteralPath $builtShim -Destination $checkedInShim -Force }
