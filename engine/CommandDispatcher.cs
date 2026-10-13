using System;

namespace MDViewerNative;

// Keyboard routing stays independent from the Win32 message procedure.
// The host supplies actions; this type owns only the command-to-action contract.
internal sealed class CommandDispatchContext
{
    internal Action Save { get; init; }
    internal Action ToggleEdit { get; init; }
    internal Action Open { get; init; }
    internal Action ToggleTheme { get; init; }
    internal Action Find { get; init; }
    internal Action ToggleToc { get; init; }
    internal Action Replace { get; init; }
    internal Action CloseTab { get; init; }
    internal Action NextTab { get; init; }
    internal Action PreviousTab { get; init; }
    internal Action ExportHtml { get; init; }
    internal Action SaveAs { get; init; }
    internal Action ZoomIn { get; init; }
    internal Action ZoomOut { get; init; }
    internal Action ResetZoom { get; init; }
}

internal sealed class CommandDispatcher
{
    private readonly CommandDispatchContext _actions;

    internal CommandDispatcher(CommandDispatchContext actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    internal bool TryDispatch(int command)
    {
        switch (command)
        {
            case 1001: _actions.Save(); return true;
            case 1002: _actions.ToggleEdit(); return true;
            case 1003: _actions.Open(); return true;
            case 1004: _actions.ToggleTheme(); return true;
            case 1005: _actions.Find(); return true;
            case 1006: _actions.ToggleToc(); return true;
            case 1008: _actions.CloseTab(); return true;
            case 1009: _actions.NextTab(); return true;
            case 1010: _actions.PreviousTab(); return true;
            case 1011: _actions.ExportHtml(); return true;
            case 1012: _actions.SaveAs(); return true;
            case 1013: _actions.Replace(); return true;
            case 1014: _actions.ZoomIn(); return true;
            case 1015: _actions.ZoomOut(); return true;
            case 1016: _actions.ResetZoom(); return true;
            default: return false;
        }
    }
}
