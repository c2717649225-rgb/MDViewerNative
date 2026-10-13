using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MDViewerNative;

// E1 状态文件兼容契约：固定 preferences.json / bookmarks.json 在文件层面的当前行为
// （往返、缺字段、钳制、未知字段、损坏、超大、未来 schema）。只读写 MDV_CONFIG_DIR 指向的临时目录。
// E7 起：未来 schema 的文件不会被本版本覆盖（此前的 TD-05 现状测试已翻转）。
// E23 起：读不懂的文件（损坏、字段类型不对、超限、缺版本号）在下次写回前改名保留；未来版本改了字段类型时照样认出是新版本。
internal static class StateCompatibilityTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("StateCompat/" + name, ok, "state file compatibility contract");
        string directory = Path.Combine(Path.GetTempPath(), "mdv-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string previous = Environment.GetEnvironmentVariable("MDV_CONFIG_DIR");
        byte[] savedPreferences = JsonSerializer.SerializeToUtf8Bytes(AppPreferences.Current, PreferenceJsonContext.Default.PreferenceData);
        Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", directory);
        string preferences = Path.Combine(directory, "preferences.json");
        try
        {
            Preferences(C, preferences);
            Bookmarks(C, directory, Path.Combine(directory, "bookmarks.json"));
            Unreadable(C, directory);
        }
        finally
        {
            // 还原进程内的全局偏好：写回测试前的快照再读入。
            File.WriteAllBytes(preferences, savedPreferences);
            AppPreferences.Load();
            Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", previous);
            Directory.Delete(directory, true);
        }
    }

    static void Preferences(Action<string, bool> C, string path)
    {
        File.WriteAllText(path, "{\"Schema\":1,\"TabWidth\":3}");
        AppPreferences.Load();
        var p = AppPreferences.Current;
        C("preferences load a schema 1 file", p.TabWidth == 3);
        C("preferences missing fields take defaults", p.WordWrap && p.RecentFiles == 20 && p.UndoSteps == 256 && !p.AutoSave);

        p.TabWidth = 4; p.UseTabs = true; p.SearchHistory = new() { "alpha", "beta" };
        AppPreferences.Save();
        AppPreferences.Current.TabWidth = 9;
        AppPreferences.Load();
        p = AppPreferences.Current;
        C("preferences round trip through save and load", p.TabWidth == 4 && p.UseTabs && p.SearchHistory.SequenceEqual(new[] { "alpha", "beta" }));

        File.WriteAllText(path, "{\"Schema\":1,\"TabWidth\":99,\"RecentFiles\":1}");
        AppPreferences.Load();
        C("preferences are clamped on load", AppPreferences.Current.TabWidth == 16 && AppPreferences.Current.RecentFiles == 5);

        File.WriteAllText(path, "{\"Schema\":1,\"TabWidth\":5,\"FutureOption\":true}");
        AppPreferences.Load();
        C("preferences ignore unknown fields on load", AppPreferences.Current.TabWidth == 5);
        AppPreferences.Save();
        C("preferences drop unknown fields on save (current)", !File.ReadAllText(path).Contains("FutureOption"));

        const string corrupt = "{not json";
        File.WriteAllText(path, corrupt);
        AppPreferences.Current.TabWidth = 6;
        AppPreferences.Load();
        C("preferences corrupt file keeps in-memory values", AppPreferences.Current.TabWidth == 6);
        C("preferences corrupt file is left untouched by load", File.ReadAllText(path) == corrupt);

        File.WriteAllText(path, "{\"Schema\":1,\"TabWidth\":2}" + new string(' ', 128 * 1024));
        AppPreferences.Load();
        C("preferences over 128 KiB are ignored", AppPreferences.Current.TabWidth == 6);

        const string future = "{\"Schema\":2,\"TabWidth\":9,\"NewThing\":1}";
        File.WriteAllText(path, future);
        AppPreferences.Load();
        C("preferences future schema is ignored on load", AppPreferences.Current.TabWidth == 6);
        C("preferences future schema is left untouched by load", File.ReadAllText(path) == future);
        AppPreferences.Current.TabWidth = 7;
        AppPreferences.Save();
        AppPreferences.Reset();
        C("preferences future schema survives save and reset", File.ReadAllText(path) == future);

        File.WriteAllText(path, "{\"Schema\":1,\"TabWidth\":3}");
        AppPreferences.Load();
        AppPreferences.Current.TabWidth = 8;
        AppPreferences.Save();
        AppPreferences.Load();
        C("preferences save resumes after a schema 1 file is loaded", AppPreferences.Current.TabWidth == 8);
    }

    static void Bookmarks(Action<string, bool> C, string directory, string path)
    {
        string doc = Path.Combine(directory, "note.md");
        File.WriteAllText(doc, "x");
        var location = new ReadingLocation(1, new string('B', 64), .5f, .5f);
        var store = new BookmarkStore();
        store.Add(doc, "one", location);
        store.Add(doc, "two", location);
        string good = File.ReadAllText(path);

        const string corrupt = "{broken";
        File.WriteAllText(path, corrupt);
        var loaded = new BookmarkStore(); loaded.Load();
        C("bookmarks corrupt file loads empty", loaded.Items.Count == 0);
        C("bookmarks corrupt file is left untouched by load", File.ReadAllText(path) == corrupt);

        const string marker = "\"Title\":\"two\",\"Location\":{\"Block\":1,";
        string tampered = good.Replace(marker, "\"Title\":\"two\",\"Location\":{\"Block\":-1,");
        File.WriteAllText(path, tampered);
        loaded.Load();
        C("bookmarks drop invalid entries and keep valid ones", tampered != good && loaded.Items.Count == 1 && loaded.Items[0].Title == "one");

        int open = good.IndexOf("\"Items\":[", StringComparison.Ordinal) + "\"Items\":[".Length;
        int close = good.LastIndexOf(']');
        string items = good[open..close];
        File.WriteAllText(path, good[..open] + string.Join(",", Enumerable.Repeat(items, 150)) + good[close..]);
        loaded.Load();
        C("bookmarks load at most 256 entries", loaded.Items.Count == 256);

        string future = good.Replace("\"Schema\":1", "\"Schema\":2");
        File.WriteAllText(path, future);
        loaded.Load();
        C("bookmarks future schema is ignored on load", future != good && loaded.Items.Count == 0);
        C("bookmarks future schema is left untouched by load", File.ReadAllText(path) == future);
        loaded.Add(doc, "three", location);
        loaded.Delete(loaded.Items[0].Id);
        loaded.Add(doc, "four", location);
        C("bookmarks future schema survives add and delete", File.ReadAllText(path) == future);
        C("bookmarks changes after a future-schema load stay in memory", loaded.Items.Count == 1 && loaded.Items[0].Title == "four");

        File.WriteAllText(path, good);
        loaded.Load();
        loaded.Add(doc, "five", location);
        var reread = new BookmarkStore(); reread.Load();
        C("bookmarks save resumes after a schema 1 file is loaded", reread.Items.Count == 3 && reread.Items[2].Title == "five");
    }

    // E23：读取时仍不动文件；真正写回前，把读不懂的原文件改名为 <文件名>.corrupt-<guid> 保留（与草稿一致），再写新文件。
    static void Unreadable(Action<string, bool> C, string directory)
    {
        string prefs = Path.Combine(directory, "preferences.json"), marks = Path.Combine(directory, "bookmarks.json");
        string[] Backups(string path) => Directory.GetFiles(directory, Path.GetFileName(path) + ".corrupt-*");
        void Clean() { foreach (string f in Directory.GetFiles(directory, "*.corrupt-*")) File.Delete(f); }
        bool KeptAside(string path, string original) => Backups(path) is { Length: 1 } b && File.ReadAllText(b[0]) == original;

        const string prefsTypeChanged = "{\"Schema\":2,\"TabWidth\":\"wide\"}";
        File.WriteAllText(prefs, prefsTypeChanged);
        AppPreferences.Load(); AppPreferences.Save();
        C("preferences future schema with changed field types survives save", File.ReadAllText(prefs) == prefsTypeChanged && Backups(prefs).Length == 0);

        foreach (var (name, text) in new[] { ("corrupt", "{not json"), ("wrong field type", "{\"Schema\":1,\"TabWidth\":\"wide\"}"),
            ("over 128 KiB", "{\"Schema\":1,\"TabWidth\":2}" + new string(' ', 128 * 1024)) })
        {
            Clean();
            File.WriteAllText(prefs, text);
            AppPreferences.Load();
            bool untouched = File.ReadAllText(prefs) == text;
            AppPreferences.Save();
            C("preferences " + name + " file is kept aside before the next save", untouched && KeptAside(prefs, text) && File.ReadAllText(prefs).Contains("\"Schema\":1"));
        }
        Clean();
        AppPreferences.Save();
        C("preferences later saves do not keep more copies", Backups(prefs).Length == 0);

        string doc = Path.Combine(directory, "note.md");
        var location = new ReadingLocation(1, new string('B', 64), .5f, .5f);
        const string marksTypeChanged = "{\"Schema\":2,\"Items\":\"x\"}";
        File.WriteAllText(marks, marksTypeChanged);
        var store = new BookmarkStore(); store.Load(); store.Add(doc, "six", location);
        C("bookmarks future schema with changed field types survives add", File.ReadAllText(marks) == marksTypeChanged && Backups(marks).Length == 0);

        foreach (var (name, text) in new[] { ("corrupt", "{broken"), ("missing schema", "{\"Items\":[]}"),
            ("over 512 KiB", "{\"Schema\":1,\"Items\":[]}" + new string(' ', 512 * 1024)) })
        {
            Clean();
            File.WriteAllText(marks, text);
            store = new BookmarkStore(); store.Load();
            store.Add(doc, "seven", location);
            var reread = new BookmarkStore(); reread.Load();
            C("bookmarks " + name + " file is kept aside before the next save", KeptAside(marks, text) && reread.Items.Count == 1 && reread.Items[0].Title == "seven");
        }
        Clean();
    }
}
