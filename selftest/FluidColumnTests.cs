using System;
using System.Reflection;
using MDViewerNative;

// S80 用户决定:正文随窗口宽度排版,两侧与上下固定留白 48 DIP,不再有 40em 上限;
// 窄窗口留白沿用 12 → 20 的连续过渡。宽窗口展开的目录让出自己的宽度(正文重排),
// 窄窗口仍是临时覆盖层。
internal static class FluidColumnTests
{
    private static readonly FieldInfo Ys = typeof(DocView).GetField("_ys", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Heights = typeof(DocView).GetField("_heights", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Run(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            using var doc = new DocView();
            doc.Load("# 标题\n\n正文第一段。\n\n## 第二节\n\n正文第二段。\n");
            string wide = "";
            bool fills = true;
            foreach (float size in new[] { 12f, 16f, 20f, 28f })
            {
                RenderResources.SetBodySize(size);
                foreach (float v in new[] { 800f, 1200f, 1600f, 2400f })
                {
                    doc.OnWidth(v);
                    doc.EnsureLayout();
                    bool ok = MathF.Abs(doc.ColX - 48f) < 0.01f && MathF.Abs(doc.ColW - (v - 96f)) < 0.01f;
                    if (!ok) { fills = false; wide = $"size={size} v={v} colX={doc.ColX} colW={doc.ColW}"; }
                }
            }
            RenderResources.SetBodySize(16f);
            check("FluidColumn/wide window: column fills it with 48 DIP margins at every size", fills, fills ? "800/1200/1600/2400 × 12/16/20/28" : wide);

            doc.OnWidth(360f); float narrow = doc.ColX;
            doc.OnWidth(520f); float mid = doc.ColX;
            check("FluidColumn/narrow windows keep the small margins", MathF.Abs(narrow - 12f) < 0.01f && MathF.Abs(mid - 20f) < 0.01f, $"360→{narrow} 520→{mid}");

            bool monotonic = true, continuous = true;
            float previous = -1f;
            for (float v = 200f; v <= 2400f; v += 1f)
            {
                doc.OnWidth(v);
                float w = doc.ColW;
                if (previous >= 0f && w < previous - 0.001f) monotonic = false;
                if (previous >= 0f && w - previous > 1.001f) continuous = false;
                previous = w;
            }
            check("FluidColumn/column never shrinks as the window grows", monotonic, "200..2400 step 1");
            check("FluidColumn/column grows without jumps", continuous, "Δcol ≤ ΔV");

            doc.OnWidth(1200f);
            var ys = (float[])Ys.GetValue(doc); var hs = (float[])Heights.GetValue(doc);
            float top = ys[0], bottom = doc.TotalH - (ys[^1] + hs[^1]);
            check("FluidColumn/48 DIP above the first block and below the last", MathF.Abs(top - 48f) < 0.01f && MathF.Abs(bottom - 48f) < 0.01f, $"top={top} bottom={bottom}");

            doc.SetToc(true);
            doc.OnWidth(1200f);
            float px = doc.ColX, pr = doc.ColX + doc.ColW;
            bool pinned = doc.TocPinned && doc.TocVisible && px >= doc.TocWidth + 48f - 0.01f && MathF.Abs(pr - (1200f - 48f)) < 0.01f;
            check("FluidColumn/pinned directory makes room instead of covering text", pinned, $"pinned={doc.TocPinned} colX={px} right={pr} toc={doc.TocWidth}");
            doc.SetToc(false);
            bool back = MathF.Abs(doc.ColX - 48f) < 0.01f && MathF.Abs(doc.ColW - 1104f) < 0.01f;
            check("FluidColumn/closing the pinned directory gives the width back", back, $"colX={doc.ColX} colW={doc.ColW}");

            doc.OnWidth(700f);
            float x700 = doc.ColX, w700 = doc.ColW;
            doc.SetToc(true);
            bool overlay = !doc.TocPinned && doc.TocVisible && doc.ColX == x700 && doc.ColW == w700;
            doc.SetToc(false);
            check("FluidColumn/narrow directory stays a transient overlay", overlay, $"pinned={doc.TocPinned} colX {x700}→{doc.ColX}");

            // 布局没变时(如只改了窗口高度)阅读位置必须原样保留:块间空隙与底部留白里的偏移
            // 按“块 + 块内比例”取回会被钳到上一块底边,来回几次就漂走(S80 窗口测试实测 4 DIP)。
            doc.OnWidth(1200f);
            var gys = (float[])Ys.GetValue(doc); var ghs = (float[])Heights.GetValue(doc);
            float gap = (gys[0] + ghs[0] + gys[1]) / 2f, tail = doc.TotalH - 8f;
            float keptGap = doc.KeepReading(gap, () => doc.OnWidth(1200f));
            float keptTail = doc.KeepReading(tail, () => doc.OnWidth(1200f));
            check("FluidColumn/unchanged layout keeps an offset between blocks", keptGap == gap, $"{gap}→{keptGap}");
            check("FluidColumn/unchanged layout keeps an offset in the bottom margin", keptTail == tail, $"{tail}→{keptTail}");
            float inBlock = gys[2] + ghs[2] * 0.5f;
            float reflowed = doc.KeepReading(inBlock, () => doc.OnWidth(420f));
            var rys = (float[])Ys.GetValue(doc); var rhs = (float[])Heights.GetValue(doc);
            check("FluidColumn/real reflow returns to the same place in the block", MathF.Abs(reflowed - (rys[2] + rhs[2] * 0.5f)) < 0.01f, $"{reflowed} vs {rys[2] + rhs[2] * 0.5f}");

            // S81:toc=1 的含义是“宽窗口常驻”。常驻目录要跟随窗口宽度:缩到放不下时收起(不留覆盖层压住正文),
            // 重新够宽(包括启动时第一次得到真实宽度)时按偏好展开;窄窗口里临时打开的覆盖层不受尺寸变化影响。
            check("FluidColumn/pinned directory folds away when the window gets too narrow",
                !OverlayGeometry.TocAfterResize(preferPinned: true, showing: true, wasPinned: true, pinnedNow: false), "pinned → narrow");
            check("FluidColumn/toc=1 pins the directory once the window is wide enough",
                OverlayGeometry.TocAfterResize(preferPinned: true, showing: false, wasPinned: false, pinnedNow: true), "startup / widen");
            check("FluidColumn/transient overlay survives resizing a narrow window",
                OverlayGeometry.TocAfterResize(preferPinned: false, showing: true, wasPinned: false, pinnedNow: false), "overlay stays");
            check("FluidColumn/closed directory stays closed without toc=1",
                !OverlayGeometry.TocAfterResize(preferPinned: false, showing: false, wasPinned: false, pinnedNow: true), "no preference");
            check("FluidColumn/toggling the overlay in a narrow window keeps the pinned preference",
                OverlayGeometry.TocPreference(preferPinned: true, showing: false, pinnedNow: false)
                && !OverlayGeometry.TocPreference(preferPinned: false, showing: true, pinnedNow: false), "narrow toggle");
            check("FluidColumn/toggling the pinned directory records the preference",
                OverlayGeometry.TocPreference(preferPinned: false, showing: true, pinnedNow: true)
                && !OverlayGeometry.TocPreference(preferPinned: true, showing: false, pinnedNow: true), "wide toggle");
        }
    }
}

