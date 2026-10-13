using System;

namespace MDViewerNative;

// Cooperative cancellation for bounded, UI-thread document work (parse and layout).
//
// This is deliberately not a CancellationToken: parse and layout run synchronously on the
// single UI thread, so there is no second thread to observe a token. The host installs a
// probe that is polled at block boundaries; the probe decides whether the user asked to stop.
//
// Contract:
//   - No scope installed => Poll() is a couple of predictable comparisons and never throws.
//     Documents that are not large keep their previous behaviour exactly.
//   - A scope only begins observing after GraceChars of work, so ordinary documents never
//     reach the probe at all and cannot be cancelled by a stray key state.
//   - Cancellation surfaces as DocumentCancelledException, which callers treat as "the old
//     document is still authoritative", never as a corrupt half-built document.
internal static class DocumentWork
{
    // Default work performed before a scope starts polling. Parsing a document under this
    // size is fast enough that offering cancellation would be noise, and polling would be
    // overhead. Users can change it in 行为设置 → 编辑行为; see GraceChars.
    internal const int DefaultGraceChars = 400_000;

    // Effective grace window, taken from preferences so the user can trade responsiveness
    // against polling coverage. 0 makes every document cancellable from the first block.
    internal static int GraceChars => Math.Clamp(AppPreferences.Current.CancelGraceKilochars, 0, 20_000) * 1_000;

    // Poll cadence in units of work. Keeps the probe off the hot path while staying
    // responsive: the probe is consulted at most once per this many characters/blocks.
    internal const int PollInterval = 8_192;

    [ThreadStatic] private static Scope _current;

    internal sealed class Scope : IDisposable
    {
        private readonly Scope _previous;
        private readonly Func<bool> _probe;
        private long _work;
        private long _nextPoll;
        private bool _disposed;

        // True once the probe reported a stop request. Sticky: a document that was abandoned
        // must not silently resume because the key was released mid-parse.
        internal bool Cancelled { get; private set; }

        internal Scope(Func<bool> probe)
        {
            _probe = probe;
            _nextPoll = GraceChars;
            _previous = _current;
            _current = this;
        }

        internal void Advance(int units)
        {
            if (units > 0) _work += units;
            if (_work < _nextPoll) return;
            _nextPoll = _work + PollInterval;
            if (Cancelled) return;
            // A throwing probe must not corrupt the parse; treat it as "no cancellation".
            try { Cancelled = _probe != null && _probe(); }
            catch { Cancelled = false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current = _previous;
        }
    }

    // Installs a probe for the duration of the returned scope. Nested scopes restore the outer
    // one on dispose, so an export running inside a load cannot leave a stale probe behind.
    internal static Scope Begin(Func<bool> probe) => new Scope(probe);

    internal static bool Active => _current != null;

    private sealed class Suspension : IDisposable
    {
        private readonly Scope _saved;
        private bool _disposed;
        internal Suspension(Scope saved) { _saved = saved; _current = null; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current = _saved;
        }
    }

    // Detaches the active probe so rollback/cleanup work cannot itself be cancelled.
    // Restoring a previous document must always run to completion.
    internal static IDisposable Suspend() => new Suspension(_current);

    // Records work and throws if the user asked to stop. Call at block boundaries only.
    internal static void Poll(int units = 1)
    {
        Scope scope = _current;
        if (scope == null) return;
        scope.Advance(units);
        if (scope.Cancelled) throw new DocumentCancelledException();
    }
}

// Signals that bounded document work stopped at a user request. Callers keep the previous
// document; this is a control-flow signal, not an I/O or parse failure.
internal sealed class DocumentCancelledException : OperationCanceledException
{
    internal DocumentCancelledException() : base("文档处理已取消。") { }
}
