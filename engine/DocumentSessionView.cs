using System;

namespace MDViewerNative;

// The session owns document identity and persistence state; the adapter owns the
// rendering lifetime. This is the workflow layer's port: it names no rendering type,
// so DocumentSession does not depend on DocView even indirectly (E19a). The concrete
// DocViewAdapter lives host-side in DocumentViewAccess.
internal interface IDocumentViewAdapter : IDisposable
{
    void SetDirectory(string directory);
    void Load(string directory, string text);
    void EnsureLayout();
    // View state the workflow carries across a refresh (DocumentSession.InheritViewState).
    bool ShowToc { get; set; }
}
