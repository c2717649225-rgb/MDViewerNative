using System;

namespace MDViewerNative;

// Binds the one window-owned EDIT to a document. A mode change does not replace
// equal text (and therefore does not clear native undo). Other tabs retain position,
// not a second copy of the native undo stack. This class owns no HWND or document.
internal sealed class EditorSession
{
    private readonly EditorControl _editor;
    private DocumentSession _document;

    public EditorSession(EditorControl editor) => _editor = editor;

    // Returns true only when the native buffer had to be replaced.
    public bool Bind(DocumentSession document)
    {
        ObjectDisposedException.ThrowIf(document.IsDisposed, document);
        bool same = ReferenceEquals(_document, document);
        string expected = document.EditorText;
        if (document.EditHistory.Current == null) document.EditHistory.Reset(expected, document.EditPosition ?? default);
        else if (document.EditHistory.Current != expected) document.EditHistory.Record(expected, document.EditPosition ?? default);
        if (same && Externs.WindowText(_editor.Handle) == expected) return false;
        if (!same) RememberPosition();
        else document.EditPosition = null; // A source-side replacement invalidates old offsets.
        if (!Externs.SetWindowTextW(_editor.Handle, expected))
            throw new System.ComponentModel.Win32Exception();
        _document = document;
        return true;
    }

    public void RecordChange(DocumentSession document)
    {
        if (!ReferenceEquals(_document, document)) return;
        document.EditHistory.Record(Externs.WindowText(_editor.Handle), _editor.CapturePosition());
    }

    public bool ApplyHistory(DocumentSession document, bool redo)
    {
        if (!ReferenceEquals(_document, document) || document.ReadOnly || _editor.IsComposing) return false;
        string text; EditorPosition position;
        bool changed = redo ? document.EditHistory.Redo(out text, out position) : document.EditHistory.Undo(out text, out position);
        if (!changed) return false;
        if (!Externs.SetWindowTextW(_editor.Handle, text)) throw new System.ComponentModel.Win32Exception();
        _editor.RestorePosition(position);
        document.EditPosition = position;
        document.ReconcileEditor(text);
        return true;
    }

    public void RememberPosition()
    {
        if (_document != null && !_document.IsDisposed)
            _document.EditPosition = _editor.CapturePosition();
    }

    public bool RestorePosition(DocumentSession document)
    {
        if (!ReferenceEquals(_document, document) || document.EditPosition is not EditorPosition position)
            return false;
        _editor.RestorePosition(position);
        return true;
    }

    // The host changes visibility only after this succeeds. It never saves the
    // source or advances the saved snapshot/disk version. On failure EDIT stays live.
    public void PreviewDraft(DocumentSession document)
    {
        if (!ReferenceEquals(_document, document))
            throw new InvalidOperationException("The editor belongs to another document.");
        if (_editor.IsComposing) throw new InvalidOperationException("Finish the input-method composition before previewing.");
        var position = _editor.CapturePosition();
        string text = document.FromEditorText(Externs.WindowText(_editor.Handle));
        document.ReplaceText(text);
        document.EditPosition = position;
    }
}
