using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace MDViewerNative;

internal static unsafe partial class Program
{
    static readonly BookmarkStore Bookmarks = new();
    const int TIMER_AUTOSAVE = 13;
    static readonly HashSet<Guid> _autoSavePaused = new();

    static void ApplyPreferences()
    {
        Find.SetOptions(new(AppPreferences.Current.SearchCase, AppPreferences.Current.SearchWord, AppPreferences.Current.SearchRegex));
        foreach (var document in Tabs) document.EditHistory.Limit = AppPreferences.Current.UndoSteps;
        if (Editor != null)
        {
            _syncingEditor = true;
            try { Editor.SetInputPreferences(AppPreferences.Current.TabWidth, AppPreferences.Current.WordWrap); }
            finally { _syncingEditor = false; }
        }
        if (_hwnd != 0)
        {
            Externs.KillTimer(_hwnd, (nint)TIMER_AUTOSAVE);
            if (AppPreferences.Current.AutoSave)
                Externs.SetTimer(_hwnd, (nint)TIMER_AUTOSAVE, (uint)(AppPreferences.Current.AutoSaveSeconds * 1000), 0);
            DocumentImages.AnimationEnabled = AppPreferences.Current.AnimateImages && SystemAnimationEnabled();
            foreach (var document in Tabs) document.Doc.Relayout();
            Externs.InvalidateRect(_hwnd, 0, 0);
        }
    }

