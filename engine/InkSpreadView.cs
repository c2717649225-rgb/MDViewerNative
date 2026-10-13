using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// U11b：换纸时的晕开过渡（演示 view-transition inkspread）：换纸前拍下主窗口客户区，
// 之后每一帧新纸照常绘制，旧画面只盖在不断扩大的圆外面。半径与缓动在 DeskAnimations。
// 子窗口（编辑框）不参与：它自己按新纸重画，圆外那一小段时间直接显示新色。
internal sealed class InkSpreadView : IDisposable
{
    nint _dc, _bmp, _old;
    int _w, _h;

    public bool Active => _dc != 0;
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width => _w;
    public int Height => _h;

    // x、y：圆心（客户区物理像素）。拍不下来时返回 false，调用方直接换纸。
    public bool Begin(nint window, int w, int h, int x, int y)
    {
        End();
        if (window == 0 || w <= 0 || h <= 0) return false;
        nint screen = GetDC(window);
        if (screen == 0) return false;
        nint dc = 0, bmp = 0, old = 0;
        bool ok = false;
        try
        {
            dc = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            if (dc == 0 || bmp == 0) return false;
            old = SelectObject(dc, bmp);
            // PW_CLIENTONLY | PW_RENDERFULLCONTENT：只拍自己的窗口。拍的过程中窗口可能同步重画一次，
            // 所以拍完才记入字段（此前 Active 为 false，重画不会误用半成品）。
            ok = PrintWindow(window, dc, 1 | 2);
        }
        finally
        {
            _ = ReleaseDC(window, screen);
            if (!ok)
            {
                if (dc != 0 && old != 0) SelectObject(dc, old);
                if (bmp != 0) Externs.DeleteObject(bmp);
                if (dc != 0) DeleteDC(dc);
            }
        }
        if (!ok) return false;
        _dc = dc; _bmp = bmp; _old = old;
        _w = w; _h = h; X = x; Y = y;
        return true;
    }

    // 画在已完成新纸绘制的目标 DC 上：圆外盖旧画面。
    public void Paint(nint dc, float radius)
    {
        if (!Active) return;
        int r = (int)MathF.Ceiling(radius);
        nint all = CreateRectRgn(0, 0, _w, _h), hole = CreateEllipticRgn(X - r, Y - r, X + r + 1, Y + r + 1);
        try
        {
            if (CombineRgn(all, all, hole, 4 /*RGN_DIFF*/) <= 1) return;   // NULLREGION / ERROR：已全部晕开
            _ = SelectClipRgn(dc, all);
            _ = BitBlt(dc, 0, 0, _w, _h, _dc, 0, 0, 0x00CC0020);
            _ = SelectClipRgn(dc, 0);
        }
        finally { Externs.DeleteObject(all); Externs.DeleteObject(hole); }
    }

    public void End()
    {
        if (_dc != 0 && _old != 0) SelectObject(_dc, _old);
        if (_bmp != 0) Externs.DeleteObject(_bmp);
        if (_dc != 0) DeleteDC(_dc);
        _dc = _bmp = _old = 0;
    }

    public void Dispose() => End();

    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern nint CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(nint dst, nint a, nint b, int mode);
    [DllImport("gdi32.dll")] static extern int SelectClipRgn(nint dc, nint rgn);
}
