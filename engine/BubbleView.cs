using System;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// U7：选区气泡的绘制（几何在 SelectionBubble）。墨色底配纸色字，悬停按钮底为半透明灰；高对比度加 1 DIP 描边。
internal static unsafe class BubbleView
{
    static nint _copy, _find;

    public static (float CopyW, float FindW, float TextH) Measure()
    {
        if (_copy == 0) _copy = Dw.MkLayout("复制", FmtDeskUi, 10000f);
        if (_find == 0) _find = Dw.MkLayout("查找", FmtDeskUi, 10000f);
        return (Dw.LayoutW(_copy), Dw.LayoutW(_find), Dw.LayoutH(_copy));
    }

    public static void Paint(nint hdc, RECT client, float scale, BubbleLayout l, BubblePart hover)
    {
        PBind(Rt, hdc, &client);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        ulong a = 0, b = 0;
        try
        {
            var box = l.Box;
            const float r = SelectionBubble.Radius;
            if (!HighContrast)
            {
                // 演示 0 10px 30px -12px：收缩 12、下移 10，只在底缘下方晕开。
                float alpha = (DarkMode ? 0.6f : 0.2f) / 4f;
                for (int k = 0; k < 5; k++)
                {
                    float g = -12f + 3f * k;
                    var s = new D2D1_ROUNDED_RECT { rect = new() { left = box.X - g, top = box.Y + 10f - g, right = box.Right + g, bottom = box.Bottom + 10f + g }, radiusX = MathF.Max(2f, r + g), radiusY = MathF.Max(2f, r + g) };
                    SetOpacity(BShade, alpha); PFillRound(Rt, &s, BShade);
                }
                SetOpacity(BShade, 1f);
            }
            if (HighContrast)
            {
                var edge = new D2D1_ROUNDED_RECT { rect = new() { left = box.X - 1f, top = box.Y - 1f, right = box.Right + 1f, bottom = box.Bottom + 1f }, radiusX = r + 1f, radiusY = r + 1f };
                PFillRound(Rt, &edge, BControlBorder);
            }
            var body = new D2D1_ROUNDED_RECT { rect = new() { left = box.X, top = box.Y, right = box.Right, bottom = box.Bottom }, radiusX = r, radiusY = r };
            PFillRound(Rt, &body, BInk);
            // 小三角：10×10 方块转 45°、中心压在底缘，露出的一半就是三角（高 = 半对角线 ≈ 7）。逐行填充。
            float ay = l.Below ? box.Y : box.Bottom, half = SelectionBubble.Arrow / 2f * 1.41421f;
            for (float t = 0f; t <= half; t += 0.5f)
            {
                float yy = l.Below ? ay - t : ay + t, wv = half - t;
                FillLine(l.ArrowX - wv, yy, l.ArrowX + wv, yy, BInk, 1f);
            }
            var (_, _, textH) = Measure();
            Button(l.Copy, _copy, 0, hover == BubblePart.Copy, textH);
            Button(l.Find, _find, 1, hover == BubblePart.Find, textH);
        }
        finally { PEnd(Rt, &a, &b); }
    }

    static void Button(BubbleBox bx, nint text, int icon, bool hot, float textH)
    {
        if (hot)
        {
            var chip = new D2D1_ROUNDED_RECT { rect = new() { left = bx.X, top = bx.Y, right = bx.Right, bottom = bx.Bottom }, radiusX = SelectionBubble.ButtonRadius, radiusY = SelectionBubble.ButtonRadius };
            nint wash = DarkMode ? BShade : BGlint;
            SetOpacity(wash, 0.18f); PFillRound(Rt, &chip, wash); SetOpacity(wash, 1f);
        }
        float ix = bx.X + SelectionBubble.ButtonInsetX, iy = bx.Y + (bx.H - SelectionBubble.Icon) / 2f;
        Glyph(icon, ix, iy, SelectionBubble.Icon, BCanvas);
        PDraw(Rt, new D2D1_POINT_2F { x = ix + SelectionBubble.Icon + SelectionBubble.IconGap, y = bx.Y + (bx.H - textH) / 2f }, text, BCanvas, 0);
    }

    // 演示 20 格 SVG：0 复制（两张叠放的纸）、1 查找（放大镜）。
    static void Glyph(int id, float x, float y, float size, nint brush)
    {
        float k = size / 20f, sw = 1.6f * k;
        float X(float u) => x + u * k;
        float Y(float v) => y + v * k;
        void Line(float u1, float v1, float u2, float v2) => StrokeLine(X(u1), Y(v1), X(u2), Y(v2), brush, sw);
        if (id == 0)
        {
            var front = new D2D1_ROUNDED_RECT { rect = new() { left = X(7f), top = Y(7f), right = X(16.5f), bottom = Y(16.5f) }, radiusX = 1.5f * k, radiusY = 1.5f * k };
            PStrokeRound(Rt, &front, brush, sw, 0);
            Line(13f, 7f, 13f, 4.5f); Line(13f, 4.5f, 11.5f, 3f); Line(11.5f, 3f, 4.5f, 3f); Line(4.5f, 3f, 3f, 4.5f);
            Line(3f, 4.5f, 3f, 11.5f); Line(3f, 11.5f, 4.5f, 13f); Line(4.5f, 13f, 7f, 13f);
        }
        else
        {
            var e = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = X(8.6f), y = Y(8.6f) }, radiusX = 5.1f * k, radiusY = 5.1f * k };
            PDrawEllipse(Rt, &e, brush, sw, 0);
            Line(12.4f, 12.4f, 16.6f, 16.6f);
        }
    }
}
