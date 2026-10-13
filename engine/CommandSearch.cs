using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U11：命令面板的纯逻辑（演示 .pal / renderPal）。条目来自命令表，外加宿主给的少量条目（纸张、起始页）与最近文件；
// 按名称子串或快捷键匹配（不区分大小写，不引入拼音表）；分组「命令 / 最近文件」；版面、命中与滚动都在这里，单位 DIP。
internal enum PaletteKind { Command, Recent }

internal sealed record PaletteEntry(PaletteKind Kind, int Id, string Label, string Accel = "", string Sub = null, bool Enabled = true, string Path = null);

// Entry < 0 表示分组标题行。
internal readonly record struct PaletteRow(int Entry, float Y, float H, string Header = null);

internal sealed record PaletteFrame(float X, float Y, float W, float InputH, float ListY, float ListH, float ContentH, IReadOnlyList<PaletteRow> Rows, bool Empty)
{
    public float H => InputH + ListH;
}

internal readonly record struct PaletteMeasure(float InputLine, float RowLine, float GroupLine, float EmptyLine);

internal static class CommandSearch
{
    public const float MaxWidth = 580f, WidthShare = 0.92f, TopShare = 0.13f, ListShare = 0.5f, Radius = 14f;
    public const float InputInsetX = 18f, InputPadY = 15f, InputGap = 12f, Icon = 18f, InputSize = 15.5f;
    public const float ListPad = 8f, RowInsetX = 12f, RowPadY = 9f, RowGap = 12f, RowRadius = 9f, RowSize = 14f, SubSize = 12f;
    public const float GroupTop = 10f, GroupBottom = 6f, GroupSize = 11.5f, GroupSpacing = 0.2f, EmptyPad = 26f, EmptySize = 13f;
    public const float KeySize = 14f * 0.76f;
    public const int RecentIdle = 3, RecentMatched = 6;
    public const string Placeholder = "输入命令或文件名", CommandsHeader = "命令", RecentHeader = "最近文件";

    // 只在选区、光标或右键那一处才有意义的命令（复制、剪切、全选、撤销…），以及面板自己，不进面板。
    static readonly HashSet<int> HereAndNow = new() { 2001, 2081, 2094, 2002, 2080, 2003, 2270, 2004, 2005, 2006, 2007, 2008, PaletteCommand };
    public const int PaletteCommand = 2291, HomeCommand = 2290;

    public static bool Listed(CmdDef c) => !HereAndNow.Contains(c.Id);

    public static List<PaletteEntry> Catalog(IEnumerable<CmdDef> commands, CmdState state, Func<CmdDef, string> label, IEnumerable<PaletteEntry> extras)
    {
        var list = new List<PaletteEntry>();
        foreach (var c in commands)
        {
            if (!Listed(c)) continue;
            bool enabled = c.IsEnabled(state);
            string sub = enabled ? null : Commands.DisabledReason(c.Id, state) ?? "当前不可用";
            list.Add(new PaletteEntry(PaletteKind.Command, c.Id, label?.Invoke(c) ?? c.Label, c.Accel, sub, enabled));
        }
        if (extras != null) list.AddRange(extras);
        return list;
    }

    public static bool Matches(PaletteEntry e, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || e.Kind == PaletteKind.Command && !string.IsNullOrEmpty(e.Accel) && LooksLikeKey(query) && Compact(e.Accel).Contains(Compact(query), StringComparison.OrdinalIgnoreCase);
    }

