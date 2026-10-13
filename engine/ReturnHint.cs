namespace MDViewerNative;

// 「返回原阅读处」(设计方针第五节):一次成功的跳转(目录、脚注、链接片段、书签)之后,
// 在正文列底部居中显示一枚安静的标签,点击等同后退;Alt+Left 与右键“后退”始终可用。
// 只存值:不持有文档、句柄或渲染资源。不可聚焦(键盘路径就是 Alt+Left),无动画;
// 到期时若指针悬停其上,延后到指针离开再隐藏。
internal sealed class ReturnHint
{
    public const int TimerId = 15;
    public const uint LifetimeMs = 6000;
    public const string Text = "返回原处  Alt+←";

    public bool Visible { get; private set; }
    public bool Hover { get; private set; }
    private bool _expired;

    public void Show() { Visible = true; _expired = false; }
    public void Hide() { Visible = false; Hover = false; _expired = false; }

    // 计时到期:返回是否需要重绘。
    public bool Expire()
    {
        if (!Visible) return false;
        if (Hover) { _expired = true; return false; }
        Hide();
        return true;
    }

    // 指针进出:返回是否需要重绘;已到期的提示在指针离开时隐藏。
    public bool SetHover(bool over)
    {
        if (!Visible || over == Hover) return false;
        Hover = over;
        if (!over && _expired) Hide();
        return true;
    }
}
