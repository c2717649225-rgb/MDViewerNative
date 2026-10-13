using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace MDViewerNative;

internal static class HtmlResources
{
    private static readonly Regex Images = new("(<img\\b[^>]*?\\bsrc=\")([^\"]*)(\")", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    public static string Prepare(string html, string sourceDirectory, bool offline)
    {
        long embedded = 0;
        return Images.Replace(html, match =>
        {
            string source = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Length > 16 * 1024 * 1024) throw new InvalidDataException("嵌入资源超过大小限制。");
                return match.Value;
            }
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                if (offline) throw new InvalidOperationException("离线导出需要先将网络图片保存为本地资源：" + source);
                return match.Value;
            }
            if (source.StartsWith("\\\\", StringComparison.Ordinal) || source.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidOperationException("导出不会自动访问网络共享图片。");
            string path = uri?.IsFile == true ? uri.LocalPath : Path.GetFullPath(Path.Combine(sourceDirectory, Uri.UnescapeDataString(source.Replace('/', Path.DirectorySeparatorChar))));
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException("导出不会自动读取 UNC 图片。");
            string value;
            if (!offline) value = new Uri(path).AbsoluteUri;
            else
            {
                string mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".webp" => "image/webp",
                    ".svg" => "image/svg+xml",
                    _ => throw new InvalidDataException("不支持嵌入此资源类型：" + path)
                };
                long size = new FileInfo(path).Length;
                if (size > 8 * 1024 * 1024 || (embedded += size) > 32 * 1024 * 1024)
                    throw new InvalidDataException("离线图片超过单图 8 MiB / 总计 32 MiB 限制。");
                value = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            return match.Groups[1].Value + WebUtility.HtmlEncode(value) + match.Groups[3].Value;
        });
    }
}
