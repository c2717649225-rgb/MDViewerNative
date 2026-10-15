// 命令表:名称/加速键文本/cmdId/渲染面/启用谓词 的单一真相源(I7,PLAN-ERGONOMICS §1)。
// 现有三份手写副本(ACCEL 表/BtnAction/ShowContextMenu)是口径漂移的根源;菜单与工具栏状态从此表生成。
// 纪律:本文件纯数据+纯函数,不触 Win32(selftest 可编译,menu_golden 对拍的前提);
// 执行与窗口依赖(TaskDialog/文件对话框/SetFocus)全部留在 Program.cs,表只带 Id。
using System;
using System.Collections.Generic;

namespace MDViewerNative;

[Flags]
public enum CmdFace
{
    None = 0,
    Toolbar = 1,   // 每会话高频(打开/编辑/保存/查找/目录/更多)
    More = 2,   // "更多"菜单 = 加速键表的曝光投影,≤10 可点项,与工具栏互斥
    ContextView = 4,   // 预览态右键:"此处此刻"(复制/选区查找/全选/文件操作)
    Documents = 16,
    EditTools = 32,
    Preferences = 64,
    Tabs = 128,
    ReadingTools = 256,
    Export = 512,
    Maintenance = 1024,
    ContextEdit = 8,   // 编辑态右键:剪贴板 + 查找/替换
}

[Flags]
public enum CmdGate
{
    None = 0,
    StableText = 1, // 组合输入/原生表单活动时，不能打断当前文本状态
    Writable = 2,   // 只读会话中不可用
    Document = 4,   // U10：作用于当前文档；起始页显示时不可用
}

// 谓词输入:五维状态快照(纯值,可枚举做真值表断言)
public sealed record CmdState(bool Editing, bool Dirty, bool TocOpen, bool FindOpen, bool HasSel, float BodyFont, bool Serif = false, bool TocAvailable = true, bool HasFile = true, bool Back = false, bool Forward = false, bool CodeBlock = false, bool Composing = false, bool ReadOnly = false, bool Pinned = false, int TabCount = 1, bool CanReopen = false, bool UndoAvailable = false, bool RedoAvailable = false, bool Zen = false, float DefaultFont = 16f, bool Home = false, bool Grain = true)
{
    public bool ZoomMax => BodyFont >= 28f;    // 12–28 钳制(Program.ZoomFont)
    public bool ZoomMin => BodyFont <= 12f;
    public bool ZoomDefault => Math.Abs(BodyFont - DefaultFont) < 0.01f;   // U3：desk 默认 17
}

public sealed record CmdDef(
    int Id,                       // 1001-1016=加速键既有契约;2001+=菜单自有;2093+=I7 新增
    string Label,                 // 主标签(含 … 表示打开对话框,与 menu_golden 文案一致)
    string Accel,                 // 加速键文本(""=无;"Ctrl+Shift+S" 形态;菜单渲染为 Label\tAccel)
    CmdFace Face,
    Func<CmdState, bool> Enabled = null,   // null=恒可用;false=灰显(状态,不是隐藏)
    Func<CmdState, bool> Checked = null,   // 打勾谓词(开关态)
    CmdGate Gate = CmdGate.None)           // 跨状态门禁由命令显式声明
{
    public string MenuText => Accel.Length == 0 ? Label : Label + "\t" + Accel;
    public bool RequiresStableText => (Gate & CmdGate.StableText) != 0;
    public bool RequiresWritable => (Gate & CmdGate.Writable) != 0;
    public bool RequiresDocument => (Gate & CmdGate.Document) != 0;
    public bool IsEnabled(CmdState s) => (!s.Composing || !RequiresStableText) && (!s.ReadOnly || !RequiresWritable) && (!s.Home || !RequiresDocument) && (Enabled == null || Enabled(s));
    public bool IsChecked(CmdState s) => Checked != null && Checked(s);
}

