// 编辑器纯逻辑(无 Win32 依赖,可单测):列表续行/块缩进变换。
// EditorControl handles native messages; these transformations operate on plain text.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MDViewerNative;

internal static class EditorKit
{
    // Native EDIT line messages describe visual wrapping, not Markdown source lines.
    public static int LineStart(string text, int position) => position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
    public static int LineEnd(string text, int position)
    {
        int newline = text.IndexOf('\n', position);
        return newline < 0 ? text.Length : newline > 0 && text[newline - 1] == '\r' ? newline - 1 : newline;
    }

    // 列表前缀:缩进 + 无序(- * +)或有序(数字 + . 或 ))
    private static readonly Regex RePrefix = new(@"^(\s*)(?:([-*+]) |(\d+)([.)]) )", RegexOptions.Compiled);

    /// 回车时的续行决策。
    /// 返回: null = 走默认换行;"" = 清除当前行前缀(空列表项退出);其它 = 插入到光标处的续行文本。
    public static string EnterContinuation(string lineText)
    {
        if (lineText == null) return null;
        var m = RePrefix.Match(lineText);
        if (!m.Success) return null;
        string indent = m.Groups[1].Value;
        string body = lineText[m.Length..];
        if (body.Trim().Length == 0)
        {
            // 空列表项回车:清除前缀,退出列表
            return "";
        }
        if (m.Groups[3].Success)
        {
            // Increment ordered markers while keeping the delimiter; overflow starts at 1.
            if (!int.TryParse(m.Groups[3].Value, out int n) || n < 0 || n >= int.MaxValue) n = 0;
            return "\r\n" + indent + (n + 1) + m.Groups[4].Value + " ";
        }
        return "\r\n" + indent + m.Groups[2].Value + " ";
    }

    /// 块缩进:给 text 的每个非空行加两个空格
    public static string IndentBlock(string text, int width = 2, bool useTabs = false)
    {
        string indent = useTabs ? "\t" : new string(' ', Math.Clamp(width, 1, 16));
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].EndsWith("\r") ? indent + lines[i] : (lines[i].Length > 0 ? indent + lines[i] : lines[i]);
        }
        return string.Join("\n", lines);
    }

    /// 块反缩进:每行去掉最多两个行首空格
    public static string DedentBlock(string text, int width = 2)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            bool crlf = lines[i].EndsWith("\r");
            string core = crlf ? lines[i][..^1] : lines[i];
            int remove = 0;
            if (core.StartsWith('\t')) remove = 1;
            else while (remove < Math.Clamp(width, 1, 16) && remove < core.Length && core[remove] == ' ') remove++;
            core = core[remove..];
            lines[i] = crlf ? core + "\r" : core;
        }
        return string.Join("\n", lines);
    }
}
