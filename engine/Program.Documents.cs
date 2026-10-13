using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using static MDViewerNative.NativeMenus;

namespace MDViewerNative;

internal static unsafe partial class Program
{
    static bool ApplyTextHistory(bool redo)
    {
        if (HasComposition || Session.ReadOnly) return false;
        if (!_editing && !ToggleEdit()) return false;
        _syncingEditor = true;
        bool changed;
        try { changed = EditSession.ApplyHistory(Session, redo); }
        finally { _syncingEditor = false; }
        if (changed)
        {
            if (Session.Dirty) ScheduleDraft(); else ClearDraft(Session);
            UpdateTitle(); Externs.InvalidateRect(_hEdit, 0, 0);
        }
        return changed;
    }

    static void RefreshDocumentSurface()
    {
        Find.Close(); StopSmoothScroll();
        if (_editing) { Externs.ShowWindow(_hEdit, 0); _editing = false; ToggleMainScrollbar(true); }
        if (_hEdit != 0) SyncEditor();
        Doc.OnWidth(ReadingWidth()); _offY = Snap(Math.Clamp(_offY, 0, MaxOff()));
        UpdateTitle(); UpdateScroll(); Externs.InvalidateRect(_hwnd, 0, 0);
    }

    static bool PrepareDocumentAction()
    {
        if (HasComposition) return false;
        if (_editing) EditSession.PreviewDraft(Session);
        return true;
    }

    static void NewDocument()
    {
        if (!PrepareDocumentAction()) return;
        if (_editing && !ToggleEdit()) return;
        if (!PlaceholderHidden) Tabs.NewDocument();   // U10：起始页上的空白占位直接拿来用，不多出一个空白标签
        _home = false;
        RefreshDocumentSurface(); ToggleEdit();
    }

    static bool SaveDocumentSet(Guid[] ids)
    {
        if (!PrepareDocumentAction()) return false;
        Guid active = Session.RecoveryId; bool editing = _editing;
        try
        {
            var result = DocumentBatch.Save(ids, Tabs.ById, document =>
            {
                int index = Tabs.IndexOf(document);
                if (index < 0) return false;
                if (index != ActiveTab) SwitchTab(index);
                return ReferenceEquals(Session, document) && SaveFile();
            });
            if (result.ReadOnlyDocument is { } blocked)
                Externs.MessageBoxW(_hwnd, "请先解除文档只读锁定：\n" + blocked.Name, "全部保存", 0x30);
            return result.Completed;
        }
        finally
        {
            int index = Tabs.IndexOfId(active);
            if (index >= 0 && index != ActiveTab) SwitchTab(index);
            if (index >= 0 && editing && !_editing && !Session.ReadOnly) ToggleEdit();
        }
    }

    static bool ResolveDocumentChanges(Guid[] ids)
    {
        if (!PrepareDocumentAction()) return false;
        while (true)
        {
            // Eligibility is policy, not window code: DocumentBatch owns identity resolution,
            // de-duplication and the disposed/clean checks.
            var pending = DocumentBatch.Pending(ids, Tabs.ById).ToArray();
            if (pending.Length == 0) return true;
            var result = NativeForm.Show(_hwnd, "处理未保存文档", "选择文档后保存或放弃；取消不会关闭任何尚未处理的文档。", null,
                pending.Select(t => t.Name + (t.ReadOnly ? " [只读]" : "") + " — " + t.Path).ToArray(), true,
                new[] { "保存所选", "放弃所选", "取消" });
            if (result == null) return false;
            if (result.Selected.Length == 0) continue;
            var selected = result.Selected.Where(i => i >= 0 && i < pending.Length).Select(i => pending[i]).ToArray();
            if (result.Action == 0)
            {
                if (!SaveDocumentSet(selected.Select(t => t.RecoveryId).ToArray())) return false;
            }
            else
            {
                if (Externs.MessageBoxW(_hwnd, "将放弃选中的 " + selected.Length + " 份草稿，是否继续？", "确认放弃", 0x134) != 6) continue;
                DocumentBatch.Discard(selected.Select(t => t.RecoveryId), Tabs.ById, document =>
                {
                    document.DiscardChanges(); ClearDraft(document);
                    if (ReferenceEquals(document, Session) && _hEdit != 0) SyncEditor();
                });
                UpdateTitle();
            }
        }
    }

