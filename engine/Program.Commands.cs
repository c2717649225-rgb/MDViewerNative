using System;

namespace MDViewerNative;

// 窗口过程的键盘、命令与右键菜单消息：WM_KEYDOWN、WM_COMMAND、WM_CONTEXTMENU。由 WndProcInner 分派。
// E17d 从 switch 原样搬出；OnKeyDown 里的 break 属于内层 switch，未改动；三个分支都以 return 结束。
internal static unsafe partial class Program
{
    static IntPtr OnKeyDown(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int vk = (int)w & 0xFF;
        if (vk is not (0x10 or 0x11 or 0x12)) HideSelectionBubble();   // U7：按键即收起选区气泡（单按修饰键不算）
        bool ctrl = (Externs.GetKeyState(0x11) & 0x8000) != 0; // GetKeyState 对 PostMessage 合成键盘状态同样生效
        if (Toolbar.HasFocus && !ctrl && ToolbarView.OwnsKey(vk))
        {
            PrepareToolbar();
            if (vk is 0x25 or 0x27 or 9) Toolbar.StepFocus(vk, (Externs.GetKeyState(0x10) & 0x8000) != 0);
            else if (vk is 0x24 or 0x23) Toolbar.FocusEdge(vk == 0x23);   // Home/End
            else if (vk is 0x0D or 0x20) InvokeToolbarTarget(Toolbar.FocusedTarget);
            else if (vk == 0x1B) { Toolbar.ClearFocus(); RestoreToolbarFocus(); }
            UpdateTitle(); InvalidateToolbar(h);
            return 0;
        }
        if (JuanKeyDown(vk, ctrl)) return 0;   // U8：卷目打开时导航键归卷目
        if (HomeKeyDown(vk, ctrl)) return 0;   // U10：起始页上的键不落到藏在后面的文档
        if (ctrl && vk == 'E') { ToggleEdit(); return default; }
        if (ctrl && vk == 'S') { if (CmdEnabled(1001)) _ = SaveFile(); return default; }   // I7(N7):与按钮灰显同闸,键盘路径不再空写盘
        if (ctrl && vk == 'D') { ToggleTheme(); return default; }
        if (ctrl && vk == 'O') { OpenFileDlgNative(); return default; }
        if (ctrl && vk == 'F') { Find.Open(); return default; }
        if (ctrl && vk == 'T') { ToggleToc(); return default; }
        if (!_editing)
        {
            if (vk == 0x1B)
            {
                if (Find.Visible) Find.Close();
                else if (Doc.ShowToc) ToggleToc();
                else if (Zen.Enabled) ToggleZen();   // Esc 链最后一步退出沉浸
                return default;
            }
            if (ctrl && vk == 'C')
            {
                string t = Doc.CopyText();
                if (t != null) _ = Clipboard.SetText(t, _hwnd);
                return default;
            }
            if (ctrl && vk == 'A') { Doc.SelAll(); _ = Externs.InvalidateRect(h, IntPtr.Zero, 0); return default; }
            switch (vk)
            {
                // Left/Right scroll the code block under the pointer. Wide code
                // must be reachable without a mouse wheel or a tilt wheel, and
                // these keys are otherwise unused in the reading view (30).
                case 0x25:                                           // Left
                case 0x27:                                           // Right
                    {
                        float step = vk == 0x27 ? 60f : -60f;
                        // Aim with the pointer when it happens to rest on a code block,
                        // otherwise fall back to the block nearest the middle of the
                        // view so the keys work with no mouse involved at all.
                        bool moved = GetCursorPos(out POINT kp) && ScreenToClient(_hwnd, ref kp)
                            && OverBody(kp.X / _scale, kp.Y / _scale)
                            && Doc.ScrollCodeAt(kp.X / _scale, kp.Y / _scale - ContentTop + _offY, step);
                        if (!moved) moved = Doc.ScrollCodeVisible(_offY, _offY + ContentHeight, step);
                        if (moved) _ = Externs.InvalidateRect(h, IntPtr.Zero, 0);
                        break;
                    }
                default:
                    if (ScrollGeometry.TryKeyTarget(vk, _offY, _viewH / _scale, out float keyTarget)) SetOffset(keyTarget);   // PgUp/PgDn/Home/End/Up/Down
                    break;
            }
        }
        return default;
    }

