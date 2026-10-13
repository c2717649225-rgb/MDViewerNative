using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MDViewerNative;

internal static class ArchitectureTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        Typography(check);
        ProductCommandRouting(check);
        DocumentSessionViewBoundary(check);
        ParagraphMeasurementTests.Run(check);
        Toolbar(check);
        Editor(check);
        TestHitTestingGeometry(check);
        TestScrollGeometry(check);
        TestPointerSession(check);
        TestWindowGeometry(check);
        TestWindowChrome(check);
        TestWindowLifecycle(check);
        TestAppState(check);
        TestToolbarInteraction(check);
        using (var table = new DocView())
        {
            const string source = "| Heading | Other |\n|---|---|\n| Value | [link](https://example.org) |\n";
            string expected = HtmlExport.Build(MdParser.Parse(source), "table", false);
            table.Load(source);
            table.OnWidth(400f);
            table.OnWidth(800f);
            check("Architecture/table layout preserves parsed content", HtmlExport.Build(table.Blocks, "table", false) == expected, "font styling and geometry do not alter exported parser output");
        }
        using (var responsive = new DocView())
        {
            responsive.Load("# Heading\n\nA paragraph for narrow navigation.");
            responsive.OnWidth(820f);
            float wideX = responsive.ColX, wideW = responsive.ColW, height = responsive.TotalH;
            // S80:宽窗口的目录常驻并让出宽度——正文在目录右侧重排,右边界不变。
            responsive.SetToc(true);
            float pinnedX = responsive.ColX, pinnedW = responsive.ColW;
            check("UI/pinned directory makes room beside the reading column", responsive.TocVisible && responsive.TocPinned && pinnedX >= responsive.TocWidth && MathF.Abs(pinnedX + pinnedW - (wideX + wideW)) < 0.01f, $"colX {wideX}→{pinnedX}");
            responsive.SetToc(false);
            check("UI/closing directory restores the full-width column", responsive.ColX == wideX && responsive.ColW == wideW && responsive.TotalH == height, "same geometry and height as before opening");
            responsive.OnWidth(500f);
            float narrowX = responsive.ColX, narrowW = responsive.ColW;
            responsive.SetToc(true);
            check("UI/narrow view supports directory overlay", responsive.TocVisible && !responsive.TocPinned && responsive.ColX == narrowX && responsive.ColW == narrowW && responsive.ColX + responsive.ColW <= 500f, "manual narrow windows retain navigation");
            responsive.OnWidth(820f);
            check("UI/widening with the directory open pins it", responsive.TocVisible && responsive.TocPinned && responsive.ColX == pinnedX && responsive.ColW == pinnedW, "same pinned geometry as opening at this width");
        }
        string directory = Path.Combine(Path.GetTempPath(), "mdv-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Files(directory, check);
            Sessions(directory, check);
            TabIdentityLookup(directory, check);
            Images(directory, check);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static unsafe void GraphicsLifetime(Action<string, bool, string> check)
    {
        nint format = 0, brush = 0, factory = 0;
        try
        {
            using var graphics = new RenderResources(1f);
            bool duplicateRejected = false;
            try { using var duplicate = new RenderResources(1f); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            check("Architecture/graphics scope has one owner", duplicateRejected && RenderResources.Rt != 0, "a second scope cannot overwrite live resources");
            format = RenderResources.FmtBody;
            brush = RenderResources.BInk;
            int heldFormat = Marshal.AddRef(format), heldBrush = Marshal.AddRef(brush);
            ((delegate* unmanaged[Stdcall]<nint, nint*, void>)(*(nint**)RenderResources.Rt)[3])(RenderResources.Rt, &factory);
            graphics.Dispose();
            int remainingFormat = Marshal.Release(format); format = 0;
            int remainingBrush = Marshal.Release(brush); brush = 0;
            check("Architecture/graphics scope releases its format", remainingFormat == heldFormat - 2, "only the explicit test reference survives shutdown");
            check("Architecture/graphics scope releases its brush", remainingBrush == heldBrush - 2, "shared brushes have a bounded lifetime");
            int remainingFactory = Marshal.Release(factory); factory = 0;
            check("Architecture/native factory creation reference is released", remainingFactory == 0, "no extra shim reference survives the render target");
        }
        finally
        {
            if (format != 0) Marshal.Release(format);
            if (brush != 0) Marshal.Release(brush);
            if (factory != 0) Marshal.Release(factory);
        }
        using var reopened = new RenderResources(1f);
        using var doc = new DocView();
        doc.Load("# Reopened\n\nNew graphics resources can lay out a document.");
        check("Architecture/graphics can reopen after disposal", doc.TotalH > 0, "sequential UI scopes do not reuse released formats");
    }

    private static void Editor(Action<string, bool, string> check)
    {
        nint parent = Externs.CreateWindowExW(0, "STATIC", "editor-test", 0, 0, 0, 600, 400, 0, 0, Externs.GetModuleHandleW(null), 0);
        if (parent == 0) throw new System.ComponentModel.Win32Exception();
        int changes = 0, menus = 0;
        var keyboard = new byte[256];
        GetKeyboardState(keyboard);
        SetKeyboardState(new byte[256]);
        nint previous = Externs.GetWindowLongPtrW(parent, -4);
        TestWindowProc callback = (h, m, w, l) =>
        {
            if (m == 0x007B) { menus++; return 0; }
            return Externs.CallWindowProcW(previous, h, m, w, l);
        };
        Externs.SetWindowLongPtrW(parent, -4, Marshal.GetFunctionPointerForDelegate(callback));
        try
        {
            using var editor = new EditorControl(parent, 1f, () => changes++, () => null);
            void Key(int key)
            {
                Externs.SendMessageW(editor.Handle, 0x0100 /*WM_KEYDOWN*/, (nint)key, 0);
                Externs.SendMessageW(editor.Handle, 0x0102 /*WM_CHAR from TranslateMessage*/, (nint)key, 0);
                Externs.SendMessageW(editor.Handle, 0x0101 /*WM_KEYUP*/, (nint)key, 0);
            }
            void Enter(string text)
            {
                Externs.SetWindowTextW(editor.Handle, text);
                Externs.SendMessageW(editor.Handle, 0x00B1, (nint)text.Length, (nint)text.Length);
                Key(0x0D);
            }
            Enter("A longer first line\r\n- ");
            check("Architecture/editor exits an empty later list item", Externs.WindowText(editor.Handle) == "A longer first line\r\n", "source line boundaries are independent of visual line lengths");
            string longList = "x\r\n- " + new string('y', 70000);
            Enter(longList);
            check("Architecture/editor continues a wrapped long list item", Externs.WindowText(editor.Handle) == longList + "\r\n- ", "70000 characters, multiple native visual lines");
            check("Architecture/editor callback reports text changes", changes == 2, "instance callback reaches its host");
            Externs.SendMessageW(editor.Handle, 0x007B, editor.Handle, (nint)(-1));
            check("Architecture/editor context menu reaches its owner", menus == 1, "actual WM_CONTEXTMENU routing replaces source-text inspection");
            Externs.SetWindowTextW(editor.Handle, "  alpha\r\n  beta");
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)12, (nint)15);
            var shift = new byte[256]; shift[0x10] = 0x80;
            SetKeyboardState(shift);
            Key(9);
            SetKeyboardState(new byte[256]);
            check("Architecture/editor dedent preserves selected characters", Externs.WindowText(editor.Handle) == "  alpha\r\nbeta" && Externs.EditSelection(editor.Handle) == (10, 13), "selection follows the actual removed indentation");
            Externs.SendMessageW(editor.Handle, 0x0304 /*WM_UNDO*/, 0, 0);
            check("Architecture/editor indentation remains undoable", Externs.WindowText(editor.Handle) == "  alpha\r\n  beta", "one native replacement restores the original text");
            Externs.SetWindowTextW(editor.Handle, "alpha");
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)2, (nint)5);
            Key(9);
            check("Architecture/editor tab indents once", Externs.WindowText(editor.Handle) == "  alpha" && Externs.EditSelection(editor.Handle) == (4, 7), "translated character does not replace the selection with a tab");
            Enter("plain text");
            check("Architecture/editor ordinary Enter remains native", Externs.WindowText(editor.Handle) == "plain text\r\n", "default EDIT handles one newline");
            SetKeyboardState(shift);
            Enter("- list item");
            SetKeyboardState(new byte[256]);
            check("Architecture/editor Shift Enter bypasses continuation", Externs.WindowText(editor.Handle) == "- list item\r\n", "shift preserves native newline behavior");
            nint font = Externs.SendMessageW(editor.Handle, 0x0031 /*WM_GETFONT*/, 0, 0);
            float height = editor.LineHeight;
            editor.SetScale(1.5f);
            check("Architecture/editor DPI change rebinds its font", Externs.SendMessageW(editor.Handle, 0x0031, 0, 0) != font && editor.LineHeight > height, "new native font and measured line height");
            Externs.SetWindowTextW(editor.Handle, "select this");
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)7, (nint)11);
            var selected = Externs.EditSelection(editor.Handle);
            float beforeZoom = editor.LineHeight;
            editor.SetFontSize(20f);
            check("UI/editor zoom updates its font without replacing text", editor.LineHeight > beforeZoom && Externs.WindowText(editor.Handle) == "select this" && Externs.EditSelection(editor.Handle) == selected, "selection and content survive a font update");
            unsafe
            {
                RECT beforeTheme = default, afterTheme = default;
                Externs.SendMessageW(editor.Handle, 0x00B2 /*EM_GETRECT*/, 0, (nint)(&beforeTheme));
                bool dark = RenderResources.DarkMode;
                RenderResources.ApplyTheme(!dark);
                editor.ApplyTheme();
                Externs.SendMessageW(editor.Handle, 0x00B2, 0, (nint)(&afterTheme));
                check("UI/editor theme change preserves text padding", beforeTheme.L == afterTheme.L && beforeTheme.T == afterTheme.T && beforeTheme.R == afterTheme.R && beforeTheme.B == afterTheme.B && beforeTheme.T > 0, "theme changes retain the formatting rectangle");
                RenderResources.ApplyTheme(dark);
                editor.ApplyTheme();
            }
            nint window = editor.Handle;
            editor.Dispose();
            check("Architecture/editor disposal destroys its HWND", !Externs.IsWindow(window), "explicit disposal is idempotent");
            uint Cycle()
            {
                for (int i = 0; i < 20; i++)
                {
                    using var sample = new EditorControl(parent, 1f, () => { }, () => null);
                    sample.SetScale(1.25f); sample.SetScale(1.5f);
                }
                GdiFlush();
                return GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
            }
            uint warm = Cycle(), after = Cycle();
            check("Architecture/editor font ownership stays bounded", after == warm, $"two batches of 20 create/DPI/dispose cycles: GDI objects {warm}->{after}");
            using var closing = new EditorControl(parent, 1f, () => { }, () => null);
            Externs.DestroyWindow(parent); parent = 0;
            check("Architecture/parent destruction detaches the editor", closing.Handle == 0, "WM_NCDESTROY releases instance ownership");
        }
        finally
        {
            if (parent != 0) Externs.DestroyWindow(parent);
            GC.KeepAlive(callback);
            SetKeyboardState(keyboard);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint TestWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    private static void Typography(Action<string, bool, string> check)
    {
        using var first = new DocView();
        using var second = new DocView();
        const string markdown = "# Heading\n\nA paragraph with several words to exercise layout and font changes.\n";
        try
        {
            first.Load(markdown);
            second.Load(markdown);
            first.OnWidth(596f);
            second.OnWidth(596f);
            float firstHeight = first.TotalH, secondHeight = second.TotalH;
            int initialPasses = first.LayoutPassCount;
            first.SetBodySize(20f, relayout: false);
            check("Architecture/deferred font change", first.TotalH == firstHeight && first.LayoutPassCount == initialPasses, "layout must wait for the host");
            second.OnWidth(596f);
            check("Architecture/inactive tab font refresh", second.TotalH > secondHeight, "same window width, new font metrics");
            first.OnWidth(596f);
            first.EnsureLayout();
            check("Architecture/same-width fallback lays out once", first.LayoutPassCount == initialPasses + 1, "repeated flush is idempotent");
            check("Architecture/fixed window column refresh", Math.Abs(first.ColW - 556f) < 0.1f, "V-2P: V=596, P=20, colW=556");
            initialPasses = first.LayoutPassCount;
            first.SetBodySize(12f, relayout: false);
            first.OnWidth(460f);
            first.EnsureLayout();
            first.OnWidth(460f);
            check("Architecture/resizing zoom lays out once", first.LayoutPassCount == initialPasses + 1 && Math.Abs(first.ColW - 420f) < 0.1f, "WM_SIZE followed by host flush");
            first.OnWidth(1000f);
            first.SetBodySize(20f);
            check("Architecture/font change keeps the window column", Math.Abs(first.ColW - (1000f - 2f * WindowFit.PadWide)) < 0.1f, "S80: column follows the window, not the font");

            string family = RenderResources.SerifFamily;
            bool serif = RenderResources.SerifMode;
            try
            {
                first.SetSerif(true, "Times New Roman"); // Exercise font changes without optional CJK fonts.
                int before = second.LayoutPassCount;
                second.OnWidth(596f);
                check("Architecture/inactive tab serif refresh", second.LayoutPassCount == before + 1, "font family invalidates every document");
                first.SetSerif(false);
                before = second.LayoutPassCount;
                second.OnWidth(596f);
                second.OnWidth(596f);
                check("Architecture/serif roundtrip invalidates once", second.LayoutPassCount == before + 1, "repeat activation stays cached");
            }
            finally { first.SetSerif(serif, family); }
        }
        finally
        {
            first.SetBodySize(16f);
        }

        using var closing = new DocView();
        closing.Load("A cached native text layout.");
        closing.OnWidth(596f);
        var cacheObject = typeof(DocView).GetField("_layoutCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(closing);
        var cache = (IDictionary)cacheObject.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cacheObject);
        object entry = cache[0];
        nint layout = (nint)entry.GetType().GetField("L").GetValue(entry);
        int retained = Marshal.AddRef(layout);
        closing.Dispose();
        int remaining = Marshal.Release(layout);
        check("Architecture/close releases native layout", remaining == retained - 2, $"retained={retained}, remaining={remaining}");
        closing.Dispose();
        bool rejected = false;
        try { closing.Load("Already closed"); }
        catch (ObjectDisposedException) { rejected = true; }
        check("Architecture/closed document cannot reload", rejected, "double disposal is safe; reuse is rejected");
    }

    private static void Files(string directory, Action<string, bool, string> check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var utf8 = new UTF8Encoding(false);
        var cases = new (string name, byte[] bytes)[]
        {
            ("empty", Array.Empty<byte>()),
            ("lf", utf8.GetBytes("# 中文\n\nbody\n")),
            ("crlf", utf8.GetBytes("# 中文\r\n\r\nbody\r\n")),
            ("no-eol", utf8.GetBytes("中文末行")),
            ("bom", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8.GetBytes("# 中文\r\nbody")).ToArray()),
            ("gbk", Encoding.GetEncoding(936).GetBytes("# 中文\r\n正文\r\n")),
        };
        foreach (var sample in cases)
        {
            string path = Path.Combine(directory, sample.name + ".md");
            File.WriteAllBytes(path, sample.bytes);
            using var session = new DocumentSession();
            session.Load(path, DocumentFile.Read(path));
            session.Save(session.FromEditorText(session.EditorText));
            check("Architecture/file roundtrip " + sample.name, File.ReadAllBytes(path).SequenceEqual(sample.bytes), "byte-for-byte encoding and line endings");
            check("Architecture/save backup " + sample.name, File.ReadAllBytes(path + ".bak").SequenceEqual(sample.bytes), "previous version retained");
        }
        string gbkPath = Path.Combine(directory, "gbk.md");
        using var gbk = new DocumentSession();
        gbk.Load(gbkPath, DocumentFile.Read(gbkPath));
        gbk.MarkDirty();
        byte[] original = File.ReadAllBytes(gbkPath);
        bool encodingFailed = false;
        try { gbk.Save("Unrepresentable 😀"); }
        catch (EncoderFallbackException) { encodingFailed = true; }
        check("Architecture/GBK save rejects lossy encoding", encodingFailed && gbk.Dirty && File.ReadAllBytes(gbkPath).SequenceEqual(original), "original survives an unsupported character");
        string unrelated = gbkPath + ".tmp";
        File.WriteAllText(unrelated, "unrelated");
        gbk.Save("中文更新");
        check("Architecture/save does not overwrite sibling tmp", File.ReadAllText(unrelated) == "unrelated", "private temporary filename");
        check("Architecture/save leaves no private temporary files", Directory.GetFiles(directory, ".mdv-*.tmp").Length == 0, "success and encoding failure");
    }

    // 身份查找收敛到 DocumentTabs(51):此前「按 RecoveryId 找标签」在宿主里手写了
    // 七处 —— 三处是传给 DocumentBatch 的解析 lambda,四处是就地展开的下标扫描。
    // 这里锁住新接口的语义,尤其是关闭时必须从后往前给下标。
    private static void TabIdentityLookup(string directory, Action<string, bool, string> check)
    {
        string aPath = Path.Combine(directory, "id-a.md"), bPath = Path.Combine(directory, "id-b.md");
        File.WriteAllText(aPath, "Alpha\n");
        File.WriteAllText(bPath, "Bravo\n");
        using var tabs = new DocumentTabs();
        var alpha = tabs.Current;
        alpha.Load(aPath, DocumentFile.Read(aPath));
        var bravo = new DocumentSession();
        bravo.Load(bPath, DocumentFile.Read(bPath));
        tabs.Add(bravo);

        check("TabLookup/按身份取回同一对象", ReferenceEquals(tabs.ById(bravo.RecoveryId), bravo),
            "ById returns the live session");
        check("TabLookup/按身份取回下标", tabs.IndexOfId(alpha.RecoveryId) == 0 && tabs.IndexOfId(bravo.RecoveryId) == 1,
            $"a={tabs.IndexOfId(alpha.RecoveryId)} b={tabs.IndexOfId(bravo.RecoveryId)}");
        check("TabLookup/按引用取回下标", tabs.IndexOf(bravo) == 1, $"got {tabs.IndexOf(bravo)}");

        // 未知身份要安静地报"没有",不能抛异常 —— 恢复流程会拿着已关闭文档的 id 来问。
        check("TabLookup/未知身份返回空而不抛", tabs.ById(Guid.NewGuid()) == null && tabs.IndexOfId(Guid.NewGuid()) < 0,
            "unknown identity is not an error");

        // 关闭用的下标必须从后往前:先删小下标会让后面的下标全部左移,
        // 照着旧下标继续删就会静默关掉不相干的标签。
        var order = tabs.IndicesOfIds(new[] { alpha.RecoveryId, bravo.RecoveryId }).ToArray();
        check("TabLookup/关闭下标从后往前", order.Length == 2 && order[0] == 1 && order[1] == 0,
            $"got [{string.Join(",", order)}]");

        // 只认识的那些才给下标,混入陌生 id 不应影响结果。
        var partial = tabs.IndicesOfIds(new[] { Guid.NewGuid(), alpha.RecoveryId }).ToArray();
        check("TabLookup/忽略不认识的身份", partial.Length == 1 && partial[0] == 0,
            $"got [{string.Join(",", partial)}]");

        int created = 0;
        using var injected = new DocumentTabs(() => { created++; return new DocumentSession(); });
        injected.NewDocument();
        check("TabLookup/会话创建可注入", created == 2 && injected.Count == 2,
            "initial and new tab both use the supplied factory");
    }

    private static void TestHitTestingGeometry(Action<string, bool, string> check)
    {
        float[] ys = { 10f, 40f, 80f };
        float[] heights = { 20f, 20f, 30f };
        check("Architecture/hit geometry handles empty layout", HitTestingGeometry.BlockAtY(ys, heights, 0, 0f) == -1, "empty documents have no candidate block");
        check("Architecture/hit geometry resolves blocks by nearest gap", HitTestingGeometry.BlockAtY(ys, heights, 3, 30f) == 0 && HitTestingGeometry.BlockAtY(ys, heights, 3, 35f) == 0 && HitTestingGeometry.BlockAtY(ys, heights, 3, 36f) == 1, "gap ties keep the preceding block");
        check("Architecture/hit geometry clamps outside layout", HitTestingGeometry.BlockAtY(ys, heights, 3, 0f) == 0 && HitTestingGeometry.BlockAtY(ys, heights, 3, 200f) == 2, "points outside the document use the nearest edge block");
    }

    private static void TestScrollGeometry(Action<string, bool, string> check)
    {
        // 网格量化与上限:150% 缩放下网格是 2/3 DIP,上限向下落网格,钳制不越上限
        float max = ScrollGeometry.MaxOffset(1000f, 300f, 1.5f);
        check("Architecture/scroll max offset lands on the pixel grid", MathF.Abs(max * 1.5f - MathF.Round(max * 1.5f)) < 1e-4f && max <= 700f && max > 699f, "floor to grid, never above the true bottom");
        check("Architecture/scroll clamp snaps and bounds", ScrollGeometry.ClampOffset(float.MaxValue, max, 1.5f) == max && ScrollGeometry.ClampOffset(-5f, max, 1.5f) == 0f && ScrollGeometry.ClampOffset(10.2f, max, 1.5f) == ScrollGeometry.Snap(10.2f, 1.5f), "top, bottom and grid");
        check("Architecture/scroll bar commands share one table",
            ScrollGeometry.TryScrollBarTarget(0, 500f, 600f, out float lineUp) && lineUp == 414f
            && ScrollGeometry.TryScrollBarTarget(3, 500f, 600f, out float pageDown) && pageDown == 1060f
            && ScrollGeometry.TryScrollBarTarget(7, 500f, 600f, out float bottom) && bottom == float.MaxValue
            && !ScrollGeometry.TryScrollBarTarget(4, 500f, 600f, out _) && !ScrollGeometry.TryScrollBarTarget(5, 500f, 600f, out _),
            "line 86, page keeps 40, thumb tracking is left to the host");
        check("Architecture/scroll keys map onto scroll bar commands",
            ScrollGeometry.TryKeyTarget(0x21, 500f, 600f, out float pgUp) && pgUp == -60f
            && ScrollGeometry.TryKeyTarget(0x28, 500f, 600f, out float down) && down == 586f
            && ScrollGeometry.TryKeyTarget(0x24, 500f, 600f, out float home) && home == 0f
            && !ScrollGeometry.TryKeyTarget(0x25, 500f, 600f, out _),
            "PgUp/Down/Home resolve, Left stays with code block scrolling");
        check("Architecture/auto scroll direction uses the edge band", ScrollGeometry.AutoScrollDirection(90, 60, 800) == -1 && ScrollGeometry.AutoScrollDirection(400, 60, 800) == 0 && ScrollGeometry.AutoScrollDirection(770, 60, 800) == 1, "40px above the bottom or below the content top");
        var info = ScrollGeometry.ScrollBarInfo(23, 2000f, 0.25f, 33.3f, 2f);
        check("Architecture/scroll bar info is expressed in physical pixels", info.nMax == 4000 && info.nPage == 1 && info.nPos == 67 && info.nMin == 0 && info.fMask == 23, "page is at least one pixel, position rounds");

        // 缓出:每帧只走 22%,单帧封顶 120,收敛后停在目标;起点与目标都在网格上时不会永远差半格
        var smooth = new SmoothScroll();
        check("Architecture/smooth scroll starts once and accumulates", smooth.Begin(100f) && !smooth.Begin(100f) && smooth.Active, "a second wheel notch does not restart the easing");
        smooth.Accumulate(-120, 5000f);
        smooth.Accumulate(-120, 5000f);
        check("Architecture/smooth scroll target follows the wheel", smooth.Target == 530f, "one notch is 215 DIP");
        smooth.Accumulate(-120000, 700f);
        check("Architecture/smooth scroll target respects the limit", smooth.Target == 700f, "target is clamped to the document");
        float current = 100f;
        int frames = 0;
        bool settled = false;
        float firstStep = 0f;
        bool framesOk = true;
        while (!settled && frames < 200)
        {
            float next = smooth.Step(current, 1.5f, 700f, out settled);
            if (frames == 0) firstStep = next - current;
            framesOk &= settled || (next > current && next - current <= 120f + 1e-3f && MathF.Abs(next * 1.5f - MathF.Round(next * 1.5f)) < 1e-3f);
            current = next;
            frames++;
        }
        check("Architecture/smooth scroll frames are capped and on the grid", framesOk, "every frame moves forward, at most 120 DIP, onto the 1.5x pixel grid");
        check("Architecture/smooth scroll converges on the target", settled && current == 700f && !smooth.Active && frames > 5 && firstStep == 120f, "large distances start at the 120 DIP cap and end exactly on target after " + frames + " frames");
        smooth.Begin(0f);
        smooth.Accumulate(-120, 700f);
        float tiny = smooth.Step(214.9f, 1f, 700f, out bool tinySettled);
        check("Architecture/smooth scroll snaps the last sub-pixel", tinySettled && tiny == 215f && !smooth.Active, "a residual below half a pixel finishes the animation");
        smooth.Begin(0f);
        smooth.Accumulate(-120, 5000f);
        smooth.Step(0f, 1f, 100f, out _);
        check("Architecture/smooth scroll re-clamps after resize", smooth.Target == 100f, "a shorter viewport pulls the target back");
        smooth.Stop();
        check("Architecture/smooth scroll stop clears the animation", !smooth.Active, "explicit jumps interrupt easing");
    }

    private static void TestPointerSession(Action<string, bool, string> check)
    {
        var pointer = new PointerSession();
        check("Architecture/pointer session initializes idle",
            !pointer.IsCapturing && pointer.LastMx == 0 && pointer.LastMy == 0 && pointer.AutoScrollDir == 0 && pointer.HoverBtn == -1 && pointer.PressBtn == -1 && !pointer.Tracking,
            "all session states start neutral");

        // 手势序列:按下 → 拖出正文 → 回到正文 → 释放
        pointer.BeginSelection(100, 200);
        check("Architecture/pointer session begins selection",
            pointer.IsCapturing && pointer.LastMx == 100 && pointer.LastMy == 200 && pointer.AutoScrollDir == 0,
            "captures mouse and records initial coordinate");

        bool extended = pointer.OnMouseMove(50, 10, overBody: false, desiredAutoScrollDir: -1, out bool autoScrollChanged);
        check("Architecture/pointer move outside body pauses selection",
            !extended && pointer.IsCapturing && pointer.LastMx == 100 && pointer.LastMy == 200 && pointer.AutoScrollDir == 0 && !autoScrollChanged,
            "outside body suppresses selection extend and halts autoscroll");

        extended = pointer.OnMouseMove(120, 250, overBody: true, desiredAutoScrollDir: 1, out autoScrollChanged);
        check("Architecture/pointer move inside body extends selection and updates autoscroll",
            extended && pointer.IsCapturing && pointer.LastMx == 120 && pointer.LastMy == 250 && pointer.AutoScrollDir == 1 && autoScrollChanged,
            "inside body records coordinate and activates autoscroll");

        bool wasDrag = pointer.EndSelection();
        check("Architecture/pointer release ends selection",
            wasDrag && !pointer.IsCapturing && pointer.AutoScrollDir == 0,
            "resets capture and autoscroll while returning prior dragging state");

        // 外部捕获丢失
        pointer.BeginSelection(10, 20);
        pointer.CancelSelection();
        check("Architecture/pointer cancel selection handles external capture steal",
            !pointer.IsCapturing && pointer.AutoScrollDir == 0 && !pointer.EndSelection(),
            "cancelling capture leaves state non-dragging");

        // 工具栏交互生命周期:按压/释放/悬停/离开
        pointer.PressButton(2);
        check("Architecture/pointer press button tracks target", pointer.PressBtn == 2, "records pressed button");
        bool released = pointer.ReleaseButton(2, out bool clicked, out int btn);
        check("Architecture/pointer release on matching button triggers action",
            released && clicked && btn == 2 && pointer.PressBtn == -1,
            "matching release reports clicked button and clears press");
        pointer.PressButton(3);
        released = pointer.ReleaseButton(0, out clicked, out btn);
        check("Architecture/pointer release on non-matching button cancels action",
            released && !clicked && btn == 3 && pointer.PressBtn == -1,
            "mismatch release does not trigger action");

        bool hoverChanged = pointer.OnMouseMoveToolbar(1);
        check("Architecture/pointer toolbar hover change reports true", hoverChanged && pointer.HoverBtn == 1, "hover enters button");
        hoverChanged = pointer.OnMouseMoveToolbar(1);
        check("Architecture/pointer toolbar hover duplicate reports false", !hoverChanged && pointer.HoverBtn == 1, "duplicate hover avoids invalidate");
        pointer.Tracking = true;
        hoverChanged = pointer.OnMouseLeave();
        check("Architecture/pointer mouse leave clears hover and tracking", hoverChanged && pointer.HoverBtn == -1 && !pointer.Tracking, "leave cleans up all hover highlights");

        // 空态容差命中纯几何
        var hits = new List<(float x, float y, float w, float h, string path)>
        {
            (100f, 200f, 80f, 20f, "alpha.md")
        };
        check("Architecture/empty hit geometry handles exact and margin bounds",
            HitTestingGeometry.FindEmptyHit(hits, 140f, 210f) == 0
            && HitTestingGeometry.FindEmptyHit(hits, 96f, 210f) == 0
            && HitTestingGeometry.FindEmptyHit(hits, 95f, 210f) == -1
            && HitTestingGeometry.FindEmptyHit(hits, 140f, 198f) == 0
            && HitTestingGeometry.FindEmptyHit(hits, 140f, 197f) == -1
            && HitTestingGeometry.FindEmptyHit(null, 140f, 210f) == -1,
            "4px horizontal and 2px vertical margins");

        // 光标优先级判定纯函数
        check("Architecture/cursor determination respects client area and hierarchy",
            HitTestingGeometry.DetermineCursor(false, true, true, false, false, false, false, false) == PointerCursor.None
            && HitTestingGeometry.DetermineCursor(true, false, true, false, false, false, false, false) == PointerCursor.None
            && HitTestingGeometry.DetermineCursor(true, true, false, false, false, false, false, false) == PointerCursor.Arrow
            && HitTestingGeometry.DetermineCursor(true, true, true, true, false, false, false, false) == PointerCursor.IBeam
            && HitTestingGeometry.DetermineCursor(true, true, true, false, true, false, false, false) == PointerCursor.Hand
            && HitTestingGeometry.DetermineCursor(true, true, true, false, false, true, false, false) == PointerCursor.Hand
            && HitTestingGeometry.DetermineCursor(true, true, true, false, false, false, true, false) == PointerCursor.Hand
            && HitTestingGeometry.DetermineCursor(true, true, true, false, false, false, false, true) == PointerCursor.IBeam
            && HitTestingGeometry.DetermineCursor(true, true, true, false, false, false, false, false) == PointerCursor.Arrow,
            "hierarchy: non-client < toolbar < editing < link/image/empty < text block < blank");

        // 释放意图分类
        check("Architecture/pointer click intent classifies links, tasks and details",
            PointerSession.ClassifyReleaseAction(true, true, false, "#ref:cite", false, out string t1) == PointerClickIntent.JumpRefBack && t1 == "cite"
            && PointerSession.ClassifyReleaseAction(true, true, false, "#fn:note", false, out string t2) == PointerClickIntent.JumpFootnote && t2 == "note"
            && PointerSession.ClassifyReleaseAction(true, true, false, "https://test", false, out string t3) == PointerClickIntent.OpenLink && t3 == "https://test"
            && PointerSession.ClassifyReleaseAction(true, true, false, null, true, out _) == PointerClickIntent.ToggleDetails
            && PointerSession.ClassifyReleaseAction(true, false, false, "https://test", false, out _) == PointerClickIntent.None
            && PointerSession.ClassifyReleaseAction(false, true, false, "https://test", false, out _) == PointerClickIntent.None
            && PointerSession.ClassifyReleaseAction(true, true, true, "https://test", false, out _) == PointerClickIntent.None,
            "editing and non-drag suppress document link navigation");
    }

    private static void TestWindowGeometry(Action<string, bool, string> check)
    {
        // 1. DPI 与缩放转换
        check("Architecture/window geometry converts dpi to scale",
            MathF.Abs(WindowGeometry.DpiToScale(96) - 1.0f) < 0.001f
            && MathF.Abs(WindowGeometry.DpiToScale(144) - 1.5f) < 0.001f
            && MathF.Abs(WindowGeometry.DpiToScale(192u) - 2.0f) < 0.001f,
            "96 dpi is 1.0 scale");

        check("Architecture/window geometry detects scale changes with tolerance",
            !WindowGeometry.HasScaleChanged(1.0f, 1.0005f)
            && WindowGeometry.HasScaleChanged(1.0f, 1.05f)
            && WindowGeometry.HasScaleChanged(1.5f, 1.0f),
            "0.001 epsilon tolerance");

        // 2. 最小跟踪尺寸 (WM_GETMINMAXINFO)
        var min10 = WindowGeometry.MinTrackSize(1.0f);
        var min15 = WindowGeometry.MinTrackSize(1.5f);
        check("Architecture/window geometry min track size scales with dpi",
            min10.minWidth == 500 && min10.minHeight == 300
            && min15.minWidth == 750 && min15.minHeight == 450,
            "500x300 DIP minimum scaled");

        // 3. 视口尺寸解包 (WM_SIZE)
        nint normalLParam = (nint)((600 << 16) | 800);
        nint zeroLParam = 0;
        bool okNormal = WindowGeometry.UnpackSize(normalLParam, out int vw, out int vh);
        bool okZero = WindowGeometry.UnpackSize(zeroLParam, out int zw, out int zh);
        check("Architecture/window geometry unpacks WM_SIZE lParam",
            okNormal && vw == 800 && vh == 600
            && !okZero && zw == 0 && zh == 0,
            "unpacks low/high word and rejects zero or negative dimensions");

        // 5. 屏幕边缘吸附 (ClampWindowRect)
        var wa = new RECT { L = 0, T = 0, R = 1920, B = 1080 };
        var inside = new RECT { L = 100, T = 100, R = 900, B = 700 };
        var clampedInside = WindowGeometry.ClampWindowRect(inside, wa);
        check("Architecture/window geometry preserves rect within work area",
            clampedInside.L == inside.L && clampedInside.T == inside.T
            && clampedInside.R == inside.R && clampedInside.B == inside.B,
            "inside rect untouched");

        var offRight = new RECT { L = 1900, T = 100, R = 2700, B = 700 };
        var clampedRight = WindowGeometry.ClampWindowRect(offRight, wa);
        check("Architecture/window geometry snaps rect outside work area right",
            clampedRight.L == wa.L + 60 && clampedRight.T == wa.T + 40
            && clampedRight.R == wa.L + 60 + 800 && clampedRight.B == wa.T + 40 + 600,
            "right overflow snaps to work area origin plus margin");

        var offBottom = new RECT { L = 100, T = 1050, R = 900, B = 1650 };
        var clampedBottom = WindowGeometry.ClampWindowRect(offBottom, wa);
        check("Architecture/window geometry snaps rect outside work area bottom",
            clampedBottom.L == wa.L + 60 && clampedBottom.T == wa.T + 40,
            "bottom overflow snaps to work area origin plus margin");

        check("Architecture/window geometry clamp handles null work area safely",
            WindowGeometry.ClampWindowRect(offRight, null).L == offRight.L,
            "null work area returns original rect");

        // 6. 初始窗口尺寸解析 (ResolveInitialBounds)
        var defaultBounds = WindowGeometry.ResolveInitialBounds(false, default, wa);
        check("Architecture/window geometry default initial bounds",
            defaultBounds.L == 140 && defaultBounds.T == 90
            && (defaultBounds.R - defaultBounds.L) == 1240
            && (defaultBounds.B - defaultBounds.T) == 900,
            "default 1240x900 at 140,90");

        var savedBounds = new RECT { L = 200, T = 150, R = 1200, B = 850 };
        var resolvedSaved = WindowGeometry.ResolveInitialBounds(true, savedBounds, wa);
        check("Architecture/window geometry resolves valid saved bounds",
            resolvedSaved.L == 200 && resolvedSaved.T == 150
            && resolvedSaved.R == 1200 && resolvedSaved.B == 850,
            "valid saved bounds preserved");

        // 8. 重绘条带计算与命中 (ToolbarStrip, ContentStrip, HitStrips)
        var toolStrip = WindowGeometry.ToolbarStrip(1000, 40f, 1.5f);
        var contentStrip = WindowGeometry.ContentStrip(1000, 800, 40f, 1.5f);
        check("Architecture/window geometry calculates invalidation strips",
            toolStrip.L == 0 && toolStrip.T == 0 && toolStrip.R == 1000 && toolStrip.B == 61
            && contentStrip.L == 0 && contentStrip.T == 60 && contentStrip.R == 1000 && contentStrip.B == 800,
            "toolbar and content strips partitioned by scale");

        var hitToolOnly = WindowGeometry.HitStrips(new RECT { T = 10, B = 50 }, 40f, 1.5f);
        var hitContentOnly = WindowGeometry.HitStrips(new RECT { T = 70, B = 300 }, 40f, 1.5f);
        var hitBoth = WindowGeometry.HitStrips(new RECT { T = 20, B = 300 }, 40f, 1.5f);
        check("Architecture/window geometry hit strips detects affected regions",
            hitToolOnly.toolHit && !hitToolOnly.contentHit
            && !hitContentOnly.toolHit && hitContentOnly.contentHit
            && hitBoth.toolHit && hitBoth.contentHit,
            "partitions repaint between toolbar and content");

        // 9. 配置序列化与反序列化
        string serialized = WindowGeometry.FormatBounds(savedBounds, true);
        bool parsed = WindowGeometry.TryParseBounds(serialized, out var parsedRect, out bool parsedMax);
        check("Architecture/window geometry serializes and parses window bounds",
            parsed && parsedMax
            && parsedRect.L == 200 && parsedRect.T == 150
            && (parsedRect.R - parsedRect.L) == 1000
            && (parsedRect.B - parsedRect.T) == 700,
            "roundtrips L,T,W,H,max format");

        check("Architecture/window geometry rejects invalid or tiny bounds strings",
            !WindowGeometry.TryParseBounds("invalid", out _, out _)
            && !WindowGeometry.TryParseBounds("0,0,100,50,0", out _, out _),
            "requires at least 200x120 minimum dimensions");

        // 10. 字号与可视高度钳制
        check("Architecture/window geometry clamps font size and calculates content height",
            WindowGeometry.ClampBodyFont(10f) == 12f
            && WindowGeometry.ClampBodyFont(30f) == 28f
            && WindowGeometry.ClampBodyFont(18f) == 18f
            && WindowGeometry.ContentHeight(600f, 40f) == 560f
            && WindowGeometry.ContentHeight(30f, 40f) == 1f,
            "clamps body font 12-28, floors content height at 1 DIP");
    }

    private static void TestWindowChrome(Action<string, bool, string> check)
    {
        // 1. DWM 颜色属性计算
        var darkActive = WindowChrome.GetDwmAttributes(true, true);
        check("Architecture/window chrome dark active attributes",
            darkActive.Caption == 0x00161718u
            && darkActive.Border == 0x00D6DACDu
            && darkActive.Text == 0x00CBD3D6u
            && darkActive.ImmersiveDarkMode == 1u,
            "dark mode with active border");

        var darkInactive = WindowChrome.GetDwmAttributes(true, false);
        check("Architecture/window chrome dark inactive attributes",
            darkInactive.Caption == 0x00161718u
            && darkInactive.Border == 0x00555250u
            && darkInactive.Text == 0x00CBD3D6u
            && darkInactive.ImmersiveDarkMode == 1u,
            "dark mode with neutral gray inactive border");

        var lightActive = WindowChrome.GetDwmAttributes(false, true);
        check("Architecture/window chrome light active attributes",
            lightActive.Caption == 0x00F5F8F9u
            && lightActive.Border == 0x00CDCED6u
            && lightActive.Text == 0x001A1C1Eu
            && lightActive.ImmersiveDarkMode == 0u,
            "light mode with active border");

        var lightInactive = WindowChrome.GetDwmAttributes(false, false);
        check("Architecture/window chrome light inactive attributes",
            lightInactive.Caption == 0x00F5F8F9u
            && lightInactive.Border == 0x00C2C0BCu
            && lightInactive.Text == 0x001A1C1Eu
            && lightInactive.ImmersiveDarkMode == 0u,
            "light mode with neutral gray inactive border");

        // 2. 滚动条主题名称决策
        check("Architecture/window chrome scrollbar themes",
            WindowChrome.GetScrollbarTheme(true, false) == WindowChrome.DarkModeExplorerTheme
            && WindowChrome.GetScrollbarTheme(false, false) == WindowChrome.ExplorerTheme
            && WindowChrome.GetScrollbarTheme(true, true) == null
            && WindowChrome.GetScrollbarTheme(false, true) == null,
            "high contrast suppresses theme; dark/light select explorer class");

        // 3. 激活状态判定 (WM_ACTIVATE)
        check("Architecture/window chrome activation check",
            !WindowChrome.IsActive(0)
            && WindowChrome.IsActive(1)
            && WindowChrome.IsActive(2),
            "WA_INACTIVE is 0; WA_ACTIVE and WA_CLICKACTIVE are active");

        // 4. v2.2 A1: ThemePalette 单一真值——标题栏/文字色=Canvas/Ink 派生,字节序 0x00BBGGRR
        check("Architecture/window chrome tracks theme palette",
            WindowChrome.LightCaption == ThemePalette.ToColorRef(ThemePalette.CanvasL)
            && WindowChrome.DarkCaption == ThemePalette.ToColorRef(ThemePalette.CanvasD)
            && WindowChrome.LightText == ThemePalette.ToColorRef(ThemePalette.InkL)
            && WindowChrome.DarkText == ThemePalette.ToColorRef(ThemePalette.InkD),
            "DWM caption/text derive from the v2.2 canvas/ink tokens (A1 drift guard)");

        check("Architecture/palette colorref byte order",
            ThemePalette.ToColorRef(0xF9F8F5u) == 0x00F5F8F9u
            && ThemePalette.ToColorRef(0x181716u) == 0x00161718u
            && ThemePalette.ToColorRef(0xD6D3CBu) == 0x00CBD3D6u,
            "COLORREF is 0x00BBGGRR; copying RGB verbatim swaps red and blue");

        // 4. 滚动条样式位操作
        nint baseStyle = 0x10000000;
        uint wsVScroll = 0x00200000u;
        nint withScroll = WindowChrome.ToggleScrollbarStyle(baseStyle, true, wsVScroll);
        nint withoutScroll = WindowChrome.ToggleScrollbarStyle(withScroll, false, wsVScroll);
        check("Architecture/window chrome toggles scrollbar style bits",
            ((long)withScroll & wsVScroll) == wsVScroll
            && ((long)withoutScroll & wsVScroll) == 0
            && withoutScroll == baseStyle,
            "pure bitwise style toggle");

        // 5. 初始主题决策
        check("Architecture/window chrome resolves initial theme",
            WindowChrome.ResolveInitialDarkMode(true, true, false) == true
            && WindowChrome.ResolveInitialDarkMode(true, false, true) == false
            && WindowChrome.ResolveInitialDarkMode(false, false, true) == true
            && WindowChrome.ResolveInitialDarkMode(false, true, false) == false,
            "config takes precedence over system default");

        // 6. 注册表深色模式判定
        check("Architecture/window chrome checks system registry preference",
            WindowChrome.IsSystemDark(0, 0) == true
            && WindowChrome.IsSystemDark(0, 1) == false
            && WindowChrome.IsSystemDark(2, 0) == false,
            "reg value 0 with successful read is dark mode");
    }

    private static void TestWindowLifecycle(Action<string, bool, string> check)
    {
        // 1. 关闭守卫
        check("Architecture/window lifecycle can close guard",
            WindowLifecycle.CanClose(false, false) == true
            && WindowLifecycle.CanClose(true, false) == false
            && WindowLifecycle.CanClose(false, true) == false
            && WindowLifecycle.CanClose(true, true) == false,
            "active composition or modal dialog blocks closing");

        // 2. 其余脏标签统计
        check("Architecture/window lifecycle count dirty tabs",
            WindowLifecycle.CountDirtyOtherTabs(0, 3, i => i == 1 || i == 2) == 2
            && WindowLifecycle.CountDirtyOtherTabs(1, 3, i => i == 1) == 0
            && WindowLifecycle.CountDirtyOtherTabs(0, 0, _ => true) == 0,
            "counts dirty tabs excluding active tab");

        // 3. 拖放路径有效长度
        check("Architecture/window lifecycle drop length validation",
            WindowLifecycle.IsValidDropLength(10, 2048) == true
            && WindowLifecycle.IsValidDropLength(0, 2048) == false
            && WindowLifecycle.IsValidDropLength(2047, 2048) == false,
            "validates non-zero and non-truncated path buffer");

        // 4. 拖放文件分类
        check("Architecture/window lifecycle classify drop path",
            WindowLifecycle.ClassifyDropPath("C:\\doc.md") == WindowLifecycle.DropKind.Document
            && WindowLifecycle.ClassifyDropPath("notes.markdown") == WindowLifecycle.DropKind.Document
            && WindowLifecycle.ClassifyDropPath("readme.txt") == WindowLifecycle.DropKind.Document
            && WindowLifecycle.ClassifyDropPath("pic.png") == WindowLifecycle.DropKind.Image
            && WindowLifecycle.ClassifyDropPath("photo.jpg") == WindowLifecycle.DropKind.Image
            && WindowLifecycle.ClassifyDropPath("app.exe") == WindowLifecycle.DropKind.None
            && WindowLifecycle.ClassifyDropPath("") == WindowLifecycle.DropKind.None,
            "classifies markdown/text documents and images");

        // 5. 上下文菜单触发坐标解析
        var kbAnchor = WindowLifecycle.ResolveContextMenuAnchor(-1, 0, 1.5f, 40f);
        var mouseAnchor = WindowLifecycle.ResolveContextMenuAnchor(150, 250, 1.0f, 40);
        check("Architecture/window lifecycle resolve context menu anchor",
            kbAnchor.IsKeyboard == true
            && kbAnchor.X == 40
            && kbAnchor.Y == (int)((40 + 24) * 1.5f)
            && mouseAnchor.IsKeyboard == false
            && mouseAnchor.X == 150
            && mouseAnchor.Y == 250,
            "keyboard menu key anchors to reading top-left; mouse preserves screen coords");

        // 6. 销毁前草稿检查点决策
        check("Architecture/window lifecycle checkpoint on destroy",
            WindowLifecycle.ShouldCheckpointDraftsOnDestroy(true) == false
            && WindowLifecycle.ShouldCheckpointDraftsOnDestroy(false) == true,
            "abnormal destruction checkpoints drafts; clean close skips");
    }

    private static void TestAppState(Action<string, bool, string> check)
    {
        var state = new AppState();

        // 1. 默认值校验
        check("Architecture/app state default initial values",
            state.Editing == false
            && state.BodyFont == 16f
            && state.RestoringStartup == true
            && state.LightboxHintsLeft == 3,
            "starts un-editing with 16 DIP font, restoring startup and 3 hints");

        // 2. 字号修改与边界钳制
        bool z1 = state.TrySetBodyFont(20f, out float c1);
        float bf1 = state.BodyFont;
        bool z2 = state.TrySetBodyFont(20f, out float c2);
        bool z3 = state.TrySetBodyFont(35f, out float c3);
        float bf3 = state.BodyFont;
        bool z4 = state.TrySetBodyFont(5f, out float c4);
        float bf4 = state.BodyFont;
        check("Architecture/app state font update and clamping",
            z1 && c1 == 20f && bf1 == 20f
            && !z2 && c2 == 20f
            && z3 && c3 == 28f && bf3 == 28f
            && z4 && c4 == 12f && bf4 == 12f,
            "modifies font clamped within 12-28 and ignores redundant updates");

        // 3. 提示计数与生命周期流转
        bool h1 = state.ConsumeLightboxHint();
        bool h2 = state.ConsumeLightboxHint();
        bool h3 = state.ConsumeLightboxHint();
        bool h4 = state.ConsumeLightboxHint();
        state.FinishStartup();
        bool d1 = state.CheckAndDeferHandoff(true, false);
        check("Architecture/app state hint consumption and startup lifecycle",
            h1 && h2 && h3 && !h4
            && state.LightboxHintsLeft == 0
            && state.RestoringStartup == false
            && d1 && state.HandoffDeferred == true,
            "consumes 3 hints then stops; marks startup finished and defers handoff");
    }

    private static void TestToolbarInteraction(Action<string, bool, string> check)
    {
        using var toolbar = new ToolbarView();
        check("Architecture/toolbar initial focus is inactive",
            toolbar.FocusIndex == -1 && toolbar.FocusCount == 6,
            "toolbar starts with no focused button and 6 buttons when overflow is empty");

        toolbar.ToggleFocus();
        check("Architecture/toolbar toggle focus activates first button",
            toolbar.FocusIndex == 0,
            "toggling focus when inactive focuses index 0");

        toolbar.ToggleFocus();
        check("Architecture/toolbar toggle focus deactivates when active",
            toolbar.FocusIndex == -1,
            "toggling focus when active restores -1");

        toolbar.FocusIndex = 0;
        toolbar.StepFocus(0x27 /*Right*/, false);
        int afterRight = toolbar.FocusIndex;
        toolbar.StepFocus(0x25 /*Left*/, false);
        int afterLeft = toolbar.FocusIndex;
        toolbar.StepFocus(0x25 /*Left*/, false);
        int wrappedLeft = toolbar.FocusIndex;
        toolbar.StepFocus(9 /*Tab*/, true /*Shift*/);
        int wrappedShiftTab = toolbar.FocusIndex;

        check("Architecture/toolbar focus navigation steps and wraps circularly",
            afterRight == 1 && afterLeft == 0 && wrappedLeft == 5 && wrappedShiftTab == 4,
            "Right/Tab steps forward, Left/Shift-Tab steps backward and wraps at boundary");
    }

    private static void ProductCommandRouting(Action<string, bool, string> check)
    {
        int command = 0;
        var dispatcher = new ProductCommandDispatcher(new ProductCommandContext
        {
            NewDocument = () => command = 2200,
            ApplyMarkdownCommand = id => command = id,
            ReportError = _ => command = -1,
            AfterCommand = () => { }
        });
        check("Architecture/product commands route through a standalone dispatcher",
            dispatcher.TryDispatch(2200) && command == 2200 && !dispatcher.TryDispatch(2299),
            "known product commands are mapped, unknown ids are left to the host");
        check("Architecture/product edit command keeps its id",
            dispatcher.TryDispatch(2265) && command == 2265,
            "edit command payload is preserved across the routing boundary");
    }

    private static void DocumentSessionViewBoundary(Action<string, bool, string> check)
    {
        var adapter = new ProbeViewAdapter();
        using (var session = new DocumentSession(adapter))
        {
            session.Load("boundary.md", new DocumentContent("# Boundary\n", false, false, "\n"));
            check("Architecture/session uses view adapter for load", adapter.Loads == 1 && session.Doc == adapter.View, "rendering lifetime is supplied by the adapter");
        }
        check("Architecture/session disposes supplied view", adapter.Disposed, "session releases injected rendering lifetime");

        using var unattached = new DocumentSession();
        check("Architecture/session default constructor does not create view",
            !unattached.HasView && unattached.ViewAdapter == null,
            "pure document model has no eager view adapter");

        unattached.Load("memo.md", new DocumentContent("# Memo\nBody\n", false, false, "\n"));
        var probe = new ProbeViewAdapter();
        unattached.AttachView(probe);
        check("Architecture/session attach view synchronizes model content",
            unattached.HasView && probe.Loads == 1 && probe.View.DocumentDirectory.Length > 0,
            "attaching a view loads current directory and markdown text");

        var detached = unattached.DetachView();
        check("Architecture/session detach view leaves model intact",
            !unattached.HasView && ReferenceEquals(detached, probe) && unattached.Text == "# Memo\nBody\n",
            "detaching returns the adapter and retains model text");

        // Loading already materializes a view, so only a detached session can show that
        // reading Doc builds one on demand and renders the text the model kept.
        var rematerialized = unattached.Doc;
        check("Architecture/session Doc materializes a detached view on demand",
            unattached.HasView && !ReferenceEquals(rematerialized, probe.View) && rematerialized.SearchText().Contains("Memo"),
            "a detached session builds a fresh adapter that renders its own text");

        using var lazySession = new DocumentSession();
        lazySession.Load("notes.md", new DocumentContent("# Notes\n", false, false, "\n"));
        var doc = lazySession.Doc;
        check("Architecture/session lazy view creation on demand",
            lazySession.HasView && doc != null && doc.SearchText().Contains("Notes"),
            "accessing Doc creates adapter on demand and loads text");
    }

    private sealed class ProbeViewAdapter : IDocViewOwner
    {
        public DocView View { get; } = new();
        public int Loads { get; private set; }
        public bool Disposed { get; private set; }
        public void SetDirectory(string directory) => View.DocumentDirectory = directory ?? "";
        public void Load(string directory, string text) { Loads++; SetDirectory(directory); View.Load(text); }
        public void EnsureLayout() => View.EnsureLayout();
        public bool ShowToc { get => View.ShowToc; set => View.SetToc(value); }
        public void Dispose() { Disposed = true; View.Dispose(); }
    }

    private static void Sessions(string directory, Action<string, bool, string> check)
    {
        string aPath = Path.Combine(directory, "a.md"), bPath = Path.Combine(directory, "b.md");
        File.WriteAllText(aPath, "Alpha\n");
        File.WriteAllText(bPath, "Bravo\r\n");
        using var tabs = new DocumentTabs();
        var alpha = tabs.Current;
        alpha.Load(aPath, DocumentFile.Read(aPath));
        alpha.ScrollOffset = 120f;
        var bravo = new DocumentSession();
        bravo.Load(bPath, DocumentFile.Read(bPath));
        bravo.ScrollOffset = 240f;
        tabs.Add(bravo);
        bravo.MarkDirty();
        bool switched = tabs.TryActivate(0, () => false);
        check("Architecture/cancelled switch preserves session", !switched && tabs.Current == bravo && bravo.Dirty && bravo.ScrollOffset == 240f, "no identity or state mutation");
        switched = tabs.TryActivate(0, () => { bravo.Save(bravo.FromEditorText("Bravo saved\r\n")); return true; });
        check("Architecture/save then switch isolates documents", switched && tabs.Current == alpha && File.ReadAllText(aPath) == "Alpha\n" && File.ReadAllText(bPath) == "Bravo saved\r\n", "only the source tab is saved");
        check("Architecture/tab scroll belongs to document", alpha.ScrollOffset == 120f && bravo.ScrollOffset == 240f, "no shared scroll shadow");
        tabs.TryActivate(1, () => true);
        check("Architecture/reopen editor uses last saved text", bravo.EditorText == "Bravo saved\r\n" && !bravo.Dirty, "saved snapshot updated");
        bravo.MarkDirty();
        bravo.DiscardChanges();
        check("Architecture/discard editor draft preserves saved snapshot", !bravo.Dirty && bravo.EditorText == "Bravo saved\r\n", "hidden editor is not authoritative");
        bravo.ReplaceText("preview mutation\r\n");
        bravo.DiscardChanges();
        check("Architecture/discard preview mutation restores document", bravo.Text == "Bravo saved\r\n" && !bravo.Dirty, "rendered text and source restored together");

        bravo.MarkDirty();
        string missing = Path.Combine(directory, "missing", "target.md");
        bool failed = false;
        try { bravo.Save("draft", missing); }
        catch (DirectoryNotFoundException) { failed = true; }
        check("Architecture/failed Save As preserves identity", failed && bravo.Path == bPath && bravo.Dirty && bravo.Text == "Bravo saved\r\n", "failed write does not commit state");
        check("Architecture/failed Save As preserves original", File.ReadAllText(bPath) == "Bravo saved\r\n", "original file untouched");
        string copy = Path.Combine(directory, "copy.md");
        bravo.Save("saved copy\r\n", copy);
        check("Architecture/successful Save As commits identity", bravo.Path == copy && bravo.Name == "copy.md" && !bravo.Dirty && bravo.EditorText == "saved copy\r\n", "new target and source agree");

        alpha.MarkDirty();
        bool closed = tabs.TryClose(0, session => !session.Dirty);
        check("Architecture/cancelled inactive close retains ownership", !closed && !alpha.IsDisposed && tabs.Count == 2 && tabs.Current == bravo, "dirty guard cancels disposal");
        closed = tabs.TryClose(0, _ => true);
        check("Architecture/close inactive tab releases only its view", closed && alpha.IsDisposed && alpha.Doc.IsDisposed && !bravo.IsDisposed && tabs.Current == bravo && tabs.ActiveIndex == 0, "index shift preserves active identity");
        tabs.TryClose(0, _ => true);
        check("Architecture/last close creates one blank session", bravo.IsDisposed && tabs.Count == 1 && tabs.Current.Path == "" && !tabs.Current.IsDisposed, "window remains usable");
        var blank = tabs.Current;
        tabs.Dispose();
        check("Architecture/window close disposes final document", blank.IsDisposed && tabs.Count == 0, "no replacement tab on shutdown");
    }

    private static void Images(string directory, Action<string, bool, string> check)
    {
        string left = Path.Combine(directory, "left"), right = Path.Combine(directory, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.Copy("../samples/test_png.png", Path.Combine(left, "image.png"));
        File.Copy("../samples/test_jpg.jpg", Path.Combine(right, "image.png"));
        using var first = new DocView { DocumentDirectory = left };
        using var second = new DocView { DocumentDirectory = right };
        first.Load("![left](image.png)");
        second.Load("![right](image.png)");
        nint a = first.GetImageBmp("image.png"), b = second.GetImageBmp("image.png");
        check("Architecture/image paths belong to each document", a != 0 && b != 0 && a != b && first.GetImageBmp("image.png") == a, "same relative name, independent assets");
        if (a != 0)
        {
            int held = Marshal.AddRef(a); // Model a lightbox retaining the bitmap beyond document closure.
            first.Dispose();
            int remaining = Marshal.Release(a);
            check("Architecture/close releases image ownership", remaining == held - 2 && second.GetImageBmp("image.png") == b, "lightbox reference survives, other document remains live");
        }
        if (b != 0)
        {
            int held = Marshal.AddRef(b);
            second.Load("New text without an image.");
            int remaining = Marshal.Release(b);
            check("Architecture/reload releases old images", remaining == held - 2 && second.GetImageBmp("image.png") == 0, "cache lifetime follows document content");
        }
        second.Load("![remote](https://example.com/image.png)");
        check("Architecture/unsupported image stays a placeholder", second.BlockDrawable(0), "invalid local paths do not crash document loading");
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

    private static void Toolbar(Action<string, bool, string> check)
    {
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, 1000, 40), previous = SelectObject(memory, bitmap);
        try
        {
            using var toolbar = new ToolbarView();
            var state = new CmdState(false, false, false, false, false, 16f);
            var rect = new RECT { R = 1000, B = 40 };
            toolbar.Paint(memory, rect, 1f, state, new List<ToolbarTab> { new(Guid.NewGuid(), "Alpha", "", true, false), new(Guid.NewGuid(), "Bravo", "", false, false) });
            check("Architecture/toolbar paints command and tab geometry", toolbar.BtnRects().Length == 6 && toolbar.TabRects.Count == 2, "independent renderer produces hit regions");
            var field = typeof(ToolbarView).GetField("_tabLayouts", BindingFlags.Instance | BindingFlags.NonPublic);
            var cache = (Dictionary<string, (nint lay, float w)>)field.GetValue(toolbar);
            nint alpha = cache["Alpha"].lay, bravo = cache["Bravo"].lay;
            int retained = Marshal.AddRef(alpha);
            toolbar.Paint(memory, rect, 1f, state, new List<ToolbarTab> { new(Guid.NewGuid(), "Bravo", "", true, false), new(Guid.NewGuid(), "Charlie", "", false, false) });
            int remaining = Marshal.Release(alpha);
            check("Architecture/toolbar prunes closed tab layouts", remaining == retained - 2 && !cache.ContainsKey("Alpha") && cache["Bravo"].lay == bravo, "closed title released, live title reused");
            var crowded = Enumerable.Range(0, 12).Select(i => new ToolbarTab(Guid.NewGuid(), "Long document title with emoji 👨‍👩‍👧‍👦 " + i, "", i == 11, false)).ToList();
            var narrow = new RECT { R = 500, B = 40 };
            toolbar.Paint(memory, narrow, 1f, state, crowded);
            var visible = toolbar.TabRects.Where(t => t.w > 0).ToArray();
            var more = toolbar.BtnRects()[5];
            check("UI/narrow tabs keep the active document visible", toolbar.TabRects[11].w > 0 && visible.Length < crowded.Count && toolbar.OverflowRect.w > 0, "overflow preserves access without squeezing every title");
            check("UI/tabs stay outside commands and inside the client", visible.All(t => t.x >= more.x + more.w + 8f && t.x + t.w <= toolbar.OverflowRect.x), "500 DIP window, 12 long names");
            var activeRect = toolbar.TabRects[11];
            check("UI/visible tab routes to its original document index", toolbar.HitTab(activeRect.x + 10f, out bool closeHit) == 11 && !closeHit && toolbar.HitTab(activeRect.x + activeRect.w - 12f, out closeHit) == 11 && closeHit, "hidden tabs do not shift selection or close indices");
            check("Architecture/toolbar hit button matches overflow button index 6",
                toolbar.HitButton(toolbar.OverflowRect.x + 2f, 10f) == 6 && toolbar.FocusTargets.Contains(ToolbarTarget.ForCommand(ToolbarCommand.Documents))
                    && toolbar.FocusTargets.Contains(ToolbarTarget.ForTab(crowded[11].Id, true)),
                "overflow command and visible tab close remain semantic focus targets");
            var b0 = toolbar.BtnRects()[0];
            check("Architecture/toolbar hit button matches normal button index",
                toolbar.HitButton(b0.x + 2f, 10f) == 0 && toolbar.HitButton(b0.x + 2f, 50f) == -1,
                "inside button rect returns index, below toolH returns -1");
            nint trimmed = cache[crowded[11].Title].lay;
            check("UI/long tab title remains a single line", Dw.LayoutW(trimmed) <= activeRect.w - 40f && SingleLine(trimmed), "ellipsis fits the available title area");
            unsafe bool SingleLine(nint layout)
            {
                Dw.Bind(layout);
                DWRITE_TEXT_METRICS metrics = default;
                Dw.GetMetrics(layout, &metrics);
                return metrics.lineCount == 1;
            }
            toolbar.Paint(memory, rect, 1f, state, new List<ToolbarTab> { new(Guid.NewGuid(), "Bravo", "", true, false), new(Guid.NewGuid(), "Charlie", "", false, false) });
            bravo = cache["Bravo"].lay;
            retained = Marshal.AddRef(bravo);
            toolbar.Dispose();
            remaining = Marshal.Release(bravo);
            check("Architecture/window releases toolbar layouts", remaining == retained - 2 && cache.Count == 0, "explicit window ownership");
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
}
