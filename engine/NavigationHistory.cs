using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Values only: history never keeps a document, HWND, or rendering resource alive.
internal sealed record NavigationPoint(Guid SessionId, string Path, ReadingLocation Location);

internal sealed class NavigationHistory
{
    private readonly List<NavigationPoint> _back = new(), _forward = new();
    private readonly int _capacity;
    public NavigationHistory(int capacity = 64) => _capacity = Math.Clamp(capacity, 1, 256);
    public bool CanBack => _back.Count != 0;
    public bool CanForward => _forward.Count != 0;
    public int BackCount => _back.Count;
    public int ForwardCount => _forward.Count;
    public IReadOnlyList<NavigationPoint> BackEntries => _back.ToArray();
    public IReadOnlyList<NavigationPoint> ForwardEntries => _forward.ToArray();
    public void Remove(bool back, int index) { var list = back ? _back : _forward; if (index >= 0 && index < list.Count) list.RemoveAt(index); }
    public void Clear() { _back.Clear(); _forward.Clear(); }
    public bool TraverseTo(bool back, int index, NavigationPoint current, Func<NavigationPoint, bool> navigate)
    {
        var source = back ? _back : _forward; var destination = back ? _forward : _back;
        if (index < 0 || index >= source.Count || !navigate(source[index])) return false;
        Push(destination, current);
        for (int i = source.Count - 1; i > index; i--) Push(destination, source[i]);
        source.RemoveRange(index, source.Count - index);
        return true;
    }

    private void Push(List<NavigationPoint> list, NavigationPoint point)
    {
        if (list.Count > 0 && list[^1] == point) return;
        list.Add(point);
        if (list.Count > _capacity) list.RemoveAt(0);
    }

    public void Record(NavigationPoint before, NavigationPoint after)
    {
        if (before == null || after == null || before == after) return;
        Push(_back, before);
        _forward.Clear();
    }

    // A failed or cancelled navigation must not consume history or invent a new entry.
    public bool Traverse(bool back, NavigationPoint current, Func<NavigationPoint, bool> navigate)
    {
        var source = back ? _back : _forward;
        if (current == null || source.Count == 0) return false;
        var destination = source[^1];
        if (!navigate(destination)) return false;
        source.RemoveAt(source.Count - 1);
        Push(back ? _forward : _back, current);
        return true;
    }

    public void Rename(Guid sessionId, string path)
    {
        foreach (var list in new[] { _back, _forward })
            for (int i = 0; i < list.Count; i++)
                if (list[i].SessionId == sessionId) list[i] = list[i] with { Path = path };
    }
}
