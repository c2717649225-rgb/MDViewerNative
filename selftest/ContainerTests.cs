using System;
using System.Linq;
using MDViewerNative;

internal static class ContainerTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        string Html(string md)
        {
            string h = HtmlExport.Build(MdParser.Parse(md), "test", false);
            int start = h.IndexOf("<body>\n", StringComparison.Ordinal) + 7;
            return h[start..h.IndexOf("</body>", StringComparison.Ordinal)].Replace("\n", "");
        }
        string Text(ListItem item) => string.Concat(item.Inlines.Select(r => r.Text));
        foreach (int depth in new[] { 1, 2, 3 })
        {
            string prefix = new string('>', depth) + " ";
            var list = MdParser.Parse(prefix + "- item\n");
            check("Container/quoted list depth " + depth, list.Count == 1 && list[0] is ListItem li && li.QuoteDepth == depth && li.XOff == depth * 20, "explicit depth independent of list indentation");
            check("Container/export quoted list " + depth, Html(prefix + "- item\n") == string.Concat(Enumerable.Repeat("<blockquote>", depth)) + "<ul><li>item</li></ul>" + string.Concat(Enumerable.Repeat("</blockquote>", depth)), "exact tags, no structural attribute stripping");
        }
        string plain = "- parent\n  - child\n";
        check("Container/ordinary list indentation is not a quote", MdParser.Parse(plain).All(b => b.QuoteDepth == 0) && !Html(plain).Contains("blockquote"), "negative control");
        check("Container/adjacent quoted and plain lists stay separate", Html("> - quoted\n\n- plain\n") == "<blockquote><ul><li>quoted</li></ul></blockquote><ul><li>plain</li></ul>", "no cross-container merge");
        check("Container/quote runs stay separate", Html("> a\n\n> b\n") == "<blockquote><p>a</p></blockquote><blockquote><p>b</p></blockquote>", "QSeq boundary retained");
        // 自用走查:同一引用内多块(段落/嵌套)的竖线曾在块间断开,像几段互不相关的引用;同一运行内按层贯穿,空行分断的相邻引用仍分开
        var nest = MdParser.Parse("> 外层\n>\n> > 内层\n>\n> 回到外层\n");
        check("Container/quote rail joins within run", nest.Count == 3 && DocView.QuoteRailJoins(nest[0], nest[1], 1) && DocView.QuoteRailJoins(nest[1], nest[2], 1), string.Join(",", nest.Select(b => b.QuoteDepth + ":" + b.QSeq)));
        check("Container/inner rail ends with inner quote", nest.Count == 3 && !DocView.QuoteRailJoins(nest[1], nest[2], 2), "level 2 stops at return to outer");
        var paras = MdParser.Parse("> a\n>\n> b\n");
        check("Container/quote rail joins paragraphs", paras.Count == 2 && DocView.QuoteRailJoins(paras[0], paras[1], 1), "two paragraphs, one quote");
        var runs = MdParser.Parse("> a\n\n> b\n");
        check("Container/separate quotes keep separate rails", runs.Count == 2 && !DocView.QuoteRailJoins(runs[0], runs[1], 1), "QSeq boundary");
        check("Container/ordered parent unordered child", Html("10) foo\n    - bar\n") == "<ol start=\"10\"><li value=\"10\">foo<ul><li>bar</li></ul></li></ol>", "CommonMark 298; valid numeric attributes");
        check("Container/mixed child sibling kinds", Html("- parent\n  - a\n  1. b\n  - c\n- tail\n") == "<ul><li>parent<ul><li>a</li></ul><ol start=\"1\"><li value=\"1\">b</li></ol><ul><li>c</li></ul></li><li>tail</li></ul>", "children remain inside their parent");
        var closed = MdParser.Parse("1. a\n\n  2. b\n\n    3. c\n");
        check("Container/code after closed list", closed.Count == 3 && closed[2] is CodeBlock cb && cb.Text.TrimEnd('\n') == "3. c" && Text((ListItem)closed[1]) == "b", "CommonMark 315; code is not appended to prior item");
        var lazy = MdParser.Parse("  1.  A paragraph\n    with two lines.\n");
        check("Container/lazy continuation remains in open item", lazy.Count == 1 && lazy[0] is ListItem li0 && Text(li0) == "A paragraph with two lines.", "CommonMark 293 regression guard");
        var children = MdParser.Parse("  1.  A paragraph\nwith two lines.\n\n          indented code\n\n      > A block quote.\n");
        check("Container/lazy paragraph then child blocks", children.Count == 1 && children[0] is ListItem li1 && li1.Sub is { Count: 2 } && li1.Sub[0] is CodeBlock && li1.Sub[1] is Quote, "CommonMark 292; preserve code and quote ownership");
        // 自用走查(真实中文 README):引用后紧跟 <p align> 时 HTML 块起始(1~6 类)能打断段落,不是懒续行;
        // 此前 <p> 被并入引用,缩进的 <img> 变成代码块,</p> 另成一块。第 7 类孤立标签不能打断段落,仍懒续。
        var htmlAfterQuote = MdParser.Parse("> 引用\n<p align=\"center\">\n    <img src='x.jpg'>\n</p>\n");
        check("Container/html block start ends lazy quote", htmlAfterQuote.Count == 2 && htmlAfterQuote[0] is Quote && htmlAfterQuote[1] is HtmlBlock { QuoteDepth: 0 } hq && hq.Text.Contains("<img") && hq.Text.Contains("</p>"),
            string.Join(",", htmlAfterQuote.Select(b => b.GetType().Name + b.QuoteDepth)));
        var tagAfterQuote = MdParser.Parse("> 引用\n<span>\n");
        check("Container/type 7 tag stays lazy in quote", tagAfterQuote.Count == 1 && tagAfterQuote[0] is Quote, string.Join(",", tagAfterQuote.Select(b => b.GetType().Name)));
        string fenced = "> ```\n> > literal\n> > ```\n> tail\n> ```\n";
        var code = MdParser.Parse(fenced);
        check("Container/quoted fence preserves greater-than text", code.Count == 1 && code[0] is CodeBlock c && c.QuoteDepth == 1 && c.Text == "> literal\n> ```\ntail\n", "prefix stripped exactly once; false closing fence stays code");
        check("Container/export literal greater-than", Html(fenced).Contains("&gt; literal") && Html(fenced).Contains("&gt; ```"), "escaped, not deleted");
        foreach (string md in new[] { fenced, "1. a\n\n  2. b\n\n    3. c\n", "> > - item\n\n- plain\n" })
        {
            using var view = new DocView();
            view.Load(md);
            view.OnWidth(596f);
            string search = view.SearchText();
            view.SelectRange(0, view.TotalChars);
            check("Container/native selection and search agree", view.CopyText() == search && search.Length > 0 && view.TotalH > 0, "real DirectWrite layout; no clipboard writes");
            view.OnWidth(420f);
            view.SelectRange(0, view.TotalChars);
            check("Container/resize preserves text", view.CopyText() == search && view.SearchText() == search, "narrow layout does not drop content");
        }
        PaperCodeWrap(check);
        WideTablePaperVsScreen(check);
        NestedCodeHScroll(check);
        ReaderWideContentTests.Run(check);
        NestedSearchReveal(check);
        KeyboardTargetPick(check);
        SearchRevealsSideways(check);
        CodeBlockLinkHits(check);
    }

    // Sideways scrolling shifts the glyphs, so every hit test against a code block has to
    // add the offset back. PosAt does; check whether link hit testing needs it too, i.e.
    // whether a fenced code block can register clickable links at all (30).
    private static void CodeBlockLinkHits(Action<string, bool, string> check)
    {
        using var view = new DocView();
        view.Load("```\nsee https://example.com/inside-code for details\n```\n");
        view.OnWidth(596f);
        // Probe across the whole first line of the block.
        string found = null;
        for (float x = 20f; x < 560f && found == null; x += 8f)
            for (float y = 38f; y < 120f && found == null; y += 6f)
                found = view.LinkAt(x, y);
        check("CodeLinks/fenced code exposes no clickable link", found == null, $"got {found ?? "null"}");
    }

    // A search hit inside a wide code block can sit past the right edge. Without a
    // horizontal reveal the viewer scrolls vertically, highlights the hit, and leaves it
    // off screen — the match looks missing even though it was found (30).
    private static void SearchRevealsSideways(Action<string, bool, string> check)
    {
        // The needle sits at the far end of a long single line of code.
        string needle = "ZZTARGETZZ";
        string line = new string('x', 400) + needle + new string('y', 40);
        using var view = new DocView();
        view.Load("```\n" + line + "\n```\n");
        view.OnWidth(596f);

        int at = view.SearchText().IndexOf(needle, StringComparison.Ordinal);
        check("Reveal/needle exists in search text", at >= 0, $"index {at}");
        if (at < 0) return;

        // Starts unscrolled, so the far-right hit is not visible yet.
        bool moved = view.RevealOffsetX(at);
        check("Reveal/scrolls the block to show a far hit", moved, "expected a sideways adjustment");

        // Idempotent: revealing the same hit twice must not keep drifting.
        bool movedAgain = view.RevealOffsetX(at);
        check("Reveal/already visible hit does not move again", !movedAgain, "second reveal should be a no-op");

        // A hit in a block that fits must never scroll anything.
        using var narrow = new DocView();
        narrow.Load("```\nshort " + needle + "\n```\n");
        narrow.OnWidth(596f);
        int at2 = narrow.SearchText().IndexOf(needle, StringComparison.Ordinal);
        check("Reveal/no scrolling when the block fits", at2 >= 0 && !narrow.RevealOffsetX(at2), "non-overflowing block stays put");

        // Offsets outside any code block are ignored rather than throwing.
        using var prose = new DocView();
        prose.Load("just a paragraph with " + needle + " inside\n");
        prose.OnWidth(596f);
        int at3 = prose.SearchText().IndexOf(needle, StringComparison.Ordinal);
        check("Reveal/prose offsets are ignored", at3 >= 0 && !prose.RevealOffsetX(at3), "only code blocks scroll sideways");
    }

    // Keyboard scrolling has no pointer to aim with, so it targets the code block nearest
    // the middle of the visible range. Verify the geometry directly: off-screen blocks must
    // be ignored, and the nearest on-screen one chosen (30).
    private static void KeyboardTargetPick(Action<string, bool, string> check)
    {
        var targets = new CodeBlockTargets();
        targets.Add(new(0f, 0f, 100f, 50f, 0, 5, "first", 1));
        targets.Add(new(0f, 400f, 100f, 460f, 10, 5, "second", 4));
        targets.Add(new(0f, 900f, 100f, 960f, 20, 5, "third", 9));

        // Viewport 350..550 sees only the middle block.
        check("Keys/picks the visible code block", targets.BlockNearestCenter(350f, 550f) == 4,
            $"got {targets.BlockNearestCenter(350f, 550f)}");

        // Two blocks visible: the one whose middle sits closer to the viewport centre wins.
        // Viewport 0..200 has centre 100; block 1 sits at 25 (distance 75) and block 4 at
        // 430, off screen entirely, so the near one must win.
        check("Keys/picks nearest to centre", targets.BlockNearestCenter(0f, 200f) == 1,
            $"got {targets.BlockNearestCenter(0f, 200f)}");

        // And with the viewport low down, the lower block wins even though the upper one
        // is still partly visible — proves the choice is by distance, not document order.
        check("Keys/lower block wins when nearer", targets.BlockNearestCenter(300f, 500f) == 4,
            $"got {targets.BlockNearestCenter(300f, 500f)}");

        // Nothing on screen: the key must keep its ordinary meaning instead of being eaten.
        check("Keys/no code on screen yields none", targets.BlockNearestCenter(600f, 800f) == -1,
            $"got {targets.BlockNearestCenter(600f, 800f)}");

        // Blocks registered without an index (nested in quotes/lists) never win.
        var nested = new CodeBlockTargets();
        nested.Add(new(0f, 0f, 100f, 50f, 0, 5, "nested"));
        check("Keys/unindexed nested block is skipped", nested.BlockNearestCenter(0f, 100f) == -1,
            $"got {nested.BlockNearestCenter(0f, 100f)}");
    }

    // Screen keeps wide code on one row and scrolls the block sideways (30). Paper cannot
    // scroll, so the print path must wrap instead — otherwise every character past the page
    // edge is silently missing from the printout, which no other test would notice.
    // 宽表格横向滚动(30):列宽从"压缩硬塞"改为"保留自然宽度 + 左右滚 + 冻结首列"。
    // 屏幕上表格变矮(不再被挤成一词一行),纸上必须仍然压缩,否则列会掉到纸外。
    private static void WideTablePaperVsScreen(Action<string, bool, string> check)
    {
        string wide = "| 项目名称 | 负责人 | 开始日期 | 结束日期 | 当前状态 | 优先级 | 预算金额 | 备注说明 |\n|---|---|---|---|---|---|---|---|\n| 子系统重构第一期 | 张三丰 | 2026-01-01 | 2026-06-30 | 进行中 | 高 | 1250000 元 | 依赖上游接口冻结 |\n";
        string narrow = "| A | B |\n|---|---|\n| 1 | 2 |\n";

        using var screen = new DocView();
        screen.Load(wide);
        screen.OnWidth(596f);
        screen.Relayout();
        float screenH = screen.TotalH;

        using var paper = new DocView();
        paper.WrapCodeForPaper = true;
        paper.Load(wide);
        paper.OnWidth(596f);
        paper.Relayout();
        float paperH = paper.TotalH;

        // 纸上压缩 => 单元格换行 => 比屏幕高。这是"打印没被横向滚动坑到"的直接证据。
        check("WideTablePaper/纸上仍压缩因而更高", paperH > screenH + 1f, $"paper={paperH} screen={screenH}");

        // 窄表格两条路径必须完全一致 —— 不溢出的表格不该被这次改动碰到。
        using var narrowScreen = new DocView();
        narrowScreen.Load(narrow);
        narrowScreen.OnWidth(596f);
        narrowScreen.Relayout();
        using var narrowPaper = new DocView();
        narrowPaper.WrapCodeForPaper = true;
        narrowPaper.Load(narrow);
        narrowPaper.OnWidth(596f);
        narrowPaper.Relayout();
        check("WideTablePaper/窄表格屏纸一致",
            MathF.Abs(narrowScreen.TotalH - narrowPaper.TotalH) < 0.5f,
            $"screen={narrowScreen.TotalH} paper={narrowPaper.TotalH}");

        // 表格不是代码块:右键"复制代码块"不能把表格当代码交出去。
        string copied = null;
        for (float y = 20f; y < screenH && copied == null; y += 10f)
            copied = screen.CodeAtPoint(200f, y);
        check("WideTablePaper/表格不冒充代码块", copied == null, $"got {copied ?? "null"}");
    }

    // 列表项内嵌代码块的横向滚动(30)。顶层代码块早已不折行,项内子块此前仍在折行 ——
    // 同一份代码在列表里外表现不一致。子块没有自己的 Ent(只是 SubLayouts 里一个扁平
    // 布局),滚动状态挂在父 Ent 的 SubOverflowW/SubScrollX 上。
    private static void NestedCodeHScroll(Action<string, bool, string> check)
    {
        string nested = "- 列表项：\n\n    ```\n    zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\n    ```\n";
        string narrow = "- 列表项：\n\n    ```\n    ok\n    ```\n";

        using var screen = new DocView();
        screen.Load(nested);
        screen.OnWidth(596f);
        screen.Relayout();
        float screenH = screen.TotalH;

        using var paper = new DocView();
        paper.WrapCodeForPaper = true;
        paper.Load(nested);
        paper.OnWidth(596f);
        paper.Relayout();
        float paperH = paper.TotalH;

        // 屏幕不折行 => 一源行一屏行 => 比纸上折行矮。这是"项内代码块确实改成滚动"的证据。
        check("NestedCode/屏幕不折行因而更矮", screenH < paperH - 1f, $"screen={screenH} paper={paperH}");

        // 短代码两条路径必须一致 —— 不溢出的子块不该被这次改动碰到。
        using var narrowScreen = new DocView();
        narrowScreen.Load(narrow);
        narrowScreen.OnWidth(596f);
        narrowScreen.Relayout();
        using var narrowPaper = new DocView();
        narrowPaper.WrapCodeForPaper = true;
        narrowPaper.Load(narrow);
        narrowPaper.OnWidth(596f);
        narrowPaper.Relayout();
        check("NestedCode/短代码屏纸一致",
            MathF.Abs(narrowScreen.TotalH - narrowPaper.TotalH) < 0.5f,
            $"screen={narrowScreen.TotalH} paper={narrowPaper.TotalH}");

        // 滚动目标表只在 Paint 时填充,无窗口的自测走不到;因此这里直接验打包索引本身。
        // 项内子块没有自己的块号,(父块, 子序号) 被打进一个负整数里穿过同一张目标表。
        // 打包算错会静默变成"滚不动",而且编译和其它用例都发现不了。
        var targets = new CodeBlockTargets();
        int packedA = DocView.NestedTargetIndexForTest(3, 0);
        int packedB = DocView.NestedTargetIndexForTest(3, 1);
        int packedC = DocView.NestedTargetIndexForTest(4, 0);
        check("NestedCode/打包索引互不相同", packedA != packedB && packedB != packedC && packedA != packedC,
            $"a={packedA} b={packedB} c={packedC}");
        // -1 是"没有目标"的保留值,打包结果绝不能撞上它,否则会被当成没命中。
        check("NestedCode/打包索引不撞 -1 保留值", packedA != -1 && packedB != -1 && packedC != -1,
            $"a={packedA} b={packedB} c={packedC}");
        // 顶层块用非负块号,项内块用负数,两者必须能区分开。
        check("NestedCode/项内索引与顶层块号可区分", packedA < 0 && packedB < 0 && packedC < 0,
            $"a={packedA} b={packedB} c={packedC}");
        // 解包必须原样还原出父块与子序号。
        check("NestedCode/打包可逆",
            DocView.NestedUnpackForTest(packedB) == (3, 1) && DocView.NestedUnpackForTest(packedC) == (4, 0),
            $"b->{DocView.NestedUnpackForTest(packedB)} c->{DocView.NestedUnpackForTest(packedC)}");

        // 目标表能原样带着这个负索引往返,不会被 Add 的有效性校验挡掉。
        targets.Add(new(0f, 0f, 100f, 50f, 0, 5, "nested", packedA));
        check("NestedCode/负索引可穿过目标表", targets.BlockAtPoint(50f, 25f) == packedA,
            $"got {targets.BlockAtPoint(50f, 25f)}");
    }

    // 巡查「孪生臂」时补上的缺口:PosAt 与 FillSelForBlock 都已认得 SubScrollX,
    // 只有查找定位没认得 —— 命中落在项内代码块右侧视野外时会纵向滚过去、横向却不动,
    // 表现为"搜到了却看不见"。这条守住修复。
    private static void NestedSearchReveal(Action<string, bool, string> check)
    {
        using var view = new DocView();
        view.Load("- 列表项：\n\n    ```\n    qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq NEEDLEWORD tail\n    ```\n");
        view.OnWidth(596f);
        view.Relayout();

        int at = view.SearchText().IndexOf("NEEDLEWORD", StringComparison.Ordinal);
        check("NestedReveal/命中存在于可搜文本", at >= 0, $"at={at}");
        if (at < 0) return;

        // 第一次调用必须真的横向滚动过去。
        bool moved = view.RevealOffsetX(at);
        check("NestedReveal/项内代码块命中会横向定位", moved, "hit past right edge must scroll");

        // 幂等:已经可见了就不该再动,否则每按一次查找下一个画面都抖一下。
        bool again = view.RevealOffsetX(at);
        check("NestedReveal/重复定位不再滚动", !again, "already visible");
    }

    private static void PaperCodeWrap(Action<string, bool, string> check)
    {
        string wide = "```\n" + new string('x', 400) + "\n```\n";

        using var screen = new DocView();
        screen.Load(wide);
        screen.OnWidth(596f);
        float screenH = screen.TotalH;
        string screenText = screen.SearchText();

        using var paper = new DocView();
        paper.WrapCodeForPaper = true;
        paper.Load(wide);
        paper.OnWidth(596f);
        paper.Relayout();
        float paperH = paper.TotalH;

        // Wrapping the same 400-character line has to produce a visibly taller block.
        check("Paper/wide code wraps when printing", paperH > screenH + 1f, $"screen={screenH} paper={paperH}");

        // The text itself must be identical: wrapping is a rendering choice, not an edit.
        check("Paper/wrapping does not alter text", paper.SearchText() == screenText, "same characters on paper and screen");

        // Screen mode must stay unwrapped, otherwise the sideways scrolling has nothing to do.
        using var screen2 = new DocView();
        screen2.Load(wide);
        screen2.OnWidth(596f);
        check("Paper/screen stays unwrapped", MathF.Abs(screen2.TotalH - screenH) < 0.5f && screenH < paperH, $"screen={screenH}");
    }
}
