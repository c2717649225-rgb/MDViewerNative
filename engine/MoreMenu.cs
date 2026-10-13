using System;
using System.Collections.Generic;

namespace MDViewerNative;

// 试用修复：desk「更多」菜单（演示 #morePop：.pop.menu / .mi / .mhr）。纯条目与几何；单位 DIP，坐标相对菜单左上角。
// 演示只列常用的几项（打开、重开关闭的标签、导出、打印、命令面板）；试用反馈补上「另存为」（原先只能从命令面板找到）。
// 其余命令都在命令面板里（它收录全部命令、快捷键与最近文件）。试用反馈：菜单行不再显示快捷键，快捷键只在命令面板里列出。
// 窄窗时折进「更多」的顶栏按钮仍排在最前（A2-B4：折叠的按钮必须够得着）。演示的「竖排阅读」本产品没有，不列。
internal enum MoreIcon { None, Open, SaveAs, Reopen, Export, Print, Palette }

internal readonly record struct MoreItem(int Id, string Label, string Accel, MoreIcon Icon, bool Enabled)
{
    public bool IsRule => Id == 0;
    public static readonly MoreItem Rule = new(0, "", "", MoreIcon.None, false);
}

// 宿主从命令表取来的一条命令（标签、快捷键、此刻能否执行）。
internal readonly record struct MoreCommand(int Id, string Label, string Accel, bool Enabled);

internal sealed record MoreFrame(float W, float H, IReadOnlyList<MoreItem> Items, IReadOnlyList<PanelRect> Rows);

internal static class MoreMenu
{
    public const float Width = 250f, Pad = 6f, ItemInsetX = 10f, ItemInsetY = 8f, ItemRadius = 8f;
    public const float IconSize = 15f, IconGap = 10f, TextSize = 13f, KeySize = 13f * 0.76f;
    public const float RuleGap = 5f, RuleInsetX = 8f;
    public const int Open = 1003, SaveAs = 1012, Reopen = 2205, Export = 1011, Print = 2232, Palette = 2291;

    // 演示顺序：打开、重开 | 导出、打印 | 命令面板。演示里导出没写快捷键，这里照实显示本产品已有的快捷键。
    public static readonly (int Id, string Label, MoreIcon Icon)[] Fixed =
    {
        (Open, "打开文件…", MoreIcon.Open), (SaveAs, "另存为…", MoreIcon.SaveAs), (Reopen, "重新打开关闭的标签", MoreIcon.Reopen), (0, "", MoreIcon.None),
        (Export, "导出为 HTML…", MoreIcon.Export), (Print, "打印…", MoreIcon.Print), (0, "", MoreIcon.None),
        (Palette, "命令面板", MoreIcon.Palette),
    };

    // folded：此刻折进「更多」的顶栏按钮；lookup：按命令号取快捷键与可用性（取不到的项不列）。
    public static IReadOnlyList<MoreItem> Items(IReadOnlyList<MoreCommand> folded, Func<int, MoreCommand?> lookup)
    {
        var items = new List<MoreItem>();
        foreach (var c in folded) items.Add(new MoreItem(c.Id, c.Label, c.Accel, MoreIcon.None, c.Enabled));
        if (items.Count > 0) items.Add(MoreItem.Rule);
        foreach (var (id, label, icon) in Fixed)
        {
            if (id == 0) { if (items.Count > 0 && !items[^1].IsRule) items.Add(MoreItem.Rule); continue; }
            if (lookup(id) is not MoreCommand c) continue;
            items.Add(new MoreItem(id, label, c.Accel, icon, c.Enabled));
        }
        while (items.Count > 0 && items[^1].IsRule) items.RemoveAt(items.Count - 1);
        return items;
    }

    // line：一行文字的高度（13px 文字的行高，调用方量好）。条目高 = 上下 8 + max(行高, 图标 15)；分隔线上下各 5。
    public static MoreFrame Layout(IReadOnlyList<MoreItem> items, float line)
    {
        float rowH = ItemInsetY * 2f + MathF.Max(line, IconSize), y = Pad;
        var rows = new PanelRect[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            float h = items[i].IsRule ? RuleGap * 2f + 1f : rowH;
            rows[i] = new PanelRect(Pad, y, Width - Pad * 2f, h);
            y += h;
        }
        return new MoreFrame(Width, y + Pad, items, rows);
    }

    // 菜单右缘对齐按钮右缘外 6（同外观面板 / 演示 .pop），不出窗口左右 8。
    public static float PlaceX(float buttonRight, float viewW) =>
        Math.Clamp(buttonRight + 6f - Width, 8f, MathF.Max(8f, viewW - 8f - Width));

    // 命中的条目下标；分隔线、灰显项与空白返回 -1。
    public static int Hit(MoreFrame f, float x, float y)
    {
        if (f == null) return -1;
        for (int i = 0; i < f.Rows.Count; i++)
            if (f.Rows[i].Contains(x, y)) return f.Items[i].IsRule || !f.Items[i].Enabled ? -1 : i;
        return -1;
    }

    // 键盘移动：跳过分隔线与灰显项，循环；current 为 -1 时 ↓ 落第一项、↑ 落最后一项。
    public static int Step(MoreFrame f, int current, int direction)
    {
        if (f == null || f.Items.Count == 0 || direction == 0) return current;
        int n = f.Items.Count, i = current < 0 ? (direction > 0 ? -1 : n) : current;
        for (int k = 0; k < n; k++)
        {
            i = ((i + direction) % n + n) % n;
            if (!f.Items[i].IsRule && f.Items[i].Enabled) return i;
        }
        return current;
    }
}
