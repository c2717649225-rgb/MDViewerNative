using System;

namespace MDViewerNative;

// 窗口主题与镶边族的纯算术与状态判定:
// 标题栏、边框、文字色表计算, 沉浸式暗色模式标识, 滚动条主题名称及激活状态判定。
// DWM 与 Win32 API 调用保留在窗口宿主; 这里的每个函数都不依赖窗口句柄即可运行。
internal static class WindowChrome
{
    // 窗口镶边 v2.2(§一「案」): 标题栏与正文同 Canvas, 分界交给 border 区域线; 激活态唯一载体=border 色。
    // COLORREF(BGR) 由 ThemePalette 编译期常量派生——不读运行期主题表,免首调未初始化闪黑;
    // 字节序与主题一致性由 SelfTest(AssertPaletteChrome)执法。激活边框既定观感本批不动(A2 登记)。
    internal static readonly uint DarkCaption = ThemePalette.ToColorRef(ThemePalette.CanvasD);
    internal static readonly uint LightCaption = ThemePalette.ToColorRef(ThemePalette.CanvasL);

    internal const uint DarkBorderActive = 0x00D6DACDu;    // hairlineZone (深色激活)
    internal const uint DarkBorderInactive = 0x00555250u;  // 中性灰一档 (深色非激活)

    internal const uint LightBorderActive = 0x00CDCED6u;   // hairlineZone (浅色激活)
    internal const uint LightBorderInactive = 0x00C2C0BCu; // 中性灰一档 (浅色非激活)

    internal static readonly uint DarkText = ThemePalette.ToColorRef(ThemePalette.InkD);      // ink (深色文字)
    internal static readonly uint LightText = ThemePalette.ToColorRef(ThemePalette.InkL);     // ink (浅色文字)

    internal const string ExplorerTheme = "Explorer";
    internal const string DarkModeExplorerTheme = "DarkMode_Explorer";

    internal readonly record struct DwmAttributes(uint Caption, uint Border, uint Text, uint ImmersiveDarkMode);

    // 计算 DWM 窗口属性色值 (BGR 格式)
    internal static DwmAttributes GetDwmAttributes(bool darkMode, bool winActive)
    {
        if (darkMode)
        {
            uint border = winActive ? DarkBorderActive : DarkBorderInactive;
            return new DwmAttributes(DarkCaption, border, DarkText, 1u);
        }
        else
        {
            uint border = winActive ? LightBorderActive : LightBorderInactive;
            return new DwmAttributes(LightCaption, border, LightText, 0u);
        }
    }

    // U2：desk 界面的镶边跟随纸张——标题栏同纸面，激活边框 rule-2，非激活 rule。
    internal static DwmAttributes GetDwmAttributes(PaperColors paper, bool winActive) => new(
        ThemePalette.ToColorRef(paper.Canvas),
        ThemePalette.ToColorRef(winActive ? paper.HairlineZone : paper.Hairline),
        ThemePalette.ToColorRef(paper.Ink),
        paper.Dark ? 1u : 0u);

    // 计算系统滚动条主题名称(高对比度下不应用特定主题, 返回 null)
    internal static string GetScrollbarTheme(bool darkMode, bool highContrast)
    {
        if (highContrast) return null;
        return darkMode ? DarkModeExplorerTheme : ExplorerTheme;
    }

    // WM_ACTIVATE: 判断是否处于激活状态 (WA_INACTIVE = 0)
    internal static bool IsActive(int wParamLow) => wParamLow != 0;

    // 切换主窗口滚动条 WS_VSCROLL 样式
    internal static nint ToggleScrollbarStyle(nint currentStyle, bool show, uint wsVScroll = 0x00200000u)
    {
        long st = (long)currentStyle;
        st = show ? (st | wsVScroll) : (st & ~wsVScroll);
        return (nint)st;
    }

    // 初始主题决策(配置优先，否则跟随系统)
    internal static bool ResolveInitialDarkMode(bool hasCfgTheme, bool cfgThemeDark, bool systemPrefersDark)
        => hasCfgTheme ? cfgThemeDark : systemPrefersDark;

    // 注册表 AppsUseLightTheme 读取结果判定 (0 为深色模式)
    internal static bool IsSystemDark(int regResult, uint appsUseLightTheme)
        => regResult == 0 && appsUseLightTheme == 0;
}
