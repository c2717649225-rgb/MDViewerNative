using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.DeskShapes;

namespace MDViewerNative;

internal interface IAppearanceHost
{
    nint Owner { get; }
    float Scale { get; }
    AppearanceState State();
    void Choose(Paper paper, int screenX, int screenY);   // 屏幕坐标：晕开的圆心
    void SetSerif(bool serif);
    void Zoom(int direction);
    void Closed();
}

// U11b：外观面板（演示 #lookPop）。无激活的弹出窗（盖得住编辑框）：键盘仍归主窗口，由宿主转交 HandleKey；
// 面板外的按下由宿主在消息循环里收起面板。几何在 AppearancePanel；本类只管窗口与绘制。
// Win11 用系统圆角（8，演示 14）与系统投影：无激活弹出窗画不了自身以外的阴影。
internal sealed unsafe class AppearancePanelView : IDisposable
{
    readonly IAppearanceHost _host;
    nint _hwnd;
    GCHandle _self;
    bool _disposed, _tracking;
    static bool _registered;
    AppearanceFrame _frame;
    AppearanceState _state;
    AppearanceTarget _hover, _focus;
    float _laidScale;
    readonly List<nint> _layouts = new();
    readonly Dictionary<(uint, float), nint> _formats = new();
    nint _head, _note, _fontLabel, _sizeLabel, _sans, _serif, _minus, _plus, _value;
    nint[] _names = Array.Empty<nint>(), _descs = Array.Empty<nint>();
    int _valueFor = -1;
    const string Sans = "Segoe UI";

    public AppearancePanelView(IAppearanceHost host) => _host = host;
    public bool Visible { get; private set; }
    public bool Owns(nint window) => Visible && window != 0 && window == _hwnd;
    float Scale => _host.Scale > 0f ? _host.Scale : 1f;

