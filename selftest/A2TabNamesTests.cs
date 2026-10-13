using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

internal static class A2TabNamesTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "display-only naming; source identity and path unchanged") => check("A2Names/" + name, ok, detail);
        static ToolbarTab Tab(string name, string path, int id, bool dirty = false)
            => new(Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"), name, path, id == 1, dirty);
        static bool Unique(IEnumerable<ToolbarTab> tabs) => tabs.Select(t => t.DisplayTitle).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tabs.Count();
        var names = new TabDisplayNames();
        var original = new[] { Tab("Readme.md", @"C:\notes\Readme.md", 1, true) };
        var single = names.Project(original);
        C("terminal md hidden", single[0].DisplayTitle == "Readme");
        C("raw title path and identity retained", original[0].Title == "Readme.md" && single[0].Title == original[0].Title && single[0].FullPath == original[0].FullPath && single[0].Id == original[0].Id);
        C("status remains separate", single[0].Dirty && single[0].Active && single[0].MenuLabel(false).StartsWith("Readme [") && single[0].MenuLabel(false).Contains("未保存"));
        foreach (var item in new[] { ("README.MD", "README"), ("note.markdown", "note.markdown"), ("report.md.md", "report.md"), (".md", ".md"), ("NoExtension", "NoExtension"), ("中😀文.md", "中😀文"), ("a.md.backup", "a.md.backup") })
            C("extension rule " + item.Item1, names.Project(new[] { Tab(item.Item1, @"C:\one\" + item.Item1, 1) })[0].DisplayTitle == item.Item2);

        var collision = new[] { Tab("Readme.md", @"C:\a\Readme.md", 1), Tab("readme.MD", @"C:\b\readme.MD", 2) };
        var projected = names.Project(collision);
        C("case-insensitive display collision", projected[0].DisplayTitle == "a › Readme" && projected[1].DisplayTitle == "b › readme" && Unique(projected));
        var depths = names.Project(new[] { Tab("same.md", @"C:\one\shared\same.md", 1), Tab("same.md", @"C:\two\shared\same.md", 2), Tab("same.md", @"C:\unique\same.md", 3) });
        C("minimum suffix chosen independently", depths[0].DisplayTitle == @"one\shared › same" && depths[1].DisplayTitle == @"two\shared › same" && depths[2].DisplayTitle == "unique › same");
        var drives = names.Project(new[] { Tab("x.md", @"C:\work\docs\x.md", 1), Tab("x.md", @"D:\work\docs\x.md", 2) });
        C("drive needed only when parent suffixes match", drives[0].DisplayTitle.StartsWith(@"C:\work\docs") && drives[1].DisplayTitle.StartsWith(@"D:\work\docs") && Unique(drives));
        var roots = names.Project(new[] { Tab("x.md", @"C:\x.md", 1), Tab("x.md", @"D:\x.md", 2) });
        C("drive-root parent not lost", roots[0].DisplayTitle == @"C:\ › x" && roots[1].DisplayTitle == @"D:\ › x");
        var unc = names.Project(new[] { Tab("x.md", @"\\server-a\share\docs\x.md", 1), Tab("x.md", @"\\server-b\share\docs\x.md", 2) });
        C("UNC roots distinguish servers", unc[0].DisplayTitle.StartsWith(@"\\server-a\share\docs") && unc[1].DisplayTitle.StartsWith(@"\\server-b\share\docs") && Unique(unc));
        var mixed = names.Project(new[] { Tab("x.md", "C:/notes/one/x.md", 1), Tab("x.md", @"C:\notes\two\x.md", 2) });
        C("mixed slash syntax handled without mutating path", mixed[0].DisplayTitle == "one › x" && mixed[1].DisplayTitle == "two › x" && mixed[0].FullPath == "C:/notes/one/x.md");
        var sameParent = names.Project(new[] { Tab("Readme.md", @"C:\same\Readme.md", 1), Tab("Readme", @"C:\same\Readme", 2) });
        C("hidden extension collision in same directory stays unambiguous", Unique(sameParent) && sameParent[0].DisplayTitle.Contains(sameParent[0].FullPath) && sameParent[1].DisplayTitle.Contains(sameParent[1].FullPath));

        var crafted = new[] { collision[0], collision[1], Tab("a › Readme.md", @"C:\literal\a › Readme.md", 3) };
        var qualified = names.Project(crafted);
        C("natural filename cannot collide with generated qualifier", Unique(qualified));
        var reverse = new TabDisplayNames().Project(crafted.Reverse().ToArray());
        C("naming independent of tab order", qualified.All(t => reverse.Single(r => r.Id == t.Id).DisplayTitle == t.DisplayTitle));
        int builds = names.RebuildCount;
        var changedState = names.Project(crafted.Reverse().Select(t => t with { Active = !t.Active, Dirty = !t.Dirty, ReadOnly = true, Pinned = true }).ToArray());
        C("dirty active readonly pinned do not rebuild names", names.RebuildCount == builds && changedState.All(t => qualified.Single(q => q.Id == t.Id).DisplayTitle == t.DisplayTitle));
        C("fresh state not cached away", changedState.All(t => t.ReadOnly && t.Pinned && t.Dirty != crafted.Single(q => q.Id == t.Id).Dirty));
        var renamed = crafted.Select(t => t.Id == collision[0].Id ? t with { Title = "Renamed.md", FullPath = @"C:\a\Renamed.md" } : t).ToArray();
        var afterRename = names.Project(renamed);
        C("name and path change invalidates naming cache", names.RebuildCount == builds + 1 && afterRename[0].DisplayTitle == "Renamed");
        names.Project(Array.Empty<ToolbarTab>());
        C("closed set releases name entries", names.CachedCount == 0);

        var unnamed = new[] { Tab("(未命名)", "", 1), Tab("(未命名)", "", 2) };
        var untitled = names.Project(unnamed);
        C("unnamed documents have identity-based labels", Unique(untitled) && untitled.All(t => t.DisplayTitle.Contains("未命名")));
        var loneUnnamed = names.Project(new[] { Tab("(未命名)", "", 3), Tab("a.md", @"C:\x\a.md", 4) });
        C("a single unnamed document has no identity suffix", loneUnnamed[0].DisplayTitle == "(未命名)");
        C("unnamed labels survive reorder", new TabDisplayNames().Project(unnamed.Reverse().ToArray()).All(t => untitled.Single(q => q.Id == t.Id).DisplayTitle == t.DisplayTitle));
        C("last unnamed label drops the identity suffix after peer close", names.Project(new[] { unnamed[1] })[0].DisplayTitle == "(未命名)");
        var literal = Tab("A&B • *.md", @"C:\literal\A&B • *.md", 1, true) with { ReadOnly = true, Pinned = true };
        var display = names.Project(new[] { literal })[0];
        C("special characters retained in display", display.DisplayTitle == "A&B • *");
        C("hint includes raw title full path and states", display.Hint.Contains(literal.Title) && display.Hint.Contains(literal.FullPath) && display.Hint.Contains("未保存") && display.Hint.Contains("只读") && display.Hint.Contains("固定"));
        C("list includes qualified display and full original path", display.MenuLabel(true).Contains(display.DisplayTitle) && display.MenuLabel(true).Contains(display.FullPath) && display.MenuLabel(true).Contains("未保存"));
        C("native ampersand escaping not baked into model", display.MenuLabel(true).Contains("A&B") && !display.MenuLabel(true).Contains("A&&B"));
        C("empty raw title never produces blank label", !string.IsNullOrWhiteSpace(names.Project(new[] { Tab("", "", 1) })[0].DisplayTitle));
        var equivalent = names.Project(new[] { Tab("same.md", @"C:\same.md", 1), Tab("same.md", @"C:\same.md", 2) });
        C("duplicate path snapshots still use distinct identities", Unique(equivalent));
        bool duplicateRejected = false;
        try { names.Project(new[] { literal, literal }); } catch (ArgumentException) { duplicateRejected = true; }
        C("duplicate identity rejected not silently rebound", duplicateRejected);

        var many = Enumerable.Range(1, 2000).Select(i => Tab("readme.md", @"C:\projects\folder-" + i + @"\readme.md", i)).ToArray();
        var all = names.Project(many);
        C("all documents including hidden tabs participate", all.Count == many.Length && Unique(all) && all.Select(t => t.Id).SequenceEqual(many.Select(t => t.Id)));
        C("short distinguishing context precedes long title budget", all[0].DisplayTitle.StartsWith("folder-1 › ") && all[^1].DisplayTitle.StartsWith("folder-2000 › "));
    }
}
