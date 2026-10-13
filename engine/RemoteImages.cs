using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDViewerNative;

// Constructing or resolving this service never sends a request. FetchAsync is called
// only by the explicit image action. The worker owns bytes/files, never WIC or HWNDs.
internal sealed class RemoteImages : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _timeoutMs;
    internal const long CacheBytes = 64L * 1024 * 1024;

    public RemoteImages(string directory, HttpMessageHandler handler = null, int timeoutMs = 15000)
    {
        _directory = directory;
        _timeoutMs = timeoutMs;
        _client = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        byte[] b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0D && b[3] == 0xB8);
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0))
            && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            && !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            Exception last = null;
            foreach (var address in addresses.Where(PublicAddress))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) { socket.Dispose(); last = ex; }
            }
            throw new HttpRequestException("不访问本机、私网或不可用的图片服务器。", last);
        }
    };

    internal static bool RasterHeader(ReadOnlySpan<byte> data) =>
        (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        || (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255)
        || (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        || (data.Length >= 2 && data[..2].SequenceEqual("BM"u8))
        || (data.Length >= 4 && (data[..4].SequenceEqual(new byte[] { 73, 73, 42, 0 }) || data[..4].SequenceEqual(new byte[] { 77, 77, 0, 42 })))
        || (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8));

    private static Uri Validate(string url)
    {
        var source = ImageSource.Resolve(url, "");
        if (source.Url == null) throw new IOException(source.Error ?? "图片地址必须使用 HTTP(S)。");
        return new Uri(source.Url);
    }

    public async Task<string> FetchAsync(string url, CancellationToken cancellation = default)
    {
        Uri uri = Validate(url);
        if (!await _gate.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new IOException("已有图片正在加载，请等待或取消。");
        string temporary = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_timeoutMs);
            var token = timeout.Token;
            Directory.CreateDirectory(_directory);
            TrimCache(ImageLimits.MaxDownloadBytes);
            string cached = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".image");
            var info = new FileInfo(cached);
            if (info.Exists && info.Length <= ImageLimits.MaxDownloadBytes && info.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24)) return cached;
            for (int redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 3 || response.Headers.Location == null) throw new IOException("图片重定向次数过多或缺少目标地址。");
                    Uri next = Validate(new Uri(uri, response.Headers.Location).AbsoluteUri);
                    if (uri.Scheme == "https" && next.Scheme != "https") throw new IOException("拒绝将 HTTPS 图片降级到 HTTP。");
                    uri = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > ImageLimits.MaxDownloadBytes) throw new IOException("网络图片超过 10 MiB 限额。");
                temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".part");
                byte[] header = new byte[16], buffer = new byte[65536];
                long total = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if (total < header.Length) Array.Copy(buffer, 0, header, (int)total, Math.Min(count, header.Length - (int)total));
                        total += count;
                        if (total > ImageLimits.MaxDownloadBytes) throw new IOException("网络图片超过 10 MiB 限额。");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                }
                if (!RasterHeader(header.AsSpan(0, (int)Math.Min(total, header.Length))))
                {
                    byte[] vector = await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false);
                    if (!SvgImage.LooksLikeSvg(vector)) throw new IOException("不是支持的图片格式。");
                    // XML validation only; native rendering remains on the UI thread. Policy rejections keep the IOException contract.
                    try { SvgImage.Validate(vector); }
                    catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NotSupportedException)
                    { throw new IOException("网络 SVG 未通过安全校验：" + ex.Message, ex); }
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, cached, true);
                temporary = null;
                return cached;
            }
        }
        finally
        {
            if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } }
            _gate.Release();
        }
    }


    // 缓存管理(35):目录级统计与清空,不触发任何网络请求;宿主负责避开进行中的下载
    public static (int Files, long Bytes) CacheUsage(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return (0, 0);
        int files = 0; long bytes = 0;
        foreach (var f in info.GetFiles()) if (f.Extension is ".image" or ".part") { files++; bytes += f.Length; }
        return (files, bytes);
    }

    public static int ClearCache(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return 0;
        int removed = 0;
        foreach (var f in info.GetFiles()) if (f.Extension is ".image" or ".part") { f.Delete(); removed++; }
        return removed;
    }

    private void TrimCache(long reserve)
    {
        var files = new DirectoryInfo(_directory).GetFiles().Where(f => f.Extension is ".image" or ".part").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long bytes = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (file.Extension == ".part" || file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24) || bytes + reserve > CacheBytes)
            { long length = file.Length; file.Delete(); bytes -= length; }
        }
        if (bytes + reserve > CacheBytes) throw new IOException("图片缓存空间不足。");
    }

    public void Dispose() => _client.Dispose();
}
