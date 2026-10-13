// HTML 实体解码:命名实体查表(须以 ';' 结尾) + 数字实体 &#169; / &#xA9;。
// 命名表覆盖常用子集,未命中的按字面保留;代码 span 不经过此处(保持字面)。
using System;
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal static class HtmlEntity
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = "\u00A0", ["ensp"] = "\u2002", ["emsp"] = "\u2003", ["thinsp"] = "\u2009",
        ["zwnj"] = "\u200C", ["zwj"] = "\u200D",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["sect"] = "§", ["para"] = "¶",
        ["hellip"] = "…", ["mdash"] = "—", ["ndash"] = "–", ["horbar"] = "―",
        ["lsquo"] = "‘", ["rsquo"] = "’", ["ldquo"] = "“", ["rdquo"] = "”",
        ["sbquo"] = "‚", ["bdquo"] = "„", ["lsaquo"] = "‹", ["rsaquo"] = "›",
        ["laquo"] = "«", ["raquo"] = "»",
        ["oelig"] = "œ", ["OElig"] = "Œ", ["aelig"] = "æ", ["AElig"] = "Æ", ["oslash"] = "ø", ["Oslash"] = "Ø",
        ["bull"] = "•", ["dagger"] = "†", ["Dagger"] = "‡", ["middot"] = "·",
        ["deg"] = "°", ["plusmn"] = "±", ["times"] = "×", ["divide"] = "÷", ["minus"] = "−",
        ["sup1"] = "¹", ["sup2"] = "²", ["sup3"] = "³",
        ["frac12"] = "½", ["frac14"] = "¼", ["frac34"] = "¾", ["permil"] = "‰",
        ["prime"] = "′", ["Prime"] = "″",
        ["larr"] = "←", ["rarr"] = "→", ["uarr"] = "↑", ["darr"] = "↓", ["harr"] = "↔",
        ["lArr"] = "⇐", ["rArr"] = "⇒", ["uArr"] = "⇑", ["dArr"] = "⇓", ["hArr"] = "⇔",
        ["infin"] = "∞", ["ne"] = "≠", ["le"] = "≤", ["ge"] = "≥", ["asymp"] = "≈", ["equiv"] = "≡",
        ["sim"] = "∼", ["cong"] = "≅", ["prop"] = "∝",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["radic"] = "√", ["part"] = "∂",
        ["isin"] = "∈", ["notin"] = "∉", ["ni"] = "∋",
        ["sub"] = "⊂", ["sup"] = "⊃", ["sube"] = "⊆", ["supe"] = "⊇", ["nsub"] = "⊄",
        ["cap"] = "∩", ["cup"] = "∪",
        ["oplus"] = "⊕", ["otimes"] = "⊗", ["perp"] = "⊥", ["ang"] = "∠", ["and"] = "∧", ["or"] = "∨",
        ["not"] = "¬", ["forall"] = "∀", ["exist"] = "∃", ["nexist"] = "∄", ["empty"] = "∅",
        ["nabla"] = "∇", ["there4"] = "∴", ["sdot"] = "⋅",
        ["lceil"] = "⌈", ["rceil"] = "⌉", ["lfloor"] = "⌊", ["rfloor"] = "⌋",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["omicron"] = "ο",
        ["pi"] = "π", ["rho"] = "ρ", ["sigmaf"] = "ς", ["sigma"] = "σ", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["thetasym"] = "ϑ", ["upsih"] = "ϒ", ["piv"] = "ϖ",
        ["Alpha"] = "Α", ["Beta"] = "Β", ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Epsilon"] = "Ε",
        ["Zeta"] = "Ζ", ["Eta"] = "Η", ["Theta"] = "Θ", ["Iota"] = "Ι", ["Kappa"] = "Κ",
        ["Lambda"] = "Λ", ["Mu"] = "Μ", ["Nu"] = "Ν", ["Xi"] = "Ξ", ["Omicron"] = "Ο",
        ["Pi"] = "Π", ["Rho"] = "Ρ", ["Sigma"] = "Σ", ["Tau"] = "Τ", ["Upsilon"] = "Υ",
        ["Phi"] = "Φ", ["Chi"] = "Χ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["euro"] = "€", ["pound"] = "£", ["yen"] = "¥", ["cent"] = "¢", ["curren"] = "¤",
        ["iexcl"] = "¡", ["iquest"] = "¿", ["szlig"] = "ß", ["micro"] = "µ",
        ["agrave"] = "à", ["aacute"] = "á", ["acirc"] = "â", ["atilde"] = "ã", ["auml"] = "ä",
        ["aring"] = "å", ["ccedil"] = "ç",
        ["egrave"] = "è", ["eacute"] = "é", ["ecirc"] = "ê", ["euml"] = "ë",
        ["igrave"] = "ì", ["iacute"] = "í", ["icirc"] = "î", ["iuml"] = "ï",
        ["ntilde"] = "ñ",
        ["ograve"] = "ò", ["oacute"] = "ó", ["ocirc"] = "ô", ["otilde"] = "õ", ["ouml"] = "ö",
        ["ugrave"] = "ù", ["uacute"] = "ú", ["ucirc"] = "û", ["uuml"] = "ü", ["yacute"] = "ý", ["yuml"] = "ÿ",
        ["Agrave"] = "À", ["Aacute"] = "Á", ["Acirc"] = "Â", ["Atilde"] = "Ã", ["Auml"] = "Ä",
        ["Aring"] = "Å", ["Ccedil"] = "Ç",
        ["Egrave"] = "È", ["Eacute"] = "É", ["Ecirc"] = "Ê", ["Euml"] = "Ë",
        ["Igrave"] = "Ì", ["Iacute"] = "Í", ["Icirc"] = "Î", ["Iuml"] = "Ï",
        ["Ntilde"] = "Ñ",
        ["Ograve"] = "Ò", ["Oacute"] = "Ó", ["Ocirc"] = "Ô", ["Otilde"] = "Õ", ["Ouml"] = "Ö",
        ["Ugrave"] = "Ù", ["Uacute"] = "Ú", ["Ucirc"] = "Û", ["Uuml"] = "Ü", ["Yacute"] = "Ý",
        ["Dcaron"] = "\u010E", ["dcaron"] = "\u010F",
        ["HilbertSpace"] = "\u210B", ["DifferentialD"] = "\u2146",
        ["ClockwiseContourIntegral"] = "\u2232", ["ngE"] = "\u2267\u0338",
    };

    // 数字实体 0x80–0x9F 按 Cp1252 解释(规范规定);未映射的返回 null
    private static string Cp1252(int cp) => cp switch
    {
        0x80 => "\u20AC",
        0x82 => "\u201A",
        0x83 => "\u0192",
        0x84 => "\u201E",
        0x85 => "\u2026",
        0x86 => "\u2020",
        0x87 => "\u2021",
        0x88 => "\u02C6",
        0x89 => "\u2030",
        0x8A => "\u0160",
        0x8B => "\u2039",
        0x8C => "\u0152",
        0x8E => "\u017D",
        0x91 => "\u2018",
        0x92 => "\u2019",
        0x93 => "\u201C",
        0x94 => "\u201D",
        0x95 => "\u2022",
        0x96 => "\u2013",
        0x97 => "\u2014",
        0x98 => "\u02DC",
        0x99 => "\u2122",
        0x9A => "\u0161",
        0x9B => "\u203A",
        0x9C => "\u0153",
        0x9E => "\u017E",
        0x9F => "\u0178",
        _ => null,
    };

    // 从 s[i] 起尝试解码一个实体;成功时给出消费长度与结果。
    // 命名实体要求以 ';' 结尾(适配版:CommonMark 允许的部分无分号形式不支持)。
    public static bool TryDecodeAt(string s, int i, out int consumed, out string decoded)
    {
        consumed = 0; decoded = null;
        if (i + 2 >= s.Length || s[i] != '&') return false;
        int n = s.Length;
        if (s[i + 1] == '#')
        {
            int j = i + 2;
            bool hex = j < n && (s[j] == 'x' || s[j] == 'X');
            if (hex) j++;
            int digits = 0; long cp = 0;
            while (j < n)
            {
                char d = s[j];
                int v = d >= '0' && d <= '9' ? d - '0'
                      : hex && d >= 'a' && d <= 'f' ? d - 'a' + 10
                      : hex && d >= 'A' && d <= 'F' ? d - 'A' + 10 : -1;
                if (v < 0) break;
                cp = cp * (hex ? 16L : 10L) + v;
                digits++; j++;
                if (digits > 7) return false;   // 超长按字面处理
            }
            if (digits == 0 || j >= n || s[j] != ';') return false;
            int c = (int)cp;
            if (c == 0 || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) decoded = "\uFFFD";
            else if (c >= 0x80 && c <= 0x9F) decoded = Cp1252(c) ?? "\uFFFD";
            else decoded = char.ConvertFromUtf32(c);
            consumed = j + 1 - i;
            return true;
        }
        int k = i + 1;
        while (k < n && (char.IsAsciiLetter(s[k]) || char.IsAsciiDigit(s[k]))) k++;
        if (k < n && k > i + 1 && s[k] == ';' && Map.TryGetValue(s.Substring(i + 1, k - i - 1), out string val))
        {
            decoded = val; consumed = k + 1 - i;
            return true;
        }
        return false;
    }

    // 整串解码(用于链接文本/URL/图片 alt 等整段提取的场景)
    public static string Decode(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '&' && TryDecodeAt(s, i, out int len, out string dec)) { sb.Append(dec); i += len; }
            else { sb.Append(s[i]); i++; }
        }
        return sb.ToString();
    }
}
