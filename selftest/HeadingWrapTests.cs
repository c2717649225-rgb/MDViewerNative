using System;
using System.Runtime.InteropServices;
using MDViewerNative;

// 标题折行精修(S74):末行孤字平衡、行首全角开括号压缩。用排版度量验证,截图见 work/arena/dogfood/r10/。
internal static class HeadingWrapTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        using var init = new DocView();   // 确保字体格式已建
        nint fmt = RenderResources.FmtH2;
        uint Last(nint l) { var lines = Dw.Lines(l); var last = lines[^1]; return last.length - last.trailingWhitespaceLength - last.newlineLength; }
        uint StartOf(nint l, int line) { var lines = Dw.Lines(l); uint s = 0; for (int i = 0; i < line; i++) s += lines[i].length; return s; }

        // 末行孤字:找出原样排版恰好剩 1~2 字的宽度(与字体度量无关),精修后末行至少为全文 1/4。
        const string widow = "九、代码块【等宽字体 + 灰底 + 特殊字符 + 长行】";
        float ww = 0f;
        for (float w = 200f; w < 700f && ww == 0f; w += 2f)
        {
            nint probe = Dw.MkLayout(widow, fmt, w);
            try { if (Dw.Lines(probe).Length == 2 && Last(probe) <= 2) ww = w; }
            finally { Marshal.Release(probe); }
        }
        nint wl = ww > 0f ? Dw.MkLayout(widow, fmt, ww) : 0;
        try
        {
            if (wl != 0) HeadingWrap.Refine(wl, widow, ww);
            check("HeadingWrap/short last line is balanced", wl != 0 && Dw.Lines(wl).Length == 2 && Last(wl) >= widow.Length / 4,
                wl == 0 ? "no widow width found" : $"width={ww} lines={Dw.Lines(wl).Length} last={Last(wl)}");
        }
        finally { if (wl != 0) Marshal.Release(wl); }

        // 行首开括号:找出第二行以“（”起头的宽度;精修后该括号的排版宽度约减半(左半字空白压掉)。
        const string bracket = "视觉与交互设计方针：安静的书桌（The Serene Desk）";
        float bw = 0f;
        for (float w = 200f; w < 900f && bw == 0f; w += 2f)
        {
            nint probe = Dw.MkLayout(bracket, fmt, w);
            try { var lines = Dw.Lines(probe); if (lines.Length == 2 && bracket[(int)StartOf(probe, 1)] == '（') bw = w; }
            finally { Marshal.Release(probe); }
        }
        nint bl = bw > 0f ? Dw.MkLayout(bracket, fmt, bw) : 0;
        try
        {
            uint at = bl != 0 ? StartOf(bl, 1) : 0;
            float before = bl != 0 ? Dw.RangeWidth(bl, at, 1) : 0f;
            if (bl != 0) HeadingWrap.Refine(bl, bracket, bw);
            bool stillStarts = bl != 0 && bracket[(int)StartOf(bl, 1)] == '（';
            float after = bl != 0 ? Dw.RangeWidth(bl, StartOf(bl, 1), 1) : 0f;
            check("HeadingWrap/line-start opening bracket is trimmed", stillStarts && after <= before * 0.6f && after >= before * 0.4f,
                bl == 0 ? "no bracket-start width found" : $"width={bw} before={before:0.0} after={after:0.0} starts={stillStarts}");
        }
        finally { if (bl != 0) Marshal.Release(bl); }

        // 对照:首行起头的括号不压(space-first),单行标题不动。
        const string first = "（附录）术语表";
        nint fl = Dw.MkLayout(first, fmt, 600f);
        try
        {
            float before = Dw.RangeWidth(fl, 0, 1);
            HeadingWrap.Refine(fl, first, 600f);
            check("HeadingWrap/first-line bracket untouched", MathF.Abs(Dw.RangeWidth(fl, 0, 1) - before) < 0.01f && Dw.Lines(fl).Length == 1, $"before={before:0.0}");
        }
        finally { Marshal.Release(fl); }
    }
}
