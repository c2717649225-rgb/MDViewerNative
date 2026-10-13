using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

internal readonly record struct JuanPaintState(float Top, float Scroll, int Current, int Focus, bool KeyboardFocus, int Hover, bool CloseHot, float Alpha, float Rise);

// U8：目录卷目的绘制与文字排版（几何在 Juan）。条目文字布局在 Prepare 时一次建好，窗口尺寸或条目变了才重建。
internal static unsafe class JuanView
{
    static readonly List<nint> _layouts = new();
    static nint[] _no = Array.Empty<nint>(), _title = Array.Empty<nint>(), _page = Array.Empty<nint>();
    static nint _heading, _sub, _tag, _close, _esc, _empty;
    static float[] _baseline = Array.Empty<float>();
    static List<JuanItem> _items;
    static string _docTitle;
    static float _w, _h;
    public static JuanFrame Frame { get; private set; }

    static nint Keep(nint layout) { if (layout != 0) _layouts.Add(layout); return layout; }

    static void Release()
    {
        foreach (nint l in _layouts) Marshal.Release(l);
        _layouts.Clear();
    }

    public static JuanFrame Prepare(List<JuanItem> items, string docTitle, float viewW, float viewH)
    {
        if (ReferenceEquals(items, _items) && docTitle == _docTitle && viewW == _w && viewH == _h) return Frame;
        Release();
        _items = items; _docTitle = docTitle; _w = viewW; _h = viewH;
        _heading = Keep(Dw.MkLayout("目录", FmtJuanHeading, 10000f));
        Dw.SetCharacterSpacing(_heading, 0f, Juan.TitleSize, 0, 2);   // letter-spacing:1em；演示再加 padding-left:1em，绘制时一并居中
        _sub = Keep(Dw.MkLayout(docTitle ?? "", FmtDeskUi, 10000f));
        Dw.SetCharacterSpacing(_sub, 0f, 0.1f * Juan.SubSize, 0, (uint)(docTitle ?? "").Length);
        _tag = Keep(Dw.MkLayout("当前", FmtJuanTag, 10000f));
        _close = Keep(Dw.MkLayout("关闭", FmtDeskUi, 10000f));
        _esc = Keep(Dw.MkLayout("Esc", FmtTipKey, 10000f));
        _empty = Keep(Dw.MkLayout("本文没有标题", FmtDeskUi, 10000f));
        float colW = MathF.Min(Juan.ColumnMax, MathF.Max(0f, viewW - 2f * Juan.SidePad));
        int n = items.Count;
        _no = new nint[n]; _title = new nint[n]; _page = new nint[n]; _baseline = new float[n];
        var levels = new int[n]; var heights = new float[n];
        float tagW = Dw.LayoutW(_tag) + 2f * Juan.TagInsetX + Juan.TagGap;
        for (int i = 0; i < n; i++)
        {
            var it = items[i];
            levels[i] = it.Level;
            _page[i] = Keep(Dw.MkLayout(it.Percent + "%", it.Level == 1 ? FmtJuanPageBold : FmtJuanPage, 10000f));   // 一级整行加粗，章节号与百分比随之
            if (it.Level < 3 && it.No.Length > 0) _no[i] = Keep(Dw.MkLayout(it.No, it.Level == 1 ? FmtJuanNoBold : FmtJuanNo, 10000f));
            float room = colW - Juan.TitleIndent(it.Level) - Juan.RowInsetX - Dw.LayoutW(_page[i]) - 2f * Juan.LeadGap - Juan.LeadMin - tagW;
            _title[i] = Keep(Dw.TrimLayout(it.Title, it.Level == 1 ? FmtJuanL1 : it.Level == 2 ? FmtJuanL2 : FmtJuanL3, MathF.Max(40f, room)));
            var lines = Dw.Lines(_title[i]);
            heights[i] = lines.Length > 0 ? lines[0].height : Dw.LayoutH(_title[i]);
            _baseline[i] = lines.Length > 0 ? lines[0].baseline : heights[i] * 0.8f;
        }
        Frame = Juan.Layout(viewW, viewH, Dw.LayoutH(_heading), Dw.LayoutH(_sub), levels, heights);
        return Frame;
    }

