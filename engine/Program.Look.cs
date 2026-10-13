using System;

namespace MDViewerNative;

// U11b：外观面板的宿主接线（desk 顶栏「外观」）。几何在 AppearancePanel，窗口与绘制在 AppearancePanelView。
// 面板是无激活弹出窗：键盘仍在主窗口，消息循环把按键先交给面板；面板外的按下收起面板（落在「外观」钮上的由按钮自己切换）。
// 换纸、字体、字号走既有入口（ChoosePaper / ToggleSerif / 1014·1015），面板随后按新状态重画。
internal static unsafe partial class Program
{
    static AppearancePanelView _look;
    static AppearancePanelView Look => _look ??= new AppearancePanelView(new LookHost());

    static bool LookOpen => _look?.Visible == true;

    static void ToggleLook(bool keyboard)
    {
        if (LookOpen) { _look.Close(); return; }
        if (!State.Appearance.Desk || HasComposition) return;
        var (x, w) = Toolbar.RectOf(ToolbarTarget.ForCommand(ToolbarCommand.Appearance));
        if (w <= 0f) return;
        Toolbar.HideTip();
        CloseMore();
        Look.Open(x + w, ToolH + 2f, keyboard);
        InvalidateToolbar(_hwnd);
    }

    static void CloseLook() => _look?.Close();

    static void RefreshLook() => _look?.Refresh();

    // 消息循环：面板开着时先看这条消息是否归它处理。返回 true 表示已消费。
    static bool LookFilter(in MSG msg)
    {
        if (!LookOpen) return false;
        if (msg.message == Externs.WM_KEYDOWN) return _look.HandleKey((int)msg.wParam);
        bool press = msg.message is 0x0201 or 0x0204 or 0x0207 or 0x00A1 or 0x00A4 or 0x00A7;
        if (!press || _look.Owns(msg.hwnd)) return false;
        if (msg.hwnd == _hwnd && msg.message == 0x0201)
        {
            float x = (short)((long)msg.lParam & 0xFFFF) / _scale, y = (short)(((long)msg.lParam >> 16) & 0xFFFF) / _scale;
            if (Toolbar.HitTarget(x, y) == ToolbarTarget.ForCommand(ToolbarCommand.Appearance)) return false;   // 按钮抬起时自己切换
        }
        _look.Close();
        return false;
    }

    // 换纸晕开（演示 inkspread）：先拍旧画面，再换纸；换完颜色没变（如跟随系统 ↔ 宣纸）就不播。
    static readonly InkSpreadView Spread = new();
    static bool _spreadArmed;   // 已拍下旧画面、尚未开播

    static void ChoosePaperWithSpread(Paper paper, int screenX, int screenY)
    {
        bool dark = SystemPrefersDark();
        var before = State.Appearance.Colors(dark);
        bool shot = State.Appearance.Desk && SystemAnimationEnabled() && !RenderResources.HighContrast && CaptureSpread(screenX, screenY);
        _spreadArmed = shot;   // 换纸期间若有同步重画，圆半径按 0 算（整幅仍是旧画面），不闪新色
        try { ChoosePaper(paper); }
        finally { _spreadArmed = false; }
        if (!shot) return;
        if (Equals(before, State.Appearance.Colors(dark))) { Spread.End(); return; }
        StartDeskAnimation(DeskAnim.Spread, DeskAnimations.SpreadMs);   // 计时从换纸完成后算起
        if (!Anim.Running(DeskAnim.Spread, Environment.TickCount64)) { Spread.End(); _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0); }
    }

    static bool CaptureSpread(int screenX, int screenY)
    {
        _ = Externs.GetClientRect(_hwnd, out var cr);
        var p = new POINT { X = screenX, Y = screenY };
        _ = ScreenToClient(_hwnd, ref p);
        return Spread.Begin(_hwnd, cr.R, cr.B, p.X, p.Y);
    }

    static void TickSpread(long now)
    {
        if (!Anim.Running(DeskAnim.Spread, now)) Spread.End();
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
    }

    // 主窗口每帧绘制的最后一步：圆外盖旧画面。
    static void PaintSpread(IntPtr hdc)
    {
        if (!Spread.Active) return;
        long now = Environment.TickCount64;
        if (!_spreadArmed && !Anim.Running(DeskAnim.Spread, now)) { Spread.End(); return; }
        float t = _spreadArmed ? 0f : Anim.Progress(DeskAnim.Spread, now);   // 已拍下、尚未开播：整幅旧画面
        Spread.Paint(hdc, DeskAnimations.SpreadRadius(t, Spread.Width, Spread.Height));
    }

    sealed class LookHost : IAppearanceHost
    {
        public nint Owner => _hwnd;
        public float Scale => _scale;
        public AppearanceState State()
        {
            var s = CmdStateNow();
            return new AppearanceState(Program.State.Appearance.Paper, s.Serif, RenderResources.SerifFamily != null,
                AppearancePanel.ZoomPercent(s.BodyFont, s.DefaultFont), !s.ZoomMax, !s.ZoomMin);
        }
        public void Choose(Paper paper, int screenX, int screenY) => ChoosePaperWithSpread(paper, screenX, screenY);
        public void SetSerif(bool serif) { if (CmdStateNow().Serif != serif) ToggleSerif(); }
        public void Zoom(int direction) => _ = Externs.SendMessageW(_hwnd, Externs.WM_COMMAND, (IntPtr)(direction > 0 ? 1014 : 1015), IntPtr.Zero);
        public void Closed() => InvalidateToolbar(_hwnd);
    }
}
