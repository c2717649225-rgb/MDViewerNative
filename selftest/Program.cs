using MDViewerNative;
using System.IO;
// 无头选区模型验证:直接驱动 PosAt/CopyText,不依赖窗口
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

internal static unsafe class Program
{
    static int Failures;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Console.WriteLine($"  PASS {name} {detail}");
        else { Console.WriteLine($"  FAIL {name} {detail}"); Failures++; }
    }

    // ---------- CommonMark 规范一致性评分:从 spec.txt 提取示例,与 HtmlExport 输出对比 ----------

    // 提取示例正文并渲染为 HTML 片段
    static string SpecRender(string md)
    {
        string html = HtmlExport.Build(MdParser.Parse(md), "t", false);
        int a = html.IndexOf("<body>\n", StringComparison.Ordinal) + 7;
        int b = html.IndexOf("</body>", StringComparison.Ordinal);
        return html.Substring(a, b - a);
    }

    // 两侧对称的输出归一化:剥掉与我们导出策略不同的装饰,聚焦结构与内容
    static string SpecNorm(string h)
    {
        h = h.Replace("\r", "");
        // 自闭合形式先统一(规范用 <br />,我们用 <br>),再做软/硬换行归一
        h = h.Replace(" />", ">").Replace("/>", ">");
        // 换行策略:我们统一输出 <br>,规范是 \n(硬换行)或 \n(软换行),归一后不计差异
        h = h.Replace("<br>", "\n");
        // 代码块语言 class:我们不输出 language-xxx(已知差异)
        h = Regex.Replace(h, " class=\"language-[^\"]*\"", "");
        // 我们的结构属性(li value/style/td 对齐/ol start)
        h = Regex.Replace(h, "<(li|td|th|ol|ul) [^>]*>", "<$1>");
        // Exported heading ids are navigation metadata, not part of CommonMark block structure.
        // Ignore only this attribute here; NavigationTests verifies actual id/href semantics.
        h = Regex.Replace(h, "<(h[1-6]) id=\"[^\"]*\">", "<$1>");
        // 块级图片的 <p> 包裹差异(渲染等价)
        h = Regex.Replace(h, "<p>(<img [^>]*>)</p>", "$1");
        // pre 外:尾随空格与多余换行折叠(硬换行表示差异)
        var parts = Regex.Split(h, "(<pre>.*?</pre>)", RegexOptions.Singleline);
        for (int i = 0; i < parts.Length; i += 2)
            parts[i] = Regex.Replace(Regex.Replace(parts[i], "[ \t]+\n", "\n"), "\n+", "\n");
        h = string.Concat(parts);
        // 标签边界换行折叠(pre 内部内容已转义,不会误伤)
        // S75:只折叠块级标签边界的空白;行内标签旁的换行是软换行,与文字间的一样按空格计(我们输出空格,规范输出 \n)。
        const string blockTag = @"</?(?:p|div|blockquote|ul|ol|li|h[1-6]|pre|table|thead|tbody|tr|td|th|hr|dl|dt|dd|details|summary)\b[^>]*>";
        h = Regex.Replace(h, @"[ \t\n]+(?=" + blockTag + ")", "");
        h = Regex.Replace(h, "(" + blockTag + @")[ \t\n]+", "$1");
        // 代码块首尾换行:我们的导出不带末尾换行(旧规则曾统一删去,保持)。
        h = Regex.Replace(h, @"(<pre><code[^>]*>)\n+", "$1");
        h = Regex.Replace(h, @"\n+(?=</code></pre>)", "");
        // 软换行(S75):规范输出 \n,我们输出空格(中日文之间为空);pre 外残留换行统一为空格,两侧对称。
        var parts2 = Regex.Split(h, "(<pre>.*?</pre>)", RegexOptions.Singleline);
        for (int i = 0; i < parts2.Length; i += 2) parts2[i] = parts2[i].Replace("\n", " ");
        h = string.Concat(parts2);
        return h.Trim();
    }

    // Legacy diagnostic: stripping emphasis can hide missing/incorrect styling, not just nesting differences.
    static string SpecNorm2(string h)
    {
        string h2 = SpecNorm(h);
        return h2.Replace("<em>", "").Replace("</em>", "").Replace("<strong>", "").Replace("</strong>", "");
    }

    // ---------- KP 断行快照:全文所有 KP 段落的逐行切分(改动前后必须逐字节一致) ----------
    // 语料快照:解析块树文本 dump(改动前后 diff 审查用,防内容列等重构悄悄改真实文档)
    static int RunBlockDump(string path)
    {
        string text = File.ReadAllText(path);
        var blocks = MdParser.Parse(text);
        var sb = new System.Text.StringBuilder();
        DumpBlocks(blocks, 0, sb);
        Console.Write(sb.ToString());
        return 0;
    }

    static void DumpBlocks(List<Block> bs, int depth, System.Text.StringBuilder sb)
    {
        string pad = new string('.', depth * 2);
        foreach (var b in bs)
        {
            switch (b)
            {
                case ListItem li:
                    sb.Append(pad).Append($"ListItem m={li.Marker} x={li.XOff} nest={li.Nest} loose={li.Loose} task={li.Task} n={li.Inlines.Count}")
                      .Append(' ').Append(OneLine(li.Inlines)).Append('\n');
                    if (li.Sub != null) DumpBlocks(li.Sub, depth + 1, sb);
                    break;
                case CodeBlock cb:
                    sb.Append(pad).Append($"CodeBlock x={cb.XOff} lang=").Append(OneLine(null)).Append(' ')
                      .Append(cb.Text.TrimEnd('\n').Replace("\n", "\\n")[..Math.Min(60, cb.Text.TrimEnd('\n').Replace("\n", "\\n").Length)]).Append('\n');
                    break;
                case Heading h:
                    sb.Append(pad).Append($"Heading x={h.XOff} lvl={h.Level} ").Append(OneLine(h.Inlines)).Append('\n');
                    break;
                case Quote q:
                    sb.Append(pad).Append($"Quote x={q.XOff} ").Append(OneLine(q.Inlines)).Append('\n');
                    break;
                case Table tb:
                    sb.Append(pad).Append($"Table x={tb.XOff} rows={tb.Rows.Length}\n");
                    break;
                case HtmlBlock hb:
                    sb.Append(pad).Append($"HtmlBlock x={hb.XOff} ").Append(hb.Text.TrimEnd('\n').Replace("\n", "\\n")[..Math.Min(60, hb.Text.TrimEnd('\n').Replace("\n", "\\n").Length)]).Append('\n');
                    break;
                case Para p:
                    sb.Append(pad).Append($"Para x={p.XOff} n={p.Inlines.Count} ").Append(OneLine(p.Inlines)).Append('\n');
                    break;
                default:
                    sb.Append(pad).Append(b.GetType().Name).Append($" x={b.XOff}\n");
                    break;
            }
        }
    }

    static string OneLine(List<Run> runs)
    {
        if (runs == null) return "";
        string s = string.Concat(runs.Select(r => r.Text)).Replace("\n", "\\n");
        return s.Length > 60 ? s[..60] : s;
    }

    static int RunKpDump(string path, string imageDirectory = null, float widthDip = 0f, bool printLines = false)
    {
        string text = File.ReadAllText(path);
        using var graphics = new RenderResources(1.0f);
        using var doc = new DocView();
        doc.DocumentDirectory = imageDirectory ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        doc.Load(text);   // 计时:Load = Parse + Relayout(含全部 KP 断行)
        sw.Stop();
        doc.OnWidth(widthDip > 0f ? widthDip : 704f);   // 语料与规范基线的固定视口(历史 704 DIP;S80 起列宽 = 704 − 2P)
        int kpBlocks = 0, kpLines = 0;
        foreach (var b in doc.Blocks)
        {
            if (b is Para) { kpBlocks++; }
        }
        Console.WriteLine($"relayout: {sw.ElapsedMilliseconds} ms  blocks={doc.BlockCount} totalH={doc.TotalH:0}");
        // 逐块吐出 KpLines(缓存经 MeasureBlock 的临时 Build 不驻留——这正是待优化点;
        // 用 PosAtText 扫描逐块驱动 GetEnt 填充缓存,与 Paint 路径同构)
        var cacheObject = typeof(DocView).GetField("_layoutCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(doc);
        var cache = (System.Collections.IDictionary)cacheObject.GetType().GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(cacheObject);
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        for (float y = 0f; y < doc.TotalH; y += 8f) _ = doc.PosAtText(300f, y);   // 触发全部块 Build(模拟首屏滚动)
        swBuild.Stop();
        Console.WriteLine($"build-all: {swBuild.ElapsedMilliseconds} ms  cache.Count={cache.Count}");
        var linesFld = cache.Count > 0 ? cache[0]?.GetType().GetField("KpLines") : null;   // 从实例取运行时类型
        var layoutsFld = linesFld != null ? linesFld.ReflectedType.GetField("KpLayouts") : null;
        if (linesFld == null)   // 缓存首块不是 KP 段(标题等)时遍历找类型,诊断不致失明
        {
            foreach (var v in cache.Values)
            {
                var t = v?.GetType();
                if (t.GetField("KpLines") != null) { linesFld = t.GetField("KpLines"); layoutsFld = t.GetField("KpLayouts"); break; }
            }
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < doc.BlockCount; i++)
        {
            if (!cache.Contains(i)) continue;
            var ent = cache[i];
            var lines = linesFld?.GetValue(ent) as System.Collections.Generic.List<string>;
            if (lines == null || lines.Count == 0) continue;
            kpBlocks++; kpLines += lines.Count;
            foreach (var ln in lines) { sb.Append(ln).Append('\u0001'); if (printLines) Console.WriteLine($"KPLINE {ln}"); }
            sb.Append('\u0002');
        }
        // 稳定哈希(FNV-1a)
        ulong h = 14695981039346656037UL;
        foreach (char ch in sb.ToString()) { h ^= ch; h *= 1099511628211UL; }
        Console.WriteLine($"kpBlocks={kpBlocks} kpLines={kpLines} hash={h:x16} len={sb.Length}");
        // 诊断:KP 行最大绘制宽 vs 栏宽(校验逐行断行贴合栏宽,行内不应再被 DirectWrite 重排;
        // 行尾悬挂标点允许 maxKpLineW 略超 colW)
        float maxW = 0f;
        for (int i = 0; i < doc.BlockCount; i++)
        {
            if (!cache.Contains(i)) continue;
            var ent = cache[i];
            var lay = layoutsFld?.GetValue(ent) as System.Collections.Generic.List<nint>;
            if (lay == null) continue;
            foreach (var l in lay) { float w = Dw.LayoutW(l); if (w > maxW) maxW = w; }
        }
        Console.WriteLine($"colW={doc.ColW:0} maxKpLineW={maxW:0}");
        return 0;
    }

    // A1-B4 几何探针:按生产同一真值源(WindowFit)打印给定视口宽下的 P/列宽/左边界,供截图像素取证对照。
    // 用法:--geo <正文字号> <V> [V ...]
    static int RunGeoProbe(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: --geo <bodySize> <V> [V ...]"); return 1; }
        float body = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        foreach (string s in args.Skip(2))
        {
            float v = float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            float w = WindowFit.ColW(v);
            float x = WindowFit.ColX(v, w);
            Console.WriteLine($"geo v={v:0.##} bs={body:0.##} P={WindowFit.PadFor(v):0} colW={w:0.##} colX={x:0.##} colRight={x + w:0.##}");
        }
        return 0;
    }

    static int RunSpec(string path, bool verify = false, bool record = false)
    {
        string spec = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = spec.Split('\n');
        string fence = new string('`', 32);
        int total = 0, passed = 0, passed2 = 0, rawPassed = 0;
        var failsBySection = new SortedDictionary<string, int>();
        var failFile = new System.Text.StringBuilder();
        var results = new List<string>();
        results.Add("# SHA256 " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(spec))));
        string section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ")) section = lines[i].Substring(3).Trim();
            if (lines[i].StartsWith(fence) && lines[i].Contains(" example") && !lines[i].Contains("{"))
            {
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith(fence)) { body.Add(lines[i]); i++; }
                string joined = string.Join("\n", body) + "\n";
                int sep = joined.IndexOf("\n.\n", StringComparison.Ordinal);
                if (sep < 0 || section.Length == 0) continue;
                string md = joined.Substring(0, sep + 1).Replace("→", "\t");   // 规范文本中 → 是 tab 的可视化,还原为真实 tab
                string expected = joined.Substring(sep + 3).Replace("→", "\t");
                total++;
                string actual;
                try { actual = SpecRender(md); }
                catch (Exception ex) { actual = "EX:" + ex.Message; }
                if (actual.Replace("\r\n", "\n") == expected) rawPassed++;
                if (SpecNorm(actual) == SpecNorm(expected))
                {
                    passed++;
                    results.Add($"{total}\tstrict");
                }
                else if (SpecNorm2(actual) == SpecNorm2(expected))
                {
                    passed++; passed2++;   // Legacy baseline category; this does not prove visual equivalence.
                    results.Add($"{total}\tloose");
                }
                else
                {
                    results.Add($"{total}\tfail");
                    if (!failsBySection.ContainsKey(section)) failsBySection[section] = 0;
                    failsBySection[section]++;
                    failFile.AppendLine($"===== #{total} [{section}]");
                    failFile.AppendLine("MD:     " + md.Replace("\n", "\\n"));
                    failFile.AppendLine("EXPECT: " + SpecNorm(expected));
                    failFile.AppendLine("ACTUAL: " + SpecNorm(actual));
                }
            }
        }
        File.WriteAllText("spec_fail.txt", failFile.ToString());
        Console.WriteLine($"Raw HTML exact (LF only): {rawPassed}/{total}");
        Console.WriteLine($"Normalized structure: {passed - passed2}/{total}");
        Console.WriteLine($"Emphasis-stripped diagnostic (NOT equivalence): {passed}/{total} [spec_fail.txt]");
        foreach (var kv in failsBySection) Console.WriteLine($"  {kv.Value,3} FAIL  {kv.Key}");
        string baseline = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "spec-baseline.tsv");
        if (record)
        {
            File.WriteAllLines(baseline, results);
            Console.WriteLine("Recorded spec baseline; review every change before accepting it.");
        }
        if (verify)
        {
            if (!File.Exists(baseline)) { Console.WriteLine("FAIL missing spec baseline"); return 1; }
            var expectedResults = File.ReadAllLines(baseline);
            if (!expectedResults.SequenceEqual(results))
            {
                foreach (string result in expectedResults.Except(results)) Console.WriteLine("BASELINE " + result);
                foreach (string result in results.Except(expectedResults)) Console.WriteLine("CURRENT  " + result);
                Console.WriteLine("FAIL per-example spec baseline drift");
                return 1;
            }
            Console.WriteLine("Spec baseline: OK (every example)");
        }
        return 0;
    }

    // 引用嵌套:期望每个块都是 Quote 且 XOff 逐层 20 递进
    static void CheckQuote(string name, string md, int[] expectedXoffs)
    {
        var blocks = MdParser.Parse(md);
        bool ok = blocks.Count == expectedXoffs.Length;
        var xs = new System.Collections.Generic.List<string>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var q = blocks[i] as Quote;
            if (q == null) ok = false;
            else { xs.Add(q.XOff.ToString()); if (i >= expectedXoffs.Length || q.XOff != expectedXoffs[i]) ok = false; }
        }
        Check($"quote/{name}", ok, $"xoffs=[{string.Join(",", xs)}] blocks={blocks.Count}");
    }

    // ── P1 探针(PLAN-EXECUTE §0):DWrite JUST 对单行布局是否真实拉伸——typo 2a 的路线门 ──
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

    static int RunJustProbe()
    {
        using var graphics = new RenderResources(1.0f);
        const int W = 700, H = 130;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, W, H);
        IntPtr old = SelectObject(mem, bmp);
        string text = new string('中', 20);   // 自然宽 ≈320,布局盒 680:是否被 JUST 拉开一目了然
        IntPtr fmt = Dw.MkFormat("Segoe UI", 400u, 16f);
        nint[] lays = new nint[3];
        for (int k = 0; k < 3; k++)
        {
            lays[k] = Dw.MkLayout(k == 2 ? text + "\n" : text, fmt, 680f);
            Dw.Bind(lays[k]);
            Dw.SetWordWrapping(lays[k], 1 /*NO_WRAP*/);
            Dw.SetTextAlignment(lays[k], k == 0 ? 0 : 3 /*JUST*/);
        }
        Dw.Bind(lays[1]); var m1 = default(DWRITE_TEXT_METRICS); Dw.GetMetrics(lays[1], &m1);
        Dw.Bind(lays[2]); var m2 = default(DWRITE_TEXT_METRICS); Dw.GetMetrics(lays[2], &m2);
        Console.WriteLine($"metrics.height: 单行={m1.height} 尾\\n={m2.height}");
        var rect = new RECT { L = 0, T = 0, R = W, B = H };
        RenderResources.PBindExternal(mem, &rect);
        RenderResources.PBegin(RenderResources.Rt);
        var white = default(D2D1_COLOR_F); white.r = 1; white.g = 1; white.b = 1; white.a = 1;
        RenderResources.PClear(RenderResources.Rt, &white);
        float[] ys = { 5, 45, 85 };
        for (int k = 0; k < 3; k++)
        {
            var pt = default(D2D1_POINT_2F); pt.x = 10; pt.y = ys[k];
            RenderResources.PDraw(RenderResources.Rt, pt, lays[k], RenderResources.BInk, 0);
        }
        ulong a = 0, b = 0;
        RenderResources.PEnd(RenderResources.Rt, &a, &b);
        string[] names = { "LEFT 基线", "JUST 单行", "JUST+\\n 首行" };
        for (int k = 0; k < 3; k++)
        {
            int rightmost = 0;
            for (int x = W - 1; x > 0 && rightmost == 0; x--)
                for (int dy = 0; dy < 24; dy++)
                {
                    uint px = GetPixel(mem, x, (int)ys[k] + dy);
                    if ((px & 0xFFFFFF) != 0xFFFFFF) { rightmost = x; break; }
                }
            Console.WriteLine($"band{k}({names[k]}): 墨迹右缘 x={rightmost} 起点10");
        }
        SelectObject(mem, old); DeleteDC(mem); DeleteObject(bmp); ReleaseDC(IntPtr.Zero, screen);
        return 0;
    }

    // I6 前置:Relayout 性能门槛(PLAN-EXECUTE §1.6)。min-of-5 抗后台噪声;门槛=基线×1.05/×1.1,基线在 commit msg 留档。
    static int RunPerf()
    {
        using var graphics = new RenderResources(1.0f);
        string big = File.ReadAllText("../engine/big20k.md");
        string qf = File.ReadAllText("../samples/全格式测试.md");
        double minBig = double.MaxValue, minQf = double.MaxValue;
        double[] bigSamples = new double[5], qfSamples = new double[5];
        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var sample = new DocView();
            sample.Load(big);
            sw.Stop(); minBig = Math.Min(minBig, sw.Elapsed.TotalMilliseconds);
            bigSamples[i] = sw.Elapsed.TotalMilliseconds;
        }
        for (int i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var sample = new DocView();
            sample.Load(qf);
            sw.Stop(); minQf = Math.Min(minQf, sw.Elapsed.TotalMilliseconds);
            qfSamples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine($"perf: big20k={minBig:F0}ms qf={minQf:F0}ms");
        foreach (var (name, samples) in new[] { ("big20k", bigSamples), ("qf", qfSamples) })
        {
            var sorted = samples.OrderBy(value => value).ToArray();
            Console.WriteLine($"perf-samples: {name} ms=[{string.Join(",", samples.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}] median={sorted[2]:F2} max={sorted[4]:F2}");
        }
        int rc = 0;
        const double GateBig = 1250, GateQf = 40;   // 基线 2026-09-09:big20k=1133ms qf=16ms(min-of-5),门槛≈×1.1/×2.5(留噪声余量)
        if (minBig > GateBig) { Console.WriteLine($"FAIL perf/big20k {minBig:F0}>{GateBig}"); rc = 1; }
        if (minQf > GateQf) { Console.WriteLine($"FAIL perf/qf {minQf:F0}>{GateQf}"); rc = 1; }
        if (rc == 0) Console.WriteLine("PERF OK");
        return rc;
    }

    [STAThread]
    static int Main(string[] args)
    {
        using var apartment = new ComApartment();
        if (args.Length > 0 && args[0] == "--parser-isolation")
        { ParserIsolationTests.Run(Check); return Failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--html-shadow")
            return HtmlShadowReport.Run(args, SpecNorm, SpecRender);
        if (args.Length > 0 && args[0] == "--spec")
            return RunSpec(args.Length > 1 ? args[1] : "spec.txt");
        if (args.Length > 0 && args[0] == "--spec-verify")
            return RunSpec(args.Length > 1 ? args[1] : "spec.txt", verify: true);
        if (args.Length > 0 && args[0] == "--spec-record")
            return RunSpec(args.Length > 1 ? args[1] : "spec.txt", record: true);
        if (args.Length > 0 && args[0] == "--kp")
            return RunKpDump(args.Length > 1 ? args[1] : "../samples/全格式测试.md", args.Length > 2 ? args[2] : null,
                             args.Length > 3 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float kpWidth) ? kpWidth : 0f,
                             Array.IndexOf(args, "--lines") >= 0);
        if (args.Length > 0 && args[0] == "--dump")
            return RunBlockDump(args.Length > 1 ? args[1] : "../samples/全格式测试.md");
        if (args.Length > 0 && args[0] == "--font-probe")
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Console.WriteLine(MDViewerNative.RenderResources.FontProbeReport());
            return 0;
        }
        if (args.Length > 0 && args[0] == "--geo")
            return RunGeoProbe(args);
        if (args.Length > 0 && args[0] == "--justprobe")
            return RunJustProbe();
        if (args.Length > 0 && args[0] == "--perf")
            return RunPerf();

        MarkdownCompatibilityTests.Run(Check);
        Console.WriteLine("── 偏差清单固化(有意行为,勿按 CommonMark 原样修改) ──");
        {
            // #613:裸 URL 自动链接是 viewer 有意功能(规范输出为纯文本)
            Check("偏差/裸URL自动链接", SpecRender("https://example.com\n").Contains("<a href=\"https://example.com\">"),
                  "改动需人工确认功能取舍");
            // #172 类:script/style 块导出必须消毒转义(安全设计,导出文件不可执行)
            string sh = HtmlExport.Build(MdParser.Parse("<script>alert(1)</script>\n"), "t", false);
            Check("偏差/script导出消毒", sh.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "安全设计不得直通");
            // #174/175/178/180 类:带属性/跨行/单行闭合的 script/style 块识别同样正确,导出同样消毒(块边界非 bug,转义为安全设计)
            string sh2 = HtmlExport.Build(MdParser.Parse("<style\n  type=\"text/css\">\nh1 {color:red;}\n</style>\nokay\n"), "t", false);
            Check("偏差/style块带属性消毒", sh2.Contains("&lt;style") && sh2.Contains("&lt;/style&gt;") && sh2.Contains("<p>okay</p>"),
                  "识别正确+导出转义(安全设计)");
            // #522 类:三嵌套病态图片(规范 alt 语义极端,未追)
            Check("偏差/病态嵌套图片", SpecRender("![[[foo](uri1)](uri2)](uri3)\n").Contains("<img src=\"uri3\""),
                  "alt 细节与规范有差,渲染同形");
            // #303 类:不同 bullet 字符(-/+)聚合为同一列表(解析期 marker 同质化)
            string bl = SpecRender("- a\n+ b\n");
            Check("偏差/bullet同质化", bl.Contains("<li>a</li>") && bl.Contains("<li>b</li>"),
                  "规范拆两个 <ul>,观感等价");
            // Simple lists with no blank separators remain tight; propagation is tested separately.
            Check("列表/无空行保持紧凑", !SpecRender("- a\n- b\n").Contains("<p>"), "整表松散性见 ListSpacingTests");
        }

        Console.WriteLine("── 解析器:引用嵌套 ──");
        CheckQuote("单层", "> 一层\n", new[] { 20 });
        CheckQuote("空行分隔同层", "> 外\n>\n> 内\n", new[] { 20, 20 });
        CheckQuote("内嵌两层", "> 外\n>\n> > 内\n", new[] { 20, 40 });
        CheckQuote("顶层>>直写", ">> 直接二级\n", new[] { 40 });
        CheckQuote("顶层>>>直写三层", ">>> 直接三层\n", new[] { 60 });
        CheckQuote("三层混合", "> a\n> > b\n> > > c\n", new[] { 20, 40, 60 });
        CheckQuote("无空格>>", ">>直接二级\n", new[] { 40 });
        // 引用内列表:产出 ListItem(而非 Quote),但缩进必须体现引用层级
        {
            var blocks = MdParser.Parse("> - 项一\n> - 项二\n");
            bool ok = blocks.Count == 2 && blocks[0] is ListItem && blocks[1] is ListItem
                      && blocks[0].XOff == 20 && blocks[1].XOff == 20;
            Check("quote/引用内列表", ok, $"types=[{blocks[0].GetType().Name},{blocks[1].GetType().Name}] xoff={blocks[0].XOff}");
        }

        // 列表项内嵌引用(29 / 规范 #294、#295):内层 > 必须成为真引用而非字面量,
        // 且不带 > 的懒续行要并入该内层引用的段落,不能掉出列表或引用容器。
        {
            var blocks = MdParser.Parse("> 1. > Blockquote\ncontinued here.\n");
            var li = blocks.Count > 0 ? blocks[0] as ListItem : null;
            var innerQuote = li?.Sub != null && li.Sub.Count == 1 ? li.Sub[0] as Quote : null;
            string innerText = innerQuote == null ? "" : string.Concat(innerQuote.Inlines.Select(r => r.Text));
            Check("quote/列表项内嵌引用#294",
                blocks.Count == 1 && innerQuote != null && innerText.Replace("\n", " ") == "Blockquote continued here.",
                $"blocks={blocks.Count} inner={(innerQuote == null ? "null" : "Quote")} text=\"{innerText.Replace("\n", "\\n")}\"");
        }
        {
            var blocks = MdParser.Parse("> 1. > Blockquote\n> continued here.\n");
            var li = blocks.Count > 0 ? blocks[0] as ListItem : null;
            var innerQuote = li?.Sub != null && li.Sub.Count == 1 ? li.Sub[0] as Quote : null;
            string innerText = innerQuote == null ? "" : string.Concat(innerQuote.Inlines.Select(r => r.Text));
            Check("quote/列表项内嵌引用#295",
                blocks.Count == 1 && innerQuote != null && innerText.Replace("\n", " ") == "Blockquote continued here.",
                $"blocks={blocks.Count} inner={(innerQuote == null ? "null" : "Quote")} text=\"{innerText.Replace("\n", "\\n")}\"");
        }
        {
            // 能打断段落的行仍须终止列表项,不得被误当作懒续行并入内层引用。
            var blocks = MdParser.Parse("> 1. > Blockquote\n# Heading\n");
            bool ok = blocks.Count == 2 && blocks[0] is ListItem && blocks[1] is Heading;
            Check("quote/列表项内嵌引用后标题不懒续", ok,
                $"types=[{string.Join(",", blocks.Select(b => b.GetType().Name))}]");
        }

        // 引用内链接定义(规范 #220):定义对全文可见,引用壳留空但层级不丢。
        {
            var blocks = MdParser.Parse("[foo]\n\n> [foo]: /url\n");
            var para = blocks.Count > 0 ? blocks[0] as Para : null;
            string href = para?.Inlines.FirstOrDefault(r => r.Link != null)?.Link;
            bool shellKept = blocks.Count == 2 && blocks[1] is Quote;
            Check("refdef/引用内定义对全文可见#220", href == "/url" && shellKept,
                $"href={href ?? "null"} blocks=[{string.Join(",", blocks.Select(b => b.GetType().Name))}]");
        }
        {
            // 保守边界:带标题的引用内定义不认,按正文渲染,宁可少认也不误吞。
            var blocks = MdParser.Parse("[bar]\n\n> [bar]: /url \"t\"\n");
            var para = blocks.Count > 0 ? blocks[0] as Para : null;
            bool noLink = para != null && para.Inlines.All(r => r.Link == null);
            Check("refdef/带标题的引用内定义仍按正文", noLink,
                $"blocks=[{string.Join(",", blocks.Select(b => b.GetType().Name))}]");
        }
        {
            // 引用内定义不得打断上方段落(沿用 #215 约束)。
            var blocks = MdParser.Parse("text\n> [baz]: /url\n");
            bool noDef = !MdParser.Parse("[baz]\n\ntext\n> [baz]: /url\n")
                .OfType<Para>().SelectMany(p => p.Inlines).Any(r => r.Link == "/url");
            Check("refdef/引用内定义不打断段落", noDef, $"blocks={blocks.Count}");
        }

        Console.WriteLine("── KP:emoji 代理对 ──");
        {
            // 假测量器:每 UTF-16 char 宽 10。代理对必须整体成 1 个 Box(宽20),而非两个孤立代理项
            var items = KpCjk.TextToItems("hi 😀 ok", s => s.Length * 10.0);
            int boxes = 0; bool badWidth = false;
            foreach (var it in items)
                if (it.K == KpItem.Kind.Box) { boxes++; if (Math.Abs(it.B.Width - 20.0) > 0.1) badWidth = true; }
            Check("emoji/代理对单Box", boxes == 3 && !badWidth, $"boxes={boxes} badWidth={badWidth}");
        }

        var text = File.ReadAllText(args.Length > 0 ? args[0] : "../engine/test2.md");
        Console.WriteLine($"=== 加载 {text.Length} 字符 ===");

        ArchitectureTests.GraphicsLifetime(Check);
        using var graphics = new RenderResources(1.0f);
        ContainerTests.Run(Check);
        HtmlReadingTests.Run(Check);
        HeadingWrapTests.Run(Check);
        CjkPunctTests.Run(Check);
        ListSpacingTests.Run(Check);
        ListCompletionTests.Run(Check);
        NavigationTests.Run(Check);
        RefreshTests.Run(Check);
        ReaderImageTests.Run(Check);
        ReadingMemoryTests.Run(Check);
        EditorSessionTests.Run(Check);
        SourceColorTests.Run(Check);
        A1ThemeTests.Run(Check);
        RenderStateScope.Run(Check);
        A1FontTests.Run(Check);
        A1LayoutTests.Run(Check);
        A1HeadingSurfaceTests.Run(Check);
        A2ToolbarTests.Run(Check);
        A2ToolbarInteractionTests.Run(Check);
        A2TabNamesTests.Run(Check);
        A2ToolbarLayoutTests.Run(Check);
        A2ToolbarTests.RunIntegration(Check);
        A3OverlayTests.Run(Check);
        FluidColumnTests.Run(Check);
        A4ContentTests.Run(Check);
        A5MatrixTests.Run(Check);
        A5KpTests.Run(Check);
        JustificationTests.Run(Check);
        ReturnHintTests.Run(Check);
        ZenChromeTests.Run(Check);
        DeskDesignTests.Run(Check);
        AppearanceTests.Run(Check);
        PaperTextureTests.Run(Check);
        ReadingStyleTests.Run(Check);
        DeskReadingTests.Run(Check);
        FooterModelTests.Run(Check);
        CordTests.Run(Check);
        DeskToolbarTests.Run(Check);
        DeskFrameTests.Run(Check);
        TitlebarQuietTests.Run(Check);
        DeskFindTests.Run(Check);
        DeskJuanTests.Run(Check);
        DeskEditBarTests.Run(Check);
        DeskLetterTests.Run(Check);
        DeskInkTests.Run(Check);
        DeskHomeTests.Run(Check);
        DeskPaletteTests.Run(Check);
        DeskAppearanceTests.Run(Check);
        NonVisualTests.Run(Check);
        DeviceContractTests.Run(Check);
        ProductCoreTests.Run(Check);
        ProductSearchStorageTests.Run(Check);
        StateCompatibilityTests.Run(Check);
        AppConfigTests.Run(Check);
        ExternalFilePromptTests.Run(Check);
        HtmlAllowlistTests.Run(Check);
        DependencyRulesTests.Run(Check);
        ProductRenderTests.Run(Check);
        using var doc = new DocView();
        doc.DocumentDirectory = "../engine";
        doc.Load(text);
        doc.OnWidth(596f);

        Console.WriteLine($"blocks={doc.BlockCount} totalH={doc.TotalH:0} totalChars={doc.TotalChars}");

        Console.WriteLine("── PosAt 映射 ──");
        var r1 = doc.PosAtText(70f, 95f);
        Console.WriteLine($"  para 起始 (70,165) → block={r1.block} pos={r1.pos}");
        Check("para 起始命中 para 块", r1.pos >= 0 && r1.pos < 10, $"pos={r1.pos}");

        var r2 = doc.PosAtText(300f, 110f);
        Console.WriteLine($"  para 行1 中部 (300,180) → pos={r2.pos}");

        var r3 = doc.PosAtText(300f, 165f);
        Console.WriteLine($"  para 行3 (300,235) → pos={r3.pos}");
        Check("行3 > 行1", r3.pos > r1.pos, $"r1={r1.pos} r3={r3.pos}");

        Console.WriteLine("── 跨行选区+复制 ──");
        // I9 内容驱动:列宽联动后固定 y 命中的块变了(95 落在引用块)——扫描定位含"这是/换行"的段落带
        string copied = null;
        for (float yA = 40f; yA + 170f < doc.TotalH; yA += 8f)
        {
            doc.SelBegin(70f, yA);
            doc.SelExtend(500f, yA + 160f);   // 该段 544 宽下约 5 行,窗口须跨整段才能同时含"这是/换行"
            string cc = doc.CopyText();
            if (!string.IsNullOrEmpty(cc) && cc.Contains("这是") && cc.Contains("换行")) { copied = cc; break; }
        }
        Console.WriteLine($"  跨行复制 len={copied?.Length ?? 0}");
        Check("跨行复制非空", !string.IsNullOrEmpty(copied));
        Check("跨行复制含首尾", (copied ?? "").Contains("这是") && (copied ?? "").Contains("换行"));

        Console.WriteLine("── 跨块选区 ──");
        doc.SelBegin(70f, 95f);
        doc.SelExtend(510f, 307f);
        string guiCopy = doc.CopyText() ?? "";
        Console.WriteLine($"  GUI 拖拽复制 len={guiCopy.Length} tail=[{guiCopy[^Math.Min(20, guiCopy.Length)..]}]");
        doc.SelBegin(70f, 95f);
        // 探测:y 向下扫,找到首个让拷贝含"引用"的终点(引用块在多个标题之下)
        float hitY = -1;
        for (float y = 300f; y < 1000f; y += 10f)
        {
            doc.SelExtend(70f, y);
            string probe = doc.CopyText();
            if ((probe ?? "").Contains("引用文字")) { hitY = y; break; }
        }
        doc.SelExtend(70f, hitY);
        string copied2 = doc.CopyText();
        Console.WriteLine($"  跨块复制 len={copied2?.Length ?? 0} hitY={hitY}");
        Check("跨块复制含首尾", hitY > 0 && (copied2 ?? "").Contains("这是") && (copied2 ?? "").Contains("引用文字"));

        Console.WriteLine("── 格式混合 ──");
        doc.SelBegin(70f, 95f);
        doc.SelExtend(500f, 110f);
        string mixed = doc.CopyText();
        Console.WriteLine($"  混合: {mixed?[..Math.Min(60, mixed?.Length ?? 0)]}");
        Check("混合含加粗", (mixed ?? "").Contains("加粗正文"));
        Check("混合含行内码", (mixed ?? "").Contains("行内代码"));

        Console.WriteLine("── 全选 ──");
        doc.SelAll();
        string all = doc.CopyText();
        Console.WriteLine($"  全选 len={all?.Length ?? 0}");
        Check("全选含标题", (all ?? "").Contains("二级标题"));
        Check("全选含表格", (all ?? "").Contains("WebView2 版"));

        Console.WriteLine("── 表格内精确命中 ──");
        {
            // I9 重调:固定 x 探针在列宽联动(680→544)下脆弱——改内容驱动扫描(QA 命运表:几何组随列宽重录)
            float tblY = -1; string headCopy = ""; float scanLo = -1f, scanHi = -1f;
            for (float y = 0f; y < doc.TotalH; y += 6f)   // A1-B4:表格 y=1122 未变,改的是列 x——colX 26→20 使表格左移 6 DIP,旧 [40,120] 窗口切在"特"字中间;x 探针随之改 ColX 相对
            {
                doc.SelBegin(doc.ColX + 14f, y);
                doc.SelExtend(doc.ColX + 94f, y);
                string pc = (doc.CopyText() ?? "").Replace(" ", "");
                if (pc.Length > 0) { if (scanLo < 0f) scanLo = y; scanHi = y; }
                if (pc.Contains("特性")) { headCopy = pc; tblY = y; break; }
            }
            Check("定位到表格行", tblY > 0, $"y={tblY} totalH={doc.TotalH:0} colW={doc.ColW:0} 文本命中区间=[{scanLo}..{scanHi}]");
            if (tblY > 0)
            {
                Console.WriteLine($"  表头拖选 [{headCopy}]");
                Check("表头部分复制=特性", headCopy.StartsWith("特性") && !headCopy.Contains("Web"), $"[{headCopy}]");
                float dataY = -1; string dataCopy = "";
                for (float y = tblY + 20f; y < tblY + 420f; y += 6f)
                {
                    doc.SelBegin(doc.ColX + 14f, y);
                    doc.SelExtend(doc.ColX + 52f, y);
                    string pc = (doc.CopyText() ?? "").Replace(" ", "").TrimEnd('|');
                    if (pc.Length > 0 && "启动速度".StartsWith(pc) && pc.Contains("启")) { dataY = y; dataCopy = pc; break; }
                }
                Check("定位到数据行", dataY > 0, $"y={dataY}");
                if (dataY > 0)
                {
                    Console.WriteLine($"  数据行部分复制 [{dataCopy}]");
                    Check("数据行部分复制=启动", dataCopy.StartsWith("启动") && dataCopy.Length <= 3, $"[{dataCopy}]");
                    doc.SelBegin(doc.ColX + 14f, dataY);
                    doc.SelExtend(doc.ColX + 234f, dataY);
                    string c3 = doc.CopyText() ?? "";
                    Console.WriteLine($"  跨列复制 [{c3}]");
                    Check("跨列复制含两列", c3.Contains("启动") && c3.Contains("1 秒"), $"[{c3}]");
                }
            }
        }

        Console.WriteLine("── 查找 API ──");
        {
            string st = doc.SearchText();
            int p = st.IndexOf("引用文字", StringComparison.OrdinalIgnoreCase);
            Check("查找/SearchText 含目标", p >= 0, $"pos={p}");
            if (p >= 0)
            {
                doc.SelectRange(p, p + 4);
                string c = doc.CopyText();
                Check("查找/选区回读一致", c == "引用文字", $"[{c}]");
            }
            int p2 = st.IndexOf("二级标题", StringComparison.OrdinalIgnoreCase);
            if (p2 >= 0)
            {
                doc.SelectRange(p2, p2 + 4);
                float y = doc.YAtOffset(p2);
                Check("查找/YAtOffset 合理", y > 0 && y < doc.TotalH, $"y={y:0} totalH={doc.TotalH:0}");
            }
        }

        Console.WriteLine("── 语法高亮分词 ──");
        {
            var toks = CodeHighlight.Tokenize("int x = 42; // note\nstring s = \"hi\";", "csharp");
            bool kw = toks.Any(t => t.kind == 1 && t.len == 3);                       // int
            bool num = toks.Any(t => t.kind == 4);                                    // 42
            bool com = toks.Any(t => t.kind == 3 && t.len > 6);                       // // note
            bool str = toks.Any(t => t.kind == 2 && t.len == 4);                      // "hi"
            Check("高亮/C# 关键字", kw, $"n={toks.Count}");
            Check("高亮/数字", num, "");
            Check("高亮/注释", com, "");
            Check("高亮/字符串", str, "");
            Check("高亮/未知语言不着色", CodeHighlight.Tokenize("int x;", "brainfuck").Count == 0, "");
            var py = CodeHighlight.Tokenize("a = b // c", "python");
            Check("高亮/Python无行注释", !py.Any(t => t.kind == 3), "");
            Check("高亮/大小写别名", CodeHighlight.Known("CSharp") && CodeHighlight.Known("golang"), "");
        }

        Console.WriteLine("── 渲染完整性(每块必须有可见输出) ──");
        {
            foreach (string f in new[] { "../samples/全格式测试.md", "../engine/test2.md", "../samples/edge_torture.md", "../samples/edge_empty.md", "../samples/edge_oneline.md", "../samples/edge_noeol.md" })
            {
                if (!File.Exists(f)) { Check($"渲染/{System.IO.Path.GetFileName(f)} 存在", false, "文件缺失"); continue; }
                using var d3 = new DocView();
                d3.Load(File.ReadAllText(f));
                d3.OnWidth(596f);
                int blanks = 0;
                for (int i = 0; i < d3.BlockCount; i++)
                {
                    if (!d3.BlockDrawable(i))
                    {
                        blanks++;
                        var bb = d3.Blocks[i];
                        string hint = bb is MDViewerNative.CodeBlock cb ? "lang=" + cb.Lang : bb.GetType().Name;
                        Console.WriteLine($"    不可绘制 #{i}: {hint}");
                    }
                }
                Check($"渲染/{System.IO.Path.GetFileName(f)} 全部可绘制", blanks == 0, $"blocks={d3.BlockCount} blank={blanks}");
            }

            // 合成文档:每种块型 + 带行内样式的 KP 长段
            string kitchen =
                "# 标题一\n\n## 标题二带样式 **粗**\n\n### 三级 *斜体* `码`\n\n#### 四级 ~~删除~~\n\n" +
                "> 引用一段 **粗体** [链接](https://example.com)\n>\n> > 嵌套引用\n\n" +
                "- 无序列表项\n- 第二项 `行内码`\n\n1. 有序一\n2. 有序二\n\n" +
                "- [ ] 任务未完成\n- [x] 任务已完成\n\n" +
                "```csharp\nint code = 42; // 代码块\n```\n\n" +
                "| 列A | 列B |\n|:---|---:|\n| 左对齐 | 右对齐 |\n\n" +
                "---\n\n![图片](test_nonexistent.png)\n\n";
            string kpPara = "这是一个超过两百字的长段落,内含 **加粗**、*斜体*、`行内代码`、[链接文字](https://example.com/x) 以及 English words 与 123 数字。" +
                            "第二句继续拉长文本以跨越多行断行,包含中文标点。英语与中文混排的断行算法需要处理不同字宽的组合," +
                            "Knuth-Plass 算法会为整段寻找全局最优的断行位置,而不是逐行贪心。第三句再次强调这段必须超过两百个字符长度。";
            using var d4 = new DocView();
            d4.Load(kitchen + kpPara + "\n");
            d4.OnWidth(596f);
            int blanks2 = 0;
            for (int i = 0; i < d4.BlockCount; i++)
                if (!d4.BlockDrawable(i)) blanks2++;
            Check("渲染/合成全格式 全部可绘制", blanks2 == 0, $"blocks={d4.BlockCount} blank={blanks2}");
        }

        Console.WriteLine("── HTML 导出 ──");
        {
            string html = HtmlExport.Build(doc.Blocks, "测试", dark: false);
            Check("导出/含标题", html.Contains("<h1 id=\""), $"len={html.Length}");
            Check("导出/含表格", html.Contains("<table"), "");
            Check("导出/含引用", html.Contains("<blockquote"), "");
            Check("导出/含代码", html.Contains("<pre><code>"), "");
            Check("导出/含转义", html.Contains("&amp;") || !html.Contains(" & "), "");
            Check("导出/结尾规范", html.TrimEnd().EndsWith("</html>"), "");
        }

        Console.WriteLine("── EditorKit 编辑逻辑 ──");
        Check("编辑/无序续行", EditorKit.EnterContinuation("- 项目") == "\r\n- ");
        Check("编辑/有序续行+1", EditorKit.EnterContinuation("3. 第三") == "\r\n4. ");
        Check("编辑/空项退出", EditorKit.EnterContinuation("- ") == "");
        Check("编辑/普通行默认", EditorKit.EnterContinuation("普通段落") == null);
        Check("编辑/块缩进", EditorKit.IndentBlock("a\nb") == "  a\n  b");
        Check("编辑/块反缩进", EditorKit.DedentBlock("  a\n    b") == "a\n  b");

        Console.WriteLine("── TOC 与搜索命中表 ──");
        {
            int headings = 0;
            foreach (var (bi, lv, txt, ey) in doc.TocEntries)
            {
                headings++;
                if (lv < 1 || lv > 6 || ey <= 0) { Check("TOC/条目合法性", false, $"lv={lv} y={ey}"); break; }
            }
            Check("TOC/条目数量", headings >= 3, $"entries={headings}");
            // 层级顺序:首条应是文档第一个标题
            if (doc.TocEntries.Count > 0) Check("TOC/首条为文档前部", doc.TocEntries[0].blockIdx <= 2, $"blk={doc.TocEntries[0].blockIdx}");

            int idx1 = doc.SetSearchQuery("标题", 0, back: false);
            Check("搜索/命中表非空", idx1 >= 0 && doc.SearchHits.Count >= 2, $"n={doc.SearchHits.Count} idx={idx1}");
            doc.SelectRange(doc.SearchHits[idx1].start, doc.SearchHits[idx1].start + doc.SearchHits[idx1].len);
            string c1 = doc.CopyText() ?? "";
            Check("搜索/当前命中复制一致", c1.Contains("标题"), $"[{c1}]");
            int idx2 = doc.SetSearchQuery("标题", doc.SelTail, back: false);
            Check("搜索/前进推进", doc.SearchHits.Count > 1 && idx2 == (idx1 + 1) % doc.SearchHits.Count, $"idx={idx2}");
            int idx3 = doc.SetSearchQuery("标题", doc.SelHead, back: true);
            Check("搜索/后退环绕至末尾", idx3 == doc.SearchHits.Count - 1, $"idx={idx3}");
            int idxNone = doc.SetSearchQuery("不存在的词xyzzy", 0, back: false);
            Check("搜索/无匹配", idxNone < 0 && doc.SearchHits.Count == 0, "");
            doc.SearchHits.Clear();
        }

        Console.WriteLine("── KP 长段落(>200字) ──");
        {
            string longPara = "这是一个超过两百字的长段落测试用例,用来验证 Knuth-Plass 断行路径的渲染、选区和命中测试。" +
                              "中文与 English 混排,加上 123 数字和一些标点。这一段会走 KP 分支,每一行单独布局," +
                              "选区高亮必须逐行裁剪,点击命中必须映射回正确的字符偏移,复制必须与原文逐字一致。再加一句凑足两百字以上。";
            using var doc2 = new DocView();
            doc2.Load(longPara + "\n");
            doc2.OnWidth(596f);
            string st2 = doc2.SearchText();
            var p1 = doc2.PosAtText(150f, 95f);
            var p2 = doc2.PosAtText(500f, 155f);
            Check("KP/命中非段首(逐行生效)", p1.block == 0 && p1.pos > 2, $"p1={p1.pos}");
            Check("KP/第二行命中更靠后", p2.pos > p1.pos, $"p2={p2.pos}");
            doc2.SelBegin(150f, 95f);
            doc2.SelExtend(500f, 155f);
            string cp = doc2.CopyText() ?? "";
            string expect = p2.pos <= p1.pos ? "" : st2[p1.pos..p2.pos];
            Check("KP/选区复制=原文切片", cp == expect, $"len={cp.Length}/{expect.Length}");
        }

        Console.WriteLine("── CommonMark:实体解码 ──");
        {
            var eDoc = MdParser.Parse("A &amp; B &copy; &#169; &#xA9; `&amp;` \\&amp; end");
            var ePara = (Para)eDoc[0];
            string eT = "";
            bool eCodeLit = false;
            foreach (var r in ePara.Inlines)
            {
                eT += r.Text;
                if (r.Code && r.Text.Contains("&amp;")) eCodeLit = true;
            }
            Check("实体/命名+数字解码", eT.Contains("A & B © © ©"), eT);
            Check("实体/代码span字面", eCodeLit, eT);
            Check("实体/转义不解码", eT.Contains("&amp; end"), eT);
            var eDoc2 = MdParser.Parse("&nosuch; &lt;");
            var ePara2 = (Para)eDoc2[0];
            string eT2 = "";
            foreach (var r in ePara2.Inlines) eT2 += r.Text;
            Check("实体/未知保留", eT2.Contains("&nosuch;") && eT2.Contains("<"), eT2);
        }

        Console.WriteLine("── CommonMark:链接标题 ──");
        {
            var lkDoc = MdParser.Parse("[a](http://x.com/a_(b) \"The Title\")");
            var lkPara = (Para)lkDoc[0];
            string lkUrl = null, lkTxt = "";
            foreach (var r in lkPara.Inlines) { lkTxt += r.Text; if (r.Link != null) lkUrl = r.Link; }
            Check("链接/括号URL+标题剥离", lkUrl == "http://x.com/a_(b)" && lkTxt.TrimEnd('\n') == "a", $"url={lkUrl} txt={lkTxt}");

            var lkDoc2 = MdParser.Parse("[click][ref]\n\n[ref]: http://y.com\n    \"Ref Title\"\n\nbody");
            bool lkHas2 = false, lkStray = false;
            foreach (var b in lkDoc2)
                if (b is Para lkPp)
                    foreach (var r in lkPp.Inlines)
                    {
                        if (r.Link == "http://y.com") lkHas2 = true;
                        if (r.Text.Contains("Ref Title")) lkStray = true;
                    }
            Check("链接/次行标题定义", lkHas2 && !lkStray, "runs=[" + string.Join("|", lkDoc2.SelectMany(b2 => b2 is Para lp2 ? lp2.Inlines : new List<Run>()).Select(r4 => r4.Text + "→" + (r4.Link ?? "-") + "+" + (r4.LinkTitle ?? "-"))) + "]");

            var lkDoc3 = MdParser.Parse("[r2][ref2]\n\n[ref2]: http://z.com \"T2\"\n\nx");
            bool lkHas3 = false;
            foreach (var b in lkDoc3)
                if (b is Para lkPp3)
                    foreach (var r in lkPp3.Inlines) if (r.Link == "http://z.com") lkHas3 = true;
            Check("链接/同侧行标题定义", lkHas3, "");

            var lkDoc4 = MdParser.Parse("![x](a(b).png)");
            bool lkImg = lkDoc4[0] is Image && ((Image)lkDoc4[0]).Src == "a(b).png";
            Check("链接/图片括号src块级", lkImg, $"src={(lkDoc4[0] as Image)?.Src}");

            var lkDoc5 = MdParser.Parse("text ![x](a(b).png) tail");
            var lkPara5 = (Para)lkDoc5[0];
            string lkSrc5 = null;
            foreach (var r in lkPara5.Inlines) if (r.Link != null) lkSrc5 = r.Link;
            Check("链接/图片括号src内联", lkSrc5 == "a(b).png", $"src={lkSrc5}");
        }

        Console.WriteLine("── CommonMark:四空格缩进代码块 ──");
        {
            var icDoc = MdParser.Parse("para\n\n    code1\n    code2\n\nafter");
            bool icOk = icDoc.Count >= 3 && icDoc[1] is CodeBlock icCb && icCb.Text.Contains("code1") && icCb.Text.Contains("code2");
            Check("缩进码/基本", icOk, $"n={icDoc.Count} t1={(icDoc.Count > 1 ? icDoc[1].GetType().Name : "-")}");
            Check("缩进码/后文段落还原", icDoc[^1] is Para, $"t={icDoc[^1].GetType().Name}");

            var icDoc2 = MdParser.Parse("- item\n    cont line\n");
            bool icCont = icDoc2.Count == 1 && icDoc2[0] is ListItem icLi2 && icLi2.Inlines.Any(r => r.Text.Contains("cont line"));
            Check("缩进码/列表续行优先", icCont, $"n={icDoc2.Count} t0={icDoc2[0].GetType().Name}");

            var icDoc3 = MdParser.Parse("- a\n\n    code-ish\n");
            // 规范 #108:项空行后 4 空格(=内容列)属项内段落(Sub);T1.1 后不再并入 Inlines
            bool icGuard = icDoc3.Count == 1 && icDoc3[0] is ListItem icLi3 && icLi3.Sub != null
                           && icLi3.Sub.Any(b => b is Para ip3 && ip3.Inlines.Any(r => r.Text.Contains("code-ish")));
            Check("缩进码/列表项内子段", icGuard, $"n={icDoc3.Count}");

            var icDoc4 = MdParser.Parse("text line\n    indented cont\n");
            Check("缩进码/段内续行不当代码", icDoc4.Count == 1 && icDoc4[0] is Para, $"n={icDoc4.Count}");

            var icDoc5 = MdParser.Parse("> para\n>\n>     code in quote\n");
            bool icQ = icDoc5.Count == 2 && icDoc5[1] is CodeBlock icCb5 && icCb5.XOff == 20;
            Check("缩进码/引用内", icQ, $"n={icDoc5.Count}");
        }

        Console.WriteLine("── CommonMark:松散/紧凑列表 ──");
        {
            var lsLoose = MdParser.Parse("- a\n\n- b\n");
            var lsTight = MdParser.Parse("- a\n- b\n");
            Check("松散列表/项间空行", lsLoose.Count == 2 && lsLoose[0] is ListItem lsA && lsA.Loose && lsLoose[1] is ListItem lsB && lsB.Loose, $"n={lsLoose.Count}");
            Check("松散列表/紧凑默认", lsTight.Count == 2 && lsTight[0] is ListItem lsC && !lsC.Loose && lsTight[1] is ListItem lsD && !lsD.Loose, "");
            var lsNl = MdParser.Parse("- a\n\ntext not list\n");
            Check("松散列表/列表终结不打标", lsNl.Count == 2 && lsNl[0] is ListItem lsE && !lsE.Loose, $"n={lsNl.Count}");
            var lsOl = MdParser.Parse("1. x\n\n2. y\n");
            Check("松散列表/有序同样生效", lsOl.Count == 2 && lsOl[0] is ListItem lsF && lsF.Loose, "");
        }

        Console.WriteLine("── CommonMark:空列表项 ──");
        {
            var eiDoc = MdParser.Parse("- foo\n-\n- bar\n");
            bool ei3 = eiDoc.Count == 3 && eiDoc.All(b => b is ListItem)
                && ((ListItem)eiDoc[1]).Inlines.Count == 0;
            Check("空列表项/裸marker成空项", ei3, $"n={eiDoc.Count}");
            var eiBlank = MdParser.Parse("-\n\n  foo\n");
            bool eiOut = eiBlank.Count == 2 && eiBlank[0] is ListItem eiLi && eiLi.Inlines.Count == 0 && eiBlank[1] is Para;
            Check("空列表项/空项后空行终止", eiOut, $"n={eiBlank.Count} t0={eiBlank[0].GetType().Name} t1={eiBlank[1].GetType().Name}");
            string eiH = SpecNorm(SpecRender("- foo\n-\n- bar\n"));
            Check("空列表项/导出空li", eiH == "<ul><li>foo</li><li></li><li>bar</li></ul>", eiH);
            string eiL = SpecNorm(SpecRender("* a\n*\n\n* c\n"));
            Check("空列表项/loose空项无空p", eiL == "<ul><li><p>a</p></li><li></li><li><p>c</p></li></ul>", eiL);
            string eiF = SpecNorm(SpecRender("-\n  foo\n-\n  ```\n  bar\n  ```\n-\n      baz\n"));
            Check("空列表项/项内围栏与缩进码", eiF == "<ul><li>foo</li><li><pre><code>bar</code></pre></li><li><pre><code>baz</code></pre></li></ul>", eiF);
            string eiS = SpecNorm(SpecRender("-   \n  foo\n"));
            Check("空列表项/续行首无前导空格", eiS == "<ul><li>foo</li></ul>", eiS);
            using var eiV = new DocView();
            eiV.Load("- 甲\n-\n- 乙\n");
            eiV.OnWidth(596f);
            bool eiVd = eiV.BlockCount == 3 && !Enumerable.Range(0, 3).Any(ix => !eiV.BlockDrawable(ix));
            Check("空列表项/渲染可绘制", eiVd, $"n={eiV.BlockCount}");
        }

        Console.WriteLine("── CommonMark:机械长尾 ──");
        {
            // #61:项内容本身是分隔线 → <li><hr></li>(HRule 进 Sub)
            var hrLi = MdParser.Parse("- Foo\n- * * *\n");
            Check("长尾/项内hr解析", hrLi.Count == 2 && hrLi[1] is ListItem hrIt && hrIt.Sub?.Count == 1 && hrIt.Sub[0] is HRule, $"n={hrLi.Count}");
            Check("长尾/项内hr导出", SpecNorm(SpecRender("- Foo\n- * * *\n")) == "<ul><li>Foo</li><li><hr></li></ul>", "");
            using var hrV = new DocView();
            hrV.Load("- 甲\n- * * *\n");
            hrV.OnWidth(596f);
            Check("长尾/项内hr渲染", hrV.BlockCount == 2 && hrV.BlockDrawable(1), $"n={hrV.BlockCount}");
            // #306:有序列表打断段落仅限起始号 1
            var noInt = MdParser.Parse("The number of windows in my house is\n14.  The number of doors is 6.\n");
            Check("长尾/有序不打断段落", noInt.Count == 1 && noInt[0] is Para, $"n={noInt.Count}");
            var yesInt = MdParser.Parse("para\n1. first\n");
            Check("长尾/1仍打断段落", yesInt.Count == 2 && yesInt[1] is ListItem, $"n={yesInt.Count}");
            // #210:label 跨行的引用定义(未命中整组回退正文)
            string multilineRef = SpecRender("[\nfoo\n]: /url\n\n[foo]\n");
            Check("长尾/定义label跨行", multilineRef.Contains("href=\"/url\""), multilineRef);
            var mlFallback = MdParser.Parse("[\nfoo\n: not a def\nbar\n");
            Check("长尾/label跨行失败回退", mlFallback.Count >= 1 && mlFallback[0] is Para && !SpecRender("[\nfoo\n: not a def\nbar\n\n[foo]\n").Contains("href="), $"n={mlFallback.Count}");
            // #542:Unicode 全大小写折叠(ẞ 与 SS 等价)
            string cf = SpecNorm(SpecRender("[ẞ]\n\n[SS]: /url\n"));
            Check("长尾/unicode大小写折叠", cf == "<p><a href=\"/url\">ẞ</a></p>", cf);
        }

        Console.WriteLine("── CommonMark:引用长尾 ──");
        {
            // #241/242:仅 > 行发空引用(外壳保留,不发空段落)
            string eq = SpecNorm(SpecRender(">\n"));
            Check("引用长尾/空引用", eq == "<blockquote></blockquote>", eq);
            // #244/254:空行分断相邻引用(QSeq),各自成块
            string qs = SpecNorm(SpecRender("> foo\n\n> bar\n"));
            Check("引用长尾/空行分断相邻引用", qs == "<blockquote><p>foo</p></blockquote><blockquote><p>bar</p></blockquote>", qs);
            // #249:懒续行并入开放段落
            string lz = SpecNorm(SpecRender("> bar\nbaz\n"));
            Check("引用长尾/懒续行", lz == "<blockquote><p>bar baz</p></blockquote>", lz);
            // #236:能打断段落的行(---)不懒续,引用终止
            string hb = SpecNorm(SpecRender("> foo\n---\n"));
            Check("引用长尾/懒续不打断段落", hb == "<blockquote><p>foo</p></blockquote><hr>", hb);
            // #252:深层引用的跨层懒续
            string dp = SpecNorm(SpecRender("> > > foo\nbar\n"));
            Check("引用长尾/深引用懒续", dp == "<blockquote><blockquote><blockquote><p>foo bar</p></blockquote></blockquote></blockquote>", dp);
            // #233:4+ 空格缩进的 > 是缩进码不是引用
            string iq = SpecNorm(SpecRender("    > # Foo\n    > bar\n    > baz\n"));
            Check("引用长尾/缩进引用是代码", iq == "<pre><code>&gt; # Foo\n&gt; bar\n&gt; baz</code></pre>", iq);
            // viewer 空引用可绘制、命中扫描不炸
            using var eqV = new DocView();
            eqV.Load(">\n");
            eqV.OnWidth(596f);
            bool eqOk = eqV.BlockCount == 1 && eqV.BlockDrawable(0);
            for (float y = 0f; y < eqV.TotalH; y += 8f) _ = eqV.PosAtText(100f, y);
            Check("引用长尾/空引用渲染", eqOk, $"n={eqV.BlockCount} draw={eqV.BlockDrawable(0)}");
        }

        Console.WriteLine("── 排版:KP 中文断行 ──");
        {
            // >200 字中文段:KP 逐字断点胶生效,段落切成多行;禁则:行首无闭合标点、行尾无开放标点
            // A1-B4:v2.2 §3.3 的禁则按 GB/T 15834 全角标点定义(KpCjk.IsCjkPunct/IsCjkOpen 同为全角表)。
            // 旧 fixture 用半角逗号,断行落点从未撞上逗号=靠运气通过;改用全角标点后,断言检的是规范定义的行为。
            // 半角逗号在中文语境不被禁则绑定(断点可落在 , 之前)是**引擎既有缺口**,非本批引入:B4 未触碰 KpCjk;
            // 证据与复现见 work/a1-b4-kp-ascii-comma.txt,已登记 A2 backlog(禁则表扩半角/中西标点口径)。
            string body = "中文排版测试段落，包含标点与较长的句子用来验证禁则和悬挂，第三句继续加长内容使得必然发生断行，「括引内容也足够长才能跨行」，结尾句号。";
            string body2 = body + body.Replace("第一", "第四") + body.Replace("第一", "第七");
            using var d2 = new DocView();
            d2.Load(body2 + "\n");
            d2.OnWidth(596f);
            for (float y = 0f; y < d2.TotalH; y += 8f) _ = d2.PosAtText(100f, y);
            var cacheField = typeof(DocView).GetField("_layoutCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cacheObject = cacheField?.GetValue(d2);
            var entriesField = cacheObject?.GetType().GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var kcache = (System.Collections.IDictionary)entriesField?.GetValue(cacheObject);
            System.Collections.Generic.List<string> lines = null;
            foreach (var v in kcache.Values)
            {
                var f = v?.GetType().GetField("KpLines");
                if (f != null) { lines = f.GetValue(v) as System.Collections.Generic.List<string>; break; }
            }
            bool multi = lines != null && lines.Count >= 3;
            bool kin = true;
            string closeSet = "。,;:!?)」』、】》";
            string openSet = "「『(《【";
            var badLines = new System.Collections.Generic.List<string>();
            foreach (var ln in lines ?? new System.Collections.Generic.List<string>())
            {
                if (ln.Length == 0) continue;
                if (closeSet.Contains(ln[0]) || openSet.Contains(ln[^1])) { kin = false; badLines.Add(ln); }
            }
            // 选区/复制链一致:全文文本 = 各 KP 行拼接(去除换行差异由 BlockTextOf 保证)
            string st3 = d2.SearchText();
            Check("排版KP/中文多行切分", multi, $"lines={lines?.Count ?? -1}");
            string kinBad = kin ? "" : $"违规 {badLines.Count} 行: " + string.Join(" ⋄ ", badLines);
            string kinLines = kin ? "" : " ;码点 " + string.Join(" ", (lines ?? new System.Collections.Generic.List<string>()).Where(x => x.Length > 0).Select(x => $"{(int)x[0]:X4}/{(int)x[^1]:X4}"))
                                  + " ;全文行 " + string.Join("|", lines ?? new System.Collections.Generic.List<string>());
            Check("排版KP/禁则", kin, kinBad + kinLines);
            Check("排版KP/复制全文一致", st3.Contains("中文排版测试段落") && st3.Contains("结尾句号。"), $"len={st3.Length}");
            // I6 前置(JUST 往返护栏,须在 JUST 落地前先绿):x→offset→y 映射必须落回同一行(±1 行高)。
            // JUST 拉伸行宽后 HitTestPoint 语义若漂移(如拉伸缝归属错行),此断言即红。
            // I6 前置(JUST 往返护栏,须在 JUST 落地前先绿):x→offset→y 映射落回 ±1.5 行内。
            // ±45=1.5 行高容差:行尾空白/块顶 padding 的"最近行钳制"是既有语义(实现评审预告,不当 bug 修);
            // JUST 若致映射漂移 ≥2 行,此断言即红。
            bool rt = true; int rtBad = 0;
            for (float y = WindowFit.PageTop - 8f; y < MathF.Min(d2.TotalH - 40f, 420f); y += 12f)   // 避开块底 margin(钳回末行是既有语义,非行映射保证区)
                foreach (float x in new[] { 80f, 250f, 450f, 640f })
                {
                    var (_, pos) = d2.PosAtText(x, y);
                    float yb = d2.YAtOffset(pos);
                    if (yb < y - 45f || yb > y + 45f) { rt = false; rtBad++; if (rtBad <= 6) Console.WriteLine($"  rt viol: x={x} y={y} pos={pos} yb={yb:F1} totalH={d2.TotalH:F0}"); }
                }
            Check("排版KP/JUST往返", rt, $"violations={rtBad}");
        }

        Console.WriteLine("── I4 禁则结构断言(弯引号入表/双字符 Box/prevC 回写) ──");
        {
            static List<KpItem> Items(string s, List<int> chars = null) => KpCjk.TextToItems(s, t => t.Length * 10.0, chars);
            // —— 双字符不可拆:整对单 Box,itemChars 记 2
            var ch1 = new List<int>();
            var it1 = Items("好——好", ch1);
            Check("禁则/破折号双字Box", ch1.Contains(2) && it1.Count(it => it.K == KpItem.Kind.Box) == 3, $"chars={string.Join(",", ch1)} items={it1.Count}");
            var ch2 = new List<int>();
            var it2 = Items("好……好", ch2);
            Check("禁则/省略号双字Box", ch2.Contains(2) && it2.Count(it => it.K == KpItem.Kind.Box) == 3, $"chars={string.Join(",", ch2)} items={it2.Count}");
            // 开放弯引号绑定后字:"说“好" → Box Glue Box Box(“ 与 好 之间无胶=不可断)
            var it3 = Items("说“好");
            Check("禁则/开引号绑后字", it3.Count >= 4 && it3[2].K == KpItem.Kind.Box && it3[3].K == KpItem.Kind.Box,
                  string.Join(",", it3.ConvertAll(x => x.K.ToString()[0])));
            // 闭合弯引号绑定前字:"好”再" → Box Box Glue Box(好与 ” 之间无胶)
            var it4 = Items("好”再");
            Check("禁则/闭引号绑前字", it4.Count >= 4 && it4[0].K == KpItem.Kind.Box && it4[1].K == KpItem.Kind.Box && it4[2].K == KpItem.Kind.Glue,
                  string.Join(",", it4.ConvertAll(x => x.K.ToString()[0])));
            // 闭合标点悬挂:” 的 Box 带 Protrude(弯引号此前在 Other 分支无悬挂)
            var it5 = Items("好”");
            bool prot = false;
            foreach (var x in it5) if (x.K == KpItem.Kind.Box && x.B.Protrude > 0) prot = true;
            Check("禁则/弯引号可悬挂", prot, "” Box 应带 Protrude");
            // 自用走查:中文里夹半角收尾标点("手,选")曾在逗号前断行,行首出现 ","。半角 , . ; : ! ? ) ] } % 同样不得居行首,其后可断
            static string Kinds(List<KpItem> v) => string.Join(",", v.ConvertAll(x => x.K.ToString()[0]));
            // S75:段内硬换行 = 强制断点(前置无限伸展胶,该行不两端对齐);曾被当普通空白胶,DirectWrite 在切片内再折一次。
            var itHb = Items("好好\n好");
            int forced = itHb.Count(x => x.K == KpItem.Kind.Penalty && x.P.Value <= -10000);
            Check("禁则/硬换行为强制断点", forced == 2 && itHb.Count(x => x.K == KpItem.Kind.Glue && x.G.Width > 0) == 0, Kinds(itHb));
            var it6 = Items("手,选");
            Check("禁则/半角逗号绑前字", it6.Count >= 5 && it6[0].K == KpItem.Kind.Box && it6[1].K == KpItem.Kind.Box && it6[2].K == KpItem.Kind.Glue, Kinds(it6));
            var it7 = Items("注)再");
            Check("禁则/半角右括号绑前字", it7.Count >= 4 && it7[1].K == KpItem.Kind.Box && it7[2].K == KpItem.Kind.Glue, Kinds(it7));
            var it8 = Items("件.md");
            Check("禁则/半角句点绑前字", it8.Count(x => x.K == KpItem.Kind.Glue) == 1, Kinds(it8));   // 仅段末胶
            var it9 = Items("a,b");
            Check("禁则/西文逗号不变", it9.Count(x => x.K == KpItem.Kind.Glue) == 1, Kinds(it9));
        }

        Console.WriteLine("── I7 命令表断言(单一真相源契约) ──");
        {
            Check("命令表/结构不变量", Commands.InvariantViolation() == null, Commands.InvariantViolation() ?? "ok");
            // Registered shortcuts must exactly match the reviewed ACCEL contract, including history navigation.
            // 反向:golden 加速键除窗口操作(Ctrl+Tab ×2,不进菜单只进速查表)外必须全部在表内曝光
            var goldenAccel = new HashSet<int>();
            foreach (var ln in File.ReadAllLines("menu_golden.txt"))
            {
                int ci = ln.IndexOf("cmd=", StringComparison.Ordinal);
                if (ci >= 0 && int.TryParse(ln.AsSpan(ci + 4).Trim(), out int cid)) goldenAccel.Add(cid);
            }
            var tableAccel = new HashSet<int>();
            foreach (var c in Commands.All) if (c.Accel.Length > 0 && (c.Id < 2000 || c.Id is 2003 or 2098 or 2099 or 2200 or 2205 or 2220 or 2270)) tableAccel.Add(c.Id);   // 1000 段=ACCEL 表契约;2000 段的 Ctrl+C/A 是显示文本非注册加速键
            bool noNew = true; foreach (int id in tableAccel) if (!goldenAccel.Contains(id)) noNew = false;
            var windowOnly = new HashSet<int> { 1009, 1010 };
            bool allExposed = true; foreach (int id in goldenAccel) if (!tableAccel.Contains(id) && !windowOnly.Contains(id)) allExposed = false;
            Check("命令表/批准快捷键与曝光一致", noNew && allExposed, $"golden={goldenAccel.Count} table={tableAccel.Count}");
            // [MENU] 全文案+分隔线契约(v2,B1/backlog#2):此前只比 id 序不比文本、[ContextEdit] 段无断言。
            // 期望序=在测试内镜像 builder 布局规则(只镜像"哪里插线",内容/文案仍来自 Commands 表——两源对拍,规则漂移即红)
            static List<string> GoldenSection(string[] lines, string section)
            {
                var v = new List<string>();
                bool inSec = false;
                foreach (var raw in lines)
                {
                    string ln = raw.Trim();
                    if (ln.StartsWith("[")) { if (inSec) break; inSec = ln.StartsWith(section); continue; }
                    if (!inSec || ln.Length == 0 || ln.StartsWith("#")) continue;
                    v.Add(ln == "---" ? "SEP" : ln);
                }
                return v;
            }
            static bool SeqEq(List<string> a, List<string> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
                return true;
            }
            var gLines = File.ReadAllLines("menu_golden.txt");
            // ShowContextMenu 预览面:`if (c.Id is 2001 or 1011) SEP` 前插
            var cvSim = new List<string>();
            foreach (var c in Commands.OnFace(CmdFace.ContextView))
            {
                if (c.Id is 2001 or 1011) cvSim.Add("SEP");
                cvSim.Add(c.Id + "|" + c.MenuText);
            }
            Check("命令表/右键预览全文案对拍", SeqEq(GoldenSection(gLines, "[ContextView]"), cvSim), string.Join(" ⋄ ", cvSim));
            // ShowContextMenu 编辑面:≥2000 遍(2004/2008 前插线)→ SEP → <2000 遍(两遍循环序≠声明序,旧注释预警的"直接对拍必假红"由此消解)
            var ceSim = new List<string>();
            foreach (var c in Commands.OnFace(CmdFace.ContextEdit))
                if (c.Id >= 2000) { if (c.Id is 2004 or 2008) ceSim.Add("SEP"); ceSim.Add(c.Id + "|" + c.MenuText); }
            ceSim.Add("SEP");
            foreach (var c in Commands.OnFace(CmdFace.ContextEdit))
                if (c.Id < 2000) ceSim.Add(c.Id + "|" + c.MenuText);
            Check("命令表/右键编辑全文案对拍", SeqEq(GoldenSection(gLines, "[ContextEdit]"), ceSim), string.Join(" ⋄ ", ceSim));
            // ShowMoreMenu:1014/1004/1011/2093 前插线;1004 契约用合并占位(运行时二选一)
            var moSim = new List<string>();
            foreach (var c in Commands.OnFace(CmdFace.More))
            {
                if (c.Id is 1014 or 1004 or 1011 or 2093) moSim.Add("SEP");
                string label = c.Id == 1004 ? "切换到深色/浅色主题" : c.Label;
                moSim.Add(c.Id + "|" + (c.Accel.Length > 0 ? label + "\t" + c.Accel : label));
            }
            Check("命令表/更多菜单全文案对拍", SeqEq(GoldenSection(gLines, "[More]"), moSim), string.Join(" ⋄ ", moSim));
            // 1004 运行时二选一执法:builder 源码必须同时含两个变体(漏一个=暗态文案谎报)
            string progSrc = File.ReadAllText("../engine/Program.cs");
            Check("命令表/主题标签双变体在源", progSrc.Contains("切换到浅色主题") && progSrc.Contains("切换到深色主题"));
            // 谓词真值点(状态快照回归:这些单元格翻转=菜单口径漂移)
            var s0 = new CmdState(false, false, false, false, false, 16f);
            var sD = new CmdState(false, true, false, false, true, 16f);
            var sE = new CmdState(true, false, false, false, false, 28f);
            var s12 = new CmdState(false, false, false, false, false, 12f);
            CmdDef Find(int id) { foreach (var c in Commands.All) if (c.Id == id) return c; return null; }
            Check("命令表/保存谓词", !Find(1001).IsEnabled(s0) && Find(1001).IsEnabled(sD) && Find(1001).IsEnabled(sE), "预览无改动态灰,脏/编辑可用");
            Check("命令表/替换仅编辑", !Find(1013).IsEnabled(sD) && Find(1013).IsEnabled(sE), "E0.3 预览灰显");
            Check("命令表/字号边界", !Find(1014).IsEnabled(sE) && !Find(1015).IsEnabled(s12) && Find(1016).IsChecked(s0) && !Find(1016).IsChecked(sE), "28 顶灰放大/12 底灰缩小/16 打勾");
            Check("命令表/选区查找依赖 HasSel", !Find(2094).IsEnabled(s0) && Find(2094).IsEnabled(sD), "无选区灰");
            Check("命令表/目录打勾=TOC 开", Find(1006).IsChecked(new CmdState(false, false, true, false, false, 16f)) && !Find(1006).IsChecked(s0), "");
            var composing = new CmdState(false, false, false, false, false, 16f, Composing: true);
            Check("命令表/门禁由命令声明", Find(2206).RequiresStableText && !Find(2206).IsEnabled(composing) && !Find(2206).RequiresWritable, "标签操作保留组合输入门禁,但不伪装成写操作");
            Check("命令表/未知命令不继承区间门禁", !Commands.RequiresStableText(9999) && !Commands.RequiresWritable(9999), "未声明命令默认无门禁");
            var readOnly = new CmdState(false, false, false, false, false, 16f, ReadOnly: true);
            Check("命令表/只读会话拒绝进入编辑", Find(1002).RequiresWritable && !Find(1002).IsEnabled(readOnly) && Find(1002).IsEnabled(s0), "Ctrl+E 与只读锁定同闸,保持门禁迁移前的契约");
        }

        Console.WriteLine("── 正文列宽随窗口与连续留白(S80) ──");
        {
            // S80:列宽 = V − 2P,与字号无关;P 连续过渡 12 → 20 → 48(宽窗口固定 48)。
            Check("版式/P 连续留白锚点", MathF.Abs(WindowFit.PadFor(360) - 12f) < 0.001f && MathF.Abs(WindowFit.PadFor(380) - 16f) < 0.001f
                  && MathF.Abs(WindowFit.PadFor(400) - 20f) < 0.001f && MathF.Abs(WindowFit.PadFor(640) - 20f) < 0.001f && MathF.Abs(WindowFit.PadFor(696) - 34f) < 0.001f
                  && MathF.Abs(WindowFit.PadFor(752) - 48f) < 0.001f && MathF.Abs(WindowFit.PadFor(2400) - 48f) < 0.001f,
                  $"360={WindowFit.PadFor(360)} 380={WindowFit.PadFor(380)} 696={WindowFit.PadFor(696)} 752={WindowFit.PadFor(752)}");
            bool continuous = true;
            foreach (float edge in new[] { 360f, 400f, 640f, 752f })
                continuous &= MathF.Abs(WindowFit.PadFor(edge + 0.001f) - WindowFit.PadFor(edge - 0.001f)) < 0.001f;
            Check("版式/P 过渡边界连续", continuous, "360/400/640/752 左右邻域无跳跃");
            float w960 = WindowFit.ColW(960), x960 = WindowFit.ColX(960, w960);
            float w640 = WindowFit.ColW(640), x640 = WindowFit.ColX(640, w640);
            float w360 = WindowFit.ColW(360), x360 = WindowFit.ColX(360, w360);
            Check("版式/列宽几何表", MathF.Abs(w960 - 864f) < 0.1f && MathF.Abs(x960 - 48f) < 0.1f && MathF.Abs(w640 - 600f) < 0.1f && MathF.Abs(x640 - 20f) < 0.1f
                  && MathF.Abs(w360 - 336f) < 0.1f && MathF.Abs(x360 - 12f) < 0.1f, $"960→{w960}/{x960} 640→{w640}/{x640} 360→{w360}/{x360}");
            bool pairOk = true, overflow = false, mono = true, nonnegative = true;
            float worstDip = 0f; string dipAt = "none"; int samples = 0;
            float prevW = -1f;
            for (float v = 0f; v <= 2400f; v += 0.25f)
            {
                float w = WindowFit.ColW(v), x = WindowFit.ColX(v, w);
                samples++;
                if (MathF.Abs((v - w) - 2f * x) > 0.001f) pairOk = false;
                if (x + w > v + 0.001f) overflow = true;
                if (w < 0f || x < 0f) nonnegative = false;
                float dip = prevW - w;
                if (dip > 0.001f) mono = false;
                if (dip > worstDip) { worstDip = dip; dipAt = $"V={v}"; }
                prevW = w;
            }
            Check("版式/留白成对且列不溢出", pairOk && !overflow && nonnegative, $"{samples} points; V 0-2400 step .25");
            Check("版式/列宽全域非递减", mono && worstDip <= 0.001f, $"worst reverse shrink={worstDip} DIP @{dipAt}");
            Check("版式/极窄视口不溢出", MathF.Abs(WindowFit.ColW(200) - 176f) < 0.1f && WindowFit.ColX(200, WindowFit.ColW(200)) + WindowFit.ColW(200) <= 200f,
                  $"V=200 colW={WindowFit.ColW(200)}");
            // 旧口径字面量与常量清零:全扫 engine(grep 执法防漂移)
            string[] srcs = Directory.GetFiles("../engine", "*.cs");
            string where = "";
            foreach (var f in srcs)
            {
                string t = File.ReadAllText(f);
                foreach (string token in new[] { "680f", "CharsPerLine", "MinCol", "MaxCol", "PadX" })
                    if (t.Contains(token)) where += Path.GetFileName(f) + ":" + token + " ";
            }
            Check("版式/列宽旧口径清零", where.Length == 0, where);
            // 三同源防漂移:DocView 列宽必须走 WindowFit,不得回退内联字面量
            string dvCol = File.ReadAllText("../engine/DocView.cs");
            Check("版式/DocView 列宽走 WindowFit", dvCol.Contains("_colW = WindowFit.ColW(_dipW, left, right)") && dvCol.Contains("_colX = WindowFit.ColX(_dipW, _colW, left, right)")
                  && !dvCol.Contains("MathF.Max(240f, eff"), "窄窗内联兜底回退即红");
            // v2.2 §3.1(B2):行高=1.75×字号(16→28、12→21、28→49,恒比缩放不裁字);mono=0.84375×bs(13.5 @16),代码行高=1.6×mono(21.6 @16)
            Check("版式/LineH 公式", MathF.Abs(RenderResources.LineH(16) - 28f) < 0.01f && MathF.Abs(RenderResources.LineH(28) - 49f) < 0.01f && MathF.Abs(RenderResources.LineH(12) - 21f) < 0.01f,
                  $"16={RenderResources.LineH(16):F1} 28={RenderResources.LineH(28):F1} 12={RenderResources.LineH(12):F1}");
            Check("版式/代码字阶与行高", MathF.Abs(RenderResources.MonoSize(16) - 13.5f) < 0.01f && MathF.Abs(RenderResources.CodeLineH(16) - 21.6f) < 0.01f && RenderResources.CodeLineH(24) > 32f,
                  $"mono16={RenderResources.MonoSize(16):F2} codeLineH16={RenderResources.CodeLineH(16):F2} codeLineH24={RenderResources.CodeLineH(24):F2}");
            // 实际 DocView 块位置、标题首块特例和相邻取大由 A1LayoutTests 验证,不再仅 grep 公式。
            // B5 探针结论(work/a1-b5-font-probe.log):CreateTextFormat 只吃单一族名——逗号列表与伪族名同值,
            // 说明整串被当作未知族落入系统默认;正文族名必须保持单一值,防止"看着加了回退、其实什么都没加"的假落地。
            Check("版式/正文族名单一(B5 探针)", MDViewerNative.RenderResources.BodyFamilyName == "Segoe UI", "body=" + MDViewerNative.RenderResources.BodyFamilyName);
        }

        Console.WriteLine("── CommonMark:内联 HTML 白名单 ──");
        {
            var htDoc = MdParser.Parse("<u>underline</u> and <b>bold</b> <mark>mk</mark> x<sub>2</sub> <kbd>Ctrl</kbd>");
            var htPara = (Para)htDoc[0];
            bool htU = false, htB = false, htMk = false, htSub = false, htKbd = false;
            string htT = "";
            foreach (var r in htPara.Inlines)
            {
                htT += r.Text;
                if (r.U && r.Text.Contains("underline")) htU = true;
                if (r.B && r.Text.Contains("bold")) htB = true;
                if (r.Mark && r.Text.Contains("mk")) htMk = true;
                if (r.SupSub == 2 && r.Text.Contains("2")) htSub = true;
                if (r.Code && r.Text.Contains("Ctrl")) htKbd = true;
            }
            Check("HTML标签/样式映射", htU && htB && htMk && htSub && htKbd, htT);
            Check("HTML标签/白名单无残留", !htT.Contains("<u>") && !htT.Contains("</b>") && !htT.Contains("<mark>"), htT);

            var htDoc2 = MdParser.Parse("<span>plain</span> keep <div>raw</div>");
            var htPara2 = (Para)htDoc2[0];
            string htT2 = "";
            foreach (var r in htPara2.Inlines) htT2 += r.Text;
            Check("HTML标签/透传丢弃", htT2.Contains("plain keep") && !htT2.Contains("<span>"), htT2);
            Check("HTML标签/未知原样", htT2.Contains("<div>raw</div>"), htT2);

            var htDoc3 = MdParser.Parse("<a href=\"https://example.com/page\">site</a>");
            var htPara3 = (Para)htDoc3[0];
            string htLink = null;
            foreach (var r in htPara3.Inlines) if (r.Link != null) htLink = r.Link;
            Check("HTML标签/ahref映射", htLink == "https://example.com/page", "runs=[" + string.Join("|", htPara3.Inlines.Select(r4 => r4.Text + "→" + (r4.Link ?? "-") + (r4.Opaque ? "OPQ" : ""))) + "]");
            // 无闭合的 <a> 按规范原样直通(#632),不再映射;前后有文本才是行内形态(孤立成行是类型7块)
            var htDoc4 = MdParser.Parse("x <a href=\"https://x.com/y\"> y\n");
            var htPara4 = (Para)htDoc4[0];
            string ht4t = "";
            foreach (var r in htPara4.Inlines) ht4t += r.Text;
            Check("HTML标签/未闭合a原样", ht4t.Contains("<a href=") && !htPara4.Inlines.Any(r => r.Link != null), ht4t);
        }

        Console.WriteLine("── CommonMark:强调分隔符栈 ──");
        {
            var emDoc = MdParser.Parse("***x***");
            var emPara = (Para)emDoc[0];
            string emT = ""; bool emBI = false;
            foreach (var r in emPara.Inlines) { emT += r.Text; if (r.B && r.I) emBI = true; }
            Check("强调栈/三重粗斜体", emBI && !emT.Contains("*"), emT);

            var emDoc2 = MdParser.Parse("snake_case_word");
            var emPara2 = (Para)emDoc2[0];
            string emT2 = ""; bool emSt2 = false;
            foreach (var r in emPara2.Inlines) { emT2 += r.Text; emSt2 |= r.B || r.I; }
            Check("强调栈/词内下划线不触发", emT2.TrimEnd('\n') == "snake_case_word" && !emSt2, emT2);

            var emDoc3 = MdParser.Parse("a * b * c");
            var emPara3 = (Para)emDoc3[0];
            string emT3 = ""; bool emSt3 = false;
            foreach (var r in emPara3.Inlines) { emT3 += r.Text; emSt3 |= r.B || r.I; }
            Check("强调栈/空格隔开不触发", emT3.TrimEnd('\n') == "a * b * c" && !emSt3, emT3);

            var emDoc4 = MdParser.Parse("**b1** and *i1*");
            var emPara4 = (Para)emDoc4[0];
            string emT4 = ""; bool emB4 = false, emI4 = false;
            foreach (var r in emPara4.Inlines) { emT4 += r.Text; if (r.B) emB4 = true; if (r.I) emI4 = true; }
            Check("强调栈/基本回归", emB4 && emI4 && !emT4.Contains("*"), emT4);

            var emDoc5 = MdParser.Parse("**a *b* c**");
            var emPara5 = (Para)emDoc5[0];
            bool emNest = false;
            foreach (var r in emPara5.Inlines) if (r.B && r.I && r.Text.Contains("b")) emNest = true;
            Check("强调栈/嵌套", emNest, "");
        }

        Console.WriteLine("── 解析细节:tab/转义/码span/标题/围栏 ──");
        {
            Check("细节/tab4列", MdParser.Parse("\tfoo\n")[0] is CodeBlock fxTb && fxTb.Text.TrimEnd('\n') == "foo", "");
            string fxT = "";
            foreach (var r in ((Para)MdParser.Parse("\\→\\A\\a\\ \\3\\φ\\«\n")[0]).Inlines) fxT += r.Text;
            Check("细节/非ASCII不转义", fxT.Contains("\\→\\A"), fxT);
            string fxT2 = "";
            foreach (var r in ((Para)MdParser.Parse("foo\\\nbaz\n")[0]).Inlines) fxT2 += r.Text;
            Check("细节/行尾反斜杠硬换行", !fxT2.Contains("\\") && fxT2.Contains("\n"), fxT2);
            var fxCS = (Para)MdParser.Parse("` `\n")[0];
            Check("细节/码span全空格保留", fxCS.Inlines.Any(r => r.Code && r.Text == " "), "[" + string.Join("|", fxCS.Inlines.Select(r2 => r2.Text)) + "]");
            var fxCS2 = (Para)MdParser.Parse("``\nfoo\nbar\n``\n")[0];
            Check("细节/码span换行转空格", fxCS2.Inlines[0].Code && fxCS2.Inlines[0].Text == "foo bar", fxCS2.Inlines[0].Text);
            Check("细节/7井为段落", MdParser.Parse("####### foo\n")[0] is Para, "");
            var fxH = (Heading)MdParser.Parse("# foo#\n")[0];
            Check("细节/紧贴尾井保留", fxH.Inlines[0].Text == "foo#", fxH.Inlines[0].Text);
            var fxL = MdParser.Parse("Foo\n    ***\n");
            Check("细节/段后4空格懒续行", fxL.Count == 1 && fxL[0] is Para, $"n={fxL.Count} t={fxL[0].GetType().Name}");
            var fxS = (Heading)MdParser.Parse("Foo\nBar\n---\n")[0];
            string fxT4 = "";
            foreach (var r in fxS.Inlines) fxT4 += r.Text;
            Check("细节/多行setext软换行为空格", fxS.Level == 2 && fxT4 == "Foo Bar", fxT4);
            var fxC = (CodeBlock)MdParser.Parse("    chunk1\n      \n      chunk2\n")[0];
            Check("细节/缩进码空行保留缩进", fxC.Text.TrimEnd('\n') == "chunk1\n  \n  chunk2", fxC.Text.Replace("\n", "\\n"));
            var fxF = (CodeBlock)MdParser.Parse("````\naaa\n```\n`````\n")[0];
            Check("细节/围栏等长闭合", fxF.Text == "aaa\n```\n", fxF.Text.Replace("\n", "\\n"));
            Check("细节/info含围栏不成栅", MdParser.Parse("``` ```\naaa\n")[0] is Para, "");
            var fxI = (CodeBlock)MdParser.Parse(" ```\n aaa\naaa\n```\n")[0];
            Check("细节/围栏缩进对齐剥离", fxI.Text == "aaa\naaa\n", fxI.Text.Replace("\n", "\\n"));
        }

        Console.WriteLine("── 链接图片保真 ──");
        {
            var lfP = (Para)MdParser.Parse("[a](/u \"ti\")\n")[0];
            var lfR = lfP.Inlines.First(r2 => r2.Link != null);
            Check("保真/链接title", lfR.Link == "/u" && lfR.LinkTitle == "ti", $"{lfR.Link}|{lfR.LinkTitle}");
            Check("保真/链接文本内联", ((Para)MdParser.Parse("[**b**](/u)\n")[0]).Inlines.Any(r2 => r2.Link != null && r2.B), "");
            Check("保真/角度含括号", ((Para)MdParser.Parse("[a](<b)c>)\n")[0]).Inlines.Any(r2 => r2.Link == "b)c"), "");
            Check("保真/角度空href", ((Para)MdParser.Parse("[a](<>)\n")[0]).Inlines.Any(r2 => r2.Link == ""), "");
            var nlt = MdParser.Parse("[r][d]\n\n[d]: /u\n    \"tt\"\n\ntext\n");
            var nltP = nlt.OfType<Para>().FirstOrDefault();
            Check("保真/次行标题进定义", nltP != null && nltP.Inlines.Any(r2 => r2.Link == "/u" && r2.LinkTitle == "tt"),
                  "link and title checked in parsed output");
            var lfI = (Image)MdParser.Parse("![a *b*](/x.png \"tt\")\n")[0];
            Check("保真/图片title与alt拍平", lfI.Title == "tt" && lfI.Alt == "a b", $"{lfI.Title}|{lfI.Alt}");
            string lfHtml = HtmlExport.Build(MdParser.Parse("[a](/u \"ti\")\n"), "t", false);
            Check("保真/导出title属性", lfHtml.Contains("title=\"ti\""), "");
            string lfHtml2 = HtmlExport.Build(MdParser.Parse("![a](/x.png \"t2\")\n"), "t", false);
            Check("保真/导出图片title", lfHtml2.Contains("title=\"t2\"") && lfHtml2.Contains("src=\"/x.png\""), "");
            string lfHtml3 = HtmlExport.Build(MdParser.Parse("[a](</a b.png>)\n"), "t", false);
            Check("保真/URL空格百分号", lfHtml3.Contains("href=\"/a%20b.png\""), "");
        }

        Console.WriteLine("── 强调mod3与自动链接 ──");
        {
            var mcP = (Para)MdParser.Parse("**foo*bar*baz**\n")[0];
            string mcT = ""; bool mcB = false;
            foreach (var r in mcP.Inlines) { mcT += r.Text; if (r.B) mcB = true; }
            Check("强调/mod3与无上限", mcB && !mcT.Contains("*"), mcT);
            var mcP2 = (Para)MdParser.Parse("____foo__ bar__\n")[0];
            string mcT2 = ""; bool mcB2 = false;
            foreach (var r in mcP2.Inlines) { mcT2 += r.Text; if (r.B) mcB2 = true; }
            Check("强调/4连下划线", mcB2 && !mcT2.Contains("_"), mcT2);
            Check("自动/email", ((Para)MdParser.Parse("<foo@bar.com>\n")[0]).Inlines.Any(r2 => r2.Link == "mailto:foo@bar.com"), "");
            Check("自动/任意scheme", ((Para)MdParser.Parse("<irc://foo.bar:2233/baz>\n")[0]).Inlines.Any(r2 => r2.Link == "irc://foo.bar:2233/baz"), "");
            Check("自动/大写scheme", ((Para)MdParser.Parse("<MAILTO:FOO@BAR.BAZ>\n")[0]).Inlines.Any(r2 => r2.Link == "MAILTO:FOO@BAR.BAZ"), "");
            Check("自动/标签不吃", ((Para)MdParser.Parse("<u>x</u>\n")[0]).Inlines.Any(r2 => r2.U), "");
        }

        Console.WriteLine("── T3.1 链接目的地完备 ──");
        {
            var l1 = (Para)MdParser.Parse("[](./t.md)\n")[0];
            Check("补链/空文本", l1.Inlines.Any(r2 => r2.Link == "./t.md"), "runs=[" + string.Join("|", l1.Inlines.Select(r3 => r3.Text + "→" + (r3.Link ?? "-"))) + "]");
            var l2 = (Para)MdParser.Parse("[a](/my uri)\n")[0];
            string l2t = "";
            foreach (var r in l2.Inlines) l2t += r.Text;
            Check("补链/裸含空格退字面", l2t.TrimEnd('\n') == "[a](/my uri)" && !l2.Inlines.Any(r2 => r2.Link != null), l2t);
            var l3 = (Para)MdParser.Parse("[a](foo\\(and\\(bar\\))\n")[0];
            Check("补链/转义括号", l3.Inlines.Any(r2 => r2.Link == "foo(and(bar)"), "");
            var l4 = (Para)MdParser.Parse("[a](\\(foo\\))\n")[0];
            Check("补链/转义括号2", l4.Inlines.Any(r2 => r2.Link == "(foo)"), "");
            string h5 = HtmlExport.Build(MdParser.Parse("[a](foo%20bä)\n"), "t", false);
            Check("补链/UTF8百分号", h5.Contains("%C3%A4"), "");
            string h6 = HtmlExport.Build(MdParser.Parse("[a](/u?q=1&i=2)\n"), "t", false);
            Check("补链/&转义", h6.Contains("&amp;i=2"), "");
            string h7 = HtmlExport.Build(MdParser.Parse("[a](/u\\[x)\n"), "t", false);
            Check("补链/方括号编码", h7.Contains("%5B"), "");
            var l8 = (Para)MdParser.Parse("[a](/u \"title \"and\" t\")\n")[0];
            string l8t = "";
            foreach (var r in l8.Inlines) l8t += r.Text;
            Check("补链/坏title退字面", l8t.Contains("](/u") && !l8.Inlines.Any(r2 => r2.Link != null), l8t);
            var l9 = (Para)MdParser.Parse("[![moon](m.jpg)](/uri)\n")[0];
            // 规范为 <a><img> 嵌套;扁平模型近似:外层链接包住内层图片文本(不覆盖内层已有链接)
            Check("补链/图嵌链BalClose", l9.Inlines.Any(r2 => r2.Text == "moon") && l9.Inlines.All(r2 => r2.Link != "m.jpg" || r2.Text != "moon]"), "runs=[" + string.Join("|", l9.Inlines.Select(r3 => r3.Text + "→" + (r3.Link ?? "-"))) + "]");
            var l10 = (Para)MdParser.Parse("[link [foo [bar]]](/uri)\n")[0];
            Check("补链/嵌套括号文本", l10.Inlines.Any(r2 => r2.Link == "/uri" && r2.Text.Contains("[foo [bar]]")), "");
        }

        Console.WriteLine("── T3.2 引用定义多行形式 ──");
        {
            var d1 = MdParser.Parse("[a][f]\n\n[f]:\n/url\n\nx\n");
            bool ok1 = false;
            foreach (var b in d1) if (b is Para p) foreach (var r in p.Inlines) if (r.Link == "/url") ok1 = true;
            Check("多行定义/目的地在下一行", ok1, $"blocks={d1.Count}");

            var d2 = MdParser.Parse("[a][f]\n\n[f]:\n<my url>\n'tt'\n\nx\n");
            bool ok2 = false; bool stray2 = false;
            foreach (var b in d2) if (b is Para p) foreach (var r in p.Inlines)
            {
                if (r.Link == "my url" && r.LinkTitle == "tt") ok2 = true;
                if (r.Text.Contains("my url") || r.Text.Contains("tt")) stray2 = true;
            }
            Check("多行定义/尖括号+次行title", ok2 && !stray2, "");

            var d3 = MdParser.Parse("[a][f]\n\n[f]: /url 'the title'\n\nx\n");
            bool ok3 = false;
            foreach (var b in d3) if (b is Para p) foreach (var r in p.Inlines) if (r.Link == "/url" && r.LinkTitle == "the title") ok3 = true;
            Check("多行定义/同行title", ok3, "");

            var d4 = MdParser.Parse("[a][F*o\\]]\n\n[F*o\\]]: /u\n\nx\n");
            bool ok4 = false; bool stray4 = false;
            foreach (var b in d4) if (b is Para p) foreach (var r in p.Inlines)
            {
                if (r.Link == "/u") ok4 = true;
                if (r.Text.Contains("]:")) stray4 = true;
            }
            Check("多行定义/label转义括号", ok4 && !stray4, "ok=" + ok4 + " stray=" + stray4);

            var d5 = MdParser.Parse("[f]: /url 'unclosed\n\n[f]\n");
            // 未闭合 title:定义行退回正文
            bool ok5 = ((Para)d5[0]).Inlines.Any(r => r.Text.Contains("[f]: /url 'unclosed"));
            Check("多行定义/未闭合title退正文", ok5, "t0=" + d5[0].GetType().Name + " txt=[" + string.Join("", ((Para)d5[0]).Inlines.Select(r2 => r2.Text)) + "]");

            var d6 = MdParser.Parse("[a][f]\n\n[f]: <>\n\nx\n");
            bool ok6 = false;
            foreach (var b in d6) if (b is Para p) foreach (var r in p.Inlines) if (r.Link == "") ok6 = true;
            Check("多行定义/空尖括号", ok6, "");
        }

        Console.WriteLine("── 步骤2 链接/定义机械补全 ──");
        {
            // #507:括号形式 title
            var m1 = (Para)MdParser.Parse("[link](/url (title))\n")[0];
            var m1r = m1.Inlines.FirstOrDefault(r => r.Link != null);
            Check("链接补全/括号title", m1r?.Link == "/url" && m1r?.LinkTitle == "title", $"link={m1r?.Link} title={m1r?.LinkTitle}");

            // #512:目的地与 title 跨行
            var blocks512 = MdParser.Parse("[link](   /uri\n  \"title\"  )\n");
            string blk512 = string.Join(" || ", blocks512.Select(b =>
                b is Para pp ? "Para:[" + string.Join("", pp.Inlines.Select(r => r.Text)).Replace("\n", "\\n") + "]"
                : b.GetType().Name));
            var m2 = (Para)blocks512[0];
            var m2r = m2.Inlines.FirstOrDefault(r => r.Link != null);
            Check("链接补全/跨行title", m2r?.Link == "/uri" && m2r?.LinkTitle == "title",
                $"blocks=[{blk512}]");

            // #204:定义 title 内转义引号与目的地转义
            var m3 = (Para)MdParser.Parse("[foo]: /url\\bar\\*baz \"foo\\\"bar\\baz\"\n\n[foo]\n")[^1];
            var m3r = m3.Inlines.FirstOrDefault(r => r.Link != null);
            Check("定义补全/转义引号title", m3r?.Link == "/url\\bar*baz" && m3r?.LinkTitle == "foo\"bar\\baz", $"link={m3r?.Link} title={m3r?.LinkTitle}");

            // #198:跨行 title(换行保留)
            var m4docs = MdParser.Parse("[foo]: /url '\ntitle\nline1\nline2\n'\n\n[foo]\n");
            var m4 = (Para)m4docs[^1];
            var m4r = m4.Inlines.FirstOrDefault(r => r.Link != null);
            Check("定义补全/跨行title", m4r?.Link == "/url" && m4r?.LinkTitle == "\ntitle\nline1\nline2\n", $"title=[{m4r?.LinkTitle}]");

            // #215:定义行不打断段落
            var m5 = MdParser.Parse("Foo\n[bar]: /baz\n\n[bar]\n");
            bool m5ok = m5.Count == 2 && m5[0] is Para p5 && m5[1] is Para p5b
                && string.Join("", p5.Inlines.Select(r => r.Text)).Contains("[bar]: /baz")
                && !p5b.Inlines.Any(r => r.Link != null);
            Check("定义补全/不打断段落", m5ok, $"n={m5.Count}");

            // #531:label 内转义 ] 的定义与引用 round-trip
            var m6 = (Para)MdParser.Parse("[Foo*bar\\]]:my_(url) 'title (with parens)'\n\n[Foo*bar\\]]\n")[^1];
            var m6r = m6.Inlines.FirstOrDefault(r => r.Link != null);
            Check("定义补全/label转义括号", m6r?.Link == "my_(url)" && m6r?.LinkTitle == "title (with parens)"
                && m6r?.Text == "Foo*bar]", $"link={m6r?.Link} title={m6r?.LinkTitle} text={m6r?.Text}");
        }

        Console.WriteLine("── T3.3 强调长尾与raw直通 ──");
        {
            var e1 = (Para)MdParser.Parse("foo *\\**\n")[0];
            string e1t = ""; bool e1i = false;
            foreach (var r in e1.Inlines) { e1t += r.Text; if (r.I) e1i = true; }
            Check("补强/转义星成em内容", e1i && e1t.TrimEnd('\n') == "foo *", e1t);
            var e2 = (Para)MdParser.Parse("*foo _bar* baz_\n")[0];
            string e2t = ""; bool e2i = false;
            foreach (var r in e2.Inlines) { e2t += r.Text; if (r.I) e2i = true; }
            Check("补强/跨定界匹配", e2i && e2t.TrimEnd('\n') == "foo _bar baz_", e2t);
            var e3 = (Para)MdParser.Parse("*<img src=\"foo\" title=\"*\"/>\n")[0];
            string e3t = ""; bool e3st = false;
            foreach (var r in e3.Inlines) { e3t += r.Text; if (r.B || r.I) e3st = true; }
            Check("补强/标签内定界不参与", !e3st && e3t.Contains("*"), e3t);
            var e4 = (Para)MdParser.Parse("<a><bab><c2c>\n")[0];
            string e4t = "";
            foreach (var r in e4.Inlines) e4t += r.Text;
            Check("raw直通/行内标签原样", e4t.Contains("<a><bab><c2c>"), e4t);
            string e4h = HtmlExport.Build(MdParser.Parse("foo <a><bab><c2c>\n"), "t", false);
            // E14b:导出改用白名单,未知标签显示为字面(与阅读态一致);白名单内的 <a> 照常输出
            Check("raw直通/导出原样", e4h.Contains("<a>&lt;bab&gt;&lt;c2c&gt;"), "");
            string e4s = HtmlExport.Build(MdParser.Parse("foo <script>alert(1)</script>\n"), "t", false);
            Check("raw直通/script拦截", !e4s.Contains("<script"), "");
        }

        Console.WriteLine("── T3.4/T3.5 硬换行与杂项 ──");
        {
            var hb1 = (Para)MdParser.Parse("foo\\\nbaz\n")[0];
            string hb1t = "";
            foreach (var r in hb1.Inlines) hb1t += r.Text;
            Check("补硬/反斜杠硬换行", hb1t.Contains("\n") && !hb1t.Contains("\\"), hb1t);
            var hb2 = (Para)MdParser.Parse("foo\\\n     bar\n")[0];
            string hb2t = "";
            foreach (var r in hb2.Inlines) hb2t += r.Text;
            Check("补硬/换行剥前导空格", hb2t.Contains("\n") && !hb2t.Contains("     "), hb2t);
            Check("杂项/setext尾空格", MdParser.Parse("Foo\n   ----      \n")[0] is Heading, "");
            var sh = (Heading)MdParser.Parse("Foo  \n-----\n")[0];
            string sht = "";
            foreach (var r in sh.Inlines) sht += r.Text;
            Check("杂项/setext剥行尾空格", sht == "Foo", sht);
            Check("杂项/空段落不产出", MdParser.Parse("a\n\n\n\nb\n").Count(b => b is Para) == 2, $"n={MdParser.Parse("a\n\n\n\nb\n").Count}");
            Check("杂项/单井成空标题", MdParser.Parse("#\n")[0] is Heading, "");
            var t60 = MdParser.Parse("* Foo\n* * *\n* Bar\n");
            Check("杂项/hr打断列表", t60.Any(b => b is HRule) && t60.Count(b => b is ListItem) == 2, $"n={t60.Count}");
            var cs344 = (Para)MdParser.Parse("[not a `link](/foo`)\n")[0];
            bool hasCode = cs344.Inlines.Any(r => r.Code);
            Check("杂项/反引号优先于链接", hasCode, "");
            var at610 = (Para)MdParser.Parse("< https://foo.bar >\n")[0];
            Check("杂项/角度autolink空格拒绝", !at610.Inlines.Any(r => r.Link != null && r.Link.Contains("foo")), "");
            var at611 = (Para)MdParser.Parse("<m:abc>\n")[0];
            Check("杂项/单字母scheme拒绝", !at611.Inlines.Any(r => r.Link != null), "");
            var tb1 = MdParser.Parse("\tfoo\n")[0];
            Check("杂项/行首tab成码块", tb1 is CodeBlock, tb1.GetType().Name);
            var en40 = (Para)MdParser.Parse("&#9;foo\n")[0];
            string en40t = "";
            foreach (var r in en40.Inlines) en40t += r.Text;
            Check("杂项/数字实体tab保留", en40t.Contains("\t") || en40t.TrimEnd('\n').EndsWith("foo"), en40t);
        }

        Console.WriteLine("── 步骤3 Tab 语义 ──");
        {
            // 行首 tab 成缩进码;码内容 tab 保留(解析与导出一致)
            var tb1 = MdParser.Parse("\tfoo\tbar\n")[0];
            Check("tab/行首成码内容保留", tb1 is CodeBlock tcb1 && tcb1.Text.Contains('\t'), $"t={tb1.GetType().Name}");
            string tb2 = HtmlExport.Build(MdParser.Parse("\tfoo\tbar\n"), "t", false);
            Check("tab/导出码内容保留", tb2.Contains("<pre><code>foo\tbar</code></pre>"), "");
            // 围栏内容 tab 保留
            var tb3 = MdParser.Parse("```\na\tb\n```\n")[0];
            Check("tab/围栏内容保留", tb3 is CodeBlock tcb3 && tcb3.Text.Contains("a\tb"), $"t={tb3.GetType().Name}");
            // #10:ATX 井号后接 tab;#11:HR 内 tab
            Check("tab/ATX井号后tab", MdParser.Parse("#\tFoo\n")[0] is Heading h10 && h10.Level == 1, "");
            Check("tab/HR内tab", MdParser.Parse("*\t*\t*\n")[0] is HRule, "");
            // 实体与转义出的 tab 与规范一致(#40)
            var tb4 = (Para)MdParser.Parse("&#9;foo\n")[0];
            Check("tab/数字实体tab", string.Join("", tb4.Inlines.Select(r => r.Text)).StartsWith('\t'), "");
        }

        Console.WriteLine("── T2 Raw HTML 块 ──");
        {
            var h1 = MdParser.Parse("<div>\n*hello*\n</div>\n");
            Check("HTML块/div成块", h1.Count == 1 && h1[0] is HtmlBlock hb1 && hb1.Text.Contains("*hello*"), $"n={h1.Count} t={h1[0].GetType().Name}");
            var h2 = MdParser.Parse("<table>\n<tr><td>hi</td></tr>\n</table>\n\nokay.\n");
            Check("HTML块/table到闭标签", h2.Count == 2 && h2[0] is HtmlBlock hb2 && hb2.Text.Contains("</table>") && h2[1] is Para, $"n={h2.Count}");
            var h3 = MdParser.Parse("<!-- comment\nspan -->\n\nafter\n");
            Check("HTML块/注释块", h3.Count >= 2 && h3[0] is HtmlBlock, $"n={h3.Count} t={h3[0].GetType().Name}");
            var h4 = MdParser.Parse("行内 <b>bold</b> 不误吞\n");
            Check("HTML块/行内不误吞", h4[0] is Para, $"t={h4[0].GetType().Name}");
            string h5 = HtmlExport.Build(MdParser.Parse("<div>\n*hi*\n</div>\n"), "t", false);
            Check("HTML块/导出直通", h5.Contains("<div>") && h5.Contains("</div>") && !h5.Contains("&lt;div&gt;"), "");
            var h6 = MdParser.Parse("<pre>\n*md*\n\nok\n</pre>\n");
            Check("HTML块/1类空行不截断", h6.Count == 1 && h6[0] is HtmlBlock hb6 && hb6.Text.Contains("*md*"), $"n={h6.Count}");
            var h6b = MdParser.Parse("<div>\n\n*md*\n\n</div>\n");
            Check("HTML块/6类空行截断", h6b.Count == 3 && h6b[0] is HtmlBlock && h6b[1] is Para && h6b[2] is HtmlBlock, $"n={h6b.Count}");
            var h7 = MdParser.Parse("<td>\nfoo\n\n*md*\n\nbar\n");
            Check("HTML块/6类空行截断td", h7.Count >= 2 && h7[0] is HtmlBlock hb7 && !hb7.Text.Contains("*md*"), $"n={h7.Count}");
            // 安全:脚本类块导出仍转义;on* 事件属性与危险 scheme 中和;引号值内不误伤
            string hs = HtmlExport.Build(MdParser.Parse("<script>alert(1)</script>\n"), "t", false);
            Check("HTML块/script导出转义", hs.Contains("&lt;script&gt;") && !hs.Contains("<script>"), "");
            string ho = HtmlExport.Build(MdParser.Parse("<div onclick=\"evil()\">x</div>\n"), "t", false);
            Check("HTML块/on事件属性中和", ho.Contains("<div>x</div>") && !ho.Contains("onclick"), "E14b:白名单直接去掉事件属性");
            string hq = HtmlExport.Build(MdParser.Parse("<div title=\"call on me=ok\">x</div>\n"), "t", false);
            Check("HTML块/引号值不误伤", hq.Contains("call on me=ok"), "");
            string hj = HtmlExport.Build(MdParser.Parse("<a href=\"javascript:alert(1)\">x</a>\n"), "t", false);
            Check("HTML块/javascript链接中和", hj.Contains("href=\"#\""), "");
            string hi = HtmlExport.Build(MdParser.Parse("x <img src=x onerror=alert(1)> y\n"), "t", false);
            Check("行内直通/on属性中和", hi.Contains("<img src=\"x\">") && !hi.Contains("onerror"), "E14b:白名单直接去掉事件属性");
            string hl2 = HtmlExport.Build(MdParser.Parse("[x](javascript:alert(1))\n"), "t", false);
            Check("链接/javascript scheme中和", hl2.Contains("href=\"#\""), "");
            // 类型7:孤立完整标签成块(逐字,不做行内处理);不能打断段落
            var t7 = MdParser.Parse("<span>\n*md*\n</span>\n");
            Check("HTML块/类型7孤立标签", t7.Count == 1 && t7[0] is HtmlBlock ht7 && ht7.Text.Contains("*md*") && !ht7.Text.Contains("<em>"), $"n={t7.Count} t={t7[0].GetType().Name}");
            var t7b = MdParser.Parse("段落文字\n<span>\n继续\n");
            Check("HTML块/类型7不打断段落", t7b.Count == 1 && t7b[0] is Para, $"n={t7b.Count} t={t7b[0].GetType().Name}");
            var t1a = MdParser.Parse("<pre>x</pre>\n");
            Check("HTML块/1类单行闭合", t1a.Count == 1 && t1a[0] is HtmlBlock t1ab && t1ab.Text.Contains("</pre>"), $"n={t1a.Count}");
        }

        Console.WriteLine("── T1.1 列表项子块(解析) ──");
        {
            var s1 = MdParser.Parse("- a\n\n  b\n");
            Check("子块/两段列表项", s1.Count == 1 && s1[0] is ListItem li1 && li1.Sub != null && li1.Sub.Count == 1 && li1.Sub[0] is Para, $"n={s1.Count}");
            var s2 = MdParser.Parse("- a\n\n      code\n");
            Check("子块/项内缩进码", s2.Count == 1 && s2[0] is ListItem li2 && li2.Sub?[0] is CodeBlock cb2 && cb2.Text.Contains("code"), $"n={s2.Count}");
            var s3 = MdParser.Parse("- a\n\n  > q\n");
            Check("子块/项内引用", s3.Count == 1 && s3[0] is ListItem li3 && li3.Sub?[0] is Quote, $"n={s3.Count}");
            var s4 = MdParser.Parse("- a\n- b\n");
            Check("子块/简单项无Sub", s4.Count == 2 && s4[0] is ListItem li4 && li4.Sub == null, $"n={s4.Count}");
            var s5 = MdParser.Parse("- a\n  - b\n");
            Check("子块/子列表仍平铺", s5.Count == 2 && s5[0] is ListItem && s5[1] is ListItem, $"n={s5.Count}");
            var s6 = MdParser.Parse("- a\n  续行合并\n");
            Check("子块/续行无Sub", s6.Count == 1 && s6[0] is ListItem li6 && li6.Sub == null, $"n={s6.Count}");
        }

        Console.WriteLine("── T1.2 列表项子块(渲染/交互) ──");
        {
            using var doc2 = new DocView();
            doc2.Load("- 首段甲\n\n  第二段乙\n\n      项内代码\n");
            doc2.OnWidth(596f);
            Check("子块渲染/单块", doc2.BlockCount == 1, $"n={doc2.BlockCount}");
            Check("子块渲染/可绘制", doc2.BlockDrawable(0), "");
            // 命中第二段:y 扫描找 pos 落在子块文本区间(>首段长度)
            int firstLen = "首段甲".Length;
            bool hitSub = false;
            for (float y = 60f; y < 400f; y += 6f)
            {
                var pr = doc2.PosAtText(100f, y);
                if (pr.block == 0 && pr.pos > firstLen + 1) { hitSub = true; break; }
            }
            Check("子块渲染/命中子块文本", hitSub, $"firstLen={firstLen}");
            // 跨段选区复制 == SearchText 切片
            string st2 = doc2.SearchText();
            doc2.SelBegin(doc2.ColX + 26f, doc2.YAtOffset(0) + 8f);
            doc2.SelExtend(600f, 300f);
            string cp2 = doc2.CopyText() ?? "";
            int p2a = st2.IndexOf("首段甲");
            int p2b = st2.IndexOf("项内代码");
            string expect2 = p2b > p2a ? st2[p2a..(p2b + "项内代码".Length)] : "";
            Check("子块渲染/跨段复制=原文切片", cp2 == expect2, $"cpLen={cp2.Length} expLen={expect2.Length}");
            Check("子块渲染/YAtOffset单调", doc2.YAtOffset(st2.IndexOf("项内代码")) > doc2.YAtOffset(st2.IndexOf("首段甲")), "");
        }

        Console.WriteLine("── T1.3 列表项子块(导出) ──");
        {
            string h1 = HtmlExport.Build(MdParser.Parse("- a\n\n  b\n\n      code\n"), "t", false);
            var liD = (ListItem)MdParser.Parse("- a\n\n  b\n\n      code\n")[0];
            bool h1p = h1.Contains("<p>b") && h1.Contains("</p>"), h1c = h1.Contains("<pre><code>code");
            Check("子块导出/嵌套结构", h1p && h1c, h1p ? "ok" : h1[..Math.Min(400, h1.Length)]);
            string h2 = HtmlExport.Build(MdParser.Parse("- a\n\n  > q\n"), "t", false);
            Check("子块导出/项内引用", h2.Contains("<blockquote>") && h2.Contains("q"), "");
            string h3 = HtmlExport.Build(MdParser.Parse("- 简单项\n- 第二项\n"), "t", false);
            Check("子块导出/简单项不嵌套", h3.Contains("<li>简单项") && !h3.Contains("<p>简单项"), "");
            // 规范 §5.2:首段后有任一子块(含代码)即首段包 <p>;#272/#266/#5(经 SpecNorm 归一换行)
            string h4 = SpecNorm(SpecRender("- foo\n\n      bar\n"));
            Check("子块导出/子代码首段包p", h4 == "<ul><li><p>foo</p><pre><code>bar</code></pre></li></ul>", h4);
            string h5 = SpecNorm(SpecRender("- a\n\n  > q\n"));
            Check("子块导出/子引用首段包p", h5 == "<ul><li><p>a</p><blockquote><p>q</p></blockquote></li></ul>", h5);
        }

        Console.WriteLine("── 防崩:异常输入 ──");
        {
            var deep = MdParser.Parse(new string('>', 5000) + " 深\n");   // 5000 层引用,递归必须被深度上限兜住
            Check("防崩/深引用不炸", deep.Count >= 1, $"blocks={deep.Count}");
            var huge = MdParser.Parse("999999999999999999999. 项\n");     // 超大序号,必须退化为段落而非 OverflowException
            Check("防崩/超大序号退化", huge.Count == 1 && huge[0] is Para, $"type={huge[0].GetType().Name}");
            var mixq = MdParser.Parse("> " + new string(' ', 4000) + "- 深\n");   // 引用+深缩进列表组合
            Check("防崩/引用深列表不炸", mixq.Count >= 1, $"blocks={mixq.Count}");
            // 大量懒续交替(探针 O(n²) 防御:预算内须秒级完成)
            var lazyPairs = new System.Text.StringBuilder();
            for (int li2 = 0; li2 < 3000; li2++) { lazyPairs.Append("> 引用").Append(li2).Append('\n').Append("懒续").Append(li2).Append('\n'); }
            var swL = System.Diagnostics.Stopwatch.StartNew();
            var lazyDoc = MdParser.Parse(lazyPairs.ToString());
            swL.Stop();
            Check("防崩/海量懒续限时", lazyDoc.Count == 1 && swL.ElapsedMilliseconds < 3000, $"blocks={lazyDoc.Count} ms={swL.ElapsedMilliseconds}");
        }

        Console.WriteLine("── 回归:本轮修复(对话框槽位不可无头测,其余逐项固化) ──");
        {
            // 表格对齐:必须读分隔行(修复前读最后一个数据行,有数据行的对齐表格全部退化左对齐)
            var tbl = MdParser.Parse("| 列A | 列B |\n|:---|---:|\n| 左 | 右 |\n").OfType<Table>().FirstOrDefault();
            Check("回归/表格对齐取分隔行", tbl != null && tbl.Aligns.Length == 2 && tbl.Aligns[0] == 0 && tbl.Aligns[1] == 2,
                  tbl == null ? "no table" : $"aligns=[{string.Join(",", tbl.Aligns)}]");
            // setext 一级标题下划线带尾随空白(修复前按未 trim 的判定降级 h2)
            var stx = MdParser.Parse("Title\n===   \n").OfType<Heading>().FirstOrDefault();
            Check("回归/setext尾随空格h1", stx != null && stx.Level == 1, stx == null ? "no heading" : $"level={stx.Level}");
            // 脚注 label 注入:fid 原样进 href 属性(修复前 [<a"><script>] 可注入导出 HTML)
            string fnHtml = HtmlExport.Build(MdParser.Parse("x[^a\"><script>alert(1)</script>] y\n\n[^a\"><script>alert(1)</script>]: note\n"), "t", false);
            Check("回归/脚注href转义", !fnHtml.Contains("<script>") && fnHtml.Contains("href=\"#fn-"), "导出文件不得含可执行标签");
            // 行内深度炸弹:2000 层嵌套链接(修复前 ParseInlines 无界递归 → StackOverflow 进程必死)
            string bomb = string.Concat(Enumerable.Repeat("[a", 2000)) + "x" + string.Concat(Enumerable.Repeat("](y)", 2000));
            var bombDoc = MdParser.Parse(bomb + "\n");
            Check("回归/行内深度炸弹", bombDoc.Count >= 1, $"blocks={bombDoc.Count}");
            // 数学深度炸弹:8000 个 { 与 4000 层 \frac(修复前 MathRender/MathUnicode 无界递归 → StackOverflow)
            Check("回归/数学括号深度炸弹", MathRender.Parse(new string('{', 8000)) == null, "限深拒绝，交由视图展示完整源码");
            string mathBomb = string.Concat(Enumerable.Repeat("\\frac{", 4000));
            Check("回归/数学frac深度炸弹", MathUnicode.Convert(mathBomb) == mathBomb, "超深输入不丢失任何源码");
            // EscUrl 代理对:URL 含 emoji 须成对百分号编码(修复前 ConvertFromUtf32 抛异常 → 导出整体静默失败)
            string emo = HtmlExport.Build(MdParser.Parse("[x](a😀b)\n"), "t", false);
            Check("回归/EscUrl代理对", emo.Contains("a%F0%9F%98%80b"), "emoji URL 正常编码");
            // 列表续行超大序号:int 溢出防崩溃(修复前 int.Parse 抛 OverflowException → EditMainProc 无兜底 → 进程死)
            string contBig = EditorKit.EnterContinuation("99999999999999999. x");
            string contOk = EditorKit.EnterContinuation("1. x");
            Check("回归/列表序号溢出", contBig == "\r\n1. " && contOk == "\r\n2. ", $"big=[{contBig}] ok=[{contOk}]");
            // GBK 严格解码回退(加载路径的编解码回路;UI 侧 LoadFile 无法无头测)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            byte[] gbkBytes = System.Text.Encoding.GetEncoding(936).GetBytes("# 中文标题\n正文内容\n");
            bool strictFail = false;
            try { _ = new System.Text.UTF8Encoding(false, true).GetString(gbkBytes); }
            catch (System.Text.DecoderFallbackException) { strictFail = true; }
            string gbkText = strictFail ? System.Text.Encoding.GetEncoding(936).GetString(gbkBytes) : "";
            Check("回归/GBK严格回退", strictFail && gbkText.Contains("中文标题"), "UTF-8 失败须回退 GBK");
        }

        Console.WriteLine("── 回归2:第二批修复(消毒器绕过/data-href/O(n²)) ──");
        {
            // on* 属性名与 = 之间空白:浏览器照常执行,消毒器必须同样中和
            string s1 = HtmlExport.SanitizeRawHtml("<img src=x onerror =\"alert(1)\">");
            Check("回归2/on属性空白中和", s1.Contains("xonerror"), s1);
            // href = "javascript:"(等号带空白):scheme 同样中和
            string s2 = HtmlExport.SanitizeRawHtml("<a href = \"javascript:alert(1)\">x</a>");
            Check("回归2/href空白scheme中和", !s2.Contains("javascript:"), s2);
            // action=/formaction=:javascript 执行点,一并中和
            string s3 = HtmlExport.SanitizeRawHtml("<form action=\"javascript:alert(1)\"><input type=submit></form>");
            Check("回归2/action白名单", !s3.Contains("javascript:"), s3);
            // customCss 的 </style 逃逸
            string cssOut = HtmlExport.NeutralizeStyleClose("a{color:red}</style><script>alert(1)</script>");
            Check("回归2/customCss逃逸", !cssOut.Contains("</style>"), cssOut);
            // data-href 不得被子串匹配误当 href
            string s4 = SpecRender("<a data-href=\"https://evil.example\">文字</a>\n");
            Check("回归2/data-href不误配", !s4.Contains("href=\"https://evil.example\""), s4);
            // 反引号 O(n²):未配对开栅整段跳过(修复前 30 万反引号 ≈ 21s)
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            var bt = MdParser.Parse("[" + new string('`', 300000) + "\n");
            sw1.Stop();
            Check("回归2/反引号限时", bt.Count >= 1 && sw1.ElapsedMilliseconds < 5000, $"ms={sw1.ElapsedMilliseconds}");
            // < 密集 O(n²):每个 < 的 > 搜索限窗(修复前 30 万 < ≈ 3.3s)
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var lt = MdParser.Parse(new string('<', 300000) + "\n");
            sw2.Stop();
            Check("回归2/<密集限时", lt.Count >= 1 && sw2.ElapsedMilliseconds < 5000, $"ms={sw2.ElapsedMilliseconds}");
            // 懒续探针按工作量计费:500 字符 ×2000 条(修复前 ≈15s)
            var sw3 = System.Diagnostics.Stopwatch.StartNew();
            var lb = new System.Text.StringBuilder("> start\n");
            for (int li3 = 0; li3 < 2000; li3++) lb.Append(new string('x', 500)).Append('\n');
            var lazyBig = MdParser.Parse(lb.ToString());
            sw3.Stop();
            Check("回归2/懒续探针限时", lazyBig.Count >= 1 && sw3.ElapsedMilliseconds < 5000, $"ms={sw3.ElapsedMilliseconds}");
        }

        Console.WriteLine("── 回归3:复查增量(LinkOuter注入/大写on*/\\f绕过) ──");
        {
            // 图片挂外层链接的脚注分支:dest 原文进 href 属性,必须转义(与 [^…] 路径同款)
            string lo = SpecRender("[![x](y)](#fn:a\"><script>alert(1)</script>)\n");
            Check("回归3/LinkOuter脚注转义", !lo.Contains("<script>"), "导出不得含可执行标签");
            // on* 属性名大写:浏览器大小写不敏感,消毒器必须同中和
            string up = HtmlExport.SanitizeRawHtml("<IMG ONERROR=alert(1)>");
            Check("回归3/大写on中和", up.Contains("xoNERROR"), up);
            // \f 作属性分隔符(HTML5 tag 内空白):on* 与 URL 中和均不得被绕过
            string f1 = HtmlExport.SanitizeRawHtml("<img src=x\fonerror=alert(1)>");
            Check("回归3/\\f拆分on中和", f1.Contains("xonerror"), f1);
            string f2 = HtmlExport.SanitizeRawHtml("<a href\f=\"javascript:alert(1)\">x</a>");
            Check("回归3/\\f拆分scheme中和", !f2.Contains("javascript:"), f2);
        }

        Console.WriteLine("── 回归4:第三批(实体scheme绕过) ──");
        {
            // 属性值里的 HTML 实体:浏览器先解码实体再解析 scheme,消毒器判定必须同形;
            // 断言看 href="#"(危险值必须被中和),不能只看有没有字面 "javascript"
            string e1 = HtmlExport.SanitizeRawHtml("<a href=\"&#106;avascript:alert(1)\">x</a>");
            Check("回归4/数字实体scheme", e1.Contains("href=\"#\""), e1);
            string e2 = HtmlExport.SanitizeRawHtml("<a href=\"&#x6A;avascript&#58;alert(1)\">x</a>");
            Check("回归4/十六进制实体+实体冒号", e2.Contains("href=\"#\""), e2);
            string e3 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&colon;alert(1)\">x</a>");
            Check("回归4/命名实体冒号", e3.Contains("href=\"#\""), e3);
            // 数字实体浏览器不要求分号、允许补零(Edge headless 实测),判定必须同形
            string e5 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&#58alert(1)\">x</a>");
            Check("回归4/无分号数字实体", e5.Contains("href=\"#\""), e5);
            string e6 = HtmlExport.SanitizeRawHtml("<a href=\"&#106avascript:alert(1)\">x</a>");
            Check("回归4/无分号j", e6.Contains("href=\"#\""), e6);
            string e7 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&#0000058;alert(1)\">x</a>");
            Check("回归4/补零实体", e7.Contains("href=\"#\""), e7);
            string e8 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&#x0000003A;alert(1)\">x</a>");
            Check("回归4/补零十六进制实体", e8.Contains("href=\"#\""), e8);
            // 位数无上限:≥11 位补零浏览器照样解码(Edge 实测),不得因扫描截断而放行
            string e9 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&#00000000058;alert(1)\">x</a>");
            Check("回归4/11位补零实体", e9.Contains("href=\"#\""), e9);
            string e10 = HtmlExport.SanitizeRawHtml("<a href=\"javascript&#x0000000003A;alert(1)\">x</a>");
            Check("回归4/11位补零十六进制", e10.Contains("href=\"#\""), e10);
            // 越界/0/代理:浏览器 U+FFFD 惰性,不成 scheme,保持原样即可
            string e11 = HtmlExport.SanitizeRawHtml("<a href=\"&#999999999999;alert(1)\">x</a>");
            Check("回归4/越界实体保持字面", e11.Contains("&#999999999999;"), e11);
            // 正常含 & 的 URL 不误伤(导出应保持 &amp; 转义,且值不被中和)
            string e4 = SpecRender("[b](https://e.com/a?x=1&y=2)\n");
            Check("回归4/正常&URL不误伤", e4.Contains("x=1&amp;y=2"), e4);
        }

        // ---------- B1 FontProbe(serif 实测暴露:注册表键名含空格永不命中 + 写死族名≠DWrite 真名) ----------
        {
            Check("FontProbe/Norm 剥空格括点", FontProbe.Norm("Noto Serif SC (TrueType)") == "notoserifsctruetype");
            // 回归锁:旧实现(小写键名直接 Contains)对本机真实键名必失配——这就是"装了字体报未装"的 bug 本体
            Check("FontProbe/旧法必失配(锁 bug)", !"noto serif sc (truetype)".Contains("notoserifsc"));
            var m1 = FontProbe.Match(new[] { ("Noto Serif SC (TrueType)", "NotoSerifSC-VF.ttf") });
            Check("FontProbe/键名含空格命中", m1 != null && m1.Value.idx == 3, m1?.name ?? "null");
            var m2 = FontProbe.Match(new[] {
                ("Noto Serif SC (TrueType)", "NotoSerifSC-VF.ttf"),
                ("Source Han Serif SC Regular (TrueType)", "SourceHanSerifSC-Regular.otf") });
            Check("FontProbe/Adobe 优先于 Google", m2 != null && m2.Value.idx == 1);
            var m3 = FontProbe.Match(new[] {
                ("Noto Serif SC (TrueType)", "NotoSerifSC-VF.ttf"),
                ("Noto Serif CJK SC (TrueType)", "NotoSerifCJKsc-Regular.otf") });
            Check("FontProbe/CJK 键优先于裸 SC 键", m3 != null && m3.Value.idx == 2);
            Check("FontProbe/拉丁 Noto Serif 不误伤", FontProbe.Match(new[] { ("Noto Serif Bold (TrueType)", "NotoSerif-Bold.ttf") }) == null);
            Check("FontProbe/Noto Sans SC 不误伤", FontProbe.Match(new[] { ("Noto Sans SC (TrueType)", "NotoSansSC-VF.ttf") }) == null);
            Check("FontProbe/无关字体为 null", FontProbe.Match(new[] { ("Arial (TrueType)", "arial.ttf") }) == null);

            // 合成最小 sfnt:验证 en-US 优先(zh-CN 记录"思源宋体"会劫持 DWrite 解析)、ID16 优先、Mac 平台可读、坏输入不崩
            static byte[] W16b(ushort v) => new byte[] { (byte)(v >> 8), (byte)v };
            static byte[] W32b(uint v) => new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            static byte[] BuildFont(params (ushort plat, ushort lang, ushort id, string val)[] names)
            {
                using var rec = new MemoryStream();
                using var strs = new MemoryStream();
                foreach (var (plat, lang, id, val) in names)
                {
                    byte[] u = System.Text.Encoding.BigEndianUnicode.GetBytes(val);
                    ushort off = (ushort)strs.Length;
                    ushort enc = (ushort)(plat == 3 ? 1 : 0);   // Windows=UCS2, Mac=Roman;12 字节含 encodingID,漏一则整表错位
                    rec.Write(W16b(plat)); rec.Write(W16b(enc)); rec.Write(W16b(lang)); rec.Write(W16b(id)); rec.Write(W16b((ushort)u.Length)); rec.Write(W16b(off));
                    strs.Write(u);
                }
                using var tbl = new MemoryStream();
                tbl.Write(W16b(0)); tbl.Write(W16b((ushort)names.Length)); tbl.Write(W16b((ushort)(6 + names.Length * 12)));
                rec.Position = 0; rec.CopyTo(tbl);
                strs.Position = 0; strs.CopyTo(tbl);
                byte[] nt = tbl.ToArray();
                using var f = new MemoryStream();
                f.Write(W32b(0x00010000)); f.Write(W16b(1)); f.Write(W16b(0)); f.Write(W16b(0)); f.Write(W16b(0));   // sfnt header
                f.Write(new byte[] { (byte)'n', (byte)'a', (byte)'m', (byte)'e' }); f.Write(W32b(0)); f.Write(W32b(28)); f.Write(W32b((uint)nt.Length));
                f.Write(nt);
                return f.ToArray();
            }
            Check("FontProbe/name表 en-US 胜过 zh-CN",
                FontProbe.ReadFamilyNameFromBytes(BuildFont(
                    (3, 0x0804, 1, "思源宋体"), (3, 0x0409, 1, "Noto Serif SC"))) == "Noto Serif SC");
            Check("FontProbe/name表 ID16 胜过 ID1",
                FontProbe.ReadFamilyNameFromBytes(BuildFont(
                    (3, 0x0409, 1, "Source Han Serif SC Regular"), (3, 0x0409, 16, "Source Han Serif SC"))) == "Source Han Serif SC");
            Check("FontProbe/name表 Mac 平台兜底",
                FontProbe.ReadFamilyNameFromBytes(BuildFont((1, 0, 1, "Fallback Mac Fam"))) == "Fallback Mac Fam");
            byte[] okFont = BuildFont((3, 0x0409, 1, "X Fam"));
            Check("FontProbe/截断字节不崩", FontProbe.ReadFamilyNameFromBytes(okFont[..(okFont.Length - 3)]) is "X Fam" or null);
            Check("FontProbe/垃圾头为 null", FontProbe.ReadFamilyNameFromBytes(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 }) == null);
            Check("FontProbe/超短输入为 null", FontProbe.ReadFamilyNameFromBytes(new byte[] { 1, 2, 3 }) == null);

            // 本机真文件验证(存在性守卫,异机自动跳过):VF 真名 + TTC 头穿透 + 端到端探测
            const string vfPath = @"C:\Windows\Fonts\NotoSerifSC-VF.ttf";
            if (File.Exists(vfPath))
                Check("FontProbe/真 VF 族名=Noto Serif SC", FontProbe.ReadFamilyName(vfPath) == "Noto Serif SC");
            if (File.Exists(@"C:\Windows\Fonts\simsun.ttc"))
                Check("FontProbe/真 TTC 穿透首字族名", (FontProbe.ReadFamilyName(@"C:\Windows\Fonts\simsun.ttc") ?? "").StartsWith("SimSun"));
            string e2e = FontProbe.ProbeFamily();
            Check("FontProbe/端到端族名合法", e2e == null || e2e is "Noto Serif SC" or "Source Han Serif SC" or "Noto Serif CJK SC" or "SimSun", "e2e=" + (e2e ?? "null"));
        }

        ArchitectureTests.Run(Check);
        DocumentWorkflowTests.Run(Check);
        LayoutCacheTests.Run(Check);
        BlockTextTests.Run(Check);
        RecoveryTests.Run(Check);
        MaturityTests.Run(Check);
        ParserIsolationTests.Run(Check);
        DocumentCancelTests.Run(Check);
        UndoBudgetTests.Run(Check);
        Console.WriteLine(Failures == 0 ? "=== ALL PASS ===" : $"=== {Failures} FAILURES ===");
        return Failures;
    }
}
