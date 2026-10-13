using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MDViewerNative;

// Bounded versioned state, separate from document text and recovery drafts.
internal sealed class ReadingMemory
{
    private readonly Dictionary<string, ReadingLocation> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    public string ActivePath { get; private set; } = "";
    public int Count => _positions.Count;
    // U10：起始页「继续阅读」取最近记下的一篇；卡片取各自的已读比例。
    public string Latest => _order.Last?.Value;
    public float? ProgressOf(string path) => path != null && _positions.TryGetValue(path, out var p) ? p.Progress : null;
    private const int Limit = 32;
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static string Normalize(string path)
    {
        if (path.Length == 0 || path.Length > 4096 || path.Any(char.IsControl)) return null;
        string full = Path.GetFullPath(path);
        return Encoding.UTF8.GetByteCount(full) <= 4096 ? full : null;
    }

    public void Remember(DocumentSession session)
    {
        if (session.IsDisposed || session.Dirty || session.Path.Length == 0 || session.Path.Length > 4096 || Encoding.UTF8.GetByteCount(session.Path) > 4096) return;
        var location = session.Doc.CaptureReading(session.ScrollOffset);
        if (location == null) return;
        Store(session.Path, location);
    }

    private void Store(string path, ReadingLocation location)
    {
        var prior = _order.First;
        while (prior != null && !string.Equals(prior.Value, path, StringComparison.OrdinalIgnoreCase)) prior = prior.Next;
        if (prior != null) _order.Remove(prior);
        _order.AddLast(path);
        _positions[path] = location;
        while (_order.Count > Limit) { _positions.Remove(_order.First.Value); _order.RemoveFirst(); }
    }

    public bool Apply(DocumentSession session)
    {
        if (session.IsDisposed || session.Dirty || !_positions.TryGetValue(session.Path, out var location)) return false;
        session.ScrollOffset = session.Doc.RestoreReading(location);
        return true;
    }

    public void Load(string path)
    {
        _positions.Clear();
        _order.Clear();
        ActivePath = "";
        if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0] != "reading-v1") return;
        foreach (string line in lines.Skip(1))
        {
            try
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && parts[0] == "active") { ActivePath = Normalize(Decode(parts[1])) ?? ""; continue; }
                if (parts.Length != 6 || parts[0] != "position") continue;
                string file = Normalize(Decode(parts[1]));
                if (file == null || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int block) || block > 1000000
                    || parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit)
                    || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float fraction) || !float.IsFinite(fraction) || fraction < 0 || fraction > 1
                    || !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float progress) || !float.IsFinite(progress) || progress < 0 || progress > 1) continue;
                Store(file, new(block, parts[3], fraction, progress));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException) { }
        }
    }

    public void Save(string path, IEnumerable<DocumentSession> sessions, string activePath)
    {
        foreach (var session in sessions) Remember(session);
        var active = sessions.FirstOrDefault(s => string.Equals(s.Path, activePath, StringComparison.OrdinalIgnoreCase));
        if (active != null) Remember(active);
        ActivePath = Normalize(activePath ?? "") ?? "";
        var text = new StringBuilder("reading-v1\nactive\t").Append(Encode(ActivePath)).Append('\n');
        foreach (string file in _order)
        {
            var p = _positions[file];
            text.Append("position\t").Append(Encode(file)).Append('\t').Append(p.Block.ToString(CultureInfo.InvariantCulture))
                .Append('\t').Append(p.Fingerprint).Append('\t').Append(p.Fraction.ToString("R", CultureInfo.InvariantCulture))
                .Append('\t').Append(p.Progress.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        AtomicFile.WriteState(path, Encoding.UTF8.GetBytes(text.ToString()));
    }
}
