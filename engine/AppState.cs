using System;

namespace MDViewerNative;

// 应用与窗口交互全局状态聚合模型:
// 统一持有编辑模式、基准字号、窗口初始边界配置、
// 提示计数以及生命周期过渡标记，从 Program.cs 抽离分散的静态状态。
internal sealed class AppState
{
    internal const float DefaultBodyFont = 16f;
    internal const int DefaultLightboxHints = 3;

    public bool Editing { get; set; }
    public float BodyFont { get; set; } = DefaultBodyFont;
    public bool RestoringStartup { get; set; } = true;
    public bool HandoffDeferred { get; set; }
    public bool DiskCheckAgain { get; set; }
    public bool ClosingNormally { get; set; }
    public bool SyncingEditor { get; set; }
    public bool ContextMenuActive { get; set; }
    public int LightboxHintsLeft { get; set; } = DefaultLightboxHints;

    // 配置持久化镜像字段
    public Appearance Appearance { get; } = new();   // U2：界面样式与纸张（ADR-0003）
    public bool HasConfigTheme { get; set; }
    public bool ConfigThemeDark { get; set; }
    public bool ConfigToc { get; set; }
    public bool ConfigSerif { get; set; }
    public bool ConfigGrain { get; set; } = true;   // 纸纹开关(grain=0 关)，缺省开=U2b 原设计
    public bool HasBounds { get; set; }
    public bool WindowMaximized { get; set; }
    public RECT WindowBounds { get; set; }

    // 尝试更新正文字号(经 WindowGeometry 钳制在 12-28 DIP 之间)
    public bool TrySetBodyFont(float target, out float clamped)
    {
        clamped = WindowGeometry.ClampBodyFont(target);
        if (Math.Abs(clamped - BodyFont) < 0.001f)
            return false;
        BodyFont = clamped;
        return true;
    }

    // 消费一次图片手势提示机会
    public bool ConsumeLightboxHint()
    {
        if (LightboxHintsLeft > 0)
        {
            LightboxHintsLeft--;
            return true;
        }
        return false;
    }

    // 完成启动初始化流程
    public void FinishStartup()
    {
        RestoringStartup = false;
    }

    // 检查并记录接力延迟状态
    public bool CheckAndDeferHandoff(bool hasComposition, bool isNativeFormActive)
    {
        if (hasComposition || isNativeFormActive)
        {
            HandoffDeferred = true;
            return true;
        }
        return false;
    }
}
