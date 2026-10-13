// 数学公式 Unicode 换算捷径:把常见 LaTeX 记号转成 Unicode 文本(希腊字母/运算符/上下标)。
// 与原生排版共享有界语法;分数为 (a)/(b),不支持的输入整体保留源码。
using System;
using System.Collections.Generic;
using System.Text;

namespace MDViewerNative;

internal static class MathUnicode
{
    private static readonly Dictionary<string, string> Syms = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["phi"] = "φ", ["chi"] = "χ",
        ["psi"] = "ψ", ["omega"] = "ω", ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ",
        ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ",
        ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["cdot"] = "·",
        ["leq"] = "≤", ["geq"] = "≥", ["neq"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["sim"] = "∼", ["propto"] = "∝", ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",
        ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
        ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔", ["to"] = "→", ["mapsto"] = "↦",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆",
        ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅", ["forall"] = "∀", ["exists"] = "∃",
        ["neg"] = "¬", ["land"] = "∧", ["lor"] = "∨", ["oplus"] = "⊕", ["otimes"] = "⊗",
        ["perp"] = "⊥", ["parallel"] = "∥", ["angle"] = "∠", ["degree"] = "°", ["circ"] = "∘",
        ["ldots"] = "…", ["cdots"] = "⋯", ["langle"] = "⟨", ["rangle"] = "⟩",
        ["hbar"] = "ℏ", ["ell"] = "ℓ", ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ",
        ["quad"] = "  ", ["qquad"] = "    ",
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["log"] = "log", ["ln"] = "ln",
        ["exp"] = "exp", ["lim"] = "lim", ["min"] = "min", ["max"] = "max",
        // S75:常用补充(自用走查 d2l 等)
        ["top"] = "⊤", ["bot"] = "⊥", ["intercal"] = "⊺", ["mid"] = "∣", ["le"] = "≤", ["ge"] = "≥", ["ne"] = "≠",
        ["lt"] = "<", ["gt"] = ">", ["prime"] = "′", ["setminus"] = "∖", ["ll"] = "≪", ["gg"] = "≫",
        ["dots"] = "…", ["vdots"] = "⋮", ["ddots"] = "⋱", ["varepsilon"] = "ε", ["varphi"] = "φ", ["vartheta"] = "ϑ",
        ["varrho"] = "ϱ", ["varsigma"] = "ς", ["upsilon"] = "υ", ["Upsilon"] = "Υ", ["omicron"] = "ο",
        ["Vert"] = "‖", ["vert"] = "|", ["lVert"] = "‖", ["rVert"] = "‖", ["lvert"] = "|", ["rvert"] = "|",
        ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉", ["star"] = "⋆", ["ast"] = "∗",
        ["bullet"] = "•", ["cdotp"] = "·", ["colon"] = ":", ["gets"] = "←", ["implies"] = "⟹", ["iff"] = "⟺",
        ["uparrow"] = "↑", ["downarrow"] = "↓", ["longrightarrow"] = "⟶", ["triangleq"] = "≜", ["coloneqq"] = "≔",
        ["simeq"] = "≃", ["cong"] = "≅", ["supseteq"] = "⊇", ["subsetneq"] = "⊊", ["nexists"] = "∄", ["dagger"] = "†",
        ["wedge"] = "∧", ["vee"] = "∨", ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["odot"] = "⊙", ["bmod"] = " mod ",
        ["sup"] = "sup", ["inf"] = "inf", ["det"] = "det", ["arg"] = "arg", ["dim"] = "dim", ["ker"] = "ker",
        ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh", ["cot"] = "cot", ["sec"] = "sec", ["csc"] = "csc",
        ["arcsin"] = "arcsin", ["arccos"] = "arccos", ["arctan"] = "arctan", ["gcd"] = "gcd", ["Pr"] = "Pr",
    };

