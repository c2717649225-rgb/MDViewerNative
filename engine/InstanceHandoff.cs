using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// UI-thread-owned mailbox. COPYDATA is copied before acknowledging; disk I/O and dialogs
// happen later in the normal message loop, never while a sender holds a synchronous call.
internal sealed unsafe class InstanceHandoff
{
    internal const uint CopyDataMessage = 0x004A;
    internal const uint DispatchMessage = 0x8001;
    internal const int Protocol = 0x4D445631;
    readonly Queue<string> _pending = new();
    bool _draining;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Payload { public nint Kind; public int Bytes; public nint Text; }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    static extern nint Send(nint window, uint message, nint sender, ref Payload data, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);

    internal static bool Forward(nint window, string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length >= 32768 || path.Contains('\0')) return false;
        // Transfer any activation permission before the synchronous handoff. The receiver
        // opens the document later, after this short-lived sender may already have exited.
        if (GetWindowThreadProcessId(window, out uint processId) != 0) _ = AllowSetForegroundWindow(processId);
        fixed (char* text = path)
        {
            var data = new Payload { Kind = Protocol, Bytes = (path.Length + 1) * 2, Text = (nint)text };
            return Send(window, CopyDataMessage, 0, ref data, 0x0002, 10000, out var accepted) != 0 && accepted == 1;
        }
    }

    internal bool Accept(nint dataPointer, Func<bool> wake)
    {
        if (dataPointer == 0 || _pending.Count >= 256) return false;
        var data = Marshal.PtrToStructure<Payload>(dataPointer);
        if (data.Kind != Protocol || data.Text == 0 || data.Bytes < 4 || data.Bytes > 65536 || (data.Bytes & 1) != 0) return false;
        int length = data.Bytes / 2 - 1;
        if (Marshal.ReadInt16(data.Text, length * 2) != 0) return false;
        string path = Marshal.PtrToStringUni(data.Text, length);
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path)) return false;
        // Posting cannot dispatch on this thread before we return. Failed posts must not
        // leave an accepted request stranded in the mailbox.
        if (!wake()) return false;
        _pending.Enqueue(path);
        return true;
    }

    internal void Drain(Action<string> open)
    {
        if (_draining) return; // Dialogs can run nested message loops.
        _draining = true;
        try { while (_pending.TryDequeue(out var path)) open(path); }
        finally { _draining = false; }
    }
}
