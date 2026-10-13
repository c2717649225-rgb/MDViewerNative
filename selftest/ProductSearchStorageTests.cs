using System;
using System.IO;
using System.Linq;
using MDViewerNative;

internal static class ProductSearchStorageTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok) => check("ProductSearchStorage/" + name, ok, "bounded search and local state");
        var regex = new SearchOptions(RegularExpression: true);
        C("regex captures return exact offsets", TextSearch.FindAll("ab12 cd34", "[a-z]+[0-9]+", regex).Select(x => x.start).SequenceEqual(new[] { 0, 5 }));
        C("regex replacement expands groups only in regex mode", TextSearch.ReplaceAll("ab12 cd34", "([a-z]+)([0-9]+)", "$2:$1", regex).text == "12:ab 34:cd");
        C("literal replacement still does not expand groups", TextSearch.ReplaceAll("ab", "ab", "$1").text == "$1");
        C("zero length regex replacement terminates", TextSearch.ReplaceAll("ab", "(?=.)", "-", regex).text == "-a-b");
        C("selection range excludes outside occurrences", TextSearch.FindAll("cat cat cat", "cat", default, 4, 7).Count == 1);
        C("selection replacement preserves outside text", TextSearch.ReplaceAll("cat cat cat", "cat", "x", default, 4, 7).text == "cat x cat");
        C("regex range does not become a separate artificial string", TextSearch.FindAll("a cat z", "(?<=a )cat", regex, 2, 5).Count == 1);
        bool invalid = false; try { TextSearch.FindAll("x", "[", regex); } catch (ArgumentException) { invalid = true; }
        C("invalid regex is rejected", invalid);
        C("variable length selection matches are supported", TextSearch.MatchesAt("abc123", 0, 6, "[a-z]+[0-9]+", regex));
        C("single regex replacement uses the same group semantics", TextSearch.ReplacementAt("ab12", 0, 4, "([a-z]+)([0-9]+)", "$2:$1", regex) == "12:ab");
        var settings = AppPreferences.Normalize(new PreferenceData { TabWidth = 999, AutoSaveSeconds = -1, RecentFiles = 999, HistoryMegabytes = -10, UndoSteps = 0 });
        C("settings are bounded", settings.TabWidth == 16 && settings.AutoSaveSeconds == 5 && settings.RecentFiles == 50 && settings.HistoryMegabytes == 8 && settings.UndoSteps == 10);
        C("automatic source writes are opt-in", !new PreferenceData().AutoSave && !new PreferenceData().VersionHistory);
        C("indentation size and tabs are explicit", EditorKit.IndentBlock("a\nb", 4) == "    a\n    b" && EditorKit.IndentBlock("a", 4, true) == "\ta");
        C("dedent handles both spaces and tabs", EditorKit.DedentBlock("\ta\n    b", 4) == "a\nb");
        string directory = Path.Combine(Path.GetTempPath(), "mdv-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string previous = Environment.GetEnvironmentVariable("MDV_CONFIG_DIR");
        bool oldHistory = AppPreferences.Current.VersionHistory;
        int oldCount = AppPreferences.Current.VersionsPerFile;
        Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", directory);
        try
        {
            string source = Path.Combine(directory, "note.md"); File.WriteAllText(source, "one");
            var bookmarks = new BookmarkStore();
            var location = new ReadingLocation(0, new string('A', 64), .25f, .3f);
            bookmarks.Add(source, "position", location);
            var loaded = new BookmarkStore(); loaded.Load();
            C("bookmarks persist logical position and title", loaded.Items.Count == 1 && loaded.Items[0].Location == location && loaded.Items[0].Title == "position");
            string renamed = Path.Combine(directory, "renamed.md"); loaded.RenamePath(source, renamed);
            var reloaded = new BookmarkStore(); reloaded.Load();
            C("bookmark paths follow rename", reloaded.Items[0].Path == renamed);
            reloaded.Delete(reloaded.Items[0].Id); reloaded.Load(); C("bookmark deletion is durable", reloaded.Items.Count == 0);
            AppPreferences.Current.VersionHistory = true; AppPreferences.Current.VersionsPerFile = 2;
            for (int i = 0; i < 4; i++) { File.WriteAllText(source + ".bak", "version " + i); VersionArchive.CapturePrevious(source); }
            var versions = VersionArchive.List(source);
            C("stored versions are bounded per document", versions.Count == 2);
            C("stored versions never write the source", File.ReadAllText(source) == "one");
            VersionArchive.CapturePrevious(source); C("unchanged backups are deduplicated", VersionArchive.List(source).Count == 2);
            bool boundary = false; try { VersionArchive.Delete(source); } catch (ArgumentException) { boundary = true; }
            C("version deletion cannot delete arbitrary source files", boundary && File.Exists(source));
        }
        finally
        {
            AppPreferences.Current.VersionHistory = oldHistory; AppPreferences.Current.VersionsPerFile = oldCount;
            Environment.SetEnvironmentVariable("MDV_CONFIG_DIR", previous);
            Directory.Delete(directory, true);
        }
    }
}
