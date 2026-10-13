using System;
using System.Collections.Generic;

namespace MDViewerNative;

internal enum MarkdownSourceKind { Text, Heading, Marker, Code, Link, Emphasis }
internal readonly record struct SourceColorSpan(int Start, int Length, MarkdownSourceKind Kind);

// Immutable palette for one formatting pass; system colors are already COLORREF values.
internal readonly record struct SourceColorTheme(bool Dark, bool HighContrast, uint Background, uint Text)
{
    // U2：desk 界面的纸张；null 时沿用 v2.2 色表。
    internal PaperColors? Paper { get; init; }
    internal uint Color(MarkdownSourceKind kind) => HighContrast ? Text
        : Paper is PaperColors p ? MarkdownSourceColors.Color(kind, p) : MarkdownSourceColors.Color(kind, Dark);
}

// A bounded lexical aid, deliberately independent of the preview parser and its model.
// It never rewrites Markdown or expands tabs, and keeps offsets in the editor's UTF-16 text.
internal static class MarkdownSourceColors
{
    internal const int CharacterLimit = 1_000_000;
    internal const int SpanLimit = 20_000;

    internal static SourceColorTheme CreateTheme(bool dark, bool highContrast = false, uint systemBackground = 0, uint systemText = 0)
        => new(dark, highContrast,
            highContrast ? systemBackground : ThemePalette.ToColorRef(dark ? ThemePalette.CanvasD : ThemePalette.CanvasL),
            highContrast ? systemText : Color(MarkdownSourceKind.Text, dark));

    internal static SourceColorTheme CreateTheme(PaperColors paper)
        => new(paper.Dark, false, ThemePalette.ToColorRef(paper.Canvas), Color(MarkdownSourceKind.Text, paper)) { Paper = paper };

    internal static uint Color(MarkdownSourceKind kind, PaperColors p) => ThemePalette.ToColorRef(kind switch
    {
        MarkdownSourceKind.Heading or MarkdownSourceKind.Link => p.Accent,
        MarkdownSourceKind.Marker or MarkdownSourceKind.Emphasis => p.Muted,
        MarkdownSourceKind.Code => p.Comment,
        _ => p.Ink
    });

    internal static uint Color(MarkdownSourceKind kind, bool dark) => ThemePalette.ToColorRef(kind switch
    {
        MarkdownSourceKind.Heading or MarkdownSourceKind.Link => dark ? ThemePalette.AccentD : ThemePalette.AccentL,
        MarkdownSourceKind.Marker or MarkdownSourceKind.Emphasis => dark ? ThemePalette.MutedD : ThemePalette.MutedL,
        MarkdownSourceKind.Code => dark ? ThemePalette.CommentD : ThemePalette.CommentL,
        _ => dark ? ThemePalette.InkD : ThemePalette.InkL
    });

    internal static List<SourceColorSpan> Scan(string source)
    {
        var spans = new List<SourceColorSpan>();
        if (source.Length > CharacterLimit) return spans;
        char fence = '\0';
        int fenceLength = 0;
        void Add(int start, int length, MarkdownSourceKind kind)
        {
            if (length > 0 && spans.Count <= SpanLimit) spans.Add(new(start, length, kind));
        }
        for (int line = 0; line < source.Length;)
        {
            int end = source.IndexOf('\n', line);
            if (end < 0) end = source.Length;
            int contentEnd = end > line && source[end - 1] == '\r' ? end - 1 : end;
            int start = line;
            while (start < contentEnd && source[start] == ' ') start++;
            int markerEnd = start;
            if (start < contentEnd && source[start] is '`' or '~')
                while (markerEnd < contentEnd && source[markerEnd] == source[start]) markerEnd++;
            int markerLength = markerEnd - start;
            if (fence != '\0')
            {
                Add(line, contentEnd - line, MarkdownSourceKind.Code);
                if (start - line <= 3 && markerLength >= fenceLength && source[start] == fence
                    && source.AsSpan(markerEnd, contentEnd - markerEnd).Trim().IsEmpty) fence = '\0';
            }
            else if (start - line <= 3 && markerLength >= 3
                && (source[start] != '`' || !source.AsSpan(markerEnd, contentEnd - markerEnd).Contains('`')))
            {
                fence = source[start]; fenceLength = markerLength;
                Add(line, contentEnd - line, MarkdownSourceKind.Code);
            }
            else if (start - line >= 4 || (start < contentEnd && source[start] == '\t'))
                Add(line, contentEnd - line, MarkdownSourceKind.Code);
            else
            {
                int hashes = start;
                while (hashes < contentEnd && source[hashes] == '#') hashes++;
                if (hashes - start is > 0 and <= 6 && (hashes == contentEnd || char.IsWhiteSpace(source[hashes])))
                    Add(start, contentEnd - start, MarkdownSourceKind.Heading);
                else
                {
                    if (start < contentEnd && source[start] is '>' or '-' or '+' or '*'
                        && (start + 1 == contentEnd || char.IsWhiteSpace(source[start + 1])))
                        Add(start, 1, MarkdownSourceKind.Marker);
                    int digits = start;
                    while (digits < contentEnd && char.IsAsciiDigit(source[digits])) digits++;
                    if (digits > start && digits + 1 < contentEnd && source[digits] is '.' or ')'
                        && char.IsWhiteSpace(source[digits + 1])) Add(start, digits + 1 - start, MarkdownSourceKind.Marker);
                    for (int i = start; i < contentEnd; i++)
                    {
                        char c = source[i];
                        if (c == '\\') { i++; continue; }
                        if (c == '`')
                        {
                            int width = 1;
                            while (i + width < contentEnd && source[i + width] == '`') width++;
                            int close = i + width;
                            while (close < contentEnd)
                            {
                                if (source[close] != '`') { close++; continue; }
                                int run = 1;
                                while (close + run < contentEnd && source[close + run] == '`') run++;
                                if (run == width) break;
                                close += run;
                            }
                            if (close < contentEnd) { Add(i, close + width - i, MarkdownSourceKind.Code); i = close + width - 1; }
                            else { Add(i, contentEnd - i, MarkdownSourceKind.Code); break; }
                        }
                        else if (c is '*' or '_' or '~')
                            Add(i, 1, MarkdownSourceKind.Emphasis);
                        else if (c is '[' or ']' or '(' or ')' || (c == '!' && i + 1 < contentEnd && source[i + 1] == '['))
                            Add(i, 1, MarkdownSourceKind.Link);
                    }
                }
            }
            if (spans.Count > SpanLimit) { spans.Clear(); return spans; }
            line = end + 1;
        }
        return spans;
    }
}
