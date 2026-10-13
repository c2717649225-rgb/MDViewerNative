using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MDViewerNative;

internal static unsafe class MaturityTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        var mailbox = new InstanceHandoff();
        string path = Path.Combine(Path.GetTempPath(), "handoff 中文 space.md");
        fixed (char* text = path)
        {
            var data = new InstanceHandoff.Payload { Kind = InstanceHandoff.Protocol, Bytes = (path.Length + 1) * 2, Text = (nint)text };
            check("Handoff/failed wake is rejected", !mailbox.Accept((nint)(&data), () => false), "no acknowledged stranded request");
            for (int i = 0; i < 24; i++) mailbox.Accept((nint)(&data), () => true);
            int received = 0;
            bool intact = true;
            mailbox.Drain(p => { received++; intact &= p == path; mailbox.Drain(_ => throw new Exception("nested drain")); });
            check("Handoff/burst retained with Unicode", received == 24 && intact, "independent copied requests; nested drain guarded");
            data.Bytes--;
            check("Handoff/odd byte count rejected", !mailbox.Accept((nint)(&data), () => true), "bounded UTF16 payload");
            data.Bytes++;
            data.Kind = 0;
            check("Handoff/unknown protocol rejected", !mailbox.Accept((nint)(&data), () => true), "versioned message");
            data.Kind = InstanceHandoff.Protocol;
            bool accepted = true;
            for (int i = 0; i < 256; i++) accepted &= mailbox.Accept((nint)(&data), () => true);
            check("Handoff/queue bounded with explicit rejection", accepted && !mailbox.Accept((nint)(&data), () => true), "no unbounded queue growth");
            mailbox.Drain(_ => { });
        }
        check("Handoff/null payload rejected", !mailbox.Accept(0, () => true), "no unmanaged dereference");

        string directory = Path.Combine(Path.GetTempPath(), "mdv-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "source.md");
            File.WriteAllText(file, "original");
            using var session = new DocumentSession();
            session.Load(file, DocumentFile.Read(file));
            session.Save("saved");
            session.MarkDirty();
            var previousBackup = File.ReadAllBytes(file + ".bak");
            bool failed = false;
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { session.Save("live draft"); }
                catch (IOException) { failed = true; }
            }
            check("Fault/locked document preserves disk and dirty state", failed && session.Dirty && File.ReadAllText(file) == "saved", "live editor draft is supplied separately");
            check("Fault/failed save does not rotate backup", File.ReadAllBytes(file + ".bak").SequenceEqual(previousBackup), "backup bytes unchanged");
            check("Fault/failed save leaves no temporary files", Directory.GetFiles(directory, ".mdv-*.tmp").Length == 0, "failure after temporary write");
            session.Save("live draft");
            check("Fault/retry succeeds after lock released", !session.Dirty && File.ReadAllText(file) == "live draft" && File.ReadAllText(file + ".bak") == "saved", "failed save did not advance version");

            var store = new DraftStore(Path.Combine(directory, "drafts"));
            session.MarkDirty();
            var snapshot = session.CaptureDraft("checkpoint one");
            store.Write(snapshot);
            var next = snapshot with { Text = "checkpoint two" };
            string draft = Path.Combine(directory, "drafts", snapshot.Id.ToString("N") + ".json");
            failed = false;
            using (var locked = new FileStream(draft, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Write(next); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            }
            store.Write(next);
            check("Fault/checkpoint retry is not incorrectly deduplicated", failed && store.ReadAll().Single() == next, "cache advances only after successful write");
        }
        finally { Directory.Delete(directory, true); }
    }
}
