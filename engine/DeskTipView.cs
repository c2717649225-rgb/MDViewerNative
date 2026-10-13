using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// U6a2：desk 悬停提示窗（演示 .tip）。独立的无激活弹出窗，盖得住编辑框、查找框等子窗口；
// 鼠标穿透（HTHTRANSPARENT）；380 ms 延迟由本窗口自己的计时器负责，宿主只报“现在该提示什么”。
// Win11 用系统小圆角（4，演示 6），不画透明度（演示 0.94）：分层窗口与系统圆角不能兼得，取圆角。
internal sealed unsafe class DeskTipView : IDisposable
{
    private nint _hwnd, _owner, _text, _key;
    private GCHandle _self;
    private string _hint, _shown;
    private OverlayBox _box, _shownBox;
    private TipSide _side;
    private float _scale = 1f, _w, _h;
    private static bool _registered;
    private bool _disposed;
    const nint DelayTimer = 1;

    public bool Visible => _shown != null;

    // hint 为 null 即收起。immediate：书签绳一列（绳、刻度、返回书签）悬停即显示，同演示。
    public void Update(nint owner, string hint, OverlayBox box, TipSide side, bool immediate, float scale)
    {
        if (_disposed || owner == 0) return;
        if (hint == null) { Hide(); return; }
        if (hint == _hint && box == _box && scale == _scale) return;
        _owner = owner; _hint = hint; _box = box; _side = side; _scale = scale > 0f ? scale : 1f;
        if (!Ensure()) return;
        if (immediate) { Externs.KillTimer(_hwnd, DelayTimer); Present(); }
        else Externs.SetTimer(_hwnd, DelayTimer, DeskMetrics.TooltipDelayMs, 0);   // 旧提示留到新提示出现（演示同）
    }

    public void Hide()
    {
        _hint = null; _box = default;
        if (_hwnd == 0) return;
        Externs.KillTimer(_hwnd, DelayTimer);
        if (_shown != null) Externs.ShowWindow(_hwnd, 0);
        _shown = null;
    }

