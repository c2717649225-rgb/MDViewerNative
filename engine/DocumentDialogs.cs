using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

internal enum DocumentChoice { Cancel, Save, Discard }
internal enum ConflictChoice { Cancel, SaveAs, Overwrite }

// Dialogs return decisions. The host commits document and editor state together.
internal static unsafe class DocumentDialogs
{
    // 宿主注入的自绘选择框（desk 纸色外观）：(所属窗口, 标题, 正文, 按钮文字——最后一个是“取消”) → 1000 + 下标 / 2；返回 -1 用系统对话框。
    internal static Func<IntPtr, string, string, string[], int> Styled;

    public static DocumentChoice Unsaved(IntPtr owner, string name) => Choose(owner, name,
        "要保存对「" + name + "」的更改吗？", "不保存的话，这次的修改会丢失。", "保存", "不保存") switch
    { 1000 => DocumentChoice.Save, 1001 => DocumentChoice.Discard, _ => DocumentChoice.Cancel };

    // 冲突差异(42):第三个按钮打开磁盘版本→草稿的逐行差异;差异窗内同样可决定,"返回"回到本对话
    public static ConflictChoice Conflict(IntPtr owner, string path, string draft = null)
    {
        while (true)
        {
            int choice = Choose(owner, "保存冲突", "磁盘上的文件已更改或删除", "当前草稿已保留。请选择如何保存：\n" + path,
                "另存为副本", "覆盖磁盘版本", draft == null ? null : "查看差异…");
            if (choice == 1000) return ConflictChoice.SaveAs;
            if (choice == 1001) return ConflictChoice.Overwrite;
            if (choice != 1002) return ConflictChoice.Cancel;
            var viewed = ShowConflictDiff(owner, path, draft);
            if (viewed != ConflictChoice.Cancel) return viewed;
        }
    }

    internal static (string Description, string[] Lines) ConflictDiffLines(string diskText, string draft)
    {
        var diff = TextDiff.Unified(diskText ?? "", draft ?? "");
        string description = diskText == null ? "磁盘文件已被删除；下面以草稿全文作为新增行显示。"
            : "磁盘版本 → 当前草稿：删除 " + diff.Removed + " 行，新增 " + diff.Added + " 行" + (diff.Truncated ? "（列表已截断）" : "") + "。";
        string[] lines = diff.Lines.Count == 0 ? new[] { "（文本内容相同；差异仅在编码、换行或 BOM）" } : diff.Lines.ToArray();
        return (description, lines);
    }

    static ConflictChoice ShowConflictDiff(IntPtr owner, string path, string draft)
    {
        string disk;
        try { disk = DocumentFile.Read(path).Text; }
        catch (System.IO.FileNotFoundException) { disk = null; }
        catch (System.IO.DirectoryNotFoundException) { disk = null; }
        catch (Exception ex)
        {
            AppLog.Error("conflict diff", ex);
            Externs.MessageBoxW(owner, "无法读取磁盘版本：" + ex.Message, "保存冲突", 0x30);
            return ConflictChoice.Cancel;
        }
        var (description, lines) = ConflictDiffLines(disk, draft);
        var result = NativeForm.Show(owner, "保存冲突 — 差异", description, null, lines, false, new[] { "另存为副本", "覆盖磁盘版本", "返回" });
        return result?.Action switch { 0 => ConflictChoice.SaveAs, 1 => ConflictChoice.Overwrite, _ => ConflictChoice.Cancel };
    }

    public static bool ConfirmOverwrite(IntPtr owner, string path) =>
        Externs.MessageBoxW(owner, "目标文件已存在，是否覆盖?\n" + path + "\n\n原版本将保留为 .bak。", "确认覆盖", 0x124 /*YESNO, warning, default No*/) == 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TaskButton { public int Id; public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TaskConfig
    {
        public uint Size;
        public IntPtr Owner, Instance;
        public uint Flags, CommonButtons;
        public IntPtr Title, Icon, Instruction, Content;
        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public IntPtr Radios;
        public int DefaultRadio;
        public IntPtr Verification, Expanded, ExpandedControl, CollapsedControl, FooterIcon, Footer, Callback, CallbackData;
        public uint Width;
    }
    [DllImport("comctl32.dll")]
    static extern int TaskDialogIndirect(in TaskConfig config, out int button, IntPtr radio, IntPtr verified);

    static int Choose(IntPtr owner, string title, string instruction, string content, string first, string second, string third = null)
    {
        if (Styled != null)
        {
            string[] labels = third != null ? new[] { first, second, third, "取消" } : new[] { first, second, "取消" };
            int styled = Styled(owner, instruction, content, labels);
            if (styled >= 0) return styled;
        }
        IntPtr t = IntPtr.Zero, i = IntPtr.Zero, c = IntPtr.Zero, a = IntPtr.Zero, b = IntPtr.Zero, d = IntPtr.Zero;
        try
        {
            t = Marshal.StringToHGlobalUni(title); i = Marshal.StringToHGlobalUni(instruction);
            c = Marshal.StringToHGlobalUni(content); a = Marshal.StringToHGlobalUni(first); b = Marshal.StringToHGlobalUni(second);
            if (third != null) d = Marshal.StringToHGlobalUni(third);
            TaskButton* buttons = stackalloc TaskButton[] { new() { Id = 1000, Text = a }, new() { Id = 1001, Text = b }, new() { Id = 1002, Text = d } };
            var config = new TaskConfig
            {
                Size = (uint)sizeof(TaskConfig),
                Owner = owner,
                Flags = 0x1000,
                CommonButtons = 8,
                Title = t,
                Instruction = i,
                Content = c,
                ButtonCount = third != null ? 3u : 2u,
                Buttons = (IntPtr)buttons,
                DefaultButton = 1000,
            };
            try
            {
                if (TaskDialogIndirect(in config, out int selected, IntPtr.Zero, IntPtr.Zero) == 0) return selected;
            }
            catch (EntryPointNotFoundException) { }
            if (third != null)
            {
                // MessageBox 兜底只有三键:先问是否查看差异,再问保存方式
                int see = Externs.MessageBoxW(owner, instruction + "\n" + content + "\n\n是否先查看差异？", title, 3);
                if (see == 6) return 1002;
                if (see != 7) return 2;
            }
            int fallback = Externs.MessageBoxW(owner, instruction + "\n" + content + "\n\n是：" + first + "\n否：" + second + "\n取消：返回文档", title, 3);
            return fallback == 6 ? 1000 : fallback == 7 ? 1001 : 2;
        }
        finally
        {
            Marshal.FreeHGlobal(t); Marshal.FreeHGlobal(i); Marshal.FreeHGlobal(c);
            Marshal.FreeHGlobal(a); Marshal.FreeHGlobal(b); if (d != IntPtr.Zero) Marshal.FreeHGlobal(d);
        }
    }
}
