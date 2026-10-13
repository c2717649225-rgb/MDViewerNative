using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MDViewerNative;

// Windows' native SVG implementation, with a deliberately bounded static profile.
internal static unsafe class SvgImage
{
    [DllImport("MDNative.dll")] private static extern int MD_DecodeSvg(byte* bytes, uint length, uint width, uint height, out nint bitmap);
    public static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return false;
        string start = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]);
        return start.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
    public static (uint Width, uint Height) Validate(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("SVG 超过 8 MiB 限制。");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 };
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, settings);
        double width = 640, height = 480; bool root = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            string name = reader.LocalName;
            if (!root)
            {
                if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不是 SVG 文档。");
                root = true;
                string box = reader.GetAttribute("viewBox");
                if (box != null)
                {
                    var values = box.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length == 4 && double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                        && double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && w > 0 && h > 0) { width = w; height = h; }
                }
                width = Length(reader.GetAttribute("width"), width); height = Length(reader.GetAttribute("height"), height);
            }
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("foreignObject", StringComparison.OrdinalIgnoreCase)
                || name.Equals("image", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("animate", StringComparison.OrdinalIgnoreCase) || name.Equals("set", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SVG 含不支持的脚本、嵌入对象、样式表或动画；仅支持安全静态 SVG。");
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    string key = reader.LocalName, value = reader.Value.Trim();
                    if (key.StartsWith("on", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SVG 事件属性被拒绝。");
                    if (key.Equals("href", StringComparison.OrdinalIgnoreCase) && !value.StartsWith('#')) throw new InvalidDataException("SVG 外部引用被拒绝。");
                    if (value.Contains("url", StringComparison.OrdinalIgnoreCase)
                        && !Regex.IsMatch(value, "^url\\(\\s*['\"]?#[A-Za-z0-9_.:-]+['\"]?\\s*\\)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        throw new InvalidDataException("SVG URL 只允许文档内引用。");
                    if (key.Equals("style", StringComparison.OrdinalIgnoreCase) && (value.Contains('\\') || value.Contains('@')))
                        throw new InvalidDataException("SVG 样式不允许转义引用或导入。");
                }
                reader.MoveToElement();
            }
        }
        if (!root || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidDataException("SVG 尺寸无效。");
        double scale = Math.Min(1, 4096 / Math.Max(width, height));
        uint outputWidth = (uint)Math.Max(1, Math.Ceiling(width * scale)), outputHeight = (uint)Math.Max(1, Math.Ceiling(height * scale));
        if ((ulong)outputWidth * outputHeight > 16UL * 1024 * 1024) throw new InvalidDataException("SVG 像素预算超限。");
        return (outputWidth, outputHeight);
    }
    private static double Length(string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimEnd().EndsWith('%')) return fallback;
        text = text.Trim(); if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }
    public static nint Decode(string path, out uint width, out uint height)
    {
        byte[] bytes = File.ReadAllBytes(path); (width, height) = Validate(bytes);
        fixed (byte* data = bytes)
        {
            int result = MD_DecodeSvg(data, (uint)bytes.Length, width, height, out nint bitmap);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return bitmap;
        }
    }
}
