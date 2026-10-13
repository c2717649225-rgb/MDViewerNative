using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MDViewerNative;

internal static class ProductContinuationTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        MathDiagramTests.Run(check);
        SourceMappingTests.Run(check);
        void C(string name, bool ok) => check("ProductContinuation/" + name, ok, "exact work reuse and independent save policy");
        var comparer = BlockMeasureComparer.Instance;
        CodeBlock Code(string text = "let x = 1;", string lang = "js") => new() { Text = text, Lang = lang };
        var code = Code(); var equal = Code();
        C("equal code has equal hash", comparer.Equals(code, equal) && comparer.GetHashCode(code) == comparer.GetHashCode(equal));
        C("language participates in geometry key", !comparer.Equals(code, Code(lang: "math")));
        C("source participates in geometry key", !comparer.Equals(code, Code("longer source")));
        equal.FrontMatter = true; C("front matter has its own geometry", !comparer.Equals(code, equal));
        equal = Code(); equal.XOff = 20; C("indent is not shared", !comparer.Equals(code, equal));
        equal = Code(); equal.QuoteDepth = 1; C("quote structure is not shared", !comparer.Equals(code, equal));
        var heading = new Heading { Level = 1, Inlines = new() { new("same") } };
        var h2 = new Heading { Level = 2, Inlines = new() { new("same") } };
        C("heading level is not shared", !comparer.Equals(heading, h2));
        h2.Level = 1; h2.Inlines[0].B = true;
        C("heading styles are not shared", !comparer.Equals(heading, h2));
        C("stateful containers excluded", !BlockMeasureComparer.Supports(new HtmlDetails()) && !BlockMeasureComparer.Supports(new ListItem { Sub = new() }) && !BlockMeasureComparer.Supports(new Image()));

        var quoteA = new Quote { Inlines = new() { new("same") }, QuoteDepth = 1, QSeq = 1 };
        var quoteB = new Quote { Inlines = new() { new("same") }, QuoteDepth = 1, QSeq = 2 };
        C("quote group identity does not affect local geometry", comparer.Equals(quoteA, quoteB) && comparer.GetHashCode(quoteA) == comparer.GetHashCode(quoteB));
        ListItem Item() => new() { Inlines = new() { new("same") }, Marker = "•" };
        var itemA = Item(); var itemB = Item(); itemB.ListGroup = 42;
        C("leaf list geometry is independent of group identity", comparer.Equals(itemA, itemB) && comparer.GetHashCode(itemA) == comparer.GetHashCode(itemB));
        itemB.Loose = true; C("list looseness changes measured height", !comparer.Equals(itemA, itemB));
        itemB = Item(); itemB.Task = 1; C("task marker is part of measurement", !comparer.Equals(itemA, itemB));
        itemB = Item(); itemB.GroupBreakAfter = true; C("list end gap is part of measurement", !comparer.Equals(itemA, itemB));
        using (var listShared = new DocView())
        using (var listReference = new DocView { ReuseBlockMeasurements = false })
        {
            string containers = string.Concat(Enumerable.Repeat("> quote words\n\n- one\n- two\n\n# Separator\n\n", 8));
            listShared.Load(containers); listReference.Load(containers);
            foreach (float width in new[] { 380f, 800f })
            {
                listShared.OnWidth(width); listReference.OnWidth(width);
                C("leaf-container geometry matches independent layout " + width, listShared.ReusedBlockMeasurements > 10 && listShared.TotalH == listReference.TotalH);
                listShared.SelAll(); listReference.SelAll();
                C("leaf-container copy remains exact " + width, listShared.CopyText() == listReference.CopyText());
            }
        }

        using var shared = new DocView();
        using var reference = new DocView { ReuseBlockMeasurements = false };
        string repeated = string.Concat(Enumerable.Repeat("# Repeated heading\n\n```js\nconst value = 42;\n```\n\n", 12));
        float originalSize = RenderResources.BodySize;
        try
        {
            shared.Load(repeated); reference.Load(repeated);
            foreach (float size in new[] { 12f, 16f, 22f })
            {
                shared.SetBodySize(size, false);
                foreach (float width in new[] { 420f, 760f })
                {
                    shared.OnWidth(width); reference.OnWidth(width);
                    C($"block measurements reused {size}/{width}", shared.ReusedBlockMeasurements == 22 && reference.ReusedBlockMeasurements == 0);
                    C($"geometry equals independent measurement {size}/{width}", shared.TotalH == reference.TotalH);
                    shared.SelAll(); reference.SelAll();
                    C($"lazy layouts preserve copy {size}/{width}", shared.CopyText() == reference.CopyText());
                }
            }
            using var bounded = new DocView();
            bounded.Load(string.Join("\n\n", Enumerable.Range(0, 300).Select(i => "# Heading " + i)) + "\n\n# Heading 0\n\n# Heading 299");
            C("block measurement lookup bounded", bounded.ReusedBlockMeasurements == 1);
            shared.Load("# New document"); C("new source never inherits old measurement", shared.ReusedBlockMeasurements == 0);
        }
        finally { shared.SetBodySize(originalSize); }

        string root = Path.Combine(Path.GetTempPath(), "mdv-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "source.md"); File.WriteAllText(path, "# Before\n");
            using var session = new DocumentSession(); session.Load(path, DocumentFile.Read(path));
            session.ReplaceText("# Previewed draft\n"); session.Doc.SelAll();
            int passes = session.Doc.LayoutPassCount; string selection = session.Doc.CopyText();
            session.Save(session.Text);
            C("saving prepared preview skips full layout", session.Doc.LayoutPassCount == passes && !session.Dirty && File.ReadAllText(path) == session.Text);
            C("saving prepared preview retains selection", session.Doc.HasSel && session.Doc.CopyText() == selection);
            File.WriteAllText(path, "external change");
            bool conflict = false; try { session.Save(session.Text); } catch (FileConflictException) { conflict = true; }
            C("work reuse does not bypass file version guard", conflict && File.ReadAllText(path) == "external change" && session.Doc.LayoutPassCount == passes);
            session.Save("# Changed editor\n", confirmedVersion: AtomicFile.Observe(path));
            C("changed source rebuilds", session.Doc.LayoutPassCount == passes + 1);
            string target = Path.Combine(root, "copy.md"); passes = session.Doc.LayoutPassCount;
            session.Save(session.Text, target);
            C("save as still refreshes resource identity", session.Doc.LayoutPassCount == passes + 1 && session.Path == target);
            session.Load(target, DocumentFile.Read(target));
            C("explicit load never skips refresh", session.Doc.LayoutPassCount == passes + 2);
        }
        finally { Directory.Delete(root, true); }

        int retainedBefore = LayoutCacheBudget.Retained;
        var pressure = new List<DocView>();
        try
        {
            string expected = null;
            float height = 0;
            for (int i = 0; i < 6; i++)
            {
                var view = new DocView(); pressure.Add(view);
                view.Load(string.Join("\n\n", Enumerable.Range(0, 450).Select(n => "Document " + i + " paragraph " + n)));
                if (i == 0) { view.SelAll(); expected = view.CopyText(); height = view.TotalH; }
                C("global layout entry cap " + i, LayoutCacheBudget.Retained <= LayoutCacheBudget.Limit);
            }
            C("old inactive layouts are evicted", pressure[0].CachedLayoutBlocks < 450);
            pressure[0].SelAll();
            C("eviction preserves source selection and geometry", pressure[0].CopyText() == expected && pressure[0].TotalH == height);
            C("revisit remains globally bounded", LayoutCacheBudget.Retained <= LayoutCacheBudget.Limit);
        }
        finally { foreach (var view in pressure) view.Dispose(); }
        C("disposing owners releases accounting", LayoutCacheBudget.Retained <= retainedBefore);

        using var a = new DocumentSession(); using var b = new DocumentSession();
        using var clean = new DocumentSession();
        a.MarkDirty(); b.MarkDirty();
        var documents = new Dictionary<Guid, DocumentSession> { [a.RecoveryId] = a, [b.RecoveryId] = b, [clean.RecoveryId] = clean };
        DocumentSession Resolve(Guid id) => documents.GetValueOrDefault(id);
        var saved = new List<Guid>();
        bool Save(DocumentSession d) { saved.Add(d.RecoveryId); return true; }
        var result = DocumentBatch.Save(new[] { Guid.NewGuid(), clean.RecoveryId, a.RecoveryId, a.RecoveryId, b.RecoveryId }, Resolve, Save);
        C("batch skips vanished clean and duplicate identities", result.Completed && saved.SequenceEqual(new[] { a.RecoveryId, b.RecoveryId }));
        saved.Clear(); b.ReadOnly = true;
        result = DocumentBatch.Save(new[] { b.RecoveryId, a.RecoveryId }, Resolve, Save);
        C("batch refuses readonly before callback", !result.Completed && result.ReadOnlyDocument == b && saved.Count == 0);
        b.ReadOnly = false;
        result = DocumentBatch.Save(new[] { a.RecoveryId, b.RecoveryId }, Resolve, _ => false);
        C("batch cancellation stops later actions", !result.Completed && saved.Count == 0);
        int attempts = 0;
        result = DocumentBatch.Save(new[] { a.RecoveryId, b.RecoveryId }, Resolve, d => { attempts++; documents.Remove(b.RecoveryId); return true; });
        C("batch resolves identity after dialog callbacks", result.Completed && attempts == 1);
        documents[b.RecoveryId] = b; attempts = 0; bool failure = false;
        try { DocumentBatch.Save(new[] { a.RecoveryId, b.RecoveryId }, Resolve, _ => { attempts++; throw new IOException("injected"); }); }
        catch (IOException) { failure = true; }
        C("batch failure propagates and never saves later documents", failure && attempts == 1 && a.Dirty && b.Dirty);
    }
}
