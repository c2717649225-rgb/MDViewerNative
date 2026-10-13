namespace MDViewerNative;

// U6c：读时退后（规格 §3「退后一步」；演示 scroll 监听）。纯状态机，单位 DIP。
// 阅读时往下滚（单步 > 6）且越过 120 → 退后；往上滚 30 以上或回到 60 以内 → 恢复。
// 指针在顶栏上或键盘焦点在顶栏里时不变；指针进入顶栏、焦点进入、切换标签或模式由宿主调 Wake。
// “上一次位置”只在有明显位移（> 6）或接近顶部时更新，慢速连续滚动也能累积到阈值（同演示）。
internal sealed class TitlebarQuiet
{
    float _lastY;

    public bool Quiet { get; private set; }

    public bool Observe(float y, bool reading, bool engaged)
    {
        bool was = Quiet;
        float dy = y - _lastY;
        if (reading && !engaged)
        {
            if (dy > DeskMetrics.QuietDownStep && y > DeskMetrics.QuietAfterY) Quiet = true;
            else if (dy < -DeskMetrics.WakeUpStep || y < DeskMetrics.WakeNearTop) Quiet = false;
        }
        if (System.Math.Abs(dy) > DeskMetrics.QuietDownStep || y < DeskMetrics.WakeNearTop) _lastY = y;
        return Quiet != was;
    }

    public bool Wake(float y)
    {
        _lastY = y;
        if (!Quiet) return false;
        Quiet = false;
        return true;
    }

    // 过渡中途反向时从当前混合量接着走，不跳变。from：变化那一刻的混合量；eased：缓动后的进度。
    public float Amount(float from, float eased) => from + ((Quiet ? 1f : 0f) - from) * eased;
}
