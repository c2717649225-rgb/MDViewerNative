using System;
using System.Linq;
using MDViewerNative;

// E11：打开外部文件前的确认。普通文件的提示保持不变；可执行类文件（系统认定的危险类型）改用更强的警告。
internal static class ExternalFilePromptTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "external file prompt") => check("ExternalFilePrompt/" + name, ok, detail);

        var plain = ExternalFilePrompt.For(@"C:\docs\report.pdf", _ => false);
        C("ordinary files keep the existing question", plain.Title == "打开外部文件" && plain.Flags == 0x124 && !plain.Executable
            && plain.Text == "此文件将交给系统默认程序打开。仅打开可信文件。\n" + @"C:\docs\report.pdf", plain.Text);

        var exe = ExternalFilePrompt.For(@"C:\docs\setup.BAT", path => path.EndsWith(".BAT", StringComparison.Ordinal));
        C("executable types get a warning that defaults to No", exe.Executable && exe.Flags == 0x134 && exe.Title != plain.Title
            && exe.Text.Contains(".bat") && exe.Text.Contains("直接运行") && exe.Text.EndsWith("\n" + @"C:\docs\setup.BAT"), exe.Title + " | " + exe.Text);

        string[] risky = { @"C:\x\a.exe", @"C:\x\a.bat", @"C:\x\a.cmd", @"C:\x\a.lnk", @"C:\x\a.EXE", @"C:\x\a.pdf.exe", @"C:\x\a.exe::$DATA", @"C:\x\a.pdf:x" };   // 带备用数据流的名字一律按可执行类警告(E26)
        string[] safe = { @"C:\x\a.pdf", @"C:\x\a.png", @"C:\x\a.zip", @"C:\x\noextension", @"C:\x\trailingdot." };
        var wrong = risky.Where(p => !ExternalFilePrompt.IsExecutableType(p)).Concat(safe.Where(ExternalFilePrompt.IsExecutableType)).ToList();
        C("the system list decides which types are executable", wrong.Count == 0, wrong.Count == 0 ? "AssocIsDangerous" : "wrong: " + string.Join(" ", wrong));
    }
}
