using System;
using System.Reflection;
using MDViewerNative;

internal static class ReaderWideContentTests
{
    // Paint fills this existing target collection. Register the same encoded child target
    // here so windowless tests exercise the PUBLIC input path, not just its index codec.
    private static CodeBlockTargets Targets(DocView view) =>
        (CodeBlockTargets)typeof(DocView).GetField("_codeTargets", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);

    private static bool HasLink(DocView view, float left, float right, string expected = null)
    {
        for (float y = 0f; y < view.TotalH; y += 3f)
            for (float x = left; x < right; x += 3f)
            {
                string found = view.LinkAt(x, y);
                if (found != null && (expected == null || found == expected)) return true;
            }
        return false;
    }

    // 自用走查(S72):模型 Aligns 1=中 2=右,DocView 曾原样交给 DirectWrite(1=TRAILING、2=CENTER),
    // 于是 :---: 列显示为右对齐、---: 列显示为居中。按像素核对:短数据与长表头在各自对齐方式下同中心/同右缘。
    private static void TableAlignment(Action<string, bool, string> check)
    {
        using var renderState = new RenderStateScope();
        RenderResources.Scale = 1f;
        using var frame = new OffscreenFrame(1000, 300);
        if (!frame.Ok) { check("WideTable/cell alignment offscreen target", false, "GDI"); return; }
        using var doc = new DocView();
        doc.Load("| 居中对齐的很长很长的表头 | 右对齐的很长很长的表头 |\n|:---:|---:|\n| x | y |\n");
        doc.OnWidth(1000f);
        frame.Paint(doc, 0f);
        // x < 60 是目录标尺(无标题时的入口短线也够深);列间内边距 8+8,按 >12 px 空隙分列。
        var bands = frame.InkBands(60);
        System.Collections.Generic.List<(int L, int R)> Clusters((int Top, int Bottom) band) => frame.InkClusters(band, 60, 12);
        // 前两条文字带:表头、数据行(工具栏不在屏外帧内)。
        var head = bands.Count >= 2 ? Clusters(bands[0]) : new();
        var data = bands.Count >= 2 ? Clusters(bands[1]) : new();
        bool shaped = head.Count == 2 && data.Count == 2;
        float Center((int L, int R) c) => (c.L + c.R) / 2f;
        check("WideTable/center column centers short cells", shaped && MathF.Abs(Center(head[0]) - Center(data[0])) <= 3f,
            shaped ? $"head={head[0]} data={data[0]}" : $"bands={bands.Count} head={head.Count} data={data.Count}");
        check("WideTable/right column right-aligns short cells", shaped && Math.Abs(head[1].R - data[1].R) <= 3,
            shaped ? $"head={head[1]} data={data[1]}" : $"bands={bands.Count}");
    }

    private static void TableLinks(Action<string, bool, string> check)
    {
        const string row = "| [Fixed](/fixed) | [COLUMN_TWO_LINK](/two) | [COLUMN_THREE_LINK](/three) | [COLUMN_FOUR_LINK](/four) | [COLUMN_FIVE_LINK](/five) | [COLUMN_SIX_LINK](/six) |\n|---|---|---|---|---|---|\n| A | B | C | D | E | F |\n";
        foreach (bool nested in new[] { false, true })
        {
            string md = nested ? "- item\n\n    " + row.TrimEnd('\n').Replace("\n", "\n    ") + "\n" : row;
            using var view = new DocView();
            view.Load(md);
            view.OnWidth(596f);
            // List marker inset (26) plus the child-table inset (6), as painted.
            float left = view.ColX + (nested ? 32f : 0f);
            float width = view.ColW - (nested ? 46f : 0f);
            string kind = nested ? "nested" : "root";
            check("WideLinks/" + kind + " visible frozen link is reachable", HasLink(view, left + 8f, left + 48f, "/fixed"), "real glyph hit, not only model metadata");
            check("WideLinks/" + kind + " clipped right margin is not clickable", !HasLink(view, left + width + 1f, left + width + 40f), "offscreen links must not be active in blank margins");
            int target = nested ? DocView.NestedTargetIndexForTest(0, 0) : 0;
            Targets(view).Add(new(0f, 0f, 200f, 100f, 0, 0, null, target));
            check("WideLinks/" + kind + " input moves table", view.ScrollCodeAt(50f, 50f, 60f), "exercise the same scroll state as painting");
            check("WideLinks/" + kind + " frozen link stays reachable", HasLink(view, left + 8f, left + 48f, "/fixed"), "first-column hit test does not inherit displacement");
            check("WideLinks/" + kind + " scrolled clipped margin is not clickable", !HasLink(view, left + width + 1f, left + width + 40f), "clip applies before and after scrolling");
            int last = view.SearchText().IndexOf("COLUMN_SIX_LINK", StringComparison.Ordinal);
            check("WideLinks/" + kind + " search reveals hidden link", last >= 0 && view.RevealOffsetX(last) && HasLink(view, left, left + width, "/six"), "search, painting coordinates and link hits agree");
        }
    }

