// 有界数学子集的原生排版树;语法由 MathSyntax 统一校验。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// 公式树的测量、绘制与释放(渲染层)。各分支的算法与 E4a 之前各节点的虚方法逐字一致。
internal static unsafe class MathLayout
{
    public static void Measure(MAtom atom, float sz)
    {
        switch (atom)
        {
            case MText t: MeasureText(t, sz); break;
            case MFrac f: MeasureFrac(f, sz); break;
            case MSqrt r: MeasureSqrt(r, sz); break;
            case MScript s: MeasureScript(s, sz); break;
            case MRow row: MeasureRow(row, sz); break;
            case MAccent a: MeasureAccent(a, sz); break;
            default: throw new InvalidOperationException("Unknown math atom: " + (atom?.GetType().Name ?? "null"));
        }
    }

    public static void Draw(MAtom atom, float x, float yTop, float sz, nint ink, nint comment)
    {
        switch (atom)
        {
            case MText t: DrawText(t, x, yTop, ink); break;
            case MFrac f: DrawFrac(f, x, yTop, sz, ink, comment); break;
            case MSqrt r: DrawSqrt(r, x, yTop, sz, ink, comment); break;
            case MScript s: DrawScript(s, x, yTop, sz, ink, comment); break;
            case MRow row: DrawRow(row, x, yTop, sz, ink, comment); break;
            case MAccent a: DrawAccent(a, x, yTop, sz, ink, comment); break;
            default: throw new InvalidOperationException("Unknown math atom: " + (atom?.GetType().Name ?? "null"));
        }
    }

    // null 安全:与原先各处的 ?.Free() 一致;子节点按原先的释放顺序(Children 顺序)。
    public static void Free(MAtom atom)
    {
        switch (atom)
        {
            case null: break;
            case MText t:
                if (t.Layout != IntPtr.Zero) { Marshal.Release(t.Layout); t.Layout = IntPtr.Zero; }
                break;
            default: foreach (MAtom child in atom.Children) Free(child); break;
        }
    }

    private static void MeasureText(MText t, float sz)
    {
        if (t.Text.Length == 0) { t.W = 0; t.H = 0; t.Base = 0; return; }
        if (t.Layout == IntPtr.Zero) t.Layout = Dw.MkLayout(t.Text, RenderResources.FmtBody, 10000f);
        if (t.Italic)   // 只斜字母(连同其后的组合重音);数字、括号、运算符直立,同 TeX
            for (int i = 0; i < t.Text.Length; i++)
                if (char.IsLetter(t.Text[i]) || CharUnicodeInfo.GetUnicodeCategory(t.Text[i]) == UnicodeCategory.NonSpacingMark && i > 0 && char.IsLetter(t.Text[i - 1]))
                    Dw.SetFontStyle(t.Layout, 1, new DWRITE_TEXT_RANGE { first = (uint)i, length = 1 });
        MathGlyphs.Apply(t.Layout, t.Text);
        MathGlyphs.ApplySymbols(t.Layout, t.Text);
        Dw.Bind(t.Layout);
        Dw.SetFontSize(t.Layout, sz, new DWRITE_TEXT_RANGE { first = 0, length = (uint)t.Text.Length });
        t.Size = sz;
        t.W = Dw.LayoutW(t.Layout);
        t.H = Dw.LayoutH(t.Layout);
        var lines = Dw.Lines(t.Layout);
        t.Base = lines.Length > 0 ? lines[0].baseline : t.H * 0.8f;   // 真实基线(S77;原 H×0.8 估值,行内嵌入需与正文基线对齐)
    }

    private static void DrawText(MText t, float x, float yTop, nint ink)
    {
        if (t.W <= 0) return;
        RenderResources.PDraw(RenderResources.Rt, new D2D1_POINT_2F { x = x, y = yTop }, t.Layout, ink, 0);
    }

    private static void MeasureFrac(MFrac f, float sz)
    {
        Measure(f.Num, sz);
        Measure(f.Den, sz);
        f.W = MathF.Max(f.Num.W, f.Den.W) + 8f;
        f.H = f.Num.H + f.Den.H + 12f;
        f.Base = f.Num.H + 6f;
    }

