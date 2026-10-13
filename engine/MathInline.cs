// 行内原生公式(S77):Unicode 线性写法表示不了的行内公式(字母下标 w_{age}、向量箭头)以
// IDWriteInlineObject 嵌入正文布局,由 MathRender 排版树绘制;Run.Text 仍是线性写法,复制/查找不变。
// COM 对象在非托管内存里手工构造(虚表 + 引用计数 + 排版树句柄),寿命跟随持有它的文字布局。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MDViewerNative;

internal static unsafe class MathInline
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Obj
    {
        public nint* Vtbl;
        public int Refs;
        public int BreakAfter;   // 1 = 段后可断行(运算符结尾的段)
        public float Size;       // 所在布局字号
        public nint Tree;   // GCHandle → MAtom
    }

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidInlineObject = new("8339FDE3-106F-47AB-8373-1C6295EB10B3");
    private static readonly nint* Vtbl = CreateVtbl();

    // 在 layout 的 [start, start + length) 挂公式,按 size(所在布局字号,标题随之放大)排版;解析/量度失败返回 false,保留线性文本。
    public static bool Attach(nint layout, string tex, uint start, uint length, float size)
    {
        if (length == 0) return false;
        List<(MAtom Tree, int Length, bool BreakAfter)> pieces;
        try
        {
            MAtom tree = MathRender.ParseInline(tex);
            if (tree == null) return false;
            pieces = Split(tree, (int)length);
            foreach (var piece in pieces) MathRender.Measure(piece.Tree, size);
        }
        catch (Exception) { return false; }
        bool ok = true;
        uint at = start;
        foreach (var (piece, len, breakAfter) in pieces)
        {
            ok &= Set(layout, piece, at, (uint)len, breakAfter, size);
            at += (uint)len;
        }
        return ok;
    }

    // 顶层在关系/二元运算符之后切段(TeX 断行位置),每段一个内嵌对象、段后可断行——长式不再整块挤到下一行。
    // 语法层把相邻普通字符并成一个 MText("ŷ = "),先按运算符拆成记号(运算符连同其后空白归前一段)。
    // 段长按线性写法逐项累计,须与 Run 文本对齐;对不上或只有一段时整式一块。
    // 段长取定之后才加数学间距,且按整式计算(断在 = 后的间距不丢)。
    private static List<(MAtom Tree, int Length, bool BreakAfter)> Split(MAtom tree, int length)
    {
        var whole = new List<(MAtom Tree, int Length, bool BreakAfter)> { (tree, length, false) };
        if (tree is not MRow row) { MathSpacing.Apply(tree); return whole; }
        var pieces = new List<(MAtom Tree, int Length, bool BreakAfter)>();
        var tokens = new List<MAtom>();
        var group = new List<MAtom>();
        int len = 0, total = 0;
        bool content = false;
        foreach (MAtom item in row.Items)
            foreach (MAtom token in Tokens(item))
            {
                tokens.Add(token);
                string text = MathUnicode.Format(token);
                group.Add(token);
                len += text.Length;
                string trimmed = text.TrimEnd();
                bool endsWithOp = token is MText && trimmed.Length > 0 && BreakOps.Contains(trimmed[^1]);
                if (!content) foreach (char c in endsWithOp ? trimmed[..^1] : text) if (!char.IsWhiteSpace(c) && !BreakOps.Contains(c)) { content = true; break; }
                if (endsWithOp && content)
                {
                    pieces.Add((new MRow { Items = group }, len, true));
                    total += len; group = new List<MAtom>(); len = 0; content = false;
                }
            }
        if (group.Count > 0)
        {
            if (!content && pieces.Count > 0)   // 尾部只剩空白/孤立运算符:并入上一段
            {
                ((MRow)pieces[^1].Tree).Items.AddRange(group);
                pieces[^1] = (pieces[^1].Tree, pieces[^1].Length + len, false);
            }
            else pieces.Add((new MRow { Items = group }, len, false));
            total += len;
        }
        if (pieces.Count < 2 || total != length) { MathSpacing.Apply(tree); return whole; }   // 拆出的记号未入树、未量度,无资源
        pieces[^1] = (pieces[^1].Tree, pieces[^1].Length, false);
        MathSpacing.Row(tokens);
        return pieces;
    }

    private const string BreakOps = "=+-−<>≤≥≈≠≡∼≃∝·×÷∈∉⊂⊆∪∩→⇒⇔↦";

    // 测试用:各段线性长度(S77)。
    internal static List<int> PieceLengths(string tex)
    {
        MAtom tree = MathRender.ParseInline(tex);
        if (tree == null) return new List<int>();
        return Split(tree, MathUnicode.Convert(tex).Length).ConvertAll(p => p.Length);
    }

    private static bool Set(nint layout, MAtom tree, uint start, uint length, bool breakAfter, float size)
    {
        var obj = (Obj*)NativeMemory.Alloc((nuint)sizeof(Obj));
        obj->Vtbl = Vtbl;
        obj->Refs = 1;
        obj->BreakAfter = breakAfter ? 1 : 0;
        obj->Size = size;
        obj->Tree = GCHandle.ToIntPtr(GCHandle.Alloc(tree));
        int hr = Dw.SetInlineObject(layout, (nint)obj, new DWRITE_TEXT_RANGE { first = start, length = length });
        Unref(obj);
        return hr >= 0;
    }

    private static IEnumerable<MAtom> Tokens(MAtom item)
    {
        if (item is not MText t || t.Verbatim || t.Text.IndexOfAny(BreakOps.ToCharArray()) < 0) { yield return item; yield break; }
        int from = 0;
        for (int i = 0; i < t.Text.Length; i++)
        {
            if (BreakOps.IndexOf(t.Text[i]) < 0) continue;
            int end = i + 1;
            while (end < t.Text.Length && char.IsWhiteSpace(t.Text[end])) end++;
            yield return new MText(t.Text[from..end], t.Size) { Italic = t.Italic, Upright = t.Upright };
            from = end; i = end - 1;
        }
        if (from < t.Text.Length) yield return new MText(t.Text[from..], t.Size) { Italic = t.Italic, Upright = t.Upright };
    }

    private static MAtom TreeOf(Obj* self) => (MAtom)GCHandle.FromIntPtr(self->Tree).Target;

    private static uint Unref(Obj* self)
    {
        int left = Interlocked.Decrement(ref self->Refs);
        if (left == 0)
        {
            var handle = GCHandle.FromIntPtr(self->Tree);
            MathRender.Free((MAtom)handle.Target);
            handle.Free();
            NativeMemory.Free(self);
        }
        return (uint)left;
    }

    private static nint* CreateVtbl()
    {
        var v = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged[Stdcall]<Obj*, Guid*, nint*, int>)&QueryInterface;
        v[1] = (nint)(delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged[Stdcall]<Obj*, nint, nint, float, float, int, int, nint, int>)&Draw;
        v[4] = (nint)(delegate* unmanaged[Stdcall]<Obj*, DWRITE_INLINE_OBJECT_METRICS*, int>)&GetMetrics;
        v[5] = (nint)(delegate* unmanaged[Stdcall]<Obj*, DWRITE_OVERHANG_METRICS*, int>)&GetOverhangMetrics;
        v[6] = (nint)(delegate* unmanaged[Stdcall]<Obj*, int*, int*, int>)&GetBreakConditions;
        return v;
    }

    private const int E_NOINTERFACE = unchecked((int)0x80004002), E_POINTER = unchecked((int)0x80004003), E_FAIL = unchecked((int)0x80004005);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(Obj* self, Guid* iid, nint* result)
    {
        if (result == null) return E_POINTER;
        *result = 0;
        if (*iid != IidUnknown && *iid != IidInlineObject) return E_NOINTERFACE;
        Interlocked.Increment(ref self->Refs);
        *result = (nint)self;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(Obj* self) => (uint)Interlocked.Increment(ref self->Refs);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(Obj* self) => Unref(self);

    // 原点为对象盒左上角(DirectWrite 已按报告的基线对齐正文);链接内的公式随链接画刷着色。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Draw(Obj* self, nint context, nint renderer, float x, float y, int sideways, int rtl, nint effect)
    {
        try
        {
            MathLayout.Draw(TreeOf(self), x, y, self->Size, effect != 0 ? effect : RenderResources.BInk, 0);
            return 0;
        }
        catch (Exception) { return E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetMetrics(Obj* self, DWRITE_INLINE_OBJECT_METRICS* metrics)
    {
        if (metrics == null) return E_POINTER;
        MAtom tree = TreeOf(self);
        *metrics = new DWRITE_INLINE_OBJECT_METRICS { width = tree.W, height = tree.H, baseline = tree.Base };
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetOverhangMetrics(Obj* self, DWRITE_OVERHANG_METRICS* overhangs)
    {
        if (overhangs == null) return E_POINTER;
        *overhangs = default;
        return 0;
    }

    // 段内不可拆;运算符结尾的段后可断行(CAN_BREAK),其余交给相邻文字决定(NEUTRAL)。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetBreakConditions(Obj* self, int* before, int* after)
    {
        if (before == null || after == null) return E_POINTER;
        *before = 0;
        *after = self->BreakAfter;
        return 0;
    }
}
