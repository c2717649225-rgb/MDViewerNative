// 有界数学子集的公式树节点(内容层):MathSyntax 负责构建,MathUnicode 转成文本,MathLayout 负责排版。
using System;
using System.Collections.Generic;

namespace MDViewerNative;

// 公式树节点只保存结构和排版结果槽位；测量、绘制与释放由渲染层的 MathLayout 完成（E4a）。
internal abstract class MAtom
{
    // 排版结果槽位：由 MathLayout 写入，节点自身不解释。
    public nint Layout;
    public float W, H, Base;

    public virtual IEnumerable<MAtom> Children => Array.Empty<MAtom>();

    // 子树中的全部文字叶子(先序)。
    public IEnumerable<MText> Texts()
    {
        if (this is MText self) { yield return self; yield break; }
        foreach (MAtom child in Children)
            if (child != null) foreach (MText text in child.Texts()) yield return text;
    }
}

internal sealed class MText : MAtom
{
    public string Text;
    public float Size;
    public bool Italic;    // 行内原生公式的变量斜体(S77);数学字母数字仍由 MathGlyphs 取直立
    public bool Upright;   // \mathrm/\operatorname/\text 的参数:不随行内斜体
    public bool Verbatim;  // \text 的参数:源码空格照印,不参与数学间距

    public MText(string t, float sz) { Text = t; Size = sz; }
}

internal sealed class MFrac : MAtom
{
    public MAtom Num, Den;

    public override IEnumerable<MAtom> Children => new[] { Num, Den };
}

internal sealed class MSqrt : MAtom
{
    public MAtom Body, Index;
    internal float RootX, BodyY;   // 排版槽位

    public override IEnumerable<MAtom> Children => new[] { Body, Index };
}

internal sealed class MScript : MAtom
{
    public MAtom BaseAtom, Super, Sub;
    public bool SupFirst;
    internal float BaseY, SuperY, SubY, SuperX;   // 排版槽位
    public float SuperBaseline => SuperY + (Super?.Base ?? 0f);   // 盒内坐标
    public float SubBaseline => SubY + (Sub?.Base ?? 0f);

    public override IEnumerable<MAtom> Children => new[] { BaseAtom, Super, Sub };
}

internal sealed class MRow : MAtom
{
    public List<MAtom> Items = new();

    public override IEnumerable<MAtom> Children => Items;
}

// 向量箭头 \vec / \overrightarrow(S77):组合箭头 U+20D7 的锚点随回退字体漂移(S76 遗留:偏到字母右侧),
// 改为按基底宽度自绘——箭杆居中压在基底上方,高于大写字母顶。
internal sealed class MAccent : MAtom
{
    public MAtom Body;
    internal float Lift, Rise, Len, Skew;   // 排版槽位

    public override IEnumerable<MAtom> Children => new[] { Body };
}
