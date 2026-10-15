using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MDViewerNative;

// 窗口过程的生命周期与计时器消息：WM_TIMER、拖放、关闭、单实例投递、销毁。由 WndProcInner 分派。
// E17e 从 switch 原样搬出；OnTimer 分支层面的 break 等价改为交给 DefWindowProcW；OnDropFiles 的 break 属于 for 循环，未改动。
internal static unsafe partial class Program
{
    static IntPtr OnTimer(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if ((int)w == TIMER_IMAGE) { FinishImageLoad(); return default; }
        if ((int)w == ReturnHint.TimerId) { _ = Externs.KillTimer(h, w); if (ReturnPrompt.Expire()) InvalidateContent(h); return default; }
        if ((int)w == TIMER_ANIMATION) { AdvanceAnimation(); return default; }
        if ((int)w == TIMER_FOOTER) { OnFooterTimer(h, w); return default; }
        if ((int)w == TIMER_DESK_ANIMATION) { OnDeskAnimationTimer(h, w); return default; }
        if ((int)w == TIMER_DISK)
        {
            if (DiskMonitor.TryComplete(out var observed))
            {
                _ = Externs.KillTimer(h, (IntPtr)TIMER_DISK);
                UpdateTitle();
                bool again = _diskCheckAgain || !ReferenceEquals(observed, Session);
                _diskCheckAgain = false;
                if (again) CheckDiskChange();
            }
            return default;
        }
        if ((int)w == TIMER_AUTOSAVE) { AutoSaveDocuments(); return 0; }
        if ((int)w == TIMER_DRAFT) { CheckpointDrafts(); return default; }
        if ((int)w == TIMER_SMOOTH)
        {
            // 指数缓出:步进必须走 SetOffset——直接改 _offY 时滚动条不跟随,内容与滚动条脱节再跳齐,观感即"抖动"。
            if (FrameProbeOn) _frameProbe.Restart();   // 方案 b:测的是 Step+同步上屏(UpdateWindow 内完成 WM_PAINT)的整帧耗时
            _frameInTick = FrameProbeOn;
            float next = _smooth.Step(_offY, _scale, MaxOff(), out bool settled);
            SetOffset(next, stopAnim: false);
            _frameInTick = false;
            if (FrameProbeOn) { _frameProbe.Stop(); _frameProbeMs.Add(_frameProbe.Elapsed.TotalMilliseconds); }
            if (settled) { _ = Externs.KillTimer(h, (IntPtr)TIMER_SMOOTH); FrameProbeReport(); }
            return Externs.DefWindowProcW(h, m, w, l);
        }
        if ((int)w == TIMER_AUTOSCROLL)
        {
            // 边缘持续滚动:定时器由 UpdateAutoScroll 以 TIMER_AUTOSCROLL(1) 启动
            // (原挂在从未 SetTimer 的 TIMER_BACKUP(3) 下,整个功能一直没生效)
            SetOffset(_offY + _pointer.AutoScrollDir * 10f);
            Doc.SelExtend(_pointer.LastMx / _scale, _pointer.LastMy / _scale - ContentTop + _offY);
            InvalidateContent(h);
        }
        return default;
    }

    static IntPtr OnDropFiles(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (HasComposition) { Externs.DragFinish(w); return 0; }
        // Document opens share the transactional guard; inserting an image keeps the current draft.
        var sb = new StringBuilder(2048);
        uint fileCount = Externs.DragQueryFileCount(w, 0xFFFFFFFF, IntPtr.Zero, 0);
        bool loadedDoc = false;
        for (uint di = 0; di < fileCount; di++)
        {
            uint got = Externs.DragQueryFileW(w, di, sb, (uint)sb.Capacity);
            if (!WindowLifecycle.IsValidDropLength(got, (uint)sb.Capacity)) continue;   // 空路径/超长路径截断(最多复制 cap-1 字符):跳过避免 File.Exists 误判
            string dropPath = sb.ToString(0, (int)got);
            var dropKind = WindowLifecycle.ClassifyDropPath(dropPath);
            if (dropKind == WindowLifecycle.DropKind.Document)
            {
                if (File.Exists(dropPath))
                {
                    if (!LoadFile(dropPath, newTab: loadedDoc || !string.IsNullOrEmpty(_path))) break;
                    loadedDoc = true;
                }
            }
            else if (dropKind == WindowLifecycle.DropKind.Image)
            {
                if (File.Exists(dropPath)) DropImage(dropPath);
            }
        }
        _ = Externs.DragFinish(w);
        return default;
    }

    static IntPtr OnClose(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (!WindowLifecycle.CanClose(HasComposition, NativeForm.IsActive)) return 0;
        // 非活动标签也可能脏(预览态拖入图片后切换):关窗前确认,防静默丢失
        int dirtyOthers = WindowLifecycle.CountDirtyOtherTabs(ActiveTab, Tabs.Count, ti => Tabs[ti].Dirty);
        if (dirtyOthers > 0)
        {
            if (!ResolveDocumentChanges(Tabs.Select(t => t.RecoveryId).ToArray())) return 0;
        }
        else if (GuardDirty() < 0) return default;
        foreach (var session in Tabs) ClearDraft(session);
        _closingNormally = true;
        _ = Externs.DestroyWindow(h);   // 走正常销毁流程,确保 WM_DESTROY 里的 SaveConfig() 执行
        return default;
    }

    static IntPtr OnHandoffDispatch(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (HasComposition || NativeForm.IsActive) { _handoffDeferred = true; return 0; }
        _ = SetForegroundWindow(h);
        Handoff.Drain(p =>
        {
            try
            {
                if (File.Exists(p)) LoadFile(p, true);
            }
            catch (Exception ex) { Log("handoff " + ex); }
        });
        return default;
    }

    static IntPtr OnDestroy(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        _imageCancel?.Cancel();
        _remoteImages?.Dispose();
        if (WindowLifecycle.ShouldCheckpointDraftsOnDestroy(_closingNormally)) CheckpointDrafts();
        SaveConfig();
        ImagePreview.Dispose();
        Find.Dispose();
        Editor?.Dispose();
        Tabs.Dispose();
        Toolbar.Dispose();
        _ = timeEndPeriod(1);
        Externs.PostQuitMessage(0);
        return default;
    }
}
