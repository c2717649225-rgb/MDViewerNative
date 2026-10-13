using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U8：目录卷目的宿主接线（desk 界面的 Ctrl+T / 顶栏「目录」）。条目与几何在 Juan，绘制在 JuanView。
// 打开时收起查找框与选区气泡（查找框的输入框是子窗口，会盖在卷目上）；打开查找、切换标签/模式、重新载入文档时关闭。
// 键盘焦点落在当前章；↑↓ Home End 移动，Enter/空格跳转，Esc 或 Ctrl+T 关闭。经典界面仍用侧栏目录，互不影响。
internal static unsafe partial class Program
{
    static bool _juanOpen, _juanFading, _juanKeyboard;
    static List<JuanItem> _juanItems = new();
    static int _juanCurrent = -1, _juanFocus = -1, _juanHover = -1;
    static bool _juanCloseHot;
    static float _juanScroll;
    static bool _juanScrollPending;   // 打开后第一帧按版式把当前章滚进视野

    // 经典界面与编辑模式下一律视为关闭（切换外观时不必逐处清理）。
    static bool JuanShown => State.Appearance.Desk && !_editing;
    static bool JuanOpen => _juanOpen && JuanShown;
    static bool JuanPainted => (_juanOpen || _juanFading) && JuanShown;
    static float JuanTop => ContentTop;
    static float JuanW => _viewW / _scale;
    static float JuanH => MathF.Max(0f, _viewH / _scale - JuanTop);

    static void ToggleJuan()
    {
        if (JuanOpen) CloseJuan(); else OpenJuan((Externs.GetKeyState(0x11) & 0x8000) != 0);
    }

    static void OpenJuan(bool keyboard)
    {
        if (!State.Appearance.Desk || _editing) return;
        if (Find.Visible) Find.Close();
        HideSelectionBubble();
        var heads = new List<(int Level, float Y, string Seal, string Title)>();
        foreach (var e in Doc.TocEntries) heads.Add((e.level, e.y, Doc.SealOf(e.blockIdx), e.text));
        _juanItems = Juan.Items(heads, MaxOff());
        var ys = new List<float>();
        foreach (var h in heads) if (h.Level <= Juan.MaxLevel) ys.Add(h.Y);
        _juanCurrent = Juan.Current(ys, _offY);
        _juanFocus = _juanCurrent; _juanHover = -1; _juanCloseHot = false; _juanKeyboard = keyboard;
        _juanScroll = 0f; _juanScrollPending = true;
        _juanOpen = true; _juanFading = false;
        StartDeskAnimation(DeskAnim.Juan, Juan.RiseMs);
        _ = Externs.SetFocus(_hwnd);
        InvalidateToolbar(_hwnd);
        InvalidateContent(_hwnd);
    }

    // animate=false：文档或模式已变，直接撤掉，不留淡出残影。
    static void CloseJuan(bool animate = true)
    {
        if (!_juanOpen && !_juanFading) return;
        bool was = _juanOpen;
        _juanOpen = false; _juanHover = -1; _juanCloseHot = false;
        _juanFading = animate && was;
        if (_juanFading) StartDeskAnimation(DeskAnim.Juan, Juan.FadeMs);
        if (!Anim.Running(DeskAnim.Juan, Environment.TickCount64)) { Anim.Stop(DeskAnim.Juan); _juanFading = false; }   // 系统关闭动画：直接消失
        if (!_juanFading) _ = Externs.SetCursor(CurArrow());
        InvalidateToolbar(_hwnd);
        InvalidateContent(_hwnd);
    }

    static void TickJuan(long now)
    {
        if (_juanFading && !Anim.Running(DeskAnim.Juan, now)) _juanFading = false;
        InvalidateContent(_hwnd);
    }

    static string JuanSubtitle()
    {
        // 自检修复：desk 的标签表最前面是起始页，不能按 ActiveTab 下标取，按“当前”标记找
        foreach (var tab in BuildTabInfo()) if (tab.Active && !tab.IsHome) return tab.DisplayTitle;
        return "";
    }

    static JuanFrame JuanFrameNow() => JuanView.Prepare(_juanItems, JuanSubtitle(), JuanW, JuanH);

