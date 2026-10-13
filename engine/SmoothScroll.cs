using System;

namespace MDViewerNative;

// 滚轮平滑滚动的状态机:目标累积与逐帧缓出。帧定时器、SCROLLINFO 与重绘仍由窗口宿主负责,
// 这里没有任何 Win32 依赖,自测可以逐帧推进直到收敛。
internal sealed class SmoothScroll
{
    internal bool Active { get; private set; }
    internal float Target { get; private set; }

    // 开始一次缓动;返回 true 表示宿主需要启动帧定时器。已在缓动中则只累积目标,不重置起点。
    internal bool Begin(float current)
    {
        if (Active) return false;
        Target = current;
        Active = true;
        return true;
    }

    // 一格滚轮 = 86 DIP × 2.5;目标始终钳在 [0, max]
    internal void Accumulate(int delta, float max) => Target = ScrollGeometry.WheelTarget(Target, delta, max);

    // 一次性跳转的平滑滚动（U5a 书签绳点击）：直接给出终点。
    internal void Retarget(float target, float max) => Target = Math.Clamp(target, 0f, max);

    internal void Stop() => Active = false;

    // 推进一帧:返回本帧应设置的偏移;settled=true 表示已到达目标,缓动结束
    internal float Step(float current, float scale, float max, out bool settled)
    {
        Target = Math.Clamp(Target, 0f, max);   // WM_SIZE 改变文档可视高后重钳,防永差 >0.5 空转
        float next = ScrollGeometry.EaseStep(current, Target, scale, out settled);
        if (settled) Active = false;
        return next;
    }
}
