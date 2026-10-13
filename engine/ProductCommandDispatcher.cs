using System;

namespace MDViewerNative;

// Product commands are mapped here, while the host owns document/window state.
// Keeping the mapping independent makes adding a command local and reviewable.
internal sealed class ProductCommandContext
{
    internal Action NewDocument { get; init; }
    internal Action<int> SaveDocumentSet { get; init; }
    internal Action<int> CloseDocumentSet { get; init; }
    internal Action ReopenClosed { get; init; }
    internal Action<int> MoveTab { get; init; }
    internal Action TogglePinned { get; init; }
    internal Action RenameDocument { get; init; }
    internal Action RevealInExplorer { get; init; }
    internal Action CopyPath { get; init; }
    internal Action ToggleReadOnly { get; init; }
    internal Action<bool> LocateDocument { get; init; }
    internal Action GoToLine { get; init; }
    internal Action DocumentInformation { get; init; }
    internal Action<bool> SetDocumentFormat { get; init; }
    internal Action<bool> ExportHtmlAs { get; init; }
    internal Action Print { get; init; }
    internal Action ExportPdf { get; init; }
    internal Action ManageRecovery { get; init; }
    internal Action ManageVersions { get; init; }
    internal Action ManageImageCache { get; init; }
    internal Action ConfigurePreferences { get; init; }
    internal Action AddBookmark { get; init; }
    internal Action ManageBookmarks { get; init; }
    internal Action ManageNavigation { get; init; }
    internal Action<int> ApplyMarkdownCommand { get; init; }
    internal Action InsertImage { get; init; }
    internal Action<Exception> ReportError { get; init; }
    internal Action AfterCommand { get; init; }
}

internal sealed class ProductCommandDispatcher
{
    private readonly ProductCommandContext _actions;

    internal ProductCommandDispatcher(ProductCommandContext actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    internal bool TryDispatch(int command)
    {
        if (command < 2200 || command > 2299) return false;
        try
        {
            switch (command)
            {
                case 2200: _actions.NewDocument(); break;
                case 2201: _actions.SaveDocumentSet(command); break;
                case 2202: _actions.CloseDocumentSet(command); break;
                case 2203: _actions.CloseDocumentSet(command); break;
                case 2204: _actions.CloseDocumentSet(command); break;
                case 2205: _actions.ReopenClosed(); break;
                case 2206: _actions.MoveTab(command); break;
                case 2207: _actions.MoveTab(command); break;
                case 2208: _actions.TogglePinned(); break;
                case 2209: _actions.RenameDocument(); break;
                case 2210: _actions.RevealInExplorer(); break;
                case 2211: _actions.CopyPath(); break;
                case 2212: _actions.ToggleReadOnly(); break;
                case 2213: _actions.LocateDocument(false); break;
                case 2214: _actions.LocateDocument(true); break;
                case 2220: _actions.GoToLine(); break;
                case 2221: _actions.DocumentInformation(); break;
                case 2222: _actions.SetDocumentFormat(false); break;
                case 2223: _actions.SetDocumentFormat(true); break;
                case 2230: _actions.ExportHtmlAs(false); break;
                case 2231: _actions.ExportHtmlAs(true); break;
                case 2232: _actions.Print(); break;
                case 2233: _actions.ExportPdf(); break;
                case 2240: _actions.ManageRecovery(); break;
                case 2242: _actions.ManageVersions(); break;
                case 2243: _actions.ManageImageCache(); break;
                case 2250: _actions.ConfigurePreferences(); break;
                case 2268: _actions.InsertImage(); break;
                case 2280: _actions.AddBookmark(); break;
                case 2281: _actions.ManageBookmarks(); break;
                case 2282: _actions.ManageNavigation(); break;
                case 2260:
                case 2261:
                case 2262:
                case 2263:
                case 2264:
                case 2265:
                case 2266:
                case 2267: _actions.ApplyMarkdownCommand(command); break;
                default: return false;
            }
        }
        catch (Exception ex)
        {
            _actions.ReportError(ex);
        }
        _actions.AfterCommand();
        return true;
    }
}
