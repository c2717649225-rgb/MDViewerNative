// 窗口宿主:消息循环、滚动、文件打开/拖放、轻量编辑模式(EDIT 控件)、保存与脏守卫。
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static MDViewerNative.NativeMenus;

namespace MDViewerNative;

internal static unsafe partial class Program
{
    static IntPtr _hwnd;
    static float _scale = 1f;

    static readonly AppState State = new();
    static readonly DocumentTabs Tabs = new();
    static readonly ReadingMemory Reading = new();
    static readonly NavigationHistory Navigation = new();
    static readonly ReturnHint ReturnPrompt = new();
    const string BackNote = "Alt+← 返回";   // U5c：返回书签出现时页脚的临时提示   // 「返回原阅读处」标签状态(只存值)
    static readonly ZenChrome Zen = new();   // 阶段 B 显式沉浸:开关持久化(zen=1),隐藏只停绘工具栏
    static bool _zenPainted;   // 工具栏带最近一次按“隐藏”绘制
    static bool ZenHidden => Zen.Hides(_editing, Toolbar.HasFocus, Find.Visible);
    static bool _handoffDeferred { get => State.HandoffDeferred; set => State.HandoffDeferred = value; }
    static bool _restoringStartup { get => State.RestoringStartup; set => State.RestoringStartup = value; }
    static readonly ToolbarView Toolbar = new();
    static nint _toolbarReturnFocus;
    static int _lastMoveLParam = int.MinValue;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern nint GetMessageExtraInfo();

    static readonly ImageWindow ImagePreview = new();
    static readonly InstanceHandoff Handoff = new();
    static readonly DiskChangeMonitor DiskMonitor = new();
    const int TIMER_DISK = 6;
    static bool _diskCheckAgain { get => State.DiskCheckAgain; set => State.DiskCheckAgain = value; }

    static void CheckDiskChange()
    {
        if (DiskMonitor.Pending) { _diskCheckAgain = true; return; }
        if (DiskMonitor.Start(Session))
            _ = Externs.SetTimer(_hwnd, (IntPtr)TIMER_DISK, 150, IntPtr.Zero);
    }

    static void RefreshFile()
    {
        if (HasComposition) return;
        var previous = Session;
        try
        {
            if (!Tabs.TryRefresh(() => Externs.MessageBoxW(_hwnd,
                "刷新将放弃「" + previous.Name + "」的未保存修改。\n\n要保留修改，请选择“否”并先保存或另存。是否放弃修改并读取磁盘版本？",
                "从磁盘刷新", 0x134 /*YESNO, warning, default No*/) == 6)) return;
        }
        catch (Exception ex)
        {
            AppLog.Error("refresh document", ex);
            string reason = ex is FileConflictException ? "确认期间磁盘文件再次变化，请检查后重新刷新。" : ex.Message;
            _ = Externs.MessageBoxW(_hwnd, "刷新失败，当前内容和草稿已保留。\n" + reason, "从磁盘刷新", 0x30);
            return;
        }
        StopSmoothScroll();
        Find.Close();
        ClearDraft(previous); // Remove recovery only after the new document was committed.
        SyncEditor();
        Doc.OnWidth(ReadingWidth());
        _offY = Snap(Math.Clamp(_offY, 0f, MaxOff()));
        UpdateTitle();
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        CheckDiskChange();
    }
    static EditorControl Editor;
    static EditorSession EditSession;
    static nint _hEdit => Editor?.Handle ?? 0;
    static readonly FindBar Find = new(new FindHost());
    static CommandDispatcher _accelerators;
    static CommandDispatcher Accelerators => _accelerators ??= new(new CommandDispatchContext
    {
        Save = () => _ = SaveFile(),
        ToggleEdit = () => ToggleEdit(),
        Open = () => OpenFileDlgNative(),
        ToggleTheme = ToggleTheme,
        Find = () => Find.Open(),
        ToggleToc = ToggleToc,
        Replace = () => Find.Open(replace: true),
        CloseTab = CloseTab,
        NextTab = () => StepTab(1),   // U10：desk 下起始页也在轮换里
        PreviousTab = () => StepTab(-1),
        ExportHtml = DoExportHtml,
        SaveAs = () => _ = DoSaveAs(),
        ZoomIn = () => ZoomFont(_bodyFont + 1, _hwnd),
        ZoomOut = () => ZoomFont(_bodyFont - 1, _hwnd),
        ResetZoom = () => ZoomFont(RenderResources.Reading.DefaultBodySize, _hwnd),   // U3：desk 默认 17
    });

    sealed class FindHost : IFindHost
    {
        public IntPtr Owner => _hwnd;
        public IntPtr Editor => _hEdit;
        public bool Editing => _editing;
        public float Scale => _scale;
        public int ViewWidth => _viewW;
        public int ViewHeight => _viewH;
        public float Offset => _offY;
        public float ContentTop => Program.ContentTop;
        public float BarTop => ToolH + EditBarHeight;
        public DocView Document => Doc;
        public void ScrollTo(float offset) => SetOffset(offset, allowTopInset: true);
        public void RevealHit(float offset, bool remember) => RevealFindHit(offset, remember);
        public void HitShown() => RestartInkCircle();
        public void TextChanged() => OnTextChanged();
        public void LayoutChanged() => LayoutControls();
    }
    static int ActiveTab => Tabs.ActiveIndex;
    static DocumentSession Session => Tabs.Current;
    static DocView Doc => Session.Doc;
    static string _path => Session.Path;
    static string _name => Session.Name;
    static bool _dirty => Session.Dirty;
    static float _offY { get => Session.ScrollOffset; set => Session.ScrollOffset = value; }
    static int _viewH = 600, _viewW = 1100;
    static readonly PointerSession _pointer = new();
    static bool _editing { get => State.Editing; set => State.Editing = value; }

    static void SwitchTab(int idx)
    {
        if (HasComposition) return;
        if (HomeShown && idx == ActiveTab) { LeaveHome(); return; }   // U10：从起始页回到藏在后面的当前文档
        if (!Tabs.TryActivate(idx, () =>
        {
            if (_editing && !ToggleEdit()) return false;
            Find.Close();
            StopSmoothScroll();
            return true;
        })) return;
        _home = false;   // U10
        if (State.Appearance.Desk) { HideReturnHint(); WakeTitlebar(); HideSelectionBubble(); CloseJuan(false); }   // U5c：切换标签清除返回书签；U6c：顶栏恢复；U7：收起气泡；U8：关卷目
        CheckDiskChange();
        Doc.OnWidth(ReadingWidth());   // 非活动标签的 TotalH 停留在旧宽度:切回后按当前宽度重算
        _offY = Snap(MathF.Min(Session.ScrollOffset, MaxOff()));   // 跨 DPI 恢复重对齐 + 钳到新标签上限(切到更短文档防越底空白帧)
        // The shared EDIT is a presentation of the newly active session.
        if (_hEdit != IntPtr.Zero && !_editing) SyncEditor();
        UpdateTitle();
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        if (!_editing) _ = Externs.SetFocus(_hwnd);
    }

    static void CloseTab()
    {
        CloseTabAt(ActiveTab);   // Ctrl+W:关当前标签
    }

    // 关闭指定标签(标签条 × / Ctrl+W)。活动标签走 GuardDirty;非活动脏标签走 ConfirmDiscardInactive;
    // 关闭活动标签之前的标签时,同一文档索引左移,_offY/EDIT 均无需变动。
    // 非活动标签也可能持有源码编辑或图片产生的草稿:切换标签不等于保存或放弃。
    // 关闭/退出窗口涉及这类标签时必须确认(无保存选项:保存非活动标签需先切换),防静默丢内容。
    static bool ConfirmDiscardInactive(int idx)
    {
        if (!Tabs[idx].Dirty || idx == ActiveTab) return true;
        return Externs.MessageBoxW(_hwnd, "标签「" + Tabs[idx].Name + "」有未保存的修改,关闭将丢失。仍要关闭?", "MDViewerNative", 0x134 /*warning, YESNO, default No*/) == 6 /*IDYES*/;
    }

    static void CloseTabAt(int idx, bool approved = false)
    {
        if (HasComposition) return;
        if (idx < 0 || idx >= Tabs.Count) return;
        if (!approved && idx != ActiveTab && Tabs[idx].Dirty)
        {
            CloseDocumentSet(new[] { Tabs[idx].RecoveryId }); return;
        }
        var closedSession = Tabs[idx];
        Reading.Remember(closedSession);
        bool contentSwitch = idx == ActiveTab;
        if (!Tabs.TryClose(idx, closing =>
        {
            if (!contentSwitch) return approved || ConfirmDiscardInactive(idx);
            if (!approved && GuardDirty() < 0) return false;
            Reading.Remember(closing);
            if (_editing)
            {
                _ = Externs.ShowWindow(_hEdit, 0);
                _editing = false;
                ToggleMainScrollbar(true);
            }
            Find.Close();
            StopSmoothScroll();
            return true;
        })) return;
        ClearDraft(closedSession);
        if (contentSwitch) CheckDiskChange();
        if (contentSwitch && _hEdit != IntPtr.Zero) SyncEditor();
        Doc.OnWidth(ReadingWidth());   // 同 SwitchTab:内容切换后重算宽度
        if (contentSwitch) _offY = Snap(MathF.Min(Session.ScrollOffset, MaxOff()));   // OnWidth 重排后再钳(TotalH 变小时防越底空白)
        UpdateTitle();
        UpdateScroll();
        HomeIfEmpty();   // U10：关掉最后一个文档，停在起始页
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
    }

    const int TIMER_IMAGE = 9;
    static RemoteImages _remoteImages;
    static System.Threading.Tasks.Task<string> _imageTask;
    static System.Threading.CancellationTokenSource _imageCancel;
    static DocumentSession _imageSession;
    static string _imageSource, _imageText;
    static long _imageRevision;

    static void FinishImageLoad()
    {
        if (_imageTask == null || !_imageTask.IsCompleted) return;
        _ = Externs.KillTimer(_hwnd, (IntPtr)TIMER_IMAGE);
        var task = _imageTask;
        _imageTask = null;
        bool cancelled = _imageCancel.IsCancellationRequested;
        _imageCancel.Dispose();
        _imageCancel = null;
        try
        {
            string path = task.GetAwaiter().GetResult();
            if (cancelled || !ReferenceEquals(Session, _imageSession) || _imageSession.IsDisposed || _imageRevision != Session.DiskRevision || _imageText != Session.Text || _editing) return;
            Doc.ApproveImage(_imageSource, path);
            _offY = Snap(Math.Clamp(_offY, 0f, MaxOff()));
            UpdateScroll();
            _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
            if (Doc.GetImageBmp(_imageSource) == 0) Externs.MessageBoxW(_hwnd, Doc.ImageMessage(_imageSource), "图片加载失败", 0x30);
            else if (Doc.IsInlineImage(_imageSource)) OpenLightbox(_imageSource);
        }
        catch (Exception ex)
        {
            if (!cancelled && ReferenceEquals(Session, _imageSession) && !_imageSession.IsDisposed)
                Externs.MessageBoxW(_hwnd, ex is OperationCanceledException ? "图片加载超时，请重试。" : ex.Message, "图片加载失败", 0x30);
        }
        finally { _imageSession = null; _imageText = null; _imageSource = null; UpdateTitle(); }
    }

