using System;
using System.Collections.Generic;
using System.Linq;

namespace MDViewerNative;

// 试用修复：desk「更多」菜单的宿主接线（顶栏「更多」）。条目与几何在 MoreMenu，窗口与绘制在 MoreMenuView；
// classic 仍用系统菜单（ShowMoreMenu）。做法同外观面板：无激活弹出窗，键盘由消息循环先交给菜单，菜单外按下即收起。
// 选中的命令投递回主窗口的 WM_COMMAND，与系统菜单、加速键走同一条分派。
internal static unsafe partial class Program
{
    static MoreMenuView _more;
    static MoreMenuView More => _more ??= new MoreMenuView(new MoreHost());

    static bool MoreOpen => _more?.Visible == true;

    static void ToggleMore(bool keyboard)
    {
        if (MoreOpen) { _more.Close(); return; }
        if (HasComposition) return;
        CloseLook();
        var (x, w) = Toolbar.RectOf(ToolbarTarget.ForCommand(ToolbarCommand.More));
        if (w <= 0f) return;
        Toolbar.HideTip();
        More.Open(x + w, ToolH + 2f, keyboard);
        InvalidateToolbar(_hwnd);
    }

    static void CloseMore() => _more?.Close();

    // 消息循环：菜单开着时先看这条消息是否归它处理。返回 true 表示已消费。
    static bool MoreFilter(in MSG msg)
    {
        if (!MoreOpen) return false;
        if (msg.message == Externs.WM_KEYDOWN) return _more.HandleKey((int)msg.wParam);
        bool press = msg.message is 0x0201 or 0x0204 or 0x0207 or 0x00A1 or 0x00A4 or 0x00A7;
        if (!press || _more.Owns(msg.hwnd)) return false;
        if (msg.hwnd == _hwnd && msg.message == 0x0201)
        {
            float x = (short)((long)msg.lParam & 0xFFFF) / _scale, y = (short)(((long)msg.lParam >> 16) & 0xFFFF) / _scale;
            if (Toolbar.HitTarget(x, y) == ToolbarTarget.ForCommand(ToolbarCommand.More)) return false;   // 按钮抬起时自己切换
        }
        _more.Close();
        return false;
    }

    static IReadOnlyList<MoreItem> MoreItems()
    {
        var st = CmdStateNow();
        MoreCommand? Lookup(int id)
        {
            var c = Commands.All.FirstOrDefault(x => x.Id == id);
            return c == null ? null : new MoreCommand(c.Id, c.Label, c.Accel, c.IsEnabled(st));
        }
        // 窄窗折进「更多」的顶栏按钮（前五个命令与 ToolbarCommand 同序）；已在固定条目里的（打开）不重复。
        var folded = new List<MoreCommand>();
        foreach (var t in Toolbar.FoldedCommands)
        {
            int i = (int)t;
            if (i < 0 || i >= 5) continue;
            var c = Commands.All[i];
            if (Array.Exists(MoreMenu.Fixed, f => f.Id == c.Id) || folded.Exists(f => f.Id == c.Id)) continue;
            folded.Add(new MoreCommand(c.Id, c.Label + (c.Id == 1001 && st.Dirty ? "（未保存）" : ""), c.Accel, c.IsEnabled(st)));
        }
        return MoreMenu.Items(folded, Lookup);
    }

    sealed class MoreHost : IMoreMenuHost
    {
        public nint Owner => _hwnd;
        public float Scale => _scale;
        public IReadOnlyList<MoreItem> Items() => MoreItems();
        public void Run(int command) => _ = Externs.PostMessage(_hwnd, Externs.WM_COMMAND, (IntPtr)command, IntPtr.Zero);
        public void Closed() => InvalidateToolbar(_hwnd);
    }
}
