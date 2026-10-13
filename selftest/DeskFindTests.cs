using System;
using MDViewerNative;

// U7：desk 查找框几何（演示稿 .findbar）。
internal static class DeskFindTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk find") => check("DeskFind/" + name, ok, detail);
        const float toolH = 44f;
        C("desk reading picks the desk box at any width",
            OverlayGeometry.Find(true, false, false, 1400f, true) == FindLayout.Desk && OverlayGeometry.Find(true, false, false, 420f, true) == FindLayout.Desk);
        C("desk editing uses the desk strip", OverlayGeometry.Find(true, true, false, 1400f, true) == FindLayout.DeskEdit
            && OverlayGeometry.Find(true, true, true, 1400f, true) == FindLayout.DeskEditReplace);
        C("classic editing unchanged", OverlayGeometry.Find(true, true, false, 1400f) == FindLayout.Editing);
        C("desk strip reserves the same height", OverlayGeometry.FindHeight(FindLayout.DeskEdit) == OverlayGeometry.RowHeight
            && OverlayGeometry.FindHeight(FindLayout.DeskEditReplace) == OverlayGeometry.TwoRowHeight);
        var de = OverlayGeometry.FindControls(FindLayout.DeskEditReplace, 1000f, toolH);
        var field = OverlayGeometry.DeskEditField(1000f, toolH, false);
        C("desk strip: icon buttons 28 with gap 2, ending 16 from the right", de.Close.Right == 1000f - 16f && de.Close.W == 28f
            && de.Options.Right == de.Close.X - 2f && de.Next.Right == de.Options.X - 2f && de.Prev.Right == de.Next.X - 2f, $"{de.Close}");
        C("desk strip: count 48 before the buttons", de.Count.W == 48f && de.Count.Right == de.Prev.X - 6f);
        C("desk strip: field after the magnifier, input inset 8", field.X == 37f && field.Right == de.Count.X - 8f
            && de.Input.X == field.X + 8f && de.Input.Right == field.Right - 8f && de.Input.H == 20f, $"{field} {de.Input}");
        var rfield = OverlayGeometry.DeskEditField(1000f, toolH, true);
        C("desk strip: replace row aligns under find", rfield.X == field.X && rfield.W == field.W && rfield.Y == field.Y + 36f
            && de.Replace.X == de.Input.X && de.ReplaceOne.X == de.Count.X && de.ReplaceAll.Right == 1000f - 16f);
        C("desk strip: no text labels", de.Label.IsEmpty && de.ReplaceLabel.IsEmpty);
        C("classic unchanged", OverlayGeometry.Find(true, false, false, 1400f) == FindLayout.Panel);
        C("floating box reserves no height", OverlayGeometry.FindHeight(FindLayout.Desk) == 0f);

        var box = OverlayGeometry.FindBox(FindLayout.Desk, 1200f, toolH);
        C("top 10 right 30 height 40", box.Y == toolH + 10f && box.Right == 1200f - 30f && box.H == 40f, $"{box}");
        C("wide width is 210 input plus fixed parts", box.W == OverlayGeometry.DeskFindWidth(210f), $"{box.W}");

        var c = OverlayGeometry.FindControls(FindLayout.Desk, 1200f, toolH);
        var icon = OverlayGeometry.DeskFindIconBox(box);
        C("icon sits after the 12 padding", icon.X == box.X + 13f && icon.W == 15f);
        C("input 210 after icon gap 6", c.Input.X == icon.Right + 6f && c.Input.W == 210f);
        C("count 48 then 6 gap", c.Count.W == 48f && c.Prev.X == c.Count.Right + 6f);
        C("buttons 28 with gap 2", c.Prev.W == 28f && c.Next.X == c.Prev.Right + 2f && c.Options.X == c.Next.Right + 2f && c.Close.X == c.Options.Right + 2f);
        C("close ends at inner padding", c.Close.Right == box.Right - 7f, $"{c.Close.Right} vs {box.Right}");
        C("no label, no replace", c.Label.IsEmpty && c.Replace.IsEmpty);
        C("controls stay inside the box", Array.TrueForAll(c.All, b => b.IsEmpty || b.X >= box.X && b.Right <= box.Right && b.Y >= box.Y && b.Bottom <= box.Bottom));

        var narrow = OverlayGeometry.FindBox(FindLayout.Desk, 420f, toolH);
        var nc = OverlayGeometry.FindControls(FindLayout.Desk, 420f, toolH);
        C("narrow windows shrink the input", nc.Input.W < 210f && nc.Input.W >= OverlayGeometry.DeskFindInputMin, $"{nc.Input.W}");
        // 朱笔圈（演示 drawCircle）
        var pts = InkCircle.Points(100f, 40f, InkCircle.Seed(123));
        C("ink circle has 47 points", pts.Length == InkCircle.Segments + 1);
        C("ink circle is deterministic per hit", InkCircle.Seed(123) == InkCircle.Seed(123) && InkCircle.Seed(123) != InkCircle.Seed(124));
        C("ink circle stays near its frame", Array.TrueForAll(pts, p => p.X > -8f && p.X < 108f && p.Y > -6f && p.Y < 46f));
        C("ink stroke overshoots the start", MathF.Abs(pts[^1].X - pts[0].X) + MathF.Abs(pts[^1].Y - pts[0].Y) > 1f);
        var f = InkCircle.Frame(10f, 20f, 40f, 18f);
        C("frame pads 12 sideways and 7.8 vertically", f.X == -2f && MathF.Abs(f.Y - 12.2f) < 1e-3f && f.W == 64f && MathF.Abs(f.H - 33.6f) < 1e-3f, $"{f}");
        C("progress 0 draws nothing", InkCircle.Progress(pts, 0f) == (0, 0f));
        C("progress 1 draws all", InkCircle.Progress(pts, 1f).Whole >= InkCircle.Segments - 1);
        C("half progress is mid-way", InkCircle.Progress(pts, 0.5f).Whole is > 15 and < 31);
        C("waits 360 ms before the stroke", InkCircle.DrawProgress(0.4f) == 0f && InkCircle.DrawProgress(0.5f) > 0f && InkCircle.DrawProgress(1f) == 1f);
        // 选区气泡（演示 .bubble）
        C("bubble needs text", !SelectionBubble.Shows(null) && !SelectionBubble.Shows("  \n ") && SelectionBubble.Shows("书签"));
        C("bubble stops past 300 chars", SelectionBubble.Shows(new string('字', 300)) && !SelectionBubble.Shows(new string('字', 301)));
        C("find takes the first 30 trimmed chars", SelectionBubble.Query("  " + new string('a', 40)) == new string('a', 30) && SelectionBubble.Query(" 书签 ") == "书签");
        var bl = SelectionBubble.Place(600f, 400f, 420f, 25f, 25f, 17f, 1200f, 44f);
        C("bubble sits 46 above the selection, centred", bl.Box.Y == 400f - 46f && MathF.Abs(bl.Box.X + bl.Box.W / 2f - 600f) < 0.01f && !bl.Below, $"{bl.Box}");
        C("buttons: padding 11, icon 13, gap 6", bl.Copy.W == 11f + 13f + 6f + 25f + 11f && bl.Find.X == bl.Copy.Right + 2f && bl.Copy.X == bl.Box.X + 4f);
        C("bubble height is padding plus button", bl.Box.H == 4f + 10f + 17f + 4f);
        var edge = SelectionBubble.Place(10f, 400f, 420f, 25f, 25f, 17f, 1200f, 44f);
        C("bubble stays inside the window, arrow follows the selection", edge.Box.X == 8f && edge.ArrowX < edge.Box.X + edge.Box.W / 2f);
        var flip = SelectionBubble.Place(600f, 70f, 90f, 25f, 25f, 17f, 1200f, 44f);
        C("near the title bar the bubble flips below", flip.Below && flip.Box.Y > 90f);
        C("bubble hit parts", SelectionBubble.Hit(bl, bl.Copy.X + 2f, bl.Copy.Y + 2f) == BubblePart.Copy && SelectionBubble.Hit(bl, bl.Find.X + 2f, bl.Find.Y + 2f) == BubblePart.Find
            && SelectionBubble.Hit(bl, bl.Box.X + 1f, bl.Box.Y + 1f) == BubblePart.Box && SelectionBubble.Hit(bl, 0f, 0f) == BubblePart.None);
        C("narrow box stays on screen", narrow.X >= 6f && narrow.Right <= 420f, $"{narrow}");
    }
}