    private static void DrawFrac(MFrac f, float x, float yTop, float sz, nint ink, nint comment)
    {
        float nx = x + (f.W - f.Num.W) / 2f;
        Draw(f.Num, nx, yTop + (f.Base - 6f - f.Num.H), sz, ink, comment);
        RenderResources.StrokeLine(x + 2f, yTop + f.Base, x + f.W - 2f, yTop + f.Base, RenderResources.BInk, 1.2f);
        float dx = x + (f.W - f.Den.W) / 2f;
        Draw(f.Den, dx, yTop + f.Base + 6f, sz, ink, comment);
    }

    private static void MeasureSqrt(MSqrt r, float sz)
    {
        Measure(r.Body, sz);
        if (r.Index != null) Measure(r.Index, sz * 0.55f);
        r.RootX = r.Index?.W * 0.75f ?? 0f;
        r.BodyY = MathF.Max(4f, (r.Index?.H ?? 0f) * 0.6f);
        r.W = MathF.Max(r.RootX + r.Body.W + 14f, r.Index?.W ?? 0f);
        r.H = MathF.Max(r.Body.H + r.BodyY + 3f, r.Index?.H ?? 0f);
        r.Base = r.Body.Base + r.BodyY;
    }

    private static void DrawSqrt(MSqrt r, float x, float yTop, float sz, nint ink, nint comment)
    {
        if (r.Index != null) Draw(r.Index, x, yTop, sz * 0.55f, ink, comment);
        float left = x + r.RootX, top = yTop + r.BodyY - 3f;
        float y1 = yTop + r.BodyY + r.Body.H * 0.55f, y2 = yTop + r.H - 3f;
        RenderResources.StrokeLine(left, y1, left + 4f, y2, ink, 1.4f);
        RenderResources.StrokeLine(left + 4f, y2, left + 10f, top, ink, 1.4f);
        RenderResources.StrokeLine(left + 10f, top, x + r.W, top, ink, 1.4f);
        Draw(r.Body, left + 12f, yTop + r.BodyY, sz, ink, comment);
    }

    private static void MeasureScript(MScript s, float sz)
    {
        Measure(s.BaseAtom, sz);
        if (s.Super != null) Measure(s.Super, sz * 0.7f);
        if (s.Sub != null) Measure(s.Sub, sz * 0.7f);
        // 按基线定位(S77,基线已为真实值):上标基线抬 0.42em,下标基线降 0.2em(TeX sup1/sub1 量级);
        // 原按盒顶估算,下标整个悬在基线下方(w_{age} 的 age 顶在 w 基线下 3 DIP)。
        float supTop = s.Super == null ? 0f : s.BaseAtom.Base - sz * 0.42f - s.Super.Base;
        float subTop = s.Sub == null ? 0f : s.BaseAtom.Base + sz * 0.2f - s.Sub.Base;
        float top = MathF.Min(0f, supTop);
        float bottom = MathF.Max(s.BaseAtom.H, MathF.Max(supTop + (s.Super?.H ?? 0f), subTop + (s.Sub?.H ?? 0f)));
        // 上标紧贴基底(S77 续:原固定前导 1 DIP,加上回退字形侧边距,]ᵀ 隔出约 4.5 DIP);斜体字母基底补斜体校正,下标不补(同 TeX)
        s.SuperX = s.BaseAtom.W + (s.BaseAtom is MText { Italic: true } b && b.Text.Length > 0 && char.IsLetter(b.Text[^1]) ? sz * 0.08f : 0f);
        s.W = MathF.Max(s.SuperX + (s.Super?.W ?? 0f), s.BaseAtom.W + (s.Sub?.W ?? 0f)) + sz * 0.03f;   // 尾随 scriptspace
        s.H = bottom - top;
        s.Base = s.BaseAtom.Base - top;
        s.BaseY = -top; s.SuperY = supTop - top; s.SubY = subTop - top;
    }