    public static (float X, float Y, float W, float H) CloseBox() =>
        Juan.CloseBox(_w, Dw.LayoutW(_close) + Juan.CloseGap + KeyW(), Dw.LayoutH(_close));

    static float KeyW() => Dw.LayoutW(_esc) + 2f * 0.45f * 9.5f + 2f;

    static float Baseline(nint layout)
    {
        var lines = Dw.Lines(layout);
        return lines.Length > 0 ? lines[0].baseline : Dw.LayoutH(layout) * 0.8f;
    }

    static readonly nint[] Faded = new nint[8];
    static void Fade(float a)
    {
        Faded[0] = BCanvas; Faded[1] = BInk; Faded[2] = BInk2; Faded[3] = BMuted; Faded[4] = BAccent; Faded[5] = BAccentSoft; Faded[6] = BHair; Faded[7] = BHairZone;
        foreach (nint b in Faded) if (b != 0) SetOpacity(b, a);
    }

    public static void Paint(nint hdc, RECT client, float scale, JuanPaintState s)
    {
        if (_items == null) return;
        PBind(Rt, hdc, &client);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        var area = new D2D1_RECT_F { left = 0f, top = s.Top, right = _w, bottom = s.Top + _h };
        PPushClip(Rt, &area, 0);
        ulong a = 0, b = 0;
        try
        {
            Fade(s.Alpha * Juan.Veil);
            PFill(Rt, &area, BCanvas);
            Fade(s.Alpha);
            float oy = s.Top - s.Scroll + s.Rise;
            var f = Frame;
            var (cx, cy, cw, ch) = CloseBox();
            PaintClose(cx, oy + cy, cw, ch, s.CloseHot);
            PDraw(Rt, new D2D1_POINT_2F { x = (_w - Dw.LayoutW(_heading) + Juan.TitleSize) / 2f, y = oy + f.TitleY }, _heading, BInk, 0);
            PDraw(Rt, new D2D1_POINT_2F { x = (_w - Dw.LayoutW(_sub)) / 2f, y = oy + f.SubY }, _sub, BMuted, 0);
            if (_items.Count == 0)
            {
                float ey = f.SubY + Dw.LayoutH(_sub) + Juan.SubGap;
                PDraw(Rt, new D2D1_POINT_2F { x = (_w - Dw.LayoutW(_empty)) / 2f, y = oy + ey }, _empty, BMuted, 0);
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var r = f.Rows[i];
                float top = oy + r.Y;
                if (top > s.Top + _h || top + r.H < s.Top) continue;
                PaintRow(i, f.ColumnX, top, f.ColumnW, r.H, s);
            }
        }
        finally
        {
            Fade(1f);
            PPopClip(Rt);
            PEnd(Rt, &a, &b);
        }
    }

