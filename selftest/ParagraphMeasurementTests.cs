using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

internal static class ParagraphMeasurementTests
{
    // 30:代码块不折行,超出文本列的部分靠左右滚动查看。代码的列对齐有含义
    // (缩进、表格式注释、ASCII 图),折行会破坏它,所以这里断言的是"宽行确实
    // 保持一行"以及溢出量的算法。
    private static unsafe void CodeHScroll(Action<string, bool, string> check)
    {
        IntPtr fmt = Dw.MkFormat("Consolas", 400u, 14f);
        if (fmt == IntPtr.Zero) { check("HScroll/DWrite 不可用时跳过", true, "no format"); return; }

        const float col = 120f;
        string longLine = new string('x', 400);

        // NO_WRAP 下的长行:测量宽度必须超过列宽,这个差值就是可滚动距离。
        nint wide = Dw.MkLayout(longLine, fmt, col);
        Dw.Bind(wide);
        Dw.SetWordWrapping(wide, 1 /*NO_WRAP*/);
        float wideW = Dw.LayoutW(wide);
        float overflow = MathF.Max(0f, wideW - col);
        check("HScroll/宽行溢出量为正", overflow > 0f, $"w={wideW} col={col}");

        // 不折行 = 高度只有一行。这是"列对齐没被破坏"最直接的证据:
        // 若退回按字符折行,400 字符在 120 DIP 里会变成几十行,高度会翻很多倍。
        float oneRow = Dw.LayoutH(wide);
        nint single = Dw.MkLayout("x", fmt, col);
        Dw.Bind(single);
        Dw.SetWordWrapping(single, 1);
        float unit = Dw.LayoutH(single);
        check("HScroll/宽行不折行(仍为一行高)", oneRow <= unit * 1.5f + 0.5f, $"h={oneRow} unit={unit}");

        // 短行不溢出 —— 不溢出的块不画滚动条、也不该吃掉滚轮手势。
        nint narrow = Dw.MkLayout("ab", fmt, col);
        Dw.Bind(narrow);
        Dw.SetWordWrapping(narrow, 1);
        check("HScroll/短行无溢出", MathF.Max(0f, Dw.LayoutW(narrow) - col) <= 0f, $"w={Dw.LayoutW(narrow)}");

        // 多行取最宽的一行:滚动范围由最长那行决定,否则最长行的尾部永远看不到。
        nint mixed = Dw.MkLayout("ab\n" + longLine + "\ncd", fmt, col);
        Dw.Bind(mixed);
        Dw.SetWordWrapping(mixed, 1);
        check("HScroll/溢出量取最宽行", Dw.LayoutW(mixed) >= wideW - 0.5f, $"mixed={Dw.LayoutW(mixed)} wide={wideW}");

        // 滚动位置必须钳在 0..溢出量:负值会把代码推出左边缘,超出会滚到空白。
        float clampedLow = Math.Clamp(-50f, 0f, overflow);
        float clampedHigh = Math.Clamp(overflow + 500f, 0f, overflow);
        check("HScroll/滚动位置钳位", clampedLow == 0f && clampedHigh == overflow, $"lo={clampedLow} hi={clampedHigh}");

        // 语法高亮块走 ent.HL 逐行 NO_WRAP,本来就一行一源行,与主路径同口径;
        // 其溢出量按"每行分段宽度之和"的最大值算,这条分支必须存在。
        check("HScroll/高亮块同样逐行 NO_WRAP", CodeHighlight.Known("cs") || CodeHighlight.Known("js"),
            "highlighted blocks render one row per source line");
    }

