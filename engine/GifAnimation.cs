using System;
using System.Collections.Generic;
using System.IO;

namespace MDViewerNative;

// Managed GIF frame decoder for animated images (item 34). WIC in this process only exposes frame 0
// through the shim path, so composed BGRA frames are produced here with explicit budgets:
// frame count, total pixels and file size are bounded; malformed data yields the frames decoded so far.
internal static class GifAnimation
{
    internal sealed record Frame(byte[] Bgra, int DelayMs);
    internal sealed record Result(int Width, int Height, List<Frame> Frames, int LoopCount, bool Truncated);
    public const int MaxFrames = 200;
    public const ulong MaxTotalPixels = 32UL * 1024 * 1024;   // all frames of one image, 128 MiB BGRA
    public const int MinDelayMs = 20;                          // browsers clamp <=10 ms to 100 ms; keep 20 ms floor

    public static bool IsGif(ReadOnlySpan<byte> data) => data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

    // Cheap pre-check: true when the file has at least two image descriptors.
    public static bool IsAnimated(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            return IsGif(data) && CountImages(data, 2) >= 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static int CountImages(byte[] d, int stopAt)
    {
        int count = 0;
        try
        {
            int p = 13; byte flags = d[10];
            if ((flags & 0x80) != 0) p += 3 << ((flags & 7) + 1);
            while (p < d.Length)
            {
                byte b = d[p++];
                if (b == 0x3B) break;
                if (b == 0x21) { p++; p = SkipSubBlocks(d, p); continue; }
                if (b != 0x2C) break;
                byte f = d[p + 8]; p += 9;
                if ((f & 0x80) != 0) p += 3 << ((f & 7) + 1);
                p++; p = SkipSubBlocks(d, p);
                if (++count >= stopAt) break;
            }
        }
        catch (IndexOutOfRangeException) { }
        return count;
    }

    static int SkipSubBlocks(byte[] d, int p)
    {
        while (true) { int n = d[p++]; if (n == 0) return p; p += n; }
    }

    public static Result Decode(byte[] d)
    {
        if (!IsGif(d)) throw new InvalidDataException("Not a GIF file.");
        int width = d[6] | d[7] << 8, height = d[8] | d[9] << 8;
        if (!ImageLimits.ValidDimensions((uint)width, (uint)height)) throw new InvalidDataException("GIF dimensions out of range.");
        byte flags = d[10]; int bg = d[11];
        int p = 13;
        uint[] gct = null;
        if ((flags & 0x80) != 0) { gct = ReadPalette(d, p, 1 << ((flags & 7) + 1)); p += gct.Length * 3; }
        var frames = new List<Frame>();
        var canvas = new uint[width * height];
        uint[] previous = null;
        int loop = 1; bool truncated = false;
        int disposal = 0, delay = 0, transparent = -1;
        ulong pixels = 0;
        try
        {
            while (p < d.Length)
            {
                byte b = d[p++];
                if (b == 0x3B) break;
                if (b == 0x21)
                {
                    byte label = d[p++];
                    if (label == 0xF9 && d[p] == 4)
                    {
                        byte pf = d[p + 1]; disposal = (pf >> 2) & 7; delay = (d[p + 2] | d[p + 3] << 8) * 10;
                        transparent = (pf & 1) != 0 ? d[p + 4] : -1;
                    }
                    else if (label == 0xFF && d[p] == 11 && System.Text.Encoding.ASCII.GetString(d, p + 1, 11) == "NETSCAPE2.0" && d[p + 12] == 3 && d[p + 13] == 1)
                        loop = d[p + 14] | d[p + 15] << 8;
                    p = SkipSubBlocks(d, p);
                    continue;
                }
                if (b != 0x2C) break;
                int fx = d[p] | d[p + 1] << 8, fy = d[p + 2] | d[p + 3] << 8, fw = d[p + 4] | d[p + 5] << 8, fh = d[p + 6] | d[p + 7] << 8;
                byte f = d[p + 8]; p += 9;
                uint[] palette = gct;
                if ((f & 0x80) != 0) { palette = ReadPalette(d, p, 1 << ((f & 7) + 1)); p += palette.Length * 3; }
                bool interlaced = (f & 0x40) != 0;
                int minCode = d[p++];
                int start = p; p = SkipSubBlocks(d, p);
                if (fw == 0 || fh == 0 || fx + fw > width || fy + fh > height || palette == null || minCode < 2 || minCode > 8) continue;   // skip broken frame
                if (frames.Count >= MaxFrames || pixels + (ulong)(width * height) > MaxTotalPixels) { truncated = true; break; }
                byte[] indices = Lzw(d, start, minCode, fw * fh);
                if (disposal == 3) previous = (uint[])canvas.Clone();
                Compose(canvas, width, indices, fx, fy, fw, fh, palette, transparent, interlaced);
                var bgra = new byte[width * height * 4];
                Buffer.BlockCopy(canvas, 0, bgra, 0, bgra.Length);
                frames.Add(new(bgra, delay < MinDelayMs ? 100 : delay));
                pixels += (ulong)(width * height);
                if (disposal == 2) ClearRect(canvas, width, fx, fy, fw, fh);
                else if (disposal == 3 && previous != null) canvas = previous;
                disposal = 0; delay = 0; transparent = -1;
            }
        }
        catch (IndexOutOfRangeException) { truncated = true; }
        catch (InvalidDataException) { truncated = true; }
        if (frames.Count == 0) throw new InvalidDataException("GIF has no decodable frame.");
        return new(width, height, frames, loop, truncated);
    }

    static uint[] ReadPalette(byte[] d, int p, int count)
    {
        var pal = new uint[count];
        for (int i = 0; i < count; i++) pal[i] = 0xFF000000u | (uint)d[p + i * 3] << 16 | (uint)d[p + i * 3 + 1] << 8 | d[p + i * 3 + 2];
        return pal;
    }

    static void ClearRect(uint[] canvas, int width, int x, int y, int w, int h)
    {
        for (int row = y; row < y + h; row++) Array.Clear(canvas, row * width + x, w);
    }

    static void Compose(uint[] canvas, int width, byte[] idx, int fx, int fy, int fw, int fh, uint[] palette, int transparent, bool interlaced)
    {
        int[] rowMap = null;
        if (interlaced)
        {
            rowMap = new int[fh]; int r = 0;
            foreach (var (startRow, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
                for (int row = startRow; row < fh; row += step) rowMap[r++] = row;
        }
        for (int row = 0; row < fh; row++)
        {
            int target = interlaced ? rowMap[row] : row;
            int dst = (fy + target) * width + fx, src = row * fw;
            for (int col = 0; col < fw; col++)
            {
                int i = idx[src + col];
                if (i == transparent || i >= palette.Length) continue;
                canvas[dst + col] = palette[i];
            }
        }
    }

    // LZW over GIF sub-blocks; output is exactly `count` indices (missing data leaves zeros).
    static byte[] Lzw(byte[] d, int p, int minCode, int count)
    {
        var output = new byte[count];
        int clear = 1 << minCode, eoi = clear + 1;
        var prefix = new short[4096]; var suffix = new byte[4096]; var stack = new byte[4097];
        int codeSize = minCode + 1, codeMask = (1 << codeSize) - 1, avail = clear + 2, old = -1;
        int bits = 0, nbits = 0, outPos = 0, blockLeft = 0, first = 0, sp = 0;
        for (int i = 0; i < clear; i++) { prefix[i] = 0; suffix[i] = (byte)i; }
        while (outPos < count)
        {
            while (nbits < codeSize)
            {
                if (blockLeft == 0) { blockLeft = d[p++]; if (blockLeft == 0) return output; }
                bits |= d[p++] << nbits; nbits += 8; blockLeft--;
            }
            int code = bits & codeMask; bits >>= codeSize; nbits -= codeSize;
            if (code == clear) { codeSize = minCode + 1; codeMask = (1 << codeSize) - 1; avail = clear + 2; old = -1; continue; }
            if (code == eoi) break;
            if (old == -1) { output[outPos++] = suffix[code]; old = code; first = code; continue; }
            int inCode = code;
            if (code >= avail) { stack[sp++] = (byte)first; code = old; }
            while (code >= clear) { stack[sp++] = suffix[code]; code = prefix[code]; }
            first = suffix[code]; stack[sp++] = (byte)first;
            while (sp > 0 && outPos < count) output[outPos++] = stack[--sp];
            sp = 0;
            if (avail < 4096)
            {
                prefix[avail] = (short)old; suffix[avail] = (byte)first; avail++;
                if (avail > codeMask && codeSize < 12) { codeSize++; codeMask = (1 << codeSize) - 1; }
            }
            old = inCode;
        }
        return output;
    }
}
