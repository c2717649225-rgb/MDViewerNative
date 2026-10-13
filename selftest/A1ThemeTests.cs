using System;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static class A1ThemeTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("A1Theme/" + name, ok, "shared tokens; injected system colors do not change desktop settings");
        var mappings = new (MarkdownSourceKind Kind, uint Light, uint Dark)[]
        {
            (MarkdownSourceKind.Text, ThemePalette.InkL, ThemePalette.InkD),
            (MarkdownSourceKind.Heading, ThemePalette.AccentL, ThemePalette.AccentD),
            (MarkdownSourceKind.Marker, ThemePalette.MutedL, ThemePalette.MutedD),
            (MarkdownSourceKind.Code, ThemePalette.CommentL, ThemePalette.CommentD),
            (MarkdownSourceKind.Link, ThemePalette.AccentL, ThemePalette.AccentD),
            (MarkdownSourceKind.Emphasis, ThemePalette.MutedL, ThemePalette.MutedD)
        };
        foreach (bool dark in new[] { false, true })
        {
            var theme = MarkdownSourceColors.CreateTheme(dark);
            C("canvas " + dark, theme.Background == ThemePalette.ToColorRef(dark ? ThemePalette.CanvasD : ThemePalette.CanvasL));
            C("ink " + dark, theme.Text == ThemePalette.ToColorRef(dark ? ThemePalette.InkD : ThemePalette.InkL));
            foreach (var mapping in mappings)
            {
                uint color = theme.Color(mapping.Kind);
                C("token " + dark + "/" + mapping.Kind, color == ThemePalette.ToColorRef(dark ? mapping.Dark : mapping.Light));
                C("contrast " + dark + "/" + mapping.Kind, Contrast(color, theme.Background) >= 4.5);
            }
            const uint systemBackground = 0x00010203u, systemText = 0x00ABCDEFu;
            var highContrast = MarkdownSourceColors.CreateTheme(dark, true, systemBackground, systemText);
            C("system background unchanged " + dark, highContrast.Background == systemBackground);
            bool allSystemText = highContrast.Text == systemText;
            foreach (var mapping in mappings) allSystemText &= highContrast.Color(mapping.Kind) == systemText;
            C("all syntax uses system text " + dark, allSystemText);
            C("native theme override disabled in high contrast " + dark, WindowChrome.GetScrollbarTheme(dark, true) == null);
        }
        ControlBrushes(check);
    }

    private static double Contrast(uint foreground, uint background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(uint colorRef)
    {
        static double Linear(uint channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(colorRef & 255) + 0.7152 * Linear((colorRef >> 8) & 255)
            + 0.0722 * Linear((colorRef >> 16) & 255);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogBrush { public uint Style, Color; public nint Hatch; }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetBrush(nint brush, int size, out LogBrush value);
    [DllImport("gdi32.dll")] private static extern uint GetBkColor(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetTextColor(nint dc);

    private static void ControlBrushes(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("A1Theme/control brush " + name, ok, "owned memory DC; injected high-contrast colors, no system setting changes");
        static bool Matches(nint dc, nint brush, SourceColorTheme theme)
            => GetBrush(brush, Marshal.SizeOf<LogBrush>(), out var value) != 0 && value.Color == theme.Background
                && GetBkColor(dc) == theme.Background && GetTextColor(dc) == theme.Text;
        nint dc = CreateCompatibleDC(0);
        C("DC available", dc != 0);
        if (dc == 0) return;
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                var normal = MarkdownSourceColors.CreateTheme(dark);
                nint brush = RenderResources.ControlColors(dc, normal);
                C("normal " + dark, Matches(dc, brush, normal));
                C("unchanged palette reuses brush " + dark, RenderResources.ControlColors(dc, normal) == brush);
                var first = MarkdownSourceColors.CreateTheme(dark, true, 0x00010203u, 0x00EEDDCCu);
                C("enter high contrast " + dark, Matches(dc, RenderResources.ControlColors(dc, first), first));
                var changed = MarkdownSourceColors.CreateTheme(dark, true, 0x00040506u, 0x00FFFFFFu);
                C("system background change without mode change " + dark, Matches(dc, RenderResources.ControlColors(dc, changed), changed));
                C("leave high contrast " + dark, Matches(dc, RenderResources.ControlColors(dc, normal), normal));
            }
        }
        finally
        {
            RenderResources.ControlColors(dc);
            DeleteDC(dc);
        }
    }

}
