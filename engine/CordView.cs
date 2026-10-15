using System;
using System.Collections.Generic;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// U5a：书签绳视图（规格 §4）。朱色丝带从视口顶端垂下，带细微横向明暗与下端燕尾缺口；
// 悬停、拖动只提亮，不改尺寸。刻度画在绳子之上（已读刻痕不被遮）。几何由 CordGeometry 决定，这里只画。返回书签画在绳子之前（塞在绳子后面）。
internal static unsafe class CordView
{
    const int Slices = 33;   // 横向切成 1/3 DIP 的细条：每条自带明暗，也顺带画出燕尾斜边

    public static void Paint(nint dc, RECT client, float scale, CordFrame frame, CordRect cord, bool hot, IReadOnlyList<CordTick> ticks, int near,
        CordRect? backFlag = null, (float Dx, float Scale, float Opacity) pose = default, bool batch = false)
    {
        if (Rt == IntPtr.Zero || BAccent == IntPtr.Zero || cord.Height <= 0f) return;
        ulong a = 0, b = 0;
        if (!batch)
        {
            PBind(Rt, dc, &client);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
            PBegin(Rt);
        }
        try
        {
            if (backFlag is CordRect flag) BackFlag(flag, pose);   // 书签在绳子后面
            Draw(cord, hot);
            Ticks(frame, ticks, near);
        }
        finally { if (!batch) PEnd(Rt, &a, &b); }
    }

    static void Draw(CordRect c, bool hot)
    {
        bool plain = HighContrast || BShade == IntPtr.Zero;   // 高对比度：纯系统色，不画阴影与明暗
        if (!plain)
        {
            // 投影：演示 drop-shadow(1px 2px 1.5px)。两层半透明近似模糊。
            float alpha = DarkMode ? 0.30f : 0.10f;
            Shape(c, 1f, 2f, BShade, alpha * 0.6f, 0.75f);
            Shape(c, 1f, 2f, BShade, alpha, 0f);
        }
        float w = c.Width, step = w / Slices;
        for (int i = 0; i < Slices; i++)
        {
            float x0 = c.Left + i * step, x1 = x0 + step, u = (i + 0.5f) / Slices;
            var r = new D2D1_RECT_F { left = x0, top = c.Top, right = x1, bottom = Bottom(c, u) };
            PFill(Rt, &r, BAccent);
            if (plain) continue;
            float shade = Shade(u);
            if (shade < 0f) { SetOpacity(BShade, -shade); PFill(Rt, &r, BShade); }
            else if (shade > 0f) { SetOpacity(BGlint, shade); PFill(Rt, &r, BGlint); }
            if (hot) { SetOpacity(BGlint, 0.07f); PFill(Rt, &r, BGlint); }   // 亮度 +8% 的近似
        }
        if (!plain) { SetOpacity(BShade, 1f); SetOpacity(BGlint, 1f); }
    }

