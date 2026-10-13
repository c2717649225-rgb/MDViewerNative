using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MDViewerNative;

// U10：起始页的宿主接线。起始页是界面状态（_home），不是文档会话：不进 Tabs、不写入 tabs=。
// 起始页显示时，Tabs 里的当前文档仍然存在，只是不画、不接收正文区的输入；文档命令按 CmdGate.Document 灰掉。
internal static unsafe partial class Program
{
    static bool _home;
    static HomeModel _homeModel;
    static float _homeScroll;
    static HomeTarget _homeHover;

    static bool HomeShown => _home && State.Appearance.Desk;

    // 只剩一个空白、未改动、没有路径的会话时，起始页之外不再显示它的标签。
    static bool PlaceholderHidden => Home.HidesPlaceholder(HomeShown, Tabs.Count, Session.Path.Length == 0, Session.Dirty);

    static int VisibleDocumentCount => PlaceholderHidden ? 0 : Tabs.Count;

    static ToolbarTab HomeTab => new(Home.TabId, Home.TabTitle, "", HomeShown, false) { IsHome = true };

    static void ShowHome()
    {
        if (!State.Appearance.Desk || HasComposition) return;
        if (!HomeShown)
        {
            if (_editing && !ToggleEdit()) return;
            Find.Close();
            StopSmoothScroll();
            HideReturnHint(); WakeTitlebar(); HideSelectionBubble(); CloseJuan(false);
            Reading.Remember(Session);
            _home = true;
        }
        _homeModel = null; _homeScroll = 0f; _homeHover = HomeTarget.None;
        AfterHomeChange();
    }

    static void LeaveHome()
    {
        if (!_home) return;
        _home = false;
        _homeHover = HomeTarget.None;
        AfterHomeChange();
    }

    static void AfterHomeChange()
    {
        RefreshTabInfo();
        UpdateTitle();
        InvalidateToolbar(_hwnd);
        _ = Externs.InvalidateRect(_hwnd, IntPtr.Zero, 0);
        if (!_editing) _ = Externs.SetFocus(_hwnd);
    }

    // 关掉最后一个文档、启动时什么都没打开：停在起始页。
    static void HomeIfEmpty()
    {
        if (State.Appearance.Desk && Tabs.Count == 1 && Session.Path.Length == 0 && !Session.Dirty && !_editing && !_home) ShowHome();
    }

    // Ctrl+Tab / Ctrl+Shift+Tab：desk 下起始页排在最前。
    static void StepTab(int step)
    {
        if (!State.Appearance.Desk) { SwitchTab((ActiveTab + (step > 0 ? 1 : Tabs.Count - 1)) % Tabs.Count); return; }
        int count = VisibleDocumentCount + 1;
        int next = Home.Cycle(HomeShown ? 0 : ActiveTab + 1, count, step);
        if (next == 0) ShowHome(); else SwitchTab(next - 1);
    }

    static HomeModel HomeModelNow() => _homeModel ??= BuildHomeModel();

    static HomeModel BuildHomeModel()
    {
        var now = DateTime.Now;
        bool Available(string p)
        {
            if (_unavailablePaths.Contains(p)) return false;
            try { return File.Exists(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
        HomeEntry continueEntry = null;
        string latest = Reading.Latest;
        if (latest != null && Available(latest)) continueEntry = Entry(latest, now);
        var recent = Home.RecentPicks(_recent, continueEntry?.Path, Available).Select(p => Entry(p, now)).ToList();
        return new HomeModel(continueEntry, recent, Home.Greeting(now.Hour), Home.ChineseDate(now));
    }

    static HomeEntry Entry(string path, DateTime now)
    {
        string name = Path.GetFileName(path);
        var (title, excerpt) = Home.Summarize(ReadHead(path));
        string when = "";
        try { when = Home.RelativeTime(File.GetLastWriteTime(path), now); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new HomeEntry(path, name, title ?? Path.GetFileNameWithoutExtension(path), excerpt, Path.GetDirectoryName(path) ?? "", when, Reading.ProgressOf(path));
    }

    // 只读文件开头 8 KB 取标题与摘要；UTF-8 解不出来时按 GBK 再试。读不了就留空。
    static string ReadHead(string path)
    {
        try
        {
            var buffer = new byte[8192];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) n = fs.Read(buffer, 0, buffer.Length);
            int start = n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            string text = new UTF8Encoding(false, false).GetString(buffer, start, n - start);
            if (text.IndexOf('\uFFFD') is int bad && bad >= 0 && bad < text.Length - 4) text = Encoding.GetEncoding(936).GetString(buffer, 0, n);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    static HomeFrame PrepareHome() => HomeView.Prepare(HomeModelNow(), _viewW / _scale, ContentTop);

    static void PaintHome(IntPtr hdc, RECT cr)
    {
        var frame = PrepareHome();
        _homeScroll = Math.Clamp(_homeScroll, 0f, Home.MaxScroll(frame, ContentHeight));
        HomeView.Paint(hdc, cr, _scale, new HomePaintState(ContentTop, _homeScroll, _homeHover));
    }

    static HomeTarget HomeHit(float x, float y) =>
        y < ContentTop || RenderResources.Rt == IntPtr.Zero ? HomeTarget.None : Home.Hit(PrepareHome(), x, y + _homeScroll);

    static void HomeHoverAt(float x, float y)
    {
        var hit = HomeHit(x, y);
        if (hit == _homeHover) return;
        _homeHover = hit;
        InvalidateContent(_hwnd);
    }

    static void HomeActivate(HomeTarget target)
    {
        var model = HomeModelNow();
        switch (target.Kind)
        {
            case HomeTargetKind.Continue when model.Continue != null: LoadFile(model.Continue.Path); break;
            case HomeTargetKind.Card when target.Index >= 0 && target.Index < model.Recent.Count: LoadFile(model.Recent[target.Index].Path); break;
            case HomeTargetKind.Open: OpenFileDlgNative(); break;
        }
    }

    static void HomeWheel(int delta)
    {
        float max = Home.MaxScroll(PrepareHome(), ContentHeight);
        float next = Math.Clamp(_homeScroll - delta / 120f * 64f, 0f, max);
        if (next == _homeScroll) return;
        _homeScroll = next;
        InvalidateContent(_hwnd);
    }

    // 起始页上的按键：Enter 继续阅读；正文导航、选区与编辑相关的键不落到藏在后面的文档。
    static bool HomeKeyDown(int vk, bool ctrl)
    {
        if (!HomeShown) return false;
        if (vk == 0x0D && !ctrl) { HomeActivate(new HomeTarget(HomeModelNow().Continue != null ? HomeTargetKind.Continue : HomeTargetKind.Open)); return true; }
        if (ctrl) return vk is 'E' or 'S' or 'F' or 'T' or 'C' or 'A' or 'G' or 'H';
        return vk is 0x1B or 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;
    }

    static FooterContent HomeFooterNow() => FooterModel.Home(VisibleDocumentCount, FooterNote.Current(Environment.TickCount64));
}
