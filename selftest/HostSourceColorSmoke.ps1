param([Parameter(Mandatory=$true)][string]$ExePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before source-color tests.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Windows.Forms
$paletteSource = [IO.File]::ReadAllText((Join-Path $root 'engine\ThemePalette.cs'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/ui' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('mdv-source-color-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$app = $null
$script:passed = 0
# Count exact foreground pixels only inside the editor; toolbar colors cannot satisfy this.
Add-Type -TypeDefinition @'
public static class SourceColorPixels {
    public static int Count(int[] pixels, int stride, int left, int top, int right, int bottom, int red, int green, int blue) {
        int count = 0;
        int expected = (red << 16) | (green << 8) | blue;
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++) {
            if ((pixels[y * stride + x] & 0xFFFFFF) == expected) count++;
        }
        return count;
    }
}
'@
function Has-Color([string]$Name, [int]$Red, [int]$Green, [int]$Blue, [int]$Minimum = 5) {
    $bitmap = [Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name + '.png')))
    try {
        $rect = New-Object HostNative+RECT
        $origin = New-Object HostNative+POINT
        [void][HostNative]::GetWindowRect($script:editor, [ref]$rect)
        [void][HostNative]::ClientToScreen($script:window, [ref]$origin)
        $area = New-Object Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $locked = $bitmap.LockBits($area, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            if ($locked.Stride -le 0) { throw 'Unexpected bottom-up screenshot.' }
            $pixels = New-Object int[] ($locked.Stride / 4 * $bitmap.Height)
            [Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
            return [SourceColorPixels]::Count($pixels, ($locked.Stride / 4), ($rect.L - $origin.X), ($rect.T - $origin.Y), [Math]::Min($bitmap.Width, ($rect.R - $origin.X)), [Math]::Min($bitmap.Height, ($rect.B - $origin.Y)), $Red, $Green, $Blue) -gt $Minimum
        } finally { $bitmap.UnlockBits($locked) }
    } finally { $bitmap.Dispose() }
}
function Has-Token([string]$Name, [string]$Token, [int]$Minimum = 5) {
    if ([Windows.Forms.SystemInformation]::HighContrast) {
        $color = if ($Token.StartsWith('Canvas')) { [Drawing.SystemColors]::Window } else { [Drawing.SystemColors]::WindowText }
        return (Has-Color $Name $color.R $color.G $color.B $Minimum)
    }
    $match = [regex]::Match($paletteSource, ('\b' + [regex]::Escape($Token) + '\s*=\s*0x([0-9A-Fa-f]{6})\b'))
    if (-not $match.Success) { throw "Missing palette token: $Token" }
    $rgb = [Convert]::ToInt32($match.Groups[1].Value, 16)
    return (Has-Color $Name (($rgb -shr 16) -band 255) (($rgb -shr 8) -band 255) ($rgb -band 255) $Minimum)
}
try {
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`ntheme=light`ntoc=0`nfont=18`nhints=0`nwin=120,120,820,600,0")
    $source = @'
# Markdown source colors

Plain text and **strong**, *emphasis*, [link](https://example.com).

> A quoted paragraph
- A list item

```csharp
var message = "source text stays unchanged";
```
'@
    $source = $source.Replace("`r`n", "`n") + "`n"
    $path = Join-Path $fixture 'source-colors.md'
    [IO.File]::WriteAllText($path, $source)
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $path + '"') -PassThru -WindowStyle Hidden
    Wait-Until { if ($app.HasExited) { throw 'Source-color host exited.' }; [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    [void][HostNative]::ShowWindow($script:window, 4)
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    Assert-Host 'source editor uses the system rich text control' ($script:editor -ne [IntPtr]::Zero)
    Write-Output ("SOURCE DPI={0} highContrast={1}" -f [HostNative]::GetDpiForWindow($script:window), [Windows.Forms.SystemInformation]::HighContrast)
    Capture 'source-colors-light'
    Assert-Host 'light editor background uses shared canvas' (Has-Token 'source-colors-light' 'CanvasL' 1000)
    Assert-Host 'light ordinary text uses shared ink' (Has-Token 'source-colors-light' 'InkL')
    Assert-Host 'light heading color is painted in the editor' (Has-Token 'source-colors-light' 'AccentL')
    Assert-Host 'light code color is painted in the editor' (Has-Token 'source-colors-light' 'CommentL')
    Assert-Host 'initial coloring leaves source and dirty state unchanged' ((Text-Of $script:editor).Replace("`r`n", "`n") -eq $source -and -not (Text-Of $script:window).StartsWith('*'))
    Command 1004
    Capture 'source-colors-dark'
    Assert-Host 'dark editor background uses shared canvas' (Has-Token 'source-colors-dark' 'CanvasD' 1000)
    Assert-Host 'dark ordinary text uses shared ink' (Has-Token 'source-colors-dark' 'InkD')
    Assert-Host 'dark heading color is painted in the editor' (Has-Token 'source-colors-dark' 'AccentD')
    Assert-Host 'dark code color is painted in the editor' (Has-Token 'source-colors-dark' 'CommentD')
    $draft = "# Changed heading`r`nordinary body`r`n"
    Edit-Text $draft
    Capture 'source-colors-draft'
    Assert-Host 'typing triggers deferred coloring' (Has-Token 'source-colors-draft' 'AccentD')
    Assert-Host 'colored draft retains exact text' ((Text-Of $script:editor) -eq $draft)
    [void](Send $script:editor 0x00C7)
    Assert-Host 'undo after deferred coloring restores source and clean state' ((Text-Of $script:editor).Replace("`r`n", "`n") -eq $source -and -not (Text-Of $script:window).StartsWith('*'))
    Assert-Host 'coloring never writes source or a backup' ([IO.File]::ReadAllText($path) -eq $source -and -not (Test-Path ($path + '.bak')))
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(6000)) { throw 'Source-color host did not close.' }
    Assert-Host 'source-color host closes normally' ($app.ExitCode -eq 0)
    Write-Output "HOST SOURCE COLOR SMOKE: $script:passed PASS (owned messages and actual pixels; no real keyboard)"
} finally {
    if ($app) { if (-not $app.HasExited) { $app.Kill(); [void]$app.WaitForExit(4000) }; $app.Dispose() }
    $env:MDV_CONFIG_DIR = $oldConfig
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
