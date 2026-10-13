using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// 点击文档中指向本地非 Markdown 文件的链接时，交给系统打开前的确认文案（E11）。
// 普通文件沿用原来的提问；可执行类文件（.exe、.bat、.lnk、.hta 等）改用警告，默认“否”。
// 哪些类型算可执行由系统决定（AssocIsDangerous，与附件安全机制同一份清单，遵循组策略），不在这里维护扩展名列表。
internal static class ExternalFilePrompt
{
    internal readonly record struct Prompt(string Title, string Text, int Flags, bool Executable);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssocIsDangerous(string assoc);

    internal static bool IsExecutableType(string path)
    {
        // 文件名里的冒号只可能是 NTFS 备用数据流（a.exe::$DATA）：扩展名认不准，一律按可执行类警告（E26）
        if (Path.GetFileName(path).Contains(':')) return true;
        string extension = Path.GetExtension(path);
        return extension.Length > 1 && AssocIsDangerous(extension);
    }

    internal static Prompt For(string path, Func<string, bool> isExecutableType)
    {
        if (!isExecutableType(path))
            return new("打开外部文件", "此文件将交给系统默认程序打开。仅打开可信文件。\n" + path, 0x124 /*YESNO, question, default No*/, false);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return new("运行程序？",
            "此链接指向可执行类文件（" + extension + "），打开后会直接运行，可能修改或损坏你的电脑。\n只有完全信任这篇文档和该文件的来源时才继续。\n" + path,
            0x134 /*YESNO, warning, default No*/, true);
    }
}
