using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Window-independent policy for a bounded user-selected save operation. Identity is
// resolved immediately before each action: dialogs may change or close a document.
// Prior successful saves are not rolled back; cancellation stops subsequent writes.
internal static class DocumentBatch
{
    internal readonly record struct Result(bool Completed, DocumentSession ReadOnlyDocument = null);

    internal static Result Save(IEnumerable<Guid> identities, Func<Guid, DocumentSession> resolve,
        Func<DocumentSession, bool> save)
    {
        var visited = new HashSet<Guid>();
        foreach (Guid id in identities)
        {
            if (!visited.Add(id)) continue;
            var document = resolve(id);
            if (document == null || document.IsDisposed || !document.Dirty) continue;
            if (document.ReadOnly) return new(false, document);
            if (!save(document)) return new(false);
        }
        return new(true);
    }

    // Window-independent policy for discarding a user-selected set of drafts. The host owns
    // the confirmation dialog and the per-document side effects (clearing the recovery draft,
    // resyncing a visible editor); this decides which documents are eligible and in what
    // order, and refuses to touch documents that changed identity or were disposed meanwhile.
    internal static int Discard(IEnumerable<Guid> identities, Func<Guid, DocumentSession> resolve,
        Action<DocumentSession> discard)
    {
        var visited = new HashSet<Guid>();
        int discarded = 0;
        foreach (Guid id in identities)
        {
            if (!visited.Add(id)) continue;
            var document = resolve(id);
            // A dialog may have saved or closed the document between selection and action.
            if (document == null || document.IsDisposed || !document.Dirty) continue;
            discard(document);
            discarded++;
        }
        return discarded;
    }

    // Documents from a candidate set that still hold unsaved changes, in the caller's order.
    // Used to drive the "处理未保存文档" loop without the host re-implementing the filter.
    internal static List<DocumentSession> Pending(IEnumerable<Guid> identities, Func<Guid, DocumentSession> resolve)
    {
        var visited = new HashSet<Guid>();
        var pending = new List<DocumentSession>();
        foreach (Guid id in identities)
        {
            if (!visited.Add(id)) continue;
            var document = resolve(id);
            if (document != null && !document.IsDisposed && document.Dirty) pending.Add(document);
        }
        return pending;
    }
}
