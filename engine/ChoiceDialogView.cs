using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.DeskShapes;

namespace MDViewerNative;

// 试用反馈：desk 的选择框（未保存关闭、保存冲突）。仍是系统模态对话框（#32770）——模态循环、Tab / Enter / Esc、
// 按钮命令号（1000…、IDCANCEL）与 TaskDialog 一致，只是外观自绘：卡片、标题、正文、BS_OWNERDRAW 按钮都用纸色画。
// 几何在 ChoiceDialog；本类只管窗口与绘制。同一时刻只有一个（模态）。
internal static unsafe class ChoiceDialogView
{
    static State _s;

    sealed class State
    {
        public float Scale;
        public nint Owner, Dialog, Hot, Heading, Body, Hollow;
        public nint[] Labels;
        public ChoiceFrame Frame;
        public readonly PaintBuffer Buffer = new();
    }

    // labels：主按钮在前，最后一个是“取消”。返回 1000 + 下标，取消 / 关闭返回 2；创建失败返回 -1（调用方改用系统对话框）。
    public static int Show(nint owner, float scale, string heading, string body, IReadOnlyList<string> labels)
    {
        if (_s != null || Rt == 0 || labels.Count == 0) return -1;
        var s = new State { Owner = owner, Scale = scale > 0f ? scale : 1f, Labels = new nint[labels.Count] };
        nint headingFormat = Dw.MkFormat("Segoe UI", 600u, ChoiceDialog.HeadingSize);
        nint bodyFormat = Dw.MkFormat("Segoe UI", 400u, ChoiceDialog.BodySize);
        nint buttonFormat = Dw.MkFormat("Segoe UI", 400u, ChoiceDialog.ButtonTextSize);
        nint template = 0;
        try
        {
            if (headingFormat == 0 || bodyFormat == 0 || buttonFormat == 0) return -1;
            var widths = new float[labels.Count];
            for (int i = 0; i < labels.Count; i++)
            {
                s.Labels[i] = Dw.MkLayout(labels[i], buttonFormat, 10000f);
                widths[i] = s.Labels[i] != 0 ? Dw.LayoutW(s.Labels[i]) : 40f;
            }
            float w = ChoiceDialog.Width(widths), text = ChoiceDialog.TextWidth(w);
            s.Heading = Dw.MkLayout(heading ?? "", headingFormat, text);
            s.Body = string.IsNullOrEmpty(body) ? 0 : Dw.MkLayout(body, bodyFormat, text);
            s.Frame = ChoiceDialog.Layout(w, s.Heading != 0 ? Dw.LayoutH(s.Heading) : 20f, s.Body != 0 ? Dw.LayoutH(s.Body) : 0f, widths);
            s.Hollow = GetStockObject(5 /*NULL_BRUSH*/);
            template = BuildTemplate(labels);
            _s = s;
            nint result = DialogBoxIndirectParamW(Externs.GetModuleHandleW(null), template, owner,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&DialogProc, 0);
            return (int)result;
        }
        finally
        {
            _s = null;
            if (template != 0) Marshal.FreeHGlobal(template);
            foreach (nint l in s.Labels) if (l != 0) Marshal.Release(l);
            if (s.Heading != 0) Marshal.Release(s.Heading);
            if (s.Body != 0) Marshal.Release(s.Body);
            foreach (nint f in new[] { headingFormat, bodyFormat, buttonFormat }) if (f != 0) Marshal.Release(f);
            s.Buffer.Dispose();
        }
    }

