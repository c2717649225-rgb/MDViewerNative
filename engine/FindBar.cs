using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.NativeMenus;

namespace MDViewerNative;

internal interface IFindHost
{
    IntPtr Owner { get; }
    IntPtr Editor { get; }
    bool Editing { get; }
    float Scale { get; }
    int ViewWidth { get; }
    int ViewHeight { get; }
    float ContentTop { get; }
    float BarTop { get; }   // U9a：查找条的顶（顶栏下缘；desk 编辑时再让出编辑条）
    float Offset { get; }
    DocView Document { get; }
    void ScrollTo(float offset);
    void RevealHit(float offset, bool remember);   // U7：desk 查找跳转（平滑；逐处前进时可留返回书签）
    void HitShown();                               // U7：当前一处换了，重描朱笔圈
    void TextChanged();
    void LayoutChanged();
}

// Native search controls overlay the reading view; editing reserves space for caret visibility.
internal sealed unsafe class FindBar : IDisposable
{
    readonly IFindHost _host;
    readonly List<nint> _controls = new();
    readonly Dictionary<string, nint> _labels = new();
    GCHandle _selfHandle;
    bool _disposed, _findVisible, _replaceRow;
    float _fontScale;
    nint _hFind, _hFindCount, _hReplace, _hBtnRep, _hBtnAll, _hPrev, _hNext, _hClose, _hFindLabel, _hReplaceLabel;
    nint _hFindFont, _hCountFont, _hOptions;
    readonly Dictionary<nint, nint> _inputProcs = new();
    readonly Dictionary<nint, ImeComposition> _compositions = new();
    public SearchOptions Options { get; private set; }
    string _scopeText, _openingText;
    DocView _scopeDocument, _openingDocument;
    bool _scopeEditing, _openingEditing;
    int _scopeStart, _scopeEnd, _openingStart, _openingEnd, _lastEmpty = -1;
    bool SelectionOnly => _scopeText != null;
    public bool IsComposing => System.Linq.Enumerable.Any(_compositions.Values, value => value.Active);
    public event Action CompositionChanged;
    public event Action<SearchOptions> OptionsChanged;
    public event Action<string> QueryCommitted;
    public event Action Opening;   // U8：查找框将要出现（宿主据此收起目录卷目）
    public bool OwnsInput(nint window) => _inputProcs.ContainsKey(window);
    public FindBar(IFindHost host) => _host = host;
    public bool Visible => _findVisible;
    public float ViewDip => _host.ViewWidth / _host.Scale;
    // A3:形态与矩形来自 OverlayGeometry;Height 只表示被顶开的高度(浮动面板为 0)
    public FindLayout Layout => OverlayGeometry.Find(Visible, _host.Editing, _replaceRow && _host.Editing, ViewDip, DeskChrome);
    public OverlayBox Box => OverlayGeometry.FindBox(Layout, ViewDip, _host.BarTop);
    public float Height => OverlayGeometry.FindHeight(Layout);
    public bool IsCountControl(nint child) => child != 0 && (child == _hFindCount || child == _hFindLabel || child == _hReplaceLabel);
    public void SetQuery(string text) => Externs.SetWindowTextW(_hFind, text);

    public bool HandleCommand(int command, int code, nint child)
    {
        if (code == 0x0300 && child != 0 && child == _hFind) { if (_settingQuery) return true; _lastEmpty = -1; if (!IsComposing) Next(false); return true; }
        if (code != 0) return false;
        if (IsComposing && command >= 3001 && command <= 3023) return true;
        switch (command)
        {
            case 3001: SearchAction(DoReplaceOne); return true;
            case 3002: SearchAction(DoReplaceAll); return true;
            case 3003: Next(true, step: true); return true;
            case 3004: Next(false, step: true); return true;
            case 3005: Close(); return true;
            case 3006: ShowOptions(); return true;
            case 3007: SetOptions(Options with { CaseSensitive = !Options.CaseSensitive }); return true;
            case 3008: SetOptions(Options with { WholeWord = !Options.WholeWord }); return true;
            case 3009: Next(false); return true;
            case 3020: SetOptions(Options with { RegularExpression = !Options.RegularExpression }); return true;
            case 3021: SearchAction(ToggleSelectionScope); return true;
            case 3022: SearchAction(ShowResults); return true;
            case 3023: SearchAction(ShowHistory); return true;
        }
        return false;
    }