    static void CloseDocumentSet(Guid[] ids)
    {
        if (!ResolveDocumentChanges(ids)) return;
        foreach (int i in Tabs.IndicesOfIds(ids).ToArray()) CloseTabAt(i, true);
    }

    static void ReopenClosed()
    {
        if (HasComposition || Tabs.LastClosed is not { } closed) return;
        if (!LoadFile(closed.Path, true)) return;
        Tabs.ConsumeClosed(); SetOffset(closed.Offset);
    }

    static void RenameDocument()
    {
        if (!PrepareDocumentAction() || _path.Length == 0 || Session.ReadOnly) return;
        Guid identity = Session.RecoveryId; string oldPath = Session.Path;
        string name = NativeForm.Input(_hwnd, "重命名文件", "新文件名（不移动目录、不覆盖已有文件）", Path.GetFileName(oldPath));
        if (name == null || name.Length == 0 || Session.RecoveryId != identity) return;
        if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("文件名无效。");
        string destination = Path.Combine(Path.GetDirectoryName(oldPath), name);
        if (string.Equals(oldPath, destination, StringComparison.Ordinal)) return;
        Session.Rename(destination); Navigation.Rename(identity, destination); Bookmarks.RenamePath(oldPath, destination);
        RecentList.Rename(_recent, oldPath, destination, AppPreferences.Current.RecentFiles); SaveConfig(); UpdateTitle();
    }

    static void SetDocumentFormat(bool reopen)
    {
        if (!PrepareDocumentAction()) return;
        Guid identity = Session.RecoveryId;
        string[] encodings = { "UTF-8", "GBK", "UTF-16 LE", "UTF-16 BE" };
        TextEncoding[] kinds = { TextEncoding.Utf8, TextEncoding.Gbk, TextEncoding.Utf16LittleEndian, TextEncoding.Utf16BigEndian };
        var result = NativeForm.Show(_hwnd, reopen ? "指定编码重新打开" : "编码与换行", reopen ? "重新解码会读取磁盘；未保存修改仍需明确处理。" : "更改只影响当前草稿格式，保存后才写入磁盘。",
            new[] {
                new FormField("编码", encodings[Math.Max(0, Array.IndexOf(kinds, Session.Encoding))], FormFieldKind.Choice, encodings),
                new FormField("写入 BOM", Session.Bom ? "true" : "false", FormFieldKind.Check),
                new FormField("换行", Session.LineEnding == "\r\n" ? "CRLF" : Session.LineEnding == "\r" ? "CR" : "LF", FormFieldKind.Choice, new[] { "LF", "CRLF", "CR" })
            });
        if (result == null || Session.RecoveryId != identity) return;
        TextEncoding encoding = kinds[Array.IndexOf(encodings, result.Values[0])];
        if (reopen)
        {
            if (_path.Length == 0 || GuardDirty() < 0) return;
            Session.Load(_path, DocumentFile.Read(_path, encoding)); ClearDraft(Session); RefreshDocumentSurface();
        }
        else
        {
            Session.SetFormat(new(encoding, result.Values[1] == "true", result.Values[2] == "CRLF" ? "\r\n" : result.Values[2] == "CR" ? "\r" : "\n"));
            ScheduleDraft(); UpdateTitle();
        }
    }

