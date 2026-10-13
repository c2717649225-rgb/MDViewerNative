using System;
using System.Linq;
using MDViewerNative;

// U11b：外观面板（纯逻辑：版面、落点、命中、键盘步进、状态）。
internal static class DeskAppearanceTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk appearance") => check("DeskAppearance/" + name, ok, detail);

        var m = new AppearanceMeasure(16f, 18f, 16f, 38f, 17f, 25f, 25f, 17f, 17f);
        var f = AppearancePanel.Layout(m);
        C("width and five options", f.W == 330f && f.Options.Count == 5 && AppearancePanel.Names.Length == 5 && AppearancePanel.Notes.Length == 5);
        C("options stack without gaps", Enumerable.Range(1, 4).All(i => f.Options[i].Y == f.Options[i - 1].Y + f.Options[i - 1].H) && f.Options[0].H == 8f * 2f + 35f);
        C("rows follow the note", f.FontRuleY > f.Note.Y + f.Note.H && f.SizeRuleY > f.Segment.Y + f.Segment.H && f.H > f.Plus.Y + f.Plus.H);
        C("segment and stepper sit on the right edge", MathF.Abs(f.Segment.X + f.Segment.W - (330f - 16f - 6f)) < 0.01f && MathF.Abs(f.Plus.X + f.Plus.W - (330f - 16f - 6f)) < 0.01f && f.Minus.X < f.Value.X && f.Value.X < f.Plus.X);
        C("place under the button, clamped", AppearancePanel.PlaceX(760f, 960f) == 760f + 6f - 330f && AppearancePanel.PlaceX(100f, 960f) == 8f && AppearancePanel.PlaceX(1000f, 960f) == 960f - 8f - 330f);

        var o2 = f.Options[2];
        C("hit option, segment, stepper", AppearancePanel.Hit(f, o2.X + 10f, o2.Y + 10f) == new AppearanceTarget(AppearanceTargetKind.Paper, 2)
            && AppearancePanel.Hit(f, f.Serif.X + 2f, f.Serif.Y + 2f) == new AppearanceTarget(AppearanceTargetKind.Font, 1)
            && AppearancePanel.Hit(f, f.Plus.X + 2f, f.Plus.Y + 2f).Kind == AppearanceTargetKind.ZoomIn
            && AppearancePanel.Hit(f, f.Minus.X + 2f, f.Minus.Y + 2f).Kind == AppearanceTargetKind.ZoomOut);
        C("hit misses the note and outside", AppearancePanel.Hit(f, f.Note.X + 5f, f.Note.Y + 5f).Kind == AppearanceTargetKind.None && AppearancePanel.Hit(f, -1f, 10f).Kind == AppearanceTargetKind.None && AppearancePanel.Hit(null, 1f, 1f).Kind == AppearanceTargetKind.None);

        var s = new AppearanceState(Paper.Xuan, false, true, 100, true, true);
        C("step starts at the current paper", AppearancePanel.Step(AppearanceTarget.None, 1, s) == new AppearanceTarget(AppearanceTargetKind.Paper, 1));
        C("step wraps both ways", AppearancePanel.Step(new(AppearanceTargetKind.ZoomIn), 1, s) == new AppearanceTarget(AppearanceTargetKind.Paper, 0) && AppearancePanel.Step(new(AppearanceTargetKind.Paper, 0), -1, s).Kind == AppearanceTargetKind.ZoomIn);
        var limited = s with { SerifAvailable = false, CanZoomIn = false };
        C("step skips what is unavailable", AppearancePanel.Step(new(AppearanceTargetKind.Font, 0), 1, limited).Kind == AppearanceTargetKind.ZoomOut
            && AppearancePanel.Step(new(AppearanceTargetKind.ZoomOut), 1, limited) == new AppearanceTarget(AppearanceTargetKind.Paper, 0)
            && !AppearancePanel.Enabled(new(AppearanceTargetKind.Font, 1), limited) && AppearancePanel.Enabled(new(AppearanceTargetKind.Font, 0), limited));
        C("on state", AppearancePanel.IsOn(new(AppearanceTargetKind.Paper, 1), s) && !AppearancePanel.IsOn(new(AppearanceTargetKind.Paper, 2), s)
            && AppearancePanel.IsOn(new(AppearanceTargetKind.Font, 0), s) && AppearancePanel.IsOn(new(AppearanceTargetKind.Font, 1), s with { Serif = true }) && !AppearancePanel.IsOn(new(AppearanceTargetKind.ZoomIn), s));
        C("spread grows from nothing to past the far corner", DeskAnimations.SpreadRadius(0f, 1000f, 600f) < 1f && DeskAnimations.SpreadRadius(0.5f, 1000f, 600f) > DeskAnimations.SpreadRadius(0.25f, 1000f, 600f)
            && DeskAnimations.SpreadRadius(0.999f, 1000f, 600f) > MathF.Sqrt(1000f * 1000f + 600f * 600f) && DeskAnimations.SpreadRadius(1f, 1000f, 600f) == float.MaxValue && DeskAnimations.SpreadMs == 900);
        C("zoom percent", AppearancePanel.ZoomPercent(17f, 17f) == 100 && AppearancePanel.ZoomPercent(18.7f, 17f) == 110 && AppearancePanel.ZoomPercent(17f, 0f) == 100);

        // 试用修复：desk「更多」菜单（演示 #morePop）
        MoreCommand? All(int id) => new MoreCommand(id, "x", id == MoreMenu.Palette ? "Ctrl+K" : "", id != MoreMenu.Reopen);
        var items = MoreMenu.Items(Array.Empty<MoreCommand>(), All);
        C("more: design order with two rules", string.Join(",", items.Select(i => i.Id)) == "1003,1012,2205,0,1011,2232,0,2291", string.Join(",", items.Select(i => i.Id)));
        C("more: labels from the design, accel from the command", items[0].Label == "打开文件…" && items[^1].Label == "命令面板" && items[^1].Accel == "Ctrl+K" && items[0].Icon == MoreIcon.Open);
        var withFold = MoreMenu.Items(new[] { new MoreCommand(1005, "查找", "Ctrl+F", true) }, All);
        C("more: folded buttons first, then a rule", withFold[0].Id == 1005 && withFold[1].IsRule && withFold[2].Id == MoreMenu.Open && withFold[0].Icon == MoreIcon.None);
        var sparse = MoreMenu.Items(Array.Empty<MoreCommand>(), id => id == MoreMenu.Palette ? All(id) : null);
        C("more: missing commands drop, no dangling rules", sparse.Count == 1 && sparse[0].Id == MoreMenu.Palette);
        var mf = MoreMenu.Layout(items, 17f);
        C("more: width 250, padding 6, rows 8+15+8, rules 11", mf.W == 250f && mf.Rows[0].Y == 6f && mf.Rows[0].H == 33f && mf.Rows[3].H == 11f
            && mf.H == 6f + 6f * 33f + 2f * 11f + 6f && mf.Rows[0].X == 6f && mf.Rows[0].W == 238f, $"{mf.H}");
        C("more: hit skips rules and disabled items", MoreMenu.Hit(mf, 20f, mf.Rows[0].Y + 5f) == 0 && MoreMenu.Hit(mf, 20f, mf.Rows[1].Y + 5f) == 1
            && MoreMenu.Hit(mf, 20f, mf.Rows[2].Y + 5f) == -1 && MoreMenu.Hit(mf, 20f, mf.Rows[3].Y + 5f) == -1 && MoreMenu.Hit(mf, 2f, mf.Rows[0].Y + 5f) == -1);
        C("more: keyboard steps skip rules and disabled, wrapping", MoreMenu.Step(mf, -1, 1) == 0 && MoreMenu.Step(mf, 0, 1) == 1 && MoreMenu.Step(mf, 1, 1) == 4
            && MoreMenu.Step(mf, 7, 1) == 0 && MoreMenu.Step(mf, -1, -1) == 7 && MoreMenu.Step(mf, 4, -1) == 1);
        C("more: save as sits under open", items[1].Id == MoreMenu.SaveAs && items[1].Label == "另存为…" && items[1].Icon == MoreIcon.SaveAs);

        // 试用反馈：desk 自绘选择框（保存 / 不保存 / 取消）
        var cw = ChoiceDialog.Width(new[] { 26f, 39f, 26f });
        var cf = ChoiceDialog.Layout(cw, 21f, 18f, new[] { 26f, 39f, 26f });
        C("choice: at least 380 wide, buttons at least 84", cw == 380f && cf.Buttons[0].W == 84f && cf.Buttons[1].W == 84f);
        C("choice: buttons right-aligned, 8 apart, primary first", cf.Buttons[2].X + cf.Buttons[2].W == 380f - 24f && cf.Buttons[1].X - (cf.Buttons[0].X + cf.Buttons[0].W) == 8f);
        C("choice: heading, body, buttons stack", cf.Heading.Y == 22f && cf.Body.Y == 22f + 21f + 8f && cf.Buttons[0].Y == cf.Body.Y + 18f + 22f && cf.H == cf.Buttons[0].Y + 32f + 20f);
        C("choice: wide button rows grow the card up to 520", ChoiceDialog.Width(new[] { 200f, 200f, 200f }) == 520f && ChoiceDialog.Width(new[] { 120f, 120f }) == 380f);
        C("choice: ids match the task dialog", ChoiceDialog.IdOf(0, 3) == 1000 && ChoiceDialog.IdOf(1, 3) == 1001 && ChoiceDialog.IdOf(2, 3) == 2 && ChoiceDialog.IdOf(2, 4) == 1002);
        C("choice: no body keeps the gap tight", ChoiceDialog.Layout(380f, 21f, 0f, new[] { 26f, 26f }).Buttons[0].Y == 22f + 21f + 22f);
        C("more: right edge 6 past the button, inside the window", MoreMenu.PlaceX(1000f, 1200f) == 756f && MoreMenu.PlaceX(100f, 1200f) == 8f && MoreMenu.PlaceX(1200f, 1200f) == 942f);
    }
}
