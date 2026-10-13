// 代码语法着色:lite-xl 式顺序模式表(首匹配优先)+ 每语言关键字符号表。
// kind: 0=plain 1=keyword 2=string 3=comment 4=number。只读渲染,无需增量解析。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MDViewerNative;

internal sealed class CodeSyntax
{
    public string Name;
    public (Regex re, int kind)[] Patterns;   // 顺序尝试,要求命中位置==扫描位置
    public HashSet<string> Symbols;           // 标识符命中此表 → keyword
}

internal static class CodeHighlight
{
    public const int KPlain = 0, KKeyword = 1, KString = 2, KComment = 3, KNumber = 4;

    private static readonly Regex ReIdent = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex ReNum = new("\\b(?:0[xX][0-9a-fA-F]+|\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?[fFlLuU]*)", RegexOptions.Compiled);
    private static readonly Regex ReStrDq = new("\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.Compiled);
    private static readonly Regex ReStrSq = new("'(?:[^'\\\\\\n]|\\\\.)*'", RegexOptions.Compiled);
    private static readonly Regex ReStrTq = new("\"\"\"[\\s\\S]*?\"\"\"", RegexOptions.Compiled);
    private static readonly Regex ReLineC = new("//[^\\n]*", RegexOptions.Compiled);
    private static readonly Regex ReLineHash = new("#[^\\n]*", RegexOptions.Compiled);
    private static readonly Regex ReLineDash = new("--[^\\n]*", RegexOptions.Compiled);
    private static readonly Regex ReBlockC = new("/\\*[\\s\\S]*?\\*/", RegexOptions.Compiled);
    private static readonly Regex ReXmlTag = new("</?[A-Za-z][\\w:.-]*|/?>", RegexOptions.Compiled);
    private static readonly Regex RePyTri = new("('''[\\s\\S]*?'''|\"\"\"[\\s\\S]*?\"\"\")", RegexOptions.Compiled);
    private static readonly Regex ReXmlComment = new("<!--[\\s\\S]*?-->", RegexOptions.Compiled);

    private static CodeSyntax Fam(string name, string[] syms, bool hashComment = false, bool dashComment = false, bool sqString = true, bool lineC = true)
    {
        var pats = new List<(Regex, int)>();
        if (hashComment) pats.Add((ReLineHash, KComment));
        if (dashComment) pats.Add((ReLineDash, KComment));
        if (lineC) pats.Add((ReBlockC, KComment));
        if (lineC) pats.Add((ReLineC, KComment));
        if (hashComment) pats.Add((RePyTri, KString));
        pats.Add((ReStrDq, KString));
        if (sqString) pats.Add((ReStrSq, KString));
        pats.Add((ReNum, KNumber));
        return new CodeSyntax { Name = name, Patterns = pats.ToArray(), Symbols = new HashSet<string>(syms) };
    }

    private static readonly string[] KwC = "auto break case char const continue default do double else enum extern float for goto if inline int long register return short signed sizeof static struct switch typedef union unsigned void volatile while".Split(' ');
    private static readonly string[] KwCpp = "class constexpr decltype delete dynamic_cast explicit export friend inline mutable namespace new noexcept nullptr operator private protected public reinterpret_cast static_cast sizeof template this throw true false try catch typedef typeid typename union using virtual void int char double float long short signed unsigned const struct enum while if else for do switch case break continue default return new delete bool".Split(' ');
    private static readonly string[] KwCs = "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while get set value".Split(' ');
    private static readonly string[] KwJava = "abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new null package private protected public return short static strictfp super switch synchronized this throw throws transient try void volatile while true false var".Split(' ');
    private static readonly string[] KwJs = "async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while with yield get set".Split(' ');
    private static readonly string[] KwGo = "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false string int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 uintptr float32 float64 complex64 complex128 bool byte rune make new append len cap copy delete panic recover".Split(' ');
    private static readonly string[] KwRust = "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while String Vec Option Some None Ok Err Result i8 i16 i32 i64 i128 u8 u16 u32 u64 u128 usize isize f32 f64 bool char str".Split(' ');
    private static readonly string[] KwSwift = "associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private protocol public rethrows static struct subscript typealias var break case continue default defer do else fallthrough for guard if in repeat return switch where while as Any catch false is nil super self throw throws true try async await some any".Split(' ');
    private static readonly string[] KwKotlin = "as break class continue do dynamic else false for fun if in interface is null object package return super this throw true try typealias typeof val var when while by get set import private public internal protected override open final abstract sealed data companion init lateinit suspend".Split(' ');
    private static readonly string[] KwPy = "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield match case self".Split(' ');
    private static readonly string[] KwJson = "true false null".Split(' ');
    private static readonly string[] KwSql = "select from where insert into values update set delete create table drop alter index view join left right inner outer on group by order having limit offset as and or not null primary key foreign references distinct union all case when then else end asc desc count sum avg min max".Split(' ');
    private static readonly string[] KwBash = "if then else elif fi for while do done case esac function return export local echo cd source set unset shift readonly in".Split(' ');

    private static readonly Dictionary<string, CodeSyntax> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = Fam("c", KwC),
        ["cpp"] = Fam("cpp", KwCpp),
        ["csharp"] = Fam("csharp", KwCs),
        ["java"] = Fam("java", KwJava),
        ["js"] = Fam("js", KwJs),
        ["go"] = Fam("go", KwGo),
        ["rust"] = Fam("rust", KwRust),
        ["swift"] = Fam("swift", KwSwift),
        ["kotlin"] = Fam("kotlin", KwKotlin),
        ["python"] = Fam("python", KwPy, hashComment: true, lineC: false),
        ["json"] = Fam("json", KwJson, lineC: false),
        ["sql"] = Fam("sql", KwSql, dashComment: true, lineC: false),
        ["bash"] = Fam("bash", KwBash, hashComment: true, lineC: false),
    };

