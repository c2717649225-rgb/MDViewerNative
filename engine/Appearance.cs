namespace MDViewerNative;

internal enum ChromeStyle { Classic = 0, Desk = 1 }

// U2：界面样式（ADR-0003 第 1 条的内部开关）与纸张选择。只存值、只做决策，不碰窗口与画刷。
// 开关只在启动时由配置决定；纸张可在运行中切换（Ctrl+D、跟随系统、外观面板）。
internal sealed class Appearance
{
    public ChromeStyle Chrome { get; set; } = ChromeStyle.Desk;   // U12：默认 desk；chrome=classic 切回老界面
    public bool Desk => Chrome == ChromeStyle.Desk;

    // 用户选择的纸张；Auto 表示跟随系统。
    public Paper Paper { get; set; } = Paper.Auto;

    // Ctrl+D 从灯下切回时回到的浅色纸张（本次运行内记忆，不持久化）。
    public Paper LastLight { get; private set; } = Paper.Xuan;

    // 外观菜单 / 面板选纸（U6a）：选中浅色纸张时一并记为 Ctrl+D 的回程。
    public void Choose(Paper paper)
    {
        Paper = paper;
        if (paper is Paper.Xuan or Paper.Celadon or Paper.White) LastLight = paper;
    }

    public PaperColors Colors(bool systemDark) => PaperPalette.Get(Paper, systemDark);

    // 启动时的纸张：有 paper= 用它；否则按老 theme= 推导；两者都没有则跟随系统（ADR-0003 第 4 条）。
    public void LoadPaper(Paper? configured, bool? legacyThemeDark) =>
        Paper = configured ?? PaperPalette.FromLegacy(legacyThemeDark);

    // Ctrl+D：当前浅色纸张 ↔ 灯下（规格 §11）。返回切换后的纸张。
    public Paper ToggleDark(bool systemDark)
    {
        Paper current = PaperPalette.Resolve(Paper, systemDark);
        if (current == Paper.Lamp) Paper = LastLight;
        else { LastLight = current; Paper = Paper.Lamp; }
        return Paper;
    }

    public static bool? ParseChrome(string token) => token switch { "desk" => true, "classic" => false, _ => null };
    public static string ChromeToken(bool desk) => desk ? "desk" : "classic";
}