    static void PaintRow(int i, float x, float y, float w, float h, JuanPaintState s)
    {
        var it = _items[i];
        bool focus = s.KeyboardFocus && s.Focus == i, hot = s.Hover == i || focus, current = s.Current == i;
        var box = new D2D1_ROUNDED_RECT { rect = new() { left = x, top = y, right = x + w, bottom = y + h }, radiusX = Juan.RowRadius, radiusY = Juan.RowRadius };
        if (hot) PFillRound(Rt, &box, HighContrast ? BSel : BAccentHover);
        if (focus)
        {
            var bar = new D2D1_RECT_F { left = x, top = y + 2f, right = x + 2f, bottom = y + h - 2f };
            PFill(Rt, &bar, BAccent);
        }
        float baseY = y + Juan.RowInsetY + _baseline[i];
        nint ink = hot || current ? BAccent : it.Level >= 3 ? BInk2 : BInk;
        if (_no[i] != 0) PDraw(Rt, new D2D1_POINT_2F { x = x + Juan.RowInsetX, y = baseY - Baseline(_no[i]) }, _no[i], BAccent, 0);
        float tx = x + Juan.TitleIndent(it.Level);
        PDraw(Rt, new D2D1_POINT_2F { x = tx, y = y + Juan.RowInsetY }, _title[i], ink, 0);
        float end = tx + Dw.LayoutW(_title[i]);
        if (current)
        {
            float tw = Dw.LayoutW(_tag), th = Dw.LayoutH(_tag), bx = end + Juan.TagGap;
            float tb = baseY - Baseline(_tag);
            var pill = new D2D1_ROUNDED_RECT { rect = new() { left = bx, top = tb - Juan.TagInsetY, right = bx + tw + 2f * Juan.TagInsetX, bottom = tb + th + Juan.TagInsetY }, radiusX = Juan.TagRadius, radiusY = Juan.TagRadius };
            PFillRound(Rt, &pill, BAccent);
            PDraw(Rt, new D2D1_POINT_2F { x = bx + Juan.TagInsetX, y = tb }, _tag, BCanvas, 0);
            end = bx + tw + 2f * Juan.TagInsetX;
        }
        float pw = Dw.LayoutW(_page[i]), px = x + w - Juan.RowInsetX - pw;
        PDraw(Rt, new D2D1_POINT_2F { x = px, y = baseY - Baseline(_page[i]) }, _page[i], BMuted, 0);
        float l0 = end + Juan.LeadGap, l1 = px - Juan.LeadGap;
        if (l1 - l0 >= Juan.LeadMin)
        {
            float fontSize = it.Level == 1 ? Juan.L1Size : it.Level == 2 ? Juan.L2Size : Juan.L3Size;
            float ly = MathF.Round(baseY - Juan.LeadLift * fontSize);
            for (float dx = l0; dx + 1f <= l1; dx += 3f)
            {
                var dot = new D2D1_RECT_F { left = dx, top = ly, right = dx + 1f, bottom = ly + 1f };
                PFill(Rt, &dot, BHairZone);
            }
        }
    }

    static void PaintClose(float x, float y, float w, float h, bool hot)
    {
        var outer = new D2D1_ROUNDED_RECT { rect = new() { left = x, top = y, right = x + w, bottom = y + h }, radiusX = Juan.CloseRadius, radiusY = Juan.CloseRadius };
        PFillRound(Rt, &outer, hot ? BHairZone : BHair);
        var inner = new D2D1_ROUNDED_RECT { rect = new() { left = x + 1f, top = y + 1f, right = x + w - 1f, bottom = y + h - 1f }, radiusX = Juan.CloseRadius - 1f, radiusY = Juan.CloseRadius - 1f };
        PFillRound(Rt, &inner, BCanvas);
        nint ink = hot ? BInk : BInk2;
        float ix = x + Juan.CloseInsetX, iy = y + (h - Juan.CloseIcon) / 2f, k = Juan.CloseIcon / 10f;
        StrokeLine(ix + 1.5f * k, iy + 1.5f * k, ix + 8.5f * k, iy + 8.5f * k, ink, 1.4f);
        StrokeLine(ix + 8.5f * k, iy + 1.5f * k, ix + 1.5f * k, iy + 8.5f * k, ink, 1.4f);
        float tx = ix + Juan.CloseIcon + Juan.CloseGap;
        PDraw(Rt, new D2D1_POINT_2F { x = tx, y = y + Juan.CloseInsetY }, _close, ink, 0);
        // 键帽（演示 kbd）：细线框、底边加粗 1、5 圆角、纸色底。
        float kx = tx + Dw.LayoutW(_close) + 4f, kw = KeyW() - 4f + 2f, kh = Dw.LayoutH(_esc) + 2f;
        float ky = y + Juan.CloseInsetY + Baseline(_close) - Baseline(_esc) - 1f;
        var cap = new D2D1_ROUNDED_RECT { rect = new() { left = kx, top = ky, right = kx + kw, bottom = ky + kh + 1f }, radiusX = 5f, radiusY = 5f };
        PFillRound(Rt, &cap, BHairZone);
        var face = new D2D1_ROUNDED_RECT { rect = new() { left = kx + 1f, top = ky + 1f, right = kx + kw - 1f, bottom = ky + kh - 1f }, radiusX = 4f, radiusY = 4f };
        PFillRound(Rt, &face, BCanvas);
        PDraw(Rt, new D2D1_POINT_2F { x = kx + (kw - Dw.LayoutW(_esc)) / 2f, y = ky + 1f }, _esc, BInk2, 0);
    }
}
