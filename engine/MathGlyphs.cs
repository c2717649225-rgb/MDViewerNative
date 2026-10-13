// 数学字母数字(S76,S75 遗留):U+1D400–U+1D7FF 自带字形样式(𝐲 直立粗体、𝑦 斜体),不在正文字体里,由回退字体绘制。
// 1) 行内公式整段是斜体 run,若再斜切,直立粗体变成 𝒚,且组合重音按直立字形定位,帽子/箭头被甩到右上方;
//    TeX 里 \mathbf 本就是直立粗体。故这些字符一律取消斜体。
// 2) 整段(字母 + 其后的组合符号)交给 Cambria Math(Windows 自带):字母与重音同一字体、按字体锚点对位,
//    带重音与不带重音的同一字母字形一致。
namespace MDViewerNative;

internal static unsafe class MathGlyphs
{
    private const string Family = "Cambria Math";

    private static bool IsMark(char c) => c >= '\u0300' && c <= '\u036F' || c >= '\u20D0' && c <= '\u20FF';

    private static bool IsMathAlnum(string text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.ConvertToUtf32(text[i], text[i + 1]) is >= 0x1D400 and <= 0x1D7FF;

    public static void Apply(nint layout, string text)
    {
        if (layout == 0 || string.IsNullOrEmpty(text) || text.IndexOf('\uD835') < 0) return;   // U+1D400–U+1D7FF 的高代理都是 D835
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (!IsMathAlnum(text, i)) continue;
            int end = i + 2;
            while (end < text.Length && (IsMark(text[end]) || IsMathAlnum(text, end))) end += IsMark(text[end]) ? 1 : 2;
            var range = new DWRITE_TEXT_RANGE { first = (uint)i, length = (uint)(end - i) };
            fixed (char* family = Family) Dw.SetFontFamilyName(layout, (nint)family, range);
            Dw.SetFontStyle(layout, 0, range);
            i = end - 1;
        }
    }

    // 原生公式里的数学运算符与箭头(S77 续):正文字体多半没有(⊤ ∇ ⇒ …),回退字体侧边距宽、上标前隔出空隙;统一交给 Cambria Math。
    // 只用于公式排版树的 MText,正文不动。
    public static void ApplySymbols(nint layout, string text)
    {
        if (layout == 0 || string.IsNullOrEmpty(text)) return;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsSymbol(text[i])) continue;
            int end = i + 1;
            while (end < text.Length && IsSymbol(text[end])) end++;
            var range = new DWRITE_TEXT_RANGE { first = (uint)i, length = (uint)(end - i) };
            fixed (char* family = Family) Dw.SetFontFamilyName(layout, (nint)family, range);
            i = end - 1;
        }
    }

    private static bool IsSymbol(char c) => c is >= '\u2190' and <= '\u22FF' or >= '\u27C0' and <= '\u27FF' or >= '\u2980' and <= '\u2AFF';
}
