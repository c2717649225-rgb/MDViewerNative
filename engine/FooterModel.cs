using System;
using System.Collections.Generic;
using System.Globalization;

namespace MDViewerNative;

// U4：页脚（兼任状态栏）的内容（规格 §5）。纯函数：给定阅读状态，决定左右两侧写什么。
// 视图在 FooterView；这里不依赖渲染层与宿主。
internal enum FooterLead { Fish, External, Internal, None }

internal readonly record struct FooterItem(string Text, bool Accent);

internal readonly record struct FooterHover(bool External, string Text);

internal sealed record FooterContent(FooterLead Lead, string Left, bool LeftSerif, IReadOnlyList<FooterItem> Right);

internal static class FooterModel
{
    public const string Opening = "开篇";

    // 阅读：左 = 鱼尾 + 当前章节（悬停链接时换成 ↗ 网址 / → 目标章节）；右 = 临时状态 · 字数 · 已读 n% · 缩放。
    public static FooterContent Reading(string chapter, int words, float readRatio, float zoom, FooterHover? hover = null, string status = null)
    {
        var right = new List<FooterItem>(4);
        if (!string.IsNullOrEmpty(status)) right.Add(new(status, true));
        right.Add(new(Words(words), false));
        right.Add(new("已读 " + Percent(readRatio), false));
        if (ShowZoom(zoom)) right.Add(new(Percent(zoom), false));
        if (hover is FooterHover h && !string.IsNullOrEmpty(h.Text))
            return new(h.External ? FooterLead.External : FooterLead.Internal, h.Text, false, right);
        return new(FooterLead.Fish, string.IsNullOrEmpty(chapter) ? Opening : chapter, true, right);
    }

    // U9a：编辑：左 = 鱼尾 + 第 n 行（源码行）；右 = 临时状态 · 格式（Markdown · 编码）· 缩放。
    public static FooterContent Editing(int line, string format, float zoom, string status = null)
    {
        var right = new List<FooterItem>(3);
        if (!string.IsNullOrEmpty(status)) right.Add(new(status, true));
        right.Add(new(format ?? "", false));
        if (ShowZoom(zoom)) right.Add(new(Percent(zoom), false));
        return new(FooterLead.Fish, "第 " + Math.Max(1, line).ToString(CultureInfo.InvariantCulture) + " 行", true, right);
    }

    // U10：起始页：左 = 起始页；右 = 临时状态 · n 个文档已打开。
    public static FooterContent Home(int openDocuments, string status = null)
    {
        var right = new List<FooterItem>(2);
        if (!string.IsNullOrEmpty(status)) right.Add(new(status, true));
        right.Add(new(Math.Max(0, openDocuments).ToString(CultureInfo.InvariantCulture) + " 个文档已打开", false));
        return new(FooterLead.Fish, "起始页", true, right);
    }

    public static string Words(int n) => Math.Max(0, n).ToString("N0", CultureInfo.InvariantCulture) + " 字";

    public static string Percent(float ratio) => ((int)MathF.Round(Math.Clamp(float.IsFinite(ratio) ? ratio : 0f, 0f, 9.99f) * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

    static bool ShowZoom(float zoom) => float.IsFinite(zoom) && MathF.Abs(zoom - 1f) >= 0.005f;

    // 字数与演示同口径：每个汉字算一个，连续的字母数字算一个。
    public static int CountWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff') { n++; inWord = false; }
            else if (char.IsAsciiLetterOrDigit(c)) { if (!inWord) n++; inWord = true; }
            else inWord = false;
        }
        return n;
    }

    // 当前章节：顶部在视口上缘往下 slack 以内（或已越过上缘）的最后一个二级及以下标题；一个都没有 = 开篇（-1）。
    // slack 与书签绳刻度、刻度跳转共用演示口径（U5b 由 24 改为 94）。
    public static int CurrentChapter(IReadOnlyList<(int Level, float Y)> heads, float offset, float slack = DeskMetrics.ChapterSlack)
    {
        int current = -1;
        for (int i = 0; i < heads.Count; i++)
        {
            if (heads[i].Level <= 1) continue;
            if (heads[i].Y <= offset + slack) current = i; else break;
        }
        return current;
    }

    public static string ChapterLabel(string seal, string title) => string.IsNullOrEmpty(seal) ? title ?? "" : seal + " · " + title;

    public static float ReadRatio(float offset, float totalHeight, float viewHeight)
    {
        float span = totalHeight - viewHeight;
        return span > 0.5f ? Math.Clamp(offset / span, 0f, 1f) : 0f;
    }
}

// 页脚右侧的临时状态（朱色，如「已保存」），到时自动消失。时间由调用方传入，便于测试。
internal sealed class FooterStatus
{
    public const int DefaultMs = 3000;
    string _text;
    long _until;

    public void Show(string text, long nowMs, int durationMs = DefaultMs) { _text = text; _until = nowMs + Math.Max(0, durationMs); }
    public void Clear() => _text = null;
    public string Current(long nowMs) => _text != null && nowMs < _until ? _text : null;
    public int DueMs(long nowMs) => _text != null && nowMs < _until ? (int)Math.Min(int.MaxValue, _until - nowMs) : -1;
}
