using System;

namespace MDViewerNative;

// Convert only newline representation. Both coordinate domains are UTF-16, not
// grapheme indexes. Positions inside a CRLF pair snap before/after the whole break.
internal static class DocumentCoordinates
{
    internal static int ToEditor(string source, int offset)
    {
        offset = Math.Clamp(offset, 0, source.Length);
        int native = 0;
        for (int i = 0; i < offset; i++)
        {
            if (source[i] == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n') i++;
                native += 2;
            }
            else native += source[i] == '\n' ? 2 : 1;
        }
        return native;
    }

    internal static int FromEditor(string source, int offset)
    {
        int native = 0, i = 0;
        while (i < source.Length && native < offset)
        {
            char c = source[i++];
            if (c == '\r' && i < source.Length && source[i] == '\n') i++;
            native += c is '\r' or '\n' ? 2 : 1;
        }
        return i;
    }
}
