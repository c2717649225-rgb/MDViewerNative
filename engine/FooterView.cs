using System;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// U4：页脚视图（规格 §5）。浮在正文之上：上方 26 高从透明渐变到纸色，正文从底下滑过；
// 视口高度不变（正文底部留白 110 保证最后一行能滚到页脚之上）。内容由 FooterModel 决定。
internal static unsafe class FooterView
{
    public const float Height = DeskMetrics.FooterHeight, FadeAbove = DeskMetrics.FooterFade, PadLeft = 20f, PadRight = 30f, Gap = 16f, TextSize = DeskMetrics.FooterTextSize;
    const float SolidFrom = 0.55f;   // 渐变在 55% 处到达纸色（演示 linear-gradient(… , paper 55%)）

    public static void Paint(nint dc, RECT client, float scale, FooterContent content, bool batch = false)
    {
        if (content == null || Rt == IntPtr.Zero || FmtFooter == IntPtr.Zero) return;
        ulong a = 0, b = 0;
        if (!batch)
        {
            PBind(Rt, dc, &client);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
            PBegin(Rt);
        }
        try { Draw(client.R / scale, client.B / scale, content); }
        finally { if (!batch) PEnd(Rt, &a, &b); }
    }

    static void Draw(float width, float bottom, FooterContent content)
    {
        float top = bottom - Height;
        if (HighContrast)
        {
            // 高对比度：实底 + 上缘一条文字色细线，不做渐变（演示 forced-colors）。
            var band = new D2D1_RECT_F { left = 0f, top = top, right = width, bottom = bottom };
            PFill(Rt, &band, BCanvas);
            var line = new D2D1_RECT_F { left = 0f, top = top, right = width, bottom = top + 1f };
            PFill(Rt, &line, BInk);
        }
        else
        {
            float fadeTop = top - FadeAbove, solid = fadeTop + (FadeAbove + Height) * SolidFrom;
            const int steps = 24;
            float step = (solid - fadeTop) / steps;
            for (int i = 0; i < steps; i++)
            {
                SetOpacity(BCanvas, (i + 0.5f) / steps);
                var strip = new D2D1_RECT_F { left = 0f, top = fadeTop + i * step, right = width, bottom = fadeTop + (i + 1) * step };
                PFill(Rt, &strip, BCanvas);
            }
            SetOpacity(BCanvas, 1f);
            var rest = new D2D1_RECT_F { left = 0f, top = solid, right = width, bottom = bottom };
            PFill(Rt, &rest, BCanvas);
        }

        float mid = top + Height * 0.5f;
        // 右侧：从右往左排，间距 16。
        float x = width - PadRight;
        for (int i = content.Right.Count - 1; i >= 0; i--)
        {
            var item = content.Right[i];
            nint layout = Dw.MkLayout(item.Text, FmtFooter, 10000f);
            if (layout == IntPtr.Zero) continue;
            try
            {
                float w = Dw.LayoutW(layout);
                x -= w;
                PDraw(Rt, new D2D1_POINT_2F { x = x, y = mid - Dw.LayoutH(layout) * 0.5f }, layout, item.Accent ? BAccent : BMuted, 0);
                x -= Gap;
            }
            finally { System.Runtime.InteropServices.Marshal.Release(layout); }
        }

        // 左侧：鱼尾 / ↗ / → 记号，随后是章节名或链接；放不下时省略号收尾。
        float left = PadLeft;
        switch (content.Lead)
        {
            case FooterLead.Fish: Fish(left, mid); left += 8f + 0.7f * TextSize; break;
            case FooterLead.External: ExternalArrow(left, mid); left += 11f + 0.6f * TextSize; break;
            case FooterLead.Internal: InternalArrow(left, mid); left += 11f + 0.6f * TextSize; break;
        }
        float room = x - left;
        if (room < 24f || string.IsNullOrEmpty(content.Left)) return;
        nint fmt = content.LeftSerif && FmtFooterSerif != IntPtr.Zero ? FmtFooterSerif : FmtFooter;
        float tracking = content.LeftSerif ? 0.08f * TextSize : 0f;   // 章节名字距 .08em
        nint text = Dw.TrimLayout(content.Left, fmt, tracking > 0f ? room / 1.1f : room);
        if (text == IntPtr.Zero) return;
        try
        {
            if (tracking > 0f) Dw.SetCharacterSpacing(text, 0f, tracking, 0, 0x7FFFFFFFu);   // 超出文本的范围由 DirectWrite 截断，含省略号
            PDraw(Rt, new D2D1_POINT_2F { x = left, y = mid - Dw.LayoutH(text) * 0.5f }, text, BInk2, 0);
        }
        finally { System.Runtime.InteropServices.Marshal.Release(text); }
    }

    // 鱼尾：8×10，下缘 V 形缺口深 3（演示 path M0 0H8V10L4 7 0 10Z）。缺口按细条逐行填，抗锯齿后与路径一致。
    static void Fish(float x, float mid)
    {
        float y = mid - 5f;
        var body = new D2D1_RECT_F { left = x, top = y, right = x + 8f, bottom = y + 7f };
        PFill(Rt, &body, BAccent);
        const int rows = 12;
        for (int r = 0; r < rows; r++)
        {
            float t = (r + 0.5f) / rows, notch = 4f * t;   // 缺口半宽
            float y0 = y + 7f + 3f * r / rows, y1 = y + 7f + 3f * (r + 1) / rows;
            var l = new D2D1_RECT_F { left = x, top = y0, right = x + 4f - notch, bottom = y1 };
            var rr = new D2D1_RECT_F { left = x + 4f + notch, top = y0, right = x + 8f, bottom = y1 };
            PFill(Rt, &l, BAccent);
            PFill(Rt, &rr, BAccent);
        }
    }

    // ↗：演示 path M4 2.5h5.5V8 M9.5 2.5 2.5 9.5（12 格缩到 11）。
    static void ExternalArrow(float x, float mid)
    {
        float s = 11f / 12f, y = mid - 5.5f;
        StrokeLine(x + 4f * s, y + 2.5f * s, x + 9.5f * s, y + 2.5f * s, BAccent, 1.5f);
        StrokeLine(x + 9.5f * s, y + 2.5f * s, x + 9.5f * s, y + 8f * s, BAccent, 1.5f);
        StrokeLine(x + 9.5f * s, y + 2.5f * s, x + 2.5f * s, y + 9.5f * s, BAccent, 1.5f);
    }

    // →：横线加箭头。
    static void InternalArrow(float x, float mid)
    {
        float s = 11f / 12f, y = mid - 5.5f;
        StrokeLine(x + 2f * s, y + 6f * s, x + 10f * s, y + 6f * s, BAccent, 1.5f);
        StrokeLine(x + 7f * s, y + 3f * s, x + 10f * s, y + 6f * s, BAccent, 1.5f);
        StrokeLine(x + 7f * s, y + 9f * s, x + 10f * s, y + 6f * s, BAccent, 1.5f);
    }

}
