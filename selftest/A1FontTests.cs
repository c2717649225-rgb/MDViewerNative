using System;
using System.IO;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static class A1FontTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "candidate priority; no font installation") => check("A1Font/" + name, ok, detail);
        var simsun = new[] { ("SimSun & NSimSun (TrueType)", "simsun.ttc") };
        C("paired SimSun registry name", FontProbe.Match(simsun)?.idx == 4);
        C("localized SimSun file", FontProbe.Match(new[] { ("宋体 & 新宋体 (TrueType)", @"C:\Windows\Fonts\simsun.ttc") })?.idx == 4);
        C("NSimSun-only is not SimSun", FontProbe.Match(new[] { ("NSimSun (TrueType)", "nsimsun.ttf") }) == null);
        C("SimSun-ExtB is not body fallback", FontProbe.Match(new[] { ("SimSun-ExtB (TrueType)", "simsunb.ttf") }) == null);
        C("unrelated SimSun substring rejected", FontProbe.Match(new[] { ("NotSimSun (TrueType)", "not-simsun.ttf") }) == null);
        var entries = new[]
        {
            ("SimSun & NSimSun (TrueType)", "simsun.ttc"),
            ("Noto Serif SC (TrueType)", "NotoSerifSC-VF.ttf"),
            ("Source Han Serif SC (TrueType)", "SourceHanSerifSC-Regular.otf")
        };
        C("existing priority retained", FontProbe.Match(entries)?.idx == 1);
        C("installed preferred family uses sfnt name",
            FontProbe.SelectFamily(entries, _ => true, path => Path.GetFileName(path) == "SourceHanSerifSC-Regular.otf" ? "Source Han Serif SC Actual" : "wrong") == "Source Han Serif SC Actual");
        C("stale preferred entries fall through to installed SimSun",
            FontProbe.SelectFamily(entries, path => Path.GetFileName(path) == "simsun.ttc", _ => "SimSun") == "SimSun");
        C("stale entry falls through within same candidate",
            FontProbe.SelectFamily(new[] { ("Noto Serif SC (TrueType)", "missing.ttf"), ("Noto Serif SC (TrueType)", "available.ttf") },
                path => Path.GetFileName(path) == "available.ttf", _ => "Noto Serif SC") == "Noto Serif SC");
        C("file exists but name unreadable uses known family", FontProbe.SelectFamily(simsun, _ => true, _ => null) == "SimSun");
        C("all files missing does not invent installed family", FontProbe.SelectFamily(entries, _ => false, _ => throw new InvalidOperationException("must not read a missing font")) == null);
        C("empty registry value not treated as installed", FontProbe.SelectFamily(new[] { ("SimSun (TrueType)", "") }, _ => true, _ => "SimSun") == null);
        C("empty font registry returns null", FontProbe.SelectFamily(Array.Empty<(string, string)>(), _ => true, _ => "unused") == null);

        string family = FontProbe.ProbeFamily() ?? "Times New Roman";
        using (new RenderStateScope())
        {
            foreach (float size in new[] { 12f, 16f, 20f, 28f })
            {
                RenderResources.SetBodySize(size);
                foreach (bool serif in new[] { false, true })
                {
                    RenderResources.SetSerif(serif, family);
                    string body = serif ? family : "Segoe UI";
                    var formats = new (string Role, nint Format, string Family, uint Weight, float Size)[]
                    {
                        ("H1", RenderResources.FmtH1, "Segoe UI", 700u, 1.625f * size),
                        ("H2", RenderResources.FmtH2, "Segoe UI", 700u, 1.3125f * size),   // S87:600 时西文用半粗、雅黑无半粗落到粗体,同一标题两种粗细
                        ("H3", RenderResources.FmtH3, "Segoe UI", 700u, 1.125f * size),
                        ("H4-6", RenderResources.FmtH4, "Segoe UI", 700u, size),
                        ("body", RenderResources.FmtBody, body, 400u, size),
                        ("UI", RenderResources.FmtUi, "Segoe UI", 400u, MathF.Max(10f, 0.8125f * size)),
                        ("code", RenderResources.FmtMono, "Consolas", 400u, RenderResources.MonoSize(size))
                    };
                    foreach (var f in formats)
                    {
                        nint reference = Dw.MkFormat(f.Family, f.Weight, f.Size);
                        try
                        {
                            float actual = Measure(f.Format), expected = Measure(reference);
                            C($"role {f.Role} size={size} serif={serif}", MathF.Abs(actual - expected) < 0.01f,
                                $"family={f.Family} actual={actual:F3} reference={expected:F3}");
                        }
                        finally { Marshal.Release(reference); }
                    }
                }
                nint sans = Dw.MkFormat("Segoe UI", 400u, size);
                try
                {
                    C("serif changes only body " + size, MathF.Abs(Measure(RenderResources.FmtBody) - Measure(sans)) > 0.1f,
                        "installed/test serif=" + family + "; distinguishable measured glyphs, not source-string assertions");
                }
                finally { Marshal.Release(sans); }
            }
        }
    }

    private static float Measure(nint format)
    {
        nint layout = Dw.MkLayout("Hamburgefonstiv ABC 0123 中英混排", format, 10000f);
        try { return Dw.LayoutW(layout); }
        finally { Marshal.Release(layout); }
    }
}