    private static void DrawScript(MScript s, float x, float yTop, float sz, nint ink, nint comment)
    {
        Draw(s.BaseAtom, x, yTop + s.BaseY, sz, ink, comment);
        if (s.Super != null) Draw(s.Super, x + s.SuperX, yTop + s.SuperY, sz * 0.7f, ink, comment);
        if (s.Sub != null) Draw(s.Sub, x + s.BaseAtom.W, yTop + s.SubY, sz * 0.7f, ink, comment);
    }

    private static void MeasureRow(MRow row, float sz)
    {
        row.W = 0; row.H = 0; row.Base = 0;
        float descent = 0;
        foreach (var a in row.Items)
        {
            Measure(a, sz);
            row.W += a.W;
            row.Base = MathF.Max(row.Base, a.Base);
            descent = MathF.Max(descent, a.H - a.Base);
        }
        row.H = row.Base + descent;
    }

    private static void DrawRow(MRow row, float x, float yTop, float sz, nint ink, nint comment)
    {
        float cx = x;
        foreach (var a in row.Items)
        {
            Draw(a, cx, yTop + (row.Base - a.Base), sz, ink, comment);
            cx += a.W;
        }
    }

    private static void MeasureAccent(MAccent a, float sz)
    {
        Measure(a.Body, sz);
        a.Len = MathF.Max(a.Body.W * 0.85f, sz * 0.45f);
        a.Rise = sz * 0.8f;
        float head = sz * 0.15f;
        a.Lift = MathF.Max(0f, head + 1f - (a.Body.Base - a.Rise));   // 箭头高出基底盒顶时整体下移让位
        // 斜体字母上部右倾:箭头随之右移(数学字母数字已由 MathGlyphs 取直立,代理对即是)
        a.Skew = a.Body is MText { Italic: true } text && text.Text.Length > 0 && !char.IsSurrogate(text.Text[0]) ? sz * 0.08f : 0f;
        a.W = MathF.Max(a.Body.W, a.Len);
        a.H = a.Body.H + a.Lift;
        a.Base = a.Body.Base + a.Lift;
    }

    private static void DrawAccent(MAccent a, float x, float yTop, float sz, nint ink, nint comment)
    {
        Draw(a.Body, x + (a.W - a.Body.W) / 2f, yTop + a.Lift, sz, ink, comment);
        float y = yTop + a.Base - a.Rise, cx = x + a.W / 2f + a.Skew;
        float x0 = cx - a.Len / 2f, x1 = cx + a.Len / 2f, head = sz * 0.15f, stroke = MathF.Max(1f, sz * 0.06f);
        RenderResources.StrokeLine(x0, y, x1, y, ink, stroke);
        RenderResources.StrokeLine(x1 - head, y - head * 0.7f, x1, y, ink, stroke);
        RenderResources.StrokeLine(x1 - head, y + head * 0.7f, x1, y, ink, stroke);
    }
}

// TeX 数学间距(S77 续):源码空白不显示,间距由原子类别决定——二元运算符两侧中等间距(4/18em≈U+205F)、
// 关系符两侧粗间距(5/18em≈U+2005)、标点后细间距(U+2009)。二元符在行首或紧跟运算符/开括号/标点、
// 或位于行尾/关系符/闭括号/标点之前时视为普通符号(一元负号)。上下标内不加间距,只去源码空白。
internal static class MathSpacing
{
    private const char Thin = '\u2009', Medium = '\u205F', Thick = '\u2005';
    private const string BinOps = "+-−×·÷±∓∗∘∙⋅∪∩∧∨⊕⊗⊖⊙⋆∖";
    private const string RelOps = "=<>≤≥≈≠≡∼≃≅∝∈∉∋⊂⊃⊆⊇→←↔⇒⇐⇔↦∣∥≪≫≺≻:";
    private enum Kind { None, Ord, Bin, Rel, Open, Close, Punct }

    public static void Apply(MAtom atom) => Walk(atom, true);

    // 一串相邻原子按同一行处理(行内切段后仍按整式计算,断在 = 后的间距不丢)。
    public static void Row(IList<MAtom> items) => Line(items, true);

