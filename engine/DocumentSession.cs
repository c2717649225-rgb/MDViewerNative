using System;
using System.IO;

namespace MDViewerNative;

// A tab owns its source snapshot, file format, view state and native document resources.
// The visible EDIT control may hold a draft; the host passes that draft explicitly to Save.
internal sealed class DocumentSession : IDisposable
{
    private IDocumentViewAdapter _view;

    // The concrete view is registered host-side (DocumentViewAccess module initializer),
    // so this workflow type never names a rendering type (E19a).
    internal static Func<IDocumentViewAdapter> ViewFactory { get; set; }

    internal DocumentSession() : this((IDocumentViewAdapter)null) { }

    internal DocumentSession(IDocumentViewAdapter view)
    {
        _view = view;
    }

    public string Path { get; private set; } = "";
    public string Name => Path.Length == 0 ? "(未命名)" : System.IO.Path.GetFileName(Path);
    public bool Dirty { get; private set; }
    public bool ReadOnly { get; set; }
    public bool Pinned { get; set; }
    public TextEncoding Encoding { get; private set; } = TextEncoding.Utf8;
    public DocumentFormat Format => new(Encoding, Bom, LineEnding);
    private DocumentFormat _savedFormat = DocumentFormat.Utf8;
    private bool _formatDirty;
    public bool HasView => _view != null;
    public IDocumentViewAdapter ViewAdapter => _view;

