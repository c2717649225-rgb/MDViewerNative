using System;
using MDViewerNative;

// U6b：合并标题栏命中（docs/design/u6b-titlebar.md）。
internal static class DeskFrameTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk frame") => check("DeskFrame/" + name, ok, detail);
        const float W = 1200f, H = 44f, B = 8f;
        C("caption buttons 46 wide from the right", DeskFrame.HitCaption(W - 1f, 20f, W, H) == CaptionPart.Close
            && DeskFrame.HitCaption(W - 46f, 20f, W, H) == CaptionPart.Close && DeskFrame.HitCaption(W - 47f, 20f, W, H) == CaptionPart.Max
            && DeskFrame.HitCaption(W - 138f, 20f, W, H) == CaptionPart.Min && DeskFrame.HitCaption(W - 139f, 20f, W, H) == CaptionPart.None
            && DeskFrame.HitCaption(W - 10f, 44f, W, H) == CaptionPart.None);
        C("buttons report system codes (Win11 snap layouts need HTMAXBUTTON)", DeskFrame.Hit(W - 70f, 20f, W, H, false, B, false, false) == DeskFrame.HtMaxButton
            && DeskFrame.Hit(W - 10f, 20f, W, H, false, B, false, false) == DeskFrame.HtClose && DeskFrame.Hit(W - 120f, 20f, W, H, false, B, false, false) == DeskFrame.HtMinButton);
        C("blank bar drags", DeskFrame.Hit(600f, 20f, W, H, false, B, false, false) == DeskFrame.HtCaption);
        C("tabs and tools stay client", DeskFrame.Hit(600f, 20f, W, H, false, B, true, false) == DeskFrame.HtClient);
        C("below the bar is client", DeskFrame.Hit(600f, 44f, W, H, false, B, false, false) == DeskFrame.HtClient);
        C("top edge resizes, corners diagonally", DeskFrame.Hit(600f, 3f, W, H, false, B, true, false) == DeskFrame.HtTop
            && DeskFrame.Hit(5f, 3f, W, H, false, B, false, false) == DeskFrame.HtTopLeft && DeskFrame.Hit(W - 5f, 3f, W, H, false, B, false, false) == DeskFrame.HtTopRight);
        C("maximized: no top resize band", DeskFrame.Hit(600f, 3f, W, H, true, B, false, false) == DeskFrame.HtCaption
            && DeskFrame.Hit(W - 5f, 3f, W, H, true, B, false, false) == DeskFrame.HtClose);
        C("immersive: only the window buttons stay", DeskFrame.Hit(600f, 20f, W, H, false, B, false, true) == DeskFrame.HtClient
            && DeskFrame.Hit(W - 10f, 20f, W, H, false, B, false, true) == DeskFrame.HtClose);
        C("code round trip", DeskFrame.PartOf(DeskFrame.CodeOf(CaptionPart.Max)) == CaptionPart.Max && DeskFrame.PartOf(DeskFrame.HtCaption) == CaptionPart.None);
    }
}
