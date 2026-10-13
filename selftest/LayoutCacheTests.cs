using System;
using System.Collections.Generic;
using System.Linq;
using MDViewerNative;

// E18a：LayoutCache 与 LayoutCacheBudget 不再依赖 DocView，可以用假条目、假视图直接测 LRU 与全局预算契约。
// 预算是进程级静态状态：每个用例结束都 Clear。其他测试留下的真实视图也会登记在预算里，
// 超限时同样可能被淘汰，所以压力用例之后只能断言 Retained 不高于用例前（与 ProductContinuationTests 同口径）。
internal static class LayoutCacheTests
{
    sealed class Entry { public readonly int Id; public Entry(int id) => Id = id; }

    sealed class View : LayoutCacheBudget.IView
    {
        public readonly List<int> Released = new();
        public readonly LayoutCache<Entry> Cache;
        public View() => Cache = new LayoutCache<Entry>(this, e => Released.Add(e.Id));
        void LayoutCacheBudget.IView.EvictLeastRecentLayout() => Cache.EvictLeastRecent();
    }

    internal static void Run(Action<string, bool, string> check)
    {
        int before = LayoutCacheBudget.Retained;

        var one = new View();
        for (int i = 0; i < 600; i++) one.Cache.Add(i, new Entry(i));
        check("LayoutCache/per-view cap evicts least recent first", one.Cache.Count == 512 && one.Released.SequenceEqual(Enumerable.Range(0, 88)),
            "count=" + one.Cache.Count + " released=" + one.Released.Count);
        one.Cache.Clear();
        check("LayoutCache/clear releases every entry and its budget", one.Cache.Count == 0 && one.Released.Count == 600 && LayoutCacheBudget.Retained == before,
            "released=" + one.Released.Count + " retained=" + LayoutCacheBudget.Retained + " before=" + before);

        var lru = new View();
        for (int i = 0; i < 512; i++) lru.Cache.Add(i, new Entry(i));
        bool hit = lru.Cache.TryGet(0, out var zero) && zero.Id == 0;
        lru.Cache.Add(512, new Entry(512));
        check("LayoutCache/hit refreshes recency", hit && lru.Released.SequenceEqual(new[] { 1 }) && lru.Cache.TryGet(0, out _),
            "released=" + string.Join(",", lru.Released));
        check("LayoutCache/miss leaves cache unchanged", !lru.Cache.TryGet(9999, out var missing) && missing == null && lru.Cache.Count == 512, "");
        lru.Cache.Clear();

        var views = Enumerable.Range(0, 5).Select(_ => new View()).ToList();
        bool bounded = true;
        foreach (var v in views)
        {
            for (int i = 0; i < 512; i++) v.Cache.Add(i, new Entry(i));
            bounded &= LayoutCacheBudget.Retained <= LayoutCacheBudget.Limit;
        }
        var last = views[^1];
        check("LayoutCache/global budget evicts other views, never the current one", bounded && last.Cache.Count == 512 && last.Released.Count == 0 && views[0].Cache.Count < 512,
            "first=" + views[0].Cache.Count + " last=" + last.Cache.Count + " retained=" + LayoutCacheBudget.Retained);
        foreach (var v in views) v.Cache.Clear();
        check("LayoutCache/budget accounting never exceeds baseline", LayoutCacheBudget.Retained <= before, "retained=" + LayoutCacheBudget.Retained + " before=" + before);
    }
}
