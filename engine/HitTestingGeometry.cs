namespace MDViewerNative;

using System.Collections.Generic;

internal enum PointerCursor
{
    None,
    Arrow,
    IBeam,
    Hand
}

// Pure geometry helpers shared by pointer selection and reading/source tools.
// The owner remains responsible for block state and layout-specific hit tests.
internal static class HitTestingGeometry
{
    internal static int BlockAtY(float[] ys, float[] heights, int count, float y)
    {
        if (count <= 0) return -1;

        int lo = 0, hi = count - 1, candidate = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ys[mid] <= y)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (candidate < 0) return 0;
        if (y <= ys[candidate] + heights[candidate]) return candidate;
        if (candidate + 1 < count)
        {
            float distanceAfter = y - (ys[candidate] + heights[candidate]);
            return ys[candidate + 1] - y >= distanceAfter ? candidate : candidate + 1;
        }
        return candidate;
    }

    internal static int FindEmptyHit(IReadOnlyList<(float x, float y, float w, float h, string path)> hits, float x, float y)
    {
        if (hits == null) return -1;
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            if (x >= h.x - 4f && x <= h.x + h.w + 4f && y >= h.y - 2f && y <= h.y + h.h + 2f)
                return i;
        }
        return -1;
    }

    internal static PointerCursor DetermineCursor(
        bool isClientArea,
        bool inBounds,
        bool overBody,
        bool editing,
        bool isImage,
        bool hasRecentFile,
        bool hasLink,
        bool hasBlock)
    {
        if (!isClientArea || !inBounds) return PointerCursor.None;
        if (!overBody) return PointerCursor.Arrow;
        if (editing) return PointerCursor.IBeam;
        if (isImage || hasRecentFile || hasLink) return PointerCursor.Hand;
        return hasBlock ? PointerCursor.IBeam : PointerCursor.Arrow;
    }
}
