// 段内换行的显示规则(S75,自用走查第四轮;docs/mcp-plan-s75.md 第 1 项)。
// 解析器保留源码换行:软换行为 '\n',硬换行(行尾反斜杠、<br>)先标为 U+2028。
// 收尾时统一转换,与 GitHub 渲染 .md 一致:
//   硬换行(含行尾两个以上空格的 '\n')→ '\n';
//   软换行 → 空格;两侧都是中日文字符时不补(CSS Text 3 换行变换:East Asian 宽字符之间去掉换行)。
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal static class LineBreaks
{
    internal const char Hard = '\u2028';

    public static void Resolve(List<Run> runs)
    {
        for (int r = 0; r < runs.Count; r++)
        {
            Run run = runs[r];
            if (run.Code || run.Opaque || run.Img || run.Text == null) continue;
            string t = run.Text;
            if (t.IndexOf('\n') < 0 && t.IndexOf(Hard) < 0) continue;
            var sb = new StringBuilder(t.Length);
            for (int k = 0; k < t.Length; k++)
            {
                char c = t[k];
                if (c == Hard) { TrimSpaces(sb); sb.Append('\n'); continue; }
                if (c != '\n') { sb.Append(c); continue; }
                int spaces = TrimSpaces(sb);
                if (spaces >= 2) { sb.Append('\n'); continue; }
                int prev = sb.Length > 0 ? LastScalar(sb.ToString()) : PrevRunScalar(runs, r);
                int next = k + 1 < t.Length ? FirstScalar(t, k + 1) : NextRunScalar(runs, r);
                if (!JoinsTight(prev, next)) sb.Append(' ');
            }
            run.Text = sb.ToString();
        }
    }

    // 换行两侧都是东亚宽字符时不补空格;弯引号、省略号、破折号、间隔号(宽度随字体)一侧为宽字符即可。
    internal static bool JoinsTight(int a, int b) =>
        (Wide(a) && (Wide(b) || Ambiguous(b))) || (Ambiguous(a) && Wide(b));

    // East Asian Width 为 F/W/H 的主要区段,按 CSS 规则排除谚文。
    internal static bool Wide(int cp) =>
        cp is >= 0x2E80 and <= 0x303E
        || cp is >= 0x3041 and <= 0x33FF && cp is not (>= 0x3130 and <= 0x318F)
        || cp is >= 0x3400 and <= 0x4DBF
        || cp is >= 0x4E00 and <= 0x9FFF
        || cp is >= 0xA000 and <= 0xA4CF
        || cp is >= 0xF900 and <= 0xFAFF
        || cp is >= 0xFE30 and <= 0xFE4F
        || cp is >= 0xFF01 and <= 0xFF60
        || cp is >= 0xFF61 and <= 0xFF9F
        || cp is >= 0xFFE0 and <= 0xFFE6
        || cp is >= 0x20000 and <= 0x3FFFD;

    private static bool Ambiguous(int cp) => cp is 0x2018 or 0x2019 or 0x201C or 0x201D or 0x2026 or 0x2014 or 0x00B7;

    private static int TrimSpaces(StringBuilder sb)
    {
        int n = 0;
        while (sb.Length > 0 && sb[^1] is ' ' or '\t') { sb.Length--; n++; }
        return n;
    }

    private static int LastScalar(string s)
    {
        int i = s.Length - 1;
        if (i > 0 && char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1])) return char.ConvertToUtf32(s[i - 1], s[i]);
        return s[i];
    }

    private static int FirstScalar(string s, int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        if (i >= s.Length) return 0;
        if (i + 1 < s.Length && char.IsSurrogatePair(s[i], s[i + 1])) return char.ConvertToUtf32(s[i], s[i + 1]);
        return s[i];
    }

    // 相邻 run:图片、原始 HTML 视为非宽字符(补空格);空 run 跳过。
    private static int PrevRunScalar(List<Run> runs, int r)
    {
        for (int j = r - 1; j >= 0; j--)
        {
            if (runs[j].Img || runs[j].Opaque) return 0;
            if (!string.IsNullOrEmpty(runs[j].Text)) return LastScalar(runs[j].Text);
        }
        return 0;
    }

    private static int NextRunScalar(List<Run> runs, int r)
    {
        for (int j = r + 1; j < runs.Count; j++)
        {
            if (runs[j].Img || runs[j].Opaque) return 0;
            if (!string.IsNullOrEmpty(runs[j].Text)) return FirstScalar(runs[j].Text, 0);
        }
        return 0;
    }
}
