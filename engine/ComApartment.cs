using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// The application entry point owns COM initialization for its UI thread.
internal sealed class ComApartment : IDisposable
{
    private bool _disposed;
    public ComApartment() => Marshal.ThrowExceptionForHR(Wic.CoInitializeEx(IntPtr.Zero, 2));
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    public void Dispose()
    {
        if (_disposed) return;
        CoUninitialize();
        _disposed = true;
    }
}