    static void GoToLine()
    {
        if (HasComposition || Session.ReadOnly) return;
        if (!_editing && !ToggleEdit()) return;
        string answer = NativeForm.Input(_hwnd, "跳转到行", "源文本行号（从 1 开始）", "1");
        if (!int.TryParse(answer, out int line) || line < 1) return;
        string text = Externs.WindowText(_hEdit); int offset = 0;
        for (int i = 1; i < line && offset < text.Length; i++) { int next = text.IndexOf('\n', offset); offset = next < 0 ? text.Length : next + 1; }
        Externs.SendMessageW(_hEdit, 0x00B1, (nint)offset, (nint)offset); Externs.SendMessageW(_hEdit, 0x00B7, 0, 0); Externs.SetFocus(_hEdit);
    }

    static void DocumentInformation()
    {
        string text = CurrentText(); int lines = text.Length == 0 ? 1 : text.Count(c => c == '\n') + 1;
        int characters = text.EnumerateRunes().Count();
        int start = 0, end = 0;
        if (_editing) (start, end) = Externs.EditSelection(_hEdit);
        string message = "路径：" + (_path.Length == 0 ? "未命名" : _path) + "\n编码：" + Session.Format.Description +
            "\n换行：" + (Session.LineEnding == "\r\n" ? "CRLF" : Session.LineEnding == "\r" ? "CR" : "LF") +
            "\n行数：" + lines + "\nUnicode 字符数：" + characters + "\n选中 UTF-16 单元：" + Math.Abs(end - start) +
            "\n状态：" + (_dirty ? "未保存" : "已保存") + (Session.ReadOnly ? "，只读" : "");
        Externs.MessageBoxW(_hwnd, message, "文档信息", 0);
    }

    static void ApplyMarkdownCommand(int command)
    {
        if (HasComposition || Session.ReadOnly) return;
        if (!_editing && !ToggleEdit()) return;
        var (a, b) = Externs.EditSelection(_hEdit); string text = Externs.WindowText(_hEdit);
        string selected = text.Substring(a, b - a), inserted;
        if (command == 2262)
        {
            string destination = NativeForm.Input(_hwnd, "插入链接", "链接地址", "https://");
            if (destination == null) return;
            inserted = "[" + (selected.Length == 0 ? "链接文字" : selected) + "](" + destination.Replace(")", "%29") + ")";
        }
        else inserted = command switch
        {
            2260 => "**" + selected + "**",
            2261 => "*" + selected + "*",
            2263 => "`" + selected + "`",
            2264 => "```\r\n" + selected + "\r\n```",
            2265 => "# " + selected,
            2266 => "> " + selected.Replace("\n", "\n> "),
            2267 => "- " + selected.Replace("\n", "\n- "),
            _ => selected
        };
        fixed (char* value = inserted) Externs.SendMessageW(_hEdit, 0x00C2, 1, (nint)value);
        MarkDirty(); UpdateTitle(); Externs.SetFocus(_hEdit);
    }

    static void ExportHtmlAs(bool offline)
    {
        if (HasComposition) return;
        var document = Session; string text = CurrentText();
        string suggested = document.Path.Length == 0 ? "未命名.html" : Path.ChangeExtension(document.Path, ".html");
        string path = NativeFileDialogs.SaveHtml(_hwnd, suggested);
        if (path == null) return;
        try
        {
            // Same bounded work as opening: the draft is re-parsed to build the HTML.
            using var cancellable = DocumentWork.Begin(EscapeRequested);
            var export = DocumentExport.Prepare(document, text, RenderResources.DarkMode, path, offline);
            if (export.TryWrite(p => DocumentDialogs.ConfirmOverwrite(_hwnd, p)))
                Externs.MessageBoxW(_hwnd, "已导出：\n" + export.Path, "导出 HTML", 0);
        }
        catch (DocumentCancelledException)
        {
            AppLog.Write("export HTML as: cancelled by user");
            _ = Externs.MessageBoxW(_hwnd, "已取消导出。\n\n未写入任何文件。", "导出 HTML", 0x40);
        }
    }