public static class Commands
{
    // 不进入菜单表的原生辅助命令仍在这里显式登记(最近文档、查找栏、Ctrl+Tab)。
    // 这些是控件协议，不再用连续数字区间推断门禁。
    private static readonly Dictionary<int, CmdGate> AuxiliaryGates = new()
    {
        [1009] = CmdGate.StableText,
        [1010] = CmdGate.StableText,
        [2100] = CmdGate.StableText,
        [2101] = CmdGate.StableText,
        [2102] = CmdGate.StableText,
        [2103] = CmdGate.StableText,
        [2104] = CmdGate.StableText,
        [3001] = CmdGate.StableText,
        [3002] = CmdGate.StableText,
        [3003] = CmdGate.StableText,
        [3004] = CmdGate.StableText,
        [3005] = CmdGate.StableText,
        [3006] = CmdGate.StableText,
        [3007] = CmdGate.StableText,
        [3008] = CmdGate.StableText,
        [3009] = CmdGate.StableText,
        [3010] = CmdGate.StableText,
        [3011] = CmdGate.StableText,
        [3012] = CmdGate.StableText,
        [3013] = CmdGate.StableText,
        [3014] = CmdGate.StableText,
        [3015] = CmdGate.StableText,
        [3016] = CmdGate.StableText,
        [3017] = CmdGate.StableText,
        [3018] = CmdGate.StableText,
        [3019] = CmdGate.StableText,
        [3020] = CmdGate.StableText,
        [3021] = CmdGate.StableText,
        [3022] = CmdGate.StableText,
        [3023] = CmdGate.StableText,
    };

    public static bool RequiresStableText(int id) => GateOf(id).HasFlag(CmdGate.StableText);

    public static bool RequiresWritable(int id) => GateOf(id).HasFlag(CmdGate.Writable);

    private static CmdGate GateOf(int id)
    {
        var command = Find(id);
        return command != null ? command.Gate : AuxiliaryGates.GetValueOrDefault(id);
    }

    private static CmdDef Find(int id)
    {
        foreach (var command in All) if (command.Id == id) return command;
        return null;
    }

    // A3:灰显命令给出一句短原因(工具栏提示与折叠菜单共用);null = 没有需要解释的原因。
    public static string DisabledReason(int id, CmdState s) => id switch
    {
        _ when s.Home && GateOf(id).HasFlag(CmdGate.Document) => "起始页上不可用",
        1006 when s.Editing => "编辑时不可用",
        1006 when !s.TocAvailable => "窗口太窄",
        _ => null,
    };

