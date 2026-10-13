using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.DeskShapes;

namespace MDViewerNative;

internal interface IPaletteHost
{
    nint Owner { get; }
    float Scale { get; }
    bool Animate { get; }
    IReadOnlyList<PaletteEntry> Commands();
    IReadOnlyList<PaletteEntry> Recent();
    void Run(PaletteEntry entry);
    void Closed();
}

// U11：命令面板（演示 .pal-wrap / .pal）。独立的弹出窗盖住主窗口整个客户区——编辑框等子窗口也在下面——
// 底图是打开那一刻主窗口的快照（缩放两次近似 3px 模糊），上面罩 45% 桌面色，再画面板；输入框是面板里的原生 EDIT
// （输入法、剪贴板、光标都交给系统）。面板开着时下面不会变（它独占键盘与鼠标），快照不必刷新。
// 条目、筛选、版面、命中在 CommandSearch；本类只管窗口、绘制与输入。
internal sealed unsafe class PaletteView : IDisposable
{
    readonly IPaletteHost _host;
    nint _hwnd, _edit, _editProc, _font, _snapshot;
    int _snapW, _snapH;
    GCHandle _self;
    bool _disposed, _closing;
    static bool _registered;
    readonly ImeComposition _ime = new();
    IReadOnlyList<PaletteEntry> _commands = Array.Empty<PaletteEntry>(), _recent = Array.Empty<PaletteEntry>();
    List<PaletteEntry> _items = new();
    string _query = "";
    int _selected;
    float _scroll, _viewW, _viewH;
    PaletteFrame _frame;
    long _openedAt;
    readonly List<nint> _layouts = new();
    readonly Dictionary<(uint, float), nint> _formats = new();
    nint[] _labels = Array.Empty<nint>(), _subs = Array.Empty<nint>(), _keys = Array.Empty<nint>();
    nint[] _headers = new nint[2];
    nint _empty;
    const nint AnimTimer = 1;
    readonly PaintBuffer _buffer = new();
    const int UnrollMs = 450;
    const string Sans = "Segoe UI";

    public PaletteView(IPaletteHost host) => _host = host;
    public bool Visible { get; private set; }
    public bool Owns(nint window) => Visible && window != 0 && (window == _hwnd || window == _edit);
    float Scale => _host.Scale > 0f ? _host.Scale : 1f;

    public void Open()
    {
        if (_disposed || Visible || _host.Owner == 0 || Rt == 0) return;
        if (!Ensure()) return;
        Externs.GetClientRect(_host.Owner, out var client);
        if (client.R <= 0 || client.B <= 0) return;
        _viewW = client.R / Scale; _viewH = client.B / Scale;
        TakeSnapshot(client.R, client.B);
        _commands = _host.Commands() ?? Array.Empty<PaletteEntry>();
        _recent = _host.Recent() ?? Array.Empty<PaletteEntry>();
        _query = ""; _selected = 0; _scroll = 0f;
        Externs.SetWindowTextW(_edit, "");
        Refresh();
        var origin = new Pt();
        _ = ClientToScreen(_host.Owner, ref origin);
        Visible = true;
        _openedAt = _host.Animate ? Environment.TickCount64 : 0;
        PlaceEdit();
        // 盖在所属窗口的客户区上；试用修复：画好第一帧再露面
        PopupWindows.ShowPainted(_hwnd, origin.X, origin.Y, client.R, client.B, 0);
        _ = SetForegroundWindow(_hwnd);
        Externs.SetFocus(_edit);
        if (_openedAt != 0) Externs.SetTimer(_hwnd, AnimTimer, 16, 0);
    }

    public void Close()
    {
        if (!Visible || _closing) return;
        _closing = true;
        try
        {
            Visible = false;
            Externs.KillTimer(_hwnd, AnimTimer);
            _ = SetForegroundWindow(_host.Owner);   // 先把激活还给主窗口，再藏起自己，避免 Z 序跳动
            Externs.ShowWindow(_hwnd, 0);
            DropSnapshot();
            Release();
            _host.Closed();
        }
        finally { _closing = false; }
    }