    static void OpenLightbox(string source)
    {
        if (_imageTask != null) { _imageCancel.Cancel(); return; }
        var resolved = ImageSource.Resolve(source, Doc.DocumentDirectory);
        if (Doc.GetImageBmp(source) == 0 && resolved.Url != null)
        {
            _remoteImages ??= new(Path.Combine(AppStorage.DirectoryPath, "image-cache"));
            _imageSession = Session;
            _imageRevision = Session.DiskRevision;
            _imageSource = source;
            _imageText = Session.Text;
            _imageCancel = new();
            _imageTask = _remoteImages.FetchAsync(resolved.Url, _imageCancel.Token);
            _ = Externs.SetTimer(_hwnd, (IntPtr)TIMER_IMAGE, 100, IntPtr.Zero);
            UpdateTitle();
            return;
        }
        if (Doc.GetImageBmp(source) == 0) Doc.LoadImageForViewing(source);
        var (width, height) = Doc.GetImageDims(source);
        if (ImagePreview.Show(_hwnd, Doc.GetImageBmp(source), width, height, _lbHintsLeft > 0))
        {
            if (_lbHintsLeft > 0) { _lbHintsLeft--; SaveConfig(); }
        }
        else Externs.MessageBoxW(_hwnd, Doc.ImageMessage(source), "查看图片", 0);
    }

    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const int WS_VSCROLL = 0x00200000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    const int ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x40, ES_NOHIDESEL = 0x100;
    const int SB_HORZ = 0, SB_VERT = 1;
    const int SIF_RANGE = 1, SIF_PAGE = 2, SIF_POS = 4, SIF_TRACKPOS = 16, SIF_ALL = 23;
    const int MB_YESNOCANCEL = 3, IDYES = 6, IDNO = 7, IDCANCEL = 2;
    const int EN_CHANGE = 0x300;
    const int TIMER_AUTOSCROLL = 1;
    const int TIMER_SMOOTH = 4;
    static readonly SmoothScroll _smooth = new();   // 滚轮缓出状态机:目标与步进在 SmoothScroll,定时器与重绘留在宿主
    static float ContentTop => ToolH + EditBarHeight + (_editing ? Find.Height : 0f);   // U9a：desk 编辑时顶栏下先是编辑条
    static float InteractionTop => ToolH + EditBarHeight + Find.Height;
    // D3:目录(与标尺)上沿;只有查找覆盖层与目录水平相交(窄窗全宽条)时才从其下缘开始。绘制、裁剪、命中共用
    static float TocTop => OverlayGeometry.TocTop(ContentTop, Find.Box, Doc.TocWidth);
    static bool OverToc(float x, float y) => !_editing && Doc.TocVisible && x >= 0f && x < Doc.TocWidth && y >= TocTop;
    static bool OverRuler(float x, float y) => !_editing && Doc.RulerVisible && x >= 0f && x < OverlayGeometry.RulerWidth && y >= TocTop;   // D5:不从 OverBody 扣除,从左留白拖选不受影响
    static bool _swallowDblClk;   // D2:收起临时目录的那一击若被系统升级为双击,第二击同样吞掉
    static bool OverBody(float x, float y) => y >= InteractionTop && !OverToc(x, y) && !OverFind(x, y);
    static bool OverFind(float x, float y) => Find.Box.Contains(x, y);   // §5.2:面板矩形含控件间空隙,不穿透到正文
    static float ContentHeight => WindowGeometry.ContentHeight(_viewH / _scale, ContentTop);
    static float ToolH => RenderResources.ToolH;        // 工具栏高(DIP)

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu")] static extern int TrackPopupMenuOwner(IntPtr m, uint f, int x, int y, int r, IntPtr h, ref RECT ownerRect);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct ACCEL { public byte fVirt; public ushort key; public ushort cmd; }
    [DllImport("user32.dll", EntryPoint = "CreateAcceleratorTableW")]
    static extern IntPtr CreateAcceleratorTable(IntPtr accel, int count);
    [DllImport("user32.dll")] static extern bool DestroyAcceleratorTable(nint table);
    [DllImport("user32.dll", EntryPoint = "TranslateAcceleratorW")]
    static extern int TranslateAccelerator(IntPtr h, IntPtr acc, ref MSG m);

    public static RECT ClientRect()
    {
        _ = Externs.GetClientRect(_hwnd, out var cr);
        return cr;
    }

    // 工具栏标签信息:仅当标签集/脏标/活动页变化时重建(每帧 WM_PAINT 传给 DrawToolbar 的只读快照)
    static List<ToolbarTab> _tabInfoCache;
    static readonly TabDisplayNames TabNames = new();
    static void RefreshTabInfo() => _tabInfoCache = null;   // 标题/脏标/切换时置空,下次绘制重建
    static List<ToolbarTab> BuildTabInfo()
    {
        if (_tabInfoCache != null) return _tabInfoCache;
        var documents = PlaceholderHidden ? new List<ToolbarTab>()
            : TabNames.Project(Tabs.Select((t, index) => new ToolbarTab(t.RecoveryId, t.Name, t.Path, index == ActiveTab && !HomeShown, t.Dirty, t.ReadOnly, t.Pinned)).ToList());
        _tabInfoCache = new List<ToolbarTab>(documents.Count + 1);
        if (State.Appearance.Desk) _tabInfoCache.Add(HomeTab);   // U10：起始页固定在最前
        _tabInfoCache.AddRange(documents);
        return _tabInfoCache;
    }

    static void Log(string msg) => AppLog.Write(msg);

    const int TIMER_DRAFT = 8;
    static DraftStore _draftStore;
    static DraftStore Drafts => _draftStore ??= new(Path.Combine(AppStorage.DirectoryPath, "drafts"));
    static bool _draftScheduled, _draftWarningShown;
    static bool _closingNormally { get => State.ClosingNormally; set => State.ClosingNormally = value; }

    static void ScheduleDraft()
    {
        if (_draftScheduled || _hwnd == IntPtr.Zero) return;
        // Throttle instead of resetting on every keystroke: continuous typing is also checkpointed.
        _draftScheduled = Externs.SetTimer(_hwnd, (IntPtr)TIMER_DRAFT, 2000, IntPtr.Zero) != IntPtr.Zero;
    }

    static void DraftError(Exception ex)
    {
        AppLog.Error("draft recovery", ex);
        if (_draftWarningShown) return;
        _draftWarningShown = true;
        _ = Externs.MessageBoxW(_hwnd, "草稿恢复副本读写失败，请及时保存文档。\n" + ex.Message, "草稿恢复", 0x30);
    }

    static void CheckpointDrafts()
    {
        _ = Externs.KillTimer(_hwnd, (IntPtr)TIMER_DRAFT);
        _draftScheduled = false;
        foreach (var session in Tabs)
        {
            if (!session.Dirty) continue;
            if (ReferenceEquals(session, Session) && _editing && Editor.IsComposing) { ScheduleDraft(); continue; }
            try { Drafts.Write(session.CaptureDraft(ReferenceEquals(session, Session) ? CurrentText() : session.Text)); }
            catch (Exception ex) { DraftError(ex); ScheduleDraft(); }
        }
    }

    static void ClearDraft(DocumentSession session)
    {
        try { Drafts.Remove(session.RecoveryId); }
        catch (Exception ex) { DraftError(ex); }
    }

    static List<DraftSnapshot> ReadDrafts()
    {
        try { return Drafts.ReadAll(); }
        catch (Exception ex) { DraftError(ex); return new(); }
    }

    static void RestoreDrafts(List<DraftSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;
        if (Externs.MessageBoxW(_hwnd, "发现 " + snapshots.Count + " 份未保存草稿，是否恢复？\n\n恢复后可继续编辑或另存，原文件不会被改写。\n选择“否”会保留副本，下次启动仍可恢复。", "恢复未保存草稿", 0x24) != 6) return;
        int restored = 0;
        foreach (var snapshot in snapshots)
        {
            try { Tabs.RestoreDraft(snapshot); restored++; }
            catch (Exception ex) { DraftError(ex); }
        }
        if (restored == 0) return;
        Doc.OnWidth(ReadingWidth());
        SyncEditor();
        UpdateTitle();
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        if (!_editing) ToggleEdit();
    }


    static string DocDir() => System.IO.Path.GetDirectoryName(_path) ?? "";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")]
    static extern bool GetUpdateRect(IntPtr h, out RECT r, int erase);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfoW(uint action, uint param, ref RECT rect, uint init);

    static RECT? GetWorkArea()
    {
        try
        {
            var wa = default(RECT);
            if (SystemParametersInfoW(0x0030 /*SPI_GETWORKAREA*/, 0, ref wa, 0)) return wa;
        }
        catch { }   // 有意回退:取不到工作区时返回 null,由 WindowGeometry 的无工作区分支处理
        return null;
    }

    // 恢复窗口位置前校验:若保存的矩形完全落在主工作区之外(如副显示器已拔),吸附回可见区域
    static RECT ClampWindowRect(RECT r) => WindowGeometry.ClampWindowRect(r, GetWorkArea());

    // 工具栏条带局部失效:悬停/按压的几像素变化不值得整页重绘
    static void InvalidateToolbar(IntPtr h)
    {
        _ = Externs.GetClientRect(h, out var cr);
        var r = WindowGeometry.ToolbarStrip(cr.R, ToolH, _scale);
        _ = Externs.InvalidateRectRect(h, ref r, 0);
    }

    // 正文条带局部失效:选区/滚动只动正文,工具栏像素不变就不参与重绘(BeginPaint 裁剪自动保护它)
    static void InvalidateContent(IntPtr h)
    {
        _ = Externs.GetClientRect(h, out var cr);
        var r = WindowGeometry.ContentStrip(cr.R, cr.B, ToolH, _scale);
        _ = Externs.InvalidateRectRect(h, ref r, 0);
    }

