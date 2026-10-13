using System;
using System.Linq;
using System.Collections.Generic;
using MDViewerNative;

internal static class MathDiagramTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "bounded math and diagram subset") => check("MathDiagram/" + name, ok, detail);
        MAtom tree = MathRender.Parse(@"\frac{1}{2}+3");
        try
        {
            var row = tree as MRow;
            var fraction = row?.Items.Count > 0 ? row.Items[0] as MFrac : null;
            C("fraction consumes exactly two arguments", fraction != null && Plain(fraction.Num) == "1" && Plain(fraction.Den) == "2", Plain(tree));
            // S77 续:分式后的 + 为二元运算符,两侧中等间距
            C("fraction keeps trailing expression outside", row?.Items.Count == 2 && Plain(row.Items[1]) == "\u205F+\u205F3", Plain(tree));
        }
        finally { MathRender.Free(tree); }
        C("indexed cube root has readable inline output", MathUnicode.Convert(@"\sqrt[3]{x}") == "∛(x)");
        C("fraction aliases have consistent inline output", MathUnicode.Convert(@"\dfrac{a}{b}") == "(a)/(b)");
        tree = MathRender.Parse(@"\begin{matrix}a&b\end{matrix}");
        try { C("unsupported environment requests source fallback", tree == null); }
        finally { MathRender.Free(tree); }
        C("chained edges support definitions at both ends", MermaidLite.TryParse("flowchart LR\nA[Start] --> B{Ready?} -->|yes| C(End)", out var chain)
            && chain.Nodes.Count == 3 && chain.Edges.Count == 2 && chain.Nodes[2].Text == "End");
        C("right to left direction lays source to the right", MermaidLite.TryParse("flowchart RL\nA --> B", out var rl) && rl.Nodes[0].X > rl.Nodes[1].X);
        C("bottom to top direction lays source below", MermaidLite.TryParse("flowchart BT\nA --> B", out var bt) && bt.Nodes[0].Y > bt.Nodes[1].Y);
        C("mismatched shape delimiter is rejected", !MermaidLite.TryParse("graph TD\nA[wrong)", out _));
        C("unsupported subgraph is not silently flattened", !MermaidLite.TryParse("graph TD\nsubgraph Group\nA --> B\nend", out _));
        C("oversized diagram is rejected before parsing", !MermaidLite.TryParse("graph TD\n%%" + new string('x', 70000) + "\nA --> B", out _));

        foreach (var sample in new[]
        {
            (Tex: @"\frac12+3", Text: "(1)/(2)+3"),
            (Tex: @"\frac {\alpha+1} {\sqrt{x}}", Text: "(α+1)/(√(x))"),
            (Tex: @"a_i^2+b^{3}c", Text: "aᵢ²+b³c"),
            (Tex: @"a^2_i", Text: "a²ᵢ"),
            (Tex: @"\sqrt[4]{x}", Text: "∜(x)"),
            (Tex: @"\sqrt[n+1]{x}", Text: "root(n+1; x)"),
            (Tex: @"\text{speed = }v", Text: "speed = v"),
            (Tex: @"\text{a\{b\}}", Text: "a{b}"),
            (Tex: @"\sin{x}+\cos{y}", Text: "sinx+cosy"),
            // 无法全部映射为 Unicode 上下标时用线性写法(S74;原为 TeX 原文 x^{α}):单字符不加括号,多字符加括号。
            (Tex: @"x^{\alpha}", Text: "x^α"),
            (Tex: @"x_\alpha", Text: "x_α"),
            (Tex: @"x_{\alpha\beta}", Text: "x_(αβ)"),
            (Tex: "😀^2", Text: "😀²"),
            (Tex: @"e^{i\pi} + 1 = 0", Text: "e^(iπ) + 1 = 0"),
            // 常用命令(S75,自用走查 d2l:\mathbf 等曾整段回退源码):字体命令映射到 Unicode 数学字母,重音转组合附加符号,
            // \left/\right 等尺寸命令忽略,\top 上标为 ᵀ。
            (Tex: @"\mathbf{x}", Text: "\U0001D431"),
            (Tex: @"\mathbf{x}^{(i)}", Text: "\U0001D431⁽ⁱ⁾"),
            (Tex: @"\mathbf{w}^\top \mathbf{x}", Text: "\U0001D430ᵀ \U0001D431"),
            (Tex: @"\hat{y}", Text: "y\u0302"),
            (Tex: @"\hat{\mathbf{y}} = \mathbf{X} \mathbf{w} + b", Text: "\U0001D432\u0302 = \U0001D417 \U0001D430 + b"),
            (Tex: @"\mathbb{R}^{d}", Text: "ℝᵈ"),
            (Tex: @"\mathcal{L}(\mathbf{w}, b)", Text: "ℒ(\U0001D430, b)"),
            (Tex: @"w_{\mathrm{area}}", Text: "wₐᵣₑₐ"),
            (Tex: @"\operatorname*{argmin}_w", Text: "argmin_w"),
            (Tex: @"\left(\frac{a}{b}\right)", Text: "((a)/(b))"),
            (Tex: @"\left.x\right|", Text: "x|"),
            (Tex: @"\|\mathbf{x}\|^2", Text: "‖\U0001D431‖²"),
            (Tex: @"y \mid x \le 1 \ne 2", Text: "y ∣ x ≤ 1 ≠ 2"),
            (Tex: @"\boldsymbol{\theta}", Text: "\U0001D73D"),
            (Tex: @"\bar{x}\,\vec{v}", Text: "x\u0304\u2009v\u20D7"),
            (Tex: @"\displaystyle\sum_i x_i", Text: "∑ᵢ xᵢ"),
            (Tex: @"\text{}", Text: "")
        }) C("shared inline grammar " + sample.Tex, MathUnicode.Convert(sample.Tex) == sample.Text, MathUnicode.Convert(sample.Tex));
        foreach (string invalid in new[] { @"\frac{1}", @"\sqrt[3{x}", @"x^^2", @"x_1_2", @"{x", @"x}", @"\unknown{x}+\alpha", "\\", new string('{', 100), new string('x', MathSyntax.MaxAtoms + 1), new string('x', MathSyntax.MaxLength + 1) })
        {
            C("invalid math retains entire source " + invalid[..Math.Min(20, invalid.Length)],
                !MathSyntax.TryParse(invalid, out _) && MathUnicode.Convert(invalid) == invalid);
        }
        C("managed grammar allocates no native layouts", MathSyntax.TryParse(@"\frac{1}{2}+x_i^2", out var syntax)
            && Atoms(syntax).All(a => a.Layout == IntPtr.Zero));
        tree = MathRender.Parse(@"a_i^2+\frac{1}{\sqrt{x}}");
        try
        {
            var script = (tree as MRow)?.Items.FirstOrDefault() as MScript;
            C("dual scripts share one base", script != null && Plain(script.BaseAtom) == "a" && Plain(script.Sub) == "i" && Plain(script.Super) == "2");
            C("native scripts really use a smaller font", script != null && Atoms(script.Super).OfType<MText>().All(t => t.Size < ((MText)script.BaseAtom).Size));
            // 字体行盒远高于字形,盒不重叠只是墨迹不撞的替身;S77 起直接约束基线:上抬、下降,且相距容得下下标 x 高度加间隙
            C("dual scripts keep their baselines apart", script != null && script.SuperBaseline < script.Base && script.SubBaseline > script.Base
                && script.SubBaseline - script.SuperBaseline > 0.5f * RenderResources.BodySize);
            C("rows contain aligned ascent and descent", tree is MRow measured && measured.Items.All(a => measured.Base + 0.01f >= a.Base && measured.H - measured.Base + 0.01f >= a.H - a.Base));
            C("native math geometry is finite", tree != null && Atoms(tree).All(a => float.IsFinite(a.W) && float.IsFinite(a.H) && a.W >= 0 && a.H >= 0));
            // TeX 数学间距(S77 续):源码空白不显示;二元运算符两侧中等间距、关系符两侧粗间距、逗号后细间距;一元负号不加;\text 保留空格
            foreach (var (tex, want) in new[] { (@"w_1  x_1 + b", "MScriptMScript\u205F+\u205Fb"), (@"-x = y", "-x\u2005=\u2005y"), (@"a, b", "a,\u2009b"), (@"\text{a b}", "a b") })
            {
                var spaced = MathRender.Parse(tex);
                C("math spacing follows TeX atom classes " + tex, spaced != null && Plain(spaced) == want);
                MathRender.Free(spaced);
            }
            MathRender.Free(tree);
            C("free releases every native math layout", Atoms(tree).All(a => a.Layout == IntPtr.Zero));
        }
        finally { MathRender.Free(tree); }
        tree = MathRender.Parse(@"\sqrt[\frac{\frac{1}{2}}{\frac{3}{4}}]{x}");
        try
        {
            var root = (tree as MRow)?.Items.FirstOrDefault() as MSqrt;
            C("complex root index remains inside its box", root != null && root.W >= root.Index.W && root.H >= root.Index.H);
        }
        finally { MathRender.Free(tree); }
        foreach (string direction in new[] { "TD", "TB", "BT", "LR", "RL" })
        {
            bool parsed = MermaidLite.TryParse("flowchart " + direction + "; A[Start] --> B[End]", out var graph);
            C("diagram direction " + direction, parsed && graph.Edges.Count == 1);
            if (!parsed) continue;
            var edge = MermaidLite.EdgePoints(graph, graph.Edges[0]);
            var from = graph.Nodes[0]; var to = graph.Nodes[1];
            C("edge attaches to direction-facing boundaries " + direction, graph.LR
                ? edge.ax == from.X + (graph.Reverse ? 0 : from.W) && edge.bx == to.X + (graph.Reverse ? to.W : 0)
                : edge.ay == from.Y + (graph.Reverse ? 0 : from.H) && edge.by == to.Y + (graph.Reverse ? to.H : 0));
            var extent = MermaidLite.Extent(graph);
            C("diagram nodes stay within extent " + direction, graph.Nodes.All(n => n.X >= 30 && n.Y >= 30 && n.X + n.W <= extent.w - 30 && n.Y + n.H <= extent.h - 30));
        }
        C("quoted labels preserve semicolons brackets and comments", MermaidLite.TryParse("graph LR; A[\"a;b] %% c\"] --> B; %% comment\nB --> C", out var quoted)
            && quoted.Nodes[0].Text == "a;b] %% c" && quoted.Edges.Count == 2);
        C("edge styles retain their semantic flags", MermaidLite.TryParse("graph TD; A --- B -.-> C ==> D === E", out var styles)
            && !styles.Edges[0].Arrow && styles.Edges[1].Dashed && styles.Edges[1].Arrow && styles.Edges[2].Thick && styles.Edges[2].Arrow && styles.Edges[3].Thick && !styles.Edges[3].Arrow);
        foreach (string invalid in new[] { "graph TD garbage\nA --> B", "sequenceDiagram\nA -> B", "graph TD\nA --> B\nclick A https://example.invalid", "graph TD\nA[unclosed", "graph TD\nA((nested))", "graph TD\nA[\"bad]", "graph TD\nA[" + new string('x', MermaidLite.MaxLabel + 1) + "]", "graph TD\n" + new string('a', 129) })
            C("unsupported diagram returns no partial graph " + invalid[..Math.Min(25, invalid.Length)], !MermaidLite.TryParse(invalid, out var partial) && partial == null);
        string nodes = "graph TD\n" + string.Join("\n", Enumerable.Range(0, MermaidLite.MaxNodes).Select(i => "N" + i));
        C("node budget boundary accepted", MermaidLite.TryParse(nodes, out var bounded) && bounded.Nodes.Count == MermaidLite.MaxNodes);
        C("one node beyond budget rejected", !MermaidLite.TryParse(nodes + "\nExtra", out _));
        string edges = "graph TD\n" + string.Concat(Enumerable.Repeat("A --> B\n", MermaidLite.MaxEdges));
        C("edge budget boundary accepted", MermaidLite.TryParse(edges, out bounded) && bounded.Edges.Count == MermaidLite.MaxEdges);
        C("one edge beyond budget rejected", !MermaidLite.TryParse(edges + "A --> B\n", out _));
        string source = "```math\n\\frac{1}{2}+3\n```\n\n```mermaid\nflowchart RL; A[Start] --> B[End]\n```\n\n```math\n\\begin{matrix}a&b\\end{matrix}\n```\n";
        using var view = new DocView();
        view.Load(source); view.OnWidth(700); view.SelAll();
        string copied = view.CopyText();
        C("native render keeps math and diagram source copyable", copied.Contains(@"\frac{1}{2}+3") && copied.Contains("flowchart RL; A[Start] --> B[End]") && copied.Contains(@"\begin{matrix}a&b\end{matrix}"));
        string exported = HtmlExport.Build(MdParser.Parse(source), "math", false);
        C("HTML shares math grammar and keeps unsupported source offline", exported.Contains("(1)/(2)+3") && exported.Contains("flowchart RL;") && exported.Contains(@"\begin{matrix}a&amp;b\end{matrix}") && !exported.Contains("<script"));
        string escaped = HtmlExport.Build(MdParser.Parse("```math\n\\text{<script>alert(1)</script>}\n```\n"), "math", false);
        C("literal math text cannot inject HTML", escaped.Contains("&lt;script&gt;") && !escaped.Contains("<script>"));
        AccentPlacement(check);
        InlineNativeMath(check);
        InlineContext(check);
        MermaidLayout(check);
    }

    // 重音居中(S76,S75 遗留):数学粗体字母来自回退字体,组合重音若由另一字体绘制,帽子会偏到字母右上方。
    // 离屏 2x 渲染,帽子墨迹须整个落在字母(x 高度内部分)的左右范围之内。改前帽子悬在字母右缘之外(行内 380..387 对 359..381)。
    // 不要求严格居中:Cambria Math 自身锚点对粗体 y 就偏右约 2 DIP(块公式同),属字体设计。
    private static void AccentPlacement(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            RenderResources.Scale = 2f; RenderResources.SetBodySize(16f); RenderResources.ApplyTheme(false);
            foreach (var (name, md) in new[] { ("inline bold", "$\\hat{\\mathbf{y}}$\n"), ("inline plain", "$\\hat{y}$\n"), ("block bold", "$$\n\\hat{\\mathbf{y}}\n$$\n"), ("inline vec", "$\\vec{\\mathbf{v}}$\n"), ("block vec", "$$\n\\vec{\\mathbf{v}}\n$$\n") })
            {
                using var frame = new OffscreenFrame(2000, 300);
                if (!frame.Ok) { check("Math/accent offscreen target", false, "GDI"); return; }
                using var doc = new DocView();
                doc.Load(md); doc.OnWidth(1000f);
                frame.Paint(doc, 0f);
                int fromX = Math.Max((int)(OverlayGeometry.RulerWidth * 2f), (int)(doc.ColX * 2f) - 4), top = (int)(OffscreenFrame.Top * 2f);
                var bands = frame.InkBands(fromX).Where(b => b.Top >= top).ToList();
                string detail = "bands=" + string.Join(";", bands.Select(b => b.Top + "-" + b.Bottom));
                bool ok = false;
                if (bands.Count >= 2)
                {
                    var hat = frame.InkClusters(bands[0], fromX, 4);
                    // 字母只量 x 高度内的上部(斜体 y 的下伸尾巴会把整体墨迹中心拉向左下)
                    var letter = frame.InkClusters((bands[1].Top, bands[1].Top + (bands[1].Bottom - bands[1].Top) * 2 / 5), fromX, 4);
                    if (hat.Count > 0 && letter.Count > 0)
                    {
                        // 向量箭头(S77)比字母宽是常态,只要求居中:箭头中心偏离字母中心不超过字母宽 1/4
                        ok = name.EndsWith("vec")
                            ? Math.Abs(hat[0].L + hat[^1].R - letter[0].L - letter[^1].R) <= (letter[^1].R - letter[0].L) / 2
                            : hat[0].L >= letter[0].L && hat[^1].R <= letter[^1].R;
                        detail += $" hat={hat[0].L}..{hat[^1].R} letter={letter[0].L}..{letter[^1].R}";
                    }
                }
                check("Math/accent sits over its letter " + name, ok, detail);
            }
        }
    }

    // 行内字母下标(S77,S76 遗留 w_(age)):Unicode 无对应下标时改由原生排版器画成内嵌对象。
    // 离屏 2x:公式右端(下标末字 e)顶部须低于左端 w 的顶部、底部低于 w 的底部,且顶部高于 w 底部;线性 w_(age) 右端是高出 w 的右括号。
    private static void InlineNativeMath(Action<string, bool, string> check)
    {
        var para = MdParser.Parse("$w_{\\mathrm{age}}$ 与 $x^2$ 与 $w_{\\mathrm{area}}$\n").OfType<Para>().FirstOrDefault();
        var maths = para?.Inlines.Where(r => r.I).ToList();
        // 上下标含字母一律原生(S77):Unicode 修饰字母多落回退字体、粗细高低不齐(同正文上下标规则);纯数字仍走 Unicode
        check("Math/inline letter subscript keeps TeX for native layout", maths != null && maths.Count == 3
            && maths[0].MathNative && maths[0].MathTex == "w_{\\mathrm{age}}" && maths[0].Text == "w_(age)" && !maths[1].MathNative && maths[1].Text == "x²"
            && maths[2].MathNative,
            maths == null ? "no para" : string.Join("|", maths.Select(r => r.Text + "=" + r.MathTex + (r.MathNative ? "[N]" : ""))));
        // 公式是原子片段(S77):线性文本里的 * _ 不参与强调配对;强调重建不丢原生标记(d2l「$w_{\mathrm{age}}$称为*权重*」)
        var atomic = MdParser.Parse("$a*b$ 和 $c*d$\n").OfType<Para>().First().Inlines;
        check("Math/inline formula text never pairs as emphasis", atomic.Count == 3 && atomic[0].Text == "a*b" && atomic[2].Text == "c*d" && !atomic[1].I,
            string.Join("|", atomic.Select(r => r.Text + (r.I ? "[i]" : ""))));
        var emphasized = MdParser.Parse("**$w_{\\mathrm{age}}$** 称为*权重*\n").OfType<Para>().First().Inlines;
        check("Math/emphasis rebuild keeps native formula", emphasized.Count > 0 && emphasized[0].MathNative && emphasized[0].B && emphasized.Any(r => r.Text == "权重" && r.I),
            string.Join("|", emphasized.Select(r => r.Text + (r.MathNative ? "[N]" : "") + (r.B ? "[b]" : ""))));
        // 长式在顶层关系/二元运算符后切段(每段一个内嵌对象,段间可断行);段长须与线性文本逐段对齐
        var pieces = MathInline.PieceLengths(@"\hat{y} = w_{\mathrm{area}} + b");
        check("Math/inline formula splits after operators", pieces.Count == 3 && pieces.Sum() == MathUnicode.Convert(@"\hat{y} = w_{\mathrm{area}} + b").Length,
            string.Join(",", pieces));
        using (new RenderStateScope())
        {
            RenderResources.Scale = 2f; RenderResources.SetBodySize(16f); RenderResources.ApplyTheme(false);
            using var frame = new OffscreenFrame(2000, 300);
            if (!frame.Ok) { check("Math/inline native offscreen target", false, "GDI"); return; }
            using var doc = new DocView();
            doc.Load("$w_{\\mathrm{age}}$\n"); doc.OnWidth(1000f);
            frame.Paint(doc, 0f);
            int fromX = Math.Max((int)(OverlayGeometry.RulerWidth * 2f), (int)(doc.ColX * 2f) - 4), top = (int)(OffscreenFrame.Top * 2f);
            var bands = frame.InkBands(fromX).Where(b => b.Top >= top).ToList();
            string detail = "bands=" + string.Join(";", bands.Select(b => b.Top + "-" + b.Bottom));
            bool ok = false;
            if (bands.Count > 0)
            {
                (int Top, int Bottom) band = (bands[0].Top, bands.Max(b => b.Bottom));
                var all = frame.InkClusters(band, fromX, 1000);
                if (all.Count > 0)
                {
                    int l = all[0].L, r = all[^1].R, w = r - l;
                    var left = Rows(frame, l, l + w / 4, band);
                    var right = Rows(frame, r - w / 5, r, band);
                    // 下标降而不脱:顶部仍高于 w 底部(与基线交叠),不整个悬在基线下方
                    ok = right.Top > left.Top + 2 && right.Bottom > left.Bottom + 2 && right.Top < left.Bottom;
                    detail += $" ink={l}..{r} w-rows={left.Top}..{left.Bottom} end-rows={right.Top}..{right.Bottom}";
                }
            }
            check("Math/inline letter subscript sits below the baseline", ok, detail);
        }
    }

    // S77 续:上标紧贴基底(]ᵀ 原先隔出约 4.5 DIP);行内原生公式随所在布局字号(标题)。
    private static void InlineContext(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            RenderResources.Scale = 2f; RenderResources.SetBodySize(16f); RenderResources.ApplyTheme(false);
            // 参照:正文 ]² 的间隙(正文字体 ] 的右侧边距 + 上标字形左侧边距);公式上标不应比它松 1 DIP 以上
            int GapOf(string md)
            {
                using var frame = new OffscreenFrame(2000, 300);
                using var doc = new DocView();
                doc.Load(md); doc.OnWidth(1000f);
                frame.Paint(doc, 0f);
                int fromX = Math.Max((int)(OverlayGeometry.RulerWidth * 2f), (int)(doc.ColX * 2f) - 4), top = (int)(OffscreenFrame.Top * 2f);
                var bands = frame.InkBands(fromX).Where(b => b.Top >= top).ToList();
                if (bands.Count == 0) return int.MaxValue;
                var clusters = frame.InkClusters((bands[0].Top, bands.Max(b => b.Bottom)), fromX, 1);
                return clusters.Count >= 2 ? clusters[^1].L - clusters[^2].R : int.MaxValue;
            }
            int reference = GapOf("[x]^2^\n");
            foreach (var (name, md) in new[] { ("inline", "$[x_{\\mathrm{a}}]^\\top$\n"), ("block", "$$\n[x]^\\top\n$$\n") })
            {
                int gap = GapOf(md);
                check("Math/superscript hugs its base " + name, gap <= reference + 2, $"gap={gap} body]²={reference}");
            }
            {
                using var frame = new OffscreenFrame(2000, 400);
                using var doc = new DocView();
                doc.Load("# $w_{\\mathrm{a}}$\n\n$w_{\\mathrm{a}}$\n"); doc.OnWidth(1000f);
                frame.Paint(doc, 0f);
                int fromX = Math.Max((int)(OverlayGeometry.RulerWidth * 2f), (int)(doc.ColX * 2f) - 4), top = (int)(OffscreenFrame.Top * 2f);
                var bands = frame.InkBands(fromX).Where(b => b.Top >= top).ToList();
                int Width((int Top, int Bottom) band) { var c = frame.InkClusters(band, fromX, 1000); return c.Count == 0 ? 0 : c[^1].R - c[0].L; }
                int hw = bands.Count >= 2 ? Width(bands[0]) : 0, pw = bands.Count >= 2 ? Width(bands[^1]) : 0;
                check("Math/inline formula follows heading size", pw > 0 && hw > pw * 1.3, $"heading={hw} para={pw} bands={bands.Count}");
            }
        }
    }

    private const string ArchDiagram =
        "graph TD\n" +
        "    Host[Program 窗口宿主] --> Tabs[DocumentTabs 打开与标签事务]\n" +
        "    Tabs --> Session[DocumentSession 文档会话]\n" +
        "    Session --> File[DocumentFile 编码与版本快照]\n" +
        "    File --> Atomic[AtomicFile 版本校验与原子替换]\n" +
        "    Host --> Export[DocumentExport 草稿导出]\n" +
        "    Export --> Atomic\n" +
        "    Export --> Parser\n" +
        "    Host --> Dialogs[NativeFileDialogs / DocumentDialogs]\n" +
        "    Host --> Find[FindBar 控件与导航]\n" +
        "    Find --> Search[TextSearch 查找与替换]\n" +
        "    Host --> Recovery[DraftStore 恢复副本]\n" +
        "    Recovery --> Atomic\n" +
        "    Recovery --> Storage[AppStorage 配置目录]\n" +
        "    Host --> Log[AppLog 错误日志]\n" +
        "    Session --> View[DocView 文档布局与绘制]\n" +
        "    View --> Parser[MdParser Markdown 解析]\n" +
        "    View --> Search\n" +
        "    View --> Images[DocumentImages 图片资源]\n" +
        "    Host --> Toolbar[ToolbarView 工具栏与标签绘制]\n" +
        "    Host --> Editor[EditorControl 编辑控件]\n" +
        "    Editor --> EditorKit[EditorKit 文本变换]\n" +
        "    Host --> ImageWindow[ImageWindow 图片窗口]\n" +
        "    Host --> Render[RenderResources 共享绘图资源]\n" +
        "    View --> Math[MathRender 公式布局]\n" +
        "    View --> Model[DocumentModel 内容模型]\n" +
        "    Parser --> Model\n" +
        "    Export --> Html[HtmlExport HTML 构建]\n" +
        "    Html --> Model\n" +
        "    View --> Render\n" +
        "    Toolbar --> Render\n" +
        "    Images --> Render\n" +
        "    Math --> Render\n" +
        "    Editor --> Render\n" +
        "    ImageWindow --> Render\n" +
        "    Find --> Render\n" +
        "    Render --> Native[DirectWrite 与 Direct2D]";

    // 用户试用(S79):ARCHITECTURE.md 的模块图,跨层的边直线穿过中间层方框、层内按出现顺序排、父节点不居中。
    private static void MermaidLayout(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail) => check("MathDiagram/" + name, ok, detail);
        foreach (string dir in new[] { "TD", "LR", "BT" })
        {
            bool parsed = MermaidLite.TryParse(ArchDiagram.Replace("graph TD", "graph " + dir), out var g);
            string hit = null;
            if (parsed)
                foreach (var e in g.Edges)
                {
                    var path = MermaidLite.EdgePath(g, e);
                    for (int s = 0; s + 1 < path.Count && hit == null; s++)
                        for (int n = 0; n < g.Nodes.Count && hit == null; n++)
                        {
                            if (n == e.From || n == e.To) continue;
                            var nd = g.Nodes[n];
                            if (SegmentHitsBox(path[s], path[s + 1], nd.X + 2f, nd.Y + 2f, nd.X + nd.W - 2f, nd.Y + nd.H - 2f))
                                hit = g.Nodes[e.From].Id + "->" + g.Nodes[e.To].Id + " crosses " + nd.Id;
                        }
                }
            C("diagram edges avoid unrelated nodes " + dir, parsed && hit == null, hit ?? (parsed ? "clear" : "parse failed"));
        }
        // X1/Y1 先声明:按出现顺序排时下层为 [X1, Y1],A→Y1 与 B→X1 交叉;排序后应不交叉。
        bool crossParsed = MermaidLite.TryParse("graph TD\nX1[x]\nY1[y]\nA --> Y1\nB --> X1", out var cg);
        bool crossing = crossParsed && PathsCross(MermaidLite.EdgePath(cg, cg.Edges[0]), MermaidLite.EdgePath(cg, cg.Edges[1]));
        C("diagram ordering removes avoidable crossing", crossParsed && !crossing, crossParsed ? "crossing=" + crossing : "parse failed");
        bool fanParsed = MermaidLite.TryParse("graph TD\nA --> B\nA --> C\nA --> D", out var fg);
        float Cx(MermaidGraph.MNode nd) => nd.X + nd.W / 2f;
        float offset = fanParsed ? Cx(fg.Nodes[0]) - (Cx(fg.Nodes[1]) + Cx(fg.Nodes[2]) + Cx(fg.Nodes[3])) / 3f : float.NaN;
        C("diagram parent is centered over its children", fanParsed && MathF.Abs(offset) <= 1f, "offset=" + offset);
        // S82:有环的流程图(“失败 → 重试 → 回到检查”)改前整张退回源码。回头边反向参与分层、走虚拟通道,
        // 画时倒回原方向,箭头仍指向原目标;自环画在方框外侧。检查:不穿无关方框,两端落在各自方框边上,
        // 箭头朝向目标,声明顺序仍是阅读顺序。
        string Clear(MermaidGraph g)
        {
            foreach (var e in g.Edges)
            {
                var path = MermaidLite.EdgePath(g, e);
                for (int s = 0; s + 1 < path.Count; s++)
                    for (int k = 0; k < g.Nodes.Count; k++)
                    {
                        if (k == e.From || k == e.To) continue;
                        var nd = g.Nodes[k];
                        if (SegmentHitsBox(path[s], path[s + 1], nd.X + 2f, nd.Y + 2f, nd.X + nd.W - 2f, nd.Y + nd.H - 2f))
                            return g.Nodes[e.From].Id + "->" + g.Nodes[e.To].Id + " crosses " + nd.Id;
                    }
            }
            return null;
        }
        static bool OnBox((float x, float y) p, MermaidGraph.MNode nd) =>
            p.x >= nd.X - 0.6f && p.x <= nd.X + nd.W + 0.6f && p.y >= nd.Y - 0.6f && p.y <= nd.Y + nd.H + 0.6f
            && !(p.x > nd.X + 0.6f && p.x < nd.X + nd.W - 0.6f && p.y > nd.Y + 0.6f && p.y < nd.Y + nd.H - 0.6f);
        string Arrival(MermaidGraph g)
        {
            foreach (var e in g.Edges)
            {
                var path = MermaidLite.EdgePath(g, e);
                var from = g.Nodes[e.From]; var to = g.Nodes[e.To];
                string name = from.Id + "->" + to.Id;
                if (!OnBox(path[0], from)) return name + " starts off its source " + path[0];
                if (!OnBox(path[^1], to)) return name + " ends off its target " + path[^1];
                var (tx, ty) = path[^1]; var (px, py) = path[^2];
                float cx = to.X + to.W / 2f, cy = to.Y + to.H / 2f;
                if ((tx - px) * (cx - tx) + (ty - py) * (cy - ty) <= 0f) return name + " arrow points away from its target";
            }
            return null;
        }
        const string Retry = "graph TD\nA[开始] --> B{检查}\nB -->|通过| C[完成]\nB -->|失败| D[重试]\nD --> B";
        foreach (string dir in new[] { "TD", "LR", "BT" })
        {
            bool ok = MermaidLite.TryParse(Retry.Replace("graph TD", "graph " + dir), out var rg);
            string why = !ok ? "parse failed" : Clear(rg) ?? Arrival(rg);
            C("cyclic diagram is drawn with arrows into their targets " + dir, ok && why == null, why ?? "clear");
        }
        bool ringOk = MermaidLite.TryParse("graph TD\nA --> B\nB --> C\nC --> D\nD --> A", out var ring);
        string ringWhy = !ringOk ? "parse failed" : Clear(ring) ?? Arrival(ring);
        C("long back edge routes around the boxes it passes", ringOk && ringWhy == null, ringWhy ?? "clear");
        bool readingOrder = ringOk && ring.Nodes[0].Y < ring.Nodes[1].Y && ring.Nodes[1].Y < ring.Nodes[2].Y && ring.Nodes[2].Y < ring.Nodes[3].Y;
        C("cycle keeps the declared order top to bottom", readingOrder, ringOk ? string.Join(",", ring.Nodes.ConvertAll(nd => nd.Y)) : "parse failed");
        bool selfOk = MermaidLite.TryParse("graph TD\nA[轮询] --> A\nA --> B[结束]", out var sg);
        string selfWhy = !selfOk ? "parse failed" : Clear(sg) ?? Arrival(sg);
        if (selfWhy == null)
        {
            var loop = MermaidLite.EdgePath(sg, sg.Edges[0]); var box = sg.Nodes[0];
            for (int s = 0; s + 1 < loop.Count && selfWhy == null; s++)
                if (SegmentHitsBox(loop[s], loop[s + 1], box.X + 2f, box.Y + 2f, box.X + box.W - 2f, box.Y + box.H - 2f)) selfWhy = "loop cuts through its box";
            if (loop.Count < 4) selfWhy ??= "loop has " + loop.Count + " points";
        }
        C("self loop is drawn outside its box", selfOk && selfWhy == null, selfWhy ?? "clear");
        // 边标签放在中间那段线旁:横线放上方,竖线放右侧,文字不压线(改前一律放正上方,竖线上就压在线上);
        // 放在右侧的标签要算进图的宽度,不被底板裁掉。
        bool labelled = MermaidLite.TryParse("graph TD\nA -->|是| B", out var ug) & MermaidLite.TryParse("graph LR\nA -->|是| B", out var lg);
        bool placed = labelled && MermaidLite.LabelAt(MermaidLite.EdgePath(ug, ug.Edges[0])).beside && !MermaidLite.LabelAt(MermaidLite.EdgePath(lg, lg.Edges[0])).beside;
        C("edge label sits beside an upright line and above a level one", placed, labelled ? "placed=" + placed : "parse failed");
        bool loopLabelled = MermaidLite.TryParse("graph TD\nA[轮询] -->|无任务| A", out var lsg);
        float labelRight = loopLabelled ? MermaidLite.LabelAt(MermaidLite.EdgePath(lsg, lsg.Edges[0])).x + 6f + 3 * 17f : float.NaN;
        float extentW = loopLabelled ? MermaidLite.Extent(lsg).w : float.NaN;
        C("label beside a loop stays inside the diagram", loopLabelled && labelRight <= extentW, $"label right {labelRight} extent {extentW}");

        // S85:一对来回的平行边(开发 ⇄ 测试)只隔约 12,标签按“横线放上方”会夹在两线之间、压到另一条:
        // 各放外侧(横线:上面那条放上方、下面那条放下方;竖线:右边那条放右侧、左边那条放左侧)。
        foreach (string pairSrc in new[] {
            "flowchart LR\nA[开始] --> B{检查}\nB -->|失败| A",
            "flowchart LR\nA[开发] -->|提交| B[测试]\nB -->|退回| A",
            "graph TD\nA[开发] --> B[测试]\nB -->|失败| A",
            "graph TD\nA[开发] -->|提交| B[测试]\nB -->|退回| A" })
        {
            string pairWhy = MermaidLite.TryParse(pairSrc, out var pg) ? null : "parse failed";
            for (int e = 0; pairWhy == null && e < pg.Edges.Count; e++)
            {
                if (string.IsNullOrEmpty(pg.Edges[e].Label)) continue;
                var (lx, ly, lw, lh) = MermaidLite.LabelBox(pg, pg.Edges[e]);
                for (int o = 0; pairWhy == null && o < pg.Edges.Count; o++)
                {
                    if (o == e) continue;
                    var other = MermaidLite.EdgePath(pg, pg.Edges[o]);
                    for (int s = 0; s + 1 < other.Count && pairWhy == null; s++)
                        if (SegmentHitsBox(other[s], other[s + 1], lx + 1f, ly + 1f, lx + lw - 1f, ly + lh - 1f)) pairWhy = $"label {pg.Edges[e].Label} at ({lx:0},{ly:0},{lw:0}x{lh:0}) crosses edge {o}";
                }
            }
            C("label of a two-way pair stays off the other line " + pairSrc.Split('\n')[0] + " " + pairSrc.Split('\n')[^1], pairWhy == null, pairWhy ?? "clear");
        }

        // S86:标签不伸出图的上/左边距(否则底板上下留白不等,TD 翻到左侧的宽标签会被裁到图外)。
        foreach (var marginSrc in new[]
        {
            "flowchart TD\nA[开发] -->|提交一份很长的合并请求| B[测试]\nB -->|退回| A",
            "flowchart LR\nA[开始] --> B{检查}\nB -->|失败| A\nB -->|通过| C[完成]",
        })
        {
            string marginWhy = !MermaidLite.TryParse(marginSrc, out var mgm) ? "parse" : null;
            for (int e = 0; marginWhy == null && e < mgm.Edges.Count; e++)
            {
                if (string.IsNullOrEmpty(mgm.Edges[e].Label)) continue;
                var (lx, ly, lw, lh) = MermaidLite.LabelBox(mgm, mgm.Edges[e]);
                if (lx < 29.5f || ly < 29.5f) marginWhy = $"label {mgm.Edges[e].Label} at ({lx:0},{ly:0},{lw:0}x{lh:0}) pokes into the 30 margin";
            }
            C("labels keep the diagram margin " + marginSrc.Split('\n')[0] + " " + marginSrc.Split('\n')[1], marginWhy == null, marginWhy ?? "clear");
        }

        // 宽图与宽代码同规则:裁在底板内,横向滚动。改前 OverflowW 为 0,整张图画出底板直到窗口外。
        using (new RenderStateScope())
        {
            RenderResources.Scale = 1f;
            using var frame = new OffscreenFrame(2000, 900);
            if (!frame.Ok) { C("wide diagram offscreen target", false, "GDI"); return; }
            using var doc = new DocView();
            doc.Load("```mermaid\n" + ArchDiagram + "\n```\n");
            doc.OnWidth(1000f);   // S80: column 904 at 1000 DIP, still narrower than the module diagram
            // 右界取底板本身(S84 起底板两侧对称外扩 22,不再以列右缘为界)。
            int limit = doc.CodeChromeForTest(0, out var panel, out _, out _, out _, out _, out _) ? (int)(panel.X + panel.W) + 1 : -2;
            int Rightmost()
            {
                int right = -1;
                for (int y = (int)OffscreenFrame.Top; y < frame.H; y++)
                    for (int x = frame.W - 21; x > right; x--)
                        if (frame.Ink(x, y)) { right = x; break; }
                return right;
            }
            frame.Paint(doc, 0f);
            int before = Rightmost();
            C("wide diagram stays inside its panel", before >= 0 && before <= limit, "rightmost=" + before + " limit=" + limit);
            bool scrolled = doc.ScrollCodeAt(doc.ColX + 100f, 150f, 100000f);
            C("wide diagram scrolls sideways", scrolled, "ScrollCodeAt over the diagram");
            frame.Paint(doc, 0f);
            int after = Rightmost();
            C("scrolled diagram stays inside its panel", after >= 0 && after <= limit, "rightmost=" + after + " limit=" + limit);
        }
    }

    private static bool SegmentHitsBox((float x, float y) a, (float x, float y) b, float l, float t, float r, float btm)
    {
        // Liang–Barsky: does any part of segment a→b lie strictly inside the box?
        float t0 = 0f, t1 = 1f, dx = b.x - a.x, dy = b.y - a.y;
        foreach (var (p, q) in new[] { (-dx, a.x - l), (dx, r - a.x), (-dy, a.y - t), (dy, btm - a.y) })
        {
            if (MathF.Abs(p) < 1e-6f) { if (q <= 0f) return false; continue; }
            float u = q / p;
            if (p < 0f) { if (u > t1) return false; t0 = MathF.Max(t0, u); }
            else { if (u < t0) return false; t1 = MathF.Min(t1, u); }
        }
        return t1 - t0 > 1e-4f;
    }

    private static bool PathsCross(List<(float x, float y)> p, List<(float x, float y)> q)
    {
        static float Cross((float x, float y) o, (float x, float y) a, (float x, float y) b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        for (int i = 0; i + 1 < p.Count; i++)
            for (int j = 0; j + 1 < q.Count; j++)
            {
                float d1 = Cross(q[j], q[j + 1], p[i]), d2 = Cross(q[j], q[j + 1], p[i + 1]);
                float d3 = Cross(p[i], p[i + 1], q[j]), d4 = Cross(p[i], p[i + 1], q[j + 1]);
                if (d1 * d2 < 0f && d3 * d4 < 0f) return true;
            }
        return false;
    }

    private static (int Top, int Bottom) Rows(OffscreenFrame frame, int l, int r, (int Top, int Bottom) band)
    {
        int t = int.MaxValue, b = -1;
        for (int y = band.Top; y <= band.Bottom; y++)
            for (int x = l; x <= r; x++)
                if (frame.Ink(x, y)) { t = Math.Min(t, y); b = Math.Max(b, y); break; }
        return (t, b);
    }

    private static IEnumerable<MAtom> Atoms(MAtom atom)
    {
        if (atom == null) yield break;
        yield return atom;
        IEnumerable<MAtom> children = atom switch
        {
            MRow row => row.Items,
            MFrac fraction => new[] { fraction.Num, fraction.Den },
            MSqrt root => new[] { root.Body, root.Index },
            MScript script => new[] { script.BaseAtom, script.Super, script.Sub },
            _ => Array.Empty<MAtom>()
        };
        foreach (var child in children) foreach (var item in Atoms(child)) yield return item;
    }

    private static string Plain(MAtom atom) => atom switch
    {
        MText text => text.Text,
        MRow row => string.Concat(row.Items.ConvertAll(Plain)),
        MFrac fraction => "frac(" + Plain(fraction.Num) + "," + Plain(fraction.Den) + ")",
        null => "null",
        _ => atom.GetType().Name
    };
}

