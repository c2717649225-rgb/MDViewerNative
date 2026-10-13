using System;
using System.Collections.Generic;
using System.Reflection;
using MDViewerNative;

// A5:长段落 KP 断行不得退化成“一词一行”(紧急遍零缺点值缺陷),两个短词不因后面的不可断长串被拆开。
internal static class A5KpTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        using (new RenderStateScope())
        {
            foreach (float size in new[] { 16f, 20f })
            {
                RenderResources.SetBodySize(size);
                foreach (float width in new[] { 640f, 720f })   // S80: columns 600 and 640 (V - 2P)
                {
                    string p2 = string.Concat(System.Linq.Enumerable.Repeat("Second paragraph continues the reading. ", 6)).TrimEnd();
                    var lines = Lines(p2, width);
                    int shortLines = 0;
                    for (int k = 0; k + 1 < lines.Count; k++) if (lines[k].Trim().Length < 20) shortLines++;
                    check($"A5Kp/long paragraph has no one-word lines size={size} V={width}", lines.Count > 1 && shortLines == 0, $"lines=[{string.Join("|", lines)}]");
                    string mixed = "Long link: https://example.com/a/very/long/path/that/keeps/going/and/going/without/any/spaces/to/break/on/index.html and a bare one https://example.org/another/long/address/with/query?alpha=1&beta=2&gamma=3 then plain words follow.";
                    var m = Lines(mixed, width);
                    check($"A5Kp/short words stay together before an unbreakable run size={size} V={width}", m.Count > 0 && m[0].Contains("Long link:"), $"lines=[{string.Join("|", m)}]");
                    bool packed = m.Exists(l => l.Contains("index.html") && l.Contains("example.org"));
                    check($"A5Kp/overfull line holds a single unbreakable run size={size} V={width}", !packed, $"lines=[{string.Join("|", m)}]");
                    var mw = Widths(mixed, width);
                    int under = m.FindIndex(l => l.TrimEnd().EndsWith("and a bare one"));   // S83:URL1 可在串内折开后,这一行以 “…index.html” 起头
                    check($"A5Kp/very underfull line is not justified size={size} V={width}", under < 0 || (under < mw.Count && mw[under] < Column(width) - 1f), under < 0 ? "no underfull line at this width" : under < mw.Count ? $"w={mw[under]:0.#} col={Column(width):0}" : "missing");
                    int url = m.FindIndex(l => l.Contains("index.html"));
                    check($"A5Kp/unbreakable run wraps inside the column size={size} V={width}", url >= 0 && url < mw.Count && mw[url] <= width + 0.5f, url >= 0 && url < mw.Count ? $"w={mw[url]:0.#}" : "missing");
                }
            }
            // S83:S80 宽度扫描发现 20 号字、列宽 1156 时第一行只剩 “Long”,“link:” 跟着长网址到了下一行。
            // 扫描全部常用宽度,不只盯两个点。
            string sweepText = "Long link: https://example.com/a/very/long/path/that/keeps/going/and/going/without/any/spaces/to/break/on/index.html and a bare one https://example.org/another/long/address/with/query?alpha=1&beta=2&gamma=3 then plain words follow.";
            var orphans = new List<string>();
            var schemes = new List<string>();   // 网址不得在 “https:” 与 “//” 之间断开(冒号后的断点只给汉字)
            var stranded = new List<string>();   // 放得下 “Long link: 网址” 时,“Long link:” 不得独占一行(S83 截图 w1782 发现)
            var overlong = new List<string>();   // 网址比整列还宽时,从 “Long link:” 那一行就开始、排满后折在串内的 “/” 后(S83 截图 w1100 发现)
            foreach (float size in new[] { 16f, 20f })
            {
                RenderResources.SetBodySize(size);
                const string url1 = "https://example.com/a/very/long/path/that/keeps/going/and/going/without/any/spaces/to/break/on/index.html";
                var urlW = Widths(url1 + " " + url1, 1300f);   // 两份并排放不下、单份放得下;取末行(行宽不含尾随空格)
                float url1W = urlW.Count > 1 ? urlW[urlW.Count - 1] : 0f;
                for (float v = 500f; v <= 1800f; v += 8f)
                {
                    var sweep = Lines(sweepText, v);
                    if (sweep.Count == 0 || !sweep[0].Contains("Long link:")) orphans.Add($"size={size} V={v} lines=[{string.Join("|", sweep)}] w=[{string.Join(",", Widths(sweepText, v).ConvertAll(x => x.ToString("0")))}]");
                    if (sweep.Count > 1 && sweep[0].TrimEnd().EndsWith("link:") && Widths(sweepText, v)[0] + 0.5f * size + url1W <= Column(v)) stranded.Add($"size={size} V={v} col={Column(v):0} url={url1W:0}");
                    if (url1W > Column(v) && (sweep.Count == 0 || !sweep[0].Contains("Long link: https://example.com/a/") || Widths(sweepText, v)[0] < 0.8f * Column(v))) overlong.Add($"size={size} V={v} col={Column(v):0} url={url1W:0.#} lines=[{string.Join("|", sweep)}] w=[{string.Join(",", Widths(sweepText, v).ConvertAll(x => x.ToString("0.#")))}]");
                    if (sweep.Exists(l => l.TrimEnd().EndsWith("https:"))) schemes.Add($"size={size} V={v}");
                }
            }
            check("A5Kp/short words stay together before an unbreakable run at every width", orphans.Count == 0,
                orphans.Count == 0 ? "V 500..1800 step 8 × 16/20" : orphans.Count + " widths: " + string.Join("; ", orphans.GetRange(0, Math.Min(4, orphans.Count))));
            check("A5Kp/a URL never breaks between its scheme and the slashes", schemes.Count == 0,
                schemes.Count == 0 ? "V 500..1800 step 8 × 16/20" : schemes.Count + " widths: " + string.Join("; ", schemes.GetRange(0, Math.Min(4, schemes.Count))));
            check("A5Kp/a short lead-in shares the line with the run when both fit", stranded.Count == 0,
                stranded.Count == 0 ? "V 500..1800 step 8 × 16/20" : stranded.Count + " widths: " + string.Join("; ", stranded.GetRange(0, Math.Min(4, stranded.Count))));
            check("A5Kp/a run wider than the column starts on the lead-in line", overlong.Count == 0,
                overlong.Count == 0 ? "V 500..1800 step 8 × 16/20" : overlong.Count + " widths: " + string.Join("; ", overlong.GetRange(0, Math.Min(3, overlong.Count))));
        }
    }

    private static List<float> Widths(string text, float width)
    {
        using var doc = new DocView();
        doc.Load(text + "\n");
        doc.OnWidth(width);
        var getEnt = typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic);
        object ent = getEnt.Invoke(doc, new object[] { 0 });
        var layouts = ent.GetType().GetField("KpLayouts").GetValue(ent) as List<nint> ?? new List<nint>();
        return layouts.ConvertAll(l => Dw.LayoutW(l));
    }

    private static List<string> Lines(string text, float width)
    {
        using var doc = new DocView();
        doc.Load(text + "\n");
        doc.OnWidth(width);
        var getEnt = typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic);
        object ent = getEnt.Invoke(doc, new object[] { 0 });
        return ent.GetType().GetField("KpLines").GetValue(ent) as List<string> ?? new List<string>();
    }

    private static float Column(float width)
    {
        using var doc = new DocView();
        doc.Load("x\n");
        doc.OnWidth(width);
        return doc.ColW;
    }
}

