using System;

namespace MDViewerNative;

unsafe partial class DocView
{
    internal int? SourceOffsetAtY(float y)
    {
        if (!float.IsFinite(y) || _blocks.Count == 0) return null;
        int block = Math.Clamp(BlockAtY(y), 0, _blocks.Count - 1);
        if (_blocks[block].Source is { } direct) return direct.Start;
        for (int distance = 1; distance < _blocks.Count; distance++)
        {
            if (block - distance >= 0 && _blocks[block - distance].Source is { } before) return before.Start;
            if (block + distance < _blocks.Count && _blocks[block + distance].Source is { } after) return after.Start;
        }
        return null;
    }

    internal float? YAtSourceOffset(int offset)
    {
        int nearest = -1;
        long distance = long.MaxValue;
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Source is not { } source) continue;
            // Container children deliberately resolve to the first outer block.
            if (offset >= source.Start && offset < source.End) return _ys[i];
            long delta = offset < source.Start ? (long)source.Start - offset : (long)offset - source.End;
            if (delta < distance) { distance = delta; nearest = i; }
        }
        return nearest < 0 ? null : _ys[nearest];
    }
}
