using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Process-wide bound on retained undo/redo delta characters, mirroring LayoutCacheBudget.
//
// Each document already caps its own history (Limit steps, CharacterBudget characters), but
// those caps are per document: N open tabs retained N times the per-document budget with no
// process-wide ceiling. This adds that ceiling.
//
// This is a character-count budget, not a claim about managed heap bytes: a retained delta
// also carries object overhead this does not model. Eviction drops the OLDEST edits of the
// least recently touched history, which is the same direction each history already trims.
// The history currently being recorded into is never chosen as a victim.
internal static class UndoBudget
{
    // Default 16 M characters = 32 MiB of UTF-16 payload across every open document.
    internal const long DefaultLimit = 16L * 1024 * 1024;

    // Effective ceiling, taken from preferences (保存与版本历史 → 全部撤销历史上限).
    internal static long Limit =>
        (long)Math.Clamp(AppPreferences.Current.UndoBudgetKilochars, 1024, 262144) * 1024;

    private static readonly LinkedList<TextEditHistory> Owners = new();
    private static readonly Dictionary<TextEditHistory, LinkedListNode<TextEditHistory>> Nodes = new();

    internal static long Retained { get; private set; }

    // Retained managed source snapshots (Text + saved text per open document), reported by
    // the host. Counted separately from undo deltas because it is not evictable: the source
    // text of an open document must stay. Shared current/saved references count once
    // within each session; this is not a global heap-object deduplication measurement.
    // Exposed so "全局预算" can be reported honestly
    // instead of implying undo history is the only managed text under a ceiling.
    internal static long SourceCharacters { get; private set; }

    internal static void ReportSources(long characters) => SourceCharacters = Math.Max(0, characters);

    // Total managed text characters the process is holding: evictable undo deltas plus
    // non-evictable source snapshots.
    internal static long TotalManagedCharacters => Retained + SourceCharacters;

    internal static void Touch(TextEditHistory history)
    {
        if (Nodes.TryGetValue(history, out var node)) { Owners.Remove(node); Owners.AddFirst(node); }
        else Nodes[history] = Owners.AddFirst(history);
    }

    internal static void Forget(TextEditHistory history)
    {
        if (!Nodes.TryGetValue(history, out var node)) return;
        Owners.Remove(node);
        Nodes.Remove(history);
    }

    // Reports a per-history delta; the history stays authoritative for its own accounting.
    internal static void Adjust(long delta)
    {
        Retained += delta;
        if (Retained < 0) Retained = 0;
    }

    // Trims other histories until the global budget is satisfied. The active history is
    // exempt: it was just edited, and dropping the user's newest undo step is never right.
    internal static void Enforce(TextEditHistory active)
    {
        int guard = 0;
        while (Retained > Limit && guard++ < 10_000)
        {
            var victim = Owners.Last;
            while (victim != null && (ReferenceEquals(victim.Value, active) || victim.Value.RetainedCharacters == 0))
                victim = victim.Previous;
            if (victim == null) return;
            if (!victim.Value.TrimOldest()) return;
        }
    }
}