    private static readonly Dictionary<char, char> SupMap = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ',
        ['a'] = 'ᵃ', ['b'] = 'ᵇ', ['c'] = 'ᶜ', ['d'] = 'ᵈ', ['e'] = 'ᵉ', ['f'] = 'ᶠ', ['g'] = 'ᵍ', ['h'] = 'ʰ',
        ['j'] = 'ʲ', ['k'] = 'ᵏ', ['l'] = 'ˡ', ['m'] = 'ᵐ', ['o'] = 'ᵒ', ['p'] = 'ᵖ', ['r'] = 'ʳ', ['s'] = 'ˢ',
        ['t'] = 'ᵗ', ['u'] = 'ᵘ', ['v'] = 'ᵛ', ['w'] = 'ʷ', ['x'] = 'ˣ', ['y'] = 'ʸ', ['z'] = 'ᶻ',
        ['⊤'] = 'ᵀ', ['T'] = 'ᵀ',   // 转置(S75)
    };

    private static readonly Dictionary<char, char> SubMap = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['k'] = 'ₖ', ['l'] = 'ₗ',
        ['m'] = 'ₘ', ['n'] = 'ₙ', ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ', ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ',
    };

    internal static bool TrySymbol(string name, out string symbol) => Syms.TryGetValue(name, out symbol);

    // 字体命令 → Unicode 数学字母(S75)。映射不到的字符(汉字、标点等)原样保留。
    internal static bool IsAlphabet(string name) => name is "mathbf" or "bf" or "boldsymbol" or "bm" or "mathbb" or "mathcal" or "mathscr" or "mathit" or "mathsf" or "mathtt" or "mathfrak";

    internal static string Alphabet(string name, string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text) sb.Append(StyledLetter(name, c));
        return sb.ToString();
    }

    private static string StyledLetter(string name, char c)
    {
        int upper = c is >= 'A' and <= 'Z' ? c - 'A' : -1, lower = c is >= 'a' and <= 'z' ? c - 'a' : -1, digit = c is >= '0' and <= '9' ? c - '0' : -1;
        int greekUpper = c is >= 'Α' and <= 'Ω' ? c - 'Α' : -1, greekLower = c is >= 'α' and <= 'ω' ? c - 'α' : -1;
        int cp = name switch
        {
            "mathbf" or "bf" => Pick(upper, 0x1D400, lower, 0x1D41A, digit, 0x1D7CE, greekUpper, 0x1D6A8, greekLower, 0x1D6C2),
            "boldsymbol" or "bm" => Pick(upper, 0x1D468, lower, 0x1D482, digit, 0x1D7CE, greekUpper, 0x1D71C, greekLower, 0x1D736),
            "mathit" => c == 'h' ? 0x210E : Pick(upper, 0x1D434, lower, 0x1D44E, -1, 0, greekUpper, 0x1D6E2, greekLower, 0x1D6FC),
            "mathbb" => c switch { 'C' => 0x2102, 'H' => 0x210D, 'N' => 0x2115, 'P' => 0x2119, 'Q' => 0x211A, 'R' => 0x211D, 'Z' => 0x2124, _ => Pick(upper, 0x1D538, lower, 0x1D552, digit, 0x1D7D8, -1, 0, -1, 0) },
            "mathcal" or "mathscr" => c switch
            {
                'B' => 0x212C,
                'E' => 0x2130,
                'F' => 0x2131,
                'H' => 0x210B,
                'I' => 0x2110,
                'L' => 0x2112,
                'M' => 0x2133,
                'R' => 0x211B,
                'e' => 0x212F,
                'g' => 0x210A,
                'o' => 0x2134,
                _ => Pick(upper, 0x1D49C, lower, 0x1D4B6, -1, 0, -1, 0, -1, 0)
            },
            "mathfrak" => c switch { 'C' => 0x212D, 'H' => 0x210C, 'I' => 0x2111, 'R' => 0x211C, 'Z' => 0x2128, _ => Pick(upper, 0x1D504, lower, 0x1D51E, -1, 0, -1, 0, -1, 0) },
            "mathsf" => Pick(upper, 0x1D5A0, lower, 0x1D5BA, digit, 0x1D7E2, -1, 0, -1, 0),
            "mathtt" => Pick(upper, 0x1D670, lower, 0x1D68A, digit, 0x1D7F6, -1, 0, -1, 0),
            _ => -1
        };
        return cp > 0 ? char.ConvertFromUtf32(cp) : c.ToString();
    }

    private static int Pick(int upper, int upperBase, int lower, int lowerBase, int digit, int digitBase, int greekUpper, int greekUpperBase, int greekLower, int greekLowerBase) =>
        upper >= 0 ? upperBase + upper : lower >= 0 ? lowerBase + lower : digit >= 0 && digitBase > 0 ? digitBase + digit
        : greekUpper >= 0 && greekUpperBase > 0 ? greekUpperBase + greekUpper : greekLower >= 0 && greekLowerBase > 0 ? greekLowerBase + greekLower : -1;

    // 重音 → 组合附加符号,逐字附在每个非空白字符(含代理对)之后(S75)。
    internal static bool TryAccent(string name, out char mark)
    {
        mark = name switch
        {
            "hat" or "widehat" => '\u0302',
            "tilde" or "widetilde" => '\u0303',
            "bar" => '\u0304',
            "overline" => '\u0305',
            "breve" => '\u0306',
            "dot" => '\u0307',
            "ddot" => '\u0308',
            "check" => '\u030C',
            "acute" => '\u0301',
            "grave" => '\u0300',
            _ => '\0'
        };
        return mark != '\0';
    }

    internal static string Accent(string text, char mark)
    {
        var sb = new StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { sb.Append(text[++i]); sb.Append(mark); continue; }
            if (!char.IsWhiteSpace(text[i])) sb.Append(mark);
        }
        return sb.ToString();
    }

    // 正文上/下标显示(^x^ ~x~ <sup> 脚注号):只取正文字体自带字形的数字与 + - = ( ),逐字 1:1;
    // 不含字母——修饰字母多落回退字体,粗细高低不齐,宁可保持缩小字号。
    internal static bool TryBodyScript(string text, bool upper, out string shown)
    {
        shown = text;
        if (string.IsNullOrEmpty(text)) return false;
        var map = upper ? SupMap : SubMap;
        var chars = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!(c is >= '0' and <= '9' or '+' or '-' or '=' or '(' or ')') || !map.TryGetValue(c, out chars[i])) return false;
        }
        shown = new string(chars);
        return true;
    }

    public static string Convert(string tex) => Convert(tex, out _);

    // native:线性写法丢了排版信息——上下标含字母(Unicode 修饰字母多落回退字体、粗细高低不齐,同正文上下标规则)
    // 或无 Unicode 形式的字符,或向量箭头——行内改交原生排版器(S77)。
    // 含分式不交:堆叠分式高出统一行高,行内保留 (a)/(b)。
    public static string Convert(string tex, out bool native)
    {
        native = false;
        if (!MathSyntax.TryParse(tex, out var row)) return tex;
        native = Any(row, a => a is MAccent || a is MScript s && !(Scriptable(s.Super, true) && Scriptable(s.Sub, false))) && !Any(row, a => a is MFrac);
        return Format(row);
    }

    private static bool Any(MAtom atom, Func<MAtom, bool> test)
    {
        if (atom == null) return false;
        if (test(atom)) return true;
        foreach (MAtom child in atom.Children) if (Any(child, test)) return true;
        return false;
    }

    private static bool Scriptable(MAtom atom, bool upper)
    {
        if (atom == null) return true;
        var map = upper ? SupMap : SubMap;
        foreach (char c in Format(atom)) if (char.IsLetter(c) || !map.ContainsKey(c)) return false;   // 字母上下标走回退字体,不用
        return true;
    }

    internal static string Format(MAtom atom) => atom switch
    {
        MText text => text.Text,
        MRow row => string.Concat(row.Items.ConvertAll(Format)),
        MFrac fraction => "(" + Format(fraction.Num) + ")/(" + Format(fraction.Den) + ")",
        MSqrt root => Root(root),
        MAccent accent => Accent(Format(accent.Body), '\u20D7'),
        MScript script => Format(script.BaseAtom) + (script.SupFirst
            ? Script(script.Super, true) + Script(script.Sub, false)
            : Script(script.Sub, false) + Script(script.Super, true)),
        _ => ""
    };

    private static string Root(MSqrt root)
    {
        string body = Format(root.Body);
        if (root.Index == null) return "√(" + body + ")";
        string index = Format(root.Index);
        return index switch { "2" => "√(" + body + ")", "3" => "∛(" + body + ")", "4" => "∜(" + body + ")", _ => "root(" + index + "; " + body + ")" };
    }

    private static string Script(MAtom atom, bool upper)
    {
        if (atom == null) return "";
        string text = Format(atom);
        var converted = new StringBuilder();
        var map = upper ? SupMap : SubMap;
        foreach (char c in text)
        {
            // 有字符无 Unicode 上下标:线性写法(UnicodeMath 惯例),单字符 x^β,多字符 e^(iπ);不回退 TeX 花括号(S74)
            if (!map.TryGetValue(c, out char value)) return (upper ? "^" : "_") + (text.Length == 1 || char.IsSurrogatePair(text, 0) && text.Length == 2 ? text : "(" + text + ")");
            converted.Append(value);
        }
        return converted.ToString();
    }
}
