using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Line diff for the save-conflict view: common prefix/suffix are stripped, the middle is
// aligned by LCS when small enough and otherwise shown as a whole replacement. Output is
// bounded so a list control can always display it; counts stay exact even when truncated.
internal static class TextDiff
{
    internal sealed record Result(List<string> Lines, int Removed, int Added, bool Truncated);
    public const int MaxOutputLines = 1800;
    private const long MaxCells = 4_000_000;

    public static Result Unified(string oldText, string newText, int context = 2)
    {
        string[] a = Split(oldText), b = Split(newText);
        List<byte> ops = Ops(a, b);   // 0 = equal, 1 = removed from old, 2 = added in new
        int n = ops.Count;
        int[] ia = new int[n + 1], ib = new int[n + 1];
        for (int k = 0, x = 0, y = 0; k <= n; k++)
        {
            ia[k] = x; ib[k] = y;
            if (k == n) break;
            if (ops[k] != 2) x++;
            if (ops[k] != 1) y++;
        }
        var lines = new List<string>(); int removed = 0, added = 0; bool truncated = false;
        void Emit(string s) { if (lines.Count < MaxOutputLines) lines.Add(s); else truncated = true; }
        int k0 = 0;
        while (k0 < n)
        {
            while (k0 < n && ops[k0] == 0) k0++;
            if (k0 >= n) break;
            int start = Math.Max(0, k0 - context), last = k0, k = k0;
            while (k < n)
            {
                if (ops[k] != 0) { last = k; k++; continue; }
                int run = k;
                while (run < n && ops[run] == 0) run++;
                if (run < n && run - k <= 2 * context) { k = run; continue; }
                break;
            }
            int end = Math.Min(n, last + 1 + context);
            Emit("@@ 磁盘 " + (ia[start] + 1) + "," + (ia[end] - ia[start]) + " 草稿 " + (ib[start] + 1) + "," + (ib[end] - ib[start]) + " @@");
            for (int q = start; q < end; q++)
            {
                if (ops[q] == 1) removed++; else if (ops[q] == 2) added++;
                Emit(ops[q] == 0 ? "  " + a[ia[q]] : ops[q] == 1 ? "- " + a[ia[q]] : "+ " + b[ib[q]]);
            }
            k0 = end;
        }
        if (truncated) lines.Add("… 差异过长，其余已省略");
        return new(lines, removed, added, truncated);
    }

    private static string[] Split(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.EndsWith('\n')) t = t[..^1];
        return t.Split('\n');
    }

    private static List<byte> Ops(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, p = 0;
        while (p < n && p < m && string.Equals(a[p], b[p], StringComparison.Ordinal)) p++;
        int s = 0;
        while (s < n - p && s < m - p && string.Equals(a[n - 1 - s], b[m - 1 - s], StringComparison.Ordinal)) s++;
        int na = n - p - s, nb = m - p - s;
        var ops = new List<byte>(n + m);
        for (int i = 0; i < p; i++) ops.Add(0);
        if ((long)na * nb <= MaxCells && na > 0 && nb > 0)
        {
            int[,] L = new int[na + 1, nb + 1];
            for (int i = na - 1; i >= 0; i--)
                for (int j = nb - 1; j >= 0; j--)
                    L[i, j] = string.Equals(a[p + i], b[p + j], StringComparison.Ordinal) ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
            int x = 0, y = 0;
            while (x < na && y < nb)
            {
                if (string.Equals(a[p + x], b[p + y], StringComparison.Ordinal)) { ops.Add(0); x++; y++; }
                else if (L[x + 1, y] >= L[x, y + 1]) { ops.Add(1); x++; }
                else { ops.Add(2); y++; }
            }
            while (x++ < na) ops.Add(1);
            while (y++ < nb) ops.Add(2);
        }
        else
        {
            for (int i = 0; i < na; i++) ops.Add(1);
            for (int j = 0; j < nb; j++) ops.Add(2);
        }
        for (int i = 0; i < s; i++) ops.Add(0);
        return ops;
    }
}
