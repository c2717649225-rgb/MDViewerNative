// CJK-aware text → KpItem sequence converter.
// Handles: CJK chars as individual Boxes with per-char break glue (KP 真正接管中文断行),
// kinsoku (行首禁则:闭合标点绑前字;行尾禁则:开放标点绑后字),
// line-end punctuation hanging (行尾标点悬挂,由 KpBreak 按末 Box Protrude 让出宽度),
// CJK-Latin boundary Glue.
using System;
using System.Collections.Generic;

namespace MDViewerNative;

internal static class KpCjk
{
    static bool IsCjk(char c) => c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF;
    // I4(GB/T 15834):弯引号 “ ” ‘ ’ 是 U+201C/201D/2018/2019(<0x2E80),此前掉进 Other 分支=可拆行/无悬挂;入表后禁则与悬挂同时生效
    static bool IsCjkPunct(char c) => c is '。' or '，' or '；' or '：' or '！' or '？' or '）' or '」' or '』' or '、' or '】' or '》' or '”' or '’';
    static bool IsCjkOpen(char c) => c is '「' or '『' or '（' or '《' or '【' or '“' or '‘';
    // 半角收尾标点:中文里常被随手使用,同样不得居行首(前无断点),其后可断(回写 prevC)
    static bool IsAsciiCloser(char c) => c is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '%';
    static bool IsLatin(char c) => char.IsLetterOrDigit(c) && c < 0x2E80;

    /// Convert plain text to KpItem sequence for optimal line breaking.
    /// wordWidth: callback to measure the pixel width of a word/char.
    /// itemChars(可选):输出每个 item 对应的 UTF-16 字符数,用于把断点映射回文本偏移。
    public static List<KpItem> TextToItems(string text, Func<string, double> wordWidth, List<int> itemChars = null, double charStretch = -1)
    {
        var items = new List<KpItem>();
        void Add(KpItem it, int chars)
        {
            items.Add(it);
            itemChars?.Add(chars);
        }
        int i = 0;
        int n = text.Length;
        char prevC = '\0';   // 上一个已发射的 CJK 侧字符(逐字断点胶与禁则判定用;'\0'=段首/上一项为拉丁词)
        double spaceW = wordWidth(" ");
        double spaceStretch = spaceW / 2.0;
        double spaceShrink = spaceW / 3.0;
        if (charStretch < 0) charStretch = spaceStretch * 0.12;   // I9:逐字胶由调用方按 em 传入(0.04em);默认回落旧值兼容 headless 探针

        while (i < n)
        {
            char c = text[i];

            // Skip whitespace, emit stretchy glue
            // 硬换行(S75):强制断点,前置无限伸展胶(同段末),该行不两端对齐;\n 归上一行,构行时不进布局
            if (c == '\n')
            {
                Add(new KpGlue(0, InfPenalty, 0), 1);
                Add(new KpPenalty(-InfPenalty, false), 0);
                prevC = '\0';
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                Add(new KpGlue(spaceW, spaceStretch, spaceShrink), 1);
                i++;
                continue;
            }

            // CJK character: each one is a Box
            if (IsCjk(c))
            {
                string ch = text[i].ToString();
                double w = wordWidth(ch);
                // 逐字断点胶:汉字之间可断、可微伸缩(宽 0,绘制不变);行首禁则(c 是闭合标点)与
                // 行尾禁则(prevC 是开放标点)不发射,标点因此绑死相邻字符
                if (prevC != '\0' && !IsCjkPunct(c) && !IsCjkOpen(prevC))
                    Add(new KpGlue(0, charStretch, charStretch), 0);
                // 闭合标点可悬挂:行尾让出半个字宽(microtype protrusion)
                // 连续标点挤压(S75,CjkPunct):量宽同步扣除;被压的闭合标点其后仍有标点,不悬挂
                double kept = CjkPunct.Kept(text, i);
                Add(new KpBox(w * kept, IsCjkPunct(c) && kept == 1.0 ? w * 0.5 : 0.0), 1);
                prevC = c;
                i++;

                // CJK punctuation: allow hanging (penalty 0 at line end, stretchy glue)
                if (i < n && IsCjkPunct(text[i]))
                {
                    string punct = text[i].ToString();
                    double pw = wordWidth(punct);
                    // Punct can be squeezed heavily (标点压缩)
                    double pk = CjkPunct.Kept(text, i);
                    Add(new KpBox(pw * pk, pk == 1.0 ? pw * 0.5 : 0.0), 1);
                    prevC = punct[0];
                    i++;
                }
                continue;
            }

            // Latin word: gather consecutive non-space non-CJK chars as one Box
            if (IsLatin(c) || c == '-' || c == '\'' || c == '"' || char.IsDigit(c))
            {
                int j = i;
                while (j < n && (IsLatin(text[j]) || text[j] == '-' || text[j] == '\'' || text[j] == '"' || char.IsDigit(text[j]) || text[j] == '.' && j + 1 < n && IsLatin(text[j + 1])))
                    j++;
                string word = text[i..j];
                double w = wordWidth(word);
                // CJK(含标点)→Latin 边界:自动间距胶(可断,标点后也补齐,#汉。Eng 原漏)
                if (IsCjk(prevC))
                    Add(new KpGlue(spaceW * 0.25, spaceStretch * 0.5, spaceShrink * 0.5), 0);
                Add(new KpBox(w), word.Length);
                prevC = '\0';
                i = j;

                // Latin→CJK boundary: insert auto-spacing glue
                if (i < n && IsCjk(text[i]))
                    Add(new KpGlue(spaceW * 0.25, spaceStretch * 0.5, spaceShrink * 0.5), 0);
                continue;
            }

            // Other characters (punctuation, symbols, emoji): treat as Box.
            // Surrogate pairs (emoji 等)必须整体测量,拆成孤立代理项会量出替换字形宽度
            {
                // I4:——(U+2014×2)/……(U+2026×2)按 GB/T 15834 占两字不可拆行,整对测宽成单个 Box(itemChars 记 2,偏移映射天然正确)
                bool pair = (c == '\u2014' && i + 1 < n && text[i + 1] == '\u2014')
                         || (c == '\u2026' && i + 1 < n && text[i + 1] == '\u2026');
                int len = pair || (i + 1 < n && char.IsSurrogatePair(text[i], text[i + 1])) ? 2 : 1;
                string ch = text.Substring(i, len);
                double w = wordWidth(ch);
                // S83:西文闭合标点(如 https: 的冒号)后接符号时不给断点,断点只留给其后的汉字(由汉字分支发射);
                // 原来 “https:” 与 “//” 之间可断,网址常在协议名后被拆开。
                if (prevC != '\0' && !IsCjkPunct(c) && !IsAsciiCloser(c) && !IsCjkOpen(prevC) && !IsAsciiCloser(prevC))
                    Add(new KpGlue(0, charStretch, charStretch), 0);
                double ok = len == 1 ? CjkPunct.Kept(text, i) : 1.0;
                Add(new KpBox(w * ok, IsCjkPunct(c) && ok == 1.0 ? w * 0.5 : 0.0), len);   // 闭合标点(含弯引号)行尾可悬挂;连续标点挤压(S75)
                if (IsCjk(c) || IsCjkPunct(c) || IsCjkOpen(c)) prevC = c;   // 弯引号必须回写:开放引号后的胶要禁("好 不断在 " 后)
                else if (pair) prevC = c;                                    // ——/…… 后:可断(prevC 非 CJK 不插中西自动间距)
                else if (IsAsciiCloser(c)) prevC = c;   // 其后接汉字时发射断点胶
                else prevC = '\0';
                i += len;
            }
        }

        // Paragraph end: infinite glue + forced break
        Add(new KpGlue(0, InfPenalty, 0), 0);
        Add(new KpPenalty(-InfPenalty, false), 0);

        return items;
    }