    static IntPtr OnCommand(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        int code = (int)((long)w >> 16) & 0xFFFF;
        int cmd = (int)(w & 0xFFFF);
        if ((code == 0 || code == 1) && HasComposition && Commands.RequiresStableText(cmd)) return 0;
        // I7(N7):菜单自投递的表命令(1001-1016)与加速键同语义同闸;EN_* 通知(code=0x0100/0x0300)不入此列
        if (code == 0 && cmd >= 1001 && cmd <= 1016) code = 1;
        // 审查修复(#4):1013 豁免闸——Find.Open(replace:true) 自带 E0.3 提示("替换需编辑模式"),闸死它=把提示通道也杀死(静默回归);
        // 菜单灰显仍由表谓词控制,两通道各司其职
        if ((code == 0 || code == 1) && cmd != 1013 && !CmdEnabled(cmd)) return default;
        if ((code == 0 || code == 1) && cmd == 2003) { ApplyTextHistory(false); return 0; }
        if ((code == 0 || code == 1) && cmd == 2270) { ApplyTextHistory(true); return 0; }
        if (Find.HandleCommand(cmd, code, l)) return default;
        if ((code == 0 || code == 1) && HandleProductCommand(cmd)) return 0;
        if (code == 0x0300 /*EN_CHANGE*/)
        {
            if (_editing && !_syncingEditor && (IntPtr)l == _hEdit) { MarkDirty(); UpdateTitle(); return default; }   // 替换框/查找框的 EN_CHANGE 不是文档修改;审查修复:置脏瞬间刷工具栏(warn 点不等下次重绘)
        }
        // E0.2:EN_KILLFOCUS 不再关闭查找栏(点正文/编辑区先夺焦,旧实现在此关=会话主杀路径;Esc 仍是唯一手势关闭点)
        if (code == 1 && Accelerators.TryDispatch(cmd)) return default;
        // 审查修复:2090/2091/2092 死分发清理——菜单已表驱动(导出/另存/关闭以 1011/1012/1008 code=0→1 归一处理)
        if (code == 0 && cmd == 2093)   // I7:键盘快捷键速查表(兜底,发现主通道是菜单 \t)
        {
            _ = Externs.MessageBoxW(_hwnd,
                "Ctrl+O 打开 · Ctrl+S 保存 · Ctrl+Alt+S 另存为\n" +
                "Ctrl+F 查找 · Ctrl+H 替换(编辑态) · Esc 关查找\n" +
                "Ctrl+E 编辑/预览 · Ctrl+W 关闭标签 · Ctrl+Tab / Shift+Tab 切标签\n" +
                "Ctrl+T 目录侧栏 · Ctrl+D 明暗主题 · F11 沉浸(指针到顶部召回工具栏)\n" +
                "F6 工具栏/正文 · 方向键选择 · Enter 执行\n" +
                "查找栏内 Tab 切换输入与按钮 · Enter 执行 · Esc 关闭\n" +
                "Ctrl+= / Ctrl+- 字号 · Ctrl+0 恢复默认(或按住 Ctrl 滚滚轮)\n" +
                "Alt+Left / Alt+Right 阅读跳转后退/前进\n" +
                "代码块右键可整块复制 · 查找选项支持大小写/整词\n" +
                "Home/End/PgUp/PgDn 滚动 · 双击选词(编辑区同)\n" +
                "点图放大 · 滚轮缩放 · 拖动平移 · 单击关闭\n" +
                "拖 .md 入窗打开 · 拖图片插入当前文档", "键盘快捷键", 0);
            return default;
        }
        if (code == 0 && cmd == 2094)   // I7:用当前选区查找(划词→右键→回车)
        {
            string sel = null;
            try { sel = Doc.CopyText(); }
            catch (Exception ex) { AppLog.Error("find selection", ex); }   // 与“复制”一致:记日志,查找框照常打开(E12)
            Find.Open();
            if (!string.IsNullOrEmpty(sel)) Find.SetQuery(sel.Replace("\n", " ").Replace("\r", " "));   // EN_CHANGE 自动跑搜索
            return default;
        }
        if (code == 0 && cmd == 2095) { if (_ctxImgSrc != null) OpenLightbox(_ctxImgSrc); return default; }   // I7:右键查看图片
        if ((code == 0 || code == 1) && cmd == 2080)
        {
            string codeText = CodeForCopy();
            if (codeText != null && !Clipboard.SetText(codeText, _hwnd))
                Externs.MessageBoxW(_hwnd, "剪贴板暂不可用，请稍后重试。", "复制代码块", 0x30);
            return 0;
        }
        if ((code == 0 || code == 1) && cmd == 2081) { CopySelectionHtml(); return 0; }
        if ((code == 0 || code == 1) && cmd == 2098) { NavigateHistory(true); return 0; }
        if ((code == 0 || code == 1) && cmd == 2099) { NavigateHistory(false); return 0; }
        if ((code == 0 || code == 1) && cmd == 2097) { RefreshFile(); return default; }
        if ((code == 0 || code == 1) && cmd == 2096) { ToggleSerif(); return default; }
        if (code == 0 && cmd >= 2300 && cmd <= 2304) { ChoosePaper((Paper)(cmd - 2300)); return default; }   // U6a：外观菜单选纸   // I11:衬线开关(仅 More 面,无加速键)
        if ((code == 0 || code == 1) && cmd == 2291) { TogglePalette(); return default; }   // U11：命令面板（Ctrl+K / Ctrl+Shift+P）
        if ((code == 0 || code == 1) && cmd == 2290) { ShowHome(); return default; }        // U11：起始页（命令面板）
        if ((code == 0 || code == 1) && cmd == 2251) { ToggleZen(); return default; }   // 阶段 B:沉浸模式(F11)
        if (code == 0 && cmd >= 2100 && cmd <= 2104)                               // 最近打开
        {
            int ri = cmd - 2100;
            if (ri < _recent.Count)
            {
                LoadFile(_recent[ri]);
            }
            return default;
        }
        if (code == 0 && cmd == 2001)
        {
            try { string t = Doc.CopyText(); if (t != null) _ = Clipboard.SetText(t, _hwnd); }
            catch (Exception ex) { Log("COPY " + ex); }
            return default;
        }
        if (code == 0 && cmd == 2002) { Doc.SelAll(); _ = Externs.InvalidateRect(h, IntPtr.Zero, 0); return default; }
        if (code == 0 && _hEdit != IntPtr.Zero)
        {
            IntPtr e = _hEdit;
            if (cmd == 2003) _ = Externs.SendMessageW(e, 0x00C7 /*WM_UNDO*/, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 2004) _ = Externs.SendMessageW(e, 0x0300, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 2005) _ = Externs.SendMessageW(e, 0x0301, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 2006) _ = Externs.SendMessageW(e, 0x0302, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 2007) _ = Externs.SendMessageW(e, 0x0303, IntPtr.Zero, IntPtr.Zero);
            if (cmd == 2008) _ = Externs.SendMessageW(e, 0x00B1, IntPtr.Zero, (IntPtr)(-1));
            return default;
        }
        return default;
    }

