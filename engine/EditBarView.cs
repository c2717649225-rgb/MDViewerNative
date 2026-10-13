using System;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

internal readonly record struct EditBarPaintState(bool Dirty, bool SaveEnabled, EditBarPart Hover, string SaveKey, string DoneKey, string SaveLabel);

// U9a：编辑条的绘制（几何在 EditBar）。纸色底、下缘 1 细线；左：✎ 编辑中（朱色半粗）· 状态圆点与文字（muted）；
// 右：「保存 Ctrl+S」（有改动时朱色实心，否则 45% 淡出）与「完成 Ctrl+E」（细边框，悬停转朱）。
internal static unsafe class EditBarView
{
    static nint _mode, _full, _shortDirty, _saved, _save, _saveKey, _done, _doneKey;
    static string _saveKeyText, _doneKeyText, _saveText;

    static void Ensure(string saveLabel, string saveKey, string doneKey)
    {
        if (_mode == 0) _mode = Dw.MkLayout(EditBar.Mode, FmtEditMode, 10000f);
        if (_full == 0) _full = Dw.MkLayout(EditBar.Unsaved + EditBar.Backed, FmtDeskUi, 10000f);
        if (_shortDirty == 0) _shortDirty = Dw.MkLayout(EditBar.Unsaved, FmtDeskUi, 10000f);
        if (_saved == 0) _saved = Dw.MkLayout(EditBar.Saved, FmtDeskUi, 10000f);
        if (_done == 0) _done = Dw.MkLayout(EditBar.Done, FmtDeskUi, 10000f);
        Swap(ref _save, ref _saveText, saveLabel, FmtDeskUi);
        Swap(ref _saveKey, ref _saveKeyText, saveKey, FmtEditKey);
        Swap(ref _doneKey, ref _doneKeyText, doneKey, FmtEditKey);
    }

    static void Swap(ref nint layout, ref string text, string value, nint format)
    {
        if (layout != 0 && text == value) return;
        if (layout != 0) System.Runtime.InteropServices.Marshal.Release(layout);
        layout = Dw.MkLayout(value ?? "", format, 10000f); text = value;
    }

    public static EditBarLayout Measure(float viewW, float top, bool dirty, string saveLabel, string saveKey, string doneKey)
    {
        Ensure(saveLabel, saveKey, doneKey);
        var t = new EditBarText(Dw.LayoutW(_mode), dirty ? Dw.LayoutW(_full) : Dw.LayoutW(_saved), dirty ? Dw.LayoutW(_shortDirty) : Dw.LayoutW(_saved),
            Dw.LayoutW(_save), Dw.LayoutW(_saveKey), Dw.LayoutW(_done), Dw.LayoutW(_doneKey));
        return EditBar.Layout(viewW, top, t);
    }

    static float Baseline(nint layout)
    {
        var lines = Dw.Lines(layout);
        return lines.Length > 0 ? lines[0].baseline : Dw.LayoutH(layout) * 0.8f;
    }

