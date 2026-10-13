using System;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// U11：desk 自绘面板共用的几样形状（圆角填充 / 描边、box-shadow 近似、文字）。只画，不做决定；调用方负责 BeginDraw。
internal static unsafe class DeskShapes
{
    public static void Draw(nint l, float x, float y, nint brush) { if (l != 0) PDraw(Rt, new D2D1_POINT_2F { x = x, y = y }, l, brush, 0); }

    public static void FillRound(float x, float y, float w, float h, float r, nint brush)
    {
        var rr = new D2D1_ROUNDED_RECT { rect = new() { left = x, top = y, right = x + w, bottom = y + h }, radiusX = r, radiusY = r };
        PFillRound(Rt, &rr, brush);
    }

    public static void StrokeRound(float x, float y, float w, float h, float r, nint brush, float width)
    {
        float i = width / 2f;
        var rr = new D2D1_ROUNDED_RECT { rect = new() { left = x + i, top = y + i, right = x + w - i, bottom = y + h - i }, radiusX = MathF.Max(0f, r - i), radiusY = MathF.Max(0f, r - i) };
        PStrokeRound(Rt, &rr, brush, width, 0);
    }

    // 键帽（演示 kbd：.76em、左右 .45em、1px 描边、下边 2px、圆角 5、纸色底）；right 为右缘，返回左缘。命令面板与「更多」菜单共用。
    public static float Key(nint layout, float right, float cy, float keySize)
    {
        float kw = Dw.LayoutW(layout), kh = Dw.LayoutH(layout);
        float insetX = keySize * 0.45f, w = kw + insetX * 2f + 2f, h = kh + 3f + 1f;
        float x = right - w, y = cy - h / 2f;
        nint edge = HighContrast ? BInk : BControlBorder;
        FillRound(x, y, w, h, 5f, edge);
        FillRound(x + 1f, y + 1f, w - 2f, h - 3f, 4f, BCanvas);
        Draw(layout, x + 1f + insetX, y + 1f, BInk2);
        return x;
    }

    // box-shadow 的近似：从内到外叠几层逐渐放大的圆角矩形，每层透明度相同，叠出由浓到淡的边。
    public static void Shadow(float x, float y, float w, float h, float radius, float dy, float blur, float spread, float alpha)
    {
        if (HighContrast || BShadow == 0 || ShadowAlpha <= 0f) return;
        const int layers = 12;
        float a = ShadowAlpha * alpha;
        for (int k = 0; k < layers; k++)
        {
            float e = spread - blur + 2f * blur * (k + 0.5f) / layers;
            float lw = w + 2f * e, lh = h + 2f * e;
            if (lw <= 0f || lh <= 0f) continue;
            SetOpacity(BShadow, a / layers);
            FillRound(x - e, y - e + dy, lw, lh, MathF.Max(0f, radius + e), BShadow);
        }
        SetOpacity(BShadow, 1f);
    }
}
