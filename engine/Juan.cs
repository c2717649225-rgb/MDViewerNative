using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U8：目录卷目（演示稿 .juan，规格 §7）。纯层：条目、当前章、键盘移动与版式几何；DWrite 量出的文字高度由视图传入。
// 坐标：卷目覆盖层自己的 DIP 坐标，原点在顶栏下缘左端；Scroll 为卷目内部的滚动量（条目多时可滚）。
internal readonly record struct JuanItem(int Level, string No, string Title, int Percent, float JumpOffset);

internal readonly record struct JuanRow(float Y, float H);

internal readonly record struct JuanFrame(float ColumnX, float ColumnW, float TitleY, float SubY, JuanRow[] Rows, float ContentHeight);

internal enum JuanKey { Up, Down, Home, End }

internal static class Juan
{
    public const int MaxLevel = 3;                       // 演示：h1–h3
    public const float ColumnMax = 600f, SidePad = 40f, TopRatio = 0.09f, BottomPad = 80f;
    public const float TitleSize = 24f, TitleGap = 0.4f * TitleSize, SubSize = 12.5f, SubGap = 2.6f * SubSize;
    public const float RowInsetX = 10f, RowInsetY = 9f, RowRadius = 8f;
    public const float NoSize = 14f, NoWidth = 2.4f * NoSize;          // .no{width:2.4em;font-size:14px}
    public const float L1Size = 19f, L2Size = 16f, L3Size = 13.5f, PageSize = 14f;
    public const float L1Gap = 0.5f * L1Size, L3Indent = 2.4f * L3Size + 10f;
    public const float LeadGap = 12f, LeadMin = 20f, LeadLift = 0.3f;   // 点线在基线上方 .3em
    public const float TagSize = 11f, TagInsetX = 7f, TagInsetY = 1f, TagGap = 10f, TagRadius = 10f;
    public const float CloseTop = 16f, CloseRight = 22f, CloseInsetX = 12f, CloseInsetY = 6f, CloseRadius = 8f, CloseIcon = 10f, CloseGap = 6f;
    public const int FadeMs = 300, RiseMs = 450;
    public const float Rise = 10f, Veil = 0.97f;         // 打开时整列从下方 10 升起；演示为 95% 纸色加 8px 模糊，原生不做模糊，改用 97% 压住底下的字
    public const float WheelStep = 96f, KeepMargin = 16f;

    // 条目：级别 ≤ 3；百分比与书签绳刻度同口径（标题停在视口上方 70 处时的进度）；跳转与刻度同口径（停在 60 处）。
    public static List<JuanItem> Items(IReadOnlyList<(int Level, float Y, string Seal, string Title)> heads, float maxOffset)
    {
        var items = new List<JuanItem>();
        foreach (var h in heads)
        {
            if (h.Level > MaxLevel) continue;
            items.Add(new JuanItem(h.Level, h.Seal ?? "", h.Title ?? "", Percent(h.Y, maxOffset), CordGeometry.TickTarget(h.Y, maxOffset)));
        }
        return items;
    }

    public static int Percent(float headY, float maxOffset) =>
        maxOffset <= 0f ? 0 : (int)MathF.Round(Math.Clamp((headY - DeskMetrics.ChapterTickInset) / maxOffset, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);

    // 当前章（演示 openToc）：顶部越过视口上方 94 的最后一条，含一级标题；一条都没越过时取第一条。
    public static int Current(IReadOnlyList<float> headYs, float offset)
    {
        int current = 0;
        for (int i = 0; i < headYs.Count; i++) if (headYs[i] <= offset + DeskMetrics.ChapterSlack) current = i;
        return headYs.Count == 0 ? -1 : current;
    }

    public static int Move(int focus, int count, JuanKey key)
    {
        if (count <= 0) return -1;
        return key switch
        {
            JuanKey.Up => Math.Max(0, focus - 1),
            JuanKey.Down => Math.Min(count - 1, focus + 1),
            JuanKey.Home => 0,
            _ => count - 1,
        };
    }

    // rowText[i]：该条目标题（按级别字号）的行高；无条目时 Rows 为空。
    public static JuanFrame Layout(float viewW, float viewH, float titleH, float subH, IReadOnlyList<int> levels, IReadOnlyList<float> rowText)
    {
        float colW = MathF.Min(ColumnMax, MathF.Max(0f, viewW - 2f * SidePad));
        float colX = (viewW - colW) / 2f;
        float y = TopRatio * viewH, titleY = y;
        y += titleH + TitleGap;
        float subY = y;
        y += subH + SubGap;
        var rows = new JuanRow[levels.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            float h = rowText[i] + 2f * RowInsetY;
            rows[i] = new JuanRow(y, h);
            y += h + (levels[i] == 1 ? L1Gap : 0f);
        }
        return new JuanFrame(colX, colW, titleY, subY, rows, y + BottomPad);
    }

    public static float MaxScroll(JuanFrame f, float viewH) => MathF.Max(0f, f.ContentHeight - viewH);

    public static float ClampScroll(JuanFrame f, float scroll, float viewH) => Math.Clamp(scroll, 0f, MaxScroll(f, viewH));

    // 键盘移动后让焦点条目整行可见（上下各留 16）。
    public static float Reveal(JuanFrame f, int row, float scroll, float viewH)
    {
        if (row < 0 || row >= f.Rows.Length) return scroll;
        var r = f.Rows[row];
        if (r.Y - KeepMargin < scroll) scroll = r.Y - KeepMargin;
        else if (r.Y + r.H + KeepMargin > scroll + viewH) scroll = r.Y + r.H + KeepMargin - viewH;
        return ClampScroll(f, scroll, viewH);
    }

    // 打开时把当前章放在视口上三分之一附近（条目少时不滚）。
    public static float InitialScroll(JuanFrame f, int row, float viewH)
    {
        if (row < 0 || row >= f.Rows.Length) return 0f;
        var r = f.Rows[row];
        return r.Y + r.H <= viewH - KeepMargin ? 0f : ClampScroll(f, r.Y - viewH / 3f, viewH);
    }

    public static int HitRow(JuanFrame f, float x, float y, float scroll)
    {
        if (x < f.ColumnX || x >= f.ColumnX + f.ColumnW) return -1;
        float cy = y + scroll;
        for (int i = 0; i < f.Rows.Length; i++) if (cy >= f.Rows[i].Y && cy < f.Rows[i].Y + f.Rows[i].H) return i;
        return -1;
    }

    // 右上「关闭 Esc」按钮：textW 为「关闭」与键帽（含内边距）的总宽。
    public static (float X, float Y, float W, float H) CloseBox(float viewW, float textW, float textH) =>
        (viewW - CloseRight - (CloseInsetX * 2f + CloseIcon + CloseGap + textW), CloseTop, CloseInsetX * 2f + CloseIcon + CloseGap + textW, CloseInsetY * 2f + textH);

    // 标题相对行左缘的缩进：一、二级在 10 内边距后留出章节号列；三级不显示章节号，缩进 2.4em+10。
    public static float TitleIndent(int level) => level >= 3 ? L3Indent : RowInsetX + NoWidth;
}