    // buttonRight：「外观」钮右缘（主窗口客户区 DIP）；top：面板上缘。keyboard：键盘打开时焦点落在当前纸张上。
    public void Open(float buttonRight, float top, bool keyboard)
    {
        if (_disposed || Visible || _host.Owner == 0 || Rt == 0 || !Ensure()) return;
        _state = _host.State();
        Prepare();
        _hover = AppearanceTarget.None;
        _focus = keyboard ? new(AppearanceTargetKind.Paper, (int)_state.Paper) : AppearanceTarget.None;
        Externs.GetClientRect(_host.Owner, out var client);
        float x = AppearancePanel.PlaceX(buttonRight, client.R / Scale);
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

    // 状态变了（换纸、字体、字号）：重取状态并重画。
    public void Refresh()
    {
        if (!Visible) return;
        _state = _host.State();
        if (_laidScale != Scale) Prepare();
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    // 主窗口转交的按键：↑↓←→ Tab 移动，Enter / 空格执行，Esc 收起。返回 true 表示已处理。
    public bool HandleKey(int vk)
    {
        if (!Visible) return false;
        bool shift = (Externs.GetKeyState(0x10) & 0x8000) != 0;
        switch (vk)
        {
            case 0x1B: Close(); return true;
            case 0x26 or 0x25: MoveFocus(-1); return true;
            case 0x28 or 0x27: MoveFocus(1); return true;
            case 0x09: MoveFocus(shift ? -1 : 1); return true;
            case 0x0D or 0x20:
                if (_focus.Kind == AppearanceTargetKind.None) MoveFocus(0);
                else Activate(_focus, CenterOf(_focus));
                return true;
        }
        return false;
    }

    void MoveFocus(int direction)
    {
        _focus = direction == 0 || _focus.Kind == AppearanceTargetKind.None ? new(AppearanceTargetKind.Paper, (int)_state.Paper) : AppearancePanel.Step(_focus, direction, _state);
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    // 键盘执行时晕开从该项中心起（屏幕坐标）。
    Pt CenterOf(AppearanceTarget t)
    {
        var r = t.Kind == AppearanceTargetKind.Paper && t.Index < _frame.Options.Count ? _frame.Options[t.Index] : new PanelRect(0f, 0f, _frame.W, _frame.H);
        var p = new Pt { X = (int)((r.X + r.W / 2f) * Scale), Y = (int)((r.Y + r.H / 2f) * Scale) };
        _ = ClientToScreen(_hwnd, ref p);
        return p;
    }

    void Activate(AppearanceTarget t, Pt origin)
    {
        if (!AppearancePanel.Enabled(t, _state)) return;
        switch (t.Kind)
        {
            case AppearanceTargetKind.Paper: _host.Choose((Paper)t.Index, origin.X, origin.Y); break;
            case AppearanceTargetKind.Font: _host.SetSerif(t.Index == 1); break;
            case AppearanceTargetKind.ZoomOut: _host.Zoom(-1); break;
            case AppearanceTargetKind.ZoomIn: _host.Zoom(1); break;
        }
        Refresh();
    }

    // ---------- 排版 ----------
    nint Fmt(uint weight, float size)
    {
        if (!_formats.TryGetValue((weight, size), out nint f)) _formats[(weight, size)] = f = Dw.MkFormat(Sans, weight, size);
        return f;
    }

    nint Text(string s, float size, float spacing = 0f)
    {
        nint l = Dw.MkLayout(s, Fmt(400, size), 10000f);
        if (l != 0) _layouts.Add(l);
        if (spacing > 0f && l != 0) Dw.SetCharacterSpacing(l, 0f, spacing, 0, (uint)s.Length);
        return l;
    }

    void Release()
    {
        foreach (nint l in _layouts) Marshal.Release(l);
        _layouts.Clear();
        _valueFor = -1;
    }

    void Prepare()
    {
        Release();
        _laidScale = Scale;
        _head = Text(AppearancePanel.Title, AppearancePanel.HeadSize, AppearancePanel.HeadSize * AppearancePanel.HeadSpacing);
        int n = AppearancePanel.Names.Length;
        _names = new nint[n]; _descs = new nint[n];
        for (int i = 0; i < n; i++) { _names[i] = Text(AppearancePanel.Names[i], AppearancePanel.NameSize); _descs[i] = Text(AppearancePanel.Notes[i], AppearancePanel.DescSize); }
        _note = Text(AppearancePanel.Note, AppearancePanel.NoteSize);
        Dw.SetMaxWidth(_note, AppearancePanel.NoteWidth);
        Dw.SetLineSpacing(_note, 1, AppearancePanel.NoteSize * 1.6f, AppearancePanel.NoteSize * 1.6f * 0.8f);
        _fontLabel = Text(AppearancePanel.FontRow, AppearancePanel.RowSize);
        _sizeLabel = Text(AppearancePanel.SizeRow, AppearancePanel.RowSize);
        _sans = Text(AppearancePanel.Sans, AppearancePanel.SegSize);
        _serif = Text(AppearancePanel.SerifLabel, AppearancePanel.SegSize);
        _minus = Text("−", AppearancePanel.RowSize);
        _plus = Text("＋", AppearancePanel.RowSize);
        _value = Text("100%", AppearancePanel.ValueSize);
        _valueFor = 100;
        var m = new AppearanceMeasure(Dw.LayoutH(_head), Dw.LayoutH(_names[0]), Dw.LayoutH(_descs[0]), Dw.LayoutH(_note), Dw.LayoutH(_fontLabel),
            Dw.LayoutW(_sans), Dw.LayoutW(_serif), Dw.LayoutH(_sans), Dw.LayoutH(_value));
        _frame = AppearancePanel.Layout(m);
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
            try { PaintPanel(); }
            finally { PEnd(Rt, &a, &b); }
        }
        finally { Externs.EndPaint(h, in ps); }
    }

    bool Hot(AppearanceTarget t) => t == _hover || t == _focus;

    void PaintPanel()
    {
        var f = _frame;
        var all = new D2D1_RECT_F { right = f.W + 1f, bottom = f.H + 1f };
        PFill(Rt, &all, BCanvas);
        StrokeRound(0.5f, 0.5f, f.W - 1f, f.H - 1f, 8f, HighContrast ? BInk : BEdge, 1f);   // 演示 .pop 1px 边
        Draw(_head, f.Head.X, f.Head.Y, BMuted);
        for (int i = 0; i < f.Options.Count; i++) PaintOption(i, f.Options[i]);
        Draw(_note, f.Note.X, f.Note.Y, BMuted);

        Rule(f.FontRuleY);
        Draw(_fontLabel, f.FontLabel.X, f.FontLabel.Y, BInk2);
        bool serifOk = _state.SerifAvailable;
        FillRound(f.Segment.X, f.Segment.Y, f.Segment.W, f.Segment.H, AppearancePanel.SegRadius, HighContrast ? BCanvas : BDesk2);
        if (HighContrast) StrokeRound(f.Segment.X, f.Segment.Y, f.Segment.W, f.Segment.H, AppearancePanel.SegRadius, BInk, 1f);
        SegButton(f.Sans, _sans, !_state.Serif, new(AppearanceTargetKind.Font, 0), true);
        SegButton(f.Serif, _serif, _state.Serif, new(AppearanceTargetKind.Font, 1), serifOk);

        Rule(f.SizeRuleY);
        Draw(_sizeLabel, f.SizeLabel.X, f.SizeLabel.Y, BInk2);
        StepButton(f.Minus, _minus, new(AppearanceTargetKind.ZoomOut), _state.CanZoomOut);
        StepButton(f.Plus, _plus, new(AppearanceTargetKind.ZoomIn), _state.CanZoomIn);
        if (_valueFor != _state.ZoomPercent)
        {
            _value = Text(_state.ZoomPercent + "%", AppearancePanel.ValueSize);
            _valueFor = _state.ZoomPercent;
        }
        Draw(_value, f.Value.X + (f.Value.W - Dw.LayoutW(_value)) / 2f, f.Value.Y, BInk);
    }

    void Rule(float y)
    {
        float x0 = AppearancePanel.Pad, x1 = _frame.W - AppearancePanel.Pad;
        PLine(Rt, new D2D1_POINT_2F { x = x0, y = y + 0.5f }, new D2D1_POINT_2F { x = x1, y = y + 0.5f }, BHair, 1f, 0);
    }

    void PaintOption(int i, PanelRect r)
    {
        var target = new AppearanceTarget(AppearanceTargetKind.Paper, i);
        bool on = AppearancePanel.IsOn(target, _state);
        if (Hot(target)) FillRound(r.X, r.Y, r.W, r.H, AppearancePanel.OptionRadius, HighContrast ? BCanvas : BSurfaceHover);
        if (target == _focus) StrokeRound(r.X, r.Y, r.W, r.H, AppearancePanel.OptionRadius, HighContrast ? BInk : BControlBorder, 1f);
        float sx = r.X + AppearancePanel.OptionPad, sy = r.Y + (r.H - AppearancePanel.SwatchH) / 2f;
        Swatch((Paper)i, sx, sy);
        float tx = sx + AppearancePanel.SwatchW + AppearancePanel.OptionGap;
        float th = Dw.LayoutH(_names[i]) + 1f + Dw.LayoutH(_descs[i]), ty = r.Y + (r.H - th) / 2f;
        Draw(_names[i], tx, ty, BInk);
        Draw(_descs[i], tx, ty + Dw.LayoutH(_names[i]) + 1f, BMuted);
        float cx = r.X + r.W - AppearancePanel.OptionPad - AppearancePanel.Check / 2f, cy = r.Y + r.H / 2f;
        var circle = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = cx, y = cy }, radiusX = 9f, radiusY = 9f };
        if (on)
        {
            PFillEllipse(Rt, &circle, BAccent);
            // 勾（演示 ::after：4×8 右下两边 1.6，旋转 42°）
            nint tick = BCanvas;
            PLine(Rt, new D2D1_POINT_2F { x = cx - 3.6f, y = cy + 0.2f }, new D2D1_POINT_2F { x = cx - 1.1f, y = cy + 2.8f }, tick, 1.6f, 0);
            PLine(Rt, new D2D1_POINT_2F { x = cx - 1.1f, y = cy + 2.8f }, new D2D1_POINT_2F { x = cx + 3.8f, y = cy - 3.2f }, tick, 1.6f, 0);
        }
        else
        {
            circle.radiusX = circle.radiusY = 8.25f;
            PDrawEllipse(Rt, &circle, HighContrast ? BInk : BFaint, 1.5f, 0);
        }
    }

