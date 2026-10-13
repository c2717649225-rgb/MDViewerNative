using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static class A1HeadingSurfaceTests
{
    private static readonly FieldInfo Ys = typeof(DocView).GetField("_ys", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo Heights = typeof(DocView).GetField("_heights", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo Entry = typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed record Fixture(string Name, string Markdown, float TopOutset, bool Paper = false);
    private readonly record struct InkPixel(int X, int Y, uint Color);

    internal static void Run(Action<string, bool, string> check)
    {
        string wide = "var value = \"" + new string('W', 180) + "\";";
        var fixtures = new[]
        {
            // A4 E4:非公式代码块上内边距 20 DIP(右上角语言标签/复制按钮带区);字面 HTML 面板 S71 起同为 20。
            new Fixture("plain", "```text\nreturn 42\n```", 20f),
            new Fixture("highlighted", "```csharp\nreturn 42;\n```", 20f),
            new Fixture("wide", "```csharp\n" + wide + "\n```", 20f),
            new Fixture("paper", "```csharp\n" + wide + "\n```", 20f, Paper: true),
            new Fixture("HTML", "<iframe src=\"about:blank\">literal HTML</iframe>", 20f),   // 白名单外标签保持字面面板(阅读器永不渲染 iframe);上内边距与代码面板同为 20
            new Fixture("mermaid", "```mermaid\nflowchart LR\nA[Start] --> B[End]\n```", 20f),
            new Fixture("math", "```math\n\\frac{1}{2}+3\n```", 0f),
            new Fixture("math fallback", "```math\n\\unknown{" + new string('W', 180) + "}\n```", 0f)
        };
        using (new RenderStateScope())
        {
            RenderResources.SetSerif(false, RenderResources.SerifFamily);
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                RenderResources.Scale = scale; // Only this owned offscreen target; no system DPI switch.
                using var bitmap = new MemoryBitmap((int)(960 * scale), (int)(340 * scale));
                foreach (float size in new[] { 12f, 16f, 20f, 28f })
                {
                    RenderResources.SetBodySize(size);
                    foreach (bool dark in new[] { false, true })
                    {
                        RenderResources.ApplyTheme(dark);
                        foreach (int level in new[] { 2, 3 })
                        {
                            string title = new string('#', level) + " H" + level + " Agpqy 中文\n";
                            using var reference = Document(title, false);
                            bitmap.Paint(reference);
                            float headerTop = Values(Ys, reference)[0], headerEnd = headerTop + Values(Heights, reference)[0];
                            var ink = bitmap.Ink(reference.ColX, reference.ColW, headerTop, headerEnd, scale);
                            bool valid = ink.Count > 20;
                            var pixelFailures = new List<string>();
                            var edgeFailures = new List<string>();
                            var hitFailures = new List<string>();
                            float expectedGap = (level == 2 ? 10f : 8f) * size / 16f;
                            foreach (var fixture in fixtures)
                            {
                                using var doc = Document(title + "\n" + fixture.Markdown + "\n", fixture.Paper);
                                bool shape = doc.BlockCount == 2 && doc.Blocks[0] is Heading;
                                if (shape)
                                {
                                    var entry = (DocView.Ent)Entry.Invoke(doc, new object[] { 1 });
                                    shape = fixture.Name switch
                                    {
                                        "HTML" => doc.Blocks[1] is HtmlBlock,
                                        "highlighted" => doc.Blocks[1] is CodeBlock && entry.HL != null,
                                        "wide" => doc.Blocks[1] is CodeBlock && entry.HL != null && entry.OverflowW > 0.5f,
                                        "paper" => doc.Blocks[1] is CodeBlock && entry.HL == null && entry.OverflowW == 0f,
                                        "mermaid" => doc.Blocks[1] is CodeBlock && entry.Mermaid != null,
                                        "math" => doc.Blocks[1] is CodeBlock && entry.NoCodeBg && entry.MathTree != null,
                                        "math fallback" => doc.Blocks[1] is CodeBlock && entry.NoCodeBg && entry.MathTree == null && entry.OverflowW > 0.5f,
                                        _ => doc.Blocks[1] is CodeBlock && entry.L != 0 && !entry.NoCodeBg
                                    };
                                }
                                if (!shape) { valid = false; pixelFailures.Add(fixture.Name + ": wrong render path"); continue; }
                                string sourceText = doc.SearchText();
                                bitmap.Paint(doc);
                                int changed = bitmap.Changed(ink);
                                if (changed != 0) pixelFailures.Add($"{fixture.Name}:{changed}/{ink.Count} heading pixels changed");
                                float bodyTop = Values(Ys, doc)[1], actualHeaderEnd = Values(Ys, doc)[0] + Values(Heights, doc)[0];
                                float outerTop = bodyTop - fixture.TopOutset;
                                float gap = outerTop - actualHeaderEnd;
                                if (MathF.Abs(gap - expectedGap) > 0.01f)
                                    edgeFailures.Add($"{fixture.Name}:edge gap={gap:F3}, expected={expectedGap:F3}");
                                if (fixture.TopOutset != 0f && !RenderResources.HighContrast)
                                {
                                    uint surface = dark ? ThemePalette.SurfaceD : ThemePalette.SurfaceL;
                                    int column = (int)((doc.ColX + doc.ColW * 0.75f) * scale);
                                    int first = bitmap.FirstColorRow(column, (int)(headerTop * scale), (int)MathF.Ceiling((bodyTop + 2f) * scale), surface);
                                    float expectedTopPx = (actualHeaderEnd + expectedGap) * scale;
                                    if (first < 0 || MathF.Abs(first - expectedTopPx) > 1.05f)
                                        edgeFailures.Add($"{fixture.Name}:painted top={first}px, expected={expectedTopPx:F3}px (one-pixel raster allowance)");
                                }
                                if (doc.Blocks[1] is CodeBlock code)
                                {
                                    float x = doc.ColX + 32f;
                                    bool hits = doc.CodeAtPoint(x, actualHeaderEnd - 0.25f) == null
                                        && doc.CodeAtPoint(x, outerTop - 0.25f) == null
                                        && doc.CodeAtPoint(x, outerTop + 0.25f) == code.Text;
                                    if (fixture.Name is "wide" or "math fallback")
                                    {
                                        hits &= !doc.ScrollCodeAt(x, actualHeaderEnd - 0.25f, 40f);
                                        hits &= doc.ScrollCodeAt(x, bodyTop + 4f, 40f);
                                        bitmap.Paint(doc);
                                        hits &= doc.CodeAtPoint(x, bodyTop + 4f) == code.Text && doc.SearchText() == sourceText;
                                        int scrolledChanges = bitmap.Changed(ink);
                                        if (scrolledChanges != 0) pixelFailures.Add($"{fixture.Name}:after horizontal scroll changed={scrolledChanges}");
                                    }
                                    if (!hits) hitFailures.Add(fixture.Name + ": heading/gap captured, code not reachable, or source changed");
                                }
                            }
                            string label = $"H{level} size={size} scale={scale} dark={dark}";
                            check("A1Surface/heading glyphs " + label, valid && pixelFailures.Count == 0,
                                pixelFailures.Count == 0 ? $"{ink.Count} reference glyph pixels preserved across eight render paths" : string.Join("; ", pixelFailures));
                            check("A1Surface/outer gap " + label, valid && edgeFailures.Count == 0,
                                edgeFailures.Count == 0 ? $"visible top gap={expectedGap:F3} DIP; raster border within one pixel; no invented math panel" : string.Join("; ", edgeFailures));
                            check("A1Surface/hit and horizontal scroll " + label, valid && hitFailures.Count == 0,
                                hitFailures.Count == 0 ? "heading/gap excluded, code copy hit and sideways scroll/source preserved" : string.Join("; ", hitFailures));
                        }
                    }
                }
            }
        }
    }

    private static DocView Document(string markdown, bool paper)
    {
        var doc = new DocView { WrapCodeForPaper = paper };
        doc.Load(markdown);
        doc.SetToc(false);
        doc.OnWidth(960f);
        return doc;
    }

    private static float[] Values(FieldInfo field, DocView doc) => (float[])field.GetValue(doc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private sealed unsafe class MemoryBitmap : IDisposable
    {
        private nint _dc, _bitmap, _previous, _bits;
        private readonly int _width, _height;
        internal MemoryBitmap(int width, int height)
        {
            _width = width; _height = height;
            _dc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
            if (_dc != 0 && _bitmap != 0) _previous = SelectObject(_dc, _bitmap);
            if (_dc == 0 || _bitmap == 0 || _bits == 0 || _previous == 0 || _previous == -1)
            { Dispose(); throw new InvalidOperationException("Cannot create the test-owned DIB target."); }
        }
        internal void Paint(DocView doc)
        {
            doc.Paint(new RECT { R = _width, B = _height }, _dc, 0f, _height / RenderResources.Scale, 0f);
            if (!GdiFlush()) throw new InvalidOperationException("Cannot synchronize the test-owned DIB.");
        }
        private uint Pixel(int x, int y) => ((uint*)_bits)[y * _width + x] & 0xFFFFFFu;
        internal List<InkPixel> Ink(float x, float width, float top, float bottom, float scale)
        {
            uint canvas = Pixel(20, 20); // Stay below the top progress track, outside the heading.
            var result = new List<InkPixel>();
            for (int yy = Math.Max(0, (int)MathF.Floor(top * scale) - 2); yy <= Math.Min(_height - 1, (int)MathF.Ceiling(bottom * scale) + 2); yy++)
                for (int xx = Math.Max(0, (int)MathF.Floor(x * scale) - 2); xx < Math.Min(_width, (int)MathF.Ceiling((x + width) * scale) + 2); xx++)
                    if (Pixel(xx, yy) is var color && color != canvas) result.Add(new(xx, yy, color));
            return result;
        }
        internal int Changed(List<InkPixel> reference)
        {
            int changed = 0;
            foreach (var p in reference) if (Pixel(p.X, p.Y) != p.Color) changed++;
            return changed;
        }
        internal int FirstColorRow(int x, int first, int last, uint color)
        {
            for (int y = Math.Max(0, first); y <= Math.Min(_height - 1, last); y++) if (Pixel(x, y) == color) return y;
            return -1;
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

