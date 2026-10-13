using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MDViewerNative;

// U10：起始页。纯逻辑：固定标签的身份与切换顺序、问候与中文日期、最近文件的摘要与时间、版面几何与命中。
// 起始页是界面上的固定标签，不是文档会话：不进文档标签集合、不写入 tabs=（ui-v3-plan U10）。
internal enum HomeTargetKind { None, Continue, Card, Open }

internal readonly record struct HomeTarget(HomeTargetKind Kind, int Index = -1)
{
    public static readonly HomeTarget None = default;
}

internal readonly record struct HomeRect(float X, float Y, float W, float H)
{
    public bool Contains(float x, float y) => W > 0f && x >= X && x < X + W && y >= Y && y < Y + H;
}

// 一条最近文件：名字、标题（首个标题，没有就用文件名）、摘要、所在文件夹、时间、已读比例（没读过为 null）。
internal sealed record HomeEntry(string Path, string Name, string Title, string Excerpt, string Folder, string When, float? Progress);

internal sealed record HomeModel(HomeEntry Continue, IReadOnlyList<HomeEntry> Recent, string Greeting, string Date);

// 视图量出来的文字尺寸（几何只做加减）。
internal readonly record struct HomeMeasure(float SecH, float ContTextH, float WhereH, float ButtonW, float ButtonH, float CardH, float OpenW, float GreetW);

internal sealed record HomeFrame(bool ShowGreeting, float LeftX, float LeftW, float GreetX, float GreetY,
    HomeRect ContinueHead, HomeRect ContinueCard, HomeRect ContinueButton, HomeRect RecentHead, HomeRect[] Cards, int Columns,
    HomeRect Actions, HomeRect OpenButton, float Height);

internal static class Home
{
    // —— 固定标签 ——
    public static readonly Guid TabId = new("6d8f3a52-0b1e-4c7a-9f10-5e3a2c1b0d01");
    public const string TabTitle = "起始页", TabHint = "最近的文件";
    public const int RecentMax = 6;

    // 关掉最后一个文档后，DocumentTabs 会留一个空白会话；起始页显示时它不作为标签出现（不再有空白标签）。
    public static bool HidesPlaceholder(bool home, int count, bool pathless, bool dirty) => home && count == 1 && pathless && !dirty;

    // Ctrl+Tab 顺序：起始页在最前（位置 0），文档依次在后。
    public static int Cycle(int position, int count, int step) => count <= 0 ? 0 : ((position + step) % count + count) % count;

    // —— 文字 ——
    public static string Greeting(int hour) => hour < 5 ? "夜深了" : hour < 11 ? "早上好" : hour < 13 ? "中午好" : hour < 18 ? "下午好" : "晚上好";

    const string Digits = "〇一二三四五六七八九";

    public static string ChineseNumber(int n)
    {
        if (n < 10) return Digits[Math.Max(0, n)].ToString();
        if (n == 10) return "十";
        if (n < 20) return "十" + Digits[n - 10];
        return Digits[n / 10] + "十" + (n % 10 != 0 ? Digits[n % 10].ToString() : "");
    }

    public static string ChineseDate(DateTime d)
    {
        var y = new StringBuilder();
        foreach (char c in d.Year.ToString(CultureInfo.InvariantCulture)) y.Append(Digits[c - '0']);
        return y + "年" + ChineseNumber(d.Month) + "月" + ChineseNumber(d.Day) + "日\u3000星期" + "日一二三四五六"[(int)d.DayOfWeek];
    }

