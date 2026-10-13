using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MDViewerNative;

// One bounded background read at a time. Only the UI thread starts/completes checks;
// workers never access a document, window or native rendering resource.
internal sealed class DiskChangeMonitor
{
    private Task<DiskState> _pending;
    private DocumentSession _session;
    private string _path;
    private FileVersion _version;
    private long _revision;
    public bool Pending => _pending != null;
    public bool Ready => _pending?.IsCompleted == true;

    internal static DiskState Observe(string path, FileVersion expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var actual = new FileVersion(Convert.ToHexString(SHA256.HashData(stream)));
            return actual == expected ? DiskState.Current : DiskState.Changed;
        }
        catch (FileNotFoundException) { return DiskState.Missing; }
        catch (DirectoryNotFoundException) { return DiskState.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return DiskState.Unavailable; }
    }

    public bool Start(DocumentSession session)
    {
        if (Pending || session.IsDisposed || session.Path.Length == 0) return false;
        _session = session;
        string path = _path = session.Path;
        FileVersion version = _version = session.DiskVersion;
        _revision = session.DiskRevision;
        _pending = Task.Run(() => Observe(path, version));
        return true;
    }

    public bool TryComplete(out DocumentSession session)
    {
        session = null;
        if (!Ready) return false;
        session = _session;
        DiskState state = _pending.Status == TaskStatus.RanToCompletion ? _pending.Result : DiskState.Unavailable;
        _ = _pending.Exception; // Observe unexpected worker failures without escaping a window callback.
        _pending = null;
        _session = null;
        // A save, Save As, refresh or closed tab invalidates an older observation.
        if (!session.IsDisposed && session.Path == _path && session.DiskVersion == _version && session.DiskRevision == _revision)
            session.DiskState = state;
        return true;
    }
}
