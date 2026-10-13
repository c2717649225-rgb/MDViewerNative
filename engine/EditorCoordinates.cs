using System;
using System.Collections.Generic;

namespace MDViewerNative;

// The application exposes UTF-16 CRLF offsets; RichEdit counts each paragraph CR once.
internal sealed class EditorCoordinates
{
    private readonly int[] _externalLf, _nativeCr;
    internal EditorCoordinates(string text)
    {
        var external = new List<int>();
        var native = new List<int>();
        for (int i = 1; i < text.Length; i++)
            if (text[i] == '\n' && text[i - 1] == '\r')
            {
                native.Add(i - 1 - external.Count);
                external.Add(i);
            }
        _externalLf = external.ToArray();
        _nativeCr = native.ToArray();
    }

    private static int Before(int[] positions, int value)
    {
        int lo = 0, hi = positions.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (positions[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    internal int ToNative(int offset) => offset < 0 ? offset : offset - Before(_externalLf, offset + (offset == int.MaxValue ? 0 : 1));
    internal int ToExternal(int offset) => offset < 0 ? offset : offset + Before(_nativeCr, offset);
    // U9a：原生偏移所在的源码行（从 1 起）＝它之前的段落 CR 个数 + 1。
    internal int LineOfNative(int offset) => Before(_nativeCr, Math.Max(0, offset)) + 1;
    // U9b：原生偏移所在段落的结尾 CR（原生偏移）；最后一段没有 CR 时为 -1。
    internal int ParagraphEndNative(int offset)
    {
        int i = Before(_nativeCr, Math.Max(0, offset));
        return i < _nativeCr.Length ? _nativeCr[i] : -1;
    }
    // U9b：原生偏移是否是段落开头（行号栏只在源码行的第一个显示行写号）。
    internal bool IsParagraphStart(int offset) => offset <= 0 || Array.BinarySearch(_nativeCr, offset - 1) >= 0;
}
