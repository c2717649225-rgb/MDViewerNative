// Win32 互操作:D2D 走原生 shim + 原始 vtable 直调,DWrite 走 ComImport(均已验证)。
// vtable 槽位权威来源:本机 SDK 头文件 d2d1.h / dwrite.h(10.0.26100),逐槽核对过。
//   DCRT 槽位 = 3(IUnknown) + 1(GetFactory) + RT 第 k 个方法(0-based) + 4
//   TextLayout flat 槽位 = 3 + 25(TextFormat) + 自有序号
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MDViewerNative;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int L, T, R, B; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
    public uint time; public int ptX, ptY;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint cbSize; public uint style; public IntPtr lpfnWndProc;
    public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
    public string lpszMenuName;
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SCROLLINFO
{
    public uint cbSize, fMask; public int nMin, nMax, nPage, nPos, nTrackPos;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_POINT_2F { public float x, y; }
internal struct D2D1_ELLIPSE { public D2D1_POINT_2F point; public float radiusX, radiusY; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RECT_F { public float left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_COLOR_F { public float r, g, b, a; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_MATRIX_3X2_F { public float m11, m12, m21, m22, dx, dy; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_ROUNDED_RECT { public D2D1_RECT_F rect; public float radiusX, radiusY; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_BRUSH_PROPERTIES { public float opacity; public D2D1_MATRIX_3X2_F transform; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_PIXEL_FORMAT { public int format; public int alphaMode; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_SIZE_U { public uint width, height; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_BITMAP_PROPERTIES { public D2D1_PIXEL_FORMAT pixelFormat; public float dpiX, dpiY; }

[StructLayout(LayoutKind.Sequential)]
internal struct D2D1_RENDER_TARGET_PROPERTIES
{
    public int type;
    public D2D1_PIXEL_FORMAT pixelFormat;
    public int usage;
    public float dpiX, dpiY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_TEXT_METRICS
{
    public float left, top, width, widthIncludingTrailingWhitespace, height, layoutWidth, layoutHeight;
    public uint maxBidiReorderingDepth, lineCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_TEXT_RANGE { public uint first, length; }

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_INLINE_OBJECT_METRICS
{
    public float width, height, baseline;
    public int supportsSideways;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_OVERHANG_METRICS
{
    public float left, top, right, bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_LINE_METRICS
{
    public uint length, trailingWhitespaceLength, newlineLength;
    public float height, baseline;
    public int isTrimmed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWRITE_HIT_TEST_METRICS
{
    public uint textPosition, length;
    public float left, top, width, height;
    public uint bidiLevel;
    public int isText, isTrimmed;
}

internal static unsafe class Externs
{
    [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
    public static extern bool InvalidateRectRect(IntPtr h, ref RECT r, int erase);

    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern uint SetBkColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")]
    public static extern int SetWindowLongW(IntPtr h, int idx, int value);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr h, string subApp, string subIdList);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr val);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vk);

    // Physical key state, independent of this thread's message queue. Required for
    // cooperative cancellation: synchronous parse/layout never pumps messages, so
    // GetKeyState would not observe the user pressing Esc during the operation.
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);

    [DllImport("user32.dll")] public static extern bool IsChild(nint parent, nint child);

    public static string WindowText(IntPtr window)
    {
        var text = new System.Text.StringBuilder(GetWindowTextLengthW(window) + 1);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    public static (int start, int end) EditSelection(IntPtr editor)
    {
        int start = 0, end = 0;
        SendMessageW(editor, 0x00B0 /*EM_GETSEL*/, (IntPtr)(&start), (IntPtr)(&end));
        return (start, end);
    }

    public const int WM_PAINT = 0x000F, WM_DESTROY = 0x0002, WM_SIZE = 0x0005,
        WM_CLOSE = 0x0010, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A,
        WM_MOUSEHWHEEL = 0x020E,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_SETCURSOR = 0x0020,
        WM_KEYDOWN = 0x0100, WM_DROPFILES = 0x0233, WM_COMMAND = 0x0111,
        WM_ERASEBKGND = 0x0014;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(in WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    public static extern IntPtr GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(in MSG m);
    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(in MSG m);
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int c);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr h, IntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr h, string text, string caption, int type);
    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr h, IntPtr rect, int erase);
    [DllImport("user32.dll")]
    public static extern int SetScrollInfo(IntPtr h, int bar, in SCROLLINFO si, int redraw);
    [DllImport("user32.dll")]
    public static extern int GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SetCursor(IntPtr cur);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("shell32.dll")]
    public static extern void DragAcceptFiles(IntPtr h, int accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hd, uint index, System.Text.StringBuilder sb, uint max);
    // 取拖放文件数:标准做法 DragQueryFileW(hd, 0xFFFFFFFF, NULL, 0);shell32 无独立的 Count 导出。
    // 必须用完整 4 参签名并显式传 index=0xFFFFFFFF/NULL/0:少参 P/Invoke 会把垃圾寄存器值传给原生函数(UB)
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileCount(IntPtr hd, uint index, IntPtr buf, uint max);
    [DllImport("shell32.dll")]
    public static extern bool DragFinish(IntPtr hd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(IntPtr h, string verb, string file, string args, string dir, int show);
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENFILENAMEW
    {
        public int lStructSize; public IntPtr hwndOwner; public IntPtr hInstance;
        public string lpstrFilter; public string lpstrCustomFilter; public IntPtr nMaxCustFilter;
        public int nFilterIndex; public string lpstrFile; public int nMaxFile;
        public string lpstrFileTitle; public IntPtr nMaxFileTitle; public string lpstrInitialDir;
        public string lpstrTitle; public int Flags; public ushort nFileOffset; public ushort nFileExtension;
        public string lpstrDefExt; public IntPtr lCustData; public IntPtr lpfnHook; public IntPtr lpTemplateName;
        public IntPtr pvReserved; public int dwReserved; public int flagsEx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr h, in PAINTSTRUCT ps);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
}


// ---------- WIC 图片解码(原始 vtable 直调;槽序来自 wincodec.idl) ----------
// IWICImagingFactory: CreateDecoderFromFilename=0, CreateFormatConverter=7
// IWICBitmapDecoder:  GetFrame=10
// IWICBitmapFrameDecode(扁平): GetSize=3, CopyPixels=7(前缀即 IWICBitmapSource)
// IWICFormatConverter: Initialize=5
internal static unsafe class Wic
{
    public static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a"); // CLSID_WICImagingFactory
    public static readonly Guid Pbg = new("6fddc324-4e03-4bfe-b185-3d77768dc910");           // 32bppPBGRA

    static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, IntPtr*, int> _pDecFile;
    static delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int> _pConv;
    static delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int> _pFrame;
    static delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int> _pGetSize;
    static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, IntPtr, int> _pInit;

    static IntPtr _fac = IntPtr.Zero;
    public static IntPtr Fac
    {
        get
        {
            if (_fac != IntPtr.Zero) return _fac;
            Guid iidU = new("00000000-0000-0000-C000-000000000046");
            int hr = Wic.CoCreateInstance(in ClsidFactory, IntPtr.Zero, 1, in iidU, out _fac);
            if (hr < 0 || _fac == IntPtr.Zero) throw new Exception($"WIC factory 0x{hr:X8}");
            return _fac;
        }
    }

    /// 解码图片为 PBGRA 的 D2D 位图,返回 像素宽/高
    public static unsafe IntPtr DecodeToBitmap(string path, out uint pxW, out uint pxH)
    {
        pxW = pxH = 0;
        var fac = Fac;
        _pDecFile = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, IntPtr*, int>)(*(IntPtr**)fac)[3];
        _pConv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)fac)[10];

        var pPath = Marshal.StringToHGlobalUni(path);
        try
        {
            IntPtr dec = IntPtr.Zero;
            int hr = _pDecFile(fac, pPath, IntPtr.Zero, 0x80000000u /*GENERIC_READ*/, 0, &dec);
            if (hr < 0 || dec == IntPtr.Zero) return IntPtr.Zero;
            IntPtr frame = IntPtr.Zero;
            IntPtr conv = IntPtr.Zero;
            bool ok = false;
            try
            {
                _pFrame = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)dec)[13];
                hr = _pFrame(dec, 0, &frame);
                if (hr < 0 || frame == IntPtr.Zero) return IntPtr.Zero;

                _pGetSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)(*(IntPtr**)frame)[3];
                uint lw = 0, lh = 0;
                hr = _pGetSize(frame, &lw, &lh);
                pxW = lw; pxH = lh;
                if (hr < 0 || !ImageLimits.ValidDimensions(lw, lh)) return IntPtr.Zero;

                hr = _pConv(fac, &conv);
                if (hr < 0 || conv == IntPtr.Zero) return IntPtr.Zero;
                _pInit = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, IntPtr, int>)(*(IntPtr**)conv)[8];
                var pb = Pbg;
                hr = _pInit(conv, frame, &pb, 0, IntPtr.Zero, 1.0, IntPtr.Zero);
                if (hr < 0) return IntPtr.Zero;
                ok = true;
                return conv; // 调用方负责 Release;decoder/frame 在此释放(converter 已持引用)
            }
            finally
            {
                if (!ok && conv != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)conv)[2])(conv);   // Initialize 失败时 converter 也要释放
                if (frame != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)frame)[2])(frame);
                if (dec != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)dec)[2])(dec);
            }
        }
        finally { Marshal.FreeHGlobal(pPath); }
    }

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pv, uint coInit);
    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint clsCtx, in Guid iid, out IntPtr obj);

}

