using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// Cache ownership is per document. Layout entries borrow these pointers; lightboxes AddRef.
internal sealed unsafe class DocumentImages : IDisposable
{
    internal readonly record struct Asset(nint Bitmap, uint Width, uint Height, string Error = null, Animation Animation = null);
    // 动画帧集:位图句柄由本缓存拥有,与 Asset.Bitmap(首帧)一起释放
    internal sealed class Animation
    {
        public nint[] Frames; public int[] DelaysMs; public int Index; public long NextTickMs; public bool Truncated;
        public nint Current => Frames[Index];
    }
    internal static bool AnimationEnabled = true;
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
    public string DirectoryPath { get; set; } = "";
    public int Count => _assets.Count;
    public ulong Pixels => _pixels;
    private readonly Dictionary<string, string> _approved = new(StringComparer.Ordinal);
    private ulong _pixels;
    private static ulong _globalPixels;
    internal static ulong GlobalPixels => _globalPixels;
    internal const ulong GlobalLimit = 32UL * 1024 * 1024;   // 像素数;BGRA 每像素 4 字节 = 128 MiB,与 ImageLimits.MaxPixels(16 Mpx = 64 MiB/文档)同口径
    private ImageSource Resolve(string source) => ImageSource.Resolve(source, DirectoryPath);

    public void Approve(string source, string localPath)
    {
        var resolved = Resolve(source);
        if (resolved.Url == null) throw new ArgumentException("Not a remote image.");
        _approved[resolved.Url] = localPath;
        if (_assets.TryGetValue(resolved.Key, out var old) && old.Bitmap == 0) _assets.Remove(resolved.Key);
    }

    public Asset Get(string source)
    {
        string key = Resolve(source).Key;
        return key != null && _assets.TryGetValue(key, out var asset) ? asset : default;
    }

    public string Message(string source)
    {
        var resolved = Resolve(source);
        if (resolved.Error != null) return resolved.Error;
        var asset = Get(source);
        if (asset.Error != null) return asset.Error;
        return resolved.Url != null ? "网络图片 · 点击加载" : "图片未加载";
    }

