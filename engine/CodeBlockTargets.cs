using System;
using System.Collections.Generic;

namespace MDViewerNative;

// Text == null marks a scroll-only target: a wide table registers its box so the wheel and
// arrow keys can reach it, but a table is not code, so it must stay out of "copy code
// block" and out of the offset lookup that feeds it (30).
internal readonly record struct CodeBlockTarget(float Left, float Top, float Right, float Bottom,
    int Start, int Length, string Text, int BlockIndex = -1);

// Rebuilt by painting; coordinates are document DIPs, while offsets use the common
// selection/search text projection. Copy returns the parsed literal code, not its
// tab-expanded or highlighted visual representation.
internal sealed class CodeBlockTargets
{
    private readonly List<CodeBlockTarget> _items = new();
    public void Clear() => _items.Clear();
    public void Add(CodeBlockTarget item)
    {
        if ((item.Text == null && item.BlockIndex == -1) || !float.IsFinite(item.Left) || !float.IsFinite(item.Top)
            || !float.IsFinite(item.Right) || !float.IsFinite(item.Bottom)
            || item.Right < item.Left || item.Bottom < item.Top || item.Start < 0 || item.Length < 0) return;
        _items.Add(item);
    }
    public string AtPoint(float x, float y)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.Text != null && x >= item.Left && x <= item.Right && y >= item.Top && y <= item.Bottom) return item.Text;
        }
        return null;
    }
    // Same hit test, but yields the block index so sideways scrolling can reach the
    // block's cached layout state (30). -1 when the point is not over a code block.
    public int BlockAtPoint(float x, float y)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (x >= item.Left && x <= item.Right && y >= item.Top && y <= item.Bottom) return item.BlockIndex;
        }
        return -1;
    }
    // The code block nearest the middle of the visible range, for keyboard scrolling where
    // there is no pointer to aim with. -1 when no code block is on screen (30).
    public int BlockNearestCenter(float viewTop, float viewBottom)
    {
        float centre = (viewTop + viewBottom) / 2f;
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var item in _items)
        {
            if (item.BlockIndex == -1 || item.Bottom < viewTop || item.Top > viewBottom) continue;
            float distance = MathF.Abs((item.Top + item.Bottom) / 2f - centre);
            if (distance < bestDistance) { bestDistance = distance; best = item.BlockIndex; }
        }
        return best;
    }
    public string AtOffset(int offset)
    {
        foreach (var item in _items)
            if (item.Text != null && offset >= item.Start && (long)offset <= (long)item.Start + item.Length) return item.Text;
        return null;
    }
}
