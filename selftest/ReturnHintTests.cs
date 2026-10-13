using System;
using System.Text;
using MDViewerNative;

// 「返回原阅读处」:状态(到期、悬停延后)与离屏几何/绘制。
internal static class ReturnHintTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        var hint = new ReturnHint();
        hint.Show();
        check("ReturnHint/expires when not hovered", hint.Expire() && !hint.Visible, "");
        hint.Show(); hint.SetHover(true);
        bool kept = !hint.Expire() && hint.Visible;
        bool goneOnLeave = hint.SetHover(false) && !hint.Visible;
        check("ReturnHint/hover defers expiry until the pointer leaves", kept && goneOnLeave, $"kept={kept} gone={goneOnLeave}");

        using (new RenderStateScope())
        {
            RenderResources.SetBodySize(16f);
            foreach (bool dark in new[] { false, true })
                foreach (float scale in new[] { 1f, 1.5f })
                {
                    RenderResources.ApplyTheme(dark);
                    RenderResources.Scale = scale;
                    Paint(check, dark, scale);
                }
        }
    }

    private static void Paint(Action<string, bool, string> check, bool dark, float scale)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("Paragraph ").Append(i).Append(" with plain body text.\n\n");
        using var doc = new DocView();
        doc.Load(sb.ToString());
        using var frame = new OffscreenFrame((int)(1000 * scale), (int)(400 * scale));
        doc.OnWidth(1000f);
        var box = doc.ReturnHintBox(OffscreenFrame.Top, frame.ViewH);
        string tag = $"{(dark ? "dark" : "light")} x{scale}";
        float centre = box.X + box.W / 2f - (doc.ColX + doc.ColW / 2f);
        float gap = OffscreenFrame.Top + frame.ViewH - (box.Y + box.H);
        check("ReturnHint/centred on the column above the bottom edge " + tag, MathF.Abs(centre) <= 1f && MathF.Abs(gap - 16f) <= 1f, $"centre={centre:0.##} gap={gap:0.##}");

        uint border = OffscreenFrame.Colorref(dark ? ThemePalette.ControlBorderD : ThemePalette.ControlBorderL);
        int x = (int)MathF.Round(box.X * scale), y = (int)((box.Y + box.H / 2f) * scale);
        frame.Paint(doc, 0f);
        uint without = frame.At(x, y);
        var hint = new ReturnHint(); hint.Show();
        frame.Paint(doc, 0f, hint);
        uint with = frame.At(x, y);
        check("ReturnHint/draws a crisp border only when visible " + tag, with == border && without != border, $"with={with:X6} without={without:X6} want={border:X6}");
    }
}

