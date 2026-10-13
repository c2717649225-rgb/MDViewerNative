using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MDViewerNative;

// One deterministic heading namespace is shared by navigation and HTML export.
// It deliberately covers parsed headings, not arbitrary raw HTML ids.
internal sealed class DocumentAnchors
{
    internal sealed record Target(Heading Heading, string Id, string Title, int Block, int Sub);
    private readonly Dictionary<string, Target> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Target> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Heading, string> _headings = new();
    public string IdOf(Heading heading) => _headings[heading];

    public DocumentAnchors(IReadOnlyList<Block> blocks)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Footnote destinations already occupy the fn- namespace in exported HTML.
        foreach (Block block in blocks)
            if (block is Para p && p.Inlines.Count > 0)
            {
                string text = p.Inlines[0].Text;
                int dot = text.IndexOf('.');
                if (text.StartsWith('^') && dot > 1) used.Add("fn-" + text[1..dot]);
            }
        void Add(Heading heading, int block, int sub)
        {
            string title = string.Concat(heading.Inlines.Select(r => r.Text)).Trim();
            string basis = Slug(title), id = basis;
            int suffix = next.GetValueOrDefault(basis, 1);
            while (!used.Add(id)) id = basis + "-" + suffix++;
            next[basis] = suffix;
            var target = new Target(heading, id, title, block, sub);
            _headings.Add(heading, id);
            _ids.Add(id, target);
            _titles.TryAdd(title, target);
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is Heading heading) Add(heading, i, -1);
            else if (blocks[i] is ListItem { Sub: not null } item)
                for (int sub = 0; sub < item.Sub.Count; sub++)
                    if (item.Sub[sub] is Heading child) Add(child, i, sub);
        }
    }

    public static string Slug(string title)
    {
        var result = new StringBuilder(title.Length);
        foreach (Rune rune in title.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) result.Append('-');
            else if (Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_'
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                result.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return result.Length == 0 ? "section" : result.ToString();
    }

    public bool TryFind(string fragment, out Target target)
    {
        target = null;
        if (!DocumentLinks.TryDecode(fragment, out string text)) return false;
        return _ids.TryGetValue(text, out target) || _titles.TryGetValue(text.Trim(), out target);
    }

    public string ExportHref(string href)
    {
        if (href.StartsWith('#') && !href.StartsWith("#fn:") && !href.StartsWith("#ref:")
            && TryFind(href[1..], out Target target)) return "#" + target.Id;
        return href;
    }
}
