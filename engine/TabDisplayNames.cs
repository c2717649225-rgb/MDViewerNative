using System;
using System.Collections.Generic;
using System.Linq;

namespace MDViewerNative;

// Display policy only. No file I/O, session mutation, path normalization or command routing.
internal sealed class TabDisplayNames
{
    private sealed record Cached(string Title, string Path, string Display);
    private sealed class SuffixNode
    {
        public int Count;
        public readonly Dictionary<string, SuffixNode> Children = new(StringComparer.OrdinalIgnoreCase);
    }
    private Dictionary<Guid, Cached> _names = new();
    internal int RebuildCount { get; private set; }
    internal int CachedCount => _names.Count;

    public List<ToolbarTab> Project(IReadOnlyList<ToolbarTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var ids = new HashSet<Guid>();
        bool changed = tabs.Count != _names.Count;
        foreach (var tab in tabs)
        {
            if (!ids.Add(tab.Id)) throw new ArgumentException("Duplicate document identity in tab snapshot.", nameof(tabs));
            changed |= !_names.TryGetValue(tab.Id, out var old) || old.Title != tab.Title || old.Path != tab.FullPath;
        }
        if (changed) Rebuild(tabs);
        var result = new List<ToolbarTab>(tabs.Count);
        foreach (var tab in tabs) result.Add(tab with { DisplayTitle = _names[tab.Id].Display });
        return result;
    }

    private static string BaseTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "(未命名)";
        return title.Length > 3 && title.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? title[..^3] : title;
    }

    private void Rebuild(IReadOnlyList<ToolbarTab> tabs)
    {
        RebuildCount++;
        var labels = new string[tabs.Count];
        // 试用修复：只有一个未命名文档时不挂身份后缀（「(未命名) · e223ba5e」像调试信息）；多个时才用后缀区分。
        int untitled = tabs.Count(t => string.IsNullOrEmpty(t.FullPath) && !t.IsHome);
        for (int i = 0; i < tabs.Count; i++)
        {
            labels[i] = BaseTitle(tabs[i].Title);
            if (untitled > 1 && string.IsNullOrEmpty(tabs[i].FullPath)) labels[i] += " · " + tabs[i].Id.ToString("N")[^8..];
        }
        // Group the whole set, including offscreen tabs. A reversed directory trie
        // finds each item's shortest unique suffix in linear component traversal.
        foreach (var group in Enumerable.Range(0, tabs.Count).GroupBy(i => labels[i], StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var indices = group.ToArray();
            if (indices.Length < 2) continue;
            var root = new SuffixNode();
            var parents = new Dictionary<int, string[]>();
            foreach (int index in indices)
            {
                string[] parts = ParentParts(tabs[index].FullPath);
                parents[index] = parts;
                var node = root;
                for (int p = parts.Length - 1; p >= 0; p--)
                {
                    if (!node.Children.TryGetValue(parts[p], out var child)) node.Children[parts[p]] = child = new SuffixNode();
                    child.Count++; node = child;
                }
            }
            foreach (int index in indices)
            {
                string[] parts = parents[index];
                var node = root;
                string qualifier = null;
                for (int p = parts.Length - 1; p >= 0; p--)
                {
                    node = node.Children[parts[p]];
                    if (node.Count == 1) { qualifier = JoinSuffix(parts, p); break; }
                }
                // Same-directory README / README.md and equivalent-path snapshots
                // cannot be distinguished by any directory suffix. Keep literal paths.
                if (qualifier == null && !string.IsNullOrEmpty(tabs[index].FullPath)) qualifier = tabs[index].FullPath;
                if (qualifier != null) labels[index] = qualifier + " › " + labels[index];
            }
        }

        // A real filename can equal a generated label. Prefix only participants
        // with their complete identity, then repeat for any newly exposed literal
        // collision. Already-prefixed identities are unique, so this is bounded by n.
        var qualified = new bool[tabs.Count];
        while (true)
        {
            var collisions = Enumerable.Range(0, tabs.Count).GroupBy(i => labels[i], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
            if (collisions.Length == 0) break;
            bool advanced = false;
            foreach (int i in collisions)
            {
                if (qualified[i]) continue;
                labels[i] = "[" + tabs[i].Id.ToString("N") + "] " + labels[i];
                qualified[i] = true; advanced = true;
            }
            if (!advanced) throw new InvalidOperationException("Tab display identities must be unique.");
        }
        var next = new Dictionary<Guid, Cached>(tabs.Count);
        for (int i = 0; i < tabs.Count; i++) next.Add(tabs[i].Id, new(tabs[i].Title, tabs[i].FullPath, labels[i]));
        _names = next; // Closing a document releases its old strings; no historical-name cache.
    }

    private static string[] ParentParts(string path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        int last = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (last < 0) return Array.Empty<string>();
        string directory = path[..last].Replace('/', '\\');
        var parts = new List<string>();
        if (directory.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var unc = directory.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (unc.Length >= 2)
            {
                parts.Add("\\\\" + unc[0] + "\\" + unc[1]);
                parts.AddRange(unc.Skip(2));
                return parts.ToArray();
            }
        }
        if (directory.Length >= 2 && directory[1] == ':')
        {
            parts.Add(directory[..2]);
            directory = directory[2..];
        }
        else if (directory.StartsWith('\\') || last == 0) parts.Add("\\");
        parts.AddRange(directory.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return parts.ToArray();
    }

    private static string JoinSuffix(string[] parts, int start)
    {
        if (start == 0 && parts[0] == "\\") return "\\" + string.Join("\\", parts.Skip(1));
        string result = string.Join("\\", parts.Skip(start));
        if (start == 0 && parts.Length == 1 && (result.EndsWith(':') || result.StartsWith("\\\\", StringComparison.Ordinal))) result += "\\";
        return result;
    }
}
