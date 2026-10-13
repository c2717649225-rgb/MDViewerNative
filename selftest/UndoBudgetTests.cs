using System;
using System.Linq;
using MDViewerNative;

// Item 49: process-wide undo budget, and the configurable cancel grace window (item 47).
internal static class UndoBudgetTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        // --- Configurable grace window (47) ---
        int savedGrace = AppPreferences.Current.CancelGraceKilochars;
        try
        {
            AppPreferences.Current.CancelGraceKilochars = 400;
            check("Grace/default maps to 400k characters", DocumentWork.GraceChars == 400_000,
                "GraceChars=" + DocumentWork.GraceChars);

            // 0 means "always cancellable": a small document must now be interruptible.
            AppPreferences.Current.CancelGraceKilochars = 0;
            string small = string.Concat(Enumerable.Repeat("paragraph\n\n", 40));
            bool cancelled = false;
            using (DocumentWork.Begin(() => true))
            {
                try { MdParser.Parse(small); }
                catch (DocumentCancelledException) { cancelled = true; }
            }
            check("Grace/zero makes small documents cancellable", cancelled, "grace=0 must cancel");

            // A very large grace window must protect a document that would otherwise cancel.
            AppPreferences.Current.CancelGraceKilochars = 20000;
            string large = string.Concat(Enumerable.Repeat("# h\n\nbody text here\n\n", 20_000));
            bool cancelledBig = false;
            using (DocumentWork.Begin(() => true))
            {
                try { MdParser.Parse(large); }
                catch (DocumentCancelledException) { cancelledBig = true; }
            }
            check("Grace/large window protects big documents", !cancelledBig,
                $"len={large.Length} < grace={DocumentWork.GraceChars}");

            // Out-of-range values are clamped rather than trusted.
            AppPreferences.Current.CancelGraceKilochars = -5;
            check("Grace/negative clamps to zero", DocumentWork.GraceChars == 0, "GraceChars=" + DocumentWork.GraceChars);
            AppPreferences.Current.CancelGraceKilochars = 999_999;
            check("Grace/oversized clamps to ceiling", DocumentWork.GraceChars == 20_000_000, "GraceChars=" + DocumentWork.GraceChars);

            var normalized = AppPreferences.Normalize(new PreferenceData { CancelGraceKilochars = 999_999 });
            check("Grace/normalize clamps persisted value", normalized.CancelGraceKilochars == 20000,
                "value=" + normalized.CancelGraceKilochars);
        }
        finally { AppPreferences.Current.CancelGraceKilochars = savedGrace; }

        // --- Configurable global undo ceiling (49) ---
        int savedBudget = AppPreferences.Current.UndoBudgetKilochars;
        try
        {
            AppPreferences.Current.UndoBudgetKilochars = 16384;
            check("UndoLimit/default maps to 16 M characters", UndoBudget.Limit == 16L * 1024 * 1024,
                "Limit=" + UndoBudget.Limit);
            AppPreferences.Current.UndoBudgetKilochars = 1;
            check("UndoLimit/below floor clamps up", UndoBudget.Limit == 1024L * 1024, "Limit=" + UndoBudget.Limit);
            AppPreferences.Current.UndoBudgetKilochars = 9_999_999;
            check("UndoLimit/above ceiling clamps down", UndoBudget.Limit == 262144L * 1024, "Limit=" + UndoBudget.Limit);
            var normalizedBudget = AppPreferences.Normalize(new PreferenceData { UndoBudgetKilochars = 9_999_999 });
            check("UndoLimit/normalize clamps persisted value", normalizedBudget.UndoBudgetKilochars == 262144,
                "value=" + normalizedBudget.UndoBudgetKilochars);
        }
        finally { AppPreferences.Current.UndoBudgetKilochars = savedBudget; }

        // --- Global undo budget (49) ---
        long before = UndoBudget.Retained;

        var single = new TextEditHistory();
        single.Reset("");
        single.Record("hello", default);
        check("Undo/global budget tracks a record", UndoBudget.Retained > before,
            $"{before} -> {UndoBudget.Retained}");

        long afterRecord = UndoBudget.Retained;
        single.Reset("");
        check("Undo/reset returns characters to the budget", UndoBudget.Retained < afterRecord,
            $"{afterRecord} -> {UndoBudget.Retained}");

        // Per-document trimming must also decrement the global counter, not just the local one.
        var trimmed = new TextEditHistory { Limit = 3 };
        trimmed.Reset("");
        for (int i = 0; i < 12; i++) trimmed.Record(new string('x', i + 1), default);
        check("Undo/per-document limit still applies", trimmed.UndoCount <= 3, "undo=" + trimmed.UndoCount);
        check("Undo/local and global accounting agree", trimmed.RetainedCharacters >= 0 && UndoBudget.Retained >= trimmed.RetainedCharacters,
            $"local={trimmed.RetainedCharacters} global={UndoBudget.Retained}");
        trimmed.Reset("");

        // Many documents together must not exceed the global ceiling.
        var histories = new TextEditHistory[24];
        for (int i = 0; i < histories.Length; i++)
        {
            histories[i] = new TextEditHistory();
            histories[i].Reset("");
        }
        // Push past the global ceiling on purpose: 24 documents x 8 rounds x ~1 M characters
        // is far more than UndoBudget.Limit, so eviction must actually run.
        string chunk = new string('y', 1_000_000);
        for (int round = 0; round < 8; round++)
            for (int i = 0; i < histories.Length; i++)
                histories[i].Record(chunk + round + "-" + i, default);
        long unbounded = histories.Sum(h => h.RetainedCharacters);
        check("Undo/global ceiling is enforced across documents", UndoBudget.Retained <= UndoBudget.Limit,
            $"retained={UndoBudget.Retained} limit={UndoBudget.Limit}");
        check("Undo/eviction actually ran", unbounded <= UndoBudget.Limit,
            $"sum of per-document retention={unbounded} must be trimmed under {UndoBudget.Limit}");

        // The history just edited must keep its newest step: eviction targets other documents.
        check("Undo/active history keeps its newest edit", histories[^1].CanUndo, "active history must retain an undo step");

        foreach (var history in histories) history.Reset("");
        check("Undo/budget returns to baseline after reset", UndoBudget.Retained <= before + 16,
            $"before={before} after={UndoBudget.Retained}");

        // Lowering the ceiling must be enforceable immediately, not only on the next edit.
        int savedForShrink = AppPreferences.Current.UndoBudgetKilochars;
        try
        {
            var big = new TextEditHistory();
            big.Reset("");
            big.Record(new string('z', 3_000_000), default);
            var other = new TextEditHistory();
            other.Reset("");
            other.Record(new string('w', 3_000_000), default);
            AppPreferences.Current.UndoBudgetKilochars = 1024;   // 1 M characters
            UndoBudget.Enforce(null);
            check("UndoLimit/lowering the ceiling trims immediately", UndoBudget.Retained <= UndoBudget.Limit,
                $"retained={UndoBudget.Retained} limit={UndoBudget.Limit}");
            big.Reset(""); other.Reset("");

            // Enforce must trim only down to the ceiling, not evict everything. With a
            // ceiling well above a single document's retention, nothing may be dropped.
            var keeper = new TextEditHistory();
            keeper.Reset("");
            keeper.Record(new string('k', 500_000), default);
            long retainedBefore = UndoBudget.Retained;
            int stepsBefore = keeper.UndoCount;
            AppPreferences.Current.UndoBudgetKilochars = 262144;   // far above current usage
            UndoBudget.Enforce(null);
            check("UndoLimit/enforce under ceiling evicts nothing",
                UndoBudget.Retained == retainedBefore && keeper.UndoCount == stepsBefore,
                $"retained {retainedBefore} -> {UndoBudget.Retained}, steps {stepsBefore} -> {keeper.UndoCount}");
            keeper.Reset("");
        }
        finally { AppPreferences.Current.UndoBudgetKilochars = savedForShrink; }

        // --- Window-independent discard policy (51) ---
        check("Batch/discard skips unknown identities",
            DocumentBatch.Discard(new[] { Guid.NewGuid() }, _ => null, _ => { }) == 0,
            "unresolved identity must be skipped, not throw");
        check("Batch/pending skips unknown identities",
            DocumentBatch.Pending(new[] { Guid.NewGuid() }, _ => null).Count == 0,
            "unresolved identity must not appear as pending");

        using var clean = new DocumentSession();
        check("Batch/pending ignores clean documents",
            DocumentBatch.Pending(new[] { clean.RecoveryId }, _ => clean).Count == 0,
            "a clean document is not pending");
        int touched = DocumentBatch.Discard(new[] { clean.RecoveryId, clean.RecoveryId }, _ => clean, _ => { });
        check("Batch/discard ignores clean documents", touched == 0, "discarded=" + touched);
    }
}

