namespace MDViewerNative;

// U7：desk 查找的宿主接线。跳转规则与描绘时机来自演示稿 showHit / drawCircle；几何在 InkCircle（纯）。
internal static unsafe partial class Program
{
    // 逐处前进且还没有返回书签时，这一跳记为页内跳转（留书签、可 Alt+← 回去）；否则只平滑滚过去。
    static void RevealFindHit(float offset, bool remember)
    {
        if (remember && !ReturnPrompt.Visible) { JumpWithinDocument(offset); return; }
        if (SystemAnimationEnabled()) GlideTo(ScrollGeometry.ClampOffset(offset, MaxOff(), _scale));
        else SetOffset(offset, allowTopInset: true);
    }

    static void RestartInkCircle()
    {
        StartDeskAnimation(DeskAnim.Ink, InkCircle.DelayMs + InkCircle.DrawMs);
        Doc.InkProgress = Anim.Running(DeskAnim.Ink, System.Environment.TickCount64) ? 0f : 1f;   // 系统关闭动画：直接画完
    }

    static void TickInkCircle(long now)
    {
        Doc.InkProgress = InkCircle.DrawProgress(Anim.Progress(DeskAnim.Ink, now));
        InvalidateContent(_hwnd);
    }
}
