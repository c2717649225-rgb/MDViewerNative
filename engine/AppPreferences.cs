using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDViewerNative;

internal sealed class PreferenceData
{
    public int Schema { get; set; } = 1;
    public int TabWidth { get; set; } = 2;
    public bool UseTabs { get; set; }
    public bool WordWrap { get; set; } = true;
    public bool AutoSave { get; set; }
    public int AutoSaveSeconds { get; set; } = 60;
    public bool VersionHistory { get; set; }
    public int VersionsPerFile { get; set; } = 8;
    public int HistoryMegabytes { get; set; } = 64;
    public bool RememberSearch { get; set; } = true;
    public bool SearchCase { get; set; }
    public bool SearchWord { get; set; }
    public bool SearchRegex { get; set; }
    public List<string> SearchHistory { get; set; } = new();
    public int UndoSteps { get; set; } = 256;
    public int RecentFiles { get; set; } = 20;
    public bool DefaultReadOnly { get; set; }
    public bool AnimateImages { get; set; } = true;   // 34:GIF 动画播放;系统关闭客户区动画时同样静止
    // 47:大文档处理进入可取消状态前的工作量(千字符)。低于该阈值不轮询探针,
    // 普通文档既不受影响也没有轮询开销。0 = 始终允许取消。
    public int CancelGraceKilochars { get; set; } = 400;
    // 49:全部文档撤销历史合计保留的增量字符上限(千字符)。这是字符口径,
    // 不是托管堆字节;超出后按最近使用淘汰其他文档最旧的编辑。
    public int UndoBudgetKilochars { get; set; } = 16384;
}

[JsonSerializable(typeof(PreferenceData))]
internal partial class PreferenceJsonContext : JsonSerializerContext { }

internal static class AppPreferences
{
    public static PreferenceData Current { get; private set; } = new();
    private static string FilePath => Path.Combine(AppStorage.DirectoryPath, "preferences.json");
    // 文件来自更新的版本(schema > 1)时不覆盖它:本次会话的修改只留在内存(E7,ADR-0001 第 4 条)。
    private static bool _newerFile;
    // 文件读不懂(损坏、字段类型不对、超限、版本号无效):读取时不动它,下次保存前先改名保留(E23)。
    private static bool _unreadable;
    public static void Load()
    {
        _newerFile = false;
        _unreadable = false;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (new FileInfo(FilePath).Length > 128 * 1024) { Unreadable("larger than 128 KiB"); return; }
            byte[] bytes = File.ReadAllBytes(FilePath);
            int schema = AppStorage.ReadSchema(bytes) ?? 1;   // 缺 Schema 时与反序列化的默认值一致,按 1 读
            if (schema > 1)
            {
                _newerFile = true;
                AppLog.Write("preferences.json schema " + schema + " is newer than this version; the file is kept and changes are not saved");
                return;
            }
            if (schema != 1) { Unreadable("schema " + schema); return; }
            Current = Normalize(JsonSerializer.Deserialize(bytes, PreferenceJsonContext.Default.PreferenceData) ?? throw new JsonException("null document"));
        }
        catch (Exception ex) { _unreadable = true; AppLog.Error("read preferences", ex); }
    }
    static void Unreadable(string reason)
    {
        _unreadable = true;
        AppLog.Write("preferences.json is unreadable (" + reason + "); it will be kept as a .corrupt copy before the next save");
    }
    public static PreferenceData Normalize(PreferenceData value)
    {
        value.TabWidth = Math.Clamp(value.TabWidth, 1, 16);
        value.AutoSaveSeconds = Math.Clamp(value.AutoSaveSeconds, 5, 3600);
        value.VersionsPerFile = Math.Clamp(value.VersionsPerFile, 1, 50);
        value.HistoryMegabytes = Math.Clamp(value.HistoryMegabytes, 8, 512);
        value.UndoSteps = Math.Clamp(value.UndoSteps, 10, 2000);
        value.RecentFiles = Math.Clamp(value.RecentFiles, 5, 50);
        value.CancelGraceKilochars = Math.Clamp(value.CancelGraceKilochars, 0, 20000);
        value.UndoBudgetKilochars = Math.Clamp(value.UndoBudgetKilochars, 1024, 262144);
        value.SearchHistory = (value.SearchHistory ?? new()).Where(s => !string.IsNullOrEmpty(s) && s.Length <= 2048).Distinct(StringComparer.Ordinal).Take(20).ToList();
        return value;
    }
    public static void Save()
    {
        if (_newerFile) return;
        if (_unreadable) { if (!AppStorage.SetAside(FilePath)) return; _unreadable = false; }
        AtomicFile.WriteState(FilePath, JsonSerializer.SerializeToUtf8Bytes(Normalize(Current), PreferenceJsonContext.Default.PreferenceData));
    }
    public static void Reset() { Current = new(); Save(); }
    public static void RememberQuery(string query)
    {
        if (!Current.RememberSearch || string.IsNullOrEmpty(query) || query.Length > 2048) return;
        Current.SearchHistory.Remove(query); Current.SearchHistory.Insert(0, query);
        if (Current.SearchHistory.Count > 20) Current.SearchHistory.RemoveRange(20, Current.SearchHistory.Count - 20);
    }
}
