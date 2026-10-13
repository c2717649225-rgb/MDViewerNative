using System;
using MDViewerNative;

// U6c：读时退后（规格 §3）。
internal static class TitlebarQuietTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "titlebar quiet") => check("TitlebarQuiet/" + name, ok, detail);
        var q = new TitlebarQuiet();
        C("near the top stays", !q.Observe(50f, true, false) && !q.Observe(110f, true, false) && !q.Quiet);
        C("scrolling down past 120 goes quiet", q.Observe(200f, true, false) && q.Quiet);
        C("small wiggles do not wake", !q.Observe(190f, true, false) && !q.Observe(180f, true, false) && q.Quiet);
        C("up 30 wakes", q.Observe(145f, true, false) && !q.Quiet);
        var slow = new TitlebarQuiet();
        float y = 100f; slow.Observe(y, true, false);
        bool quiet = false;
        for (int i = 0; i < 10; i++) { y += 4f; quiet |= slow.Observe(y, true, false); }
        C("slow scrolling accumulates past the 6 step", quiet && slow.Quiet);
        var top = new TitlebarQuiet(); top.Observe(400f, true, false);
        C("back near the top wakes", top.Quiet && top.Observe(40f, true, false) && !top.Quiet);
        var edit = new TitlebarQuiet();
        C("editing never quiets", !edit.Observe(500f, false, false) && !edit.Quiet);
        var hover = new TitlebarQuiet();
        C("pointer or focus on the bar holds", !hover.Observe(500f, true, true) && !hover.Quiet);
        hover.Observe(900f, true, false);
        C("wake", hover.Quiet && hover.Wake(900f) && !hover.Quiet && !hover.Wake(900f));
        C("wake resets the reference", !hover.Observe(905f, true, false) && hover.Observe(920f, true, false));
        var mix = new TitlebarQuiet(); mix.Observe(500f, true, false);
        C("amount continues from the current mix", mix.Amount(0.4f, 0f) == 0.4f && mix.Amount(0.4f, 1f) == 1f && mix.Wake(500f) && mix.Amount(0.4f, 0.5f) == 0.2f);
    }
}
