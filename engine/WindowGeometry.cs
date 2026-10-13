using System;
using System.Globalization;

namespace MDViewerNative;

// 窗口尺寸与 DPI 族的纯算术与状态判定:
// 最小窗口限制、DPI 换算、窗口尺寸解包、越界吸附、带区判定。
// 窗口宿主保留 SetWindowPos、DwmSetWindowAttribute 与 Win32 调用;这里的每个函数都不依赖窗口句柄即可运行。
internal static class WindowGeometry
{
    internal const float DefaultDpi = 96f;
    internal const int MinTrackWidthDip = 500;
    internal const int MinTrackHeightDip = 300;
    internal const int DefaultWindowX = 140;
    internal const int DefaultWindowY = 90;
    internal const int DefaultWindowWidth = 1240;
    internal const int DefaultWindowHeight = 900;
    internal const float MinBodyFont = 12f;
    internal const float MaxBodyFont = 28f;

    // DPI 缩放换算(96 DPI = 1.0)
    internal static float DpiToScale(int dpi) => dpi / DefaultDpi;
    internal static float DpiToScale(uint dpi) => dpi / DefaultDpi;

    // DPI 变化敏感度判断
    internal static bool HasScaleChanged(float newScale, float oldScale, float epsilon = 0.001f)
        => MathF.Abs(newScale - oldScale) > epsilon;

    // WM_GETMINMAXINFO: 最小跟踪尺寸(物理像素)
    internal static (int minWidth, int minHeight) MinTrackSize(float scale)
        => ((int)(MinTrackWidthDip * scale), (int)(MinTrackHeightDip * scale));

    // WM_SIZE: 解包 lParam 得到视口物理宽高,返回是否有效(最小化等情况宽或高 < 1 返回 false)
    internal static bool UnpackSize(nint lParam, out int viewW, out int viewH)
    {
        viewW = (short)(lParam & 0xFFFF);
        viewH = (short)((long)lParam >> 16);
        if (viewW < 1 || viewH < 1) return false;
        if (viewH < 1) viewH = 1;
        return true;
    }

    // 恢复窗口位置前校验:若保存的矩形完全落在主工作区之外(如副显示器已拔),吸附回可见区域
    internal static RECT ClampWindowRect(RECT r, RECT? workArea)
    {
        if (!workArea.HasValue) return r;
        RECT wa = workArea.Value;
        int w = r.R - r.L, h = r.B - r.T;
        if (r.L >= wa.R - 80 || r.R <= wa.L + 80 || r.T >= wa.B - 80 || r.B <= wa.T + 80)
            return new RECT { L = wa.L + 60, T = wa.T + 40, R = wa.L + 60 + w, B = wa.T + 40 + h };
        return r;
    }

    // 解析初始窗口矩形(若无历史配置则使用默认 1240x900 居于 140,90)
    internal static RECT ResolveInitialBounds(bool hasBounds, RECT winBounds, RECT? workArea)
    {
        if (!hasBounds)
            return new RECT { L = DefaultWindowX, T = DefaultWindowY, R = DefaultWindowX + DefaultWindowWidth, B = DefaultWindowY + DefaultWindowHeight };
        return ClampWindowRect(winBounds, workArea);
    }

    // 工具栏与正文局部重绘条带计算
    internal static RECT ToolbarStrip(int clientWidth, float toolH, float scale)
        => new RECT { L = 0, T = 0, R = clientWidth, B = (int)((toolH + 1f) * scale) };

    internal static RECT ContentStrip(int clientWidth, int clientHeight, float toolH, float scale)
        => new RECT { L = 0, T = (int)(toolH * scale), R = clientWidth, B = clientHeight };

    // WM_PAINT: 判定重绘区域是否波及工具栏或正文
    internal static (bool toolHit, bool contentHit) HitStrips(RECT updateRect, float toolH, float scale)
    {
        int strip = (int)(toolH * scale);
        return (updateRect.T < strip, updateRect.B > strip);
    }

    // 配置序列化与反序列化。数字固定按不变区域读写,不依赖项目的 InvariantGlobalization 开关(E22)。
    internal static bool TryParseBounds(string value, out RECT bounds, out bool isMaximized)
    {
        bounds = default;
        isMaximized = false;
        var pp = value.Split(',');
        static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (pp.Length == 5 && Int(pp[0], out int wx) && Int(pp[1], out int wy)
            && Int(pp[2], out int ww) && Int(pp[3], out int wh)
            && ww > 200 && wh > 120)
        {
            bounds = new RECT { L = wx, T = wy, R = wx + ww, B = wy + wh };
            isMaximized = pp[4] == "1";
            return true;
        }
        return false;
    }

    internal static string FormatBounds(RECT r, bool isMaximized)
        => string.Create(CultureInfo.InvariantCulture, $"{r.L},{r.T},{r.R - r.L},{r.B - r.T},{(isMaximized ? 1 : 0)}");

    // 字号缩放边界钳制
    internal static float ClampBodyFont(float target) => Math.Clamp(target, MinBodyFont, MaxBodyFont);

    // 正文可视高度(DIP)
    internal static float ContentHeight(float viewHeightDip, float contentTopDip)
        => MathF.Max(1f, viewHeightDip - contentTopDip);
}