    void Run(int entry)
    {
        if (entry < 0 || entry >= _items.Count) return;
        var e = _items[entry];
        if (e.Kind == PaletteKind.Command && !e.Enabled) return;   // 禁用的命令只显示原因
        Close();
        _host.Run(e);
    }

    // ---------- 条目与排版 ----------
    nint Fmt(uint weight, float size)
    {
        if (!_formats.TryGetValue((weight, size), out nint f)) _formats[(weight, size)] = f = Dw.MkFormat(Sans, weight, size);
        return f;
    }

    nint Keep(nint layout) { if (layout != 0) _layouts.Add(layout); return layout; }

    void Release()
    {
        foreach (nint l in _layouts) Marshal.Release(l);
        _layouts.Clear();
        _labels = _subs = _keys = Array.Empty<nint>();
        _headers = new nint[2]; _empty = 0;
    }

    void Refresh()
    {
        Release();
        _items = CommandSearch.Filter(_commands, _recent, _query);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _items.Count - 1));
        int n = _items.Count;
        _labels = new nint[n]; _subs = new nint[n]; _keys = new nint[n];
        nint row = Fmt(400, CommandSearch.RowSize), sub = Fmt(400, CommandSearch.SubSize), key = Fmt(400, CommandSearch.KeySize);
        var (hs, hl) = (0, 0);
        for (int i = 0; i < n; i++)
        {
            var e = _items[i];
            _labels[i] = Keep(Dw.MkLayout(e.Label, row, 10000f));
            (hs, hl) = CommandSearch.Highlight(e.Label, _query);
            if (hl > 0)
            {
                var range = new DWRITE_TEXT_RANGE { first = (uint)hs, length = (uint)hl };
                Dw.SetDrawingEffect(_labels[i], BAccent, range);
                SetFontWeight(_labels[i], 600, range);
            }
            if (!string.IsNullOrEmpty(e.Sub)) _subs[i] = Keep(Dw.MkLayout(e.Sub, sub, 10000f));
            if (!string.IsNullOrEmpty(e.Accel)) _keys[i] = Keep(Dw.MkLayout(e.Accel, key, 10000f));
        }
        string[] heads = { CommandSearch.CommandsHeader, CommandSearch.RecentHeader };
        for (int g = 0; g < 2; g++)
        {
            _headers[g] = Keep(Dw.MkLayout(heads[g], Fmt(400, CommandSearch.GroupSize), 10000f));
            Dw.SetCharacterSpacing(_headers[g], 0f, CommandSearch.GroupSize * CommandSearch.GroupSpacing, 0, (uint)heads[g].Length);
        }
        _empty = Keep(Dw.MkLayout("没有找到「" + _query.Trim() + "」", Fmt(400, CommandSearch.EmptySize), 10000f));
        var m = new PaletteMeasure(LineOf(Fmt(400, CommandSearch.InputSize)), Dw.LayoutH(Keep(Dw.MkLayout("命", row, 10000f))),
            Dw.LayoutH(_headers[0]), Dw.LayoutH(_empty));
        _frame = CommandSearch.Layout(_viewW, _viewH, _items, m);
        _scroll = CommandSearch.Reveal(_frame, _selected, CommandSearch.ClampScroll(_frame, _scroll));
    }

    float LineOf(nint fmt) => Dw.LayoutH(Keep(Dw.MkLayout("命", fmt, 10000f)));

    // IDWriteTextLayout::SetFontWeight（槽 32）
    static void SetFontWeight(nint layout, uint weight, DWRITE_TEXT_RANGE range) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, DWRITE_TEXT_RANGE, int>)(*(nint**)layout)[32])(layout, weight, range);

    void Select(int entry, bool reveal)
    {
        if (entry < 0 || entry >= _items.Count || entry == _selected) return;
        _selected = entry;
        if (reveal) _scroll = CommandSearch.Reveal(_frame, _selected, _scroll);
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    void PlaceEdit()
    {
        if (_frame == null || _edit == 0) return;
        float x = _frame.X + CommandSearch.InputInsetX + CommandSearch.Icon + CommandSearch.InputGap;
        float w = _frame.X + _frame.W - CommandSearch.InputInsetX - x;
        float lh = _frame.InputH - 1f - CommandSearch.InputPadY * 2f;
        float s = Scale;
        Externs.MoveWindow(_edit, (int)MathF.Round(x * s), (int)MathF.Round((_frame.Y + CommandSearch.InputPadY) * s), (int)MathF.Round(w * s), (int)MathF.Ceiling(lh * s), true);
    }

    // ---------- 快照 ----------
    void TakeSnapshot(int w, int h)
    {
        DropSnapshot();
        nint screen = GetDC(_host.Owner);
        if (screen == 0) return;
        nint mem = CreateCompatibleDC(screen);
        _snapshot = CreateCompatibleBitmap(screen, w, h);
        _snapW = w; _snapH = h;
        nint old = SelectObject(mem, _snapshot);
        // PW_CLIENTONLY | PW_RENDERFULLCONTENT：连子窗口（编辑框）一起；失败就退回直接拷屏幕上的客户区
        if (!PrintWindow(_host.Owner, mem, 1 | 2)) BitBlt(mem, 0, 0, w, h, screen, 0, 0, 0x00CC0020);
        // 缩到 1/3 再放回：HALFTONE 取均值，近似演示的 blur(3px)
        int sw = Math.Max(1, w / 3), sh = Math.Max(1, h / 3);
        nint small = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, sw, sh), oldSmall = SelectObject(small, bmp);
        SetStretchBltMode(small, 4); SetBrushOrgEx(small, 0, 0, 0);
        StretchBlt(small, 0, 0, sw, sh, mem, 0, 0, w, h, 0x00CC0020);
        SetStretchBltMode(mem, 4); SetBrushOrgEx(mem, 0, 0, 0);
        StretchBlt(mem, 0, 0, w, h, small, 0, 0, sw, sh, 0x00CC0020);
        SelectObject(small, oldSmall); Externs.DeleteObject(bmp); DeleteDC(small);
        SelectObject(mem, old); DeleteDC(mem);
        ReleaseDC(_host.Owner, screen);
    }

    void DropSnapshot()
    {
        if (_snapshot != 0) { Externs.DeleteObject(_snapshot); _snapshot = 0; }
    }

    // ---------- 绘制 ----------
    void Paint(nint h)
    {
        nint win = Externs.BeginPaint(h, out var ps);
        Externs.GetClientRect(h, out var rect);
        // 试用修复：底图与面板先画到离屏缓冲，再一次贴出——直接画到窗口时 DWM 会在两步之间露出未罩色的底图（打开动画每帧一闪）。
        nint dc = _buffer.Begin(win, rect.R, rect.B, rect, out _);
        if (dc == 0) dc = win;
        try
        {
            if (_snapshot != 0)
            {
                nint mem = CreateCompatibleDC(dc), old = SelectObject(mem, _snapshot);
                BitBlt(dc, 0, 0, _snapW, _snapH, mem, 0, 0, 0x00CC0020);
                SelectObject(mem, old); DeleteDC(mem);
            }
            if (Rt == 0 || _frame == null) return;
            PBind(Rt, dc, &rect);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * Scale, 96f * Scale);
            PBegin(Rt);
            ulong a = 0, b = 0;
            try { PaintScene(); }
            finally { PEnd(Rt, &a, &b); }
        }
        finally
        {
            if (dc != win) _buffer.Present(win, rect);
            Externs.EndPaint(h, in ps);
        }
    }

    float Unroll()
    {
        if (_openedAt == 0) return 1f;
        float t = (Environment.TickCount64 - _openedAt) / (float)UnrollMs;
        if (t >= 1f) { _openedAt = 0; Externs.KillTimer(_hwnd, AnimTimer); return 1f; }
        return DeskAnimations.Ease(t);
    }

    void PaintScene()
    {
        var f = _frame;
        var all = new D2D1_RECT_F { right = _viewW + 1f, bottom = _viewH + 1f };
        if (HighContrast) { SetOpacity(BCanvas, 0.7f); PFill(Rt, &all, BCanvas); SetOpacity(BCanvas, 1f); }
        else { SetOpacity(BDesk, 0.45f); PFill(Rt, &all, BDesk); SetOpacity(BDesk, 1f); }
        // 展开：从上往下揭开（演示 unroll：clip-path 自上而下 .45s）
        float shown = f.H * Unroll();
        Shadow(f.X, f.Y, f.W, shown, CommandSearch.Radius, 40f, 40f, -30f, 1.4f);
        var clip = new D2D1_RECT_F { left = f.X - 1f, top = f.Y - 1f, right = f.X + f.W + 1f, bottom = f.Y + shown + 1f };
        PPushClip(Rt, &clip, 0);
        try
        {
            FillRound(f.X, f.Y, f.W, f.H, CommandSearch.Radius, BCanvas);
            StrokeRound(f.X, f.Y, f.W, f.H, CommandSearch.Radius, HighContrast ? BInk : BEdge, 1f);
            // 输入行：放大镜 + 输入框（原生 EDIT）+ 下边细线
            float iy = f.Y + (f.InputH - 1f) / 2f, ix = f.X + CommandSearch.InputInsetX;
            Magnifier(ix, iy - CommandSearch.Icon / 2f, CommandSearch.Icon, BMuted);
            PLine(Rt, new D2D1_POINT_2F { x = f.X + 1f, y = f.Y + f.InputH - 0.5f }, new D2D1_POINT_2F { x = f.X + f.W - 1f, y = f.Y + f.InputH - 0.5f }, BHair, 1f, 0);
            var list = new D2D1_RECT_F { left = f.X, top = f.ListY, right = f.X + f.W, bottom = f.ListY + f.ListH };
            PPushClip(Rt, &list, 0);
            try { PaintList(f); }
            finally { PPopClip(Rt); }
        }
        finally { PPopClip(Rt); }
    }

    void PaintList(PaletteFrame f)
    {
        if (f.Empty)
        {
            float ew = Dw.LayoutW(_empty);
            Draw(_empty, f.X + (f.W - ew) / 2f, f.ListY + CommandSearch.EmptyPad, BMuted);
            return;
        }
        float top = f.ListY - _scroll;
        foreach (var r in f.Rows)
        {
            float y = top + r.Y;
            if (y + r.H < f.ListY || y > f.ListY + f.ListH) continue;
            if (r.Entry < 0)
            {
                nint head = _headers[r.Header == CommandSearch.RecentHeader ? 1 : 0];
                Draw(head, f.X + CommandSearch.ListPad + CommandSearch.RowInsetX, y + CommandSearch.GroupTop, BMuted);
                continue;
            }
            int i = r.Entry;
            var e = _items[i];
            float x = f.X + CommandSearch.ListPad, w = f.W - CommandSearch.ListPad * 2f;
            if (i == _selected)
            {
                FillRound(x, y, w, r.H, CommandSearch.RowRadius, BAccentHover);
                if (HighContrast) StrokeRound(x, y, w, r.H, CommandSearch.RowRadius, BInk, 1f);
            }
            float tx = x + CommandSearch.RowInsetX, cy = y + r.H / 2f;
            bool enabled = e.Kind != PaletteKind.Command || e.Enabled;
            float lh = Dw.LayoutH(_labels[i]);
            Draw(_labels[i], tx, cy - lh / 2f, enabled ? BInk : BMuted);
            float right = x + w - CommandSearch.RowInsetX;
            if (_keys[i] != 0) right = Key(_keys[i], right, cy) - CommandSearch.RowGap;
            if (_subs[i] != 0)
            {
                float sx = tx + Dw.LayoutW(_labels[i]) + CommandSearch.RowGap;
                if (sx < right)
                {
                    var subClip = new D2D1_RECT_F { left = sx, top = y, right = right, bottom = y + r.H };
                    PPushClip(Rt, &subClip, 0);
                    Draw(_subs[i], sx, cy - Dw.LayoutH(_subs[i]) / 2f + 1f, BMuted);
                    PPopClip(Rt);
                }
            }
        }
    }

    // 键帽（演示 kbd：.76em、左右 .45em、1px 描边、下边 2px、圆角 5、纸色底）；返回左缘。
    static float Key(nint layout, float right, float cy) => DeskShapes.Key(layout, right, cy, CommandSearch.KeySize);

    // 演示图标：圆 (8.6,8.6) r5.1 + 柄 (12.4,12.4)→(16.6,16.6)，20 格，描边 1.6。
    static void Magnifier(float x, float y, float size, nint brush)
    {
        float k = size / 20f;
        var el = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = x + 8.6f * k, y = y + 8.6f * k }, radiusX = 5.1f * k, radiusY = 5.1f * k };
        PDrawEllipse(Rt, &el, brush, 1.6f * k, 0);
        PLine(Rt, new D2D1_POINT_2F { x = x + 12.4f * k, y = y + 12.4f * k }, new D2D1_POINT_2F { x = x + 16.6f * k, y = y + 16.6f * k }, brush, 1.6f * k, 0);
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
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                hInstance = Externs.GetModuleHandleW(null),
                hCursor = LoadCursorW(0, 32512),
                lpszClassName = "MDPalette"
            };
            if (Externs.RegisterClassExW(in wc) == 0) return false;
            _registered = true;
        }
        _self = GCHandle.Alloc(this);
        // WS_POPUP | WS_CLIPCHILDREN；WS_EX_TOOLWINDOW（不进任务栏）
        // 与所属窗口同一 DPI 感知（否则弹出窗被系统按位图放大，快照与文字都会发虚）
        nint previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(_host.Owner));
        Externs.CreateWindowExW(0x80, "MDPalette", "命令面板", 0x80000000 | 0x02000000, 0, 0, 1, 1, _host.Owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (previous != 0) SetThreadDpiAwarenessContext(previous);
        if (_hwnd == 0) { _self.Free(); return false; }
        uint none = 1 /*DWMWCP_DONOTROUND*/;
        _ = DwmSetWindowAttribute(_hwnd, 33, &none, 4);
        // WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL
        _edit = Externs.CreateWindowExW(0, "EDIT", "", 0x40000000 | 0x10000000 | 0x0080, 0, 0, 0, 0, _hwnd, (nint)1, Externs.GetModuleHandleW(null), 0);
        if (_edit == 0) return false;
        _font = Externs.CreateFontW(-(int)MathF.Round(CommandSearch.InputSize * Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0, Sans);
        if (_font != 0) Externs.SendMessageW(_edit, 0x0030 /*WM_SETFONT*/, _font, 0);
        Externs.SendMessageW(_edit, 0x00D3 /*EM_SETMARGINS*/, 3, 0);
        fixed (char* p = CommandSearch.Placeholder) Externs.SendMessageW(_edit, 0x1501 /*EM_SETCUEBANNER*/, 1, (nint)p);
        _editProc = Externs.GetWindowLongPtrW(_edit, -4);
        Externs.SetWindowLongPtrW(_edit, -21, GCHandle.ToIntPtr(_self));
        Externs.SetWindowLongPtrW(_edit, -4, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&EditProc);
        return true;
    }

    // 输入框只截方向键、回车、Esc 与 Ctrl+K；组词时一律交给输入法。
    bool EditKey(nint vk)
    {
        if (_ime.Active) return false;
        bool ctrl = (Externs.GetKeyState(0x11) & 0x8000) != 0, shift = (Externs.GetKeyState(0x10) & 0x8000) != 0;
        switch ((int)vk)
        {
            case 0x26: Select(CommandSearch.Step(_selected, _items.Count, -1), true); return true;
            case 0x28: Select(CommandSearch.Step(_selected, _items.Count, 1), true); return true;
            case 0x0D: Run(_selected); return true;
            case 0x1B: Close(); return true;
            case 0x4B when ctrl:
            case 0x50 when ctrl && shift: Close(); return true;
        }
        return false;
    }

    void QueryChanged()
    {
        string q = Externs.WindowText(_edit);
        if (q == _query) return;
        _query = q; _selected = 0; _scroll = 0f;
        Refresh();
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    void Pointer(uint message, nint l, nint w)
    {
        float s = Scale;
        float x = (short)(l & 0xFFFF) / s, y = (short)((l >> 16) & 0xFFFF) / s;
        switch (message)
        {
            case 0x0200:   // WM_MOUSEMOVE：悬停即选中（演示 mousemove → sel）
                { int hit = CommandSearch.Hit(_frame, x, y, _scroll); if (hit >= 0) Select(hit, false); break; }
            case 0x0201:   // WM_LBUTTONDOWN：条目即执行，面板外即关闭
                if (!CommandSearch.InPanel(_frame, x, y)) { Close(); break; }
                Run(CommandSearch.Hit(_frame, x, y, _scroll));
                if (Visible) Externs.SetFocus(_edit);
                break;
        }
    }

    void Wheel(nint w)
    {
        float delta = (short)((w >> 16) & 0xFFFF) / 120f;
        float next = CommandSearch.ClampScroll(_frame, _scroll - delta * 3f * (CommandSearch.RowPadY * 2f + 20f));
        if (next == _scroll) return;
        _scroll = next;
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        PaletteView view = null;
        try
        {
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) view = (PaletteView)GCHandle.FromIntPtr(token).Target;
            switch (message)
            {
                case 0x0081 when view != null:   // WM_NCCREATE
                    view._hwnd = h;
                    Externs.SetWindowLongPtrW(h, -21, token);
                    break;
                case Externs.WM_PAINT when view != null: view.Paint(h); return 0;
                case 0x0014: return 1;           // WM_ERASEBKGND：全部自绘
                case 0x0006 when view != null && (w & 0xFFFF) == 0 && view.Visible:   // WM_ACTIVATE(WA_INACTIVE)：切到别处即收起
                    Externs.PostMessage(h, 0x0010 + 0x0400, 0, 0);
                    break;
                case 0x0410 when view != null: view.Close(); return 0;   // 延后的收起（不在激活切换中途改激活）
                case 0x0133 when view != null:   // WM_CTLCOLOREDIT：纸色底、墨色字
                    return ControlColors(w);
                case 0x0111 when view != null && (w >> 16 & 0xFFFF) == 0x0300: view.QueryChanged(); return 0;   // EN_CHANGE
                case 0x0200 or 0x0201 when view != null: view.Pointer(message, l, w); return 0;
                case 0x020A when view != null: view.Wheel(w); return 0;
                case 0x0113 when view != null && w == AnimTimer: Externs.InvalidateRect(h, 0, 0); return 0;
                case 0x0082 when view != null:   // WM_NCDESTROY
                    Externs.SetWindowLongPtrW(h, -21, 0);
                    view._hwnd = 0; view._edit = 0; view.Visible = false;
                    if (view._self.IsAllocated) view._self.Free();
                    break;
            }
        }
        catch (Exception ex) { AppLog.Error("palette callback", ex); }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint EditProc(nint h, uint message, nint w, nint l)
    {
        PaletteView view = null;
        try
        {
            nint token = Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) view = (PaletteView)GCHandle.FromIntPtr(token).Target;
            if (view != null)
            {
                view._ime.Observe(message);
                if (message == Externs.WM_KEYDOWN && view.EditKey(w)) return 0;
                if (message == 0x0102 && !view._ime.Active && (w == 0x0D || w == 0x1B || w == 0x0B)) return 0;   // WM_CHAR：回车 / Esc / Ctrl+K 不响铃
            }
        }
        catch (Exception ex) { AppLog.Error("palette input callback", ex); }
        return view != null && view._editProc != 0 ? Externs.CallWindowProcW(view._editProc, h, message, w, l) : Externs.DefWindowProcW(h, message, w, l);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0) Externs.DestroyWindow(_hwnd);
        if (_font != 0) Externs.DeleteObject(_font);
        DropSnapshot();
        _buffer.Dispose();
        Release();
        foreach (nint f in _formats.Values) Marshal.Release(f);
        _formats.Clear();
    }

    struct Pt { public int X, Y; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint h, int attr, uint* value, int size);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref Pt p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern nint GetWindowDpiAwarenessContext(nint h);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(nint dc, int x, int y, nint old);
}
