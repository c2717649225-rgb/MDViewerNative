using System;

namespace MDViewerNative;

internal sealed record ImageSource(string Path, string Url, string Error)
{
    public string Key => Url ?? Path?.ToUpperInvariant();
    public static ImageSource Resolve(string source, string directory)
    {
        if (!DocumentLinks.TryResolve(source, directory, out var link, out string error)) return new(null, null, error);
        if (link.Kind == LinkKind.Web)
        {
            var uri = new Uri(link.Location);
            if (uri.UserInfo.Length > 0) return new(null, null, "图片地址不能包含用户名或密码");
            return new(null, new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri, null);
        }
        if (link.NetworkFile) return new(null, null, "网络共享图片不自动读取，请先保存到本地");
        if (link.Kind is LinkKind.ExternalFile or LinkKind.Markdown) return new(link.Location, null, null);
        return new(null, null, "不支持此图片地址类型");
    }
}

internal static class ImageLimits
{
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const long MaxDownloadBytes = 10L * 1024 * 1024;
    public const ulong MaxPixels = 16UL * 1024 * 1024;
    public static bool ValidDimensions(uint width, uint height) => width > 0 && height > 0
        && width <= 16384 && height <= 16384 && (ulong)width * height <= MaxPixels;
}
