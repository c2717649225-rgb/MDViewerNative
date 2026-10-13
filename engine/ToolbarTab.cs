using System;
using System.Collections.Generic;

namespace MDViewerNative;

// UI snapshot only: session identity and source/path ownership remain in DocumentSession.
internal sealed record ToolbarTab(Guid Id, string Title, string FullPath, bool Active, bool Dirty, bool ReadOnly = false, bool Pinned = false)
{
    public string DisplayTitle { get; init; } = Title;
    // U10：起始页标签（界面上的固定标签，没有关闭钮，不对应文档会话）。
    public bool IsHome { get; init; }

    public string StatusText
    {
        get
        {
            var states = new List<string>();
            if (Dirty) states.Add("未保存");
            if (ReadOnly) states.Add("只读");
            if (Pinned) states.Add("固定");
            return string.Join(" · ", states);
        }
    }

    public string Hint => IsHome ? Home.TabTitle + "\n" + Home.TabHint : DisplayTitle + (DisplayTitle != Title ? "\n" + Title : "")
        + (FullPath.Length > 0 ? "\n" + FullPath : "\n未命名文档")
        + (StatusText.Length > 0 ? "\n" + StatusText : "");
    // U6a2：desk 提示只写路径 / 关闭（演示 data-tip）。
    public string DeskHint => IsHome ? Home.TabHint : (FullPath.Length > 0 ? FullPath : "未命名文档") + (StatusText.Length > 0 ? "\n" + StatusText : "");
    public string DeskCloseHint => (Dirty ? "有未保存的修改 · 关闭" : "关闭") + " · Ctrl+W";
    public string CloseHint => (Dirty ? "关闭未保存文档" : "关闭标签") + " · Ctrl+W\n" + Hint;
    public string MenuLabel(bool usePath) => DisplayTitle + (usePath && FullPath.Length > 0 ? " — " + FullPath : "")
        + (StatusText.Length > 0 ? " [" + StatusText + "]" : "");
}