    private bool Ensure()
    {
        if (_hwnd != 0) return true;
        if (!_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                hInstance = Externs.GetModuleHandleW(null),
                lpszClassName = "MDDeskTip"
            };
            if (Externs.RegisterClassExW(in wc) == 0) return false;
            _registered = true;
        }
        _self = GCHandle.Alloc(this);
        // WS_POPUP；WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        Externs.CreateWindowExW(0x80 | 0x08000000, "MDDeskTip", null, 0x80000000, 0, 0, 1, 1, _owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (_hwnd == 0) { _self.Free(); return false; }
        uint round = 3 /*DWMWCP_ROUNDSMALL*/, none = 0xFFFFFFFE /*DWMWA_COLOR_NONE*/;
        _ = DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &round, 4);
        _ = DwmSetWindowAttribute(_hwnd, 34 /*DWMWA_BORDER_COLOR*/, &none, 4);
        return true;
    }

    private void Present()
    {
        if (_hint == null || _hwnd == 0 || Rt == 0) return;
        var (text, key) = DeskTip.Split(_hint);
        Release();
        _text = Dw.MkLayout(text, FmtTip != 0 ? FmtTip : FmtToolbar, 10000f);
        _ = key;   // 试用反馈：提示只写做什么，不再附快捷键（快捷键在命令面板里查）
        float keyW = _key != 0 ? DeskTip.KeyGap + Dw.LayoutW(_key) + DeskTip.KeyInsetX * 2f + 2f : 0f;
        _w = MathF.Ceiling(DeskTip.InsetX * 2f + Dw.LayoutW(_text) + keyW);
        _h = MathF.Ceiling(DeskTip.InsetY * 2f + MathF.Max(DeskTip.LineHeight, Dw.LayoutH(_text)));
        Externs.GetClientRect(_owner, out var client);
        var (x, y) = DeskTip.Place(_box.X, _box.Y, _box.W, _box.H, _w, _h, client.R / _scale, client.B / _scale, _side);
        var origin = new Pt { X = (int)MathF.Round(x * _scale), Y = (int)MathF.Round(y * _scale) };
        _ = ClientToScreen(_owner, ref origin);
        // SWP_NOACTIVATE | SWP_SHOWWINDOW；HWND_TOP 在所属窗口之上
        _shown = _hint; _shownBox = _box;
        PopupWindows.ShowPainted(_hwnd, origin.X, origin.Y, (int)MathF.Ceiling(_w * _scale), (int)MathF.Ceiling(_h * _scale), 0x0010);   // 试用修复：换提示时不闪出上一条
    }

    private void Paint(nint h)
    {
        nint dc = Externs.BeginPaint(h, out var paint);
        try
        {
            if (Rt == 0 || _text == 0) return;
            Externs.GetClientRect(h, out var rect);
            PBind(Rt, dc, &rect);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * _scale, 96f * _scale);
            PBegin(Rt);
            ulong a = 0, b = 0;
            try
            {
                var all = new D2D1_RECT_F { right = rect.R / _scale + 1f, bottom = rect.B / _scale + 1f };
                PFill(Rt, &all, BInk);
                float th = Dw.LayoutH(_text);
                PDraw(Rt, new D2D1_POINT_2F { x = DeskTip.InsetX, y = (_h - th) / 2f }, _text, BCanvas, 0);
                if (_key != 0)
                {
                    float kw = Dw.LayoutW(_key), kh = Dw.LayoutH(_key);
                    float kx = DeskTip.InsetX + Dw.LayoutW(_text) + DeskTip.KeyGap, boxH = kh + DeskTip.KeyInsetY * 2f + 3f, ky = (_h - boxH) / 2f;
                    // 演示 .tip kbd：透明底、灰 60% 描边、下边 2
                    SetOpacity(BMuted, 0.6f);
                    var cap = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = kx + 0.5f, top = ky + 0.5f, right = kx + kw + DeskTip.KeyInsetX * 2f + 1.5f, bottom = ky + boxH - 0.5f }, radiusX = DeskTip.KeyRadius, radiusY = DeskTip.KeyRadius };
                    PStrokeRound(Rt, &cap, BMuted, 1f, 0);
                    PLine(Rt, new D2D1_POINT_2F { x = kx + DeskTip.KeyRadius, y = ky + boxH - 1f }, new D2D1_POINT_2F { x = kx + kw + DeskTip.KeyInsetX * 2f + 2f - DeskTip.KeyRadius, y = ky + boxH - 1f }, BMuted, 1f, 0);
                    SetOpacity(BMuted, 1f);
                    PDraw(Rt, new D2D1_POINT_2F { x = kx + 1f + DeskTip.KeyInsetX, y = ky + 1f + DeskTip.KeyInsetY }, _key, BCanvas, 0);
                }
            }
            finally { PEnd(Rt, &a, &b); }
        }
        finally { Externs.EndPaint(h, in paint); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        DeskTipView view = null;
        try
        {
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) view = (DeskTipView)GCHandle.FromIntPtr(token).Target;
            switch (message)
            {
                case 0x0081 when view != null:   // WM_NCCREATE
                    view._hwnd = h;
                    Externs.SetWindowLongPtrW(h, -21, token);
                    break;
                case 0x0084: return -1;          // WM_NCHITTEST → HTTRANSPARENT：鼠标穿透到主窗口
                case 0x0021: return 3;           // WM_MOUSEACTIVATE → MA_NOACTIVATE
                case Externs.WM_PAINT when view != null: view.Paint(h); return 0;
                case 0x0014: return 1;           // WM_ERASEBKGND：全部自绘
                case 0x0113 when view != null && w == DelayTimer:
                    Externs.KillTimer(h, DelayTimer);
                    view.Present();
                    return 0;
                case 0x0082 when view != null:   // WM_NCDESTROY
                    Externs.SetWindowLongPtrW(h, -21, 0);
                    view._hwnd = 0; view._shown = null;
                    if (view._self.IsAllocated) view._self.Free();
                    break;
            }
        }
        catch (Exception ex) { AppLog.Error("desk tip callback", ex); }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    private void Release()
    {
        if (_text != 0) { Marshal.Release(_text); _text = 0; }
        if (_key != 0) { Marshal.Release(_key); _key = 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0) Externs.DestroyWindow(_hwnd);
        Release();
    }

    private struct Pt { public int X, Y; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attr, uint* value, int size);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint h, ref Pt p);
}
