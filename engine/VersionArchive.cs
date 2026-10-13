using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDViewerNative;

internal sealed record StoredVersion(int Schema, string Source, DateTime CreatedUtc, string Hash, byte[] Bytes);
[JsonSerializable(typeof(StoredVersion))]
internal partial class VersionJsonContext : JsonSerializerContext { }

internal static class VersionArchive
{
    private static string DirectoryPath => Path.Combine(AppStorage.DirectoryPath, "versions");
    private static string Prefix(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..24];
    public static void CapturePrevious(string source)
    {
        if (!AppPreferences.Current.VersionHistory) return;
        string backup = source + ".bak";
        if (!File.Exists(backup) || new FileInfo(backup).Length > 8 * 1024 * 1024) return;
        byte[] bytes = File.ReadAllBytes(backup);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var previous = List(source);
        if (previous.Count > 0 && previous[0].Version.Hash == hash) return;
        string file = Prefix(source) + "-" + DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N") + ".json";
        var version = new StoredVersion(1, Path.GetFullPath(source), DateTime.UtcNow, hash, bytes);
        AtomicFile.WriteState(Path.Combine(DirectoryPath, file), JsonSerializer.SerializeToUtf8Bytes(version, VersionJsonContext.Default.StoredVersion));
        foreach (var old in List(source).Skip(AppPreferences.Current.VersionsPerFile)) File.Delete(old.File);
        var files = new DirectoryInfo(DirectoryPath).GetFiles("*.json").Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"^[A-F0-9]{24}-[0-9]{16,20}-[a-f0-9]{32}\.json$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length), limit = AppPreferences.Current.HistoryMegabytes * 1024L * 1024L;
        foreach (var old in files)
        {
            if (total <= limit) break;
            long length = old.Length; old.Delete(); total -= length;
        }
    }
    public static List<(string File, StoredVersion Version)> List(string source)
    {
        var result = new List<(string, StoredVersion)>();
        if (!Directory.Exists(DirectoryPath) || string.IsNullOrEmpty(source)) return result;
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, Prefix(source) + "-*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 12 * 1024 * 1024) continue;
                var value = JsonSerializer.Deserialize(File.ReadAllBytes(path), VersionJsonContext.Default.StoredVersion);
                if (value?.Schema != 1 || value.Bytes == null || value.Bytes.Length > 8 * 1024 * 1024
                    || !string.Equals(value.Source, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
                    || Convert.ToHexString(SHA256.HashData(value.Bytes)) != value.Hash) continue;
                result.Add((path, value));
            }
            catch (Exception ex) { AppLog.Error("read stored version", ex); }
        }
        return result.OrderByDescending(x => x.Item2.CreatedUtc).ToList();
    }
    public static void Delete(string file)
    {
        string full = Path.GetFullPath(file), root = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(full) != ".json") throw new ArgumentException("Not an owned version record.");
        File.Delete(full);
    }
}
