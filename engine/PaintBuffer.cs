using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// 试用修复：主窗口 WM_PAINT 的离屏缓冲。一帧分多层画（正文、书签绳、页脚、浮层、顶栏），
// 每层各自 BindDC + EndDraw；直接画到窗口时 DWM 可能在两层之间合成，露出半成品（顶栏被正文盖住、页脚一闪）。
// 各层改画到这张常驻位图上，最后按更新矩形一次贴到窗口。位图跨帧保留，没被波及的区域保持上一帧像素，
// 与原来“只重绘被波及条带”的分工一致；尺寸变化时重建，重建那一帧由调用方整幅重画。
internal sealed class PaintBuffer : IDisposable
{
    nint _dc, _bmp, _old;
    int _w, _h;

    // 返回离屏 DC，并把裁剪设为 paint（与 BeginPaint 的裁剪同效）；fresh 为 true 表示位图刚重建、内容为空。
    // 建不起来时返回 0，调用方直接画到窗口 DC。
    public nint Begin(nint target, int w, int h, RECT paint, out bool fresh)
    {
        fresh = false;
        if (target == 0 || w <= 0 || h <= 0) return 0;
        if (_dc == 0 || w != _w || h != _h)
        {
            End();
            nint dc = CreateCompatibleDC(target);
            nint bmp = dc == 0 ? 0 : CreateCompatibleBitmap(target, w, h);
            if (bmp == 0)
            {
                if (dc != 0) DeleteDC(dc);
                return 0;
            }
            _dc = dc; _bmp = bmp; _old = SelectObject(dc, bmp);
            _w = w; _h = h;
            fresh = true;
            paint = new RECT { L = 0, T = 0, R = w, B = h };
        }
        nint clip = CreateRectRgn(paint.L, paint.T, paint.R, paint.B);
        _ = SelectClipRgn(_dc, clip);
        Externs.DeleteObject(clip);
        return _dc;
    }

    // 把 paint 矩形贴到窗口 DC（其裁剪仍是本次更新区域），然后清掉离屏裁剪。
    public void Present(nint target, RECT paint)
    {
        if (_dc == 0) return;
        _ = BitBlt(target, paint.L, paint.T, paint.R - paint.L, paint.B - paint.T, _dc, paint.L, paint.T, 0x00CC0020 /*SRCCOPY*/);
        _ = SelectClipRgn(_dc, 0);
    }

    public void End()
    {
        if (_dc != 0 && _old != 0) SelectObject(_dc, _old);
        if (_bmp != 0) Externs.DeleteObject(_bmp);
        if (_dc != 0) DeleteDC(_dc);
        _dc = _bmp = _old = 0;
        _w = _h = 0;
    }

    public void Dispose() => End();

    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int SelectClipRgn(nint dc, nint rgn);
    [DllImport("gdi32.dll")] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
