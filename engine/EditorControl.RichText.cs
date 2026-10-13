using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MDViewerNative;

internal sealed unsafe partial class EditorControl
{
    private static nint _richLibrary;
    private nint _textDocument;
    private EditorCoordinates _coordinates;
    private bool _formatting;
    private const nuint HighlightTimer = 7;
    private nint Native(uint message, nint w = 0, nint l = 0) => Externs.CallWindowProcW(_previous, Handle, message, w, l);
    private EditorCoordinates Coordinates => _coordinates ??= new(Externs.WindowText(Handle));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string name, nint file, uint flags);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint delay, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct CharacterRange { public int Start, End; }
    private struct TextPoint { public int X, Y; }

    internal float LineTop(int line)
    {
        int index = (int)Native(0x00BB /*EM_LINEINDEX*/, line, 0);
        TextPoint point = default;
        Native(0x00D6 /*EM_POSFROMCHAR*/, (nint)(&point), index);
        return point.Y / Scale;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EditStream { public nint Cookie; public uint Error; public nint Callback; }
    private struct TextStream { public byte* Data; public int Length, Position; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint ReadPlainText(nint cookie, byte* buffer, int capacity, int* copied)
    {
        var input = (TextStream*)cookie;
        int length = Math.Min(capacity, input->Length - input->Position);
        new ReadOnlySpan<byte>(input->Data + input->Position, length).CopyTo(new Span<byte>(buffer, capacity));
        input->Position += length;
        *copied = length;
        return 0;
    }
    private static void EnsureRichEdit()
    {
        if (_richLibrary == 0) _richLibrary = LoadLibraryExW("msftedit.dll", 0, 0x800);
        if (_richLibrary == 0) throw new System.ComponentModel.Win32Exception();
    }

    private void InitializeRichEdit()
    {
        Native(0x0445 /*EM_SETEVENTMASK*/, 0, 1 /*ENM_CHANGE*/);
        nint ole = 0;
        Native(0x043C /*EM_GETOLEINTERFACE*/, 0, (nint)(&ole));
        if (ole == 0) throw new InvalidOperationException("Cannot access RichEdit text services.");
        try
        {
            Guid iid = new("8CC497C0-A1DF-11CE-8098-00AA0047BE5D");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ole, in iid, out _textDocument));
        }
        finally { Marshal.Release(ole); }
    }

    private void ReleaseRichEdit()
    {
        if (Handle != 0) KillTimer(Handle, HighlightTimer);
        if (_textDocument != 0) { Marshal.Release(_textDocument); _textDocument = 0; }
    }

    private void QueueHighlight()
    {
        if (!_disposed && Handle != 0 && !_formatting) SetTimer(Handle, HighlightTimer, 180, 0);
    }

    private nint StreamPlainText(string source, bool selection)
    {
        fixed (char* text = source)
        {
            TextStream input = new() { Data = (byte*)text, Length = source.Length * 2 };
            EditStream stream = new()
            {
                Cookie = (nint)(&input),
                Callback = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int, int*, uint>)&ReadPlainText
            };
            Native(0x0449 /*EM_STREAMIN*/, (nint)(0x11 | (selection ? 0x8000 : 0)), (nint)(&stream));
            return stream.Error == 0 ? 1 : 0;
        }
    }

    // Keep the public EDIT message contract so search, drafts, history and host navigation
    // share one coordinate system. RichEdit-only formatting uses separate TOM ranges.
    private bool TryRichMessage(uint message, nint w, nint l, out nint result)
    {
        result = 0;
        switch (message)
        {
            case 0x0031: // RichEdit does not retain an HFONT; expose the font we own.
                result = _font;
                return true;
            case 0x0100 when !_ime.Active && ((int)w == 13 || (int)w == 9):
                // RichEdit acts on Enter during KEYDOWN; our EDIT contract acts on CHAR.
                return true;
            case 0x000C: // Explicit SF_TEXT prevents RichEdit interpreting RTF-looking source.
                string source = l == 0 ? "" : new string((char*)l);
                result = StreamPlainText(source, false);
                _coordinates = null;
                Native(0x00CD /*EM_EMPTYUNDOBUFFER*/);
                return true;
            case 0x00C2 when l != 0 && ((char*)l)[0] == '{' && ((char*)l)[1] == '\\'
                && ((char*)l)[2] == 'r' && ((char*)l)[3] == 't' && ((char*)l)[4] == 'f':
                // EM_REPLACESEL also auto-detects RTF. Preserve literal replacement text.
                if (w == 0) SuspendFormattingUndo(true);
                try { result = StreamPlainText(new string((char*)l), true); }
                finally { if (w == 0) SuspendFormattingUndo(false); }
                return true;
            case 0x00B0: // EM_GETSEL: preserve full-width pointer outputs and packed sentinel.
                CharacterRange selected = default;
                Native(0x0434 /*EM_EXGETSEL*/, 0, (nint)(&selected));
                int start = Coordinates.ToExternal(selected.Start), end = Coordinates.ToExternal(selected.End);
                if (w != 0) *(int*)w = start;
                if (l != 0) *(int*)l = end;
                result = start > 65535 || end > 65535 ? -1 : (nint)(uint)((uint)start | ((uint)end << 16));
                return true;
            case 0x00B1: // EM_SETSEL
                result = Native(message, Coordinates.ToNative((int)w), Coordinates.ToNative((int)l));
                return true;
            case 0x00BB: // EM_LINEINDEX
                result = Coordinates.ToExternal((int)Native(message, w, l));
                return true;
            case 0x00C9: // EM_LINEFROMCHAR
                result = Native(0x0436 /*EM_EXLINEFROMCHAR*/, 0, Coordinates.ToNative((int)w));
                return true;
            case 0x0113 when (nuint)w == HighlightTimer:
                KillTimer(Handle, HighlightTimer);
                if (!_ime.Active) ApplyHighlight();
                return true;
        }
        return false;
    }

    private void SuspendFormattingUndo(bool suspend)
    {
        var table = *(nint**)_textDocument;
        int count = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int*, int>)table[22])(_textDocument,
            suspend ? -9999995 /*tomSuspend*/ : -9999994 /*tomResume*/, &count);
        Marshal.ThrowExceptionForHR(hr);
    }

    private void SetColor(uint color, int start, int end)
    {
        // Format a TOM range, never the selection: reversed selections keep their active end.
        nint range = 0, font = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)(*(nint**)_textDocument)[24])(_textDocument, start, end, &range));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)range)[18])(range, &font));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)font)[25])(font, color));
        }
        finally
        {
            if (font != 0) Marshal.Release(font);
            if (range != 0) Marshal.Release(range);
        }
    }

    internal void ApplyHighlight()
    {
        if (_disposed || _formatting || _ime.Active || _textDocument == 0) return;
        string source = Externs.WindowText(Handle);
        var spans = MarkdownSourceColors.Scan(source);
        var theme = RenderResources.SourceTheme;
        var coordinates = Coordinates;
        long scroll = 0; // POINT: two 32-bit coordinates.
        Native(0x04DD /*EM_GETSCROLLPOS*/, 0, (nint)(&scroll));
        nint modified = Native(0x00B8 /*EM_GETMODIFY*/);
        nint events = Native(0x043B /*EM_GETEVENTMASK*/);
        bool visible = IsWindowVisible(Handle);
        SuspendFormattingUndo(true);
        _formatting = true;
        try
        {
            Native(0x0445, 0, 0);
            if (visible) Native(0x000B /*WM_SETREDRAW*/, 0, 0);
            SetColor(theme.Color(MarkdownSourceKind.Text), 0, coordinates.ToNative(source.Length) + 1);
            foreach (var span in spans)
                SetColor(theme.Color(span.Kind),
                    coordinates.ToNative(span.Start), coordinates.ToNative(span.Start + span.Length));
        }
        finally
        {
            Native(0x04DE /*EM_SETSCROLLPOS*/, 0, (nint)(&scroll));
            Native(0x00B9 /*EM_SETMODIFY*/, modified, 0);
            Native(0x0445, 0, events);
            SuspendFormattingUndo(false);
            _formatting = false;
            if (visible) { Native(0x000B, 1, 0); Externs.InvalidateRect(Handle, 0, 0); }
        }
    }
}