    private static void Walk(MAtom atom, bool spacing)
    {
        switch (atom)
        {
            case MRow row: Line(row.Items, spacing); break;
            case MText text: Line(new MAtom[] { text }, spacing); break;
            case MFrac fraction: Walk(fraction.Num, spacing); Walk(fraction.Den, spacing); break;
            case MSqrt root: Walk(root.Body, spacing); if (root.Index != null) Walk(root.Index, false); break;
            case MAccent accent: Walk(accent.Body, spacing); break;
            case MScript script:
                Walk(script.BaseAtom, spacing);
                if (script.Super != null) Walk(script.Super, false);
                if (script.Sub != null) Walk(script.Sub, false);
                break;
        }
    }

    private static bool Source(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static Kind Classify(char c) =>
        BinOps.IndexOf(c) >= 0 ? Kind.Bin : RelOps.IndexOf(c) >= 0 ? Kind.Rel
        : c is '(' or '[' or '{' or '⟨' or '⌊' or '⌈' ? Kind.Open : c is ')' or ']' or '}' or '⟩' or '⌋' or '⌉' ? Kind.Close
        : c is ',' or ';' ? Kind.Punct : Kind.Ord;

    private static void Line(IList<MAtom> items, bool spacing)
    {
        // 1) 全行记号序列:文字逐字(组合符号随前字),其余原子记为普通符号
        var kinds = new List<Kind>();
        foreach (MAtom item in items)
        {
            if (item is MText text && !text.Verbatim)
            {
                foreach (char c in text.Text)
                    if (!Source(c) && !char.IsLowSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                        kinds.Add(Classify(c));
            }
            else
            {
                if (item is not MText) Walk(item, spacing);
                kinds.Add(Kind.Ord);
            }
        }
        for (int i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] != Kind.Bin) continue;
            Kind prev = i > 0 ? kinds[i - 1] : Kind.None, next = i + 1 < kinds.Count ? kinds[i + 1] : Kind.None;
            if (prev is Kind.None or Kind.Bin or Kind.Rel or Kind.Open or Kind.Punct || next is Kind.None or Kind.Rel or Kind.Close or Kind.Punct)
                kinds[i] = Kind.Ord;
        }
        // 2) 重写文字:去源码空白,按类别加间距
        int at = 0;
        foreach (MAtom item in items)
        {
            if (item is not MText text || text.Verbatim) { at++; continue; }
            var sb = new StringBuilder(text.Text.Length + 4);
            foreach (char c in text.Text)
            {
                if (Source(c)) continue;
                if (char.IsLowSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) { sb.Append(c); continue; }
                Kind kind = kinds[at++];
                if (!spacing) { sb.Append(c); continue; }
                switch (kind)
                {
                    case Kind.Bin: sb.Append(Medium).Append(c).Append(Medium); break;
                    case Kind.Rel: sb.Append(Thick).Append(c).Append(Thick); break;
                    case Kind.Punct: sb.Append(c).Append(Thin); break;
                    default: sb.Append(c); break;
                }
            }
            text.Text = sb.ToString();
        }
    }
}

internal unsafe static class MathRender
{
    // 块公式:解析、加数学间距、按正文字号量度。
    public static MAtom Parse(string tex)
    {
        if (!MathSyntax.TryParse(tex, out var row)) return null;
        MathSpacing.Apply(row);
        return Measure(row, RenderResources.BodySize);
    }

    // 行内原生公式(S77):只解析并标斜体(字母斜、正体命令参数直立);切段、间距、量度由 MathInline 按所在布局字号完成。
    public static MAtom ParseInline(string tex)
    {
        if (!MathSyntax.TryParse(tex, out var row)) return null;
        foreach (MText text in row.Texts()) text.Italic = !text.Upright;
        return row;
    }

    public static MAtom Measure(MAtom tree, float size)
    {
        try
        {
            MathLayout.Measure(tree, size);
            return tree;
        }
        catch
        {
            MathLayout.Free(tree);
            throw;
        }
    }

    public static void Free(MAtom a) => MathLayout.Free(a);

    public static void Draw(MAtom tree, float xLeft, float yTop, nint ink, nint comment)
    {
        MathLayout.Draw(tree, xLeft, yTop, RenderResources.BodySize, ink, comment);
    }
}