    // 色样 44×32（演示 .opt .sw）：纸色底、三道墨痕（60% 墨 / 88% 与 75% 淡墨 .55）、右上一条燕尾书签；跟随系统是宣纸 / 灯下对角两半。
    void Swatch(Paper paper, float x, float y)
    {
        const float w = AppearancePanel.SwatchW, h = AppearancePanel.SwatchH, r = AppearancePanel.SwatchRadius;
        if (HighContrast) { StrokeRound(x, y, w, h, r, BInk, 1f); return; }
        try
        {
            if (paper == Paper.Auto)
            {
                SetBrushRgb(BShade, PaperPalette.Xuan.Canvas);
                FillRound(x, y, w, h, r, BShade);
                SetBrushRgb(BShade, PaperPalette.Lamp.Canvas);
                // 对角线从右上到左下，右下一半深色；逐行画，圆角处按圆弧收进
                for (float yy = 0.25f; yy < h; yy += 0.5f)
                {
                    float inset = Corner(yy, h, r), x0 = x + MathF.Max(w * (1f - yy / h), inset), x1 = x + w - inset;
                    if (x1 > x0) PLine(Rt, new D2D1_POINT_2F { x = x0, y = y + yy }, new D2D1_POINT_2F { x = x1, y = y + yy }, BShade, 1f, 0);
                }
            }
            else
            {
                var c = PaperPalette.Get(paper, false);
                SetBrushRgb(BShade, c.Canvas);
                FillRound(x, y, w, h, r, BShade);
                float inner = w - 14f, by = y + 7f;
                SetBrushRgb(BShade, c.Ink);
                FillRound(x + 7f, by, inner * 0.6f, 2f, 1f, BShade);
                SetBrushRgb(BShade, c.Muted);
                SetOpacity(BShade, 0.55f);
                FillRound(x + 7f, by + 6f, inner * 0.88f, 2f, 1f, BShade);
                FillRound(x + 7f, by + 12f, inner * 0.75f, 2f, 1f, BShade);
                SetOpacity(BShade, 1f);
                SetBrushRgb(BShade, c.Accent);
                float bx = x + w - 6f - 4f, bh = h * 0.62f, notch = bh * 0.82f;
                var body = new D2D1_RECT_F { left = bx, top = y, right = bx + 4f, bottom = y + notch };
                PFill(Rt, &body, BShade);
                for (float yy = notch; yy < bh; yy += 0.5f)
                {
                    float part = 2f * (yy - notch) / (bh - notch);
                    PLine(Rt, new D2D1_POINT_2F { x = bx, y = y + yy + 0.25f }, new D2D1_POINT_2F { x = bx + part, y = y + yy + 0.25f }, BShade, 1f, 0);
                    PLine(Rt, new D2D1_POINT_2F { x = bx + 4f - part, y = y + yy + 0.25f }, new D2D1_POINT_2F { x = bx + 4f, y = y + yy + 0.25f }, BShade, 1f, 0);
                }
            }
            // 内描边 1px rgba(0,0,0,.1)
            SetBrushRgb(BShade, 0);
            SetOpacity(BShade, 0.1f);
            StrokeRound(x, y, w, h, r, BShade, 1f);
        }
        finally { SetOpacity(BShade, 1f); SetBrushRgb(BShade, 0); }
    }

