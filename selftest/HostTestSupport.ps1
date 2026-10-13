Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HostNative {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string s);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW")] public static extern IntPtr Send(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode)] public static extern IntPtr SendText(IntPtr h, uint m, IntPtr w, string l, uint f, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode)] public static extern IntPtr ReadBuffer(IntPtr h, uint m, IntPtr w, StringBuilder l, uint f, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint="SetWindowPos")] private static extern bool NativeSetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetClientRect")] private static extern bool NativeGetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint="GetWindowRect")] private static extern bool NativeGetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint="ClientToScreen")] private static extern bool NativeClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    // PowerShell may be DPI-unaware while the viewer is per-monitor aware. Query
    // physical pixels explicitly, then restore only this calling thread's context.
    public static bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int ht, uint flags) {
        var previous=SetThreadDpiAwarenessContext((IntPtr)(-4));
        try { return NativeSetWindowPos(h,a,x,y,w,ht,flags); }
        finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    public static bool GetClientRect(IntPtr h, out RECT r) {
        var previous=SetThreadDpiAwarenessContext((IntPtr)(-4));
        try { return NativeGetClientRect(h,out r); }
        finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    public static bool GetWindowRect(IntPtr h, out RECT r) {
        var previous=SetThreadDpiAwarenessContext((IntPtr)(-4));
        try { return NativeGetWindowRect(h,out r); }
        finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    public static bool ClientToScreen(IntPtr h, ref POINT p) {
        var previous=SetThreadDpiAwarenessContext((IntPtr)(-4));
        try { return NativeClientToScreen(h,ref p); }
        finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    // Mouse coordinates below are physical client pixels. DPI-unaware senders can
    // otherwise have their coordinates virtualized before an aware host sees them.
    // This changes only the calling test thread, never Windows display settings.
    public static bool PostPhysical(IntPtr h, uint m, IntPtr w, IntPtr l) {
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        if (previous == IntPtr.Zero) throw new InvalidOperationException("Cannot set test sender DPI awareness.");
        try { return PostMessage(h, m, w, l); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    public static long SendPhysical(IntPtr h, uint m, IntPtr w, IntPtr l) {
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        if (previous == IntPtr.Zero) throw new InvalidOperationException("Cannot set test sender DPI awareness.");
        try {
            IntPtr result;
            if (Send(h, m, w, l, 2, 4000, out result) == IntPtr.Zero) throw new InvalidOperationException("Send timeout: " + m);
            return result.ToInt64();
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
    public static bool ClickPhysicalClient(IntPtr h, int x, int y) {
        if (x < short.MinValue || x > short.MaxValue || y < short.MinValue || y > short.MaxValue)
            throw new ArgumentOutOfRangeException("Mouse coordinates exceed LPARAM bounds.");
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        if (previous == IntPtr.Zero) throw new InvalidOperationException("Cannot set test sender DPI awareness.");
        try {
            IntPtr point = (IntPtr)((y << 16) | (x & 0xFFFF)), result;
            if (Send(h, 0x0201, (IntPtr)1, point, 2, 4000, out result) == IntPtr.Zero)
                throw new InvalidOperationException("Mouse down delivery timed out.");
            // Release may open a modal dialog; keep it asynchronous for the test driver.
            return PostMessage(h, 0x0202, IntPtr.Zero, point);
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO s);
    public struct RECT { public int L,T,R,B; }
    public struct POINT { public int X,Y; }
    public struct SCROLLINFO { public uint cbSize, fMask; public int nMin,nMax; public uint nPage; public int nPos,nTrackPos; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
        public uint size, flags;
        public IntPtr active, focus, capture, menuOwner, moveSize, caret;
        public RECT caretRect;
    }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    public static IntPtr FocusedControl(IntPtr window) {
        uint process; uint thread = GetWindowThreadProcessId(window, out process);
        var info = new GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(GUITHREADINFO)) };
        return GetGUIThreadInfo(thread, ref info) ? info.focus : IntPtr.Zero;
    }
    public static IntPtr Window(uint process, string className) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h,p) => { uint id; GetWindowThreadProcessId(h,out id); var s=new StringBuilder(256); GetClassName(h,s,256); if(id==process && s.ToString()==className && ((className!="#32770" && className!="#32768") || IsWindowVisible(h))) found=h; return true; },IntPtr.Zero);
        return found;
    }
    public static IntPtr Dialog(uint process) { return Window(process, "#32770"); }
    public static string Snapshot(uint process) {
        var result = new StringBuilder();
        EnumWindows((h,p) => { uint id; GetWindowThreadProcessId(h,out id); if(id==process) {
            var cls=new StringBuilder(256); GetClassName(h,cls,256);
            var title=new StringBuilder(256); GetWindowText(h,title,256);
            result.AppendLine(h+" "+cls+" visible="+IsWindowVisible(h)+" "+title);
            EnumChildWindows(h, (child,arg) => {
                var cc=new StringBuilder(256); GetClassName(child,cc,256);
                var ct=new StringBuilder(256); GetWindowText(child,ct,256);
                result.AppendLine("  "+child+" id="+GetDlgCtrlID(child)+" "+cc+" "+ct);
                return true;
            },IntPtr.Zero);
        } return true; },IntPtr.Zero);
        return result.ToString();
    }
    public static IntPtr Control(IntPtr parent, int controlId, string className = null) {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (h,p) => {
            var cls=new StringBuilder(256); GetClassName(h,cls,256);
            if(GetDlgCtrlID(h)==controlId && (className==null || cls.ToString().Equals(className,StringComparison.OrdinalIgnoreCase))) result=h;
            return result==IntPtr.Zero;
        }, IntPtr.Zero);
        return result;
    }
    public static string ReadText(IntPtr h) {
        IntPtr length;
        if (Send(h, 0x000E, IntPtr.Zero, IntPtr.Zero, 2, 4000, out length) == IntPtr.Zero) throw new Exception("Get text length timed out.");
        var buffer = new StringBuilder(length.ToInt32() + 1);
        IntPtr result;
        if (ReadBuffer(h, 0x000D, (IntPtr)buffer.Capacity, buffer, 2, 4000, out result) == IntPtr.Zero) throw new Exception("Get text timed out.");
        return buffer.ToString();
    }
}
'@

# A timeout is the one failure the window suites cannot explain after the fact, so callers
# may hand in a diagnostic block; it runs before the throw and lands in the log.
function Wait-Until([scriptblock]$Condition, [scriptblock]$Diagnostic = $null) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
    if ($Diagnostic) {
        Write-Output '--- wait timeout: observed state ---'
        try { foreach ($line in @(& $Diagnostic)) { Write-Output ('    ' + $line) } }
        catch { Write-Output ('    diagnostic failed: ' + $_.Exception.Message) }
    }
    throw 'Timed out waiting for host state.'
}
function Assert-Host([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL $Name" }
    $script:passed++
    Write-Output "PASS $Name"
}
function Text-Of([IntPtr]$Window) {
    return [HostNative]::ReadText($Window)
}
function Send([IntPtr]$Window, [uint32]$Message, [long]$W = 0, [long]$L = 0) {
    $result = [IntPtr]::Zero
    if ([HostNative]::Send($Window, $Message, [IntPtr]$W, [IntPtr]$L, 2, 4000, [ref]$result) -eq [IntPtr]::Zero) { throw "Send timeout: $Message" }
    return $result.ToInt64()
}
# Mouse messages with physical client coordinates (see HostNative.SendPhysical).
function Send-Physical([IntPtr]$Window, [uint32]$Message, [long]$W = 0, [long]$L = 0) {
    return [HostNative]::SendPhysical($Window, $Message, [IntPtr]$W, [IntPtr]$L)
}
# Left edge (DIP) of the reading column when no directory is pinned; mirrors WindowFit.PadFor (S80).
function Column-Left([double]$ViewDip) {
    if ($ViewDip -le 360) { return 12.0 }
    if ($ViewDip -lt 400) { return 12.0 + 8.0 * ($ViewDip - 360.0) / 40.0 }
    if ($ViewDip -le 640) { return 20.0 }
    if ($ViewDip -lt 752) { return 20.0 + 28.0 * ($ViewDip - 640.0) / 112.0 }
    return 48.0
}
function Command([int]$Id) { [void](Send $script:window 0x0111 ((1 -shl 16) -bor $Id)) }
# U12: the desk chrome keeps the Home tab in the Ctrl+Tab cycle (as the design does); document tests step over it.
function Next-Document { Command 1009; if ((Text-Of $script:window).StartsWith("起始页")) { Command 1009 } }
# U12: closing the last tab shows the start page on desk (classic keeps a blank document); New gives the blank document there.
function Close-LastTab { Command 1008; if ((Text-Of $script:window).StartsWith("起始页")) { Command 2200; if ($script:editor -and [HostNative]::IsWindowVisible($script:editor)) { Command 1002 } } }
function Edit-Text([string]$Value) {
    [void](Send $script:editor 0x00B1 0 -1)
    $result = [IntPtr]::Zero
    if ([HostNative]::SendText($script:editor, 0x00C2, [IntPtr]1, $Value, 2, 4000, [ref]$result) -eq [IntPtr]::Zero) { throw 'Editor replacement timed out.' }
}
function Choose-Dialog([IntPtr]$Dialog, [int]$Choice) {
    Wait-Until {
        [HostNative]::Control($Dialog, $Choice) -ne [IntPtr]::Zero -or
        ($Choice -eq 1 -and [HostNative]::Control($Dialog, 2) -ne [IntPtr]::Zero) -or
        ($Choice -eq 1000 -and [HostNative]::Control($Dialog, 6) -ne [IntPtr]::Zero) -or
        ($Choice -eq 1001 -and [HostNative]::Control($Dialog, 7) -ne [IntPtr]::Zero) -or
        [HostNative]::FindWindowEx($Dialog, [IntPtr]::Zero, 'DirectUIHWND', $null) -ne [IntPtr]::Zero
    }
    $button = [HostNative]::Control($Dialog, $Choice)
    if ($button -eq [IntPtr]::Zero -and $Choice -eq 1) { $button = [HostNative]::Control($Dialog, 2) }
    if ($button -eq [IntPtr]::Zero -and $Choice -eq 1000) { $button = [HostNative]::Control($Dialog, 6) }
    if ($button -eq [IntPtr]::Zero -and $Choice -eq 1001) { $button = [HostNative]::Control($Dialog, 7) }
    if ($button -ne [IntPtr]::Zero) { [void](Send $button 0x00F5) }
    else { [void](Send $Dialog 0x0466 $Choice) } # TDM_CLICK_BUTTON: native TaskDialog uses DirectUI controls.
}
function Guarded-Command([int]$Id, [int]$Choice) {
    [void][HostNative]::PostMessage($script:window, 0x0111, [IntPtr]((1 -shl 16) -bor $Id), [IntPtr]::Zero)
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -ne [IntPtr]::Zero }
    $dialog = [HostNative]::Dialog([uint32]$app.Id)
    Choose-Dialog $dialog $Choice
    Wait-Until { [HostNative]::Dialog([uint32]$app.Id) -eq [IntPtr]::Zero }
    [void](Send $script:window 0)
}
# Named test fixtures are unchanged on disk at these call sites. Discard is now
# explicit refresh (with confirmation); merely showing a preview must keep the draft.
function Discard-ToPreview {
    Guarded-Command 2097 6
    Command 1002
}
function Capture([string]$Name) {
    [void][HostNative]::SetWindowPos($script:window, [IntPtr]::Zero, 120, 120, 0, 0, 5)
    [void][HostNative]::RedrawWindow($script:window, [IntPtr]::Zero, [IntPtr]::Zero, 0x0185)
    Start-Sleep -Milliseconds 350
    $wr = New-Object HostNative+RECT
    $cr = New-Object HostNative+RECT
    $point = New-Object HostNative+POINT
    [void][HostNative]::GetWindowRect($script:window, [ref]$wr)
    [void][HostNative]::GetClientRect($script:window, [ref]$cr)
    [void][HostNative]::ClientToScreen($script:window, [ref]$point)
    $bitmap = New-Object Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { [void][HostNative]::PrintWindow($script:window, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    $cropRect = New-Object Drawing.Rectangle ($point.X - $wr.L), ($point.Y - $wr.T), $cr.R, $cr.B
    $crop = $bitmap.Clone($cropRect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { $crop.Save((Join-Path $OutputDirectory ($Name + '.png'))) }
    finally { $crop.Dispose(); $bitmap.Dispose() }
}
# U12: read-only scroll query (WM_APP+0x3A; 0 pos, 1 max, 2 page, physical px). The desk chrome has no system scrollbar.
function Scroll-Query([int]$Which) { return [int](Send $script:window 0x803A $Which 0) }
function Scroll-Extent { return (Scroll-Query 1) }
