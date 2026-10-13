using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U9c：墨未干（规格 §8.1，演示稿 .tab .dot）。纯层：每个标签的墨滴阶段与动画姿态；绘制在 ToolbarView。
// 第一次产生改动：墨滴 0 → 1.25 → 1（450 ms，每段缓出）。保存：前 35% 失去光泽与淡晕、收到 0.9，
// 再收到 0.55 并淡出（共 1200 ms，每段缓入），之后恢复为普通 ×。
internal readonly record struct InkPose(float Scale, float Opacity, float Gloss, float Halo, float Saturate, float Brightness);

internal static class InkDrop
{
    public const int FreshMs = 450, DryMs = 1200;
    public const float Radius = 4f, HaloWidth = 1.5f, HaloAlpha = 0.16f;
    public static readonly InkPose Wet = new(1f, 1f, 1f, 1f, 1f, 1f);

    // CSS ease-in：cubic-bezier(.42,0,1,1)
    static float EaseIn(float t) => DeskAnimations.CubicBezier(0.42f, 0f, 1f, 1f, Math.Clamp(t, 0f, 1f));
    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static InkPose Fresh(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float s = t < 0.6f ? Lerp(0f, 1.25f, DeskAnimations.Ease(t / 0.6f)) : Lerp(1.25f, 1f, DeskAnimations.Ease((t - 0.6f) / 0.4f));
        return Wet with { Scale = s };
    }

    public static InkPose Dry(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.35f)
        {
            float k = EaseIn(t / 0.35f);
            return new(Lerp(1f, 0.9f, k), 1f, 1f - k, 1f - k, Lerp(1f, 0.75f, k), Lerp(1f, 0.92f, k));
        }
        float q = EaseIn((t - 0.35f) / 0.65f);
        return new(Lerp(0.9f, 0.55f, q), 1f - q, 0f, 0f, Lerp(0.75f, 0.6f, q), Lerp(0.92f, 0.9f, q));
    }

    // CSS filter saturate() + brightness()，作用在 0xRRGGBB 上。
    public static uint Filter(uint rgb, float saturate, float brightness)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f, s = saturate;
        float nr = (0.213f + 0.787f * s) * r + (0.715f - 0.715f * s) * g + (0.072f - 0.072f * s) * b;
        float ng = (0.213f - 0.213f * s) * r + (0.715f + 0.285f * s) * g + (0.072f - 0.072f * s) * b;
        float nb = (0.213f - 0.213f * s) * r + (0.715f - 0.715f * s) * g + (0.072f + 0.928f * s) * b;
        static uint C(float v) => (uint)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return (C(nr * brightness) << 16) | (C(ng * brightness) << 8) | C(nb * brightness);
    }
}

// 各标签墨滴的时间线（按文档会话 Id）。宿主在“干净→有改动”与“保存成功”时各记一笔；视图每帧取姿态。
internal sealed class InkDrops
{
    readonly Dictionary<Guid, (bool Drying, long Since)> _marks = new();

    public void Fresh(Guid id, long now) => _marks[id] = (false, now);
    public void Dry(Guid id, long now) => _marks[id] = (true, now);
    public void Forget(Guid id) => _marks.Remove(id);

    // dirty：标签当前是否有改动。有改动：新墨滴动画或静止的湿墨滴；没有改动：正在干的墨滴，或 null（画 ×）。
    public InkPose? Pose(Guid id, bool dirty, long now)
    {
        bool known = _marks.TryGetValue(id, out var m);
        if (dirty)
        {
            if (known && !m.Drying && now - m.Since < InkDrop.FreshMs) return InkDrop.Fresh((now - m.Since) / (float)InkDrop.FreshMs);
            return InkDrop.Wet;
        }
        if (known && m.Drying && now - m.Since < InkDrop.DryMs) return InkDrop.Dry((now - m.Since) / (float)InkDrop.DryMs);
        return null;
    }

    // 还需要推进多少毫秒（宿主据此保持帧计时器）。
    public int RemainingMs(long now)
    {
        long left = 0;
        foreach (var m in _marks.Values) left = Math.Max(left, m.Since + (m.Drying ? InkDrop.DryMs : InkDrop.FreshMs) - now);
        return (int)Math.Max(0, left);
    }
}
