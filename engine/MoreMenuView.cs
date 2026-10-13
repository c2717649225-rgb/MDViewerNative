using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.DeskShapes;

namespace MDViewerNative;

internal interface IMoreMenuHost
{
    nint Owner { get; }
    float Scale { get; }
    IReadOnlyList<MoreItem> Items();
    void Run(int command);
    void Closed();
}

// 试用修复：desk「更多」菜单（演示 #morePop）。与外观面板同一做法：无激活弹出窗，键盘仍在主窗口，由宿主转交 HandleKey；
// 菜单外的按下由宿主在消息循环里收起。条目与几何在 MoreMenu；本类只管窗口与绘制。
internal sealed unsafe class MoreMenuView : IDisposable
{
    readonly IMoreMenuHost _host;
    nint _hwnd;
    GCHandle _self;
    bool _disposed, _tracking;
    static bool _registered;
    MoreFrame _frame;
    int _hover = -1, _focus = -1;
    readonly List<nint> _layouts = new();
    nint _format;
    nint[] _labels = Array.Empty<nint>();
    const string Sans = "Segoe UI";

    public MoreMenuView(IMoreMenuHost host) => _host = host;
    public bool Visible { get; private set; }
    public bool Owns(nint window) => Visible && window != 0 && window == _hwnd;
    float Scale => _host.Scale > 0f ? _host.Scale : 1f;

    // buttonRight：「更多」钮右缘（主窗口客户区 DIP）；top：菜单上缘。keyboard：键盘打开时焦点落在第一项。
    public void Open(float buttonRight, float top, bool keyboard)
    {
        if (_disposed || Visible || _host.Owner == 0 || Rt == 0 || !Ensure()) return;
        Prepare(_host.Items());
        if (_frame.Items.Count == 0) return;
        _hover = -1;
        _focus = keyboard ? MoreMenu.Step(_frame, -1, 1) : -1;
        Externs.GetClientRect(_host.Owner, out var client);
        float x = MoreMenu.PlaceX(buttonRight, client.R / Scale);
        var origin = new Pt { X = (int)MathF.Round(x * Scale), Y = (int)MathF.Round(top * Scale) };
        _ = ClientToScreen(_host.Owner, ref origin);
        Visible = true;
        // SWP_NOACTIVATE | SWP_SHOWWINDOW
        PopupWindows.ShowPainted(_hwnd, origin.X, origin.Y, (int)MathF.Ceiling(_frame.W * Scale), (int)MathF.Ceiling(_frame.H * Scale), 0x0010);
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        Externs.ShowWindow(_hwnd, 0);
        _host.Closed();
    }

    // 主窗口转交的按键：↑↓ Tab 移动，Home / End，Enter / 空格执行，Esc 收起。菜单开着时其余按键也吞掉（同系统菜单）。
    public bool HandleKey(int vk)
    {
        if (!Visible) return false;
        bool shift = (Externs.GetKeyState(0x10) & 0x8000) != 0;
        switch (vk)
        {
            case 0x1B or 0x12: Close(); return true;   // Esc / Alt
            case 0x26: Move(-1); return true;
            case 0x28: Move(1); return true;
            case 0x09: Move(shift ? -1 : 1); return true;
            case 0x24: _focus = MoreMenu.Step(_frame, -1, 1); Externs.InvalidateRect(_hwnd, 0, 0); return true;    // Home
            case 0x23: _focus = MoreMenu.Step(_frame, -1, -1); Externs.InvalidateRect(_hwnd, 0, 0); return true;   // End
            case 0x0D or 0x20:
                if (_focus < 0) Move(1); else Activate(_focus);
                return true;
        }
        return true;
    }

    void Move(int direction)
    {
        _focus = MoreMenu.Step(_frame, _focus < 0 ? _hover : _focus, direction);
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    void Activate(int index)
    {
        if (index < 0 || index >= _frame.Items.Count) return;
        var item = _frame.Items[index];
        if (item.IsRule || !item.Enabled) return;
        Close();
        _host.Run(item.Id);
    }

    // ---------- 排版 ----------
    void Release()
    {
        foreach (nint l in _layouts) Marshal.Release(l);
        _layouts.Clear();
    }

    nint Text(string s, nint format)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        nint l = Dw.MkLayout(s, format, 10000f);
        if (l != 0) _layouts.Add(l);
        return l;
    }

