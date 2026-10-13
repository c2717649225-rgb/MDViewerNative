namespace MDViewerNative;

// 两端对齐判定(纯函数,不触 DirectWrite):KP 断出的非末行是否拉伸到栏宽。
// A5:行内没有词间空格也没有 CJK 字符(单个词/不可断长串)时不拉伸,否则 DirectWrite 会把字母拉开("L o n g");
//     严重欠满行(自然宽 < 80% 栏宽)也不拉伸。
// S67 自用走查:空隙摊到每个可伸展位置(汉字间、空格)不得明显可见——含汉字的行每处 ≤ 0.2em(空格多贴着行内代码,
//     代码底色内边距已占位,再拉宽很显眼),纯西文行每个空格 ≤ 0.5em;超出则左齐。含长行内代码的中文行自然宽
//     虽 ≥ 80%,空隙却集中在少数位置,出现“全 部 测 试”式逐字散开。
internal static class Justification
{
    public static bool HasInnerBreak(string line)
    {
        foreach (char c in line.Trim()) if (c == ' ' || IsCjk(c)) return true;
        return false;
    }

    public static bool ShouldJustify(string line, float naturalWidth, float width, float em)
    {
        if (!HasInnerBreak(line) || naturalWidth < 0.8f * width) return false;
        float slack = width - naturalWidth;
        if (slack <= 0f) return true;   // 超宽行维持原逻辑
        int cjk = 0, spaces = 0;
        foreach (char c in line.Trim()) { if (c == ' ') spaces++; else if (IsCjk(c)) cjk++; }
        return slack <= (cjk > 0 ? 0.2f : 0.5f) * em * (cjk + spaces);
    }

    private static bool IsCjk(char c) => c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;
}
