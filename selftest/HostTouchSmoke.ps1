param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$AllowInput)
# Review #2 real-window acceptance: on a hybrid device the first touch on the hidden close of a clean inactive tab
# only activates it; the next tap (close now visible) closes it. Touch goes through InjectTouchInput and the system touch-to-mouse promotion; no system setting changes.
$ErrorActionPreference='Stop'
if (-not $AllowInput) { throw 'An explicitly approved idle desktop is required; pass -AllowInput.' }
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Existing reader left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TouchProbe {
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINTER_INFO {
        public int pointerType; public uint pointerId, frameId, pointerFlags; public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int inputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType; }
    [StructLayout(LayoutKind.Sequential)] struct POINTER_TOUCH_INFO {
        public POINTER_INFO pointerInfo; public uint touchFlags, touchMask; public RECT rcContact, rcContactRaw; public uint orientation, pressure; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; public long pad; }
    [DllImport("user32.dll", SetLastError=true)] static extern bool InitializeTouchInjection(uint max, uint mode);
    [DllImport("user32.dll", SetLastError=true)] static extern bool InjectTouchInput(uint count, POINTER_TOUCH_INFO[] contacts);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    static bool ready;
    public static void Init() { SetThreadDpiAwarenessContext((IntPtr)(-4)); if (!ready && !InitializeTouchInjection(1, 3)) throw new Exception("InitializeTouchInjection failed " + Marshal.GetLastWin32Error()); ready = true; }
    static POINT Screen(IntPtr h, int x, int y) { var p = new POINT { X = x, Y = y }; ClientToScreen(h, ref p); return p; }
    static void Inject(POINT p, uint flags) {
        var c = new POINTER_TOUCH_INFO();
        c.pointerInfo.pointerType = 2; c.pointerInfo.pointerId = 0; c.pointerInfo.pointerFlags = flags; c.pointerInfo.ptPixelLocation = p;
        c.touchMask = 7; c.orientation = 90; c.pressure = 32000;
        c.rcContact = new RECT { L = p.X - 2, T = p.Y - 2, R = p.X + 2, B = p.Y + 2 };
        if (!InjectTouchInput(1, new[] { c })) throw new Exception("InjectTouchInput failed " + Marshal.GetLastWin32Error());
    }
    public static void Tap(IntPtr h, int x, int y) {
        var p = Screen(h, x, y);
        Inject(p, 0x10000 | 0x2 | 0x4);          // DOWN | INRANGE | INCONTACT
        System.Threading.Thread.Sleep(60);
        Inject(p, 0x40000);                      // UP
    }
    // Real mouse motion (extra info 0) over the document body: hover presentation, nothing on the toolbar hovered.
    public static void MouseOver(IntPtr h, int x, int y) {
        var p = Screen(h, x, y); SetCursorPos(p.X, p.Y);
        var move = new INPUT { type = 0, mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = 1 } };
        SendInput(1, new[] { move }, Marshal.SizeOf(typeof(INPUT)));
        move.mi.dx = -1; SendInput(1, new[] { move }, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-touch-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0; $app=$null
$prefix='\[\u5DE5\u5177\u680F\uFF1A'
$closeAlpha=$prefix+'\u5173\u95ED\u6807\u7B7E\uFF1Aalpha\]'
function Stroke([uint16[]]$Keys) { Press-Owned $Keys; Start-Sleep -Milliseconds 100 }
function Focus-Target([string]$Pattern) {
    if ((Text-Of $script:window) -notmatch $prefix) { Stroke @(0x75) }
    $steps=0
    while ((Text-Of $script:window) -notmatch $Pattern -and $steps -lt 64) { Stroke @(0x27); $steps++ }
    if ((Text-Of $script:window) -notmatch $Pattern) { throw ('Target not reached: '+$Pattern) }
}
function Focus-Center([string]$Name) {
    Capture $Name
    $image=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name+'.png')))
    try {
        $left=$image.Width; $right=-1
        for($y=0;$y -le [int](6*$script:scale);$y++) { for($x=0;$x -lt $image.Width;$x++) {
            $c=$image.GetPixel($x,$y)
            if(($c.B-$c.R) -gt 20 -and ($c.B-$c.G) -gt 10) { $left=[Math]::Min($left,$x);$right=[Math]::Max($right,$x) }
        }}
        if($right -lt $left) { throw 'No real focus outline pixels found.' }
        return [int](($left+$right)/2)
    } finally { $image.Dispose() }
}
try {
    [TouchProbe]::Init()
    Write-Output ("TOUCH_DIGITIZER="+[TouchProbe]::GetSystemMetrics(94))
    $alpha=Join-Path $fixture 'alpha.md';$bravo=Join-Path $fixture 'bravo.md'
    [IO.File]::WriteAllText($alpha,"# Alpha`nOriginal.`n");[IO.File]::WriteAllText($bravo,"# Bravo`nSecond.`n")
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1100,700,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$alpha+'"') -PassThru
    Wait-Until { if($app.HasExited){throw 'Host exited.'};[HostNative]::Window([uint32]$app.Id,'MorningDesk') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    $handoff=Start-Process -FilePath $ExePath -ArgumentList ('"'+$bravo+'"') -PassThru -WindowStyle Hidden
    if(-not $handoff.WaitForExit(8000)){throw 'Handoff timeout.'};$handoff.Dispose()
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') }
    if(-not [DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)){throw 'Owned foreground unavailable; input not sent.'}
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("TOUCH_DPI="+[HostNative]::GetDpiForWindow($script:window))
    Focus-Target $closeAlpha
    $closeX=Focus-Center 'touch-0-locate-close'
    $closeY=[int](18*$script:scale)
    Stroke @(27)
    Wait-Until { (Text-Of $script:window) -notmatch $prefix }
    [TouchProbe]::MouseOver($script:window,[int](500*$script:scale),[int](300*$script:scale))
    Start-Sleep -Milliseconds 250
    Capture 'touch-1-mouse-mode'
    Assert-Host 'setup: bravo active, alpha inactive and clean, mouse hover mode' ((Text-Of $script:window).StartsWith('bravo.md'))

    [TouchProbe]::Tap($script:window,$closeX,$closeY)
    Start-Sleep -Milliseconds 600
    Capture 'touch-2-after-first-tap'
    Assert-Host 'first touch on the hidden close activates alpha instead of closing it' ((Text-Of $script:window).StartsWith('alpha.md'))
    # Touch mode must persist after the tap: the other clean tab (bravo) shows its close in Ink.
    $shot=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory 'touch-2-after-first-tap.png'))
    try {
        $ink=0
        for($y=[int](12*$script:scale);$y -le [int](24*$script:scale);$y++) { for($x=$closeX+[int](20*$script:scale);$x -lt $shot.Width;$x++) {
            $c=$shot.GetPixel($x,$y); if(($c.R+$c.G+$c.B)/3 -lt 70) { $ink++ }
        }}
    } finally { $shot.Dispose() }
    Write-Output "TOUCH_OTHER_CLOSE_INK=$ink"
    Assert-Host 'touch mode persists after the tap: the other clean tab shows its close' ($ink -gt 0)
    Command 1009
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') }
    Command 1010
    Wait-Until { (Text-Of $script:window).StartsWith('alpha.md') }
    Assert-Host 'both documents are still open after the first tap' $true

    [TouchProbe]::Tap($script:window,$closeX,$closeY)
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') }
    Capture 'touch-3-after-second-tap'
    Command 1009
    Start-Sleep -Milliseconds 300
    Assert-Host 'with the close visible the second tap closes exactly alpha' ((Text-Of $script:window).StartsWith('bravo.md') -and [IO.File]::Exists($alpha))
    Write-Output "HOST_TOUCH_PASS=$script:passed"
}
finally {
    if($app -and -not $app.HasExited){ [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero); if(-not $app.WaitForExit(5000)){ $app.Kill() } }
    $env:MDV_CONFIG_DIR=$oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