    // Called before TranslateMessage: native buttons retain focus/Space behavior; Tab stays in this strip.
    public bool TranslateKey(ref MSG message)
    {
        if (!Visible || message.message != Externs.WM_KEYDOWN || !_controls.Contains(message.hwnd)) return false;
        if (IsComposing) return false;
        int key = (int)message.wParam;
        if (key == 0x1B) { Close(); return true; }
        if (key == 0x0D)
        {
            if (message.hwnd == _hFind) Next((Externs.GetKeyState(0x10) & 0x8000) != 0, step: true);
            else if (message.hwnd == _hReplace) SearchAction(DoReplaceOne);
            else Externs.SendMessageW(message.hwnd, 0x00F5 /*BM_CLICK*/, 0, 0);
            return true;
        }
        if (key != 9) return false;
        nint[] order = _replaceRow && _host.Editing ? new[] { _hFind, _hReplace, _hBtnRep, _hBtnAll, _hPrev, _hNext, _hClose, _hOptions }
            : new[] { _hFind, _hPrev, _hNext, _hClose, _hOptions };
        int index = Array.IndexOf(order, message.hwnd), step = (Externs.GetKeyState(0x10) & 0x8000) != 0 ? -1 : 1;
        Externs.SetFocus(order[(index + step + order.Length) % order.Length]);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint FindProc(nint h, uint m, nint w, nint l)
    {
        FindBar bar = null;
        try
        {
            nint token = Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) bar = (FindBar)GCHandle.FromIntPtr(token).Target;
            if (bar != null && bar._compositions.TryGetValue(h, out var composition))
            {
                if (composition.Observe(m))
                {
                    bar.CompositionChanged?.Invoke();
                    if (!composition.Active) Externs.PostMessage(bar._host.Owner, Externs.WM_COMMAND, (nint)3009, 0);
                }
                if (!composition.Active && m == Externs.WM_KEYDOWN)
                {
                    if (w == 0x0D)
                    {
                        if (h == bar._hReplace) bar.SearchAction(bar.DoReplaceOne);
                        else bar.Next((Externs.GetKeyState(0x10) & 0x8000) != 0, step: true);
                        return 0;
                    }
                    if (w == 0x1B) { bar.Close(); return 0; }
                }
            }
        }
        catch (Exception ex) { AppLog.Error("find input callback", ex); }
        return bar != null && bar._inputProcs.TryGetValue(h, out nint previous) ? Externs.CallWindowProcW(previous, h, m, w, l) : Externs.DefWindowProcW(h, m, w, l);
    }

    nint Create(string cls, string text, int id, uint style)
    {
        nint h = Externs.CreateWindowExW(0, cls, text, 0x40000000 | 0x04000000 | style,
            0, 0, 0, 0, _host.Owner, (nint)id, Externs.GetModuleHandleW(null), 0);
        if (h == 0) throw new System.ComponentModel.Win32Exception();
        _controls.Add(h);
        if (_hFindFont != 0) Externs.SendMessageW(h, 0x0030, _hFindFont, 0);
        return h;
    }

    nint Input(int id, string hint)
    {
        // U7：desk 查找框自带描边，输入框不再画边框（演示 input{border:0}）。
        nint h = Create("EDIT", "", id, DeskChrome ? 0x00010180u : 0x00810180u /*[border,] tabstop, autohscroll, no-hide-selection*/);
        fixed (char* p = hint) Externs.SendMessageW(h, 0x1501 /*EM_SETCUEBANNER*/, (nint)1, (nint)p);
        return h;
    }

    void AttachInput(nint input)
    {
        _inputProcs[input] = Externs.GetWindowLongPtrW(input, -4);
        _compositions[input] = new ImeComposition();
        Externs.SetWindowLongPtrW(input, -21, GCHandle.ToIntPtr(_selfHandle));
        Externs.SetWindowLongPtrW(input, -4, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&FindProc);
    }

