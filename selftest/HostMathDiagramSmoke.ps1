param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before formula tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
# Resolve the actual drawing assemblies on both .NET Framework and modern PowerShell.
$drawingReferences = @([Drawing.Bitmap].Assembly.Location, [Drawing.Color].Assembly.Location) | Select-Object -Unique
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $drawingReferences += Join-Path $PSHOME 'System.Runtime.dll'
    $drawingReferences += @(Get-ChildItem -LiteralPath $PSHOME -Filter 'System.Private.Windows.*.dll' | ForEach-Object { $_.FullName })
}
Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.Drawing;
public static class MathPixels {
    public static int LargestInkSquare(string path, int top) {
        using (var image = new Bitmap(path)) {
            var previous = new int[image.Width]; var current = new int[image.Width];
            int largest = 0;
            for (int y = top; y < image.Height - 8; y++) {
                for (int x = 20; x < image.Width - 20; x++) {
                    Color pixel = image.GetPixel(x,y);
                    current[x] = pixel.R + pixel.G + pixel.B < 500 ? 1 + Math.Min(previous[x],Math.Min(previous[x-1],current[x-1])) : 0;
                    largest = Math.Max(largest,current[x]);
                }
                var swap = previous; previous = current; current = swap;
            }
            return largest;
        }
    }
}
'@
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\ui' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-math-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig = $env:MDV_CONFIG_DIR
$script:passed = 0
$app = $null
function Body-Ink([string]$Name, [double]$Scale) {
    $image = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name + '.png')))
    $count = 0
    try {
        for ($y=[int](80 * $Scale); $y -lt $image.Height - 8; $y+=3) {
            for ($x=20; $x -lt $image.Width - 20; $x+=3) {
                $pixel = $image.GetPixel($x,$y)
                if ($pixel.R + $pixel.G + $pixel.B -lt 500) { $count++ }
            }
        }
    } finally { $image.Dispose() }
    return $count
}
$cases = [ordered]@{
    'math-subset' = @'
```math
\frac{1}{2}+3
```

```math
x_i^2+\sqrt[3]{8}+\sqrt[n+1]{x}
```

```math
\frac{\alpha+1}{\sqrt{x}}+\text{speed = }v
```
'@
    'diagram-subset' = @'
```mermaid
flowchart RL; A[Start] --> B[End]
```

```mermaid
flowchart BT; C[Start] --> D[End]
```

```mermaid
flowchart LR; E[One] --- F{Two} -.-> G(Three)
```
'@
    'extension-fallback' = @'
```math
\begin{matrix}a&b\end{matrix}
```

```mermaid
flowchart TD
A --> B --> A
```

```mermaid
flowchart TD
A --> B
click A https://example.invalid/not-launched
```
'@
}
try {
    foreach ($name in $cases.Keys) {
        $env:MDV_CONFIG_DIR = Join-Path $fixture ('config-' + $name)
        [void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
        [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=140,90,1040,900,0")
        $source = $cases[$name]
        $path = Join-Path $fixture ($name + '.md')
        [IO.File]::WriteAllText($path, $source)
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
        Wait-Until { if ($app.HasExited) { throw 'Formula host exited during startup.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
        $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
        Wait-Until { (Text-Of $script:window).Contains($name + '.md') }
        [void][HostNative]::ShowWindow($script:window, 4)
        $scale = [HostNative]::GetDpiForWindow($script:window) / 96.0
        Capture $name
        Assert-Host ($name + ' paints a nonempty native body') ((Body-Ink $name $scale) -gt 20)
        if ($name -ne 'extension-fallback') {
            $largest = [MathPixels]::LargestInkSquare((Join-Path $OutputDirectory ($name + '.png')), [int](80 * $scale))
            Assert-Host ($name + ' strokes do not become solid rectangles; largest=' + $largest) ($largest -lt [Math]::Max(5,[int](5 * $scale)))
        }
        Command 1002
        $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
        Assert-Host ($name + ' retains the exact source and clean state') ((Text-Of $script:editor).Replace("`r`n","`n") -eq $source.Replace("`r`n","`n") -and -not (Text-Of $script:window).StartsWith('*'))
        Command 1002
        if ($name -eq 'math-subset') {
            Command 1014
            Capture 'math-subset-zoom'
            Assert-Host 'math zoom rebuilds a painted native frame' ((Body-Ink 'math-subset-zoom' $scale) -gt 20 -and (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'math-subset.png')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'math-subset-zoom.png')).Hash)
        }
        Assert-Host ($name + ' does not write source or fetch external resources') ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $hash -and -not (Test-Path (Join-Path $env:MDV_CONFIG_DIR 'image-cache')))
        [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $app.WaitForExit(6000)) { throw 'Formula host did not close.' }
        Assert-Host ($name + ' closes normally') ($app.ExitCode -eq 0)
        $app.Dispose(); $app = $null
    }
    Write-Output "HOST MATH DIAGRAM SMOKE: $script:passed PASS (owned messages; no real keyboard or network)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app -and -not $app.HasExited) { Write-Output ([HostNative]::Snapshot([uint32]$app.Id)) }
    throw
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
