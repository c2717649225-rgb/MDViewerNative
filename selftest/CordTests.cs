using System;
using System.Collections.Generic;
using MDViewerNative;

// U5a：书签绳几何与交互（规格 §4）。
internal static class CordTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "cord") => check("Cord/" + name, ok, detail);
        var f = new CordFrame(1000f, 36f, 800f);   // 正文视口：右缘 1000，上缘 36，高 800
        float span = CordGeometry.Span(f);
        C("span leaves 36 on top and 60 for the footer", span == 800f - 36f - 60f, span.ToString());
        var zero = CordGeometry.Cord(f, 0f); var full = CordGeometry.Cord(f, 1f);
        C("cord hangs 36 at the start", zero.Top == 36f && zero.Height == 36f && zero.Right == 995f && zero.Width == 11f);
        C("cord reaches the footer reserve at the end", full.Bottom == 36f + 800f - 60f && full.Width == 11f);
        C("size never depends on hover or drag", CordGeometry.Cord(f, 0.5f).Width == DeskMetrics.CordWidth);

        C("strip: whole 24 column", CordGeometry.InStrip(f, 976f, 100f) && CordGeometry.InStrip(f, 999.5f, 100f) && !CordGeometry.InStrip(f, 975.9f, 100f) && !CordGeometry.InStrip(f, 1000f, 100f));
        C("strip: not over the footer or toolbar", !CordGeometry.InStrip(f, 990f, 35f) && CordGeometry.InStrip(f, 990f, 36f + 800f - 34.1f) && !CordGeometry.InStrip(f, 990f, 36f + 800f - 34f));
        C("progress at a point", CordGeometry.ProgressAt(f, 36f + 36f) == 0f && CordGeometry.ProgressAt(f, 36f + 36f + span / 2f) == 0.5f && CordGeometry.ProgressAt(f, 5000f) == 1f && CordGeometry.ProgressAt(f, 0f) == 0f);
        float p = 0.3f, y = CordGeometry.Cord(f, p).Bottom;
        C("cord end maps back to its progress", MathF.Abs(CordGeometry.ProgressAt(f, y) - p) < 1e-5f);
        C("drag is relative and clamped", CordGeometry.DragOffset(1000f, span / 4f, f, 4000f) == 2000f && CordGeometry.DragOffset(100f, -span, f, 4000f) == 0f
            && CordGeometry.DragOffset(3900f, span, f, 4000f) == 4000f && CordGeometry.DragOffset(0f, 50f, f, 0f) == 0f);
        C("progress from offset", CordGeometry.Progress(500f, 1000f) == 0.5f && CordGeometry.Progress(10f, 0f) == 0f);

        var c = new CordInteraction();
        c.Down(300f, 1000f);
        C("small moves are still a click", c.Move(303.9f, f, 4000f).Action == CordAction.None && !c.Dragging);
        var drag = c.Move(300f + span / 4f, f, 4000f);
        C("moving 4 starts a drag", c.Dragging && drag.Action == CordAction.Scroll && drag.Offset == 2000f);
        C("drag keeps going below the slop", c.Move(301f, f, 4000f).Action == CordAction.Scroll);
        C("releasing a drag does not jump", c.Up(400f, f, 4000f, 1500f).Action == CordAction.None && !c.Pressed);

        c.Down(36f + 36f + span / 2f, 0f);
        var click = c.Up(36f + 36f + span / 2f + 2f, f, 4000f, 0f);
        C("click jumps to the point", click.Action == CordAction.Jump && MathF.Abs(click.Offset - (0.5f + 2f / span) * 4000f) < 0.01f, click.Offset.ToString("0.#"));
        c.Down(36f + 36f, 5f);
        C("click next to the current place does nothing", c.Up(36f + 36f, f, 4000f, 5f).Action == CordAction.None);
        c.Down(500f, 0f); c.Cancel();
        C("cancel forgets the press", !c.Pressed && c.Up(500f, f, 4000f, 0f).Action == CordAction.None && c.Move(600f, f, 4000f).Action == CordAction.None);

        // U5b：刻度
        var heads = new List<(int, float)> { (1, 60f), (2, 400f), (3, 700f), (2, 1200f), (4, 5000f) };
        var ticks = new List<CordTick>();
        CordGeometry.Ticks(ticks, heads, 2, f, 4000f);
        C("ticks: no tick for the title", ticks.Count == 4 && ticks[0].Entry == 1, ticks.Count.ToString());
        C("ticks: minor below level 2", !ticks[0].Minor && ticks[1].Minor && !ticks[2].Minor && ticks[3].Minor);
        C("ticks: read up to the current chapter", ticks[0].Read && ticks[1].Read && !ticks[2].Read && !ticks[3].Read);
        C("ticks: placed where the heading sits 70 below the top", MathF.Abs(ticks[0].Y - (36f + 36f + (400f - 70f) / 4000f * span)) < 1e-3f && ticks[3].Y == 36f + 36f + span);
        C("ticks: jump leaves the heading 60 below the top", ticks[0].Target == 340f && ticks[3].Target == 4000f);
        C("ticks: cord reaching a tick makes it the current chapter", FooterModel.CurrentChapter(heads, CordGeometry.ProgressAt(f, ticks[2].Y) * 4000f) == 3);
        var tr = CordGeometry.TickRect(f, ticks[0], false); var tm = CordGeometry.TickRect(f, ticks[1], false); var tn = CordGeometry.TickRect(f, ticks[0], true);
        C("ticks: share the cord axis, 13 / 7 wide, 1.5 high", tr.Left == 1000f - 4f - 13f && tr.Width == 13f && tm.Width == 7f && MathF.Abs(tm.Left - (1000f - 7f - 7f)) < 1e-4f && MathF.Abs(tr.Height - 1.5f) < 1e-3f);
        C("ticks: near grows 1.35 sideways only", MathF.Abs(tn.Width - 13f * 1.35f) < 1e-4f && MathF.Abs(tn.Height - 1.5f) < 1e-3f && MathF.Abs((tn.Left + tn.Right) - (tr.Left + tr.Right)) < 1e-4f);
        C("ticks: nearest within 7", CordGeometry.NearTick(ticks, ticks[1].Y + 6.9f) == 1 && CordGeometry.NearTick(ticks, ticks[1].Y + 7f) == -1 && CordGeometry.NearTick(new List<CordTick>(), 100f) == -1);
        c.Down(ticks[0].Y, 340f);
        var snap = c.Up(ticks[0].Y + 1f, f, 4000f, 340f, ticks[0].Target);
        C("ticks: click next to a tick jumps to the chapter", snap.Action == CordAction.Jump && snap.Offset == 340f);
        c.Down(300f, 0f); c.Move(300f + span / 4f, f, 4000f);
        C("ticks: dragging past a tick does not snap", c.Up(ticks[0].Y, f, 4000f, 1000f, ticks[0].Target).Action == CordAction.None);

        // U5c：返回书签与动画时钟
        var flag = CordGeometry.BackFlag(f, 0.5f);
        C("flag: 24x16, 12 from the right, centred on the origin", flag.Right == 988f && flag.Width == 24f && flag.Height == 16f && (flag.Top + flag.Bottom) / 2f == 36f + CordGeometry.Length(f, 0.5f));
        float fy = (flag.Top + flag.Bottom) / 2f;
        C("flag: clickable left of the cord, 24 tall", CordGeometry.InBackFlag(f, 0.5f, 964f, fy + 11.9f) && CordGeometry.InBackFlag(f, 0.5f, 983.9f, fy)
            && !CordGeometry.InBackFlag(f, 0.5f, 984f, fy) && !CordGeometry.InBackFlag(f, 0.5f, 963.9f, fy) && !CordGeometry.InBackFlag(f, 0.5f, 970f, fy + 12.1f));

        var anim = new DeskAnimations();
        anim.Start(DeskAnim.BackFlag, 600, 1000);
        C("anim: runs for its length", anim.Running(DeskAnim.BackFlag, 1000) && anim.Progress(DeskAnim.BackFlag, 1300) == 0.5f && anim.AnyRunning(1599) && !anim.AnyRunning(1600) && anim.Progress(DeskAnim.BackFlag, 1600) == 1f);
        anim.Stop(DeskAnim.BackFlag);
        C("anim: stop lands on the end", !anim.Running(DeskAnim.BackFlag, 1100) && anim.Progress(DeskAnim.BackFlag, 1100) == 1f);
        anim.Enabled = false; anim.Start(DeskAnim.BackFlag, 600, 1000);
        C("anim: system animations off jumps to the end", !anim.AnyRunning(1000) && anim.Progress(DeskAnim.BackFlag, 1000) == 1f);
        C("anim: ease endpoints and shape", DeskAnimations.Ease(0f) == 0f && MathF.Abs(DeskAnimations.Ease(1f) - 1f) < 1e-4f && DeskAnimations.Ease(0.5f) > 0.8f && DeskAnimations.Ease(0.25f) < DeskAnimations.Ease(0.5f));
        var p0 = DeskAnimations.BackFlagPose(0f); var p6 = DeskAnimations.BackFlagPose(0.6f); var p1 = DeskAnimations.BackFlagPose(1f);
        C("anim: back flag pops in from the right", p0.Dx == 14f && p0.Scale == 0.6f && p0.Opacity == 0f
            && MathF.Abs(p6.Dx + 3f) < 1e-3f && MathF.Abs(p6.Scale - 1.08f) < 1e-3f && p1 == (0f, 1f, 1f), $"{p6.Dx} {p6.Scale}");
    }
}
