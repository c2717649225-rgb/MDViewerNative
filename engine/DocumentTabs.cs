using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace MDViewerNative;

// Window-independent tab transitions. A cancelled guard never changes identity or ownership.
internal sealed class DocumentTabs : IReadOnlyList<DocumentSession>, IDisposable
{
    private readonly Func<DocumentSession> _createSession;
    private readonly List<DocumentSession> _items;
    private bool _disposed;
    private readonly List<(string Path, float Offset)> _closed = new();
    internal DocumentTabs(Func<DocumentSession> createSession = null)
    {
        _createSession = createSession ?? (() => new DocumentSession());
        _items = new() { CreateSession() };
    }

    private DocumentSession CreateSession() => _createSession();
    public bool CanReopen => _closed.Count > 0;
    public (string Path, float Offset)? LastClosed => _closed.Count == 0 ? null : _closed[^1];
    public void ConsumeClosed() { if (_closed.Count > 0) _closed.RemoveAt(_closed.Count - 1); }
    public DocumentSession NewDocument() { var session = CreateSession(); Add(session); return session; }
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= Count || to < 0 || to >= Count || from == to) return false;
        var active = Current; var item = _items[from];
        _items.RemoveAt(from); _items.Insert(to, item); ActiveIndex = _items.IndexOf(active);
        return true;
    }
    public int ActiveIndex { get; private set; }
    public DocumentSession Current => _items[ActiveIndex];
    public int Count => _items.Count;
    public DocumentSession this[int index] => _items[index];

    public int IndexOfPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return _items.FindIndex(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    // Identity lookups belong here for the same reason IndexOfPath does: the host was
    // hand-rolling "find the tab with this RecoveryId" in seven places, three of them as
    // resolver lambdas passed to DocumentBatch and four as open-coded index scans (51).
    public DocumentSession ById(Guid id) => _items.Find(item => item.RecoveryId == id);

    public int IndexOfId(Guid id) => _items.FindIndex(item => item.RecoveryId == id);

    public int IndexOf(DocumentSession session) =>
        _items.FindIndex(item => ReferenceEquals(item, session));

    // Closing walks back to front so earlier removals cannot shift the indices still to
    // come — getting that order wrong silently closes the wrong tab.
    public IEnumerable<int> IndicesOfIds(IEnumerable<Guid> ids)
    {
        var wanted = new HashSet<Guid>(ids);
        for (int i = _items.Count - 1; i >= 0; i--)
            if (wanted.Contains(_items[i].RecoveryId)) yield return i;
    }

    // E16:打开是否保留当前文档(激活已有标签或新增标签),否则当前文档会被替换。
    // 宿主据此决定打开前是只退出编辑还是做未保存确认;TryOpen 用同一条规则,两边不会分叉。
    public bool OpenKeepsCurrent(string path, bool newTab) => IndexOfPath(path) >= 0 || AddsTab(newTab);

    // 新标签打开时,空白且未改动的当前标签直接被替换,不留下多余的空标签。
    private bool AddsTab(bool newTab) => newTab && (Current.Path.Length > 0 || Current.Dirty);

    // Read and lay out a candidate before preparing the current document.
    // Ownership changes only after preparation succeeds; cancellation disposes the candidate.
    public bool TryOpen(string path, bool newTab, Func<bool> prepareCurrent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        path = Path.GetFullPath(path);
        int existing = IndexOfPath(path);
        if (existing >= 0) return existing == ActiveIndex || TryActivate(existing, prepareCurrent);
        var candidate = CreateSession();
        try
        {
            candidate.Load(path, DocumentFile.Read(path));
            if (!prepareCurrent()) return false;
            // Saving the previous draft may have changed its path via Save As.
            existing = IndexOfPath(path);
            if (existing >= 0) { ActiveIndex = existing; return true; }
            if (AddsTab(newTab)) Add(candidate);
            else
            {
                var previous = Current;
                _items[ActiveIndex] = candidate;
                previous.Dispose();
            }
            candidate = null;
            return true;
        }
        finally { candidate?.Dispose(); }
    }

    // Refresh is a read-only transaction. A failed read, cancelled discard or changed
    // disk version never replaces the original session (including its live EDIT draft).
    public bool TryRefresh(Func<bool> confirmDiscard)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = Current;
        if (previous.Path.Length == 0) return false;
        string path = previous.Path;
        var candidate = CreateSession();
        try
        {
            candidate.Load(path, DocumentFile.Read(path));
            candidate.InheritViewState(previous);
            candidate.ScrollOffset = previous.ScrollOffset;
            if (previous.Dirty && !confirmDiscard()) return false;
            if (!ReferenceEquals(previous, Current) || previous.Path != path) return false;
            FileVersion actual = AtomicFile.Observe(path);
            if (actual != candidate.DiskVersion) throw new FileConflictException(path, actual);
            _items[ActiveIndex] = candidate;
            candidate = null;
            previous.Dispose();
            return true;
        }
        finally { candidate?.Dispose(); }
    }

    public void Add(DocumentSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (session.IsDisposed || _items.Contains(session)) throw new ArgumentException("Session must be new and live.", nameof(session));
        _items.Add(session);
        ActiveIndex = _items.Count - 1;
    }

    // Startup restoration may replace a clean disk tab, but must never displace a live draft.
    public void RestoreDraft(DraftSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int existing = snapshot.Path.Length == 0 ? -1 : IndexOfPath(snapshot.Path);
        if (existing >= 0 && _items[existing].Dirty) throw new InvalidOperationException("A draft is already open for this path.");
        var candidate = CreateSession();
        try
        {
            candidate.RestoreDraft(snapshot);
            if (existing < 0 && Count == 1 && Current.Path.Length == 0 && !Current.Dirty) existing = 0;
            if (existing >= 0)
            {
                var previous = _items[existing];
                _items[existing] = candidate;
                ActiveIndex = existing;
                previous.Dispose();
            }
            else Add(candidate);
            candidate = null;
        }
        finally { candidate?.Dispose(); }
    }

    public bool TryActivate(int index, Func<bool> prepareCurrent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= Count || index == ActiveIndex) return false;
        if (!prepareCurrent()) return false;
        ActiveIndex = index;
        return true;
    }

    public bool TryClose(int index, Func<DocumentSession, bool> canClose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= Count || !canClose(_items[index])) return false;
        var removed = _items[index];
        if (removed.Path.Length > 0)
        {
            _closed.Add((removed.Path, removed.ScrollOffset));
            if (_closed.Count > 20) _closed.RemoveAt(0);
        }
        _items.RemoveAt(index);
        if (index < ActiveIndex) ActiveIndex--;
        else if (ActiveIndex >= Count) ActiveIndex = Math.Max(0, Count - 1);
        if (Count == 0) _items.Add(CreateSession());
        removed.Dispose();
        return true;
    }

    public IEnumerator<DocumentSession> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var session in _items) session.Dispose();
        _items.Clear();
        _disposed = true;
    }
}
