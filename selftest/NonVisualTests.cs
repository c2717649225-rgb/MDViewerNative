using System;
using System.Collections.Generic;
using MDViewerNative;

internal static class NonVisualTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("NonVisual/" + name, ok, "shared search, history, code-copy and input-state contracts");
        var exact = new SearchOptions(CaseSensitive: true);
        var whole = new SearchOptions(WholeWord: true);
        var both = new SearchOptions(true, true);
        C("legacy search remains case-insensitive literal", TextSearch.FindAll("Cat cat catalog", "cat").Count == 3);
        C("case option is ordinal and sensitive", TextSearch.FindAll("Cat cat catalog", "cat", exact).Count == 2);
        C("whole-word excludes identifier prefixes", TextSearch.FindAll("Cat cat catalog", "cat", whole).Count == 2);
        C("both options compose", TextSearch.FindAll("Cat cat catalog", "cat", both).Count == 1);
        C("identifier underscores and digits are word characters", TextSearch.FindAll("a_cat cat2 2cat cat", "cat", whole).Count == 1);
        C("combining marks are part of the word", TextSearch.FindAll("e\u0301 e", "e", whole).Count == 1);
        C("astral letters form word boundaries", TextSearch.FindAll("\U00010400cat cat", "cat", whole).Count == 1);
        C("emoji are not identifier letters", TextSearch.FindAll("😀cat😀", "cat", whole).Count == 1);
        C("CJK whole-word is a boundary rule not tokenization", TextSearch.FindAll("中文文档 中文", "中文", whole).Count == 1);
        C("valid surrogate pairs are never half selected", TextSearch.FindAll("😀", "\uD83D").Count == 0);
        C("literal regex characters remain literal", TextSearch.FindAll("a.*b aXXb", ".*", both).Count == 0 && TextSearch.FindAll("a.*b aXXb", ".*").Count == 1);
        C("empty query has no results", TextSearch.FindAll("text", "", both).Count == 0);
        C("single replacement cannot bypass word boundaries", !TextSearch.MatchesAt("catalog", 0, 3, "cat", whole));
        C("single replacement cannot bypass case", !TextSearch.MatchesAt("Cat", 0, 3, "cat", exact));
        C("single replacement validates its complete selection", !TextSearch.MatchesAt("cat", -1, 3, "cat") && !TextSearch.MatchesAt("cat", 0, 2, "cat"));
        var replaced = TextSearch.ReplaceAll("Cat cat catalog", "cat", "$1\\x", both);
        C("replacement options match search without regex expansion", replaced.count == 1 && replaced.text == "Cat $1\\x catalog");
        C("replace with empty text remains supported", TextSearch.ReplaceAll("cat cat", "cat", "", whole).text == " ");
        var hits = TextSearch.FindAll("cat Cat cat", "cat", exact);
        C("navigation uses filtered hit offsets", hits.Count == 2 && TextSearch.Next(hits, 3, false) == 1 && TextSearch.Next(hits, 0, true) == 1);
        using (var document = new DocView())
        {
            document.Load("Cat cat catalog\n");
            document.SetSearchQuery("cat", 0, false, both);
            C("preview shares the exact matching options", document.SearchHits.Count == 1);
            document.SetSearchQuery("cat", 0, false);
            C("preview default behavior is retained", document.SearchHits.Count == 3);
        }

        CodeBlock FirstCode(System.Collections.Generic.IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is CodeBlock codeBlock) return codeBlock;
                if (block is ListItem { Sub: not null } item && FirstCode(item.Sub) is CodeBlock nested) return nested;
            }
            return null;
        }
        C("fenced payload retains a leading tab", FirstCode(MdParser.Parse("```text\n\tcode\n\n```\n")).Text == "\tcode\n\n");
        C("quoted fence retains its code tab", FirstCode(MdParser.Parse("> ```\n> \tcode\n> ```\n")).Text == "\tcode\n");
        C("list fence strips container spaces not content tabs", FirstCode(MdParser.Parse("- ```\n  \tcode\n  ```\n")).Text == "\tcode\n");
        C("nested list fence retains code indentation", FirstCode(MdParser.Parse("- - ```\n    \tcode\n    ```\n")).Text == "\tcode\n");
        C("partial structural tab becomes only its remaining columns", FirstCode(MdParser.Parse("  ```\n\tcode\n  ```\n")).Text == "  code\n");
        C("indented code preserves tabs after its structural indent", FirstCode(MdParser.Parse("\t\tcode\n")).Text == "\tcode\n");
        C("partial structural tab retains following tab columns", FirstCode(MdParser.Parse("- foo\n\n\t\tbar\n")).Text == "  bar\n");
        C("fence partial tab preserves subsequent indentation columns", FirstCode(MdParser.Parse("  ```\n\t\tcode\n  ```\n")).Text == "      code\n");
        C("fenced blank whitespace is retained", FirstCode(MdParser.Parse("```\n\t\n```\n")).Text == "\t\n");

        var targets = new CodeBlockTargets();
        string code = "\t\"literal\"\n\n";
        targets.Add(new(10, 20, 110, 80, 100, 20, code));
        C("code hit keeps tabs quotes and trailing blank lines", targets.AtPoint(40, 30) == code);
        C("code text is available at a collapsed end caret", targets.AtOffset(120) == code);
        C("unrelated text and outside points are not code", targets.AtPoint(0, 30) == null && targets.AtOffset(99) == null && targets.AtOffset(121) == null);
        targets.Add(new(20, 30, 80, 60, 110, 5, "nested"));
        C("more specific later painted code wins a point hit", targets.AtPoint(30, 40) == "nested");
        targets.Add(new(float.NaN, 0, 30, 30, 0, 2, "invalid"));
        C("invalid geometry cannot become a copy action", targets.AtPoint(0, 0) == null);
        targets.Clear();
        C("repaint clears stale code targets", targets.AtPoint(40, 30) == null && targets.AtOffset(100) == null);
        targets.Add(new(0, 0, 30, 30, 0, 0, ""));
        C("empty code is distinguished from a missing target", targets.AtOffset(0) == "");

        Guid id = Guid.NewGuid();
        NavigationPoint P(int index) => new(id, "alpha.md", new ReadingLocation(index, "fingerprint-" + index, 0, index / 10f));
        var history = new NavigationHistory(3);
        C("new history has no directions", !history.CanBack && !history.CanForward);
        history.Record(P(0), P(0));
        C("same-location clicks create no history", history.BackCount == 0);
        history.Record(P(0), P(1));
        C("successful jump records its origin", history.CanBack && !history.CanForward);
        bool result = history.Traverse(true, P(1), _ => false);
        C("failed navigation preserves both stacks", !result && history.BackCount == 1 && history.ForwardCount == 0);
        NavigationPoint destination = null;
        C("back restores the original location", history.Traverse(true, P(1), p => { destination = p; return true; }) && destination == P(0));
        C("back records the current return point", !history.CanBack && history.CanForward);
        C("forward is symmetric", history.Traverse(false, P(0), p => { destination = p; return true; }) && destination == P(1));
        history.Traverse(true, P(1), _ => true);
        history.Record(P(0), P(2));
        C("new jump discards the old forward branch", !history.CanForward && history.CanBack);
        for (int i = 2; i < 9; i++) history.Record(P(i), P(i + 1));
        C("history has a fixed capacity", history.BackCount == 3);
        history.Rename(id, "renamed.md");
        history.Traverse(true, P(9), p => { destination = p; return true; });
        C("Save As updates existing history paths", destination.Path == "renamed.md" && destination.SessionId == id);
        bool threw = false;
        int before = history.BackCount;
        try { history.Traverse(true, P(8), _ => throw new InvalidOperationException()); }
        catch (InvalidOperationException) { threw = true; }
        C("throwing restore does not consume history", threw && history.BackCount == before);

        var ime = new ImeComposition();
        C("IME tracker begins inactive", !ime.Active);
        C("native start activates input protection", ime.Observe(0x010D) && ime.Active);
        C("ordinary typing cannot clear composition", !ime.Observe(0x0102) && ime.Active);
        C("duplicate start is not a new transition", !ime.Observe(0x010D));
        C("native end releases input protection", ime.Observe(0x010E) && !ime.Active);
        ime.Observe(0x010D);
        C("input language change clears the old context", ime.Observe(0x0051) && !ime.Active);
        ime.Observe(0x010D);
        C("destroy cannot leave stale input state", ime.Observe(0x0082) && !ime.Active);
    }
}