    // U5b：未读刻度 faint 色，已读刻度是绳子上 paper 色 80% 的浅刻痕；靠近的刻度横向放大 1.35 倍并变亮。
    static void Ticks(CordFrame f, IReadOnlyList<CordTick> ticks, int near)
    {
        if (ticks == null) return;
        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            bool isNear = i == near;
            var c = CordGeometry.TickRect(f, t, isNear);
            var r = new D2D1_RECT_F { left = c.Left, top = c.Top, right = c.Right, bottom = c.Bottom };
            nint brush = t.Read ? BCanvas : isNear ? BAccent : HighContrast || BFaint == IntPtr.Zero ? BInk : BFaint;
            if (t.Read) SetOpacity(BCanvas, isNear ? 1f : 0.8f);
            PFill(Rt, &r, brush);
        }
        SetOpacity(BCanvas, 1f);
    }

    // U5c：返回书签。朱色、亮度 .94，左尖右平（演示 polygon(8px 0,100% 0,100% 100%,8px 100%,0 50%)），
    // 内有纸色回退箭头（演示 12 格 path，缩到 10，距左 6）。pose 为弹入动画的位移、缩放、透明度。
    static void BackFlag(CordRect b, (float Dx, float Scale, float Opacity) pose)
    {
        float s = pose.Scale <= 0f ? 1f : pose.Scale, op = pose.Scale <= 0f ? 1f : pose.Opacity;
        float cx = (b.Left + b.Right) / 2f + pose.Dx, cy = (b.Top + b.Bottom) / 2f, w = b.Width, h = b.Height;
        float X(float u) => cx + (u - w / 2f) * s;
        float Y(float v) => cy + (v - h / 2f) * s;
        SetOpacity(BAccent, op);
        if (!HighContrast && BShade != IntPtr.Zero) SetOpacity(BShade, 0.06f * op);
        const int rows = 32;
        for (int i = 0; i < rows; i++)
        {
            float v0 = h * i / rows, v1 = h * (i + 1) / rows, tip = MathF.Abs((v0 + v1) / 2f - h / 2f) / (h / 2f) * 8f;   // 左尖：越靠上下两端越往右缩
            var r = new D2D1_RECT_F { left = X(tip), top = Y(v0), right = X(w), bottom = Y(v1) };
            PFill(Rt, &r, BAccent);
            if (!HighContrast && BShade != IntPtr.Zero) PFill(Rt, &r, BShade);
        }
        SetOpacity(BAccent, 1f);
        if (BShade != IntPtr.Zero) SetOpacity(BShade, 1f);

        // 箭头：M5 2.5 1.8 5.7 5 8.9 M2 5.7 h5.5 a3 3 0 0 1 3 3 v1（12 格 → 10 DIP，左起 6，竖向居中）
        float k = 10f / 12f, ox = 6f, oy = (h - 10f) / 2f, sw = 1.6f * k * s;
        float PX(float u) => X(ox + u * k);
        float PY(float v) => Y(oy + v * k);
        SetOpacity(BCanvas, op);
        StrokeLine(PX(5f), PY(2.5f), PX(1.8f), PY(5.7f), BCanvas, sw);
        StrokeLine(PX(1.8f), PY(5.7f), PX(5f), PY(8.9f), BCanvas, sw);
        StrokeLine(PX(2f), PY(5.7f), PX(7.5f), PY(5.7f), BCanvas, sw);
        float px = 7.5f, py = 5.7f;
        for (int i = 1; i <= 6; i++)   // 四分之一圆弧，圆心 (7.5, 8.7)，半径 3
        {
            float a = MathF.PI / 2f * i / 6f, nx = 7.5f + 3f * MathF.Sin(a), ny = 8.7f - 3f * MathF.Cos(a);
            StrokeLine(PX(px), PY(py), PX(nx), PY(ny), BCanvas, sw);
            px = nx; py = ny;
        }
        StrokeLine(PX(10.5f), PY(8.7f), PX(10.5f), PY(9.7f), BCanvas, sw);
        SetOpacity(BCanvas, 1f);
    }

    // 下端燕尾：中间缩进 9，两侧到底（演示 clip-path polygon(0 0,100% 0,100% 100%,50% calc(100% - 9px),0 100%)）。
    static float Bottom(CordRect c, float u)
    {
        float notch = MathF.Min(DeskMetrics.CordNotch, c.Height);
        return c.Bottom - notch * (1f - MathF.Abs(u - 0.5f) * 2f);
    }

    // 横向明暗：演示 linear-gradient(90deg, rgba(0,0,0,.14), transparent 28%, rgba(255,255,255,.22) 48%, transparent 66%, rgba(0,0,0,.12))。
    // 负值为压暗，正值为提亮。
    internal static float Shade(float u)
    {
        if (u <= 0.28f) return -0.14f * (1f - u / 0.28f);
        if (u <= 0.48f) return 0.22f * (u - 0.28f) / 0.20f;
        if (u <= 0.66f) return 0.22f * (1f - (u - 0.48f) / 0.18f);
        return -0.12f * (u - 0.66f) / 0.34f;
    }

    static void Shape(CordRect c, float dx, float dy, nint brush, float opacity, float grow)
    {
        SetOpacity(brush, opacity);
        float w = c.Width + grow * 2f, step = w / Slices;
        for (int i = 0; i < Slices; i++)
        {
            float u = (i + 0.5f) / Slices;
            var r = new D2D1_RECT_F { left = c.Left - grow + dx + i * step, top = c.Top + dy, right = c.Left - grow + dx + (i + 1) * step, bottom = Bottom(c, u) + dy + grow };
            PFill(Rt, &r, brush);
        }
    }
}
