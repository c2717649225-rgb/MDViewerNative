using System;
using System.Linq;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static unsafe class SourceColorTests
{
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("SourceColor/" + name, ok, "real RichEdit, UTF-16 source contract");
        string text = "# 中文 😀\r\nplain **strong** [label](url)\r\n```cs\r\nvar x = 1;\r\n```\r\nlast";
        var spans = MarkdownSourceColors.Scan(text);
        C("heading classified", spans.Any(s => s.Start == 0 && s.Kind == MarkdownSourceKind.Heading));
        C("fenced body classified", spans.Any(s => s.Kind == MarkdownSourceKind.Code && text.Substring(s.Start, s.Length) == "var x = 1;"));
        C("escaped delimiters stay plain", MarkdownSourceColors.Scan("\\*plain\\*").Count == 0);
        C("inline code shields markup", MarkdownSourceColors.Scan("`[a] **b**`").Count == 1);
        C("oversize source has bounded plain fallback", MarkdownSourceColors.Scan(new string('*', MarkdownSourceColors.CharacterLimit + 1)).Count == 0);
        C("excessive spans have bounded plain fallback", MarkdownSourceColors.Scan(new string('*', MarkdownSourceColors.SpanLimit + 1)).Count == 0);
        var coordinates = new EditorCoordinates(text);
        bool roundTrip = true;
        for (int i = 0; i <= text.Length; i++)
            if (i == text.Length || text[i] != '\n')
                roundTrip &= coordinates.ToExternal(coordinates.ToNative(i)) == i;
        C("all Unicode and paragraph coordinates round trip", roundTrip);
        C("negative selection sentinels preserved", coordinates.ToNative(-1) == -1 && coordinates.ToExternal(-1) == -1);

        nint parent = Externs.CreateWindowExW(0, "STATIC", "source-color-test", 0, 0, 0, 640, 480, 0, 0, Externs.GetModuleHandleW(null), 0);
        if (parent == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var editor = new EditorControl(parent, 1f, () => { }, () => null);
            nint h = editor.Handle;
            C("native background follows shared canvas", BackgroundAt(h) == RenderResources.SourceTheme.Background);
            Externs.SetWindowTextW(h, text);
            int last = text.IndexOf("last", StringComparison.Ordinal);
            Externs.SendMessageW(h, 0x00B1, last, last + 4);
            C("selection after many CRLF is exact", Externs.EditSelection(h) == (last, last + 4));
            C("line index uses external coordinates", (int)Externs.SendMessageW(h, 0x00BB, 5, 0) == last);
            C("line from character uses external coordinates", (int)Externs.SendMessageW(h, 0x00C9, last, 0) == 5);
            fixed (char* p = "tail") Externs.SendMessageW(h, 0x00C2, 1, (nint)p);
            string edited = text[..last] + "tail";
            var position = editor.CapturePosition();
            nint modified = Externs.SendMessageW(h, 0x00B8, 0, 0);
            editor.ApplyHighlight();
            C("formatting preserves exact Unicode source", Externs.WindowText(h) == edited);
            C("formatting preserves selection and viewport", editor.CapturePosition() == position);
            C("formatting preserves modified flag", Externs.SendMessageW(h, 0x00B8, 0, 0) == modified);
            C("actual heading color applied", ColorAt(h, 0) == RenderResources.SourceTheme.Color(MarkdownSourceKind.Heading));
            C("actual body color applied", ColorAt(h, text.IndexOf("plain", StringComparison.Ordinal)) == RenderResources.SourceTheme.Color(MarkdownSourceKind.Text));
            C("actual fenced code color applied", ColorAt(h, text.IndexOf("var x", StringComparison.Ordinal)) == RenderResources.SourceTheme.Color(MarkdownSourceKind.Code));
            C("gutter follows actual native line positions", editor.LineTop(2) > editor.LineTop(1) && editor.LineTop(1) > editor.LineTop(0));
            bool dark = RenderResources.DarkMode;
            try
            {
                RenderResources.ApplyTheme(!dark);
                editor.ApplyTheme();
                editor.ApplyHighlight();
                uint themedHeading = RenderResources.SourceTheme.Color(MarkdownSourceKind.Heading);
                C("theme updates native text colors", ColorAt(h, 0) == themedHeading);
                C("theme updates native background", BackgroundAt(h) == RenderResources.SourceTheme.Background);
                Externs.SendMessageW(h, 0x010D, 0, 0);
                RenderResources.ApplyTheme(dark);
                editor.ApplyHighlight();
                C("composition blocks formatting", ColorAt(h, 0) == themedHeading);
                Externs.SendMessageW(h, 0x010E, 0, 0);
                editor.ApplyHighlight();
                C("composition end allows formatting", ColorAt(h, 0) == RenderResources.SourceTheme.Color(MarkdownSourceKind.Heading));
            }
            finally { RenderResources.ApplyTheme(dark); }
            Externs.SendMessageW(h, 0x00C7, 0, 0);
            C("undo skips formatting and restores text", Externs.WindowText(h) == text);
            editor.ApplyHighlight();
            Externs.SendMessageW(h, 0x0454, 0, 0);
            C("redo survives formatting", Externs.WindowText(h) == edited);
            editor.SetInputPreferences(4, false);
            editor.SetInputPreferences(4, true);
            Externs.SendMessageW(h, 0x00C7, 0, 0);
            C("wrapping preference preserves undo", Externs.WindowText(h) == text);
            Externs.SetWindowTextW(h, new string('W', 300));
            editor.SetInputPreferences(4, false);
            C("unwrapped long source exposes horizontal scrolling", (Externs.GetWindowLongPtrW(h, -16).ToInt64() & 0x00100000) != 0);
            C("unwrapped source remains one visual line", Externs.SendMessageW(h, 0x00BA, 0, 0) == 1);
            editor.SetInputPreferences(4, true);
            C("wrapping long source creates visual lines", Externs.SendMessageW(h, 0x00BA, 0, 0) > 1);
            string large = string.Concat(Enumerable.Repeat("中文😀\r\n", 12000)) + "needle";
            Externs.SetWindowTextW(h, large);
            Externs.SendMessageW(h, 0x00B1, large.Length - 6, large.Length);
            C("selection above 64K is full width", Externs.EditSelection(h) == (large.Length - 6, large.Length));
            fixed (char* p = "found") Externs.SendMessageW(h, 0x00C2, 1, (nint)p);
            C("replacement above 64K uses exact target", Externs.WindowText(h) == large[..^6] + "found");
            const string literal = "{\\rtf1\\ansi This is source, not rich text.}\r\n";
            Externs.SetWindowTextW(h, literal);
            editor.ApplyHighlight();
            C("RTF-looking source stays literal", Externs.WindowText(h) == literal);
            Externs.SendMessageW(h, 0x00B1, 0, -1);
            fixed (char* p = literal + "replacement") Externs.SendMessageW(h, 0x00C2, 1, (nint)p);
            editor.ApplyHighlight();
            C("RTF-looking replacement stays literal", Externs.WindowText(h) == literal + "replacement");
            Externs.SendMessageW(h, 0x00C7, 0, 0);
            C("literal replacement remains undoable", Externs.WindowText(h) == literal);
            Externs.SetWindowTextW(h, "");
            editor.ApplyHighlight();
            C("empty source remains empty and clean", Externs.WindowText(h) == "" && Externs.SendMessageW(h, 0x00C6, 0, 0) == 0);
            Externs.SetWindowTextW(h, "plain source");
            Externs.SendMessageW(h, 0x00B1, 0, -1);
            var keys = new byte[256]; keys[0x11] = 0x80;
            try
            {
                SetKeyboardState(keys);
                Externs.SendMessageW(h, 0x0100, 'B', 0);
                Externs.SendMessageW(h, 0x0102, 2, 0);
            }
            finally { SetKeyboardState(new byte[256]); }
            byte* format = stackalloc byte[92];
            new Span<byte>(format, 92).Clear(); *(uint*)format = 92;
            Externs.SendMessageW(h, 0x043A, 1, (nint)format);
            C("native rich formatting shortcut cannot style source", (*(uint*)(format + 8) & 1) == 0 && Externs.WindowText(h) == "plain source");
        }
        finally { Externs.DestroyWindow(parent); }
    }

    // EM_SETBKGNDCOLOR returns the previous color; reapply the expected color only on this owned test control.
    private static uint BackgroundAt(nint editor)
        => (uint)Externs.SendMessageW(editor, 0x0443, 0, (nint)RenderResources.SourceTheme.Background);

    private static uint ColorAt(nint editor, int offset)
    {
        var saved = Externs.EditSelection(editor);
        Externs.SendMessageW(editor, 0x00B1, offset, offset + 1);
        byte* format = stackalloc byte[92];
        new Span<byte>(format, 92).Clear();
        *(uint*)format = 92;
        Externs.SendMessageW(editor, 0x043A /*EM_GETCHARFORMAT*/, 1, (nint)format);
        uint color = *(uint*)(format + 20);
        Externs.SendMessageW(editor, 0x00B1, saved.start, saved.end);
        return color;
    }
}
