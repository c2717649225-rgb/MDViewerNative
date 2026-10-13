using System;
using System.Runtime.CompilerServices;

namespace MDViewerNative;

// 滚动的纯算术:像素网格量化、滚动上限、滚动条/键盘命令表、滚轮目标与缓出步进、拖拽边缘判定。
// 窗口宿主保留定时器、SCROLLINFO 调用与重绘;这里的每个函数都不需要窗口即可运行。
internal static class ScrollGeometry
{
    internal const float LineDip = 86f;          // 一行(上下键/滚动条箭头)
    internal const float PageKeepDip = 40f;      // 翻页保留的可视重叠
    internal const int EdgeBandPx = 40;          // 拖拽自动滚动的边缘触发带(物理像素)

    // 量化到物理像素网格(D2D 文本竖直对齐的最小稳定步长)
    internal static float Snap(float dip, float scale) => MathF.Round(dip * scale) / scale;

    // 滚动上限向下落到物理像素网格:所有钳制点统一口径,否则 Snap 向上取整会越上限一整格,底部首格滚轮回跳 1px
    internal static float MaxOffset(float totalH, float contentHeight, float scale)
        => MathF.Floor(MathF.Max(0f, totalH - contentHeight) * scale) / scale;

    // 像素网格量化:滚轮缓出是亚像素步进,D2D 文本竖直像素对齐会随偏移的小数部分变化让各行独立 ±1px 跳动(视觉即"字上下抖")。
    // 偏移落到物理像素网格后,每帧内容整体位移整数像素,行间相对位置恒定,滚动观感稳定。
    internal static float ClampOffset(float offDip, float max, float scale)
    {
        float q = Snap(offDip, scale);
        if (q > max) q = max;   // max 已在网格上,距真实底部 <1px
        return Math.Clamp(q, 0f, max);
    }

    // 滚动条命令(SB_LINEUP=0 … SB_BOTTOM=7)→ 目标偏移;拇指拖动(4/5)需要读取 nTrackPos,留给宿主
    internal static bool TryScrollBarTarget(int command, float offY, float viewDip, out float target)
    {
        switch (command)
        {
            case 0: target = offY - LineDip; return true;
            case 1: target = offY + LineDip; return true;
            case 2: target = offY - viewDip + PageKeepDip; return true;
            case 3: target = offY + viewDip - PageKeepDip; return true;
            case 6: target = 0f; return true;
            case 7: target = float.MaxValue; return true;
            default: target = offY; return false;
        }
    }

    // 阅读态翻页键与滚动条命令共用一张表:Up/Down=行,PgUp/PgDn=页,Home/End=顶/底
    internal static bool TryKeyTarget(int vk, float offY, float viewDip, out float target)
    {
        int command = vk switch
        {
            0x26 => 0,
            0x28 => 1,
            0x21 => 2,
            0x22 => 3,
            0x24 => 6,
            0x23 => 7,
            _ => -1,
        };
        return TryScrollBarTarget(command, offY, viewDip, out target);
    }

    // 滚轮目标累积:一格 = 86 DIP × 2.5,目标钳在 [0, max]
    internal static float WheelTarget(float target, int delta, float max)
        => Math.Clamp(target - delta / 120f * 86f * 2.5f, 0f, max);

    // 指数缓出一帧。converged=true 时返回 target,宿主应停表;否则返回下一帧偏移。
    // 收敛阈值须 > 半格:量化后残余 diff 停在网格点上,阈值 0.5 在小 diff 段不可达 → 永久停摆 + 60fps 空转重绘。
    // 单帧步长封顶:快速滚轮时 diff 可达数千 DIP,不封顶则起步一步数百像素的突跳。
    internal static float EaseStep(float current, float target, float scale, out bool converged)
    {
        float diff = target - current;
        float grid = 1f / scale;   // 物理像素网格(DIP)
        if (MathF.Abs(diff) < 0.5f + 0.5f * grid)
        {
            converged = true;
            return target;
        }
        const float MaxStep = 120f;   // ≈半格滚轮量;单格滚动(215)的前几步(47/37/…)不受影响,只有大 diff 被摊平
        float step = diff * 0.22f;
        if (MathF.Abs(step) > MaxStep) step = MaxStep * MathF.Sign(step);
        float snapped = Snap(current + step, scale);
        if (MathF.Abs(snapped - current) < 0.5f * grid)
            snapped = current + MathF.Sign(diff) * grid;   // 量化吞步(银行家舍入中点陷阱):补整格,否则收敛不可达
        converged = false;
        return snapped;
    }

    // 拖拽选区接近视口上/下边缘 40px 内 → 持续滚动方向(-1/0/+1)
    internal static int AutoScrollDirection(int my, int contentTopPx, int clientHeightPx)
    {
        int topEdge = contentTopPx + EdgeBandPx;
        int botEdge = clientHeightPx - EdgeBandPx;
        return my >= botEdge ? 1 : my <= topEdge ? -1 : 0;
    }

    // 系统滚动条参数:范围/页长/位置全部换算成物理像素,页长至少 1
    internal static SCROLLINFO ScrollBarInfo(uint mask, float totalH, float contentHeight, float offY, float scale)
    {
        var si = new SCROLLINFO { cbSize = (uint)Unsafe.SizeOf<SCROLLINFO>(), fMask = mask };
        si.nMin = 0;
        si.nMax = (int)(totalH * scale);
        si.nPage = Math.Clamp((int)(contentHeight * scale), 1, 1 << 20);
        si.nPos = (int)MathF.Round(offY * scale);
        return si;
    }
}
