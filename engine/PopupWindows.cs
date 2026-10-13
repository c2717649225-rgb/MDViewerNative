using System.Runtime.InteropServices;

namespace MDViewerNative;

// 试用修复：弹出窗（命令面板、外观面板、「更多」菜单、提示）的“画好再露面”。
// 直接 SetWindowPos 显示再失效重画时，DWM 会先合成窗口里上一次留下的画面（上一条提示、上次的高亮）或空白底，下一帧才换成新内容——看起来一闪。
// 这里先隐身（DWMWA_CLOAK），显示并同步画完本窗与子窗口，再现身。
internal static unsafe class PopupWindows
{
    public static void ShowPainted(nint hwnd, int x, int y, int w, int h, uint flags)
    {
        if (hwnd == 0) return;
        int on = 1;
        bool cloaked = DwmSetWindowAttribute(hwnd, 13 /*DWMWA_CLOAK*/, &on, 4) >= 0;
        Externs.SetWindowPos(hwnd, 0, x, y, w, h, flags | 0x0040 /*SWP_SHOWWINDOW*/);
        // RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN：本窗与子窗口（输入框）都在现身前画好
        _ = RedrawWindow(hwnd, 0, 0, 0x0001 | 0x0100 | 0x0080);
        if (!cloaked) return;
        int off = 0;
        _ = DwmSetWindowAttribute(hwnd, 13, &off, 4);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint h, int attr, int* value, int size);
    [DllImport("user32.dll")] static extern bool RedrawWindow(nint h, nint rect, nint region, uint flags);
}
