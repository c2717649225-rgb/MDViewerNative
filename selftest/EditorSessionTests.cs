using System;
using System.IO;
using MDViewerNative;

internal static class EditorSessionTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "") => check("EditorSession/" + name, ok, detail);
        string root = Path.Combine(Path.GetTempPath(), "mdv-editor-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        nint parent = Externs.CreateWindowExW(0, "STATIC", "editor-session-test", 0,
            0, 0, 640, 480, 0, 0, Externs.GetModuleHandleW(null), 0);
        if (parent == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var editor = new EditorControl(parent, 1f, () => { }, () => null);
            var binding = new EditorSession(editor);
            string a = Path.Combine(root, "alpha.md"), b = Path.Combine(root, "bravo.md");
            string original = "# Alpha 中文 😀\n\n" + string.Concat(System.Linq.Enumerable.Repeat("paragraph\n", 90));
            File.WriteAllText(a, original);
            File.WriteAllText(b, "# Bravo\r\n");
            using var alpha = new DocumentSession();
            using var bravo = new DocumentSession();
            alpha.Load(a, DocumentFile.Read(a));
            bravo.Load(b, DocumentFile.Read(b));
            var disk = alpha.DiskVersion;
            var recovery = alpha.RecoveryId;
            C("initial bind installs the document buffer", binding.Bind(alpha) && Externs.WindowText(editor.Handle) == alpha.EditorText);
            C("equal rebind leaves the native buffer alone", !binding.Bind(alpha));

            int length = Externs.GetWindowTextLengthW(editor.Handle);
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)length, (nint)length);
            Replace(editor.Handle, "draft tail");
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)3, (nint)9);
            Externs.SendMessageW(editor.Handle, 0x00B6, 0, (nint)6);
            var position = editor.CapturePosition();
            string live = Externs.WindowText(editor.Handle);
            binding.PreviewDraft(alpha);
            C("preview publishes the live source to the session", alpha.Dirty && alpha.Text == alpha.FromEditorText(live));
            C("preview never writes the source file", File.ReadAllText(a) == original && !File.Exists(a + ".bak"));
            C("preview keeps disk and recovery identities", alpha.DiskVersion == disk && alpha.RecoveryId == recovery);
            C("preview keeps the saved snapshot", alpha.CaptureDraft(alpha.Text).SavedText == original);
            C("preview preserves source line endings", !alpha.Text.Contains('\r') && alpha.EditorText.Contains("\r\n"));
            C("preview has a rendered draft", alpha.Doc.SearchText().Contains("draft tail"));
            C("return to the same document avoids text replacement", !binding.Bind(alpha) && binding.RestorePosition(alpha));
            C("selection and viewport survive a mode round trip", editor.CapturePosition() == position);
            C("native undo survives preview", Externs.SendMessageW(editor.Handle, 0x00C6 /*EM_CANUNDO*/, 0, 0) != 0);
            Externs.SendMessageW(editor.Handle, 0x00C7 /*EM_UNDO*/, 0, 0);
            C("undo still restores the original editor text", Externs.WindowText(editor.Handle) == DocumentFile.ToEditorText(original));
            binding.PreviewDraft(alpha);
            C("undo to saved source clears dirty without saving", !alpha.Dirty && alpha.Text == original && File.ReadAllText(a) == original);

            Replace(editor.Handle, "alpha draft");
            Externs.SendMessageW(editor.Handle, 0x00B1, (nint)1, (nint)5);
            binding.PreviewDraft(alpha);
            var alphaPosition = alpha.EditPosition;
            string alphaDraft = alpha.Text;
            C("another tab replaces only the native buffer", binding.Bind(bravo) && Externs.WindowText(editor.Handle) == bravo.EditorText);
            Replace(editor.Handle, "bravo draft");
            binding.PreviewDraft(bravo);
            C("different tabs keep independent drafts", alpha.Text == alphaDraft && alpha.Dirty && bravo.Dirty && File.ReadAllText(b) == "# Bravo\r\n");
            C("tab restoration has a remembered position", binding.Bind(alpha) && binding.RestorePosition(alpha));
            C("tab restoration keeps source and selection", Externs.WindowText(editor.Handle) == alpha.EditorText && editor.CapturePosition() == alphaPosition);
            C("tab replacement does not pretend to serialize native undo", Externs.SendMessageW(editor.Handle, 0x00C6, 0, 0) == 0);
            bool wrongOwner = false;
            try { binding.PreviewDraft(bravo); }
            catch (InvalidOperationException) { wrongOwner = true; }
            C("a stale binding cannot publish into another document", wrongOwner && alpha.Text == alphaDraft && bravo.Text.Contains("bravo draft"));

            File.WriteAllText(a, "external change\n");
            bool conflict = false;
            try { alpha.Save(alpha.Text); }
            catch (FileConflictException) { conflict = true; }
            C("preview does not bypass the external-change guard", conflict && alpha.Dirty && File.ReadAllText(a) == "external change\n" && alpha.DiskVersion == disk);
            File.WriteAllText(a, original);
            alpha.Save(alpha.Text);
            C("explicit save alone commits the draft", !alpha.Dirty && File.ReadAllText(a) == alphaDraft);
            C("save in preview still avoids needless native replacement", !binding.Bind(alpha));

            Externs.SetWindowTextW(editor.Handle, "poisoned hidden editor");
            C("hidden editor is not the preview source of truth", binding.Bind(alpha) && Externs.WindowText(editor.Handle) == alpha.EditorText && alpha.Text == alphaDraft);
            C("source replacement invalidates an old editing position", alpha.EditPosition == null);
            using var recovered = new DocumentSession();
            recovered.RestoreDraft(alpha.CaptureDraft(alpha.Text));
            binding.Bind(recovered);
            binding.PreviewDraft(recovered);
            C("preview does not silently accept a recovered saved-text snapshot", recovered.Dirty && recovered.Text == alpha.Text && recovered.DiskVersion == alpha.DiskVersion);
            recovered.DiscardChanges();
            C("only explicit discard clears recovered dirty intent", !recovered.Dirty && recovered.Text == alpha.Text);
            var clamped = new EditorPosition(-2, int.MaxValue, int.MaxValue).Clamp(4);
            C("position bounds use full UTF-16 offsets", clamped == new EditorPosition(0, 4, 4));

            using var unnamed = new DocumentSession();
            binding.Bind(unnamed);
            Replace(editor.Handle, "# Unnamed\r\n\r\nDraft");
            binding.PreviewDraft(unnamed);
            C("unnamed draft can be previewed without Save As", unnamed.Path.Length == 0 && unnamed.Dirty && unnamed.Text == "# Unnamed\n\nDraft");
            using var tabs = new DocumentTabs();
            tabs.Current.ReplaceText("unnamed draft");
            var retained = tabs.Current;
            bool opened = tabs.TryOpen(b, true, () => true);
            C("new-tab open retains an unnamed draft", opened && tabs.Count == 2 && !retained.IsDisposed && retained.Dirty && retained.Text == "unnamed draft");
            C("an explicit cancelled close still protects that draft", !tabs.TryClose(0, _ => false) && tabs.Count == 2 && !retained.IsDisposed);
        }
        finally
        {
            Externs.DestroyWindow(parent);
            Directory.Delete(root, true);
        }
    }

    private static unsafe void Replace(nint editor, string text)
    {
        fixed (char* value = text)
            Externs.SendMessageW(editor, 0x00C2 /*EM_REPLACESEL*/, 1, (nint)value);
    }
}
