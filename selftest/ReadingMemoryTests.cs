using System;
using System.IO;
using System.Linq;
using System.Globalization;
using MDViewerNative;

internal static class ReadingMemoryTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("ReadingMemory/" + name, ok, "logical position and bounded persistence");
        string dir = Path.GetFullPath("../work/reading-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "中文 document.md");
            string md = string.Join("\n\n", Enumerable.Range(0, 45).Select(i => "block " + i + " " + new string('x', 140)));
            File.WriteAllText(path, md);
            using var session = new DocumentSession();
            session.Load(path, DocumentFile.Read(path));
            session.Doc.OnWidth(596);
            float offset = session.Doc.YAtOffset(session.Doc.SearchText().IndexOf("block 20", StringComparison.Ordinal)) + 8;
            var location = session.Doc.CaptureReading(offset);
            C("capture identifies a content block", location != null && location.Block >= 0 && location.Fingerprint.Length == 64);
            C("same-layout round trip", Math.Abs(session.Doc.RestoreReading(location) - offset) < 1);
            // 块间空隙与底部留白里的偏移也要原样取回(否则被钳到上一块底边:后退/前进、书签都会差几个到几十 DIP)
            float gapOffset = session.Doc.YAtOffset(session.Doc.SearchText().IndexOf("block 21", StringComparison.Ordinal)) - 4;
            float tailOffset = session.Doc.TotalH - 10;
            C("same-layout round trip between blocks", Math.Abs(session.Doc.RestoreReading(session.Doc.CaptureReading(gapOffset)) - gapOffset) < 0.01f);
            C("same-layout round trip in the bottom margin", Math.Abs(session.Doc.RestoreReading(session.Doc.CaptureReading(tailOffset)) - tailOffset) < 0.01f);
            C("top stays at zero", session.Doc.RestoreReading(session.Doc.CaptureReading(0)) == 0);
            C("invalid capture ignored", session.Doc.CaptureReading(float.NaN) == null);
            session.Doc.OnWidth(360);
            float reflow = session.Doc.RestoreReading(location);
            C("reflow restores matching logical block", session.Doc.CaptureReading(reflow).Fingerprint == location.Fingerprint && Math.Abs(reflow - offset) > 10);
            session.Doc.Load("# Inserted introduction\n\nnew paragraph\n\n" + md);
            float moved = session.Doc.RestoreReading(location);
            C("inserted content relocates original block", session.Doc.CaptureReading(moved).Fingerprint == location.Fingerprint && session.Doc.CaptureReading(moved).Block > location.Block);
            session.Doc.Load("completely replaced\n\nshort body");
            float fallback = session.Doc.RestoreReading(location);
            C("replaced document falls back to progress", Math.Abs(fallback - session.Doc.TotalH * location.Progress) < 1 && float.IsFinite(fallback));
            session.Doc.Load("");
            C("empty document safe", session.Doc.CaptureReading(100) == null && session.Doc.RestoreReading(location) == 0);
            session.Load(path, DocumentFile.Read(path));
            session.Doc.OnWidth(596);
            session.ScrollOffset = offset;
            var memory = new ReadingMemory();
            string state = Path.Combine(dir, "reading-state.tsv");
            memory.Save(state, new[] { session }, path);
            C("versioned state written atomically", File.ReadAllText(state).StartsWith("reading-v1\n") && Directory.GetFiles(dir, ".mdv-*.tmp").Length == 0);
            C("state does not contain document text", !File.ReadAllText(state).Contains("block 20"));
            var reopened = new ReadingMemory();
            reopened.Load(state);
            session.ScrollOffset = 0;
            C("Unicode path and active document restored", reopened.ActivePath == path && reopened.Apply(session) && Math.Abs(session.ScrollOffset - offset) < 1);
            session.MarkDirty();
            session.ScrollOffset = 123;
            C("dirty document is not relocated", !reopened.Apply(session) && session.ScrollOffset == 123);
            reopened.Save(state, new[] { session }, path);
            session.DiscardChanges();
            var kept = new ReadingMemory(); kept.Load(state);
            C("dirty draft does not overwrite disk reading position", kept.Apply(session) && Math.Abs(session.ScrollOffset - offset) < 1);
            using var unnamed = new DocumentSession();
            unnamed.ReplaceText("draft");
            kept.Remember(unnamed);
            C("unnamed draft not persisted", kept.Count == 1 && !kept.Apply(unnamed));
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                kept.Save(state, new[] { session }, path);
                var invariant = new ReadingMemory(); invariant.Load(state);
                C("numeric state uses invariant format", invariant.Apply(session) && Math.Abs(session.ScrollOffset - offset) < 1);
            }
            finally { CultureInfo.CurrentCulture = culture; }
            File.AppendAllText(state, "active\t%%%\nposition\tinvalid\t-1\tbad\tNaN\tInfinity\n");
            kept.Load(state);
            C("malformed records leave valid records usable", kept.Count == 1 && kept.Apply(session));
            File.WriteAllText(state, "reading-v999\nactive\tYWJj\n"); kept.Load(state);
            C("unknown schema ignored", kept.Count == 0 && kept.ActivePath.Length == 0);
            File.WriteAllText(state, new string('x', 256 * 1024 + 1)); kept.Load(state);
            C("oversized state ignored", kept.Count == 0);
            string longPath = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Path.Combine(dir, new string('中', 1500) + ".md")));
            File.WriteAllText(state, "reading-v1\nposition\t" + longPath + "\t0\t" + location.Fingerprint + "\t0\t0\n"); kept.Load(state);
            C("UTF-8 path bound keeps serialized state below read limit", kept.Count == 0);
            File.Delete(state); kept.Load(state);
            C("missing state is harmless", kept.Count == 0);
            using var many = new DocumentSession();
            for (int i = 0; i < 40; i++)
            {
                many.Load(Path.Combine(dir, i + ".md"), new("text " + i, false, false, "\n"));
                kept.Remember(many);
            }
            C("history bounded to 32 documents", kept.Count == 32);
            many.Load(Path.Combine(dir, "39.md"), new("text 39", false, false, "\n"));
            C("recent item retained", kept.Apply(many));
            many.Load(Path.Combine(dir, "0.md"), new("text 0", false, false, "\n"));
            C("oldest item evicted", !kept.Apply(many));
            many.Load(Path.Combine(dir, "8.md"), new("text 8", false, false, "\n")); kept.Remember(many);
            many.Load(Path.Combine(dir, "40.md"), new("text 40", false, false, "\n")); kept.Remember(many);
            many.Load(Path.Combine(dir, "8.md"), new("text 8", false, false, "\n"));
            C("touching old entry preserves it through eviction", kept.Apply(many));
            many.Load(Path.Combine(dir, "9.md"), new("text 9", false, false, "\n"));
            C("eviction uses recency rather than dictionary slots", !kept.Apply(many));
            kept.Save(state, Array.Empty<DocumentSession>(), "");
            C("state remains bounded on disk", new FileInfo(state).Length < 256 * 1024);
            C("reading persistence never changes source", File.ReadAllText(path) == md && !File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
