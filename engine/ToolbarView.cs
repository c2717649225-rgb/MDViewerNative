using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// One shared layout drives paint, hit testing, keyboard targets and tooltip rectangles.
internal sealed unsafe class ToolbarView : IDisposable
{
    private readonly Dictionary<string, (nint lay, float w)> _tabLayouts = new();
    private readonly Dictionary<string, nint> _labels = new();
    private readonly (float x, float w)[] _btnRects = new (float, float)[6];
    // U6a：desk 顶栏多出的两个目标（阅读、外观）；经典界面恒为 0 宽。
    private readonly (float x, float w)[] _deskRects = new (float, float)[3];
    private List<DeskToolSlot> _deskSlots = new();
    private (float x, float w) _segment;
    // U6a：desk 顶栏（ADR-0003 开关，启动时由宿主设定，运行中不变）。
    public bool Desk { get; set; }
    // U6b：合并标题栏——右端画窗口按钮，工具区让出 138。状态由宿主从非客户区消息同步。
    public bool Caption { get; set; }
    public bool Maximized { get; set; }
    public bool WindowActive { get; set; } = true;
    // U6c：读时退后的混合量 0..1（已缓动），宿主每帧给。
    public float Quiet { get; set; }
    public CaptionPart CaptionHover { get; private set; }
    public CaptionPart CaptionPressed { get; private set; }
    public bool SetCaptionHover(CaptionPart part) { if (CaptionHover == part) return false; CaptionHover = part; return true; }
    public bool SetCaptionPressed(CaptionPart part) { if (CaptionPressed == part) return false; CaptionPressed = part; return true; }
    public CaptionPart HitCaption(float x, float y) => Caption ? DeskFrame.HitCaption(x, y, _width, ToolH) : CaptionPart.None;
    // 标签、「+」、工具之外的顶栏空白：交给系统当标题栏拖动。
    public bool OverTarget(float x, float y) => HitTarget(x, y).Part != ToolbarPart.None;
    private IReadOnlyList<ToolbarTab> _tabSnapshot;
    private IReadOnlyList<ToolbarTab> _tabs = Array.Empty<ToolbarTab>();
    private List<ToolbarTarget> _focusTargets = Enumerable.Range(0, 6).Select(i => ToolbarTarget.ForCommand((ToolbarCommand)i)).ToList();
    private ToolbarTarget _focus, _hover, _press;
    private (float x, float w) _pressRect;
    private Guid _firstVisible;
    private float _width;
    private bool _prepared;
    internal int LayoutPassCount { get; private set; }
    private bool _disposed, _directoryEnabled;
    private nint _tooltip, _tipText;
    private readonly DeskTipView _deskTip = new();   // U6a2：desk 自绘提示
    private string _hint;
    private ToolbarTarget _hintHit, _tipMuted;   // 试用修复：按下后同一目标上不再弹提示，指针离开该目标才恢复
    private bool _tipMutedSet;
    private OverlayBox _hintRect;
    private float _hintScale;
    private CmdState _state;
    public bool AlwaysShowClose { get; set; }
    // Windows promotes pen/touch to mouse messages with the documented MI_WP signature.
    // Keep a conservative always-visible close for those inputs; real mouse motion
    // restores hover presentation even on a hybrid device. No system settings change.
    // `press`: the observation of a button-down, made after BeginPointer judged the press. A touch
    // tap arrives as a promoted WM_MOUSEMOVE and then WM_LBUTTONDOWN; the move switches to the
    // always-visible mode but the user has not seen that frame yet (review #2), so a close is only
    // "shown" in touch mode once a press has been handled in that mode.
    public bool ObservePointerSource(nint extraInfo, bool press = false)
    {
        bool fallback = IsPromotedSource(extraInfo);
        bool changed = AlwaysShowClose != fallback;
        if (changed) _closeModeUnseen = fallback;
        if (press) _closeModeUnseen = false;
        AlwaysShowClose = fallback;
        return changed;
    }
    private bool _closeModeUnseen;
    // Windows marks mouse messages promoted from pen/touch with the documented MI_WP signature.
    internal static bool IsPromotedSource(nint extraInfo) => ((ulong)(long)extraInfo & 0xFFFFFF00UL) == 0xFF515700UL;

    public bool HasFocus => _focus.Part != ToolbarPart.None;
    public bool HasPress => _press.Part != ToolbarPart.None;
    public ToolbarTarget FocusedTarget => _focus;
    public ToolbarTarget HoveredTarget => _hover;
    public IReadOnlyList<ToolbarTarget> FocusTargets => _focusTargets;
    // Compatibility for diagnostics only. Host actions use FocusedTarget, never this position.
    public int FocusIndex { get => _focusTargets.IndexOf(_focus); set => _focus = value >= 0 && value < _focusTargets.Count ? _focusTargets[value] : default; }
    public int FocusCount => _focusTargets.Count;
    public List<(float x, float w)> TabRects { get; private set; }
    public (float x, float w) OverflowRect { get; private set; }
    public (float x, float w)[] BtnRects() => _btnRects;
    // A2-B4: commands folded into More at the current width, in fold order (TOC, Find, Save).
    // Folding is decided from measured button widths plus the minimum tab area, never from
    // a fixed breakpoint; reachable window widths keep the full strip.
    private static readonly ToolbarCommand[] FoldOrder = { ToolbarCommand.ToggleToc, ToolbarCommand.ToggleFind, ToolbarCommand.Save };
    public IReadOnlyList<ToolbarCommand> FoldedCommands { get; private set; } = Array.Empty<ToolbarCommand>();
    // The active document's unsaved dot: on Save while visible, otherwise carried by More.
    public ToolbarTarget DirtyMarkerTarget { get; private set; } = ToolbarTarget.ForCommand(ToolbarCommand.Save);
    // A dirty document whose tab is not visible marks the document list entry.
    public bool OverflowDirty { get; private set; }
    internal const float TabMinWidth = 88f;
    // Hidden documents behind the list entry; the entry shows "+N" instead of a second ▾ so it
    // cannot be confused with "更多 ▾" (A2-B5 decision, quiet Muted ink until hovered or focused).
    public int HiddenCount { get; private set; }
    internal string OverflowLabel => HiddenCount > 99 ? "99+" : "+" + HiddenCount;
    // Quiet secondary labels (inactive tab, "+N") use Muted; in system high contrast Muted maps to
    // COLOR_GRAYTEXT, which means "disabled", so enabled labels switch to Ink (COLOR_WINDOWTEXT).
    internal static bool LabelInk(bool emphasised, bool enabled, bool highContrast) => enabled && (emphasised || highContrast);
    // The unsaved dot sits in the entry's top-right corner, above the count's cap height.
    internal (float x, float y) OverflowDotCenter => (OverflowRect.x + OverflowRect.w - 6f, 8f);

