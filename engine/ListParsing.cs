using System;
using System.Collections.Generic;
using System.Linq;

namespace MDViewerNative;

internal sealed partial class ParserContext
{
    private int ListSequence;

    private static int FenceLength(string text)
    {
        int indent = text.Length - text.TrimStart(' ').Length;
        if (indent > 3) return 0;
        text = text[indent..];
        if (text.Length < 3 || (text[0] != '`' && text[0] != '~')) return 0;
        int n = 1;
        while (n < text.Length && text[n] == text[0]) n++;
        return n >= 3 && !(text[0] == '`' && text[n..].Contains('`')) ? n : 0;
    }

    private static bool ClosesFence(string text, char marker, int length)
    {
        if (text.Length - text.TrimStart(' ').Length > 3) return false;
        text = text.TrimStart(' ');
        int n = 0;
        while (n < text.Length && text[n] == marker) n++;
        return n >= length && text[n..].Trim().Length == 0;
    }

    private static bool AtxStart(string text)
    {
        int n = 0;
        while (n < text.Length && text[n] == '#') n++;
        return n is > 0 and <= 6 && (n == text.Length || text[n] is ' ' or '\t');
    }

    // A block quote opens where '>' sits within three columns of the content edge.
    private static bool QuoteStart(string text)
    {
        int lead = text.Length - text.TrimStart(' ').Length;
        return lead <= 3 && lead < text.Length && text[lead] == '>';
    }

    // True when a collected body line leaves a quote paragraph open, so the next
    // dedented line lazily continues it.
    private static bool EndsOpenQuotePara(string text)
    {
        if (!QuoteStart(text ?? "")) return false;
        return StripQuotes(text, int.MaxValue, out _).Trim().Length > 0;
    }

    private bool StructuredStart(string first, string[] lines, int next, int contentCol)
    {
        if (FenceLength(first) > 0 || AtxStart(first) || MatchItem(first).ok) return true;
        // A quote marker opens a container too (#294/#295). Without this the item body
        // never reaches ParseBlocks and the '>' is rendered as literal text.
        if (QuoteStart(first)) return true;
        if (first.Length == 0 || next >= lines.Length) return false;
        string line = ExpandLeadingTabs(lines[next]);
        int lead = line.Length - line.TrimStart(' ').Length;
        string t = line.Trim();
        return lead >= contentCol && lead <= contentCol + 3 && t.Length > 0
            && (t.All(c => c == '=') || t.All(c => c == '-'));
    }

    // Collect one item body in its own coordinate system. Markers inside a fence are
    // literal; dedented nonblank lines end the item even when its fence is unclosed.
    private List<Block> ReadItemBody(string first, string[] lines, ref int i, int contentCol, int depth, out bool loose)
    {
        var body = new List<string> { first };
        int fence = FenceLength(first);
        char marker = fence > 0 ? first.TrimStart()[0] : '\0';
        var firstItem = MatchItem(first);
        int childColumn = firstItem.ok ? firstItem.mw + 1 : 0;
        bool pendingBlank = false;
        loose = false;
        while (i < lines.Length)
        {
            string line = ExpandLeadingTabs(lines[i]);
            if (string.IsNullOrWhiteSpace(line))
            {
                int next = i;
                while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
                if (fence == 0 && (next == lines.Length || ExpandLeadingTabs(lines[next]).Length - ExpandLeadingTabs(lines[next]).TrimStart(' ').Length < contentCol)) break;
                if (i == lines.Length - 1 && line.Length == 0) { i++; break; }
                while (i < next)
                {
                    if (i == lines.Length - 1 && lines[i].Length == 0) { i++; break; }
                    body.Add(fence > 0 ? StripIndentColumns(lines[i], contentCol) : "");
                    i++;
                }
                if (fence == 0) pendingBlank = true;
                continue;
            }
            int lead = line.Length - line.TrimStart(' ').Length;
            if (lead < contentCol)
            {
                // Lazy continuation into a container opened inside this item (#294/#295).
                // A dedented line that cannot start a block joins the open quote paragraph
                // rather than ending the item. Only the quote case is taken here; plain
                // item paragraphs keep their existing continuation handling.
                if (fence == 0 && !pendingBlank && EndsOpenQuotePara(body[^1])
                    && !CanInterruptPara(line.TrimStart(' ')))
                {
                    body.Add(line.TrimStart(' '));
                    i++;
                    continue;
                }
                break;
            }
            string relative = line[contentCol..];
            int relativeLead = relative.Length - relative.TrimStart(' ').Length;
            if (childColumn > 0 && relativeLead < childColumn && !MatchItem(relative.TrimStart(' ')).ok) childColumn = 0;
            if (pendingBlank && childColumn == 0) loose = true;
            if (fence == 0 && relativeLead <= 3)
            {
                var child = MatchItem(relative.TrimStart(' '));
                if (child.ok) childColumn = relativeLead + child.mw + 1;
            }
            pendingBlank = false;
            body.Add(StripIndentColumns(lines[i], contentCol));
            if (fence > 0)
            {
                if (ClosesFence(relative, marker, fence)) fence = 0;
            }
            else
            {
                fence = FenceLength(relative);
                if (fence > 0) marker = relative.TrimStart()[0];
            }
            i++;
        }
        body.Add(""); // ParseBlocks uses a terminal split-line sentinel.
        var parsed = new List<Block>();
        int subI = 0;
        ParseBlocks(body.ToArray(), ref subI, 0, 0, parsed, depth + 1);
        return parsed;
    }

