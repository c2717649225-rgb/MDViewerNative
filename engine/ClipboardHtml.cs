using System.Text;

namespace MDViewerNative;

// Windows "HTML Format" clipboard payload: byte offsets over the UTF-8 encoding, fixed-width header.
internal static class ClipboardHtml
{
    private const string Pre = "<html>\r\n<body>\r\n<!--StartFragment-->";
    private const string Post = "<!--EndFragment-->\r\n</body>\r\n</html>";

    public static byte[] Wrap(string fragment)
    {
        static string Header(int sh, int eh, int sf, int ef) =>
            "Version:0.9\r\nStartHTML:" + sh.ToString("D10") + "\r\nEndHTML:" + eh.ToString("D10")
            + "\r\nStartFragment:" + sf.ToString("D10") + "\r\nEndFragment:" + ef.ToString("D10") + "\r\n";
        int headerLen = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        int preLen = Encoding.UTF8.GetByteCount(Pre), fragLen = Encoding.UTF8.GetByteCount(fragment ?? ""), postLen = Encoding.UTF8.GetByteCount(Post);
        int startFragment = headerLen + preLen, endFragment = startFragment + fragLen, endHtml = endFragment + postLen;
        string payload = Header(headerLen, endHtml, startFragment, endFragment) + Pre + (fragment ?? "") + Post;
        byte[] bytes = new byte[endHtml + 1];   // trailing NUL for the global memory block
        Encoding.UTF8.GetBytes(payload, 0, payload.Length, bytes, 0);
        return bytes;
    }
}
