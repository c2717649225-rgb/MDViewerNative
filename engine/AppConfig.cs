using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MDViewerNative;

// config.txt 中读到的值；null 表示没有这一行（或这一行的值无效），调用方保留原值。
internal sealed class AppConfigValues
{
    public bool? ThemeDark;   // theme=dark 为 true，其他值为 false
    public bool? Toc;
    public int? Hints;        // 只接受 0..3
    public bool? Serif;
    public bool? Grain;       // grain=0 关纸纹；缺行视为开（U2b 纸纹的开关）
    public bool? Zen;
    public float? Font;       // 已钳制到 12..28；NaN、∞ 丢弃
    public Paper? Paper;      // paper=，v3 纸张（U2）；无效值为 null
    public bool? ChromeDesk;  // chrome=desk 为 true、classic 为 false（ADR-0003 的内部开关）
    public RECT? Bounds;      // win=，只保留最后一个有效值
    public bool Maximized;    // 与 Bounds 同一行
    public readonly List<string> UnknownLines = new();   // 不认识的键所在的整行，原样保留（E9）
    public readonly List<string> OfflineTabs = new();    // 所在的盘或共享暂时不在的标签：不恢复，保存时保留（E10）
    public readonly HashSet<string> UnavailablePaths = new(StringComparer.OrdinalIgnoreCase);   // 本次读取中判定为暂时不在的全部路径
}

// config.txt（宿主的窗口和会话状态，无版本号的 key=value 行）的解析与格式化（E8）。
// 纯逻辑：不碰窗口和全局状态，文件是否存在由调用方注入。
// 语义与 E8 之前 Program.LoadConfig/SaveConfig 的循环逐条一致：同一键出现多次时后者覆盖前者；
// 无效值整行跳过；tabs、recentN 追加到调用方的列表，按路径不区分大小写去重（recent 自 E24 起与 RecentList 一致）。
// 不认识的键（例如新版本加的设置）整行原样保留，写回时放在最后（E9）；没有键名的行仍然丢弃。
// 传入 rootExists 时，所在的盘或共享暂时不在的最近文件照样保留，这样的标签放进 OfflineTabs（E10）；根还在而文件不在时照旧删除。
internal static class AppConfig
{
    internal static AppConfigValues Parse(IEnumerable<string> lines, Func<string, bool> fileExists, List<string> sessionTabs, List<string> recent,
        Func<string, bool> rootExists = null)
    {
        var values = new AppConfigValues();
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // 所在的根（盘符或 \\server\share）不存在时视为暂时不在，不再逐个探测文件：离线共享每次探测都要等超时。
        bool Unavailable(string path)
        {
            if (rootExists == null || !Path.IsPathFullyQualified(path)) return false;
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            if (!roots.TryGetValue(root, out bool present)) roots[root] = present = rootExists(root);
            if (!present) values.UnavailablePaths.Add(path);
            return !present;
        }
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq], v = line[(eq + 1)..];
            if (!IsKnownKey(k)) { values.UnknownLines.Add(line); continue; }
            if (k == "theme") values.ThemeDark = v == "dark";
            else if (k == "toc") values.Toc = v == "1";
            else if (k == "hints" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hv) && hv >= 0 && hv <= 3) values.Hints = hv;
            else if (k == "serif") values.Serif = v == "1";
            else if (k == "grain") values.Grain = v == "1";
            else if (k == "zen") values.Zen = v == "1";
            else if (k == "paper") { if (PaperPalette.Parse(v) is Paper paper) values.Paper = paper; }
            else if (k == "chrome") { if (Appearance.ParseChrome(v) is bool desk) values.ChromeDesk = desk; }
            else if (k == "font" && float.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float fs) && float.IsFinite(fs)) values.Font = Math.Clamp(fs, 12f, 28f);
            else if (k == "win" && WindowGeometry.TryParseBounds(v, out var wb, out var wm))
            {
                values.Bounds = wb;
                values.Maximized = wm;
            }
            else if (k == "tabs")
            {
                foreach (string tp in v.Split('|'))
                {
                    if (tp.Length == 0 || sessionTabs.Contains(tp, StringComparer.OrdinalIgnoreCase)
                        || values.OfflineTabs.Contains(tp, StringComparer.OrdinalIgnoreCase)) continue;
                    if (Unavailable(tp)) values.OfflineTabs.Add(tp);
                    else if (fileExists(tp)) sessionTabs.Add(tp);
                }
            }
            else if (IsRecentKey(k) && v.Length > 0 && !recent.Contains(v, StringComparer.OrdinalIgnoreCase) && (Unavailable(v) || fileExists(v))) recent.Add(v);
        }
        return values;
    }

    // 键名区分大小写，与解析分支一致；recentN 归本版本管理（写回时重新编号）。
    static bool IsKnownKey(string k) =>
        k is "theme" or "toc" or "hints" or "serif" or "grain" or "zen" or "font" or "win" or "tabs" or "paper" or "chrome" || IsRecentKey(k);

    // 只认 recent 加数字：其他以 recent 开头的键（例如新版本的 recentFolder）按不认识的键原样保留（E24）。
    static bool IsRecentKey(string k) =>
        k.Length > 6 && k.StartsWith("recent", StringComparison.Ordinal) && k.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0;

    // 保存时的标签：已打开的标签在前，读取时暂时不在、本次也没打开的标签追加在后（E10）。
    internal static List<string> MergeSessionTabs(IEnumerable<string> openPaths, IEnumerable<string> offlineTabs)
    {
        var tabs = openPaths.ToList();
        foreach (string path in offlineTabs)
            if (!tabs.Contains(path, StringComparer.OrdinalIgnoreCase)) tabs.Add(path);
        return tabs;
    }

    // 本次会话中重新打开（或另存到）的离线标签此后按普通标签对待：关掉就不再恢复（E25）。
    internal static void ForgetOffline(List<string> offlineTabs, string path) =>
        offlineTabs.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    // windowBounds 为 null 时不写 win 行（窗口尚未创建或取不到矩形）。paper、chrome 为 null 时不写（U2）。
    internal static string Format(bool dark, bool toc, int hints, bool serif, bool zen, float font, string windowBounds,
        IReadOnlyList<string> recent, int recentLimit, IEnumerable<string> tabs, IReadOnlyList<string> unknownLines = null,
        string paper = null, string chrome = null, bool? grain = null)
    {
        var sb = new StringBuilder();
        sb.Append("theme=").Append(dark ? "dark" : "light").Append('\n');
        sb.Append("toc=").Append(toc ? "1" : "0").Append('\n');
        sb.Append("hints=").Append(hints).Append('\n');
        sb.Append("serif=").Append(serif ? 1 : 0).Append('\n');
        sb.Append("zen=").Append(zen ? 1 : 0).Append('\n');
        sb.Append("font=").Append(font.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');   // 与区域设置无关(E22)
        if (windowBounds != null) sb.Append("win=").Append(windowBounds).Append('\n');
        for (int i = 0; i < recent.Count && i < recentLimit; i++)
            sb.Append("recent").Append(i + 1).Append('=').Append(recent[i]).Append('\n');
        sb.Append("tabs=").Append(string.Join("|", tabs)).Append('\n');
        if (paper != null) sb.Append("paper=").Append(paper).Append('\n');
        if (chrome != null) sb.Append("chrome=").Append(chrome).Append('\n');
        if (grain is false) sb.Append("grain=0\n");   // 默认开，只在用户关掉后写行；旧版本按不认识的键原样保留(E9)
        if (unknownLines != null)
            foreach (string line in unknownLines) sb.Append(line).Append('\n');
        return sb.ToString();
    }
}
