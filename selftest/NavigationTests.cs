using System;
using System.Linq;
using System.Threading.Tasks;
using MDViewerNative;

internal static class NavigationTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        const string md = "# Quick **Start**\n\n# Quick Start\n\n# Quick Start-1\n\n# 中文 标题！\n\n# !!!\n\n- # In List\n\n  body\n\n  ## Lower Heading\n\n> # Quoted Heading\n";
        var blocks = MdParser.Parse(md);
        var anchors = new DocumentAnchors(blocks);
        string[] ids = { "quick-start", "quick-start-1", "quick-start-1-1", "中文-标题", "section", "in-list", "lower-heading", "quoted-heading" };
        foreach (string id in ids)
            check("Navigation/heading namespace " + id, anchors.TryFind(Uri.EscapeDataString(id), out var a) && a.Id == id, "unique ids include Unicode, quoted and list headings");
        check("Navigation/legacy title alias", anchors.TryFind("Quick%20Start", out var legacy) && legacy.Id == "quick-start", "old title-text links still work");
        check("Navigation/duplicate canonical id wins", anchors.TryFind("quick-start-1", out var duplicate) && duplicate.Title == "Quick Start", "do not confuse suffix collision with a title alias");
        check("Navigation/unknown anchor fails", !anchors.TryFind("not-here", out _) && !anchors.TryFind("bad%ZZ", out _), "no silent success for unknown destinations");
        string exported = HtmlExport.Build(blocks, "test", false);
        foreach (string id in ids) check("Navigation/export id " + id, exported.Contains("id=\"" + id + "\""), "same namespace as native navigation");
        check("Navigation/export resolves legacy title", HtmlExport.Build(MdParser.Parse("[go](#Quick%20Start)\n\n# Quick Start\n"), "t", false).Contains("href=\"#quick-start\""), "browser href agrees with emitted id");
        check("Navigation/export links in tables", HtmlExport.Build(MdParser.Parse("# Quick Start\n\n| a |\n| --- |\n| [go](#Quick%20Start) |\n"), "t", false).Contains("href=\"#quick-start\""), "context reaches table runs");
        check("Navigation/export linked image", HtmlExport.Build(MdParser.Parse("# Quick Start\n\n[![alt](a.png)](#Quick%20Start)\n"), "t", false).Contains("href=\"#quick-start\""), "image outer link uses the same resolution");
        string foot = HtmlExport.Build(MdParser.Parse("# fn-note\n\ntext[^note]\n\n[^note]: definition\n"), "t", false);
        check("Navigation/footnote id collision", foot.Contains("id=\"fn-note-1\"") && foot.Contains("id=\"fn-note\""), "heading ids do not steal footnote targets");
        using var view = new DocView();
        view.Load(md);
        view.OnWidth(596f);
        float previous = -1;
        foreach (string id in ids)
        {
            bool found = view.TryAnchorJump(Uri.EscapeDataString(id), out float y);
            check("Navigation/native target position " + id, found && y > previous, "source order, including two headings within one list item");
            previous = y;
        }
        view.OnWidth(360f);
        check("Navigation/resize retains destinations", view.TryAnchorJump("lower-heading", out _) && view.TryAnchorJump("", out float top) && top == 0f, "empty fragment goes to document top");
        view.Load("# Different\n");
        check("Navigation/reload clears old anchors", !view.TryAnchorJump("quick-start", out _) && view.TryAnchorJump("different", out _), "no stale headings after replacing a document");
        int errors = 0;
        Parallel.For(0, 80, iteration => { var a = new DocumentAnchors(MdParser.Parse("# X\n# X\n")); if (!a.TryFind("x-1", out _)) System.Threading.Interlocked.Increment(ref errors); });
        check("Navigation/parallel anchor isolation", errors == 0, "no global heading counter");
        var repeated = new DocumentAnchors(MdParser.Parse(string.Concat(Enumerable.Repeat("# X\n", 10000))));
        check("Navigation/duplicate work is bounded", repeated.TryFind("x-9999", out _), "per-base suffix cursor, not rescanning all prior suffixes");

        const string dir = @"C:\docs";
        foreach (var (url, kind, path, fragment) in new[] {
            ("#中文%20标题", LinkKind.Anchor, "", "中文%20标题"),
            ("other%20file.md#install", LinkKind.Markdown, @"C:\docs\other file.md", "install"),
            ("../guide.markdown#中文", LinkKind.Markdown, @"C:\guide.markdown", "中文"),
            ("a%23b.md#section", LinkKind.Markdown, @"C:\docs\a#b.md", "section"),
            ("a%2520b.md", LinkKind.Markdown, @"C:\docs\a%20b.md", ""),
            ("file:///C:/docs/a%2520b.md#section", LinkKind.Markdown, @"C:\docs\a%20b.md", "section"),
            (@"C:\docs\absolute.md#target", LinkKind.Markdown, @"C:\docs\absolute.md", "target"),
            ("notes.txt", LinkKind.Markdown, @"C:\docs\notes.txt", ""),
            ("report.pdf#page=2", LinkKind.ExternalFile, @"C:\docs\report.pdf", "page=2") })
        {
            bool ok = DocumentLinks.TryResolve(url, dir, out var target, out string error);
            check("Navigation/resolve " + url, ok && target.Kind == kind && target.Location == path && target.Fragment == fragment, error);
        }
        foreach (string url in new[] { "https://example.org/a?q=x#part", "http://example.org", "www.example.org/a" })
            check("Navigation/web " + url, DocumentLinks.TryResolve(url, dir, out var target, out _) && target.Kind == LinkKind.Web, "validate and normalize without launching browser");
        check("Navigation/mailto", DocumentLinks.TryResolve("mailto:a@example.org?subject=Hello%20World", dir, out var mail, out _) && mail.Kind == LinkKind.Mail, "mail handler selected without launching an app");
        check("Navigation/network needs host confirmation", DocumentLinks.TryResolve(@"\\server\share\readme.md", dir, out var network, out _) && network.NetworkFile, "resolver never probes a network share");
        // Obsidian 双链(S75):同目录没有目标时,在含 .obsidian 的库根目录内按文件名查找;不在库内则不找。
        string vault = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdv-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(vault, "notes", "deep"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(vault, "daily"));
            string real = System.IO.Path.Combine(vault, "notes", "deep", "Target Note.md");
            System.IO.File.WriteAllText(real, "# t");
            string missing = System.IO.Path.Combine(vault, "daily", "Target Note.md");
            check("Navigation/wikilink outside vault not searched", WikiLinks.FindInVault(missing) == null, "no .obsidian yet");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(vault, ".obsidian"));
            string found = WikiLinks.FindInVault(missing);
            check("Navigation/wikilink resolves by name in vault", string.Equals(found, real, StringComparison.OrdinalIgnoreCase), found ?? "null");
        }
        finally { try { System.IO.Directory.Delete(vault, true); } catch (System.IO.IOException) { } }
        foreach (string bad in new[] { "javascript:alert(1)", "data:text/html,x", "vscode:open", "javascript%3Aalert(1)", "a.md:stream", "file:///C:/docs/a.md:stream", "bad%ZZ.md", "a%00.md", "a%0A.md", "#bad%00", "#bad%ZZ", "https://", "mailto:", "\nhttps://example.org", @"C:relative.md" })
            check("Navigation/reject unsafe or invalid " + bad.Replace('\n', ' '), !DocumentLinks.TryResolve(bad, dir, out _, out _), "no shell or filesystem side effects");
        check("Navigation/unnamed relative path rejected", !DocumentLinks.TryResolve("readme.md", "", out _, out _), "do not silently resolve relative to process working directory");
        check("Navigation/unnamed absolute path allowed", DocumentLinks.TryResolve(@"C:\docs\readme.md", "", out _, out _), "absolute links remain usable");
    }
}