    static float Corner(float yy, float h, float r)
    {
        float d = yy < r ? r - yy : yy > h - r ? yy - (h - r) : 0f;
        return d <= 0f ? 0f : r - MathF.Sqrt(MathF.Max(0f, r * r - d * d));
    }

    void SegButton(PanelRect r, nint label, bool on, AppearanceTarget target, bool enabled)
    {
        if (on)
        {
            if (!HighContrast) Shadow(r.X, r.Y, r.W, r.H, AppearancePanel.SegButtonRadius, 1f, 2f, 0f, 0.6f);
            FillRound(r.X, r.Y, r.W, r.H, AppearancePanel.SegButtonRadius, BSegOn);
            if (HighContrast) StrokeRound(r.X, r.Y, r.W, r.H, AppearancePanel.SegButtonRadius, BInk, 1f);
        }
        if (target == _focus) StrokeRound(r.X, r.Y, r.W, r.H, AppearancePanel.SegButtonRadius, HighContrast ? BInk : BControlBorder, 1f);
        nint ink = !enabled ? BMuted : on || Hot(target) ? BInk : BInk2;
        if (!enabled) SetOpacity(BMuted, 0.5f);
        Draw(label, r.X + (r.W - Dw.LayoutW(label)) / 2f, r.Y + (r.H - Dw.LayoutH(label)) / 2f, ink);
        if (!enabled) SetOpacity(BMuted, 1f);
    }

