using System;

namespace MDViewerNative;

// U3：正文版式的唯一来源（规格 §2；ADR-0003 第 2 条）。纯值与纯函数，不依赖渲染层。
// Classic 复现 v2.2 版式（数值与原先字面量逐一相同，旧路径渲染不变）；Desk 是 v3「安静的书桌」。
// 间距一律以正文字号为单位（CSS 里相对标题字号的 em 已换算）。U13 删除 Classic。
internal sealed class ReadingStyle
{
    public bool IsDesk { get; private init; }
    public float DefaultBodySize { get; private init; }
    public float LineRatio { get; private init; }
    public float PageTop { get; private init; }
    public float PageBottom { get; private init; }
    public float RightPadRatio { get; private init; }   // 右留白 / 左留白；窄窗时两侧按同一曲线收窄
    public bool SerifHeadings { get; private init; }

    public static readonly ReadingStyle Classic = new()
    {
        DefaultBodySize = 16f,
        LineRatio = 1.75f,
        PageTop = WindowFit.PageTop,
        PageBottom = WindowFit.PageBottom,
        RightPadRatio = 1f,
    };

    public static readonly ReadingStyle Desk = new()
    {
        IsDesk = true,
        DefaultBodySize = DeskMetrics.BodyFontSize,
        LineRatio = DeskMetrics.BodyLineHeight,
        PageTop = DeskMetrics.BodyTopSpace,
        PageBottom = DeskMetrics.BodyBottomSpace,
        RightPadRatio = DeskMetrics.BodyMarginRight / DeskMetrics.BodyMarginLeft,
        SerifHeadings = true,
    };

    public const float H3Scale = 1.05f;
    public const float QuoteScale = 1.08f;       // 引用：宋体 1.08em、次级墨色、2px 浅线（演示 blockquote）
    public const float InlineCodeScale = 0.85f;  // 行内代码字号（v2.2 为固定 13.6，即 16 档的 0.85）

    // Desk 引用上下 1.9em；同一段引用里的相邻段落只隔 .4em（相对引用字号）。
    public float DeskQuoteMargin(float bs) => 1.9f * bs;
    public float DeskQuoteInnerGap(float bs) => 0.4f * QuoteScale * bs;

    public float HeadingScale(int level) => IsDesk
        ? level switch { 1 => DeskMetrics.H1Scale, 2 => DeskMetrics.H2Scale, 3 => H3Scale, _ => 1f }
        : level switch { 1 => 1.625f, 2 => 1.3125f, 3 => 1.125f, _ => 1f };

    // 字距（em，相对标题字号）：H1 .05、H2 .04（演示 letter-spacing）。
    public float HeadingTracking(int level) => IsDesk ? level switch { 1 => 0.05f, 2 => 0.04f, _ => 0f } : 0f;

    // 标题上 / 下间距（Desk）：H1 0/.5em、H2 2.6/.9em、H3 2/.6em（各自字号）；H1 出现在文中时与 H2 同上距。
    public float DeskHeadingBefore(int level, float bs) => bs * level switch
    {
        1 or 2 => 2.6f * DeskMetrics.H2Scale,
        3 => 2f * H3Scale,
        _ => 2f,
    };

    public float DeskHeadingAfter(int level, float bs) => bs * level switch
    {
        1 => 0.5f * DeskMetrics.H1Scale,
        2 => 0.9f * DeskMetrics.H2Scale,
        3 => 0.6f * H3Scale,
        _ => 0.6f,
    };

    public float DeskParagraphAfter(float bs) => DeskMetrics.ParagraphSpacing * bs;

    // H2 前的方印：边长 1.55×(.6em) = .93em，与文字相隔 .7em（em 为 H2 字号）。
    public static float SealSide(float h2Size) => 0.93f * h2Size;
    public static float SealIndent(float h2Size) => (0.93f + 0.7f) * h2Size;
    public const float SealTiltDegrees = -3f;

    // H3 前的朱色短线：长 .9em，与文字相隔 .6em（em 为 H3 字号）。
    public static float DashLength(float h3Size) => 0.9f * h3Size;
    public static float DashIndent(float h3Size) => 1.5f * h3Size;

    // 壹贰叁…拾、拾壹…玖拾玖；再往后退回阿拉伯数字（方印放不下）。
    public static string SealNumber(int n)
    {
        const string digits = "零壹贰叁肆伍陆柒捌玖";
        if (n <= 0 || n > 99) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (n < 10) return digits[n].ToString();
        int tens = n / 10, ones = n % 10;
        return (tens == 1 ? "" : digits[tens].ToString()) + "拾" + (ones == 0 ? "" : digits[ones].ToString());
    }

    // 标题自己带编号（“1. 原则”“第二章”“三、”“(一)”“IV.”）时不再加方印，免得两套编号并列。
    public static bool HasOwnNumber(string text)
    {
        ReadOnlySpan<char> s = (text ?? "").AsSpan().TrimStart();
        if (s.IsEmpty) return false;
        if (char.IsDigit(s[0]) || s[0] == '第' || s[0] is >= '①' and <= '⑳') return true;
        if (s[0] is '(' or '（' && s.Length > 1 && (char.IsDigit(s[1]) || IsChineseNumeral(s[1]))) return true;
        int k = 0;
        while (k < s.Length && IsChineseNumeral(s[k])) k++;
        if (k > 0 && k < s.Length && s[k] is '、' or '.' or '．' or ' ' or '　') return true;
        k = 0;
        while (k < s.Length && "IVXLC".Contains(s[k])) k++;
        return k > 0 && k < s.Length && s[k] is '.' or '、' or '．';
    }

    static bool IsChineseNumeral(char c) => "一二三四五六七八九十百〇零壹贰叁肆伍陆柒捌玖拾".Contains(c);
}
