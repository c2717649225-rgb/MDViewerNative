using System;
using System.IO;
using System.Linq;
using System.Text;
using MDViewerNative;

internal static class DocumentWorkflowTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mdv-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Files(directory, check);
            Export(directory, check);
            PdfAdoption(directory, check);
            SourceSnapshots(directory, check);
            Open(directory, check);
            OpenRule(directory, check);
            RecentPromote(check);
            RecentRename(check);
            Search(check);
            check("Workflow/temporary files cleaned", Directory.GetFiles(directory, ".mdv-*.tmp").Length == 0, "success, conflicts and failures");
        }
        finally { Directory.Delete(directory, true); }
    }

    static void SourceSnapshots(string directory, Action<string, bool, string> check)
    {
        string path = Path.Combine(directory, "source-budget.md");
        string saved = "# original\n";
        File.WriteAllText(path, saved);
        using var session = new DocumentSession();
        session.Load(path, DocumentFile.Read(path));
        check("SourceBudget/load counts shared snapshot once", session.SourceSnapshotCharacters == saved.Length, "current and saved alias");
        string edited = "# edited and longer\n";
        session.ReplaceText(edited);
        check("SourceBudget/edit includes saved snapshot", session.SourceSnapshotCharacters == saved.Length + edited.Length, "two retained instances");
        session.DiscardChanges();
        check("SourceBudget/discard collapses shared snapshot", session.SourceSnapshotCharacters == saved.Length, "saved instance restored");
        string sameContents = new string(saved.ToCharArray());
        session.RestoreDraft(session.CaptureDraft(sameContents));
        check("SourceBudget/equal contents distinct instances both count", session.SourceSnapshotCharacters == 2L * saved.Length, "value equality must not deduplicate retained instances");
        session.Save(sameContents);
        check("SourceBudget/save collapses shared snapshot", session.SourceSnapshotCharacters == saved.Length, "no stale accounting after save");
        session.ReconcileEditor(edited);
        check("SourceBudget/unpreviewed editor draft excluded", session.SourceSnapshotCharacters == saved.Length, "only the two retained session snapshots are measured");
    }

    static void PdfAdoption(string directory, Action<string, bool, string> check)
    {
        string spool = Path.Combine(directory, "spool.pdf"), target = Path.Combine(directory, "adopted.pdf");
        byte[] valid = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
        File.WriteAllBytes(spool, valid);
        DocumentPrinter.AdoptSpooledPdf(spool, target, FileVersion.Missing, () => { });
        check("PDF/adoption preserves exact spool bytes", File.ReadAllBytes(target).SequenceEqual(valid), "signature check does not rewrite PDF");
        foreach (string invalid in new[] { "", "%P", "%P123", "%PDX-", "%PDFx" })
        {
            File.WriteAllText(spool, invalid);
            var expected = AtomicFile.Observe(target);
            bool rejected = false;
            try { DocumentPrinter.AdoptSpooledPdf(spool, target, expected, () => { }); }
            catch (IOException) { rejected = true; }
            check("PDF/invalid signature preserves target " + invalid, rejected && File.ReadAllBytes(target).SequenceEqual(valid), "must not adopt invalid spool");
            File.WriteAllBytes(target, valid);
        }
        File.WriteAllBytes(spool, valid);
        File.WriteAllText(target, "external change");
        bool conflict = false;
        try { DocumentPrinter.AdoptSpooledPdf(spool, target, FileVersion.Of(valid), () => { }); }
        catch (FileConflictException) { conflict = true; }
        check("PDF/adoption retains pre-print version contract", conflict && File.ReadAllText(target) == "external change", "cannot overwrite a later external edit");
        File.Delete(target);
        File.WriteAllText(spool, "%P123");
        bool missingRejected = false;
        try { DocumentPrinter.AdoptSpooledPdf(spool, target, FileVersion.Missing, () => { }); }
        catch (IOException) { missingRejected = true; }
        check("PDF/invalid spool cannot create destination", missingRejected && !File.Exists(target), "new path stays absent");
        File.Delete(target);
        File.Delete(spool);
        int waits = 0;
        bool timedOut = false;
        try { DocumentPrinter.AdoptSpooledPdf(spool, target, FileVersion.Missing, () => waits++); }
        catch (IOException) { timedOut = true; }
        check("PDF/missing spool has bounded wait", timedOut && waits == 100 && !File.Exists(target), "existing 100-attempt policy retained");
    }

    static void Files(string directory, Action<string, bool, string> check)
    {
        string path = Path.Combine(directory, "shared.md");
        File.WriteAllText(path, "# Original\n");
        using var first = new DocumentSession();
        using var second = new DocumentSession();
        first.Load(path, DocumentFile.Read(path));
        second.Load(path, DocumentFile.Read(path));
        first.Save("# First edit\n");
        second.MarkDirty();
        FileConflictException conflict = null;
        try { second.Save("# Second edit\n"); }
        catch (FileConflictException ex) { conflict = ex; }
        check("Workflow/stale tab cannot overwrite saved tab", conflict != null && File.ReadAllText(path) == "# First edit\n", "new disk content retained");
        check("Workflow/conflict preserves session snapshot", second.Dirty && second.Text == "# Original\n" && second.Path == path, "draft owner remains dirty");
        check("Workflow/conflict preserves backup", File.ReadAllText(path + ".bak") == "# Original\n", "failed save does not rotate backup");

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "# Third edit\n");
        File.SetLastWriteTimeUtc(path, stamp); // Same length and timestamp must still conflict.
        bool changedAgain = false;
        try { second.Save("# Second edit\n", confirmedVersion: conflict?.ActualVersion); }
        catch (FileConflictException) { changedAgain = true; }
        check("Workflow/overwrite approval covers one version", changedAgain && File.ReadAllText(path) == "# Third edit\n", "recheck after the confirmation dialog");
        second.Save("# Second edit\n", confirmedVersion: AtomicFile.Observe(path));
        check("Workflow/explicit overwrite preserves external backup", !second.Dirty && File.ReadAllText(path + ".bak") == "# Third edit\n", "confirmed overwrite succeeds");
        second.Save("# Next save\n");
        check("Workflow/save advances expected version", File.ReadAllText(path) == "# Next save\n", "no false conflict on subsequent save");

        File.Delete(path);
        bool deletedConflict = false;
        try { second.Save("recreate"); }
        catch (FileConflictException ex) { deletedConflict = !ex.ActualVersion.Exists; }
        check("Workflow/deletion requires confirmation", deletedConflict && !File.Exists(path), "deleted external file is not silently recreated");
        second.Save("recreate", confirmedVersion: FileVersion.Missing);
        check("Workflow/confirmed recreation succeeds", File.ReadAllText(path) == "recreate", "missing is an explicit version");

        string target = Path.Combine(directory, "existing.md");
        File.WriteAllText(target, "Existing target");
        second.MarkDirty();
        bool saveAsConflict = false;
        try { second.Save("Save As draft", target); }
        catch (FileConflictException) { saveAsConflict = true; }
        check("Workflow/Save As protects existing target", saveAsConflict && File.ReadAllText(target) == "Existing target" && second.Path == path && second.Dirty, "default is create-only");
        second.Save("Save As draft", target, AtomicFile.Observe(target));
        check("Workflow/confirmed Save As commits identity", second.Path == target && !second.Dirty && File.ReadAllText(target + ".bak") == "Existing target", "source and target versions stay separate");

        string malformed = Path.Combine(directory, "invalid.md");
        byte[] bytes = { 0xEF, 0xBB, 0xBF, 0x23, 0x20, 0xC3, 0x28, 0x0A };
        File.WriteAllBytes(malformed, bytes);
        bool rejected = false;
        try { DocumentFile.Read(malformed); }
        catch (DecoderFallbackException) { rejected = true; }
        check("Workflow/malformed BOM UTF8 rejected", rejected && File.ReadAllBytes(malformed).SequenceEqual(bytes), "no replacement characters or disk mutation");
        File.WriteAllBytes(malformed, new byte[] { 0x81 });
        rejected = false;
        try { DocumentFile.Read(malformed); }
        catch (DecoderFallbackException) { rejected = true; }
        check("Workflow/malformed GBK rejected", rejected, "fallback decoder also refuses lossy conversion");

        string missing = Path.Combine(directory, "missing-target.md");
        var expected = AtomicFile.Observe(missing);
        File.WriteAllText(missing, "Created by another writer");
        bool appeared = false;
        try { AtomicFile.Write(missing, Encoding.UTF8.GetBytes("ours"), expected); }
        catch (FileConflictException) { appeared = true; }
        check("Workflow/new target race preserves other writer", appeared && File.ReadAllText(missing) == "Created by another writer", "create does not become an overwrite");
    }

    static void Export(string directory, Action<string, bool, string> check)
    {
        string path = Path.Combine(directory, "export.md");
        File.WriteAllText(path, "# Saved version\n");
        using var session = new DocumentSession();
        session.Load(path, DocumentFile.Read(path));
        session.MarkDirty();
        var export = DocumentExport.Prepare(session, "# Unsaved draft\n", false);
        bool created = export.TryWrite(_ => throw new Exception("New target should not prompt."));
        check("Workflow/export includes active draft", created && File.ReadAllText(export.Path).Contains("Unsaved draft") && !File.ReadAllText(export.Path).Contains("Saved version"), "export parses the supplied live text");
        check("Workflow/export preserves source and dirty state", session.Dirty && session.Text == "# Saved version\n" && File.ReadAllText(path) == session.Text, "export does not save the document");
        var hostile = DocumentExport.Prepare(session, "x <noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">\n", false);
        check("Workflow/export filters raw HTML through the allowlist", !hostile.Html.Contains("<noscript") && hostile.Html.Contains("&lt;noscript&gt;") && !hostile.Html.Contains("<img src=x"), "E14b: the user export path uses the allowlist by default");

        File.WriteAllText(export.Path, "Hand-maintained HTML");
        export = DocumentExport.Prepare(session, "# New export", false);
        bool prompted = false;
        bool cancelled = !export.TryWrite(p => { prompted = p == export.Path; return false; });
        check("Workflow/cancelled export preserves existing HTML", cancelled && prompted && File.ReadAllText(export.Path) == "Hand-maintained HTML" && !File.Exists(export.Path + ".bak"), "no write before confirmation");
        export.TryWrite(_ => true);
        check("Workflow/confirmed export creates backup", File.ReadAllText(export.Path + ".bak") == "Hand-maintained HTML" && File.ReadAllText(export.Path).Contains("New export"), "atomic replacement shared with save");
        export = DocumentExport.Prepare(session, "# Raced export", false);
        bool conflict = false;
        try { export.TryWrite(p => { File.WriteAllText(p, "Concurrent HTML edit"); return true; }); }
        catch (FileConflictException) { conflict = true; }
        check("Workflow/export rechecks approved target", conflict && File.ReadAllText(export.Path) == "Concurrent HTML edit", "confirmation cannot authorize a later version");

        string htmlSource = Path.Combine(directory, "source.html");
        File.WriteAllText(htmlSource, "HTML source");
        session.Load(htmlSource, DocumentFile.Read(htmlSource));
        export = DocumentExport.Prepare(session, session.Text, false);
        export.TryWrite(_ => true);
        check("Workflow/export never replaces HTML source", export.Path.EndsWith("source.export.html") && File.ReadAllText(htmlSource) == "HTML source", "source and export path differ");
    }

    static void Open(string directory, Action<string, bool, string> check)
    {
        string a = Path.Combine(directory, "alpha.md"), b = Path.Combine(directory, "bravo.md");
        File.WriteAllText(a, "Alpha"); File.WriteAllText(b, "Bravo");
        using var tabs = new DocumentTabs();
        tabs.TryOpen(a, false, () => true);
        var alpha = tabs.Current;
        alpha.ReplaceText("Alpha draft");
        int guardCalls = 0;
        bool cancelled = !tabs.TryOpen(b, false, () => { guardCalls++; return false; });
        check("Workflow/cancelled open preserves current draft", cancelled && guardCalls == 1 && tabs.Current == alpha && alpha.Dirty && alpha.Text == "Alpha draft" && !alpha.IsDisposed, "no ownership change before approval");
        bool failed = false;
        try { tabs.TryOpen(Path.Combine(directory, "does-not-exist.md"), false, () => { guardCalls++; return true; }); }
        catch (FileNotFoundException) { failed = true; }
        check("Workflow/read failure precedes dirty guard", failed && guardCalls == 1 && tabs.Current == alpha && alpha.Dirty, "failed open cannot discard a draft");
        bool opened = tabs.TryOpen(a.ToUpperInvariant(), true, () => throw new Exception("Same tab must not guard."));
        check("Workflow/reopen active path preserves draft", opened && tabs.Count == 1 && tabs.Current == alpha && alpha.Dirty, "Windows case-insensitive path reuse");
        tabs.TryOpen(b, true, () => true);
        var bravo = tabs.Current;
        tabs.TryOpen(a, true, () => true);
        check("Workflow/reopen inactive path reuses session", tabs.Count == 2 && tabs.Current == alpha && alpha.Dirty && !bravo.IsDisposed, "no duplicate file sessions");
        string c = Path.Combine(directory, "charlie.md"); File.WriteAllText(c, "Charlie");
        tabs.TryOpen(c, false, () => true);
        check("Workflow/replacing tab releases previous view", alpha.IsDisposed && tabs.Count == 2 && tabs.Current.Path == c && !bravo.IsDisposed, "candidate owns successful replacement");

        string renamed = Path.Combine(directory, "rename.md"); File.WriteAllText(renamed, "Old target");
        var current = tabs.Current;
        tabs.TryOpen(renamed, true, () => { current.Save("Saved while opening", renamed, AtomicFile.Observe(renamed)); return true; });
        check("Workflow/Save As during open reuses new identity", tabs.Current == current && tabs.Count == 2 && current.Text == "Saved while opening", "preloaded candidate cannot replace a just-saved target");
    }

    // E16:OpenKeepsCurrent 必须与 TryOpen 的实际结果一致(宿主靠它决定是否做未保存确认)
    static void OpenRule(string directory, Action<string, bool, string> check)
    {
        string named = Path.Combine(directory, "rule-named.md"), other = Path.Combine(directory, "rule-other.md"), fresh = Path.Combine(directory, "rule-fresh.md");
        foreach (string p in new[] { named, other, fresh }) File.WriteAllText(p, "# " + Path.GetFileName(p) + "\n");
        var wrong = new System.Collections.Generic.List<string>();
        int cases = 0;
        foreach (bool isNamed in new[] { false, true })
            foreach (bool dirty in new[] { false, true })
                foreach (bool newTab in new[] { false, true })
                    foreach (string target in new[] { "fresh", "other", "self" })
                    {
                        if (target == "self" && !isNamed) continue;
                        using var tabs = new DocumentTabs();
                        if (target == "other") tabs.TryOpen(other, false, () => true);
                        if (isNamed) tabs.TryOpen(named, target == "other", () => true);
                        else if (target == "other") tabs.NewDocument();
                        var previous = tabs.Current;
                        bool setupOk = tabs.Count == (target == "other" ? 2 : 1) && previous.Path == (isNamed ? named : "");
                        if (dirty) previous.MarkDirty();
                        string path = target == "fresh" ? fresh : target == "other" ? other : named;
                        bool predicted = tabs.OpenKeepsCurrent(path, newTab);
                        tabs.TryOpen(path, newTab, () => true);
                        bool kept = !previous.IsDisposed && tabs.IndexOf(previous) >= 0;
                        cases++;
                        if (!setupOk || predicted != kept)
                            wrong.Add($"{(isNamed ? "named" : "untitled")}/{(dirty ? "dirty" : "clean")}/{(newTab ? "newtab" : "sametab")}/{target}:predicted={predicted},kept={kept},setup={setupOk}");
                    }
        check("Workflow/open rule predicts whether the current document is kept", wrong.Count == 0 && cases == 20,
            wrong.Count == 0 ? cases + " cases" : string.Join(" ", wrong));
    }

    static void RecentPromote(Action<string, bool, string> check)
    {
        static string Run(string[] start, string path, int max)
        {
            var list = new System.Collections.Generic.List<string>(start);
            RecentList.Promote(list, path, max);
            return string.Join(",", list);
        }
        var wrong = new System.Collections.Generic.List<string>();
        void Expect(string name, string actual, string expected) { if (actual != expected) wrong.Add(name + ":" + actual); }
        Expect("new", Run(new[] { "b", "c" }, "a", 20), "a,b,c");
        Expect("existing", Run(new[] { "a", "b", "c" }, "c", 20), "c,a,b");
        Expect("front", Run(new[] { "a", "b" }, "a", 20), "a,b");
        Expect("full", Run(new[] { "a", "b", "c", "d", "e" }, "f", 5), "f,a,b,c,d");
        Expect("over", Run(new[] { "a", "b", "c", "d", "e", "f", "g" }, "g", 5), "g,a,b,c,d");
        // E21（TD-20）：按 Windows 路径规则不区分大小写；保留最新一次的写法。
        Expect("ignore case", Run(new[] { "A.md" }, "a.md", 20), "a.md");
        // 旧配置里已有的大小写重复条目，在该文件再次打开时一并合并（不做迁移，自然收敛）。
        Expect("merge old duplicates", Run(new[] { "A.md", "b", "a.MD" }, "a.md", 20), "a.md,b");
        check("Workflow/recent files promote to front, dedupe and stay bounded", wrong.Count == 0,
            wrong.Count == 0 ? "7 cases" : string.Join(" ", wrong));
    }

    static void RecentRename(Action<string, bool, string> check)
    {
        static string Run(string[] start, string oldPath, string newPath, int max)
        {
            var list = new System.Collections.Generic.List<string>(start);
            RecentList.Rename(list, oldPath, newPath, max);
            return string.Join(",", list);
        }
        var wrong = new System.Collections.Generic.List<string>();
        void Expect(string name, string actual, string expected) { if (actual != expected) wrong.Add(name + ":" + actual); }
        Expect("listed", Run(new[] { "x", "old", "y" }, "old", "new", 20), "new,x,y");
        Expect("unlisted", Run(new[] { "x", "y" }, "old", "new", 20), "new,x,y");
        Expect("bounded", Run(new[] { "a", "b", "c", "d", "e" }, "old", "new", 5), "new,a,b,c,d");
        Expect("dedupe", Run(new[] { "old", "new", "z" }, "old", "new", 20), "new,z");
        Expect("case-only rename", Run(new[] { "x", "a.md" }, "a.md", "A.md", 20), "A.md,x");
        Expect("old path in other case", Run(new[] { "OLD", "y" }, "old", "new", 20), "new,y");
        check("Workflow/rename keeps recent files unique and bounded", wrong.Count == 0,
            wrong.Count == 0 ? "6 cases" : string.Join(" ", wrong));
    }

    static void Search(Action<string, bool, string> check)
    {
        var empty = TextSearch.FindAll("text", "");
        check("Workflow/empty search terminates", empty.Count == 0 && TextSearch.Next(empty, 0, true) == -1, "empty query has no matches");
        var hits = TextSearch.FindAll("Alpha ALPHA alpha", "alpha");
        check("Workflow/search navigation wraps consistently", hits.Count == 3 && TextSearch.Next(hits, 5, false) == 1 && TextSearch.Next(hits, 6, true) == 0 && TextSearch.Next(hits, 0, true) == 2, "preview and editor share selection semantics");
        var replaced = TextSearch.ReplaceAll("Aa aa", "aa", "$1\\path");
        check("Workflow/replacement text is literal", replaced.text == "$1\\path $1\\path" && replaced.count == 2, "no regex substitutions in user text");
        replaced = TextSearch.ReplaceAll("aaaa", "aa", "x");
        check("Workflow/search and replacement agree on overlap", TextSearch.FindAll("aaaa", "aa").Count == replaced.count && replaced.text == "xx", "one non-overlapping matching rule");
        check("Workflow/backwards search in empty document", TextSearch.Next(TextSearch.FindAll("", "x"), 0, true) == -1, "no negative string index");
    }
}