    // 卡片右下角的时间（文件最后修改时间）。
    public static string RelativeTime(DateTime t, DateTime now)
    {
        var span = now - t;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟前";
        string hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == now.Date) return "今天 " + hm;
        if (t.Date == now.Date.AddDays(-1)) return "昨天 " + hm;
        string md = t.Month.ToString(CultureInfo.InvariantCulture) + " 月 " + t.Day.ToString(CultureInfo.InvariantCulture) + " 日";
        return t.Year == now.Year ? md : t.Year.ToString(CultureInfo.InvariantCulture) + " 年 " + md;
    }

    public static string Percent(float progress) => ((int)MathF.Round(Math.Clamp(progress, 0f, 1f) * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

    // 从文件开头取标题（第一个 ATX 标题）与摘要（第一段正文，去掉常见标记）。只看给定的文本，不读盘。
    public static (string Title, string Excerpt) Summarize(string text, int maxExcerpt = 120)
    {
        string title = null;
        var sb = new StringBuilder();
        if (string.IsNullOrEmpty(text)) return (null, "");
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (i = 1; i < lines.Length && lines[i].Trim() is not ("---" or "..."); i++) { }
            i++;
        }
        bool fence = false;
        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal)) { fence = !fence; if (sb.Length > 0) break; continue; }
            if (fence) continue;
            if (t.Length == 0) { if (sb.Length > 0) break; continue; }
            if (t[0] == '#')
            {
                string h = t.TrimStart('#').Trim().TrimEnd('#').Trim();
                if (title == null && h.Length > 0) title = Inline(h);
                if (sb.Length > 0) break;
                continue;
            }
            if (t[0] is '<' or '|' || t.StartsWith("![", StringComparison.Ordinal) || IsRule(t)) { if (sb.Length > 0) break; continue; }
            string body = Inline(StripMarker(t));
            if (body.Length == 0) continue;
            if (sb.Length > 0 && !IsCjk(sb[^1]) && !IsCjk(body[0])) sb.Append(' ');
            sb.Append(body);
            if (sb.Length >= maxExcerpt) break;
        }
        string excerpt = sb.Length > maxExcerpt ? sb.ToString(0, maxExcerpt) + "…" : sb.ToString();
        return (title, excerpt);
    }

    static bool IsRule(string t) => t.Length >= 3 && (t.Replace(" ", "").Trim('-').Length == 0 || t.Replace(" ", "").Trim('*').Length == 0 || t.Replace(" ", "").Trim('_').Length == 0);

    static bool IsCjk(char c) => c >= 0x2E80;

    static string StripMarker(string t)
    {
        while (t.StartsWith('>')) t = t[1..].TrimStart();
        if (t.Length >= 2 && t[0] is '-' or '*' or '+' && t[1] == ' ') t = t[2..];
        int d = 0;
        while (d < t.Length && char.IsAsciiDigit(t[d])) d++;
        if (d > 0 && d + 1 < t.Length && t[d] is '.' or ')' && t[d + 1] == ' ') t = t[(d + 2)..];
        if (t.StartsWith("[ ] ", StringComparison.Ordinal) || t.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        return t;
    }

    // 链接留文字、图片去掉、强调与行内代码的记号去掉。
    static string Inline(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '!' && i + 1 < s.Length && s[i + 1] == '[')
            {
                int close = s.IndexOf("](", i + 2, StringComparison.Ordinal), end = close < 0 ? -1 : s.IndexOf(')', close + 2);
                if (end > 0) { i = end; continue; }
            }
            if (c == '[')
            {
                int close = s.IndexOf("](", i + 1, StringComparison.Ordinal), end = close < 0 ? -1 : s.IndexOf(')', close + 2);
                if (end > 0) { sb.Append(s, i + 1, close - i - 1); i = end; continue; }
            }
            if (c is '*' or '`' or '~' || (c == '_' && (i + 1 < s.Length && s[i + 1] == '_' || i > 0 && s[i - 1] == '_'))) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // 最近打开：去掉「继续阅读」那一篇，最多 6 个。
    public static List<string> RecentPicks(IEnumerable<string> recent, string continuePath, Func<string, bool> available)
    {
        var picks = new List<string>(RecentMax);
        foreach (string p in recent)
        {
            if (picks.Count >= RecentMax) break;
            if (string.IsNullOrEmpty(p) || string.Equals(p, continuePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (picks.Exists(q => string.Equals(q, p, StringComparison.OrdinalIgnoreCase)) || !available(p)) continue;
            picks.Add(p);
        }
        return picks;
    }

    // —— 版面（演示 .home）——
    public const float MaxWidth = 1180f, SidePad = 56f, PadTop = 56f, PadBottom = 90f, ColumnGap = 56f;
    public const float NarrowBelow = 820f, NarrowSide = 28f, NarrowPadY = 40f;
    public const float SecSize = 12f, SecSpacing = 0.25f, SecGap = 14f, SecRuleGap = 12f;
    public const float ContPadTop = 26f, ContPadRight = 28f, ContPadBottom = 22f, ContPadLeft = 34f, ContGapX = 28f, ContGapY = 6f, ContBottom = 38f, ContRadius = 6f;
    public const float TitleSize = 24f, SubSize = 14f, SubGap = 6f, ProgGap = 14f, ProgH = 2f, WhereGap = 12f, WhereSize = 12f;
    public const float RibbonW = 10f, RibbonH = 40f;
    public const float ButtonSize = 13.5f, ButtonInsetX = 16f, ButtonPadY = 8f, ButtonRadius = 9f, ButtonIcon = 15f, ButtonIconGap = 8f;
    public const float CardMin = 230f, CardGap = 18f, CardBottom = 34f, CardRadius = 5f;
    public const float CardPadTop = 18f, CardInsetX = 20f, CardPadBottom = 14f, NameSize = 16f, NameGap = 6f, ExcerptSize = 12.5f, ExcerptLine = 1.7f, ExcerptGap = 12f, FootSize = 11.5f, FootPad = 9f;
    public const float ActionsPadY = 18f, ActionsInsetX = 20f, ActionsRadius = 12f, ActionsStroke = 1.5f, ActionsGap = 14f, ActionsText = 13f;
    public const float GreetSize = 50f, GreetSpacing = 0.18f, GreetLine = 1.2f, DateSize = 15f, DateSpacing = 0.35f, DatePadTop = 10f, GreetGap = 22f, GreetPadTop = 6f;
    public const float StampW = 32f, StampPadY = 6f, StampSize = 13f, StampSpacing = 0.15f, StampTilt = 3f, StampTop = 10f, StampRadius = 3f;
    public const string StampText = "书桌";
    public static readonly float[] CardTilt = { -0.8f, 0.6f, -0.4f, 0.9f, -0.6f, 0.5f };
    public const float CardHoverLift = 4f, ContHoverLift = 2f;

    public static float ExcerptHeight => 3.4f * ExcerptSize;   // 两行，多出的截掉

    public static int Columns(float width) => Math.Max(1, (int)((width + CardGap) / (CardMin + CardGap)));

    public static HomeFrame Layout(float viewW, float top, HomeMeasure m, bool hasContinue, int cards)
    {
        bool greet = viewW >= NarrowBelow;
        float side = greet ? SidePad : NarrowSide, padTop = greet ? PadTop : NarrowPadY, padBottom = greet ? PadBottom : NarrowPadY;
        float homeW = MathF.Min(viewW, MaxWidth), homeX = MathF.Max(0f, (viewW - homeW) / 2f);
        float innerX = homeX + side, innerW = MathF.Max(0f, homeW - 2f * side);
        float leftW = greet ? MathF.Max(0f, innerW - ColumnGap - m.GreetW) : innerW;
        float y = top + padTop;
        HomeRect contHead = default, card = default, button = default, recentHead = default;
        if (hasContinue)
        {
            contHead = new(innerX, y, leftW, m.SecH);
            y += m.SecH + SecGap;
            float row = MathF.Max(m.ContTextH, m.ButtonH);
            float h = ContPadTop + row + ContGapY + ProgGap + ProgH + ContGapY + WhereGap + m.WhereH + ContPadBottom;
            card = new(innerX, y, leftW, h);
            button = new(innerX + leftW - ContPadRight - m.ButtonW, y + ContPadTop + (row - m.ButtonH) / 2f, m.ButtonW, m.ButtonH);
            y += h + ContBottom;
        }
        var rects = new HomeRect[Math.Max(0, cards)];
        int cols = Columns(leftW);
        if (cards > 0)
        {
            recentHead = new(innerX, y, leftW, m.SecH);
            y += m.SecH + SecGap;
            float cw = (leftW - CardGap * (cols - 1)) / cols;
            for (int i = 0; i < cards; i++)
                rects[i] = new(innerX + (i % cols) * (cw + CardGap), y + (i / cols) * (m.CardH + CardGap), cw, m.CardH);
            int rows = (cards + cols - 1) / cols;
            y += rows * m.CardH + (rows - 1) * CardGap + CardBottom;
        }
        var actions = new HomeRect(innerX, y, leftW, 2f * ActionsPadY + m.ButtonH);
        var open = new HomeRect(innerX + ActionsInsetX, y + ActionsPadY, m.OpenW, m.ButtonH);
        y += actions.H;
        return new(greet, innerX, leftW, innerX + leftW + ColumnGap, top + padTop + GreetPadTop,
            contHead, card, button, recentHead, rects, cols, actions, open, y + padBottom - top);
    }

    // 卡片的高：上 18 + 名字 + 6 + 两行摘要 + 12 + 虚线 1 + 9 + 页脚一行 + 下 14。
    public static float CardHeight(float nameH, float footH) => CardPadTop + nameH + NameGap + ExcerptHeight + ExcerptGap + 1f + FootPad + footH + CardPadBottom;

    public static HomeTarget Hit(HomeFrame f, float x, float y)
    {
        if (f == null) return HomeTarget.None;
        if (f.ContinueCard.Contains(x, y)) return new(HomeTargetKind.Continue);
        for (int i = 0; i < f.Cards.Length; i++) if (f.Cards[i].Contains(x, y)) return new(HomeTargetKind.Card, i);
        if (f.OpenButton.Contains(x, y)) return new(HomeTargetKind.Open);
        return HomeTarget.None;
    }

    public static float MaxScroll(HomeFrame f, float viewH) => f == null ? 0f : MathF.Max(0f, f.Height - viewH);
}
