using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// Items 26-28: front matter, GitHub alerts, task-list toggling. Parser/export/source-mapping contracts only.
internal static class ProductRenderTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        // 自用走查:上/下标与脚注号只缩字号不离基线,"x^2^" 读作 x2、"log~2~8" 读作 log28。数字与 + - = ( ) 显示为 Unicode 上/下标(正文字体自带),
        // 逐字 1:1 替换只作用于显示;其余字符保持缩小字号回退。
        static string Shown(Run r, out bool full) => DocView.ShownRunText(r, out full);
        string sup = Shown(new Run("2") { SupSub = 1 }, out bool supFull);
        check("Script/digit superscript shown raised", sup == "²" && supFull, sup);
        string sub = Shown(new Run("2") { SupSub = 2 }, out bool subFull);
        check("Script/digit subscript shown lowered", sub == "₂" && subFull, sub);
        string word = Shown(new Run("st") { SupSub = 1 }, out bool wordFull);
        check("Script/letters keep small-size fallback", word == "st" && !wordFull, word);
        // 脚注号不替换:Unicode 上标数字在正文字体里仅约 4px 宽,既难察觉也难点中;保留 "^1" 小字号(可点区域约 12px)
        string fn = Shown(new Run("^12") { Fn = true }, out bool fnFull);
        check("Script/footnote marker keeps clickable caret form", fn == "^12" && !fnFull, fn);
        string fnName = Shown(new Run("^note") { Fn = true }, out bool fnNameFull);
        check("Script/named footnote keeps marker", fnName == "^note" && !fnNameFull && Shown(new Run("x2"), out bool plainFull) == "x2" && !plainFull, fnName);
        string Html(string md)
        {
            string h = HtmlExport.Build(MdParser.Parse(md), "test", false);
            int start = h.IndexOf("<body>\n", StringComparison.Ordinal) + 7;
            return h[start..h.IndexOf("</body>", StringComparison.Ordinal)].Replace("\n", "");
        }
        void C(string name, bool ok, string detail = "") => check("ProductRender/" + name, ok, detail);

        // 26 front matter
        var fm = MdParser.Parse("---\ntitle: Demo\ntags: [a, b]\n---\n# Heading\n");
        C("front matter becomes a marked yaml block", fm.Count == 2 && fm[0] is CodeBlock { FrontMatter: true, Lang: "yaml" } cb && cb.Text == "title: Demo\ntags: [a, b]\n" && fm[1] is Heading, "isolated from body");
        C("front matter export is a distinct pre", Html("---\ntitle: Demo\n---\nbody\n").StartsWith("<pre class=\"frontmatter\"><code>title: Demo</code></pre><p>body</p>"), "class carries styling only");
        C("front matter requires first line", MdParser.Parse("\n---\ntitle: x\n---\n").All(b => b is not CodeBlock { FrontMatter: true }), "leading blank line disables");
        C("front matter requires a key line", MdParser.Parse("---\njust text\n---\n").All(b => b is not CodeBlock { FrontMatter: true }), "thematic break plus setext heading stays CommonMark");
        C("front matter allows dots terminator", MdParser.Parse("---\na: 1\n...\nbody\n")[0] is CodeBlock { FrontMatter: true }, "YAML document end");
        C("front matter unterminated stays body", MdParser.Parse("---\na: 1\nb: 2\n").All(b => b is not CodeBlock { FrontMatter: true }), "no closing fence");
        C("front matter bounded scan", MdParser.Parse("---\na: 1\n" + string.Concat(Enumerable.Repeat("x\n", 250)) + "---\n").All(b => b is not CodeBlock { FrontMatter: true }), "200 line limit");
        C("crlf front matter", MdParser.Parse("---\r\ntitle: x\r\n---\r\nbody\r\n")[0] is CodeBlock { FrontMatter: true } crlf && crlf.Text == "title: x\n", "line endings normalized");

        // 27 alerts
        var alert = MdParser.Parse("> [!WARNING]\n> Careful here.\n");
        C("alert produces synthetic title then body", alert.Count == 2 && alert[0] is Quote { AlertTitle: true, Alert: "warning" } t && string.Concat(t.Inlines.Select(r => r.Text)) == "Warning" && alert[1] is Quote { Alert: "warning", AlertTitle: false } body && string.Concat(body.Inlines.Select(r => r.Text)) == "Careful here.", "marker line consumed");
        C("alert export classes", Html("> [!NOTE]\n> text\n") == "<blockquote class=\"alert alert-note\"><p class=\"alert-title\"><strong>Note</strong></p><p>text</p></blockquote>", "title bold in both renderers");
        C("alert kinds", new[] { "NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION" }.All(k => MdParser.Parse("> [!" + k + "]\n> x\n")[0].Alert == k.ToLowerInvariant()), "five GitHub kinds");
        // S75 起兼容 Obsidian 提示框(GitHub 五种为其子集):小写类型、标记后跟标题都识别(原为严格 GitHub 语法,有意变更)。
        C("lowercase marker is a callout", MdParser.Parse("> [!note]\n> x\n")[0].Alert == "note", "Obsidian case-insensitive");
        C("marker with trailing text becomes title", MdParser.Parse("> [!NOTE] inline\n> x\n") is var ti && ti[0] is Quote { AlertTitle: true } tq && string.Concat(tq.Inlines.Select(r => r.Text)) == "inline", "Obsidian custom title");
        C("alert only marker still renders title", MdParser.Parse("> [!TIP]\n").Count == 1 && MdParser.Parse("> [!TIP]\n")[0].AlertTitle, "empty body");
        C("nested alert depth", MdParser.Parse("> > [!NOTE]\n> > deep\n") is var nested && nested.Count == 2 && nested[0].QuoteDepth == 2 && nested[0].Alert == "note", "inner quote hosts alert");
        C("alert with list body", MdParser.Parse("> [!IMPORTANT]\n> - a\n> - b\n") is var lst && lst.Count == 3 && lst[1] is ListItem && lst[2].Alert == "important", "structure preserved");
        C("ordinary quote unaffected", MdParser.Parse("> plain\n")[0] is Quote { Alert: null, AlertTitle: false }, "negative control");

        // 28 task list source mapping
        string src = "- [ ] one\n- [x] two\n\n```md\n- [ ] not a task\n```\n\n> - [ ] quoted\n1. [X] ordered\n* text\n  - [ ] nested\n";
        var marks = TaskListSource.Locate(src);
        C("locate finds document-order markers", marks.Count == 5 && marks.Select(m => src[m]).SequenceEqual(new[] { ' ', 'x', ' ', 'X', ' ' }), "fence skipped; quotes and nesting included");
        int rendered = 0;
        foreach (var b in MdParser.Parse(src)) if (b is ListItem li) { if (li.Task > 0) rendered++; if (li.Sub != null) rendered += li.Sub.OfType<ListItem>().Count(s => s.Task > 0); }
        C("renderer and source agree on count", rendered == marks.Count, "rendered=" + rendered + " source=" + marks.Count);
        string toggled = TaskListSource.Toggle(src, 0, marks.Count);
        C("toggle unchecked to checked", toggled != null && toggled.StartsWith("- [x] one\n- [x] two") && toggled.Length == src.Length, "single character edit");
        C("toggle checked to unchecked", TaskListSource.Toggle(src, 1, marks.Count).StartsWith("- [ ] one\n- [ ] two"), "x cleared");
        C("toggle refuses count mismatch", TaskListSource.Toggle(src, 0, marks.Count + 1) == null, "never guesses");
        C("toggle refuses out of range", TaskListSource.Toggle(src, 5, marks.Count) == null, "bounds");
        C("toggle preserves crlf", TaskListSource.Toggle("- [ ] a\r\n- [ ] b\r\n", 1, 2) == "- [ ] a\r\n- [x] b\r\n", "line endings untouched");
        C("tilde fence skipped", TaskListSource.Locate("~~~\n- [ ] x\n~~~\n- [ ] y\n").Count == 1, "alternate fence");
        C("marker requires following space", TaskListSource.Locate("- [ ]x\n- [x]\n").Count == 0, "matches parser rule");

        // 41 copy as HTML
        using (var view = new DocView())
        {
            view.Load("# Title\n\nPlain **bold** and `code` here.\n\n- item one\n- item two\n");
            string all = view.SearchText();
            view.SelectRange(0, all.Length);
            var rich = view.CopyRich();
            C("copy rich returns text and html", rich.Text == view.CopyText() && rich.Html.Contains("<h1") && rich.Html.Contains("<strong>bold</strong>") && rich.Html.Contains("<ul>"), "same emitter as export");
            int boldAt = all.IndexOf("bold", StringComparison.Ordinal);
            view.SelectRange(boldAt - 2, boldAt + 7);   // "n bold an"
            var part = view.CopyRich();
            C("copy rich slices runs", part.Text == "n bold an" && part.Html == "<p>n <strong>bold</strong> an</p>\n", "html=" + part.Html);
            int itemAt = all.IndexOf("item two", StringComparison.Ordinal);
            view.SelectRange(itemAt, itemAt + 4);
            var li = view.CopyRich();
            C("copy rich keeps list structure", li.Html.StartsWith("<ul>") && li.Html.Contains("<li>item</li>"), "html=" + li.Html);
            view.SelectRange(3, 3);
            C("copy rich needs a selection", view.CopyRich().Text == null, "empty selection");
        }
        byte[] cf = ClipboardHtml.Wrap("<p>héllo</p>");
        string cfText = System.Text.Encoding.UTF8.GetString(cf, 0, cf.Length - 1);
        int sf = int.Parse(cfText.Substring(cfText.IndexOf("StartFragment:", StringComparison.Ordinal) + 14, 10));
        int ef = int.Parse(cfText.Substring(cfText.IndexOf("EndFragment:", StringComparison.Ordinal) + 12, 10));
        int sh = int.Parse(cfText.Substring(cfText.IndexOf("StartHTML:", StringComparison.Ordinal) + 10, 10));
        int eh = int.Parse(cfText.Substring(cfText.IndexOf("EndHTML:", StringComparison.Ordinal) + 8, 10));
        C("clipboard html offsets are byte accurate", System.Text.Encoding.UTF8.GetString(cf, sf, ef - sf) == "<p>héllo</p>" && System.Text.Encoding.UTF8.GetString(cf, sh, eh - sh).StartsWith("<html>") && eh == cf.Length - 1 && cf[^1] == 0, "sf=" + sf + " ef=" + ef);

        // 42 conflict diff
        var d1 = TextDiff.Unified("a\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\nf\n");
        C("diff counts and hunks", d1.Removed == 1 && d1.Added == 2 && !d1.Truncated && d1.Lines[0].StartsWith("@@") && d1.Lines.Contains("- b") && d1.Lines.Contains("+ B") && d1.Lines.Contains("+ f"), string.Join("|", d1.Lines));
        C("diff identical is empty", TextDiff.Unified("x\ny\n", "x\r\ny\r\n").Lines.Count == 0, "line endings ignored");
        C("diff empty old", TextDiff.Unified("", "one\ntwo\n") is { Added: 2, Removed: 0 }, "all added");
        C("diff empty new", TextDiff.Unified("one\ntwo\n", "") is { Added: 0, Removed: 2 }, "all removed");
        var big = string.Concat(Enumerable.Range(0, 5000).Select(i => "line " + i + "\n"));
        var dBig = TextDiff.Unified(big, big.Replace("line 2500\n", "changed\n"));
        C("diff large common affix stays exact", dBig.Removed == 1 && dBig.Added == 1 && dBig.Lines.Count <= 7, "prefix/suffix trimmed before LCS");
        var huge = TextDiff.Unified(string.Concat(Enumerable.Range(0, 3000).Select(i => "a" + i + "\n")), string.Concat(Enumerable.Range(0, 3000).Select(i => "b" + i + "\n")));
        C("diff output is bounded", huge.Truncated && huge.Lines.Count == TextDiff.MaxOutputLines + 1 && huge.Removed + huge.Added == 6000, "counts exact, list capped");
        var (desc, lines) = DocumentDialogs.ConflictDiffLines(null, "draft\n");
        C("conflict diff handles deleted disk file", desc.Contains("删除") && lines.Contains("+ draft"), desc);
        C("conflict diff explains format-only difference", DocumentDialogs.ConflictDiffLines("same\n", "same\r\n").Lines[0].Contains("相同"), "encoding/newline/BOM note");
        C("conflict command keeps decisions", Enum.GetNames(typeof(ConflictChoice)).SequenceEqual(new[] { "Cancel", "SaveAs", "Overwrite" }), "no new destructive outcome");

        // 36 path maintenance on save-as
        string oldDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-rebase-old"), newDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-rebase-new", "deep");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.Combine(oldDir, "img", "a.png"), System.IO.Path.Combine(oldDir, "notes.md"), System.IO.Path.Combine(oldDir, "my pic.png") };
        bool Exists(string p) => known.Contains(p);
        string src36 = "![a](img/a.png)\n[n](notes.md#sec \"t\")\n![m](<my pic.png>)\n[x](img/missing.png)\n[u](https://h/x.png)\n[abs](C:/abs/x.png)\n[anchor](#top)\n\n```\n![code](img/a.png)\n```\n\n[ref]: img/a.png\n";
        var plan = DocumentPaths.Rebase(src36, oldDir, newDir, Exists);
        string expect36 = "![a](../../mdv-rebase-old/img/a.png)\n[n](../../mdv-rebase-old/notes.md#sec \"t\")\n![m](<../../mdv-rebase-old/my pic.png>)\n[x](img/missing.png)\n[u](https://h/x.png)\n[abs](C:/abs/x.png)\n[anchor](#top)\n\n```\n![code](img/a.png)\n```\n\n[ref]: ../../mdv-rebase-old/img/a.png\n";
        C("rebase rewrites only existing relative refs", plan.Count == 4 && plan.Text == expect36, plan.Text);
        C("rebase same directory is a no-op", DocumentPaths.Rebase(src36, oldDir, oldDir, Exists).Count == 0, "identity");
        C("rebase keeps percent encoding style", DocumentPaths.Rewrite("my%20pic.png", oldDir, newDir, Exists) == "../../mdv-rebase-old/my%20pic.png", "encoded stays encoded");
        C("rebase refuses unknown files", DocumentPaths.Rewrite("img/missing.png", oldDir, newDir, Exists) == null, "never invents paths");

        // 35 cache management entry
        C("image cache command registered", Commands.All.Any(c => c.Id == 2243 && (c.Face & CmdFace.Maintenance) != 0), "maintenance submenu");
        string cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-cache-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(cacheDir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(cacheDir, "a.image"), new byte[10]);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(cacheDir, "b.part"), new byte[5]);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(cacheDir, "keep.txt"), new byte[1]);
        var usage = RemoteImages.CacheUsage(cacheDir);
        int cleared = RemoteImages.ClearCache(cacheDir);
        C("remote cache usage and clear", usage == (2, 15) && cleared == 2 && System.IO.File.Exists(System.IO.Path.Combine(cacheDir, "keep.txt")) && RemoteImages.CacheUsage(cacheDir) == (0, 0), "only cache extensions touched");
        C("remote cache missing directory", RemoteImages.CacheUsage(System.IO.Path.Combine(cacheDir, "none")) == (0, 0) && RemoteImages.ClearCache(System.IO.Path.Combine(cacheDir, "none")) == 0, "no throw");
        try { System.IO.Directory.Delete(cacheDir, true); } catch (System.IO.IOException) { }
        using (var view = new DocView())
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-cache-doc-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.Copy("../samples/test_png.png", System.IO.Path.Combine(dir, "p.png"));
            view.DocumentDirectory = dir;
            view.Load("![p](p.png)\n");
            var before = view.ImageCacheUsage;
            view.ReleaseRenderCache();
            var after = view.ImageCacheUsage;
            C("document image usage reflects release", before.Count == 1 && before.Pixels > 0 && after == (0, 0UL), "before=" + before + " after=" + after);
            try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
        }

        // 34 animated GIF: fixtures generated externally and cross-checked with Pillow (composited RGBA per frame)
        static uint Px(GifAnimation.Frame f, int i) => BitConverter.ToUInt32(f.Bgra, i * 4);
        var two = GifAnimation.Decode(Convert.FromBase64String("R0lGODlhAQABAIEAAP8AAAAA/wD/AAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQABQAAACwAAAAAAQABAAACAkQBACH5BAAFAAAALAAAAAABAAEAAAICTAEAOw=="));
        C("gif two frames decode", two.Width == 1 && two.Height == 1 && two.Frames.Count == 2 && two.LoopCount == 0 && !two.Truncated && Px(two.Frames[0], 0) == 0xFFFF0000 && Px(two.Frames[1], 0) == 0xFF0000FF && two.Frames[0].DelayMs == 50, "frames=" + two.Frames.Count);
        var disp = GifAnimation.Decode(Convert.FromBase64String("R0lGODlhAwACAIEAAP8AAAAA/wD/AAAAACH/C05FVFNDQVBFMi4wAwECAAAh+QQECgAAACwAAAAAAwACAAACAoRfACH5BAgAAAAALAAAAAABAAEAAAICTAEAIfkEABkAAAAsAgABAAEAAQAAAgJUAQAh+QQNBQADACwBAAAAAQABAAACAlwBADs="));
        bool dispOk = disp.Frames.Count == 4 && disp.LoopCount == 2
            && Px(disp.Frames[0], 0) == 0xFFFF0000 && disp.Frames[0].DelayMs == 100
            && Px(disp.Frames[1], 0) == 0xFF0000FF && disp.Frames[1].DelayMs == 100          // 0 ms clamps to 100
            && Px(disp.Frames[2], 0) == 0x00000000 && Px(disp.Frames[2], 5) == 0xFF00FF00 && disp.Frames[2].DelayMs == 250   // disposal 2 clears (0,0) to transparent (browser semantics; Pillow would paint the background index)
            && Px(disp.Frames[3], 5) == 0xFF00FF00 && Px(disp.Frames[3], 1) == 0xFFFF0000 && disp.Frames[3].DelayMs == 50;   // transparent pixel leaves canvas
        C("gif disposal transparency and delays match browser reference", dispOk, string.Join(",", disp.Frames.ConvertAll(f => Px(f, 0).ToString("X8") + "/" + Px(f, 5).ToString("X8") + "@" + f.DelayMs)));
        var lct = GifAnimation.Decode(Convert.FromBase64String("R0lGODlhAgABAIEAAP8AAAAA/wD/AAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQABQAAACwAAAAAAgABAAACAkQKACH5BAAFAAAALAAAAAACAAEAgQD///8A/wAAAAAAAAICRAoAOw=="));
        C("gif local color table", lct.Frames.Count == 2 && Px(lct.Frames[1], 0) == 0xFF00FFFF && Px(lct.Frames[1], 1) == 0xFFFF00FF, "second frame uses its own palette");
        var il = GifAnimation.Decode(Convert.FromBase64String("R0lGODlhAgAEAIEAAP8AAAAA/wD/AAAAACwAAAAAAgAEAEACBQQkETMFADs="));
        C("gif interlaced rows", il.Frames.Count == 1 && Px(il.Frames[0], 0) == 0xFFFF0000 && Px(il.Frames[0], 2) == 0xFF0000FF && Px(il.Frames[0], 4) == 0xFF00FF00 && Px(il.Frames[0], 6) == 0xFF000000, "row order restored");
        var trunc = GifAnimation.Decode(Convert.FromBase64String("R0lGODlhAQABAIEAAP8AAAAA/wD/AAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQABQAAACwAAAAAAQABAAACAkQBACH5BAAFAAAALAAAAAABAAEAAAI="));
        C("gif truncated file keeps decoded frames", trunc.Frames.Count >= 1 && trunc.Truncated && Px(trunc.Frames[0], 0) == 0xFFFF0000, "no exception, partial result flagged");
        bool rejected = false;
        try { GifAnimation.Decode(new byte[] { 1, 2, 3 }); } catch (System.IO.InvalidDataException) { rejected = true; }
        C("gif rejects non-gif", rejected, "InvalidDataException");
        string gifDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-gif-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(gifDir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(gifDir, "two.gif"), Convert.FromBase64String("R0lGODlhAQABAIEAAP8AAAAA/wD/AAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQABQAAACwAAAAAAQABAAACAkQBACH5BAAFAAAALAAAAAABAAEAAAICTAEAOw=="));
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(gifDir, "one.gif"), Convert.FromBase64String("R0lGODlhAgACAIEAAP8AAAAA/wD/AAAAACwAAAAAAgACAAACA0Q0BQA7"));
        C("gif animated pre-check", GifAnimation.IsAnimated(System.IO.Path.Combine(gifDir, "two.gif")) && !GifAnimation.IsAnimated(System.IO.Path.Combine(gifDir, "one.gif")) && !GifAnimation.IsAnimated(System.IO.Path.Combine(gifDir, "missing.gif")), "cheap descriptor count");
        bool savedFlag = DocumentImages.AnimationEnabled;
        try
        {
            DocumentImages.AnimationEnabled = true;
            using var view = new DocView();
            view.DocumentDirectory = gifDir;
            view.Load("![t](two.gif)\n\n![s](one.gif)\n");
            nint first = view.GetImageBmp("two.gif");
            var gifUsage = view.ImageCacheUsage;
            C("gif frames enter the pixel budget", view.HasAnimatedImages && first != 0 && gifUsage.Pixels == 1 + 2 + 4, "static 4px + first frame 1px + 2 frames: " + gifUsage.Pixels);
            int due0 = view.NextAnimationDueMs();
            bool changed0 = view.AdvanceAnimations();   // arms the clock
            System.Threading.Thread.Sleep(80);
            bool changed1 = view.AdvanceAnimations();
            C("gif animation advances by delay", due0 == 0 && !changed0 && changed1 && view.NextAnimationDueMs() is >= 0 and <= 50, "due0=" + due0 + " next=" + view.NextAnimationDueMs());
            view.ReleaseRenderCache();
            C("gif release frees frames", view.ImageCacheUsage == (0, 0UL) && !view.HasAnimatedImages, "no leak in budget accounting");
            DocumentImages.AnimationEnabled = false;
            using var still = new DocView();
            still.DocumentDirectory = gifDir;
            still.Load("![t](two.gif)\n");
            C("gif animation disabled keeps first frame only", still.GetImageBmp("two.gif") != 0 && !still.HasAnimatedImages && still.NextAnimationDueMs() == -1 && still.ImageCacheUsage.Pixels == 1, "preference/system animation off");
        }
        finally { DocumentImages.AnimationEnabled = savedFlag; try { System.IO.Directory.Delete(gifDir, true); } catch (System.IO.IOException) { } }
        C("gif animation preference defaults on and is bounded", new PreferenceData().AnimateImages && GifAnimation.MaxFrames == 200 && GifAnimation.MaxTotalPixels == 32UL * 1024 * 1024, "budget constants");
    }
}
