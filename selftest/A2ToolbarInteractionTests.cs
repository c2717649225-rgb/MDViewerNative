using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

internal static class A2ToolbarInteractionTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "semantic identity, shared layout and bounded UI state") => check("A2Interaction/" + name, ok, detail);
        var a = new ToolbarTab(Guid.NewGuid(), "Alpha.md", "C:\\one\\Alpha.md", false, true);
        var b = new ToolbarTab(Guid.NewGuid(), "Bravo.md", "C:\\two\\Bravo.md", true, false);
        var c = new ToolbarTab(Guid.NewGuid(), "Charlie.md", "C:\\three\\Charlie.md", false, false);
        var state = new CmdState(false, false, false, false, false, 16f);
        using var bar = new ToolbarView();
        var tabs = new List<ToolbarTab> { a, b, c };
        bar.Prepare(1100, state, tabs);
        int layoutPasses = bar.LayoutPassCount;
        bar.Prepare(1100, state, tabs);
        bar.MovePointer(20, 18);
        bar.Prepare(1100, state, tabs);
        C("pointer-only state reuses geometry and text measurements", bar.LayoutPassCount == layoutPasses);
        bar.LeavePointer();
        var closeA = ToolbarTarget.ForTab(a.Id, true);
        C("visible tab and close exposed", bar.FocusTargets.Contains(ToolbarTarget.ForTab(a.Id)) && bar.FocusTargets.Contains(closeA));
        C("disabled save excluded from keyboard sequence", !bar.FocusTargets.Contains(ToolbarTarget.ForCommand(ToolbarCommand.Save)));
        C("each focus target has a usable rectangle", bar.FocusTargets.All(t => { var r = bar.RectOf(t); return r.x >= 0 && r.w >= 24 && r.x + r.w <= 1100.01f; }));
        var seen = new HashSet<ToolbarTarget>();
        bar.ToggleFocus();
        var first = bar.FocusedTarget;
        for (int i = 0; i < bar.FocusCount; i++) { seen.Add(bar.FocusedTarget); bar.StepFocus(9, false); }
        C("Tab visits all targets exactly once and wraps", seen.SetEquals(bar.FocusTargets) && bar.FocusedTarget == first);
        bar.StepFocus(9, true);
        C("Shift Tab reverses to last target", bar.FocusedTarget == bar.FocusTargets[^1]);
        bar.FocusEdge(false);
        C("Home focuses the first target (review #4)", bar.FocusedTarget == bar.FocusTargets[0]);
        bar.FocusEdge(true);
        C("End focuses the last target (review #4)", bar.FocusedTarget == bar.FocusTargets[^1]);
        C("focused toolbar owns only its navigation keys (review #4)",
            ToolbarView.OwnsKey(0x24) && ToolbarView.OwnsKey(0x23) && ToolbarView.OwnsKey(0x1B) && ToolbarView.OwnsKey(9)
            && !ToolbarView.OwnsKey(0x21) && !ToolbarView.OwnsKey(0x22) && !ToolbarView.OwnsKey(0x26) && !ToolbarView.OwnsKey(0x28) && !ToolbarView.OwnsKey(0x2E),
            "PgUp/PgDn/Up/Down keep reading-view scrolling; Delete does not close a tab");
        bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(closeA);
        C("close focus description states unsaved identity", bar.FocusDescription.Contains("关闭未保存文档") && bar.FocusDescription.Contains(a.Title));

        var close = bar.RectOf(closeA);
        C("negative/outside coordinates cannot hit", bar.HitTarget(close.x + 4, -1).Part == ToolbarPart.None && bar.HitButton(20, -1) == -1 && bar.HitTarget(1100, 18).Part == ToolbarPart.None);
        C("pointer down tracks but does not invoke", bar.BeginPointer(close.x + 4, 18) && bar.HasPress);
        C("release outside cancels", bar.EndPointer(close.x + 4, -1, out var none) && none.Part == ToolbarPart.None && !bar.HasPress);
        bar.MovePointer(close.x + 4, 18);   // a real mouse hovers (revealing the close) before pressing
        bar.BeginPointer(close.x + 4, 18);
        C("release inside emits exact document close", bar.EndPointer(close.x + 4, 18, out var action) && action == closeA);
        C("one release emits at most one action", !bar.EndPointer(close.x + 4, 18, out _));
        bar.BeginPointer(close.x + 4, 18);
        C("lost capture cancels pending UI action", bar.CancelPointer() && !bar.EndPointer(close.x + 4, 18, out _));

        bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(closeA);
        bar.BeginPointer(close.x + 4, 18);
        var reordered = new List<ToolbarTab> { c, b, a };
        bar.Prepare(1100, state, reordered);
        C("reordering cancels a moved pressed rectangle", !bar.HasPress);
        bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(closeA);
        bar.Prepare(1100, state, tabs);
        C("keyboard focus follows identity not old screen index", bar.FocusedTarget == closeA && bar.RectOf(closeA).x == close.x);
        var bodyA = bar.RectOf(ToolbarTarget.ForTab(a.Id));
        var switched = new List<ToolbarTab> { a with { Active = true }, b with { Active = false }, c };
        bar.Prepare(1100, state, switched);
        C("activating an already visible tab preserves geometry", bar.RectOf(ToolbarTarget.ForTab(a.Id)) == bodyA);

        bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(closeA);
        // A2-B4: the active tab keeps its measured minimum, so squeeze out inactive `a` instead
        // of relying on the old zero-tab degenerate state at 400 DIP; focus follows the designed
        // fallback order (visible active tab, then document list).
        bar.Prepare(400, state, tabs);
        C("hidden tab focus falls back to visible active tab", bar.FocusedTarget == ToolbarTarget.ForTab(b.Id)
            && !bar.FocusTargets.Contains(closeA) && !bar.FocusTargets.Contains(ToolbarTarget.ForTab(a.Id)));
        C("hidden tab has no stale rectangle", bar.RectOf(closeA).w == 0);
        bar.Prepare(1100, state, tabs);
        bar.FocusIndex = bar.FocusTargets.ToList().IndexOf(closeA);
        bar.Prepare(1100, state, new List<ToolbarTab> { b, c });
        C("removed target falls back to live active tab", bar.FocusedTarget == ToolbarTarget.ForTab(b.Id));

        bar.Prepare(1100, state, tabs);
        close = bar.RectOf(closeA);
        bar.BeginPointer(close.x + 4, 18);
        bar.Prepare(1100, state with { Composing = true }, tabs);
        C("composition disables actions and clears pending press/focus", bar.FocusCount == 0 && !bar.HasPress && !bar.HasFocus && !bar.IsEnabled(closeA));
        bar.Prepare(1100, state with { ReadOnly = true }, tabs);
        C("readonly does not remove safe tab-close access", bar.IsEnabled(closeA) && !bar.FocusTargets.Contains(ToolbarTarget.ForCommand(ToolbarCommand.ToggleEdit)));
        C("unknown identity never resolves to another tab", bar.RectOf(ToolbarTarget.ForTab(Guid.NewGuid(), true)).w == 0);
        bar.ClearFocus();
        C("explicit exit clears virtual focus", !bar.HasFocus && bar.FocusIndex == -1);
        bar.AlwaysShowClose = true;
        C("conservative no-hover mode is independently selectable", bar.AlwaysShowClose);
        C("ordinary mouse leaves initial digitizer fallback", bar.ObservePointerSource(0) && !bar.AlwaysShowClose);
        C("promoted touch preserves discoverable close", bar.ObservePointerSource(unchecked((nint)0xFF515780u)) && bar.AlwaysShowClose);
        C("promoted pen conservatively retains close", !bar.ObservePointerSource(unchecked((nint)0xFF515701u)) && bar.AlwaysShowClose);
        C("returning to mouse restores hover mode", bar.ObservePointerSource(0) && !bar.AlwaysShowClose);

        // Review #2 (hybrid device, first touch): Program judges the press before observing the new
        // source, so the press sees the mouse-mode presentation where a clean inactive close is hidden.
        bar.Prepare(1100, state, tabs);
        bar.ClearFocus(); bar.LeavePointer();
        var closeC = ToolbarTarget.ForTab(c.Id, true);
        var slot = bar.RectOf(closeC);
        float tx = slot.x + slot.w / 2f, ty = 18f;
        nint touch = unchecked((nint)0xFF515780u);
        // Real tap order: promoted WM_MOUSEMOVE (touch signature) -> WM_LBUTTONDOWN -> observe(press).
        bar.ObservePointerSource(touch); bar.MovePointer(tx, ty);
        bool began = bar.BeginPointer(tx, ty);
        bar.ObservePointerSource(touch, press: true);
        bool firstEnded = bar.EndPointer(tx, ty, out var firstTap);
        C("first touch on a hidden clean close activates the tab instead of closing it",
            began && firstEnded && firstTap == ToolbarTarget.ForTab(c.Id), firstTap.Part.ToString());
        bar.ObservePointerSource(touch); bar.MovePointer(tx, ty);
        bool again = bar.BeginPointer(tx, ty);
        bar.ObservePointerSource(touch, press: true);
        bool secondEnded = bar.EndPointer(tx, ty, out var secondTap);
        C("once the close is visible the next tap closes exactly that document",
            again && secondEnded && secondTap == closeC, secondTap.Part.ToString());
        bar.ObservePointerSource(0); bar.LeavePointer();
        bar.BeginPointer(tx, ty);
        C("a press straight onto a hidden close without hover does not close", bar.EndPointer(tx, ty, out var blind) && blind == ToolbarTarget.ForTab(c.Id), blind.Part.ToString());
        bar.ObservePointerSource(0); bar.LeavePointer();
        bar.MovePointer(tx, ty);
        bar.BeginPointer(tx, ty);
        C("mouse hover then click still closes (the close was on screen)", bar.EndPointer(tx, ty, out var mouseClick) && mouseClick == closeC, mouseClick.Part.ToString());
        bar.LeavePointer();
        bar.BeginPointer(tx, ty);
        C("a promoted press released off the tab emits nothing", bar.EndPointer(tx, -1, out var offTab) && offTab.Part == ToolbarPart.None, offTab.Part.ToString());
        bar.LeavePointer();
        bar.BeginPointer(tx, ty);
        C("a promoted press shows the tab pressed, not the hidden close", bar.HasPress && bar.RectOf(ToolbarTarget.ForTab(c.Id)).w > 0);
        bar.CancelPointer();
        DoubleClickRouting(C);
    }

    // CS_DBLCLKS turns the second rapid press into WM_LBUTTONDBLCLK. The toolbar must treat it as
    // an ordinary press before any body/editor routing, otherwise the second click is silently lost.
    // Source contract only; the native message path still needs a host run with a free desktop.
    private static void DoubleClickRouting(Action<string, bool, string> c)
    {
        // E17 起分支体搬进 Program.*.cs 的处理方法：先取 case 段，若只是分派就换成被调方法的方法体。
        string src = string.Concat(System.IO.Directory.GetFiles("../engine", "Program*.cs").Order().Select(System.IO.File.ReadAllText)).Replace("\r\n", "\n");
        static string Case(string s, string label)
        {
            int start = s.IndexOf(label, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = s.IndexOf("\n            case ", start + label.Length, StringComparison.Ordinal);
            string segment = end < 0 ? s[start..] : s[start..end];
            var call = System.Text.RegularExpressions.Regex.Match(segment, @"^[^\n]*\n\s*return (On\w+)\(h, m, w, l\);\s*$");
            if (!call.Success) return segment;
            int method = s.IndexOf("static IntPtr " + call.Groups[1].Value + "(", StringComparison.Ordinal);
            if (method < 0) return "";
            int close = s.IndexOf("\n    }\n", method, StringComparison.Ordinal);
            return close < 0 ? "" : s[method..close];
        }
        string down = Case(src, "case 0x0201:"), dbl = Case(src, "case 0x0203:");
        int shared = dbl.IndexOf("ToolbarPointerDown(", StringComparison.Ordinal);
        int editing = dbl.IndexOf("_editing", StringComparison.Ordinal);
        int body = dbl.IndexOf("OverBody(", StringComparison.Ordinal);
        c("double click routes toolbar press through the shared handler", shared >= 0 && down.Contains("ToolbarPointerDown(", StringComparison.Ordinal),
            "WM_LBUTTONDOWN and WM_LBUTTONDBLCLK share one toolbar press path");
        c("toolbar double click is handled before editor/body routing", shared >= 0 && (editing < 0 || shared < editing) && (body < 0 || shared < body),
            "editing mode must not hand the toolbar's second click to DefWindowProc");
    }
}
