// Obsidian 双链(S75,自用走查第四轮;docs/mcp-plan-s75.md 第 6 项)。
// [[页面]] [[页面|别名]] [[页面#标题]] [[#标题]] ![[嵌入]]:显示别名,没有别名时显示页面名(带标题时为“页面 › 标题”);
// 目标为 页面.md(末段无扩展名时补 .md)加标题锚点(与本文标题 id 同一 slug);块引用 #^id 不带锚点。
// ![[x.png]] 等图片扩展名为本地图片(| 后是尺寸,忽略),其余嵌入按链接处理。纯函数,不读文件;
// 同目录找不到时按库根目录查找由 FindInVault 负责:链接在打开时(Program.OpenDocumentLink),图片在首次加载时(DocumentImages.Load,S76)。
using System;
using System.IO;
using System.Linq;

namespace MDViewerNative;

internal static class WikiLinks
{
    private static readonly string[] ImageExtensions = { "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg", "avif" };

    public static bool TryParse(string s, int i, out int end, out Run run)
    {
        end = i; run = null;
        bool embed = s[i] == '!';
        int open = embed ? i + 1 : i;
        if (open + 1 >= s.Length || s[open] != '[' || s[open + 1] != '[') return false;
        if (i > 0 && s[i - 1] is '[' or ']') return false;   // 紧贴其他方括号([x][[y]]、[[[y]]])按 CommonMark 处理
        int start = open + 2, close = s.IndexOf("]]", start, StringComparison.Ordinal);
        if (close < 0 || close + 2 < s.Length && s[close + 2] == ']') return false;
        string inner = s[start..close];
        if (inner.Trim().Length == 0 || inner.IndexOfAny(new[] { '\n', '[', ']', LineBreaks.Hard }) >= 0) return false;
        int bar = inner.IndexOf('|');
        string target = (bar >= 0 ? inner[..bar] : inner).Trim();
        string alias = bar >= 0 ? inner[(bar + 1)..].Trim() : "";
        int hash = target.IndexOf('#');
        string page = (hash >= 0 ? target[..hash] : target).Trim();
        string heading = hash >= 0 ? target[(hash + 1)..].Trim() : "";
        bool block = heading.StartsWith('^');
        if (page.Length == 0 && (heading.Length == 0 || block)) return false;
        string extension = Extension(page);
        if (embed && ImageExtensions.Contains(extension))
        {
            run = new Run(Path.GetFileName(page), link: Escape(page), img: true);
            end = close + 2;
            return true;
        }
        string fragment = heading.Length > 0 && !block ? "#" + DocumentAnchors.Slug(heading) : "";
        string link = page.Length == 0 ? fragment : Escape(extension.Length > 0 ? page : page + ".md") + fragment;
        string shown = alias.Length > 0 ? alias : page.Length == 0 ? heading : heading.Length > 0 && !block ? page + " › " + heading : page;
        run = new Run(shown, link: link);
        end = close + 2;
        return true;
    }

    // 末段的扩展名(1–5 个字母数字);“v1.2 notes”这类带空格的不算。
    private static string Extension(string page)
    {
        string last = page[(page.LastIndexOf('/') + 1)..];
        int dot = last.LastIndexOf('.');
        if (dot <= 0 || dot == last.Length - 1) return "";
        string ext = last[(dot + 1)..];
        return ext.Length <= 5 && ext.All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : "";
    }

    private static string Escape(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    // 打开链接时:目标不存在,则向上找到含 .obsidian 的库根目录,按文件名查找(Obsidian 按最短路径解析,库内文件名通常唯一)。
    // 最多向上 12 层、遍历 20000 个文件,找不到返回 null。
    public static string FindInVault(string missingPath)
    {
        try
        {
            string name = Path.GetFileName(missingPath);
            if (string.IsNullOrEmpty(name)) return null;
            var dir = new DirectoryInfo(Path.GetDirectoryName(missingPath) ?? "");
            for (int up = 0; up < 12 && dir != null; up++, dir = dir.Parent)
            {
                if (!dir.Exists || !Directory.Exists(Path.Combine(dir.FullName, ".obsidian"))) continue;
                int seen = 0;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                foreach (string file in Directory.EnumerateFiles(dir.FullName, "*", options))
                {
                    if (++seen > 20000) return null;
                    if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) return file;
                }
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return null;
    }
}
