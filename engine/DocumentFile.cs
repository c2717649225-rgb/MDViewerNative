using System;
using System.IO;
using System.Text;

namespace MDViewerNative;

internal sealed record DocumentContent(string Text, bool Gbk, bool Bom, string LineEnding, FileVersion Version = null,
    TextEncoding Encoding = TextEncoding.Auto)
{
    public DocumentFormat Format => new(Encoding == TextEncoding.Auto ? (Gbk ? TextEncoding.Gbk : TextEncoding.Utf8) : Encoding, Bom, LineEnding);
}

internal static class DocumentFile
{
    static DocumentFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static DocumentContent Read(string path, TextEncoding forced = TextEncoding.Auto)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool utf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        bool littleBom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        bool bigBom = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF;
        TextEncoding kind = forced;
        if (kind == TextEncoding.Auto) kind = littleBom ? TextEncoding.Utf16LittleEndian : bigBom ? TextEncoding.Utf16BigEndian : TextEncoding.Utf8;
        bool bom = kind == TextEncoding.Utf8 ? utf8Bom : kind == TextEncoding.Utf16LittleEndian ? littleBom : kind == TextEncoding.Utf16BigEndian && bigBom;
        int skip = bom ? (kind == TextEncoding.Utf8 ? 3 : 2) : 0;
        string text;
        try { text = new DocumentFormat(kind, bom, "\n").CreateEncoding().GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) when (forced == TextEncoding.Auto && !bom && kind == TextEncoding.Utf8)
        {
            kind = TextEncoding.Gbk;
            text = new DocumentFormat(kind, false, "\n").CreateEncoding().GetString(bytes);
        }
        string ending = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : "\n";
        return new(text, kind == TextEncoding.Gbk, bom, ending, FileVersion.Of(bytes), kind);
    }

    public static byte[] Encode(DocumentContent content)
    {
        var format = content.Format.Validate();
        var encoding = format.CreateEncoding();
        byte[] bytes = encoding.GetBytes(content.Text), prefix = encoding.GetPreamble();
        if (prefix.Length == 0) return bytes;
        byte[] result = new byte[prefix.Length + bytes.Length];
        prefix.CopyTo(result, 0); bytes.CopyTo(result, prefix.Length);
        return result;
    }

    public static FileVersion Write(string path, DocumentContent content, FileVersion expected = null) =>
        AtomicFile.Write(path, Encode(content), expected ?? content.Version);

    public static string NormalizeLines(string text, string lineEnding) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", lineEnding);
    public static string ToEditorText(string text) => NormalizeLines(text, "\r\n");
    public static string FromEditorText(string text, string lineEnding) => NormalizeLines(text, lineEnding);
}