    // 顺序即菜单顺序(契约:menu_golden 对拍含顺序)
    public static readonly CmdDef[] All =
    {
        // —— 工具栏(5 钮 + 更多;有钮命令永不进"更多",互斥焊死)——
        new(1003, "打开",   "Ctrl+O", CmdFace.Toolbar | CmdFace.ContextView, Gate: CmdGate.StableText),
        new(1002, "编辑",   "Ctrl+E", CmdFace.Toolbar, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),            // 编辑态标签变"预览"(DrawToolbar 既有语义);只读会话不进入编辑,与区间规则时期一致
        new(1001, "保存",   "Ctrl+S", CmdFace.Toolbar, s => s.Dirty || s.Editing, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(1005, "查找",   "Ctrl+F", CmdFace.Toolbar | CmdFace.More | CmdFace.ContextView | CmdFace.ContextEdit, Gate: CmdGate.StableText | CmdGate.Document),   // 编辑态也要搜字(可发现性评审:比预览态更刚需的半)
        new(1006, "目录",   "Ctrl+T", CmdFace.Toolbar, s => !s.Editing && s.TocAvailable, Checked: s => s.TocOpen, Gate: CmdGate.Document),

        // —— 右键"此处此刻"(预览态;声明序即菜单序,与 menu_golden 契约一致:文件|此处|操作)——
        new(2001, "复制",   "Ctrl+C", CmdFace.ContextView, s => s.HasSel, Gate: CmdGate.Document),
        new(2081, "复制为 HTML", "", CmdFace.ContextView, s => s.HasSel && !s.Editing, Gate: CmdGate.StableText | CmdGate.Document),   // 41:纯文本 + HTML Format 双格式
        new(2094, "用当前选区查找…", "", CmdFace.ContextView, s => s.HasSel, Gate: CmdGate.Document),   // 划词→右键→回车:全案最短鼠标搜索路径
        new(2002, "全选",   "Ctrl+A", CmdFace.ContextView, Gate: CmdGate.Document),

        // —— "更多"菜单:加速键曝光投影 + 低频文件操作(≤10 可点项)——
        new(1013, "替换…",  "Ctrl+H", CmdFace.More | CmdFace.ContextEdit, s => s.Editing, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),   // E0.3:预览态灰显而非静默降级
        new(1014, "放大",   "Ctrl+=", CmdFace.More, s => !s.ZoomMax),
        new(1015, "缩小",   "Ctrl+-", CmdFace.More, s => !s.ZoomMin),
        new(1016, "恢复默认字号", "Ctrl+0", CmdFace.More, s => !s.ZoomDefault, s => s.ZoomDefault),
        new(1004, "切换到深色主题", "Ctrl+D", CmdFace.More),        // 标签按当前主题翻转(Program 渲染时取反)
        new(2096, "衬线正文", "", CmdFace.More, Checked: s => s.Serif),   // I11:serif opt-in 开关(无加速键——用户明令;需装思源宋体,未装时点击提示)
        new(1011, "导出 HTML…", "Ctrl+Shift+S", CmdFace.More | CmdFace.ContextView, Gate: CmdGate.StableText | CmdGate.Document),
        new(1012, "另存为…", "Ctrl+Alt+S", CmdFace.More | CmdFace.ContextView, Gate: CmdGate.StableText | CmdGate.Document),
        new(1008, "关闭标签", "Ctrl+W", CmdFace.ContextView, Gate: CmdGate.StableText | CmdGate.Document),   // I11:让出 More 第 10 席给衬线开关;Ctrl+W 曝光由右键面承担
        new(2093, "键盘快捷键…", "", CmdFace.More),

        // —— 右键(编辑态):剪贴板六项 + 查找/替换(编辑者也要搜字)——
        new(2003, "撤销",   "Ctrl+Z", CmdFace.ContextEdit, s => s.UndoAvailable, Gate: CmdGate.StableText | CmdGate.Writable),
        new(2270, "重做", "Ctrl+Y", CmdFace.ContextEdit | CmdFace.EditTools, s => s.RedoAvailable, Gate: CmdGate.StableText | CmdGate.Writable),
        new(2004, "剪切",   "Ctrl+X", CmdFace.ContextEdit, Gate: CmdGate.Writable),
        new(2005, "复制",   "Ctrl+C", CmdFace.ContextEdit),
        new(2006, "粘贴",   "Ctrl+V", CmdFace.ContextEdit, Gate: CmdGate.Writable),
        new(2007, "删除",   "",       CmdFace.ContextEdit, Gate: CmdGate.Writable),
        new(2008, "全选",   "Ctrl+A", CmdFace.ContextEdit),
        new(2097, "从磁盘刷新…", "", CmdFace.ContextView | CmdFace.ContextEdit, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2080, "复制代码块", "", CmdFace.ContextView, s => !s.Editing && s.CodeBlock, Gate: CmdGate.StableText | CmdGate.Document),
        new(2098, "后退", "Alt+Left", CmdFace.ContextView | CmdFace.ContextEdit, s => s.Back, Gate: CmdGate.StableText | CmdGate.Document),
        new(2099, "前进", "Alt+Right", CmdFace.ContextView | CmdFace.ContextEdit, s => s.Forward, Gate: CmdGate.StableText | CmdGate.Document),
        new(2200, "新建文档", "Ctrl+N", CmdFace.Documents, Gate: CmdGate.StableText),
        new(2201, "全部保存", "", CmdFace.Documents, Gate: CmdGate.StableText),
        new(2202, "关闭其他标签", "", CmdFace.Tabs, s => s.TabCount > 1, Gate: CmdGate.StableText | CmdGate.Document),
        new(2203, "关闭右侧标签", "", CmdFace.Tabs, s => s.TabCount > 1, Gate: CmdGate.StableText | CmdGate.Document),
        new(2204, "关闭全部标签", "", CmdFace.Tabs, Gate: CmdGate.StableText),
        new(2205, "重新打开关闭的文档", "Ctrl+Shift+T", CmdFace.Tabs, s => s.CanReopen, Gate: CmdGate.StableText),
        new(2206, "标签左移", "", CmdFace.Tabs, s => s.TabCount > 1, Gate: CmdGate.StableText | CmdGate.Document),
        new(2207, "标签右移", "", CmdFace.Tabs, s => s.TabCount > 1, Gate: CmdGate.StableText | CmdGate.Document),
        new(2208, "固定标签", "", CmdFace.Tabs, Checked: s => s.Pinned, Gate: CmdGate.StableText | CmdGate.Document),
        new(2209, "重命名文件…", "", CmdFace.Documents, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2210, "在资源管理器中定位", "", CmdFace.Documents, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2211, "复制完整路径", "", CmdFace.Documents, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2212, "只读锁定", "", CmdFace.Documents, Checked: s => s.ReadOnly, Gate: CmdGate.StableText | CmdGate.Document),
        new(2213, "定位标签…", "", CmdFace.Tabs, Gate: CmdGate.StableText),
        new(2214, "最近文档列表…", "", CmdFace.Documents, Gate: CmdGate.StableText),
        new(2242, "历史版本…", "", CmdFace.Maintenance, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2243, "图片缓存…", "", CmdFace.Maintenance, Gate: CmdGate.StableText),
        new(2250, "行为设置…", "", CmdFace.Preferences, Gate: CmdGate.StableText),
        new(2251, "沉浸模式", "F11", CmdFace.Preferences, Checked: s => s.Zen),   // 阶段 B:隐藏工具栏绘制,几何不变;更多已满 10 项
        new(2252, "纸纹", "", CmdFace.Preferences, Checked: s => s.Grain),   // U2b 纸纹开关：仅命令面板可见(desk 阅读才画；classic/高对比度本就不画)
        new(2280, "添加书签…", "", CmdFace.ReadingTools, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2281, "书签管理…", "", CmdFace.ReadingTools, Gate: CmdGate.StableText),
        new(2282, "跳转历史管理…", "", CmdFace.Maintenance, Gate: CmdGate.StableText),
        new(2220, "跳转到行…", "Ctrl+G", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Document),
        new(2221, "文档信息…", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Document),
        new(2222, "编码与换行…", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2223, "指定编码重新打开…", "", CmdFace.EditTools, s => s.HasFile, Gate: CmdGate.StableText | CmdGate.Document),
        new(2230, "导出 HTML 为…", "", CmdFace.Export, Gate: CmdGate.StableText | CmdGate.Document),
        new(2231, "导出离线 HTML…", "", CmdFace.Export, Gate: CmdGate.StableText | CmdGate.Document),
        new(2232, "打印…", "", CmdFace.Export, Gate: CmdGate.StableText | CmdGate.Document),
        new(2233, "导出 PDF…", "", CmdFace.Export, Gate: CmdGate.StableText | CmdGate.Document),
        new(2240, "恢复副本管理…", "", CmdFace.Maintenance, Gate: CmdGate.StableText),
        new(2260, "加粗", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2261, "斜体", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2262, "插入链接…", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2263, "行内代码", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2264, "围栏代码块", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2265, "标题", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2266, "引用", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2267, "无序列表", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        new(2268, "插入图片文件…", "", CmdFace.EditTools, Gate: CmdGate.StableText | CmdGate.Writable | CmdGate.Document),
        // —— U11：只从命令面板与快捷键到达（不进任何菜单面）——
        new(2290, "起始页", "", CmdFace.None, Gate: CmdGate.StableText),
        new(2291, "命令面板", "Ctrl+K", CmdFace.None, Gate: CmdGate.StableText),
    };

    public static IEnumerable<CmdDef> OnFace(CmdFace face)
    {
        foreach (var c in All) if ((c.Face & face) != 0) yield return c;
    }

    // 结构不变量自检(PLAN-ERGONOMICS §7.3 的机器化;selftest 直调)
    public static string InvariantViolation()
    {
        var ids = new HashSet<int>();
        foreach (var c in All)
        {
            if (!ids.Add(c.Id)) return $"重复 cmdId {c.Id}";
        }
        int moreCount = 0;
        foreach (var c in OnFace(CmdFace.More))
        {
            // 互斥规则唯一例外:查找(可发现性评审裁决——频率 top-2 的操作双入口是保险不是冗余)
            if ((c.Face & CmdFace.Toolbar) != 0 && c.Id != 1005) return $"工具栏命令 {c.Id} 出现在更多(互斥违例)";
            moreCount++;
        }
        if (moreCount > 10) return $"更多菜单 {moreCount} 项 >10 上限";
        foreach (var c in OnFace(CmdFace.ContextView | CmdFace.ContextEdit))
        {
            if (c.Id is 1004 or 1014 or 1015 or 1016) return $"全局设置 {c.Id} 进了右键(此处此刻违例)";
        }
        return null;
    }
}
