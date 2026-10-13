using System;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// U9b：信笺叠画。RichEdit 自己画完（WM_PAINT，以及它在输入、选区、滚动时的直接重画）之后，
// 用 GDI 在编辑框里补上：每个显示行底一道淡朱格线、左侧双线页边、光标所在源码行最后一个显示行下的镇尺。
// 只画 1–2 像素的横线与竖线，不碰字形；镇尺换行时让旧位置失效，由 RichEdit 重画后再叠一次。
internal sealed unsafe partial class EditorControl
{
    private LetterStyle? _letter;
    private int _rulerSource = -1;
    private long _rulerSince;
    private RECT _rulerRect;
    private const nuint RulerTimer = 8;

    [DllImport("user32.dll")] private static extern int FillRect(nint dc, RECT* rect, nint brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, RECT* rect, bool erase);

    // null＝经典编辑框（不画信笺，行号按显示行）。
    public LetterStyle? Letter
    {
        get => _letter;
        set
        {
            if (_letter == value) return;
            _letter = value;
            _rulerSource = -1;
            if (Handle == 0) return;
            Resize(_top);
            InvalidateRect(Handle, null, true);
        }
    }

    private float TextLeftInset => _letter != null ? LetterPaper.TextInset : 8f;

    private static uint Bgr(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private int LineTopPx(int line)
    {
        int index = (int)Native(0x00BB /*EM_LINEINDEX*/, line, 0);
        TextPoint point = default;
        Native(0x00D6 /*EM_POSFROMCHAR*/, (nint)(&point), index);
        return point.Y;
    }

    // 光标（选区起点）所在源码行的最后一个显示行。
    private (int Source, int Visual) CaretParagraph()
    {
        int* range = stackalloc int[2];
        Native(0x0434 /*EM_EXGETSEL*/, 0, (nint)range);
        var c = Coordinates;
        int end = c.ParagraphEndNative(range[0]);
        int visual = end < 0 ? (int)Native(0x00BA /*EM_GETLINECOUNT*/) - 1 : (int)Native(0x0436 /*EM_EXLINEFROMCHAR*/, 0, end);
        return (c.LineOfNative(range[0]), visual);
    }

    private static void Fill(nint dc, int l, int t, int r, int b, nint brush)
    {
        if (r <= l || b <= t) return;
        var rect = new RECT { L = l, T = t, R = r, B = b };
        FillRect(dc, &rect, brush);
    }

    private void PaintLetter()
    {
        if (_letter is not LetterStyle s || Handle == 0 || !IsWindowVisible(Handle)) return;
        nint dc = GetDC(Handle);
        if (dc == 0) return;
        try { DrawLetter(dc, s); }
        finally { ReleaseDC(Handle, dc); }
    }

    private void DrawLetter(nint dc, LetterStyle s)
    {
        Externs.GetClientRect(Handle, out var rc);
        int px = Math.Max(1, (int)MathF.Round(Scale));
        int mx = (int)MathF.Round(LetterPaper.MarginX * Scale), inner = mx + 3 * px;
        var (source, visual) = CaretParagraph();
        if (source != _rulerSource)
        {
            if (_rulerRect.B > _rulerRect.T) { var old = _rulerRect; InvalidateRect(Handle, &old, false); }
            _rulerSource = source;
            _rulerSince = Environment.TickCount64;
            SetTimer(Handle, RulerTimer, 16, 0);
        }
        nint rule = Externs.CreateSolidBrush(Bgr(s.Rule));
        try
        {
            Fill(dc, mx, rc.T, mx + px, rc.B, rule);
            Fill(dc, mx + 2 * px, rc.T, inner, rc.B, rule);
            int first = (int)Native(0x00CE /*EM_GETFIRSTVISIBLELINE*/), count = (int)Native(0x00BA /*EM_GETLINECOUNT*/);
            int pitch = (int)MathF.Round(LineHeight), top = LineTopPx(first);
            _rulerRect = default;
            for (int n = first; n < count && top < rc.B; n++)
            {
                int next = n + 1 < count ? LineTopPx(n + 1) : top + pitch;
                if (next > top) pitch = next - top;
                int y = LetterPaper.RuleY(top, next, pitch);
                if (n == visual) DrawRuler(dc, s, inner, rc.R, y + 1);
                else if (y >= rc.T) Fill(dc, inner, y, rc.R, y + px, rule);
                top = next;
            }
        }
        finally { Externs.DeleteObject(rule); }
    }

    // 镇尺底边贴着该行底（与格线同一条），高 1.5；动画期间按进度混色、横向展开。
    private void DrawRuler(nint dc, LetterStyle s, int left, int right, int bottom)
    {
        float t = Math.Clamp((Environment.TickCount64 - _rulerSince) / (float)LetterPaper.RulerInMs, 0f, 1f);
        int h = Math.Max(1, (int)MathF.Round(LetterPaper.RulerHeight * Scale));
        var (l, r) = LetterPaper.RulerSpan(left, right, t);
        nint brush = Externs.CreateSolidBrush(Bgr(LetterPaper.RulerColor(s, t)));
        try { Fill(dc, (int)MathF.Round(l), bottom - h, (int)MathF.Round(r), bottom, brush); }
        finally { Externs.DeleteObject(brush); }
        _rulerRect = new RECT { L = left, T = bottom - h, R = right, B = bottom };
        if (t >= 1f) KillTimer(Handle, RulerTimer);
    }

    // RichEdit 在这些消息里会直接重画文字（不经 WM_PAINT），画完后补一次信笺。
    private static bool RepaintsText(uint m, nint w) => m is 0x000F or 0x0100 or 0x0101 or 0x0102 or 0x010F or 0x0201 or 0x0202 or 0x0203 or 0x020A or 0x0115 or 0x00B1 or 0x00C2 or 0x000C or 0x0302 or 0x0300 or 0x0303 or 0x0304
        || (m == 0x0200 && (w & 1) != 0) || m == 0x0113;
}