    // OnPaint：正文、书签绳、页脚、查找框之后，气泡之前。
    static void PaintJuan(IntPtr hdc, RECT cr)
    {
        if (!JuanPainted) return;
        var f = JuanFrameNow();
        if (_juanScrollPending) { _juanScroll = Juan.InitialScroll(f, _juanFocus, JuanH); _juanScrollPending = false; }
        _juanScroll = Juan.ClampScroll(f, _juanScroll, JuanH);
        long now = Environment.TickCount64;
        float t = Anim.Progress(DeskAnim.Juan, now);
        float alpha, rise;
        if (_juanOpen)
        {
            alpha = DeskAnimations.Ease(MathF.Min(1f, t * Juan.RiseMs / Juan.FadeMs));
            rise = (1f - DeskAnimations.Ease(t)) * Juan.Rise;
        }
        else { alpha = 1f - DeskAnimations.Ease(t); rise = 0f; }
        JuanView.Paint(hdc, cr, _scale, new JuanPaintState(JuanTop, _juanScroll, _juanCurrent, _juanFocus, _juanKeyboard, _juanHover, _juanCloseHot, alpha, rise));
    }

    static bool OverJuan(float x, float y) => JuanOpen && y >= JuanTop;

    static bool OverJuanClose(float x, float y)
    {
        var (cx, cy, cw, ch) = JuanView.CloseBox();
        float yy = y - JuanTop + _juanScroll;
        return x >= cx && x < cx + cw && yy >= cy && yy < cy + ch;
    }

    static int JuanRowAt(float x, float y) => Juan.HitRow(JuanFrameNow(), x, y - JuanTop, _juanScroll);

    // 按下：点条目跳过去（留返回书签），点关闭或空白处关闭；这一击都不落到正文。
    static bool JuanPointerDown(float x, float y)
    {
        if (!OverJuan(x, y)) return false;
        int row = OverJuanClose(x, y) ? -1 : JuanRowAt(x, y);
        ActivateJuan(row);
        return true;
    }

    static void ActivateJuan(int row)
    {
        CloseJuan();
        if (row >= 0 && row < _juanItems.Count) JumpWithinDocument(_juanItems[row].JumpOffset);
    }

    static void JuanHoverAt(float x, float y)
    {
        int row = OverJuan(x, y) && !OverJuanClose(x, y) ? JuanRowAt(x, y) : -1;
        bool close = OverJuan(x, y) && OverJuanClose(x, y);
        if (row == _juanHover && close == _juanCloseHot) return;
        _juanHover = row; _juanCloseHot = close;
        InvalidateContent(_hwnd);
    }

    static bool JuanCursor(float x, float y)
    {
        if (!OverJuan(x, y)) return false;
        bool hand = OverJuanClose(x, y) || JuanRowAt(x, y) >= 0;
        _ = Externs.SetCursor(hand ? CurHand() : CurArrow());
        return true;
    }

    static void JuanWheel(int delta)
    {
        var f = JuanFrameNow();
        float next = Juan.ClampScroll(f, _juanScroll - delta / 120f * Juan.WheelStep, JuanH);
        if (next == _juanScroll) return;
        _juanScroll = next;
        _juanHover = -1;
        InvalidateContent(_hwnd);
    }

    // 卷目打开时的按键：导航键在这里消化；Ctrl 组合键照常走命令（Ctrl+T 关闭、Ctrl+F 打开查找并关闭卷目）。
    static bool JuanKeyDown(int vk, bool ctrl)
    {
        if (!JuanOpen || ctrl) return false;
        var f = JuanFrameNow();
        switch (vk)
        {
            case 0x1B:
                CloseJuan();
                return true;
            case 0x0D:
            case 0x20:
                if (_juanFocus >= 0) ActivateJuan(_juanFocus);
                return true;
            case 0x26:
            case 0x28:
            case 0x24:
            case 0x23:
                var key = vk == 0x26 ? JuanKey.Up : vk == 0x28 ? JuanKey.Down : vk == 0x24 ? JuanKey.Home : JuanKey.End;
                _juanFocus = Juan.Move(_juanFocus, _juanItems.Count, key);
                _juanKeyboard = true;
                _juanScroll = Juan.Reveal(f, _juanFocus, _juanScroll, JuanH);
                InvalidateContent(_hwnd);
                return true;
            case 0x21:
            case 0x22:
                _juanScroll = Juan.ClampScroll(f, _juanScroll + (vk == 0x22 ? 0.9f : -0.9f) * JuanH, JuanH);
                InvalidateContent(_hwnd);
                return true;
            case 0x25:
            case 0x27:
            case 9:
                return true;
        }
        return false;
    }
}