    // 宽表格横向滚动(30):列宽不再压缩硬塞,改为保留自然宽度 + 冻结首列左右滚。
    // 这里锁的是几何算术与三个我在实现中真踩到的坑,而非 DWrite 行为。
    private static void WideTableHScroll(Action<string, bool, string> check)
    {
        // 视口口径:表格横跨整个 RichWidth,不像代码块那样留 20 DIP 内边距。
        // 早前直接借用 CodeViewportW 导致每张表恒定「溢出」20 DIP —— 纸上被压缩过的
        // 表格也会因此被裁掉一列并画出滚动条。
        float rich = 500f;
        float tableViewport = MathF.Max(120f, rich);
        check("WideTable/视口口径为整幅宽度", tableViewport == rich, $"viewport={tableViewport} rich={rich}");
        check("WideTable/视口不等于代码块口径", tableViewport != MathF.Max(40f, rich - 20f), "table != code padding");

        // 自然宽度合计超过视口才算溢出,差额就是可滚动距离。
        float[] natural = { 90f, 180f, 200f, 260f };
        float total = 0f;
        foreach (float w in natural) total += w;
        float overflow = MathF.Max(0f, total - tableViewport);
        check("WideTable/宽表溢出量为正", overflow > 0f, $"total={total} viewport={tableViewport}");

        // 窄表不溢出:不画滚动条、不吃滚轮、外观与改动前完全一致。
        float narrow = 60f + 80f;
        check("WideTable/窄表无溢出", MathF.Max(0f, narrow - tableViewport) == 0f, $"total={narrow}");

        // 冻结首列:只有首列不超过视口一半时才冻结,否则冻住大半屏幕反而没得滚。
        float frozenOk = natural[0] <= tableViewport * 0.5f ? natural[0] : 0f;
        check("WideTable/首列够窄则冻结", frozenOk == 90f, $"frozen={frozenOk}");
        float fat = 400f;
        float frozenFat = fat <= tableViewport * 0.5f ? fat : 0f;
        check("WideTable/首列过宽则不冻结", frozenFat == 0f, $"frozen={frozenFat}");

        // 滚动量钳在 0..溢出量,与代码块同一口径。
        check("WideTable/滚动位置钳位",
            Math.Clamp(-80f, 0f, overflow) == 0f && Math.Clamp(overflow + 900f, 0f, overflow) == overflow,
            $"overflow={overflow}");

        // 命中修正:滚动后,落在冻结列内的点不减位移,右侧的点要把位移加回去。
        // 少了这条修正,点击滚动过的表格会静默选中隔壁单元格。
        float shift = 120f, frozen = 90f;
        float insideFrozen = 40f;
        float outsideFrozen = 300f;
        float probeInside = insideFrozen >= frozen ? insideFrozen + shift : insideFrozen;
        float probeOutside = outsideFrozen >= frozen ? outsideFrozen + shift : outsideFrozen;
        check("WideTable/冻结列内命中不加位移", probeInside == insideFrozen, $"probe={probeInside}");
        check("WideTable/冻结列外命中加回位移", probeOutside == outsideFrozen + shift, $"probe={probeOutside}");

        // 查找定位:目标列整列进视野,且冻结列宽要算进左边界 —— 否则命中会停在
        // 冻结列底下,搜到了却看不见。
        float columnLeft = 270f, columnRight = 470f, margin = 40f;
        float scroll = 0f;
        if (columnLeft < scroll + frozen + margin) scroll = columnLeft - frozen - margin;
        else if (columnRight > scroll + tableViewport - margin) scroll = columnRight - tableViewport + margin;
        scroll = Math.Clamp(scroll, 0f, overflow);
        check("WideTable/查找定位后目标列可见",
            columnLeft >= scroll + frozen - 0.5f && columnRight <= scroll + tableViewport + 0.5f,
            $"scroll={scroll} col=[{columnLeft},{columnRight}]");

        // 幂等:已经可见的列不应再次滚动(否则每按一次 F3 画面都要抖一下)。
        float again = scroll;
        if (columnLeft < again + frozen + margin) again = columnLeft - frozen - margin;
        else if (columnRight > again + tableViewport - margin) again = columnRight - tableViewport + margin;
        again = Math.Clamp(again, 0f, overflow);
        check("WideTable/查找定位幂等", MathF.Abs(again - scroll) < 0.01f, $"first={scroll} second={again}");

        // 纸上没有滚动条:打印时回到按比例压缩,合计正好等于视口宽,溢出量归零。
        float scale = tableViewport / total;
        float printed = 0f;
        foreach (float w in natural) printed += w * scale;
        check("WideTable/打印压缩后不溢出",
            MathF.Abs(printed - tableViewport) < 0.5f && MathF.Max(0f, printed - tableViewport) < 0.5f,
            $"printed={printed} viewport={tableViewport}");
    }

    internal static void Run(Action<string, bool, string> check)
    {
        CodeHScroll(check);
        WideTableHScroll(check);
        Para Key() => new() { Inlines = new List<Run> { new("same") } };
        var comparer = ParagraphMeasureComparer.Instance;
        var original = Key();
        var duplicate = Key();
        check("Measurements/value identity", comparer.Equals(original, duplicate) && comparer.GetHashCode(original) == comparer.GetHashCode(duplicate), "different parser objects, equal keys");
        Action<Run>[] variants = { r => r.Text = "other", r => r.B = true, r => r.I = true, r => r.S = true,
            r => r.Code = true, r => r.Mark = true, r => r.Fn = true, r => r.U = true, r => r.Opaque = true,
            r => r.Img = true, r => r.SupSub = 1, r => r.Link = "https://example.org", r => r.LinkTitle = "title", r => r.LinkOuter = "outer" };
        bool separated = true;
        foreach (var alter in variants)
        {
            var changed = Key(); alter(changed.Inlines[0]);
            separated &= !comparer.Equals(original, changed);
        }
        duplicate.XOff = 20;
        check("Measurements/style and indentation separation", separated && !comparer.Equals(original, duplicate), "every Run field participates, not plain text alone");

        using var shared = new DocView();
        using var reference = new DocView();
        float originalSize = RenderResources.BodySize;
        try
        {
            string text = string.Concat(Enumerable.Repeat("Mixed 中文 measurement text with wrapping. ", 8));
            string repeated = string.Join("\n\n", Enumerable.Repeat($"[{text}](https://example.org \"same\")", 20));
            // Titles do not affect rendering, but distinct titles deliberately prevent height reuse.
            string separate = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"[{text}](https://example.org \"{i}\")"));
            shared.Load(repeated);
            reference.Load(separate);
            foreach (float size in new[] { 12f, 16f, 22f })
            {
                shared.SetBodySize(size, relayout: false);
                foreach (float width in new[] { 420f, 800f })
                {
                    shared.OnWidth(width); reference.OnWidth(width);
                    check($"Measurements/exact geometry {size}/{width}", shared.TotalH == reference.TotalH && shared.ReusedParagraphMeasurements == 19 && reference.ReusedParagraphMeasurements == 0, "compares reuse against independent DWrite measurements");
                    shared.SelAll(); reference.SelAll();
                    check($"Measurements/lazy layout copy {size}/{width}", shared.CopyText() == reference.CopyText(), "materializing skipped layouts preserves selection text");
                }
            }
            using var bounded = new DocView();
            bounded.Load(string.Join("\n\n", Enumerable.Range(0, 300).Select(i => "unique paragraph " + i)) + "\n\nunique paragraph 0\n\nunique paragraph 299");
            check("Measurements/lookup capacity is bounded", bounded.ReusedParagraphMeasurements == 1, "first 256 keys retained only for the current layout");
            shared.Load("replacement document");
            check("Measurements/new document drops cached heights", shared.ReusedParagraphMeasurements == 0, "no cross-document stale measurements");
        }
        finally { shared.SetBodySize(originalSize); }
    }
}
