if (-not ('ClipboardSnapshot' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public sealed class ClipboardSnapshot {
    sealed class Item { public uint Format; public byte[] Bytes; }
    readonly List<Item> items=new List<Item>();
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format,IntPtr data);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    static bool OpenOwned(IntPtr owner) {
        for(int i=0;i<24;i++) {if(OpenClipboard(owner))return true;System.Threading.Thread.Sleep(25);}
        return false;
    }
    public static ClipboardSnapshot Capture(IntPtr owner) {
        var result=new ClipboardSnapshot();
        if(!OpenOwned(owner)) throw new InvalidOperationException("Clipboard is busy; no test changes made.");
        try {
            uint format=0;long total=0;
            while((format=EnumClipboardFormats(format))!=0) {
                if(format==2 || format==3 || format==14 || format==0x80 || (format>=0x300&&format<=0x3ff))
                    throw new InvalidOperationException("Clipboard contains non-memory handles; test will not replace it.");
                IntPtr handle=GetClipboardData(format);
                ulong size=GlobalSize(handle).ToUInt64();
                if(handle==IntPtr.Zero || size==0 || size>16777216 || (total+=(long)size)>67108864)
                    throw new InvalidOperationException("Clipboard format cannot be safely snapshotted; unchanged.");
                IntPtr data=GlobalLock(handle);
                if(data==IntPtr.Zero) throw new InvalidOperationException("Clipboard lock failed; unchanged.");
                try { var bytes=new byte[(int)size];Marshal.Copy(data,bytes,0,bytes.Length);result.items.Add(new Item{Format=format,Bytes=bytes}); }
                finally {GlobalUnlock(handle);}
            }
            return result;
        } finally {CloseClipboard();}
    }
    public static string Text(IntPtr owner) {
        if(!OpenOwned(owner)) throw new InvalidOperationException("Clipboard is busy.");
        try {
            IntPtr handle=GetClipboardData(13);if(handle==IntPtr.Zero)return null;
            int length=checked((int)GlobalSize(handle).ToUInt64()/2);
            IntPtr data=GlobalLock(handle);if(data==IntPtr.Zero)throw new InvalidOperationException("Clipboard lock failed.");
            try {string text=Marshal.PtrToStringUni(data,length);int end=text.IndexOf('\0');return end<0?text:text.Substring(0,end);}
            finally {GlobalUnlock(handle);}
        } finally {CloseClipboard();}
    }
    public bool Restore(IntPtr owner,uint expectedSequence) {
        var allocated=new List<IntPtr>();
        try {
            foreach(var item in items) {
                IntPtr memory=GlobalAlloc(2,(UIntPtr)item.Bytes.Length);if(memory==IntPtr.Zero)throw new OutOfMemoryException();
                allocated.Add(memory);IntPtr data=GlobalLock(memory);if(data==IntPtr.Zero)throw new InvalidOperationException("Restore allocation lock failed.");
                try {Marshal.Copy(item.Bytes,0,data,item.Bytes.Length);}finally {GlobalUnlock(memory);}
            }
            if(!OpenOwned(owner))throw new InvalidOperationException("Clipboard busy; snapshot still in memory.");
            try {
                if(GetClipboardSequenceNumber()!=expectedSequence)return false; // Preserve a newer user copy.
                if(!EmptyClipboard())throw new InvalidOperationException("Clipboard restore failed.");
                for(int i=0;i<items.Count;i++) {
                    if(SetClipboardData(items[i].Format,allocated[i])==IntPtr.Zero)throw new InvalidOperationException("Clipboard format restore failed.");
                    allocated[i]=IntPtr.Zero;
                }
                return true;
            } finally {CloseClipboard();}
        } finally {foreach(IntPtr memory in allocated)if(memory!=IntPtr.Zero)GlobalFree(memory);}
    }
}
'@
}