    const int TIMER_ANIMATION = 14;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint init);
    static bool SystemAnimationEnabled()
    {
        // SPI_GETCLIENTAREAANIMATION:辅助功能"在 Windows 中显示动画"关闭时静止显示首帧
        return !SystemParametersInfoW(0x1042, 0, out int enabled, 0) || enabled != 0;
    }

    // 动画帧推进(34):只有预览态且视口内存在动画图片时计时器才运行;每次 tick 只失效图片矩形
    static void ScheduleAnimation()
    {
        Externs.KillTimer(_hwnd, (nint)TIMER_ANIMATION);
        if (_editing || !DocumentImages.AnimationEnabled) return;
        int due = Doc.NextAnimationDueMs();
        if (due >= 0) Externs.SetTimer(_hwnd, (nint)TIMER_ANIMATION, (uint)Math.Clamp(due, 10, 1000), 0);
    }

    static void AdvanceAnimation()
    {
        Externs.KillTimer(_hwnd, (nint)TIMER_ANIMATION);
        if (_editing || !DocumentImages.AnimationEnabled) return;
        if (Doc.AdvanceAnimations()) InvalidateContent(_hwnd);
        ScheduleAnimation();
    }

    static void ConfigurePreferences()
    {
        if (HasComposition) return;
        var category = NativeForm.Show(_hwnd, "行为设置", "不包含美化主题设置。", null,
            new[] { "编辑行为", "保存与版本历史", "查找和最近文件", "恢复默认设置" }, false);
        if (category == null || category.Selected.Length == 0) return;
        var p = AppPreferences.Current;
        FormResult result;
        switch (category.Selected[0])
        {
            case 0:
                result = NativeForm.Show(_hwnd, "编辑行为", "撤销历史属于各自文档，改变换行显示不会丢失它。", new[] {
                    new FormField("缩进宽度（1–16）", p.TabWidth.ToString()),
                    new FormField("插入 Tab 而非空格", p.UseTabs ? "true" : "false", FormFieldKind.Check),
                    new FormField("编辑区自动换行", p.WordWrap ? "true" : "false", FormFieldKind.Check),
                    new FormField("每文档撤销步骤（10–2000）", p.UndoSteps.ToString()),
                    new FormField("新打开文档默认只读", p.DefaultReadOnly ? "true" : "false", FormFieldKind.Check),
                    new FormField("播放 GIF 动画（系统关闭动画时不播放）", p.AnimateImages ? "true" : "false", FormFieldKind.Check),
                    new FormField("大文档可按 Esc 取消的起始大小（千字符，0–20000）", p.CancelGraceKilochars.ToString())
                });
                if (result == null) return;
                if (!int.TryParse(result.Values[0], out int tabs) || !int.TryParse(result.Values[3], out int steps)
                    || !int.TryParse(result.Values[6], out int grace)) throw new ArgumentException("请输入有效整数。");
                p.TabWidth = tabs; p.UseTabs = result.Values[1] == "true"; p.WordWrap = result.Values[2] == "true"; p.UndoSteps = steps; p.DefaultReadOnly = result.Values[4] == "true";
                p.AnimateImages = result.Values[5] == "true";
                p.CancelGraceKilochars = grace;
                break;
            case 1:
                result = NativeForm.Show(_hwnd, "保存与版本历史", "自动保存默认关闭；仅保存已命名可写文档，发生冲突时暂停，不自动覆盖。", new[] {
                    new FormField("启用源文件自动保存", p.AutoSave ? "true" : "false", FormFieldKind.Check),
                    new FormField("自动保存间隔（秒，5–3600）", p.AutoSaveSeconds.ToString()),
                    new FormField("保留有上限的历史版本", p.VersionHistory ? "true" : "false", FormFieldKind.Check),
                    new FormField("每文档版本数（1–50）", p.VersionsPerFile.ToString()),
                    new FormField("全部历史最大 MiB（8–512）", p.HistoryMegabytes.ToString()),
                    new FormField("全部文档撤销历史上限（千字符，1024–262144）", p.UndoBudgetKilochars.ToString())
                });
                if (result == null) return;
                if (!int.TryParse(result.Values[1], out int seconds) || !int.TryParse(result.Values[3], out int versions)
                    || !int.TryParse(result.Values[4], out int megabytes) || !int.TryParse(result.Values[5], out int undoBudget)) throw new ArgumentException("请输入有效整数。");
                p.AutoSave = result.Values[0] == "true"; p.AutoSaveSeconds = seconds; p.VersionHistory = result.Values[2] == "true"; p.VersionsPerFile = versions; p.HistoryMegabytes = megabytes;
                p.UndoBudgetKilochars = undoBudget;
                // A lowered ceiling must take effect immediately, not only on the next edit.
                UndoBudget.Enforce(null);
                _autoSavePaused.Clear();
                break;
            case 2:
                result = NativeForm.Show(_hwnd, "查找和最近文件", "查找历史仅保存在本机配置目录。", new[] {
                    new FormField("记住查找内容", p.RememberSearch ? "true" : "false", FormFieldKind.Check),
                    new FormField("最近文件数量（5–50）", p.RecentFiles.ToString()),
                    new FormField("清空查找历史", "false", FormFieldKind.Check),
                    new FormField("清空最近文件列表", "false", FormFieldKind.Check)
                });
                if (result == null) return;
                if (!int.TryParse(result.Values[1], out int recent)) throw new ArgumentException("请输入有效整数。");
                p.RememberSearch = result.Values[0] == "true"; p.RecentFiles = recent;
                if (result.Values[2] == "true" || !p.RememberSearch) p.SearchHistory.Clear();
                if (result.Values[3] == "true") _recent.Clear();
                break;
            default:
                if (Externs.MessageBoxW(_hwnd, "恢复行为设置默认值？不删除文档、书签或恢复副本。", "恢复默认设置", 0x134) != 6) return;
                AppPreferences.Reset(); break;
        }
        AppPreferences.Save(); ApplyPreferences(); SaveConfig(); UpdateTitle();
    }

    static void AutoSaveDocuments()
    {
        if (!AppPreferences.Current.AutoSave || HasComposition || NativeForm.IsActive || _closingNormally) return;
        foreach (var document in Tabs.ToArray())
        {
            if (!document.Dirty || document.ReadOnly || document.Path.Length == 0 || _autoSavePaused.Contains(document.RecoveryId)) continue;
            try
            {
                string text = ReferenceEquals(document, Session) ? CurrentText() : document.Text;
                document.Save(text); ClearDraft(document);
                try { VersionArchive.CapturePrevious(document.Path); } catch (Exception ex) { AppLog.Error("archive autosave", ex); }
            }
            catch (Exception ex) { _autoSavePaused.Add(document.RecoveryId); AppLog.Error("autosave paused", ex); }
        }
        UpdateTitle(); UpdateScroll(); Externs.InvalidateRect(_hwnd, 0, 0);
    }

    static void AddBookmark()
    {
        if (!PrepareDocumentAction()) return;
        string title = NativeForm.Input(_hwnd, "添加书签", "书签名称", Session.Name);
        if (title == null) return;
        Bookmarks.Add(_path, title, Doc.CaptureReading(MathF.Max(0f, _offY)));
    }
    static void ManageBookmarks()
    {
        var items = Bookmarks.Items.ToArray();
        if (items.Length == 0) { Externs.MessageBoxW(_hwnd, "还没有书签。", "书签", 0); return; }
        var result = NativeForm.Show(_hwnd, "书签", "选择书签打开，或删除选中的书签。", null,
            items.Select(i => i.Title + " — " + i.Path).ToArray(), false, new[] { "打开", "删除", "取消" });
        if (result == null || result.Selected.Length == 0) return;
        var item = items[result.Selected[0]];
        if (result.Action == 1) { Bookmarks.Delete(item.Id); return; }
        if (!PrepareDocumentAction() || (_editing && !ToggleEdit())) return;
        var before = CaptureNavigation();
        if (RestoreNavigation(new(Guid.Empty, item.Path, item.Location))) RecordNavigation(before);
    }

    static void ManageNavigation()
    {
        var back = Navigation.BackEntries; var forward = Navigation.ForwardEntries;
        var labels = back.Select((p, i) => "后退 " + (back.Count - i) + " — " + p.Path)
            .Concat(forward.Select((p, i) => "前进 " + (forward.Count - i) + " — " + p.Path)).ToArray();
        if (labels.Length == 0) { Externs.MessageBoxW(_hwnd, "没有跳转历史。", "历史", 0); return; }
        var result = NativeForm.Show(_hwnd, "跳转历史", "失效位置可以删除，不会修改文档。", null, labels, false,
            new[] { "前往", "移除", "清空", "取消" });
        if (result == null) return;
        if (result.Action == 2) { Navigation.Clear(); return; }
        if (result.Selected.Length == 0) return;
        int index = result.Selected[0]; bool isBack = index < back.Count; int at = isBack ? index : index - back.Count;
        if (result.Action == 1) { Navigation.Remove(isBack, at); return; }
        if (!PrepareDocumentAction() || (_editing && !ToggleEdit())) return;
        Navigation.TraverseTo(isBack, at, CaptureNavigation(), RestoreNavigation);
    }

    static void ManageVersions()
    {
        if (_path.Length == 0) return;
        var versions = VersionArchive.List(_path);
        if (versions.Count == 0) { Externs.MessageBoxW(_hwnd, "没有历史版本。可在行为设置中启用有上限的版本保存。", "历史版本", 0); return; }
        var result = NativeForm.Show(_hwnd, "历史版本", "恢复只生成当前草稿；不会自动覆盖磁盘文件。", null,
            versions.Select(v => v.Version.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " — " + v.Version.Bytes.Length + " bytes").ToArray(), false,
            new[] { "恢复草稿", "导出", "删除", "取消" });
        if (result == null || result.Selected.Length == 0) return;
        var selected = versions[result.Selected[0]];
        if (result.Action == 2)
        {
            if (Externs.MessageBoxW(_hwnd, "删除这个历史版本？", "删除历史", 0x134) == 6) VersionArchive.Delete(selected.File);
            return;
        }
        string temp = Path.Combine(AppStorage.DirectoryPath, "version-read-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, selected.Version.Bytes);
            if (result.Action == 0)
            {
                if (!PrepareDocumentAction() || Session.ReadOnly) return;
                if (_dirty && Externs.MessageBoxW(_hwnd, "用历史版本替换当前草稿？源文件不会被写入。", "恢复版本", 0x134) != 6) return;
                var content = DocumentFile.Read(temp);
                Session.ReplaceText(content.Text); Session.SetFormat(content.Format); ScheduleDraft(); RefreshDocumentSurface();
            }
            else
            {
                string destination = NativeFileDialogs.Save(_hwnd, Path.GetFileName(_path));
                if (destination == null) return;
                var expected = AtomicFile.Observe(destination);
                if (!expected.Exists || DocumentDialogs.ConfirmOverwrite(_hwnd, destination)) AtomicFile.Write(destination, selected.Version.Bytes, expected);
            }
        }
        finally { File.Delete(temp); }
    }

    static void LocateDocument(bool recent)
    {
        string filter = NativeForm.Input(_hwnd, recent ? "最近文档" : "定位标签", "筛选名称或路径（留空显示全部）", "");
        if (filter == null) return;
        var snapshot = recent ? null : BuildTabInfo();
        var paths = recent ? _recent.ToArray() : snapshot.Select(t => t.FullPath).ToArray();
        var indices = Enumerable.Range(0, paths.Length).Where(i => (recent ? paths[i] : snapshot[i].Title + " " + snapshot[i].DisplayTitle + " " + paths[i]).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = NativeForm.Show(_hwnd, recent ? "最近文档" : "定位标签", "选择目标文档", null,
            indices.Select(i => recent ? paths[i] : snapshot[i].MenuLabel(usePath: true)).ToArray(), false);
        if (result == null || result.Selected.Length == 0 || result.Selected[0] < 0 || result.Selected[0] >= indices.Length) return;
        int selected = indices[result.Selected[0]];
        if (recent) LoadFile(paths[selected], true);
        else
        {
            int index = Tabs.IndexOfId(snapshot[selected].Id);
            if (index >= 0) SwitchTab(index);
        }
    }
}