    void Prepare(IReadOnlyList<MoreItem> items)
    {
        Release();
        if (_format == 0) _format = Dw.MkFormat(Sans, 400, MoreMenu.TextSize);
        _labels = new nint[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsRule) continue;
            _labels[i] = Text(items[i].Label, _format);
        }
        nint probe = Text("打开", _format);
        _frame = MoreMenu.Layout(items, probe == 0 ? 17f : Dw.LayoutH(probe));
    }

    // ---------- 绘制 ----------
    void Paint(nint h)
    {
        nint dc = Externs.BeginPaint(h, out var ps);
        try
        {
            if (Rt == 0 || _frame == null) return;
            Externs.GetClientRect(h, out var rect);
            PBind(Rt, dc, &rect);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * Scale, 96f * Scale);
            PBegin(Rt);
            ulong a = 0, b = 0;
            try { PaintMenu(); }
            finally { PEnd(Rt, &a, &b); }
        }
        finally { Externs.EndPaint(h, in ps); }
    }

    void PaintMenu()
    {
        var f = _frame;
        var all = new D2D1_RECT_F { right = f.W + 1f, bottom = f.H + 1f };
        PFill(Rt, &all, BCanvas);
        StrokeRound(0.5f, 0.5f, f.W - 1f, f.H - 1f, 8f, HighContrast ? BInk : BEdge, 1f);   // 演示 .pop 1px 边
        for (int i = 0; i < f.Items.Count; i++)
        {
            var r = f.Rows[i];
            var item = f.Items[i];
            if (item.IsRule)
            {
                float y = r.Y + MoreMenu.RuleGap + 0.5f;
                PLine(Rt, new D2D1_POINT_2F { x = r.X + MoreMenu.RuleInsetX, y = y }, new D2D1_POINT_2F { x = r.X + r.W - MoreMenu.RuleInsetX, y = y }, BHair, 1f, 0);
                continue;
            }
            bool hot = item.Enabled && (i == _hover || i == _focus);
            if (hot) FillRound(r.X, r.Y, r.W, r.H, MoreMenu.ItemRadius, HighContrast ? BCanvas : BSurfaceHover);   // 演示 .mi:hover → paper-2；暗纸加大反差
            if (i == _focus) StrokeRound(r.X, r.Y, r.W, r.H, MoreMenu.ItemRadius, HighContrast ? BInk : BControlBorder, 1f);
            float opacity = item.Enabled ? 1f : 0.4f;   // 演示 .mi:disabled opacity .4
            float cy = r.Y + r.H / 2f, x = r.X + MoreMenu.ItemInsetX;
            SetOpacity(BInk, opacity); SetOpacity(BInk2, opacity);
            try
            {
                if (item.Icon != MoreIcon.None) Icon(item.Icon, x, cy - MoreMenu.IconSize / 2f, MoreMenu.IconSize, BInk2);
                float tx = x + MoreMenu.IconSize + MoreMenu.IconGap;
                if (_labels[i] != 0) Draw(_labels[i], tx, cy - Dw.LayoutH(_labels[i]) / 2f, BInk);
            }
            finally { SetOpacity(BInk, 1f); SetOpacity(BInk2, 1f); }
        }
    }

    // 演示 .mi svg：20 格、描边 1.5、圆头，缩到 size。
    static void Icon(MoreIcon icon, float x, float y, float size, nint brush)
    {
        float k = size / 20f, sw = 1.5f * k;
        float X(float u) => x + u * k;
        float Y(float v) => y + v * k;
        void L(float u1, float v1, float u2, float v2) => StrokeLine(X(u1), Y(v1), X(u2), Y(v2), brush, sw);
        void Arc(float cu, float cv, float r, float from, float to, int steps = 10)
        {
            for (int s = 0; s < steps; s++)
            {
                float a0 = from + (to - from) * s / steps, a1 = from + (to - from) * (s + 1) / steps;
                L(cu + r * MathF.Cos(a0), cv + r * MathF.Sin(a0), cu + r * MathF.Cos(a1), cv + r * MathF.Sin(a1));
            }
        }
        const float Q = MathF.PI / 2f;
        switch (icon)
        {
            case MoreIcon.Open:   // 文件夹：M3 6.5V15a1.5 1.5 0 0 0 1.5 1.5h11A1.5 1.5 0 0 0 17 15V8a1.5 1.5 0 0 0-1.5-1.5H10L8.3 4.5H4.5A1.5 1.5 0 0 0 3 6z
                L(3f, 6f, 3f, 15f); Arc(4.5f, 15f, 1.5f, 2f * Q, Q, 4); L(4.5f, 16.5f, 15.5f, 16.5f); Arc(15.5f, 15f, 1.5f, Q, 0f, 4);
                L(17f, 15f, 17f, 8f); Arc(15.5f, 8f, 1.5f, 0f, -Q, 4); L(15.5f, 6.5f, 10f, 6.5f); L(10f, 6.5f, 8.3f, 4.5f);
                L(8.3f, 4.5f, 4.5f, 4.5f); Arc(4.5f, 6f, 1.5f, -Q, -2f * Q, 4); break;
            case MoreIcon.SaveAs:   // 文档加下箭头：M6 3h5.5L15 6.5V17H5V3zM10 8.5v5M7.8 11.3 10 13.5l2.2-2.2
                L(5f, 3f, 11.5f, 3f); L(11.5f, 3f, 15f, 6.5f); L(15f, 6.5f, 15f, 17f); L(15f, 17f, 5f, 17f); L(5f, 17f, 5f, 3f);
                L(10f, 8.5f, 10f, 13.5f); L(7.8f, 11.3f, 10f, 13.5f); L(10f, 13.5f, 12.2f, 11.3f); break;
            case MoreIcon.Reopen:   // 回转箭头：M4 10a6 6 0 1 0 2-4.5M4 3.5V7h3.5（大弧、逆时针：左 → 下 → 右 → 上，止于 (6, 5.5)）
                Arc(10f, 10f, 6f, MathF.PI, MathF.PI - 5.4385f, 18); L(4f, 3.5f, 4f, 7f); L(4f, 7f, 7.5f, 7f); break;
            case MoreIcon.Export:   // 下载：M10 3v9M6.5 8.5 10 12l3.5-3.5M4 14v2.5h12V14
                L(10f, 3f, 10f, 12f); L(6.5f, 8.5f, 10f, 12f); L(10f, 12f, 13.5f, 8.5f); L(4f, 14f, 4f, 16.5f); L(4f, 16.5f, 16f, 16.5f); L(16f, 16.5f, 16f, 14f); break;
            case MoreIcon.Print:   // 打印机
                L(6f, 7f, 6f, 3.5f); L(6f, 3.5f, 14f, 3.5f); L(14f, 3.5f, 14f, 7f);
                L(6f, 14f, 4f, 14f); L(4f, 14f, 4f, 8.5f); Arc(5.5f, 8.5f, 1.5f, 2f * Q, 3f * Q, 4); L(5.5f, 7f, 14.5f, 7f); Arc(14.5f, 8.5f, 1.5f, -Q, 0f, 4); L(16f, 8.5f, 16f, 14f); L(16f, 14f, 14f, 14f);
                L(6f, 11.5f, 14f, 11.5f); L(14f, 11.5f, 14f, 16.5f); L(14f, 16.5f, 6f, 16.5f); L(6f, 16.5f, 6f, 11.5f); break;
            case MoreIcon.Palette:   // 提示符：M5 7l3 3-3 3M10 13.5h5
                L(5f, 7f, 8f, 10f); L(8f, 10f, 5f, 13f); L(10f, 13.5f, 15f, 13.5f); break;
        }
    }

    // ---------- 窗口 ----------
    bool Ensure()
    {
        if (_hwnd != 0) return true;
        if (!_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x00020000 /*CS_DROPSHADOW*/,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                hInstance = Externs.GetModuleHandleW(null),
                hCursor = LoadCursorW(0, 32512),
                lpszClassName = "MDMoreMenu"
            };
            if (Externs.RegisterClassExW(in wc) == 0) return false;
            _registered = true;
        }
        _self = GCHandle.Alloc(this);
        nint previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(_host.Owner));
        // WS_POPUP；WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        Externs.CreateWindowExW(0x80 | 0x08000000, "MDMoreMenu", "更多", 0x80000000, 0, 0, 1, 1, _host.Owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (previous != 0) SetThreadDpiAwarenessContext(previous);
        if (_hwnd == 0) { _self.Free(); return false; }
        uint round = 2 /*DWMWCP_ROUND*/;
        _ = DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &round, 4);
        return true;
    }

    void Pointer(uint message, nint l)
    {
        float x = (short)(l & 0xFFFF) / Scale, y = (short)((l >> 16) & 0xFFFF) / Scale;
        int hit = MoreMenu.Hit(_frame, x, y);
        if (message == 0x0200)
        {
            if (!_tracking)
            {
                var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = _hwnd };
                _tracking = TrackMouseEvent(ref tme);
            }
            if (hit != _hover) { _hover = hit; Externs.InvalidateRect(_hwnd, 0, 0); }
            return;
        }
        if (hit >= 0) Activate(hit);   // 抬起时执行（同系统菜单）
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        MoreMenuView view = null;
        try
        {
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) view = (MoreMenuView)GCHandle.FromIntPtr(token).Target;
            switch (message)
            {
                case 0x0081 when view != null:   // WM_NCCREATE
                    view._hwnd = h;
                    Externs.SetWindowLongPtrW(h, -21, token);
                    break;
                case 0x0021: return 3;           // WM_MOUSEACTIVATE → MA_NOACTIVATE：键盘留在主窗口
                case Externs.WM_PAINT when view != null: view.Paint(h); return 0;
                case 0x0014: return 1;           // WM_ERASEBKGND：全部自绘
                case 0x0200 or 0x0202 when view != null: view.Pointer(message, l); return 0;
                case 0x02A3 when view != null:   // WM_MOUSELEAVE
                    view._tracking = false;
                    if (view._hover >= 0) { view._hover = -1; Externs.InvalidateRect(h, 0, 0); }
                    return 0;
                case 0x0082 when view != null:   // WM_NCDESTROY
                    Externs.SetWindowLongPtrW(h, -21, 0);
                    view._hwnd = 0; view.Visible = false;
                    if (view._self.IsAllocated) view._self.Free();
                    break;
            }
        }
        catch (Exception ex) { AppLog.Error("more menu callback", ex); }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0) Externs.DestroyWindow(_hwnd);
        Release();
        if (_format != 0) Marshal.Release(_format);
        _format = 0;
    }

    struct Pt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint h, int attr, uint* value, int size);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref Pt p);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern nint GetWindowDpiAwarenessContext(nint h);
}
