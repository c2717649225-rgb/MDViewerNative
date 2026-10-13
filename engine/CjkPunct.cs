// 中文标点挤压(S75,自用走查第四轮;docs/mcp-plan-s75.md 第 4 项),参照 clreq:
// 1) 连续标点:闭合标点后面紧跟标点时压去它右侧半字;开放标点后面紧跟开放标点时压去后一个左侧半字。
// 2) 行首开括号:折行后(非段首、非硬换行后)压去左侧半字,同 CSS text-spacing-trim 的 space-first(S74 起于标题)。
// 前提是被压的半字确为空白:雅黑/雅黑 UI 实测闭合类墨迹都在左半(最右 0.51em),开放类都在右半(最左 0.50em)。
// 弯引号在两种雅黑中宽度不一致(雅黑 UI 为比例宽度),不纳入。
using System.Collections.Generic;

namespace MDViewerNative;

internal static class CjkPunct
{
    private const string Closers = "，。、；：！？）】」』》〉〕";
    private const string SqueezeOpeners = "（【「『《〈〔";
    internal const string Openers = "（【「『〔〖《〈｛［〘〚";

    private static bool TrailSqueezed(string text, int i) =>
        Closers.IndexOf(text[i]) >= 0 && i + 1 < text.Length && (Closers.IndexOf(text[i + 1]) >= 0 || SqueezeOpeners.IndexOf(text[i + 1]) >= 0);

    private static bool LeadSqueezed(string text, int i) =>
        SqueezeOpeners.IndexOf(text[i]) >= 0 && i > 0 && SqueezeOpeners.IndexOf(text[i - 1]) >= 0;

    // 挤压后保留的宽度比例(KP 量宽用)。
    internal static double Kept(string text, int i) => 1.0 - (TrailSqueezed(text, i) ? 0.5 : 0.0) - (LeadSqueezed(text, i) ? 0.5 : 0.0);

    public static void Squeeze(nint layout, string text, List<(int s, int l)> skip = null)
    {
        if (layout == 0 || text == null || text.Length < 2 || text.IndexOfAny(Closers.ToCharArray()) < 0 && text.IndexOfAny(SqueezeOpeners.ToCharArray()) < 0) return;
        // 先量后改:SetCharacterSpacing 会使布局失效,边量边改会让每次量宽都重排整段。
        var marks = new List<(int i, bool lead, bool trail, float advance)>();
        for (int i = 0; i < text.Length; i++)
        {
            bool trail = TrailSqueezed(text, i), lead = LeadSqueezed(text, i);
            if (!trail && !lead || Skipped(skip, i)) continue;
            marks.Add((i, lead, trail, Dw.RangeWidth(layout, (uint)i, 1)));
        }
        foreach (var (i, lead, trail, advance) in marks)
            if (advance > 0f) Dw.SetCharacterSpacing(layout, lead ? -advance / 2f : 0f, trail ? -advance / 2f : 0f, (uint)i, 1);
    }

    private static bool Skipped(List<(int s, int l)> skip, int i)
    {
        if (skip == null) return false;
        foreach (var (s, l) in skip) if (i >= s && i < s + l) return true;
        return false;
    }

    // 单行布局(KP 行)的行首开括号。
    public static void TrimFirst(nint layout, string text)
    {
        if (layout == 0 || string.IsNullOrEmpty(text) || Openers.IndexOf(text[0]) < 0) return;
        float advance = Dw.RangeWidth(layout, 0, 1);
        if (advance > 0f) Dw.SetCharacterSpacing(layout, -advance / 2f, 0f, 0, 1);
    }

    // 自动折行布局:第二行起的行首开括号。压缩后行内容变宽松,行首可能改变:最多复检两轮,不再位于行首的括号恢复原样。
    // 硬换行后的行首不压(space-first)。已因连续开括号压过左半字的不再压。
    public static void TrimLineStarts(nint layout, string text)
    {
        if (layout == 0 || string.IsNullOrEmpty(text) || text.IndexOfAny(Openers.ToCharArray()) < 0) return;
        var trimmed = new HashSet<uint>();
        for (int pass = 0; pass < 2; pass++)
        {
            var marks = new List<(uint at, float advance)>();   // 先量后改,理由同 Squeeze
            foreach (uint at in LineStarts(layout))
            {
                if (at >= text.Length || Openers.IndexOf(text[(int)at]) < 0 || trimmed.Contains(at)) continue;
                if (at > 0 && text[(int)at - 1] == '\n' || LeadSqueezed(text, (int)at)) continue;
                marks.Add((at, Dw.RangeWidth(layout, at, 1)));
            }
            bool changed = false;
            foreach (var (at, advance) in marks)
                if (advance > 0f && Dw.SetCharacterSpacing(layout, -advance / 2f, 0f, at, 1)) { trimmed.Add(at); changed = true; }
            if (!changed) break;
        }
        if (trimmed.Count == 0) return;
        var starts = new HashSet<uint>(LineStarts(layout));
        foreach (uint at in trimmed)
            if (!starts.Contains(at)) Dw.SetCharacterSpacing(layout, 0f, 0f, at, 1);
    }

    // 第二行起各行的起始位置。
    private static IEnumerable<uint> LineStarts(nint layout)
    {
        var lines = Dw.Lines(layout);
        uint pos = 0;
        for (int i = 0; i < lines.Length - 1; i++) { pos += lines[i].length; yield return pos; }
    }
}
