param([Parameter(Mandatory=$true)][string]$ExePath, [switch]$AllowInput)
$ErrorActionPreference = 'Stop'
if (-not $AllowInput) { throw 'This owned-window test injects real keyboard input. Opt in with -AllowInput on an idle desktop.' }
if (Get-Process -Name MorningDesk -ErrorAction SilentlyContinue) { throw 'Close MorningDesk before this test.' }
. (Join-Path $PSScriptRoot 'HostTestSupport.ps1')
. (Join-Path $PSScriptRoot 'DesktopInputSupport.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopKeys {
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size=32)] struct Union { [FieldOffset(0)] public Keyboard key; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Union data; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr menu, int position);
    [StructLayout(LayoutKind.Sequential)] struct Contrast { public uint size, flags; public IntPtr scheme; }
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] static extern bool SystemParametersInfo(uint action, uint size, ref Contrast value, uint flags);
    public static bool HighContrast() {
        var value = new Contrast { size=(uint)Marshal.SizeOf(typeof(Contrast)) };
        if(!SystemParametersInfo(0x0042,value.size,ref value,0)) throw new InvalidOperationException("Cannot read contrast setting.");
        return (value.flags&1)!=0;
    }
    public static void Stroke(params ushort[] keys) {
        if (IntPtr.Size != 8) throw new InvalidOperationException("This probe requires x64 PowerShell.");
        foreach (int key in new[] {16,17,18,91,92}) if ((GetAsyncKeyState(key)&0x8000)!=0) throw new InvalidOperationException("User modifier is held; input cancelled.");
        foreach (int key in keys) if ((GetAsyncKeyState(key)&0x8000)!=0) throw new InvalidOperationException("User key is held; input cancelled.");
        var input = new Input[keys.Length*2];
        for(int i=0;i<keys.Length;i++) {
            input[i].type=1; input[i].data.key.vk=keys[i];
            input[i].data.key.flags = keys[i]==38 || keys[i]==46 ? 1u : 0u; // Arrow/Delete are extended keys, not numpad keys.
        }
        for(int i=0;i<keys.Length;i++) {
            input[keys.Length+i]=input[keys.Length-1-i]; input[keys.Length+i].data.key.flags |= 2;
        }
        if(SendInput((uint)input.Length,input,Marshal.SizeOf(typeof(Input))) != input.Length) {
            var release = new Input[keys.Length]; Array.Copy(input,keys.Length,release,0,keys.Length);
            SendInput((uint)release.Length,release,Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("Input injection failed or was partial.");
        }
    }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('work\keyboard-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$oldConfig = $env:MDV_CONFIG_DIR
$env:MDV_CONFIG_DIR = Join-Path $fixture 'config'
[void][IO.Directory]::CreateDirectory($env:MDV_CONFIG_DIR)
[IO.File]::WriteAllText((Join-Path $env:MDV_CONFIG_DIR 'config.txt'), "chrome=classic`nhints=0`n")
$previous = [HostNative]::GetForegroundWindow()
$app = $null
$script:passed = 0
function Stroke([IntPtr]$Expected, [uint16[]]$Keys) {
    if ([HostNative]::GetForegroundWindow() -ne $Expected) { [void][DesktopInput]::FocusOwned($Expected,[uint32]$app.Id) }
    if ([HostNative]::GetForegroundWindow() -ne $Expected) { throw 'Cannot reacquire owned foreground; no keys sent.' }
    [DesktopKeys]::Stroke($Keys)
}
try {
    $file = Join-Path $fixture 'keyboard.md'
    $original = '# Saved source'
    [IO.File]::WriteAllText($file, $original)
    $app = Start-Process -FilePath $ExePath -ArgumentList ('"' + $file + '"') -PassThru
    Wait-Until { [HostNative]::Window([uint32]$app.Id, 'MorningDesk') -ne [IntPtr]::Zero }
    $script:window = [HostNative]::Window([uint32]$app.Id, 'MorningDesk')
    [void][DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)
    Wait-Until { [HostNative]::GetForegroundWindow() -eq $script:window }
    Command 1002
    $script:editor = [HostNative]::FindWindowEx($script:window, [IntPtr]::Zero, 'RICHEDIT50W', $null)
    $draft = '# Unsaved source ' + [char]0x4E2D + [char]0x6587
    Edit-Text $draft
    [void](Send $script:editor 0x00B1 2 7)
    $selected = Send $script:editor 0x00B0
    Stroke $script:window @(18,32) # Actual Alt+Space, not WM_SYSCOMMAND injection.
    Wait-Until { [HostNative]::Window([uint32]$app.Id, '#32768') -ne [IntPtr]::Zero }
    Assert-Host 'real Alt+Space opens system menu' $true
    Stroke $script:window @(27)
    Wait-Until { [HostNative]::Window([uint32]$app.Id, '#32768') -eq [IntPtr]::Zero }
    if ([HostNative]::FocusedControl($script:window) -ne $script:editor) { Stroke $script:window @(27) }
    Wait-Until { [HostNative]::FocusedControl($script:window) -eq $script:editor -and (Send $script:editor 0x00B0) -eq $selected }
    Assert-Host 'real Escape closes the system menu and restores editor focus and selection' $true
    Assert-Host 'system menu keys cannot alter draft or disk' ((Text-Of $script:editor) -ceq $draft -and [IO.File]::ReadAllText($file) -ceq $original)
    Write-Output ('Desktop inventory (read-only): monitors=' + [DesktopKeys]::GetSystemMetrics(80) + ' dpi=' + [HostNative]::GetDpiForWindow($script:window) + ' keyboardLayouts=' + [DesktopKeys]::GetKeyboardLayoutList(0,$null))
    Write-Output ('High contrast (read-only): ' + [DesktopKeys]::HighContrast())
    Discard-ToPreview
    [void][HostNative]::PostMessage($script:window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $app.WaitForExit(8000)) { throw 'Host failed to exit.' }
    Assert-Host 'keyboard test host exits cleanly' ($app.ExitCode -eq 0)
    Write-Output "HOST KEYBOARD SMOKE: $script:passed PASS (SendInput; no clipboard or system-setting changes)"
} catch {
    Write-Output $_.ScriptStackTrace
    if ($app) {
        Write-Output ('foreground=' + [HostNative]::GetForegroundWindow())
        Write-Output ('focus=' + [HostNative]::FocusedControl($script:window) + ' expectedEditor=' + $script:editor + ' selected=' + (Send $script:editor 0x00B0) + ' expectedSelection=' + $selected)
        Write-Output ([HostNative]::Snapshot([uint32]$app.Id))
        $menu = [DesktopKeys]::GetSystemMenu($script:window,$false)
        for($i=0; $i -lt [DesktopKeys]::GetMenuItemCount($menu); $i++) { Write-Output ('menu ' + $i + ' id=' + [DesktopKeys]::GetMenuItemID($menu,$i) + ' flags=' + [DesktopKeys]::GetMenuState($menu,$i,0x400)) }
    }
    throw
}
finally {
    $foreground = [HostNative]::GetForegroundWindow()
    $ownedForeground = $foreground -eq [IntPtr]::Zero -or ($app -and ($foreground -eq $script:window))
    if ($app) { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }; $app.Dispose() }
    if ($ownedForeground -and [DesktopKeys]::IsWindow($previous)) { [void][HostNative]::SetForegroundWindow($previous) }
    $env:MDV_CONFIG_DIR = $oldConfig
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
