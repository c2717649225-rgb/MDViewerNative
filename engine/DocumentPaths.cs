using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MDViewerNative;

// Rewrites relative Markdown destinations (inline `](dest)` and `[id]: dest` definitions) when a
// document moves to another directory. Only existing local files are rewritten; absolute paths,
// URLs, anchors and unknown files stay untouched. Fenced code is skipped.
internal static class DocumentPaths
{
    internal sealed record Plan(string Text, List<(string From, string To)> Changes) { public int Count => Changes.Count; }

    public static Plan Rebase(string text, string oldDirectory, string newDirectory, Func<string, bool> exists = null)
    {
        exists ??= File.Exists;
        var changes = new List<(string, string)>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldDirectory) || string.IsNullOrEmpty(newDirectory)
            || string.Equals(Path.GetFullPath(oldDirectory), Path.GetFullPath(newDirectory), StringComparison.OrdinalIgnoreCase))
            return new(text, changes);
        var sb = new StringBuilder(text.Length + 64);
        char fence = '\0'; int fenceLen = 0;
        int pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl + 1;
            string line = text[pos..end];
            string trimmed = line.TrimStart(' ');
            if (trimmed.Length > 0 && (trimmed[0] == '`' || trimmed[0] == '~'))
            {
                char c = trimmed[0]; int n = 0;
                while (n < trimmed.Length && trimmed[n] == c) n++;
                if (n >= 3)
                {
                    if (fence == '\0') { fence = c; fenceLen = n; }
                    else if (fence == c && n >= fenceLen && trimmed[n..].Trim().Length == 0) fence = '\0';
                    sb.Append(line); pos = end; if (nl < 0) break; continue;
                }
            }
            sb.Append(fence != '\0' ? line : RewriteLine(line, oldDirectory, newDirectory, exists, changes));
            pos = end;
            if (nl < 0) break;
        }
        return new(changes.Count == 0 ? text : sb.ToString(), changes);
    }

    private static string RewriteLine(string line, string oldDir, string newDir, Func<string, bool> exists, List<(string, string)> changes)
    {
        var sb = new StringBuilder(line.Length + 16);
        int i = 0;
        // reference definition: [id]: dest
        int t = 0; while (t < line.Length && t < 3 && line[t] == ' ') t++;
        if (t < line.Length && line[t] == '[')
        {
            int close = line.IndexOf("]:", t, StringComparison.Ordinal);
            if (close > t + 1)
            {
                int d = close + 2; while (d < line.Length && line[d] == ' ') d++;
                int e = d; while (e < line.Length && !char.IsWhiteSpace(line[e])) e++;
                if (e > d)
                {
                    string dest = line[d..e], rewritten = Rewrite(dest, oldDir, newDir, exists);
                    if (rewritten != null) { changes.Add((dest, rewritten)); return line[..d] + rewritten + line[e..]; }
                }
                return line;
            }
        }
        while (i < line.Length)
        {
            int open = line.IndexOf("](", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(line, i, line.Length - i); break; }
            int d = open + 2, e;
            bool angled = false;
            if (d < line.Length && line[d] == '<') { e = line.IndexOf('>', d + 1); if (e < 0) { sb.Append(line, i, d - i); i = d; continue; } d++; angled = true; }
            else
            {
                e = d; int depth = 0;
                while (e < line.Length && !(line[e] == ')' && depth == 0) && line[e] != ' ' && line[e] != '\t')
                { if (line[e] == '(') depth++; else if (line[e] == ')') depth--; e++; }
            }
            string dest = line[d..e], rewritten = Rewrite(dest, oldDir, newDir, exists, angled);
            sb.Append(line, i, d - i);
            if (rewritten != null) { changes.Add((dest, rewritten)); sb.Append(rewritten); }
            else sb.Append(dest);
            i = e;
        }
        return sb.ToString();
    }

    // null = leave as is
    internal static string Rewrite(string dest, string oldDir, string newDir, Func<string, bool> exists, bool angled = false)
    {
        if (dest.Length == 0 || dest[0] is '#' or '/' or '\\' || dest.Contains("://", StringComparison.Ordinal)
            || dest.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || dest.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        if (dest.Length >= 2 && char.IsAsciiLetter(dest[0]) && dest[1] == ':') return null;   // drive-absolute
        int hash = dest.IndexOf('#');
        string pathPart = hash >= 0 ? dest[..hash] : dest, fragment = hash >= 0 ? dest[hash..] : "";
        if (pathPart.Length == 0) return null;
        string decoded;
        try { decoded = Uri.UnescapeDataString(pathPart); } catch (UriFormatException) { return null; }
        foreach (char c in decoded) if (char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*') return null;
        string full;
        try { full = Path.GetFullPath(decoded, oldDir); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!exists(full)) return null;
        string relative = Path.GetRelativePath(newDir, full).Replace('\\', '/');
        if (string.Equals(relative, decoded.Replace('\\', '/'), StringComparison.Ordinal)) return null;
        bool percent = pathPart.Contains('%');
        string encoded = percent ? Uri.EscapeDataString(relative).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase) : relative;
        if (!percent && !angled && (encoded.Contains(' ') || encoded.Contains('(') || encoded.Contains(')'))) encoded = "<" + encoded + ">";
        return encoded + fragment;
    }
}