    public static void Paint(nint hdc, RECT client, float scale, EditBarLayout l, EditBarPaintState s)
    {
        PBind(Rt, hdc, &client);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            var bar = new D2D1_RECT_F { left = l.Bar.X, top = l.Bar.Y, right = l.Bar.Right, bottom = l.Bar.Bottom };
            PFill(Rt, &bar, BCanvas);
            var rule = new D2D1_RECT_F { left = l.Bar.X, top = l.Bar.Bottom - 1f, right = l.Bar.Right, bottom = l.Bar.Bottom };
            PFill(Rt, &rule, HighContrast ? BControlBorder : BHair);
            // 统一基线：以 12.5 的正文字为准，整条垂直居中。
            float textH = Dw.LayoutH(_saved), baseY = l.Bar.Y + (EditBar.Height - textH) / 2f + Baseline(_saved);
            Pencil(l.IconX, l.Bar.Y + (EditBar.Height - EditBar.Icon) / 2f, BAccent);
            PDraw(Rt, new D2D1_POINT_2F { x = l.ModeX, y = baseY - Baseline(_mode) }, _mode, BAccent, 0);
            if (l.State != EditBarState.Hidden)
            {
                float cy = l.Bar.Y + EditBar.Height / 2f;
                if (s.Dirty)
                {
                    var dot = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = l.StateDotX + EditBar.Ring / 2f, y = cy }, radiusX = EditBar.Dot / 2f, radiusY = EditBar.Dot / 2f };
                    PFillEllipse(Rt, &dot, BAccent);
                }
                else
                {
                    float r = (EditBar.Ring - EditBar.RingStroke) / 2f;
                    var ring = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = l.StateDotX + EditBar.Ring / 2f, y = cy }, radiusX = r, radiusY = r };
                    PDrawEllipse(Rt, &ring, BMuted, EditBar.RingStroke, 0);
                }
                nint text = !s.Dirty ? _saved : l.State == EditBarState.Full ? _full : _shortDirty;
                PDraw(Rt, new D2D1_POINT_2F { x = l.StateX, y = baseY - Baseline(text) }, text, BMuted, 0);
            }
            Button(l.Save, _save, _saveKey, l.SaveKeyX, baseY, solid: s.SaveEnabled && !HighContrast, faded: !s.SaveEnabled, hot: s.Hover == EditBarPart.Save && s.SaveEnabled);
            Button(l.Done, _done, _doneKey, l.DoneKeyX, baseY, solid: false, faded: false, hot: s.Hover == EditBarPart.Done);
        }
        finally { PEnd(Rt, &a, &b); }
    }

    static void Button(EditBarBox box, nint label, nint key, float keyX, float baseY, bool solid, bool faded, bool hot)
    {
        float op = faded ? EditBar.DisabledOpacity : 1f;
        nint edge = solid ? BAccent : hot ? BAccent : HighContrast ? BControlBorder : BHairZone;
        nint face = solid ? BAccent : BCanvas;
        nint ink = solid ? BGlint : hot ? BAccent : BInk;   // 实心按钮字色固定近白（演示 #fbf3e8），不随纸张
        nint keyInk = solid ? BGlint : BMuted;
        foreach (nint br in new[] { edge, face, ink, keyInk, BHairZone }) SetOpacity(br, op);
        try
        {
            var outer = new D2D1_ROUNDED_RECT { rect = new() { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom }, radiusX = EditBar.ButtonRadius, radiusY = EditBar.ButtonRadius };
            PFillRound(Rt, &outer, edge);
            var inner = new D2D1_ROUNDED_RECT { rect = new() { left = box.X + 1f, top = box.Y + 1f, right = box.Right - 1f, bottom = box.Bottom - 1f }, radiusX = EditBar.ButtonRadius - 1f, radiusY = EditBar.ButtonRadius - 1f };
            PFillRound(Rt, &inner, face);
            PDraw(Rt, new D2D1_POINT_2F { x = box.X + EditBar.ButtonInsetX, y = baseY - Baseline(label) }, label, ink, 0);
            // 键帽：1 细框、底边 2、5 圆角；实心按钮上为半透明白框、透明底。宿主不给快捷键时不画。
            if (key == 0 || Dw.LayoutW(key) <= 0f) return;
            float kw = EditBar.KeyWidth(Dw.LayoutW(key)), kh = Dw.LayoutH(key) + 2f * EditBar.KeyInsetY + 3f;
            float ky = baseY - Baseline(key) - EditBar.KeyInsetY - 1f;
            var cap = new D2D1_ROUNDED_RECT { rect = new() { left = keyX, top = ky, right = keyX + kw, bottom = ky + kh }, radiusX = EditBar.KeyRadius, radiusY = EditBar.KeyRadius };
            if (solid)
            {
                SetOpacity(BGlint, 0.45f); PStrokeRound(Rt, &cap, BGlint, 1f, 0); SetOpacity(BGlint, 1f);
            }
            else
            {
                PFillRound(Rt, &cap, BHairZone);
                var capFace = new D2D1_ROUNDED_RECT { rect = new() { left = keyX + 1f, top = ky + 1f, right = keyX + kw - 1f, bottom = ky + kh - 2f }, radiusX = EditBar.KeyRadius - 1f, radiusY = EditBar.KeyRadius - 1f };
                PFillRound(Rt, &capFace, BCanvas);
            }
            PDraw(Rt, new D2D1_POINT_2F { x = keyX + 1f + EditBar.KeyInsetX, y = baseY - Baseline(key) }, key, keyInk, 0);
        }
        finally { foreach (nint br in new[] { edge, face, ink, keyInk, BHairZone }) SetOpacity(br, 1f); }
    }

    // 演示 16 格 SVG：斜放的笔（M10.8 2.7 13.3 5.2 5.6 12.9 2.5 13.5 3.1 10.4z）。
    static void Pencil(float x, float y, nint brush)
    {
        float k = EditBar.Icon / 16f, sw = 1.4f * k;
        float[] p = { 10.8f, 2.7f, 13.3f, 5.2f, 5.6f, 12.9f, 2.5f, 13.5f, 3.1f, 10.4f };
        for (int i = 0; i < 5; i++)
        {
            int j = (i + 1) % 5;
            StrokeLine(x + p[2 * i] * k, y + p[2 * i + 1] * k, x + p[2 * j] * k, y + p[2 * j + 1] * k, brush, sw);
        }
    }
}