    private static void NestedTable(Action<string, bool, string> check)
    {
        const string table = "- item\n\n    | ID | SECOND_COLUMN | THIRD_COLUMN | FOURTH_COLUMN | FIFTH_COLUMN | SIXTH_COLUMN | SEVENTH_COLUMN | LAST_COLUMN |\n    |---|---|---|---|---|---|---|---|\n    | A | alpha beta | alpha beta | alpha beta | alpha beta | alpha beta | alpha beta | NEEDLE_LAST |\n";
        using var view = new DocView();
        view.Load(table);
        view.OnWidth(596f);
        check("WideInput/nested table fixture is structured", view.Blocks[0] is ListItem item && item.Sub[0] is Table, "test the real child-table path");
        int nested = DocView.NestedTargetIndexForTest(0, 0);
        var targets = Targets(view);
        targets.Add(new(0f, 0f, 200f, 100f, 0, 0, null, nested));
        check("WideInput/nested table registers as scroll-only", targets.BlockAtPoint(50f, 50f) == nested && targets.AtPoint(50f, 50f) == null && targets.AtOffset(0) == null, "null text must not reject a valid encoded target");
        check("WideInput/nested table pointer moves", view.ScrollCodeAt(50f, 50f, 60f), "child rich layout owns its offset");
        check("WideInput/nested table keyboard moves back", view.ScrollCodeVisible(0f, 100f, -60f), "same state for both input paths");
        string text = view.SearchText();
        int offset = text.IndexOf("NEEDLE_LAST", StringComparison.Ordinal);
        check("WideInput/nested table search reaches hidden last column", offset >= 0 && view.RevealOffsetX(offset), "search shares the nested table state");
        check("WideInput/nested table search is stable", !view.RevealOffsetX(offset), "already visible target must not oscillate");
        view.SelectRange(0, view.TotalChars);
        check("WideInput/nested table retains all copied text", view.CopyText() == text && text.Contains("NEEDLE_LAST"), "no columns lost from the text model");
    }

    internal static void Run(Action<string, bool, string> check)
    {
        TableLinks(check);
        NestedTable(check);
        TableAlignment(check);
        int nested = DocView.NestedTargetIndexForTest(0, 0);
        var pick = new CodeBlockTargets();
        pick.Add(new(0f, 0f, 200f, 60f, 0, 1, "unindexed"));
        pick.Add(new(0f, 80f, 200f, 140f, 0, 1, "nested", nested));
        pick.Add(new(0f, 200f, 200f, 260f, 0, 1, "top", 1));
        check("WideInput/keyboard includes encoded child", pick.BlockNearestCenter(60f, 160f) == nested, "-1 alone is the absent target");
        check("WideInput/keyboard picks nearest across both kinds", pick.BlockNearestCenter(50f, 170f) == nested, "do not silently scroll a different top-level block");
        check("WideInput/unindexed target remains excluded", pick.BlockNearestCenter(0f, 60f) == -1, "legacy copy-only box is not scrollable");
        check("WideInput/offscreen child is not selected", pick.BlockNearestCenter(180f, 280f) == 1, "visible top-level block still works");

        string md = "- item\n\n    ```\n    LEFT " + new string('w', 400) + " RIGHT\n    ```\n";
        using var view = new DocView();
        view.Load(md);
        view.OnWidth(596f);
        var targets = Targets(view);
        targets.Add(new(0f, 0f, 200f, 100f, 0, 1, "nested", nested));
        string original = view.SearchText();
        float height = view.TotalH;
        check("WideInput/pointer reaches nested state", view.ScrollCodeAt(50f, 50f, 60f), "public pointer entry must accept encoded target");
        check("WideInput/keyboard reaches nested state", view.ScrollCodeVisible(0f, 100f, 60f), "public keyboard entry must accept encoded target");
        check("WideInput/child scrolls back", view.ScrollCodeAt(50f, 50f, -120f), "same child owns both inputs");
        check("WideInput/left boundary is not consumed", !view.ScrollCodeAt(50f, 50f, -60f), "ordinary scrolling can resume at the boundary");
        check("WideInput/child clamps to far edge", view.ScrollCodeAt(50f, 50f, 100000f), "bounded independent child displacement");
        check("WideInput/right boundary is not consumed", !view.ScrollCodeAt(50f, 50f, 60f), "no phantom movement");
        check("WideInput/child returns from far edge", view.ScrollCodeVisible(0f, 100f, -100000f), "keyboard and pointer share one offset");
        check("WideInput/missing target is harmless", !view.ScrollCodeAt(500f, 500f, 60f), "no implicit target outside the boxes");
        view.SelectRange(0, view.TotalChars);
        check("WideInput/scroll preserves search and selection text", view.SearchText() == original && view.CopyText() == original, "render-only movement");
        check("WideInput/scroll does not relayout", view.TotalH == height, "height remains exact and stable");

        using var shortView = new DocView();
        shortView.Load("- item\n\n    ```\n    short\n    ```\n");
        shortView.OnWidth(596f);
        Targets(shortView).Add(new(0f, 0f, 200f, 100f, 0, 1, "short", nested));
        check("WideInput/nonoverflow child keeps normal gestures", !shortView.ScrollCodeAt(50f, 50f, 60f) && !shortView.ScrollCodeVisible(0f, 100f, 60f), "no scroll without overflow");

        using var paper = new DocView { WrapCodeForPaper = true };
        paper.Load(md);
        paper.OnWidth(596f);
        Targets(paper).Add(new(0f, 0f, 200f, 100f, 0, 1, "paper", nested));
        check("WideInput/paper child cannot scroll", !paper.ScrollCodeAt(50f, 50f, 60f) && !paper.ScrollCodeVisible(0f, 100f, 60f), "printing wraps instead of hiding off-page text");
    }
}
