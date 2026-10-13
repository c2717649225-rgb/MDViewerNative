using System;
using System.IO;

namespace MDViewerNative;

// 窗口生命周期、退出确认、拖拽分类与上下文菜单纯函数模块:
// 负责窗口关闭前的脏状态汇总与退出阻断仲裁、文件拖放后缀分类、
// 异常销毁草稿检查点决策，以及上下文菜单坐标与目标判定。
// 所有 Win32 消息循环、窗口销毁与 UI 弹窗均保留在 Program.cs 宿主中。
internal static class WindowLifecycle
{
    internal enum DropKind
    {
        None = 0,
        Document = 1,
        Image = 2
    }

    internal readonly record struct ContextMenuAnchor(bool IsKeyboard, int X, int Y);

    // 判定窗口是否处于允许关闭状态(输入法合成中或原生弹出表单活动时禁止关闭)
    internal static bool CanClose(bool hasComposition, bool isNativeFormActive)
    {
        return !hasComposition && !isNativeFormActive;
    }

    // 计算除当前活动标签外,其余处于脏状态的标签数(防非活动标签静默丢失)
    internal static int CountDirtyOtherTabs(int activeTab, int totalTabs, Func<int, bool> isTabDirty)
    {
        if (totalTabs <= 0 || isTabDirty == null) return 0;
        int dirtyCount = 0;
        for (int i = 0; i < totalTabs; i++)
        {
            if (i != activeTab && isTabDirty(i))
                dirtyCount++;
        }
        return dirtyCount;
    }

    // 拖放路径有效性检查(避免截断或空路径误判)
    internal static bool IsValidDropLength(uint copiedLength, uint capacity)
    {
        return copiedLength > 0 && copiedLength < (capacity - 1);
    }

    // 拖放文件分类: Markdown/文本文档 vs 图片
    internal static DropKind ClassifyDropPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return DropKind.None;
        string ext;
        try
        {
            ext = Path.GetExtension(path);
        }
        catch
        {
            return DropKind.None;
        }
        if (string.IsNullOrEmpty(ext)) return DropKind.None;

        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return DropKind.Document;
        }

        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return DropKind.Image;
        }

        return DropKind.None;
    }

    // 解析 WM_CONTEXTMENU 触发源坐标(键盘菜单键下 lParam 为 -1，锚定至阅读列左上角固定位置)
    internal static ContextMenuAnchor ResolveContextMenuAnchor(int rawX, int rawY, float scale, float contentTop)
    {
        if (rawX == -1)
        {
            int defaultY = (int)((contentTop + 24f) * scale);
            return new ContextMenuAnchor(true, 40, defaultY);
        }
        return new ContextMenuAnchor(false, rawX, rawY);
    }

    // 判定鼠标右键位置是否位于正文区域(非键盘模式下需要命中正文)
    internal static bool IsContextMenuOverBody(int clientX, int clientY, float scale, Func<float, float, bool> overBody)
    {
        if (scale <= 0f || overBody == null) return false;
        return overBody(clientX / scale, clientY / scale);
    }

    // 判定异常退出时是否需要进行草稿安全存档
    internal static bool ShouldCheckpointDraftsOnDestroy(bool closingNormally)
    {
        return !closingNormally;
    }
}
