using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDViewerNative;

internal sealed record DocumentBookmark(Guid Id, string Path, string Title, ReadingLocation Location);
internal sealed record BookmarkFile(int Schema, List<DocumentBookmark> Items);
[JsonSerializable(typeof(BookmarkFile))]
internal partial class BookmarkJsonContext : JsonSerializerContext { }

internal sealed class BookmarkStore
{
    private readonly List<DocumentBookmark> _items = new();
    public IReadOnlyList<DocumentBookmark> Items => _items;
    private static string FilePath => Path.Combine(AppStorage.DirectoryPath, "bookmarks.json");
    // 文件来自更新的版本(schema > 1)时不覆盖它:本次会话的增删只留在内存(E7,ADR-0001 第 4 条)。
    private bool _newerFile;
    // 文件读不懂(损坏、字段类型不对、超限、缺版本号):读取时不动它,下次保存前先改名保留(E23)。
    private bool _unreadable;
    public void Load()
    {
        _items.Clear();
        _newerFile = false;
        _unreadable = false;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (new FileInfo(FilePath).Length > 512 * 1024) { Unreadable("larger than 512 KiB"); return; }
            byte[] bytes = File.ReadAllBytes(FilePath);
            int? schema = AppStorage.ReadSchema(bytes);
            if (schema > 1)
            {
                _newerFile = true;
                AppLog.Write("bookmarks.json schema " + schema + " is newer than this version; the file is kept and changes are not saved");
                return;
            }
            if (schema != 1) { Unreadable("schema " + (schema?.ToString() ?? "missing")); return; }
            var file = JsonSerializer.Deserialize(bytes, BookmarkJsonContext.Default.BookmarkFile) ?? throw new JsonException("null document");
            if (file.Items == null) return;
            foreach (var item in file.Items.Take(256))
                if (item != null && item.Id != Guid.Empty && !string.IsNullOrEmpty(item.Path) && Path.IsPathFullyQualified(item.Path) && item.Path.Length <= 4096 && item.Title != null && item.Title.Length <= 256
                    && item.Location is { Block: >= 0 } location && location.Block <= 1000000 && location.Fingerprint?.Length == 64 && location.Fingerprint.All(Uri.IsHexDigit) && location.Fraction is >= 0 and <= 1 && location.Progress is >= 0 and <= 1) _items.Add(item);
        }
        catch (Exception ex) { _unreadable = true; AppLog.Error("read bookmarks", ex); }
    }
    void Unreadable(string reason)
    {
        _unreadable = true;
        AppLog.Write("bookmarks.json is unreadable (" + reason + "); it will be kept as a .corrupt copy before the next save");
    }
    public void Add(string path, string title, ReadingLocation location)
    {
        if (string.IsNullOrEmpty(path) || location == null) throw new InvalidOperationException("书签需要已命名文档和可定位内容。");
        if (_items.Count >= 256) throw new InvalidOperationException("书签达到 256 个上限，请先删除不再需要的书签。");
        _items.Add(new(Guid.NewGuid(), Path.GetFullPath(path), (title ?? "书签")[..Math.Min(256, (title ?? "书签").Length)], location)); Save();
    }
    public void Delete(Guid id) { _items.RemoveAll(i => i.Id == id); Save(); }
    public void RenamePath(string oldPath, string newPath)
    {
        for (int i = 0; i < _items.Count; i++) if (string.Equals(_items[i].Path, oldPath, StringComparison.OrdinalIgnoreCase)) _items[i] = _items[i] with { Path = newPath };
        Save();
    }
    private void Save()
    {
        if (_newerFile) return;
        if (_unreadable) { if (!AppStorage.SetAside(FilePath)) return; _unreadable = false; }
        AtomicFile.WriteState(FilePath, JsonSerializer.SerializeToUtf8Bytes(new BookmarkFile(1, _items), BookmarkJsonContext.Default.BookmarkFile));
    }
}
