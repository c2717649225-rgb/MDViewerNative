using System;
using System.IO;
using System.Linq;
using MDViewerNative;

internal static class RecoveryTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mdv-recovery-" + Guid.NewGuid().ToString("N"));
        string oldConfig = Environment.GetEnvironmentVariable("MDV_CONFIG_DIR");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", directory);
        try
        {
            string drafts = Path.Combine(directory, "drafts");
            var store = new DraftStore(drafts);
            store.Remove(Guid.NewGuid());
            check("Recovery/save before first checkpoint requires no directory", store.ReadAll().Count == 0 && !Directory.Exists(drafts), "no spurious recovery error on immediate save");
            string path = Path.Combine(directory, "source.md");
            File.WriteAllText(path, "original\n");
            using var source = new DocumentSession();
            source.Load(path, DocumentFile.Read(path));
            source.MarkDirty();
            var snapshot = source.CaptureDraft("live editor 草稿\n");
            store.Write(snapshot);
            check("Recovery/captures live editor separately from saved snapshot", source.Text == "original\n" && store.ReadAll().Single() == snapshot, "round trip with Unicode");
            check("Recovery/checkpoint leaves source bytes untouched", File.ReadAllText(path) == "original\n", "no source save");
            string snapshotPath = Path.Combine(drafts, snapshot.Id.ToString("N") + ".json");
            bool failed = false;
            using (var locked = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Write(snapshot with { Text = "new checkpoint" }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            }
            check("Recovery/failed checkpoint retains last complete snapshot", failed && store.ReadAll().Single() == snapshot, "locked destination");
            check("Recovery/failed checkpoint cleans private temporary files", Directory.GetFiles(drafts, "*.tmp").Length == 0, "atomic state writer");

            File.WriteAllText(path, "external\n");
            using var restored = new DocumentSession();
            restored.RestoreDraft(new DraftStore(drafts).ReadAll().Single());
            check("Recovery/restored draft is dirty and keeps file identity", restored.Dirty && restored.Text == snapshot.Text && restored.Path == path, "restart through a new store");
            bool conflict = false;
            try { restored.Save(restored.Text); }
            catch (FileConflictException) { conflict = true; }
            check("Recovery/stale recovered version cannot overwrite external edit", conflict && File.ReadAllText(path) == "external\n", "original disk hash survives restart");
            restored.DiscardChanges();
            check("Recovery/discard restores original saved text", !restored.Dirty && restored.Text == "original\n", "external file remains untouched");
            store.Remove(restored.RecoveryId);
            store.Remove(restored.RecoveryId);
            check("Recovery/cleanup is repeatable", store.ReadAll().Count == 0, "save/discard cleanup");

            using var unnamed = new DocumentSession();
            var blank = unnamed.CaptureDraft("unsaved new document");
            store.Write(blank);
            store.Write(snapshot);
            using var tabs = new DocumentTabs();
            tabs.RestoreDraft(blank);
            tabs.RestoreDraft(snapshot);
            check("Recovery/unnamed and named drafts restore as separate tabs", tabs.Count == 2 && tabs[0].Path == "" && tabs[0].Dirty && tabs[1].Dirty, "initial blank is replaced");
            bool duplicate = false;
            try { tabs.RestoreDraft(snapshot with { Text = "different draft" }); }
            catch (InvalidOperationException) { duplicate = true; }
            check("Recovery/duplicate path cannot displace a live draft", duplicate && tabs[1].Text == snapshot.Text && tabs.Count == 2, "transaction preserves existing tab");

            string broken = Path.Combine(drafts, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(broken, "{truncated");
            check("Recovery/one corrupt record does not block other drafts", store.ReadAll().Count == 2, "valid records retained");
            check("Recovery/corrupt bytes retained for manual recovery", !File.Exists(broken) && Directory.GetFiles(drafts, "*.corrupt-*").Length == 1, "quarantine without deletion");
            string future = Path.Combine(drafts, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(future, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(snapshotPath), "\"Schema\":\\d+", "\"Schema\":999"));
            check("Recovery/newer schema preserved on older application", store.ReadAll().Count == 2 && File.Exists(future), "unsupported version retained");
            string copy = Path.Combine(directory, "recovered-copy.md");
            tabs.Current.Save(tabs.Current.Text, copy);
            store.Remove(tabs.Current.RecoveryId);
            check("Recovery/save as clears only the saved draft", !tabs.Current.Dirty && store.ReadAll().Single() == blank && File.ReadAllText(copy) == snapshot.Text, "other recovery record retained");

            string log = Path.Combine(directory, "app.log");
            File.WriteAllText(log, new string('x', 1024 * 1024 + 1));
            AppLog.Error("regression probe", new IOException("expected failure"));
            check("Diagnostics/log rotates and records operation and exception", File.Exists(log + ".1") && File.ReadAllText(log).Contains("regression probe: System.IO.IOException"), "isolated configuration directory");
            Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", path); // An existing file cannot be a directory.
            AppLog.Write("unwritable log");
            check("Diagnostics/log failure does not escape", File.ReadAllText(path) == "external\n", "no new application failure");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", oldConfig);
            Directory.Delete(directory, true);
        }
    }
}
