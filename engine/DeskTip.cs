using System;

namespace MDViewerNative;

// U6a2：悬停提示「做什么 + 快捷键」（规格 §3；演示 .tip / showTip）。纯函数，单位 DIP。
// 位置：默认在目标下方 8；放不下就翻到上方；书签绳一列的提示放在目标左侧 12；左右夹在窗口内 6。
internal enum TipSide { Below, Beside }

internal static class DeskTip
{
    public const float TextSize = 12f, KeyTextSize = 10f;           // 演示 kbd 为 .76em（9.1），Windows 小字发虚，取 10
    public const float InsetX = 10f, InsetY = 5f, LineHeight = 18f, Radius = 6f;
    public const float Offset = 8f, BesideOffset = 12f, Margin = 6f;
    public const float KeyGap = 6f, KeyInsetX = 4.5f, KeyInsetY = 0.5f, KeyRadius = 5f;
    public const float Opacity = 0.94f;

    // 提示最后一段若是快捷键（「… · Ctrl+F」），拆出来单独画成键帽；只看第一行。
    public static (string Text, string Key) Split(string hint)
    {
        if (string.IsNullOrEmpty(hint)) return (hint ?? "", null);
        int end = hint.IndexOf('\n');
        string first = end < 0 ? hint : hint[..end];
        int cut = first.LastIndexOf(" · ", StringComparison.Ordinal);
        if (cut <= 0 || !IsKey(first[(cut + 3)..])) return (hint, null);
        return (first[..cut] + (end < 0 ? "" : hint[end..]), first[(cut + 3)..]);
    }

    public static bool IsKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Contains(' ')) return false;
        string[] parts = s.Length > 1 && s.EndsWith("++", StringComparison.Ordinal) ? (s[..^2] + "+plus").Split('+') : s.Split('+');
        foreach (string p in parts)
        {
            if (p.Length == 0) return false;
            bool known = p is "Ctrl" or "Alt" or "Shift" or "Win" or "Esc" or "Enter" or "Tab" or "Home" or "End" or "PgUp" or "PgDn" or "Del" or "Space" or "plus"
                || (p.Length == 1 && (p[0] < 128 && (char.IsLetterOrDigit(p[0]) || "=-,./;[]\\`'".Contains(p[0])) || "←→↑↓".Contains(p[0])))
                || (p.Length is 2 or 3 && p[0] == 'F' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24);
            if (!known) return false;
        }
        return true;
    }

    public static (float X, float Y) Place(float ax, float ay, float aw, float ah, float w, float h, float winW, float winH, TipSide side)
    {
        float x = ax + aw / 2f - w / 2f, y = ay + ah + Offset;
        if (side == TipSide.Beside) { x = ax - w - BesideOffset; y = ay + ah / 2f - h / 2f; }
        if (y + h > winH - Margin) y = ay - h - Offset;
        x = MathF.Max(Margin, MathF.Min(winW - w - Margin, x));
        return (x, MathF.Max(0f, y));
    }
}
