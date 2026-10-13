using System;

namespace MDViewerNative;

// U7：查找当前一处的朱笔圈（演示稿 drawCircle）。纯几何：由命中的第一段矩形生成一条略不闭合的手绘椭圆折线，
// 起笔略早、收笔越过起点，x 半径随笔逐渐放大、y 半径逐渐收小（像手腕带出去的一笔）。
// 种子由命中位置决定：同一处每次画出来一样，换一处形状就不同。绘制与描绘进度由 DocView 负责。
internal static class InkCircle
{
    public const int Segments = 46;
    public const float Pad = 12f, Stroke = 1.8f, Opacity = 0.9f;
    public const int DelayMs = 360, DrawMs = 500;   // 演示：跳转后 360 ms 起笔，dashoffset 描绘 500 ms

    // 圈所在的框（相对命中矩形外扩：左右各 12，上 7.8、下 7.8）。
    public static (float X, float Y, float W, float H) Frame(float left, float top, float width, float height) =>
        (left - Pad, top - Pad * 0.65f, width + Pad * 2f, height + Pad * 1.3f);

    public static float Seed(int hitStart) => (uint)(hitStart * 7919 + 17) % 1000u / 100f;

    // Segments + 1 个点，坐标相对框左上角。
    public static (float X, float Y)[] Points(float w, float h, float seed)
    {
        var p = new (float X, float Y)[Segments + 1];
        float cx = w / 2f, cy = h / 2f, rx = w / 2f - 3f, ry = h / 2f - 3f;
        for (int k = 0; k <= Segments; k++)
        {
            float t = -0.5f + k / (float)Segments * (MathF.PI * 2f + 0.9f);
            float j = 1f + MathF.Sin(t * 3f + seed) * 0.035f + MathF.Sin(t * 7f + seed * 2f) * 0.02f;
            float g = 1f + k / (float)Segments * 0.07f;
            p[k] = (cx + MathF.Cos(t) * rx * j * g, cy + MathF.Sin(t) * ry * j * (2f - g));
        }
        return p;
    }

    // 描到进度 t（0..1，按弧长）时的最后一个整段下标与该段已画比例；与 SVG dashoffset 同口径。
    public static (int Whole, float Part) Progress((float X, float Y)[] p, float t)
    {
        float total = 0f;
        for (int k = 1; k < p.Length; k++) total += Len(p[k - 1], p[k]);
        float target = Math.Clamp(t, 0f, 1f) * total, run = 0f;
        for (int k = 1; k < p.Length; k++)
        {
            float len = Len(p[k - 1], p[k]);
            if (run + len >= target) return (k - 1, len > 0f ? (target - run) / len : 1f);
            run += len;
        }
        return (p.Length - 1, 0f);
    }

    // 从 DelayMs + DrawMs 的整体时钟换算描绘进度：起笔前为 0，之后按演示 --ease 缓出。
    public static float DrawProgress(float clock)
    {
        float total = DelayMs + DrawMs, ms = Math.Clamp(clock, 0f, 1f) * total;
        return ms <= DelayMs ? 0f : DeskAnimations.Ease((ms - DelayMs) / DrawMs);
    }

    static float Len((float X, float Y) a, (float X, float Y) b) => MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
