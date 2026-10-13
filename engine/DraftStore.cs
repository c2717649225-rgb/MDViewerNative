using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDViewerNative;

internal sealed record DraftSnapshot(int Schema, Guid Id, string Path, string Text, string SavedText,
    string DiskHash, bool Gbk, bool Bom, string LineEnding, TextEncoding Encoding = TextEncoding.Auto,
    TextEncoding SavedEncoding = TextEncoding.Auto, bool? SavedBom = null, string SavedLineEnding = null);

[JsonSerializable(typeof(DraftSnapshot))]
internal partial class DraftJsonContext : JsonSerializerContext { }

// Persistence only: the host supplies the live EDIT text and decides when to restore/discard.
internal sealed class DraftStore
{
    readonly string _directory;
    readonly Dictionary<Guid, DraftSnapshot> _written = new();
    public DraftStore(string directory) => _directory = System.IO.Path.GetFullPath(directory);
    string FilePath(Guid id) => System.IO.Path.Combine(_directory, id.ToString("N") + ".json");

    public void Write(DraftSnapshot snapshot)
    {
        Validate(snapshot);
        if (_written.TryGetValue(snapshot.Id, out var previous) && previous == snapshot) return;
        AtomicFile.WriteState(FilePath(snapshot.Id), JsonSerializer.SerializeToUtf8Bytes(snapshot, DraftJsonContext.Default.DraftSnapshot));
        _written[snapshot.Id] = snapshot;
    }

    public void Remove(Guid id)
    {
        try { File.Delete(FilePath(id)); }
        catch (DirectoryNotFoundException) { } // Saving before the first checkpoint has nothing to remove.
        _written.Remove(id);
    }

    public List<DraftSnapshot> ReadAll()
    {
        var result = new List<DraftSnapshot>();
        string[] files;
        try { files = Directory.GetFiles(_directory, "*.json"); }
        catch (DirectoryNotFoundException) { return result; }
        foreach (string path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize(File.ReadAllBytes(path), DraftJsonContext.Default.DraftSnapshot);
                if (snapshot != null && snapshot.Schema is not (1 or 2))
                { AppLog.Write("Unsupported draft schema retained: " + path); continue; }
                Validate(snapshot);
                if (!string.Equals(path, FilePath(snapshot.Id), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Draft identity does not match its file name.");
                result.Add(snapshot);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                AppLog.Error("read corrupt draft " + path, ex);
                // Preserve damaged bytes for manual recovery without blocking all other tabs.
                try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); }
                catch (Exception moveError) { AppLog.Error("quarantine draft", moveError); }
            }
            catch (Exception ex) { AppLog.Error("read draft " + path, ex); }
        }
        return result;
    }

    static void Validate(DraftSnapshot s)
    {
        if (s == null || s.Schema is not (1 or 2) || s.Id == Guid.Empty || s.Text == null || s.SavedText == null || s.Path == null
            || (s.Path.Length > 0 && !System.IO.Path.IsPathFullyQualified(s.Path))
            || (s.LineEnding != "\n" && s.LineEnding != "\r\n" && s.LineEnding != "\r")
            || !Enum.IsDefined(s.Encoding) || !Enum.IsDefined(s.SavedEncoding)
            || (s.SavedLineEnding != null && s.SavedLineEnding is not ("\n" or "\r\n" or "\r"))
            || (s.DiskHash != null && (s.DiskHash.Length != 64 || !s.DiskHash.All(Uri.IsHexDigit))))
            throw new InvalidDataException("Invalid draft snapshot.");
    }
}
