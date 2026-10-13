using System;
using System.IO;
using System.Security.Cryptography;

namespace MDViewerNative;

// A version describes the bytes observed by the reader, not just a timestamp.
internal sealed record FileVersion(string Hash)
{
    public static readonly FileVersion Missing = new(Hash: null);
    public bool Exists => Hash != null;
    public static FileVersion Of(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));
}

internal sealed class FileConflictException : IOException
{
    public string TargetPath { get; }
    public FileVersion ActualVersion { get; }
    public FileConflictException(string path, FileVersion actual)
        : base("磁盘上的文件已更改或删除，请重新确认保存方式。")
    { TargetPath = path; ActualVersion = actual; }
}

// Documents and internal snapshots share the temporary-file commit mechanism.
internal static class AtomicFile
{
    public static FileVersion Observe(string path)
    {
        try { return FileVersion.Of(File.ReadAllBytes(path)); }
        catch (FileNotFoundException) { return FileVersion.Missing; }
        catch (DirectoryNotFoundException) { return FileVersion.Missing; }
    }

    // Markdown saves and HTML exports: an absent expectation means create-only.
    public static FileVersion Write(string path, byte[] bytes, FileVersion expected = null)
    {
        path = Path.GetFullPath(path);
        expected ??= FileVersion.Missing;
        Commit(path, bytes, temporary =>
        {
            // Check immediately before replacement; external programs do not share our locks.
            FileVersion actual = Observe(path);
            if (actual != expected) throw new FileConflictException(path, actual);
            if (actual.Exists) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path); // Never overwrite a file created after the check.
        });
        return FileVersion.Of(bytes);
    }

    // Internal state replaces its own snapshot without document conflicts or backup rotation.
    public static void WriteState(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Commit(path, bytes, temporary => File.Move(temporary, path, overwrite: true));
    }

    static void Commit(string path, byte[] bytes, Action<string> replace)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path), ".mdv-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            replace(temporary);
        }
        finally
        {
            try { if (ownsTemporary) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLog.Error("atomic temporary cleanup", ex); }
        }
    }
}
