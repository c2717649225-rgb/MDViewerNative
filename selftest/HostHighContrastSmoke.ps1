param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$AllowSystemChange)
# System high-contrast observation. Turns HC on for this session only (SPIF_SENDCHANGE, no ini write),
# captures the toolbar, and always restores the saved HIGHCONTRAST value in finally, then verifies that
# system colors and the current theme match the saved state.
$ErrorActionPreference='Stop'
if (-not $AllowSystemChange) { throw 'Explicit approval required; pass -AllowSystemChange.' }
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Existing reader left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HcProbe {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct HIGHCONTRAST { public uint cbSize, dwFlags; public IntPtr scheme; }
    [DllImport("user32.dll", SetLastError=true)] static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint flags);
    [DllImport("user32.dll")] public static extern uint GetSysColor(int index);
    public static uint Flags; public static string Scheme;
    public static void Save() {
        var v = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST)) };
        if (!SystemParametersInfo(0x42, v.cbSize, ref v, 0)) throw new Exception("SPI_GETHIGHCONTRAST failed");
        Flags = v.dwFlags; Scheme = v.scheme == IntPtr.Zero ? null : Marshal.PtrToStringUni(v.scheme);
    }
    public static bool IsOn() {
        var v = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST)) };
        return SystemParametersInfo(0x42, v.cbSize, ref v, 0) && (v.dwFlags & 1) != 0;
    }
    static void Set(uint flags) {
        var v = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST)), dwFlags = flags };
        IntPtr s = Scheme == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(Scheme);
        try { v.scheme = s; if (!SystemParametersInfo(0x43, v.cbSize, ref v, 2)) throw new Exception("SPI_SETHIGHCONTRAST failed " + Marshal.GetLastWin32Error()); }
        finally { if (s != IntPtr.Zero) Marshal.FreeHGlobal(s); }
    }
    public static void On() { Set(Flags | 1u); }
    public static void Restore() { Set(Flags); }
}
'@
function Colors { return (@(5,8,13,15,17,26) | ForEach-Object { '{0:X6}' -f [HcProbe]::GetSysColor($_) }) -join ',' }
$themeKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes'
[HcProbe]::Save()
if (([HcProbe]::Flags -band 1) -ne 0) { throw 'High contrast is already on; nothing changed.' }
$savedColors=Colors
$savedTheme=(Get-ItemProperty -Path $themeKey -Name CurrentTheme -ErrorAction SilentlyContinue).CurrentTheme
Write-Output ("HC_SAVED flags=0x{0:X} scheme={1} colors={2} theme={3}" -f [HcProbe]::Flags,[HcProbe]::Scheme,$savedColors,$savedTheme)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-hc-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0; $app=$null
function Stroke([uint16[]]$Keys) { Press-Owned $Keys; Start-Sleep -Milliseconds 120 }
try {
    [HcProbe]::On()
    Wait-Until { [HcProbe]::IsOn() -and (Colors) -ne $savedColors }
    Start-Sleep -Seconds 4
    $hcColors=Colors
    Write-Output ("HC_ON colors="+$hcColors)
    $docs=@('alpha','bravo','charlie') | ForEach-Object { $p=Join-Path $fixture ($_+'.md'); [IO.File]::WriteAllText($p,"# $_`n`nHigh contrast paragraph for $_.`n"); $p }
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,700,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$docs[0]+'"') -PassThru
    Wait-Until { if($app.HasExited){throw 'Host exited.'};[HostNative]::Window([uint32]$app.Id,'MorningDesk') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    Command 1002
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    Edit-Text "# alpha`r`n`r`nUnsaved high contrast draft.`r`n"
    Wait-Until { (Text-Of $script:window).StartsWith('*alpha.md') }
    Command 1002
    foreach($d in $docs[1..2]) {
        $h=Start-Process -FilePath $ExePath -ArgumentList ('"'+$d+'"') -PassThru -WindowStyle Hidden
        if(-not $h.WaitForExit(8000)){throw 'Handoff timeout.'};$h.Dispose()
    }
    Wait-Until { (Text-Of $script:window).StartsWith('charlie.md') }
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](1100*$script:scale),[int](640*$script:scale),4)
    Start-Sleep -Milliseconds 400
    Capture 'hc-wide'
    $img=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'hc-wide.png'))
    try { $c=$img.GetPixel($img.Width-30,[int](300*$script:scale)); $canvas='{0:X2}{1:X2}{2:X2}' -f $c.B,$c.G,$c.R } finally { $img.Dispose() }
    Write-Output ("HC_CANVAS_BGR="+$canvas+" COLOR_WINDOW="+('{0:X6}' -f [HcProbe]::GetSysColor(5)))
    Assert-Host 'reader canvas follows the system COLOR_WINDOW in high contrast' ($canvas -eq ('{0:X6}' -f [HcProbe]::GetSysColor(5)))
    # An inactive (enabled) tab label must use COLOR_WINDOWTEXT, not COLOR_GRAYTEXT (which means disabled).
    if(-not [DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)){ throw 'Owned foreground unavailable; input not sent.' }
    $prefix='\[\u5DE5\u5177\u680F\uFF1A'
    $tabBravo=$prefix+'\u6807\u7B7E\uFF1Abravo\]'
    Stroke @(0x75); $steps=0
    while((Text-Of $script:window) -notmatch $tabBravo -and $steps -lt 32){ Stroke @(0x27); $steps++ }
    if((Text-Of $script:window) -notmatch $tabBravo){ throw 'bravo tab focus not reached' }
    Capture 'hc-focus-bravo'
    Stroke @(27)
    $img=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'hc-focus-bravo.png'))
    try {
        $left=$img.Width; $right=-1
        for($y=0;$y -le [int](6*$script:scale);$y++){ for($x=0;$x -lt $img.Width;$x++){ $c=$img.GetPixel($x,$y); if(($c.B-$c.R) -gt 20 -and ($c.B-$c.G) -gt 10){ $left=[Math]::Min($left,$x); $right=[Math]::Max($right,$x) } } }
        if($right -lt $left){ throw 'No focus ring found for bravo.' }
        $text=[HcProbe]::GetSysColor(8); $tr=$text -band 0xFF; $tg=($text -shr 8) -band 0xFF; $tb=($text -shr 16) -band 0xFF
        $gray=[HcProbe]::GetSysColor(17); $gr=$gray -band 0xFF; $gg=($gray -shr 8) -band 0xFF; $gb=($gray -shr 16) -band 0xFF
        $near=0; $grayNear=0
        for($y=[int](12*$script:scale);$y -le [int](24*$script:scale);$y++){ for($x=$left+[int](8*$script:scale);$x -lt [Math]::Min($right,$left+[int](48*$script:scale));$x++){
            $c=$img.GetPixel($x,$y)
            if([Math]::Abs($c.R-$tr)+[Math]::Abs($c.G-$tg)+[Math]::Abs($c.B-$tb) -le 24){ $near++ }
            if([Math]::Abs($c.R-$gr)+[Math]::Abs($c.G-$gg)+[Math]::Abs($c.B-$gb) -le 6){ $grayNear++ }
        }}
    } finally { $img.Dispose() }
    Write-Output ("HC_INACTIVE_TAB tab=[{0},{1}] windowTextPixels={2} grayTextPixels={3}" -f $left,$right,$near,$grayNear)
    Assert-Host 'inactive tab label uses COLOR_WINDOWTEXT (enabled), not COLOR_GRAYTEXT' ($near -gt 0)
    # A3 overlays: the floating find panel, the expanded TOC and the collapsed ruler must draw with system colours only.
    Command 1005
    $findInput=[HostNative]::GetDlgItem($script:window,3011)
    $r0=[IntPtr]::Zero; [void][HostNative]::SendText($findInput,0x000C,[IntPtr]::Zero,'contrast',2,4000,[ref]$r0)
    Start-Sleep -Milliseconds 300
    $ir=New-Object HostNative+RECT; [void][HostNative]::GetWindowRect($findInput,[ref]$ir); $io=New-Object HostNative+POINT; [void][HostNative]::ClientToScreen($script:window,[ref]$io)
    $panelLeft=$ir.L-$io.X-[int](8*$script:scale); $panelTop=$ir.T-$io.Y-[int](8*$script:scale)
    Capture 'hc-find-panel'
    $system=@(5,8,13,14,15,16,17,18,26) | ForEach-Object { [HcProbe]::GetSysColor($_) }
    $img=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'hc-find-panel.png'))
    try {
        $foreign=0; $samples=0; $edge=-1
        for($y=$panelTop-2;$y -le $panelTop+2 -and $edge -lt 0;$y++){ $c=$img.GetPixel($panelLeft+[int](220*$script:scale),$y); $bg=$img.GetPixel($panelLeft+[int](220*$script:scale),$panelTop-[int](3*$script:scale)); if($c.ToArgb() -ne $bg.ToArgb()){ $edge=$y } }
        if($edge -lt 0){ throw 'No find panel edge found.' }
        for($x=$panelLeft+[int](40*$script:scale);$x -lt $panelLeft+[int](400*$script:scale);$x++){
            $c=$img.GetPixel($x,$edge); $samples++
            $hit=$false; foreach($s in $system){ if([Math]::Abs($c.R-($s -band 0xFF))+[Math]::Abs($c.G-(($s -shr 8) -band 0xFF))+[Math]::Abs($c.B-(($s -shr 16) -band 0xFF)) -le 6){ $hit=$true; break } }
            if(-not $hit){ $foreign++ }
        }
    } finally { $img.Dispose() }
    Write-Output ("HC_FIND_PANEL edgeY={0} samples={1} nonSystem={2} count='{3}'" -f $edge,$samples,$foreign,(Text-Of ([HostNative]::GetDlgItem($script:window,3012))))
    Assert-Host 'find panel edge uses a system colour in high contrast' ($samples -gt 0 -and $foreign -eq 0)
    Command 1006; Start-Sleep -Milliseconds 300; Capture 'hc-find-toc'
    Command 1006; [void](Send $script:window 0x0111 3005); Start-Sleep -Milliseconds 300; Capture 'hc-ruler'
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](640*$script:scale),[int](640*$script:scale),4)
    Start-Sleep -Milliseconds 400
    Capture 'hc-narrow'
    if([DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)) {
        Stroke @(0x75)
        Capture 'hc-narrow-focus'
        Stroke @(27)
    } else { Write-Output 'HC_FOCUS_SKIPPED owned foreground unavailable' }
    Assert-Host 'high-contrast captures written' ((Test-Path (Join-Path $OutputDirectory 'hc-narrow.png')))
    Write-Output "HOST_HC_PASS=$script:passed"
}
finally {
    if($app -and -not $app.HasExited){ $app.Kill(); [void]$app.WaitForExit(5000) }
    [HcProbe]::Restore()
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    while(([HcProbe]::IsOn() -or (Colors) -ne $savedColors) -and [DateTime]::UtcNow -lt $deadline){ Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    $afterColors=Colors
    $afterTheme=(Get-ItemProperty -Path $themeKey -Name CurrentTheme -ErrorAction SilentlyContinue).CurrentTheme
    Write-Output ("HC_RESTORED on={0} colors={1} theme={2}" -f [HcProbe]::IsOn(),$afterColors,$afterTheme)
    if([HcProbe]::IsOn() -or $afterColors -ne $savedColors -or $afterTheme -ne $savedTheme){ Write-Output 'HC_RESTORE_MISMATCH' } else { Write-Output 'HC_RESTORE_OK' }
    $env:MDV_CONFIG_DIR=$oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
