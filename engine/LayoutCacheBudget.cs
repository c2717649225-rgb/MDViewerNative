using System;
using System.Collections.Generic;

namespace MDViewerNative;

// UI-thread-only, process-wide bound on retained root layout entries. This is an
// entry budget, not a claim about native byte size or the whole process heap.
// Never evict from the view currently building/borrowing an entry; its own LRU
// is bounded separately. Other views rebuild exact layouts lazily when revisited.
internal static class LayoutCacheBudget
{
    internal const int Limit = 2048;

    // Implemented by the view that owns a LayoutCache; held weakly, so a collected view is unregistered lazily.
    internal interface IView { void EvictLeastRecentLayout(); }

    internal sealed class Owner
    {
        internal readonly WeakReference<IView> View;
        internal LinkedListNode<Owner> Node;
        internal int Count;
        internal Owner(IView view) => View = new(view);
    }

    private static readonly LinkedList<Owner> Owners = new();
    internal static int Retained { get; private set; }

    internal static Owner Register(IView view)
    {
        var owner = new Owner(view);
        owner.Node = Owners.AddFirst(owner);
        return owner;
    }

    internal static void Touch(Owner owner)
    {
        Owners.Remove(owner.Node);
        Owners.AddFirst(owner.Node);
    }

    internal static void Add(Owner owner)
    {
        owner.Count++; Retained++; Touch(owner);
        while (Retained > Limit)
        {
            var victim = Owners.Last;
            while (victim != null && (victim.Value == owner || victim.Value.Count == 0)) victim = victim.Previous;
            if (victim == null) break;
            if (!victim.Value.View.TryGetTarget(out var view)) Unregister(victim.Value);
            else view.EvictLeastRecentLayout();
        }
    }

    internal static void Remove(Owner owner)
    {
        owner.Count--; Retained--;
    }

    internal static void Unregister(Owner owner)
    {
        Retained -= owner.Count;
        owner.Count = 0;
        Owners.Remove(owner.Node);
    }
}
