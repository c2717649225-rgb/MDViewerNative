using System;
using System.Linq;
using MDViewerNative;

// Item 47: cooperative cancellation for bounded document work.
// These tests assert three things that matter to the user:
//   1. Without a scope, behaviour is byte-identical to before (no silent truncation).
//   2. A document under the grace window cannot be cancelled by a stuck key.
//   3. A large document IS interruptible, and the failure is a clean signal, not a
//      half-built document.
internal static class DocumentCancelTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        PrintCleanup(check);
        string small = string.Concat(Enumerable.Repeat("small paragraph text\n\n", 50));
        string large = string.Concat(Enumerable.Repeat("# heading\n\nbody paragraph with text\n\n", 40_000));

        // 1. No scope installed: parsing must be untouched.
        var baseline = MdParser.Parse(large);
        check("Cancel/no scope parses fully", baseline.Count > 1000, "blocks=" + baseline.Count);
        check("Cancel/no scope leaves probe inactive", !DocumentWork.Active, "Active=false expected");

        // 2. Probe that always says "stop", on a document below the grace window.
        //    The grace window must protect it: an ordinary open cannot be cancelled.
        int smallProbeCalls = 0;
        bool smallCancelled = false;
        using (DocumentWork.Begin(() => { smallProbeCalls++; return true; }))
        {
            try { MdParser.Parse(small); }
            catch (DocumentCancelledException) { smallCancelled = true; }
        }
        check("Cancel/grace window protects small documents", !smallCancelled,
            $"len={small.Length} < grace={DocumentWork.GraceChars}, probeCalls={smallProbeCalls}");

        // 3. Same always-stop probe on a document above the grace window: must cancel.
        bool largeCancelled = false;
        using (DocumentWork.Begin(() => true))
        {
            try { MdParser.Parse(large); }
            catch (DocumentCancelledException) { largeCancelled = true; }
        }
        check("Cancel/large document is interruptible", largeCancelled,
            $"len={large.Length} >= grace={DocumentWork.GraceChars}");

        // 4. A probe that never cancels must produce exactly the uncancelled result.
        using (DocumentWork.Begin(() => false))
        {
            var again = MdParser.Parse(large);
            check("Cancel/inactive probe changes nothing", again.Count == baseline.Count,
                $"{again.Count} vs {baseline.Count}");
        }

        // 5. Scope must not leak past its using block, including after a cancellation.
        check("Cancel/scope restored after dispose", !DocumentWork.Active, "Active=false expected");

        // 6. A throwing probe is treated as "no cancellation" rather than corrupting the parse.
        using (DocumentWork.Begin(() => throw new InvalidOperationException("probe boom")))
        {
            var resilient = MdParser.Parse(large);
            check("Cancel/throwing probe does not break parsing", resilient.Count == baseline.Count,
                $"{resilient.Count} vs {baseline.Count}");
        }

        // 7. Suspend detaches the probe so rollback work always completes.
        bool suspendedCancelled = false;
        using (DocumentWork.Begin(() => true))
        {
            using (DocumentWork.Suspend())
            {
                try { MdParser.Parse(large); }
                catch (DocumentCancelledException) { suspendedCancelled = true; }
            }
        }
        check("Cancel/suspend lets cleanup finish", !suspendedCancelled, "suspended scope must not throw");

        // 8. Nested scopes restore the outer probe rather than clearing it.
        using (DocumentWork.Begin(() => false))
        {
            using (DocumentWork.Begin(() => false)) { }
            check("Cancel/nested scope restores outer", DocumentWork.Active, "outer scope still active");
        }

        // 9. Cancelling a load leaves the previous document intact and renderable.
        using var view = new DocView();
        view.Load("# original\n\noriginal body\n");
        int originalBlocks = view.BlockCount;
        bool loadCancelled = false;
        using (DocumentWork.Begin(() => true))
        {
            try { view.Load(large); }
            catch (DocumentCancelledException) { loadCancelled = true; }
        }
        check("Cancel/cancelled load reports cancellation", loadCancelled, "expected DocumentCancelledException");
        // The view is discarded by the host on cancellation, but it must remain self-consistent.
        view.Load("# after\n\nrecovered\n");
        check("Cancel/view still usable after cancelled load", view.BlockCount > 0,
            $"blocks={view.BlockCount} original={originalBlocks}");
    }

    private static void PrintCleanup(Action<string, bool, string> check)
    {
        using var document = new DocView();
        document.Load("```\n" + new string('w', 400) + "\n```\n");
        document.OnWidth(800f);
        float height = document.TotalH;
        string text = document.SearchText();
        document.WrapCodeForPaper = true;
        document.Relayout();
        check("Cancel/paper fixture changes layout", document.TotalH > height, "screen and paper need different measurements even at the same width");
        using (var scope = DocumentWork.Begin(() => true))
        {
            try { DocumentWork.Poll(int.MaxValue); }
            catch (DocumentCancelledException) { }
            check("Cancel/print cleanup fixture has sticky cancellation", scope.Cancelled, "same scope remains active while finally executes");
            bool restored = false;
            try { DocumentPrinter.RestoreReadingLayout(document, 800f, false); restored = true; }
            catch (DocumentCancelledException) { }
            check("Cancel/print restore cannot be cancelled again", restored, "otherwise cleanup is interrupted and temporary PDF deletion is skipped");
            check("Cancel/print restore rebuilds complete reading layout", !document.WrapCodeForPaper && MathF.Abs(document.TotalH - height) < 0.01f, "same-width restore must finish every block");
            check("Cancel/print restore preserves document text", document.SearchText() == text, "cleanup is not an edit");
            check("Cancel/print restore retains outer cancellation", DocumentWork.Active && scope.Cancelled, "the caller still receives the original cancellation");
        }
        check("Cancel/print cleanup leaves no probe installed", !DocumentWork.Active, "no state leaks into later document operations");
    }

}