    void StepButton(PanelRect r, nint glyph, AppearanceTarget target, bool enabled)
    {
        if (enabled && Hot(target)) FillRound(r.X, r.Y, r.W, r.H, AppearancePanel.StepRadius, HighContrast ? BCanvas : BSurfaceHover);
        StrokeRound(r.X, r.Y, r.W, r.H, AppearancePanel.StepRadius, HighContrast ? BInk : target == _focus ? BControlBorder : BEdge, 1f);
        Draw(glyph, r.X + (r.W - Dw.LayoutW(glyph)) / 2f, r.Y + (r.H - Dw.LayoutH(glyph)) / 2f, enabled ? BInk : BMuted);
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
                lpszClassName = "MDAppearance"
            };
            if (Externs.RegisterClassExW(in wc) == 0) return false;
            _registered = true;
        }
        _self = GCHandle.Alloc(this);
        nint previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(_host.Owner));
        // WS_POPUP；WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        Externs.CreateWindowExW(0x80 | 0x08000000, "MDAppearance", "外观", 0x80000000, 0, 0, 1, 1, _host.Owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
        if (previous != 0) SetThreadDpiAwarenessContext(previous);
        if (_hwnd == 0) { _self.Free(); return false; }
        uint round = 2 /*DWMWCP_ROUND*/;
        _ = DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &round, 4);
        return true;
    }

    void Pointer(uint message, nint l)
    {
        float x = (short)(l & 0xFFFF) / Scale, y = (short)((l >> 16) & 0xFFFF) / Scale;
        var hit = AppearancePanel.Hit(_frame, x, y);
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
        _focus = AppearanceTarget.None;   // 鼠标操作时不显示键盘焦点
        var at = new Pt { X = (short)(l & 0xFFFF), Y = (short)((l >> 16) & 0xFFFF) };
        _ = ClientToScreen(_hwnd, ref at);
        if (hit.Kind != AppearanceTargetKind.None) Activate(hit, at);
        else Externs.InvalidateRect(_hwnd, 0, 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        AppearancePanelView view = null;
        try
        {
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) view = (AppearancePanelView)GCHandle.FromIntPtr(token).Target;
            switch (message)
            {
                case 0x0081 when view != null:   // WM_NCCREATE
                    view._hwnd = h;
                    Externs.SetWindowLongPtrW(h, -21, token);
                    break;
                case 0x0021: return 3;           // WM_MOUSEACTIVATE → MA_NOACTIVATE：键盘留在主窗口
                case Externs.WM_PAINT when view != null: view.Paint(h); return 0;
                case 0x0014: return 1;           // WM_ERASEBKGND：全部自绘
                case 0x0200 or 0x0201 when view != null: view.Pointer(message, l); return 0;
                case 0x02A3 when view != null:   // WM_MOUSELEAVE
                    view._tracking = false;
                    if (view._hover.Kind != AppearanceTargetKind.None) { view._hover = AppearanceTarget.None; Externs.InvalidateRect(h, 0, 0); }
                    return 0;
                case 0x0082 when view != null:   // WM_NCDESTROY
                    Externs.SetWindowLongPtrW(h, -21, 0);
                    view._hwnd = 0; view.Visible = false;
                    if (view._self.IsAllocated) view._self.Free();
                    break;
            }
        }
        catch (Exception ex) { AppLog.Error("appearance panel callback", ex); }
        return Externs.DefWindowProcW(h, message, w, l);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0) Externs.DestroyWindow(_hwnd);
        Release();
        foreach (nint f in _formats.Values) Marshal.Release(f);
        _formats.Clear();
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
