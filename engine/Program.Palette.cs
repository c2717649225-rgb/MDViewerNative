using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U11：命令面板的宿主接线（desk 界面：顶栏印章「阅」、Ctrl+K、Ctrl+Shift+P）。条目与筛选在 CommandSearch，窗口与绘制在 PaletteView。
// 条目＝命令表（去掉只在光标 / 选区处有意义的）＋四种纸张＋最近文件；执行一律回到 WM_COMMAND，与菜单、快捷键同一道门禁。
internal static unsafe partial class Program
{
    static PaletteView _palette;
    static PaletteView Palette => _palette ??= new PaletteView(new PaletteHost());

    static bool PaletteOpen => _palette?.Visible == true;

    static void TogglePalette()
    {
        if (PaletteOpen) { _palette.Close(); return; }
        CloseLook();
        CloseMore();
        if (!State.Appearance.Desk || HasComposition) return;
        CloseJuan(false);
        if (Find.Visible) Find.Close();
        HideSelectionBubble();
        Toolbar.HideTip();
        Palette.Open();
    }

    static readonly string[] PaperNames = { "跟随系统", "宣纸", "青瓷", "素白", "灯下" };
    static readonly string[] PaperNotes = { "Windows 浅色用宣纸，深色用灯下", "白天、室内、日常阅读", "长时间阅读：更暗、更绿，不刺眼", "投屏、截图、强光；同打印效果", "夜里、暗处" };

    static string PaletteLabel(CmdDef c) => c.Id switch
    {
        1002 => _editing ? "回到阅读" : "编辑",
        1004 => RenderResources.DarkMode ? "换回浅色纸张" : "换到灯下",
        _ => c.Label.TrimEnd('…'),
    };

    static List<PaletteEntry> PaletteCommands()
    {
        var extras = new List<PaletteEntry>();
        for (int i = 0; i < PaperNames.Length; i++)
            extras.Add(new PaletteEntry(PaletteKind.Command, 2300 + i, "纸张：" + PaperNames[i], "", PaperNotes[i]));
        return CommandSearch.Catalog(Commands.All, CmdStateNow(), PaletteLabel, extras);
    }

    static List<PaletteEntry> PaletteRecent()
    {
        var list = new List<PaletteEntry>();
        for (int i = 0; i < _recent.Count; i++)
        {
            string path = _recent[i];
            list.Add(new PaletteEntry(PaletteKind.Recent, i, System.IO.Path.GetFileName(path), "", System.IO.Path.GetDirectoryName(path), true, path));
        }
        return list;
    }

    static void RunPaletteEntry(PaletteEntry e)
    {
        if (e.Kind == PaletteKind.Recent) { if (e.Path != null) LoadFile(e.Path); return; }
        _ = Externs.PostMessage(_hwnd, Externs.WM_COMMAND, (IntPtr)e.Id, IntPtr.Zero);
    }

    sealed class PaletteHost : IPaletteHost
    {
        public nint Owner => _hwnd;
        public float Scale => _scale;
        public bool Animate => SystemAnimationEnabled();
        public IReadOnlyList<PaletteEntry> Commands() => PaletteCommands();
        public IReadOnlyList<PaletteEntry> Recent() => PaletteRecent();
        public void Run(PaletteEntry entry) => RunPaletteEntry(entry);
        public void Closed()
        {
            _ = Externs.SetFocus(ActiveFocusTarget());
            InvalidateToolbar(_hwnd);
        }
    }
}
