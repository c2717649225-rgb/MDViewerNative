using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Owns the per-view LRU and its process-wide budget registration. Layout and
// rendering code can borrow entries without owning the eviction policy.
// E18a: generic over the entry type and told how to release it, so the cache no
// longer depends on DocView. LRU nodes live in _nodes instead of on the entry;
// _entries keeps its shape because self-tests inspect it by reflection.
internal sealed class LayoutCache<TEntry> where TEntry : class
{
    private const int MaxEntries = 512;
    private readonly LayoutCacheBudget.IView _owner;
    private readonly Action<TEntry> _release;
    private readonly Dictionary<int, TEntry> _entries = new();
    private readonly Dictionary<int, LinkedListNode<int>> _nodes = new();
    private readonly LinkedList<int> _lru = new();
    private LayoutCacheBudget.Owner _budgetOwner;

    internal LayoutCache(LayoutCacheBudget.IView owner, Action<TEntry> release)
    {
        _owner = owner;
        _release = release;
    }

    internal int Count => _entries.Count;

    internal bool TryGet(int index, out TEntry entry)
    {
        if (!_entries.TryGetValue(index, out entry)) return false;
        LayoutCacheBudget.Touch(_budgetOwner);
        var node = _nodes[index];
        _lru.Remove(node);
        _lru.AddFirst(node);
        return true;
    }

    internal void Add(int index, TEntry entry)
    {
        _entries[index] = entry;
        _nodes[index] = _lru.AddFirst(index);
        _budgetOwner ??= LayoutCacheBudget.Register(_owner);
        LayoutCacheBudget.Add(_budgetOwner);
        while (_lru.Count > MaxEntries) EvictLeastRecent();
    }

    internal void EvictLeastRecent()
    {
        if (_lru.Last == null) return;
        int index = _lru.Last.Value;
        _lru.RemoveLast();
        _release(_entries[index]);
        _entries.Remove(index);
        _nodes.Remove(index);
        LayoutCacheBudget.Remove(_budgetOwner);
    }

    internal void Clear()
    {
        if (_budgetOwner != null)
        {
            LayoutCacheBudget.Unregister(_budgetOwner);
            _budgetOwner = null;
        }
        foreach (var entry in _entries.Values) _release(entry);
        _entries.Clear();
        _nodes.Clear();
        _lru.Clear();
    }
}
