using System;
using System.Linq;
using System.Text;
using MDViewerNative;

// A4 内容外观:进度线、引用竖线、三线表、代码块与复制入口的离屏像素契约(不显示窗口、不注入输入)。
internal static class A4ContentTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            RenderResources.SetBodySize(16f);
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                Progress(check, dark);
                QuoteRail(check, dark);
                ThreeLineTable(check, dark);
                CodeBlockChrome(check, dark);
                LinkColor(check, dark);
                WideTableCue(check, dark);
                QuotedPanelRail(check, dark);
                CodePanelBalance(check, dark);
            }
            CodePanelSides(check);
            MermaidPanelBalance(check);
            MermaidStyle(check);
            Footnotes(check);
            HtmlPanelCorner(check);
            MermaidSearch(check);
            BareUrlCjk(check);
        }
    }

    private const float Top = OffscreenFrame.Top;

    private static uint Accent(bool dark) => Colorref(dark ? ThemePalette.AccentD : ThemePalette.AccentL);
    private static uint Canvas(bool dark) => Colorref(dark ? ThemePalette.CanvasD : ThemePalette.CanvasL);
    private static uint Hairline(bool dark) => Colorref(dark ? ThemePalette.HairlineD : ThemePalette.HairlineL);

    private static string LongDoc()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("Paragraph ").Append(i).Append(" with a few words of plain body text.\n\n");
        return sb.ToString();
    }

    // E1:进度在视口底缘 1 物理像素 Accent;顶缘只剩 Hairline 分隔;顶部/不足一屏/打印不画。
    private static void Progress(Action<string, bool, string> check, bool dark)
    {
        RenderResources.Scale = 1f;
        using var frame = new OffscreenFrame(1000, 400);
        if (!frame.Ok) { check("A4Content/progress offscreen target " + dark, false, "GDI"); return; }
        uint accent = Accent(dark);
        using (var doc = new DocView())
        {
            doc.Load(LongDoc());
            doc.OnWidth(frame.W);
            float span = doc.TotalH - (frame.H - Top);
            float half = MathF.Round(span / 2f);
            frame.Paint(doc, half);
            int bottom = frame.CountRow(frame.H - 1, accent), above = frame.CountRow(frame.H - 2, accent);
            int topRow = frame.CountRow((int)Top, accent);
            int expect = (int)MathF.Round(frame.W * half / span);
            check("A4Content/progress sits on the bottom edge " + dark, Math.Abs(bottom - expect) <= 3 && frame.At(2, frame.H - 1) == accent && above == 0,
                $"bottom={bottom} expect={expect} rowAbove={above} span={span:0}");
            check("A4Content/top edge keeps only the hairline separator " + dark, topRow == 0 && frame.At(frame.W - 3, (int)Top) == Hairline(dark),
                $"topAccent={topRow} px={frame.At(frame.W - 3, (int)Top):X6}");
            frame.Paint(doc, 0f);
            check("A4Content/no progress at the top of the document " + dark, frame.CountRow(frame.H - 1, accent) == 0, "scroll 0");
            doc.WrapCodeForPaper = true;
            frame.Paint(doc, half);
            check("A4Content/paper rendering draws no progress " + dark, frame.CountRow(frame.H - 1, accent) == 0 && frame.CountRow((int)Top, accent) == 0, "WrapCodeForPaper");
        }
        using (var doc = new DocView())
        {
            doc.Load("One short paragraph.\n");
            doc.OnWidth(frame.W);
            frame.Paint(doc, 0f);
            check("A4Content/short document draws no progress " + dark, frame.CountRow(frame.H - 1, accent) == 0, $"total={doc.TotalH:0}");
        }
    }

    // E2:引用竖线宽 round(2.5×scale) 物理像素、左缘吸附:在小数列坐标下仍是整像素实色,两侧紧邻即画布。
    private static void QuoteRail(Action<string, bool, string> check, bool dark)
    {
        uint accent = Accent(dark), canvas = Canvas(dark);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            RenderResources.Scale = scale;
            int expected = Math.Max(1, (int)MathF.Round(2.5f * scale, MidpointRounding.AwayFromZero));
            int chosen = 1000; float frac = 0f;
            for (int w = 1000; w < 1008; w++)
            {
                using var probe = new DocView();
                probe.Load("> Quoted text here.\n");
                probe.OnWidth(w / scale);
                float px = (probe.ColX + 7f) * scale;
                frac = px - MathF.Floor(px);
                if (frac > 0.2f && frac < 0.8f) { chosen = w; break; }
            }
            using var frame = new OffscreenFrame(chosen, 200);
            if (!frame.Ok) { check("A4Content/quote offscreen target " + dark, false, "GDI"); return; }
            using var doc = new DocView();
            doc.Load("> Quoted text here.\n");
            doc.OnWidth(chosen / scale);
            frame.Paint(doc, 0f);
            int row = -1;
            for (int y = (int)(Top * scale) + 2; y < frame.H && row < 0; y++) if (frame.CountRow(y, accent) > 0) row = y + 6;
            int run = 0, start = -1;
            if (row >= 0)
                for (int x = 0; x < frame.W / 2; x++)
                    if (frame.At(x, row) == accent) { if (start < 0) start = x; run++; }
                    else if (start >= 0) break;
            bool crisp = start > 0 && frame.At(start - 1, row) == canvas && frame.At(start + run, row) == canvas;
            check($"A4Content/quote rail is a crisp {expected}px Accent bar at scale {scale} " + dark, run == expected && crisp,
                $"width={chosen} frac={frac:0.00} row={row} run={run} start={start} left={(start > 0 ? frame.At(start - 1, row) : 0):X6} right={(start >= 0 ? frame.At(start + run, row) : 0):X6}");
        }
    }

    // E3:三线表。表格左内边距处的竖直像素列:HairlineZone 恰三段(顶/表头下/底,粗 round(1.5s)/round(s)/round(1.5s)),
    // 无表头 Surface 底、无体行间 Hairline,偶数体行 SurfaceAlt 保留。
    private static void ThreeLineTable(Action<string, bool, string> check, bool dark)
    {
        uint zone = Colorref(dark ? ThemePalette.HairlineZoneD : ThemePalette.HairlineZoneL);
        uint surface = Colorref(dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL);
        uint alt = Colorref(dark ? ThemePalette.SurfaceAltD : ThemePalette.SurfaceAltL);
        uint canvas = Canvas(dark);
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            RenderResources.Scale = scale;
            using var frame = new OffscreenFrame((int)(1000 * scale), (int)(360 * scale));
            if (!frame.Ok) { check("A4Content/table offscreen target " + dark, false, "GDI"); return; }
            using var doc = new DocView();
            doc.Load("| Name | Role |\n| --- | --- |\n| Alpha | Reader |\n| Bravo | Editor |\n| Charlie | Viewer |\n| Delta | Owner |\n");
            doc.OnWidth(1000f);
            frame.Paint(doc, 0f);
            int x = (int)MathF.Ceiling((doc.ColX + 3f) * scale);
            var runs = new System.Collections.Generic.List<int>();
            int current = 0, surfaces = 0, alts = 0, others = 0;
            for (int y = (int)(Top * scale) + 2; y < frame.H; y++)
            {
                uint c = frame.At(x, y);
                if (c == zone) current++; else { if (current > 0) runs.Add(current); current = 0; }
                if (c == surface) surfaces++;
                if (c == alt) alts++;
                if (c != zone && c != surface && c != alt && c != canvas) others++;
            }
            if (current > 0) runs.Add(current);
            int thick = Math.Max(1, (int)MathF.Round(1.5f * scale, MidpointRounding.AwayFromZero)), thin = Math.Max(1, (int)MathF.Round(scale, MidpointRounding.AwayFromZero));
            string got = string.Join(",", runs);
            check($"A4Content/table has exactly three rules {thick},{thin},{thick} at scale {scale} " + dark, got == $"{thick},{thin},{thick}", $"x={x} runs=[{got}]");
            check($"A4Content/table header has no Surface fill and no blended row lines at scale {scale} " + dark, surfaces == 0 && others == 0, $"surface={surfaces} other={others}");
            check($"A4Content/table keeps SurfaceAlt on even rows at scale {scale} " + dark, alts > 0, $"alt={alts}");
        }
    }

    // 宽表格横向溢出提示:画在底部规则线之下(至少隔 2 物理像素,不与规则线挤成双线),滑块用 Muted 而非语法注释色。
    private static void WideTableCue(Action<string, bool, string> check, bool dark)
    {
        uint zone = Colorref(dark ? ThemePalette.HairlineZoneD : ThemePalette.HairlineZoneL);
        uint muted = Colorref(dark ? ThemePalette.MutedD : ThemePalette.MutedL);
        static int Dist(uint a, uint b) => Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        const string wide = "| Column one | Column two with a much longer header | Column three with a longer header | Column four also long |\n| --- | --- | --- | --- |\n| a | b | c | d |\n| g | h | i | j |\n";
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            RenderResources.Scale = scale;
            using var frame = new OffscreenFrame((int)(620 * scale), (int)(300 * scale));
            if (!frame.Ok) { check("A4Content/wide table offscreen target " + dark, false, "GDI"); return; }
            using var doc = new DocView();
            doc.Load(wide); doc.OnWidth(620f);
            frame.Paint(doc, 0f);
            int x = (int)MathF.Ceiling((doc.ColX + 6f) * scale);
            int lastZone = -1, firstMuted = -1, mutedRows = 0;
            for (int y = (int)(Top * scale) + 2; y < frame.H; y++)
            {
                uint c = frame.At(x, y);
                if (c == zone) lastZone = y;
                if (Dist(c, muted) < 24) { mutedRows++; if (firstMuted < 0) firstMuted = y; }
            }
            check($"A4Content/wide table overflow cue is Muted and sits below the bottom rule at scale {scale} " + dark,
                mutedRows > 0 && lastZone > 0 && firstMuted >= lastZone + 3, $"x={x} lastRule={lastZone} firstCue={firstMuted} cueRows={mutedRows}");
        }
        // 表后紧跟代码块:底板顶边距表格底部规则线至少 6(溢出提示所占)+ 0.5 行高,不与提示条挤在一起。
        RenderResources.Scale = 1f;
        using (var frame = new OffscreenFrame(620, 400))
        using (var doc = new DocView())
        {
            doc.Load(wide + "\n```csharp\nvar x = 1;\n```\n"); doc.OnWidth(620f);
            frame.Paint(doc, 0f);
            int x = (int)MathF.Ceiling(doc.ColX + 6f), lastZone = -1;
            for (int y = (int)Top + 2; y < frame.H; y++) if (frame.At(x, y) == zone) lastZone = y;
            float want = 6f + 0.5f * RenderResources.LineH(RenderResources.BodySize);
            bool ok = doc.CodeChromeForTest(0, out var box, out _, out _, out _, out _, out _);
            float gap = ok ? box.Y + Top - (lastZone + 1) : -1f;
            check("A4Content/code panel after a table clears the overflow cue by half a line " + dark, ok && lastZone > 0 && gap >= want - 1f, $"gap={gap:0.##} want={want:0.##}");
        }
    }

    // 引用内的代码块:竖线覆盖整个底板(底板上伸 20、下伸 12),不只覆盖文字高度。
    private static void QuotedPanelRail(Action<string, bool, string> check, bool dark)
    {
        RenderResources.Scale = 1f;
        uint accent = Accent(dark);
        using var frame = new OffscreenFrame(1000, 400);
        if (!frame.Ok) { check("A4Content/quoted panel offscreen target " + dark, false, "GDI"); return; }
        using var doc = new DocView();
        doc.Load("> Quoted words before.\n>\n> ```text\n> literal\n> ```\n");
        doc.OnWidth(1000f);
        frame.Paint(doc, 0f);
        if (!doc.CodeChromeForTest(0, out var box, out _, out _, out _, out _, out _)) { check("A4Content/quoted code panel keeps its rail along the whole panel " + dark, false, "no panel"); return; }
        int x = (int)MathF.Round(doc.ColX + 20f - 13f) + 1;
        int top = (int)MathF.Ceiling(box.Y + Top) + 3, bottom = (int)MathF.Floor(box.Y + box.H + Top) - 3;
        uint a = frame.At(x, top), b = frame.At(x, bottom);
        check("A4Content/quoted code panel keeps its rail along the whole panel " + dark, a == accent && b == accent, $"x={x} top={top}:{a:X6} bottom={bottom}:{b:X6} accent={accent:X6}");
    }

    // 代码底板上下对称:文字下方留白与上方 20 DIP 相同(一行与两行块之差即一行高,由此求底部留白)。
    private static void CodePanelBalance(Action<string, bool, string> check, bool dark)
    {
        RenderResources.Scale = 1f;
        using var frame = new OffscreenFrame(1000, 400);
        if (!frame.Ok) { check("A4Content/code balance offscreen target " + dark, false, "GDI"); return; }
        float PanelH(string source)
        {
            using var doc = new DocView();
            doc.Load(source); doc.OnWidth(1000f);
            frame.Paint(doc, 0f);
            return doc.CodeChromeForTest(0, out var box, out _, out _, out _, out _, out _) ? box.H : -1f;
        }
        foreach (string lang in new[] { "", "python" })
        {
            float one = PanelH("```" + lang + "\nx = 1\n```\n"), two = PanelH("```" + lang + "\nx = 1\ny = 2\n```\n");
            float line = two - one, bottom = one - 20f - line;
            check($"A4Content/code panel bottom padding matches the 20 DIP top [{(lang == "" ? "plain" : lang)}] " + dark,
                one > 0f && MathF.Abs(bottom - 20f) < 0.5f, $"one={one:0.##} line={line:0.##} bottom={bottom:0.##}");
        }
    }

    // E4/E5/E6:代码块 r6、上内边距 20 DIP 且底板不压上一块;语言标签常显;复制按钮仅在悬停/选区头在块内/触控时出现,
    // 点击返回原文并显示“已复制”,指针离开复原;打印不画标签与按钮。
    private static void CodeBlockChrome(Action<string, bool, string> check, bool dark)
    {
        uint surface = Colorref(dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL), canvas = Canvas(dark);
        const string source = "Intro paragraph with a few words.\n\n```python\nprint(1)\n```\n\nMiddle paragraph.\n\n```\nplain\n```\n";
        RenderResources.Scale = 1f;
        int width = 1000;
        for (int w = 1000; w < 1004; w++)
        {
            using var probe = new DocView();
            probe.Load(source); probe.OnWidth(w);
            probe.Paint(new RECT { R = 10, B = 10 }, 0, 0f, 10f, Top);
            if (probe.CodeChromeForTest(0, out var pb, out _, out _, out _, out _, out _) && MathF.Abs(pb.X - MathF.Round(pb.X)) < 0.01f) { width = w; break; }
        }
        using var frame = new OffscreenFrame(width, 400);
        if (!frame.Ok) { check("A4Content/code offscreen target " + dark, false, "GDI"); return; }
        using var doc = new DocView();
        doc.Load(source); doc.OnWidth(width);
        frame.Paint(doc, 0f);
        if (!doc.CodeChromeForTest(0, out var box, out var chip, out var label, out float textTop, out float previousBottom, out int start)
            || !doc.CodeChromeForTest(1, out var box2, out var chip2, out var label2, out _, out _, out _))
        { check("A4Content/code chrome geometry " + dark, false, "no code block"); return; }
        check("A4Content/code text sits 20 DIP below the panel top " + dark, MathF.Abs(textTop - box.Y - 20f) < 0.01f, $"outset={textTop - box.Y:0.##}");
        check("A4Content/code panel does not overlap the previous block " + dark, box.Y >= previousBottom - 0.01f, $"panelTop={box.Y:0.##} previousBottom={previousBottom:0.##}");
        // 相邻两个代码块:底板之间至少 0.5 行高(16 时 14 DIP),不连成一片。
        using (var pair = new DocView())
        {
            pair.Load("```python\na = 1\n```\n\n```\nb = 2\n```\n");
            pair.OnWidth(width);
            frame.Paint(pair, 0f);
            if (pair.CodeChromeForTest(0, out var first, out _, out _, out _, out _, out _) && pair.CodeChromeForTest(1, out var second, out _, out _, out _, out _, out _))
            {
                float gap = second.Y - (first.Y + first.H), want = 0.5f * RenderResources.LineH(RenderResources.BodySize);
                check("A4Content/adjacent code panels keep half a line apart " + dark, gap >= want - 0.01f, $"gap={gap:0.##} want={want:0.##}");
            }
            else check("A4Content/adjacent code panels keep half a line apart " + dark, false, "geometry");
        }
        // 圆角:把面板顶缘滚到整像素,取 (L+1,T+2) 与 (L+2,T+1):r6 时落在圆内(近 Surface),r8 时在圆外(近画布)。
        float scroll = box.Y + Top - MathF.Floor(box.Y + Top);
        frame.Paint(doc, scroll);
        int L = (int)MathF.Round(box.X), T = (int)MathF.Round(box.Y + Top - scroll);
        static int Dist(uint a, uint b) => Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        uint c1 = frame.At(L + 1, T + 2), c2 = frame.At(L + 2, T + 1);
        check("A4Content/code panel has a 6 DIP corner radius " + dark, Dist(c1, surface) < Dist(c1, canvas) && Dist(c2, surface) < Dist(c2, canvas), $"L={L} T={T} c1={c1:X6} c2={c2:X6}");
        int NonSurface(OverlayBox b, float dy)
        {
            int n = 0;
            for (int y = (int)MathF.Ceiling(b.Y + dy) + 1; y < (int)MathF.Floor(b.Y + b.H + dy) - 1; y++)
                for (int x = (int)MathF.Ceiling(b.X) + 1; x < (int)MathF.Floor(b.X + b.W) - 1; x++) if (frame.At(x, y) != surface) n++;
            return n;
        }
        frame.Paint(doc, 0f);
        check("A4Content/language label is drawn in the top-right band " + dark, label.W > 0f && NonSurface(label, Top) > 10 && label.X + label.W <= chip.X - 5.9f, $"label={label} ink={NonSurface(label, Top)}");
        check("A4Content/code without a language has no label " + dark, label2.W == 0f, $"label2={label2}");
        check("A4Content/copy button is hidden at rest " + dark, NonSurface(chip, Top) == 0 && NonSurface(chip2, Top) == 0, $"ink={NonSurface(chip, Top)}");
        float cx = box.X + box.W / 2f, cy = box.Y + box.H / 2f;
        bool changed = doc.SetCodeHover(cx, cy);
        frame.Paint(doc, 0f);
        check("A4Content/hover shows the copy button on that block only " + dark, changed && NonSurface(chip, Top) > 30 && NonSurface(chip2, Top) == 0, $"ink={NonSurface(chip, Top)} other={NonSurface(chip2, Top)}");
        string miss = doc.CopyChipAt(cx, cy);
        string copied = doc.CopyChipAt(chip.X + chip.W / 2f, chip.Y + chip.H / 2f);
        check("A4Content/copy button returns the block source " + dark, miss == null && copied?.TrimEnd('\n') == "print(1)" && doc.CodeCopiedBlock >= 0, $"miss={miss ?? "null"} copied={copied ?? "null"}");
        doc.SetCodeHover(5f, 5f);
        frame.Paint(doc, 0f);
        check("A4Content/leaving the block hides the button and resets copied " + dark, doc.CodeCopiedBlock == -1 && NonSurface(chip, Top) == 0
            && doc.CopyChipAt(chip.X + chip.W / 2f, chip.Y + chip.H / 2f) == null, $"copied={doc.CodeCopiedBlock}");
        doc.SelectRange(start, start + 3);
        frame.Paint(doc, 0f);
        check("A4Content/selection inside the block shows the button " + dark, NonSurface(chip, Top) > 30, $"ink={NonSurface(chip, Top)}");
        doc.SelectRange(0, 3);
        doc.CodeCopyAlways = true;
        frame.Paint(doc, 0f);
        check("A4Content/touch mode shows every copy button " + dark, NonSurface(chip, Top) > 30 && NonSurface(chip2, Top) > 30, $"ink={NonSurface(chip, Top)},{NonSurface(chip2, Top)}");
        doc.WrapCodeForPaper = true;
        frame.Paint(doc, 0f);
        check("A4Content/paper rendering draws no label or button " + dark, NonSurface(chip, Top) == 0 && NonSurface(label, Top) == 0, $"chip={NonSurface(chip, Top)} label={NonSurface(label, Top)}");
    }

    // E7 链接:文字与下划线用 Accent;落在选区/作者高亮底上时退回 Ink(暗色 Accent 在这些底上 <4.5:1),离开后复原。
    // 同一句话分别以普通文字与链接渲染,比较“接近 Accent”的像素数,抵消 ClearType 彩边的干扰。
    private static void LinkColor(Action<string, bool, string> check, bool dark)
    {
        RenderResources.Scale = 1f;
        uint accent = Accent(dark);
        static int Dist(uint a, uint b) => Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        using var frame = new OffscreenFrame(700, 160);
        if (!frame.Ok) { check("A4Content/link offscreen target " + dark, false, "GDI"); return; }
        int AccentInk(string source, int selectTo)
        {
            using var doc = new DocView();
            doc.Load(source); doc.OnWidth(700);
            if (selectTo > 0) doc.SelectRange(0, selectTo);
            frame.Paint(doc, 0f);
            int n = 0;
            for (int y = 0; y < frame.H; y++) for (int x = 0; x < frame.W; x++) if (Dist(frame.At(x, y), accent) < 40) n++;
            return n;
        }
        const string plain = "Some words and a linked phrase here.\n";
        const string link = "Some words and [a linked phrase here](https://example.com/x).\n";
        int basePlain = AccentInk(plain, 0), baseLink = AccentInk(link, 0);
        check("A4Content/link text is drawn in Accent " + dark, baseLink >= basePlain + 40, $"link={baseLink} plain={basePlain}");
        int selPlain = AccentInk(plain, 60), selLink = AccentInk(link, 60);
        check("A4Content/selected link text falls back to Ink " + dark, selLink <= selPlain + 10, $"link={selLink} plain={selPlain}");
        int markPlain = AccentInk("Some words and ==a linked phrase here==.\n", 0), markLink = AccentInk("Some words and ==[a linked phrase here](https://example.com/x)==.\n", 0);
        check("A4Content/highlighted link text falls back to Ink " + dark, markLink <= markPlain + 10, $"link={markLink} plain={markPlain}");
        using (var doc = new DocView())
        {
            doc.Load(link); doc.OnWidth(700);
            doc.SelectRange(0, 60); frame.Paint(doc, 0f);
            doc.SelectRange(0, 0); frame.Paint(doc, 0f);
            int n = 0;
            for (int y = 0; y < frame.H; y++) for (int x = 0; x < frame.W; x++) if (Dist(frame.At(x, y), accent) < 40) n++;
            check("A4Content/link returns to Accent after the selection clears " + dark, n >= basePlain + 40, $"after={n} plain={basePlain}");
        }
    }


    // S83 第 3 项:代码/流程图底板原先左侧外扩 22、右缘缩进列内 20,整块相对正文列左移约 21。
    // 现在两侧等量外扩(代码文字仍与正文左右对齐);窄窗时两侧都收在离窗口边 4 DIP 以内。
    private static void CodePanelSides(Action<string, bool, string> check)
    {
        const string source = "Intro paragraph.\n\n```python\nprint(1)\n```\n\n```mermaid\nflowchart LR\nA[Start] --> B[End]\n```\n";
        foreach (float w in new[] { 1000f, 600f, 380f })
        {
            using var doc = new DocView();
            doc.Load(source); doc.OnWidth(w);
            doc.Paint(new RECT { R = 10, B = 10 }, 0, 0f, 10f, Top);
            for (int i = 0; i <= 1; i++)   // CodeChromeForTest 按代码块计数:0 = python,1 = mermaid
            {
                string kind = i == 0 ? "code" : "mermaid";
                if (!doc.CodeChromeForTest(i, out var b, out _, out _, out _, out _, out _)) { check($"A4Content/{kind} panel bleeds equally on both sides V={w}", false, "no panel"); continue; }
                float left = doc.ColX - b.X, right = b.X + b.W - (doc.ColX + doc.ColW);
                check($"A4Content/{kind} panel bleeds equally on both sides V={w}", MathF.Abs(left - right) < 0.5f && left > 0f && b.X >= 3.99f && b.X + b.W <= w - 3.99f,
                    $"left={left:0.#} right={right:0.#} x={b.X:0.#} r={b.X + b.W:0.#} col={doc.ColX:0.#}+{doc.ColW:0.#}");
            }
        }
    }


    // S86:流程图底板上下留白相等 —— 底板上缘到图内容(方框、线、标签)顶的距离 = 内容底到底板下缘。
    // 原来 H = Extent.h + 24,Extent 已含下方 Margin,底部比顶部多 16。
    private static void MermaidPanelBalance(Action<string, bool, string> check)
    {
        RenderResources.Scale = 1f;
        using var frame = new OffscreenFrame(1000, 600);
        if (!frame.Ok) { check("A4Content/mermaid panel balance offscreen target", false, "GDI"); return; }
        var cases = new[]
        {
            ("LR", "flowchart LR\nA[Start] --> B[End]\n"),
            ("LR pair", "flowchart LR\nA[开始] --> B{检查}\nB -->|失败| A\nB -->|通过| C[完成]\n"),
            ("TD", "flowchart TD\nA[开发] --> B[测试]\nB --> C[发布]\n"),
        };
        foreach (var (name, graph) in cases)
        {
            using var doc = new DocView();
            doc.Load("Intro.\n\n```mermaid\n" + graph + "```\n"); doc.OnWidth(1000f);
            frame.Paint(doc, 0f);
            if (!doc.CodeChromeForTest(0, out var box, out _, out _, out _, out _, out _) || !MermaidLite.TryParse(graph, out var g))
            { check($"A4Content/mermaid panel top and bottom padding match [{name}]", false, "no panel"); continue; }
            float top = float.MaxValue, bottom = 0f;
            foreach (var node in g.Nodes) { top = MathF.Min(top, node.Y); bottom = MathF.Max(bottom, node.Y + node.H); }
            foreach (var edge in g.Edges)
            {
                if (edge.Path == null) continue;
                foreach (var (_, y) in edge.Path) { top = MathF.Min(top, y); bottom = MathF.Max(bottom, y); }
                if (!string.IsNullOrEmpty(edge.Label) && edge.Path.Count >= 2)
                {
                    var (_, ly, _, lh) = MermaidLite.LabelBox(g, edge);
                    top = MathF.Min(top, ly); bottom = MathF.Max(bottom, ly + lh);
                }
            }
            float above = 20f + top, below = box.H - 20f - bottom;   // 块顶在底板上缘下 20(CodeTopOutset)
            check($"A4Content/mermaid panel top and bottom padding match [{name}]", MathF.Abs(above - below) < 0.5f,
                $"above={above:0.#} below={below:0.#} panelH={box.H:0.#} content={top:0.#}..{bottom:0.#}");
        }
    }


    // S88:流程图结构退后、内容在前 —— 线与箭头用 Muted、方框描边用 ControlBorder、边标签不用注释绿;
    // 比底板窄的图在底板里水平居中(与公式块一致)。改前:线与框为墨色、标签为注释绿、图贴左。
    private static void MermaidStyle(Action<string, bool, string> check)
    {
        RenderResources.Scale = 1f; RenderResources.ApplyTheme(false);
        using var frame = new OffscreenFrame(1000, 600);
        if (!frame.Ok) { check("A4Content/mermaid style offscreen target", false, "GDI"); return; }
        const string graph = "flowchart LR\nA[开始] -->|通过| B[完成]\n";
        using var doc = new DocView();
        doc.Load("Intro.\n\n```mermaid\n" + graph + "```\n"); doc.OnWidth(1000f);
        frame.Paint(doc, 0f);
        if (!doc.CodeChromeForTest(0, out var box, out _, out _, out _, out _, out _) || !MermaidLite.TryParse(graph, out var g) || g.Nodes.Count < 2 || g.Edges.Count < 1)
        { check("A4Content/mermaid style panel", false, "no panel"); return; }
        static int Sum(uint c) => (int)((c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF));
        float panelTop = box.Y + OffscreenFrame.Top;   // 底板坐标不含离屏画面顶部的工具栏带
        uint surface = frame.At((int)(box.X + 6f), (int)(panelTop + box.H - 4f));
        var a = g.Nodes[0]; var b = g.Nodes[1];
        int top = (int)MathF.Round(panelTop + 20f);   // 块顶在底板上缘下 20(CodeTopOutset)
        int row = top + (int)MathF.Round(a.Y + a.H / 2f);
        int first = -1, last = -1;
        for (int x = (int)box.X + 2; x < (int)(box.X + box.W) - 2; x++)
            if (Sum(surface) - Sum(frame.At(x, row)) > 60) { if (first < 0) first = x; last = x; }
        if (first < 0) { check("A4Content/mermaid style ink", false, $"no ink on row {row}"); return; }
        float ox = first - a.X;   // 图原点 = A 的左边框 - A.X
        int ink = Sum(Colorref(ThemePalette.InkL));
        int edgeMin = 999;
        for (int x = (int)(ox + a.X + a.W + 8f); x < (int)(ox + b.X - 14f); x++)
            for (int y = row - 1; y <= row + 1; y++) edgeMin = Math.Min(edgeMin, Sum(frame.At(x, y)));
        check("A4Content/mermaid edges are lighter than ink", edgeMin >= (ink + Sum(surface)) / 2 && edgeMin <= Sum(surface) - 100, $"darkest={edgeMin} half-ink={(ink + Sum(surface)) / 2} surface={Sum(surface)} (antialiased 1.2 Muted ≈ 466, 1.4 ink ≈ 271)");
        int borderMin = 999;
        for (int x = first - 1; x <= first + 1; x++)
            for (int y = top + (int)(a.Y + 8f); y < top + (int)(a.Y + a.H - 8f); y++) borderMin = Math.Min(borderMin, Sum(frame.At(x, y)));
        check("A4Content/mermaid box borders are lighter than ink", borderMin >= (ink + Sum(surface)) / 2 && borderMin <= Sum(surface) - 100, $"darkest={borderMin} half-ink={(ink + Sum(surface)) / 2} surface={Sum(surface)} (1px ControlBorder ≈ 547, 1.2 ink ≈ 333)");
        var (lx, ly, lw, lh) = MermaidLite.LabelBox(g, g.Edges[0]);
        uint darkest = surface;
        for (int x = (int)(ox + lx); x < (int)(ox + lx + lw); x++)
            for (int y = top + (int)ly; y < top + (int)(ly + lh) - 3; y++) { uint c = frame.At(x, y); if (Sum(c) < Sum(darkest)) darkest = c; }   // 下缘 3 px 让给线
        int r = (int)(darkest & 0xFF), gr = (int)((darkest >> 8) & 0xFF);
        check("A4Content/mermaid edge label is muted, not comment green", Sum(darkest) < Sum(surface) - 120 && gr <= r + 6, $"darkest={darkest:X6} r={r} g={gr}");
        float left = first - box.X, right = box.X + box.W - 1f - last;
        check("A4Content/small mermaid diagram is centred in its panel", MathF.Abs(left - right) <= 3f, $"left={left:0.#} right={right:0.#} panel={box.X:0.#}+{box.W:0.#}");
        // S88 续:菱形与方框同样填 Canvas,不透出底板色
        const string dg = "flowchart LR\nA{是}\n";
        using var dd = new DocView();
        dd.Load("Intro.\n\n```mermaid\n" + dg + "```\n"); dd.OnWidth(1000f);
        frame.Paint(dd, 0f);
        if (!dd.CodeChromeForTest(0, out var db, out _, out _, out _, out _, out _) || !MermaidLite.TryParse(dg, out var g2) || g2.Nodes.Count < 1)
        { check("A4Content/mermaid diamond panel", false, "no panel"); return; }
        var dn = g2.Nodes[0];
        int drow = (int)MathF.Round(db.Y + OffscreenFrame.Top + 20f + dn.Y + dn.H / 2f);
        int dfirst = -1;
        for (int x = (int)db.X + 2; x < (int)(db.X + db.W) - 2 && dfirst < 0; x++)
            if (Sum(surface) - Sum(frame.At(x, drow)) > 60) dfirst = x;
        uint inside = frame.At(dfirst + (int)(dn.W * 0.2f), drow);
        int canvas = Sum(Colorref(ThemePalette.CanvasL));
        check("A4Content/mermaid diamond is filled like boxes", dfirst > 0 && Math.Abs(Sum(inside) - canvas) <= 6, $"tip={dfirst} inside={inside:X6} canvas={canvas} surface={Sum(surface)} w={dn.W:0.#}");
    }
    // S94:裸链接遇到汉字/全角字符即止(产品以中文阅读为先;GFM 原规则一直延续到空白)。
    // 末尾的半角标点仍按原规则剥掉,所以 "ref,且" 取 "ref"。%编码的中文网址不受影响。
    private static void BareUrlCjk(Action<string, bool, string> check)
    {
        static string Url(string md) => ((Para)MdParser.Parse(md)[0]).Inlines.FirstOrDefault(r => r.Link != null)?.Text ?? "(none)";
        string a = Url("访问https://example.com/a获取更多\n");
        string b = Url("指向 https://example.com/ref,且 \"标题\"\n");
        string c = Url("见 https://example.com/p?q=1&x=2 。\n");
        string d = Url("见 https://zh.wikipedia.org/wiki/%E4%B8%AD%E6%96%87 条目\n");
        string e = Url("主页www.example.com欢迎\n");
        check("A4Content/bare url stops at CJK text", a == "https://example.com/a" && b == "https://example.com/ref" && e == "www.example.com", $"a={a} b={b} e={e}");
        check("A4Content/bare url keeps ascii and percent-encoded paths", c == "https://example.com/p?q=1&x=2" && d == "https://zh.wikipedia.org/wiki/%E4%B8%AD%E6%96%87", $"c={c} d={d}");
    }

    // S90:脚注往返跳转与未定义引用。标签按引用定义规则折叠大小写;回跳按显示文本 "^id" 定位。
    // S92:流程图里的查找命中要看得见——在命中的节点/边标签文字下画 Hit 底色(原先计数 1/1 却无任何高亮)
    private static void MermaidSearch(Action<string, bool, string> check)
    {
        RenderResources.Scale = 1f; RenderResources.ApplyTheme(false);
        using var frame = new OffscreenFrame(800, 400);
        if (!frame.Ok) { check("A4Content/mermaid search offscreen target", false, "GDI"); return; }
        using var doc = new DocView();
        doc.Load("Intro.\n\n```mermaid\nflowchart LR\nA[开始] --> B[完成]\n```\n"); doc.OnWidth(800f);
        frame.Paint(doc, 0f);
        var before = new uint[800 * 400];
        for (int y = 0; y < 400; y++) for (int x = 0; x < 800; x++) before[y * 800 + x] = frame.At(x, y);
        doc.SetSearchQuery("开始", 0, false);
        frame.Paint(doc, 0f);
        int diff = 0, minX = 9999, maxX = -1;
        for (int y = 0; y < 400; y++) for (int x = 0; x < 800; x++)
            if (frame.At(x, y) != before[y * 800 + x]) { diff++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
        // 两个节点等宽并排:变化只应出现在左边的“开始”,不应波及右边的“完成”
        check("A4Content/mermaid search hit is highlighted on the node label", doc.SearchHits.Count == 1 && diff > 20 && maxX - minX < 120, $"hits={doc.SearchHits.Count} diff={diff} x={minX}..{maxX}");
    }

    // S91:字面 HTML 底板与代码块同为 6 DIP 圆角(原为直角矩形)
    private static void HtmlPanelCorner(Action<string, bool, string> check)
    {
        RenderResources.Scale = 1f; RenderResources.ApplyTheme(false);
        using var frame = new OffscreenFrame(800, 400);
        if (!frame.Ok) { check("A4Content/html panel offscreen target", false, "GDI"); return; }
        using var doc = new DocView();
        doc.Load("Intro.\n\n<iframe src=\"about:blank\">\n</iframe>\n"); doc.OnWidth(800f);
        frame.Paint(doc, 0f);
        static int Sum(uint c) => (int)((c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF));
        uint surface = Colorref(ThemePalette.SurfaceL);
        int top = -1, mid = 400, left = -1;
        for (int y = (int)OffscreenFrame.Top; y < 380 && top < 0; y++) if (frame.At(mid, y) == surface) top = y;
        if (top < 0) { check("A4Content/html panel found", false, "no surface"); return; }
        for (int x = 0; x < mid && left < 0; x++) if (frame.At(x, top + 10) == surface) left = x;
        uint corner = frame.At(left, top);
        check("A4Content/html panel has rounded corners like code", left >= 0 && Sum(corner) >= Sum(surface) + 20, $"left={left} top={top} corner={corner:X6} surface={Sum(surface)}");
    }

    private static void Footnotes(Action<string, bool, string> check)
    {
        const string md = "正文[^1]与[^Note]和[^x]。\n\n中段[^12]。\n\n[^1]: 一\n[^note]: 二\n[^12]: 十二\n";
        var runs = ((Para)MdParser.Parse(md)[0]).Inlines;
        var refs = runs.Where(r => r.Fn).ToList();
        string flat = string.Concat(runs.Select(r => r.Text));
        check("A4Content/undefined footnote reference stays literal", refs.Count == 2 && flat.Contains("[^x]"), $"refs={refs.Count} text={flat}");
        using var doc = new DocView();
        doc.Load(md); doc.OnWidth(800f);
        foreach (var r in refs)
        {
            string id = r.Link[4..];
            bool fwd = doc.TryFootnoteY(id, out float fy);
            bool back = doc.TryRefBackY(id, out float ry);
            check("A4Content/footnote " + r.Text + " jumps both ways", fwd && back && ry < fy, $"link={r.Link} fwd={fwd}@{fy:0} back={back}@{ry:0}");
        }
        bool b12 = doc.TryRefBackY("12", out float y12), b1 = doc.TryRefBackY("1", out float y1);
        check("A4Content/footnote back-jump matches whole id", b12 && b1 && y12 > y1, $"^1@{y1:0} ^12@{y12:0}");
    }


    private static uint Colorref(uint rgb) => OffscreenFrame.Colorref(rgb);
}

