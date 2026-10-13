using System;

namespace MDViewerNative;

// Commands are semantic actions, not positions in the current keyboard sequence.
internal enum ToolbarCommand { Open, ToggleEdit, Save, ToggleFind, ToggleToc, More, Documents, Read, Appearance, Palette }   // Palette：desk 顶栏左上角印章「阅」（U11）   // Read / Appearance 只在 desk 顶栏（U6a）
internal enum ToolbarPart { None, Command, Tab, Close }
internal readonly record struct ToolbarTarget(ToolbarPart Part, ToolbarCommand Command = default, Guid DocumentId = default)
{
    public static ToolbarTarget ForCommand(ToolbarCommand command) => new(ToolbarPart.Command, command);
    public static ToolbarTarget ForTab(Guid id, bool close = false) => new(close ? ToolbarPart.Close : ToolbarPart.Tab, DocumentId: id);
    public bool IsTab => Part is ToolbarPart.Tab or ToolbarPart.Close;
}
