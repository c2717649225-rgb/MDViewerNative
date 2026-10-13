using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MDViewerNative;

// 中文标点挤压(S75,自用走查周刊:“（第 413 期）：”“…）。”之间空出一个字宽)。用排版度量验证。
internal static class CjkPunctTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        using var init = new DocView();   // 确保字体格式已建
        nint fmt = RenderResources.FmtBody;
        // (文本, 应被压的位置, 不应被压的位置)
        var cases = new (string Text, int[] Squeezed, int[] Kept)[]
        {
            ("（第 413 期）：", new[] { 8 }, new[] { 0, 9 }),
            ("邮箱）。好", new[] { 2 }, new[] { 3 }),
            ("句号。（括号", new[] { 2 }, new[] { 3 }),
            ("好（「好", new[] { 2 }, new[] { 1 }),
            ("好）好。", new int[0], new[] { 1, 3 }),
        };
        foreach (var (text, squeezed, kept) in cases)
        {
            nint plain = Dw.MkLayout(text, fmt, 2000f), tight = Dw.MkLayout(text, fmt, 2000f);
            try
            {
                CjkPunct.Squeeze(tight, text);
                bool ok = true;
                var detail = new List<string>();
                foreach (int i in squeezed.Concat(kept))
                {
                    float a = Dw.RangeWidth(plain, (uint)i, 1), b = Dw.RangeWidth(tight, (uint)i, 1);
                    bool want = squeezed.Contains(i);
                    ok &= want ? b >= a * 0.4f && b <= a * 0.6f : MathF.Abs(a - b) < 0.01f;
                    detail.Add($"{text[i]}:{a:0.0}->{b:0.0}");
                }
                check("CjkPunct/squeeze " + text, ok, string.Join(" ", detail));
            }
            finally { Marshal.Release(plain); Marshal.Release(tight); }
        }
        // 代码 run 内不挤压。
        {
            const string text = "a）。b";
            nint l = Dw.MkLayout(text, fmt, 2000f);
            try
            {
                float a = Dw.RangeWidth(l, 1, 1);
                CjkPunct.Squeeze(l, text, new List<(int, int)> { (0, text.Length) });
                check("CjkPunct/code run untouched", MathF.Abs(Dw.RangeWidth(l, 1, 1) - a) < 0.01f, $"{a:0.0}");
            }
            finally { Marshal.Release(l); }
        }
        // KP 量宽同步扣除:断行宽度与实际排版一致。
        var boxes = KpCjk.TextToItems("好）。好", t => t.Length * 10.0).Where(x => x.K == KpItem.Kind.Box).Select(x => x.B.Width).ToArray();
        check("CjkPunct/KP box widths follow squeeze", boxes.SequenceEqual(new[] { 10.0, 5.0, 10.0, 10.0 }), string.Join(",", boxes));
    }
}