    private static ListItem ContinuationOf(ListItem parent) => new ListItem
    {
        Marker = parent.Marker,
        Inlines = new List<Run>(),
        Nest = parent.Nest,
        XOff = parent.XOff,
        QuoteDepth = parent.QuoteDepth,
        ListGroup = parent.ListGroup,
        Continuation = true
    };

    private void AppendItemBody(ListItem item, List<Block> parsed, List<Block> output,
        List<ListItem> own, int contentCol, int quoteDepth)
    {
        output.Add(item);
        own.Add(item);
        ListItem current = item;
        bool afterChild = false;
        foreach (Block block in parsed)
        {
            if (block is ListItem child)
            {
                child.Nest += item.Nest + 1;
                child.XOff += contentCol / 2 * 20 + quoteDepth * 20;
                child.QuoteDepth += quoteDepth;
                output.Add(child);
                afterChild = true;
                continue;
            }
            if (afterChild)
            {
                current = ContinuationOf(item);
                output.Add(current);
                own.Add(current);
                afterChild = false;
            }
            if (current.Inlines.Count == 0 && current.Sub == null && block is Para p)
                current.Inlines = p.Inlines;
            else (current.Sub ??= new List<Block>()).Add(block);
        }
    }

    private bool TryParentTail(string[] lines, ref int i, ListItem parent, int contentCol,
        int quoteDepth, int depth, List<Block> output, List<ListItem> own, out bool loose)
    {
        loose = false;
        int start = i;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
        if (start == lines.Length) return false;
        string line = ExpandLeadingTabs(lines[start]);
        if (line.Length - line.TrimStart(' ').Length < contentCol || MatchItem(line.TrimStart(' ')).ok) return false;
        loose = start > i;
        i = start + 1;
        var parsed = ReadItemBody(line[contentCol..], lines, ref i, contentCol, depth, out bool internalBlank);
        loose |= internalBlank;
        AppendItemBody(ContinuationOf(parent), parsed, output, own, contentCol, quoteDepth);
        return true;
    }

    private static string StripQuotes(string line, int limit, out int count)
    {
        count = 0;
        while (count < limit)
        {
            int lead = line.Length - line.TrimStart(' ').Length;
            if (lead > 3 || lead >= line.Length || line[lead] != '>') break;
            int end = lead + 1;
            if (end < line.Length && line[end] == ' ') end++;
            line = line[end..];
            count++;
        }
        return line;
    }

    // Protect fenced source from both definition collection and textual reference
    // substitution. This handles outer quote prefixes and same-line list prefixes.
    private bool[] FencedSourceLines(string[] lines)
    {
        var mask = new bool[lines.Length];
        int active = 0, quotes = 0, column = 0;
        char marker = '\0';
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (active > 0)
            {
                string rest = StripQuotes(line, quotes, out int got);
                int lead = rest.Length - rest.TrimStart(' ').Length;
                if (got == quotes && (string.IsNullOrWhiteSpace(rest) || lead >= column))
                {
                    mask[i] = true;
                    rest = rest[Math.Min(column, rest.Length)..];
                    if (ClosesFence(rest, marker, active)) active = 0;
                    continue;
                }
                active = 0;
            }
            string body = StripQuotes(line, 100, out quotes);
            column = 0;
            for (int depth = 0; depth < 100; depth++)
            {
                int lead = body.Length - body.TrimStart(' ').Length;
                if (lead > 3) break;
                string text = body.TrimStart(' ');
                var item = MatchItem(text);
                if (!item.ok) break;
                int spaces = 0;
                while (item.mw + spaces < text.Length && text[item.mw + spaces] == ' ') spaces++;
                int consumed = lead + item.mw + (spaces > 4 ? 1 : spaces);
                if (consumed >= body.Length) break;
                column += consumed;
                body = body[consumed..];
            }
            active = FenceLength(body);
            if (active > 0) { marker = body.TrimStart()[0]; mask[i] = true; }
        }
        return mask;
    }
}
