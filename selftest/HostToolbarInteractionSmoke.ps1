param([Parameter(Mandatory=$true)][string]$ExePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$AllowInput)
$ErrorActionPreference='Stop'
if (-not $AllowInput) { throw 'An explicitly approved idle desktop is required; pass -AllowInput.' }
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Existing reader left untouched.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
Add-Type -AssemblyName System.Windows.Forms
if ([Windows.Forms.SystemInformation]::HighContrast) { throw 'Focus-pixel observation needs a separately coordinated non-HC session; no settings changed.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ToolbarPointerProbe {
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW")] static extern IntPtr Send(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    public static void Pointer(IntPtr h,uint message,int x,int y) {
        IntPtr previous=SetThreadDpiAwarenessContext((IntPtr)(-4));
        if(previous==IntPtr.Zero) throw new InvalidOperationException("Cannot set sender awareness.");
        try {
            IntPtr point=(IntPtr)((y<<16)|(x&0xffff)), result;
            if(message==0x0202) { if(!PostMessage(h,message,IntPtr.Zero,point)) throw new Exception("Post failed."); }
            else if(Send(h,message,message==0x0201?(IntPtr)1:IntPtr.Zero,point,2,4000,out result)==IntPtr.Zero) throw new Exception("Pointer send timeout.");
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('mdv-toolbar-input-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldConfig=$env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR=Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
$script:passed=0; $app=$null
$prefix='\[\u5DE5\u5177\u680F\uFF1A'
# A2-B3: toolbar descriptions use the display name (".md" hidden); window titles keep the file name.
$closeDirtyAlpha=$prefix+'\u5173\u95ED\u672A\u4FDD\u5B58\u6587\u6863\uFF1Aalpha\]'
$closeBravo=$prefix+'\u5173\u95ED\u6807\u7B7E\uFF1Abravo\]'
$more=$prefix+'\u66F4\u591A\]'
function Stroke([uint16[]]$Keys) { Press-Owned $Keys; Start-Sleep -Milliseconds 100 }
function Focus-Target([string]$Pattern) {
    if ((Text-Of $script:window) -notmatch $prefix) { Stroke @(0x75) }
    $steps=0
    while ((Text-Of $script:window) -notmatch $Pattern -and $steps -lt 64) { Stroke @(0x27); $steps++ }
    if ((Text-Of $script:window) -notmatch $Pattern) { throw ('Target not reached: '+$Pattern+' title='+ (Text-Of $script:window)) }
}
function Focus-Center([string]$Name) {
    Capture $Name
    $image=[Drawing.Bitmap]::FromFile((Join-Path $OutputDirectory ($Name+'.png')))
    try {
        $left=$image.Width; $right=-1
        for($y=0;$y -le [int](6*$script:scale);$y++) { for($x=0;$x -lt $image.Width;$x++) {
            $c=$image.GetPixel($x,$y)
            # Accent focus border only, above label glyphs; tolerate normal edge AA.
            if(($c.B-$c.R) -gt 20 -and ($c.B-$c.G) -gt 10) { $left=[Math]::Min($left,$x);$right=[Math]::Max($right,$x) }
        }}
        if($right -lt $left) { throw 'No real focus outline pixels found.' }
        return [int](($left+$right)/2)
    } finally { $image.Dispose() }
}
try {
    $alpha=Join-Path $fixture 'alpha.md';$bravo=Join-Path $fixture 'bravo.md'
    $source="# Alpha`nOriginal.`n";$second="# Bravo`nSecond.`n"
    [IO.File]::WriteAllText($alpha,$source);[IO.File]::WriteAllText($bravo,$second)
    [IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'),"chrome=classic`ntheme=light`ntoc=0`nfont=16`nhints=0`nwin=120,120,1200,800,0`n")
    $app=Start-Process -FilePath $ExePath -ArgumentList ('"'+$alpha+'"') -PassThru
    Wait-Until { if($app.HasExited){throw 'Host exited.'};[HostNative]::Window([uint32]$app.Id,'MorningDesk') -ne [IntPtr]::Zero }
    $script:window=[HostNative]::Window([uint32]$app.Id,'MorningDesk')
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    $handoff=Start-Process -FilePath $ExePath -ArgumentList ('"'+$bravo+'"') -PassThru -WindowStyle Hidden
    if(-not $handoff.WaitForExit(8000)){throw 'Handoff timeout.'};$handoff.Dispose()
    Wait-Until { (Text-Of $script:window).Contains('bravo.md') }
    Command 1010
    Wait-Until { (Text-Of $script:window).Contains('alpha.md') }
    if(-not [DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)){throw 'Owned foreground unavailable; input not sent.'}
    $script:scale=[HostNative]::GetDpiForWindow($script:window)/96.0
    Write-Output ("TOOLBAR_INTERACTION_DPI="+[HostNative]::GetDpiForWindow($script:window))
    [void][HostNative]::SetWindowPos($script:window,[IntPtr]::Zero,120,120,[int](1200*$script:scale),800,4)
    Command 1002
    $script:editor=[HostNative]::FindWindowEx($script:window,[IntPtr]::Zero,'RICHEDIT50W',$null)
    $draft="# Alpha draft`r`nPrivate test draft only.`r`n"
    Edit-Text $draft
    [void](Send $script:editor 0x00B1 2 7);$selection=Send $script:editor 0x00B0
    Stroke @(0x75)
    Assert-Host 'real F6 enters visible toolbar focus from editor' ((Text-Of $script:window) -match $prefix -and [HostNative]::FocusedControl($script:window) -eq $script:window)
    Stroke @(0x10,9)
    Assert-Host 'real Shift Tab reaches last visible close target' ((Text-Of $script:window) -match $closeBravo)
    Focus-Target $closeDirtyAlpha
    $closeX=Focus-Center 'b2-close-focus'
    Stroke @(13)
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
    Assert-Host 'keyboard close uses existing unsaved guard' $true
    Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 2
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
    Assert-Host 'cancel keeps exact dirty draft and close focus' ((Text-Of $script:window).StartsWith('*alpha.md') -and (Text-Of $script:window) -match $closeDirtyAlpha -and (Text-Of $script:editor) -eq $draft)
    Stroke @(27)
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $script:editor }
    Assert-Host 'Escape restores editor selection' ((Send $script:editor 0x00B0) -eq $selection)

    [ToolbarPointerProbe]::Pointer($script:window,0x0200,$closeX,[int](18*$script:scale))
    Capture 'b2-pointer-hover'
    [ToolbarPointerProbe]::Pointer($script:window,0x0201,$closeX,[int](18*$script:scale))
    Capture 'b2-pointer-pressed'
    Assert-Host 'mouse down alone cannot close or prompt' ([HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero -and (Text-Of $script:window).StartsWith('*alpha.md'))
    [ToolbarPointerProbe]::Pointer($script:window,0x0202,$closeX,[int](60*$script:scale))
    Start-Sleep -Milliseconds 150
    Assert-Host 'release outside cancels close' ([HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero -and (Text-Of $script:editor) -eq $draft)
    # Review #2: a real mouse returns to the close (WM_MOUSEMOVE reveals it) before pressing again;
    # a press on a close that is not on screen only activates the tab.
    [ToolbarPointerProbe]::Pointer($script:window,0x0200,$closeX,[int](18*$script:scale))
    [ToolbarPointerProbe]::Pointer($script:window,0x0201,$closeX,[int](18*$script:scale))
    [ToolbarPointerProbe]::Pointer($script:window,0x0202,$closeX,[int](18*$script:scale))
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
    Assert-Host 'release inside invokes guarded close exactly once' $true
    Choose-Dialog ([HostNative]::Dialog([uint32]$app.Id)) 2
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }

    Stroke @(0x11,9)
    Wait-Until { (Text-Of $script:window).StartsWith('bravo.md') }
    Focus-Target $closeDirtyAlpha
    Stroke @(13)
    Wait-Until { [HostNative]::Window([uint32]$app.Id,'MDVNativeForm') -ne [IntPtr]::Zero }
    $form=[HostNative]::Window([uint32]$app.Id,'MDVNativeForm')
    Assert-Host 'inactive dirty close uses existing document-set guard' ([HostNative]::GetDlgItem($form,4002) -ne [IntPtr]::Zero)
    [void](Send ([HostNative]::GetDlgItem($form,4002)) 0x00F5)
    Wait-Until { [HostNative]::Window([uint32]$app.Id,'MDVNativeForm') -eq [IntPtr]::Zero }
    Assert-Host 'cancel inactive dirty close retains active identity and target state' ((Text-Of $script:window).StartsWith('bravo.md') -and (Text-Of $script:window) -match $closeDirtyAlpha)
    Stroke @(27)
    Stroke @(0x11,0x10,9)
    Wait-Until { (Text-Of $script:window).StartsWith('*alpha.md') }
    Command 1002
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $script:editor }
    Assert-Host 'inactive draft and literal file survive the cancel path' ((Text-Of $script:editor) -eq $draft -and [IO.File]::ReadAllText($alpha) -eq $source)

    Stroke @(0x11,0x46)
    $query=[HostNative]::GetDlgItem($script:window,3011)
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $query }
    Stroke @(0x75)
    Focus-Target $more
    Capture 'b2-more-focus'
    Stroke @(13)
    Wait-Until { [HostNative]::Window([uint32]$app.Id,'#32768') -ne [IntPtr]::Zero }
    Start-Sleep -Milliseconds 150
    Assert-Host 'Enter opens More without auto-executing first item' ([HostNative]::Window([uint32]$app.Id,'#32768') -ne [IntPtr]::Zero)
    Stroke @(27)
    Wait-Until { [HostNative]::Window([uint32]$app.Id,'#32768') -eq [IntPtr]::Zero }
    Stroke @(27)
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $query }
    Assert-Host 'toolbar Escape returns to original find input' $true
    Stroke @(27)
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $script:editor }

    Focus-Target $closeBravo
    Stroke @(13)
    Wait-Until { (Text-Of $script:window) -match $more }
    Assert-Host 'inactive clean close preserves active dirty identity' ((Text-Of $script:window).StartsWith('*alpha.md') -and (Text-Of $script:editor) -eq $draft)
    Assert-Host 'all fixture files remain unchanged' ([IO.File]::ReadAllText($alpha) -eq $source -and [IO.File]::ReadAllText($bravo) -eq $second)
    Capture 'b2-after-inactive-close'
    Stroke @(27)
    Guarded-Command 2097 6
    [void][HostNative]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if(-not $app.WaitForExit(6000)){throw 'Host failed to close.'}
    Assert-Host 'interaction host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST TOOLBAR INTERACTION SMOKE: $script:passed PASS (owned real keyboard; isolated pointer messages; no system setting changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if($app -and -not $app.HasExited){Write-Output ([HostNative]::Snapshot([uint32]$app.Id));Capture 'b2-failure'}
    throw
} finally {
    if($app){if(-not $app.HasExited){$app.Kill();[void]$app.WaitForExit(4000)};$app.Dispose()}
    $env:MDV_CONFIG_DIR=$oldConfig
    if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
