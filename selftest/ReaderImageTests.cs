using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MDViewerNative;

internal static class ReaderImageTests
{
    private sealed class Handler : HttpMessageHandler
    {
        internal int Count;
        internal bool PrivateHeaders;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            PrivateHeaders |= request.Headers.Referrer != null || request.Headers.Authorization != null
                || request.Headers.Contains("Cookie") || request.Headers.Contains("Proxy-Authorization");
            return Reply(request, token);
        }
    }
    private sealed class Unseekable : MemoryStream
    {
        public Unseekable(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }
    private static byte[] Bitmap()
    {
        byte[] data = new byte[54 + 120 * 60 * 4];
        using var stream = new MemoryStream(data);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42); writer.Write(data.Length); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(120); writer.Write(60); writer.Write((ushort)1); writer.Write((ushort)32);
        return data;
    }
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name).GetValue(obj);
    private static object Entry(DocView view) => typeof(DocView).GetMethod("GetEnt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { 0 });
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("ReaderImages/" + name, ok, "native resource ownership and explicit network policy");
        // 自用走查(S73):HelloGitHub 头部 logo 无 alt,占位曾为“[ · 网络图片 · 点击加载]”。
        check("ReaderImages/placeholder without alt has no dangling separator", DocView.ImagePlaceholder("", "网络图片 · 点击加载") == "[网络图片 · 点击加载]", DocView.ImagePlaceholder("", "网络图片 · 点击加载"));
        check("ReaderImages/placeholder keeps alt", DocView.ImagePlaceholder("封面", "图片未加载") == "[封面 · 图片未加载]", DocView.ImagePlaceholder("封面", "图片未加载"));
        string dir = Path.GetFullPath("../work/reader-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string image = Path.Combine(dir, "test image.bmp");
            File.WriteAllBytes(image, Bitmap());
            C("encoded local path", ImageSource.Resolve("test%20image.bmp", dir).Path == image);
            C("file URI decoded once", ImageSource.Resolve(new Uri(image).AbsoluteUri, dir).Path == image);
            C("remote URL path case retained", ImageSource.Resolve("https://example.test/A.png", dir).Key != ImageSource.Resolve("https://example.test/a.png", dir).Key);
            C("credentials rejected", ImageSource.Resolve("https://user:secret@example.test/a", dir).Error != null);
            C("network share not opened by layout", ImageSource.Resolve(@"\\server\share\a.png", dir).Error != null);
            C("data and executable protocols rejected", ImageSource.Resolve("data:image/svg+xml,abc", dir).Error != null && ImageSource.Resolve("javascript:alert(1)", dir).Error != null);
            C("dimensions bounded with wide multiplication", !ImageLimits.ValidDimensions(0, 10) && !ImageLimits.ValidDimensions(uint.MaxValue, uint.MaxValue) && !ImageLimits.ValidDimensions(16385, 1) && ImageLimits.ValidDimensions(4096, 4096));
            using (var assets = new DocumentImages { DirectoryPath = dir })
            {
                var one = assets.Load("test%20image.bmp");
                C("native image decoded", one.Bitmap != 0 && one.Width == 120 && one.Height == 60);
                C("canonical local aliases share cache", assets.Load(new Uri(image).AbsoluteUri).Bitmap == one.Bitmap && assets.Count == 1);
                C("remote stays unloaded until explicit approval", assets.Load("https://example.test/a.bmp").Bitmap == 0 && assets.Count == 1);
                assets.Approve("https://example.test/a.bmp", image);
                C("approved local cache decoded", assets.Load("https://example.test/a.bmp").Bitmap != 0);
                assets.Clear();
                C("clearing document drops approvals and assets", assets.Count == 0 && assets.Load("https://example.test/a.bmp").Bitmap == 0);
            }
            // Obsidian 库(S76,S75 遗留):同目录没有的本地图片按库根(含 .obsidian)内文件名查找,![[图片]] 与普通图片同样适用;库外不查。
            foreach (bool vault in new[] { true, false })
            {
                string root = Path.Combine(dir, vault ? "vault" : "plain"), note = Path.Combine(root, "notes", "deep");
                Directory.CreateDirectory(note); Directory.CreateDirectory(Path.Combine(root, "attachments"));
                if (vault) Directory.CreateDirectory(Path.Combine(root, ".obsidian"));
                File.WriteAllBytes(Path.Combine(root, "attachments", "Pasted image.bmp"), Bitmap());
                using var assets = new DocumentImages { DirectoryPath = note };
                bool loaded = assets.Load("Pasted%20image.bmp").Bitmap != 0;
                check(vault ? "ReaderImages/vault attachment found by name" : "ReaderImages/no vault lookup outside a vault", loaded == vault, assets.Message("Pasted%20image.bmp"));
            }
            using (var view = new DocView { DocumentDirectory = dir })
            {
                view.Load("- # title\n\n  ![alt](test%20image.bmp)\n\n  | head | value |\n  | --- | --- |\n  | [link](https://example.test/target) | second |\n");
                view.OnWidth(596);
                var parent = Entry(view);
                var children = Field<IList>(parent, "SubRich");
                int picture = Enumerable.Range(0, children.Count).Single(i => children[i] != null && Field<nint>(children[i], "Bmp") != 0);
                int table = Enumerable.Range(0, children.Count).Single(i => children[i] != null && Field<nint[]>(children[i], "Cells") != null);
                C("nested image uses bitmap not alt-only fallback", Field<nint>(children[picture], "Bmp") != 0 && Field<float>(children[picture], "ImgH") == 60);
                C("nested table has all native cell layouts", Field<nint[]>(children[table], "Cells").Length == 4);
                string text = view.SearchText();
                view.SelectRange(0, view.TotalChars);
                C("rich children preserve logical copy/search domain", view.CopyText() == text && text.Contains("alt") && text.Contains("link | second"));
                float tableY = view.YAtOffset(0) + Field<System.Collections.Generic.List<float>>(parent, "SubYs")[table];
                float x = view.ColX + 26 + Field<System.Collections.Generic.List<float>>(parent, "SubXs")[table];
                NativeCell(view, children[table], x, tableY, 1, 0, "https://example.test/target", C);
                C("table empty margin does not launch link", view.LinkAt(x + 450, tableY + Field<float>(children[table], "HeadH") + 10) == null);
                for (int i = 0; i < 12; i++) view.OnWidth(i % 2 == 0 ? 360 : 596);
                C("resize rebuild preserves image ownership", view.GetImageBmp("test%20image.bmp") != 0 && view.CopyText() == text);
                view.Load("| heading | other |\n| --- | --- |\n| [go](https://example.test/top) | tail |\n");
                var root = Entry(view);
                NativeCell(view, root, view.ColX, view.YAtOffset(0), 1, 0, "https://example.test/top", C);
                view.Load("![remote](https://example.test/cached.bmp)\n");
                C("remote document layout has no bitmap", view.GetImageBmp("https://example.test/cached.bmp") == 0);
                view.ApproveImage("https://example.test/cached.bmp", image);
                C("approval reflows existing document", view.GetImageBmp("https://example.test/cached.bmp") != 0 && view.SearchText() == "remote");
                view.Load("before ![inline](test%20image.bmp) after\n");
                C("inline image source recognized without shell dispatch", view.IsInlineImage("test%20image.bmp"));
                view.LoadImageForViewing("test%20image.bmp");
                C("inline image opens through owned bitmap cache", view.GetImageBmp("test%20image.bmp") != 0);
                view.Load("before [![badge](https://example.test/badge.png)](https://example.test/project) after\n");
                var badge = Entry(view);
                var links = Field<System.Collections.Generic.List<(int s, int l, string url)>>(badge, "Links");
                C("linked inline image retains outer destination", links.Any(l => l.url == "https://example.test/project"));
            }
            Network(dir, C).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(dir, true); }
    }
    private static unsafe void NativeCell(DocView view, object entry, float x, float y, int row, int col, string url, Action<string, bool> check)
    {
        int columns = Field<float[]>(entry, "ColWs").Length;
        nint layout = Field<nint[]>(entry, "Cells")[row * columns + col];
        if (row > 0) y += Field<float>(entry, "HeadH");
        for (int r = 1; r < row; r++) y += Field<float[]>(entry, "RowHs")[r - 1];
        for (int c = 0; c < col; c++) x += Field<float[]>(entry, "ColWs")[c];
        float px = 0, py = 0;
        DWRITE_HIT_TEST_METRICS metrics = default;
        var hit = (delegate* unmanaged[Stdcall]<nint, uint, int, float*, float*, DWRITE_HIT_TEST_METRICS*, int>)(*(nint**)layout)[65];
        hit(layout, 1, 0, &px, &py, &metrics);
        float mx = x + 8 + px + 1, my = y + 7 + py + metrics.height / 2;
        check("cell link " + url, view.LinkAt(mx, my) == url);
        var pos = view.PosAtText(mx, my);
        check("cell position belongs to logical text " + url, pos.block >= 0 && pos.pos >= 0);
    }
    private static async Task Network(string dir, Action<string, bool> C)
    {
        HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        async Task<bool> Fails<T>(Func<Task> action) where T : Exception { try { await action(); return false; } catch (T) { return true; } }
        var handler = new Handler { Reply = (r, t) => Task.FromResult(Ok(Bitmap())) };
        string cache = Path.Combine(dir, "cache");
        using (var loader = new RemoteImages(cache, handler))
        {
            C("constructing loader does not request", handler.Count == 0);
            string file = await loader.FetchAsync("https://example.test/A.bmp");
            C("explicit request writes bounded cache", File.Exists(file) && handler.Count == 1 && new FileInfo(file).Length == Bitmap().Length);
            C("request has no credentials cookies or referer", !handler.PrivateHeaders);
            C("cache reuse needs no second request", await loader.FetchAsync("https://example.test/A.bmp") == file && handler.Count == 1);
            await loader.FetchAsync("https://example.test/a.bmp");
            C("case-sensitive paths have distinct cache entries", handler.Count == 2);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
            await loader.FetchAsync("https://example.test/A.bmp");
            C("expired cache refetched only on explicit action", handler.Count == 3);
            string pressure = Path.Combine(cache, "pressure.image");
            using (var stream = File.Create(pressure)) stream.SetLength(RemoteImages.CacheBytes);
            await loader.FetchAsync("https://example.test/pressure.bmp");
            C("cache bytes remain bounded", new DirectoryInfo(cache).GetFiles().Sum(f => f.Length) <= RemoteImages.CacheBytes);
            int redirectStart = handler.Count;
            handler.Reply = (r, t) => Task.FromResult(r.RequestUri.AbsolutePath == "/redirect" ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/final", UriKind.Relative) } } : Ok(Bitmap()));
            await loader.FetchAsync("https://example.test/redirect");
            C("safe relative redirect supported", handler.Count == redirectStart + 2);
            handler.Reply = (r, t) => { var response = Ok(new byte[] { 1 }); response.Content.Headers.ContentLength = ImageLimits.MaxDownloadBytes + 1; return Task.FromResult(response); };
            C("oversized content length rejected", await Fails<IOException>(() => loader.FetchAsync("https://example.test/large")));
            handler.Reply = (r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(new byte[(int)ImageLimits.MaxDownloadBytes + 1])) });
            C("unannounced streaming size bounded", await Fails<IOException>(() => loader.FetchAsync("https://example.test/chunked")));
            handler.Reply = (r, t) => Task.FromResult(Ok("<svg><script>evil()</script></svg>"u8.ToArray()));
            C("SVG and executable content never cached", await Fails<IOException>(() => loader.FetchAsync("https://example.test/svg")));
            handler.Reply = (r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.test/insecure") } });
            C("HTTPS downgrade rejected", await Fails<IOException>(() => loader.FetchAsync("https://example.test/downgrade")));
            handler.Reply = (r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.test/loop") } });
            int before = handler.Count;
            C("redirect loop bounded", await Fails<IOException>(() => loader.FetchAsync("https://example.test/start")) && handler.Count - before == 4);
            C("failure removes temporary files", Directory.GetFiles(cache, "*.part").Length == 0);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Reply = async (r, t) => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, t); return Ok(Bitmap()); };
            using var cancel = new CancellationTokenSource();
            var pending = loader.FetchAsync("https://example.test/slow", cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            C("concurrency bounded to one request", await Fails<IOException>(() => loader.FetchAsync("https://example.test/second")));
            cancel.Cancel();
            C("explicit cancellation completes", await Fails<OperationCanceledException>(() => pending));
        }
        var slow = new Handler { Reply = async (r, t) => { await Task.Delay(Timeout.Infinite, t); return Ok(Bitmap()); } };
        using (var loader = new RemoteImages(cache, slow, 50))
            C("total request timeout enforced", await Fails<OperationCanceledException>(() => loader.FetchAsync("https://example.test/timeout")));
        foreach (string ip in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.0.1", "169.254.169.254", "100.64.0.1", "::1", "fe80::1", "fc00::1", "::ffff:127.0.0.1" })
            C("private endpoint blocked " + ip, !RemoteImages.PublicAddress(IPAddress.Parse(ip)));
        C("public address classification", RemoteImages.PublicAddress(IPAddress.Parse("8.8.8.8")) && RemoteImages.PublicAddress(IPAddress.Parse("2001:4860:4860::8888")));
    }
}