    static bool DoSaveAs()
    {
        try
        {
            string picked = NativeFileDialogs.Save(_hwnd, _path.Length == 0 ? "未命名.md" : _path);
            if (picked == null) return false;
            int other = Tabs.IndexOfPath(picked);
            if (other >= 0 && other != ActiveTab)
            {
                _ = Externs.MessageBoxW(_hwnd, "目标文件已在另一标签中打开，请选择其他文件名。", "另存为", 0);
                return false;
            }
            FileVersion confirmed = null;
            if (!string.Equals(Path.GetFullPath(picked), _path, StringComparison.OrdinalIgnoreCase))
            {
                confirmed = AtomicFile.Observe(picked);
                if (confirmed.Exists && !DocumentDialogs.ConfirmOverwrite(_hwnd, picked)) return false;
            }
            // 路径维护(36):另存到其他目录时,相对引用会失效;经确认后改写为继续指向原文件的相对路径
            string rebased = null, oldDir = DocDir(), newDir = Path.GetDirectoryName(Path.GetFullPath(picked)) ?? "";
            if (oldDir.Length > 0 && newDir.Length > 0 && !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
            {
                var plan = DocumentPaths.Rebase(CurrentText(), oldDir, newDir);
                if (plan.Count > 0 && Externs.MessageBoxW(_hwnd, "文档中有 " + plan.Count + " 处相对路径引用（图片/链接）指向原目录。\n另存到新目录后是否改写这些引用，使其继续指向原文件？\n\n是：改写后保存\n否：按原文保存", "另存为", 0x24 /*YESNO, question*/) == 6)
                    rebased = plan.Text;
            }
            if (!SaveFile(picked, confirmed, rebased)) return false;
            picked = Session.Path; // Conflict resolution may have saved to another destination.
            RecentList.Promote(_recent, picked, AppPreferences.Current.RecentFiles);
            AppConfig.ForgetOffline(_offlineTabs, picked);
            SaveConfig();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("save as", ex);
            _ = Externs.MessageBoxW(_hwnd, "另存为失败: " + ex.Message, "MDViewerNative", 0);
            return false;
        }
    }

    static void DoExportHtml()
    {
        if (_path.Length == 0) { ExportHtmlAs(false); return; }
        try
        {
            // Export re-parses the draft, so it is the same bounded work as opening.
            using var cancellable = DocumentWork.Begin(EscapeRequested);
            var export = DocumentExport.Prepare(Session, CurrentText(), RenderResources.DarkMode);
            if (!export.TryWrite(path => DocumentDialogs.ConfirmOverwrite(_hwnd, path))) return;
            _ = Externs.MessageBoxW(_hwnd, "已导出: " + export.Path, "导出 HTML", 0);
            _ = Externs.ShellExecuteW(_hwnd, "open", export.Path, null, null, 5);
        }
        catch (DocumentCancelledException)
        {
            AppLog.Write("export HTML: cancelled by user");
            _ = Externs.MessageBoxW(_hwnd, "已取消导出。\n\n未写入任何文件。", "导出 HTML", 0x40);
        }
        catch (Exception ex)
        {
            AppLog.Error("export HTML", ex);
            _ = Externs.MessageBoxW(_hwnd, "导出失败: " + ex.Message, "导出 HTML", 0);
        }
    }

    static readonly List<string> _recent = new();
    static readonly List<string> _sessionTabs = new();
    static IReadOnlyList<string> _cfgUnknownLines = Array.Empty<string>();   // config.txt 中不认识的行，写回时原样保留（E9）
    static List<string> _offlineTabs = new();       // 启动时所在盘或共享暂时不在的标签，保存时保留（E10）；重新打开后移出（E25）
    static IReadOnlySet<string> _unavailablePaths = new HashSet<string>();
    static Paper? _cfgPaper;     // config.txt 中的 paper=（U2）
    static bool? _cfgChrome;     // config.txt 中的 chrome=（U2）
    static float _bodyFont { get => State.BodyFont; set => State.BodyFont = value; }
    static bool _cfgThemeDark { get => State.ConfigThemeDark; set => State.ConfigThemeDark = value; }
    static bool _hasCfgTheme { get => State.HasConfigTheme; set => State.HasConfigTheme = value; }
    static bool _cfgToc { get => State.ConfigToc; set => State.ConfigToc = value; }
    static bool _cfgSerif { get => State.ConfigSerif; set => State.ConfigSerif = value; }
    static RECT _winBounds { get => State.WindowBounds; set => State.WindowBounds = value; }
    static bool _hasBounds { get => State.HasBounds; set => State.HasBounds = value; }
    static bool _winMax { get => State.WindowMaximized; set => State.WindowMaximized = value; }

    static string ConfigPath() => Path.Combine(AppStorage.DirectoryPath, "config.txt");

    static void LoadConfig()
    {
        AppPreferences.Load(); Bookmarks.Load();
        string p = ConfigPath();
        if (p.Length == 0) return;
        try { Reading.Load(Path.Combine(Path.GetDirectoryName(p), "reading-state.tsv")); }
        catch (Exception ex) { AppLog.Error("reading state", ex); }
        if (!File.Exists(p)) { if (State.Appearance.Desk) _bodyFont = ReadingStyle.Desk.DefaultBodySize; return; }   // U12：首次运行即 desk 默认字号
        try
        {
            // 解析规则见 AppConfig（E8）；值为 null 表示文件里没有这一行或值无效，保留原值。
            var v = AppConfig.Parse(File.ReadAllLines(p), File.Exists, _sessionTabs, _recent, Directory.Exists);
            if (v.ThemeDark is bool dark) { _hasCfgTheme = true; _cfgThemeDark = dark; }
            if (v.Toc is bool toc) _cfgToc = toc;
            if (v.Hints is int hints) _lbHintsLeft = hints;   // I10 提示计数(0-3)
            if (v.Serif is bool serif) _cfgSerif = serif;     // I11 衬线 opt-in(默认 0=零回归)
            if (v.Zen is bool zen) Zen.Enabled = zen;         // 阶段 B 沉浸(默认 0)
            if (v.Font is float font) _bodyFont = font;
            _cfgPaper = v.Paper; _cfgChrome = v.ChromeDesk;   // U2：原样写回，classic 下也不丢
            if (v.ChromeDesk is bool desk) State.Appearance.Chrome = desk ? ChromeStyle.Desk : ChromeStyle.Classic;   // 开关只在启动时读取（ADR-0003）；没写时默认 desk（U12）
            State.Appearance.LoadPaper(v.Paper, v.ThemeDark);
            // U3：desk 默认字号 17。旧版每次都写 font=16，所以 16 视同“没改过”，迁移为 desk 默认。
            if (State.Appearance.Desk && (v.Font is null || v.Font == AppState.DefaultBodyFont)) _bodyFont = ReadingStyle.Desk.DefaultBodySize;
            _cfgUnknownLines = v.UnknownLines;
            _offlineTabs = v.OfflineTabs;
            _unavailablePaths = v.UnavailablePaths;
            if (v.Bounds is RECT bounds)
            {
                _winBounds = bounds;
                _winMax = v.Maximized;
                _hasBounds = true;
            }
        }
        catch (Exception ex) { AppLog.Error("configuration", ex); }
    }

    static void SaveConfig()
    {
        if (_restoringStartup) return; // Never persist a half-restored tab set.
        string p = ConfigPath();
        if (p.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            string windowBounds = _hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var r) ? WindowGeometry.FormatBounds(r, IsZoomed(_hwnd)) : null;
            var openPaths = Tabs.Where(t => !string.IsNullOrEmpty(t.Path)).Select(t => t.Path);
            string text = AppConfig.Format(RenderResources.DarkMode, _cfgToc, _lbHintsLeft, RenderResources.SerifMode, Zen.Enabled, _bodyFont,
                windowBounds, _recent, AppPreferences.Current.RecentFiles, AppConfig.MergeSessionTabs(openPaths, _offlineTabs), _cfgUnknownLines,
                State.Appearance.Desk ? PaperPalette.Token(State.Appearance.Paper) : _cfgPaper is Paper keptPaper ? PaperPalette.Token(keptPaper) : null,
                _cfgChrome is bool desk ? Appearance.ChromeToken(desk) : null);
            AtomicFile.WriteState(p, Encoding.UTF8.GetBytes(text));
            Reading.Save(Path.Combine(Path.GetDirectoryName(p), "reading-state.tsv"), Tabs, Session.Path);
        }
        catch (Exception ex) { AppLog.Error("configuration", ex); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegGetValueW(UIntPtr hkey, string sub, string name, uint flags, IntPtr type, out uint data, out uint size);
    static bool SystemPrefersDark()
    {
        try
        {
            uint sz = 4;
            int rc = RegGetValueW((UIntPtr)0x80000001u /*HKCU*/, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0x14 /*RRF_RT_DWORD*/, IntPtr.Zero, out uint v, out sz);
            return WindowChrome.IsSystemDark(rc, v);
        }
        catch { return false; }
    }

    // Return 1 after saving, 0 after discarding, -1 when the operation must stop.
    static int GuardDirty()
    {
        if (!_dirty) return 0;
        switch (DocumentDialogs.Unsaved(_hwnd, _name))
        {
            case DocumentChoice.Save: return SaveFile() ? 1 : -1;
            case DocumentChoice.Discard:
                Session.DiscardChanges();
                ClearDraft(Session);
                SyncEditor();
                UpdateTitle();
                return 0;
            default: return -1;
        }
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    static bool TrySaveClipboardImage(out string path)
    {
        path = "";
        if (!IsClipboardFormatAvailable(CF_DIB) || !Clipboard.OpenClipboard(_hEdit)) return false;
        try
        {
            return TrySaveClipboardImageCore(out path);
        }
        catch (Exception ex)
        {
            AppLog.Error("paste image", ex);
            // 本函数从 EditMainProc(UnmanagedCallersOnly 原生回调)调用,异常逃逸 = 进程终止,必须就地兜底
            Log("PASTEIMG " + ex);
            return false;
        }
        finally { _ = Clipboard.CloseClipboard(); }
    }

    static bool TrySaveClipboardImageCore(out string path)
    {
        path = "";
        IntPtr h = GetClipboardData(CF_DIB);
        if (h == IntPtr.Zero) return false;
        uint size = (uint)GlobalSize(h);   // 原型 SIZE_T GlobalSize(HGLOBAL):大小在返回值
        IntPtr gp = Clipboard.GlobalLock(h);
        if (gp == IntPtr.Zero) return false;
        if (size < 40) { _ = Clipboard.GlobalUnlock(h); return false; }   // 已加锁:早退必须解锁
        byte[] data;
        try { data = new byte[size]; Marshal.Copy(gp, data, 0, (int)size); }
        finally { _ = Clipboard.GlobalUnlock(h); }
        int biSize = BitConverter.ToInt32(data, 0);
        short bpp = BitConverter.ToInt16(data, 14);
        int comp = BitConverter.ToInt32(data, 30);
        if (biSize < 40) return false;
        int clrUsed = BitConverter.ToInt32(data, 32);
        if (bpp <= 8 && clrUsed == 0) clrUsed = 1 << bpp;
        int pixOff = 14 + biSize + (bpp <= 8 ? clrUsed * 4 : 0);
        if ((comp == 3 || comp == 6) && biSize == 40) pixOff += 12;   // BI_BITFIELDS/BI_ALPHABITFIELDS:40 字节头后带 3 个掩码 DWORD,漏加则解码错位
        string dir = DocDir();
        if (string.IsNullOrEmpty(dir)) dir = ".";
        int n = 1;
        do { path = Path.Combine(dir, "PasteImg" + n + ".bmp"); n++; } while (File.Exists(path));
        using var fs = new FileStream(path, FileMode.Create);
        fs.WriteByte(0x42); fs.WriteByte(0x4D);
        fs.Write(BitConverter.GetBytes((uint)(14 + data.Length)), 0, 4);
        fs.Write(new byte[4], 0, 4);
        fs.Write(BitConverter.GetBytes((uint)pixOff), 0, 4);
        fs.Write(data, 0, data.Length);
        return true;
    }

    static nint _curArrow, _curIBeam, _curHand;   // E0.4:光标句柄缓存(旧实现每次 WM_SETCURSOR 都 LoadCursorW 四连)
    static nint CurArrow() => _curArrow != 0 ? _curArrow : (_curArrow = Externs.LoadCursorW(IntPtr.Zero, (IntPtr)32512));
    static nint CurIBeam() => _curIBeam != 0 ? _curIBeam : (_curIBeam = Externs.LoadCursorW(IntPtr.Zero, (IntPtr)32513));
    static nint CurHand() => _curHand != 0 ? _curHand : (_curHand = Externs.LoadCursorW(IntPtr.Zero, (IntPtr)32649));
    static nint CursorHandle(PointerCursor cursor) => cursor switch
    {
        PointerCursor.Arrow => CurArrow(),
        PointerCursor.IBeam => CurIBeam(),
        PointerCursor.Hand => CurHand(),
        _ => 0
    };

    [STAThread]
    private static int Main(string[] args)
    {
        using var apartment = new ComApartment();
        // GBK/ANSI 编码支持(CodePages 内置于共享运行时,AOT 兼容);需在首次 GetEncoding(936) 前注册
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // 平滑滚动定时器:系统默认 ~15.6ms 分辨率会让 16ms tick 与 60Hz 合成产生 4Hz 拍频(缓出尾段 1px 步"1步/2步/0步"交替=微顿)
        _ = timeBeginPeriod(1);
        // Single instance: acknowledge an independent copied request, never overwrite a shared file.
        using var mutex = new System.Threading.Mutex(true, "MDViewerNative.SingleInstance", out var created);
        if (!created)
        {
            IntPtr prev = IntPtr.Zero;
            for (int wi = 0; wi < 40; wi++)   // 首实例可能仍在建窗:最多等 ~2s,防快速连开文件时窗口堆积
            {
                prev = FindWindowW("MDViewerNative", null);
                if (prev != IntPtr.Zero) break;
                System.Threading.Thread.Sleep(50);
            }
            if (prev != IntPtr.Zero)
            {
                if (args.Length > 0 && File.Exists(args[0]))
                {
                    if (!InstanceHandoff.Forward(prev, args[0]))
                    {
                        Externs.MessageBoxW(prev, "已有窗口未确认接收文件，请稍后重试。", "打开文件", 0x30);
                        return 1;
                    }
                }
                _ = SetForegroundWindow(prev);
                return 0;
            }
            // 找不到已有窗口(罕见):继续以新实例运行
        }
        AppDomain.CurrentDomain.UnhandledException += (s2, e2) =>
            Log("CRASH " + e2.ExceptionObject + Environment.NewLine +
                (e2.ExceptionObject is Exception ex ? ex.StackTrace + Environment.NewLine : ""));
        _ = Externs.SetProcessDpiAwarenessContext((IntPtr)(-4));
        LoadConfig();
        var hInst = Externs.GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x000B, // CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS
            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInst,
            hCursor = Externs.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = "MDViewerNative",
        };
        if (Externs.RegisterClassExW(in wc) == 0) return Marshal.GetLastWin32Error();

        RenderResources.UseChrome(State.Appearance.Desk);   // U6b：顶栏高在窗口创建前定下
        var initBounds = WindowGeometry.ResolveInitialBounds(_hasBounds, _winBounds, GetWorkArea());
        _hwnd = Externs.CreateWindowExW(0, "MDViewerNative", "MD 查看器", WS_OVERLAPPEDWINDOW | (State.Appearance.Desk ? 0u : (uint)WS_VSCROLL) /*U5a：desk 用书签绳取代滚动条*/ | 0x02000000u /*WS_CLIPCHILDREN:父窗重绘不碰子控件,消除子控件闪烁*/,
            initBounds.L, initBounds.T, initBounds.R - initBounds.L, initBounds.B - initBounds.T,
            default, default, hInst, default);
        if (_hwnd == IntPtr.Zero) return Marshal.GetLastWin32Error();
        Externs.DragAcceptFiles(_hwnd, 1);
        // 试用反馈：desk 下“保存 / 不保存 / 取消”等选择框用纸色自绘；经典界面与高对比度仍用系统对话框
        DocumentDialogs.Styled = (owner, heading, body, labels) =>
            State.Appearance.Desk && !RenderResources.HighContrast ? ChoiceDialogView.Show(owner, _scale, heading, body, labels) : -1;
        ApplyWindowChrome();
        ApplyCustomFrame(_hwnd);   // U6b：desk 合并标题栏

        _scale = WindowGeometry.DpiToScale(Externs.GetDpiForWindow(_hwnd));
        RenderResources graphics;
        try { graphics = new RenderResources(_scale, _bodyFont, ProbeSerifFamily(), _cfgSerif, State.Appearance.Desk ? ReadingStyle.Desk : ReadingStyle.Classic); }
        catch (Exception ex)
        {
            AppLog.Error("initialize graphics", ex);
            _ = Externs.MessageBoxW(_hwnd, "图形初始化失败: " + ex.Message, "MDViewerNative", 0);
            return 1;
        }

        using var graphicsScope = graphics;
        Toolbar.AlwaysShowClose = (GetSystemMetrics(94 /*SM_DIGITIZER*/) & 0x0F) != 0;
        Toolbar.Desk = State.Appearance.Desk;   // U6a：desk 顶栏

        // 恢复主题:配置优先,首次运行跟随系统明暗
        bool dark = WindowChrome.ResolveInitialDarkMode(_hasCfgTheme, _cfgThemeDark, SystemPrefersDark());
        if (State.Appearance.Desk)
        {
            ApplyPaper();   // U2：desk 界面按纸张上色，浅色纸张同样需要重新上色
            ApplyWindowChrome();
            ApplyScrollbarTheme();
        }
        else if (dark)
        {
            RenderResources.ApplyTheme(true);
            ApplyWindowChrome();
            ApplyScrollbarTheme();
        }

        var recoverable = ReadDrafts();
        if (args.Length > 0 && File.Exists(args[0])) LoadFile(Path.GetFullPath(args[0]));
        else if (args.Length > 0 && args[0] == "--edit") OpenFileDlgNative();
        else if (_sessionTabs.Count > 0)
        {
            // 会话恢复:多标签逐个打开
            foreach (string tp in _sessionTabs) LoadFile(tp, newTab: !string.IsNullOrEmpty(_path));
        }
        else if (_recent.FirstOrDefault(r => !_unavailablePaths.Contains(r)) is string lastRead) LoadFile(lastRead);   // 跳过所在盘暂时不在的条目(E10)
        else if (recoverable.Count == 0 && !State.Appearance.Desk) OpenFileDlgNative();   // U10：desk 什么都没打开时停在起始页，不弹对话框
        if (args.Length == 0 && Reading.ActivePath.Length > 0)
        {
            int restoredActive = Tabs.IndexOfPath(Reading.ActivePath);
            if (restoredActive >= 0) SwitchTab(restoredActive);
        }
        RestoreDrafts(recoverable); // Explicit draft recovery takes priority over saved reading state.
        _restoringStartup = false;
        HomeIfEmpty();
        UpdateTitle();
        if (args.Any(a => a == "--edit") && !string.IsNullOrEmpty(_path) && !_editing) ToggleEdit();
        SaveConfig();

        ApplyPreferences();
        Find.OptionsChanged += options =>
        {
            var preferences = AppPreferences.Current;
            preferences.SearchCase = options.CaseSensitive; preferences.SearchWord = options.WholeWord; preferences.SearchRegex = options.RegularExpression;
            try { AppPreferences.Save(); } catch (Exception ex) { AppLog.Error("save search preferences", ex); }
        };
        Find.QueryCommitted += query =>
        {
            AppPreferences.RememberQuery(query);
            try { AppPreferences.Save(); } catch (Exception ex) { AppLog.Error("save search history", ex); }
        };
        Find.CompositionChanged += CompositionChanged;
        Find.Opening += () => CloseJuan(false);   // U8：查找框的输入框是子窗口，会盖在卷目上
        NativeForm.Dismissed += CompositionChanged;
        // 加速键:Ctrl+S/E/O/F/D 在 EDIT 持有焦点时也生效
        var accs = new ACCEL[]
        {
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'S', cmd = 1001 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'E', cmd = 1002 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'O', cmd = 1003 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'D', cmd = 1004 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'F', cmd = 1005 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'T', cmd = 1006 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'H', cmd = 1013 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'W', cmd = 1008 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)0x09, cmd = 1009 },
            new ACCEL { fVirt = 0x08 | 0x04 | 0x01, key = (ushort)0x09, cmd = 1010 },
            new ACCEL { fVirt = 0x08 | 0x04 | 0x01, key = (ushort)'S', cmd = 1011 },
            new ACCEL { fVirt = 0x10 | 0x08 | 0x01, key = (ushort)'S', cmd = 1012 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)0xBB, cmd = 1014 },   // Ctrl+= 字号放大(VK_OEM_PLUS)
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)0x6B, cmd = 1014 },   // Ctrl+小键盘+ (VK_ADD)
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)0xBD, cmd = 1015 },   // Ctrl+- 字号缩小(VK_OEM_MINUS)
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)0x6D, cmd = 1015 },   // Ctrl+小键盘- (VK_SUBTRACT)
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'Z', cmd = 2003 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'Y', cmd = 2270 },
            new ACCEL { fVirt = 0x08 | 0x04 | 0x01, key = (ushort)'Z', cmd = 2270 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'N', cmd = 2200 },
            new ACCEL { fVirt = 0x08 | 0x04 | 0x01, key = (ushort)'T', cmd = 2205 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'G', cmd = 2220 },
            new ACCEL { fVirt = 0x10 | 0x01, key = 0x25, cmd = 2098 },
            new ACCEL { fVirt = 0x10 | 0x01, key = 0x27, cmd = 2099 },
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'0', cmd = 1016 },    // Ctrl+0 字号复位
            new ACCEL { fVirt = 0x01, key = 0x7A, cmd = 2251 },   // F11 沉浸模式
            new ACCEL { fVirt = 0x08 | 0x01, key = (ushort)'K', cmd = 2291 },   // U11：命令面板
            new ACCEL { fVirt = 0x08 | 0x04 | 0x01, key = (ushort)'P', cmd = 2291 },
        };
        nint hAccel;
        fixed (ACCEL* buffer = accs) hAccel = CreateAcceleratorTable((nint)buffer, accs.Length);
        try
        {
            _ = Externs.ShowWindow(_hwnd, _winMax ? 3 : 1);
            _ = Externs.UpdateWindow(_hwnd);

            // GetMessageW 返回 -1 表示出错(判 !=0 会无限死转):仅 >0 时派发
            while ((int)Externs.GetMessageW(out var msg, default, 0, 0) > 0)
            {
                if (Toolbar.HasFocus)
                {
                    nint focus = Externs.GetFocus();
                    if (focus != 0 && focus != _hwnd && (focus == _hEdit || Find.OwnsInput(focus)))
                    {
                        Toolbar.ClearFocus(); _toolbarReturnFocus = 0;
                        UpdateTitle(); InvalidateToolbar(_hwnd);
                    }
                }
                if (HasComposition && (msg.hwnd == _hEdit || Find.OwnsInput(msg.hwnd))
                    && (msg.message == Externs.WM_KEYDOWN || msg.message == 0x0104))
                {
                    Externs.TranslateMessage(in msg); Externs.DispatchMessageW(in msg); continue;
                }
                if (LookFilter(in msg)) continue;
                if (MoreFilter(in msg)) continue;   // 试用修复：desk「更多」菜单，同外观面板   // U11b：外观面板开着时方向键 / 回车 / Esc 归它，面板外按下即收起
                if (_palette != null && _palette.Owns(msg.hwnd))   // U11：命令面板开着时键盘只归它（不走主窗口加速键）
                {
                    Externs.TranslateMessage(in msg); Externs.DispatchMessageW(in msg); continue;
                }
                if (msg.message == Externs.WM_KEYDOWN && msg.wParam == 0x75 /*F6*/)
                {
                    ToggleToolbarFocus();
                    continue;
                }
                if (!Find.TranslateKey(ref msg) && TranslateAccelerator(_hwnd, hAccel, ref msg) == 0)
                {
                    // Toolbar keys are commands. Do not queue a character that a nested popup
                    // menu could interpret as a second Enter/Space before the user selects an item.
                    if (!(msg.message == Externs.WM_KEYDOWN && msg.hwnd == _hwnd && Toolbar.HasFocus))
                        _ = Externs.TranslateMessage(in msg);
                    _ = Externs.DispatchMessageW(in msg);
                }
            }
            return 0;
        }
        finally { if (hAccel != 0) DestroyAcceleratorTable(hAccel); }
    }

    // ---------- 文件 ----------
    // 正文随窗口排版(S80):改窗口宽、改字号、开关常驻目录都会重排,行数随之变化。
    // 按阅读位置(所在块与块内比例)回到原处,而不是停在旧的像素偏移上。
    // 排版宽度始终按“有滚动条”计算:滚动条随内容长短自动显隐时,正文不跟着重排、不横跳,
    // 同一文档也不会因为滚动条出现再排一遍。没有滚动条时右侧只多出一道滚动条宽的空白。
    static float ReadingWidth()
    {
        float w = _viewW;
        if (!State.Appearance.Desk && _hwnd != IntPtr.Zero && (Externs.GetWindowLongPtrW(_hwnd, GWL_STYLE).ToInt64() & WS_VSCROLL) == 0)
            w -= GetSystemMetricsForDpi(2 /*SM_CXVSCROLL*/, (uint)MathF.Round(96f * _scale));
        return MathF.Max(0f, w) / _scale;
    }

    static void KeepReadingPlace(Action relayout)
    {
        _offY = Snap(MathF.Min(Doc.KeepReading(_offY, relayout), MaxOff()));
    }

    // 字号缩放(Ctrl+滚轮 / Ctrl+= / Ctrl+- / Ctrl+0 共用):仅实际变化时重排,偏移按新范围回钳
    static void ZoomFont(float target, IntPtr h)
    {
        float bs = WindowGeometry.ClampBodyFont(target);
        if (bs == _bodyFont) return;
        _bodyFont = bs;
        KeepReadingPlace(() => Doc.SetBodySize(bs, relayout: true));
        Editor?.SetFontSize(bs);
        Doc.EnsureLayout();
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        UpdateScroll();
        _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
        SaveConfig();
        RefreshLook();
    }

    static NavigationPoint CaptureNavigation() => new(Session.RecoveryId, Session.Path, Doc.CaptureReading(MathF.Max(0f, _offY)));

    // 成功且确实移动了位置的跳转才入历史并显示“返回原处”;历史回溯本身不经过这里。
    // after 可由调用方给出：平滑跳转时终点尚未到达（U5a）。
    static void RecordNavigation(NavigationPoint before, NavigationPoint after = null)
    {
        after ??= CaptureNavigation();
        Navigation.Record(before, after);
        if (before != null && before != after && Navigation.CanBack) { _returnPoint = before; ShowReturnHint(); }
    }

    static void ShowReturnHint()
    {
        ReturnPrompt.Show();
        if (State.Appearance.Desk)
        {
            // U5c：绳上返回书签留到返回或切换标签/模式为止，不计时；弹入 600 ms，页脚提示 3 秒。
            _ = Externs.KillTimer(_hwnd, (IntPtr)ReturnHint.TimerId);
            StartDeskAnimation(DeskAnim.BackFlag, DeskMetrics.BackFlagPopMs);
            ShowFooterNote(BackNote);
            InvalidateCord();
            return;
        }
        _ = Externs.SetTimer(_hwnd, (IntPtr)ReturnHint.TimerId, ReturnHint.LifetimeMs, IntPtr.Zero);
        InvalidateContent(_hwnd);
    }

    static void HideReturnHint()
    {
        if (!ReturnPrompt.Visible) return;
        ReturnPrompt.Hide();
        Anim.Stop(DeskAnim.BackFlag);
        if (FooterNote.Current(Environment.TickCount64) == BackNote) { FooterNote.Clear(); InvalidateFooter(); }
        _ = Externs.KillTimer(_hwnd, (IntPtr)ReturnHint.TimerId);
        InvalidateContent(_hwnd);
    }

    static bool OverReturnHint(float x, float y)
    {
        if (!ReturnPrompt.Visible || _editing || State.Appearance.Desk) return false;   // desk 下是绳上书签（U5c）
        var b = Doc.ReturnHintBox(ContentTop, ContentHeight);
        return x >= b.X && x <= b.X + b.W && y >= b.Y && y <= b.Y + b.H;
    }

    static bool RestoreNavigation(NavigationPoint point)
    {
        int index = Tabs.IndexOfId(point.SessionId);
        bool glide = State.Appearance.Desk && index >= 0 && index == ActiveTab && SystemAnimationEnabled();   // U5c：同一文档内平滑滚回
        if (index >= 0)
        {
            if (index != ActiveTab) SwitchTab(index);
            if (Tabs.Current.RecoveryId != point.SessionId) return false;
        }
        else
        {
            if (point.Path.Length == 0 || !File.Exists(point.Path))
            {
                Externs.MessageBoxW(_hwnd, "历史位置的文档已关闭或不存在，当前位置已保留。", "无法返回历史位置", 0x30);
                return false;
            }
            if (point.Path.StartsWith(@"\\", StringComparison.Ordinal)
                && Externs.MessageBoxW(_hwnd, "此历史位置需要重新访问网络共享，是否继续？\n" + point.Path, "打开网络文件", 0x124) != 6) return false;
            if (!LoadFile(point.Path, true)) return false;
        }
        Find.Close();
        Doc.OnWidth(ReadingWidth());
        float to = Doc.RestoreReading(point.Location);
        if (glide) { StopSmoothScroll(); GlideTo(ScrollGeometry.ClampOffset(to, MaxOff(), _scale)); }
        else SetOffset(to);
        return true;
    }

    static void NavigateHistory(bool back)
    {
        if (HasComposition || (_editing && !ToggleEdit())) return;
        HideReturnHint();
        Navigation.Traverse(back, CaptureNavigation(), RestoreNavigation);
    }

    private static void OpenDocumentLink(string address)
    {
        void Error(string message) => Externs.MessageBoxW(_hwnd, message, "无法打开链接", 0x30);
        try
        {
            if (Doc.IsInlineImage(address)) { OpenLightbox(address); return; }
            if (!DocumentLinks.TryResolve(address, Doc.DocumentDirectory, out var link, out string error)) { Error(error); return; }
            if (link.NetworkFile && Externs.MessageBoxW(_hwnd, "此链接将访问网络共享，是否继续？\n" + link.Location,
                "打开网络文件", 0x124) != 6) return;
            var before = CaptureNavigation();
            if (link.Kind is LinkKind.Markdown or LinkKind.ExternalFile)
            {
                // Obsidian 双链(S75):同目录没有时,在库根目录(含 .obsidian)内按文件名查找
                if (!File.Exists(link.Location) && WikiLinks.FindInVault(link.Location) is string found) link = link with { Location = found };
                if (!File.Exists(link.Location)) { Error("找不到链接文件：\n" + link.Location); return; }
                if (link.Kind == LinkKind.Markdown)
                {
                    if (!LoadFile(link.Location, true)) return;
                    // An active dirty editor remains owned by its session. Never discard it to navigate.
                    if (_editing) { Error("请先切换到预览，再跳转到文档标题。当前草稿未改动。"); return; }
                    if (link.Fragment.Length == 0) { RecordNavigation(before); return; }
                }
                else
                {
                    var prompt = ExternalFilePrompt.For(link.Location, ExternalFilePrompt.IsExecutableType);
                    if (Externs.MessageBoxW(_hwnd, prompt.Text, prompt.Title, prompt.Flags) != 6) return;
                    if (prompt.Executable) AppLog.Write("open link: executable type confirmed (" + Path.GetExtension(link.Location) + ")");
                }
            }
            if (link.Kind is LinkKind.Anchor or LinkKind.Markdown)
            {
                if (Doc.TryAnchorJump(link.Fragment, out float y)) SetOffset(MathF.Max(0f, y - 16f));
                else Error("未找到目标标题：\n" + link.Fragment);
                RecordNavigation(before);
                return;
            }
            nint result = Externs.ShellExecuteW(_hwnd, "open", link.LaunchAddress, null, null, 5);
            if (result.ToInt64() <= 32) Error("系统未能打开此链接，请检查默认应用或地址。\n" + link.Location);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Error("open link", ex);
            Error("链接打开失败：" + ex.Message);
        }
    }

    // Cancellation probe for long document work. Esc must be held or pressed while the
    // operation runs; the high bit is the current physical state. Ignored while an IME
    // composition is active, where Esc belongs to the candidate window.
    static bool EscapeRequested() => !HasComposition && (Externs.GetAsyncKeyState(0x1B) & 0x8000) != 0;

    static bool LoadFile(string path, bool newTab = false)
    {
        if (HasComposition) return false;
        bool fromHome = HomeShown;
        if (fromHome) newTab = true;   // U10：起始页上打开，不替换藏在后面的当前文档（空白占位会被直接替换）
        if (State.Appearance.Desk) newTab = true;   // 试用修复：desk 的「+」即打开文件，打开总是新标签（已打开的切过去，空白占位照旧替换）；classic 保持替换当前
        var previous = Session;
        Reading.Remember(previous);
        try
        {
            bool preserveCurrent = Tabs.OpenKeepsCurrent(path, newTab);
            // Esc cancels only after DocumentWork.GraceChars of work, so ordinary documents
            // are never affected. GetAsyncKeyState is required here: this path is synchronous
            // and pumps no messages, so a queued WM_KEYDOWN would not be seen until after.
            using var cancellable = DocumentWork.Begin(EscapeRequested);
            if (!Tabs.TryOpen(path, newTab, () =>
            {
                if (preserveCurrent)
                {
                    if (_editing && !ToggleEdit()) return false;
                }
                else if (GuardDirty() < 0) return false;
                Reading.Remember(Session); // Include a document just saved by the guard.
                return true;
            })) return false;
        }
        catch (DocumentCancelledException)
        {
            // The user pressed Esc while a large document was being parsed or laid out.
            // TryOpen already disposed the candidate, so the previous document is intact.
            AppLog.Write("open document: cancelled by user");
            _ = Externs.MessageBoxW(_hwnd, "已取消打开该文档。\n\n当前文档未被替换。", "MDViewerNative", 0x40);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("open document", ex);
            _ = Externs.MessageBoxW(_hwnd, "读取失败: " + ex.Message, "MDViewerNative", 0);
            return false;
        }
        // Reopening the active path must preserve the live editor and its undo history.
        if (fromHome) _home = false;
        if (ReferenceEquals(previous, Session)) { CheckDiskChange(); if (fromHome) AfterHomeChange(); return true; }
        Find.Close();
        StopSmoothScroll();
        if (_editing)
        {
            _ = Externs.ShowWindow(_hEdit, 0);
            _editing = false;
            ToggleMainScrollbar(true);
        }
        SyncEditor();
        path = Session.Path;
        Session.ReadOnly |= AppPreferences.Current.DefaultReadOnly;
        CheckDiskChange();
        RecentList.Promote(_recent, path, AppPreferences.Current.RecentFiles);
        AppConfig.ForgetOffline(_offlineTabs, path);
        UpdateTitle();
        Doc.OnWidth(ReadingWidth());
        CloseJuan(false);   // U8：换了文档，卷目条目作废；desk 不用侧栏目录（toc= 偏好留给经典界面）
        bool wantToc = _cfgToc && Doc.TocPinned && !State.Appearance.Desk;   // D2:toc=1 表示“宽窗常驻展开”;压正文的窄窗打开文档时保持收起
        if (Doc.ShowToc != wantToc) Doc.SetToc(wantToc);
        Reading.Apply(Session);
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        SaveConfig();
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        _ = Externs.SetFocus(_hwnd);
        return true;
    }

    // Only the visible editor owns a live draft. A hidden EDIT never supplies another tab's text.
    static string CurrentText() => _editing && _hEdit != IntPtr.Zero
        ? Session.FromEditorText(Externs.WindowText(_hEdit)) : Session.Text;

    static bool _syncingEditor { get => State.SyncingEditor; set => State.SyncingEditor = value; }
    static void SyncEditor()
    {
        if (_hEdit == IntPtr.Zero) return;
        _syncingEditor = true;
        try { EditSession.Bind(Session); Externs.SendMessageW(_hEdit, 0x00CF, Session.ReadOnly ? 1 : 0, 0); }
        finally { _syncingEditor = false; }
    }

    static bool SaveFile(string targetPath = null, FileVersion confirmedVersion = null, string rebasedText = null)
    {
        if (HasComposition || Session.ReadOnly) return false;
        if (targetPath == null && string.IsNullOrEmpty(_path)) return DoSaveAs();
        bool inkWet = _dirty;   // U9c：只有真有墨滴时，保存才画“墨干了”
        while (true)
        {
            try
            {
                Session.Save(rebasedText ?? CurrentText(), targetPath, confirmedVersion);
                _autoSavePaused.Remove(Session.RecoveryId);
                try { VersionArchive.CapturePrevious(Session.Path); } catch (Exception ex) { AppLog.Error("version archive", ex); }
                Navigation.Rename(Session.RecoveryId, Session.Path);
                ClearDraft(Session);
                if (!_editing || rebasedText != null) SyncEditor(); // Preserve the active editor's caret and undo history unless the text was rebased.
                UpdateTitle();
                if (Find.Visible && !_editing) Find.Next(back: false);
                _offY = Snap(MathF.Min(_offY, MaxOff()));
                UpdateScroll();
                _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
                ShowFooterNote("已保存");   // U4：页脚朱色临时状态
                if (inkWet) NoteInk(fresh: false);
                return true;
            }
            catch (FileConflictException ex)
            {
                AppLog.Error("save document", ex);
                switch (DocumentDialogs.Conflict(_hwnd, ex.TargetPath, CurrentText()))
                {
                    case ConflictChoice.SaveAs: return DoSaveAs();
                    case ConflictChoice.Overwrite:
                        confirmedVersion = ex.ActualVersion;
                        continue; // A later external change will trigger another conflict.
                    default: return false;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("save document", ex);
                _ = Externs.MessageBoxW(_hwnd, "保存失败: " + ex.Message, "MDViewerNative", 0);
                return false;
            }
        }
    }

    static string _ctxCode;
    static Guid _ctxCodeOwner;
    static bool _contextMenuActive { get => State.ContextMenuActive; set => State.ContextMenuActive = value; }
    static string CodeForCopy() => _editing ? null : _contextMenuActive
        ? (_ctxCodeOwner == Session.RecoveryId ? _ctxCode : null) : Doc.CodeAtSelection();

    static string _ctxImgSrc;   // I7:右键命中图片时的 src(0=无;"查看图片…"项)
    static long _moreClosedAt;  // "更多"菜单关闭时刻(owner rect 穿透点击的吞击窗口)

    static unsafe void ShowContextMenu(IntPtr h, int sx, int sy)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        _contextMenuActive = true;
        var st = CmdStateNow();
        if (_editing)
        {
            // I7:剪贴板六项(既有语义不动)+ 查找/替换(编辑者也要搜字,可发现性评审)
            foreach (var c in Commands.OnFace(CmdFace.ContextEdit))
            {
                if (c.Id < 2000) continue;
                if (c.Id is 2004 or 2008) _ = AppendMenuW(menu, 0x0800 /*MF_SEPARATOR*/, (IntPtr)0, null);
                _ = AppendMenuW(menu, c.IsEnabled(st) ? 0u : 1u, (IntPtr)c.Id, c.MenuText);
            }
            _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);
            foreach (var c in Commands.OnFace(CmdFace.ContextEdit))
            {
                if (c.Id >= 2000) continue;
                _ = AppendMenuW(menu, c.IsEnabled(st) ? 0u : 1u, (IntPtr)c.Id, c.MenuText);
            }
        }
        else
        {
            if (_ctxImgSrc != null) _ = AppendMenuW(menu, 0, (IntPtr)2095, "查看图片…");   // E0.4 配套:图片右键入口
            foreach (var c in Commands.OnFace(CmdFace.ContextView))
            {
                if (c.Id is 2001 or 1011) _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);   // 组:文件|此处|操作
                _ = AppendMenuW(menu, c.IsEnabled(st) ? 0u : 1u, (IntPtr)c.Id, c.MenuText);
            }
        }
        AddProductMenus(menu);
        if (_recent.Count > 0)
        {
            _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);
            IntPtr sub = CreatePopupMenu();
            if (sub != IntPtr.Zero)
            {
                for (int i = 0; i < _recent.Count && i < 5; i++)
                    _ = AppendMenuW(sub, 0, (IntPtr)(2100 + i), System.IO.Path.GetFileName(_recent[i]).Replace("&", "&&"));
                _ = AppendMenuW(menu, 0x0010 /*MF_POPUP*/, sub, "最近打开");   // I7:平铺改弹出子菜单(实现评审:此前"现成"是虚报)
                _ = DestroyMenuAfterTrack(sub);   // 审查修复:MF_POPUP 挂入的自创子菜单须应用侧销毁(TrackPopupMenu 后统一回收)
            }
        }
        _ = SetForegroundWindow(h);
        int id = TrackPopupMenu(menu, 0x0002 | 0x0100, sx, sy, 0, h, IntPtr.Zero);
        _ = DestroyMenu(menu);
        DrainSubMenus();   // 子菜单在挂入后由系统持有,弹出结束才可安全销毁
        try
        {
            if (id != 0) _ = Externs.SendMessageW(h, Externs.WM_COMMAND, (IntPtr)id, IntPtr.Zero);
        }
        finally { _contextMenuActive = false; _ctxCode = null; }
    }

    // 审查修复(#2):MF_POPUP 子菜单句柄登记/回收——DestroyMenu(父) 不递归销毁自创子菜单,漏 Destroy 即每次右键漏一个 HMENU
    static readonly System.Collections.Generic.List<IntPtr> _subMenus = new();
    static IntPtr DestroyMenuAfterTrack(IntPtr sub) { _subMenus.Add(sub); return sub; }
    static void DrainSubMenus()
    {
        foreach (var s in _subMenus) _ = DestroyMenu(s);
        _subMenus.Clear();
    }

    // I7(PLAN-ERGONOMICS §1):"更多"菜单由命令表生成——加速键曝光投影,≤10 项+最近打开子菜单;
    // 状态三通道:灰显(边界/模式)、打勾(字号复位=16)、主题标签按当前态翻转。
    static unsafe void ShowMoreMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        var st = CmdStateNow();
        // A2-B4: buttons folded at narrow widths stay reachable from More (same IDs, same dispatch).
        bool justSeparated = false;
        var foldedIds = ToolbarView.MoreProjection(Toolbar.FoldedCommands);
        if (State.Appearance.Desk)   // U6a：desk 顶栏没有「打开」钮（在「+」与这里）
        {
            var open = Commands.All.First(c => c.Id == 1003);
            _ = AppendMenuW(menu, open.IsEnabled(st) ? 0u : 1u, (IntPtr)1003, open.Label + "\t" + open.Accel);
            _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);
        }
        foreach (var c in Commands.All)
        {
            if (Array.IndexOf(foldedIds, c.Id) < 0) continue;
            uint ff = (c.IsEnabled(st) ? 0u : 1u) | (c.IsChecked(st) ? 0x0008u : 0u);
            string fl = c.Label + (c.Id == 1001 && st.Dirty ? "（未保存）" : "");
            if (!c.IsEnabled(st) && Commands.DisabledReason(c.Id, st) is string why) fl += "（" + why + "）";   // A3:灰显时说明原因
            _ = AppendMenuW(menu, ff, (IntPtr)c.Id, fl + (c.Accel.Length > 0 ? "\t" + c.Accel : ""));
        }
        if (foldedIds.Length > 0) { _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null); justSeparated = true; }
        foreach (var c in Commands.OnFace(CmdFace.More))
        {
            if (State.Appearance.Desk && c.Id is 1004 or 1014 or 1015 or 1016 or 2096) continue;   // U11b：纸张、字体、字号在外观面板里
            if (c.Id is 1014 or 1004 or 1011 or 2093 && !justSeparated) _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);   // 审查修复:组界=查找替换|字号|主题|文件|说明(原 1013 前插线把查找/替换拆散)
            justSeparated = false;
            string label = c.Id == 1004 ? (RenderResources.DarkMode ? "切换到浅色主题" : "切换到深色主题") : c.Label;
            uint f = (c.IsEnabled(st) ? 0u : 1u) | (c.IsChecked(st) ? 0x0008u /*MF_CHECKED*/ : 0u);
            _ = AppendMenuW(menu, f, (IntPtr)c.Id, label + (c.Accel.Length > 0 ? "\t" + c.Accel : ""));
        }
        AddProductMenus(menu);
        if (_recent.Count > 0)
        {
            _ = AppendMenuW(menu, 0x0800, (IntPtr)0, null);
            IntPtr sub = CreatePopupMenu();
            if (sub != IntPtr.Zero)
            {
                for (int i = 0; i < _recent.Count && i < 5; i++)
                    _ = AppendMenuW(sub, 0, (IntPtr)(2100 + i), System.IO.Path.GetFileName(_recent[i]).Replace("&", "&&"));
                _ = AppendMenuW(menu, 0x0010, sub, "最近打开");
                DestroyMenuAfterTrack(sub);
            }
        }
        // 从"更多"钮下缘展开(菜单归属可见)
        TrackUnderButton(menu, Toolbar.RectOf(ToolbarTarget.ForCommand(ToolbarCommand.More)), ref _moreClosedAt);
    }

    // 菜单从按钮下缘展开；owner rect = 按钮矩形。
    static unsafe void TrackUnderButton(IntPtr menu, (float x, float w) button, ref long closedAt)
    {
        int px = (int)(256 * _scale), py = (int)(ToolH * _scale);   // 审查修复:兜底也乘 scale
        RECT owner = default;
        if (button.w > 0)
        {
            px = (int)(button.x * _scale);
            // 用户反馈修复:owner rect=按钮矩形——菜单开着时再点钮,点击落在归属区内→只关菜单且不穿透重开
            // (否则 TrackPopupMenu 把"外点击"关闭后的那一下透传给按钮,BtnAction 立即重开=看起来关不掉)
            owner.L = (int)(button.x * _scale);
            owner.R = (int)((button.x + button.w) * _scale);
        }
        POINT origin = default;   // 客户区 (0,0) 的屏幕坐标 → owner 转屏幕系
        _ = ClientToScreen(_hwnd, ref origin);
        owner.L += origin.X; owner.R += origin.X;
        owner.T = origin.Y; owner.B = origin.Y + py;
        POINT anchor = new POINT { X = px, Y = py };
        _ = ClientToScreen(_hwnd, ref anchor);
        _ = SetForegroundWindow(_hwnd);
        int id = TrackPopupMenuOwner(menu, 0x0002 | 0x0100, anchor.X, anchor.Y, 0, _hwnd, ref owner);
        _ = DestroyMenu(menu);
        DrainSubMenus();
        // 审查修复:仅"点击落在钮归属区(会穿透回按钮)"才记吞击时刻;Esc/点别处关闭不计时,不误吞真心再点击
        if (id == 0 && GetCursorPos(out var curP) && curP.X >= owner.L && curP.X < owner.R && curP.Y >= owner.T && curP.Y < owner.B)
            closedAt = Environment.TickCount64;
        if (id != 0) _ = Externs.SendMessageW(_hwnd, Externs.WM_COMMAND, (IntPtr)id, IntPtr.Zero);
    }

    static bool HasComposition => (_editing && Editor?.IsComposing == true) || Find.IsComposing;
    static void CompositionChanged()
    {
        if (_closingNormally || _hwnd == 0 || !Externs.IsWindow(_hwnd)) return;
        UpdateTitle();
        InvalidateToolbar(_hwnd);
        if (!HasComposition && !NativeForm.IsActive)
        {
            if (_editing && _hEdit != 0) { EditSession.RecordChange(Session); Session.ReconcileEditor(Externs.WindowText(_hEdit)); UpdateTitle(); }
            if (_dirty) ScheduleDraft();
            if (_handoffDeferred) { _handoffDeferred = false; Externs.PostMessage(_hwnd, InstanceHandoff.DispatchMessage, 0, 0); }
        }
    }

    static void OnTextChanged() { MarkDirty(); UpdateTitle(); }

    static void EnsureEdit()
    {
        if (Editor != null) return;
        Editor = new EditorControl(_hwnd, _scale, OnTextChanged,
            () => TrySaveClipboardImage(out string path) ? path : null);
        EditSession = new EditorSession(Editor);
        Editor.CompositionChanged += CompositionChanged;
        Editor.UndoRequested = () => ApplyTextHistory(false);
        Editor.RedoRequested = () => ApplyTextHistory(true);
        Editor.HasUndo = () => Session.EditHistory.CanUndo;
        Editor.HasRedo = () => Session.EditHistory.CanRedo;
        Editor.CaretMoved = () => { if (EditBarShown) InvalidateFooter(); };   // U9a：页脚「第 n 行」
        ApplyLetter();
        ApplyPreferences();
    }

    static float RevealInset() => _editing || !Find.Visible ? 0f : OverlayGeometry.RevealInset(ContentTop, Find.Box);

    static void LayoutControls()
    {
        if (Editor != null) Editor.BottomInset = EditorBottomInset;   // U9a：desk 编辑时为页脚让出底部
        Editor?.Resize(ContentTop);
        Find.Reposition();
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        if (_offY < 0f) _offY = -MathF.Min(-_offY, RevealInset());   // 查找关闭/变形后收回越界(关闭时回到 0)
        UpdateScroll();
        var oldFocus = Toolbar.FocusedTarget;
        PrepareToolbar();
        if (oldFocus != Toolbar.FocusedTarget) UpdateTitle();
        Externs.InvalidateRect(_hwnd, 0, 0);
    }

    static void ShowTabMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            var snapshot = BuildTabInfo();
            for (int i = 0; i < snapshot.Count; i++)
            {
                string name = snapshot[i].MenuLabel(usePath: true);
                AppendMenuW(menu, snapshot[i].Active ? 8u : 0u, (nint)(i + 1), name.Replace("&", "&&"));
            }
            var point = new POINT { X = (int)(Toolbar.OverflowRect.x * _scale), Y = (int)(ToolH * _scale) };
            ClientToScreen(_hwnd, ref point);
            int chosen = TrackPopupMenu(menu, 0x0102, point.X, point.Y, 0, _hwnd, 0);
            if (chosen > 0 && chosen <= snapshot.Count)
            {
                if (snapshot[chosen - 1].IsHome) ShowHome();   // U10
                int index = Tabs.IndexOfId(snapshot[chosen - 1].Id);
                if (index >= 0) SwitchTab(index);
            }
        }
        finally { DestroyMenu(menu); }
    }

    static bool ToggleEdit()
    {
        if (HasComposition || (!_editing && Session.ReadOnly)) return false;
        if (State.Appearance.Desk) { HideReturnHint(); WakeTitlebar(); HideSelectionBubble(); CloseJuan(false); }   // U5c：切换模式清除返回书签；U6c：顶栏恢复；U7：收起气泡；U8：关卷目
        if (!_editing)
        {
            EnsureEdit();
            SyncEditor();
            _ = Externs.ShowWindow(_hEdit, 5);
            _editing = true;
            ToggleMainScrollbar(false);   // 编辑模式:隐藏主窗口滚动条(EDIT 自带)
            LayoutControls();
            _ = Externs.SetFocus(_hEdit);
            if (!EditSession.RestorePosition(Session))
            {
                int li;
                if (Doc.SourceOffsetAtY(_offY) is int sourceOffset)
                    li = DocumentCoordinates.ToEditor(Session.Text, sourceOffset);
                else
                {
                    float ratio = Doc.TotalH > 0 ? MathF.Min(1f, _offY / MathF.Max(1f, Doc.TotalH - ContentHeight)) : 0f;
                    int target = (int)(ratio * EmLineCount(_hEdit));
                    li = (int)Externs.SendMessageW(_hEdit, 0x00BB /*EM_LINEINDEX*/, (IntPtr)target, IntPtr.Zero);
                }
                _ = Externs.SendMessageW(_hEdit, 0x00B1 /*EM_SETSEL*/, li, li);
                _ = Externs.SendMessageW(_hEdit, 0x00B7 /*EM_SCROLLCARET*/, IntPtr.Zero, IntPtr.Zero);
            }
            if (Find.Visible) Find.Next(false);
            UpdateTitle();
            return true;
        }
        else
        {
            try { EditSession.PreviewDraft(Session); }
            catch (Exception ex)
            {
                AppLog.Error("preview draft", ex);
                _ = Externs.MessageBoxW(_hwnd, "预览失败，编辑内容已保留，请继续编辑或保存。\n" + ex.Message, "预览草稿", 0x30);
                return false;
            }
            if (Session.Dirty) ScheduleDraft();
            else ClearDraft(Session); // Undo back to the saved text must not leave an older recovery draft.
            // Native word-wrap lines first resolve to a UTF-16 source position;
            // parser ranges then select the owning block (containers are coarse).
            int firstLine = (int)Externs.SendMessageW(_hEdit, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, IntPtr.Zero, IntPtr.Zero);
            int totalLines = EmLineCount(_hEdit);
            float ratio = totalLines > 0 ? MathF.Min(1f, (float)firstLine / totalLines) : 0f;
            int firstCharacter = (int)Externs.SendMessageW(_hEdit, 0x00BB /*EM_LINEINDEX*/, (IntPtr)firstLine, IntPtr.Zero);
            int sourceOffset = DocumentCoordinates.FromEditor(Session.Text, Math.Max(0, firstCharacter));
            _offY = Snap(Doc.YAtSourceOffset(sourceOffset) ?? ratio * Doc.TotalH);
            _offY = Snap(MathF.Min(_offY, MaxOff()));
            _ = Externs.ShowWindow(_hEdit, 0);
            _editing = false;
            ToggleMainScrollbar(true);
            LayoutControls();
            if (Find.Visible) Find.Next(false);
            UpdateScroll();
            _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
            _ = Externs.SetFocus(_hwnd);
            UpdateTitle();
            return true;
        }
    }

    static int EmLineCount(IntPtr h) => (int)Externs.SendMessageW(h, 0x00BA /*EM_GETLINECOUNT*/, IntPtr.Zero, IntPtr.Zero);

    // 拖入图片:拷贝到文档目录,编辑态插入引用/预览态追加到末尾
    static void DropImage(string srcPath)
    {
        if (Session.ReadOnly) return;
        string dir = DocDir();
        if (string.IsNullOrEmpty(dir)) { _ = Externs.MessageBoxW(_hwnd, "请先保存文档后再拖入图片", "拖入图片", 0); return; }
        string ext = Path.GetExtension(srcPath);
        int n = 1;
        string dst;
        do { dst = Path.Combine(dir, "image" + n + ext); n++; } while (File.Exists(dst));
        try { File.Copy(srcPath, dst); }
        catch (Exception ex)
        {
            AppLog.Error("copy image", ex);
            _ = Externs.MessageBoxW(_hwnd, "图片拷贝失败: " + ex.Message, "拖入图片", 0);
            return;
        }
        string ins = "![](" + Path.GetFileName(dst) + ")";
        if (_editing && _hEdit != IntPtr.Zero)
        {
            fixed (char* pch = ins) _ = Externs.SendMessageW(_hEdit, 0x00C2 /*EM_REPLACESEL*/, (IntPtr)1, (IntPtr)pch); MarkDirty();
            UpdateTitle();
        }
        else
        {
            // 预览态:追加到文本末尾并刷新
            Session.ReplaceText(Session.Text + Session.LineEnding + ins + Session.LineEnding);
            ScheduleDraft();
            SyncEditor();
            InvalidateToolbar(_hwnd);
            UpdateTitle();
            _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        }
    }

    // U2：desk 界面的纸张随系统明暗重新解析（跟随系统时生效）。
    static void ApplyPaper()
    {
        var paper = State.Appearance.Colors(SystemPrefersDark());
        RenderResources.SetPaper(paper);
        RenderResources.ApplyTheme(paper.Dark);
        RefreshLook();
    }

    // U6a：外观菜单选纸（2300–2304）。
    static void ChoosePaper(Paper paper)
    {
        if (!State.Appearance.Desk) return;
        State.Appearance.Choose(paper);
        ApplyPaper();
        ApplyScrollbarTheme();
        ApplyWindowChrome();
        SaveConfig();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        if (_hEdit != IntPtr.Zero) _ = Externs.InvalidateRect(_hEdit, IntPtr.Zero, 0);
        Find.InvalidateControls();
    }

    static void ToggleTheme()
    {
        if (State.Appearance.Desk) { State.Appearance.ToggleDark(SystemPrefersDark()); ApplyPaper(); }   // Ctrl+D：浅色纸张 ↔ 灯下
        else RenderResources.ApplyTheme(!RenderResources.DarkMode);
        ApplyScrollbarTheme();
        ApplyWindowChrome();
        SaveConfig();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        if (_hEdit != IntPtr.Zero) _ = Externs.InvalidateRect(_hEdit, IntPtr.Zero, 0); // 编辑模式:EDIT 子控件需单独重绘才跟随主题
        Find.InvalidateControls();
    }

    static bool TextEqualsEdit(string s)
    {
        int len = Externs.GetWindowTextLengthW(_hEdit);
        var sb = new StringBuilder(len + 1);
        _ = Externs.GetWindowTextW(_hEdit, sb, sb.Capacity);
        return sb.ToString() == s;
    }

    static void StopAutoScroll()
    {
        _pointer.StopAutoScroll();
        if (_hwnd != IntPtr.Zero) _ = Externs.KillTimer(_hwnd, (IntPtr)TIMER_AUTOSCROLL);
    }

    // 拖拽接近视口上/下边缘 40px 内 → 启动持续滚动(lite-xl/Scintilla 同款定时器模式)
    static void UpdateAutoScroll(IntPtr h, int mx, int my)
    {
        _ = Externs.GetClientRect(h, out var cr);
        int dir = ScrollGeometry.AutoScrollDirection(my, (int)(ContentTop * _scale), cr.B - cr.T);
        if (_pointer.UpdateAutoScrollDir(dir))
        {
            if (dir != 0) _ = Externs.SetTimer(h, (IntPtr)TIMER_AUTOSCROLL, 30, IntPtr.Zero);
            else _ = Externs.KillTimer(h, (IntPtr)TIMER_AUTOSCROLL);
        }
    }

    // ---------- 工具栏 ----------
    static void PrepareToolbar()
    {
        if (_hwnd == 0 || RenderResources.Rt == 0) return;
        var rect = ClientRect();
        Toolbar.Prepare((rect.R - rect.L) / _scale, CmdStateNow(), ToolbarTabInfo());
    }

    static void RestoreToolbarFocus()
    {
        nint target = _toolbarReturnFocus;
        _toolbarReturnFocus = 0;
        if (target == _hwnd && _editing) target = _hEdit;
        bool valid = target != 0 && Externs.IsWindow(target) && IsWindowVisible(target)
            && (target == _hwnd || Externs.IsChild(_hwnd, target));
        Externs.SetFocus(valid ? target : (_editing && _hEdit != 0 ? _hEdit : _hwnd));
    }

    static void ToggleToolbarFocus()
    {
        if (HasComposition || NativeForm.IsActive) return;
        PrepareToolbar();
        if (Toolbar.HasFocus) { Toolbar.ClearFocus(); RestoreToolbarFocus(); }
        else
        {
            _toolbarReturnFocus = Externs.GetFocus();
            Toolbar.ToggleFocus();
            if (Toolbar.HasFocus) WakeTitlebar();   // U6c：焦点进入顶栏
            if (Toolbar.HasFocus) Externs.SetFocus(_hwnd);
        }
        UpdateTitle(); InvalidateToolbar(_hwnd);
    }

    // 经典界面单文档不显示标签；desk 顶栏总显示（演示：标签就是文档的名字）。
    static IReadOnlyList<ToolbarTab> ToolbarTabInfo() => Tabs.Count > (State.Appearance.Desk ? 0 : 1) ? BuildTabInfo() : null;

    static void InvokeToolbarTarget(ToolbarTarget target)
    {
        if (HasComposition || NativeForm.IsActive) return;
        PrepareToolbar();
        if (!Toolbar.IsEnabled(target)) return;
        if (target.Part == ToolbarPart.Command) BtnAction((int)target.Command);
        else
        {
            if (target.DocumentId == Home.TabId) { ShowHome(); PrepareToolbar(); Externs.SetFocus(ActiveFocusTarget()); UpdateTitle(); InvalidateToolbar(_hwnd); return; }   // U10
            int index = Tabs.IndexOfId(target.DocumentId);
            if (index < 0) return;
            if (target.Part == ToolbarPart.Close) CloseTabAt(index);
            else SwitchTab(index);
            PrepareToolbar();
            Externs.SetFocus(ActiveFocusTarget());
        }
        UpdateTitle(); InvalidateToolbar(_hwnd);
    }

    static int HitBtn(int mx, int my) =>
        RenderResources.Rt == IntPtr.Zero ? -1 : Toolbar.HitButton(mx / _scale, my / _scale, ToolH);

    // 左键按下的共享前段(WM_LBUTTONDOWN 与工具栏区的 WM_LBUTTONDBLCLK 共用):任何按下都退出工具栏虚拟焦点;
    // 位于工具栏时开始按压跟踪并返回 true,由释放决定是否触发动作。
    static bool ToolbarPointerDown(IntPtr h, int mx, int my)
    {
        PrepareToolbar();
        // 先按“按下前屏幕上呈现的样子”判定按压目标(未显示的关闭叉只激活标签,评审 #2),
        // 再记录输入来源切换常显关闭模式。
        bool inBar = my >= 0 && my < (int)(ToolH * _scale);
        if (inBar && Toolbar.BeginPointer(mx / _scale, my / _scale)) Externs.SetCapture(h);
        Toolbar.ObservePointerSource(GetMessageExtraInfo(), press: true);
        Toolbar.ClearFocus(); _toolbarReturnFocus = 0;
        if (!inBar) return false;
        UpdateTitle(); InvalidateToolbar(h);
        return true;
    }

    static void ToggleToc()
    {
        if (!CmdEnabled(1006)) return;
        if (State.Appearance.Desk) { ToggleJuan(); return; }   // U8：desk 用目录卷目，不动侧栏与 toc= 偏好
        KeepReadingPlace(() => Doc.SetToc(!Doc.ShowToc));   // 常驻目录让出宽度会重排
        _cfgToc = OverlayGeometry.TocPreference(_cfgToc, Doc.ShowToc, Doc.TocPinned);   // 全局偏好,后续打开的新文档继承(S81:只记常驻目录的开关)
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
    }

    static void BtnAction(int idx)
    {
        if (HasComposition) return;
        switch (idx)
        {
            case 0: OpenFileDlgNative(); break;
            case 1: ToggleEdit(); break;
            case 2: if (CmdEnabled(1001)) _ = SaveFile(); break;   // 审查修复:谓词单一真相源(原硬编码副本与表分叉)
            case 3: if (Find.Visible) Find.Close(); else Find.Open(); break;   // 用户反馈修复:查找钮=开关(第二次点击关栏);Ctrl+F 保持"打开+聚焦"
            case 4: if (!_editing) ToggleToc(); break;
            case 6: ShowTabMenu(); break;
            case 5:
                if (State.Appearance.Desk)   // 试用修复：desk 用自绘菜单（演示 #morePop）；键盘打开时焦点留在「更多」钮上
                {
                    bool moreByKeyboard = Toolbar.HasFocus;
                    ToggleMore(moreByKeyboard);
                    if (moreByKeyboard) return;
                    break;
                }
                if (Environment.TickCount64 - _moreClosedAt < 400) break;   // owner rect 语义=关菜单且点击穿透回按钮:这一击是"关闭"动作本身,吞掉防关了即重开
                ShowMoreMenu(); break;   // I7:"更多"=加速键表的曝光投影
            case 7: if (_editing) ToggleEdit(); break;   // U6a：分段按钮「阅读」
            case 8:   // U11b：外观面板；键盘打开时焦点留在「外观」钮上，Esc 收起后仍在原处
                bool fromKeyboard = Toolbar.HasFocus;
                ToggleLook(fromKeyboard);
                if (fromKeyboard) return;
                break;
            case 9: TogglePalette(); return;   // U11：印章「阅」
        }
        if (idx == 3) { Toolbar.ClearFocus(); _toolbarReturnFocus = 0; }
        else Externs.SetFocus(ActiveFocusTarget());
    }

    static IntPtr ActiveFocusTarget() =>
        Toolbar.HasFocus || !_editing ? _hwnd : _hEdit;

    // I7(N7):表命令统一过 enabled 谓词闸——菜单(code=0)/加速键(code=1)/键盘链共用,
    // 修掉"Ctrl+S 不查脏直调 SaveFile 空写盘清脏"与按钮灰显的分叉;不在表内的 id(EN_*/3001+/2100+)恒过。
    static bool CmdEnabled(int cmd)
    {
        if ((HasComposition || NativeForm.IsActive) && Commands.RequiresStableText(cmd)) return false;
        foreach (var c in Commands.All)
            if (c.Id == cmd) return c.IsEnabled(CmdStateNow());
        return true;
    }

    // I11→B1:serif 族探测迁入 FontProbe(实测暴露旧实现两个 bug:键名含空格永不命中 / 写死族名≠DWrite 真名)。
    static string ProbeSerifFamily() => FontProbe.ProbeFamily();

    static void ToggleZen()
    {
        Zen.Enabled = !Zen.Enabled;
        InvalidateToolbar(_hwnd);
        SaveConfig();
    }

    static void ToggleSerif()
    {
        if (RenderResources.SerifFamily == null)
        {
            _ = Externs.MessageBoxW(_hwnd, "未检测到可用的衬线中文字体。\n" +
                "可使用已安装的宋体（SimSun），或安装 Source Han Serif SC（思源宋体）、Noto Serif CJK SC、Noto Serif SC（均为 OFL 免费授权）。" +
                "安装后重开本程序即可启用；程序不会自动下载字体。", "衬线正文", 0x40);
            return;
        }
        Doc.SetSerif(!RenderResources.SerifMode);
        _offY = Snap(MathF.Min(_offY, MaxOff()));
        UpdateScroll();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        SaveConfig();
        RefreshLook();
    }

    static CmdState CmdStateNow() => new(_editing, _dirty, State.Appearance.Desk ? JuanOpen : Doc.TocVisible, Find.Visible, Doc.HasSel, _bodyFont, RenderResources.SerifMode, State.Appearance.Desk || OverlayGeometry.TocWidth(_viewW / _scale) > 0f, _path.Length > 0, Navigation.CanBack, Navigation.CanForward, CodeForCopy() != null, HasComposition, Session.ReadOnly, Session.Pinned, Tabs.Count, Tabs.CanReopen, Session.EditHistory.CanUndo, Session.EditHistory.CanRedo, Zen.Enabled, RenderResources.Reading.DefaultBodySize, HomeShown);

    // 审查修复:置脏统一入口——clean→dirty 翻转即刷工具栏(warn 点与标题 * 同帧;反向消脏由 SaveFile 全窗失效覆盖)
    static void MarkDirty()
    {
        if (_editing && !_syncingEditor && Editor?.IsMutating == true) { Editor.NoteMutation(); return; }
        bool was = _dirty;
        if (_editing && !_syncingEditor && _hEdit != 0 && !Editor.IsComposing) EditSession.RecordChange(Session);
        if (_editing && _hEdit != 0 && !Editor.IsComposing) Session.ReconcileEditor(Externs.WindowText(_hEdit));
        else Session.MarkDirty();
        if (_dirty) ScheduleDraft(); else ClearDraft(Session);
        if (was != _dirty) { InvalidateToolbar(_hwnd); InvalidateEditBar(); }   // U9a：编辑条状态与保存钮
        if (!was && _dirty) NoteInk(fresh: true);   // U9c：落下一滴墨
    }

    static void UpdateTitle()
    {
        _tabInfoCache = null;
        PrepareToolbar();
        string notice = Session.DiskState switch
        {
            DiskState.Changed => " [磁盘已更改，右键可刷新]",
            DiskState.Missing => " [磁盘文件已删除，当前内容保留]",
            DiskState.Unavailable => " [磁盘暂不可读，右键可重试刷新]",
            _ => ""
        };
        if (Session.ReadOnly) notice += " [只读]";
        if (_autoSavePaused.Contains(Session.RecoveryId)) notice += " [自动保存已暂停，请手动处理]";
        if (HasComposition) notice += " [输入法组词中，请先确认候选]";
        if (_imageTask != null) notice += " [图片加载中，再点图片可取消]";
        if (Toolbar.HasFocus) notice += " [工具栏：" + Toolbar.FocusDescription + "]";
        Externs.SetWindowTextW(_hwnd, (HomeShown ? Home.TabTitle : (_dirty ? "*" : "") + _name) + " — MD 查看器" + (HomeShown ? "" : notice));
        // The immutable tab snapshot was refreshed before PrepareToolbar above.
    }

    // ---------- 滚动 ----------
    // U12：窗口测试读滚动位置的只读查询（desk 没有系统滚动条，GetScrollInfo 读不到）。
    // w：0 位置、1 上限、2 一页；单位与 classic 滚动条相同（物理像素）。不改任何状态。
    const uint QueryScrollMessage = 0x8000 + 0x3A;   // WM_APP + 0x3A

    static IntPtr QueryScroll(int which)
    {
        var si = ScrollGeometry.ScrollBarInfo(SIF_ALL, Doc.TotalH, ContentHeight, MathF.Max(0f, _offY), _scale);
        return (IntPtr)(which switch { 1 => si.nMax, 2 => (int)si.nPage, _ => si.nPos });
    }

    static void UpdateScroll()
    {
        if (State.Appearance.Desk) return;   // U5a：SetScrollInfo 会把系统滚动条自动显示出来
        var si = ScrollGeometry.ScrollBarInfo(SIF_ALL, Doc.TotalH, ContentHeight, MathF.Max(0f, _offY), _scale);
        _ = Externs.SetScrollInfo(_hwnd, SB_VERT, in si, 1);
    }

    static void StopSmoothScroll()
    {
        _smooth.Stop();
        _ = Externs.KillTimer(_hwnd, (IntPtr)TIMER_SMOOTH);
    }

    // 量化到物理像素网格(D2D 文本竖直对齐的最小稳定步长)
    static float Snap(float dip) => ScrollGeometry.Snap(dip, _scale);

    // 当前文档滚动上限,向下落到物理像素网格(SetOffset 与所有钳制点统一口径;否则 Snap 向上取整会越上限一整格,底部首格滚轮回跳 1px)
    static float MaxOff() => ScrollGeometry.MaxOffset(Doc.TotalH, ContentHeight, _scale);

    // stopAnim=false 仅供缓出定时器自用:用户显式跳转(TOC/脚注/滚动条/翻页键)应打断进行中的缓动,
    // 否则一次性跳转会被仍在收敛的旧 target 拉回
    static void SetOffset(float offDip, bool stopAnim = true, bool allowTopInset = false)
    {
        if (stopAnim) StopSmoothScroll();
        float offsetBefore = _offY;
        // 像素网格量化与上限钳制在 ScrollGeometry.ClampOffset:偏移落到物理像素网格后每帧整体位移整数像素,行间相对位置恒定。
        _offY = ScrollGeometry.ClampOffset(offDip, MaxOff(), _scale);
        // 查找命中导航:文档开头被查找覆盖层挡住时,允许向上越界至多被挡高度(仅此路径;关闭查找时收回)
        if (allowTopInset && offDip < 0f) _offY = -MathF.Floor(MathF.Min(-offDip, RevealInset()) * _scale) / _scale;
        if (_offY != offsetBefore) HideSelectionBubble();   // U7：滚动即收起选区气泡（演示稿）
        if (!State.Appearance.Desk)
        {
            var si = ScrollGeometry.ScrollBarInfo(SIF_POS | SIF_RANGE | SIF_PAGE, Doc.TotalH, ContentHeight, MathF.Max(0f, _offY), _scale);
            _ = Externs.SetScrollInfo(_hwnd, SB_VERT, in si, 1);
        }
        InvalidateContent(_hwnd);   // 滚动只重绘正文条带,工具栏像素不变不参与
        ObserveQuiet();   // U6c：读时退后
        _ = Externs.UpdateWindow(_hwnd);
    }

    // ---------- 窗口过程 ----------
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        try
        {
            return WndProcInner(h, m, w, l);
        }
        catch (Exception ex)
        {
            Log("WNDPROC msg=0x" + m.ToString("X") + " " + ex + Environment.NewLine + (ex.StackTrace ?? ""));
            return Externs.DefWindowProcW(h, m, w, l);
        }
    }

    static IntPtr WndProcInner(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case 0x002B: // WM_DRAWITEM: native search buttons keep native keyboard semantics.
                if (Find.DrawButton(l)) return (nint)1;
                break;
            case 0x0024: // WM_GETMINMAXINFO
                return OnGetMinMaxInfo(h, m, w, l);
            case Externs.WM_PAINT:
                return OnPaint(h, m, w, l);
            case 0x0014: // WM_ERASEBKGND — D2D 每帧全幅不透明绘制,禁止擦除以免闪底
                return (IntPtr)1;
            case QueryScrollMessage: return QueryScroll((int)w);   // U12：窗口测试的只读查询
            case Externs.WM_SIZE:
                CloseLook();   // U11b：面板按窗口位置摆放，窗口变了就收起
                CloseMore();
                if (CustomFrame && Toolbar.Maximized != IsZoomed(h)) { Toolbar.Maximized = IsZoomed(h); InvalidateToolbar(h); }
                return OnSize(h, m, w, l);
            case 0x0083: // WM_NCCALCSIZE（U6b）
                return OnNcCalcSize(h, m, w, l);
            case 0x0084: // WM_NCHITTEST（U6b）
                return OnNcHitTest(h, m, w, l);
            case 0x00A0:
            case 0x00A1:
            case 0x00A2:
            case 0x00A3:
            case 0x02A2: // 非客户区鼠标（U6b 窗口按钮）
                if (OnNcMouse(h, m, w, l, out var ncResult)) return ncResult;
                break;
            case Externs.WM_VSCROLL:
                return OnVScroll(h, m, w, l);
            case Externs.WM_MOUSEHWHEEL:
                return OnMouseHWheel(h, m, w, l);
            case Externs.WM_MOUSEWHEEL:
                return OnMouseWheel(h, m, w, l);
            case 0x0201: // WM_LBUTTONDOWN
                return OnLButtonDown(h, m, w, l);
            case 0x0203: // WM_LBUTTONDBLCLK — 双击选词后仍按住拖动 = 词选扩展(Windows 标准行为),必须同样捕获
                return OnLButtonDoubleClick(h, m, w, l);
            case Externs.WM_MOUSEMOVE:
                return OnMouseMove(h, m, w, l);
            case 0x02A3: // WM_MOUSELEAVE — 鼠标离窗,清除按钮悬停高亮（原写成 0x02A2＝WM_NCMOUSELEAVE，从未触发）
                return OnMouseLeave(h, m, w, l);
            case 0x0113: // WM_TIMER — id 1:拖到视口边缘持续滚动 / id 4:平滑滚动缓出
                return OnTimer(h, m, w, l);
            case 0x001F: // WM_CANCELMODE must also release an owned capture, then reach DefWindowProc so menus and other system modal loops end.
                return OnCancelMode(h, m, w, l);
            case 0x0216: // WM_CAPTURECHANGED — cancel only our pending UI/document gestures.
                return OnCaptureChanged(h, m, w, l);
            case Externs.WM_LBUTTONUP:
                return OnLButtonUp(h, m, w, l);
            case 0x007B: // WM_CONTEXTMENU
                return OnContextMenu(h, m, w, l);
            case Externs.WM_SETCURSOR:
                return OnSetCursor(h, m, w, l);

            case 0x0006: // WM_ACTIVATE — 标题栏与正文同色后,激活态唯一载体是 border 色
                _winActive = WindowChrome.IsActive((int)w & 0xFFFF);
                if (!_winActive) { CloseLook(); CloseMore(); }
                ApplyWindowChrome();
                if (CustomFrame) { Toolbar.WindowActive = _winActive; InvalidateToolbar(h); }
                if (_winActive) CheckDiskChange();
                break;
            case 0x0015: // WM_SYSCOLORCHANGE
            case 0x001A: // WM_SETTINGCHANGE (theme/high-contrast changes)
                if (State.Appearance.Desk) ApplyPaper();   // 跟随系统的纸张在这里切换
                else RenderResources.ApplyTheme(RenderResources.DarkMode);
                ApplyWindowChrome();
                ApplyScrollbarTheme();
                Externs.InvalidateRect(h, 0, 0);
                break;
            case 0x0133: // WM_CTLCOLOREDIT
                return RenderResources.ControlColors(w);
            case 0x0138: // WM_CTLCOLORSTATIC
                if (Find.IsCountControl(l)) return Find.Layout switch { FindLayout.Panel => RenderResources.SurfaceControlColors(w), FindLayout.Desk or FindLayout.DeskEdit or FindLayout.DeskEditReplace => RenderResources.MutedControlColors(w), _ => RenderResources.ControlColors(w) };
                break;
            case Externs.WM_KEYDOWN:
                return OnKeyDown(h, m, w, l);
            case Externs.WM_DROPFILES:
                return OnDropFiles(h, m, w, l);
            case Externs.WM_COMMAND:
                return OnCommand(h, m, w, l);
            case Externs.WM_CLOSE:
                return OnClose(h, m, w, l);
            case InstanceHandoff.CopyDataMessage:
                return Handoff.Accept(l, () => Externs.PostMessage(h, InstanceHandoff.DispatchMessage, 0, 0)) ? 1 : 0;
            case InstanceHandoff.DispatchMessage:
                return OnHandoffDispatch(h, m, w, l);
            case 0x02E0: // WM_DPICHANGED — 窗口拖到不同缩放的显示器:更新缩放并采用系统建议矩形
                return OnDpiChanged(h, m, w, l);
            case Externs.WM_DESTROY:
                return OnDestroy(h, m, w, l);
        }
        return Externs.DefWindowProcW(h, m, w, l);
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr h, ref POINT p);   // I7:"更多"菜单锚点(钮下缘→屏幕坐标)

    const int GWL_STYLE = -16;
    const uint SWP_FRAMECHANGED = 0x20, SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    static extern int DwmSetWindowAttributeRef(IntPtr h, int attr, uint* val, int size);
    const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;   // Win11 22000+

    // 窗口镶边"纸与墨"(PLAN-UI-9PLUS §3.0):标题栏与正文同 canvas,分界交给 border 区域线;激活态唯一载体=border 色。
    // COLORREF(BGR) 本地计算——不读 ThemeColors:Main 首调在 InitGraphics 前,静态色未初始化会闪黑。
    static bool _winActive = true;
    static void ApplyWindowChrome()
    {
        var attrs = RenderResources.ActivePaper is PaperColors paper && !RenderResources.HighContrast
            ? WindowChrome.GetDwmAttributes(paper, _winActive)
            : WindowChrome.GetDwmAttributes(RenderResources.DarkMode, _winActive);
        uint caption = attrs.Caption, border = attrs.Border, text = attrs.Text, imm = attrs.ImmersiveDarkMode;
        _ = DwmSetWindowAttributeRef(_hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, &imm, 4);
        _ = DwmSetWindowAttributeRef(_hwnd, DWMWA_BORDER_COLOR, &border, 4);
        _ = DwmSetWindowAttributeRef(_hwnd, DWMWA_CAPTION_COLOR, &caption, 4);
        _ = DwmSetWindowAttributeRef(_hwnd, DWMWA_TEXT_COLOR, &text, 4);
    }

    // 滚动条主题:系统绘制的滚动条不走 WM_CTLCOLOREDIT,只能靠 comctl v6 + DarkMode_Explorer(Win10 1809+;失败则保持原样)
    static void ApplyScrollbarTheme()
    {
        string theme = WindowChrome.GetScrollbarTheme(RenderResources.DarkMode, RenderResources.HighContrast);
        int hr1 = Externs.SetWindowTheme(_hwnd, theme, null);
        // I10:主题覆盖全部子控件(原只 _hwnd/_hEdit——查找框/替换钮/新三钮深色下滞留浅色,抽查观察的确凿根因)
        Editor?.ApplyTheme();
        ApplyLetter();   // U9b：纸张或外观变了，信笺跟着换色 / 开关
        Find.ApplyTheme();
        if (hr1 < 0) Log("SetWindowTheme 0x" + hr1.ToString("X"));
    }

    // 编辑模式下主窗口滚动条无用(EDIT 有自己的),隐藏以免出现第二条浅色竖条
    static void ToggleMainScrollbar(bool show)
    {
        if (State.Appearance.Desk) return;   // U5a：desk 始终没有系统滚动条
        IntPtr st = WindowChrome.ToggleScrollbarStyle(Externs.GetWindowLongPtrW(_hwnd, GWL_STYLE), show);
        _ = Externs.SetWindowLongPtrW(_hwnd, GWL_STYLE, st);
        _ = Externs.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);

    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint fmt);

    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);   // SIZE_T GlobalSize(HGLOBAL)
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    static int _lbHintsLeft { get => State.LightboxHintsLeft; set => State.LightboxHintsLeft = value; }
    const uint CF_DIB = 8;

    struct POINT { public int X, Y; }

    static void OpenFileDlgNative()
    {
        try
        {
            string[] picked = NativeFileDialogs.OpenMany(_hwnd);
            if (picked != null)
                foreach (string file in picked) if (!LoadFile(file, picked.Length > 1)) break;
        }
        catch (Exception ex)
        {
            AppLog.Error("open file dialog", ex);
            _ = Externs.MessageBoxW(_hwnd, "打开失败: " + ex.Message, "MDViewerNative", 0);
        }
    }
}
