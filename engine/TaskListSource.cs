using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Task-list source mapping without a full source map: locate every "[ ]"/"[x]" list marker in
// document order (skipping fenced code, tolerating quote prefixes) and toggle by ordinal.
// Callers must pass the renderer's task count; any mismatch refuses the edit instead of guessing.
internal static class TaskListSource
{
    public static List<int> Locate(string text)
    {
        var result = new List<int>();
        char fence = '\0'; int fenceLen = 0;
        int pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            int len = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            int k = pos;
            // quote prefixes: "> > "
            while (true)
            {
                int save = k;
                while (k < len && text[k] == ' ') k++;
                if (k < len && text[k] == '>') { k++; continue; }
                k = save; break;
            }
            while (k < len && text[k] == ' ') k++;
            // fences
            if (k < len && (text[k] == '`' || text[k] == '~'))
            {
                char c = text[k]; int n = 0;
                while (k + n < len && text[k + n] == c) n++;
                if (n >= 3)
                {
                    if (fence == '\0') { fence = c; fenceLen = n; goto next; }
                    if (fence == c && n >= fenceLen && text.AsSpan(k + n, len - k - n).Trim().IsEmpty) { fence = '\0'; goto next; }
                }
            }
            if (fence != '\0') goto next;
            {
                int j = k;
                if (j < len && (text[j] == '-' || text[j] == '*' || text[j] == '+')) j++;
                else
                {
                    int d = 0;
                    while (j < len && char.IsAsciiDigit(text[j]) && d < 9) { j++; d++; }
                    if (d == 0 || j >= len || (text[j] != '.' && text[j] != ')')) goto next;
                    j++;
                }
                int spaces = 0;
                while (j < len && (text[j] == ' ' || text[j] == '\t')) { j++; spaces++; }
                if (spaces == 0 || j + 3 >= len) goto next;
                if (text[j] == '[' && (text[j + 1] == ' ' || text[j + 1] == 'x' || text[j + 1] == 'X') && text[j + 2] == ']' && (text[j + 3] == ' ' || text[j + 3] == '\t'))
                    result.Add(j + 1);
            }
        next:
            if (nl < 0) break;
            pos = nl + 1;
        }
        return result;
    }

    // Returns the toggled source, or null when the ordinal cannot be mapped unambiguously.
    public static string Toggle(string text, int ordinal, int expectedCount)
    {
        var marks = Locate(text);
        if (marks.Count != expectedCount || ordinal < 0 || ordinal >= marks.Count) return null;
        int at = marks[ordinal];
        return string.Concat(text.AsSpan(0, at), text[at] == ' ' ? "x" : " ", text.AsSpan(at + 1));
    }
}