    // Printing and PDF export re-render every page, so both honour Esc like opening and
    // exporting do. AbortDoc in the printer's finally block means a cancelled job leaves
    // no partial output on the printer or on disk.
    static bool RunCancellablePrint(Func<bool> action, string caption)
    {
        try
        {
            using var cancellable = DocumentWork.Begin(EscapeRequested);
            return action();
        }
        catch (DocumentCancelledException)
        {
            AppLog.Write(caption + ": cancelled by user");
            _ = Externs.MessageBoxW(_hwnd, "已取消。\n\n打印任务已中止，未生成输出。", caption, 0x40);
            return false;
        }
    }

    static void ManageImageCache()
    {
        string cacheDir = Path.Combine(AppStorage.DirectoryPath, "image-cache");
        var disk = RemoteImages.CacheUsage(cacheDir);
        string Mib(ulong pixels) => (pixels * 4 / 1048576.0).ToString("0.0");
        var items = Tabs.Select(t => { var u = t.Doc.ImageCacheUsage; return t.Name + " — 已解码 " + u.Count + " 项，" + Mib(u.Pixels) + " MiB" + (ReferenceEquals(t, Session) ? "（当前标签，释放后重绘时会重新解码）" : ""); }).ToArray();
        // Report managed text alongside bitmaps so the budget picture is honest: undo deltas
        // are evictable under a ceiling, source snapshots are not.
        UndoBudget.ReportSources(Tabs.Sum(t => t.SourceSnapshotCharacters));
        string MibChars(long characters) => (characters * 2 / 1048576.0).ToString("0.0");
        string description = "解码位图：全部标签 " + Mib(DocumentImages.GlobalPixels) + " / " + Mib(DocumentImages.GlobalLimit) + " MiB，单文档上限 " + Mib(ImageLimits.MaxPixels)
            + " MiB。网络图片磁盘缓存：" + disk.Files + " 个文件，" + (disk.Bytes / 1048576.0).ToString("0.0") + " / " + (RemoteImages.CacheBytes / 1048576) + " MiB。"
            + "撤销历史：" + MibChars(UndoBudget.Retained) + " / " + MibChars(UndoBudget.Limit) + " MiB（超出按最近使用淘汰）；源文快照 "
            + MibChars(UndoBudget.SourceCharacters) + " MiB（文档打开期间不可淘汰）。布局缓存条目 " + LayoutCacheBudget.Retained + " / " + LayoutCacheBudget.Limit + "。";
        var result = NativeForm.Show(_hwnd, "图片缓存", description, null, items, true, new[] { "释放所选标签的位图", "清空网络缓存", "关闭" });
        if (result == null) return;
        if (result.Action == 0)
        {
            foreach (int index in result.Selected) if (index >= 0 && index < Tabs.Count) Tabs[index].Doc.ReleaseRenderCache();
            Doc.OnWidth(ReadingWidth()); _offY = Snap(Math.Clamp(_offY, 0, MaxOff())); UpdateScroll(); Externs.InvalidateRect(_hwnd, 0, 0);
        }
        else if (result.Action == 1)
        {
            if (_imageTask != null) { Externs.MessageBoxW(_hwnd, "有网络图片正在加载，请等待完成或取消后再清空。", "图片缓存", 0x30); return; }
            Externs.MessageBoxW(_hwnd, "已删除 " + RemoteImages.ClearCache(cacheDir) + " 个缓存文件。", "图片缓存", 0);
        }
    }

