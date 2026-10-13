using System.Collections.Generic;

namespace MDViewerNative;

// Shared content contract for parsing, preview and export. View geometry belongs to DocView.
/// 内联片段:文本 + 样式标志
internal sealed class Run
{
    public string Text;
    public bool B, I, S, Code;
    public bool Mark;        // ==高亮==
    public int SupSub;       // 1=上标 2=下标
    public bool Fn;          // 脚注引用(小字号渲染)
    public bool U;           // 下划线(<u>/<ins>)
    public bool Opaque;      // 原始HTML/字面片段:不参与强调匹配,渲染按原样
    public string ImageAlt;
    public bool Img;         // 行内图片:导出 <img>(alt=Text),渲染与链接同形(下划线)
    public string Link;   // 非 null = 链接(下划线渲染);Img 时为图片源
    public string LinkTitle; // 链接/图片 title 属性(可选)
    public string LinkOuter; // 图片的外层链接(图片位于链接 label 内时挂,#519);导出 <a> 包裹
    public bool Bare;        // 裸网址自动链接(EmitText 拆出):不算“链接内嵌链接”,外层链接挂载时被覆盖(A5)
    public string MathTex;   // 行内公式的 TeX 源(S77):非 null 即公式——原子片段,不参与强调配对
    public bool MathNative;  // Unicode 表示不了(字母上下标、\vec):由原生排版器画成内嵌对象;Text 仍是线性写法供复制/查找
    public Run(string t, bool b = false, bool i = false, bool s = false, bool code = false, string link = null,
               string title = null, bool mark = false, int supsub = 0, bool fn = false, bool u = false, bool img = false)
    { Text = img && string.IsNullOrEmpty(t) ? "图片" : t; ImageAlt = img ? t : null; B = b; I = i; S = s; Code = code; Link = link; LinkTitle = title; Mark = mark; SupSub = supsub; Fn = fn; U = u; Img = img; }
}

/// 块在原文中的 UTF-16 范围;Container=true 表示范围继承自外层容器(引用/列表),不是本块自己的行。
internal readonly record struct SourceRange(int Start, int End, bool Container)
{
    internal int Length => End - Start;
}

/// 扁平块模型(引用/列表在解析期展开为带缩进的平铺块)
internal abstract class Block
{
    internal SourceRange? Source; // Original UTF-16 range; null for synthetic content.
    public int XOff;        // 左侧额外缩进(引用/列表)
    public int QuoteDepth; // Structural quote depth; list indentation is not a quote.
    public int QSeq;        // 引用运行序号(同一次引用收集 = 1;导出按此分断相邻引用,#244)
    public string Alert;    // GitHub 提示块类型(note/tip/important/warning/caution);整段引用运行共享
    public bool AlertTitle; // 提示块合成的标题段(解析期插入,不来自源码)
}

internal sealed class Heading : Block { public int Level; public List<Run> Inlines; }
internal sealed class Para : Block { public List<Run> Inlines; }
internal sealed class CodeBlock : Block { public string Text; public string Lang; public bool FrontMatter; }   // FrontMatter:文首 YAML 元数据,渲染为弱化代码块
internal sealed class HtmlDetails : Block { public List<Run> Summary; public string Body; public List<Run> BodyRuns; }   // BodyRuns:阅读态正文(视图层,S74),导出仍用 Body
internal sealed class Quote : Block { public List<Run> Inlines; }   // 嵌套引用在解析期平铺,层级体现在 XOff
internal sealed class ListItem : Block { public string Marker; public List<Run> Inlines; public int Task; public bool Loose; public List<Block> Sub; public int Nest; public int ListGroup; public bool Continuation; public bool GroupBreakAfter; }
internal sealed class Table : Block { public List<Run>[] Header; public List<Run>[][] Rows; public int[] Aligns; public bool PlainHeader; }  // Aligns: 0=左,1=中,2=右;PlainHeader:阅读态 HTML 表格无 <th> 时首行占表头位但不加粗(S72)
internal sealed class HRule : Block { }
internal sealed class HtmlBlock : Block { public string Text; }   // 原始 HTML 块(viewer 字面渲染;导出经白名单过滤,脚本类整块转义,见 ADR-0002)
internal sealed class Image : Block { public string Alt, Src, Title; }
