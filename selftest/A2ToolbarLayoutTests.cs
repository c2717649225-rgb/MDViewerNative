using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// A2-B4: measured command folding and overflow. Offscreen layout only; the real
// window matrix is a separate desktop batch and is not claimed here.
internal static class A2ToolbarLayoutTests
{
    static readonly float[] Widths = { 240f, 320f, 360f, 400f, 468f, 500f, 640f, 960f, 1280f };
    static readonly ToolbarCommand[] FoldOrder = { ToolbarCommand.ToggleToc, ToolbarCommand.ToggleFind, ToolbarCommand.Save };

    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail) => check("A2Layout/" + name, ok, detail);
        var clean = new CmdState(false, false, false, false, false, 16f);
        var dirty = new CmdState(false, true, false, false, false, 16f);
        var fails = new Dictionary<string, List<string>>();
        void Note(string rule, bool ok, string where)
        {
            if (!fails.TryGetValue(rule, out var list)) fails[rule] = list = new List<string>();
            if (!ok) list.Add(where);
        }

        float stripEnd;
        using (var wide = new ToolbarView())
        {
            wide.Prepare(1280f, clean, null);
            var b = wide.BtnRects();
            stripEnd = b[5].x + b[5].w + 12f;
        }

        foreach (int n in new[] { 1, 2, 20 })
        {
            foreach (float w in Widths)
            {
                foreach (bool isDirty in new[] { false, true })
                {
                    var tabs = MakeTabs(n, isDirty);
                    using var bar = new ToolbarView();
                    bar.Prepare(w, isDirty ? dirty : clean, n > 1 ? tabs : null);
                    string at = "n=" + n + " w=" + w + " dirty=" + isDirty;
                    var btn = bar.BtnRects();
                    var rects = new List<(float x, float wd)>();
                    for (int i = 0; i < 6; i++) if (btn[i].w > 0) rects.Add((btn[i].x, btn[i].w));
                    if (bar.TabRects != null) foreach (var r in bar.TabRects) if (r.w > 0) rects.Add((r.x, r.w));
                    if (bar.OverflowRect.w > 0) rects.Add((bar.OverflowRect.x, bar.OverflowRect.w));
                    bool disjoint = true;
                    for (int i = 0; i < rects.Count; i++)
                        for (int j = i + 1; j < rects.Count; j++)
                            if (rects[i].x < rects[j].x + rects[j].wd - 0.01f && rects[j].x < rects[i].x + rects[i].wd - 0.01f) disjoint = false;
                    Note("visible targets stay inside the toolbar", rects.All(r => r.x >= 0f && r.x + r.wd <= w + 0.01f), at);
                    Note("visible targets never intersect", disjoint, at);

                    var folded = bar.FoldedCommands;
                    Note("folding follows TOC, Find, Save order", folded.Count <= FoldOrder.Length && folded.SequenceEqual(FoldOrder.Take(folded.Count)), at);
                    bool accounted = Enumerable.Range(0, 6).All(i =>
                    {
                        var cmd = (ToolbarCommand)i;
                        var r = bar.RectOf(ToolbarTarget.ForCommand(cmd));
                        bool visible = r.w > 0 && r.x >= 0 && r.x + r.w <= w + 0.01f;
                        return folded.Contains(cmd) ? r.w == 0 && bar.HitTarget(btn[i].x + 1f, 18f) != ToolbarTarget.ForCommand(cmd) : visible;
                    });
                    Note("every command is fully visible or folded without a ghost hit", accounted, at);
                    Note("open, mode and More are never folded", new[] { ToolbarCommand.Open, ToolbarCommand.ToggleEdit, ToolbarCommand.More }
                        .All(c => bar.RectOf(ToolbarTarget.ForCommand(c)).w > 0 && !folded.Contains(c)), at);
                    Note("folded commands leave the keyboard sequence", folded.All(c => !bar.FocusTargets.Contains(ToolbarTarget.ForCommand(c))), at);
                    if (w >= 468f) Note("reachable window widths never fold", folded.Count == 0, at);

                    if (n > 1)
                    {
                        int active = tabs.FindIndex(t => t.Active);
                        var activeRect = bar.RectOf(ToolbarTarget.ForTab(tabs[active].Id));
                        bool listVisible = bar.OverflowRect.w > 0 && bar.OverflowRect.x + bar.OverflowRect.w <= w + 0.01f;
                        Note("active document or document list stays reachable", activeRect.w > 0 || listVisible, at);
                        if (w >= 360f) Note("active tab keeps the measured minimum width", bar.TabRects[active].w >= ToolbarView.TabMinWidth, at);
                        if (w >= 468f)
                        {
                            // Legacy geometry at reachable window widths is a hard constraint.
                            float available = MathF.Max(0f, w - stripEnd - 8f);
                            int capacity = (int)((available + 4f) / 124f);
                            bool overflow = capacity < n;
                            if (overflow) capacity = available >= 108f ? Math.Max(1, (int)((available - 36f + 4f) / 124f)) : 0;
                            int count = Math.Min(n, capacity);
                            float tabWidth = count > 0 ? MathF.Min(180f, (available - (overflow ? 36f : 0f)) / count - 4f) : 0f;
                            var visible = bar.TabRects.Where(r => r.w > 0).ToList();
                            bool same = visible.Count == count && visible.All(r => MathF.Abs(r.w - tabWidth) < 0.01f)
                                && (count == 0 || MathF.Abs(visible[0].x - stripEnd) < 0.01f)
                                && (overflow ? bar.OverflowRect.w == 32f : bar.OverflowRect.w == 0f);
                            Note("reachable widths keep legacy tab geometry", same, at);
                        }
                        if (isDirty)
                        {
                            bool hidden = bar.TabRects[0].w <= 0;
                            Note("hidden dirty tab marks the document list entry", !hidden || bar.OverflowDirty, at);
                            Note("visible dirty tabs do not mark the list", hidden || !bar.OverflowDirty, at);
                        }
                    }
                    else if (isDirty)
                    {
                        var expected = ToolbarTarget.ForCommand(folded.Contains(ToolbarCommand.Save) ? ToolbarCommand.More : ToolbarCommand.Save);
                        Note("unsaved dot moves to More only when Save is folded", bar.DirtyMarkerTarget == expected, at);
                    }
                }
            }
        }
        foreach (var (rule, where) in fails)
            C(rule, where.Count == 0, where.Count == 0 ? "9 widths x 1/2/20 tabs x clean/dirty" : string.Join("; ", where.Take(6)) + (where.Count > 6 ? " ..." : ""));

        using (var bar = new ToolbarView())
        {
            var tabs = MakeTabs(2, false);
            var toc = ToolbarTarget.ForCommand(ToolbarCommand.ToggleToc);
            bar.Prepare(1280f, clean, tabs);
            bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(toc);
            bool had = bar.FocusedTarget == toc;
            bar.Prepare(300f, clean, tabs);
            C("focus on a folded command falls back to a live target", had && bar.HasFocus && bar.FocusedTarget != toc && bar.FocusTargets.Contains(bar.FocusedTarget),
                "B2 fallback rule applies to folding");
        }
        C("More projection adds folded Save and TOC once in toolbar order", ToolbarView.MoreProjection(FoldOrder).SequenceEqual(new[] { 1001, 1006 }),
            "Find is already on the static More face");
        C("More projection never duplicates static More commands", !ToolbarView.MoreProjection(new[] { ToolbarCommand.ToggleFind }).Any(), "dedupe by command id");
        C("no folding keeps the static More menu unchanged", ToolbarView.MoreProjection(Array.Empty<ToolbarCommand>()).Length == 0, "menu_golden stays valid");
    }

    static List<ToolbarTab> MakeTabs(int n, bool dirtyFirst)
    {
        var list = new List<ToolbarTab>();
        for (int i = 0; i < n; i++)
            list.Add(new ToolbarTab(Guid.NewGuid(), "文档" + i + ".md", "C:\\docs\\文档" + i + ".md", i == n / 2, dirtyFirst && i == 0));
        return list;
    }
}
