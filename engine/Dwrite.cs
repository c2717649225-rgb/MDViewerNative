// DWrite 原始 vtable 直调(AOT 兼容,零 marshaler)。
// 槽位来源 dwrite.h:工厂 = 3(IUnknown) + 0-based 方法序;布局 flat = 3 + 25(TextFormat) + 自有序号。
// 注意:SetLineSpacing 参数序 = (method, lineSpacing, baseline);WRAP=0。
using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

internal static unsafe class Dw
{
    public static IntPtr Factory;

    public static void Init()
    {
        Guid iid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"); // IID_IDWriteFactory — 必须取工厂接口指针(非 IUnknown),否则 vtable 只有 3 槽
        int hr = DWriteCreateFactory(0, in iid, out Factory);
        if (hr < 0 || Factory == IntPtr.Zero) throw new Exception("DWriteCreateFactory 0x" + hr.ToString("X8"));
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int type, in Guid iid, out IntPtr factory);

    static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, int, int, float, IntPtr, IntPtr*, int> _pFmt;   // 15
    static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, float, float, IntPtr*, int> _pLayout;          // 18

    public static IntPtr MkFormat(string family, uint weight, float size)
    {
        _pFmt = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, int, int, float, IntPtr, IntPtr*, int>)(*(IntPtr**)Factory)[15];
        IntPtr pf = Marshal.StringToHGlobalUni(family);
        IntPtr pl = Marshal.StringToHGlobalUni("zh-cn");
        IntPtr f = IntPtr.Zero;
        int hr = _pFmt(Factory, pf, IntPtr.Zero, weight, 0, 5, size, pl, &f);
        Marshal.FreeHGlobal(pf);
        Marshal.FreeHGlobal(pl);
        if (hr < 0 || f == IntPtr.Zero) throw new Exception("CreateTextFormat 0x" + hr.ToString("X8"));
        return f;
    }

    public static IntPtr MkLayout(string s, IntPtr fmt, float w)
    {
        _pLayout = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, float, float, IntPtr*, int>)(*(IntPtr**)Factory)[18];
        IntPtr l = IntPtr.Zero;
        fixed (char* ps = s)
        {
            int hr = _pLayout(Factory, (IntPtr)ps, (uint)s.Length, fmt, w, 100000f, &l);
            if (hr < 0 || l == IntPtr.Zero) throw new Exception("CreateTextLayout 0x" + hr.ToString("X8"));
        }
        return l;
    }

    // 布局 flat 槽位
    public static delegate* unmanaged[Stdcall]<IntPtr, int, void> SetTextAlignment;                   // 3
    public static delegate* unmanaged[Stdcall]<IntPtr, int, void> SetParagraphAlignment;              // 4
    public static delegate* unmanaged[Stdcall]<IntPtr, int, void> SetWordWrapping;                    // 5
    public static delegate* unmanaged[Stdcall]<IntPtr, int, float, float, void> SetLineSpacing;       // 10
    public static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWRITE_TEXT_RANGE, void> SetFontFamilyName; // 31
    public static delegate* unmanaged[Stdcall]<IntPtr, uint, DWRITE_TEXT_RANGE, void> SetFontWeight;  // 32
    public static delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void> SetFontStyle;    // 33
    public static delegate* unmanaged[Stdcall]<IntPtr, float, DWRITE_TEXT_RANGE, void> SetFontSize;   // 35
    public static delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void> SetUnderline;    // 36
    public static delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void> SetStrikethrough; // 37
    public static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWRITE_TEXT_RANGE, int> SetDrawingEffect; // 38(效果对象为 D2D 画刷)
    public static delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, DWRITE_TEXT_RANGE*, int> GetDrawingEffect; // 53(返回值已 AddRef)
    public static delegate* unmanaged[Stdcall]<IntPtr, DWRITE_LINE_METRICS*, uint, uint*, int> GetLineMetrics;  // 59
    public static delegate* unmanaged[Stdcall]<IntPtr, DWRITE_TEXT_METRICS*, void> GetMetrics;        // 60
    public static delegate* unmanaged[Stdcall]<IntPtr, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int> HitPoint;  // 64(PosAt 已改行定位,不再使用)
    public static delegate* unmanaged[Stdcall]<IntPtr, uint, uint, float, float, DWRITE_HIT_TEST_METRICS*, uint, uint*, int> HitRange;  // 66

    /// 把各槽位委托绑定到指定布局(所有布局同构,每次建后调用)
    public static void Bind(IntPtr l)
    {
        SetTextAlignment = (delegate* unmanaged[Stdcall]<IntPtr, int, void>)(*(IntPtr**)l)[3];
        SetParagraphAlignment = (delegate* unmanaged[Stdcall]<IntPtr, int, void>)(*(IntPtr**)l)[4];
        SetWordWrapping = (delegate* unmanaged[Stdcall]<IntPtr, int, void>)(*(IntPtr**)l)[5];
        SetLineSpacing = (delegate* unmanaged[Stdcall]<IntPtr, int, float, float, void>)(*(IntPtr**)l)[10];
        SetFontFamilyName = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[31];
        SetFontWeight = (delegate* unmanaged[Stdcall]<IntPtr, uint, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[32];
        SetFontStyle = (delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[33];
        SetFontSize = (delegate* unmanaged[Stdcall]<IntPtr, float, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[35];
        SetUnderline = (delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[36];
        SetStrikethrough = (delegate* unmanaged[Stdcall]<IntPtr, int, DWRITE_TEXT_RANGE, void>)(*(IntPtr**)l)[37];
        SetDrawingEffect = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWRITE_TEXT_RANGE, int>)(*(IntPtr**)l)[38];
        GetDrawingEffect = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, DWRITE_TEXT_RANGE*, int>)(*(IntPtr**)l)[53];
        GetLineMetrics = (delegate* unmanaged[Stdcall]<IntPtr, DWRITE_LINE_METRICS*, uint, uint*, int>)(*(IntPtr**)l)[59];
        GetMetrics = (delegate* unmanaged[Stdcall]<IntPtr, DWRITE_TEXT_METRICS*, void>)(*(IntPtr**)l)[60];
        HitPoint = (delegate* unmanaged[Stdcall]<IntPtr, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int>)(*(IntPtr**)l)[64];
        HitRange = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, float, float, DWRITE_HIT_TEST_METRICS*, uint, uint*, int>)(*(IntPtr**)l)[66];
    }

    public static float LayoutH(nint l)
    {
        if (l == IntPtr.Zero)
        {
            return 0f;
        }
        Dw.Bind(l);
        DWRITE_TEXT_METRICS dWRITE_TEXT_METRICS = default(DWRITE_TEXT_METRICS);
        Dw.GetMetrics(l, &dWRITE_TEXT_METRICS);
        return dWRITE_TEXT_METRICS.height;
    }

    // 行度量(每行字符数、尾随空白等)。
    public static DWRITE_LINE_METRICS[] Lines(nint l)
    {
        Bind(l);
        uint n = 0;
        GetLineMetrics(l, null, 0, &n);
        var lines = new DWRITE_LINE_METRICS[n];
        if (n > 0) fixed (DWRITE_LINE_METRICS* p = lines) GetLineMetrics(l, p, n, &n);
        return lines;
    }

    // 一段文字的排版宽度(各命中矩形宽度之和;字距调整计入)。
    public static float RangeWidth(nint l, uint start, uint length)
    {
        if (length == 0) return 0f;
        Bind(l);
        uint n = 0;
        HitRange(l, start, length, 0f, 0f, null, 0, &n);
        if (n == 0) return 0f;
        var metrics = new DWRITE_HIT_TEST_METRICS[n];
        float width = 0f;
        fixed (DWRITE_HIT_TEST_METRICS* p = metrics)
        {
            if (HitRange(l, start, length, 0f, 0f, p, n, &n) < 0) return 0f;
            for (int i = 0; i < n; i++) width += p[i].width;
        }
        return width;
    }

    // IDWriteTextLayout::SetMaxWidth(28)。
    public static void SetMaxWidth(nint l, float width) => ((delegate* unmanaged[Stdcall]<IntPtr, float, int>)(*(IntPtr**)l)[28])(l, width);
    // 内嵌对象(S77 行内公式);布局 AddRef 持有对象。
    public static int SetInlineObject(nint l, nint obj, DWRITE_TEXT_RANGE r) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DWRITE_TEXT_RANGE, int>)(*(IntPtr**)l)[39])(l, obj, r);
    // 格式默认字号(IDWriteTextFormat::GetFontSize,第 25 槽;布局继承自格式)。标题格式各自带字号。
    public static float FontSize(nint l) => ((delegate* unmanaged[Stdcall]<IntPtr, float>)(*(IntPtr**)l)[25])(l);

    // IDWriteTextLayout1::SetCharacterSpacing(69),经 QueryInterface(0)取接口(Windows 8+)。
    public static bool SetCharacterSpacing(nint l, float leading, float trailing, uint start, uint length)
    {
        Guid iid = new("9064d822-80a7-465c-a986-df65f78b8feb");   // IID_IDWriteTextLayout1
        IntPtr l1 = IntPtr.Zero;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)l)[0])(l, &iid, &l1) < 0 || l1 == IntPtr.Zero) return false;
        try
        {
            var range = new DWRITE_TEXT_RANGE { first = start, length = length };
            return ((delegate* unmanaged[Stdcall]<IntPtr, float, float, float, DWRITE_TEXT_RANGE, int>)(*(IntPtr**)l1)[69])(l1, leading, trailing, 0f, range) >= 0;
        }
        finally { Marshal.Release(l1); }
    }

    public static float LayoutW(nint l)
    {
        if (l == IntPtr.Zero)
        {
            return 0f;
        }
        Dw.Bind(l);
        DWRITE_TEXT_METRICS dWRITE_TEXT_METRICS = default(DWRITE_TEXT_METRICS);
        Dw.GetMetrics(l, &dWRITE_TEXT_METRICS);
        return dWRITE_TEXT_METRICS.widthIncludingTrailingWhitespace;
    }

    // Shared single-line ellipsis for navigation text; trim only at Unicode text-element boundaries.
    public static nint TrimLayout(string text, nint format, float width)
    {
        bool Fits(string value)
        {
            nint layout = MkLayout(value, format, 10000f);
            try { return LayoutW(layout) <= width; }
            finally { Marshal.Release(layout); }
        }
        if (text.Length == 0 || Fits(text)) return MkLayout(text, format, 10000f);
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        int lo = 0, hi = starts.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            int end = mid == starts.Length ? text.Length : starts[mid];
            if (Fits(text[..end] + "…")) lo = mid; else hi = mid - 1;
        }
        return MkLayout(text[..(lo == starts.Length ? text.Length : starts[lo])] + "…", format, 10000f);
    }

    internal static void Shutdown()
    {
        if (Factory == IntPtr.Zero) return;
        Marshal.Release(Factory);
        Factory = IntPtr.Zero;
    }
}