    void EnsureFind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hFind != 0) return;
        _hFindLabel = Create("STATIC", "查找", 3010, 0x200);
        _hFind = Input(3011, DeskChrome ? "查找" : "查找内容");
        _hFindCount = Create("STATIC", "", 3012, 0x202);
        _hPrev = Create("BUTTON", "上一处", 3003, 0x1000B);
        _hNext = Create("BUTTON", "下一处", 3004, 0x1000B);
        _hClose = Create("BUTTON", "关闭", 3005, 0x1000B);
        _hOptions = Create("BUTTON", "选项", 3006, 0x1000B);
        _selfHandle = GCHandle.Alloc(this);
        AttachInput(_hFind);
        ApplyTheme();
    }

    void EnsureReplace()
    {
        if (_hReplace != 0) return;
        _hReplaceLabel = Create("STATIC", "替换为", 3013, 0x200);
        _hReplace = Input(3014, "替换内容（可留空）");
        AttachInput(_hReplace);
        _hBtnRep = Create("BUTTON", "替换", 3001, 0x1000B);
        _hBtnAll = Create("BUTTON", "全部替换", 3002, 0x1000B);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        foreach (nint h in _controls) Externs.SetWindowTheme(h, DarkMode ? "DarkMode_Explorer" : "Explorer", null);
        Reposition();
    }

    public void InvalidateControls()
    {
        foreach (nint h in _controls) Externs.InvalidateRect(h, 0, 0);
    }

    void SetStatus(string text) { if (_hFindCount != 0) Externs.SetWindowTextW(_hFindCount, text); }

    public void Reposition()
    {
        if (_hFind == 0) return;
        if (_fontScale != _host.Scale)
        {
            nint font = Externs.CreateFontW(-(int)MathF.Round((DeskChrome ? 13.5f : 13f) * _host.Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            if (font == 0) throw new System.ComponentModel.Win32Exception();
            foreach (nint h in _controls) Externs.SendMessageW(h, 0x0030, font, (nint)1);
            if (_hFindFont != 0) Externs.DeleteObject(_hFindFont);
            _hFindFont = font; _fontScale = _host.Scale;
            if (DeskChrome)   // U7：演示 .cnt 为 12px
            {
                nint small = Externs.CreateFontW(-(int)MathF.Round(12f * _host.Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
                if (small == 0) throw new System.ComponentModel.Win32Exception();
                Externs.SendMessageW(_hFindCount, 0x0030, small, (nint)1);
                if (_hCountFont != 0) Externs.DeleteObject(_hCountFont);
                _hCountFont = small;
            }
        }
        var c = OverlayGeometry.FindControls(Layout, ViewDip, _host.BarTop);
        void Place(nint h, OverlayBox box)
        {
            if (h == 0) return;
            bool visible = Visible && !box.IsEmpty;
            if (visible) Externs.MoveWindow(h, (int)(box.X * _host.Scale), (int)(box.Y * _host.Scale), (int)(box.W * _host.Scale), (int)(box.H * _host.Scale), true);
            Externs.ShowWindow(h, visible ? 5 : 0);
        }
        Place(_hFindLabel, c.Label);
        Place(_hFind, c.Input);
        Place(_hFindCount, c.Count);
        Place(_hPrev, c.Prev);
        Place(_hNext, c.Next);
        Place(_hClose, c.Close);
        Place(_hOptions, c.Options);
        Place(_hReplaceLabel, c.ReplaceLabel);
        Place(_hReplace, c.Replace);
        Place(_hBtnRep, c.ReplaceOne);
        Place(_hBtnAll, c.ReplaceAll);
        int padding = (int)((DeskStyle ? 0f : 6f) * _host.Scale);
        foreach (nint h in new[] { _hFind, _hReplace })
            if (h != 0) Externs.SendMessageW(h, 0x00D3 /*EM_SETMARGINS*/, (nint)3, (nint)(padding | (padding << 16)));
    }

    public void Open(bool replace = false)
    {
        if (IsComposing) return;
        bool wasVisible = Visible;
        if (!wasVisible)
        {
            Opening?.Invoke();
            _openingDocument = _host.Document; _openingEditing = _host.Editing;
            _openingText = CurrentSearchText();
            if (_host.Editing && _host.Editor != 0) (_openingStart, _openingEnd) = Externs.EditSelection(_host.Editor);
            else { _openingStart = _host.Document.SelHead; _openingEnd = _host.Document.SelTail; }
        }
        EnsureFind();
        if (replace && _host.Editing) { EnsureReplace(); _replaceRow = true; }
        else if (!(Visible && _host.Editing)) _replaceRow = false;
        _findVisible = true;
        _host.LayoutChanged();
        Externs.SetFocus(_hFind);
        Externs.SendMessageW(_hFind, 0x00B1, 0, (nint)(-1));
        if (!wasVisible) Next(false);
    }

    // U7：选区气泡「查找」：换上查询词再打开（已打开则重查）。换词时的 EN_CHANGE 不单独查一次。
    bool _settingQuery;
    public void OpenWith(string query)
    {
        if (IsComposing) return;
        EnsureFind();
        _settingQuery = true;
        try { Externs.SetWindowTextW(_hFind, query); }
        finally { _settingQuery = false; }
        _lastEmpty = -1;
        if (!Visible) { Open(); return; }
        Externs.SetFocus(_hFind);
        Externs.SendMessageW(_hFind, 0x00B1, 0, (nint)(-1));
        Next(false);
    }

    public void Close()
    {
        if (!Visible || IsComposing) return;
        QueryCommitted?.Invoke(Externs.WindowText(_hFind));
        _scopeText = null; _lastEmpty = -1; UpdateOptionLabel();
        _findVisible = false;
        foreach (nint h in _controls) Externs.ShowWindow(h, 0);
        _host.Document.SetSearchQuery("", 0, false);
        _host.LayoutChanged();
        Externs.SetFocus(_host.Editing && _host.Editor != 0 ? _host.Editor : _host.Owner);
    }

    public void Paint(nint dc, RECT client) => PaintSurface(dc, client, _host.Scale, Layout, ViewDip, _host.BarTop);

    // desk 样式：阅读态浮动框与编辑态条（图标钮、「n / m」计数、无边框输入）。
    bool DeskStyle => Layout is FindLayout.Desk or FindLayout.DeskEdit or FindLayout.DeskEditReplace;

    // 浮动面板:Surface 底 + 1 DIP ControlBorder 描边、6 DIP 圆角,不画阴影,面板外一个像素都不碰;
    // 窄窗条/编辑态:全宽 Canvas 底 + 底缘细线。静态以便离屏像素测试。
    internal static void PaintSurface(nint dc, RECT client, float scale, FindLayout layout, float view, float top = float.NaN)
    {
        if (layout == FindLayout.Hidden) return;
        PBind(Rt, dc, &client);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            var box = OverlayGeometry.FindBox(layout, view, float.IsNaN(top) ? ToolH : top);
            if (layout == FindLayout.Desk) PaintDeskSurface(box);
            else if (layout is FindLayout.DeskEdit or FindLayout.DeskEditReplace) PaintDeskEditStrip(box, view, float.IsNaN(top) ? ToolH : top, layout == FindLayout.DeskEditReplace);
            else if (layout == FindLayout.Panel)
            {
                var outer = new D2D1_ROUNDED_RECT { rect = new() { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom }, radiusX = 6f, radiusY = 6f };
                PFillRound(Rt, &outer, BControlBorder);
                var inner = new D2D1_ROUNDED_RECT { rect = new() { left = box.X + 1f, top = box.Y + 1f, right = box.Right - 1f, bottom = box.Bottom - 1f }, radiusX = 5f, radiusY = 5f };
                PFillRound(Rt, &inner, BCode);
            }
            else
            {
                var rect = new D2D1_RECT_F { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom };
                PFill(Rt, &rect, BCanvas);
                FillLine(box.X, box.Bottom - 0.5f, box.Right, box.Bottom - 0.5f, BHair, 1f);
            }
        }
        finally { PEnd(Rt, &a, &b); }
    }

    // U7：演示 .findbar：纸色底、1 DIP 描边、12 圆角，阴影 0 14px 34px -18px（分层近似）；左侧放大镜为弱化墨色。
    static void PaintDeskSurface(OverlayBox box)
    {
        const float r = OverlayGeometry.DeskFindRadius;
        if (!HighContrast)
        {
            // 收缩 18、下移 14、模糊 34：两侧几乎不露，只在底缘下方晕出约 13 DIP。
            float alpha = (DarkMode ? 0.6f : 0.2f) / 4f;
            for (int k = 0; k < 6; k++)
            {
                float g = -18f + 3.4f * k;
                var s = new D2D1_ROUNDED_RECT { rect = new() { left = box.X - g, top = box.Y + 14f - g, right = box.Right + g, bottom = box.Bottom + 14f + g }, radiusX = MathF.Max(2f, r + g), radiusY = MathF.Max(2f, r + g) };
                SetOpacity(BShade, alpha); PFillRound(Rt, &s, BShade);
            }
            SetOpacity(BShade, 1f);
        }
        var outer = new D2D1_ROUNDED_RECT { rect = new() { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom }, radiusX = r, radiusY = r };
        PFillRound(Rt, &outer, HighContrast ? BControlBorder : BHair);
        var inner = new D2D1_ROUNDED_RECT { rect = new() { left = box.X + 1f, top = box.Y + 1f, right = box.Right - 1f, bottom = box.Bottom - 1f }, radiusX = r - 1f, radiusY = r - 1f };
        PFillRound(Rt, &inner, BCanvas);
        var icon = OverlayGeometry.DeskFindIconBox(box);
        DeskGlyph(3000, icon.X, icon.Y, icon.W, BMuted, 1.6f);
    }

    // 试用修复：desk 编辑态条——纸色底 + 底缘细线，放大镜，输入框画描边框（EDIT 本身无边框、纸色底）。
    static void PaintDeskEditStrip(OverlayBox box, float view, float top, bool replaceRow)
    {
        var rect = new D2D1_RECT_F { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom };
        PFill(Rt, &rect, BCanvas);
        FillLine(box.X, box.Bottom - 0.5f, box.Right, box.Bottom - 0.5f, BHair, 1f);
        var icon = OverlayGeometry.DeskEditIconBox(top);
        DeskGlyph(3000, icon.X, icon.Y, icon.W, BMuted, 1.6f);
        for (int row = 0; row < (replaceRow ? 2 : 1); row++)
        {
            var f = OverlayGeometry.DeskEditField(view, top, row == 1);
            float r = OverlayGeometry.DeskEditFieldRadius;
            var outline = new D2D1_ROUNDED_RECT { rect = new() { left = f.X + 0.5f, top = f.Y + 0.5f, right = f.Right - 0.5f, bottom = f.Bottom - 0.5f }, radiusX = r, radiusY = r };
            PStrokeRound(Rt, &outline, HighContrast ? BInk : BHairZone, 1f, 0);
        }
    }

    // 演示 20 格 SVG 缩到 size：3000 放大镜、3003 上一处、3004 下一处、3005 关闭、3006 选项（滑杆）。
    static void DeskGlyph(uint id, float x, float y, float size, nint brush, float stroke)
    {
        float k = size / 20f, sw = stroke * k;
        float X(float u) => x + u * k;
        float Y(float v) => y + v * k;
        void Line(float u1, float v1, float u2, float v2) => StrokeLine(X(u1), Y(v1), X(u2), Y(v2), brush, sw);
        void Ring(float u, float v, float rr)
        {
            var e = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = X(u), y = Y(v) }, radiusX = rr * k, radiusY = rr * k };
            PDrawEllipse(Rt, &e, brush, sw, 0);
        }
        switch (id)
        {
            case 3000: Ring(8.6f, 8.6f, 5.1f); Line(12.4f, 12.4f, 16.6f, 16.6f); break;
            case 3003: Line(5.5f, 12.5f, 10f, 8f); Line(10f, 8f, 14.5f, 12.5f); break;
            case 3004: Line(5.5f, 7.5f, 10f, 12f); Line(10f, 12f, 14.5f, 7.5f); break;
            case 3005: Line(6f, 6f, 14f, 14f); Line(14f, 6f, 6f, 14f); break;
            case 3006:
                Line(4f, 6.5f, 16f, 6.5f); Line(4f, 13.5f, 16f, 13.5f);
                Ring(7.5f, 6.5f, 1.9f); Ring(12.5f, 13.5f, 1.9f); break;
        }
    }

    // U7：desk 图标钮（演示 .ib）：28×28、7 圆角、ink2 图标 15；按下为 paper-2 底、ink 图标；
    // 选项非默认时在图标右上角加朱点（原“选项*”的等价提示）。
    internal static void PaintDeskButton(nint dc, RECT pixels, float scale, uint id, uint state, bool marked)
    {
        PBind(Rt, dc, &pixels);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            float width = (pixels.R - pixels.L) / scale, height = (pixels.B - pixels.T) / scale;
            var rect = new D2D1_RECT_F { right = width, bottom = height };
            PFill(Rt, &rect, BCanvas);
            bool down = (state & 1) != 0, focus = (state & 0x10) != 0;
            if (down || HighContrast && focus)
            {
                var chip = new D2D1_ROUNDED_RECT { rect = new() { right = width, bottom = height }, radiusX = 7f, radiusY = 7f };
                PFillRound(Rt, &chip, down ? BCode : BControlBorder);
            }
            float g = 15f;
            DeskGlyph(id, (width - g) / 2f, (height - g) / 2f, g, down ? BInk : BInk2, 1.7f);
            if (marked)
            {
                var dot = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = width - 7f, y = 7f }, radiusX = 2.2f, radiusY = 2.2f };
                PFillEllipse(Rt, &dot, BAccent);
            }
            if (focus && !HighContrast) FillLine(7f, height - 2f, width - 7f, height - 2f, BAccent, 1.5f);
        }
        finally { PEnd(Rt, &a, &b); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DRAWITEMSTRUCT
    {
        public uint type, id, item, action, state;
        public nint window, dc;
        public RECT rect;
        public nuint data;
    }

    public bool DrawButton(nint pointer)
    {
        var item = (DRAWITEMSTRUCT*)pointer;
        if (item->type != 4 || item->id < 3001 || item->id > 3006) return false;
        if (DeskStyle && item->id >= 3003)
        {
            PaintDeskButton(item->dc, item->rect, _host.Scale, item->id, item->state, item->id == 3006 && (SelectionOnly || Options != default));
            return true;
        }
        string label = Externs.WindowText(item->window);
        if (!_labels.TryGetValue(label, out nint layout)) _labels[label] = layout = Dw.MkLayout(label, FmtToolbar, 10000f);
        PaintButton(item->dc, item->rect, _host.Scale, layout, Layout == FindLayout.Panel, item->state);
        return true;
    }

    // 面板上按钮反相(Surface 面板配 Canvas 芯片)并加 1 DIP ControlBorder 描边:深色下 Surface/Canvas 太接近;
    // 高对比度下两者同为系统窗口色,描边是唯一的按钮边界。窄窗条按钮不变。静态以便离屏像素测试。
    internal static void PaintButton(nint dc, RECT pixels, float scale, nint layout, bool panel, uint state)
    {
        PBind(Rt, dc, &pixels);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            float width = (pixels.R - pixels.L) / scale, height = (pixels.B - pixels.T) / scale;
            var rect = new D2D1_RECT_F { right = width, bottom = height };
            PFill(Rt, &rect, panel ? BCode : BCanvas);
            nint face = (state & 1) != 0 ? BSel : panel ? BCanvas : BCode;
            var chip = new D2D1_ROUNDED_RECT { rect = new() { left = 1f, top = 1f, right = width - 1f, bottom = height - 1f }, radiusX = 4f, radiusY = 4f };
            if (panel || HighContrast)
            {
                PFillRound(Rt, &chip, BControlBorder);
                var inner = new D2D1_ROUNDED_RECT { rect = new() { left = 2f, top = 2f, right = width - 2f, bottom = height - 2f }, radiusX = 3f, radiusY = 3f };
                PFillRound(Rt, &inner, face);
            }
            else PFillRound(Rt, &chip, face);
            PDraw(Rt, new() { x = (width - Dw.LayoutW(layout)) / 2f, y = (height - Dw.LayoutH(layout)) / 2f }, layout, BInk, 0);
            if ((state & 0x10) != 0) FillLine(5f, height - 3f, width - 5f, height - 3f, BAccent, 2f);
        }
        finally { PEnd(Rt, &a, &b); }
    }

    public void SetOptions(SearchOptions options)
    {
        if (IsComposing) return;
        Options = options; _lastEmpty = -1;
        OptionsChanged?.Invoke(options);
        UpdateOptionLabel();
        if (Visible) Next(false);
    }

    void ShowOptions()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, Options.CaseSensitive ? 8u : 0u, (nint)3007, "区分大小写");
            AppendMenuW(menu, Options.WholeWord ? 8u : 0u, (nint)3008, "整词匹配（Unicode 边界）");
            AppendMenuW(menu, Options.RegularExpression ? 8u : 0u, (nint)3020, "正则表达式");
            AppendMenuW(menu, SelectionOnly ? 8u : 0u, (nint)3021, "仅选区（关闭或切换模式后清除）");
            AppendMenuW(menu, 0, (nint)3022, "查看匹配结果…");
            AppendMenuW(menu, 0, (nint)3023, "查找历史…");
            GetWindowRect(_hOptions, out var rect);
            int selected = TrackPopupMenu(menu, 0x0102, rect.L, rect.B, 0, _host.Owner, 0);
            if (selected != 0) HandleCommand(selected, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }

    // step：用户逐处前进（Enter、上一处/下一处）；打字、打开、重算时为 false。演示稿只在逐处前进时留返回书签。
    public void Next(bool back, bool step = false)
    {
        if (IsComposing) return;
        SearchAction(() => NextCore(back, step));
    }
    void NextCore(bool back, bool step)
    {
        string q = Externs.WindowText(_hFind);
        if (q.Length == 0)
        {
            _host.Document.SetSearchQuery("", 0, false);
            SetStatus("");
            _ = Externs.InvalidateRect(_host.Owner, IntPtr.Zero, 0);
            return;
        }
        if (_host.Editing && _host.Editor != IntPtr.Zero) FindInEdit(q, back);
        else FindInDoc(q, back, step);
    }

    void DoReplaceOne()
    {
        if (!_host.Editing || _host.Editor == IntPtr.Zero) { SetStatus("仅编辑模式可替换"); return; }
        string q = Externs.WindowText(_hFind), r = Externs.WindowText(_hReplace);
        if (q.Length == 0) return;
        var (a, b) = Externs.EditSelection(_host.Editor);
        string text = Externs.WindowText(_host.Editor);
        var range = SearchRange(text);
        if (a >= range.Start && b <= range.End && TextSearch.MatchesAt(text, a, b - a, q, Options))
        {
            string value = TextSearch.ReplacementAt(text, a, b - a, q, r, Options);
            fixed (char* p = value) _ = Externs.SendMessageW(_host.Editor, 0x00C2, (IntPtr)1, (IntPtr)p); _host.TextChanged();
            if (SelectionOnly) { _scopeEnd += value.Length - (b - a); _scopeText = Externs.WindowText(_host.Editor); }
        }
        FindInEdit(q, false);
    }

    void DoReplaceAll()
    {
        if (!_host.Editing || _host.Editor == IntPtr.Zero) { SetStatus("仅编辑模式可替换"); return; }
        string q = Externs.WindowText(_hFind), r = Externs.WindowText(_hReplace);
        if (q.Length == 0) return;
        string text = Externs.WindowText(_host.Editor);
        var range = SearchRange(text);
        var (ns, n) = TextSearch.ReplaceAll(text, q, r, Options, range.Start, range.End);
        if (n == 0) { SetStatus("无匹配"); return; }
        _ = Externs.SendMessageW(_host.Editor, 0x00B1, IntPtr.Zero, (IntPtr)(-1));
        fixed (char* p = ns) _ = Externs.SendMessageW(_host.Editor, 0x00C2, (IntPtr)1, (IntPtr)p);
        _host.TextChanged();
        if (SelectionOnly) { _scopeEnd += ns.Length - text.Length; _scopeText = ns; }
        SetStatus($"{n} 处已替换");
    }

    void FindInDoc(string q, bool back, bool step)
    {
        // 全命中表:DocView 重建并定位当前命中;Enter/Shift+Enter 相对当前选区推进
        int from = back ? _host.Document.SelHead : _host.Document.SelTail;
        string text = _host.Document.SearchText(); var range = SearchRange(text);
        if (from == _lastEmpty) from = back ? (from == 0 ? int.MaxValue : from - 1) : from + 1;
        int idx = _host.Document.SetSearchQuery(q, from, back, Options, range.Start, range.End);
        var hits = _host.Document.SearchHits;
        if (idx < 0 || hits.Count == 0) { SetStatus(NoMatch); return; }
        // Reveal the complete match: a transient directory would cover it; a pinned one (D2) never covers the column.
        if (!_host.Document.TocPinned) _host.Document.SetToc(false);
        var (hs, hl2) = hits[idx];
        _lastEmpty = hl2 == 0 ? hs : -1;
        _host.Document.SelectRange(hs, hs + hl2);
        // 命中不在视口内才滚动(块内线性插值定位)
        float y = _host.Document.YAtOffset(hs);
        if (Layout == FindLayout.Desk)
        {
            // U7（演示 showHit）：命中不在视口 [40, 可见高 − 90] 内才滚，滚到可见高的 1/3 处；随后重描朱笔圈。
            float vis = _host.ViewHeight / _host.Scale - _host.ContentTop;
            if (y < _host.Offset + 40f || y > _host.Offset + vis - 90f) _host.RevealHit(y - vis / 3f, step);
            _host.HitShown();
        }
        else RevealClassic(y);
        _host.Document.RevealOffsetX(hs);
        SetStatus(CountText(idx + 1, hits.Count));
        Externs.InvalidateRect(_host.Owner, 0, 0);
    }

    void RevealClassic(float y)
    {
        float bot = _host.Offset + _host.ViewHeight / _host.Scale - _host.ContentTop - 30f;
        // 按覆盖层实际矩形判断遮挡(宽窗面板 / 窄窗条);文档开头被挡时宿主允许在查找期间向上越界露出
        float covered = _host.Offset + OverlayGeometry.RevealInset(_host.ContentTop, Box);
        if (y < covered + 8f || y > bot) _host.ScrollTo(y - (_host.ViewHeight / _host.Scale) * 0.35f);
        // Code blocks scroll sideways, so a hit can also be off to the right; scrolling the
        // page vertically alone would highlight it where it cannot be seen (30): RevealOffsetX in FindInDoc.
    }

    void FindInEdit(string q, bool back)
    {
        string text = Externs.WindowText(_host.Editor); var range = SearchRange(text);
        var hits = TextSearch.FindAll(text, q, Options, range.Start, range.End);
        var (head, tail) = Externs.EditSelection(_host.Editor);
        int from = back ? head : tail;
        if (from == _lastEmpty) from = back ? (from == 0 ? int.MaxValue : from - 1) : from + 1;
        int selected = TextSearch.Next(hits, from, back);
        if (selected < 0) { SetStatus(NoMatch); return; }
        var (start, length) = hits[selected];
        _lastEmpty = length == 0 ? start : -1;
        _ = Externs.SendMessageW(_host.Editor, 0x00B1, (IntPtr)start, (IntPtr)(start + length));
        _ = Externs.SendMessageW(_host.Editor, 0x00B7, IntPtr.Zero, IntPtr.Zero);
        SetStatus(CountText(selected + 1, hits.Count));
    }

    // U7：desk 计数照演示写“n / m”“无结果”；classic 保持原文案。
    string NoMatch => DeskStyle ? "无结果" : "无匹配";
    string CountText(int index, int count) => DeskStyle ? $"{index} / {count}" : $"{index}/{count}";
    void UpdateOptionLabel()
    {
        if (_hOptions == 0) return;
        Externs.SetWindowTextW(_hOptions, SelectionOnly ? "选项/选区" : Options == default ? "选项" : "选项*");
        Externs.InvalidateRect(_hOptions, 0, 0);   // desk 图标钮的朱点随选项变化
    }
    string CurrentSearchText() => _host.Editing && _host.Editor != 0 ? Externs.WindowText(_host.Editor) : _host.Document.SearchText();
    void SearchAction(Action action)
    {
        try { action(); }
        catch (RegexMatchTimeoutException) { SetStatus("正则超时"); }
        catch (ArgumentException) { SetStatus("表达式无效"); }
        catch (InvalidOperationException ex) { SetStatus(ex.Message); }
    }
    (int Start, int End) SearchRange(string text)
    {
        if (SelectionOnly && (!ReferenceEquals(_scopeDocument, _host.Document) || _scopeEditing != _host.Editing)) { _scopeText = null; UpdateOptionLabel(); }
        if (!SelectionOnly) return (0, text.Length);
        if (text != _scopeText) throw new InvalidOperationException("选区已变化，请重选范围");
        return (_scopeStart, _scopeEnd);
    }
    void ToggleSelectionScope()
    {
        if (SelectionOnly) { _scopeText = null; UpdateOptionLabel(); Next(false); return; }
        string text = CurrentSearchText(); int start, end;
        if (ReferenceEquals(_openingDocument, _host.Document) && _openingEditing == _host.Editing && _openingText == text)
        { start = _openingStart; end = _openingEnd; }
        else if (_host.Editing) (start, end) = Externs.EditSelection(_host.Editor);
        else { start = _host.Document.SelHead; end = _host.Document.SelTail; }
        if (end <= start) throw new InvalidOperationException("请先选择正文范围");
        _scopeDocument = _host.Document; _scopeEditing = _host.Editing; _scopeText = text;
        _scopeStart = Math.Clamp(start, 0, text.Length); _scopeEnd = Math.Clamp(end, _scopeStart, text.Length);
        Externs.SetWindowTextW(_hOptions, "选项/选区"); _lastEmpty = -1; Next(false);
    }
    void ShowResults()
    {
        string query = Externs.WindowText(_hFind), text = CurrentSearchText(); var document = _host.Document; bool editing = _host.Editing;
        var range = SearchRange(text); var hits = TextSearch.FindAll(text, query, Options, range.Start, range.End);
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        var labels = hits.Take(1000).Select(hit =>
        {
            int index = starts.BinarySearch(hit.start); if (index < 0) index = ~index - 1;
            int begin = Math.Max(starts[index], hit.start - 30), length = Math.Min(110, text.Length - begin);
            return (editing ? "源码 " : "预览文本 ") + (index + 1) + ": " + text.Substring(begin, length).Replace("\r", "").Replace("\n", " ");
        }).ToArray();
        var result = NativeForm.Show(_host.Owner, "查找结果", hits.Count + " 处匹配；最多显示前 1000 处。", null, labels, false);
        if (result == null || result.Selected.Length == 0) return;
        if (!ReferenceEquals(document, _host.Document) || editing != _host.Editing || text != CurrentSearchText()) throw new InvalidOperationException("文档已变化，请重新查找");
        var chosen = hits[result.Selected[0]];
        if (editing) { Externs.SendMessageW(_host.Editor, 0x00B1, (nint)chosen.start, (nint)(chosen.start + chosen.len)); Externs.SendMessageW(_host.Editor, 0x00B7, 0, 0); }
        else
        {
            document.SelectRange(chosen.start, chosen.start + chosen.len);
            _host.ScrollTo(Math.Max(0, document.YAtOffset(chosen.start) - 40));
            document.RevealOffsetX(chosen.start);   // 同上:代码块可能横向滚走了(30)
        }
    }
    void ShowHistory()
    {
        var history = AppPreferences.Current.SearchHistory.ToArray();
        var result = NativeForm.Show(_host.Owner, "查找历史", "本机保存的最近查询；可在行为设置清空。", null, history, false);
        if (result != null && result.Selected.Length > 0) SetQuery(history[result.Selected[0]]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _findVisible = false;
        foreach (var input in _inputProcs)
            if (Externs.IsWindow(input.Key))
            {
                Externs.SetWindowLongPtrW(input.Key, -4, input.Value);
                Externs.SetWindowLongPtrW(input.Key, -21, 0);
            }
        _inputProcs.Clear();
        _compositions.Clear();
        foreach (nint h in _controls) if (Externs.IsWindow(h)) Externs.DestroyWindow(h);
        _controls.Clear();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (_hFindFont != 0) Externs.DeleteObject(_hFindFont);
        if (_hCountFont != 0) Externs.DeleteObject(_hCountFont);
        foreach (nint layout in _labels.Values) Marshal.Release(layout);
        _labels.Clear();
    }
}
