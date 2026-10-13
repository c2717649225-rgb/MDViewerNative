using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Keys are borrowed only during a single synchronous Relayout. Width and typography
// are fixed by that scope; parsed runs must not be mutated while measuring.
internal sealed class ParagraphMeasureComparer : IEqualityComparer<Para>
{
    internal static readonly ParagraphMeasureComparer Instance = new();

    public bool Equals(Para x, Para y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.XOff != y.XOff || x.QSeq != y.QSeq || x.QuoteDepth != y.QuoteDepth
            || x.Alert != y.Alert || x.AlertTitle != y.AlertTitle) return false;
        return RunsEqual(x.Inlines, y.Inlines);
    }

    internal static bool RunsEqual(List<Run> x, List<Run> y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++)
        {
            var a = x[i];
            var b = y[i];
            if (a.Text != b.Text || Flags(a) != Flags(b) || a.SupSub != b.SupSub || a.Link != b.Link
                || a.LinkTitle != b.LinkTitle || a.LinkOuter != b.LinkOuter || a.ImageAlt != b.ImageAlt) return false;
        }
        return true;
    }

    public int GetHashCode(Para paragraph)
    {
        var hash = new HashCode();
        hash.Add(paragraph.XOff);
        hash.Add(paragraph.QSeq);
        hash.Add(paragraph.QuoteDepth); hash.Add(paragraph.Alert); hash.Add(paragraph.AlertTitle);
        AddRuns(ref hash, paragraph.Inlines);
        return hash.ToHashCode();
    }

    internal static void AddRuns(ref HashCode hash, List<Run> runs)
    {
        if (runs == null) return;
        foreach (var run in runs)
        {
            hash.Add(run.Text, StringComparer.Ordinal);
            hash.Add(Flags(run));
            hash.Add(run.SupSub);
            hash.Add(run.Link, StringComparer.Ordinal);
            hash.Add(run.LinkTitle, StringComparer.Ordinal);
            hash.Add(run.LinkOuter, StringComparer.Ordinal);
            hash.Add(run.ImageAlt, StringComparer.Ordinal);
        }
    }

    static int Flags(Run r) => (r.B ? 1 : 0) | (r.I ? 2 : 0) | (r.S ? 4 : 0) | (r.Code ? 8 : 0)
        | (r.Mark ? 16 : 0) | (r.Fn ? 32 : 0) | (r.U ? 64 : 0) | (r.Opaque ? 128 : 0) | (r.Img ? 256 : 0);
}
