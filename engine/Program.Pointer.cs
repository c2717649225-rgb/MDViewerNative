using System;

namespace MDViewerNative;

// 窗口过程的指针消息：按下、双击、移动、离开、抬起、捕获变化与光标。由 WndProcInner 分派。
// E17a 从 switch 原样搬出；分支里的 break 等价改为交给 DefWindowProcW（与原先落到 switch 末尾同一调用）。
internal static unsafe partial class Program
{
    static IntPtr OnLButtonDown(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int mx = (short)(long)l, my = (short)((long)l >> 16);
        _swallowDblClk = false;
        Toolbar.HideTip();   // U6a2：按下即收起提示
        if (BubblePointerDown(mx / _scale, my / _scale)) { _swallowDblClk = true; return 0; }   // U7：选区气泡
        // Zen:隐藏的工具栏不可点(无悬停的触/笔按下先召回,不触发看不见的按钮)
        if (my < ToolH * _scale && ZenHidden) { _ = Zen.SetPointer(true); InvalidateToolbar(h); return 0; }
        if (ToolbarPointerDown(h, mx, my)) return 0;
        if (JuanPointerDown(mx / _scale, my / _scale)) { _swallowDblClk = true; return 0; }   // U8：目录卷目
        if (EditBarPointerDown(mx / _scale, my / _scale)) { _swallowDblClk = true; return 0; }   // U9a：编辑条
        UpdateTitle();
        if (my < (int)(InteractionTop * _scale)) return 0;
        if (HomeShown) { var home = HomeHit(mx / _scale, my / _scale); if (home.Kind != HomeTargetKind.None) HomeActivate(home); _swallowDblClk = true; return 0; }   // U10
        if (OverFind(mx / _scale, my / _scale)) return 0;   // 浮动面板空隙也吞掉,不落到正文
        if (OverBackFlag(mx / _scale, my / _scale)) { _swallowDblClk = true; NavigateHistory(true); return 0; }   // U5c：点返回书签
        if (CordPointerDown(h, mx / _scale, my / _scale)) { _swallowDblClk = true; return 0; }   // U5a：书签绳整列可点可拖
        if (!_editing)
        {
            // TOC 侧栏条目点击 → 跳转
            if (OverToc(mx / _scale, my / _scale))
            {
                if (Doc.TocClick(my / _scale, out float ty, TocTop))
                {
                    if (!Doc.TocPinned) Doc.SetToc(false); // D2:临时目录跳转后收起露出目标;常驻目录保持展开(偏好均不变)
                    InvalidateToolbar(h);
                    JumpWithinDocument(ty - 40f);
                }
                return default;
            }
            // D5:点击标尺任意处展开目录(显式操作,与 Ctrl+T 同路径);不跳章节
            if (OverRuler(mx / _scale, my / _scale))
            {
                ToggleToc();
                _swallowDblClk = true;
                return default;
            }
            // D2:临时目录展开时,外部点击只收起目录(偏好不变);本击被吞掉,不落到正文的选区/链接/图片
            if (Doc.TocVisible && !Doc.TocPinned)
            {
                Doc.SetToc(false);
                _swallowDblClk = true;
                InvalidateToolbar(h);
                _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
                return default;
            }
            // I8 空态最近文件:点击=打开(过脏守卫);矩形含 4px 宽限
            if (Doc.EmptyHits.Count > 0)
            {
                int ei = HitTestingGeometry.FindEmptyHit(Doc.EmptyHits, mx / _scale, my / _scale);
                if (ei >= 0)
                {
                    LoadFile(Doc.EmptyHits[ei].path);
                    return default;
                }
            }
            // 「返回原处」标签:点击等同后退(Alt+Left)
            if (OverReturnHint(mx / _scale, my / _scale)) { _swallowDblClk = true; NavigateHistory(true); return default; }
            // E5:代码块复制按钮(先于图片与选区;与右键“复制代码块”同一剪贴板路径与失败提示)
            Doc.CodeCopyAlways = Toolbar.AlwaysShowClose;
            if (Doc.CopyChipAt(mx / _scale, my / _scale - ContentTop + _offY) is string chipCode)
            {
                if (!Clipboard.SetText(chipCode, _hwnd))
                {
                    Doc.ResetCodeCopied();
                    Externs.MessageBoxW(_hwnd, "剪贴板暂不可用，请稍后重试。", "复制代码块", 0x30);
                }
                _swallowDblClk = true;
                InvalidateContent(h);
                return default;
            }
            // 图片点击 → 全屏放大
            if (Doc.TryImageHit(mx / _scale, my / _scale - ContentTop + _offY, out string imgSrc)) { OpenLightbox(imgSrc); return default; }
            Doc.SelBegin(mx / _scale, my / _scale - ContentTop + _offY);
            _ = Externs.SetCapture(h);
            _pointer.BeginSelection(mx, my);
            StopAutoScroll();
            InvalidateContent(h);
        }
        return default;
    }

