using System;
using System.Runtime.InteropServices;
using MDViewerNative;

// 离屏渲染一帧并读取像素(不显示窗口、不注入输入)。宽高为物理像素,滚动/视口/顶栏为 DIP。
// 供各离屏像素测试共用。
internal sealed class OffscreenFrame : IDisposable
{
    public const float Top = 36f;
    private readonly nint _screen, _memory, _bitmap, _previous;
    public readonly int W, H;

    public OffscreenFrame(int width, int height)
    {
        W = width; H = height;
        _screen = GetDC(0); _memory = CreateCompatibleDC(_screen);
        _bitmap = CreateCompatibleBitmap(_screen, width, height); _previous = SelectObject(_memory, _bitmap);
    }

    public bool Ok => _screen != 0 && _memory != 0 && _bitmap != 0;
    public float ViewH => H / RenderResources.Scale - Top;

    public void Paint(DocView doc, float scrollDip, ReturnHint returnHint = null, bool zenQuiet = false)
        => doc.Paint(new RECT { R = W, B = H }, _memory, scrollDip, ViewH, Top, null, 0f, returnHint, zenQuiet);

    public uint At(int x, int y) => GetPixel(_memory, x, y);
    public int CountRow(int y, uint color) { int n = 0; for (int x = 0; x < W; x++) if (At(x, y) == color) n++; return n; }

    // 深色像素(文字墨迹)。fromX 之左(目录标尺,无标题时的入口短线也够深)不计;浅色细线、斑马纹、面板底色不算墨迹。
    public bool Ink(int x, int y) { uint c = At(x, y); return (c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF) < 3 * 110; }

    // 自上而下的文字带(连续有墨迹的像素行)。
    public System.Collections.Generic.List<(int Top, int Bottom)> InkBands(int fromX)
    {
        var bands = new System.Collections.Generic.List<(int Top, int Bottom)>();
        int start = -1;
        for (int y = 0; y < H; y++)
        {
            bool any = false;
            for (int x = fromX; x < W && !any; x++) any = Ink(x, y);
            if (any && start < 0) start = y;
            if (!any && start >= 0) { bands.Add((start, y)); start = -1; }
        }
        if (start >= 0) bands.Add((start, H));
        return bands;
    }

    // 一条文字带内按空隙 > gap 像素切分的墨迹块(左右边缘)。
    public System.Collections.Generic.List<(int L, int R)> InkClusters((int Top, int Bottom) band, int fromX, int gap)
    {
        var result = new System.Collections.Generic.List<(int L, int R)>();
        int left = -1, last = -gap - 1;
        for (int x = fromX; x < W; x++)
        {
            bool any = false;
            for (int y = band.Top; y < band.Bottom && !any; y++) any = Ink(x, y);
            if (!any) continue;
            if (x - last > gap) { if (left >= 0) result.Add((left, last)); left = x; }
            last = x;
        }
        if (left >= 0) result.Add((left, last));
        return result;
    }

    public static uint Colorref(uint rgb) => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

    public void Dispose()
    {
        if (_previous != 0) SelectObject(_memory, _previous);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_memory != 0) DeleteDC(_memory);
        if (_screen != 0) ReleaseDC(0, _screen);
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