    static IntPtr OnContextMenu(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (HomeShown) return 0;   // U10：起始页没有正文右键菜单
        var anchor = WindowLifecycle.ResolveContextMenuAnchor((short)(long)l, (short)((long)l >> 16), _scale, ContentTop);
        int sx = anchor.X, sy = anchor.Y;
        bool keyboard = anchor.IsKeyboard;
        if (keyboard)
        {
            var ptAnchor = new POINT { X = sx, Y = sy };
            ClientToScreen(h, ref ptAnchor); sx = ptAnchor.X; sy = ptAnchor.Y;
        }
        _ctxCodeOwner = Session.RecoveryId;
        _ctxCode = !_editing && keyboard ? Doc.CodeAtSelection() : null;
        _ctxImgSrc = null;   // I7:命中图片则菜单顶部加"查看图片…"(E0.4 手型的右键配套)
        if (!_editing)
        {
            var ptCtx = new POINT { X = sx, Y = sy };
            _ = ScreenToClient(h, ref ptCtx);
            if (!keyboard && !WindowLifecycle.IsContextMenuOverBody(ptCtx.X, ptCtx.Y, _scale, OverBody)) return 0;
            _ = Externs.GetClientRect(h, out var crCtx);
            if (!keyboard && ptCtx.Y >= ContentTop * _scale && ptCtx.Y < crCtx.B)
                _ctxCode = Doc.CodeAtPoint(ptCtx.X / _scale, ptCtx.Y / _scale - ContentTop + _offY);
            if (ptCtx.X >= 0 && ptCtx.X < crCtx.R && ptCtx.Y > (int)(ContentTop * _scale) && ptCtx.Y < crCtx.B &&
                Doc.TryImageHit(ptCtx.X / _scale, ptCtx.Y / _scale - ContentTop + _offY, out string srcCtx))
                _ctxImgSrc = srcCtx;
        }
        ShowContextMenu(h, sx, sy);
        return default;
    }
}
