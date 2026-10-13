using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U5a：书签绳几何（规格 §4）。纯函数，单位 DIP。frame 描述正文视口：右缘 x、上缘 y、高度。
internal readonly record struct CordFrame(float Right, float Top, float Height);

// U5b：一枚刻度。Y 为客户区 DIP；Target 为点击后要滚到的偏移；Entry 指回目录条目。
internal readonly record struct CordTick(float Y, bool Minor, bool Read, float Target, int Entry);

internal readonly record struct CordRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

internal static class CordGeometry
{
    public const float JumpMin = 12f;   // 点击处离当前位置不足 12 DIP 不跳（演示同值）

    // 绳子可伸长的部分：视口高减去顶部 36 与底部留给页脚的 60。
    public static float Span(CordFrame f) => MathF.Max(1f, f.Height - DeskMetrics.CordTop - DeskMetrics.CordBottomReserve);

    public static float Length(CordFrame f, float progress) => DeskMetrics.CordTop + Math.Clamp(progress, 0f, 1f) * Span(f);

    public static CordRect Cord(CordFrame f, float progress)
    {
        float right = f.Right - DeskMetrics.CordRight;
        return new(right - DeskMetrics.CordWidth, f.Top, right, f.Top + Length(f, progress));
    }

    // 整列都可点可拖（右侧 24 宽，页脚之上）。
    public static bool InStrip(CordFrame f, float x, float y)
        => x >= f.Right - DeskMetrics.StripWidth && x < f.Right && y >= f.Top && y < f.Top + f.Height - DeskMetrics.FooterHeight;

    public static float ProgressAt(CordFrame f, float y) => Math.Clamp((y - f.Top - DeskMetrics.CordTop) / Span(f), 0f, 1f);

    // 相对拖动：手移多少，绳子走多少。
    public static float DragOffset(float startOffset, float dy, CordFrame f, float maxOffset)
        => maxOffset <= 0f ? 0f : Math.Clamp(startOffset + dy / Span(f) * maxOffset, 0f, maxOffset);

    // U5b：刻度。二级标题宽 13，三级及以下宽 7，高 1.5，与绳子同一中轴；没有轨道线。
    // 刻度放在“标题停在视口上方 70 处”对应的进度上（演示 OFF=70）；绳子垂到刻度时，这一章恰好成为当前章节。
    public static float Axis(CordFrame f) => f.Right - DeskMetrics.CordRight - DeskMetrics.CordWidth / 2f;

    public static float TickY(CordFrame f, float headY, float maxOffset)
        => f.Top + DeskMetrics.CordTop + Progress(headY - DeskMetrics.ChapterTickInset, maxOffset) * Span(f);

    // 点击刻度跳到的偏移：标题停在视口上方 60 处（演示 OFF-10）。
    public static float TickTarget(float headY, float maxOffset) => Math.Clamp(headY - DeskMetrics.ChapterJumpInset, 0f, MathF.Max(0f, maxOffset));

    public static CordRect TickRect(CordFrame f, CordTick t, bool near)
    {
        float w = (t.Minor ? DeskMetrics.TickWidthMinor : DeskMetrics.TickWidth) * (near ? DeskMetrics.TickNearScale : 1f), axis = Axis(f), h = DeskMetrics.TickHeight / 2f;
        return new(axis - w / 2f, t.Y - h, axis + w / 2f, t.Y + h);
    }

    // heads 为目录条目（级别、文档 y）；一级标题不出刻度。current 为当前章节下标（含）之前的刻度算已读。
    public static void Ticks(List<CordTick> into, IReadOnlyList<(int Level, float Y)> heads, int current, CordFrame f, float maxOffset)
    {
        into.Clear();
        for (int i = 0; i < heads.Count; i++)
        {
            if (heads[i].Level <= 1) continue;
            into.Add(new(TickY(f, heads[i].Y, maxOffset), heads[i].Level >= 3, i <= current, TickTarget(heads[i].Y, maxOffset), i));
        }
    }

    // 离 y 最近且在 ±7 内的刻度；没有返回 -1。
    public static int NearTick(IReadOnlyList<CordTick> ticks, float y)
    {
        int best = -1; float bestD = DeskMetrics.TickNear;
        for (int i = 0; i < ticks.Count; i++)
        {
            float d = MathF.Abs(ticks[i].Y - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // U5c：返回书签。距右 12，24×16，左尖右平，竖向中心在“原处”对应的进度上；塞在绳子后面、从左侧探出。
    public static CordRect BackFlag(CordFrame f, float progress)
    {
        float right = f.Right - DeskMetrics.BackFlagRight, cy = f.Top + Length(f, progress);
        return new(right - DeskMetrics.BackFlagWidth, cy - DeskMetrics.BackFlagHeight / 2f, right, cy + DeskMetrics.BackFlagHeight / 2f);
    }

    // 可点区域：书签露在绳子左侧的部分，竖向 24 高（演示 .back height 24）；被绳子盖住的部分归绳子。
    public static bool InBackFlag(CordFrame f, float progress, float x, float y)
    {
        var b = BackFlag(f, progress);
        float cy = (b.Top + b.Bottom) / 2f;
        return x >= b.Left && x < f.Right - DeskMetrics.CordRight - DeskMetrics.CordWidth && MathF.Abs(y - cy) <= DeskMetrics.BackFlagHitHalf;
    }

    public static float Progress(float offset, float maxOffset) => maxOffset > 0.5f ? Math.Clamp(offset / maxOffset, 0f, 1f) : 0f;
}

internal enum CordAction { None, Scroll, Jump }

internal readonly record struct CordResult(CordAction Action, float Offset);

// U5a：书签绳的按下 / 拖动 / 点击。按下后移动不足 4 DIP 就松开算点击。
internal sealed class CordInteraction
{
    float _startY, _startOffset;
    public bool Pressed { get; private set; }
    public bool Dragging { get; private set; }

    public void Down(float y, float offset)
    {
        Pressed = true; Dragging = false;
        _startY = y; _startOffset = offset;
    }

    public CordResult Move(float y, CordFrame f, float maxOffset)
    {
        if (!Pressed) return default;
        float dy = y - _startY;
        if (!Dragging && MathF.Abs(dy) < DeskMetrics.ClickSlop) return default;
        Dragging = true;
        return new(CordAction.Scroll, CordGeometry.DragOffset(_startOffset, dy, f, maxOffset));
    }

    // snap：点击处 ±7 内有刻度时，跳到该章节（不受 12 的限制）。
    public CordResult Up(float y, CordFrame f, float maxOffset, float currentOffset, float? snap = null)
    {
        if (!Pressed) return default;
        bool dragged = Dragging;
        Pressed = Dragging = false;
        if (dragged) return default;
        if (snap is float chapter) return new(CordAction.Jump, chapter);
        float target = CordGeometry.ProgressAt(f, y) * MathF.Max(0f, maxOffset);
        return MathF.Abs(target - currentOffset) < CordGeometry.JumpMin ? default : new(CordAction.Jump, target);
    }

    public void Cancel() => Pressed = Dragging = false;
}
