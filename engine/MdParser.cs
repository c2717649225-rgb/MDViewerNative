// Markdown 解析:行式分块 + 内联样式扫描(够看日常文档,不求全兼容)。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MDViewerNative;

internal static class MdParser
{
    public static List<Block> Parse(string text) => new ParserContext().Parse(text);
}

// One parse owns reference definitions, footnotes and work budgets, including recursive probes.
internal sealed partial class ParserContext
{
    // 引用式链接定义表:[label] → url(当前解析实例私有)
    private Dictionary<string, (string url, string title)> RefLinks = new(StringComparer.OrdinalIgnoreCase);
    private int QuoteRunSeq;   // 引用运行计数(同一次 > 收集 = 同一运行,导出据此分断相邻引用)

    // 脚注定义:[^n] → 全文(Parse 时收集,文末生成脚注节)
    private List<(string id, string text)> Footnotes = new();

    private string NormalizeRef(string s)
    {
        var sb = new StringBuilder();
        bool pendingSpace = false;
        foreach (char c in s.ToLowerInvariant().Replace("ß", "ss"))   // 全大小写折叠:ẞ/ß 与 ss 等价(#542)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) sb.Append(' ');
            sb.Append(c);
            pendingSpace = false;
        }
        return sb.ToString();
    }

    // Reference labels keep escape/entity spelling: normalization is whitespace and case only.
    private static bool ValidRefLabel(string label)
    {
        int count = 0;
        bool escaped = false, content = false;
        foreach (Rune rune in label.EnumerateRunes())
        {
            if (++count > 999) return false;
            if (!Rune.IsWhiteSpace(rune)) content = true;
            if (escaped) { escaped = false; continue; }
            if (rune.Value == 0x5C) { escaped = true; continue; }
            if (rune.Value is '[' or ']') return false;
        }
        return content;
    }

    public List<Block> Parse(string text)
    {
        PrepareSource(text);
        // Charge the whole source up front so the grace window is measured against real
        // document size, not against how many block boundaries happen to be crossed.
        DocumentWork.Poll(text.Length);
        if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text[1..];
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var raw0 = text.Split('\n');
        // Front Matter(26):仅首行 `---`、含 key: 行、200 行内以 `---`/`...` 闭合时切出;其余情况原样交给块解析
        string frontMatter = null;
        int frontLines = FrontMatterLength(raw0);
        if (frontLines > 0)
        {
            frontMatter = string.Join("\n", raw0, 1, frontLines - 2) + "\n";
            raw0 = raw0[frontLines..];
        }
        var raw = new string[raw0.Length];
        for (int li = 0; li < raw0.Length; li++) raw[li] = ExpandLeadingTabs(raw0[li]);   // 仅前导空白内展开;内容 tab 保留(#1)
        // 引用式链接:先收集 [label]: url 定义行(不渲染、跳过围栏内),再把正文里的 [text][ref] 改写成内联形式
        RefLinks = new Dictionary<string, (string url, string title)>(StringComparer.OrdinalIgnoreCase);
        QuoteRunSeq = 0;
        ListSequence = 0;
        ProbeCount = 0;
        Footnotes = new List<(string id, string text)>();
        var kept = new List<string>(raw.Length);
        var sourceLines = new List<int>(raw.Length);
        void Keep(string value, int line) { kept.Add(value); sourceLines.Add(line + frontLines); }
        bool[] fenced = FencedSourceLines(raw);
        var literalLines = new HashSet<int>();
        bool lastWasDef = false;   // 上一行是否被消费为定义/脚注(定义可连续出现)
        for (int ln = 0; ln < raw.Length; ln++)
        {
            string line0 = raw[ln];
            string t = line0.TrimStart(' ');
            if (fenced[ln])
            {
                literalLines.Add(kept.Count);
                Keep(raw0[ln], ln); // Fence payload must not lose its leading tabs.
                lastWasDef = false;
                continue;
            }
            // 引用内的链接定义(#220):`> [foo]: /url` 的定义对全文可见,引用壳留空。
            // 严格限定「单行、无标题、目的地不含空白」这一最有把握的子集:带标题或跨行的
            // 引用内定义仍按正文渲染,宁可少认也不误吞。
            if (t.StartsWith(">"))
            {
                string qinner = StripQuotes(t, int.MaxValue, out int qn);
                string qt = qinner.TrimStart(' ');
                if (qn > 0 && qt.StartsWith("[") && qinner.Length - qt.Length < 4
                    && (ln == 0 || !IsParaLine(raw[ln - 1]) || lastWasDef))
                {
                    int qclose = BalClose(qt, 1);
                    if (qclose > 0 && qclose + 1 < qt.Length && qt[qclose + 1] == ':' && ValidRefLabel(qt[1..qclose]))
                    {
                        string qlabel = NormalizeRef(qt[1..qclose]);
                        string qrest = qt[(qclose + 2)..].Trim();
                        if (!qlabel.StartsWith("^") && qrest.Length > 0
                            && !qrest.Any(char.IsWhiteSpace) && qrest != "<>")
                        {
                            RefLinks.TryAdd(qlabel, (UnescapeDest(ExtractLinkUrl(qrest)), null));
                            // 定义本身不渲染,但引用层级必须保留,否则外壳会整个消失
                            Keep(new string('>', qn), ln);
                            lastWasDef = true;
                            continue;
                        }
                    }
                }
            }
            if (t.StartsWith("[") && line0.Length - t.Length < 4   // 缩进≥4 的是代码块,不是定义(#213)
                && (ln == 0 || !IsParaLine(raw[ln - 1]) || lastWasDef))   // 定义不能打断段落(#215)
            {
                int close = BalClose(t, 1);
                // label 跨行(#210):] 不在本行时向后拼行重试(最多 2 行,不越空行/围栏/新定义);失败整组回退正文
                string tt = t;
                int joinExtra = 0;
                if (close < 0)
                {
                    while (close < 0 && joinExtra < 2 && ln + 1 + joinExtra < raw.Length)
                    {
                        string nj = raw[ln + 1 + joinExtra].TrimStart(' ');
                        if (nj.Length == 0 || nj.StartsWith("```") || nj.StartsWith("~~~")) break;
                        tt += "\n" + nj;
                        joinExtra++;
                        close = BalClose(tt, 1);
                    }
                }
                if (close > 0 && close + 1 < tt.Length && tt[close + 1] == ':' && ValidRefLabel(tt[1..close]))
                {
                    string label = NormalizeRef(tt[1..close]);
                    // 脚注定义 [^n]: 全文保存,不进链接表
                    if (label.StartsWith("^"))
                    {
                        string ftext = tt[(close + 2)..].Trim();
                        if (ftext.Length > 0) Footnotes.Add((label[1..], ftext));
                        ln += joinExtra;   // 跨行 label 的行一并消费,不泄漏进正文
                        lastWasDef = true;
                        continue;
                    }
                    // 多行定义:仅当定义不完整(url 缺失,或 title 引号开了未闭合)才向下一行续(#195/197/200/198)
                    string rest0 = tt[(close + 2)..].Trim();
                    int extra = 0;
                    while (extra < 9 && ln + joinExtra + 1 + extra < raw.Length)
                    {
                        bool needUrl = rest0.Length == 0 || (rest0[0] != '<' && ExtractLinkUrl(rest0).Length == 0 && rest0.Trim(' ', '\t') != "<>");
                        (bool qo, bool qo2) = UnclosedQuotes(rest0);
                        bool needTitle = qo || qo2;
                        if (!needUrl && !needTitle)
                        {
                            // 定义已完整:仅当下一行是纯 title 行(整行一个完整引号串)才并入(#197/213 vs #212)
                            string nx = raw[ln + joinExtra + 1 + extra].Trim();
                            if (nx.Length >= 2 && (nx[0] == '"' || nx[0] == '\'') && nx[^1] == nx[0]
                                && !rest0.Contains('"') && !rest0.Contains('\''))
                            {
                                rest0 += " " + nx;
                                extra++;
                            }
                            break;
                        }
                        string nl = raw[ln + joinExtra + 1 + extra].Trim();
                        if (nl.Length == 0) break;
                        if (nl.StartsWith("[") && BalClose(nl, 1) is int nc && nc > 0 && nc + 1 < nl.Length && nl[nc + 1] == ':') break;
                        rest0 = rest0.Length == 0 ? nl : rest0 + "\n" + nl;   // 换行拼接:title 内换行保留(#198)
                        extra++;
                    }
                    string url = ExtractLinkUrl(rest0);
                    if (label.Length > 0 && url.Length > 0)
                    {
                        string title = ExtractLinkTitle(rest0);
                        // title 未闭合(引号奇数/跨行遇空行) → 定义行退回正文(#199)
                        (bool uq1, bool uq2) = UnclosedQuotes(rest0);
                        if ((uq1 || uq2) && (rest0.Contains('"') || rest0.Contains('\'')))
                        {
                            Keep(line0, ln);
                            for (int je = 0; je < joinExtra; je++) Keep(raw[ln + 1 + je], ln + 1 + je);
                            for (int e2 = 0; e2 < extra; e2++) Keep(raw[ln + joinExtra + 1 + e2], ln + joinExtra + 1 + e2);
                            ln += extra + joinExtra;
                            lastWasDef = false;
                            continue;
                        }
                        // 检查 URL+title 之后是否有非法残留(#203/#211)
                        string tail;
                        if (rest0.Length > 0 && rest0[0] == '<')
                        {
                            int gt = rest0.IndexOf('>', 1);
                            tail = gt > 0 ? rest0[(gt + 1)..].TrimStart(' ', '\t') : "";
                        }
                        else
                        {
                            int sp = rest0.IndexOfAny(new[] { ' ', '\t', '\n' });
                            tail = sp > 0 ? rest0[sp..].TrimStart(' ', '\t') : "";
                        }
                        if (title != null && tail.Length > 0 && tail[0] is '"' or '\'' or '(')
                        {
                            char qc = tail[0] == '(' ? ')' : tail[0];
                            int eq = 1;
                            while (eq < tail.Length && tail[eq] != qc) { if (tail[eq] == '\\' && eq + 1 < tail.Length) eq++; eq++; }
                            tail = eq < tail.Length ? tail[(eq + 1)..].TrimStart(' ', '\t') : "";
                        }
                        if (tail.Trim(' ', '\t', '\n').Length > 0)
                        {   // 残留非法:整组回退正文(含拼行/续行,#210)
                            Keep(line0, ln);
                            for (int je = 0; je < joinExtra; je++) Keep(raw[ln + 1 + je], ln + 1 + je);
                            for (int e2 = 0; e2 < extra; e2++) Keep(raw[ln + joinExtra + 1 + e2], ln + joinExtra + 1 + e2);
                            ln += extra + joinExtra;
                            lastWasDef = false;
                            continue;
                        }
                        RefLinks.TryAdd(label, (url, title == null ? null : UnescapeDest(title)));
                        ln += extra + joinExtra;
                        lastWasDef = true;
                        continue;   // 定义行(含续行)不渲染
                    }
                    if (label.Length > 0 && rest0.Trim() == "<>")
                    {
                        RefLinks.TryAdd(label, ("", null));   // 空目的地定义(#202)
                        ln += extra + joinExtra;
                        lastWasDef = true;
                        continue;
                    }
                }
            }
            Keep(line0.Length - line0.TrimStart(' ').Length >= 4 ? raw0[ln] : line0, ln);
            lastWasDef = false;
        }
        if (RefLinks.Count > 0)
            for (int li = 0; li < kept.Count; li++)
                if (!literalLines.Contains(li)) kept[li] = SubstituteRefLinks(kept[li]);
        var lines = kept.ToArray();
        _sourceLines = lines; _keptSourceLines = sourceLines.ToArray();
        var blocks = new List<Block>();
        int i = 0;
        ParseBlocks(lines, ref i, 0, 0, blocks);
        // 文末脚注节
        if (Footnotes.Count > 0)
        {
            blocks.Add(new HRule());
            blocks.Add(new Heading { Level = 2, Inlines = ParseInlines("脚注") });
            for (int fi = 0; fi < Footnotes.Count; fi++)
            {
                var (id, ftext) = Footnotes[fi];
                blocks.Add(new Para
                {
                    Inlines = new List<Run>
                {
                    new Run("^" + id + ". ", link: "#ref:" + id),
                    new Run(ftext)
                }
                });
            }
        }
        if (frontMatter != null) blocks.Insert(0, new CodeBlock
        {
            Text = frontMatter,
            Lang = "yaml",
            FrontMatter = true,
            Source = new SourceRange(_sourceStarts[0], frontLines < _sourceStarts.Length ? _sourceStarts[frontLines] : _sourceLength, false)
        });
        return blocks;
    }

    // 返回 Front Matter 占用的行数(含两条分隔线);0 = 不是 Front Matter
    internal static int FrontMatterLength(string[] lines)
    {
        if (lines.Length < 3 || lines[0].TrimEnd() != "---") return 0;
        bool keyed = false;
        for (int i = 1; i < lines.Length && i <= 200; i++)
        {
            string t = lines[i].TrimEnd();
            if (t == "---" || t == "...") return keyed ? i + 1 : 0;
            if (!keyed) keyed = LooksLikeYamlKey(t);
        }
        return 0;
    }

    private static bool LooksLikeYamlKey(string line)
    {
        int k = 0;
        while (k < line.Length && (char.IsLetterOrDigit(line[k]) || line[k] == '_' || line[k] == '-')) k++;
        if (k == 0) return false;
        while (k < line.Length && line[k] == ' ') k++;
        return k < line.Length && line[k] == ':';
    }

    // 提示块首行:GitHub `[!NOTE]` 等五种;S75 起兼容 Obsidian——类型大小写不敏感,可带 +/- 折叠标记(不做交互,一律展开)
    // 与自定义标题;类型归并到五种样式。title 为 null 时用类型名(五种之外首字母大写)。
    private static readonly Regex CalloutHead = new(@"^\[!([A-Za-z][\w-]*)\]([+-]?)(?:\s+(.*))?$", RegexOptions.CultureInvariant);
    private static bool TryCallout(string line, out string kind, out string title)
    {
        kind = title = null;
        var m = CalloutHead.Match(line.Trim());
        if (!m.Success) return false;
        string type = m.Groups[1].Value.ToLowerInvariant();
        kind = type switch
        {
            "tip" or "hint" or "success" or "check" or "done" => "tip",
            "important" => "important",
            "warning" or "attention" => "warning",
            "caution" or "danger" or "error" or "failure" or "fail" or "missing" or "bug" => "caution",
            _ => "note"
        };
        string custom = m.Groups[3].Value.Trim();
        title = custom.Length > 0 ? custom : type is "note" or "tip" or "important" or "warning" or "caution" ? AlertTitleOf(type) : char.ToUpperInvariant(type[0]) + type[1..];
        return true;
    }
    private static string AlertTitleOf(string kind) => kind switch
    {
        "note" => "Note",
        "tip" => "Tip",
        "important" => "Important",
        "warning" => "Warning",
        _ => "Caution"
    };

    // [text][ref] / [text][] / 独立 [ref] → 内联 [text](url);跳过代码 span
    private static bool ClosingPunct(char c) => c is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}'
        or '，' or '。' or '；' or '：' or '！' or '？' or '、' or '）' or '】' or '」' or '』' or '》';

    private string SubstituteRefLinks(string line)
    {
        if (!line.Contains('[')) return line;
        var sb = new StringBuilder();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '`')
            {
                int j = line.IndexOf('`', i + 1);
                if (j < 0) { sb.Append(line[i..]); break; }
                sb.Append(line[i..(j + 1)]);
                i = j + 1;
                continue;
            }
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(line[i..(i + 2)]);
                i += 2;
                continue;
            }
            if (c == '!' && i + 1 < line.Length && line[i + 1] == '[')
            {
                sb.Append('!');   // 图片前缀透传,交给下方 [ 分支
                i++;
                continue;
            }
            if (c == '[')
            {
                int close = LinkLabelEnd(line, i);   // 开 [ 计深度
                if (close > i)
                {
                    string first = line[(i + 1)..close].Trim();
                    string labelRaw = line[(i + 1)..close].Trim();   // 改写保留原转义切片,\] 等才能 round-trip(#531)
                    string text = first, refr = first;
                    // [text][ref] 二段式
                    if (close + 1 < line.Length && line[close + 1] == '[')
                    {
                        int close2 = LinkLabelEnd(line, close + 1);   // 二段式开 [ 计深度
                        if (close2 > close + 1)
                        {
                            refr = line[(close + 2)..close2].Trim();
                            text = labelRaw;
                            if (refr.Length == 0) refr = first;   // 折叠式 [text][]:文字即标签
                            if (RefLinks.TryGetValue(NormalizeRef(refr), out var rl))
                            {
                                // 链接文字内的引用式图片同样替换([![A][s]][u] 徽章写法,S74)
                                sb.Append('[').Append(SubstituteRefLinks(text)).Append("](").Append(MkDest(rl.url, rl.title)).Append(')');
                                i = close2 + 1;
                                continue;
                            }
                        }
                    }
                    else if (close + 1 >= line.Length || line[close + 1] != '(')
                    {
                        // [ref] 短式(不吞 [ ] 任务列表等:label 必须能命中定义表);空目的地 [foo]() 为内联链接,让给下方分支(#569)
                        if (RefLinks.TryGetValue(NormalizeRef(first), out var rl2))
                        {
                            sb.Append('[').Append(SubstituteRefLinks(labelRaw)).Append("](").Append(MkDest(rl2.url, rl2.title)).Append(')');
                            i = close + 1;
                            continue;
                        }
                    }
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // indent:本层内容的最小缩进(用于列表/引用内部);depth:递归层数,>100 视为异常嵌套退化为正文,防栈溢出
    private void ParseBlocks(string[] lines, ref int i, int indent, int quoteDepth, List<Block> outBlocks, int depth = 0, int level = 0, bool listOnly = false)
    {
        var para = new StringBuilder();
        int paraStart = -1;
        void FlushPara(int afterLine)
        {
            if (para.Length == 0) return;
            if (para.ToString().Trim().Length == 0) { para.Clear(); paraStart = -1; return; }   // 全空白段落不产出(#97)
            // 段尾换行/行尾空格是块分隔与换行标记,段落末无意义(#618/648/228);行内换行保留(软换行),
            // 行尾反斜杠的硬换行经 TrimEnd 后由 \ 语义承载
            var runs = ParseInlines(para.ToString().TrimEnd());
            if (quoteDepth > 0) outBlocks.Add(new Quote { Inlines = runs, XOff = quoteDepth * 20 });
            else outBlocks.Add(new Para { Inlines = runs });
            if (ReferenceEquals(lines, _sourceLines)) AssignSource(outBlocks[^1], paraStart, afterLine);
            para.Clear(); paraStart = -1;
        }

        while (i < lines.Length)
        {
            DocumentWork.Poll();
            using var sourceScope = new BlockSourceScope(this, lines, outBlocks, i, ref i);
            string raw = ExpandLeadingTabs(lines[i]);   // 引用剥层/子块切片可能重新暴露行首 tab,幂等展开
            string trimmed = raw.TrimStart(' ');

            // A dedented sibling owns the preceding blank separator. Leave it for
            // the parent list so looseness propagates at the correct nesting level.
            if (indent > 0 && string.IsNullOrWhiteSpace(raw))
            {
                int next = i;
                while (next < lines.Length && string.IsNullOrWhiteSpace(lines[next])) next++;
                if (next < lines.Length)
                {
                    string nextLine = ExpandLeadingTabs(lines[next]);
                    if (nextLine.Length - nextLine.TrimStart(' ').Length < indent) { FlushPara(i); return; }
                }
                FlushPara(i);
                i = next; // Consume a retained blank run once, including end-of-document.
                continue;
            }

            // 引用行:收集连续引用行,每次只剥一层 > 前缀后递归。
            // 嵌套层级 = 递归深度,`>>`/`>>>` 顶层直写与 `> > 内嵌` 都由后续递归自然处理。
            // 顶层 4+ 空格缩进的 > 是缩进码不是引用(#233);列表/引用递归层(indent>0)不设限
            if (trimmed.StartsWith(">") && depth < 100 && (raw.Length - trimmed.Length <= 3 || indent > 0))
            {
                FlushPara(i);
                var inner = new StringBuilder();
                int runSeq = ++QuoteRunSeq;   // 同一次收集 = 同一引用运行(导出据此分断相邻引用,#244/254)
                string lastInner = null;
                while (i < lines.Length)
                {
                    string t2 = lines[i].TrimStart(' ');
                    if (t2.StartsWith(">"))
                    {
                        int k = 1;   // 只剥当前层的一个 >,保留更深层前缀给递归
                        if (k < t2.Length && t2[k] == ' ') k++;
                        lastInner = t2[k..];
                        inner.AppendLine(lastInner);
                        i++;
                        continue;
                    }
                    // 懒续行:引用内容以开放段落结尾(末内行非空且探针末块是段落)且本行不能打断段落时并入。
                    // 不带 > 的行(#252 bar)与深度不足的 > 行剥层后的残余(#253 bar、qf 回到外层)同此:规范都并入最深开放段落
                    if (inner.Length > 0 && !string.IsNullOrWhiteSpace(t2) && !string.IsNullOrWhiteSpace(lastInner)
                        && !CanInterruptPara(t2) && InnerEndsOpenPara(inner.ToString(), quoteDepth, depth, level))
                    {
                        lastInner = t2;
                        inner.AppendLine(t2);
                        i++;
                        continue;
                    }
                    break;
                }
                // 递归解析:剥一层后的行当作子文档;子块 XOff 已由 quoteDepth*20 正确累进,勿再加
                var innerLines = inner.ToString().Replace("\r\n", "\n").Split('\n');
                string alert = null;   // 提示块(27;S75 兼容 Obsidian):首行标记不进正文
                List<Run> alertTitle = null;
                if (innerLines.Length > 0 && TryCallout(innerLines[0], out alert, out string titleText))
                {
                    alertTitle = ParseInlines(titleText);
                    foreach (Run tr in alertTitle) tr.B = true;
                }
                if (alert != null) innerLines = innerLines[1..];
                int subI = 0;
                int mark = outBlocks.Count;
                ParseBlocks(innerLines, ref subI, 0, quoteDepth + 1, outBlocks, depth + 1, level);
                if (outBlocks.Count == mark && alert == null)
                    outBlocks.Add(new Quote { Inlines = new List<Run>(), XOff = (quoteDepth + 1) * 20 });   // 仅 > 行的空引用:外壳与内容同层(#241/242)
                if (alert != null)
                    outBlocks.Insert(mark, new Quote { Inlines = alertTitle, XOff = (quoteDepth + 1) * 20, AlertTitle = true });
                for (int bi = mark; bi < outBlocks.Count; bi++)
                {
                    outBlocks[bi].QSeq = runSeq;
                    outBlocks[bi].QuoteDepth = Math.Max(outBlocks[bi].QuoteDepth, quoteDepth + 1);
                    if (alert != null) outBlocks[bi].Alert = alert;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed)) { FlushPara(i); i++; continue; }
            if (trimmed.Length - trimmed.TrimStart(' ').Length < indent && trimmed.StartsWith(" ") && indent > 0) { FlushPara(i); return; }

            // 段落懒续行:段落开放时,4+ 空格行不构成任何块(缩进代码/标题/分隔线均不能打断段落)
            if (para.Length > 0 && raw.Length - trimmed.Length >= 4) { para.Append(trimmed).Append('\n'); i++; continue; }

            // 四空格缩进代码块:仅 indent==0 层(顶层/引用内)处理,列表层不处理以免与续行歧义;
            // para 非空时是段落懒续行,不构成代码块(与 CommonMark 一致)
            if (indent == 0 && para.Length == 0 && raw.Length - trimmed.Length >= 4)
            {
                // List continuations were consumed in the list parser. Remaining indentation
                // starts a sibling code block; do not append it to an already closed item.
                FlushPara(i);
                var cd = new StringBuilder();
                while (i < lines.Length)
                {
                    string cl = ExpandLeadingTabs(lines[i]);   // 缩进按展开后列数计;内容 tab 在前导空白之后,保留
                    if (cl.Trim().Length == 0) { cd.Append(StripIndentColumns(lines[i], 4)).Append('\n'); i++; continue; }
                    int clead = cl.Length - cl.TrimStart(' ').Length;
                    if (clead < 4) break;
                    cd.Append(StripIndentColumns(lines[i], 4)).Append('\n');
                    i++;
                }
                var cbi = new CodeBlock { Text = cd.ToString().TrimEnd('\n') + "\n", Lang = "" };
                if (quoteDepth > 0) cbi.XOff = quoteDepth * 20;
                outBlocks.Add(cbi);
                continue;
            }

            // $$ 数学块:单行 $$..$$ 或多行直到闭合 $$
            if (trimmed.StartsWith("$$"))
            {
                FlushPara(i);
                var math = new StringBuilder();
                string first = trimmed.Length > 4 && trimmed.EndsWith("$$") ? trimmed[2..^2].Trim() : "";
                if (first.Length > 0)
                {
                    math.Append(first).Append('\n');
                    i++;
                }
                else
                {
                    i++;   // 跳过起始 $$
                    while (i < lines.Length)
                    {
                        string ml = lines[i].Trim();
                        if (ml.EndsWith("$$"))
                        {
                            if (ml.Length > 2) math.Append(ml[..^2]).Append('\n');
                            i++;
                            break;
                        }
                        math.Append(ml).Append('\n');
                        i++;
                    }
                }
                var cbm = new CodeBlock { Text = math.ToString(), Lang = "math" };
                if (quoteDepth > 0) cbm.XOff = quoteDepth * 20;
                outBlocks.Add(cbm);
                continue;
            }

            // <details> 折叠块(viewer 扩展,优先于通用 HTML 块):收集到 </details>,summary 单独提
            if (trimmed.StartsWith("<details"))
            {
                FlushPara(i);
                string sumText = "", body = "";
                i++;
                while (i < lines.Length)
                {
                    string ml = lines[i].Trim();
                    i++;
                    if (ml.StartsWith("</details")) break;
                    if (ml.StartsWith("<summary") || ml.StartsWith("</summary") || ml.StartsWith("<p") || ml.StartsWith("</p"))
                    {
                        int a = ml.IndexOf(">", StringComparison.Ordinal), b2 = ml.LastIndexOf("<", StringComparison.Ordinal);
                        if (ml.StartsWith("<summary") && a >= 0 && b2 > a) sumText = ml[(a + 1)..b2].Trim();
                        continue;
                    }
                    body += ml + "\n";
                }
                var dd = new HtmlDetails { Summary = ParseInlines(sumText.Length > 0 ? sumText : "详情"), Body = body.TrimEnd('\n') };
                if (quoteDepth > 0) dd.XOff = quoteDepth * 20;
                outBlocks.Add(dd);
                continue;
            }

            // 原始 HTML 块:规范 4.6 七类起始;1 类到含闭标签行,2-5 类到结束串,6/7 类到空行(不吞)
            if (trimmed.StartsWith("<") && RawHtmlStart(trimmed, out int hkind))
            {
                if (hkind != 7 || para.Length == 0)   // 类型7不能打断段落
                {
                    FlushPara(i);
                    var hb = new StringBuilder();
                    while (i < lines.Length)
                    {
                        if (hkind >= 6 && lines[i].Trim().Length == 0) break;   // 空行止,不吞,留给段落分隔
                        string hl = lines[i].Trim();
                        hb.Append(lines[i]).Append('\n');
                        i++;
                        if (hkind == 1 && ContainsRawEnd(hl)) break;
                        if (hkind == 2 && hl.Contains("-->")) break;
                        if (hkind == 3 && hl.Contains("?>")) break;
                        if (hkind == 5 && hl.Contains("]]>")) break;
                        if (hkind == 4 && hl.Contains('>')) break;
                    }
                    var hbl = new HtmlBlock { Text = hb.ToString() };
                    if (quoteDepth > 0) hbl.XOff = quoteDepth * 20;
                    outBlocks.Add(hbl);
                    continue;
                }
            }

            // 围栏代码块
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                int fl = 0;
                while (fl < trimmed.Length && trimmed[fl] == trimmed[0]) fl++;   // 围栏长度:闭合须等长以上
                string info = trimmed[fl..].Trim();
                if (!(trimmed[0] == '`' && info.Contains('`')))   // 仅反引号围栏禁止 info 含反引号;波浪围栏允许
                {
                    FlushPara(i);
                    string lang = info.Length > 0 ? info.Split(' ', '\t')[0] : "";
                    int fenceLead = raw.Length - trimmed.Length;
                    i++;
                    var code = new StringBuilder();
                    while (i < lines.Length)
                    {
                        string l0 = lines[i];
                        if (i == lines.Length - 1 && l0.Length == 0) { i++; break; }
                        string structural = ExpandLeadingTabs(l0);
                        string lt = structural.TrimStart(' ');
                        // The quote collector already removed container prefixes. A remaining
                        // '>' inside a fence is literal code, not another prefix to strip.
                        int rawIndent = structural.Length - lt.Length;
                        if (rawIndent <= 3 || indent > 0)   // 列表内围栏缩进是相对的,不做绝对门槛
                        {
                            int cl2 = 0;
                            while (cl2 < lt.Length && lt[cl2] == trimmed[0]) cl2++;
                            if (cl2 >= fl && lt[cl2..].Trim().Length == 0) { i++; break; }
                        }
                        // 内容行剥去与围栏对齐的缩进
                        code.Append(StripIndentColumns(l0, fenceLead)).Append('\n');
                        i++;
                    }
                    var cb = new CodeBlock { Text = code.ToString(), Lang = lang };
                    if (quoteDepth > 0) cb.XOff = quoteDepth * 20;
                    outBlocks.Add(cb);
                    continue;
                }
            }

            // 独占一行的图片 ![alt](src)
            if (trimmed.StartsWith("![") && para.Length == 0)
            {
                int close = LinkLabelEnd(trimmed, 1);   // 开 [ 计深度:alt 内嵌套元素配平(#576/577)
                int paren = close > 0 && close + 1 < trimmed.Length ? trimmed.IndexOf('(', close) : -1;
                int end = paren > 0 ? ParenEnd(trimmed, paren) : -1;
                if (close > 0 && paren == close + 1 && end > paren && trimmed[(end + 1)..].Trim() == "")
                {
                    FlushPara(i);
                    string ldest = trimmed[(paren + 1)..end];
                    string itl = ExtractLinkTitle(ldest);
                    var im = new Image
                    {
                        Alt = FlattenText(ParseInlines(trimmed[2..close])),
                        Src = HtmlEntity.Decode(ExtractLinkUrl(ldest)),
                        Title = itl == null ? null : HtmlEntity.Decode(itl),
                    };
                    if (quoteDepth > 0) im.XOff = quoteDepth * 20;
                    outBlocks.Add(im);
                    i++;
                    continue;
                }
            }

            // ATX 标题(井号后接空格或 tab,#10)
            if (trimmed.StartsWith("#") && (trimmed.Length == 1 || trimmed[1] == ' ' || trimmed[1] == '\t' || trimmed[1] == '#'))
            {
                int hl = 0;
                while (hl < trimmed.Length && trimmed[hl] == '#') hl++;
                if (hl <= 6 && hl <= trimmed.Length && (hl >= trimmed.Length || trimmed[hl] == ' ' || trimmed[hl] == '\t'))
                {
                    FlushPara(i);
                    string t = hl < trimmed.Length ? trimmed[hl..].Trim() : "";
                    // 关闭序列:结尾 空格+若干# 才剥,紧贴文字的 # 保留
                    int e2 = t.Length, n2 = 0;
                    while (n2 < e2 && t[e2 - 1 - n2] == '#') n2++;
                    if (n2 > 0 && (e2 - n2 == 0 || t[e2 - n2 - 1] == ' ')) t = t[..(e2 - n2)].TrimEnd();
                    var h = new Heading { Level = Math.Min(hl, 6), Inlines = ParseInlines(t) };
                    if (quoteDepth > 0) h.XOff = quoteDepth * 20;
                    outBlocks.Add(h);
                    i++;
                    continue;
                }
            }

            // Setext 标题:文字行的下一行是 = 或 - 串(CommonMark 核心特性,优先级高于分隔线)
            if (para.Length > 0 && trimmed.Length > 0)
            {
                string trimEnd = trimmed.TrimEnd();
                bool allEq2 = trimEnd.Length > 0, allDash2 = trimEnd.Length > 0;
                foreach (char ch in trimEnd) { if (ch != '=') allEq2 = false; if (ch != '-') allDash2 = false; }
                if (allEq2 || (allDash2 && trimEnd.Length >= 2))
                {
                    string t = para.ToString().TrimEnd('\n').TrimEnd();
                    para.Clear();
                    var h = new Heading { Level = allEq2 ? 1 : 2, Inlines = ParseInlines(t) };
                    if (ReferenceEquals(lines, _sourceLines)) AssignSource(h, paraStart, i + 1);
                    paraStart = -1;
                    if (quoteDepth > 0) h.XOff = quoteDepth * 20;
                    outBlocks.Add(h);
                    i++;
                    continue;
                }
            }

            // 分隔线
            if (IsHr(trimmed))
            {
                FlushPara(i);
                var hr = new HRule();
                if (quoteDepth > 0) hr.XOff = quoteDepth * 20;
                outBlocks.Add(hr);
                i++;
                continue;
            }

            // 表格:当前行含 | 且下一行是分隔行
            if (trimmed.Contains('|') && i + 1 < lines.Length && IsTableSep(lines[i + 1].Trim()))
            {
                FlushPara(i);
                var header = SplitRow(trimmed);
                int sepIdx = i + 1;   // 分隔行位置:必须在跳过表头+分隔行、消费数据行之前记下,否则 i-1 落在最后一个数据行上
                i += 2;
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    rows.Add(SplitRow(lines[i].Trim()));
                    i++;
                }
                // 解析分隔行的对齐标记: :--- = 左(0), :---: = 中(1), ---: = 右(2), --- = 默认左(0)
                var sepParts = SplitRow(lines[sepIdx].Trim());
                var aligns = new int[header.Length];
                for (int ai = 0; ai < header.Length && ai < sepParts.Length; ai++)
                {
                    string sp = sepParts[ai];
                    bool lft = sp.StartsWith(":");
                    bool rgt = sp.EndsWith(":");
                    if (lft && rgt) aligns[ai] = 1;
                    else if (rgt) aligns[ai] = 2;
                    else aligns[ai] = 0;
                }
                var tb = new Table
                {
                    Header = header.Select(h => ParseInlines(h)).ToArray(),
                    Rows = rows.Select(r => r.Select(c => ParseInlines(c)).ToArray()).ToArray(),
                    Aligns = aligns,
                };
                if (quoteDepth > 0) tb.XOff = quoteDepth * 20;
                outBlocks.Add(tb);
                continue;
            }

            // 列表项
            var mi = MatchItem(trimmed);
            if (mi.ok && para.Length > 0 && ((mi.ordered && mi.num != 1) || mi.text.Length == 0))
                mi.ok = false;   // 有序列表打断段落仅限起始号 1(#306);空 marker 项不打断段落(#287/369)
            if (mi.ok)
            {
                // 比当前层内容列更浅的列表项:交回上层处理(嵌套递归以父项内容列为 indent)
                if (raw.Length - trimmed.Length < Math.Max(0, indent)) { FlushPara(i); return; }
                FlushPara(i);
                int lead = raw.Length - trimmed.Length;
                int group = ++ListSequence;
                char delimiter = trimmed[mi.mw - 1];
                if (outBlocks.Count > 0 && outBlocks[^1] is ListItem previous && previous.Nest >= level)
                    previous.GroupBreakAfter = true;
                // 收集同层列表项;每项携带精确内容列(规范 §3.2:marker 宽 + 其后空格,空格 >4 只计 1)
                bool blankSeen = false;
                bool blankSinceItem = false;   // 末项后出现过空行(懒续行只在无空行时生效,#312 vs #315)
                var own = new List<ListItem>();
                int curCc = lead + 2;            // 最近一项的内容列(首项前为占位,仅 own.Count>0 时使用)
                StringBuilder lastRuns = null;   // 末项正文(懒续行并入用)
                ListItem lastItem = null;
                while (i < lines.Length)
                {
                    string raw2 = ExpandLeadingTabs(lines[i]);
                    string t2 = raw2.TrimStart(' ');
                    if (depth < 100 && lastItem != null && outBlocks.Count > 0 && outBlocks[^1] is ListItem lastChild
                        && lastChild.Nest > level && TryParentTail(lines, ref i, lastItem, curCc, quoteDepth, depth, outBlocks, own, out bool tailLoose))
                    { blankSeen |= tailLoose; continue; }
                    if (IsHr(t2))
                    {
                        int hrLead = raw2.Length - t2.Length;
                        if (own.Count > 0 && hrLead >= curCc && hrLead < curCc + 4)
                        {   // 项内容列上的分隔线:并入末项子块(#61 形态);更浅处终止列表(#60)
                            (lastItem.Sub ??= new List<Block>()).Add(new HRule());
                            i++;
                            continue;
                        }
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(t2))
                    {
                        int peek = i;
                        while (peek < lines.Length && string.IsNullOrWhiteSpace(lines[peek])) peek++;
                        if (peek < lines.Length)
                        {
                            string pt = ExpandLeadingTabs(lines[peek]);
                            int plead = pt.Length - pt.TrimStart(' ').Length;
                            var pm = MatchItem(pt.TrimStart(' '));
                            if (pm.ok && plead >= lead)
                            {
                                if (plead < curCc && (pm.ordered != mi.ordered || pt.TrimStart(' ')[pm.mw - 1] != delimiter)) break;
                                blankSeen = true; blankSinceItem = true; i = peek; continue;
                            }
                        }
                        break;
                    }
                    var m2 = MatchItem(t2);
                    if (!m2.ok) break;
                    int lead2 = raw2.Length - t2.Length;
                    if (own.Count > 0 && lead2 >= curCc)
                    {
                        // 更深层:递归成子列表(压缩为 ListItem 平铺,XOff 随真实层深递增)
                        int save = i;
                        var sub = new List<Block>();
                        if (depth < 100) ParseBlocks(lines, ref i, curCc, quoteDepth, sub, depth + 1, level + 1, listOnly: true);
                        if (sub.Count == 0) { i = save; break; }
                        if (outBlocks.Count > 0 && outBlocks[^1] is ListItem priorChild && sub[0] is ListItem nextChild
                            && priorChild.Nest >= nextChild.Nest && priorChild.ListGroup != nextChild.ListGroup)
                            priorChild.GroupBreakAfter = true;
                        outBlocks.AddRange(sub);
                        continue;
                    }
                    if (own.Count > 0)
                    {
                        if (lead2 < lead) break;   // 更浅:交回上层
                        if (lead2 > lead + 3)
                        {   // 非兄弟(超出首项 3 空格)又不及内容列:懒续行并入末项段落,仅限无空行且末项是简单段(#312/- e)
                            if (!blankSinceItem && lastRuns != null && lastItem != null && lastItem.Task == 0 && lastItem.Sub == null)
                            { lastRuns.Append('\n').Append(t2.TrimStart()); lastItem.Inlines = ParseInlines(lastRuns.ToString().TrimEnd()); i++; continue; }
                            break;
                        }
                        // 兄弟项(缩进 ≥ 首项且 ≤ 首项+3,规范 §5.2 #312)落入下方本层分支
                        if (m2.ordered != mi.ordered || t2[m2.mw - 1] != delimiter) break;
                    }
                    blankSinceItem = false;
                    {
                        string marker = m2.ordered ? m2.num + "." : "•";
                        // 精确内容列:marker 宽 + 其后空格;空格 >4 只计 1,首块即项内缩进码(#275/276)
                        int mw = m2.mw;
                        int spacesAfter = 0;
                        while (mw + spacesAfter < t2.Length && t2[mw + spacesAfter] == ' ') spacesAfter++;
                        if (mw + spacesAfter >= t2.Length) spacesAfter = 1;   // 裸/无内容 marker:规范只要求 1 空格(#281)
                        int contentCol = lead2 + mw + (spacesAfter > 4 ? 1 : spacesAfter);
                        bool firstIsCode = lead2 + mw + spacesAfter >= contentCol + 4;
                        if (IsHr(m2.text))
                        {   // 项内容本身是分隔线:发 <li><hr></li>,不收续行(#61)
                            var hli = new ListItem { Marker = marker, Inlines = new List<Run>(), XOff = lead2 / 2 * 20 + (quoteDepth > 0 ? quoteDepth * 20 : 0), Nest = level, Sub = new List<Block> { new HRule() } };
                            hli.ListGroup = group;
                            outBlocks.Add(hli); own.Add(hli);
                            curCc = contentCol;
                            lastRuns = null; lastItem = hli;
                            i++;
                            continue;
                        }
                        var itemRuns = new StringBuilder(firstIsCode ? "" : m2.text + (t2.EndsWith("  ") ? "  " : ""));   // 首行行尾两空格=硬换行,MatchItem 已 Trim(S75)
                        if (!firstIsCode && depth < 100 && StructuredStart(m2.text, lines, i + 1, contentCol))
                        {
                            i++;
                            var parsed = ReadItemBody(m2.text, lines, ref i, contentCol, depth, out bool bodyLoose);
                            var structured = new ListItem
                            {
                                Marker = marker,
                                Inlines = new List<Run>(),
                                Nest = level,
                                XOff = lead2 / 2 * 20 + quoteDepth * 20,
                                QuoteDepth = quoteDepth,
                                ListGroup = group
                            };
                            AppendItemBody(structured, parsed, outBlocks, own, contentCol, quoteDepth);
                            blankSeen |= bodyLoose;
                            curCc = contentCol;
                            lastItem = structured; lastRuns = null;
                            continue;
                        }
                        i++;
                        // 续行:非空、非列表标记、缩进 ≥ 内容列;深缩进(≥内容列+4)/围栏/引用让位给子块收集(#280 项内缩进码)
                        if (!firstIsCode)
                        {
                            while (i < lines.Length)
                            {
                                string t3 = ExpandLeadingTabs(lines[i]);
                                if (string.IsNullOrWhiteSpace(t3)) break;
                                string t3t = t3.TrimStart(' ');
                                if (MatchItem(t3t).ok) break;
                                int lead3 = t3.Length - t3t.Length;   // 本行真实前导空格(t3t 已去前导空格,不能再算一次)
                                if (t3t.StartsWith(">") || t3t.StartsWith("```")) break;
                                if (IsHr(t3t)) break;   // 分隔线终止列表项(#60/61)
                                if (lead3 < contentCol && (itemRuns.Length == 0 || CanInterruptPara(t3t))) break;
                                if (lead3 >= contentCol + 4) break;   // 项内缩进码,子块收集处理
                                if (itemRuns.Length > 0) itemRuns.Append('\n');   // 段落软换行(与顶层段一致)
                                itemRuns.Append(t3t);   // 保留行尾空格:两个以上即硬换行(S75)
                                i++;
                            }
                        }
                        // 项内子块:空行后仍为项缩进(≥ 内容列)且非列表项 → 收集为子块行集(段落/缩进码/引用);
                        // 紧邻(无空行)仅限围栏/引用/分隔线/深缩进码/首块即码起始(续行循环已为此让位,#280)
                        List<string> subLines = null;
                        int subScan = i;
                        while (subScan < lines.Length && string.IsNullOrWhiteSpace(lines[subScan])) subScan++;
                        if (subScan < lines.Length)
                        {
                            string pl = ExpandLeadingTabs(lines[subScan]);
                            string plt = pl.TrimStart(' ');
                            int pllead = pl.Length - plt.Length;
                            bool adjacent = subScan == i;
                            bool subStart = plt.StartsWith("```") || plt.StartsWith(">") || pllead >= contentCol + 4 || IsHr(plt);
                            // 空 marker 项(项首即空行)遇空行即终止,后续内容不再并入(规范"项至多一个空行开始",#282)
                            bool emptyStart = itemRuns.Length == 0 && !adjacent && !firstIsCode;
                            if (pllead >= contentCol && (firstIsCode || !adjacent || subStart) && !emptyStart && !MatchItem(plt).ok)
                            {
                                if (!adjacent) blankSeen = true;
                                subLines = new List<string>();
                                if (firstIsCode) subLines.Add(new string(' ', 4) + t2[Math.Min(contentCol + 4, t2.Length)..]);   // 首块即码:补 4 空格走缩进码语法(#275)
                                int si = subScan;
                                while (si < lines.Length)
                                {
                                    string sl = ExpandLeadingTabs(lines[si]);
                                    if (string.IsNullOrWhiteSpace(sl)) { subLines.Add(""); si++; continue; }
                                    int sllead = sl.Length - sl.TrimStart(' ').Length;
                                    if (sllead < contentCol) break;
                                    if (MatchItem(sl.TrimStart(' ')).ok) break;
                                    subLines.Add(StripIndentColumns(lines[si], contentCol));
                                    si++;
                                }
                                if (subLines.Count > 0 && subLines[^1] == "" && si < lines.Length)
                                {
                                    string nextLine = ExpandLeadingTabs(lines[si]);
                                    string nextText = nextLine.TrimStart(' ');
                                    int nextLead = nextLine.Length - nextText.Length;
                                    var nextItem = MatchItem(nextText);
                                    if (nextItem.ok && nextItem.ordered == m2.ordered && nextText[nextItem.mw - 1] == delimiter && nextLead >= lead && nextLead < contentCol)
                                        blankSeen = true;
                                }
                                i = si;
                            }
                        }
                        var li = new ListItem { Marker = marker, Inlines = ParseInlines(itemRuns.ToString().TrimEnd()), XOff = lead2 / 2 * 20 + (quoteDepth > 0 ? quoteDepth * 20 : 0), Nest = level };
                        if (subLines != null)
                        {
                            while (subLines.Count > 0 && subLines[^1] == "") subLines.RemoveAt(subLines.Count - 1);
                            if (subLines.Count > 0)
                            {
                                int subI = 0;
                                var sub2 = new List<Block>();
                                if (depth < 100) ParseBlocks(subLines.ToArray(), ref subI, 0, 0, sub2, depth + 1);
                                if (sub2.Count > 0) li.Sub = sub2;
                            }
                        }
                        {   // 任务列表检测: [ ] 或 [x] 开头
                            string trimmedRuns = itemRuns.ToString().Trim();
                            if (trimmedRuns.StartsWith("[ ] ")) li.Task = 1;
                            else if (trimmedRuns.StartsWith("[x] ") || trimmedRuns.StartsWith("[X] ")) li.Task = 2;
                            if (li.Task > 0)
                            {
                                int skip = li.Task == 1 ? 4 : 4;
                                li.Inlines = ParseInlines(trimmedRuns[skip..]);
                            }
                        }
                        li.ListGroup = group;
                        outBlocks.Add(li); own.Add(li);
                        curCc = contentCol;
                        lastRuns = firstIsCode ? null : itemRuns; lastItem = li;
                    }
                }
                if (blankSeen) foreach (var li2 in own) li2.Loose = true;
                if (listOnly) return;
                continue;
            }

            // 普通段落行
            if (para.Length == 0) paraStart = i;
            para.Append(trimmed).Append('\n');
            i++;
        }
        FlushPara(i);
    }

    // 引用懒续行判定:探针解析引用内容,末块是开放段落(引用层的 Quote)才允许无 > 行并入(#234/235/249/252)
    private int ProbeCount;   // 探针工作量预算(字符):每次探针都是全量重解析,按次数限仍是平方级(1MB 文件实测 15s)
    private bool InnerEndsOpenPara(string innerText, int quoteDepth, int depth, int level)
    {
        ProbeCount += innerText.Length;
        if (ProbeCount > 4_000_000) return true;
        // Each probe re-parses its inner text, so this is the quadratic path the 1 MB
        // measurement calls out. Charge it before recursing so a pathological document
        // becomes interruptible instead of merely bounded.
        DocumentWork.Poll(innerText.Length);
        var ils = innerText.Split('\n');
        int pi = 0;
        var pb = new List<Block>();
        if (depth < 100) ParseBlocks(ils, ref pi, 0, quoteDepth + 1, pb, depth + 1, level);
        return EndsOpenQuotePara(pb);
    }

    // The trailing block may be a list item that itself ends with an open quote
    // paragraph (#294). Descend so lazy continuation sees the innermost paragraph.
    private static bool EndsOpenQuotePara(List<Block> blocks)
    {
        if (blocks.Count == 0) return false;
        return blocks[^1] switch
        {
            Quote => true,
            ListItem { Sub: { } sub } => EndsOpenQuotePara(sub),
            _ => false
        };
    }

    // 能打断/终止开放段落的块起始(ATX/围栏/分隔线/setext 下划线/缩进≤3 的列表标记);
    // 此类行不能作为懒续行并入引用(#236 ---、#248 ***、#251;HTML 块 1~6 类同理)。缩进码(≥4)与纯文本不能打断,可懒续(#240)
    private bool CanInterruptPara(string line)
    {
        string lt = line.TrimStart(' ');
        if (lt.Length == 0) return false;
        char c0 = lt[0];
        if (c0 == '#' && (lt.Length == 1 || lt[1] == ' ' || lt[1] == '\t')) return true;
        if (lt.StartsWith("```") || lt.StartsWith("~~~")) return true;
        if (IsHr(lt)) return true;
        if (c0 == '=' && lt.All(ch => ch == '=')) return true;   // setext h1 下划线
        if (MatchItem(lt).ok && line.Length - lt.Length <= 3) return true;   // 列表标记可打断段落
        if (c0 == '<' && line.Length - lt.Length <= 3 && RawHtmlStart(lt, out int htmlKind) && htmlKind != 7) return true;   // HTML 块 1~6 类可打断段落(规范 4.6)
        return false;
    }

    // 原始 HTML 块起始判定(规范 4.6 七类):kind 1=pre/script/style/textarea,2=注释,3=PI,4=声明,5=CDATA,6=块名标签,7=孤立完整标签
    private static readonly string[] RawType1 = { "pre", "script", "style", "textarea" };
    private static readonly string[] RawEndTags = { "</pre>", "</script>", "</style>", "</textarea>" };
    private static readonly string[] RawType6 =
        { "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center", "col", "colgroup",
          "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form",
          "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "iframe", "legend",
          "li", "link", "main", "menu", "menuitem", "nav", "noframes", "ol", "optgroup", "option", "p", "param",
          "search", "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul" };

    private bool RawHtmlStart(string t, out int kind)
    {
        kind = 0;
        if (t.StartsWith("<!--")) { kind = 2; return true; }
        if (t.StartsWith("<?")) { kind = 3; return true; }
        if (t.StartsWith("<![CDATA[")) { kind = 5; return true; }
        if (t.StartsWith("<!")) { kind = 4; return t.Length > 2 && char.IsAsciiLetter(t[2]); }
        bool closing = t.StartsWith("</");
        string body = closing ? t[2..] : t[1..];
        int sp = body.IndexOfAny(new[] { ' ', '\t', '>', '/' });
        string name = (sp < 0 ? body : body[..sp]).ToLowerInvariant();
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) return false;
        foreach (char ch in name) if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') return false;
        bool type1 = System.Array.IndexOf(RawType1, name) >= 0;
        if (!closing && type1 && (sp < 0 || body[sp] == ' ' || body[sp] == '\t' || body[sp] == '>')) { kind = 1; return true; }
        if (System.Array.IndexOf(RawType6, name) >= 0) { kind = 6; return true; }
        // 类型7:完整开标签(名字不得是类型1四个)或完整闭标签,单独成行;不能打断段落(调用方保证)。
        // 严格语法(TryParseHtmlTag):非法属性/闭合带属性不算起始(#626)
        if (closing || !type1)
        {
            if (!TryParseHtmlTag(t, 0, out int te)) return false;
            for (int m = te; m < t.Length; m++) if (t[m] != ' ' && t[m] != '\t') return false;
            kind = 7;
            return true;
        }
        return false;
    }

    // 类型7 起始的完整性:引号感知扫到 >,内无 <,> 后仅空白到行尾
    private bool CompleteTagAlone(string t, bool closing)
    {
        char quote = '\0';
        for (int k = closing ? 2 : 1; k < t.Length; k++)
        {
            char c = t[k];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '<') return false;
            if (c == '>')
            {
                for (int m = k + 1; m < t.Length; m++) if (t[m] != ' ' && t[m] != '\t') return false;
                return true;
            }
        }
        return false;
    }

    // 严格 HTML 标签语法解析(块级 type7 起始与行内直通共用,单一语法源):
    // 开标签 <name attr=val ... />,闭标签 </name>(无属性);属性名 [A-Za-z_:][A-Za-z0-9_.:-]*,
    // 值 ".."/'..'(引号内任意字符)或裸序列;引号值后须空白/`>`/`/` 分隔(#624);语法非法整体失败(#621/622/634)
    private bool TryParseHtmlTag(string s, int i, out int end)
    {
        end = 0;
        int n = s.Length;
        if (i >= n || s[i] != '<') return false;
        bool closing = i + 1 < n && s[i + 1] == '/';
        int k = i + (closing ? 2 : 1);
        if (k >= n || !char.IsAsciiLetter(s[k])) return false;
        int k0 = k;
        while (k < n && (char.IsAsciiLetterOrDigit(s[k]) || s[k] == '-')) k++;
        if (k == k0) return false;
        string name = s[k0..k];
        if (closing)
        {
            while (k < n && (s[k] == ' ' || s[k] == '\t' || s[k] == '\n')) k++;
            if (k < n && s[k] == '>') { end = k + 1; return true; }
            return false;
        }
        while (k < n)
        {
            int ws = 0;
            while (k < n && (s[k] == ' ' || s[k] == '\t' || s[k] == '\n')) { k++; ws++; }
            if (k >= n) return false;
            if (s[k] == '>') { end = k + 1; return true; }
            if (s[k] == '/')
            {
                if (k + 1 < n && s[k + 1] == '>') { end = k + 2; return true; }
                return false;
            }
            if (ws == 0) return false;   // 属性必须由空白引导:<m:abc> 的 :abc 不算(#611)
            if (s[k] == '=') return false;   // 无名字的属性非法(名字后的 = 由名字段处理)
            if (!(char.IsAsciiLetter(s[k]) || s[k] == '_' || s[k] == ':')) return false;
            k++;
            while (k < n && (char.IsAsciiLetterOrDigit(s[k]) || s[k] is '_' or ':' or '.' or '-')) k++;
            // 值引导:名字后可直接 =,或经空白后 =(#618 attr = value)
            int ke = k;
            while (ke < n && (s[ke] == ' ' || s[ke] == '\t' || s[ke] == '\n')) ke++;
            if (ke < n && s[ke] == '=')
            {
                k = ke + 1;
                while (k < n && (s[k] == ' ' || s[k] == '\t' || s[k] == '\n')) k++;
                if (k >= n) return false;
                char q = s[k];
                if (q == '"' || q == '\'')
                {
                    k++;
                    while (k < n && s[k] != q) k++;
                    if (k >= n) return false;
                    k++;
                    if (k < n && s[k] != ' ' && s[k] != '\t' && s[k] != '\n' && s[k] != '>' && s[k] != '/') return false;
                }
                else
                {
                    int v0 = k;
                    while (k < n && s[k] != ' ' && s[k] != '\t' && s[k] != '\n' && s[k] != '"' && s[k] != '\'' && s[k] != '<' && s[k] != '>' && s[k] != '=') k++;
                    if (k == v0) return false;
                }
            }
        }
        return false;
    }

    // 类型1 结束:行内含四个闭标签之一(大小写不敏感,无需与起始匹配)
    private bool ContainsRawEnd(string line)
    {
        foreach (string e in RawEndTags)
            if (line.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // 行首前导空白内的 tab 按 4 列停靠展开为空格;内容 tab 保留(码块保真,Tabs 节 #1-#3)
    // Remove only structural columns; a tab wholly inside code stays a tab.
    private static string StripIndentColumns(string line, int columns)
    {
        int index = 0, column = 0;
        while (index < line.Length && column < columns && line[index] is ' ' or '\t')
        {
            int width = line[index] == '\t' ? 4 - column % 4 : 1;
            index++;
            if (column + width > columns)
            {
                // Splitting a tab shifts the relative tab-stop origin. Expand the
                // remaining leading whitespace in the original column system.
                int endColumn = column + width;
                while (index < line.Length && line[index] is ' ' or '\t')
                {
                    endColumn += line[index] == '\t' ? 4 - endColumn % 4 : 1;
                    index++;
                }
                return new string(' ', endColumn - columns) + line[index..];
            }
            column += width;
        }
        return line[index..];
    }

    private string ExpandLeadingTabs(string line)
    {
        int k = 0;
        bool hasTab = false;
        while (k < line.Length && (line[k] == ' ' || line[k] == '\t'))
        {
            if (line[k] == '\t') hasTab = true;
            k++;
        }
        if (!hasTab) return line;
        var sb = new StringBuilder(line.Length + 8);
        int col = 0;
        for (int m = 0; m < k; m++)
        {
            if (line[m] == '\t') { int add = 4 - col % 4; sb.Append(' ', add); col += add; }
            else { sb.Append(' '); col++; }
        }
        sb.Append(line[k..]);
        return sb.ToString();
    }

    private bool IsHr(string s)
    {
        if (s.Length < 3) return false;
        char c = s[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (char ch in s) if (ch != c && ch != ' ' && ch != '\t') return false;   // tab 与空格等价(#11)
        return s.Trim().Length >= 3;
    }

    private bool IsTableSep(string s)
    {
        if (!s.Contains('-') || !s.Contains('|')) return false;
        foreach (char ch in s) if (ch != '-' && ch != ':' && ch != '|' && ch != ' ') return false;
        return true;
    }

    private string[] SplitRow(string row)
    {
        row = row.Trim();
        if (row.StartsWith("|")) row = row[1..];
        if (row.EndsWith("|")) row = row[..^1];
        var parts = row.Split('|');
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim().Replace("\\|", "|");
        return parts;
    }

    // 有序 marker 校验(数字+./) +1):公共语法源,带内容与裸 marker 两路共用
    private bool IsOrderedMark(string mark, out int n)
    {
        n = 0;
        if (mark.Length < 2 || mark.Length > 10 || !char.IsDigit(mark[0])) return false;
        char last = mark[^1];
        if (last != '.' && last != ')') return false;
        foreach (char c in mark[..^1]) if (!char.IsDigit(c)) return false;
        return int.TryParse(mark[..^1], out n);   // 超大序号退化为普通段落,防 OverflowException
    }

    private (bool ok, bool ordered, int num, string text, int mw) MatchItem(string s)
    {
        if (s.Length == 0) return (false, false, 0, null, 0);
        int sp = s.IndexOf(' ');
        if (sp < 0)
        {   // 裸 marker:整行仅 marker,构成空列表项(#282/283/285/317)
            if (s is "-" or "*" or "+") return (true, false, 0, "", s.Length);
            if (IsOrderedMark(s, out int n0)) return (true, true, n0, "", s.Length);
            return (false, false, 0, null, 0);
        }
        if (sp < 1) return (false, false, 0, null, 0);
        string mark = s[..sp];
        if (mark is "-" or "*" or "+") return (true, false, 0, s[(sp + 1)..].Trim(), mark.Length);
        if (IsOrderedMark(mark, out int n)) return (true, true, n, s[(sp + 1)..].Trim(), mark.Length);
        return (false, false, 0, null, 0);
    }

    // 深度配平的 ] 查找:label 内的 [x] 嵌套不计为 close(#514/519)
    private int BalClose(string s, int start)
    {
        int d = 0;
        for (int k = start; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length) { k++; continue; }
            if (s[k] == '[') d++;
            else if (s[k] == ']')
            {
                if (d == 0) return k;
                d--;
            }
        }
        return -1;
    }

    // 链接/图片 label 的配平 ] 查找:start 指向开 [ 本身(d 计深度,首个使栈空(减到 0)的 ] 即 close,
    // 与 BalClose 同语义),并在扫描中跳过 code span(#344:span 内的 ] 不当闭合)与
    // HTML 标签属性(#526:标签内的 ] 不当闭合)。嵌套元素 [..](..) 由其开 [ 计深度天然配平(#519/522/533)。
    // 未闭合 code span / 未闭标签按普通字符继续。
    private int LinkLabelEnd(string s, int start)
    {
        int d = 0;
        for (int k = start; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length) { k++; continue; }
            if (s[k] == '`')
            {
                int n = 1;
                while (k + n < s.Length && s[k + n] == '`') n++;
                int j = k + n;
                bool closed = false;
                while (j < s.Length)
                {
                    j = s.IndexOf('`', j);
                    if (j < 0) break;
                    int m = 1;
                    while (j + m < s.Length && s[j + m] == '`') m++;
                    if (m == n) { k = j + m - 1; closed = true; break; }
                    j += m;
                }
                // 未配对:整个开栅跳过(最大 run 内的 ` 不能再单独成栅,逐字重扫是 O(n²))
                if (!closed) k += n - 1;
                continue;
            }
            if (s[k] == '<')
            {
                int gt = s.IndexOf('>', k + 1);
                if (gt > k) k = gt;   // 标签整体跳过,属性内 ] 不参与配平(#526)
                continue;
            }
            if (s[k] == '[') { d++; continue; }
            if (s[k] == ']')
            {
                d--;
                if (d == 0) return k;
            }
        }
        return -1;
    }

    // 上一行是否为段落延续行(非任何块起始):链接定义不能打断段落(#215)
    private bool IsParaLine(string s)
    {
        string t = s.TrimStart(' ');
        if (t.Length == 0) return false;
        if (t.StartsWith("#") || t.StartsWith(">") || t.StartsWith("```") || t.StartsWith("~~~")
            || t.StartsWith("|") || t.StartsWith("$$") || t.StartsWith("<")) return false;
        if (IsHr(t) || MatchItem(t).ok) return false;
        if (t.StartsWith("[") && BalClose(t, 1) is int c0 && c0 > 0 && c0 + 1 < t.Length && t[c0 + 1] == ':') return false;   // 连续定义行
        return true;
    }

    // 引号奇偶(转义引号 \" 不计入):title 闭合判定用(#204)
    private (bool dq, bool sq) UnclosedQuotes(string s)
    {
        int a = 0, b = 0;
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length) { k++; continue; }
            if (s[k] == '"') a++; else if (s[k] == '\'') b++;
        }
        return (a % 2 == 1, b % 2 == 1);
    }

    // 目的地之后的标题:"..." / '...' / (...) 形式;须完整闭合且其后无残留;起始分隔只认 ASCII 空白
    private string ExtractLinkTitle(string dest)
    {
        string d = dest.Trim();
        int p = 0;
        if (d.Length > 1 && d[0] == '<')
        {
            int gt = d.IndexOf('>', 1);
            if (gt < 0) return null;
            p = gt + 1;
        }
        else
        {
            while (p < d.Length && d[p] != ' ' && d[p] != '\t' && d[p] != '\n') p++;
        }
        // NBSP 等非 ASCII 空白不属于分隔(算 URL 的一部分);分隔空白可跨行(#512)
        string rest = d[p..];
        if (rest.Length >= 2 && (rest[0] == ' ' || rest[0] == '\t' || rest[0] == '\n'))
        {
            rest = rest.TrimStart(' ', '\t', '\n');
            if (rest.Length >= 2)
            {
                char q = rest[0];
                if (q == '"' || q == '\'' || q == '(')
                {
                    char qc = q == '(' ? ')' : q;
                    int e = 1;
                    while (e < rest.Length)
                    {
                        if (rest[e] == '\\' && e + 1 < rest.Length) { e += 2; continue; }
                        if (rest[e] == qc) break;
                        e++;
                    }
                    // 引号闭合且其后仅剩 ASCII 空白才算 title;title 内首尾空白保留(#198 期望含首尾换行)
                    if (e < rest.Length && rest[(e + 1)..].Trim(' ', '\t', '\n').Length == 0)
                        return rest[1..e];
                }
            }
        }
        return null;
    }

    // 引用改写用的目的地重组:含标题/含空格走尖括号形式保证完整
    private string MkDest(string url, string title)
    {
        if (title == null) return url.Contains(' ') ? "<" + url + ">" : url;
        // 无损转义:先 \ 后 ",行内 UnescapeDest 逆变换还原(#204 含 \" 的标题)
        return "<" + url + "> \"" + title.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // 内联 runs 拍平为纯文本(用于 alt 等场景)
    internal string FlattenText(List<Run> runs)
    {
        var sb = new StringBuilder();
        foreach (var r in runs) sb.Append(r.Text);
        return sb.ToString();
    }

    // 链接目的地括号的引号感知闭合查找:转义括号跳过,"ti)tle" 内的 ')' 不闭合
    private int ParenEnd(string s, int paren)
    {
        bool dq = false, sq = false;
        int depth = 0;
        for (int j = paren + 1; j < s.Length; j++)
        {
            char ch = s[j];
            if (ch == '\\' && j + 1 < s.Length) { j++; continue; }
            if (ch == '"' && !sq) dq = !dq;
            else if (ch == '\'' && !dq) sq = !sq;
            else if (ch == '(' && !dq && !sq) depth++;
            else if (ch == ')' && !dq && !sq)
            {
                if (depth == 0) return j;
                depth--;
            }
        }
        return -1;
    }

    // 目的地内反斜杠转义解码(\X → X,仅 ASCII 标点)
    private bool HasAsciiWs(string s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length) { k++; continue; }
            if (s[k] == ' ' || s[k] == '\t' || s[k] == '\n') return true;
        }
        return false;
    }

    private string UnescapeDest(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length && s[k + 1] > ' ' && s[k + 1] < '\u007F'
                && "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".IndexOf(s[k + 1]) >= 0)
            { sb.Append(s[k + 1]); k++; }
            else sb.Append(s[k]);
        }
        return sb.ToString();
    }

    // 链接目的地提取:<url> 尖括号形式取内部;否则截到第一个空白(剥标题/尺寸参数)
    internal string ExtractLinkUrl(string raw)
    {
        string u = raw.Trim();
        if (u.Length >= 2 && u[0] == '<')
        {
            int close = u.IndexOf('>', 1);
            if (close > 0) return u[1..close];
        }
        int sp = u.IndexOfAny(new[] { ' ', '\t', '\n' });
        if (sp > 0) u = u[..sp];
        return u;
    }

    // ---------- 内联解析 ----------

    // 深度上限:嵌套链接/标签每层递归一次,超限按字面文本返回(StackOverflowException 不可捕获,进程必死)
    internal const int InlineMaxDepth = 64;

    // 工作量预算:嵌套链接被拒后内层 [ 逐个重扫,成本随嵌套层数相乘(实测 20 层 >500ms)。
    // 按实际扫描字符计费,跨递归共享,超限后 [/![ 一律按字面;正常段落总扫描 ≈ 正文自身长度,10 万字符余量充足。
    private const int InlineWorkCap = 100_000;
    private int _inlineWork;

    internal List<Run> ParseInlines(string s, bool noLinks = false, int depth = 0)
    {
        if (depth > InlineMaxDepth) return new List<Run> { new Run(s) };
        if (depth == 0) _inlineWork = 0;
        var runs = new List<Run>();
        var cur = new StringBuilder();
        void Flush()
        {
            if (cur.Length == 0) return;
            // 纯文本里的裸链接拆分
            EmitText(cur.ToString(), runs, false, false, false, null);
            cur.Clear();
        }

        int i = 0;
        // 段内闭合标签索引:惰性建立(首个白名单开标签出现时扫描一次),供 TryInlineTag 二分定位。
        // 与逐次 IndexOf 语义等价:同为"ge+1 起首个该名闭合"(OrdinalIgnoreCase)。
        Dictionary<string, List<int>> closeIdx = null;
        bool closeIdxBuilt = false;
        Dictionary<string, List<int>> CloseIndex()
        {
            if (!closeIdxBuilt) { closeIdx = BuildCloseIndex(s); closeIdxBuilt = true; }
            return closeIdx;
        }
        int CloseTagAt(string name, int from)
        {
            var idx = CloseIndex();
            return idx != null && idx.TryGetValue(name, out var list) ? LowerBound(list, from) : -1;
        }
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char nx = s[i + 1];
                if (nx == '\n') { if (i + 2 < s.Length) { cur.Append(LineBreaks.Hard); i += 2; continue; } cur.Append(c); i++; continue; }   // 行尾反斜杠=硬换行(段末为字面)
                // 仅 ASCII 标点可转义(规范),其余反斜杠按字面
                if (nx > ' ' && nx < '\u007F' && "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".IndexOf(nx) >= 0)
                {
                    // * / _ 解码后加哨兵,防 ApplyEmphasis 误配(#439/454);&#42; 实体同
                    cur.Append(nx == '*' || nx == '_' ? "\u0000" + nx.ToString() : nx.ToString());
                    i += 2; continue;
                }
                cur.Append(c); i++; continue;
            }
            if (c == '&' && HtmlEntity.TryDecodeAt(s, i, out int entLen, out string entDec))
            {
                // 实体解码出 */_ 单字符时加哨兵,防 ApplyEmphasis 误配(#37)
                cur.Append(entDec is "*" or "_" ? "\u0000" + entDec : entDec);
                i += entLen; continue;
            }
            if (c == '`')
            {
                // 多柵反引号:开闭柵数量相等才配对(``code with ` tick``)
                int n = 1;
                while (i + n < s.Length && s[i + n] == '`') n++;
                int j = i + n;
                bool matched = false;
                while (!matched)
                {
                    j = s.IndexOf('`', j);
                    if (j < 0) break;
                    int m = 1;
                    while (j + m < s.Length && s[j + m] == '`') m++;
                    if (m == n)
                    {
                        Flush();
                        string body = s[(i + n)..j].Replace("\n", " ");
                        if (body.Length >= 2 && body[0] == ' ' && body[^1] == ' ' && body.Trim().Length > 0) body = body[1..^1];
                        runs.Add(new Run(body, code: true));
                        i = j + n;
                        matched = true;
                    }
                    else
                    {
                        j += m;
                    }
                }
                if (matched) continue;
                cur.Append(s.Substring(i, n));   // 无配对:按字面
                i += n;
                continue;
            }
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                int j = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (j > i)
                {
                    Flush();
                    string inner = s[(i + 2)..j];
                    foreach (var r in ParseInlines(inner, noLinks, depth + 1))
                    {
                        r.S = true;
                        runs.Add(r);
                    }
                    i = j + 2; continue;
                }
            }
            // * / _ 强调:移交 ApplyEmphasis 分隔符栈(文件尾部)统一处理,此处仅按字面收集
            // Obsidian 双链 [[…]] / ![[…]](S75);已定义的引用式链接已由 SubstituteRefLinks 先行替换,内含 ] 不再匹配
            if (!noLinks && (c == '[' || c == '!') && WikiLinks.TryParse(s, i, out int wikiEnd, out Run wiki))
            {
                Flush();
                runs.Add(wiki);
                i = wikiEnd;
                continue;
            }
            if (c == '!' && i + 1 < s.Length && s[i + 1] == '[')
            {
                if (_inlineWork > InlineWorkCap) { cur.Append("!["); i += 2; continue; }
                int close = LinkLabelEnd(s, i + 1);   // ![ 的开 [ 计深度:首个归零 ] 即 close(#576/519/522)
                _inlineWork += (close >= 0 ? close : s.Length) - i;
                int paren = close >= 0 && close + 1 < s.Length ? s.IndexOf('(', close) : -1;
                if (close > 0 && paren == close + 1)
                {
                    int end; string ldest;
                    if (paren + 1 < s.Length && s[paren + 1] == '<')
                    {
                        int gt = -1;
                        for (int k = paren + 2; k < s.Length && s[k] != '\n'; k++)
                        {
                            if (s[k] == '\\') { k++; continue; }
                            if (s[k] == '>') { gt = k; break; }
                        }
                        end = -1; ldest = null;
                        if (gt > 0)
                        {
                            int q = gt + 1;
                            while (q < s.Length && s[q] == ' ') q++;
                            if (q < s.Length && (s[q] == ')' || s[q] == '"' || s[q] == '\'' || s[q] == '('))
                            {
                                int pe = ParenEnd(s, gt);
                                if (pe > gt) { end = pe; ldest = s[(paren + 1)..pe]; }
                            }
                        }
                    }
                    else
                    {
                        end = ParenEnd(s, paren);
                        ldest = end > paren ? s[(paren + 1)..end] : null;
                    }
                    if (end > paren)
                    {
                        Flush();
                        string isrc = HtmlEntity.Decode(ExtractLinkUrl(ldest));
                        string ititle = ExtractLinkTitle(ldest);
                        if (ititle != null) ititle = HtmlEntity.Decode(ititle);
                        // 行内图片:Img run 携带源与 alt 文本(渲染同链接形;导出 <img>,不参与外层链接挂载)
                        runs.Add(new Run(FlattenText(ParseInlines(s[(i + 2)..close], false, depth + 1)), link: isrc, title: ititle, img: true));
                        i = end + 1; continue;
                    }
                }
            }
            if (!noLinks && c == '[')
            {
                if (_inlineWork > InlineWorkCap) { cur.Append(c); i++; continue; }
                // LinkLabelEnd 跳 code span/HTML 标签/内层元素:code span 在链接内优先(#344),label 内 span 不当闭合(#518/527)
                int close = LinkLabelEnd(s, i);   // 开 [ 计深度,首个归零 ] 即 close(#519/522/533)
                _inlineWork += (close >= 0 ? close : s.Length) - i;
                int paren = close >= 0 && close + 1 < s.Length ? s.IndexOf('(', close) : -1;
                if (close > 0 && paren == close + 1)
                {
                    int end; string ldest;
                    if (paren + 1 < s.Length && s[paren + 1] == '<')
                    {
                        // 尖括号目的地:<url> 后必须紧跟 ')' 或标题,否则整体按普通文本
                        int gt = -1;
                        for (int k = paren + 2; k < s.Length && s[k] != '\n'; k++)
                        {
                            if (s[k] == '\\') { k++; continue; }
                            if (s[k] == '>') { gt = k; break; }
                        }
                        end = -1; ldest = null;
                        if (gt > 0)
                        {
                            int q = gt + 1;
                            while (q < s.Length && s[q] == ' ') q++;
                            if (q < s.Length && (s[q] == ')' || s[q] == '"' || s[q] == '\'' || s[q] == '('))
                            {
                                int pe = ParenEnd(s, gt);
                                if (pe > gt) { end = pe; ldest = s[(paren + 1)..pe]; }
                            }
                        }
                    }
                    else
                    {
                        end = ParenEnd(s, paren);
                        ldest = end > paren ? s[(paren + 1)..end] : null;
                    }
                    if (end > paren)
                    {
                        // 裸目的地:首个未转义空白后必须是 title 起始(" ' ( ),否则整体字面(#490/492);空白跨行允许(#512)
                        bool bad = false;
                        string bare = ldest.Trim();
                        if (bare.Length >= 1 && bare[0] != '<')
                        {
                            int k = 0;
                            while (k < bare.Length)
                            {
                                if (bare[k] == '\\' && k + 1 < bare.Length) { k += 2; continue; }
                                if (bare[k] == ' ' || bare[k] == '\t' || bare[k] == '\n') break;
                                k++;
                            }
                            if (k < bare.Length)
                            {
                                string after = bare[k..].TrimStart(' ', '\t', '\n');
                                if (after.Length == 0 || (after[0] != '"' && after[0] != '\'' && after[0] != '(')) bad = true;
                            }
                        }
                        else
                        {
                            // 尖括号目的地内部换行非法(#493);其后的 title 部分可跨行(#198)
                            int gt2 = bare.IndexOf('>');
                            if ((gt2 >= 0 ? bare[..gt2] : bare).Contains('\n')) bad = true;
                        }
                        string ltitle = bad ? null : ExtractLinkTitle(ldest);
                        if (!bad && ltitle == null && (bare.Length >= 1 && bare[0] != '<') && HasAsciiWs(ldest))
                        {
                            // 空白后非合法 title(引号未闭合/有残留) → 非法目的地
                            bad = true;
                        }
                        if (!bad)
                        {
                            Flush();
                            string lurl = HtmlEntity.Decode(UnescapeDest(ExtractLinkUrl(ldest)));
                            if (ltitle != null) ltitle = HtmlEntity.Decode(UnescapeDest(ltitle));
                            // 链接文本做内联解析(嵌套样式)
                            var sub = ParseInlines(s[(i + 1)..close], false, depth + 1);
                            if (sub.Count == 0) sub.Add(new Run(""));   // 空文本链接也产出 run
                            // 链接内禁止再嵌套链接:label 中出现链接 run(图片除外)则本链接整体失败落字面(#520);
                            // 失败后外层 [ 按普通字符,内层链接由主循环另行成立
                            bool nested = false;
                            foreach (var r2 in sub)
                                if (r2.Link != null && !r2.Img && !r2.Bare) { nested = true; break; }   // 裸网址不算嵌套链接(GFM:[url](url) 是一个链接)
                            if (nested)
                            {
                                cur.Append(c);
                                i++;
                                continue;
                            }
                            // 扁平挂载:文本/样式 run 挂链接(渲染/选区零改动),图片 run 挂外层链接(#519)
                            foreach (var r in sub)
                            {
                                if (r.Img) { if (r.LinkOuter == null) r.LinkOuter = lurl; if (r.LinkTitle == null) r.LinkTitle = ltitle; }
                                else if (r.Link == null || r.Bare) { r.Link = lurl; r.LinkTitle = ltitle; r.Bare = false; }
                            }
                            runs.AddRange(sub);
                            i = end + 1; continue;
                        }
                    }
                }
                // 脚注引用 [^n] → 小字上标 + #fn 链接。S90:只有存在定义时才成链接(GFM:未定义按字面);标签按定义同样折叠大小写
                if (close > i + 1 && s[i + 1] == '^')
                {
                    string fid = s[(i + 2)..close], fkey = fid.Length > 0 ? NormalizeRef("^" + fid)[1..] : "";
                    if (fkey.Length > 0 && Footnotes.Exists(f => f.id == fkey))
                    {
                        Flush();
                        runs.Add(new Run("^" + fid, link: "#fn:" + fkey, fn: true));
                        i = close + 1; continue;
                    }
                }
            }
            // ==高亮==
            if (c == '=' && i + 1 < s.Length && s[i + 1] == '=')
            {
                int j = s.IndexOf("==", i + 2, StringComparison.Ordinal);
                if (j > i + 2)
                {
                    Flush();
                    foreach (var r in ParseInlines(s[(i + 2)..j], noLinks, depth + 1))
                    {
                        r.Mark = true;
                        runs.Add(r);
                    }
                    i = j + 2; continue;
                }
            }
            // 上标 ^x^
            if (c == '^')
            {
                int j = s.IndexOf('^', i + 1);
                if (j > i + 1)
                {
                    Flush();
                    foreach (var r in ParseInlines(s[(i + 1)..j], noLinks, depth + 1))
                    {
                        r.SupSub = 1;
                        runs.Add(r);
                    }
                    i = j + 1; continue;
                }
            }
            // 下标 ~x~(双波浪删除线已在前处理)
            if (c == '~' && (i + 1 >= s.Length || s[i + 1] != '~'))
            {
                int j = s.IndexOf('~', i + 1);
                if (j > i + 1)
                {
                    Flush();
                    foreach (var r in ParseInlines(s[(i + 1)..j], noLinks, depth + 1))
                    {
                        r.SupSub = 2;
                        runs.Add(r);
                    }
                    i = j + 1; continue;
                }
            }
            // 行内数学 $..$
            if (c == '$' && i + 2 < s.Length)
            {
                int j = s.IndexOf('$', i + 1);
                if (j > i + 1 && !char.IsWhiteSpace(s[i + 1]) && !char.IsWhiteSpace(s[j - 1]) && !s[(i + 1)..j].Contains('\n'))
                {
                    Flush();
                    // 斜体公式后紧跟收尾标点:补 1/6 em 空格(U+2006)作斜体校正(上标等末字形右倾压住标点,S74;U+200A 约 1.3 px 不足)
                    string tex = s[(i + 1)..j];
                    string shownMath = MathUnicode.Convert(tex, out bool native);
                    // 原生公式(S77)下标直立、盒宽即墨迹宽,不需要斜体校正
                    if (!native && j + 1 < s.Length && ClosingPunct(s[j + 1])) shownMath += "\u2006";
                    runs.Add(new Run(shownMath, i: true) { MathTex = tex, MathNative = native });
                    i = j + 1; continue;
                }
            }
            // <br> 硬换行
            if (c == '<')
            {
                string up = s.Substring(i, Math.Min(7, s.Length - i)).ToUpperInvariant();
                if (up.StartsWith("<BR>") || up.StartsWith("<BR/>") || up.StartsWith("<BR "))
                {
                    int gt = IndexOfGt(s, i, 64);   // 限窗:病态行里每个 < 都全串找 > 是 O(n²)
                    if (gt > i)
                    {
                        Flush();
                        runs.Add(new Run(LineBreaks.Hard.ToString()));
                        i = gt + 1; continue;
                    }
                }
            }
            // emoji 短代码 :name:
            if (c == ':')
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '+' || s[j] == '-')) j++;
                if (j > i + 1 && j < s.Length && s[j] == ':' && EmojiMap.Map.TryGetValue(s[(i + 1)..j], out var em))
                {
                    cur.Append(em);
                    i = j + 1; continue;
                }
            }
            if (c == '<' && i + 4 < s.Length && (MatchesAt(s, "http://", i) || MatchesAt(s, "https://", i)))
            {
                int j = IndexOfGt(s, i, 512);   // autolink 限窗(同上)
                if (j > i && s[(i + 1)..j].IndexOfAny(new[] { ' ', '\n', '<' }) < 0) { Flush(); runs.Add(new Run(s[(i + 1)..j], link: s[(i + 1)..j])); i = j + 1; continue; }
            }
            // <scheme:...> 任意协议自动链接 + <email> mailto(规范形式);失败整体字面(不经裸URL拆分,#610)
            if (c == '<')
            {
                int gt2 = IndexOfGt(s, i + 1, 512);   // 限窗(同上)
                if (gt2 > i + 1)
                {
                    string inner2 = s[(i + 1)..gt2];
                    // 仅当尖括号内整体是 URL 形(开头即 scheme://)才可能走字面整体;HTML 标签交由后续分支
                    int cix = inner2.IndexOf(':');
                    bool urlShaped = cix > 1 && cix < inner2.Length - 2 && inner2[(cix + 1)..].StartsWith("//")
                                     && char.IsAsciiLetter(inner2[0]);
                    if (!inner2.Contains(' ') && inner2.Length >= 3)
                    {
                        int colon = inner2.IndexOf(':');
                        bool scheme = colon >= 2 && char.IsAsciiLetter(inner2[0]);
                        for (int k = 0; k < colon && scheme; k++)
                            if (!(char.IsAsciiLetterOrDigit(inner2[k]) || inner2[k] is '.' or '+' or '-')) scheme = false;
                        if (scheme)
                        {
                            Flush();
                            runs.Add(new Run(inner2, link: inner2));
                            i = gt2 + 1; continue;
                        }
                        int at = inner2.IndexOf('@');
                        if (at > 0 && at < inner2.Length - 1 && ValidEmail(inner2))
                        {
                            Flush();
                            runs.Add(new Run(inner2, link: "mailto:" + inner2));
                            i = gt2 + 1; continue;
                        }
                        if (urlShaped)
                        {
                            // 前缀必须是纯 scheme 字符(防 <a href="https://..."> 被误判)
                            bool pure = true;
                            for (int k = 0; k < cix && pure; k++)
                                if (!(char.IsAsciiLetterOrDigit(inner2[k]) || inner2[k] is '.' or '+' or '-')) pure = false;
                            if (pure)
                            {
                                Flush();
                                runs.Add(new Run(s[i..(gt2 + 1)]));   // 失败的 autolink 整体字面
                                i = gt2 + 1; continue;
                            }
                        }
                    }
                    else
                    {
                        string it2 = inner2.Trim();
                        int cix2 = it2.IndexOf(':');
                        bool urlShaped2 = cix2 > 1 && cix2 < it2.Length - 2 && it2[(cix2 + 1)..].StartsWith("//")
                                          && char.IsAsciiLetter(it2[0]);
                        if (urlShaped2 && !it2.Contains('<'))
                        {
                            bool pure2 = true;
                            for (int k = 0; k < cix2 && pure2; k++)
                                if (!(char.IsAsciiLetterOrDigit(it2[k]) || it2[k] is '.' or '+' or '-')) pure2 = false;
                            if (pure2)
                            {
                                Flush();
                                runs.Add(new Run(s[i..(gt2 + 1)]));   // 含空白的 URL 形整体字面(#610)
                                i = gt2 + 1; continue;
                            }
                        }
                    }
                }
            }
            // 白名单内联标签 <b> <i> <u> <mark> <sub> <sup> <a href> 等 → 原生样式(优先于 raw 直通);
            // 带属性(非 a)的标签让位 raw 直通:白名单只剥无属性样式标签(#170 跨行属性直通)
            if (c == '<' && TryInlineTag(s, i, CloseTagAt, out int tagEnd, out var tagSub, depth))
            {
                Flush();
                runs.AddRange(tagSub);
                i = tagEnd;
                continue;
            }
            // 行内原始 HTML 直通:语法合法的标签原样保留(脚本类黑名单拦截),不参与强调
            if (c == '<' && TryRawHtmlInline(s, i, out int rawEnd, out string rawText))
            {
                Flush();
                runs.Add(new Run(rawText) { Opaque = true });
                i = rawEnd;
                continue;
            }
            cur.Append(c);
            i++;
        }
        Flush();
        ApplyEmphasis(runs);
        if (depth == 0) LineBreaks.Resolve(runs);   // 软/硬换行定形(S75)
        return runs;
    }

    // ---------- 内联 HTML 白名单 ----------

    // <email> 自动链接的地址校验(规范简化版:local@域名多段,末段≥2字母)
    private bool ValidEmail(string s)
    {
        int at = s.IndexOf('@');
        string loc = s[..at], dom = s[(at + 1)..];
        if (loc.Length == 0 || dom.Length == 0) return false;
        foreach (char ch in loc) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-')) return false;
        var labels = dom.Split('.');
        if (labels.Length < 2) return false;
        foreach (var lb in labels)
        {
            if (lb.Length == 0) return false;
            foreach (char ch in lb) if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') return false;
        }
        if (labels[^1].Length < 2) return false;
        foreach (char ch in labels[^1]) if (!char.IsAsciiLetter(ch)) return false;
        return true;
    }

    private bool TagKnown(string n)
    {
        switch (n.ToLowerInvariant())
        {
            case "b":
            case "strong":
            case "i":
            case "em":
            case "cite":
            case "var":
            case "del":
            case "s":
            case "strike":
            case "mark":
            case "sub":
            case "sup":
            case "u":
            case "ins":
            case "kbd":
            case "samp":
            case "a":
            case "span":
            case "font":
            case "small":
            case "big":
            case "abbr":
            case "time":
            case "ruby":
            case "rt":
            case "rp":
                return true;
            default: return false;
        }
    }

    // 白名单标签 → 样式位:1=B 2=I 4=S 8=Mark 16=Sub 32=Sup 64=U 128=Code;0=透传(只剥标签)
    private int InlineTagStyle(string n)
    {
        switch (n.ToLowerInvariant())
        {
            case "b": case "strong": return 1;
            case "i": case "em": case "cite": case "var": return 2;
            case "del": case "s": case "strike": return 4;
            case "mark": return 8;
            case "sub": return 16;
            case "sup": return 32;
            case "u": case "ins": return 64;
            case "kbd": case "samp": return 128;
            default: return 0;
        }
    }

    private string HtmlAttr(string tag, string name)
    {
        int p = tag.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        while (p > 0 && (char.IsAsciiLetterOrDigit(tag[p - 1]) || tag[p - 1] == '-' || tag[p - 1] == ':'))
        {
            // 前边界:防止 data-href= 里的 href= 被误取(p>0 时才可能撞前缀;命中则跳过继续找)
            p = tag.IndexOf(name + "=", p + 1, StringComparison.OrdinalIgnoreCase);
        }
        if (p < 0) return null;
        p += name.Length + 1;
        if (p >= tag.Length) return null;
        char q = tag[p];
        if (q == '"' || q == '\'')
        {
            int e = tag.IndexOf(q, p + 1);
            return e > p ? tag[(p + 1)..e] : null;
        }
        int e2 = p;
        while (e2 < tag.Length && !char.IsWhiteSpace(tag[e2])) e2++;
        return tag[p..e2];
    }

    // 升序列表中 ≥ from 的最小元素(无则 -1)
    private int LowerBound(List<int> list, int from)
    {
        int lo = 0, hi = list.Count - 1, res = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid] >= from) { res = list[mid]; hi = mid - 1; }
            else lo = mid + 1;
        }
        return res;
    }

    // 段内闭合标签索引:一次线性扫描收集 "</name" 位置(名字→升序位置表)。
    // 惰性建立:只有真正出现白名单开标签时才扫描(普通文档零开销)。
    private Dictionary<string, List<int>> BuildCloseIndex(string s)
    {
        Dictionary<string, List<int>> idx = null;
        int p = 0;
        while ((p = s.IndexOf("</", p, StringComparison.Ordinal)) >= 0)
        {
            int q = p + 2;
            while (q < s.Length && (char.IsAsciiLetterOrDigit(s[q]) || s[q] == '-')) q++;
            if (q > p + 2)
            {
                idx ??= new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                string nm = s.Substring(p + 2, q - p - 2).ToLowerInvariant();
                if (!idx.TryGetValue(nm, out var list)) { list = new List<int>(); idx[nm] = list; }
                list.Add(p);
            }
            p = q > p ? q : p + 2;
        }
        return idx;
    }

    // 白名单内联标签解析:成功时给出闭合位置与内容 runs(未挂接),失败返回 false(按普通文本处理)
    // closeAt(name, from):段内 from 起首个 "</name" 位置(无则 -1),由调用方的索引器提供
    private bool TryInlineTag(string s, int i, Func<string, int, int> closeAt, out int end, out List<Run> sub, int depth = 0)
    {
        end = 0; sub = null;
        int ge = IndexOfGt(s, i + 1, 512);   // 限窗本身(后置的长度判断挡不住全串扫描的 O(n²))
        if (ge < 0 || ge - i > 512) return false;
        string tag = s[(i + 1)..ge];
        string tn = tag;
        int tsp = tn.IndexOfAny(new[] { ' ', '\t', '\n', '/' });
        if (tsp > 0) tn = tn[..tsp];
        if (tn.Length == 0 || !char.IsAsciiLetter(tn[0]) || !TagKnown(tn)) return false;
        // 非 a 标签带属性(白名单只支持裸样式标签):让位 raw 直通,属性不丢(#170 等)
        if (tn != "a" && tag.IndexOfAny(new[] { ' ', '\t', '\n' }) >= 0) return false;
        int ce = closeAt(tn.ToLowerInvariant(), ge + 1);
        if (ce < 0) return false;   // 无闭合标签:按普通文本处理
        int ceEnd = s.IndexOf('>', ce);
        if (ceEnd < 0) return false;
        sub = ParseInlines(s[(ge + 1)..ce], false, depth + 1);
        if (tn == "a")
        {
            string href = HtmlAttr(tag, "href");
            if (href != null)
            {
                href = HtmlEntity.Decode(href.Trim());
                foreach (var r in sub) if (r.Link == null) r.Link = href;
            }
        }
        else
        {
            int st = InlineTagStyle(tn);
            foreach (var r in sub)
            {
                if ((st & 1) != 0) r.B = true;
                if ((st & 2) != 0) r.I = true;
                if ((st & 4) != 0) r.S = true;
                if ((st & 8) != 0) r.Mark = true;
                if ((st & 16) != 0) r.SupSub = 2;   // 下标
                if ((st & 32) != 0) r.SupSub = 1;   // 上标
                if ((st & 64) != 0) r.U = true;
                if ((st & 128) != 0) r.Code = true;
            }
        }
        end = ceEnd + 1;
        return true;
    }

    // ---------- 强调分隔符栈(CommonMark 附录 A 适配版) ----------

    private static Rune EmRune(string text, int index)
    {
        if (index < 0 || index >= text.Length) return new Rune(' ');
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1])) index--;
        return Rune.TryGetRuneAt(text, index, out var rune) ? rune : Rune.ReplacementChar;
    }

    private static bool EmPunct(Rune c) => Rune.IsPunctuation(c) || Rune.IsSymbol(c);

    // 对已生成的 runs 做一遍 * / _ 分隔符匹配:成对区间合并进 B/I,定界字符删除。
    // 代码 span 与链接 run 视为不透明,不参与匹配(区间跨过时也不染色)。
    internal void ApplyEmphasis(List<Run> runs)
    {
        if (runs.Count == 0) return;
        // 1) 拼接全文;不透明 run 用单个对象占位符
        var full = new StringBuilder();
        var runStart = new int[runs.Count];
        bool hasSentinel = false;
        for (int r = 0; r < runs.Count; r++)
        {
            runStart[r] = full.Length;
            // 公式(S77)同为原子片段:线性文本里的 * _ 不参与配对
            if (runs[r].Code || runs[r].Link != null || runs[r].Opaque || runs[r].MathTex != null) full.Append("\uFFFC");
            else { full.Append(runs[r].Text); if (runs[r].Text.Contains('\u0000')) hasSentinel = true; }
        }
        string s = full.ToString();
        if (s.IndexOf('*') < 0 && s.IndexOf('_') < 0)
        {
            if (hasSentinel) StripSentinel(runs);
            return;
        }

        // 2) 收集分隔符段(连续同字符)与侧翼信息;哨兵后的 */_ 是转义产物,跳过
        var ds = new List<(int abs, int len, char ch, bool open, bool close)>();
        for (int p = 0; p < s.Length; p++)
        {
            if (s[p] != '*' && s[p] != '_') continue;
            if (p > 0 && s[p - 1] == '\u0000') { continue; }   // 哨兵保护的定界字符:字面
            int q = p;
            while (q < s.Length && s[q] == s[p]) q++;
            Rune before = EmRune(s, p - 1);
            Rune after = EmRune(s, q);
            bool beforeWs = Rune.IsWhiteSpace(before);
            bool afterWs = Rune.IsWhiteSpace(after);
            bool lF = !afterWs && (!EmPunct(after) || beforeWs || EmPunct(before));
            bool rF = !beforeWs && (!EmPunct(before) || afterWs || EmPunct(after));
            bool open = lF && (s[p] == '*' || !rF || EmPunct(before));
            bool close = rF && (s[p] == '*' || !lF || EmPunct(after));
            if (s[p] == '_' && Rune.IsLetterOrDigit(before) && Rune.IsLetterOrDigit(after)) open = close = false;
            if (open || close) ds.Add((p, q - p, s[p], open, close));
            p = q - 1;
        }

        // 3) 匹配:闭合符从左到右,向左找最近的同字符未耗尽开启符;两侧均≥2 取粗体,否则斜体
        var styles = new List<(int a, int b, bool em, bool strong)>();
        var del = new bool[s.Length];
        for (int ci = 0; ci < ds.Count; ci++)
        {
            if (!ds[ci].close || ds[ci].len <= 0) continue;
            int oi = -1;
            for (int k = ci - 1; k >= 0; k--)
            {
                if (!ds[k].open || ds[k].ch != ds[ci].ch || ds[k].len <= 0) continue;
                // CommonMark mod-3 规则:任一侧可开可合时,长度和不得为 3 的倍数(除非两侧各自都是)
                bool oBoth = ds[k].open && ds[k].close;
                bool cBoth = ds[ci].open && ds[ci].close;
                if ((oBoth || cBoth) && (ds[k].len + ds[ci].len) % 3 == 0 && !(ds[k].len % 3 == 0 && ds[ci].len % 3 == 0)) continue;
                oi = k; break;
            }
            if (oi < 0) continue;
            // 规范:open 匹配成功后,栈中介于其上的定界符全部作废(#471/472)
            for (int k2 = oi + 1; k2 < ci; k2++)
            {
                var dk = ds[k2];
                dk.len = 0;
                ds[k2] = dk;
            }
            var o = ds[oi]; var cc = ds[ci];
            int use = o.len >= 2 && cc.len >= 2 ? 2 : 1;
            styles.Add((o.abs + use, cc.abs, use == 1, use == 2));
            for (int k = 0; k < use; k++) { del[o.abs + k] = true; del[cc.abs + k] = true; }
            o.abs += use; o.len -= use;
            cc.abs += use; cc.len -= use;
            ds[oi] = o; ds[ci] = cc;
            if (cc.len > 0) ci--;   // 剩余闭合符继续配对(***x*** 的斜体层)
        }
        if (styles.Count == 0)
        {
            if (hasSentinel) StripSentinel(runs);
            return;
        }

        // 4) 重建:逐字符跳过定界符与哨兵,按样式区间并集切分普通文本 run
        var result = new List<Run>();
        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Code || run.Link != null || run.Opaque) { result.Add(run); continue; }
            if (run.MathTex != null)   // 公式原样保留,继承外层粗体(本身已斜体)
            {
                bool emM = false, stM = false;
                EmStyleAt(styles, runStart[r], ref emM, ref stM);
                run.B |= stM;
                result.Add(run);
                continue;
            }
            int rs = runStart[r];
            int p2 = 0;
            while (p2 < run.Text.Length)
            {
                if (del[rs + p2]) { p2++; continue; }
                if (run.Text[p2] == '\u0000') { p2++; continue; }   // 哨兵剥除
                bool em0 = false, st0 = false;
                EmStyleAt(styles, rs + p2, ref em0, ref st0);
                var seg = new StringBuilder();
                while (p2 < run.Text.Length && !del[rs + p2] && run.Text[p2] != '\u0000')
                {
                    bool em1 = false, st1 = false;
                    EmStyleAt(styles, rs + p2, ref em1, ref st1);
                    if (em1 != em0 || st1 != st0) break;
                    seg.Append(run.Text[p2]);
                    p2++;
                }
                if (seg.Length > 0)
                    result.Add(new Run(seg.ToString(), run.B || st0, run.I || em0, run.S, run.Code, run.Link, run.LinkTitle, run.Mark, run.SupSub, run.Fn, run.U));
            }
        }
        runs.Clear();
        runs.AddRange(result);
    }

    // 转义/实体产生的定界字符哨兵剥除(无匹配路径)
    private void StripSentinel(List<Run> runs)
    {
        for (int r = 0; r < runs.Count; r++)
            if (!runs[r].Code && runs[r].Link == null && !runs[r].Opaque && runs[r].MathTex == null && runs[r].Text.Contains('\u0000'))
                runs[r] = new Run(runs[r].Text.Replace("\u0000", ""), runs[r].B, runs[r].I, runs[r].S, runs[r].Code, runs[r].Link, runs[r].LinkTitle, runs[r].Mark, runs[r].SupSub, runs[r].Fn, runs[r].U);
    }

    private void EmStyleAt(List<(int a, int b, bool em, bool strong)> styles, int pos, ref bool em, ref bool strong)
    {
        foreach (var (a, b, e, t) in styles)
            if (pos >= a && pos < b) { if (e) em = true; if (t) strong = true; }
    }

    // 行内原始 HTML:<tag> / </tag> / <tag ... /> / <!-- --> / <?..?> / <!..> 语法合法即直通
    // 限窗找 '>':count 钳制到串尾(IndexOf(start,count) 要求 start+count<=len);from 可等于 s.Length
    private int IndexOfGt(string s, int from, int cap)
        => from >= s.Length ? -1 : s.IndexOf('>', from, Math.Min(cap, s.Length - from));

    // s[pos..] 是否以 lit 开头(Ordinal,零分配)
    private bool MatchesAt(string s, string lit, int pos)
    {
        if (pos + lit.Length > s.Length) return false;
        for (int k = 0; k < lit.Length; k++) if (s[pos + k] != lit[k]) return false;
        return true;
    }

    private bool TryRawHtmlInline(string s, int i, out int end, out string text)
    {
        end = 0; text = null;
        int gt = IndexOfGt(s, i + 1, 512);   // 限窗本身(后置的长度判断挡不住全串扫描的 O(n²))
        if (gt < 0 || gt - i > 512) return false;
        string t = s[i..(gt + 1)];
        string name;
        if (t.StartsWith("<!--"))
        {
            int ce = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
            if (ce < 0) return false;
            end = ce + 3; text = s[i..end];
            return true;
        }
        else if (t.StartsWith("<![CDATA["))
        {
            int ce = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
            if (ce < 0) return false;
            end = ce + 3; text = s[i..end];
            return true;
        }
        else if (t.StartsWith("<?") || t.StartsWith("<!"))
        {
            end = gt + 1; text = t;
            return true;
        }
        // 开/闭标签:严格属性语法(引号感知/属性名校验),非法整体失败(#621/622/624/626/634)
        if (!TryParseHtmlTag(s, i, out int te)) return false;
        int nk = i + (t.StartsWith("</") ? 2 : 1);
        int n0 = nk;
        while (nk < s.Length && (char.IsAsciiLetterOrDigit(s[nk]) || s[nk] == '-')) nk++;
        name = s[n0..nk];   // 标签名独立提取(gt 可能被引号内 > 截断)
        // 脚本类黑名单:viewer 字面显示,导出也不透传
        if (name.Equals("script", StringComparison.OrdinalIgnoreCase)
            || name.Equals("style", StringComparison.OrdinalIgnoreCase)
            || name.Equals("iframe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("object", StringComparison.OrdinalIgnoreCase)
            || name.Equals("embed", StringComparison.OrdinalIgnoreCase)) return false;
        end = te; text = s[i..te];
        return true;
    }

    private void EmitText(string t, List<Run> runs, bool b, bool i2, bool s, string link)
    {
        // 裸 URL 拆分
        int pos = 0;
        while (pos < t.Length)
        {
            int idx = IndexOfUrl(t, pos);
            if (idx < 0) break;
            if (idx > pos) runs.Add(new Run(t[pos..idx], b, i2, s, false, link));
            int end = UrlEnd(t, idx);
            string tok = t[idx..end];
            string href = tok.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + tok : tok;
            runs.Add(new Run(tok, b, i2, s, false, link ?? href) { Bare = link == null });
            pos = end;
        }
        if (pos < t.Length) runs.Add(new Run(t[pos..], b, i2, s, false, link));
    }

    private int IndexOfUrl(string t, int start)
    {
        int a = t.IndexOf("http://", start, StringComparison.OrdinalIgnoreCase);
        int b = t.IndexOf("https://", start, StringComparison.OrdinalIgnoreCase);
        int w = -1;
        int probe = start;
        while (probe <= t.Length - 5)
        {
            int k = t.IndexOf("www.", probe, StringComparison.OrdinalIgnoreCase);
            if (k < 0) break;
            if (k == 0 || !char.IsAsciiLetterOrDigit(t[k - 1])) { w = k; break; }   // S94:汉字后紧跟 www. 也识别(原 IsLetterOrDigit 把汉字算作词内)
            probe = k + 1;
        }
        int best = -1;
        if (a >= 0) best = a;
        if (b >= 0 && (best < 0 || b < best)) best = b;
        if (w >= 0 && (best < 0 || w < best)) best = w;
        return best;
    }

    // S94:裸链接遇到汉字/假名/韩文/全角字符即止。中文里常写“访问https://x.com获取”;
    // 浏览器复制出的中文网址一般是 %编码,不受影响。偏离 GFM(原规则延续到空白),见设计规范 §4.5。
    private static bool IsCjkStop(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uAC00' && c <= '\uD7AF') || (c >= '\uF900' && c <= '\uFAFF')
        || (c >= '\uFE30' && c <= '\uFE4F') || (c >= '\uFF00' && c <= '\uFFEF') || (c >= '\uD840' && c <= '\uD8BF');

    private int UrlEnd(string t, int start)
    {
        int j = start;
        while (j < t.Length)
        {
            char c = t[j];
            if (char.IsWhiteSpace(c) || c is ')' or ']' or '}' or '<' or '"' or '\''
                || c is '。' or '，' or '；' or '：' or '！' or '？' or '」' or '』' or '》' or '】' or '\u201D' or '\u2019' || IsCjkStop(c))
                break;
            j++;
        }
        // GFM:末尾的 ? ! . , : ; * _ ~ 不属于自动链接(句末网址不带走句号)
        while (j > start + 1 && t[j - 1] is '?' or '!' or '.' or ',' or ':' or ';' or '*' or '_' or '~') j--;
        return j > start ? j : start + 1;
    }
}
