using System;
using System.Runtime.InteropServices;
using MDViewerNative;

// A3 目录与查找:覆盖层几何纯函数 + 展开目录离屏像素契约(不显示窗口、不注入输入)。
internal static class A3OverlayTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        Geometry(check);
        FindGeometry(check);
        using (new RenderStateScope())
        {
            RenderResources.SetBodySize(16f);
            DirectoryModes(check);
            ExpandedDirectory(check);
            CollapsedRuler(check);
            FindSurface(check);
            CurrentHitOutline(check);
            FindButtons(check);
        }
    }

    private static void Geometry(Action<string, bool, string> check)
    {
        // D1:标尺只在左留白 ≥32 时出现,且从不压正文列;16 DIP 及以上字号等价于 V≥704。
        bool rulerNeverCovers = true, rulerThreshold = true;
        foreach (float size in new[] { 12f, 16f, 20f, 28f })
            for (float v = 480f; v <= 1600f; v += 1f)
            {
                float colX = WindowFit.ColX(v, WindowFit.ColW(v));
                if (OverlayGeometry.RulerShown(colX) && colX < OverlayGeometry.RulerWidth) rulerNeverCovers = false;
                if (OverlayGeometry.RulerShown(colX) != (WindowFit.PadFor(v) >= OverlayGeometry.RulerWidth)) rulerThreshold = false;
            }
        check("A3Overlay/ruler never covers the column", rulerNeverCovers, "RulerShown ⇒ colX ≥ 32");
        check("A3Overlay/ruler shows once the margin reaches 32 DIP", rulerThreshold, "S80: margin 12→20→48, ruler at P≥32");

        check("A3Overlay/toc width is min(240, V-48)",
            OverlayGeometry.TocWidth(1000f) == 240f && OverlayGeometry.TocWidth(288f) == 240f &&
            OverlayGeometry.TocWidth(280f) == 232f && OverlayGeometry.TocWidth(40f) == 0f, "§4.3");
        // S80:目录让位后正文至少 480 DIP 才常驻,留白仍为 48:V − 240 − 96 ≥ 480 ⇔ V ≥ 816。
        check("A3Overlay/toc pins only when the remaining column stays readable",
            !OverlayGeometry.TocPinned(815f) && OverlayGeometry.TocPinned(816f), $"col(815)={WindowFit.ColW(815f, 240f)} col(816)={WindowFit.ColW(816f, 240f)}");

        check("A3Overlay/find layout by width and mode",
            OverlayGeometry.Find(false, false, false, 1000f) == FindLayout.Hidden &&
            OverlayGeometry.Find(true, false, false, 720f) == FindLayout.Panel &&
            OverlayGeometry.Find(true, false, false, 719f) == FindLayout.Strip &&
            OverlayGeometry.Find(true, false, false, 576f) == FindLayout.Strip &&
            OverlayGeometry.Find(true, false, false, 575f) == FindLayout.StripTwoRows &&
            OverlayGeometry.Find(true, true, false, 1000f) == FindLayout.Editing &&
            OverlayGeometry.Find(true, true, true, 500f) == FindLayout.EditingReplace, "D4 / §4.4");
        bool oneRowInput = true;
        for (float v = 480f; v <= 719f; v += 1f)
            if (OverlayGeometry.StripFitsOneRow(v))
                oneRowInput &= v - 12f - 244f - OverlayGeometry.StripCountWidth(v) - 64f - 16f >= 180f;
        check("A3Overlay/one-row strip keeps a 180 DIP input", oneRowInput, "D4");

        var panel = OverlayGeometry.FindBox(FindLayout.Panel, 1000f, 36f);
        check("A3Overlay/panel box is 440x44 at top right", panel == new OverlayBox(548f, 40f, 440f, 44f), panel.ToString());
        check("A3Overlay/panel does not push content", OverlayGeometry.FindHeight(FindLayout.Panel) == 0f, "floating");
        var strip = OverlayGeometry.FindBox(FindLayout.StripTwoRows, 500f, 36f);
        check("A3Overlay/narrow strip spans the view", strip == new OverlayBox(0f, 36f, 500f, 80f), strip.ToString());
        check("A3Overlay/panel box contains its gaps",
            panel.Contains(548f, 40f) && panel.Contains(700f, 60f) && !panel.Contains(988f, 60f) && !panel.Contains(700f, 84f), "hit rect");

        check("A3Overlay/wide panel leaves directory at content top", OverlayGeometry.TocTop(36f, panel, 240f) == 36f, "D3");
        check("A3Overlay/strip pushes directory below it", OverlayGeometry.TocTop(36f, strip, 240f) == 116f, "D3");
        check("A3Overlay/no find keeps directory at content top", OverlayGeometry.TocTop(36f, default, 240f) == 36f, "D3");
    }

    private static void DirectoryModes(Action<string, bool, string> check)
    {
        using var doc = new DocView();
        doc.Load("# One\n\nText.\n\n## Two\n\nMore.\n");
        doc.SetToc(true);
        doc.OnWidth(1152f);
        bool widePinned = doc.TocPinned && doc.TocVisible && doc.TocWidth == 240f;
        doc.OnWidth(815f);
        bool narrowTransient = !doc.TocPinned && doc.TocVisible;
        doc.OnWidth(300f);
        bool smallOverlay = doc.TocVisible && doc.TocWidth == 240f && !doc.TocPinned;
        doc.OnWidth(40f);
        bool tooSmall = !doc.TocVisible && doc.TocWidth == 0f;
        check("A3Overlay/wide directory is pinned", widePinned, "S80: column beside it ≥ 480 at V=1152");
        check("A3Overlay/covering directory is transient", narrowTransient, "S80: column beside it < 480 at V=815");
        check("A3Overlay/small window keeps a 240 DIP overlay", smallOverlay, "min(240, V-48)");
        check("A3Overlay/directory unavailable without context", tooSmall, "V-48 <= 0");
        doc.SetToc(false);
        doc.OnWidth(1000f);
        bool rulerWide = doc.RulerVisible;
        doc.SetToc(true);
        bool rulerHiddenByToc = !doc.RulerVisible;
        doc.SetToc(false);
        doc.OnWidth(660f);
        bool rulerNarrow = !doc.RulerVisible;
        check("A3Overlay/ruler shows in a wide margin", rulerWide, "D1");
        check("A3Overlay/expanded directory replaces the ruler", rulerHiddenByToc, "same left region");
        check("A3Overlay/ruler hides when it would cover text", rulerNarrow, "colX 31.25 < 32");
        using (var empty = new DocView()) { empty.OnWidth(1000f); check("A3Overlay/no ruler on the empty start page", !empty.RulerVisible, "no document"); }
        float t0 = OverlayGeometry.RulerTickY(0f, 1000f, 36f, 364f), t1 = OverlayGeometry.RulerTickY(1000f, 1000f, 36f, 364f), tm = OverlayGeometry.RulerTickY(500f, 1000f, 36f, 364f);
        check("A3Overlay/ruler ticks are proportional inside the band", t0 == 44f && t1 == 392f && tm == 218f && OverlayGeometry.RulerTickY(-5f, 1000f, 36f, 364f) == 44f && OverlayGeometry.RulerTickY(5f, 0f, 36f, 364f) == 44f, $"{t0}/{tm}/{t1}");
        // 自用走查:几百个二级标题的更新日志把标尺刻成"条形码"(每 3 DIP 一条),违背 §4.4"章节刻度低调"。
        // 一、二级按最小间距 8 DIP 放不下时只画一级;一级也放不下时按 8 DIP 抽稀。当前章 Accent 标记不受影响。
        check("A3Overlay/ruler keeps two levels when they fit", OverlayGeometry.RulerDepth(12, 700f) == 2 && OverlayGeometry.RulerMinGap(12, 700f) == 3f, $"{OverlayGeometry.RulerDepth(12, 700f)}");
        check("A3Overlay/ruler drops to chapters when crowded", OverlayGeometry.RulerDepth(277, 700f) == 1, $"{OverlayGeometry.RulerDepth(277, 700f)}");
        check("A3Overlay/ruler thins crowded chapters", OverlayGeometry.RulerMinGap(200, 700f) == OverlayGeometry.RulerMinPitch && OverlayGeometry.RulerMinPitch == 8f, $"{OverlayGeometry.RulerMinGap(200, 700f)}");
        var editing = new CmdState(true, false, false, false, false, 16f);
        var narrow = new CmdState(false, false, false, false, false, 16f, TocAvailable: false);
        var reading = new CmdState(false, false, false, false, false, 16f);
        check("A3Overlay/disabled directory explains editing", Commands.DisabledReason(1006, editing) == "编辑时不可用", "B1 edit-mode reason");
        check("A3Overlay/disabled directory explains width", Commands.DisabledReason(1006, narrow) == "窗口太窄", "B1 width reason");
        check("A3Overlay/enabled directory has no reason", Commands.DisabledReason(1006, reading) == null && Commands.DisabledReason(1005, editing) == null, "only greyed commands explain");
    }

    private const float Far = 5000f;   // 正文滚出视口,画面只剩目录,像素判断不受正文干扰

    private static void ExpandedDirectory(Action<string, bool, string> check)
    {
        const int width = 1000, height = 400;
        const float top = 36f;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            check("A3Overlay/offscreen target created", screen != 0 && memory != 0 && bitmap != 0, "test-owned memory bitmap");
            if (screen == 0 || memory == 0 || bitmap == 0) return;
            var rect = new RECT { R = width, B = height };
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                using (var doc = new DocView())
                {
                    doc.Load("# Start\n\n## A very long heading title that certainly needs trimming at the directory edge\n\nBody.\n");
                    doc.OnWidth(width);
                    doc.SetToc(true);
                    doc.Paint(rect, memory, Far, height - top, top);
                    uint canvas = GetPixel(memory, 120, 300);
                    bool noRule = true;
                    for (int y = 150; y < height; y += 10)
                        foreach (int x in new[] { 218, 219, 220, 238, 239 })
                            noRule &= GetPixel(memory, x, y) == canvas;
                    check("A3Overlay/expanded directory has no divider " + dark, noRule, "§4.3: opaque canvas, no vertical rule");
                    bool reaches = false;
                    for (int y = 70; y <= 94; y++)
                        for (int x = 221; x <= 233; x++)
                            reaches |= GetPixel(memory, x, y) != canvas;
                    check("A3Overlay/directory entries use the 240 DIP width " + dark, reaches, "trimmed title reaches past 220");
                    bool clipped = true;
                    for (int y = 44; y <= 94; y++)
                        for (int x = 240; x <= 300; x++)
                            clipped &= GetPixel(memory, x, y) == canvas;
                    check("A3Overlay/directory text stays inside 240 DIP " + dark, clipped, "ellipsis instead of overflow");
                    string full = doc.TocHint(75f, top, out float rowTop);
                    check("A3Overlay/trimmed directory entry offers its full title " + dark, full == doc.TocEntries[1].text && rowTop == 70f, $"row {rowTop}");
                    check("A3Overlay/untrimmed entry has no hint " + dark, doc.TocHint(50f, top, out _) == null, "only trimmed titles");
                }
                using (var doc = new DocView())
                {
                    doc.Load("Just a paragraph without headings.\n");
                    doc.OnWidth(width);
                    doc.SetToc(true);
                    doc.Paint(rect, memory, Far, height - top, top);
                    uint canvas = GetPixel(memory, 120, 300);
                    bool message = false;
                    for (int y = 44; y <= 68; y++)
                        for (int x = 8; x <= 170; x++)
                            message |= GetPixel(memory, x, y) != canvas;
                    check("A3Overlay/headingless document explains the empty directory " + dark, message, "本文没有标题");
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

    private static bool Disjoint(OverlayBox[] boxes)
    {
        for (int a = 0; a < boxes.Length; a++)
            for (int b = a + 1; b < boxes.Length; b++)
            {
                OverlayBox p = boxes[a], q = boxes[b];
                if (p.IsEmpty || q.IsEmpty) continue;
                if (p.X < q.Right && q.X < p.Right && p.Y < q.Bottom && q.Y < p.Bottom) return false;
            }
        return true;
    }

    private static bool Inside(OverlayBox outer, OverlayBox[] boxes)
    {
        foreach (var b in boxes)
            if (!b.IsEmpty && (b.X < outer.X || b.Y < outer.Y || b.Right > outer.Right + 0.01f || b.Bottom > outer.Bottom + 0.01f)) return false;
        return true;
    }

    private static void FindGeometry(Action<string, bool, string> check)
    {
        const float tool = 36f;
        bool panelOk = true, stripOk = true, twoRowsOk = true;
        string panelWhy = "", stripWhy = "", twoWhy = "";
        for (float v = 720f; v <= 1600f; v += 1f)
        {
            var c = OverlayGeometry.FindControls(FindLayout.Panel, v, tool);
            var box = OverlayGeometry.FindBox(FindLayout.Panel, v, tool);
            bool ok = c.Label.IsEmpty && Inside(box, c.All) && Disjoint(c.All) && c.Input.W >= 140f;
            if (!ok && panelOk) panelWhy = $"V={v} input={c.Input.W}";
            panelOk &= ok;
        }
        for (float v = 576f; v < 720f; v += 1f)
        {
            var c = OverlayGeometry.FindControls(FindLayout.Strip, v, tool);
            var box = OverlayGeometry.FindBox(FindLayout.Strip, v, tool);
            bool ok = Inside(box, c.All) && Disjoint(c.All) && c.Input.W >= OverlayGeometry.StripInputMin;
            if (!ok && stripOk) stripWhy = $"V={v} input={c.Input.W}";
            stripOk &= ok;
        }
        for (float v = 460f; v < 576f; v += 1f)
        {
            var c = OverlayGeometry.FindControls(FindLayout.StripTwoRows, v, tool);
            var box = OverlayGeometry.FindBox(FindLayout.StripTwoRows, v, tool);
            bool ok = Inside(box, c.All) && Disjoint(c.All) && c.Input.W >= 170f && c.Count.Y == tool + 44f && c.Options.Y == tool + 44f;
            if (!ok && twoRowsOk) twoWhy = $"V={v} input={c.Input.W}";
            twoRowsOk &= ok;
        }
        check("A3Overlay/panel controls fit inside the panel", panelOk, "no label, >=140 DIP input " + panelWhy);
        check("A3Overlay/one-row strip controls fit", stripOk, ">=180 DIP input " + stripWhy);
        check("A3Overlay/two-row strip puts count and options on row two", twoRowsOk, "D4 " + twoWhy);
        var edit = OverlayGeometry.FindControls(FindLayout.EditingReplace, 900f, tool);
        check("A3Overlay/editing replace row unchanged", edit.Replace.Y == tool + 44f && edit.Input.X == 64f && edit.Prev.W == 60f
            && Inside(OverlayGeometry.FindBox(FindLayout.EditingReplace, 900f, tool), edit.All) && Disjoint(edit.All), "44/80 editing layout");
        bool compactOk = true; string compactWhy = "";
        for (float v = 460f; v < 576f; v += 1f)
            foreach (var mode in new[] { FindLayout.Editing, FindLayout.EditingReplace })
            {
                var c = OverlayGeometry.FindControls(mode, v, tool);
                bool replace = mode == FindLayout.EditingReplace;
                bool ok = c.Label.IsEmpty && c.ReplaceLabel.IsEmpty && Inside(OverlayGeometry.FindBox(mode, v, tool), c.All) && Disjoint(c.All)
                    && c.Input.W >= 160f && (!replace || (c.Replace.X == c.Input.X && c.Replace.W == c.Input.W && !c.ReplaceAll.IsEmpty));
                if (!ok && compactOk) compactWhy = $"{mode} V={v} input={c.Input.W}";
                compactOk &= ok;
            }
        check("A3Overlay/narrow editing find drops labels for a usable input", compactOk, ">=160 DIP, cue banners name the fields " + compactWhy);
        var roomy = OverlayGeometry.FindControls(FindLayout.EditingReplace, 600f, tool);
        check("A3Overlay/roomy editing find keeps its labels", !roomy.Label.IsEmpty && !roomy.ReplaceLabel.IsEmpty && roomy.Input.X == 64f, "V=600 unchanged");
        var panel = OverlayGeometry.FindBox(FindLayout.Panel, 1000f, tool);
        check("A3Overlay/reveal inset follows the actual overlay",
            OverlayGeometry.RevealInset(tool, panel) == 48f &&
            OverlayGeometry.RevealInset(tool, OverlayGeometry.FindBox(FindLayout.StripTwoRows, 500f, tool)) == 80f &&
            OverlayGeometry.RevealInset(tool, default) == 0f &&
            OverlayGeometry.RevealInset(tool + 44f, OverlayGeometry.FindBox(FindLayout.Editing, 900f, tool)) == 0f, "panel 48 / strip 80 / none / editing reserves");
    }

    private static void FindSurface(Action<string, bool, string> check)
    {
        const int width = 1000, height = 400;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0) { check("A3Overlay/find offscreen target", false, "GDI"); return; }
            var rect = new RECT { R = width, B = height };
            const uint sentinel = 0x00FF00FF;
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                uint surface = Colorref(dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL);
                uint border = Colorref(dark ? ThemePalette.ControlBorderD : ThemePalette.ControlBorderL);
                Fill(memory, width, height, sentinel);
                FindBar.PaintSurface(memory, rect, 1f, FindLayout.Panel, width);
                // 面板 548..988 × 40..84
                check("A3Overlay/panel uses Surface " + dark, GetPixel(memory, 700, 62) == surface, $"{GetPixel(memory, 700, 62):X6}");
                check("A3Overlay/panel has a ControlBorder edge " + dark, GetPixel(memory, 548, 62) == border && GetPixel(memory, 987, 62) == border
                    && GetPixel(memory, 700, 40) == border && GetPixel(memory, 700, 83) == border, "1 DIP");
                check("A3Overlay/panel paints nothing outside its box " + dark, GetPixel(memory, 547, 62) == sentinel && GetPixel(memory, 300, 50) == sentinel
                    && GetPixel(memory, 700, 85) == sentinel && GetPixel(memory, 989, 62) == sentinel, "floating, no shadow");
                Fill(memory, width, height, sentinel);
                FindBar.PaintSurface(memory, rect, 1f, FindLayout.StripTwoRows, 540f);
                uint canvas = Colorref(dark ? ThemePalette.CanvasD : ThemePalette.CanvasL);
                check("A3Overlay/two-row strip is 80 DIP of canvas " + dark, GetPixel(memory, 300, 60) == canvas && GetPixel(memory, 300, 110) == canvas
                    && GetPixel(memory, 300, 117) == sentinel && GetPixel(memory, 300, 30) == sentinel, "36..116");
                check("A3Overlay/strip ends with a hairline " + dark, GetPixel(memory, 300, 115) != canvas && GetPixel(memory, 300, 115) != sentinel, "bottom edge");
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

    private static void Fill(nint dc, int width, int height, uint color)
    {
        nint brush = CreateSolidBrush(color);
        var r = new RECT { R = width, B = height };
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref RECT rect, nint brush);

    private static uint Colorref(uint rgb) => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

    private static string LongDocument()
    {
        string para = string.Concat(System.Linq.Enumerable.Repeat("Reading text that wraps over several lines in the column. ", 8)) + "\n\n";
        var sb = new System.Text.StringBuilder();
        foreach (string h in new[] { "# One", "## One point one", "### Deep", "# Two", "## Two point one" })
        {
            sb.Append(h).Append("\n\n");
            for (int p = 0; p < 6; p++) sb.Append(para);
        }
        return sb.ToString();
    }

    private static void CollapsedRuler(Action<string, bool, string> check)
    {
        const int width = 1000, height = 400;
        const float top = 36f;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0) { check("A3Overlay/ruler offscreen target", false, "GDI"); return; }
            var rect = new RECT { R = width, B = height };
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                uint accent = Colorref(dark ? ThemePalette.AccentD : ThemePalette.AccentL);
                uint border = Colorref(dark ? ThemePalette.ControlBorderD : ThemePalette.ControlBorderL);   // S89:刻度比正文次要色再淡一档
                bool tickTone = true; string tickSeen = "";
                using (var doc = new DocView())
                {
                    doc.Load(LongDocument());
                    doc.OnWidth(width);
                    doc.Paint(rect, memory, 0f, height - top, top);
                    uint canvas = GetPixel(memory, 120, 300);
                    int runs = 0; bool inRun = false, accentSeen = false, beyond = true;
                    for (int y = (int)top + 2; y < height; y++)
                    {
                        bool ink = GetPixel(memory, 20, y) != canvas;
                        if (ink && !inRun) runs++;
                        inRun = ink;
                        for (int x = 8; x <= 24; x++) accentSeen |= GetPixel(memory, x, y) == accent;
                        { uint c20 = GetPixel(memory, 20, y); if (c20 != canvas && c20 != accent && c20 != border) { tickTone = false; tickSeen = c20.ToString("X6"); } }
                        for (int x = 26; x < 32; x++) beyond &= GetPixel(memory, x, y) == canvas;
                    }
                    check("A3Overlay/collapsed ruler marks H1/H2 chapters " + dark, runs == 4, $"runs={runs} (H3 has no tick)");
                    check("A3Overlay/ruler current chapter uses Accent " + dark, accentSeen, "D5");
                    check("A3Overlay/ruler ticks use the control border tone " + dark, tickTone, "non-current ticks = ControlBorder, quieter than Muted text; seen " + (tickSeen == "" ? "ok" : tickSeen));
                    check("A3Overlay/ruler stays inside its 32 DIP slot " + dark, beyond, "x 26-31 canvas");
                }
                using (var doc = new DocView())
                {
                    doc.Load("Plain reading text without headings.\n");
                    doc.OnWidth(width);
                    doc.Paint(rect, memory, 0f, height - top, top);
                    uint canvas = GetPixel(memory, 120, 300);
                    bool entry = false;
                    for (int y = (int)top + 6; y <= (int)top + 30; y++)
                        for (int x = 8; x <= 24; x++) entry |= GetPixel(memory, x, y) != canvas;
                    check("A3Overlay/headingless ruler shows an entry " + dark, entry, "§4.4: recognisable entry, no fake ticks");
                }
                using (var doc = new DocView())
                {
                    doc.Load(LongDocument());
                    doc.OnWidth(660f);   // S80:V=660 时留白 25 < 32;只看刻度所在的 x<26,正文字形抗锯齿会渗到 29
                    doc.Paint(new RECT { R = 660, B = height }, memory, 0f, height - top, top);
                    uint canvas = GetPixel(memory, 10, 300);
                    bool clear = true; string where = "";
                    for (int y = (int)top + 2; y < height; y += 2)
                        for (int x = 0; x < 20; x++)
                        {
                            uint px = GetPixel(memory, x, y);
                            if (px != canvas && clear) { clear = false; where = $"x={x} y={y} px={px:X6} canvas={canvas:X6} colX={doc.ColX}"; }
                        }
                    check("A3Overlay/no ruler when the margin is too narrow " + dark, clear, "D1 " + where);
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

    // §4.3:当前命中 = 选区底色 + Accent 轮廓;选区不是当前命中时没有轮廓。
    private static void CurrentHitOutline(Action<string, bool, string> check)
    {
        const int width = 1000, height = 300;
        const float top = 36f;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0) { check("A3Overlay/current hit offscreen target", false, "GDI"); return; }
            var rect = new RECT { R = width, B = height };
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                uint accent = Colorref(dark ? ThemePalette.AccentD : ThemePalette.AccentL);
                int AccentPixels(bool onHit)
                {
                    using var doc = new DocView();
                    doc.Load("Plain alpha words and more words.\n\nSecond alpha here.\n");
                    doc.OnWidth(width);
                    int idx = doc.SetSearchQuery("alpha", 0, false);
                    var (s, l) = doc.SearchHits[Math.Max(0, idx)];
                    if (onHit) doc.SelectRange(s, s + l); else doc.SelectRange(0, 5);
                    doc.Paint(rect, memory, 0f, height - top, top);
                    int n = 0;
                    for (int y = (int)top + 1; y < height; y++)
                        for (int x = 40; x < width; x++) if (GetPixel(memory, x, y) == accent) n++;
                    return n;
                }
                int on = AccentPixels(true), off = AccentPixels(false);
                check("A3Overlay/current hit has an Accent outline " + dark, on > 0, $"accent px={on}");
                check("A3Overlay/plain selection has no Accent outline " + dark, off == 0, $"accent px={off}");
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

    // 面板上的按钮芯片加 1 DIP ControlBorder 描边(深色 Surface/Canvas 太接近);窄窗条按钮不变。
    private static void FindButtons(Action<string, bool, string> check)
    {
        const int width = 48, height = 28;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        nint layout = Dw.MkLayout("上一处", RenderResources.FmtToolbar, 10000f);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0 || layout == 0) { check("A3Overlay/find button offscreen target", false, "GDI/DWrite"); return; }
            var rect = new RECT { R = width, B = height };
            foreach (bool dark in new[] { false, true })
            {
                RenderResources.ApplyTheme(dark);
                uint border = Colorref(dark ? ThemePalette.ControlBorderD : ThemePalette.ControlBorderL);
                uint canvas = Colorref(dark ? ThemePalette.CanvasD : ThemePalette.CanvasL);
                uint surface = Colorref(dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL);
                FindBar.PaintButton(memory, rect, 1f, layout, true, 0);
                check("A3Overlay/panel button chip has a ControlBorder edge " + dark, GetPixel(memory, 1, 14) == border && GetPixel(memory, 46, 14) == border
                    && GetPixel(memory, 3, 14) == canvas && GetPixel(memory, 0, 14) == surface, $"x1={GetPixel(memory, 1, 14):X6} x3={GetPixel(memory, 3, 14):X6}");
                FindBar.PaintButton(memory, rect, 1f, layout, false, 0);
                check("A3Overlay/strip button chip is unchanged " + dark, GetPixel(memory, 1, 14) == surface && GetPixel(memory, 0, 14) == canvas, $"x1={GetPixel(memory, 1, 14):X6}");
            }
        }
        finally
        {
            if (layout != 0) Marshal.Release(layout);
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}

