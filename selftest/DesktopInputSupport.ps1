if (-not ('DesktopInput' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopInput {
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size=32)] struct Union { [FieldOffset(0)] public Keyboard key; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public Union data; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    public static IntPtr Layout(IntPtr window) { uint process; return GetKeyboardLayout(GetWindowThreadProcessId(window,out process)); }
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    public static bool FocusOwned(IntPtr target, uint processId) {
        uint owner; uint targetThread=GetWindowThreadProcessId(target,out owner);
        if(owner!=processId || targetThread==0) throw new InvalidOperationException("Not the owned test window.");
        foreach(int key in new[]{16,17,18,91,92}) if((GetAsyncKeyState(key)&0x8000)!=0) throw new InvalidOperationException("A user modifier is held; focus unchanged.");
        uint current=GetCurrentThreadId(), foregroundThread=GetWindowThreadProcessId(GetForegroundWindow(),out owner);
        bool foregroundAttached=false,targetAttached=false;
        try {
            if(foregroundThread!=0 && foregroundThread!=current) foregroundAttached=AttachThreadInput(current,foregroundThread,true);
            if(targetThread!=current && targetThread!=foregroundThread) targetAttached=AttachThreadInput(current,targetThread,true);
            ShowWindow(target,9); BringWindowToTop(target); SetForegroundWindow(target);
            return GetForegroundWindow()==target;
        } finally {
            if(targetAttached) AttachThreadInput(current,targetThread,false);
            if(foregroundAttached) AttachThreadInput(current,foregroundThread,false);
        }
    }
    public static void Stroke(IntPtr expected, params ushort[] keys) {
        if(GetForegroundWindow()!=expected) throw new InvalidOperationException("Foreground changed; no input sent.");
        if(IntPtr.Size!=8) throw new InvalidOperationException("x64 probe required.");
        foreach(int key in new[]{16,17,18,91,92}) if((GetAsyncKeyState(key)&0x8000)!=0) throw new InvalidOperationException("A user modifier is held.");
        foreach(int key in keys) if((GetAsyncKeyState(key)&0x8000)!=0) throw new InvalidOperationException("A user key is held.");
        var input=new Input[keys.Length*2];
        for(int i=0;i<keys.Length;i++) { input[i].type=1; input[i].data.key.vk=keys[i]; input[i].data.key.flags=(keys[i]>=33&&keys[i]<=46)?1u:0u; }
        for(int i=0;i<keys.Length;i++) { input[keys.Length+i]=input[keys.Length-1-i]; input[keys.Length+i].data.key.flags|=2; }
        if(SendInput((uint)input.Length,input,Marshal.SizeOf(typeof(Input)))!=input.Length) {
            var up=new Input[keys.Length];Array.Copy(input,keys.Length,up,0,keys.Length);SendInput((uint)up.Length,up,Marshal.SizeOf(typeof(Input)));
            throw new InvalidOperationException("Input injection was incomplete.");
        }
    }
}
'@
}
function Press-Owned([uint16[]]$Keys) {
    if([DesktopInput]::GetForegroundWindow() -ne $script:window){
        [void][DesktopInput]::FocusOwned($script:window,[uint32]$app.Id)
    }
    [DesktopInput]::Stroke($script:window, $Keys)
}
function Type-OwnedLatin([string]$Text) {
    foreach ($character in $Text.ToUpperInvariant().ToCharArray()) {
        Press-Owned @([uint16]$character)
        Start-Sleep -Milliseconds 40
    }
}
