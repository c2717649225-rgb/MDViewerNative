using System;
using MDViewerNative;

// U9c：墨未干的动画姿态与时间线。
internal static class DeskInkTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk ink") => check("DeskInk/" + name, ok, detail);
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
        C("fresh starts from nothing", InkDrop.Fresh(0f).Scale == 0f);
        C("fresh overshoots to 1.25 at 60%", Near(InkDrop.Fresh(0.6f).Scale, 1.25f));
        C("fresh settles at 1", Near(InkDrop.Fresh(1f).Scale, 1f) && InkDrop.Fresh(1f).Gloss == 1f);
        var d35 = InkDrop.Dry(0.35f);
        C("drying loses gloss and halo, shrinks to .9 by 35%", Near(d35.Scale, 0.9f) && Near(d35.Gloss, 0f) && Near(d35.Halo, 0f) && d35.Opacity == 1f && Near(d35.Saturate, 0.75f));
        var d100 = InkDrop.Dry(1f);
        C("drying ends at .55 and invisible", Near(d100.Scale, 0.55f) && Near(d100.Opacity, 0f) && Near(d100.Brightness, 0.9f));
        C("drying starts wet", InkDrop.Dry(0f) == InkDrop.Wet);
        C("filter identity", InkDrop.Filter(0xB0361F, 1f, 1f) == 0xB0361F);
        uint dull = InkDrop.Filter(0xB0361F, 0.6f, 0.9f);
        C("filter dulls and darkens", ((dull >> 16) & 0xFF) < 0xB0 && (dull & 0xFF) > 0x1F * 0.9f, dull.ToString("X6"));

        var ink = new InkDrops();
        var id = Guid.NewGuid();
        C("unknown clean tab draws the close glyph", ink.Pose(id, false, 0) == null);
        C("unknown dirty tab is a still wet drop", ink.Pose(id, true, 0) == InkDrop.Wet);
        ink.Fresh(id, 1000);
        C("fresh drop animates for 450 ms", ink.Pose(id, true, 1100)?.Scale is > 0f and < 1.3f && ink.Pose(id, true, 1450) == InkDrop.Wet);
        C("remaining frames", ink.RemainingMs(1100) == 350 && ink.RemainingMs(2000) == 0);
        ink.Dry(id, 3000);
        C("saved drop dries for 1200 ms, then the close glyph returns", ink.Pose(id, false, 3600) is { } p && p.Scale < 0.9f && ink.Pose(id, false, 4200) == null);
        C("typing again during drying shows a wet drop", ink.Pose(id, true, 3600) == InkDrop.Wet);
    }
}
