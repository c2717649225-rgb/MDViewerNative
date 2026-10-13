using System;
using System.Collections.Generic;
using System.Linq;

namespace MDViewerNative;

// 块的阅读态文本：排版、选区、复制、查找共用的同一个文本域（只依赖文档模型，不碰 DirectWrite）。
// E18b 从 DocView 原样搬出，名字不变（DocView 以 using static 引用）；TableRowLens 必须与 BlockTextOf 的表格行一致。
internal static class BlockText
{
    // viewer 侧码类文本:tab 展开为 4 空格(布局/选区/复制一致);块内原始文本仍保留 tab 供导出(规范 Tabs 节)
    internal static string Vt(string s) => s.Replace("\t", "    ");

    // <details> 正文文本域:阅读态 BodyRuns(S74)或原文;排版、选区、复制共用。
    internal static string DetailsBodyText(HtmlDetails d) => d.BodyRuns != null ? string.Concat(d.BodyRuns.Select(r => r.Text)) : Vt(d.Body);

    internal static string SubBlockText(Block b)
    {
        if (b is CodeBlock code)
        {
            string text = Vt(code.Text);
            return text.EndsWith('\n') ? text[..^1] : text;
        }
        return BlockTextOf(b);
    }

    internal static string BlockTextOf(Block b)
    {
        if (b is HtmlDetails dd)
        {
            return Vt(string.Concat(dd.Summary.Select((Run r) => r.Text))) + "\n" + DetailsBodyText(dd);
        }
        if (b is ListItem lbi && lbi.Sub != null)
        {
            // 项内子块:主文本 + 各子块文本(与 _blockStart/SearchText 链一致)
            var sbl = new System.Text.StringBuilder(string.Concat(lbi.Inlines.Select((Run r) => r.Text)));
            for (int k = 0; k < lbi.Sub.Count; k++)
            {
                if (k > 0 || lbi.Inlines.Count > 0) sbl.Append('\n');
                sbl.Append(SubBlockText(lbi.Sub[k]));
            }
            return sbl.ToString();
        }
        if (b is HtmlBlock hb2)
        {
            return Vt(hb2.Text).TrimEnd('\n');
        }
        if (!(b is CodeBlock codeBlock))
        {
            if (!(b is Image { Alt: var alt }))
            {
                if (!(b is Heading heading))
                {
                    if (!(b is Para para))
                    {
                        if (!(b is Quote quote))
                        {
                            if (!(b is ListItem listItem))
                            {
                                Table table = b as Table;
                                if (table != null)
                                {
                                    return string.Join("\n", new string[1] { string.Join(" | ", table.Header.Select((List<Run> c) => string.Concat(c.Select((Run r) => r.Text)))) }.Concat(table.Rows.Select((List<Run>[] row) => string.Join(" | ", from c in Enumerable.Range(0, table.Header.Length)
                                                                                                                                                                                                                                                     select (c >= row.Length) ? "" : string.Concat(row[c].Select((Run r) => r.Text))))));
                                }
                                return "";
                            }
                            return string.Concat(listItem.Inlines.Select((Run r) => r.Text));
                        }
                        return string.Concat(quote.Inlines.Select((Run r) => r.Text));
                    }
                    return string.Concat(para.Inlines.Select((Run r) => r.Text));
                }
                return string.Concat(heading.Inlines.Select((Run r) => r.Text));
            }
            return alt;
        }
        return Vt(codeBlock.Text).TrimEnd('\n');
    }

    internal static string CellText(Table t, int row, int col)
    {
        List<Run> list = ((row == 0) ? t.Header[col] : ((col < t.Rows[row - 1].Length) ? t.Rows[row - 1][col] : null));
        if (list != null)
        {
            return string.Concat(list.Select((Run r) => r.Text));
        }
        return "";
    }

    internal static int[] TableRowLens(Table t)
    {
        int[] array = new int[t.Rows.Length + 1];
        array[0] = string.Join(" | ", t.Header.Select((List<Run> c) => string.Concat(c.Select((Run r) => r.Text)))).Length;
        int r2;
        for (r2 = 0; r2 < t.Rows.Length; r2++)
        {
            array[r2 + 1] = string.Join(" | ", from c in Enumerable.Range(0, t.Header.Length)
                                               select (c >= t.Rows[r2].Length) ? "" : string.Concat(t.Rows[r2][c].Select((Run x) => x.Text))).Length;
        }
        return array;
    }
}