    static void ManageRecovery()
    {
        var snapshots = Drafts.ReadAll();
        if (snapshots.Count == 0) { Externs.MessageBoxW(_hwnd, "没有可恢复副本。", "恢复副本", 0); return; }
        var result = NativeForm.Show(_hwnd, "恢复副本管理", "选择副本进行恢复、导出或删除。未选择的副本保留。", null,
            snapshots.Select(s => (s.Path.Length == 0 ? "未命名" : Path.GetFileName(s.Path)) + " — " + s.Text.Length + " 字符").ToArray(), true,
            new[] { "恢复所选", "导出所选", "删除所选", "取消" });
        if (result == null) return;
        foreach (int index in result.Selected)
        {
            if (index < 0 || index >= snapshots.Count) continue;
            var snapshot = snapshots[index];
            if (result.Action == 0)
            {
                if (!PrepareDocumentAction()) return;
                Tabs.RestoreDraft(snapshot); RefreshDocumentSurface();
            }
            else if (result.Action == 1)
            {
                string path = NativeFileDialogs.Save(_hwnd, "恢复草稿.md");
                if (path == null) return;
                var version = AtomicFile.Observe(path);
                if (version.Exists && !DocumentDialogs.ConfirmOverwrite(_hwnd, path)) continue;
                AtomicFile.Write(path, new System.Text.UTF8Encoding(false, true).GetBytes(snapshot.Text), version);
            }
            else if (Externs.MessageBoxW(_hwnd, "删除这一份恢复副本？源文件不会被删除。\n" + snapshot.Path, "删除恢复副本", 0x134) == 6)
                Drafts.Remove(snapshot.Id);
        }
    }

    static void AddProductMenus(nint parent)
    {
        void AddGroup(nint owner, string label, CmdFace face, params (string Label, CmdFace Face)[] children)
        {
            var commands = Commands.OnFace(face).ToArray();
            if (commands.Length == 0 && children.Length == 0) return;
            nint menu = CreatePopupMenu(); if (menu == 0) return;
            var state = CmdStateNow();
            foreach (var command in commands)
                AppendMenuW(menu, (command.IsEnabled(state) ? 0u : 1u) | (command.IsChecked(state) ? 8u : 0u), (nint)command.Id, command.MenuText);
            foreach (var child in children) AddGroup(menu, child.Label, child.Face);
            AppendMenuW(owner, 0x10, menu, label);
            DestroyMenuAfterTrack(menu);
        }
        AddGroup(parent, "文档操作", CmdFace.Documents,
            ("标签", CmdFace.Tabs), ("书签", CmdFace.ReadingTools), ("导出与打印", CmdFace.Export));
        AddGroup(parent, "编辑工具", CmdFace.EditTools);
        AddGroup(parent, "设置与维护", CmdFace.Preferences, ("恢复、历史与缓存", CmdFace.Maintenance));
    }

    // 任务清单(28):预览态点击复选框改写源码;映射不唯一时拒绝并提示,不猜测
    static void CopySelectionHtml()
    {
        if (_editing) return;
        var rich = Doc.CopyRich();
        if (rich.Text == null) return;
        if (!Clipboard.SetTextAndHtml(rich.Text, rich.Html, _hwnd))
            Externs.MessageBoxW(_hwnd, "剪贴板暂不可用，请稍后重试。", "复制为 HTML", 0x30);
    }

    static bool TryToggleTask(float mx, float myDoc)
    {
        if (HasComposition || _editing || Session.ReadOnly) return false;
        int ordinal = Doc.TaskOrdinalAt(mx, myDoc);
        if (ordinal < 0) return false;
        string updated = TaskListSource.Toggle(Session.Text, ordinal, Doc.TaskCount);
        if (updated == null)
        {
            Externs.MessageBoxW(_hwnd, "无法唯一确定该任务项在源码中的位置，请切换到编辑模式修改。", "任务清单", 0x30);
            return true;
        }
        float keep = _offY;
        Session.ReplaceText(updated); ScheduleDraft(); SyncEditor();
        Doc.OnWidth(ReadingWidth()); _offY = Snap(Math.Clamp(keep, 0, MaxOff()));
        UpdateTitle(); UpdateScroll(); InvalidateToolbar(_hwnd); Externs.InvalidateRect(_hwnd, 0, 0);
        return true;
    }

