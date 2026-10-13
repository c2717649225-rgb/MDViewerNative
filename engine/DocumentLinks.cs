using System;
using System.IO;

namespace MDViewerNative;

internal enum LinkKind { Anchor, Markdown, ExternalFile, Web, Mail }
internal sealed record LinkTarget(LinkKind Kind, string Location, string Fragment)
{
    public bool NetworkFile => Kind is LinkKind.Markdown or LinkKind.ExternalFile && Location.StartsWith(@"\\");
    public string LaunchAddress => Kind == LinkKind.ExternalFile && Fragment.Length > 0
        ? new Uri(new Uri(Location).AbsoluteUri + "#" + Fragment).AbsoluteUri : Location;
}

// Resolution is pure: no file reads, network requests, shell launches or UI changes.
internal static class DocumentLinks
{
    public static bool TryDecode(string value, out string decoded)
    {
        decoded = "";
        if (value == null || value.Length > 32768) return false;
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%')
            {
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
                i += 2;
            }
        decoded = Uri.UnescapeDataString(value);
        foreach (char c in decoded) if (char.IsControl(c)) return false;
        return true;
    }

    public static bool TryResolve(string address, string directory, out LinkTarget target, out string error)
    {
        target = null;
        error = "链接地址无效。";
        if (address == null || address.Length > 32768) return false;
        foreach (char c in address) if (char.IsControl(c)) return false;
        address = address.Trim();
        if (address.StartsWith('#'))
        {
            if (!TryDecode(address[1..], out _)) return false;
            target = new(LinkKind.Anchor, "", address[1..]);
            return true;
        }
        if (address.Length == 0) return false;
        if (address.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
        bool drive = address.Length >= 3 && char.IsAsciiLetter(address[0]) && address[1] == ':' && address[2] is '\\' or '/';
        int colon = address.IndexOf(':');
        if (!drive && colon >= 0)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri)) return false;
            if (uri.Scheme is "http" or "https" or "mailto")
            {
                if (uri.Scheme == "mailto")
                {
                    string recipient = address[(uri.Scheme.Length + 1)..].Split('?', '#')[0];
                    if (!TryDecode(recipient, out recipient) || string.IsNullOrWhiteSpace(recipient)) return false;
                }
                else if (uri.Host.Length == 0) return false;
                target = new(uri.Scheme == "mailto" ? LinkKind.Mail : LinkKind.Web, uri.AbsoluteUri, "");
                return true;
            }
            if (uri.Scheme != "file")
            {
                error = "为安全起见，不打开此链接协议：" + uri.Scheme;
                return false;
            }
            // Uri.LocalPath decodes once; never feed it back through percent decoding.
            return Local(uri.LocalPath, uri.Fragment.Length > 0 ? uri.Fragment[1..] : "", directory, out target, out error);
        }
        int hash = address.IndexOf('#');
        string path = hash >= 0 ? address[..hash] : address;
        string fragment = hash >= 0 ? address[(hash + 1)..] : "";
        if (!TryDecode(path, out path)) return false;
        return Local(path, fragment, directory, out target, out error);
    }

    private static bool Local(string path, string fragment, string directory, out LinkTarget target, out string error)
    {
        target = null;
        error = "本地链接地址无效。";
        if (!TryDecode(fragment, out _)) return false;
        foreach (char c in path) if (char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*') return false;
        int colon = path.IndexOf(':');
        if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(path[0]) || path.Length < 3 || path[2] is not ('\\' or '/') || path.IndexOf(':', 2) >= 0)) return false;
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) return false;
        if (!Path.IsPathFullyQualified(path) && string.IsNullOrEmpty(directory))
        {
            error = "请先保存文档，再打开相对路径链接。";
            return false;
        }
        try
        {
            string full = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, directory);
            string extension = Path.GetExtension(full);
            LinkKind kind = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ? LinkKind.Markdown : LinkKind.ExternalFile;
            target = new(kind, full, fragment);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }
}