    static CodeHighlight()
    {
        Table["c++"] = Table["cpp"];
        Table["cs"] = Table["csharp"];
        Table["javascript"] = Table["js"];
        Table["typescript"] = Table["js"];
        Table["golang"] = Table["go"];
        Table["rs"] = Table["rust"];
        Table["py"] = Table["python"];
        Table["sh"] = Table["bash"];
        Table["shell"] = Table["bash"];
        Table["xml"] = Fam("xml", Array.Empty<string>(), lineC: false);
        Table["html"] = Table["xml"];
    }

    public static bool Known(string lang) => !string.IsNullOrEmpty(lang) && Table.ContainsKey(lang.Trim());

    // 输出全局偏移 token 序列;不认识的语言返回空表(不着色)
    public static List<(int start, int len, int kind)> Tokenize(string text, string lang)
    {
        var result = new List<(int start, int len, int kind)>();
        if (!Table.TryGetValue((lang ?? "").Trim(), out var syn) || text.Length == 0) return result;
        // xml/html 用标签模式组
        var patterns = syn.Patterns;
        if (ReferenceEquals(syn, Table["xml"]))
        {
            patterns = new[] { (ReXmlComment, KComment), (ReStrDq, KString), (ReXmlTag, KKeyword) };
        }
        int pos = 0;
        while (pos < text.Length)
        {
            int matched = -1, mlen = 0;
            foreach (var (re, kind) in patterns)
            {
                var m = re.Match(text, pos);
                if (m.Success && m.Index == pos && m.Length > 0) { matched = kind; mlen = m.Length; break; }
            }
            if (matched < 0)
            {
                var m = ReIdent.Match(text, pos);
                if (m.Success && m.Index == pos)
                {
                    matched = syn.Symbols.Contains(m.Value) ? KKeyword : KPlain;
                    mlen = m.Length;
                }
            }
            if (matched < 0 || mlen == 0)
            {
                // 单字符 plain,相邻合并
                if (result.Count > 0 && result[^1].kind == KPlain && result[^1].start + result[^1].len == pos)
                    result[^1] = (result[^1].start, result[^1].len + 1, KPlain);
                else
                    result.Add((pos, 1, KPlain));
                pos++;
            }
            else
            {
                result.Add((pos, mlen, matched));
                pos += mlen;
            }
        }
        return result;
    }
}
