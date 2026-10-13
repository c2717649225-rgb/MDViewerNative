using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MDViewerNative;

internal readonly record struct SearchOptions(bool CaseSensitive = false, bool WholeWord = false, bool RegularExpression = false)
{
    public StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
internal readonly record struct SearchOccurrence(int Start, int Length, Match Match)
{
    public string Replacement(string value) => Match == null ? value : Match.Result(value);
}

internal static class TextSearch
{
    private const int MaximumHits = 100000;
    private static bool ScalarBoundary(string text, int index) => index <= 0 || index >= text.Length
        || !char.IsHighSurrogate(text[index - 1]) || !char.IsLowSurrogate(text[index]);
    private static bool WordAt(string text, int index)
    {
        if (index < 0 || index >= text.Length) return false;
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1])) index--;
        if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;
        return Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
    }
    private static bool Boundary(string text, int start, int length, SearchOptions options) => ScalarBoundary(text, start)
        && ScalarBoundary(text, start + length) && (!options.WholeWord || (!WordAt(text, start - 1) && !WordAt(text, start + length)));
    private static Regex Pattern(string query, SearchOptions options) => new(query,
        RegexOptions.CultureInvariant | RegexOptions.Multiline | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(150));

    public static List<SearchOccurrence> Occurrences(string text, string query, SearchOptions options = default, int start = 0, int end = -1)
    {
        var result = new List<SearchOccurrence>();
        if (string.IsNullOrEmpty(query)) return result;
        start = Math.Clamp(start, 0, text.Length); end = end < 0 ? text.Length : Math.Clamp(end, start, text.Length);
        void Add(int at, int length, Match match)
        {
            if (at < start || at + length > end || !Boundary(text, at, length, options)) return;
            if (result.Count >= MaximumHits) throw new InvalidOperationException("匹配超过 100000 处，请缩小范围或修改表达式。");
            result.Add(new(at, length, match));
        }
        if (options.RegularExpression)
        {
            foreach (Match match in Pattern(query, options).Matches(text, start))
            { if (match.Index > end) break; Add(match.Index, match.Length, match); }
        }
        else
        {
            int from = start, found;
            while (from <= end && (found = text.IndexOf(query, from, options.Comparison)) >= 0 && found + query.Length <= end)
            {
                bool accept = Boundary(text, found, query.Length, options);
                if (accept) Add(found, query.Length, null);
                from = found + (accept ? query.Length : 1);
            }
        }
        return result;
    }
    public static bool MatchesAt(string text, int start, int length, string query, SearchOptions options = default)
    {
        if (string.IsNullOrEmpty(query) || start < 0 || length < 0 || start > text.Length - length) return false;
        if (options.RegularExpression)
        {
            var match = Pattern(query, options).Match(text, start);
            return match.Success && match.Index == start && match.Length == length && Boundary(text, start, length, options);
        }
        return length == query.Length && text.AsSpan(start, length).Equals(query.AsSpan(), options.Comparison) && Boundary(text, start, length, options);
    }
    public static string ReplacementAt(string text, int start, int length, string query, string replacement, SearchOptions options)
    {
        if (!MatchesAt(text, start, length, query, options)) throw new InvalidOperationException("选区不是当前匹配。");
        return options.RegularExpression ? Pattern(query, options).Match(text, start).Result(replacement) : replacement;
    }
    public static List<(int start, int len)> FindAll(string text, string query, SearchOptions options = default, int start = 0, int end = -1) =>
        Occurrences(text, query, options, start, end).Select(m => (m.Start, m.Length)).ToList();
    public static int Next(IReadOnlyList<(int start, int len)> hits, int from, bool back)
    {
        if (hits.Count == 0) return -1;
        from = Math.Max(from, 0);
        if (back) { for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].start + hits[i].len <= from) return i; return hits.Count - 1; }
        for (int i = 0; i < hits.Count; i++) if (hits[i].start >= from) return i;
        return 0;
    }
    public static (string text, int count) ReplaceAll(string text, string query, string replacement, SearchOptions options = default, int start = 0, int end = -1)
    {
        var hits = Occurrences(text, query, options, start, end);
        if (hits.Count == 0) return (text, 0);
        var output = new StringBuilder(text.Length); int from = 0;
        foreach (var hit in hits)
        {
            output.Append(text, from, hit.Start - from).Append(hit.Replacement(replacement));
            from = hit.Start + hit.Length;
        }
        output.Append(text, from, text.Length - from);
        return (output.ToString(), hits.Count);
    }
}
