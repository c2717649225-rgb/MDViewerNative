using System;
using System.Security.Cryptography;
using System.Text;
using static MDViewerNative.BlockText;

namespace MDViewerNative;

unsafe partial class DocView
{
    private string ReadingFingerprint(int index) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(_blocks[index].GetType().Name + "\n" + BlockTextOf(_blocks[index]))));

    // 块占据的纵向跨度:到下一块顶部(最后一块到文末),含块后的空隙。块内比例按跨度取,
    // 空隙与底部留白里的偏移才能原样取回,而不是被钳到块的底边。
    private float ReadingSpan(int block) => MathF.Max(1, (block + 1 < _blocks.Count ? _ys[block + 1] : _totalH) - _ys[block]);

    public ReadingLocation CaptureReading(float offset)
    {
        if (_blocks.Count == 0 || !float.IsFinite(offset)) return null;
        int block = Math.Clamp(BlockAtY(MathF.Max(0, offset)), 0, _blocks.Count - 1);
        while (block > 0 && _ys[block] > offset) block--;   // 命中测试把块间空隙算给下一块;阅读位置归上一块的跨度
        float fraction = Math.Clamp((offset - _ys[block]) / ReadingSpan(block), 0, 1);
        return new(block, ReadingFingerprint(block), fraction, Math.Clamp(offset / MathF.Max(1, TotalH), 0, 1));
    }

    // 重排前后保持阅读位置(S80:改窗口宽、改字号、开关常驻目录)。
    public float KeepReading(float offset, Action relayout)
    {
        var at = offset > 0f ? CaptureReading(offset) : null;
        float colX = _colX, colW = _colW, total = _totalH;
        relayout();
        // 布局没变(只改了高度,或宽度变化没带来重排)时原样保留:块间空隙与底部留白里的
        // 偏移按“块 + 块内比例”取回会被钳到上一块底边。
        if (at == null || (_colX == colX && _colW == colW && _totalH == total)) return offset;
        return RestoreReading(at);
    }

    public float RestoreReading(ReadingLocation location)
    {
        if (location == null || _blocks.Count == 0 || location.Progress == 0) return 0;
        int best = -1;
        if (location.Block >= 0 && location.Block < _blocks.Count && ReadingFingerprint(location.Block) == location.Fingerprint) best = location.Block;
        else
        {
            // Match unchanged content after insertions; duplicate content uses the closest old index.
            for (int i = 0; i < _blocks.Count; i++)
                if (ReadingFingerprint(i) == location.Fingerprint && (best < 0 || Math.Abs((long)i - location.Block) < Math.Abs((long)best - location.Block))) best = i;
        }
        float result = best >= 0 ? _ys[best] + ReadingSpan(best) * location.Fraction : TotalH * location.Progress;
        return float.IsFinite(result) ? Math.Clamp(result, 0, TotalH) : 0;
    }
}
