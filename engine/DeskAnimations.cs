using System;

namespace MDViewerNative;

internal enum DeskAnim { BackFlag, Quiet, Ink, Juan, WetInk, Spread, Count }   // Quiet：U6c 读时退后；Ink：U7 查找朱笔圈；Juan：U8 目录卷目；WetInk：U9c 墨未干

// U5c：desk 界面的统一动画时钟（计划 §3.6）。只记每个动画的起点与时长，按当前时刻给进度；
// 帧定时器与失效区域由宿主负责（一个定时器，没有动画时停表）。系统关闭动画时 Start 直接落到终态。
// 滚动缓出 SmoothScroll 保持原样，不并入这里。
internal sealed class DeskAnimations
{
    public const int FrameMs = 16;
    readonly long[] _start = new long[(int)DeskAnim.Count];
    readonly int[] _length = new int[(int)DeskAnim.Count];

    public bool Enabled { get; set; } = true;

    public void Start(DeskAnim a, int ms, long now)
    {
        _start[(int)a] = now;
        _length[(int)a] = Enabled ? Math.Max(0, ms) : 0;
    }

    public void Stop(DeskAnim a) => _length[(int)a] = 0;

    // 帧定时器每拍问一次：动画进行中，或上一拍还在进行（补画终态的最后一帧）时为真。
    // 让宿主只重画真正在动的那块（书签绳 / 顶栏 / 正文），而不是每拍全窗重画。
    readonly bool[] _live = new bool[(int)DeskAnim.Count];
    public bool NeedsFrame(DeskAnim a, long now)
    {
        bool running = Running(a, now), need = running || _live[(int)a];
        _live[(int)a] = running;
        return need;
    }

    public bool Running(DeskAnim a, long now) => _length[(int)a] > 0 && now - _start[(int)a] < _length[(int)a];

    public float Progress(DeskAnim a, long now) => Running(a, now) ? Math.Clamp((now - _start[(int)a]) / (float)_length[(int)a], 0f, 1f) : 1f;

    public bool AnyRunning(long now)
    {
        for (int i = 0; i < (int)DeskAnim.Count; i++) if (Running((DeskAnim)i, now)) return true;
        return false;
    }

    // U11b：换纸晕开（演示 inkspread：.9s cubic-bezier(.6,.05,.3,1)，圆半径 0 → 160vmax）。
    public const int SpreadMs = 900;
    public static float SpreadRadius(float t, float w, float h) =>
        t >= 1f ? float.MaxValue : CubicBezier(0.6f, 0.05f, 0.3f, 1f, Math.Clamp(t, 0f, 1f)) * 1.6f * MathF.Max(w, h);

    // 演示 --ease: cubic-bezier(.2,.7,.2,1)。
    public static float Ease(float t) => t <= 0f ? 0f : t >= 1f ? 1f : CubicBezier(0.2f, 0.7f, 0.2f, 1f, t);

    // 返回书签弹入（演示 @keyframes backin：0% 右移 14、缩到 .6、透明；60% 左移 3、放大 1.08；100% 原位）。
    // CSS 的缓动作用在每一段关键帧上；透明度只在 0% 定义，按整段缓动到 1。
    public static (float Dx, float Scale, float Opacity) BackFlagPose(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t >= 1f) return (0f, 1f, 1f);
        float opacity = Ease(t);
        if (t < 0.6f)
        {
            float k = Ease(t / 0.6f);
            return (14f - 17f * k, 0.6f + 0.48f * k, opacity);
        }
        float k2 = Ease((t - 0.6f) / 0.4f);
        return (-3f + 3f * k2, 1.08f - 0.08f * k2, opacity);
    }

    internal static float CubicBezier(float x1, float y1, float x2, float y2, float x)
    {
        // 先二分求参数 s 使 X(s) = x，再取 Y(s)。X 单调，20 次足够到 1e-6。
        float lo = 0f, hi = 1f, s = x;
        for (int i = 0; i < 20; i++)
        {
            s = (lo + hi) / 2f;
            if (Bezier(x1, x2, s) < x) lo = s; else hi = s;
        }
        return Bezier(y1, y2, s);
    }

    static float Bezier(float p1, float p2, float s)
    {
        float u = 1f - s;
        return 3f * u * u * s * p1 + 3f * u * s * s * p2 + s * s * s;
    }
}