    /// S83:比整行还宽的一串 Box(长网址)放在哪一行都放不下,只能折在串内。在这种串里每个 “/” 之后给一个零宽断点,
    /// 让 KP 从引导语那一行就开始排网址、折在路径分隔处;否则 KP 只能让引导语独占一行、网址整行溢出,
    /// 再由 DirectWrite 在行内硬折(尾段常孤零零占一行)。放得下一行的串不动,仍整串不断;“://” 里的斜杠不给断点。
    public static void SplitOverlongRuns(List<KpItem> items, List<int> itemChars, string text, double maxWidth)
    {
        if (itemChars == null || itemChars.Count != items.Count) return;
        var ends = new int[items.Count];   // ends[k] = item k 之后的字符偏移
        for (int t = 0, pos = 0; t < items.Count; t++) { pos += itemChars[t]; ends[t] = pos; }
        int k = items.Count - 1;
        while (k >= 0)
        {
            if (items[k].K != KpItem.Kind.Box) { k--; continue; }
            int last = k;
            double w = 0.0;
            while (k >= 0 && items[k].K == KpItem.Kind.Box) { w += items[k].B.Width; k--; }
            if (w <= maxWidth) continue;
            for (int j = last - 1; j > k; j--)   // 从后往前插,前面的下标不受影响;串尾本就可断,不再加
            {
                int c = ends[j] - 1;
                if (c < 0 || c >= text.Length || text[c] != '/') continue;
                if (c + 1 < text.Length && text[c + 1] == '/') continue;
                if (c >= 2 && text[c - 1] == '/' && text[c - 2] == ':') continue;
                items.Insert(j + 1, new KpPenalty(0, false));
                itemChars.Insert(j + 1, 0);
            }
        }
    }

    const double InfPenalty = 10_000.0;
}