    // 只有像快捷键的输入才比快捷键（带 + 或以 Ctrl / Alt / Shift / F 数字开头），免得敲一个字母就把一串 Ctrl+X 都拉出来。
    public static bool LooksLikeKey(string query)
    {
        string q = (query ?? "").Trim();
        if (q.Contains('+')) return true;
        foreach (string m in new[] { "ctrl", "alt", "shift" }) if (q.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return true;
        return q.Length >= 2 && (q[0] is 'f' or 'F') && char.IsAsciiDigit(q[1]);
    }

    // 快捷键按去掉空格与加号比较：“ctrl k”“Ctrl+K”“ctrlk”都算。
    static string Compact(string s) => s.Replace(" ", "").Replace("+", "");

    public static List<PaletteEntry> Filter(IReadOnlyList<PaletteEntry> commands, IReadOnlyList<PaletteEntry> recent, string query)
    {
        string q = (query ?? "").Trim();
        var list = new List<PaletteEntry>();
        foreach (var c in commands) if (Matches(c, q)) list.Add(c);
        int cap = q.Length > 0 ? RecentMatched : RecentIdle, n = 0;
        if (recent != null)
            foreach (var r in recent)
            {
                if (n >= cap) break;
                if (!Matches(r, q)) continue;
                list.Add(r); n++;
            }
        return list;
    }

    // 名称里第一处命中（朱色加粗）；快捷键命中或没有命中时返回 (-1, 0)。
    public static (int Start, int Length) Highlight(string label, string query)
    {
        string q = (query ?? "").Trim();
        if (q.Length == 0 || string.IsNullOrEmpty(label)) return (-1, 0);
        int i = label.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? (-1, 0) : (i, q.Length);
    }

    public static int Step(int selected, int count, int direction) =>
        count <= 0 ? 0 : ((selected + direction) % count + count) % count;

    public static PaletteFrame Layout(float viewW, float viewH, IReadOnlyList<PaletteEntry> items, PaletteMeasure m)
    {
        float w = MathF.Min(MaxWidth, viewW * WidthShare);
        float x = (viewW - w) / 2f, y = viewH * TopShare;
        float inputH = InputPadY * 2f + m.InputLine + 1f;   // 下边 1 的分隔线
        var rows = new List<PaletteRow>();
        float cy = ListPad;
        bool empty = items == null || items.Count == 0;
        if (empty) cy += EmptyPad * 2f + m.EmptyLine - ListPad * 2f;
        else
        {
            PaletteKind? group = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Kind != group)
                {
                    group = items[i].Kind;
                    float gh = GroupTop + m.GroupLine + GroupBottom;
                    rows.Add(new PaletteRow(-1, cy, gh, group == PaletteKind.Command ? CommandsHeader : RecentHeader));
                    cy += gh;
                }
                float rh = RowPadY * 2f + m.RowLine;
                rows.Add(new PaletteRow(i, cy, rh));
                cy += rh;
            }
        }
        float content = cy + ListPad;
        float listH = MathF.Min(content, MathF.Max(0f, viewH * ListShare));
        return new PaletteFrame(x, y, w, inputH, y + inputH, listH, content, rows, empty);
    }

    public static float ClampScroll(PaletteFrame f, float scroll) => Math.Clamp(scroll, 0f, MathF.Max(0f, f.ContentH - f.ListH));

    // 让选中行完整露出（演示 scrollIntoView block:nearest）；紧挨着的组标题一起露出，首行连上边距，末行连下边距。
    public static float Reveal(PaletteFrame f, int entry, float scroll)
    {
        for (int k = 0; k < f.Rows.Count; k++)
        {
            var r = f.Rows[k];
            if (r.Entry != entry) continue;
            float top = k > 0 && f.Rows[k - 1].Entry < 0 ? f.Rows[k - 1].Y : r.Y;
            if (k <= 1) top = 0f;
            float bottom = r.Y + r.H + (k == f.Rows.Count - 1 ? ListPad : 0f);
            if (top < scroll) scroll = top;
            else if (bottom > scroll + f.ListH) scroll = bottom - f.ListH;
            break;
        }
        return ClampScroll(f, scroll);
    }

    public static bool InPanel(PaletteFrame f, float x, float y) => f != null && x >= f.X && x < f.X + f.W && y >= f.Y && y < f.Y + f.H;

    // 条目行命中（组标题、空白、输入行都不算）。
    public static int Hit(PaletteFrame f, float x, float y, float scroll)
    {
        if (f == null || x < f.X || x >= f.X + f.W || y < f.ListY || y >= f.ListY + f.ListH) return -1;
        float ly = y - f.ListY + scroll;
        foreach (var r in f.Rows)
            if (r.Entry >= 0 && ly >= r.Y && ly < r.Y + r.H) return r.Entry;
        return -1;
    }
}