// ---------- 剪贴板 ----------
internal static class Clipboard
{
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint fmt, IntPtr data);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 2;

    public static bool SetText(string s, nint owner)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(s + "\0");
        return Set(owner, (CF_UNICODETEXT, bytes));
    }

    // 复制为 HTML(41):纯文本 + "HTML Format" 双格式;任一格式失败即整体失败(剪贴板已清空,不留半套)
    public static bool SetTextAndHtml(string text, string htmlFragment, nint owner)
    {
        uint cfHtml = RegisterClipboardFormatW("HTML Format");
        if (cfHtml == 0) return false;
        return Set(owner, (CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0")), (cfHtml, ClipboardHtml.Wrap(htmlFragment)));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);

    // Prepare every payload before opening/emptying the shared clipboard; Windows owns a block only after a successful transfer.
    static bool Set(nint owner, params (uint Format, byte[] Bytes)[] payloads)
    {
        var handles = new nint[payloads.Length];
        try
        {
            for (int i = 0; i < payloads.Length; i++)
            {
                var bytes = payloads[i].Bytes;
                nint memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
                if (memory == 0) return false;
                handles[i] = memory;
                nint data = GlobalLock(memory);
                if (data == 0) return false;
                try { Marshal.Copy(bytes, 0, data, bytes.Length); }
                finally { GlobalUnlock(memory); }
            }
            if (!OpenClipboard(owner)) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                for (int i = 0; i < payloads.Length; i++)
                {
                    if (SetClipboardData(payloads[i].Format, handles[i]) == 0) return false;
                    handles[i] = 0;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { foreach (nint h in handles) if (h != 0) GlobalFree(h); }
    }

    [DllImport("user32.dll")] public static extern bool CloseClipboard();
}

internal static class D2D
{
    // 原生 shim:单次调用建齐 工厂+DCRT+SetDpi+笔刷(绕开 .NET 进程中跨 P/Invoke 拆分的 E_INVALIDARG 怪癖)
    [DllImport("MDNative.dll")]
    public static extern int MD_CreateD2D(float dpiX, float dpiY,
        out IntPtr rt, out IntPtr brushInk, out IntPtr brushCode,
        out IntPtr brushHair, out IntPtr brushQuote, out uint hr);
}
