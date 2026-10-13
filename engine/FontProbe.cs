using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MDViewerNative;

// B1(serif 实测暴露,I11 遗留验证):serif 字体族探测,自旧 Program.ProbeSerifFamily 重写。
// 旧实现双重错误:① 注册表键名含空格("Noto Serif SC (TrueType)"),Contains("notoserifsc") 永不命中
// ——本机实际装了 Noto Serif SC 却被报"未安装";② 命中后返回写死族名,而键名≠DWrite 族名(VF 真名 "Noto Serif SC")。
// 修法:键名+文件名去 空格/连字符/点 归一化后匹配;命中后解析字体文件 sfnt name 表取真实族名
// (ID16 preferred 优先、ID1 兜底),写死族名仅作 name 表不可读时的回退。纯字节解析不碰 COM(探针纪律)。
// 拆为独立静态类供 selftest 直测(打桩输入,不硬编码本机字体存在性)。
static class FontProbe
{
    // 匹配键按优先级:Adobe 思源宋体 SC → Noto Serif CJK SC → Noto Serif SC(VF) → 已安装宋体。
    public static readonly (string Key, string FallbackFamily)[] Candidates =
    {
        ("sourceshanserifsc", "Source Han Serif SC"),
        ("sourcehanserifsc", "Source Han Serif SC"),
        ("notoserifcjksc", "Noto Serif CJK SC"),
        ("notoserifsc", "Noto Serif SC"),
        ("simsun", "SimSun"),
    };

    // 归一化:小写 + 剔除 空格/-/_/./括号(注册表键名 "Noto Serif SC (TrueType)" 与文件名 "NotoSerifSC-VF.ttf" 都收敛到 notoserifsc…)
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is ' ' or '-' or '_' or '.' or '(' or ')') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // 纯函数:注册表 (键名,值) 序列 → 首个命中的候选(候选序优先,同级保持输入序);null=无命中
    public static (int idx, string name, string val)? Match(IEnumerable<(string name, string val)> entries)
    {
        var list = new List<(string name, string val)>(entries);
        for (int c = 0; c < Candidates.Length; c++)
        {
            foreach (var (name, val) in list)
                if (Matches(c, name, val))
                    return (c, name, val);
        }
        return null;
    }

    private static bool Matches(int candidate, string name, string val)
    {
        string key = Candidates[candidate].Key;
        if (key != "simsun") return Norm(name).Contains(key) || Norm(val).Contains(key);
        // SimSun-ExtB and NSimSun-only families are not the requested body fallback.
        string file = Path.GetFileName(val ?? "").ToLowerInvariant();
        if (file is "simsun.ttc" or "simsun.ttf" or "simsun.otf") return true;
        foreach (string part in (name ?? "").Split('&'))
            if (Norm(part) is "simsun" or "simsuntruetype" or "simsunopentype") return true;
        return false;
    }

    // Candidate order wins, but stale registry entries must not hide a later installed font.
    internal static string SelectFamily(IEnumerable<(string name, string val)> entries, Func<string, bool> exists, Func<string, string> readFamily)
    {
        var list = new List<(string name, string val)>(entries);
        for (int c = 0; c < Candidates.Length; c++)
            foreach (var (name, val) in list)
            {
                if (!Matches(c, name, val)) continue;
                string path = ResolveFontFile(val);
                if (string.IsNullOrEmpty(path) || !exists(path)) continue;
                string family = readFamily(path);
                return string.IsNullOrWhiteSpace(family) ? Candidates[c].FallbackFamily : family;
            }
        return null;
    }

    // 注册表值 = 相对文件名(系统安装)或绝对路径(每用户 "Just for me" 安装)
    public static string ResolveFontFile(string regValue)
    {
        if (string.IsNullOrEmpty(regValue)) return null;
        if (Path.IsPathRooted(regValue)) return regValue;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", regValue);
    }

    // ---------- sfnt name 表解析(TTF/OTF;TTC/OTC 取首个 font 的表目录) ----------
    public static string ReadFamilyName(string path)
    {
        try { return ReadFamilyNameFromBytes(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    public static string ReadFamilyNameFromBytes(byte[] f)
    {
        if (f == null || f.Length < 16) return null;
        uint tag = U32(f, 0);
        int tableBase;
        if (tag == 0x74746366)                      // "ttcf" → 首 font 目录偏移
            tableBase = (int)U32(f, 12);
        else if (tag is 0x00010000 or 0x4F54544F or 0x74727565)   // TTF / "OTTO" / "true"
            tableBase = 0;
        else return null;
        if (tableBase < 0 || tableBase + 12 > f.Length) return null;
        int numTables = U16(f, tableBase + 4);
        if (numTables <= 0 || numTables > 256) return null;
        int nameOff = -1, nameLen = 0;
        for (int i = 0; i < numTables; i++)
        {
            int rec = tableBase + 12 + i * 16;
            if (rec + 16 > f.Length) return null;
            if (U32(f, rec) == 0x6E616D65)          // "name"
            {
                nameOff = (int)U32(f, rec + 8);
                nameLen = (int)U32(f, rec + 12);
                break;
            }
        }
        if (nameOff < 0 || nameLen < 6 || nameOff + nameLen > f.Length) return null;
        int count = U16(f, nameOff + 2);
        int storage = nameOff + U16(f, nameOff + 4);
        if (storage > nameOff + nameLen) return null;
        // 记录打分选优:DWrite 桌面端用 en-US 名,"思源宋体"(zh-CN)会让族解析静默失败,必须 en-US 强加权;
        // ID16(preferred family)优于 ID1(subfamily 拆分时的老式家族名),plat3(Windows)优于 plat1(Mac)
        string best = null; int bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            int rec = nameOff + 6 + i * 12;
            if (rec + 12 > nameOff + nameLen) break;
            int plat = U16(f, rec), lang = U16(f, rec + 4), id = U16(f, rec + 6), len = U16(f, rec + 8), off = U16(f, rec + 10);
            if (id is not (1 or 16) || len == 0 || len % 2 != 0) continue;
            int s = storage + off;
            if (s + len > nameOff + nameLen) continue;
            string val = DecodeUtf16BE(f, s, len);  // plat 1/3 的 UTF-16 均大端
            if (val.Length == 0) continue;
            int score = (id == 16 ? 8 : 0) + (plat == 3 ? 2 : 0) + (lang == 0x0409 ? 20 : lang == 0x0804 ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = val; }
        }
        return best;
    }

    static string DecodeUtf16BE(byte[] f, int s, int len)
    {
        var sb = new StringBuilder(len / 2);
        for (int i = 0; i < len; i += 2)
            sb.Append((char)((f[s + i] << 8) | f[s + i + 1]));
        return sb.ToString();
    }

    static ushort U16(byte[] b, int i) => (ushort)((b[i] << 8) | b[i + 1]);
    static uint U32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    // 组合入口:双 hive 收集 → 优先级与存在性筛选 → sfnt 真名。null=无可用候选,不自动下载字体。
    public static string ProbeFamily()
    {
        try
        {
            var entries = new List<(string, string)>();
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (k == null) continue;
                foreach (string n in k.GetValueNames())
                    entries.Add((n, k.GetValue(n) as string));
            }
            return SelectFamily(entries, File.Exists, ReadFamilyName);
        }
        catch { return null; }
    }
}
