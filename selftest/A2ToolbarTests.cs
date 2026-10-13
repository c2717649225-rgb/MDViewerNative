using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MDViewerNative;
using static MDViewerNative.RenderResources;

internal static unsafe class A2ToolbarTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        var id = Guid.NewGuid();
        var literal = new ToolbarTab(id, "A&B • *.md", @"C:\literal\A&B • *.md", true, true, true, true);
        check("A2Toolbar/independent identity and literal title", literal.Id == id && literal.Title == "A&B • *.md" && (literal with { Dirty = false }).Title == literal.Title, "status is not parsed out of a filename");
        check("A2Toolbar/full path keeps dirty/read-only/pinned state", literal.MenuLabel(true).Contains(literal.FullPath) && literal.MenuLabel(true).Contains("未保存") && literal.MenuLabel(true).Contains("只读") && literal.MenuLabel(true).Contains("固定"), "path substitution never drops state");
        check("A2Toolbar/close hint warns about unsaved source", literal.CloseHint.StartsWith("关闭未保存文档") && literal.Hint.Contains(literal.FullPath), "visible text status in addition to Warn color");
        check("A2Toolbar/command eligibility preserves gates", !ToolbarView.ButtonEnabled(1, new(false, false, false, false, false, 16, ReadOnly: true))
            && ToolbarView.ButtonEnabled(0, new(false, false, false, false, false, 16, ReadOnly: true))
            && !ToolbarView.ButtonEnabled(5, new(false, false, false, false, false, 16, Composing: true)), "readonly edit disabled; readonly open retained; composition does not enable toolbar commands");
        using (new RenderStateScope())
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Scale = scale;
                using var image = new BitmapTarget((int)(1000 * scale), (int)(36 * scale));
                using var toolbar = new ToolbarView();
                foreach (bool dark in new[] { false, true })
                {
                    ApplyTheme(dark);
                    var cleanEditing = new CmdState(true, false, false, false, false, 16f);
                    image.Paint(toolbar, cleanEditing, scale);
                    var save = toolbar.BtnRects()[2];
                    float cx = save.x + save.w - 10f, cy = ToolH / 2f;
                    image.Circle(cx, cy);
                    var expectedCircle = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                    image.Paint(toolbar, cleanEditing with { Dirty = true }, scale);
                    var actualCircle = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                    check($"A2Toolbar/save is actual 4DIP circle scale={scale} dark={dark}", Equal(expectedCircle, actualCircle), "compared to an independently drawn radius-2 rounded primitive, not merely presence of Warn pixels");

                    var clean = new CmdState(false, false, false, false, false, 16f);
                    image.Paint(toolbar, clean, scale);
                    save = toolbar.BtnRects()[2];
                    var disabledText = image.Read(save.x + 5f, 5f, save.w - 21f, 26f, scale);
                    image.Paint(toolbar, clean with { Dirty = true, ReadOnly = true }, scale);
                    check($"A2Toolbar/read-only save uses disabled text scale={scale} dark={dark}",
                        Equal(disabledText, image.Read(save.x + 5f, 5f, save.w - 21f, 26f, scale)), "visible eligibility must follow existing read-only command gate");
                    image.Paint(toolbar, clean with { Dirty = true, Composing = true }, scale);
                    check($"A2Toolbar/composition save uses disabled text scale={scale} dark={dark}",
                        Equal(disabledText, image.Read(save.x + 5f, 5f, save.w - 21f, 26f, scale)), "composition cannot advertise save as executable");

                    var first = new ToolbarTab(Guid.NewGuid(), "项目 A&B • 长标题名字不会随脏态改变.md", @"C:\one\项目 A&B • 长标题名字不会随脏态改变.md", false, false);
                    var second = new ToolbarTab(Guid.NewGuid(), "Second.md", @"C:\two\Second.md", true, false);
                    var tabs = new List<ToolbarTab> { first, second };
                    image.Paint(toolbar, clean, scale, tabs);
                    var rect = toolbar.TabRects[0];
                    var activeRect = toolbar.TabRects[1];
                    uint canvas = image.PixelDip(1f, 2f, scale);
                    image.Paint(toolbar, clean with { Dirty = true, FindOpen = true }, scale, tabs, hidden: true);
                    var zenBlank = image.Read(0f, 0f, 1000f, ToolH, scale);
                    check($"A2Toolbar/Zen 隐藏时整条工具栏带只有画布 scale={scale} dark={dark}", Array.TrueForAll(zenBlank, p => p == canvas),
                        "按钮、标签、脏点、下划线与分隔线都不绘制;工具栏几何与正文坐标不变");
                    image.Paint(toolbar, clean, scale, tabs);
                    check($"A2Toolbar/B2 idle active tab has no permanent fill scale={scale} dark={dark}", image.PixelDip(activeRect.x + 3f, 18f, scale) == canvas, "active underline remains the persistent indicator, not a solid tab background");
                    var idleClose = image.Read(rect.x + rect.w - 20f, 11f, 14f, 14f, scale);
                    check($"A2Toolbar/B2 idle clean close slot is blank scale={scale} dark={dark}", Array.TrueForAll(idleClose, p => p == canvas), "mouse-capable idle clean tab shows neither marker nor close cross");
                    check($"A2Toolbar/B2 tab and close keyboard targets exist scale={scale} dark={dark}", toolbar.FocusCount == 9, "five enabled commands plus two tab-body/two close targets, not legacy six-only focus");
                    var title = image.Read(rect.x, 0f, rect.w - 30f, ToolH, scale);
                    var slot = image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale);
                    tabs = new() { first with { Dirty = true }, second };
                    image.Paint(toolbar, clean, scale, tabs);
                    check($"A2Toolbar/dirty keeps title pixels and budget scale={scale} dark={dark}",
                        toolbar.TabRects[0] == rect && Equal(title, image.Read(rect.x, 0f, rect.w - 30f, ToolH, scale)), "same display title, no status suffix, unchanged title rectangle/ellipsis pixels");
                    check($"A2Toolbar/dirty changes only independent tail slot scale={scale} dark={dark}",
                        !Equal(slot, image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale)) && toolbar.HitTab(rect.x + rect.w - 2f, out bool close) == 0 && close,
                        "fixed 28DIP close area remains reachable; circle does not shift title");
                    var idleDirty = image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale);
                    toolbar.MovePointer(rect.x + 8f, 18f);
                    image.Paint(toolbar, clean, scale, tabs);
                    check($"A2Toolbar/B2 hover reveals close without moving title box scale={scale} dark={dark}",
                        toolbar.TabRects[0] == rect && !Equal(idleDirty, image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale)), "Warn cross replaces dirty point in fixed slot; hover background is permitted");
                    toolbar.LeavePointer();
                    var closeTarget = ToolbarTarget.ForTab(first.Id, true);
                    toolbar.FocusIndex = new List<ToolbarTarget>(toolbar.FocusTargets).IndexOf(closeTarget);
                    image.Paint(toolbar, clean, scale, tabs);
                    check($"A2Toolbar/B2 keyboard close has separate focus rectangle scale={scale} dark={dark}",
                        toolbar.FocusedTarget == closeTarget && toolbar.RectOf(closeTarget).w == 28f && !Equal(idleDirty, image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale)), "focus is a stable close target, not a title-text mutation");
                    // Review #6: the focus rectangle and the active tab's 2 DIP Accent underline must not touch.
                    var activeBody = ToolbarTarget.ForTab(second.Id);
                    toolbar.FocusIndex = new List<ToolbarTarget>(toolbar.FocusTargets).IndexOf(activeBody);
                    image.Paint(toolbar, clean, scale, tabs);
                    var focusedActive = toolbar.RectOf(activeBody);
                    check($"A2Toolbar/review #6 focus ring clears the active underline scale={scale} dark={dark}",
                        toolbar.FocusedTarget == activeBody && image.PixelDip(focusedActive.x + focusedActive.w / 2f, ToolH - 3.75f, scale) == canvas,
                        "a canvas gap separates the focus rectangle's bottom edge from the Accent line");
                    toolbar.ClearFocus(); toolbar.AlwaysShowClose = true;
                    image.Paint(toolbar, clean, scale, tabs);
                    check($"A2Toolbar/B2 no-hover fallback keeps close and dirty state scale={scale} dark={dark}",
                        !Equal(idleDirty, image.Read(rect.x + rect.w - 28f, 0f, 28f, ToolH, scale)), "conservative no-hover fallback retains both signals");
                    toolbar.AlwaysShowClose = false;
                    toolbar.LeavePointer();
                    image.Paint(toolbar, clean with { Composing = true }, scale, tabs);
                    var blockedSlot = image.Read(rect.x, 0f, rect.w, ToolH, scale);
                    toolbar.MovePointer(rect.x + rect.w - 12f, 18f);
                    image.Paint(toolbar, clean with { Composing = true }, scale, tabs);
                    check($"A2Toolbar/B2 composition hover does not advertise close scale={scale} dark={dark}",
                        Equal(blockedSlot, image.Read(rect.x, 0f, rect.w, ToolH, scale)), "dirty status remains, disabled tab interaction does not reveal an enabled close affordance");
                    toolbar.LeavePointer();
                    toolbar.Prepare(1000f, cleanEditing with { Dirty = true }, null);
                    toolbar.MovePointer(save.x + 8f, 18f);
                    toolbar.BeginPointer(save.x + 8f, 18f);   // a real press, not a painter-only override (review #5)
                    image.Paint(toolbar, cleanEditing with { Dirty = true }, scale);
                    toolbar.CancelPointer(); toolbar.LeavePointer();
                    if (!HighContrast)
                        check($"A2Toolbar/pressed dirty save keeps contrasting surface scale={scale} dark={dark}",
                            image.PixelDip(save.x + 3f, 18f, scale) == (dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL), "Warn remains on Surface, not the below-3:1 light Selection combination");

                }
            }
        }
    }

    private static bool Equal(uint[] a, uint[] b) => a.AsSpan().SequenceEqual(b);

    // A2-B5 integration: the B4 status carriers drawn at every offscreen scale and theme,
    // plus UI geometry independence from body font size and theme.
    internal static void RunIntegration(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail) => check("A2Integration/" + name, ok, detail);
        using (new RenderStateScope())
        {
            var hidden = new ToolbarTab(Guid.NewGuid(), "Hidden.md", @"C:\h\Hidden.md", false, false);
            var active = new ToolbarTab(Guid.NewGuid(), "Active.md", @"C:\a\Active.md", true, false);
            var clean = new CmdState(false, false, false, false, false, 16f);
            int foldWidth = 0;
            using (var probe = new ToolbarView())
                for (int w = 320; w >= 160 && foldWidth == 0; w -= 4)
                {
                    probe.Prepare(w, clean, null);
                    if (probe.FoldedCommands.Contains(ToolbarCommand.Save)) foldWidth = w;
                }
            C("single-document Save folds at a measured width", foldWidth > 0, $"widest folding width={foldWidth} DIP");
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Scale = scale;
                foreach (bool dark in new[] { false, true })
                {
                    ApplyTheme(dark);
                    string at = $" scale={scale} dark={dark}";
                    using (var image = new BitmapTarget((int)(480 * scale), (int)(36 * scale)))
                    using (var toolbar = new ToolbarView())
                    {
                        var cleanTabs = new List<ToolbarTab> { hidden, active };
                        var dirtyTabs = new List<ToolbarTab> { hidden with { Dirty = true }, active };
                        image.Paint(toolbar, clean, scale, cleanTabs);
                        var o = toolbar.OverflowRect;
                        bool layout = o.w > 0 && toolbar.TabRects[0].w <= 0 && toolbar.TabRects[1].w > 0;
                        var (cx, cy) = toolbar.OverflowDotCenter;
                        bool inside = cx - 2f >= o.x && cx + 2f <= o.x + o.w && cy - 2f >= 4f;
                        var glyph = image.Read(o.x, 11f, o.w, ToolH - 11f, scale);
                        image.Circle(cx, cy);
                        var expected = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                        image.Paint(toolbar, clean, scale, cleanTabs);
                        image.Circle(cx + 1f, cy);
                        var shifted = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                        image.Paint(toolbar, clean, scale, dirtyTabs);
                        var actual = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                        C("hidden dirty document marks the list entry with a 4DIP circle" + at,
                            layout && inside && toolbar.OverflowDirty && Equal(expected, actual), "independently drawn radius-2 primitive at the entry's top-right corner");
                        C("list-entry circle comparison detects a 1DIP shift" + at, !Equal(shifted, actual), "negative control: the pixel comparison is position-sensitive");
                        C("list-entry dot leaves the +N count pixels unchanged" + at, Equal(glyph, image.Read(o.x, 11f, o.w, ToolH - 11f, scale)), "the corner dot stays above the count's glyphs");
                    }
                    if (foldWidth == 0) continue;
                    using (var image = new BitmapTarget((int)(foldWidth * scale), (int)(36 * scale)))
                    using (var toolbar = new ToolbarView())
                    {
                        image.Paint(toolbar, clean, scale);
                        var more = toolbar.BtnRects()[5];
                        float cx = more.x + more.w - 4f, cy = 9f;
                        var label = image.Read(more.x, 0f, more.w - 8f, ToolH, scale);
                        image.Circle(cx, cy);
                        var expected = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                        image.Paint(toolbar, clean with { Dirty = true }, scale);
                        var actual = image.Read(cx - 3f, cy - 3f, 6f, 6f, scale);
                        C("folded Save moves the unsaved dot to More as a 4DIP circle" + at,
                            toolbar.DirtyMarkerTarget.Command == ToolbarCommand.More && toolbar.BtnRects()[2].w == 0 && Equal(expected, actual), "badge in More's right padding");
                        C("More badge leaves the More label unchanged" + at, Equal(label, image.Read(more.x, 0f, more.w - 8f, ToolH, scale)), "badge never overlaps the label");
                    }
                }
            }
            Scale = 1f;
            ApplyTheme(false);
            string Geometry(ToolbarView t)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var r in t.BtnRects()) sb.Append($"{r.x:F3}/{r.w:F3};");
                if (t.TabRects != null) foreach (var r in t.TabRects) sb.Append($"{r.x:F3}/{r.w:F3};");
                sb.Append($"|{t.OverflowRect.x:F3}/{t.OverflowRect.w:F3}|{string.Join(",", t.FoldedCommands)}|{t.FocusTargets.Count}");
                return sb.ToString();
            }
            bool fontIndependent = true, themeIndependent = true;
            string firstDiff = "";
            foreach (float w in new[] { 468f, 640f, 960f, 1280f })
                foreach (int n in new[] { 1, 2, 20 })
                {
                    var tabs = new List<ToolbarTab>();
                    for (int i = 0; i < n; i++) tabs.Add(new ToolbarTab(Guid.NewGuid(), $"文档{i}.md", $@"C:\d{i}\文档{i}.md", i == n / 2, i == 0));
                    using var toolbar = new ToolbarView();
                    toolbar.Prepare(w, clean, tabs);
                    string baseline = Geometry(toolbar);
                    foreach (float font in new[] { 20f, 24f, WindowGeometry.MaxBodyFont })
                    {
                        toolbar.Prepare(w, clean with { BodyFont = font }, tabs);
                        if (Geometry(toolbar) != baseline) { fontIndependent = false; firstDiff = $"w={w} n={n} font={font}"; }
                    }
                    ApplyTheme(true);
                    using (var darkBar = new ToolbarView())
                    {
                        darkBar.Prepare(w, clean, tabs);
                        if (Geometry(darkBar) != baseline) { themeIndependent = false; firstDiff = $"w={w} n={n} dark"; }
                    }
                    ApplyTheme(false);
                }
            C("toolbar geometry is independent of body font size 16-28", fontIndependent, "UI stays at 13 DIP; widths 468/640/960/1280 x 1/2/20 tabs " + firstDiff);
            C("toolbar geometry is independent of theme", themeIndependent, "light and dark lay out identically " + firstDiff);

            // A2-B5 decisions: the list entry counts hidden documents; narrow qualified names keep the file name.
            using (var bar = new ToolbarView())
            {
                List<ToolbarTab> Many(int n)
                {
                    var list = new List<ToolbarTab>();
                    for (int i = 0; i < n; i++) list.Add(new ToolbarTab(Guid.NewGuid(), $"d{i}.md", $@"C:\d\d{i}.md", i == n - 1, false));
                    return list;
                }
                bar.Prepare(480f, clean, Many(20));
                C("list entry shows the hidden count", bar.HiddenCount == 19 && bar.OverflowLabel == "+19" && bar.OverflowRect.w == 32f, $"hidden={bar.HiddenCount} label={bar.OverflowLabel}");
                bar.Prepare(480f, clean, Many(120));
                C("large hidden counts are capped as 99+", bar.OverflowLabel == "99+", bar.OverflowLabel);
                bar.Prepare(1280f, clean, Many(2));
                C("no hidden documents means no list entry and no count", bar.HiddenCount == 0 && bar.OverflowRect.w == 0f, $"hidden={bar.HiddenCount}");
            }
            static float Chars(string s) => s.Length;
            C("quiet labels stay Muted in normal themes", !ToolbarView.LabelInk(false, true, false) && ToolbarView.LabelInk(true, true, false), "inactive tab and +N are Muted until hovered/focused");
            C("high contrast never paints an enabled label as GrayText", ToolbarView.LabelInk(false, true, true), "COLOR_GRAYTEXT means disabled");
            C("disabled labels stay Muted in every theme", !ToolbarView.LabelInk(true, false, false) && !ToolbarView.LabelInk(true, false, true), "composition / disabled keep the disabled colour");
            C("fit keeps a qualified title that already fits", ToolbarView.FitTabTitle("one › README", 20f, Chars) == "one › README", "no change when there is room");
            string fitted = ToolbarView.FitTabTitle("folder-2000 › README", 16f, Chars);
            C("fit shrinks the qualifier from its far end and keeps the whole file name",
                fitted.StartsWith("…") && fitted.EndsWith(" › README") && fitted.Contains("2000") && fitted.Length <= 16, fitted);
            C("fit falls back to the bare file name", ToolbarView.FitTabTitle("folder-2000 › README", 8f, Chars) == "README", "DirectWrite trimming handles what still overflows");
            C("fit leaves unqualified titles to DirectWrite trimming", ToolbarView.FitTabTitle("长标题没有目录前缀.md", 3f, Chars) == "长标题没有目录前缀.md", "only qualified names are re-ordered");
            string surrogate = ToolbarView.FitTabTitle("\U0001F4D8dir › a", 8f, Chars);
            bool paired = true;
            for (int i = 0; i < surrogate.Length; i++)
                if (char.IsHighSurrogate(surrogate[i]) ? i + 1 >= surrogate.Length || !char.IsLowSurrogate(surrogate[++i]) : char.IsLowSurrogate(surrogate[i])) paired = false;
            C("fit never splits a surrogate pair in the qualifier", paired && surrogate.EndsWith(" › a"), surrogate);

            // Toolbar token contrast (WCAG relative luminance on the palette the toolbar paints with):
            // Ink/Muted are readable text (Muted = inactive tab labels) >= 4.5:1; Warn (dot, dirty close
            // cross) and Accent (focus ring, active line) are non-text indicators >= 3:1. Backgrounds are
            // Canvas and Surface (hover/press/list entry). Disabled command text is exempt and not asserted.
            static double Lum(uint rgb)
            {
                static double F(uint v) { double c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                return 0.2126 * F((rgb >> 16) & 0xFF) + 0.7152 * F((rgb >> 8) & 0xFF) + 0.0722 * F(rgb & 0xFF);
            }
            static double Ratio(uint a, uint b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
            foreach (bool dark in new[] { false, true })
            {
                uint canvas = dark ? ThemePalette.CanvasD : ThemePalette.CanvasL, surface = dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL;
                foreach (var (name, fg, min) in new[] {
                    ("ink text", dark ? ThemePalette.InkD : ThemePalette.InkL, 4.5),
                    ("muted inactive-tab text", dark ? ThemePalette.MutedD : ThemePalette.MutedL, 4.5),
                    ("warn unsaved dot", dark ? ThemePalette.WarnD : ThemePalette.WarnL, 3.0),
                    ("accent focus ring and active line", dark ? ThemePalette.AccentD : ThemePalette.AccentL, 3.0) })
                {
                    double onCanvas = Ratio(fg, canvas), onSurface = Ratio(fg, surface);
                    C($"token contrast {name} dark={dark}", onCanvas >= min && onSurface >= min, $"canvas={onCanvas:F2} surface={onSurface:F2} min={min}");
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public Header Header; public uint Color; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref Info info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private sealed class BitmapTarget : IDisposable
    {
        private nint _dc, _bitmap, _previous, _bits;
        private readonly int _width, _height;
        internal BitmapTarget(int width, int height)
        {
            _width = width; _height = height;
            _dc = CreateCompatibleDC(0);
            var info = new Info { Header = new Header { Size = (uint)Marshal.SizeOf<Header>(), Width = width, Height = -height, Planes = 1, Bits = 32 } };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
            if (_dc != 0 && _bitmap != 0) _previous = SelectObject(_dc, _bitmap);
            if (_dc == 0 || _bitmap == 0 || _bits == 0 || _previous == 0 || _previous == -1) { Dispose(); throw new InvalidOperationException("Cannot create owned toolbar DIB."); }
        }
        internal void Paint(ToolbarView toolbar, CmdState state, float scale, IReadOnlyList<ToolbarTab> tabs = null, bool hidden = false)
        {
            toolbar.Paint(_dc, new RECT { R = _width, B = _height }, scale, state, tabs, hidden);
            GdiFlush();
        }
        internal void Circle(float x, float y)
        {
            PBegin(Rt);
            var circle = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = x - 2f, top = y - 2f, right = x + 2f, bottom = y + 2f }, radiusX = 2f, radiusY = 2f };
            PFillRound(Rt, &circle, BWarn);
            ulong a = 0, b = 0;
            PEnd(Rt, &a, &b);
            GdiFlush();
        }
        internal uint PixelDip(float x, float y, float scale) => ((uint*)_bits)[(int)(y * scale) * _width + (int)(x * scale)] & 0xFFFFFFu;
        internal uint[] Read(float x, float y, float width, float height, float scale)
        {
            int left = Math.Max(0, (int)MathF.Floor(x * scale)), top = Math.Max(0, (int)MathF.Floor(y * scale));
            int right = Math.Min(_width, (int)MathF.Ceiling((x + width) * scale)), bottom = Math.Min(_height, (int)MathF.Ceiling((y + height) * scale));
            var pixels = new uint[(right - left) * (bottom - top)];
            int i = 0;
            for (int yy = top; yy < bottom; yy++) for (int xx = left; xx < right; xx++) pixels[i++] = ((uint*)_bits)[yy * _width + xx] & 0xFFFFFFu;
            return pixels;
        }
        public void Dispose()
        {
            if (_previous != 0 && _previous != -1) SelectObject(_dc, _previous);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_dc != 0) DeleteDC(_dc);
            _dc = _bitmap = _previous = _bits = 0;
        }
    }
}

