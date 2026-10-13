using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// Shared menu declarations. Every caller retains explicit menu lifetime ownership.
internal static class NativeMenus
{
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32.dll")] public static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);   // U6a：外观菜单的纸张单选
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
}
