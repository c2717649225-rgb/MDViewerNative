using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MDViewerNative;

// A5 B1:字号 × 客户区宽度矩阵下的 A4 元素几何(代码外框、复制按钮、语言标签、宽内容可视窗、列宽单调)。
internal static class A5MatrixTests
{
    private const float Top = 36f;
    private static readonly float[] Sizes = { 12f, 16f, 20f, 24f, 28f };
    private static readonly float[] Widths = { 360f, 640f, 960f, 1280f };

    private const string Sample =
        "# Matrix sample\n\nIntro paragraph with a [link](https://example.com) and enough words to wrap across narrow columns.\n\n" +
        "```python\ndef greet(name):\n    return name\n```\n\n" +
        "```typescriptreact-extended-language-name\nconst x = 1;\n```\n\n" +
        "```csharp\nvar veryLongLine = \"this line is deliberately long enough to overflow the code viewport at every width\";\n```\n\n" +
        "| Column one | Column two with a much longer header | Column three with a longer header | Column four |\n| --- | --- | --- | --- |\n| a | b | c | d |\n\n" +
        "> Quoted words.\n>\n> ```text\n> quoted code\n> ```\n\nClosing paragraph.\n";

    internal static void Run(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            RenderResources.Scale = 1f;
            foreach (float size in Sizes)
            {
                RenderResources.SetBodySize(size);
                float previousColW = -1f;
                foreach (float v in Widths) Cell(check, size, v, ref previousColW);
            }
        }
    }

    private static void Cell(Action<string, bool, string> check, float size, float v, ref float previousColW)
    {
        int width = (int)v, height = 4000;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height), old = SelectObject(memory, bitmap);
        try
        {
            using var doc = new DocView();
            doc.Load(Sample);
            doc.OnWidth(v);
            doc.Paint(new RECT { R = width, B = height }, memory, 0f, height - Top, Top);
            var problems = new List<string>();
            int panels = 0;
            for (int n = 0; n < 8; n++)
            {
                if (!doc.CodeChromeForTest(n, out var box, out var chip, out var label, out float textTop, out _, out _)) break;
                panels++;
                if (box.X < -0.01f || box.X + box.W > v + 0.01f) problems.Add($"#{n} panel {box.X:0.#}..{box.X + box.W:0.#} outside 0..{v}");
                if (chip.X < box.X - 0.01f || chip.X + chip.W > box.X + box.W + 0.01f || chip.Y < box.Y - 0.01f) problems.Add($"#{n} chip outside panel");
                if (chip.Y + chip.H > textTop + 0.01f) problems.Add($"#{n} chip covers first line ({chip.Y + chip.H:0.#}>{textTop:0.#})");
                if (label.W > 0f)
                {
                    if (label.X + label.W > chip.X - 5.9f) problems.Add($"#{n} label touches chip");
                    if (label.X < box.X + 4f) problems.Add($"#{n} label leaves panel ({label.X:0.#}<{box.X + 4f:0.#})");
                    if (label.Y + label.H > textTop + 0.01f) problems.Add($"#{n} label covers first line");
                }
            }
            if (panels < 4) problems.Add($"panels={panels}");
            check($"A5Matrix/code chrome fits size={size} V={v}", problems.Count == 0, string.Join("; ", problems));
            float colW = doc.ColW, expected = WindowFit.ColW(v);
            bool monotonic = previousColW < 0f || colW >= previousColW - 0.01f;
            check($"A5Matrix/column follows the formula and never shrinks as V grows size={size} V={v}",
                MathF.Abs(colW - expected) < 0.51f && doc.ColX >= -0.01f && doc.ColX + colW <= v + 0.01f && monotonic,
                $"colW={colW:0.##} expected={expected:0.##} colX={doc.ColX:0.##} previous={previousColW:0.##}");
            previousColW = colW;
        }
        finally
        {
            SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}