    public Asset Load(string source)
    {
        var resolved = Resolve(source);
        if (resolved.Key == null) return default;
        if (_assets.TryGetValue(resolved.Key, out var asset)) return asset;
        string path = resolved.Path;
        if (resolved.Url != null && !_approved.TryGetValue(resolved.Url, out path)) return default;
        // Obsidian 库(S76):同目录没有的本地图片,按库根(含 .obsidian)内文件名查找;库外只多几次目录存在检查。每个来源只查一次(结果进缓存)。
        if (resolved.Url == null && !File.Exists(path) && WikiLinks.FindInVault(path) is string found) path = found;
        uint width = 0, height = 0;
        nint wic = 0, bitmap = 0;
        string error = null;
        Animation animation = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) error = "图片文件不存在";
            else if (file.Length > ImageLimits.MaxFileBytes) error = "图片文件超过 32 MiB 限额";
            else
            {
                byte[] signature = new byte[Math.Min(1024, (int)file.Length)];
                using (var input = File.OpenRead(path)) input.ReadExactly(signature);
                wic = SvgImage.LooksLikeSvg(signature) ? SvgImage.Decode(path, out width, out height) : Wic.DecodeToBitmap(path, out width, out height);
                if (!ImageLimits.ValidDimensions(width, height)) error = "图片尺寸无效或超出限额";
                else if (_pixels + (ulong)width * height > ImageLimits.MaxPixels) error = "当前文档图片已达到 64 MiB 解码限额";
                else if (_globalPixels + (ulong)width * height > GlobalLimit) error = "全部文档图片已达到 128 MiB 解码预算，请释放其他标签缓存";
                else if (wic != 0)
                {
                    int hr = RenderResources.PCreateBmpWic(RenderResources.Rt, wic, 0, &bitmap);
                    if (hr < 0 && bitmap != 0) { Marshal.Release(bitmap); bitmap = 0; }
                }
                if (bitmap == 0 && error == null) error = "图片格式不受支持或文件损坏";
                if (bitmap != 0 && AnimationEnabled && GifAnimation.IsGif(signature) && GifAnimation.IsAnimated(path)) animation = DecodeAnimation(path, width, height);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or System.Xml.XmlException or NotSupportedException)
        {
            if (bitmap != 0) Marshal.Release(bitmap);
            bitmap = 0;
            error = "图片读取失败";
        }
        finally { if (wic != 0) Marshal.Release(wic); }
        if (bitmap != 0) { _pixels += (ulong)width * height; _globalPixels += (ulong)width * height; }
        asset = new(bitmap, width, height, error, animation);
        _assets.Add(resolved.Key, asset);
        return asset;
    }

    // 帧位图计入同一像素预算;预算不足时保留首帧静止(不报错,不回退到旧行为以外的状态)
    private Animation DecodeAnimation(string path, uint width, uint height)
    {
        GifAnimation.Result decoded;
        try { decoded = GifAnimation.Decode(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        if (decoded.Width != width || decoded.Height != height || decoded.Frames.Count < 2) return null;
        ulong perFrame = (ulong)width * height, need = perFrame * (ulong)decoded.Frames.Count;
        if (_pixels + need > ImageLimits.MaxPixels || _globalPixels + need > GlobalLimit) return null;
        var frames = new nint[decoded.Frames.Count]; var delays = new int[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            fixed (byte* pixels = decoded.Frames[i].Bgra)
            {
                int hr = RenderResources.CreateBitmapFromMemory(width, height, (nint)pixels, width * 4, out nint bmp);
                if (hr < 0 || bmp == 0) { for (int k = 0; k < i; k++) Marshal.Release(frames[k]); return null; }
                frames[i] = bmp;
            }
            delays[i] = decoded.Frames[i].DelayMs;
        }
        _pixels += need; _globalPixels += need;
        return new Animation { Frames = frames, DelaysMs = delays, Truncated = decoded.Truncated };
    }

    public bool HasAnimations
    {
        get { foreach (var a in _assets.Values) if (a.Animation != null) return true; return false; }
    }

    // 推进到期的动画;返回是否有帧变化。nowMs 为单调时钟
    public bool Advance(long nowMs)
    {
        bool changed = false;
        foreach (var a in _assets.Values)
        {
            var anim = a.Animation; if (anim == null) continue;
            if (anim.NextTickMs == 0) { anim.NextTickMs = nowMs + anim.DelaysMs[anim.Index]; continue; }
            if (nowMs < anim.NextTickMs) continue;
            anim.Index = (anim.Index + 1) % anim.Frames.Length;
            anim.NextTickMs = nowMs + anim.DelaysMs[anim.Index];
            changed = true;
        }
        return changed;
    }

    public int NextDueMs(long nowMs)
    {
        long best = long.MaxValue;
        foreach (var a in _assets.Values) if (a.Animation != null) best = Math.Min(best, a.Animation.NextTickMs == 0 ? nowMs : a.Animation.NextTickMs);
        return best == long.MaxValue ? -1 : (int)Math.Max(0, best - nowMs);
    }

    public nint CurrentFrame(string source)
    {
        var asset = Get(source);
        return asset.Animation != null ? asset.Animation.Current : asset.Bitmap;
    }

    public void ReleaseBitmaps()
    {
        foreach (var asset in _assets.Values)
        {
            if (asset.Bitmap != 0) Marshal.Release(asset.Bitmap);
            if (asset.Animation != null) foreach (nint frame in asset.Animation.Frames) Marshal.Release(frame);
        }
        _assets.Clear(); _globalPixels -= _pixels; _pixels = 0;
    }
    public void Clear()
    {
        ReleaseBitmaps();
        _approved.Clear();
    }

    public void Dispose() => Clear();
}
