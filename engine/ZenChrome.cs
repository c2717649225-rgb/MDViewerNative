namespace MDViewerNative;

// 阶段 B:显式沉浸(Zen)。只存值,不触 Win32:用户开关(持久化)与指针是否停在工具栏带。
// 规则(mcp-design-direction §五):几何不变——隐藏只停止绘制工具栏,正文坐标与滚动位置不动;
// 编辑态、工具栏有键盘焦点(F6)、查找打开、指针在工具栏带时一律显示;焦点回到正文后才再隐退。
public sealed class ZenChrome
{
    public bool Enabled { get; set; }
    public bool PointerInBand { get; private set; }

    public bool Hides(bool editing, bool toolbarFocus, bool findOpen) => Enabled && !PointerInBand && !editing && !toolbarFocus && !findOpen;

    // 返回是否需要重绘工具栏带。
    public bool SetPointer(bool inBand)
    {
        if (PointerInBand == inBand) return false;
        PointerInBand = inBand;
        return Enabled;
    }
}
