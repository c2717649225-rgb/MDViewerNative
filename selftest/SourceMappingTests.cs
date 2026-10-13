using System;
using System.Linq;
using MDViewerNative;

internal static class SourceMappingTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("SourceMapping/" + name, ok, "original UTF-16 block spans; containers explicitly coarse");
        string source = "\uFEFF[ref]: notes.md\r\n\r\n# Same\r\n\r\nfirst 😀 line\r\nsecond line\r\n\r\nSame\r\n====\r\n\r\n```md\r\n# not a heading\r\n```\r\n\r\n# Same\r\n";
        var blocks = MdParser.Parse(source);
        var headings = blocks.OfType<Heading>().ToArray();
        C("repeated headings retain distinct source positions", headings.Length == 3
            && headings[0].Source?.Start == source.IndexOf("# Same", StringComparison.Ordinal)
            && headings[2].Source?.Start == source.LastIndexOf("# Same", StringComparison.Ordinal));
        C("setext includes text and underline", headings[1].Source is { } setext && source[setext.Start..setext.End] == "Same\r\n====\r\n");
        var paragraph = blocks.OfType<Para>().Single();
        C("paragraph preserves CRLF and surrogate coordinates", paragraph.Source is { } para
            && source[para.Start..para.End] == "first 😀 line\r\nsecond line\r\n");
        var code = blocks.OfType<CodeBlock>().Single();
        C("fence range includes delimiters and literal heading", code.Source is { } range && source[range.Start..range.End] == "```md\r\n# not a heading\r\n```\r\n");
        C("all direct blocks bounded and ordered", blocks.All(b => b.Source is { Start: >= 0 } r && r.End <= source.Length && r.Length > 0)
            && blocks.Select(b => b.Source.Value.Start).SequenceEqual(blocks.Select(b => b.Source.Value.Start).Order()));
        var empty = MdParser.Parse(""); C("empty source emits no invented ranges", empty.Count == 0);
        string carriage = "# One\r\r# Two\r";
        var crBlocks = MdParser.Parse(carriage);
        C("CR-only documents parse logical lines", crBlocks.Count == 2 && crBlocks.All(b => b is Heading));
        C("CR-only ranges reference original bytes as UTF16 text", crBlocks[^1].Source is { } cr && carriage[cr.Start..cr.End] == "# Two\r");
        string front = "---\nname: example\n---\n# Body\n";
        var frontBlocks = MdParser.Parse(front);
        C("front matter owns its actual lines", frontBlocks[0].Source is { } fm && front[fm.Start..fm.End] == "---\nname: example\n---\n");
        C("post-metadata heading uses original offset", frontBlocks[1].Source?.Start == front.IndexOf("# Body", StringComparison.Ordinal));
        string nested = "> # Quoted\n> body\n\n- one\n- two\n\n# Outside\n";
        var nestedBlocks = MdParser.Parse(nested);
        C("quote spans are explicitly container-level", nestedBlocks.Where(b => b.QuoteDepth > 0).All(b => b.Source is { Container: true, Start: 0 }));
        var items = nestedBlocks.OfType<ListItem>().ToArray();
        C("list siblings use container range not guessed text match", items.Length == 2 && items.All(b => b.Source is { Container: true } r && r.Start == nested.IndexOf("- one", StringComparison.Ordinal)));
        C("content after container does not inherit container span", nestedBlocks[^1].Source is { Container: false } end && end.Start == nested.IndexOf("# Outside", StringComparison.Ordinal));
        var notes = MdParser.Parse("body[^n]\n\n[^n]: footnote\n");
        C("synthetic footnote section remains unmapped", notes.Count >= 4 && notes.Skip(1).All(b => b.Source == null));
        var invalidRef = MdParser.Parse("[a]: <missing\ntext\n\n# Last\n");
        C("invalid references keep original source ranges", invalidRef[^1].Source?.Start == "[a]: <missing\ntext\n\n".Length);
        foreach (string text in new[] { "a\nb😀\n", "a\r\nb😀\r\n", "a\rb😀\r", "no breaks", "" })
        {
            bool roundTrip = true;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i > 0 && i < text.Length && text[i - 1] == '\r' && text[i] == '\n') continue;
                roundTrip &= DocumentCoordinates.FromEditor(text, DocumentCoordinates.ToEditor(text, i)) == i;
            }
            C("coordinate round trip " + text.Replace("\r", "R").Replace("\n", "N"), roundTrip);
            C("coordinate clamps " + text.Length, DocumentCoordinates.FromEditor(text, int.MaxValue) == text.Length && DocumentCoordinates.ToEditor(text, -1) == 0);
        }
        using var view = new DocView(); view.Load(source);
        float? y = view.YAtSourceOffset(headings[2].Source.Value.Start);
        C("source-to-layout picks repeated heading by source identity", y is > 0 && view.SourceOffsetAtY(y.Value) == headings[2].Source.Value.Start);
        view.OnWidth(420);
        y = view.YAtSourceOffset(headings[2].Source.Value.Start);
        C("reflow preserves mapping", y is > 0 && view.SourceOffsetAtY(y.Value) == headings[2].Source.Value.Start);
        C("nonfinite view position is unmapped", view.SourceOffsetAtY(float.NaN) == null);
        view.Load(""); C("empty view has no mapping", view.SourceOffsetAtY(0) == null && view.YAtSourceOffset(0) == null);
    }
}
