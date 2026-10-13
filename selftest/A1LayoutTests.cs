using System;
using System.Reflection;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static class A1LayoutTests
{
    private static readonly FieldInfo Ys = typeof(DocView).GetField("_ys", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Heights = typeof(DocView).GetField("_heights", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Run(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            RenderResources.SetSerif(false, RenderResources.SerifFamily);
            HtmlPanelBalance(check);
            foreach (float size in new[] { 12f, 16f, 20f, 28f })
            {
                RenderResources.SetBodySize(size);
                Geometry(check, size);
                var following = new (string Name, string Markdown, Type Type, float TopOutset)[]
                {
                    ("paragraph", "Body text", typeof(Para), 0f),
                    ("code", "```text\ncode line\n```", typeof(CodeBlock), 20f),   // A4 E4:代码块上内边距 20(右上角标签/复制带区)
                    ("quote", "> Quoted text", typeof(Quote), 0f),
                    ("list", "- list item", typeof(ListItem), 0f),
                    ("table", "| A | B |\n| --- | --- |\n| a | b |", typeof(Table), 0f),
                    ("HTML", "<iframe src=\"about:blank\">raw block</iframe>", typeof(HtmlBlock), 20f),   // 白名单外标签保持字面面板;S71 起上内边距 20
                    ("author rule", "---", typeof(HRule), 0f),
                    ("math", "```math\n\\frac{1}{2}\n```", typeof(CodeBlock), 0f),
                    ("math fallback", "```math\n\\unknown{x}\n```", typeof(CodeBlock), 0f)
                };
                for (int level = 1; level <= 6; level++)
                {
                    float expected = (level == 1 ? 16f : level == 2 ? 10f : 8f) * size / 16f;
                    foreach (var f in following)
                        Gap(check, $"H{level} to {f.Name} size={size}", new string('#', level) + " Heading\n\n" + f.Markdown,
                            typeof(Heading), f.Type, expected, checkTop: f.Name == "paragraph", topOutset: f.TopOutset);
                }
                Gap(check, "mid-document H1 upper spacing " + size, "Body text\n\n# Heading", typeof(Para), typeof(Heading), 28f * size / 16f);
                Gap(check, "adjacent headings retain maximum " + size, "## Heading\n\n### Next", typeof(Heading), typeof(Heading), 22f * size / 16f);
                Gap(check, "paragraph to code retains maximum " + size, "Body text\n\n```\ncode\n```", typeof(Para), typeof(CodeBlock), 14f * size / 16f, topOutset: 8f);   // A4 E4:正文多让 8 DIP,面板外沿到段落不变
                Gap(check, "code to paragraph retains maximum " + size, "```\ncode\n```\n\nBody text", typeof(CodeBlock), typeof(Para), 21f * size / 16f);
                Gap(check, "paragraph to author rule retains maximum " + size, "Body text\n\n---", typeof(Para), typeof(HRule), 21f * size / 16f);
            }
            RenderResources.SetBodySize(16f);
            RenderResources.Scale = 1f; // Offscreen render target only; never a system DPI change.
            Rules(check);
        }
    }

    private static void Gap(Action<string, bool, string> check, string name, string markdown, Type first, Type second, float expected, bool checkTop = false, float topOutset = 0f)
    {
        using var doc = new DocView();
        doc.Load(markdown + "\n");
        doc.OnWidth(960f);
        bool fixture = doc.BlockCount == 2 && doc.Blocks[0].GetType() == first && doc.Blocks[1].GetType() == second;
        float actual = fixture ? Values(Ys, doc)[1] - topOutset - Values(Ys, doc)[0] - Values(Heights, doc)[0] : float.NaN;
        check("A1Layout/" + name, fixture && MathF.Abs(actual - expected) < 0.01f,
            $"blocks={doc.BlockCount} edgeGap={actual:F3} topOutset={topOutset:F3} expected={expected:F3} DIP");
        if (checkTop)
            check("A1Layout/no extra top heading margin " + name, MathF.Abs(Values(Ys, doc)[0] - 48f) < 0.01f, "document inset=48 DIP, independent of heading before-margin");
    }

    private static void Geometry(Action<string, bool, string> check, float size)
    {
        using var doc = new DocView();
        doc.Load("# Heading\n\nBody text\n");
        bool sameGeometry = true, overlayStable = true;
        foreach (float v in new[] { 360f, 380f, 399f, 400f, 640f, 672f, 703f, 704f, 960f })
        {
            doc.OnWidth(v);
            float expected = WindowFit.ColW(v);
            sameGeometry &= MathF.Abs(doc.ColW - expected) < 0.001f && MathF.Abs(doc.ColX - WindowFit.ColX(v, expected)) < 0.001f;
            float height = doc.TotalH, x = doc.ColX, width = doc.ColW;
            if (OverlayGeometry.TocPinned(v)) continue;   // S80:宽窗口的常驻目录让出宽度,由 FluidColumnTests 覆盖
            doc.SetToc(true);
            overlayStable &= doc.TotalH == height && doc.ColX == x && doc.ColW == width;
            doc.SetToc(false);
            overlayStable &= doc.TotalH == height && doc.ColX == x && doc.ColW == width;
        }
        check("A1Layout/DocView continuous geometry " + size, sameGeometry, "actual columns at 360/380/399/400/640/672/703/704/960 DIP");
        check("A1Layout/transient TOC does not change reading column " + size, overlayStable, "actual width, origin and total height unchanged");
    }

    // 自用走查(S71):字面 HTML 面板底部留白 36(布局 +24、底板下伸 12)对顶部 12,上窄下宽;
    // 改与代码面板同为上下 20。一行与两行块之差即一行高,由此求文字下方留白。
    private static void HtmlPanelBalance(Action<string, bool, string> check)
    {
        float PanelH(string md)
        {
            using var doc = new DocView();
            doc.Load(md);
            doc.OnWidth(960f);
            return doc.BlockCount == 1 && doc.Blocks[0] is HtmlBlock ? Values(Heights, doc)[0] : float.NaN;
        }
        float one = PanelH("<iframe src=\"a\"></iframe>\n"), two = PanelH("<iframe src=\"a\">\n</iframe>\n");
        float below = one - (two - one) + 12f;
        check("A1Layout/HTML panel bottom padding matches the 20 DIP top", MathF.Abs(below - 20f) < 0.5f, $"one={one:0.##} two={two:0.##} below={below:0.##}");
    }

    private static float[] Values(FieldInfo field, DocView doc) => (float[])field.GetValue(doc);

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);

    private static void Rules(Action<string, bool, string> check)
    {
        const int width = 960, height = 400;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            check("A1Layout/offscreen target created", screen != 0 && memory != 0 && bitmap != 0, "test-owned memory bitmap; no window shown or desktop input");
            if (screen == 0 || memory == 0 || bitmap == 0) return;
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                var rect = new RECT { R = width, B = height };
                using (var heading = new DocView())
                {
                    heading.Load("## Short heading\n");
                    heading.OnWidth(width);
                    heading.Paint(rect, memory, 0f, height, 0f);
                    int row = (int)MathF.Round(Values(Ys, heading)[0] - 12f);
                    uint background = GetPixel(memory, 20, 20);
                    bool clear = background != 0xFFFFFFFFu;
                    for (int x = 240; x <= 720; x += 40)
                        for (int y = row - 1; y <= row + 1; y++) clear &= GetPixel(memory, x, y) == background;
                    check("A1Layout/no automatic H2 rule " + dark, clear, "former separator band must be canvas pixels");
                }
                using (var rule = new DocView())
                {
                    rule.Load("---\n");
                    rule.OnWidth(width);
                    rule.Paint(rect, memory, 0f, height, 0f);
                    int row = (int)MathF.Round(Values(Ys, rule)[0]);
                    uint background = GetPixel(memory, 20, 20);
                    int paintedColumns = 0;
                    for (int x = 240; x <= 720; x += 40)
                    {
                        bool painted = false;
                        for (int y = row - 2; y <= row + 2; y++)
                        {
                            uint pixel = GetPixel(memory, x, y);
                            painted |= pixel != background && pixel != 0xFFFFFFFFu;
                        }
                        if (painted) paintedColumns++;
                    }
                    check("A1Layout/author Markdown rule still drawn " + dark,
                        rule.BlockCount == 1 && rule.Blocks[0] is HRule && paintedColumns == 13,
                        $"author rule has ink in {paintedColumns}/13 sampled columns");
                }
            }
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}

