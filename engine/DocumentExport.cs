using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MDViewerNative;

// Export owns a frozen draft and target version; it never saves the source document.
internal sealed record DocumentExport(string Path, string Html, FileVersion ExpectedVersion)
{
    public static DocumentExport Prepare(DocumentSession session, string text, bool dark, string destination = null, bool offline = false)
    {
        if (session.Path.Length == 0 && destination == null) throw new InvalidOperationException("请选择导出位置。");
        string target = System.IO.Path.GetFullPath(destination ?? System.IO.Path.ChangeExtension(session.Path, ".html"));
        if (string.Equals(target, session.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (destination != null) throw new InvalidOperationException("不能用 HTML 导出覆盖 Markdown 源文件。");
            target = System.IO.Path.ChangeExtension(session.Path, ".export.html");
        }
        string directory = session.Path.Length == 0 ? Environment.CurrentDirectory : System.IO.Path.GetDirectoryName(session.Path);
        FileVersion expected = AtomicFile.Observe(target);
        string cssPath = System.IO.Path.Combine(directory, "custom.css");
        string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";
        if (offline && Regex.IsMatch(css, "@import|url\\s*\\(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("离线导出不包含 custom.css 的外部引用，请先内联或移除它们。");
        string html = HtmlExport.Build(MdParser.Parse(text), session.Name, dark, css);
        if (offline || destination != null) html = HtmlResources.Prepare(html, directory, offline);
        return new(target, html, expected);
    }
    public bool TryWrite(Func<string, bool> confirmOverwrite)
    {
        if (ExpectedVersion.Exists && !confirmOverwrite(Path)) return false;
        AtomicFile.Write(Path, new UTF8Encoding(false, true).GetBytes(Html), ExpectedVersion);
        return true;
    }
}