    // 内存里的 DLGTEMPLATE：无标题栏的弹出窗 + 每个选项一个 BS_OWNERDRAW 按钮；尺寸与位置在 WM_INITDIALOG 按像素重排。
    static nint BuildTemplate(IReadOnlyList<string> labels)
    {
        var words = new List<ushort>();
        void Dword(uint v) { words.Add((ushort)(v & 0xFFFF)); words.Add((ushort)(v >> 16)); }
        void Str(string v) { foreach (char c in v) words.Add(c); words.Add(0); }
        void Align() { if ((words.Count & 1) != 0) words.Add(0); }
        Dword(0x80000000u | 0x02000000u /*WS_POPUP | WS_CLIPCHILDREN*/);
        Dword(0);
        words.Add((ushort)labels.Count);
        for (int i = 0; i < 4; i++) words.Add(0);   // x, y, cx, cy
        words.Add(0); words.Add(0); Str("");        // 无菜单、默认类、无标题
        for (int i = 0; i < labels.Count; i++)
        {
            Align();
            Dword(0x40000000u | 0x10000000u | 0x00010000u | 0x0000000Bu /*WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW*/);
            Dword(0);
            for (int k = 0; k < 4; k++) words.Add(0);
            words.Add((ushort)ChoiceDialog.IdOf(i, labels.Count));
            words.Add(0xFFFF); words.Add(0x0080);    // BUTTON
            Str(labels[i]);                          // 文字留给读屏与测试辨认；绘制用自己的布局
            words.Add(0);
        }
        nint mem = Marshal.AllocHGlobal(words.Count * 2 + 8);
        var span = new Span<ushort>((void*)mem, words.Count);
        for (int i = 0; i < words.Count; i++) span[i] = words[i];
        return mem;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static nint DialogProc(nint h, uint m, nint w, nint l)
    {
        var s = _s;
        if (s == null) return 0;
        try { return Handle(s, h, m, w, l); }
        catch (Exception ex) { AppLog.Error("choice dialog", ex); EndDialog(h, ChoiceDialog.CancelId); return 1; }
    }

    static nint Handle(State s, nint h, uint m, nint w, nint l)
    {
        switch (m)
        {
            case 0x0110: Init(s, h); return 1;                                        // WM_INITDIALOG：焦点落第一个按钮（主按钮）
            case 0x0014: return 1;                                                    // WM_ERASEBKGND：WM_PAINT 整幅画
            case 0x000F: Paint(s, h); return 1;
            case 0x002B: DrawButton(s, (DrawItem*)l); return 1;                       // WM_DRAWITEM
            case 0x0135: return s.Hollow;                                             // WM_CTLCOLORBTN：按钮不先刷底
            case 0x0111:                                                              // WM_COMMAND
                {
                    int id = (int)(w & 0xFFFF);
                    if (id == 1) id = GetDlgCtrlID(Externs.GetFocus());               // Enter：按下有焦点的按钮
                    if (id == ChoiceDialog.CancelId || (id >= ChoiceDialog.FirstId && id < ChoiceDialog.FirstId + s.Labels.Length - 1)) EndDialog(h, id);
                    return 1;
                }
            case 0x0466: EndDialog(h, w); return 1;                                   // TDM_CLICK_BUTTON（与 TaskDialog 相同的自动化入口）
            case 0x0010: EndDialog(h, ChoiceDialog.CancelId); return 1;               // WM_CLOSE
            case 0x0020: Hover(s, h, w); return 0;                                    // WM_SETCURSOR：光标所在的按钮
            case 0x0113 when w == 1:                                                  // 光标离开按钮后清掉悬停
                if (GetCursorPos(out var p) && WindowFromPoint(p) != s.Hot) { SetHot(s, h, 0); Externs.KillTimer(h, 1); }
                return 1;
        }
        return 0;
    }

    static void Init(State s, nint h)
    {
        s.Dialog = h;
        var f = s.Frame;
        float k = s.Scale, room = ChoiceDialog.RingRoom;
        for (int i = 0; i < f.Buttons.Count; i++)
        {
            var b = f.Buttons[i];
            nint button = GetDlgItem(h, ChoiceDialog.IdOf(i, f.Buttons.Count));
            Externs.MoveWindow(button, (int)MathF.Round((b.X - room) * k), (int)MathF.Round((b.Y - room) * k),
                (int)MathF.Round((b.W + room * 2f) * k), (int)MathF.Round((b.H + room * 2f) * k), false);
        }
        Externs.GetClientRect(s.Owner, out var client);
        var (x, y) = ChoiceDialog.Place(client.R / k, client.B / k, f.W, f.H);
        var origin = new Pt { X = (int)MathF.Round(x * k), Y = (int)MathF.Round(y * k) };
        _ = ClientToScreen(s.Owner, ref origin);
        uint round = 2 /*DWMWCP_ROUND*/, edge = Bgr(ActivePaper is PaperColors p ? p.Edge : 0x808080u);
        _ = DwmSetWindowAttribute(h, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &round, 4);
        _ = DwmSetWindowAttribute(h, 34 /*DWMWA_BORDER_COLOR*/, &edge, 4);
        PopupWindows.ShowPainted(h, origin.X, origin.Y, (int)MathF.Ceiling(f.W * k), (int)MathF.Ceiling(f.H * k), 0);
    }

    static uint Bgr(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    static void Hover(State s, nint h, nint window)
    {
        nint hot = window != h && window != 0 ? window : 0;
        if (hot == s.Hot) return;
        SetHot(s, h, hot);
        if (hot != 0) Externs.SetTimer(h, 1, 50, 0);
    }

    static void SetHot(State s, nint h, nint hot)
    {
        nint before = s.Hot;
        s.Hot = hot;
        if (before != 0) Externs.InvalidateRect(before, 0, 0);
        if (hot != 0) Externs.InvalidateRect(hot, 0, 0);
    }

    static void Bind(State s, nint dc, RECT* rect)
    {
        PBind(Rt, dc, rect);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * s.Scale, 96f * s.Scale);
    }

    // 卡片：纸色底、标题（墨色、半粗）、正文（次墨色）。先画进离屏缓冲再一次贴出。
    static void Paint(State s, nint h)
    {
        nint win = Externs.BeginPaint(h, out var ps);
        Externs.GetClientRect(h, out var rect);
        nint dc = s.Buffer.Begin(win, rect.R, rect.B, rect, out _);
        if (dc == 0) dc = win;
        try
        {
            Bind(s, dc, &rect);
            PBegin(Rt);
            ulong a = 0, b = 0;
            try
            {
                var f = s.Frame;
                var all = new D2D1_RECT_F { right = f.W + 1f, bottom = f.H + 1f };
                PFill(Rt, &all, BCanvas);
                Draw(s.Heading, f.Heading.X, f.Heading.Y, BInk);
                if (s.Body != 0) Draw(s.Body, f.Body.X, f.Body.Y, BInk2);
            }
            finally { PEnd(Rt, &a, &b); }
        }
        finally
        {
            if (dc != win) s.Buffer.Present(win, rect);
            Externs.EndPaint(h, in ps);
        }
    }

    // 主按钮：朱色实心、近白字（同编辑条“保存”）；其余：纸色底、细边、墨色字。悬停加深 / 换悬停底，按下再深一档；
    // 键盘焦点（系统显示焦点提示时）画 1.5 朱色外环，同顶栏。
    static void DrawButton(State s, DrawItem* item)
    {
        if (item == null || item->CtlType != 4 /*ODT_BUTTON*/) return;
        int count = s.Labels.Length, index = -1;
        for (int i = 0; i < count; i++) if (ChoiceDialog.IdOf(i, count) == (int)item->CtlID) index = i;
        if (index < 0) return;
        var rect = item->rcItem;
        Bind(s, item->hDC, &rect);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            float k = s.Scale, room = ChoiceDialog.RingRoom, r = ChoiceDialog.ButtonRadius;
            float w = rect.R / k, hgt = rect.B / k, x = room, y = room, bw = w - room * 2f, bh = hgt - room * 2f;
            var all = new D2D1_RECT_F { right = w + 1f, bottom = hgt + 1f };
            PFill(Rt, &all, BCanvas);
            bool primary = index == 0, down = (item->itemState & 0x1) != 0, hot = item->hwndItem == s.Hot;
            bool focus = (item->itemState & 0x10) != 0 && (item->itemState & 0x200) == 0;
            nint ink;
            if (primary)
            {
                SetOpacity(BAccent, down ? 0.78f : hot ? 0.9f : 1f);
                FillRound(x, y, bw, bh, r, BAccent);
                SetOpacity(BAccent, 1f);
                ink = BGlint;
            }
            else
            {
                FillRound(x, y, bw, bh, r, down ? BSel : hot ? BSurfaceHover : BCanvas);
                StrokeRound(x, y, bw, bh, r, hot || down ? BControlBorder : BHairZone, 1f);
                ink = BInk;
            }
            nint label = s.Labels[index];
            if (label != 0) Draw(label, x + (bw - Dw.LayoutW(label)) / 2f, y + (bh - Dw.LayoutH(label)) / 2f, ink);
            if (focus)
            {
                SetOpacity(BAccent, 0.75f);
                StrokeRound(x - 2f, y - 2f, bw + 4f, bh + 4f, r + 2f, BAccent, DeskMetrics.FocusRingWidth);
                SetOpacity(BAccent, 1f);
            }
        }
        finally { PEnd(Rt, &a, &b); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawItem { public uint CtlType, CtlID, itemID, itemAction, itemState; public nint hwndItem, hDC; public RECT rcItem; public nint itemData; }
    [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }

    [DllImport("user32.dll")] static extern nint DialogBoxIndirectParamW(nint instance, nint template, nint owner, nint proc, nint param);
    [DllImport("user32.dll")] static extern bool EndDialog(nint h, nint result);
    [DllImport("user32.dll")] static extern nint GetDlgItem(nint h, int id);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(nint h);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref Pt p);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Pt p);
    [DllImport("gdi32.dll")] static extern nint GetStockObject(int index);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint h, int attr, uint* value, int size);
}