    static ProductCommandDispatcher _productCommands;
    static ProductCommandDispatcher ProductCommands => _productCommands ??= new(new ProductCommandContext
    {
        NewDocument = NewDocument,
        SaveDocumentSet = command => SaveDocumentSet(Tabs.Select(t => t.RecoveryId).ToArray()),
        CloseDocumentSet = command => CloseDocumentSet(command switch
        {
            2202 => Tabs.Where(t => !ReferenceEquals(t, Session) && !t.Pinned).Select(t => t.RecoveryId).ToArray(),
            2203 => Tabs.Skip(ActiveTab + 1).Where(t => !t.Pinned).Select(t => t.RecoveryId).ToArray(),
            _ => Tabs.Where(t => !t.Pinned).Select(t => t.RecoveryId).ToArray()
        }),
        ReopenClosed = ReopenClosed,
        MoveTab = command => { Tabs.Move(ActiveTab, command == 2206 ? Math.Max(0, ActiveTab - 1) : Math.Min(Tabs.Count - 1, ActiveTab + 1)); UpdateTitle(); },
        TogglePinned = () => { Session.Pinned = !Session.Pinned; UpdateTitle(); },
        RenameDocument = RenameDocument,
        RevealInExplorer = () => Externs.ShellExecuteW(_hwnd, "open", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + _path + "\"", null, 1),
        CopyPath = () => { if (_path.Length > 0 && !Clipboard.SetText(_path, _hwnd)) throw new IOException("剪贴板忙。"); },
        ToggleReadOnly = () => { if (!PrepareDocumentAction()) return; if (_editing && !ToggleEdit()) return; Session.ReadOnly = !Session.ReadOnly; if (_hEdit != 0) Externs.SendMessageW(_hEdit, 0x00CF, Session.ReadOnly ? 1 : 0, 0); UpdateTitle(); },
        LocateDocument = LocateDocument,
        GoToLine = GoToLine,
        DocumentInformation = DocumentInformation,
        SetDocumentFormat = SetDocumentFormat,
        ExportHtmlAs = ExportHtmlAs,
        Print = () => { if (PrepareDocumentAction()) RunCancellablePrint(() => DocumentPrinter.Print(_hwnd, Doc, Session.Name), "打印"); },
        ExportPdf = ExportPdfCommand,
        ManageRecovery = ManageRecovery,
        ManageVersions = ManageVersions,
        ManageImageCache = ManageImageCache,
        ConfigurePreferences = ConfigurePreferences,
        AddBookmark = AddBookmark,
        ManageBookmarks = ManageBookmarks,
        ManageNavigation = ManageNavigation,
        ApplyMarkdownCommand = ApplyMarkdownCommand,
        InsertImage = InsertImageCommand,
        ReportError = ex => { AppLog.Error("document operation", ex); Externs.MessageBoxW(_hwnd, ex.Message, "操作未完成", 0x30); },
        AfterCommand = () => InvalidateToolbar(_hwnd),
    });

    static void ExportPdfCommand()
    {
        if (!PrepareDocumentAction()) return;
        string pdf = NativeFileDialogs.SavePdf(_hwnd, _path.Length == 0 ? "未命名.pdf" : Path.ChangeExtension(_path, ".pdf"));
        if (pdf != null && RunCancellablePrint(() => DocumentPrinter.Print(_hwnd, Doc, Session.Name, pdf), "导出 PDF"))
            Externs.MessageBoxW(_hwnd, "PDF 已生成：\n" + pdf, "导出 PDF", 0);
    }

    static void InsertImageCommand()
    {
        string image = NativeFileDialogs.OpenImage(_hwnd);
        if (image != null) DropImage(image);
    }

    static bool HandleProductCommand(int command)
    {
        if (command < 2200 || command > 2299) return false;
        if (HasComposition) return true;
        return ProductCommands.TryDispatch(command);
    }
}
