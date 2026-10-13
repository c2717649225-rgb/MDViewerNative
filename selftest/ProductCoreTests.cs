using System;
using System.IO;
using System.Linq;
using System.Text;
using MDViewerNative;

internal static class ProductCoreTests
{
    public static void Run(Action<string, bool, string> check)
    {
        ProductContinuationTests.Run(check);
        void C(string name, bool ok) => check("ProductCore/" + name, ok, "document operations and format/export contracts");
        string root = Path.Combine(Path.GetTempPath(), "mdv-product-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var history = new TextEditHistory(); history.Reset("one");
            history.Record("one two", new EditorPosition(7, 7, 0));
            history.Record("ONE two", new EditorPosition(3, 3, 0));
            C("multi-level undo restores the previous edit", history.Undo(out var undo, out _) && undo == "one two");
            C("multi-level undo reaches the loaded source", history.Undo(out undo, out _) && undo == "one");
            C("redo restores forward edits", history.Redo(out var redo, out _) && redo == "one two");
            history.Record("new branch", default); C("editing clears redo branch", !history.CanRedo);
            var unicode = new TextEditHistory(); unicode.Reset("😀x"); unicode.Record("😁x", default);
            C("history never splits a Unicode surrogate pair", unicode.Undo(out undo, out _) && undo == "😀x" && unicode.Redo(out redo, out _) && redo == "😁x");
            var bounded = new TextEditHistory { Limit = 2 }; bounded.Reset(""); bounded.Record("1", default); bounded.Record("12", default); bounded.Record("123", default);
            C("history has a finite operation budget", bounded.UndoCount == 2);
            string text = "# 中文 😀\r\nbody\r\n";
            foreach (var kind in new[] { TextEncoding.Utf8, TextEncoding.Utf16LittleEndian, TextEncoding.Utf16BigEndian })
                foreach (bool bom in new[] { false, true })
                {
                    string path = Path.Combine(root, kind + "-" + bom + ".md");
                    var content = new DocumentContent(text, false, bom, "\r\n", Encoding: kind);
                    DocumentFile.Write(path, content);
                    var read = DocumentFile.Read(path, bom ? TextEncoding.Auto : kind);
                    C(kind + "/" + bom + "/round trip", read.Text == text && read.Format.Encoding == kind && read.Bom == bom);
                    byte[] before = File.ReadAllBytes(path);
                    DocumentFile.Write(path, read, read.Version);
                    C(kind + "/" + bom + "/byte-preserving save", before.SequenceEqual(File.ReadAllBytes(path)));
                }
            string utf16 = Path.Combine(root, "utf16.md");
            DocumentFile.Write(utf16, new(text, false, true, "\r\n", Encoding: TextEncoding.Utf16LittleEndian));
            using var session = new DocumentSession(); session.Load(utf16, DocumentFile.Read(utf16));
            C("session owns its UTF16 format", session.Encoding == TextEncoding.Utf16LittleEndian && session.Format.Bom);
            session.SetFormat(new(TextEncoding.Utf8, false, "\n"));
            C("format change is dirty without changing disk", session.Dirty && File.ReadAllBytes(utf16)[0] == 0xFF);
            session.DiscardChanges();
            C("discard restores original format", !session.Dirty && session.Encoding == TextEncoding.Utf16LittleEndian && session.LineEnding == "\r\n");
            session.SetFormat(new(TextEncoding.Utf8, false, "\n")); session.Save(session.FromEditorText(session.EditorText));
            C("explicit format conversion writes intended bytes", DocumentFile.Read(utf16).Encoding == TextEncoding.Utf8 && !File.ReadAllText(utf16).Contains('\r'));
            session.ReconcileEditor(session.EditorText + "x"); C("typing marks dirty immediately", session.Dirty);
            session.ReconcileEditor(session.EditorText); C("undo to saved text clears dirty immediately", !session.Dirty);
            session.ReadOnly = true;
            bool blocked = false; try { session.Save("must not write"); } catch (UnauthorizedAccessException) { blocked = true; }
            C("read-only prevents direct model writes", blocked && File.ReadAllText(utf16) == session.Text);
            session.ReadOnly = false;
            string renamed = Path.Combine(root, "renamed.md"); session.Rename(renamed);
            C("rename commits identity after moving the file", session.Path == renamed && File.Exists(renamed) && !File.Exists(utf16));
            C("rename leaves document text intact", File.ReadAllText(renamed) == session.Text);

            var snapshot = session.CaptureDraft(session.Text + "draft");
            var store = new DraftStore(Path.Combine(root, "recovery")); store.Write(snapshot);
            using var recovered = new DocumentSession(); recovered.RestoreDraft(store.ReadAll().Single());
            C("recovery preserves format metadata", recovered.Format == session.Format && recovered.Dirty);
            using var tabs = new DocumentTabs();
            var first = tabs.Current; first.ReplaceText("one"); var second = tabs.NewDocument(); second.ReplaceText("two");
            C("new document retains prior unnamed draft", tabs.Count == 2 && first.Dirty && first.Text == "one");
            C("tab move preserves active document identity", tabs.Move(1, 0) && ReferenceEquals(tabs.Current, second));
            first.Pinned = true; C("pin is per document", first.Pinned && !second.Pinned);
            var named = new DocumentSession(); named.Load(renamed, DocumentFile.Read(renamed)); tabs.Add(named);
            C("closing a named tab records a reopen target", tabs.TryClose(tabs.ActiveIndex, _ => true) && tabs.LastClosed?.Path == renamed);
            tabs.ConsumeClosed(); C("reopen history is consumed explicitly", !tabs.CanReopen);

            using var untitled = new DocumentSession(); untitled.ReplaceText("# Draft\n");
            string htmlPath = Path.Combine(root, "custom-name.html");
            var export = DocumentExport.Prepare(untitled, untitled.Text, false, htmlPath);
            C("unnamed draft exports without saving Markdown", export.TryWrite(_ => true) && untitled.Path.Length == 0 && untitled.Dirty && File.ReadAllText(htmlPath).Contains("Draft"));
            bool sourceGuard = false;
            try { DocumentExport.Prepare(session, session.Text, false, session.Path); } catch (InvalidOperationException) { sourceGuard = true; }
            C("export cannot overwrite its source", sourceGuard);
            string image = Path.Combine(root, "tiny.png");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
            File.WriteAllBytes(image, png);
            string embedded = HtmlResources.Prepare("<img src=\"tiny.png\">", root, true);
            C("offline HTML embeds local bytes", embedded.Contains("data:image/png;base64," + Convert.ToBase64String(png)));
            C("export-as rebases relative image paths", HtmlResources.Prepare("<img src=\"tiny.png\">", root, false).Contains("file:"));
            bool network = false; try { HtmlResources.Prepare("<img src=\"https://example.invalid/a.png\">", root, true); } catch (InvalidOperationException) { network = true; }
            C("offline export never silently fetches network content", network);
            bool unc = false; try { HtmlResources.Prepare("<img src=\"file://server/share/a.png\">", root, true); } catch (InvalidOperationException) { unc = true; }
            C("offline export refuses UNC access", unc);
            C("CR-only conversion is idempotent", DocumentFile.ToEditorText("a\rb\r") == "a\r\nb\r\n" && DocumentFile.FromEditorText("a\r\nb\r\n", "\r") == "a\rb\r");
        }
        finally { Directory.Delete(root, true); }
    }
}
