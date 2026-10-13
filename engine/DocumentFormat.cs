using System;
using System.Text;

namespace MDViewerNative;

internal enum TextEncoding { Auto, Utf8, Gbk, Utf16LittleEndian, Utf16BigEndian }
internal readonly record struct DocumentFormat(TextEncoding Encoding, bool Bom, string LineEnding)
{
    static DocumentFormat() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static DocumentFormat Utf8 => new(TextEncoding.Utf8, false, "\n");
    public string Description => Encoding switch
    {
        TextEncoding.Gbk => "GBK",
        TextEncoding.Utf16LittleEndian => "UTF-16 LE",
        TextEncoding.Utf16BigEndian => "UTF-16 BE",
        _ => Bom ? "UTF-8 BOM" : "UTF-8"
    };
    public DocumentFormat Validate()
    {
        if (!Enum.IsDefined(Encoding) || Encoding == TextEncoding.Auto || LineEnding is not ("\n" or "\r\n" or "\r"))
            throw new ArgumentException("Invalid document format.");
        return this;
    }
    public Encoding CreateEncoding() => Encoding switch
    {
        TextEncoding.Gbk => System.Text.Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        TextEncoding.Utf16LittleEndian => new UnicodeEncoding(false, Bom, true),
        TextEncoding.Utf16BigEndian => new UnicodeEncoding(true, Bom, true),
        _ => new UTF8Encoding(Bom, true)
    };
}