    static IntPtr OnLButtonDoubleClick(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        // CS_DBLCLKS 把快速第二击变成 DBLCLK:工具栏区按普通按下处理,否则第二击丢失(正文/编辑路由不变)
        int tx = (short)(long)l, ty = (short)((long)l >> 16);
        if (ty >= 0 && ty < (int)(ToolH * _scale)) { ToolbarPointerDown(h, tx, ty); return 0; }
        if (_editing) return Externs.DefWindowProcW(h, m, w, l);
        if (HomeShown) return 0;   // U10
        if (_swallowDblClk) { _swallowDblClk = false; return 0; }
        if (OverRuler(tx / _scale, ty / _scale)) return 0;
        int mx = (short)(long)l, my = (short)((long)l >> 16);
        if (!OverBody(mx / _scale, my / _scale)) return 0;
        Doc.SelWord(mx / _scale, my / _scale - ContentTop + _offY);
        _ = Externs.SetCapture(h);
        _pointer.BeginSelection(mx, my);
        StopAutoScroll();
        InvalidateContent(h);
        return default;
    }

    static IntPtr OnMouseMove(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int mx = (short)(long)l, my = (short)((long)l >> 16);
        if (!_pointer.Tracking)
        {
            var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = h };
            _ = TrackMouseEvent(ref tme);
        }
        PrepareToolbar();
        if (_bubble != null) BubbleHover(mx / _scale, my / _scale);   // U7
        if (JuanOpen) JuanHoverAt(mx / _scale, my / _scale);   // U8
        if (EditBarShown) EditBarHoverAt(mx / _scale, my / _scale);   // U9a
        // 触摸抬起/释放捕获后,系统会在原地合成一条来源为 0 的 WM_MOUSEMOVE;只有位置真正
        // 变化的鼠标移动才退回悬停模式,否则触摸的常显关闭会立刻失效。触摸/笔的提升移动照常切换。
        nint moveSource = GetMessageExtraInfo();
        bool stationary = (int)(long)l == _lastMoveLParam;
        _lastMoveLParam = (int)(long)l;
        if (!stationary) _cordProgressMuted = false;
        bool inputModeChanged = !(stationary && !ToolbarView.IsPromotedSource(moveSource))
            && Toolbar.ObservePointerSource(moveSource);
        OverlayBox overlayBox = default;
        string overlayHint = OverJuan(mx / _scale, my / _scale) ? null : CordHint(mx / _scale, my / _scale, out overlayBox);   // U8：卷目盖住的绳子不出提示   // U5a：悬停绳子提示「已读 n%」
        if (overlayHint == null && EditBarShown) overlayHint = EditBarHint(mx / _scale, my / _scale, out overlayBox);   // U9a：编辑条按钮说明
        bool beside = overlayHint != null && !OverEditBar(mx / _scale, my / _scale);   // U6a2：绳子一列的提示放在左侧、立即显示
        if (overlayHint == null && OverRuler(mx / _scale, my / _scale)) { overlayHint = "展开目录 · Ctrl+T"; overlayBox = new OverlayBox(0f, TocTop, OverlayGeometry.RulerWidth, MathF.Max(0f, _viewH / _scale - TocTop)); }
        else if (overlayHint == null && OverToc(mx / _scale, my / _scale) && Doc.TocHint(my / _scale, TocTop, out float rowTop) is string fullTitle) { overlayHint = fullTitle; overlayBox = new OverlayBox(0f, rowTop, Doc.TocWidth, 26f); }
        Toolbar.UpdateHint(h, mx / _scale, my / _scale, _scale, BuildTabInfo(), overlayHint, overlayBox, beside);
        _pointer.Tracking = true;
        if (Toolbar.MovePointer(mx / _scale, my / _scale) | inputModeChanged) InvalidateToolbar(h);
        if (Zen.SetPointer(my < ToolH * _scale)) InvalidateToolbar(h);   // Zen:指针到顶边工具栏带即召回
        if (my < ToolH * _scale) WakeTitlebar();   // U6c：指针进入顶栏
        if (HomeShown) { HomeHoverAt(mx / _scale, my / _scale); return Externs.DefWindowProcW(h, m, w, l); }   // U10
        if (CordPointerMove(my / _scale)) return 0;   // U5a：拖书签绳时不做悬停与选区
        // E5:代码块悬停 → 复制按钮;触控/笔输入时常显。覆盖层(查找、目录)之上不算悬停。
        if (!_pointer.IsCapturing)
        {
            bool codeChanged = Doc.CodeCopyAlways != Toolbar.AlwaysShowClose;
            Doc.CodeCopyAlways = Toolbar.AlwaysShowClose;
            float hx = mx / _scale, hy = my / _scale;
            codeChanged |= !_editing && OverBody(hx, hy) && !OverFind(hx, hy) && !OverToc(hx, hy)
                ? Doc.SetCodeHover(hx, hy - ContentTop + _offY) : Doc.ClearCodeHover();
            codeChanged |= ReturnPrompt.SetHover(OverReturnHint(hx, hy));
            if (codeChanged) InvalidateContent(h);
            UpdateFooterHover(hx, hy, !_editing && OverBody(hx, hy) && !OverFind(hx, hy) && !OverToc(hx, hy));   // U4：页脚显示悬停链接
        }
        if (_pointer.IsCapturing)
        {
            if (!OverBody(mx / _scale, my / _scale)) { StopAutoScroll(); return 0; }
            _pointer.ExtendSelection(mx, my);
            Doc.SelExtend(mx / _scale, my / _scale - ContentTop + _offY);
            UpdateAutoScroll(h, mx, my);
            InvalidateContent(h);
        }
        return Externs.DefWindowProcW(h, m, w, l);
    }

    static IntPtr OnMouseLeave(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        bool hoverChanged = _pointer.OnMouseLeave() | Toolbar.LeavePointer() | Doc.ClearCodeHover() | ReturnPrompt.SetHover(false) | Zen.SetPointer(false);
        Toolbar.UpdateHint(h, -1, -1, _scale, BuildTabInfo());
        ClearFooterHover();
        ClearCordHover();
        if (_homeHover.Kind != HomeTargetKind.None) { _homeHover = HomeTarget.None; InvalidateContent(h); }   // U10
        if (_editBarHover != EditBarPart.None) { _editBarHover = EditBarPart.None; InvalidateEditBar(); }
        if (hoverChanged) _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
        return Externs.DefWindowProcW(h, m, w, l);
    }

    static IntPtr OnCancelMode(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        bool captured = Toolbar.CancelPointer() | _pointer.IsCapturing;
        captured |= _cord.Pressed; CancelCord();
        _pointer.CancelSelection(); StopAutoScroll();
        if (captured) Externs.ReleaseCapture();
        InvalidateToolbar(h);
        return Externs.DefWindowProcW(h, m, w, l);
    }

    static IntPtr OnCaptureChanged(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (Toolbar.CancelPointer()) InvalidateToolbar(h);
        CancelCord();
        _pointer.CancelSelection();
        StopAutoScroll();
        return default;
    }

    static IntPtr OnLButtonUp(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int mx = (short)(long)l, my = (short)((long)l >> 16);
        if (BubblePointerUp()) return default;   // U7：按在气泡上的那一击
        PrepareToolbar();
        if (Toolbar.EndPointer(mx / _scale, my / _scale, out var action))
        {
            Externs.ReleaseCapture();
            if (action.Part != ToolbarPart.None) InvokeToolbarTarget(action);
            InvalidateToolbar(h);
            return default;
        }
        if (CordPointerUp(my / _scale)) return default;   // U5a
        bool wasDrag = _pointer.EndSelection();
        StopAutoScroll();
        _ = Externs.ReleaseCapture();
        if (wasDrag && OverBody(mx / _scale, my / _scale)) Doc.SelExtend(mx / _scale, my / _scale - ContentTop + _offY);
        if (!_editing) RequestSelectionBubble();   // U7：松开鼠标时有选区就浮出「复制 / 查找」
        if (_editing) return Externs.DefWindowProcW(h, m, w, l);
        if (!wasDrag || !OverBody(mx / _scale, my / _scale)) return 0;
        string url = Doc.LinkAt(mx / _scale, my / _scale - ContentTop + _offY);
        if (url != null)
        {
            if (url.StartsWith("#ref:"))
            {
                // 脚注回跳:滚回正文引用处
                if (Doc.TryRefBackY(url[5..], out float ry)) JumpWithinDocument(ry - 80f);
                return default;
            }
            if (url.StartsWith("#fn:"))
            {
                // 脚注引用:滚动到文末脚注节
                if (Doc.TryFootnoteY(url[4..], out float fy)) JumpWithinDocument(fy - 80f);
                return default;
            }
            OpenDocumentLink(url);
            return default;
        }
        if (!wasDrag && TryToggleTask(mx / _scale, my / _scale - ContentTop + _offY)) return default;
        // <details> 摘要行点击切换展开
        if (Doc.HasOpenDetailsHit(mx / _scale, my / _scale - ContentTop + _offY)) Doc.ToggleDetailsAt(mx / _scale, my / _scale - ContentTop + _offY);
        return default;
    }

    static IntPtr OnSetCursor(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        bool isClientArea = (short)(long)l == 1; // 非客户区(边框等)交给默认光标
        _ = Externs.GetClientRect(h, out var cr);
        GetCursorPos(out var pt);
        _ = ScreenToClient(h, ref pt);
        bool inBounds = pt.X >= 0 && pt.Y >= 0 && pt.X < cr.R - cr.L && pt.Y < cr.B - cr.T;

        if (!isClientArea || !inBounds) return Externs.DefWindowProcW(h, m, w, l);

        float dipX = pt.X / _scale;
        float dipY = pt.Y / _scale;
        if (!_pointer.IsCapturing && OverBubble(dipX, dipY)) { _ = Externs.SetCursor(CurArrow()); return 1; }   // U7
        if (HomeShown && dipY >= ContentTop) { _ = Externs.SetCursor(HomeHit(dipX, dipY).Kind != HomeTargetKind.None ? CurHand() : CurArrow()); return 1; }   // U10
        if (!_pointer.IsCapturing && JuanCursor(dipX, dipY)) return 1;   // U8：条目与关闭钮为手形
        if (!_pointer.IsCapturing && OverEditBar(dipX, dipY)) { _ = Externs.SetCursor(CurArrow()); return 1; }   // U9a
        if (!_cord.Pressed && !_pointer.IsCapturing && OverBackFlag(dipX, dipY)) { _ = Externs.SetCursor(CurHand()); return 1; }
        if (_cord.Pressed || (!_pointer.IsCapturing && OverCord(dipX, dipY))) { _ = Externs.SetCursor(CurArrow()); return 1; }   // U5a：书签绳列不显示文字光标
        if (OverRuler(dipX, dipY) && !_pointer.IsCapturing) { _ = Externs.SetCursor(CurHand()); return 1; }   // D5:标尺可点
        if (!_pointer.IsCapturing && OverReturnHint(dipX, dipY)) { _ = Externs.SetCursor(CurHand()); return 1; }
        if (!_editing && !_pointer.IsCapturing && OverBody(dipX, dipY) && !OverFind(dipX, dipY) && !OverToc(dipX, dipY)
            && Doc.OverCodeChip(dipX, dipY - ContentTop + _offY)) { _ = Externs.SetCursor(CurHand()); return 1; }   // E5:复制按钮可点
        bool overBody = OverBody(dipX, dipY);
        float mx = dipX;
        float my = dipY - ContentTop + _offY;

        int ehv = -1;
        if (overBody && !_editing && (Doc.EmptyHits.Count > 0 || Doc.EmptyHover >= 0))
        {
            int ehv2 = HitTestingGeometry.FindEmptyHit(Doc.EmptyHits, dipX, dipY);
            if (ehv2 != Doc.EmptyHover)
            {
                Doc.EmptyHover = ehv2;
                InvalidateContent(h);
            }
            ehv = ehv2;
        }

        bool isImage = overBody && !_editing && Doc.TryImageHit(mx, my, out _);
        bool hasRecent = overBody && !_editing && ehv >= 0;
        bool hasLink = overBody && !_editing && Doc.LinkAt(mx, my) != null;
        bool hasBlock = overBody && !_editing && Doc.HitBlock(mx, my);

        var cursor = HitTestingGeometry.DetermineCursor(
            isClientArea: true,
            inBounds: true,
            overBody: overBody,
            editing: _editing,
            isImage: isImage,
            hasRecentFile: hasRecent,
            hasLink: hasLink,
            hasBlock: hasBlock);

        nint hCur = CursorHandle(cursor);
        if (hCur != 0)
        {
            _ = Externs.SetCursor(hCur);
            return (IntPtr)1;
        }
        return Externs.DefWindowProcW(h, m, w, l);
    }
}