    // A2-B5 decision: when a qualified name ("one › README") does not fit, shrink the qualifier
    // from its far end first so the file name a reader recognises stays whole; only then fall
    // back to the bare name (DirectWrite trimming ellipsises whatever still overflows).
    internal static string FitTabTitle(string display, float budget, Func<string, float> measure)
    {
        const string sep = " › ";
        if (string.IsNullOrEmpty(display) || measure(display) <= budget) return display;
        int cut = display.LastIndexOf(sep, StringComparison.Ordinal);
        if (cut <= 0) return display;
        string qualifier = display[..cut], name = display[(cut + sep.Length)..];
        if (name.Length == 0) return display;
        // Shrink at text-element boundaries (same rule as Dw.TrimLayout): never split a surrogate pair.
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(qualifier);
        for (int j = 1; j < starts.Length; j++)
        {
            string candidate = "…" + qualifier[starts[j]..] + sep + name;
            if (measure(candidate) <= budget) return candidate;
        }
        string marked = "…" + sep + name;
        return measure(marked) <= budget ? marked : name;
    }
    // U6a：desk 顶栏文字 12.5（演示 .tb / .tab font-size），经典界面 13。
    private nint UiFormat => Desk && FmtDeskUi != 0 ? FmtDeskUi : FmtToolbar;
    private float MeasureText(string text)
    {
        nint layout = Dw.MkLayout(text, UiFormat, 10000f);
        if (layout == 0) return float.MaxValue;
        try { return Dw.LayoutW(layout); } finally { Marshal.Release(layout); }
    }
    // Minimum tab area with overflow: one tab of TabMinWidth, its gap and the 36 DIP list entry.
    private const float TabAreaMin = TabMinWidth + 40f;
    // Command IDs More must add for folded buttons, in toolbar order and deduplicated
    // against the static More face (Find is already there).
    internal static int[] MoreProjection(IReadOnlyList<ToolbarCommand> folded)
    {
        var ids = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            var c = Commands.All[i];
            if (folded == null || !folded.Contains((ToolbarCommand)i) || (c.Face & CmdFace.More) != 0 || ids.Contains(c.Id)) continue;
            ids.Add(c.Id);
        }
        return ids.ToArray();
    }

    internal static bool ButtonEnabled(int index, CmdState state)
        => index >= 0 && index <= 9 && !state.Composing && (index is 5 or 6 or 8 or 9 || Commands.All[index == 7 ? 1 : index].IsEnabled(state));   // 7「阅读」与编辑切换同源

    public void ToggleFocus() => FocusIndex = HasFocus ? -1 : 0;
    public void ClearFocus() => _focus = default;
    public void StepFocus(int vk, bool shift)
    {
        if (_focusTargets.Count == 0) { _focus = default; return; }
        bool backward = vk == 0x25 || vk == 9 && shift;
        int current = Math.Max(0, FocusIndex);
        FocusIndex = (current + (backward ? _focusTargets.Count - 1 : 1)) % _focusTargets.Count;
    }
    // Review #4: Home/End reach the first/last target, as in a native toolbar.
    public void FocusEdge(bool last)
    {
        if (_focusTargets.Count == 0) { _focus = default; return; }
        _focus = _focusTargets[last ? _focusTargets.Count - 1 : 0];
    }
    // Keys the focused toolbar consumes. Anything else (PgUp/PgDn/Up/Down) keeps its reading-view
    // meaning instead of being swallowed. Delete deliberately does not close a tab: Ctrl+W does,
    // and a quiet reader has no one-key destructive action.
    internal static bool OwnsKey(int vk) => vk is 0x25 or 0x27 or 9 or 0x0D or 0x20 or 0x1B or 0x24 or 0x23;

    public bool MovePointer(float x, float y)
    {
        var next = HitTarget(x, y);
        bool changed = next != _hover;
        _hover = next;
        return changed;
    }
    public bool LeavePointer() { bool changed = _hover.Part != ToolbarPart.None; _hover = default; return changed; }
    public bool BeginPointer(float x, float y)
    {
        var hoverBefore = _hover; var focusBefore = _focus;
        MovePointer(x, y);
        _focus = default;
        var hit = _hover;
        // Review #2: a close glyph that was not on screen before this press (first touch on a
        // hybrid device, where hover presentation hides clean closes) must not close the tab;
        // the press activates the tab instead and the now-visible close works on the next tap.
        _pressPromoted = hit.Part == ToolbarPart.Close && !CloseWasShown(hit.DocumentId, hoverBefore, focusBefore);
        if (_pressPromoted) hit = ToolbarTarget.ForTab(hit.DocumentId);
        _press = IsEnabled(hit) ? hit : default;
        _pressRect = RectOf(_press);
        return HasPress;
    }
    private bool _pressPromoted;
    private bool CloseWasShown(Guid id, ToolbarTarget hover, ToolbarTarget focus) =>
        (focus.IsTab && focus.DocumentId == id) || Desk   // desk 的关闭钮常显（演示）
        || (AlwaysShowClose ? !_closeModeUnseen : hover.IsTab && hover.DocumentId == id);
    private bool SamePress(ToolbarTarget hit) =>
        hit == _press || (_pressPromoted && hit.IsTab && _press.IsTab && hit.DocumentId == _press.DocumentId);
    public bool EndPointer(float x, float y, out ToolbarTarget action)
    {
        action = default;
        if (!HasPress) return false;
        bool same = SamePress(HitTarget(x, y));
        var pressed = _press;
        _press = default; _pressPromoted = false;
        var hit = HitTarget(x, y);
        _hover = hit;
        if (same && IsEnabled(pressed) && RectOf(pressed) == _pressRect) action = pressed;
        return true;
    }
    public bool CancelPointer() { bool had = HasPress; _press = default; _pressPromoted = false; return had; }

    private bool Visible((float x, float w) rect) => rect.w > 0f && rect.x >= 0f && rect.x + rect.w <= _width + 0.01f;
    public int HitButton(float x, float y, float toolH = 36f)
    {
        if (x < 0f || x >= _width || y < 0f || y >= toolH) return -1;
        if (Visible(OverflowRect) && x >= OverflowRect.x && x < OverflowRect.x + OverflowRect.w) return 6;
        for (int i = 0; i < _btnRects.Length; i++)
            if (Visible(_btnRects[i]) && x >= _btnRects[i].x && x < _btnRects[i].x + _btnRects[i].w) return i;
        for (int i = 0; i < _deskRects.Length; i++)
            if (Visible(_deskRects[i]) && x >= _deskRects[i].x && x < _deskRects[i].x + _deskRects[i].w) return 7 + i;
        return -1;
    }
    public int HitTab(float x, out bool close)
    {
        close = false;
        if (x < 0f || x >= _width || TabRects == null) return -1;
        for (int i = 0; i < TabRects.Count; i++)
        {
            var tab = TabRects[i];
            if (!Visible(tab) || x < tab.x || x >= tab.x + tab.w) continue;
            close = !_tabs[i].IsHome && x >= tab.x + tab.w - 28f;
            return i;
        }
        return -1;
    }
    public ToolbarTarget HitTarget(float x, float y)
    {
        if (y < 0f || y >= ToolH || x < 0f || x >= _width) return default;
        int button = HitButton(x, y);
        if (button >= 0) return ToolbarTarget.ForCommand((ToolbarCommand)button);
        int tab = HitTab(x, out bool close);
        return tab >= 0 && tab < _tabs.Count ? ToolbarTarget.ForTab(_tabs[tab].Id, close) : default;
    }
    public (float x, float w) RectOf(ToolbarTarget target)
    {
        if (target.Part == ToolbarPart.Command)
            return target.Command == ToolbarCommand.Documents ? OverflowRect : (int)target.Command is >= 0 and < 6 ? _btnRects[(int)target.Command]
                : (int)target.Command is >= 7 and <= 9 ? _deskRects[(int)target.Command - 7] : default;
        if (target.IsTab && TabRects != null)
        {
            int i = IndexOf(target.DocumentId);
            if (i >= 0 && i < TabRects.Count && TabRects[i].w > 0)
            {
                var rect = TabRects[i];
                if (_tabs[i].IsHome) return target.Part == ToolbarPart.Close ? default : rect;
                return target.Part == ToolbarPart.Close ? (rect.x + rect.w - 28f, 28f) : (rect.x, rect.w - 28f);
            }
        }
        return default;
    }
    private int IndexOf(Guid id)
    {
        for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Id == id) return i;
        return -1;
    }
    public bool IsEnabled(ToolbarTarget target)
    {
        if (_state == null || !Visible(RectOf(target))) return false;
        return target.Part == ToolbarPart.Command ? ButtonEnabled((int)target.Command, _state) : target.IsTab && !_state.Composing;
    }
    public string FocusDescription => Description(_focus);
    public string Description(ToolbarTarget target)
    {
        if (target.Part == ToolbarPart.Command)
        {
            string label = target.Command switch
            {
                ToolbarCommand.Open => "打开",
                ToolbarCommand.ToggleEdit => _state?.Editing == true && !Desk ? "预览" : "编辑",
                ToolbarCommand.Read => "阅读",
                ToolbarCommand.Appearance => "外观",
                ToolbarCommand.Palette => "命令面板",
                ToolbarCommand.Save => "保存",
                ToolbarCommand.ToggleFind => "查找",
                ToolbarCommand.ToggleToc => "目录",
                ToolbarCommand.More => "更多",
                _ => "文档列表"
            };
            bool carriesDirty = _state?.Dirty == true && target == DirtyMarkerTarget;
            return label + (carriesDirty ? "（未保存）" : "")
                + (target.Command == ToolbarCommand.Documents && OverflowDirty ? "（含未保存的隐藏文档）" : "");
        }
        int i = IndexOf(target.DocumentId);
        if (!target.IsTab || i < 0) return "";
        var tab = _tabs[i];
        return (target.Part == ToolbarPart.Close ? (tab.Dirty ? "关闭未保存文档：" : "关闭标签：") : "标签：") + tab.DisplayTitle
            + (tab.Dirty && target.Part == ToolbarPart.Tab ? "（未保存）" : "");
    }

    private nint Label(string text)
    {
        if (!_labels.TryGetValue(text, out nint layout)) _labels[text] = layout = Dw.MkLayout(text, UiFormat, 10000f);
        return layout;
    }
    private static void Surface(float x, float width, nint brush)
    {
        var rect = new D2D1_ROUNDED_RECT
        {
            rect = new D2D1_RECT_F { left = x, top = 4f, right = x + width, bottom = ToolH - 4f },
            radiusX = 4f,
            radiusY = 4f
        };
        PFillRound(Rt, &rect, brush);
    }
    private static void Text(nint layout, float x, nint brush, float dy = 0f)
    {
        float height = Dw.LayoutH(layout);
        PDraw(Rt, new D2D1_POINT_2F { x = x, y = (ToolH - height) / 2f + dy }, layout, brush, 0);
    }
    private static void DirtyDot(float x, float y)
    {
        var dot = new D2D1_ROUNDED_RECT
        {
            rect = new D2D1_RECT_F { left = x - 2f, top = y - 2f, right = x + 2f, bottom = y + 2f },
            radiusX = 2f,
            radiusY = 2f
        };
        PFillRound(Rt, &dot, BWarn);
    }
    private string ButtonLabel(int index) => index switch { 0 => "打开", 1 => _state.Editing ? "预览" : "编辑", 2 => "保存", 3 => "查找", 4 => "目录", _ => "更多 ▾" };

    public void Prepare(float width, CmdState state, IReadOnlyList<ToolbarTab> tabs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = MathF.Max(0f, width);
        if (_prepared && _width == width && _state == state && ReferenceEquals(_tabSnapshot, tabs)) return;
        _prepared = true; LayoutPassCount++;
        PruneTabs(tabs);
        _width = MathF.Max(0f, width); _state = state; _tabs = tabs ?? Array.Empty<ToolbarTab>();
        if (Desk) { PrepareDesk(); return; }
        var widths = new float[6];
        for (int i = 0; i < 6; i++) widths[i] = Dw.LayoutW(Label(ButtonLabel(i))) + 16f + (i == 2 ? 8f : 0f);
        var folded = new List<ToolbarCommand>();
        // Group gaps: [Open] [Edit Save] [Find TOC More]; the last visible button of a group gets 12.
        float Gap(int i) => i is 0 or 2 or 5 || i == 1 && folded.Contains(ToolbarCommand.Save) ? 12f : 4f;
        float StripEnd()
        {
            float end = 8f;
            for (int i = 0; i < 6; i++) if (!folded.Contains((ToolbarCommand)i)) end += widths[i] + Gap(i);
            return end;
        }
        bool Fits() => _tabs.Count > 1 ? StripEnd() + 8f + TabAreaMin <= _width : StripEnd() - 4f <= _width;
        foreach (var candidate in FoldOrder)
        {
            if (Fits()) break;
            folded.Add(candidate);
        }
        float x = 8f;
        for (int i = 0; i < 6; i++)
        {
            if (folded.Contains((ToolbarCommand)i)) { _btnRects[i] = (x, 0f); continue; }
            _btnRects[i] = (x, widths[i]);
            x += widths[i] + Gap(i);
        }
        FoldedCommands = folded.ToArray();
        DirtyMarkerTarget = ToolbarTarget.ForCommand(folded.Contains(ToolbarCommand.Save) ? ToolbarCommand.More : ToolbarCommand.Save);
        _directoryEnabled = ButtonEnabled(4, state);
        LayoutTabs(x, MathF.Max(0f, _width - x - 8f), 180f, 2);
        FinishPrepare(Enumerable.Range(0, 6));
    }

    // 标签条排布（经典与 desk 共用）：从 x 起、可用 available 宽，单个标签最宽 maxTab；标签数达到 minTabs 才显示。
    // natural：desk 的标签按内容取宽（演示 .tab 由内容撑开，最宽 220），经典界面等宽。
    private float LayoutTabs(float x, float available, float maxTab, int minTabs, Func<ToolbarTab, float> natural = null)
    {
        TabRects = null; OverflowRect = default; OverflowDirty = false; HiddenCount = 0;
        if (_tabs.Count >= minTabs)
        {
            TabRects = Enumerable.Repeat((0f, 0f), _tabs.Count).ToList();
            int capacity = (int)((available + 4f) / 124f);
            bool overflow = capacity < _tabs.Count;
            if (overflow) capacity = available >= 108f ? Math.Max(1, (int)((available - 36f + 4f) / 124f)) : 0;
            int count = Math.Min(_tabs.Count, capacity);
            int active = Math.Max(0, Enumerable.Range(0, _tabs.Count).FirstOrDefault(i => _tabs[i].Active));
            int previous = IndexOf(_firstVisible);
            int first = previous < 0 ? Math.Clamp(active - count / 2, 0, _tabs.Count - count) : Math.Clamp(previous, 0, _tabs.Count - count);
            if (count > 0)
            {
                if (active < first) first = active;
                if (active >= first + count) first = active - count + 1;
                _firstVisible = _tabs[first].Id;
            }
            float tabWidth = count > 0 ? MathF.Min(maxTab, (available - (overflow ? 36f : 0f)) / count - 4f) : 0f;
            for (int i = first; i < first + count; i++)
            {
                float w = natural == null ? tabWidth : MathF.Min(tabWidth, natural(_tabs[i]));
                TabRects[i] = (x, w); x += w + 4f;
            }
            // The entry follows the last visible tab or the strip; it never slides back over commands.
            if (overflow)
            {
                for (int i = 0; i < _tabs.Count; i++)
                    if (TabRects[i].w <= 0) { HiddenCount++; if (_tabs[i].Dirty) OverflowDirty = true; }
                if (available >= 36f) { OverflowRect = (x, 32f); x += 36f; }   // 放不下「+n」时不画（Ctrl+Tab 与命令面板仍可切换）
            }
        }
        return x;
    }

    // 焦点目标与悬停/按压的收尾（经典与 desk 共用）。commandsFirst：经典界面命令在标签之前；desk 标签在左、工具在右。
    // leading：desk 里画在标签左边的命令（印章），按视觉顺序排在标签之前。
    private void FinishPrepare(IEnumerable<int> commands, bool commandsFirst = true, IEnumerable<int> leading = null)
    {
        var targets = new List<ToolbarTarget>();
        void AddCommands(IEnumerable<int> ids)
        {
            foreach (int i in ids)
            {
                var target = ToolbarTarget.ForCommand((ToolbarCommand)i);
                if (IsEnabled(target)) targets.Add(target);
            }
        }
        if (commandsFirst) AddCommands(commands);
        if (leading != null) AddCommands(leading);
        if (TabRects != null)
            for (int i = 0; i < _tabs.Count; i++)
                if (Visible(TabRects[i]) && !_state.Composing)
                {
                    targets.Add(ToolbarTarget.ForTab(_tabs[i].Id));
                    if (!_tabs[i].IsHome) targets.Add(ToolbarTarget.ForTab(_tabs[i].Id, true));
                }
        var documents = ToolbarTarget.ForCommand(ToolbarCommand.Documents);
        if (IsEnabled(documents)) targets.Add(documents);
        if (!commandsFirst) AddCommands(commands);
        _focusTargets = targets;
        if (HasFocus && !targets.Contains(_focus))
        {
            var activeTab = _tabs.FirstOrDefault(t => t.Active);
            var activeTarget = activeTab == null ? default : ToolbarTarget.ForTab(activeTab.Id);
            _focus = targets.Contains(activeTarget) ? activeTarget : targets.Contains(documents) ? documents
                : targets.Contains(ToolbarTarget.ForCommand(ToolbarCommand.More)) ? ToolbarTarget.ForCommand(ToolbarCommand.More) : targets.FirstOrDefault();
        }
        if (_hover.Part != ToolbarPart.None && !Visible(RectOf(_hover))) _hover = default;
        if (HasPress && (!IsEnabled(_press) || RectOf(_press) != _pressRect)) _press = default;
    }

    // ---------- U6a：desk 顶栏 ----------
    private void SetDeskRect(DeskTool tool, (float x, float w) r)
    {
        switch (tool)
        {
            case DeskTool.Find: _btnRects[3] = r; break;
            case DeskTool.Toc: _btnRects[4] = r; break;
            case DeskTool.Edit: _btnRects[1] = r; break;
            case DeskTool.More: _btnRects[5] = r; break;
            case DeskTool.Read: _deskRects[0] = r; break;
            case DeskTool.Appearance: _deskRects[1] = r; break;
        }
    }

    internal static ToolbarCommand CommandOf(DeskTool tool) => tool switch
    {
        DeskTool.Find => ToolbarCommand.ToggleFind,
        DeskTool.Toc => ToolbarCommand.ToggleToc,
        DeskTool.Edit => ToolbarCommand.ToggleEdit,
        DeskTool.Read => ToolbarCommand.Read,
        DeskTool.Appearance => ToolbarCommand.Appearance,
        _ => ToolbarCommand.More,
    };

    private void PrepareDesk()
    {
        bool narrow = DeskToolbarLayout.Narrow(_width);
        _deskSlots = DeskToolbarLayout.Layout(Caption ? DeskFrame.Left(_width) : _width, narrow, t => Dw.LayoutW(Label(t)));
        Array.Clear(_btnRects); Array.Clear(_deskRects);
        foreach (var slot in _deskSlots) SetDeskRect(slot.Tool, (slot.X, slot.W));
        _deskRects[2] = (0f, SealWidth);   // U11：印章「阅」占左边 54
        _segment = DeskToolbarLayout.Segment(_deskSlots);
        FoldedCommands = Array.Empty<ToolbarCommand>();
        DirtyMarkerTarget = default;   // desk 的未保存标记在标签上（墨点）
        _directoryEnabled = ButtonEnabled(4, _state);
        // 标签紧跟印章，给紧随其后的「+」留 28 + 4，与工具区之间留 8。
        // 试用修复：窗口很窄、连一个标签带「+n」都放不下时，让出「+」的位置给「+n」（打开仍有 Ctrl+O 与命令面板），避免「+」压在「查找」上。
        float strip = MathF.Max(0f, DeskToolbarLayout.Left(_deskSlots) - SealWidth - 8f);
        bool showAdd = strip - 32f >= 108f || _tabs.Count * 124f <= strip - 32f + 4f;
        float room = MathF.Max(0f, strip - (showAdd ? 32f : 0f));
        float x = LayoutTabs(SealWidth, room, DeskMetrics.TabMaxWidth, 1, NaturalTabWidth);
        _btnRects[0] = (TabRects == null ? SealWidth : x, showAdd ? AddSize : 0f);   // 「+」打开（演示 .tab-add）
        FinishPrepare(new[] { 0, 3, 4, 2, 7, 1, 8, 5 }, commandsFirst: false, leading: new[] { 9 });   // 试用修复：键盘顺序与视觉一致，印章在最左
    }

    // 36 高工具栏里标签离顶 5（U6b 并入 44 高标题栏后改为离顶 8，标签高仍 36−5）。
    // 左 14 + 标题 + 8 + 关闭钮 20 + 右 8。
    private readonly Dictionary<string, float> _tabNatural = new();
    private float NaturalTabWidth(ToolbarTab tab)
    {
        // U10：起始页标签＝左 14 + 图标 14 + 8 + 标题 + 右 8；窗口不宽于 1180 时只留图标（左右各 12）。
        if (tab.IsHome) return HomeTabNarrow ? 38f : MathF.Ceiling(14f + 14f + 8f + MeasureText(tab.DisplayTitle) + 8f);
        if (!_tabNatural.TryGetValue(tab.DisplayTitle, out float w))
        {
            if (_tabNatural.Count > 128) _tabNatural.Clear();
            _tabNatural[tab.DisplayTitle] = w = MathF.Max(96f, MathF.Ceiling(MeasureText(tab.DisplayTitle)) + 50f);
        }
        return w;
    }

    bool HomeTabNarrow => _width <= 1180f;

    // U10：起始页标签：房子图标（演示 20 格路径，缩到 14）+「起始页」；窄窗只留图标。
    private void PaintHomeTab(float x, nint ink)
    {
        float ix = x + (HomeTabNarrow ? 12f : 14f), cy = (ToolH + TabTop) / 2f, k = 14f / 20f, iy = cy - 7f;
        nint icon = HighContrast ? BInk : BMuted;
        (float X, float Y)[] p = { (3.5f, 9f), (10f, 3.8f), (16.5f, 9f), (16.5f, 16.2f), (12.2f, 16.2f), (12.2f, 11.7f), (7.8f, 11.7f), (7.8f, 16.2f), (3.5f, 16.2f), (3.5f, 9f) };
        for (int i = 0; i + 1 < p.Length; i++) StrokeLine(ix + p[i].X * k, iy + p[i].Y * k, ix + p[i + 1].X * k, iy + p[i + 1].Y * k, icon, 1.5f * k);
        if (!HomeTabNarrow) Text(Label(Home.TabTitle), ix + 14f + 8f, ink, TabTop / 2f);
    }

    // 标签高 36、贴底（44 高标题栏里离顶 8）。
    const float AddSize = 28f, TabRadius = 9f;
    internal const float SealWidth = 54f;
    static nint _sealFormat, _sealGlyph;
    static string _sealFamily;

    // U11：印章「阅」（演示 .seal i）：24 见方朱色、圆角 4、斜 -4°，边上一圈半透明米白细线；衬线粗体 13.5 米白字。
    // 悬停时摆正并放大到 1.07（演示 transition .4s，这里直接到位）。
    private void PaintSeal(bool hot)
    {
        string family = SerifFamily ?? "SimSun";
        if (_sealGlyph == 0 || _sealFamily != family)
        {
            if (_sealGlyph != 0) Marshal.Release(_sealGlyph);
            if (_sealFormat != 0) Marshal.Release(_sealFormat);
            _sealFormat = Dw.MkFormat(family, 700, 13.5f);
            _sealGlyph = Dw.MkLayout("阅", _sealFormat, 10000f);
            _sealFamily = family;
        }
        float cx = SealWidth / 2f, cy = ToolH / 2f, k = hot ? 1.07f : 1f;
        float rad = (hot ? 0f : -4f) * MathF.PI / 180f, cos = MathF.Cos(rad) * k, sin = MathF.Sin(rad) * k;
        var m = new D2D1_MATRIX_3X2_F { m11 = cos, m12 = sin, m21 = -sin, m22 = cos, dx = cx - cos * cx + sin * cy, dy = cy - sin * cx - cos * cy };
        SetTransform(&m);
        try
        {
            Round(cx - 12f, cy - 12f, 24f, 24f, 4f, BAccent);
            if (!HighContrast)
            {
                SetOpacity(BGlint, 0.5f);
                var ring = new D2D1_ROUNDED_RECT { rect = new() { left = cx - 11.25f, top = cy - 11.25f, right = cx + 11.25f, bottom = cy + 11.25f }, radiusX = 3.25f, radiusY = 3.25f };
                PStrokeRound(Rt, &ring, BGlint, 1.5f, 0);
            }
            nint glyph = _sealGlyph;
            SetOpacity(BGlint, 0.94f);
            PDraw(Rt, new D2D1_POINT_2F { x = cx - Dw.LayoutW(glyph) / 2f, y = cy - Dw.LayoutH(glyph) / 2f }, glyph, HighContrast ? BCanvas : BGlint, 0);
            SetOpacity(BGlint, 1f);
        }
        finally
        {
            var identity = new D2D1_MATRIX_3X2_F { m11 = 1f, m22 = 1f };
            SetTransform(&identity);
        }
    }
    static float TabTop => MathF.Max(0f, ToolH - DeskMetrics.TabHeight);

    private void PaintDesk(CmdState state)
    {
        var bounds = new D2D1_RECT_F { right = _width, bottom = ToolH };
        PFill(Rt, &bounds, HighContrast ? BCanvas : BDesk);
        MixQuiet(HighContrast ? 0f : Quiet);   // U6c：读时退后的混合色，每帧按当前纸张重算
        nint idle = HighContrast ? BInk : Quiet > 0f ? BQuietIdle : BInk2;
        if (Visible(_deskRects[2])) PaintSeal(!_state.Composing && _hover == ToolbarTarget.ForCommand(ToolbarCommand.Palette));
        // 标签
        if (TabRects != null)
            for (int i = 0; i < _tabs.Count; i++)
            {
                var r = TabRects[i]; if (r.w <= 0) continue;
                var tab = _tabs[i];
                bool over = !_state.Composing && _hover.IsTab && _hover.DocumentId == tab.Id;
                if (tab.Active) ActiveTab(r.x, r.w);
                else if (over) TabShape(r.x, r.w, BDeskHover);
                if (tab.IsHome) { PaintHomeTab(r.x, tab.Active || over || HighContrast ? BInk : idle); continue; }
                if (!_tabLayouts.TryGetValue(tab.DisplayTitle, out var cached) || cached.w != r.w)
                {
                    if (cached.lay != 0) Marshal.Release(cached.lay);
                    _tabLayouts[tab.DisplayTitle] = cached = (Dw.TrimLayout(FitTabTitle(tab.DisplayTitle, r.w - 50f, MeasureText), UiFormat, r.w - 50f), r.w);
                }
                Text(cached.lay, r.x + 14f, tab.Active || over || HighContrast ? BInk : idle, TabTop / 2f);
                // 关闭钮 20×20，距右 8；未保存时是朱色墨点，悬停变回 ×（规格 §3、§8.1）。
                float cx = r.x + r.w - 18f, cy = (ToolH + TabTop) / 2f;
                bool overClose = !_state.Composing && _hover.Part == ToolbarPart.Close && _hover.DocumentId == tab.Id;
                if (overClose) Round(cx - 10f, cy - 10f, 20f, 20f, 5f, tab.Active ? BSurfaceHover : BDeskHover);
                InkPose? ink = Ink?.Pose(tab.Id, tab.Dirty, Environment.TickCount64) ?? (tab.Dirty ? InkDrop.Wet : null);   // U9c：墨未干
                if (ink is InkPose pose && !overClose) InkDot(cx, cy, pose);
                else
                {
                    nint brush = _state.Composing ? BMuted : overClose ? BInk : BMuted;
                    PLine(Rt, new() { x = cx - 3.5f, y = cy - 3.5f }, new() { x = cx + 3.5f, y = cy + 3.5f }, brush, 1.4f, 0);
                    PLine(Rt, new() { x = cx - 3.5f, y = cy + 3.5f }, new() { x = cx + 3.5f, y = cy - 3.5f }, brush, 1.4f, 0);
                }
            }
        if (Visible(OverflowRect))
        {
            var target = ToolbarTarget.ForCommand(ToolbarCommand.Documents);
            bool over = _hover == target;
            if (over) Round(OverflowRect.x, (ToolH - 28f) / 2f + 3f, OverflowRect.w, 28f, 7f, BDeskHover);
            nint glyph = Label(OverflowLabel);
            Text(glyph, OverflowRect.x + (OverflowRect.w - Dw.LayoutW(glyph)) / 2f, over || HighContrast ? BInk : idle, 3f);
            if (OverflowDirty) InkDot(OverflowDotCenter.x, OverflowDotCenter.y);
        }
        // 「+」打开
        var add = _btnRects[0];
        if (Visible(add))
        {
            bool over = _hover == ToolbarTarget.ForCommand(ToolbarCommand.Open);
            float top = (ToolH - AddSize) / 2f + 3f, cx = add.x + add.w / 2f, cy = top + AddSize / 2f;
            if (over && ButtonEnabled(0, state)) Round(add.x, top, add.w, AddSize, 7f, BDeskHover);
            nint brush = !ButtonEnabled(0, state) ? BMuted : over || HighContrast ? BInk : idle;
            StrokeLine(cx, cy - 4.5f, cx, cy + 4.5f, brush, 1.5f);
            StrokeLine(cx - 4.5f, cy, cx + 4.5f, cy, brush, 1.5f);
        }
        // 分段按钮底
        float bh = DeskToolbarLayout.ButtonHeight, bt = (ToolH - bh) / 2f;
        if (_segment.w > 0f) Round(_segment.x, bt, _segment.w, bh, DeskToolbarLayout.SegRadius, HighContrast ? BCanvas : BDesk2);
        foreach (var slot in _deskSlots)
        {
            var cmd = CommandOf(slot.Tool);
            var target = ToolbarTarget.ForCommand(cmd);
            bool enabled = ButtonEnabled((int)cmd, state);
            bool over = enabled && _hover == target, down = over && _press == target;
            nint ink = !enabled ? BMuted : over || HighContrast ? BInk : idle;
            if (slot.Tool is DeskTool.Read or DeskTool.Edit)
            {
                bool on = (slot.Tool == DeskTool.Edit) == state.Editing;
                float inset = DeskToolbarLayout.SegPad;
                if (on)
                {
                    float keep = HighContrast ? 1f : 1f - Quiet;   // 退后时选中底色与阴影淡出（演示 .quiet .seg button.on）
                    if (!HighContrast && BShade != IntPtr.Zero && keep > 0f) { SetOpacity(BShade, 0.08f * keep); Round(slot.X, bt + inset + 1f, slot.W, bh - inset * 2f, DeskToolbarLayout.SegButtonRadius, BShade); SetOpacity(BShade, 1f); }
                    if (keep > 0f) { nint onBrush = HighContrast ? BSel : BSegOn; SetOpacity(onBrush, keep); Round(slot.X, bt + inset, slot.W, bh - inset * 2f, DeskToolbarLayout.SegButtonRadius, onBrush); SetOpacity(onBrush, 1f); }
                }
                nint seg = Label(DeskToolbarLayout.LabelOf(slot.Tool));
                if (!enabled) SetOpacity(BInk2, 0.35f);
                Text(seg, slot.X + (slot.W - Dw.LayoutW(seg)) / 2f, !enabled ? BInk2 : over || HighContrast ? BInk : on ? (Quiet > 0f ? BQuietOn : BInk) : idle);
                SetOpacity(BInk2, 1f);
                continue;
            }
            bool lit = cmd == ToolbarCommand.ToggleFind && state.FindOpen || cmd == ToolbarCommand.ToggleToc && state.TocOpen && !state.Editing;
            if (lit) Round(slot.X, bt, slot.W, bh, DeskToolbarLayout.ButtonRadius, HighContrast ? BSel : BAccentSoft);
            else if (over || down) Round(slot.X, bt, slot.W, bh, DeskToolbarLayout.ButtonRadius, down ? BSel : BDeskHover);
            if (lit && !HighContrast) ink = BAccent;
            nint drawn = ink;
            if (!enabled) { drawn = BInk2; SetOpacity(BInk2, 0.35f); }   // 禁用 35% 透明（规格 §3）
            float x = slot.X + DeskToolbarLayout.Pad(DeskToolbarLayout.Narrow(_width));
            DeskIcon(slot.Tool, x, ToolH / 2f, drawn); x += DeskToolbarLayout.Icon + DeskToolbarLayout.IconGap;
            if (slot.Label) Text(Label(DeskToolbarLayout.LabelOf(slot.Tool)), x, drawn);
            SetOpacity(BInk2, 1f);
        }
        PaintCaption();
        // 键盘焦点：1.5 描边、外扩 1、圆角 6（演示 :focus-visible）
        if (HasFocus)
        {
            var r = RectOf(_focus);
            if (Visible(r))
            {
                bool tabLike = _focus.IsTab;
                float top = tabLike ? TabTop + 1f : bt, h = tabLike ? ToolH - TabTop - 3f : bh;
                if (_focus.Command == ToolbarCommand.Open || _focus.Command == ToolbarCommand.Documents) { top = (ToolH - AddSize) / 2f + 3f; h = AddSize; }
                float w = HighContrast ? DeskMetrics.FocusRingWidthHighContrast : DeskMetrics.FocusRingWidth;
                SetOpacity(BAccent, HighContrast ? 1f : 0.75f);
                var ring = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = r.x - 1f, top = top - 1f, right = r.x + r.w + 1f, bottom = top + h + 1f }, radiusX = 6f, radiusY = 6f };
                PStrokeRound(Rt, &ring, BAccent, w, 0);
                SetOpacity(BAccent, 1f);
            }
        }
    }

    // U6b：窗口按钮（演示 .caption）。悬停 desk-2，关闭悬停红底白字；最大化时画“还原”；失去激活时图标转 muted。
    private void PaintCaption()
    {
        if (!Caption) return;
        float left = DeskFrame.Left(_width);
        for (int i = 0; i < 3; i++)
        {
            var part = (CaptionPart)(i + 1);
            float x = left + i * DeskFrame.ButtonWidth;
            bool over = CaptionHover == part, down = CaptionPressed == part && over;
            nint glyph = HighContrast ? BInk : !WindowActive ? BMuted : Quiet > 0f ? BQuietIdle : BInk2;
            if (over)
            {
                var r = new D2D1_RECT_F { left = x, top = 0f, right = x + DeskFrame.ButtonWidth, bottom = ToolH };
                if (part == CaptionPart.Close && !HighContrast && BCloseHover != 0)
                {
                    SetOpacity(BCloseHover, down ? 0.85f : 1f); PFill(Rt, &r, BCloseHover); SetOpacity(BCloseHover, 1f);
                    glyph = BCloseGlyph;
                }
                else { PFill(Rt, &r, down || HighContrast ? BSel : BDeskHover); if (!HighContrast) glyph = BInk; }
            }
            float cx = x + DeskFrame.ButtonWidth / 2f, cy = ToolH / 2f, g = DeskFrame.Glyph / 2f;
            switch (part)
            {
                case CaptionPart.Min:
                    StrokeLine(cx - g, cy + 0.5f, cx + g, cy + 0.5f, glyph, 1f);
                    break;
                case CaptionPart.Max when Maximized:
                    {
                        // 还原：前框完整，后框只露出上沿和右沿
                        var front = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = cx - g + 0.5f, top = cy - g + 2.5f, right = cx + g - 2.5f, bottom = cy + g - 0.5f }, radiusX = 1.5f, radiusY = 1.5f };
                        PStrokeRound(Rt, &front, glyph, 1f, 0);
                        StrokeLine(cx - g + 2.5f, cy - g + 0.5f, cx + g - 1.5f, cy - g + 0.5f, glyph, 1f);
                        StrokeLine(cx + g - 0.5f, cy - g + 1.5f, cx + g - 0.5f, cy + g - 2.5f, glyph, 1f);
                        break;
                    }
                case CaptionPart.Max:
                    {
                        var box = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = cx - g + 0.5f, top = cy - g + 0.5f, right = cx + g - 0.5f, bottom = cy + g - 0.5f }, radiusX = 1.5f, radiusY = 1.5f };
                        PStrokeRound(Rt, &box, glyph, 1f, 0);
                        break;
                    }
                default:
                    StrokeLine(cx - g + 0.5f, cy - g + 0.5f, cx + g - 0.5f, cy + g - 0.5f, glyph, 1f);
                    StrokeLine(cx + g - 0.5f, cy - g + 0.5f, cx - g + 0.5f, cy + g - 0.5f, glyph, 1f);
                    break;
            }
        }
    }

    // 当前标签：与纸面同色、连成一片；顶端 2 高朱线（左右各缩 14）；底部两侧内凹圆角（演示 .tab.active::before）。
    private static void ActiveTab(float x, float w)
    {
        nint paper = BCanvas;
        TabShape(x, w, paper);
        if (!HighContrast)
        {
            var l = new D2D1_RECT_F { left = x - TabRadius, top = ToolH - TabRadius, right = x, bottom = ToolH };
            var r = new D2D1_RECT_F { left = x + w, top = ToolH - TabRadius, right = x + w + TabRadius, bottom = ToolH };
            PFill(Rt, &l, paper); PFill(Rt, &r, paper);
            var el = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = x - TabRadius, y = ToolH - TabRadius }, radiusX = TabRadius, radiusY = TabRadius };
            var er = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = x + w + TabRadius, y = ToolH - TabRadius }, radiusX = TabRadius, radiusY = TabRadius };
            PFillEllipse(Rt, &el, BDesk); PFillEllipse(Rt, &er, BDesk);
        }
        else
        {
            // 高对比度：演示 .tab.active 加 1px 边框
            var edge = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = x + 0.5f, top = TabTop + 0.5f, right = x + w - 0.5f, bottom = ToolH + TabRadius }, radiusX = TabRadius, radiusY = TabRadius };
            PStrokeRound(Rt, &edge, BInk, 1f, 0);
        }
        var line = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = x + 14f, top = TabTop, right = x + w - 14f, bottom = TabTop + DeskMetrics.TabAccentLine }, radiusX = 1f, radiusY = 1f };
        PFillRound(Rt, &line, BAccent);
    }

    // 标签外形：上方圆角 9，下方直角（圆角落在工具栏下缘之外被裁掉）。
    private static void TabShape(float x, float w, nint brush)
    {
        var rr = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = x, top = TabTop, right = x + w, bottom = ToolH + TabRadius }, radiusX = TabRadius, radiusY = TabRadius };
        PFillRound(Rt, &rr, brush);
    }

    private static void Round(float x, float y, float w, float h, float radius, nint brush)
    {
        var rr = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = x, top = y, right = x + w, bottom = y + h }, radiusX = radius, radiusY = radius };
        PFillRound(Rt, &rr, brush);
    }

    // 未保存的墨点：8 直径朱色圆点，外圈 1.5 淡朱晕（演示 .tab .dot）。
    // U9c：各标签墨滴的时间线（宿主持有；null 时有改动一律画静止的湿墨滴）。
    public InkDrops Ink { get; set; }

    private static void InkDot(float cx, float cy) => InkDot(cx, cy, InkDrop.Wet);

    // 墨滴：8 直径朱色，外圈 1.5 淡晕（朱色 16%），左上角极小的湿润高光；干的过程中按姿态缩放、褪色、淡出。
    private static void InkDot(float cx, float cy, InkPose p)
    {
        float s = p.Scale;
        if (s <= 0.01f || p.Opacity <= 0.001f) return;
        if (!HighContrast && p.Halo > 0f)
        {
            SetOpacity(BAccent, InkDrop.HaloAlpha * p.Halo * p.Opacity);
            var halo = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = cx, y = cy }, radiusX = (InkDrop.Radius + InkDrop.HaloWidth) * s, radiusY = (InkDrop.Radius + InkDrop.HaloWidth) * s };
            PFillEllipse(Rt, &halo, BAccent);
        }
        bool dull = p.Saturate < 1f && !HighContrast && ActivePaper is PaperColors paper;
        if (dull) SetBrushRgb(BAccent, InkDrop.Filter(ActivePaper.Value.Accent, p.Saturate, p.Brightness));
        SetOpacity(BAccent, p.Opacity);
        var dot = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = cx, y = cy }, radiusX = InkDrop.Radius * s, radiusY = InkDrop.Radius * s };
        PFillEllipse(Rt, &dot, BAccent);
        SetOpacity(BAccent, 1f);
        if (dull) SetBrushRgb(BAccent, ActivePaper.Value.Accent);
        if (!HighContrast && p.Gloss > 0f)
        {
            // 演示 radial-gradient(circle at 34% 30%, 白 .3 → .08@1.4 → 透明@2.4)：两层同心圆近似。
            var at = new D2D1_POINT_2F { x = cx - 1.28f * s, y = cy - 1.6f * s };
            var wide = new D2D1_ELLIPSE { point = at, radiusX = 2.4f * s, radiusY = 2.4f * s };
            SetOpacity(BGlint, 0.08f * p.Gloss * p.Opacity); PFillEllipse(Rt, &wide, BGlint);
            var core = new D2D1_ELLIPSE { point = at, radiusX = 1.4f * s, radiusY = 1.4f * s };
            SetOpacity(BGlint, 0.22f * p.Gloss * p.Opacity); PFillEllipse(Rt, &core, BGlint);
            SetOpacity(BGlint, 1f);
        }
    }

    // 工具图标：演示 20 格 SVG 缩到 16。
    private static void DeskIcon(DeskTool tool, float x, float cy, nint brush)
    {
        const float k = 16f / 20f, sw = 1.6f * k;
        float y = cy - 8f;
        float X(float u) => x + u * k;
        float Y(float v) => y + v * k;
        void Line(float u1, float v1, float u2, float v2) => StrokeLine(X(u1), Y(v1), X(u2), Y(v2), brush, sw);
        void Circle(float u, float v, float r, bool fill)
        {
            var e = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = X(u), y = Y(v) }, radiusX = r * k, radiusY = r * k };
            if (fill) PFillEllipse(Rt, &e, brush); else PDrawEllipse(Rt, &e, brush, sw, 0);
        }
        switch (tool)
        {
            case DeskTool.Find:
                Circle(8.6f, 8.6f, 5.1f, false); Line(12.4f, 12.4f, 16.6f, 16.6f); break;
            case DeskTool.Toc:
                Line(3.5f, 5f, 16.5f, 5f); Line(7f, 10f, 16.5f, 10f); Line(7f, 15f, 16.5f, 15f);
                Circle(3.8f, 10f, 0.7f, true); Circle(3.8f, 15f, 0.7f, true); break;
            case DeskTool.Appearance:
                Circle(10f, 10f, 6.3f, false);
                var half = new D2D1_RECT_F { left = X(10f), top = Y(0f), right = X(20f), bottom = Y(20f) };
                PPushClip(Rt, &half, 0); Circle(10f, 10f, 6.3f, true); PPopClip(Rt); break;
            case DeskTool.More:
                Circle(4.5f, 10f, 1.4f, true); Circle(10f, 10f, 1.4f, true); Circle(15.5f, 10f, 1.4f, true); break;
        }
    }

    public void Paint(nint hdc, RECT clientRect, float scale, CmdState state, IReadOnlyList<ToolbarTab> tabs = null, bool hidden = false)
    {
        Prepare((clientRect.R - clientRect.L) / scale, state, tabs);
        var bounds = new D2D1_RECT_F { right = _width, bottom = ToolH };
        PBind(Rt, hdc, &clientRect);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt); PPushClip(Rt, &bounds, 0);
        ulong a = 0, b = 0;
        try
        {
            PFill(Rt, &bounds, Desk && !HighContrast ? BDesk : BCanvas);
            if (hidden) { if (Desk) PaintCaption(); return; }   // 阶段 B Zen:保留几何,只画画布(finally 负责收尾)；窗口按钮保留（U6b）
            if (Desk) { PaintDesk(state); return; }
            for (int i = 0; i < 6; i++)
            {
                var r = _btnRects[i]; var target = ToolbarTarget.ForCommand((ToolbarCommand)i);
                if (r.w <= 0) continue;   // folded into More (A2-B4)
                bool over = _hover == target, down = _press == target && _hover == target;
                if (ButtonEnabled(i, state) && (over || down)) Surface(r.x, r.w, down && !(i == 2 && state.Dirty) ? BSel : BCode);
                Text(Label(ButtonLabel(i)), r.x + 8f, ButtonEnabled(i, state) ? BInk : BMuted);
                if (i == 3 && state.FindOpen || i == 4 && state.TocOpen && !state.Editing)
                    FillLine(r.x + 8f, ToolH - 2f, r.x + r.w - 8f, ToolH - 2f, BAccent, 2f);
            }
            if (state.Dirty)
            {
                var carrier = RectOf(DirtyMarkerTarget);
                if (DirtyMarkerTarget.Command == ToolbarCommand.Save) DirtyDot(carrier.x + carrier.w - 10f, ToolH / 2f);
                else if (carrier.w > 0) DirtyDot(carrier.x + carrier.w - 4f, 9f);   // badge on More: Save is folded
            }
            if (TabRects != null)
                for (int i = 0; i < _tabs.Count; i++)
                {
                    var r = TabRects[i]; if (r.w <= 0) continue;
                    var tab = _tabs[i];
                    bool over = !_state.Composing && _hover.IsTab && _hover.DocumentId == tab.Id;
                    bool focused = _focus.IsTab && _focus.DocumentId == tab.Id;
                    bool down = !_state.Composing && _press.IsTab && _press.DocumentId == tab.Id && SamePress(_hover);
                    if (over || down) Surface(r.x, r.w, down && !tab.Dirty ? BSel : BCode);
                    if (!_tabLayouts.TryGetValue(tab.DisplayTitle, out var cached) || cached.w != r.w)
                    {
                        if (cached.lay != 0) Marshal.Release(cached.lay);
                        _tabLayouts[tab.DisplayTitle] = cached = (Dw.TrimLayout(FitTabTitle(tab.DisplayTitle, r.w - 40f, MeasureText), UiFormat, r.w - 40f), r.w);
                    }
                    Text(cached.lay, r.x + 8f, tab.Active || LabelInk(false, !_state.Composing, HighContrast) ? BInk : BMuted);
                    bool showClose = AlwaysShowClose || over || focused;
                    if (tab.Dirty && (!showClose || AlwaysShowClose)) DirtyDot(r.x + r.w - (AlwaysShowClose ? 24f : 14f), ToolH / 2f);
                    if (showClose)
                    {
                        float cx = r.x + r.w - 14f, cy = ToolH / 2f;
                        nint brush = _state.Composing ? BMuted : tab.Dirty ? BWarn : BInk;
                        PLine(Rt, new() { x = cx - 3f, y = cy - 3f }, new() { x = cx + 3f, y = cy + 3f }, brush, 1.25f, 0);
                        PLine(Rt, new() { x = cx - 3f, y = cy + 3f }, new() { x = cx + 3f, y = cy - 3f }, brush, 1.25f, 0);
                    }
                    if (tab.Active) FillLine(r.x + 8f, ToolH - 2f, r.x + r.w - 8f, ToolH - 2f, BAccent, 2f);
                }
            if (Visible(OverflowRect))
            {
                var target = ToolbarTarget.ForCommand(ToolbarCommand.Documents);
                bool over = _hover == target;
                Surface(OverflowRect.x, OverflowRect.w, over && ButtonEnabled(6, state) ? BSel : BCode);
                nint glyph = Label(OverflowLabel);
                bool lit = LabelInk(over || _focus == target, ButtonEnabled(6, state), HighContrast);
                Text(glyph, OverflowRect.x + (OverflowRect.w - Dw.LayoutW(glyph)) / 2f, lit ? BInk : BMuted);
                if (OverflowDirty) DirtyDot(OverflowDotCenter.x, OverflowDotCenter.y);
            }
            if (HasFocus)
            {
                var r = RectOf(_focus);
                if (Visible(r))
                {
                    // Review #6: the bottom edge stays 1.5 DIP clear of the 2 DIP Accent underline (ToolH-3..ToolH-1).
                    FillLine(r.x + 2f, 3f, r.x + r.w - 2f, 3f, BAccent, 1f);
                    FillLine(r.x + 2f, ToolH - 5f, r.x + r.w - 2f, ToolH - 5f, BAccent, 1f);
                    FillLine(r.x + 2f, 3f, r.x + 2f, ToolH - 5f, BAccent, 1f);
                    FillLine(r.x + r.w - 2f, 3f, r.x + r.w - 2f, ToolH - 5f, BAccent, 1f);
                }
            }
            if (state.Editing || state.FindOpen) FillLine(0, ToolH - 0.5f, _width, ToolH - 0.5f, BHair, 1f);
        }
        finally { PPopClip(Rt); PEnd(Rt, &a, &b); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOOLINFOW
    {
        public uint size, flags;
        public nint owner, id;
        public RECT rect;
        public nint instance, text, data, reserved;
    }

    // One native tooltip follows the current toolbar target or, when none, an overlay target (ruler, trimmed directory entry); its text buffer outlives the tool.
    public void UpdateHint(nint owner, float x, float y, float scale, IReadOnlyList<ToolbarTab> tabs, string overlayHint = null, OverlayBox overlayBox = default, bool overlayBeside = false)
    {
        string hint = null;
        (float x, float w) target = default;
        var hit = HitTarget(x, y);
        if (x >= 0f)   // 隐藏请求（x<0：离开窗口、弹出菜单抢走指针）不算“离开该目标”
        {
            _hintHit = hit;
            if (_tipMutedSet && hit != _tipMuted) _tipMutedSet = false;
        }
        if (_tipMutedSet) hint = null;
        else if (hit.IsTab)
        {
            int i = IndexOf(hit.DocumentId);
            if (i >= 0) { target = RectOf(hit); hint = hit.Part == ToolbarPart.Close ? (Desk ? _tabs[i].DeskCloseHint : _tabs[i].CloseHint) : Desk ? _tabs[i].DeskHint : _tabs[i].Hint; }
        }
        else if (hit.Part == ToolbarPart.Command)
        {
            int i = (int)hit.Command;
            target = RectOf(hit);
            hint = Desk && i != 6 ? DeskHint(i) : i == 6 ? $"所有打开的文档（{HiddenCount} 个未在标签栏显示）· Ctrl+Tab 切换" : i == 4 && !_directoryEnabled ? $"目录（{(_state is null ? null : Commands.DisabledReason(1006, _state)) ?? "当前不可用"}）· Ctrl+T"
                : i < 5 ? Commands.All[i].MenuText.Replace("\t", " · ") : "更多命令和外观设置 · F6 聚焦工具栏";
            if (_state?.Composing == true) hint += " · 输入法组词中，请先确认候选";
            else if ((i is 1 or 2) && _state?.ReadOnly == true) hint += " · 当前文档只读";
            if (i == 2 && _state?.Dirty == true) hint += " · 未保存";
            if (i == 5 && FoldedCommands.Count > 0) hint += " · 已收起：" + string.Join("、", FoldedCommands.Select(c => ButtonLabel((int)c)));
            if (i == 5 && _state?.Dirty == true && DirtyMarkerTarget.Command == ToolbarCommand.More) hint += " · 未保存";
            if (i == 6 && OverflowDirty) hint += " · 含未保存的隐藏文档";
        }
        bool beside = hint == null && overlayHint != null && overlayBeside;
        OverlayBox box = hint != null ? new OverlayBox(target.x, 0f, target.w, ToolH) : overlayBox;
        if (hint == null) hint = overlayHint;
        if (Desk)
        {
            _deskTip.Update(owner, hint, box, beside ? TipSide.Beside : TipSide.Below, beside, scale);
            _hint = hint; _hintRect = box; _hintScale = scale;
            return;
        }
        if (hint == _hint && box == _hintRect && scale == _hintScale) return;
        _hint = hint; _hintRect = box; _hintScale = scale;
        if (hint == null) { if (_tooltip != 0) Externs.SendMessageW(_tooltip, 0x041C /*TTM_POP*/, 0, 0); return; }
        bool created = _tooltip == 0;
        if (created) _tooltip = Externs.CreateWindowExW(8, "tooltips_class32", null, 0x80000003, 0, 0, 0, 0, owner, 0, Externs.GetModuleHandleW(null), 0);
        if (_tooltip == 0) return;
        Externs.SendMessageW(_tooltip, 0x041C, 0, 0);
        nint old = _tipText;
        _tipText = Marshal.StringToHGlobalUni(hint);
        var info = new TOOLINFOW
        {
            size = (uint)sizeof(TOOLINFOW),
            flags = 0x10 /*TTF_SUBCLASS; TTS_NOPREFIX is already in the tooltip window style*/,
            owner = owner,
            id = 1,
            rect = new RECT { L = (int)(box.X * scale), T = (int)(box.Y * scale), R = (int)(box.Right * scale), B = (int)(box.Bottom * scale) },
            text = _tipText
        };
        Externs.SendMessageW(_tooltip, created ? 0x0432u /*TTM_ADDTOOLW*/ : 0x0436u /*TTM_SETTOOLINFOW*/, 0, (nint)(&info));
        Externs.SendMessageW(_tooltip, 0x0418 /*TTM_SETMAXTIPWIDTH*/, 0, (nint)(480f * scale));
        if (old != 0) Marshal.FreeHGlobal(old);
    }

    // U6a：desk 工具提示只写“做什么 + 快捷键”（规格 §3）。
    private string DeskHint(int i) => i switch
    {
        0 => "打开文件 · Ctrl+O",
        1 or 7 => "阅读 / 编辑 · Ctrl+E",
        2 => "保存 · Ctrl+S",
        3 => "在文档中查找 · Ctrl+F",
        4 => _directoryEnabled ? "打开目录 · Ctrl+T" : $"目录（{(_state is null ? null : Commands.DisabledReason(1006, _state)) ?? "当前不可用"}）· Ctrl+T",
        8 => "纸张、字体、字号",
        9 => "命令面板 · Ctrl+K",
        _ => "更多：打开、导出、打印…",
    };

    // 按下鼠标即收起提示（演示 mousedown → hideTip）。
    public void HideTip()
    {
        _deskTip.Hide();
        _hint = null;
        _tipMuted = _hintHit; _tipMutedSet = true;
    }

    private void PruneTabs(IReadOnlyList<ToolbarTab> tabs)
    {
        if (ReferenceEquals(_tabSnapshot, tabs)) return;
        _tabSnapshot = tabs;
        var names = tabs == null ? new HashSet<string>() : new HashSet<string>(tabs.Select(t => t.DisplayTitle));
        foreach (string title in _tabLayouts.Keys.ToArray())
        {
            if (names.Contains(title)) continue;
            Marshal.Release(_tabLayouts[title].lay);
            _tabLayouts.Remove(title);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var entry in _tabLayouts.Values) Marshal.Release(entry.lay);
        foreach (nint layout in _labels.Values) Marshal.Release(layout);
        _tabLayouts.Clear(); _labels.Clear();
        if (_tooltip != 0) Externs.DestroyWindow(_tooltip);
        _deskTip.Dispose();
        if (_tipText != 0) Marshal.FreeHGlobal(_tipText);
        TabRects = null; _tabSnapshot = null;
        _disposed = true;
    }
}