    // A session created for a blank tab carries no rendering lifetime until a document
    // needs one, so the model stays usable without a view. Hosts reach the concrete view
    // through DocumentViewAccess (session.Doc); the session itself only knows the adapter (E15).
    internal IDocumentViewAdapter EnsureView()
    {
        if (_view == null)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _view = (ViewFactory ?? throw new InvalidOperationException("No document view factory is registered.")).Invoke();
            _view.Load(ViewDirectory, Text);
        }
        return _view;
    }

    // Materializing the model into the view is the only way the two stay consistent:
    // a view attached later must render the text the session already holds. Re-attaching
    // the view this session already owns is a no-op — every mutation path reloads it, and
    // reloading again would discard selection and decoded images for nothing.
    public void AttachView(IDocumentViewAdapter view)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(view);
        if (ReferenceEquals(_view, view)) return;
        _view?.Dispose();
        _view = view;
        ReloadView();
    }

    // Ownership of the rendering lifetime moves to the caller; the model stays here and
    // materializes a fresh view on demand.
    public IDocumentViewAdapter DetachView()
    {
        var view = _view;
        _view = null;
        return view;
    }
    public float ScrollOffset { get; set; }
    internal EditorPosition? EditPosition { get; set; }
    internal TextEditHistory EditHistory { get; } = new();
    public string Text { get; private set; } = "";
    public string LineEnding { get; private set; } = "\n";
    public bool Gbk { get; private set; }
    public bool Bom { get; private set; }
    public bool IsDisposed { get; private set; }
    public Guid RecoveryId { get; private set; } = Guid.NewGuid();
    public string EditorText => DocumentFile.ToEditorText(Text);
    private string _savedText = "";
    // Count the two retained session snapshots, sharing only by reference, not value.
    // Excludes live native editor text, undo history and object/renderer overhead.
    internal long SourceSnapshotCharacters => (long)Text.Length + (ReferenceEquals(Text, _savedText) ? 0 : _savedText.Length);
    private bool _recoveredDraft;
    private FileVersion _diskVersion = FileVersion.Missing;
    internal FileVersion DiskVersion => _diskVersion;
    internal long DiskRevision { get; private set; }
    internal DiskState DiskState { get; set; }

    public DraftSnapshot CaptureDraft(string liveText) => new(2, RecoveryId, Path, liveText, _savedText,
        _diskVersion.Hash, Gbk, Bom, LineEnding, Encoding, _savedFormat.Encoding, _savedFormat.Bom, _savedFormat.LineEnding);

    public void ReconcileEditor(string text)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Dirty = _recoveredDraft || _formatDirty || FromEditorText(text) != _savedText;
    }

    public void SetFormat(DocumentFormat format)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ReadOnly) throw new UnauthorizedAccessException("Document is read-only.");
        format.Validate();
        Encoding = format.Encoding; Gbk = Encoding == TextEncoding.Gbk; Bom = format.Bom; LineEnding = format.LineEnding;
        _formatDirty = format != _savedFormat;
        Dirty = _recoveredDraft || _formatDirty || Text != _savedText;
    }

    public void Rename(string destination)
    {
        if (ReadOnly || Path.Length == 0) throw new InvalidOperationException("A writable named document is required.");
        destination = System.IO.Path.GetFullPath(destination);
        var actual = AtomicFile.Observe(Path);
        if (actual != _diskVersion) throw new FileConflictException(Path, actual);
        File.Move(Path, destination);
        Path = destination;
        _view?.SetDirectory(ViewDirectory);
        DiskRevision++;
    }

    public void RestoreDraft(DraftSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        RecoveryId = snapshot.Id;
        Path = snapshot.Path;
        Text = snapshot.Text;
        _savedText = snapshot.SavedText;
        // Never observe the current disk version on behalf of an older recovered draft.
        _diskVersion = new FileVersion(snapshot.DiskHash);
        DiskRevision++;
        Gbk = snapshot.Gbk;
        Bom = snapshot.Bom;
        LineEnding = snapshot.LineEnding;
        Encoding = snapshot.Encoding == TextEncoding.Auto ? (Gbk ? TextEncoding.Gbk : TextEncoding.Utf8) : snapshot.Encoding;
        _savedFormat = new(snapshot.SavedEncoding == TextEncoding.Auto ? Encoding : snapshot.SavedEncoding,
            snapshot.SavedBom ?? Bom, snapshot.SavedLineEnding ?? LineEnding);
        _formatDirty = Format != _savedFormat;
        _recoveredDraft = true;
        Dirty = true;
        EditPosition = null;
        ScrollOffset = 0;
        EditHistory.Reset(EditorText);
        ReloadView();
    }

    public void Load(string path, DocumentContent content)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Path = System.IO.Path.GetFullPath(path);
        Gbk = content.Gbk;
        Bom = content.Bom;
        LineEnding = content.LineEnding;
        Encoding = content.Format.Encoding;
        _savedFormat = Format; _formatDirty = false;
        ReadOnly = File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        Text = _savedText = content.Text;
        _diskVersion = content.Version ?? FileVersion.Missing;
        DiskRevision++;
        DiskState = DiskState.Current;
        _recoveredDraft = false;
        Dirty = false;
        EditPosition = null;
        ScrollOffset = 0;
        EditHistory.Reset(EditorText);
        ReloadView();
    }

    public string FromEditorText(string text) => DocumentFile.FromEditorText(text, LineEnding);

    public void MarkDirty()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Dirty = true;
    }

    public void ReplaceText(string text)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Text != text)
        {
            // Keep the old source snapshot if preview preparation throws. The
            // active native editor remains authoritative until the host hides it.
            EnsureView().Load(ViewDirectory, text);
            Text = text;
            EditPosition = null;
        }
        // Merely previewing an explicitly recovered draft never clears its guard.
        Dirty = _recoveredDraft || _formatDirty || Text != _savedText;
    }

    public void DiscardChanges()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        bool changed = Text != _savedText;
        Text = _savedText;
        Encoding = _savedFormat.Encoding; Gbk = Encoding == TextEncoding.Gbk; Bom = _savedFormat.Bom; LineEnding = _savedFormat.LineEnding;
        _formatDirty = false;
        _recoveredDraft = false;
        EditPosition = null;
        Dirty = false;
        EditHistory.Reset(EditorText);
        if (changed) ReloadView();
    }

    public void Save(string text, string targetPath = null, FileVersion confirmedVersion = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ReadOnly) throw new UnauthorizedAccessException("Document is read-only.");
        string destination = System.IO.Path.GetFullPath(targetPath ?? Path);
        text = DocumentFile.NormalizeLines(text, LineEnding);
        bool sameFile = string.Equals(destination, Path, StringComparison.OrdinalIgnoreCase);
        bool viewUnchanged = sameFile && text == Text;
        FileVersion version = DocumentFile.Write(destination, new(text, Gbk, Bom, LineEnding, Encoding: Encoding),
            confirmedVersion ?? (sameFile ? _diskVersion : FileVersion.Missing));
        // Commit identity and snapshot only after the atomic write succeeds.
        Path = destination;
        Text = _savedText = text;
        _savedFormat = Format; _formatDirty = false;
        _diskVersion = version;
        DiskRevision++;
        DiskState = DiskState.Current;
        _recoveredDraft = false;
        Dirty = false;
        // A previewed draft already owns this exact content and resource base. Saving
        // it must not parse/layout it again or discard selection and decoded images.
        // Save As and changed editor text still rebuild; explicit refresh uses Load.
        if (viewUnchanged) EnsureView().EnsureLayout(); else ReloadView();
    }

    // The view resolves relative links and images against the document's own folder.
    private string ViewDirectory => Path.Length == 0 ? "" : System.IO.Path.GetDirectoryName(Path);

    private void ReloadView() => EnsureView().Load(ViewDirectory, Text);

    public void Dispose()
    {
        if (IsDisposed) return;
        // Closing a tab must return its undo characters to the global budget, otherwise
        // the process-wide ceiling drifts down over a long session.
        EditHistory.Reset("");
        _view?.Dispose();
        IsDisposed = true;
    }

    // Refresh swaps in a freshly loaded session; the reader's table-of-contents choice
    // is view state that must survive the swap, like the reading offset.
    internal void InheritViewState(DocumentSession previous) => EnsureView().ShowToc = previous.EnsureView().ShowToc;
}
