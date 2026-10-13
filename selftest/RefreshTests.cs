using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MDViewerNative;

internal static class RefreshTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("Refresh/" + name, ok, "transactional disk refresh and stale-result safety");
        bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
        string dir = Path.GetFullPath("../work/refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.md");
            using var tabs = new DocumentTabs();
            C("unnamed cannot refresh", !tabs.TryRefresh(() => throw new Exception("should not prompt")));
            File.WriteAllText(path, "# old\n");
            tabs.TryOpen(path, false, () => true);
            var original = tabs.Current;
            original.ScrollOffset = 240;
            C("unchanged content", DiskChangeMonitor.Observe(path, original.DiskVersion) == DiskState.Current);
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "# new\n");
            File.SetLastWriteTimeUtc(path, stamp);
            C("same size and timestamp still detected", DiskChangeMonitor.Observe(path, original.DiskVersion) == DiskState.Changed);
            original.Doc.SetToc(true);
            C("clean refresh needs no discard prompt", tabs.TryRefresh(() => throw new Exception("should not prompt")));
            C("refresh replaces and disposes only old session", original.IsDisposed && tabs.Count == 1 && tabs.Current.Text == "# new\n");
            C("reading offset carried forward", tabs.Current.ScrollOffset == 240);
            C("table of contents visibility carried forward", tabs.Current.Doc.ShowToc);
            C("refresh never writes disk or backup", File.ReadAllText(path) == "# new\n" && !File.Exists(path + ".bak"));
            var live = tabs.Current;
            live.MarkDirty(); // The EDIT may own text not yet copied to the session.
            C("live editor cancel keeps exact session", !tabs.TryRefresh(() => false) && ReferenceEquals(live, tabs.Current) && live.Dirty && !live.IsDisposed);
            live.ReplaceText("# unsaved preview draft\n");
            Guid recovery = live.RecoveryId;
            C("preview draft cancel preserves recovery identity", !tabs.TryRefresh(() => false) && live.Text.Contains("unsaved") && live.RecoveryId == recovery);
            C("change during confirmation rejects candidate", Throws<FileConflictException>(() => tabs.TryRefresh(() => { File.WriteAllText(path, "# later\n"); return true; })));
            C("race retains draft and view ownership", ReferenceEquals(live, tabs.Current) && live.Dirty && live.Text.Contains("unsaved") && !live.IsDisposed);
            File.Delete(path);
            C("missing file detected", DiskChangeMonitor.Observe(path, live.DiskVersion) == DiskState.Missing);
            C("missing refresh fails before discard prompt", Throws<FileNotFoundException>(() => tabs.TryRefresh(() => throw new Exception("should not prompt"))));
            C("failed read leaves draft intact", ReferenceEquals(live, tabs.Current) && live.Dirty && live.RecoveryId == recovery);
            File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
            C("invalid encoding preserves original", Throws<DecoderFallbackException>(() => tabs.TryRefresh(() => true)) && ReferenceEquals(live, tabs.Current));
            File.WriteAllText(path, "# restored\r\n", new UTF8Encoding(true));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                C("locked probe reports unavailable", DiskChangeMonitor.Observe(path, live.DiskVersion) == DiskState.Unavailable);
                C("locked refresh retains draft", Throws<IOException>(() => tabs.TryRefresh(() => true)) && ReferenceEquals(live, tabs.Current) && live.Dirty);
            }
            C("confirmed discard commits latest readable file", tabs.TryRefresh(() => true) && live.IsDisposed && !tabs.Current.Dirty);
            C("refresh retains disk format", tabs.Current.Bom && tabs.Current.LineEnding == "\r\n" && tabs.Current.Text == "# restored\r\n");
            string other = Path.Combine(dir, "other.md");
            File.WriteAllText(other, "other");
            var first = tabs.Current;
            tabs.TryOpen(other, true, () => true);
            C("refresh leaves inactive tab alone", tabs.TryRefresh(() => true) && tabs.Count == 2 && tabs.ActiveIndex == 1 && ReferenceEquals(first, tabs[0]) && !first.IsDisposed);

            var monitor = new DiskChangeMonitor();
            using var unnamed = new DocumentSession();
            C("no background read for unnamed", !monitor.Start(unnamed));
            var current = tabs.Current;
            File.WriteAllText(other, "changed");
            C("start exactly one background check", monitor.Start(current) && !monitor.Start(first));
            C("background completion is bounded in test", SpinWait.SpinUntil(() => monitor.Ready, 5000));
            C("worker never mutates session", current.DiskState == DiskState.Current && current.Text == "other");
            C("UI completion reports external change", monitor.TryComplete(out var observed) && ReferenceEquals(observed, current) && current.DiskState == DiskState.Changed && !monitor.Pending);
            C("completion consumed once", !monitor.TryComplete(out _));
            monitor.Start(current);
            C("stale-save check ready", SpinWait.SpinUntil(() => monitor.Ready, 5000));
            current.Save("other", confirmedVersion: AtomicFile.Observe(other));
            monitor.TryComplete(out _);
            C("same-hash save invalidates older changed result", current.DiskState == DiskState.Current && current.Text == "other");
            File.Delete(other);
            monitor.Start(current);
            C("disposed-check ready", SpinWait.SpinUntil(() => monitor.Ready, 5000));
            current.Dispose();
            C("disposed tab ignores observation", monitor.TryComplete(out _) && current.DiskState == DiskState.Current);
            var refresh = Commands.All.Single(c => c.Id == 2097);
            C("unnamed refresh menu disabled", !refresh.IsEnabled(new(false, false, false, false, false, 16, HasFile: false)));
            C("named refresh enabled in either mode", refresh.IsEnabled(new(false, false, false, false, false, 16)) && refresh.IsEnabled(new(true, true, false, false, false, 16)));
            C("no added accelerator or More item", refresh.Accel.Length == 0 && (refresh.Face & CmdFace.More) == 0 && Commands.InvariantViolation() == null);
        }
        finally { Directory.Delete(dir, true); }
    }
}
