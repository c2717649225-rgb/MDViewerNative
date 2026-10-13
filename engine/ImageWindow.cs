using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// Owns the popup HWND and one bitmap reference, independently of its source document.
internal sealed unsafe class ImageWindow : IDisposable
{
    public nint Handle { get; private set; }
    private GCHandle _self;
    private nint _bitmap;
    private float _width, _height, _zoom = 1f, _panX, _panY;
    private bool _hint, _drag, _dragMoved;
    private int _dragX, _dragY;
    private static bool _registered;
    private bool _disposed;

    public bool Show(nint owner, nint bitmap, float width, float height, bool hint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();
        if (bitmap == 0) return false;
        Marshal.AddRef(bitmap);
        _bitmap = bitmap;
        _width = MathF.Max(1f, width); _height = MathF.Max(1f, height);
        _zoom = 1f; _panX = _panY = 0; _drag = _dragMoved = false; _hint = hint;
        try
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                    hInstance = Externs.GetModuleHandleW(null),
                    hCursor = Externs.LoadCursorW(0, (nint)32512),
                    lpszClassName = "MDLightbox"
                };
                if (Externs.RegisterClassExW(in wc) == 0) throw new System.ComponentModel.Win32Exception();
                _registered = true;
            }
            var bounds = new RECT { R = GetSystemMetrics(0), B = GetSystemMetrics(1) };
            var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (GetMonitorInfo(MonitorFromWindow(owner, 2), ref monitor)) bounds = monitor.rcMonitor;
            _self = GCHandle.Alloc(this);
            nint created = Externs.CreateWindowExW(0x80, "MDLightbox", "图片查看", 0x80000000,
                bounds.L, bounds.T, bounds.R - bounds.L, bounds.B - bounds.T,
                owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
            if (created == 0) { Close(); return false; }
            Externs.ShowWindow(created, 1);
            if (_hint) Externs.SetTimer(created, (nint)7, 6000, 0);
            Externs.InvalidateRect(created, 0, 0);
            return true;
        }
        catch { Close(); throw; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        ImageWindow window = null;
        try
        {
            // CREATESTRUCTW begins with lpCreateParams.
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) window = (ImageWindow)GCHandle.FromIntPtr(token).Target;
            if (message == 0x0081 && window != null)
            {
                window.Handle = h;
                Externs.SetWindowLongPtrW(h, -21, token);
            }
            if (message == 0x0082 && window != null)
            {
                Externs.SetWindowLongPtrW(h, -21, 0);
                window.Handle = 0;
                window.Release();
            }
            else if (window != null) return window.HandleMessage(h, message, w, l);
        }
        catch (Exception ex) { AppLog.Error("image window callback", ex); window?.Close(); }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    private nint HandleMessage(nint h, uint message, nint w, nint l)
    {
        switch (message)
        {
            case Externs.WM_PAINT:
                nint dc = Externs.BeginPaint(h, out var paint);
                try
                {
                    Externs.GetClientRect(h, out var rect);
                    PBindExternal(dc, &rect);
                    PBegin(Rt);
                    ulong a = 0, b = 0;
                    try
                    {
                        PFillExternal(0, 0, rect.R, rect.B, BCode);
                        float scale = MathF.Min(rect.R / _width, rect.B / _height) * _zoom;
                        float iw = _width * scale, ih = _height * scale;
                        PDrawBmpExternal(_bitmap, (rect.R - iw) / 2f + _panX, (rect.B - ih) / 2f + _panY, iw, ih);
                        if (_hint) DrawHintBar(rect.R, rect.B, "单击关闭 · 滚轮缩放 · 拖动平移");
                    }
                    finally { PEnd(Rt, &a, &b); }
                }
                finally { Externs.EndPaint(h, in paint); }
                return 0;
            case 0x0113 when w == 7:
                _hint = false;
                Externs.KillTimer(h, (nint)7);
                Externs.InvalidateRect(h, 0, 0);
                return 0;
            case 0x0100 when w == 0x1B:
            case 0x0008:
                Close();
                return 0;
            case 0x020A:
                _zoom = Math.Clamp(_zoom * ((short)((long)w >> 16) > 0 ? 1.2f : 1f / 1.2f), 1f, 16f);
                Externs.InvalidateRect(h, 0, 0);
                return 0;
            case 0x0201:
                _drag = true; _dragMoved = false;
                _dragX = (short)(long)l; _dragY = (short)((long)l >> 16);
                Externs.SetCapture(h);
                return 0;
            case 0x0200 when _drag:
                int x = (short)(long)l, y = (short)((long)l >> 16);
                _dragMoved |= Math.Abs(x - _dragX) + Math.Abs(y - _dragY) > 2;
                _panX += x - _dragX; _panY += y - _dragY;
                _dragX = x; _dragY = y;
                Externs.InvalidateRect(h, 0, 0);
                return 0;
            case 0x0202:
                Externs.ReleaseCapture();
                if (!_dragMoved) Close();
                _drag = _dragMoved = false;
                return 0;
        }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    // I10:Lightbox 手势提示条(前 3 次,6 秒)——canvas 亮卡+ink 字,底部居中;不引入新色
    public unsafe static void DrawHintBar(float clientW, float clientH, string text)
    {
        nint l = Dw.MkLayout(text, FmtToolbar, 10000f);
        if (l == IntPtr.Zero) return;
        Dw.Bind(l);
        var m = default(DWRITE_TEXT_METRICS);
        Dw.GetMetrics(l, &m);
        float bw = m.width + 28f, bh = m.height + 12f;
        float bx = (clientW - bw) / 2f, by = clientH - bh - 28f;
        D2D1_RECT_F rr = default(D2D1_RECT_F);
        rr.left = bx; rr.top = by; rr.right = bx + bw; rr.bottom = by + bh;
        D2D1_ROUNDED_RECT card = default(D2D1_ROUNDED_RECT);
        card.rect = rr; card.radiusX = 4f; card.radiusY = 4f;
        PFillRound(Rt, &card, BCanvas);
        D2D1_POINT_2F pt = default(D2D1_POINT_2F);
        pt.x = bx + 14f; pt.y = by + 6f;
        PDraw(Rt, pt, l, BInk, 0);
        Marshal.Release(l);
    }

    public void Close()
    {
        nint h = Handle;
        Handle = 0;
        if (h != 0 && Externs.IsWindow(h)) Externs.DestroyWindow(h);
        Release();
    }

    private void Release()
    {
        if (_bitmap != 0) { Marshal.Release(_bitmap); _bitmap = 0; }
        if (_self.IsAllocated) _self.Free();
    }

    public void Dispose() { if (_disposed) return; Close(); _disposed = true; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint flags; }
}
