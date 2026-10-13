using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Exact geometry keys, borrowed for one synchronous layout at one width/font.
// Stateful/recursive containers and standalone images remain excluded. Flattened
// quotes and leaf list items depend on local geometry, not their group identity.
internal sealed class BlockMeasureComparer : IEqualityComparer<Block>
{
    internal static readonly BlockMeasureComparer Instance = new();
    internal static bool Supports(Block block) => block is Heading or CodeBlock or HtmlBlock or Quote || block is ListItem { Sub: null };

    public bool Equals(Block x, Block y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.GetType() != y.GetType() || x.XOff != y.XOff
            || (x is not (Quote or ListItem) && x.QSeq != y.QSeq) || x.QuoteDepth != y.QuoteDepth || x.Alert != y.Alert || x.AlertTitle != y.AlertTitle) return false;
        return (x, y) switch
        {
            (Heading a, Heading b) => a.Level == b.Level && ParagraphMeasureComparer.RunsEqual(a.Inlines, b.Inlines),
            (CodeBlock a, CodeBlock b) => a.Text == b.Text && a.Lang == b.Lang && a.FrontMatter == b.FrontMatter,
            (HtmlBlock a, HtmlBlock b) => a.Text == b.Text,
            (Quote a, Quote b) => ParagraphMeasureComparer.RunsEqual(a.Inlines, b.Inlines),
            (ListItem a, ListItem b) => a.Sub == null && b.Sub == null && a.Marker == b.Marker && a.Task == b.Task
                && a.Loose == b.Loose && a.Nest == b.Nest && a.Continuation == b.Continuation
                && a.GroupBreakAfter == b.GroupBreakAfter && ParagraphMeasureComparer.RunsEqual(a.Inlines, b.Inlines),
            _ => false
        };
    }

    public int GetHashCode(Block block)
    {
        var hash = new HashCode();
        hash.Add(block.GetType()); hash.Add(block.XOff); hash.Add(block is Quote or ListItem ? 0 : block.QSeq);
        hash.Add(block.QuoteDepth); hash.Add(block.Alert); hash.Add(block.AlertTitle);
        switch (block)
        {
            case Heading heading:
                hash.Add(heading.Level); ParagraphMeasureComparer.AddRuns(ref hash, heading.Inlines); break;
            case CodeBlock code:
                hash.Add(code.Text); hash.Add(code.Lang); hash.Add(code.FrontMatter); break;
            case HtmlBlock html:
                hash.Add(html.Text); break;
            case Quote quote:
                ParagraphMeasureComparer.AddRuns(ref hash, quote.Inlines); break;
            case ListItem item:
                hash.Add(item.Marker); hash.Add(item.Task); hash.Add(item.Loose); hash.Add(item.Nest);
                hash.Add(item.Continuation); hash.Add(item.GroupBreakAfter);
                ParagraphMeasureComparer.AddRuns(ref hash, item.Inlines); break;
        }
        return hash.ToHashCode();
    }
}
