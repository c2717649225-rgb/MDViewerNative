using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using static MDViewerNative.RenderResources;
using static MDViewerNative.BlockText;
namespace MDViewerNative;

unsafe partial class DocView : IDisposable, LayoutCacheBudget.IView
{
    public DocView()
    {
        _layoutCache = new LayoutCache<Ent>(this, ReleaseEnt);
    }

    internal sealed class Ent
    {
        public string Text;

        // How far this code block's widest line exceeds the text column, in DIPs.
        // Code lines are never re-broken, so the remainder is reached by scrolling
        // the block sideways (30). Zero means the block fits and never scrolls.
        public float OverflowW;

        // Current sideways offset of this block, 0..OverflowW.
        public float ScrollX;

        // Width of the frozen first column in a sideways-scrolling table. It stays put
        // while the remaining columns slide under it, so the row stays identifiable (30).
        // Zero for code blocks and single-column tables, which freeze nothing.
        public float FrozenW;

        public nint Bmp;

        public float ImgW;

        public float ImgH;

        public nint L;

        public nint Marker;

        public nint[] Cells;

        public float[] ColWs;

        public float[] RowHs;

        public float HeadH;

        public List<(int s, int l)> Code = new List<(int, int)>();

        public List<(int s, int l, string url)> Links = new List<(int, int, string)>();

        public float H;
        public float DetailsSummaryH;       // 摘要行高
        public nint DetailsBody;            // 展开时正文布局
        public MAtom MathTree;    // 块级数学排版树(math 块)
        public MermaidGraph Mermaid; // mermaid 流程图(子集)
        public float MermaidInset;   // S88:比底板窄的图水平居中的左移量(排版时算一次)
        public Dictionary<string, (nint lay, float w, float h)> MermaidLayouts;   // 标签布局缓存(首次绘制构建,随 Ent 释放)
        public bool NoCodeBg;               // 数学块:代码块绘制路径不加灰底
        public List<(int s, int l)> MarkRanges = new();   // ==高亮== 范围(布局内坐标)
        public List<string> KpLines;   // KP 断行结果(非 null 时逐行绘制,不走 DrawTextLayout)
        public List<List<(nint lay, float w, int kind)>> HL;   // 代码高亮逐行分段布局
        public float HLLineH;                                   // 高亮模式下行高
        public List<nint> KpLayouts;   // 每行独立布局(样式随切片 Run 保留)
        public List<int> KpStarts;     // 每行首字符的段内偏移
        public List<float> KpHeights;  // 每行布局高(随字号 L=1.45·bs+5.5,超宽回退包装时取实测值)
        public List<List<(int, int)>> KpCodes;  // 每行的行内码范围(行内坐标)
        public List<Ent> SubRich;
        public float RichWidth;
        public List<nint> SubLayouts;  // 列表项子块布局(项内多段/代码/引用)
        public List<float> SubHeights; // 各子块高
        public List<int> SubStarts;    // 各子块文本在 ent.Text 内的起始偏移
        public List<string> SubTexts;  // 各子块文本
        public float SubTop;
        public List<float> SubXs;
        public List<float> SubYs;    // 各子块文字原点相对块顶的 y(已含间隔与底板内边距;绘制/命中/选区/锚点/链接共用)
        public List<List<(int s, int l)>> SubCodes;
        public List<List<(int s, int l)>> SubMarks;

        // Sideways scroll state for code blocks nested inside a list item. These live on the
        // parent Ent because a nested sub-block has no Ent of its own — it is just a flat
        // layout in SubLayouts. Index-parallel with SubLayouts; 0 for anything not scrollable.
        public List<float> SubOverflowW;
        public List<float> SubScrollX;
        public string[][] CellTexts;   // 表格单元格纯文本缓存([row][col],row 0=表头;Build 时填充)
        public int[] RowLens;      // 表格行字符长缓存(含表头行,BlockTextOf 同源;Build 时填充)

        public int KpLineAt(float relY)
        {
            float acc = 0f;
            for (int i = 0; i < KpHeights.Count; i++)
            {
                if (relY < acc + KpHeights[i] || i == KpHeights.Count - 1) return i;
                acc += KpHeights[i];
            }
            return 0;
        }

        public float KpTop(int k)
        {
            float a = 0f;
            for (int i = 0; i < k && i < KpHeights.Count; i++) a += KpHeights[i];
            return a;
        }
    }
    private int _layoutTypographyVersion = -1;
    public int LayoutPassCount { get; private set; }
    internal int ReusedParagraphMeasurements { get; private set; }
    internal int ReusedBlockMeasurements { get; private set; }
    internal bool ReuseBlockMeasurements { get; set; } = true;
    public bool IsDisposed { get; private set; }

    // I11:衬线开关(菜单"更多→衬线正文")。族未安装时调用无效(Program 侧已提示)。
    public void SetSerif(bool on) => SetSerif(on, SerifFamily);

    public void SetSerif(bool on, string family)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (SerifMode == on && SerifFamily == family) { EnsureLayout(); return; }
        RenderResources.SetSerif(on, family);
        Relayout();
    }

    /// Ctrl+滚轮缩放入口:重建全部文本格式并重排
    public void SetBodySize(float bs, bool relayout = true)   // I9:relayout=false 供 ZoomFont 单遍路径(窗口将变时由 WM_SIZE→OnWidth 统一重排)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (MathF.Abs(bs - BodySize) < 0.01f) { if (relayout) EnsureLayout(); return; }
        RenderResources.SetBodySize(bs);
        // Toolbar layouts use fixed FmtToolbar and do not depend on the body font.
        if (relayout) Relayout();
    }

    private List<Block> _blocks = new List<Block>();
    private DocumentAnchors _anchors = new(Array.Empty<Block>());

    private float[] _ys = Array.Empty<float>();
    private float[] _heights = Array.Empty<float>();

    public string DocumentDirectory { get => _images.DirectoryPath; set => _images.DirectoryPath = value ?? ""; }

    private float _totalH;

    private float _dipW;

    private float _colW = WindowFit.ColW(1000f);   // 未收到宽度前的占位;OnWidth 后按实际视口重算

    private float _colX = 24f;

    private string _lastBuiltText = "";

    private readonly LayoutCache<Ent> _layoutCache;

    private readonly DocumentImages _images = new();

    internal int CachedLayoutBlocks => _layoutCache.Count;

    private int _selStart = -1;

    private int _selEnd = -1;

    private int[] _blockStart = new int[0];

    private int _totalChars;

    private readonly HashSet<int> _openDetails = new();   // <details> 展开状态(按块序号)

    public float TotalH => _totalH;

    public int BlockCount => _blocks.Count;

    public List<Block> Blocks => _blocks;

    /// 诊断:该块在当前实现下是否必然有可见输出(渲染完整性自测用)
    public bool BlockDrawable(int i)
    {
        Block b = _blocks[i];
        if (b is HRule || b is Image)
        {
            return true;   // 分隔线必画线;图片缺载时有 alt 文本兜底
        }
        Ent e = GetEnt(i);
        if (b is Table)
        {
            return e.Cells != null && e.Cells.Length > 0 && !e.Cells.Any((nint c) => c == IntPtr.Zero);
        }
        if (b is ListItem)
        {
            return e.L != IntPtr.Zero || e.Marker != IntPtr.Zero || (e.KpLayouts != null && e.KpLayouts.Count > 0);
        }
        if (e.MathTree != null || e.Mermaid != null)
        {
            return true;   // 数学排版树 / mermaid 流程图
        }
        return e.L != IntPtr.Zero || (e.KpLayouts != null && e.KpLayouts.Count > 0);
    }

    public int TotalChars => _totalChars;

    public float ColX => _colX;

    public float ColW => _colW;

    public float DipW => _dipW;

    public bool HasSel
    {
        get
        {
            if (_selStart >= 0 && _selEnd >= 0)
            {
                return _selStart != _selEnd;
            }
            return false;
        }
    }

    public void Load(string text)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // Parse before mutating anything: a cancelled parse must leave the previously
        // loaded document completely untouched, not a half-cleared view.
        var blocks = HtmlReading.Apply(MdParser.Parse(text));   // 阅读态:白名单 HTML 按等价 Markdown 显示(导出另行解析,不受影响)
        ClearCache();
        _images.Clear();
        _blocks = blocks;
        _wordCount = -1;
        _anchors = new DocumentAnchors(blocks);
        _openDetails.Clear();
        SelClear();
        SearchHits.Clear();
        ImageRects.Clear();
        // Past this point the view already refers to the new blocks. If layout is cancelled
        // the caller discards this DocView, but the object must still be self-consistent:
        // finish the pass with the probe detached rather than leaving sizing arrays ragged.
        try
        {
            Relayout();
        }
        catch (DocumentCancelledException)
        {
            using (DocumentWork.Suspend()) Relayout();
            throw;
        }
    }

    public readonly List<(float x, float y, float w, float h, string path)> EmptyHits = new();
    public int EmptyHover = -1;

    private unsafe float DrawHintAt(string s, float yDip, nint brush, bool recordHit, string path)
    {
        nint l = Dw.MkLayout(s, FmtUi, 10000f);
        Dw.Bind(l);
        var m = default(DWRITE_TEXT_METRICS);
        Dw.GetMetrics(l, &m);
        float x = (_dipW - m.width) / 2f;
        PDraw(Rt, new D2D1_POINT_2F { x = x, y = yDip }, l, brush, 0);
        if (recordHit) EmptyHits.Add((x, yDip, m.width, m.height, path));
        Marshal.Release(l);
        return m.width;
    }

    public float LayoutWidth => _dipW;
    public void ReleaseRenderCache() { ClearCache(); _images.ReleaseBitmaps(); }
    public (int Count, ulong Pixels) ImageCacheUsage => (_images.Count, _images.Pixels);

    // 动画(34):由宿主计时器驱动;无动画图片时返回 -1,宿主不设计时器
    public bool HasAnimatedImages => _images.HasAnimations;
    public bool AdvanceAnimations() => _images.Advance(Environment.TickCount64);
    public int NextAnimationDueMs() => _images.HasAnimations ? _images.NextDueMs(Environment.TickCount64) : -1;


    public void OnWidth(float dipW)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        bool changed = !(MathF.Abs(dipW - _dipW) < 0.5f);
        if (changed) _dipW = dipW;
        if (changed || _layoutTypographyVersion != TypographyVersion)
        {
            Relayout();
        }
    }

    public void EnsureLayout()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_layoutTypographyVersion != TypographyVersion) Relayout();
    }

    // TOC 侧栏
    public bool ShowToc;
    public bool TocVisible => ShowToc && TocWidth > 0f;
    public float TocWidth => OverlayGeometry.TocWidth(_dipW);          // §4.3:min(240, V−48)
    public bool TocPinned => OverlayGeometry.TocPinned(_dipW);  // 宽窗口常驻并让出宽度,否则为临时覆盖层
    // D1:收起时左留白放得下 32 DIP 标尺才显示;展开目录覆盖同一区域时不画
    public bool RulerVisible => !RenderResources.Reading.IsDesk && !TocVisible && TocWidth > 0f && _blocks.Count > 0 && OverlayGeometry.RulerShown(_colX);
    private readonly HashSet<int> _tocTrimmed = new();   // 被省略号截断的目录条目(悬停提示显示全文)

    // §4.4:长标题省略时提示保留全文;只对被截断的条目返回文字
    public string TocHint(float clientDipY, float contentTop, out float rowTop)
    {
        rowTop = 0f;
        if (!TocVisible || TocEntries.Count == 0) return null;
        int idx = (int)MathF.Floor((clientDipY - contentTop - 8f + TocScroll) / 26f);
        if (idx < 0 || idx >= TocEntries.Count || !_tocTrimmed.Contains(idx)) return null;
        rowTop = contentTop + 8f + idx * 26f - TocScroll;
        return TocEntries[idx].text;
    }
    public float TocScroll;
    public bool TocUserScrolled;        // 用户在 TOC 上滚过轮:暂停自动跟随,文档再次滚动时恢复
    private float _followAnchorY = float.NaN;   // 上次跟随判定时的文档偏移(视口坐标)
    public float LastViewHDip = 400f;   // 最近一次 Paint 的视口高(DIP),Relayout 钳制 TocScroll 用

    // Paper cannot scroll sideways. On screen a wide code line stays on one row and the
    // reader scrolls the block (30); when printing or exporting to PDF the same line must
    // wrap instead, or everything past the page edge is silently lost. Set around the
    // print render and followed by a Relayout, since it changes measured heights.
    public bool WrapCodeForPaper;
    public readonly List<(int blockIdx, int level, string text, float y)> TocEntries = new();
    private readonly Dictionary<int, nint> _tocLayouts = new();   // TOC 条目布局缓存(键=条目序号,Relayout 重建 TocEntries 时释放)
    public readonly List<(float x, float y, float w, float h, string src)> ImageRects = new();
    private readonly CodeBlockTargets _codeTargets = new();
    public string CodeAtPoint(float x, float documentY) => _codeTargets.AtPoint(x, documentY);
    public string CodeAtSelection() => _selStart >= 0 ? _codeTargets.AtOffset(SelHead) : null;

    private void RecomputeColumn()
    {
        // 正文随窗口排版(S80):V − 2P。宽窗口展开的目录常驻在左侧并让出自己的宽度,
        // 两侧留白仍按整个窗口取;临时目录是覆盖层,不计入。
        float left = ShowToc && TocPinned ? TocWidth : 0f;
        float right = RenderResources.Reading.RightPadRatio;   // U3:desk 左 48 / 右 30
        _colW = WindowFit.ColW(_dipW, left, right);
        _colX = WindowFit.ColX(_dipW, _colW, left, right);
    }

    public void SetToc(bool on)
    {
        if (ShowToc == on) return;
        ShowToc = on;
        _followAnchorY = float.NaN;
        // 常驻目录占用正文宽度:开关它要重排。临时目录只是覆盖层,不动正文。
        if (_dipW > 0f && TocPinned) Relayout();
    }

    // 点击侧栏条目 → 目标文档 Y(DIP);未命中返回 false
    public bool TocClick(float clientDipY, out float targetY, float contentTop = -1f)
    {
        if (contentTop < 0f) contentTop = ToolH;
        targetY = 0f;
        if (!TocVisible || TocEntries.Count == 0) return false;
        int idx = (int)((clientDipY - contentTop - 8f + TocScroll) / 26f);
        if (idx < 0 || idx >= TocEntries.Count) return false;
        targetY = TocEntries[idx].y;
        return true;
    }

    // 搜索命中表(全局偏移);SetSearchQuery 重建并定位当前命中
    public readonly List<(int start, int len)> SearchHits = new();
    public int SearchHitIdx = -1;

    public int SetSearchQuery(string q, int fromPos, bool back, SearchOptions options = default, int rangeStart = 0, int rangeEnd = -1)
    {
        SearchHits.Clear();
        SearchHitIdx = -1;
        if (string.IsNullOrEmpty(q)) return -1;
        string text = SearchText();
        SearchHits.AddRange(TextSearch.FindAll(text, q, options, rangeStart, rangeEnd));
        int idx = TextSearch.Next(SearchHits, fromPos, back);
        SearchHitIdx = idx;
        return idx;
    }

    // U3 desk:H2 前的方印（壹贰叁…）与 H3 前的朱色短线。文字整体右移，位移走 Indent，
    // 于是绘制、命中、选区、查找高亮都自动一致；标题自带编号时不加方印。
    private readonly Dictionary<Block, string> _headingMarks = new(ReferenceEqualityComparer.Instance);   // 值：方印数字；H3 为 ""
    private readonly Dictionary<string, nint> _sealLayouts = new();

    // U3b desk 正文细节：引用（宋体、次级墨色、2px 浅线），无序列表记号朱色。提示块（> [!NOTE] 等）保持原样。
    private static bool DeskQuote(Block b) => RenderResources.Reading.IsDesk && FmtQuote != IntPtr.Zero && b is Quote && b.Alert == null;
    private static bool DeskRail(Block b) => RenderResources.Reading.IsDesk && b.Alert == null;
    private static float DeskRailWidth => MathF.Max(1f, MathF.Round(2f * (Scale > 0f ? Scale : 1f))) / (Scale > 0f ? Scale : 1f);
    private static bool DeskBullet(ListItem li) => RenderResources.Reading.IsDesk && li.Marker is { Length: > 0 } m && !char.IsDigit(m[0]) && !char.IsLetter(m[0]);

    // U4 页脚：字数（与演示同口径，首次读取时统计，换文档重算）与标题的方印数字。
    private int _wordCount = -1;
    public int WordCount
    {
        get
        {
            if (_wordCount < 0) { int n = 0; foreach (Block b in _blocks) n += FooterModel.CountWords(BlockTextOf(b)); _wordCount = n; }
            return _wordCount;
        }
    }
    public string SealOf(int blockIdx) => blockIdx >= 0 && blockIdx < _blocks.Count && _headingMarks.TryGetValue(_blocks[blockIdx], out string s) && s.Length > 0 ? s : null;

    internal float BlockIndent(int i) => Indent(_blocks[i]);                 // 自检用
    internal (float Top, float Height) BlockBox(int i) => (_ys[i], _heights[i]);

    private float Indent(Block block)
        => block.XOff + (_headingMarks.Count > 0 && block is Heading h && _headingMarks.ContainsKey(block)
            ? (h.Level == 2 ? ReadingStyle.SealIndent(RenderResources.Reading.HeadingScale(2) * BodySize) : ReadingStyle.DashIndent(RenderResources.Reading.HeadingScale(3) * BodySize))
            : 0f);

    private void ComputeHeadingMarks()
    {
        _headingMarks.Clear();
        ReleaseSealLayouts();
        if (!RenderResources.Reading.IsDesk) return;
        int seal = 0;
        foreach (Block b in _blocks)
        {
            if (b is not Heading h || h.QuoteDepth > 0) continue;
            if (h.Level == 3) _headingMarks[b] = "";
            else if (h.Level == 2 && !ReadingStyle.HasOwnNumber(string.Concat(h.Inlines.Select(r => r.Text)))) _headingMarks[b] = ReadingStyle.SealNumber(++seal);
        }
    }

    private void ReleaseSealLayouts()
    {
        foreach (var layout in _sealLayouts.Values) if (layout != 0) Marshal.Release(layout);
        _sealLayouts.Clear();
    }

    private unsafe void PaintHeadingMark(Block block, Ent ent, float textX, float top)
    {
        if (!_headingMarks.TryGetValue(block, out string number) || ent?.L == IntPtr.Zero || ent == null) return;
        var lines = Dw.Lines(ent.L);
        float lineH = lines.Length > 0 ? lines[0].height : ent.H;
        float mid = top + lineH * 0.5f;
        float size = Dw.FontSize(ent.L);
        if (number.Length == 0)
        {
            float len = ReadingStyle.DashLength(size);
            float x0 = textX - ReadingStyle.DashIndent(size);
            float y0 = MathF.Round(mid * Scale) / Scale;
            D2D1_RECT_F dash = new D2D1_RECT_F { left = x0, top = y0 - 0.5f, right = x0 + len, bottom = y0 + 0.5f };
            PFill(Rt, &dash, BAccent);
            return;
        }
        float side = ReadingStyle.SealSide(size);
        float cx = textX - ReadingStyle.SealIndent(size) + side * 0.5f;
        float rad = ReadingStyle.SealTiltDegrees * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        var tilt = new D2D1_MATRIX_3X2_F { m11 = cos, m12 = sin, m21 = -sin, m22 = cos, dx = cx - cos * cx + sin * mid, dy = mid - sin * cx - cos * mid };
        var identity = new D2D1_MATRIX_3X2_F { m11 = 1f, m22 = 1f };
        SetTransform(&tilt);
        try
        {
            D2D1_ROUNDED_RECT box = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = cx - side * 0.5f, top = mid - side * 0.5f, right = cx + side * 0.5f, bottom = mid + side * 0.5f }, radiusX = 3f, radiusY = 3f };
            PStrokeRound(Rt, &box, BAccent, 1f, IntPtr.Zero);
            if (FmtSeal == IntPtr.Zero) return;
            if (!_sealLayouts.TryGetValue(number, out nint layout)) _sealLayouts[number] = layout = Dw.MkLayout(number, FmtSeal, 4f * side);
            if (layout == IntPtr.Zero) return;
            float w = Dw.LayoutW(layout), h = Dw.LayoutH(layout);
            PDraw(Rt, new D2D1_POINT_2F { x = cx - w * 0.5f, y = mid - h * 0.5f }, layout, BAccent, 0);
        }
        finally { SetTransform(&identity); }
    }

    private void ClearCache()
    {
        _codeTargets.Clear();
        _codeHover = _codeCopied = -1;
        _layoutCache.Clear();
    }

    // A4 E7 链接着色:构建布局时把链接范围的绘制效果设为 BAccent(文字与下划线同色)。
    // 选区/搜索命中/作者高亮底上暗色 Accent 不足 4.5:1,这些范围在本帧临时设为 Ink,帧末恢复。
    // 临时改动的布局先 AddRef:即使本帧内被布局缓存淘汰,恢复与释放也不会触及已释放对象。
    private nint _linkBrush;
    private readonly List<(nint layout, DWRITE_TEXT_RANGE range)> _linkInked = new();

    private unsafe void InkLinksIn(nint layout, int s, int l)
    {
        if (layout == IntPtr.Zero || l <= 0 || _linkBrush == IntPtr.Zero || _linkBrush != BAccent) return;
        uint pos = (uint)s, end = (uint)(s + l);
        while (pos < end)
        {
            nint effect = IntPtr.Zero;
            DWRITE_TEXT_RANGE same = default;
            if (Dw.GetDrawingEffect(layout, pos, &effect, &same) < 0) return;
            uint next = same.length == 0 ? end : (uint)Math.Min((ulong)end, (ulong)same.first + same.length);
            if (effect != IntPtr.Zero)
            {
                if (effect == BAccent && next > pos)
                {
                    var range = new DWRITE_TEXT_RANGE { first = pos, length = next - pos };
                    Marshal.AddRef(layout);
                    Dw.SetDrawingEffect(layout, BInk, range);
                    _linkInked.Add((layout, range));
                }
                Marshal.Release(effect);
            }
            if (next <= pos) break;
            pos = next;
        }
    }

    private void RestoreInkedLinks()
    {
        foreach (var (layout, range) in _linkInked)
        {
            Dw.SetDrawingEffect(layout, BAccent, range);
            Marshal.Release(layout);
        }
        _linkInked.Clear();
    }

    internal static void ReleaseEnt(Ent e)
    {
        if (e.SubRich != null) foreach (var child in e.SubRich) if (child != null) ReleaseEnt(child);
        if (e.L != IntPtr.Zero)
        {
            Marshal.Release(e.L);
        }
        if (e.MathTree != null)
        {
            MathRender.Free(e.MathTree);
        }
        if (e.KpLayouts != null)
        {
            foreach (nint k in e.KpLayouts)
            {
                if (k != IntPtr.Zero)
                {
                    Marshal.Release(k);
                }
            }
        }
        if (e.HL != null)
        {
            foreach (var line in e.HL)
            {
                foreach (var (lay, w, kind) in line)
                {
                    if (lay != IntPtr.Zero)
                    {
                        Marshal.Release(lay);
                    }
                }
            }
        }
        if (e.Marker != IntPtr.Zero)
        {
            Marshal.Release(e.Marker);
        }
        if (e.MermaidLayouts != null)
        {
            foreach (var v in e.MermaidLayouts.Values)
                if (v.lay != IntPtr.Zero) Marshal.Release(v.lay);
            e.MermaidLayouts = null;
        }
        if (e.DetailsBody != IntPtr.Zero)
        {
            Marshal.Release(e.DetailsBody);
            e.DetailsBody = IntPtr.Zero;
        }
        if (e.SubLayouts != null)
        {
            foreach (nint sl in e.SubLayouts)
                if (sl != IntPtr.Zero) Marshal.Release(sl);
            e.SubLayouts = null;
            e.SubHeights = null;
            e.SubYs = null;
            e.SubStarts = null;
            e.SubTexts = null;
        }
        if (e.Cells == null)
        {
            return;
        }
        nint[] cells = e.Cells;
        foreach (nint num in cells)
        {
            if (num != IntPtr.Zero)
            {
                Marshal.Release(num);
            }
        }
    }

    // An empty list parent is a marker anchor sharing its first child's row.
    private bool SharesChildRow(int i) => i >= 0 && i + 1 < _blocks.Count
        && _blocks[i] is ListItem { Continuation: false, Sub: null } parent && parent.Inlines.Count == 0
        && _blocks[i + 1] is ListItem child && child.Nest > parent.Nest;

    public void Relayout()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // A font change also changes the target column, even when the HWND width is unchanged.
        if (_dipW > 0f) RecomputeColumn();
        ClearCache();
        _blockStart = new int[_blocks.Count + 1];
        int acc = 0;
        for (int i = 0; i < _blocks.Count; i++) { _blockStart[i] = acc; acc += BlockTextOf(_blocks[i]).Length + (SharesChildRow(i) ? 0 : 1); }
        _totalChars = acc;
        _ys = new float[_blocks.Count];
        _heights = new float[_blocks.Count];
        ComputeHeadingMarks();
        float y = RenderResources.Reading.PageTop;   // Document inset is independent of toolbar and search-strip height.
        var paragraphHeights = new Dictionary<Para, float>(ParagraphMeasureComparer.Instance);
        ReusedParagraphMeasurements = 0;
        ReusedBlockMeasurements = 0;
        var blockHeights = new Dictionary<Block, float>(BlockMeasureComparer.Instance);
        for (int i = 0; i < _blocks.Count; i++)
        {
            Block b = _blocks[i];
            DocumentWork.Poll();
            // 相邻取大,标题→非标题首块使用标题下间距特例;空父项共享行仍不加间距。
            if (i > 0 && !SharesChildRow(i - 1)) y += BlockGap(_blocks[i - 1], b);
            _ys[i] = y;
            float h;
            if (b is Para paragraph && paragraphHeights.TryGetValue(paragraph, out h))
                ReusedParagraphMeasurements++;
            else if (ReuseBlockMeasurements && BlockMeasureComparer.Supports(b) && blockHeights.TryGetValue(b, out h))
                ReusedBlockMeasurements++;
            else
            {
                h = MeasureBlock(i); // Exact DWrite measurement, never an estimated height.
                if (b is Para measured && paragraphHeights.Count < 256) paragraphHeights[measured] = h;
                if (ReuseBlockMeasurements && BlockMeasureComparer.Supports(b) && blockHeights.Count < 256) blockHeights[b] = h;
            }
            bool sharedRow = SharesChildRow(i);
            if (sharedRow) h = 0f;
            _heights[i] = h;
            y += h;   // B3:块间距全部由下一块块首的相邻取大给出,块尾零记账(sharedRow 边界两端 before/after 与原逻辑同样被跳过)
        }
        _totalH = y + RenderResources.Reading.PageBottom;
        // TOC 条目(标题层级树);条目集变化,旧布局缓存全部释放重建
        ReleaseChromeLayouts();   // 语言标签/按钮文字随字号(FmtUi)重建
        foreach (var tl in _tocLayouts.Values) if (tl != IntPtr.Zero) Marshal.Release(tl);
        _tocLayouts.Clear();
        _tocTrimmed.Clear();
        TocEntries.Clear();
        for (int ti = 0; ti < _blocks.Count; ti++)
        {
            if (_blocks[ti] is Heading th)
            {
                TocEntries.Add((ti, th.Level, string.Concat(th.Inlines.Select((Run r) => r.Text)), _ys[ti]));
            }
        }
        float tocViewH = MathF.Max(400f, LastViewHDip);   // TOC 可视高度=正文区高(与滚轮路径同口径);未渲染过回退 400
        float tocMax = MathF.Max(0f, TocEntries.Count * 26f + 16f - tocViewH);   // 与滚轮路径同口径
        if (TocScroll > tocMax) TocScroll = MathF.Floor(tocMax * Scale) / Scale;   // 上限向下落网格(与 Program 侧 TocScroll 口径一致)
        _layoutTypographyVersion = TypographyVersion;
        LayoutPassCount++;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        ClearCache();
        ReleaseChromeLayouts();
        ReleaseSealLayouts();
        _headingMarks.Clear();
        foreach (var layout in _tocLayouts.Values) if (layout != 0) Marshal.Release(layout);
        _tocLayouts.Clear();
        _tocTrimmed.Clear();
        _images.Dispose();
        _blocks.Clear();
        _anchors = new DocumentAnchors(Array.Empty<Block>());
        TocEntries.Clear();
        SearchHits.Clear();
        ImageRects.Clear();
        EmptyHits.Clear();
        _openDetails.Clear();
        _ys = Array.Empty<float>();
        _heights = Array.Empty<float>();
        _blockStart = Array.Empty<int>();
        IsDisposed = true;
    }

    private const float PanelTopOutset = 12f;
    private const float CodeTopOutset = 20f;   // E4:右上角标签/复制按钮带区(块顶 +2、高 18)不压首行

    private static bool IsMathCode(CodeBlock block)
        => (block.Lang ?? "").Trim() is "math" or "latex" or "tex";

    // The panel starts above its text layout. Use the same top footprint for
    // heading clearance, painting, clipping and code hit targets. Background-less
    // math has no panel above its layout (nor an invisible hit area there).
    // 无 alt(徽章、logo 常见)时只显示状态,不留悬空分隔点(自用走查 S73)。
    internal static string ImagePlaceholder(string alt, string status)
        => string.IsNullOrWhiteSpace(alt) ? "[" + status + "]" : "[" + alt + " · " + status + "]";

    // 模型 Aligns(0=左 1=中 2=右,解析与导出共用)→ DirectWrite(0=LEADING 1=TRAILING 2=CENTER)。
    // 曾原样传入,:---: 列显示为右对齐、---: 列显示为居中(自用走查 S72)。
    private static int CellAlignment(Table table, int col)
        => (table.Aligns != null && col < table.Aligns.Length ? table.Aligns[col] : 0) switch { 1 => 2, 2 => 1, _ => 0 };

    private static float BlockTopOutset(Block block)
        => block is HtmlBlock || block is CodeBlock code && !IsMathCode(code) ? CodeTopOutset : 0f;   // 字面 HTML 面板与代码面板同一种“面”(S71)

    private static float BlockGap(Block previous, Block next)
    {
        // U3b desk：同一段引用里的相邻段落只隔 .4em，引用前后才用 1.9em。
        if (RenderResources.Reading.IsDesk && previous is Quote && next is Quote && previous.QSeq == next.QSeq && previous.Alert == null && next.Alert == null)
            return RenderResources.Reading.DeskQuoteInnerGap(BodySize);
        // 列表结束:整张列表的下边距按段落距 0.5L;0.15L 只是项与项之间的距离。否则列表后的段落比项间还近,
        // 读起来像列表的一部分(自用走查 S67)。项内续段在 ListItem.Sub/Continuation 中,不经过这里。
        float after = previous is ListItem && next is not ListItem ? MathF.Max(MarginAfter(previous), 0.5f * LineH(BodySize)) : MarginAfter(previous);
        float gap = previous is Heading && next is not Heading ? MarginAfter(previous) + BlockTopOutset(next)
            : MathF.Max(after, MarginBefore(next)) + CodeChromeReserve(next);
        // A4:下一块带底板(代码/HTML)时,底板与上一块的“面”之间至少留 0.5 行高:上一块是底板(下伸 12)
        // 或表格(底部规则线下还有横滚提示,占 6)。原先代码接代码重叠 3 DIP,表格接代码只余约 8 DIP。
        float previousOut = BlockTopOutset(previous) > 0f ? PanelBottomOutset : previous is Table ? TableCueReserve : -1f;
        if (previousOut >= 0f && BlockTopOutset(next) > 0f)
            gap = MathF.Max(gap, previousOut + BlockTopOutset(next) + 0.5f * LineH(BodySize));
        return gap;
    }

    private const float PanelBottomOutset = 12f;   // 底板在布局下方多出的高度(绘制 bottom = 布局底 + 12)
    private const float CodeBottomPad = 8f;          // 代码块布局高度在文字下多出的部分;+ PanelBottomOutset = 20,与 CodeTopOutset 对称
    private const float TableCueReserve = 6f;       // 表格底部规则线下的横滚提示区(轨道 y+4,粗 2)
    private const float PanelMinLeft = 4f;          // 窄窗回退:底板左缘至少离窗口左边 4 DIP,不被窗口裁掉

    // 底板左缘:正文列左 PanelBleed;引用内不越过竖线右侧;窄窗时不越出窗口(绘制、命中、裁剪、横滚提示共用)。
    // 底板右缘(S84):与左缘对称,列右 + PanelBleed,窄窗时同样收在离窗口边 PanelMinLeft 以内。
    // 以前右缘缩进列内 20,整块底板相对正文列左移约 21;代码文字始终与正文左右对齐。
    // 项内代码/HTML 子块的底板内边距(自用走查 S69):原先只外扩 2 DIP、文字距左缘 6,与正文代码块相比局促
    internal const float NestedPanelInsetY = 10f;
    internal const float NestedPanelInsetX = 14f;
    private static float NestedPad(Block sub) => sub is CodeBlock or HtmlBlock ? NestedPanelInsetY : 0f;
    private const float PanelBleed = 22f;
    private float PanelLeft(Block block, float x)
        => MathF.Max(block.QuoteDepth > 0 ? MathF.Max(x - PanelBleed, _colX + block.QuoteDepth * 20f - 8f) : x - PanelBleed, PanelMinLeft);
    private float PanelRight()
        => MathF.Max(_colX + _colW, MathF.Min(_colX + _colW + PanelBleed, _dipW - (RenderResources.Reading.IsDesk ? DeskMetrics.StripWidth : PanelMinLeft)));   // U3b:desk 不压右侧书签绳列


    // 代码块上内边距从面板通用的 12 加到 20:底板外沿位置不变,正文向下让出同样的量,底板不压上一块。
    private static float CodeChromeReserve(Block next) => next is HtmlBlock || next is CodeBlock code && !IsMathCode(code) ? CodeTopOutset - PanelTopOutset : 0f;
    private static float MarginBefore(Block b)
    {
        var rs = RenderResources.Reading;
        if (rs.IsDesk && b is Heading dh) return rs.DeskHeadingBefore(dh.Level, BodySize);   // U3:规格 §2
        if (rs.IsDesk && b is Quote && b.Alert == null) return rs.DeskQuoteMargin(BodySize);
        // 段距=行高倍数,L=LineH(BodySize)(v2.2 §3.1,16 档 L=28)。非标题块维持现行比例档(嵌套列表/引用/容器样例定稿前不动)。
        float L = LineH(BodySize);
        if (b is Heading h)
            // 标题上间距(v2.2 §3.1):H1 文中 28(同 H2,§10 B-2 裁定档)、H3 22、H4–H6 18;顶部 H1 由 Relayout 的 i>0 天然为零。
            return h.Level switch { 1 => L, 2 => L, 3 => 22f / 28f * L, _ => 18f / 28f * L };
        if (b is CodeBlock || b is HtmlBlock) return 0.5f * L;
        if (b is Quote) return 0.5f * L;
        if (b is ListItem) return 0.25f * L;
        if (b is Table) return 0.5f * L;
        if (b is HRule) return 0.75f * L;
        return 0f;
    }

    private static float MarginAfter(Block b)
    {
        var rs = RenderResources.Reading;
        if (rs.IsDesk && b is Heading dh) return rs.DeskHeadingAfter(dh.Level, BodySize);
        if (rs.IsDesk && b is Para) return rs.DeskParagraphAfter(BodySize);   // 段距 1.15em
        if (rs.IsDesk && b is Quote && b.Alert == null) return rs.DeskQuoteMargin(BodySize);
        float L = LineH(BodySize);
        if (b is Heading h)
            // 标题下间距(v2.2 §3.1):H1 16、H2 10、H3/H4+ 8;正文段后 0.5L=14 已合规不动。
            return h.Level switch { 1 => 16f / 28f * L, 2 => 10f / 28f * L, 3 => 8f / 28f * L, _ => 8f / 28f * L };
        if (b is CodeBlock || b is HtmlBlock) return 0.75f * L;
        if (b is Quote) return 0.5f * L;
        if (b is ListItem) return 0.15f * L;
        if (b is Table) return 0.7f * L;
        if (b is HRule) return 0.7f * L;
        if (b is Para) return 0.5f * L;
        return 0.5f * L;
    }

    private float MeasureBlock(int index)
    {
        Ent entry = GetEnt(index);
        return entry.Marker == 0 ? entry.H : MathF.Max(entry.H, Dw.LayoutH(entry.Marker));
    }

    private Ent GetEnt(int i)
    {
        if (_layoutCache.TryGet(i, out var value)) return value;
        value = Build(i);
        _layoutCache.Add(i, value);
        return value;
    }

    void LayoutCacheBudget.IView.EvictLeastRecentLayout() => _layoutCache.EvictLeastRecent();

    // 把内联片段按段内 [start, start+len) 切出一段,加粗/斜体/删除线/行内码/链接样式随 Run 保留;
    // 行内码与链接范围由 BuildLayout 依据切片后的 Run 自动重建(行内坐标)
    private static List<Run> SliceRuns(List<Run> inlines, int start, int len)
    {
        var result = new List<Run>();
        int pos = 0;
        int end = start + len;
        foreach (Run r in inlines)
        {
            int rs = pos, re = pos + r.Text.Length;
            pos = re;
            int a = Math.Max(rs, start), b = Math.Min(re, end);
            if (b <= a) continue;
            result.Add(new Run(r.Text[(a - rs)..(b - rs)], r.B, r.I, r.S, r.Code, r.Link, r.LinkTitle, r.Mark, r.SupSub, r.Fn, r.U, r.Img) { LinkOuter = r.LinkOuter, ImageAlt = r.ImageAlt, MathTex = a == rs && b == re ? r.MathTex : null, MathNative = a == rs && b == re && r.MathNative });
        }
        return result;
    }

    [DllImport("MDNative.dll")]
    private static extern int MD_SetInlineBitmap(nint layout, nint target, nint bitmap, uint start, uint length, float width, float height);

    // 显示文本:上/下标换成 Unicode 上/下标(长度不变;选区/查找/复制仍用原文 _lastBuiltText)。full=已抬离基线,不再缩字号。脚注号不换(太小难点)
    internal static string ShownRunText(Run run, out bool full)
    {
        full = false;
        string text = run.Text;
        if (run.SupSub != 0 && MathUnicode.TryBodyScript(text, run.SupSub == 1, out string shown)) { full = true; return shown; }
        return text;
    }

    private unsafe nint BuildLayout(IEnumerable<Run> runs, float width, nint fmt, bool uniformSpacing, List<(int s, int l)> code, List<(int s, int l, string url)> links, int align = 0, List<(int s, int l)> marks = null, bool justLine = false, bool bold = false)
    {
        StringBuilder stringBuilder = new StringBuilder();
        List<bool> fullScript = null;   // 仅当有上/下标时才建显示串:KP 量宽逐词调用本函数,常见路径零额外分配
        foreach (Run run in runs)
        {
            stringBuilder.Append(run.Text);
            if (run.SupSub != 0) fullScript ??= new List<bool>();
        }
        _lastBuiltText = stringBuilder.ToString();
        string shownText = _lastBuiltText;
        if (fullScript != null)
        {
            var shown = new StringBuilder(_lastBuiltText.Length);
            foreach (Run run in runs) { shown.Append(ShownRunText(run, out bool scripted)); fullScript.Add(scripted); }
            shownText = shown.ToString();
        }
        nint num = Dw.MkLayout(shownText, fmt, width);
        Dw.Bind(num);
        Dw.SetTextAlignment(num, align);
        Dw.SetParagraphAlignment(num, 0);
        Dw.SetWordWrapping(num, 0);
        bool monoFmt = fmt == FmtMono;
        float spacingDip = uniformSpacing ? LineH(BodySize) : (monoFmt ? CodeLineH(BodySize) : 0f);
        if (spacingDip > 0f && !runs.Any(r => r.Img))
        {
            // v2.2 §3.1(B2):正文 uniform=LineH(1.75×bs);等宽布局=CodeLineH(1.6×mono≈21.6 @16)。
            // 等宽判定:FmtMono 为 RenderResources 单一对象(仅代码块/TOC 代码段使用),恒等比较零调用点改动。
            // 基线占比分轨:等宽=MonoBase 探针、serif=SerifBase 探针、sans 恒 0.73(零回归)。
            float baseF = monoFmt ? MonoBase : (SerifMode && SerifFamily != null ? SerifBase : 0.73f);
            Dw.SetLineSpacing(num, 1, spacingDip, baseF * spacingDip);
        }
        if (justLine)
        {
            // I6(JUST 渲染兑现,PLAN-TYPO-9 §1.1):KP 行从"自然宽左齐"变两端对齐,逐字胶/中西间距/悬挂额度自此真正落在像素上。
            // NO_WRAP 双保险:超宽行不被 DWrite 二次折行(P1 探针:JUST 对单行布局真实拉伸 328→688)
            Dw.SetWordWrapping(num, 1 /*NO_WRAP*/);
            Dw.SetTextAlignment(num, 3 /*JUST*/);
        }
        int num2 = 0;
        int runIndex = -1;
        foreach (Run run2 in runs)
        {
            runIndex++;
            int length = run2.Text.Length;
            if (length != 0)
            {
                DWRITE_TEXT_RANGE dWRITE_TEXT_RANGE = default(DWRITE_TEXT_RANGE);
                dWRITE_TEXT_RANGE.first = (uint)num2;
                dWRITE_TEXT_RANGE.length = (uint)length;
                DWRITE_TEXT_RANGE dWRITE_TEXT_RANGE2 = dWRITE_TEXT_RANGE;
                if (run2.Img && run2.Link != null)
                {
                    var image = _images.Load(run2.Link);
                    if (image.Bitmap != 0)
                    {
                        float ratio = MathF.Min(1f, MathF.Min(MathF.Max(20f, width * .7f) / image.Width, BodySize * 6f / image.Height));
                        Marshal.ThrowExceptionForHR(MD_SetInlineBitmap(num, Rt, image.Bitmap, (uint)num2, (uint)length, image.Width * ratio, image.Height * ratio));
                    }
                }
                if (run2.MathNative)
                {
                    // 行内原生公式(S77)
                    MathInline.Attach(num, run2.MathTex, (uint)num2, (uint)length, Dw.FontSize(num));   // 随布局字号(标题)
                }
                if (run2.B || bold)
                {
                    Dw.SetFontWeight(num, 700u, dWRITE_TEXT_RANGE2);
                }
                if (run2.I)
                {
                    Dw.SetFontStyle(num, 1, dWRITE_TEXT_RANGE2);
                }
                if (run2.S)
                {
                    Dw.SetStrikethrough(num, 1, dWRITE_TEXT_RANGE2);
                }
                if (run2.Code)
                {
                    fixed (char* family = "Consolas") Dw.SetFontFamilyName(num, (nint)family, dWRITE_TEXT_RANGE2);
                    Dw.SetFontSize(num, ReadingStyle.InlineCodeScale * BodySize, dWRITE_TEXT_RANGE2);   // 随字号缩放(原固定 13.6 = 16 档)
                    code.Add((num2, length));
                }
                if (run2.Link != null)
                {
                    Dw.SetUnderline(num, 1, dWRITE_TEXT_RANGE2);
                    Dw.SetDrawingEffect(num, BAccent, dWRITE_TEXT_RANGE2);   // E7:链接用 Accent
                    _linkBrush = BAccent;
                    links.Add((num2, length, run2.Img ? run2.LinkOuter ?? run2.Link : run2.Link));
                }
                if (run2.U)
                {
                    Dw.SetUnderline(num, 1, dWRITE_TEXT_RANGE2);
                }
                if (run2.Mark && marks != null)
                {
                    marks.Add((num2, length));
                }
                if ((run2.SupSub != 0 || run2.Fn) && !(fullScript != null && fullScript[runIndex]))
                {
                    Dw.SetFontSize(num, BodySize * 0.72f, dWRITE_TEXT_RANGE2);
                }
                num2 += length;
            }
        }
        // 中文标点挤压(S75);代码 run 与等宽布局(代码块)保持字面
        if (!monoFmt)
        {
            List<(int s, int l)> codeSpans = null;
            int at = 0;
            foreach (Run r in runs) { if (r.Code && r.Text.Length > 0) (codeSpans ??= new List<(int s, int l)>()).Add((at, r.Text.Length)); at += r.Text.Length; }
            MathGlyphs.Apply(num, shownText);   // 数学字母数字:取消斜体、与重音同字体(S76)
            CjkPunct.Squeeze(num, shownText, codeSpans);
        }
        return num;
    }

    private unsafe Ent Build(int i, Block child = null, float? width = null)
    {
        Block block = child ?? _blocks[i];
        Ent ent = new Ent();
        float num = width ?? (_colW - Indent(block));
        ent.RichWidth = num;
        if (block is HtmlDetails dt)
        {
            // <details> 折叠块:摘要行 + 可展开正文
            bool open = _openDetails.Contains(i);
            string sTxt = Vt(string.Concat(dt.Summary.Select((Run r) => r.Text)));
            string bTxt = DetailsBodyText(dt);
            var runs2 = new List<Run> { new Run(open ? "▾ " : "▸ ") };
            runs2.AddRange(dt.Summary);
            var lcHead = new List<(int, int)>();
            var llHead = new List<(int, int, string)>();
            ent.L = BuildLayout(runs2, MathF.Max(120f, num), FmtBody, uniformSpacing: true, lcHead, llHead, 0, ent.MarkRanges);
            foreach (var (a, b) in lcHead) ent.Code.Add((a - 2, b - 2));   // runs2 坐标含 2 字符 ▸ 前缀,折算回块文本域
            foreach (var (a, b, u) in llHead) ent.Links.Add((a - 2, b - 2, u));
            ent.Text = sTxt + "\n" + bTxt;   // 与 BlockTextOf 同域(Vt)
            float sh = Dw.LayoutH(ent.L);
            ent.DetailsSummaryH = sh;
            ent.H = sh;
            if (open && bTxt.Length > 0)
            {
                var lcBody = new List<(int, int)>();
                var llBody = new List<(int, int, string)>();
                ent.DetailsBody = BuildLayout(dt.BodyRuns ?? (IEnumerable<Run>)new Run[1] { new Run(bTxt) }, MathF.Max(120f, num - 14f), FmtBody, uniformSpacing: true, lcBody, llBody);
                int baseOff = sTxt.Length + 1;   // 正文域起点 = 摘要 + 换行
                foreach (var (a, b) in lcBody) ent.Code.Add((a + baseOff, b + baseOff));
                foreach (var (a, b, u) in llBody) ent.Links.Add((a + baseOff, b + baseOff, u));
                ent.H = sh + Dw.LayoutH(ent.DetailsBody) + 4f;
            }
            return ent;
        }
        if (!(block is Heading heading))
        {
            if (!(block is Para para))
            {
                if (!(block is Quote quote))
                {
                    if (!(block is ListItem listItem))
                    {
                        if (block is HtmlBlock hbl)
                        {
                            // 原始 HTML 块:等宽字面渲染(与代码块同路径,无高亮)
                            ent.L = BuildLayout(new Run[1] { new Run(Vt(hbl.Text).TrimEnd('\n')) }, MathF.Max(120f, num - 40f), FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                            ent.Text = Vt(hbl.Text).TrimEnd('\n');
                            ent.H = Dw.LayoutH(ent.L) + CodeBottomPad;   // S71:原 +24 → 底部 36 对顶部 12;与代码面板同为上下 20
                        }
                        else if (!(block is CodeBlock codeBlock))
                        {
                            if (!(block is Table table))
                            {
                                if (!(block is Image image))
                                {
                                    if (block is HRule)
                                    {
                                        ent.H = 1f;
                                    }
                                }
                                else
                                {
                                    var asset = _images.Load(image.Src);
                                    nint value = asset.Bitmap;
                                    ent.Text = image.Alt;
                                    ent.Bmp = value;
                                    if (value != IntPtr.Zero)
                                    {
                                        (float w, float h) tuple = (asset.Width, asset.Height);
                                        float item = tuple.w;
                                        float item2 = tuple.h;
                                        float num3 = MathF.Min(item, MathF.Max(120f, num - 8f));
                                        float num4 = item2 * (num3 / MathF.Max(1f, item));
                                        ent.ImgW = num3;
                                        ent.ImgH = num4;
                                        ent.H = num4 + 6f;
                                    }
                                    else
                                    {
                                        ent.ImgW = 0f;
                                        ent.ImgH = 0f;
                                        ent.L = BuildLayout(new Run[1]
                                        {
                                            new Run(ImagePlaceholder(image.Alt, _images.Message(image.Src)))
                                        }, num, FmtBody, uniformSpacing: true, ent.Code, ent.Links);
                                        ent.Text = image.Alt;
                                        ent.H = Dw.LayoutH(ent.L) + 6f;
                                    }
                                }
                            }
                            else
                            {
                                int num5 = table.Header.Length;
                                float num6 = MathF.Max(120f, num);
                                float[] array = new float[num5];
                                nint[] array2 = new nint[num5];
                                for (int k = 0; k < num5; k++)
                                {
                                    array2[k] = BuildLayout((k < table.Header.Length) ? table.Header[k] : new List<Run>(), 10000f, FmtBody, uniformSpacing: true, ent.Code, ent.Links, bold: !table.PlainHeader);
                                    array[k] = ((k < table.Header.Length) ? Dw.LayoutW(array2[k]) : 40f);
                                    if (array2[k] != IntPtr.Zero)
                                    {
                                        Marshal.Release(array2[k]);
                                        array2[k] = IntPtr.Zero;
                                    }
                                    for (int l = 0; l < table.Rows.Length; l++)
                                    {
                                        List<Run> list = ((k < table.Rows[l].Length) ? table.Rows[l][k] : null);
                                        if (list != null && list.Count != 0)
                                        {
                                            nint num7 = BuildLayout(list, 10000f, FmtBody, uniformSpacing: true, ent.Code, ent.Links);
                                            float num8 = Dw.LayoutW(num7);
                                            if (num7 != IntPtr.Zero)
                                            {
                                                Marshal.Release(num7);
                                            }
                                            if (num8 > array[k])
                                            {
                                                array[k] = num8;
                                            }
                                        }
                                    }
                                }
                                float num9 = 0f;
                                for (int m = 0; m < num5; m++)
                                {
                                    array[m] = Math.Clamp(array[m] + 20f, 60f, num6 * 0.7f);
                                    num9 += array[m];
                                }
                                // Wide tables used to be squeezed until every column fitted, which
                                // turns a many-column table into a wall of one-word-per-line cells.
                                // Keep the natural widths and let the table scroll sideways instead;
                                // the first column is frozen so the row is always identifiable (30).
                                // Paper cannot scroll, so printing keeps the old squeeze.
                                if (num9 > num6 && WrapCodeForPaper)
                                {
                                    float num10 = num6 / num9;
                                    for (int n = 0; n < num5; n++)
                                    {
                                        array[n] *= num10;
                                    }
                                    num9 = num6;
                                }
                                // Freeze the first column only when it leaves room worth scrolling:
                                // a 70%-wide first column would pin most of the viewport and leave
                                // the reader a sliver. Nothing frozen then — the whole table slides.
                                ent.FrozenW = num5 > 1 && array[0] <= num6 * 0.5f ? array[0] : 0f;
                                ent.OverflowW = MathF.Max(0f, num9 - num6);   // num6 == TableViewportW(ent)
                                ent.ColWs = array;
                                int num11 = table.Rows.Length;
                                ent.Cells = new nint[(num11 + 1) * num5];
                                ent.RowHs = new float[num11 + 1];
                                for (int num12 = 0; num12 < num5; num12++)
                                {
                                    if (array2[num12] != IntPtr.Zero)
                                    {
                                        Marshal.Release(array2[num12]);
                                    }
                                    ent.Cells[num12] = BuildLayout(table.Header[num12], array[num12] - 16f, FmtBody, uniformSpacing: true, ent.Code, ent.Links, CellAlignment(table, num12), bold: !table.PlainHeader);
                                }
                                float num13 = 0f;
                                for (int num14 = 0; num14 < num5; num14++)
                                {
                                    num13 = MathF.Max(num13, Dw.LayoutH(ent.Cells[num14]));
                                }
                                ent.HeadH = num13 + 14f;
                                for (int num15 = 0; num15 < num11; num15++)
                                {
                                    float num16 = 0f;
                                    for (int num17 = 0; num17 < num5; num17++)
                                    {
                                        List<Run> runs = ((num17 < table.Rows[num15].Length) ? table.Rows[num15][num17] : new List<Run>());
                                        ent.Cells[(num15 + 1) * num5 + num17] = BuildLayout(runs, array[num17] - 16f, FmtBody, uniformSpacing: true, ent.Code, ent.Links, CellAlignment(table, num17));
                                        num16 = MathF.Max(num16, Dw.LayoutH(ent.Cells[(num15 + 1) * num5 + num17]));
                                    }
                                    ent.RowHs[num15] = num16 + 14f;
                                }
                                ent.H = ent.HeadH;
                                for (int num18 = 0; num18 < num11; num18++)
                                {
                                    ent.H += ent.RowHs[num18];
                                }
                                // 单元格文本/行字符长缓存:选区/命中路径每帧查表,不再重建(BlockTextOf 同源,块文本不可变)
                                ent.CellTexts = new string[num11 + 1][];
                                ent.CellTexts[0] = new string[num5];
                                for (int ct = 0; ct < num5; ct++) ent.CellTexts[0][ct] = CellText(table, 0, ct);
                                for (int cr = 0; cr < num11; cr++)
                                {
                                    ent.CellTexts[cr + 1] = new string[num5];
                                    for (int cc = 0; cc < num5; cc++) ent.CellTexts[cr + 1][cc] = CellText(table, cr + 1, cc);
                                }
                                ent.RowLens = new int[num11 + 1];
                                for (int rl = 0; rl <= num11; rl++)
                                {
                                    int rlSum = 0;
                                    for (int rc = 0; rc < num5; rc++) rlSum += (rc > 0 ? 3 : 0) + ent.CellTexts[rl][rc].Length;
                                    ent.RowLens[rl] = rlSum;
                                }
                                ent.Links.Clear();
                                int tableOffset = 0;
                                for (int row = 0; row <= num11; row++)
                                {
                                    for (int col = 0; col < num5; col++)
                                    {
                                        var runs = row == 0 ? table.Header[col] : col < table.Rows[row - 1].Length ? table.Rows[row - 1][col] : null;
                                        if (runs != null) foreach (var run in runs)
                                        {
                                            string destination = run.Img ? run.LinkOuter ?? run.Link : run.Link;
                                            if (destination != null) ent.Links.Add((tableOffset, run.Text.Length, destination));
                                            tableOffset += run.Text.Length;
                                        }
                                        if (col + 1 < num5) tableOffset += 3;
                                    }
                                    tableOffset++;
                                }
                            }
                        }
                        else
                        {
                            string lang = (codeBlock.Lang ?? "").Trim();
                            if (IsMathCode(codeBlock))
                            {
                                // 块级数学:排版树居中绘制(无灰底);解析失败降级原文
                                ent.NoCodeBg = true;
                                ent.MathTree = MathRender.Parse(Vt(codeBlock.Text).TrimEnd('\n'));
                                if (ent.MathTree != null)
                                {
                                    ent.Text = Vt(codeBlock.Text).TrimEnd('\n');
                                    ent.H = ent.MathTree.H + 16f;
                                }
                                else
                                {
                                    ent.L = BuildLayout(new Run[1] { new Run(Vt(codeBlock.Text).TrimEnd('\n')) }, MathF.Max(120f, num), FmtMono, uniformSpacing: false, ent.Code, ent.Links, 1, ent.MarkRanges);
                                    // Code keeps its own line structure: never re-break a source line.
                                    // Overflow is reached by scrolling the block sideways instead (30).
                                    // On paper there is nowhere to scroll, so wrap per character there.
                                    Dw.SetWordWrapping(ent.L, WrapCodeForPaper ? 2 /*WRAP_CHARACTER*/ : 1 /*NO_WRAP*/);
                                    ent.OverflowW = WrapCodeForPaper ? 0f : MathF.Max(0f, Dw.LayoutW(ent.L) - CodeViewportW(num));
                                    ent.Text = Vt(codeBlock.Text).TrimEnd('\n');
                                    ent.H = Dw.LayoutH(ent.L) + 16f;
                                }
                            }
                            else if (lang == "mermaid")
                            {
                                if (MermaidLite.TryParse(codeBlock.Text, out var mg))
                                {
                                    ent.Mermaid = mg;
                                    ent.Text = Vt(codeBlock.Text).TrimEnd('\n');
                                    var extent = MermaidLite.Extent(mg);
                                    ent.H = extent.h + CodeBottomPad;   // S86:下方 Margin + 8 + 下伸 12 = 上方 20 + Margin,上下留白相等(原 +24)
                                    // 宽图与宽代码同规则:裁在底板内、横向滚动;纸面无处可滚,保持原样(S79)。
                                    ent.OverflowW = WrapCodeForPaper ? 0f : MathF.Max(0f, extent.w - CodeViewportW(num));
                                    ent.MermaidInset = MathF.Max(0f, (CodeViewportW(num) - extent.w) / 2f);   // S88:小图居中,与公式块一致;宽图为 0,照旧横向滚动
                                }
                                else
                                {
                                    ent.L = BuildLayout(new Run[1] { new Run("(Mermaid 图表类未支持,以下为源码)\n" + Vt(codeBlock.Text).TrimEnd('\n')) }, MathF.Max(120f, num - 40f), FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                    ent.Text = Vt(codeBlock.Text).TrimEnd('\n');
                                    ent.H = Dw.LayoutH(ent.L) + CodeBottomPad;   // A4:代码底板底部留白 8 + 下伸 12 = 20,与上内边距对称(原 +24 → 36)
                                }
                            }
                            else
                            {
                                // On paper the per-line highlighted segments cannot wrap and cannot be
                                // scrolled, so anything past the page edge would be lost outright. Fall
                                // back to the plain ent.L path there: losing syntax colour on a printout
                                // is an acceptable trade, losing code is not.
                                if (CodeHighlight.Known(lang) && Vt(codeBlock.Text).Length < 60000 && !WrapCodeForPaper)
                                {
                                    // 语法高亮:逐行按 token 分段布局(等宽定位),同色相邻自动合并在分词器内。
                                    // ent.L 仍照常构建:选区/命中/复制继续走原路径,HL 只负责绘制。
                                    var toks = CodeHighlight.Tokenize(Vt(codeBlock.Text), lang);
                                    var codeLines = Vt(codeBlock.Text).TrimEnd('\n').Split('\n');
                                    ent.HL = new List<List<(nint, float, int)>>();
                                    float hh = 0f;
                                    int g = 0;
                                    float lineH = 0f;
                                    foreach (string ln in codeLines)
                                    {
                                        var segs = new List<(nint, float, int)>();
                                        int ll = ln.Length;
                                        var kinds = new int[ll];
                                        foreach (var (ts, tl, tk) in toks)
                                        {
                                            if (ts >= g + ll) break;   // token 按位置有序:越过本行即止
                                            if (ts + tl <= g) continue;
                                            int a = Math.Max(ts - g, 0), b = Math.Min(ts + tl - g, ll);
                                            for (int c2 = a; c2 < b; c2++) kinds[c2] = tk;
                                        }
                                        int p = 0;
                                        while (p < ll)
                                        {
                                            int q = p;
                                            while (q < ll && kinds[q] == kinds[p]) q++;
                                            nint lay = BuildLayout(new Run[1] { new Run(ln[p..q]) }, MathF.Max(120f, num - 40f), FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                            Dw.SetWordWrapping(lay, 1 /*NO_WRAP*/);   // 分段按每源行一行的模型顺序绘制,段内折行会与后续分段重叠
                                            segs.Add((lay, Dw.LayoutW(lay), kinds[p]));
                                            if (lineH == 0f) lineH = Dw.LayoutH(lay);
                                            p = q;
                                        }
                                        if (segs.Count == 0)
                                        {
                                            nint lay = BuildLayout(new Run[1] { new Run(" ") }, MathF.Max(120f, num - 40f), FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                            segs.Add((lay, 0f, 0));
                                            if (lineH == 0f) lineH = Dw.LayoutH(lay);
                                        }
                                        ent.HL.Add(segs);
                                        hh += lineH;
                                        g += ll + 1;
                                    }
                                    ent.HLLineH = lineH == 0f ? 24f : lineH;
                                    ent.H = hh + CodeBottomPad;   // A4:代码底板底部留白 8 + 下伸 12 = 20,与上内边距对称(原 +24 → 36)
                                    // Highlighted rows never wrap either; measure the widest
                                    // row so the same sideways scrolling applies (30).
                                    float widest = 0f;
                                    foreach (var segs in ent.HL)
                                    {
                                        float rowW = 0f;
                                        foreach (var (_, segW, _) in segs) rowW += segW;
                                        if (rowW > widest) widest = rowW;
                                    }
                                    ent.OverflowW = MathF.Max(0f, widest - CodeViewportW(num));
                                }
                                ent.L = BuildLayout(new Run[1]
                                {
                                new Run(Vt(codeBlock.Text).TrimEnd('\n'))
                                }, MathF.Max(120f, num - 40f), FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                ent.Text = Vt(codeBlock.Text).TrimEnd('\n');
                                if (ent.HL == null)
                                {
                                    // Code keeps its own line structure: never re-break a source
                                    // line. Overflow is reached by scrolling the block sideways (30).
                                    // On paper there is nowhere to scroll, so wrap per character there.
                                    Dw.SetWordWrapping(ent.L, WrapCodeForPaper ? 2 /*WRAP_CHARACTER*/ : 1 /*NO_WRAP*/);
                                    ent.OverflowW = WrapCodeForPaper ? 0f : MathF.Max(0f, Dw.LayoutW(ent.L) - CodeViewportW(num));
                                    ent.H = Dw.LayoutH(ent.L) + CodeBottomPad;   // A4:代码底板底部留白 8 + 下伸 12 = 20,与上内边距对称(原 +24 → 36)
                                }
                            }
                        }
                    }
                    else
                    {
                        string markerText = listItem.Continuation ? "" : listItem.Marker;
                        if (listItem.Task == 1) markerText = "☐";
                        else if (listItem.Task == 2) markerText = "☑";
                        ent.Marker = BuildLayout(new Run[1]
                        {
                            new Run(markerText)
                        }, 40f, FmtBody, uniformSpacing: false, ent.Code, ent.Links);
                        ent.L = BuildLayout(listItem.Inlines, MathF.Max(120f, num - 26f), FmtBody, uniformSpacing: true, ent.Code, ent.Links);
                        CjkPunct.TrimLineStarts(ent.L, _lastBuiltText);   // 折行后行首开括号(S75)
                        float x = Dw.LayoutH(ent.Marker);
                        float y = Dw.LayoutH(ent.L);
                        ent.H = MathF.Max(x, y);
                        if (listItem.Sub != null)
                        {
                            // 项内子块:逐子块布局堆叠(样式与独立块一致),文本串与 BlockTextOf 同源
                            ent.SubRich = new();
                            ent.SubLayouts = new List<nint>();
                            ent.SubHeights = new List<float>();
                            ent.SubStarts = new List<int>();
                            ent.SubTexts = new List<string>();
                            ent.SubXs = new List<float>();
                            ent.SubYs = new List<float>();
                            ent.SubCodes = new();
                            ent.SubMarks = new();
                            ent.SubOverflowW = new List<float>();
                            ent.SubScrollX = new List<float>();
                            ent.SubTop = listItem.Inlines.Count == 0 ? 0f : y + 6f;
                            int subOff = string.Concat(listItem.Inlines.Select((Run r) => r.Text)).Length + (listItem.Inlines.Count > 0 ? 1 : 0);
                            float subAcc = 0f;
                            foreach (Block sb in listItem.Sub)
                            {
                                nint sl;
                                float sw = MathF.Max(120f, num - 46f);
                                string stx;
                                int codesBefore = ent.Code.Count, linksBefore = ent.Links.Count, marksBefore = ent.MarkRanges.Count;
                                Ent rich = null;
                                float nestedOverflow = 0f;
                                if (sb is Table or Image)
                                {
                                    rich = Build(i, sb, sw);
                                    sl = IntPtr.Zero;
                                    stx = BlockTextOf(sb);
                                    foreach (var link in rich.Links) ent.Links.Add(link);
                                }
                                else if (sb is CodeBlock scb)
                                {
                                    stx = SubBlockText(scb);
                                    sl = BuildLayout(new Run[1] { new Run(stx) }, sw, FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                    // Same rule as a top-level code block: column alignment carries
                                    // meaning, so do not wrap on screen — scroll sideways instead.
                                    // Paper cannot scroll, so printing keeps wrapping (30).
                                    if (!WrapCodeForPaper && sl != IntPtr.Zero)
                                    {
                                        Dw.SetWordWrapping(sl, 1 /*NO_WRAP*/);
                                        nestedOverflow = MathF.Max(0f, Dw.LayoutW(sl) - NestedViewportW(sw));
                                    }
                                }
                                else if (sb is Quote sq)
                                {
                                    sl = BuildLayout(sq.Inlines, sw, FmtBody, uniformSpacing: true, ent.Code, ent.Links, 0, ent.MarkRanges);
                                    stx = string.Concat(sq.Inlines.Select((Run r) => r.Text));
                                }
                                else if (sb is Para sp)
                                {
                                    sl = BuildLayout(sp.Inlines, sw, FmtBody, uniformSpacing: true, ent.Code, ent.Links);
                                    stx = string.Concat(sp.Inlines.Select((Run r) => r.Text));
                                }
                                else if (sb is HtmlBlock shb)
                                {
                                    sl = BuildLayout(new Run[1] { new Run(Vt(shb.Text).TrimEnd('\n')) }, sw, FmtMono, uniformSpacing: false, ent.Code, ent.Links);
                                    stx = Vt(shb.Text).TrimEnd('\n');
                                }
                                else if (sb is HRule)
                                {
                                    sl = IntPtr.Zero;
                                    stx = "";
                                }
                                else if (sb is Heading headingSub)
                                {
                                    nint fmt = headingSub.Level switch { 1 => FmtH1, 2 => FmtH2, 3 => FmtH3, _ => FmtH4 };
                                    sl = BuildLayout(headingSub.Inlines, sw, fmt, uniformSpacing: false, ent.Code, ent.Links);
                                    HeadingWrap.Refine(sl, _lastBuiltText, sw);
                                    stx = string.Concat(headingSub.Inlines.Select(r => r.Text));
                                }
                                else
                                {
                                    stx = BlockTextOf(sb);
                                    sl = BuildLayout(new[] { new Run(stx) }, sw, FmtBody, uniformSpacing: true, ent.Code, ent.Links);
                                }
                                ent.SubCodes.Add(ent.Code.GetRange(codesBefore, ent.Code.Count - codesBefore));
                                ent.Code.RemoveRange(codesBefore, ent.Code.Count - codesBefore);
                                ent.SubMarks.Add(ent.MarkRanges.GetRange(marksBefore, ent.MarkRanges.Count - marksBefore));
                                ent.MarkRanges.RemoveRange(marksBefore, ent.MarkRanges.Count - marksBefore);
                                for (int lk = linksBefore; lk < ent.Links.Count; lk++)
                                {
                                    var link = ent.Links[lk];
                                    ent.Links[lk] = (link.s + subOff, link.l, link.url);
                                }
                                ent.SubRich.Add(rich);
                                ent.SubLayouts.Add(sl);
                                ent.SubXs.Add(sb is Quote ? 16f : sb is CodeBlock or HtmlBlock ? NestedPanelInsetX : 6f);
                                float sh = rich != null ? rich.H : sl != IntPtr.Zero ? Dw.LayoutH(sl) : (sb is HRule ? 10f : 0f);
                                ent.SubHeights.Add(sh);
                                ent.SubYs.Add(ent.SubTop + subAcc + NestedPad(sb));
                                ent.SubStarts.Add(subOff);
                                ent.SubTexts.Add(stx);
                                ent.SubOverflowW.Add(nestedOverflow);
                                ent.SubScrollX.Add(0f);
                                subOff += stx.Length + 1;
                                subAcc += sh + 2f * NestedPad(sb) + 6f;
                            }
                            // ent.Text = 首段 + '\n' + 各子块(与 BlockTextOf/复制链一致)
                            var txtB = new System.Text.StringBuilder(string.Concat(listItem.Inlines.Select((Run r) => r.Text)));
                            for (int k = 0; k < ent.SubTexts.Count; k++)
                            {
                                if (k > 0 || listItem.Inlines.Count > 0) txtB.Append('\n');
                                txtB.Append(ent.SubTexts[k]);
                            }
                            ent.Text = txtB.ToString();
                            ent.H = MathF.Max(x, ent.SubTop + subAcc - 6f);
                        }
                        else
                        {
                            ent.Text = _lastBuiltText;
                        }
                        ent.H += (listItem.Loose ? 9f : 0f);
                        ent.H += (listItem.GroupBreakAfter ? 9f : 0f);
                    }
                }
                else
                {
                    ent.L = BuildLayout(quote.Inlines, MathF.Max(120f, num - 20f), DeskQuote(block) ? FmtQuote : FmtBody, uniformSpacing: true, ent.Code, ent.Links, 0, ent.MarkRanges);
                    CjkPunct.TrimLineStarts(ent.L, _lastBuiltText);   // 折行后行首开括号(S75)
                    ent.Text = _lastBuiltText;
                    ent.H = Dw.LayoutH(ent.L);
                }
            }
            else
            {
                int totalChars = 0;
                foreach (Run r in para.Inlines) totalChars += r.Text.Length;
                // KP 逐行切片建布局:行内图片与原生公式(S77)是整块内嵌对象,留给 DirectWrite 断行
                if (totalChars > 200 && _colW > 0f && !para.Inlines.Any(r => r.Img || r.MathNative))
                {
                    // KP 最优断行:长段落按 Knuth-Plass 切行,每行单独建布局(样式随切片 Run 保留)。
                    // 渲染/选区/命中/链接全部走逐行路径——KP 不是只占位,是真实的逐行渲染。
                    string fullText = string.Concat(para.Inlines.Select((Run r) => r.Text));
                    var wordWidth = new Func<string, double>(delegate (string w)
                    {
                        // 单字符走宽度缓存(同字号同字符宽度恒定,SetBodySize 清空);串不缓存(避免字距差异)
                        if (w.Length == 1 && CharacterWidths.TryGetValue(w[0], out float cw)) return cw;
                        nint tmp = BuildLayout(new Run[1] { new Run(w) }, 10000f, FmtBody, uniformSpacing: false, ent.Code, ent.Links);
                        float width = Dw.LayoutW(tmp);
                        if (tmp != IntPtr.Zero) Marshal.Release(tmp);
                        if (w.Length == 1) CharacterWidths[w[0]] = width;
                        return width;
                    });
                    var charMap = new List<int>();
                    var items = KpCjk.TextToItems(fullText, wordWidth, charMap, 0.04f * BodySize);   // I9:逐字胶 ±0.26px→±0.04em(16px 0.64px),JUST 落地后 KP 全局寻优从摆设变武器
                    KpCjk.SplitOverlongRuns(items, charMap, fullText, num);   // S83:比整列还宽的网址折在串内 “/” 后
                    var spec = KpLineSpec.Uniform(num);
                    var kpParam = new KpParams { LinePenalty = 10.0, Tolerance = 500.0, PreTolerance = 200.0, EmergencyStretch = 1.5 * RenderResources.BodySize };   // A5:1.5em 紧急伸展(TeX 常用 1–3em),避免落到零伸展紧急遍
                    var breaks = KpBreak.Paragraph(items, spec, kpParam);
                    ent.Text = fullText;
                    ent.KpLines = new List<string>();
                    ent.KpStarts = new List<int>();
                    ent.KpLayouts = new List<nint>();
                    ent.KpHeights = new List<float>();
                    ent.KpCodes = new List<List<(int, int)>>();
                    // 断点 item(通常是空格 glue)的字符归属上一行;charMap 记录每个 item 的字符数
                    int prev = 0;
                    foreach (int br in breaks)
                    {
                        int charPos = 0;
                        for (int ii = 0; ii <= br && ii < charMap.Count; ii++) charPos += charMap[ii];
                        charPos = Math.Min(charPos, fullText.Length);
                        if (charPos > prev) ent.KpLines.Add(fullText[prev..charPos]);
                        prev = charPos;
                    }
                    if (prev < fullText.Length) ent.KpLines.Add(fullText[prev..]);
                    // 段内全局行内码/链接范围(供链接命中)
                    int scan = 0;
                    foreach (Run r in para.Inlines)
                    {
                        if (r.Code) ent.Code.Add((scan, r.Text.Length));
                        if (r.Link != null) ent.Links.Add((scan, r.Text.Length, r.Link));
                        scan += r.Text.Length;
                    }
                    float hAcc = 0f;
                    int off = 0;
                    for (int ki = 0; ki < ent.KpLines.Count; ki++)
                    {
                        string ln = ent.KpLines[ki];
                        var lc = new List<(int, int)>();
                        var ll = new List<(int, int, string)>();
                        // 末行豁免:单行布局无"段落末行"概念,P1 探针证实 JUST 会拉伸它,必须手动不 JUST
                        // 硬换行(S75):行尾 \n 不进布局(否则多出一空行),该行同段末行不两端对齐
                        bool hardEnd = ln.EndsWith('\n');
                        bool justLine = !hardEnd && ki < ent.KpLines.Count - 1 && Justification.HasInnerBreak(ln);
                        nint layout = BuildLayout(SliceRuns(para.Inlines, off, hardEnd ? ln.Length - 1 : ln.Length), num, FmtBody, uniformSpacing: true, lc, ll, justLine: justLine);
                        if (ki > 0 && !ent.KpLines[ki - 1].EndsWith('\n')) CjkPunct.TrimFirst(layout, ln);   // KP 行首开括号(S75;硬换行后不压)
                        if (justLine)
                        {
                            // 先以左齐量自然宽度(NO_WRAP 下即单行宽),再由 Justification 判定是否拉伸;不拉伸则左齐 + 正常折行。
                            Dw.SetTextAlignment(layout, 0);
                            if (!Justification.ShouldJustify(ln, Dw.LayoutW(layout), num, BodySize)) Dw.SetWordWrapping(layout, 0);
                            else Dw.SetTextAlignment(layout, 3 /*JUST*/);
                        }
                        float lh = Dw.LayoutH(layout);
                        ent.KpLayouts.Add(layout);
                        ent.KpStarts.Add(off);
                        ent.KpHeights.Add(lh);
                        ent.KpCodes.Add(lc);
                        hAcc += lh;
                        off += ln.Length;
                    }
                    ent.H = hAcc;
                }
                else
                {
                    ent.L = BuildLayout(para.Inlines, num, FmtBody, uniformSpacing: true, ent.Code, ent.Links, 0, ent.MarkRanges);
                    CjkPunct.TrimLineStarts(ent.L, _lastBuiltText);   // 折行后行首开括号(S75)
                    ent.Text = _lastBuiltText;
                    ent.H = Dw.LayoutH(ent.L);
                }
            }
        }
        else
        {
            nint fmt = ((heading.Level == 1) ? FmtH1 : ((heading.Level == 2) ? FmtH2 : ((heading.Level == 3) ? FmtH3 : FmtH4)));
            ent.L = BuildLayout(heading.Inlines, num, fmt, uniformSpacing: false, ent.Code, ent.Links);
            float tracking = RenderResources.Reading.HeadingTracking(heading.Level);   // U3:H1/H2 字距
            if (tracking > 0f && ent.L != IntPtr.Zero && _lastBuiltText.Length > 0) Dw.SetCharacterSpacing(ent.L, 0f, tracking * Dw.FontSize(ent.L), 0, (uint)_lastBuiltText.Length);
            HeadingWrap.Refine(ent.L, _lastBuiltText, num);   // 末行孤字平衡、行首开括号压缩(S74)
            ent.Text = _lastBuiltText;
            ent.H = Dw.LayoutH(ent.L);
        }
        return ent;
    }

    // D5 收起目录标尺:只画一、二级标题刻度,按文档位置等比分布;当前章 Accent。
    // 没有可画的刻度时画一个“列表”入口,而不是伪造刻度(§4.4)。不铺底色,只画刻度。
    private unsafe void PaintRuler(float top, float bottom, float offsetDip)
    {
        float s = Scale > 0f ? Scale : 1f;
        float one = MathF.Max(1f, MathF.Round(s)) / s;   // 至少 1 物理像素、落整像素的细线
        float Snap(float v) => MathF.Round(v * s) / s;
        int cur = -1;
        for (int i = 0; i < TocEntries.Count; i++)
            if (TocEntries[i].level <= 2 && TocEntries[i].y <= offsetDip + 120f) cur = i;
        if (cur < 0)
            for (int i = 0; i < TocEntries.Count; i++)
                if (TocEntries[i].level <= 2) { cur = i; break; }   // 文档开头在第一章之前:指示将要进入的章
        int chapters = 0, parts = 0;
        foreach (var entry in TocEntries) { if (entry.level <= 2) chapters++; if (entry.level == 1) parts++; }
        int depth = OverlayGeometry.RulerDepth(chapters, bottom - top);
        float minGap = OverlayGeometry.RulerMinGap(depth == 2 ? chapters : parts, bottom - top);
        bool any = false;
        float last = float.NegativeInfinity, curY = float.NaN;
        for (int i = 0; i < TocEntries.Count; i++)
        {
            var (_, level, _, y) = TocEntries[i];
            if (level > 2) continue;
            any = true;
            float ty = Snap(OverlayGeometry.RulerTickY(y, TotalH, top, bottom - top));
            if (i == cur) { curY = ty; continue; }
            if (level > depth) continue;   // 拥挤时只画一级,当前章标记仍按一、二级定位
            if (ty - last < minGap) continue;   // 密集章节合并,不糊成一条
            last = ty;
            var tick = new D2D1_RECT_F { left = level == 1 ? 10f : 14f, top = ty, right = 22f, bottom = ty + one };
            PFill(Rt, &tick, RenderResources.HighContrast ? BMuted : BControlBorder);   // S89:刻度比正文次要色淡一档,当前章仍是 Accent;高对比度保持 GrayText(S93)
        }
        if (!float.IsNaN(curY))
        {
            var mark = new D2D1_RECT_F { left = 8f, top = curY - one, right = 24f, bottom = curY + one };
            PFill(Rt, &mark, BAccent);
        }
        if (!any)
        {
            for (int k = 0; k < 3; k++)
            {
                float ly = Snap(top + 12f + k * 5f);
                var line = new D2D1_RECT_F { left = 10f, top = ly, right = k == 2 ? 18f : 22f, bottom = ly + one };
                PFill(Rt, &line, BMuted);
            }
        }
    }

    // §4.5 E5:代码块外框、复制按钮与语言标签的唯一几何(文档坐标),绘制、命中与测试共用。
    private const float CodeChipW = 22f, CodeChipH = 18f;   // 试用反馈：复制按钮只放图标（两张叠放的方块，复制后换成对勾），不写字
    public bool CodeCopyAlways;              // 最近一次指针输入为触控/笔:无悬停环境时复制按钮常显
    private int _codeHover = -1, _codeCopied = -1;
    internal int CodeCopiedBlock => _codeCopied;
    private readonly Dictionary<string, nint> _codeChromeLayouts = new();

    private static string CodeLangLabel(CodeBlock code)
    {
        string lang = (code.Lang ?? "").Trim();
        int space = lang.IndexOfAny(new[] { ' ', '\t', '{' });
        if (space >= 0) lang = lang.Substring(0, space);
        return lang.Length == 0 ? null : lang;
    }

    private nint ChromeLayout(string text)
    {
        if (!_codeChromeLayouts.TryGetValue(text, out nint layout)) _codeChromeLayouts[text] = layout = Dw.TrimLayout(text, FmtUi, 120f);
        return layout;
    }

    private void ReleaseChromeLayouts()
    {
        foreach (var layout in _codeChromeLayouts.Values) if (layout != 0) Marshal.Release(layout);
        _codeChromeLayouts.Clear();
    }

    private bool CodeChrome(int i, out D2D1_RECT_F box, out OverlayBox chip, out OverlayBox label)
    {
        box = default; chip = default; label = default;
        if (i < 0 || i >= _blocks.Count || _blocks[i] is not CodeBlock code || IsMathCode(code) || i >= _ys.Length) return false;
        Ent ent = GetEnt(i);
        if (ent == null || ent.NoCodeBg) return false;
        Block block = _blocks[i];
        float x = _colX + Indent(block);
        box = new D2D1_RECT_F
        {
            left = PanelLeft(block, x),
            top = _ys[i] - BlockTopOutset(block),
            right = PanelRight(),
            bottom = _ys[i] + _heights[i] + 12f
        };
        chip = new OverlayBox(box.right - 6f - CodeChipW, box.top + 2f, CodeChipW, CodeChipH);
        if (CodeLangLabel(code) is string lang && ChromeLayout(lang) is nint layout && layout != 0)
        {
            float w = MathF.Min(120f, Dw.LayoutW(layout));
            label = new OverlayBox(chip.X - 6f - w, chip.Y, w, CodeChipH);
        }
        return true;
    }

    private bool CodeChipVisible(int i)
    {
        if (WrapCodeForPaper) return false;
        if (i == _codeHover || CodeCopyAlways) return true;
        if (_selStart < 0 || i >= _blockStart.Length) return false;
        int head = SelHead, start = _blockStart[i];
        return head >= start && head <= start + (GetEnt(i)?.Text?.Length ?? 0);
    }

    private int CodeBlockAt(float x, float documentY)
    {
        int block = _codeTargets.BlockAtPoint(x, documentY);
        if (block < 0 || !CodeChrome(block, out var box, out _, out _)) return -1;
        return x >= box.left && x <= box.right && documentY >= box.top && documentY <= box.bottom ? block : -1;
    }

    // 悬停跟踪:返回是否需要重绘。指针离开“已复制”的块时复原按钮图标。
    public bool SetCodeHover(float x, float documentY) => SetHoverBlock(CodeBlockAt(x, documentY));
    public bool ClearCodeHover() => SetHoverBlock(-1);
    private bool SetHoverBlock(int block)
    {
        if (block == _codeHover) return false;
        if (_codeCopied != block) _codeCopied = -1;
        _codeHover = block;
        return true;
    }

    private int CodeChipHit(float x, float documentY)
    {
        int block = CodeBlockAt(x, documentY);
        if (block < 0 || !CodeChipVisible(block) || !CodeChrome(block, out _, out var chip, out _)) return -1;
        return x >= chip.X && x <= chip.X + chip.W && documentY >= chip.Y && documentY <= chip.Y + chip.H ? block : -1;
    }

    public bool OverCodeChip(float x, float documentY) => CodeChipHit(x, documentY) >= 0;

    // 点击复制按钮:返回与右键“复制代码块”(2080)相同的原文;按钮随后显示对勾。
    public string CopyChipAt(float x, float documentY)
    {
        int block = CodeChipHit(x, documentY);
        if (block < 0) return null;
        _codeCopied = block;
        return _codeTargets.AtPoint(x, documentY);
    }

    public void ResetCodeCopied() => _codeCopied = -1;

    internal bool CodeChromeForTest(int nth, out OverlayBox box, out OverlayBox chip, out OverlayBox label, out float textTop, out float previousBottom, out int start)
    {
        box = chip = label = default; textTop = previousBottom = 0f; start = -1;
        for (int i = 0, n = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is not CodeBlock) continue;
            if (n++ != nth) continue;
            if (!CodeChrome(i, out var r, out chip, out label)) return false;
            box = new OverlayBox(r.left, r.top, r.right - r.left, r.bottom - r.top);
            textTop = _ys[i]; start = _blockStart[i];
            previousBottom = i > 0 ? _ys[i - 1] + _heights[i - 1] : 0f;
            return true;
        }
        return false;
    }

    // 语言标签常显(Muted);复制按钮与查找面板按钮同式(Canvas 底 + 1 DIP ControlBorder),按需出现。打印不画。
    // 「返回原阅读处」标签:正文列水平居中,底边距视口底缘 16 DIP(在进度线之上),高 24,宽随文字;
    // 坐标吸附物理像素,1 DIP 描边保持实色。坐标系与 Paint 的 contentTopDip/viewHDip 相同(窗口 DIP)。
    public OverlayBox ReturnHintBox(float contentTopDip, float viewHDip)
    {
        float px = Scale > 0f ? Scale : 1f;
        static float Snap(float v, float s) => MathF.Round(v * s) / s;
        nint text = ChromeLayout(ReturnHint.Text);
        float w = Snap((text != 0 ? Dw.LayoutW(text) : 96f) + 24f, px), h = 24f;
        return new OverlayBox(Snap(_colX + (_colW - w) / 2f, px), Snap(contentTopDip + viewHDip - 16f - h, px), w, h);
    }

    private unsafe void DrawReturnHint(ReturnHint hint, float contentTopDip, float viewHDip)
    {
        var b = ReturnHintBox(contentTopDip, viewHDip);
        var outer = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = b.X, top = b.Y, right = b.X + b.W, bottom = b.Y + b.H }, radiusX = 4f, radiusY = 4f };
        PFillRound(Rt, &outer, BControlBorder);
        var inner = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = b.X + 1f, top = b.Y + 1f, right = b.X + b.W - 1f, bottom = b.Y + b.H - 1f }, radiusX = 3f, radiusY = 3f };
        PFillRound(Rt, &inner, hint.Hover ? BSurfaceHover : BCanvas);
        nint text = ChromeLayout(ReturnHint.Text);
        if (text != 0)
            PDraw(Rt, new D2D1_POINT_2F { x = b.X + (b.W - Dw.LayoutW(text)) / 2f, y = b.Y + (b.H - Dw.LayoutH(text)) / 2f }, text, BInk, 0);
    }

    private void DrawCodeChrome(int i, float dy)
    {
        if (WrapCodeForPaper || !CodeChrome(i, out _, out var chip, out var label)) return;
        if (label.W > 0f && CodeLangLabel((CodeBlock)_blocks[i]) is string lang && ChromeLayout(lang) is nint tag && tag != 0)
            PDraw(Rt, new D2D1_POINT_2F { x = label.X, y = label.Y + dy + (label.H - Dw.LayoutH(tag)) / 2f }, tag, BMuted, 0);
        if (!CodeChipVisible(i)) return;
        // 只画图标，不加外框（命中区仍是整个 chip）
        float ix = chip.X + (chip.W - CopyIcon) / 2f, iy = chip.Y + dy + (chip.H - CopyIcon) / 2f;
        if (i == _codeCopied)
        {
            // 对勾：已复制
            PLine(Rt, new D2D1_POINT_2F { x = ix + 1f, y = iy + 6.5f }, new D2D1_POINT_2F { x = ix + 4.5f, y = iy + 10f }, BAccent, 1.5f, 0);
            PLine(Rt, new D2D1_POINT_2F { x = ix + 4.5f, y = iy + 10f }, new D2D1_POINT_2F { x = ix + 11f, y = iy + 2.5f }, BAccent, 1.5f, 0);
            return;
        }
        // 经典复制图标：后一张只描边，前一张用代码块底色盖住重叠处再描边
        const float side = 8.5f, shift = 4.5f;
        var back = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = ix + 0.6f, top = iy + 0.6f, right = ix + side, bottom = iy + side }, radiusX = 1.8f, radiusY = 1.8f };
        PStrokeRound(Rt, &back, BInk2, 1.2f, 0);
        var front = new D2D1_ROUNDED_RECT { rect = new D2D1_RECT_F { left = ix + shift, top = iy + shift, right = ix + shift + side - 0.6f, bottom = iy + shift + side - 0.6f }, radiusX = 1.8f, radiusY = 1.8f };
        PFillRound(Rt, &front, BCode);
        PStrokeRound(Rt, &front, BInk2, 1.2f, 0);
    }

    private const float CopyIcon = 13f;

    // §4.5 E2:引用竖线 2.5 DIP,按物理像素取整(至少 1 px)并吸附左缘,任何 DPI 下都是实色、不发虚。
    private static float QuoteRailWidth => MathF.Max(1f, MathF.Round(2.5f * (Scale > 0f ? Scale : 1f), MidpointRounding.AwayFromZero)) / (Scale > 0f ? Scale : 1f);
    private static float QuoteRailLeft(float left) => MathF.Round(left * (Scale > 0f ? Scale : 1f)) / (Scale > 0f ? Scale : 1f);
    // 同一引用运行内,下一块仍在该层 → 该层竖线跨块间空白贯穿(空行分断的相邻引用 QSeq 不同,保持分开)
    internal static bool QuoteRailJoins(Block current, Block next, int level)
        => next != null && current.QuoteDepth >= level && next.QuoteDepth >= level && next.QSeq == current.QSeq;

    public unsafe void Paint(RECT cr, nint hdc, float offsetDip, float viewHDip, float contentTopDip, IReadOnlyList<string> recentFiles = null, float overlayHeight = 0f, ReturnHint returnHint = null, bool zenQuiet = false, bool batch = false)
    {
        if (_linkBrush != IntPtr.Zero && _linkBrush != BAccent) { ClearCache(); _linkBrush = IntPtr.Zero; }   // 图形资源换过:旧布局里的链接画刷已失效
        EnsureLayout();
        LastViewHDip = viewHDip;
        _selIsHit = SelectionIsCurrentHit();
        _inkRectSet = false;
        _selBoundsSet = false;
        RECT rECT = default(RECT);
        rECT.L = 0;
        rECT.T = 0;
        rECT.R = cr.R - cr.L;
        rECT.B = cr.B - cr.T;
        RECT rECT2 = rECT;
        if (!batch)
        {
            PBind(Rt, hdc, &rECT2);
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)51 * (nint)sizeof(nint))))(Rt, 96f * Scale, 96f * Scale);
            PBegin(Rt);
        }
        D2D1_RECT_F d2D1_RECT_F = default(D2D1_RECT_F);
        d2D1_RECT_F.left = 0f;
        d2D1_RECT_F.top = contentTopDip;   // DIP:与 DrawToolbar 的 DIP 底边(22)衔接,避免中缝不清屏
        d2D1_RECT_F.right = cr.R - cr.L;
        d2D1_RECT_F.bottom = cr.B - cr.T;
        D2D1_RECT_F d2D1_RECT_F2 = d2D1_RECT_F;
        D2D1_COLOR_F d2D1_COLOR_F = CanvasColor;
        D2D1_COLOR_F canvasRef = d2D1_COLOR_F;
        PClear(Rt, &canvasRef);
        PPushClip(Rt, &d2D1_RECT_F2, 0);
        PaintPaperGrain(d2D1_RECT_F2.left, d2D1_RECT_F2.top, d2D1_RECT_F2.right, d2D1_RECT_F2.bottom);   // U2b：desk 纸纹（classic 下不画）
        bool tocClip = false;   // 内层 TOC 裁剪是否已压栈(异常路径 finally 里要配对弹出)
        ulong num12 = 0uL;
        ulong num13 = 0uL;
        try
        {
            if (_blocks.Count == 0)
            {
                // I8 空态合并版(chrome §3.4 × ergo §6):提示行(ink)+最近文件≤3 条(muted,hover accent,可点)+功能预告。
                // 无 M↓ 图形无吉祥物——空态是唯一允许教育的版面,但它是预告片不是说明书。
                EmptyHits.Clear();   // 抽查修复:先清后填(原 clear 在本分支之后,刚填的命中矩形立刻被抹掉→悬停/点击全失效)
                float cy = contentTopDip + viewHDip * 0.30f;
                DrawHintAt("拖入 .md 文件,或点左上角「打开」", cy, BInk, false, null);
                cy += 2.1f * BodySize;
                var rec = recentFiles;
                if (rec != null && rec.Count > 0)
                {
                    DrawHintAt("最近打开", cy, BMuted, false, null);
                    cy += 1.9f * BodySize;
                    for (int ri = 0; ri < rec.Count && ri < 3; ri++)
                    {
                        DrawHintAt("·  " + System.IO.Path.GetFileName(rec[ri]), cy, ri == EmptyHover ? BAccent : BMuted, true, rec[ri]);
                        cy += 1.9f * BodySize;   // 抽查修复:逐条步进(原循环后一次性累加,3 条重印在同一行)
                    }
                }
                DrawHintAt("提示:点图片可放大 · 全部快捷键在「更多」里", cy + 1.2f * BodySize, BMuted, false, null);
            }
            float num2 = offsetDip - 260f;
            float num3 = offsetDip + viewHDip + 260f;
            if (_blocks.Count != 0) EmptyHits.Clear();   // 有文档时清掉空态遗留命中(空态分支自带先清后填)
            _codeTargets.Clear();
            ImageRects.Clear();   // 无条件清:挂在"块0可见"分支内时,块0滚出视口后会残留上一帧的陈旧矩形
            for (int i = 0; i < _blocks.Count; i++)
            {
                Block block = _blocks[i];
                if (_ys[i] + _heights[i] < num2)
                {
                    continue;
                }
                if (_ys[i] > num3)
                {
                    break;
                }
                Ent ent = GetEnt(i);
                float num4 = _colX + Indent(block);
                float num5 = _ys[i] - offsetDip + contentTopDip;
                if (block is Heading && _headingMarks.Count > 0) PaintHeadingMark(block, ent, num4, num5);   // U3:H2 方印、H3 短线
                // Quote rails follow structural depth, including lists and non-paragraph blocks.
                // 带底板的块(代码/HTML):竖线与底板等高(上伸 BlockTopOutset、下伸 12),不只覆盖文字。
                float railOut = BlockTopOutset(block);
                for (int q = 1; q <= block.QuoteDepth; q++)
                {
                    D2D1_RECT_F rail = new D2D1_RECT_F
                    {
                        left = QuoteRailLeft(_colX + q * 20f - 13f),
                        right = QuoteRailLeft(_colX + q * 20f - 13f) + (DeskRail(block) ? DeskRailWidth : QuoteRailWidth),
                        top = railOut > 0f ? num5 - railOut : num5 - 2f,
                        bottom = railOut > 0f ? num5 + _heights[i] + PanelBottomOutset : num5 + _heights[i] + 2f
                    };
                    if (i > 0 && QuoteRailJoins(_blocks[i - 1], block, q))   // 上一块已滚出视口时,块间空白仍由本块向上补齐
                    {
                        float prevBottom = _ys[i - 1] + _heights[i - 1] - offsetDip + contentTopDip + (BlockTopOutset(_blocks[i - 1]) > 0f ? PanelBottomOutset : 2f);
                        rail.top = MathF.Min(rail.top, prevBottom);
                    }
                    if (i + 1 < _blocks.Count && QuoteRailJoins(block, _blocks[i + 1], q))
                    {
                        float nextOut = BlockTopOutset(_blocks[i + 1]);
                        rail.bottom = MathF.Max(rail.bottom, _ys[i + 1] - offsetDip + contentTopDip - (nextOut > 0f ? nextOut : 2f));
                    }
                    PFill(Rt, &rail, block.Alert is "warning" or "caution" ? BWarn : DeskRail(block) ? BHairZone : BAccent);   // U3b：desk 普通引用为浅线   // 提示块(27):警示类用暖色轨
                }
                if (block is HtmlDetails dd)
                {
                    // <details> 折叠块:摘要行(点击展开/收起) + 展开时正文
                    FillHitsBg(ent, i, num4, num5);   // details 两段搜索高亮(摘要+展开正文)
                    PDraw(Rt, new D2D1_POINT_2F { x = num4, y = num5 }, ent.L, BInk, 0);
                    FillSelForBlock(i, num4, num5);
                    if (_openDetails.Contains(i) && ent.DetailsBody != IntPtr.Zero)
                    {
                        PDraw(Rt, new D2D1_POINT_2F { x = num4 + 14f, y = num5 + ent.DetailsSummaryH }, ent.DetailsBody, BMuted, 0);
                    }
                    continue;
                }
                if (!(block is Heading heading))
                {
                    if (!(block is Para))
                    {
                        if (!(block is Quote))
                        {
                            if (!(block is ListItem))
                            {
                                if (!(block is Image))
                                {
                                    if (block is HtmlBlock hbd)
                                    {
                                        d2D1_RECT_F = default(D2D1_RECT_F);
                                        d2D1_RECT_F.left = PanelLeft(block, num4);
                                        d2D1_RECT_F.top = num5 - BlockTopOutset(block);
                                        d2D1_RECT_F.right = PanelRight();
                                        d2D1_RECT_F.bottom = num5 + _heights[i] + 12f;
                                        D2D1_ROUNDED_RECT htmlRound = new D2D1_ROUNDED_RECT { rect = d2D1_RECT_F, radiusX = 6f, radiusY = 6f };   // S91:与代码块同为 r6(原直角)
                                        PFillRound(Rt, &htmlRound, BCode);
                                        FillHitsBg(ent, i, num4, num5);
                                        FillSelForBlock(i, num4, num5);
                                        if (ent.L != IntPtr.Zero)
                                        {
                                            PDraw(Rt, new D2D1_POINT_2F { x = num4, y = num5 }, ent.L, BInk, 0);
                                        }
                                        continue;
                                    }
                                    if (!(block is CodeBlock))
                                    {
                                        if (!(block is Table table))
                                        {
                                            if (block is HRule)
                                            {
                                                Line(num4, num5, num4 + _colW - Indent(block), num5);
                                            }
                                            continue;
                                        }
                                        // Text null = scroll target only, never "copy code block" (30).
                                        if (ent.OverflowW > 0.5f)
                                        {
                                            _codeTargets.Add(new(num4, _ys[i], num4 + TableViewportW(ent),
                                                _ys[i] + _heights[i], _blockStart[i], 0, null, i));
                                        }
                                        DrawRichTable(table, ent, num4, num5, _blockStart[i]);
                                    }
                                    else
                                    {
                                        _codeTargets.Add(new(PanelLeft(block, num4), _ys[i] - BlockTopOutset(block),
                                            PanelRight(), _ys[i] + _heights[i] + 12f,
                                            _blockStart[i], ent.Text?.Length ?? 0, ((CodeBlock)block).Text, i));
                                        float chromeDy = num5 - _ys[i];
                                        if (!ent.NoCodeBg && CodeChrome(i, out var codeBox, out _, out _))
                                        {
                                            // E4:代码块 r6(§4.5);外框与复制按钮、命中区同一几何(CodeChrome)
                                            D2D1_ROUNDED_RECT rrCode = default(D2D1_ROUNDED_RECT);
                                            rrCode.rect = new D2D1_RECT_F { left = codeBox.left, top = codeBox.top + chromeDy, right = codeBox.right, bottom = codeBox.bottom + chromeDy };
                                            rrCode.radiusX = 6f;
                                            rrCode.radiusY = 6f;
                                            PFillRound(Rt, &rrCode, BCode);
                                        }
                                        DrawCodeBlockBody(ent, i, block, num4, num5);
                                        if (!ent.NoCodeBg) DrawCodeChrome(i, chromeDy);
                                    }
                                }
                                else
                                {
                                    DrawRichImage((Image)block, ent, num4, num5, _ys[i]);
                                }
                            }
                            else
                            {
                                if (ent.Marker != IntPtr.Zero)
                                {
                                    PDraw(Rt, new D2D1_POINT_2F
                                    {
                                        x = num4,
                                        y = num5 + 3f
                                    }, ent.Marker, DeskBullet((ListItem)block) ? BAccent : BInk, 0);   // U3b：desk 无序列表记号朱色
                                }
                                FillCodeBg(ent, num4 + 26f, num5);
                                FillHitsBg(ent, i, num4 + 26f, num5);   // 列表项头部的其他搜索命中(此前缺失)
                                if (ent.SubLayouts == null) FillSelForBlock(i, num4 + 26f, num5);
                                else FillSelectionOn(ent.L, _blockStart[i], ((ListItem)block).Inlines.Sum(r => r.Text.Length), num4 + 26f, num5);
                                if (ent.L != IntPtr.Zero)
                                {
                                    PDraw(Rt, new D2D1_POINT_2F
                                    {
                                        x = num4 + 26f,
                                        y = num5
                                    }, ent.L, BInk, 0);
                                }
                                if (ent.SubLayouts != null)
                                {
                                    // 项内子块:首段下方逐块绘制(引用带左竖线,代码块灰底)
                                    for (int sk = 0; sk < ent.SubLayouts.Count; sk++)
                                    {
                                        nint sl = ent.SubLayouts[sk];
                                        Block sb = ((ListItem)block).Sub?[sk];
                                        float subY = num5 + ent.SubYs[sk];
                                        float subPad = NestedPad(sb);
                                        if (ent.SubRich[sk] is Ent rich)
                                        {
                                            float rx = num4 + 26f + ent.SubXs[sk];
                                            if (sb is Table childTable)
                                            {
                                                float documentY = _ys[i] + subY - num5;
                                                _codeTargets.Add(new(rx, documentY, rx + TableViewportW(rich),
                                                    documentY + ent.SubHeights[sk], _blockStart[i] + ent.SubStarts[sk],
                                                    0, null, NestedTargetIndex(i, sk)));
                                                DrawRichTable(childTable, rich, rx, subY, _blockStart[i] + ent.SubStarts[sk]);
                                            }
                                            else if (sb is Image childImage) DrawRichImage(childImage, rich, rx, subY, _ys[i] + (subY - num5));
                                            continue;
                                        }
                                        if (sb is Quote)
                                        {
                                            d2D1_RECT_F = default(D2D1_RECT_F);
                                            d2D1_RECT_F.left = QuoteRailLeft(num4 + 26f + ent.SubXs[sk] - 13f);   // 与顶层引用同规格:2.5 DIP 吸附、距文字 13
                                            d2D1_RECT_F.top = subY - 2f;
                                            d2D1_RECT_F.right = d2D1_RECT_F.left + QuoteRailWidth;
                                            d2D1_RECT_F.bottom = subY + ent.SubHeights[sk] + 2f;
                                            D2D1_RECT_F dq = d2D1_RECT_F;
                                            PFill(Rt, &dq, BAccent);   // 与顶层引用左条同色
                                        }
                                        if (sb is CodeBlock nestedCode)
                                        {
                                            float documentY = _ys[i] + subY - num5;
                                            _codeTargets.Add(new(num4 + 26f, documentY - subPad,
                                                num4 + _colW - Indent(block) - 14f, documentY + ent.SubHeights[sk] + subPad,
                                                _blockStart[i] + ent.SubStarts[sk], ent.SubTexts[sk].Length, nestedCode.Text,
                                                NestedTargetIndex(i, sk)));
                                            D2D1_RECT_F codeRect = new D2D1_RECT_F
                                            {
                                                left = num4 + 26f,
                                                right = num4 + _colW - Indent(block) - 14f,
                                                top = subY - subPad,
                                                bottom = subY + ent.SubHeights[sk] + subPad
                                            };
                                            D2D1_ROUNDED_RECT codeRound = new D2D1_ROUNDED_RECT { rect = codeRect, radiusX = 6f, radiusY = 6f };   // 与顶层代码块同 r6
                                            PFillRound(Rt, &codeRound, BCode);
                                        }
                                        if (sl != IntPtr.Zero)
                                        {
                                            float nestedShift = NestedScrollX(ent, sk);
                                            float sx = num4 + 26f + ent.SubXs[sk] - nestedShift;
                                            // Clip to the block box so a scrolled long line cannot paint
                                            // over the list marker or past the right edge (30).
                                            bool clipNested = nestedShift > 0f || NestedOverflow(ent, sk) > 0.5f;
                                            if (clipNested)
                                            {
                                                var nestedClip = new D2D1_RECT_F
                                                {
                                                    left = num4 + 26f,
                                                    top = subY - subPad,
                                                    right = num4 + _colW - Indent(block) - 14f,
                                                    bottom = subY + ent.SubHeights[sk] + subPad
                                                };
                                                PPushClip(Rt, &nestedClip, 0);
                                            }
                                            FillRangesOn(sl, ent.SubCodes[sk], sx, subY, BCode, 2f);
                                            FillRangesOn(sl, ent.SubMarks[sk], sx, subY, BMark, 0f);
                                            FillSearchOn(sl, _blockStart[i] + ent.SubStarts[sk], ent.SubTexts[sk].Length, sx, subY);
                                            FillSelectionOn(sl, _blockStart[i] + ent.SubStarts[sk], ent.SubTexts[sk].Length, sx, subY);
                                            PDraw(Rt, new D2D1_POINT_2F { x = sx, y = subY }, sl, BInk, 0);
                                            if (clipNested)
                                            {
                                                PPopClip(Rt);
                                                DrawNestedScrollbar(ent, sk, num4 + 26f,
                                                    num4 + _colW - Indent(block) - 14f, subY + ent.SubHeights[sk] + subPad);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            d2D1_RECT_F = default(D2D1_RECT_F);
                            d2D1_RECT_F.left = QuoteRailLeft(num4 - 13f);
                            d2D1_RECT_F.top = num5 - 2f;
                            d2D1_RECT_F.right = QuoteRailLeft(num4 - 13f) + QuoteRailWidth;
                            d2D1_RECT_F.bottom = num5 + _heights[i] + 2f;
                            D2D1_RECT_F d2D1_RECT_F6 = d2D1_RECT_F;
                            if (block.QuoteDepth == 0) PFill(Rt, &d2D1_RECT_F6, BAccent); // Legacy manually constructed Quote.
                            FillMarkBg(ent, num4, num5);
                            FillHitsBg(ent, i, num4, num5);
                            FillSelForBlock(i, num4, num5);
                            if (ent.L != IntPtr.Zero)
                            {
                                PDraw(Rt, new D2D1_POINT_2F
                                {
                                    x = num4,
                                    y = num5
                                }, ent.L, DeskQuote(block) ? BInk2 : BInk, 0);
                            }
                        }
                    }
                    else
                    {
                        FillCodeBg(ent, num4, num5);
                        FillMarkBg(ent, num4, num5);
                        FillHitsBg(ent, i, num4, num5);
                        FillSelForBlock(i, num4, num5);
                        if (ent.KpLayouts != null)
                        {
                            // KP 长段落:逐行绘制(每行一个布局,行高来自 KpHeights)
                            // I6:JUST 后悬挂标点/病态超宽行可越列 ≤8px,clip 到列宽+10 兜底(NO_WRAP 后 DWrite 不再二次折行)
                            D2D1_RECT_F kpClip = new D2D1_RECT_F { left = num4 - 2f, top = num5 - 2f, right = num4 + _colW - Indent(block) + 10f, bottom = num5 + ent.H + 2f };
                            PPushClip(Rt, &kpClip, 0);
                            float ky = num5;
                            for (int k = 0; k < ent.KpLayouts.Count; k++)
                            {
                                PDraw(Rt, new D2D1_POINT_2F { x = num4, y = ky }, ent.KpLayouts[k], BInk, 0);
                                ky += ent.KpHeights[k];
                            }
                            PPopClip(Rt);
                        }
                        else if (ent.L != IntPtr.Zero)
                        {
                            PDraw(Rt, new D2D1_POINT_2F
                            {
                                x = num4,
                                y = num5
                            }, ent.L, BInk, 0);
                        }
                    }
                }
                else
                {
                    FillMarkBg(ent, num4, num5);
                    FillHitsBg(ent, i, num4, num5);
                    FillSelForBlock(i, num4, num5);
                    if (ent.L != IntPtr.Zero)
                    {
                        PDraw(Rt, new D2D1_POINT_2F
                        {
                            x = num4,
                            y = num5
                        }, ent.L, BInk, 0);
                    }
                }
            }
            if (TocVisible)
            {
                float tocTop = contentTopDip + overlayHeight;
                // TOC 侧栏:ScrollSpy 高亮当前标题,点击跳转(Program 侧处理)
                D2D1_RECT_F tocRect = new D2D1_RECT_F { left = 0f, top = tocTop, right = TocWidth, bottom = contentTopDip + viewHDip };
                PPushClip(Rt, &tocRect, 0);
                tocClip = true;
                PFill(Rt, &tocRect, BCanvas);
                // §4.3:不透明画布即可与正文分开,不画分隔线;仅高对比下用系统色 1 DIP 边界
                if (RenderResources.HighContrast) FillLine(TocWidth - 0.5f, tocTop, TocWidth - 0.5f, contentTopDip + viewHDip, BHair, 1f);
                int curToc = -1;
                for (int ti = 0; ti < TocEntries.Count; ti++)
                {
                    if (TocEntries[ti].y <= offsetDip + 120f) curToc = ti;
                }
                if (curToc >= 0)
                {
                    // 自动跟随:当前章节滚出 TOC 视野时把 TOC 滚过去;用户手动滚 TOC 后暂停,待文档再动才恢复
                    bool docMoved = MathF.Abs(offsetDip - _followAnchorY) > 0.5f;
                    _followAnchorY = offsetDip;
                    if (docMoved) TocUserScrolled = false;
                    if (!TocUserScrolled)
                    {
                        float bandBot = contentTopDip + viewHDip;
                        float visH = bandBot - (tocTop + 8f);
                        float topForCur = curToc * 26f;                       // curToc 贴顶所需的 TocScroll
                        float minForCur = topForCur + 26f - visH;              // curToc 露出底缘所需的最小 TocScroll
                        float lo = MathF.Max(0f, minForCur), hi = MathF.Max(lo, topForCur);   // 窗口极矮时 minForCur>topForCur,防 Clamp 参数倒挂
                        float want = Math.Min(MathF.Max(TocScroll, lo), hi);
                        float maxToc = MathF.Max(0f, TocEntries.Count * 26f + 16f - (bandBot - tocTop));   // 与滚轮上限同口径
                        want = Math.Clamp(want, 0f, maxToc);
                        TocScroll = MathF.Floor(want * Scale) / Scale;         // 落像素网格,与滚轮/触底钳制同一机制,防 ±1px 抖
                    }
                }
                int first = Math.Max(0, (int)(TocScroll / 26f) - 1);
                int last = Math.Min(TocEntries.Count, first + (int)(viewHDip / 26f) + 3);
                float ty = tocTop + 8f + first * 26f - TocScroll;
                for (int ti = first; ti < last; ti++)
                {
                    var (bi, lv, txt, ey) = TocEntries[ti];
                    if (ti == curToc)
                    {
                        // I8 重样式:去整行灰底块(现界面最吵元素),激活=ink 文字+左缘 2px accent 条(一状态一通道)
                        FillLine(5f, ty, 5f, ty + 24f, BAccent, 2f);
                    }
                    if (!_tocLayouts.TryGetValue(ti, out nint el))
                    {
                        float tx0 = 10f + (lv - 1) * 12f;
                        el = Dw.TrimLayout(txt, FmtUi, TocWidth - tx0 - 8f);   // I8 省略号:超宽截断带 …(此前硬截断无提示,评审点名的真缺陷)
                        nint whole = Dw.MkLayout(txt, FmtUi, 10000f);
                        if (whole != 0) { if (Dw.LayoutW(whole) > TocWidth - tx0 - 8f) _tocTrimmed.Add(ti); Marshal.Release(whole); }
                        _tocLayouts[ti] = el;
                    }
                    Dw.Bind(el);
                    DWRITE_TEXT_METRICS em = default(DWRITE_TEXT_METRICS);
                    Dw.GetMetrics(el, &em);
                    PDraw(Rt, new D2D1_POINT_2F { x = 10f + (lv - 1) * 12f, y = ty + (24f - em.height) / 2f }, el, ti == curToc ? BInk : BMuted, 0);
                    ty += 26f;
                }
                if (TocEntries.Count == 0)
                {
                    // 无标题文档:说明为什么是空的,而不是一片空白(布局与条目同缓存,键 −1,随 Relayout 释放)
                    if (!_tocLayouts.TryGetValue(-1, out nint none)) _tocLayouts[-1] = none = Dw.TrimLayout("本文没有标题", FmtUi, TocWidth - 18f);
                    if (none != 0)
                    {
                        Dw.Bind(none);
                        DWRITE_TEXT_METRICS nm = default(DWRITE_TEXT_METRICS);
                        Dw.GetMetrics(none, &nm);
                        PDraw(Rt, new D2D1_POINT_2F { x = 10f, y = tocTop + 8f + (24f - nm.height) / 2f }, none, BMuted, 0);
                    }
                }
                PPopClip(Rt);
                tocClip = false;
            }
            else if (RulerVisible) PaintRuler(contentTopDip + overlayHeight, contentTopDip + viewHDip, offsetDip);
            // 顶缘 1 DIP Hairline 是顶栏下缘的弱分隔(§4.1);阅读进度在视口底缘(§4.5,E1)。
            PaintInkCircle();   // U7：画在正文之上、暖光与页脚之下
            float px = Scale > 0f ? Scale : 1f, viewWDip = (cr.R - cr.L) / px;
            if (!WrapCodeForPaper) PaintLampGlow(0f, contentTopDip, viewWDip, contentTopDip + viewHDip);   // U2b：灯下暖光与暗角
            D2D1_RECT_F track = new D2D1_RECT_F { left = 0f, top = contentTopDip, right = viewWDip, bottom = contentTopDip + 1f };
            if (!zenQuiet && !RenderResources.Reading.IsDesk) PFill(Rt, &track, BHair);   // 阶段 B Zen:工具栏隐藏时分隔线随之隐藏；desk 顶栏与纸面靠色差分界、当前标签与纸面连成一片（U6a）
            float spanDip = TotalH - viewHDip;
            if (spanDip > 4f && offsetDip > 0.5f && !WrapCodeForPaper && !RenderResources.Reading.IsDesk)   // desk:进度在页脚与书签绳
            {
                // 1 物理像素、落整像素网格:底缘最后一行像素,从左向右按滚动比例延伸;不占布局、无底轨。
                float progRatio = Math.Clamp(offsetDip / spanDip, 0f, 1f);
                float edge = MathF.Round((contentTopDip + viewHDip) * px) / px;
                D2D1_RECT_F prog = new D2D1_RECT_F { left = 0f, top = edge - 1f / px, right = MathF.Round(viewWDip * progRatio * px) / px, bottom = edge };
                PFill(Rt, &prog, BAccent);
            }
            if (returnHint != null && returnHint.Visible && !WrapCodeForPaper) DrawReturnHint(returnHint, contentTopDip, viewHDip);
        }
        finally
        {
            // Paint 中途抛异常(mermaid/公式/布局失败等)时裁剪栈与 EndDraw 也必须收尾,
            // 否则残留裁剪区会污染后续所有帧直到重建渲染目标。
            // batch 模式下 EndDraw 由宿主 OnPaint 的 EndBatch 统一收(裁剪栈仍要在这里配对弹出)。
            if (tocClip) PPopClip(Rt);
            PPopClip(Rt);
            RestoreInkedLinks();
            if (!batch) PEnd(Rt, &num12, &num13);
        }
    }

    private void Line(float x1, float y1, float x2, float y2)
    {
        FillLine(x1, y1, x2, y2, BHair, 1f);
    }

    // Width of the visible text area inside a code block, i.e. exactly what the paint
    // path lays text out in: the block column, from the text left edge to the column's right
    // edge (the panel bleeds PanelBleed beyond both). Overflow and scrolling are
    // measured against this one definition so the longest line's tail is reachable and
    // no further (30).
    internal static float CodeViewportW(float blockWidth) => MathF.Max(40f, blockWidth);
    // 列表项内的代码子块底板在项内、不外扩,文字右侧仍留 20 DIP 内边距。
    internal static float NestedViewportW(float blockWidth) => MathF.Max(40f, blockWidth - 20f);

    // Visible width of a table, from the same base the column widths were measured against
    // (Build clamps to 120 before laying columns out). Draw, hit testing and the scroll
    // target must all ask this one function: the first attempt at sideways code blocks had
    // three slightly different width definitions and the clipping silently disagreed with
    // the overflow amount by 20 DIP (30).
    // Note this is NOT CodeViewportW: a code panel bleeds past the column and its text stops at
    // the column edge (nested code keeps 20 DIP of inner padding), while a
    // table is drawn edge to edge across RichWidth. Borrowing the old code formula left every
    // table 20 DIP "overflowing" — including squeezed ones on paper, which would then clip
    // a column away and print a scrollbar.
    private static float TableViewportW(Ent e) => MathF.Max(120f, e.RichWidth);

    // The inside of a code block: background highlights, selection and glyphs, all shifted
    // together by the block's sideways scroll and clipped to the block. Extracted from the
    // paint loop, which reaches fourteen levels of nesting here — the three defects found
    // while building sideways scrolling all came from editing one arm of this branch and
    // missing its twin (30).
    private unsafe void DrawCodeBlockBody(Ent ent, int i, Block block, float x, float y)
    {
        float codeRight = PanelRight();
        bool clipCode = ent.OverflowW > 0.5f;
        if (clipCode)
        {
            D2D1_RECT_F clip = new D2D1_RECT_F { left = PanelLeft(block, x), top = y - BlockTopOutset(block), right = codeRight, bottom = y + _heights[i] + 12f };
            PPushClip(Rt, &clip, 0);
        }
        // One offset for everything inside: if the glyphs move but the selection does not,
        // the highlight silently drifts away from the text it is meant to mark.
        float codeX = x - ent.ScrollX;
        FillHitsBg(ent, i, codeX, y);
        FillSelForBlock(i, codeX, y);
        if (ent.Mermaid != null)
        {
            var marks = MermaidMarks(ent, i);
            DrawMermaidGraph(ent.Mermaid, codeX + ent.MermaidInset, y, ent.MermaidLayouts ??= new Dictionary<string, (nint, float, float)>(),
                marks == null ? null : (lay, tx, ty, text) => MarkMermaidLabel(lay, tx, ty, text, marks));
        }
        else if (ent.MathTree != null)
        {
            MathRender.Draw(ent.MathTree, x + MathF.Max(0f, (_colW - ent.MathTree.W) / 2f), y + 8f, BInk, BComment);
        }
        else if (ent.HL != null)
        {
            // 语法高亮:逐行分段绘制,等宽定位
            float ly = y;
            foreach (var segs in ent.HL)
            {
                float lx = codeX;
                foreach (var (lay, w, kind) in segs)
                {
                    nint brush = kind switch
                    {
                        CodeHighlight.KKeyword => BKeyword,
                        CodeHighlight.KString => BString,
                        CodeHighlight.KComment => BComment,
                        CodeHighlight.KNumber => BNumber,
                        _ => BInk
                    };
                    PDraw(Rt, new D2D1_POINT_2F { x = lx, y = ly }, lay, brush, 0);
                    lx += w;
                }
                ly += ent.HLLineH;
            }
        }
        else if (ent.L != IntPtr.Zero)
        {
            PDraw(Rt, new D2D1_POINT_2F { x = codeX, y = y }, ent.L, BInk, 0);
        }
        if (clipCode) PPopClip(Rt);
        DrawCodeScrollbar(ent, PanelLeft(block, x), codeRight, y + _heights[i] + 12f, CodeViewportW(_colW - Indent(block)));
    }

    // A thin indicator along the bottom edge of an overflowing code block, showing how
    // much is off-screen and where the view currently sits. Only drawn when the block
    // actually overflows, so ordinary code is unchanged (30).
    private void DrawCodeScrollbar(Ent e, float left, float right, float bottom, float viewportW, float trackY = float.NaN)
    {
        if (e.OverflowW <= 0.5f) return;
        float visible = right - left;
        if (visible <= 20f || viewportW <= 0f) return;
        float total = viewportW + e.OverflowW;
        float thumbW = MathF.Max(24f, visible * (viewportW / total));
        float travel = visible - thumbW;
        float frac = e.OverflowW <= 0f ? 0f : Math.Clamp(e.ScrollX / e.OverflowW, 0f, 1f);
        float x = left + travel * frac;
        float y = float.IsNaN(trackY) ? bottom - 4f : trackY;   // 表格传入规则线下方的位置
        FillLine(left, y, right, y, BHair, 2f);              // track
        FillLine(x, y, x + thumbW, y, BMuted, 2f);           // thumb:A4 用 Muted(原借语法注释色)
    }

    // Scroll the code block under the cursor sideways. Returns true when a block took
    // the gesture, so the host can suppress the usual vertical scroll (30).
    public bool ScrollCodeAt(float x, float y, float delta)
    {
        int block = _codeTargets.BlockAtPoint(x, y);
        if (block == -1 || block >= _blocks.Count) return false;
        return ScrollCodeBlock(block, delta);
    }

    // Keyboard path: no pointer to aim with, so scroll the code block nearest the middle
    // of the visible range. Returns false when nothing on screen can scroll, letting the
    // caller leave the key to its normal meaning (30).
    public bool ScrollCodeVisible(float viewTop, float viewBottom, float delta)
    {
        int block = _codeTargets.BlockNearestCenter(viewTop, viewBottom);
        if (block == -1 || block >= _blocks.Count) return false;
        return ScrollCodeBlock(block, delta);
    }

    private bool ScrollCodeBlock(int block, float delta)
    {
        // Negative indices address a code block nested in a list item (see NestedTargetIndex).
        if (block <= NestedTargetBase) return ScrollNestedBlock(block, delta);
        return ScrollEntry(GetEnt(block), delta);
    }

    private static bool ScrollEntry(Ent e, float delta)
    {
        if (e == null || e.OverflowW <= 0.5f) return false;
        float before = e.ScrollX;
        e.ScrollX = Math.Clamp(e.ScrollX + delta, 0f, e.OverflowW);
        return MathF.Abs(e.ScrollX - before) > 0.01f;
    }

    // A code block inside a list item has no Ent and therefore no block index of its own.
    // Pack (parent, sub) into one negative int so it can travel through the same scroll
    // target list as a top-level block. -1 stays reserved for "no target" (30).
    private const int NestedTargetBase = -16;
    private const int NestedTargetStride = 4096;
    private static int NestedTargetIndex(int parent, int sub) =>
        NestedTargetBase - (parent * NestedTargetStride + sub);

    private static (int parent, int sub) NestedUnpack(int packed)
    {
        int flat = NestedTargetBase - packed;
        return (flat / NestedTargetStride, flat % NestedTargetStride);
    }

    // Exposed for self-tests: the scroll target list is only filled while painting, so a
    // windowless test cannot reach the packing through the normal path (30).
    internal static int NestedTargetIndexForTest(int parent, int sub) => NestedTargetIndex(parent, sub);
    internal static (int parent, int sub) NestedUnpackForTest(int packed) => NestedUnpack(packed);

    private bool ScrollNestedBlock(int packed, float delta)
    {
        var (parent, sub) = NestedUnpack(packed);
        if (parent < 0 || parent >= _blocks.Count) return false;
        var e = GetEnt(parent);
        if (e?.SubOverflowW == null || sub < 0 || sub >= e.SubOverflowW.Count) return false;
        if (e.SubRich[sub] is Ent rich) return ScrollEntry(rich, delta);
        if (e.SubOverflowW[sub] <= 0.5f) return false;
        float before = e.SubScrollX[sub];
        e.SubScrollX[sub] = Math.Clamp(before + delta, 0f, e.SubOverflowW[sub]);
        return MathF.Abs(e.SubScrollX[sub] - before) > 0.01f;
    }

    // Old Ents built before these lists existed simply do not scroll.
    private static float NestedOverflow(Ent e, int sub) =>
        e.SubOverflowW != null && sub < e.SubOverflowW.Count ? e.SubOverflowW[sub] : 0f;

    private static float NestedScrollX(Ent e, int sub) =>
        e.SubScrollX != null && sub < e.SubScrollX.Count && NestedOverflow(e, sub) > 0.5f ? e.SubScrollX[sub] : 0f;

    private void DrawNestedScrollbar(Ent e, int sub, float left, float right, float bottom)
    {
        float overflow = NestedOverflow(e, sub);
        if (overflow <= 0.5f) return;
        float visible = right - left;
        if (visible <= 20f) return;
        float total = visible + overflow;
        float thumbW = MathF.Max(24f, visible * (visible / total));
        float travel = visible - thumbW;
        float frac = Math.Clamp(NestedScrollX(e, sub) / overflow, 0f, 1f);
        float x = left + travel * frac;
        float y = bottom - 4f;
        FillLine(left, y, right, y, BHair, 2f);
        FillLine(x, y, x + thumbW, y, BComment, 2f);
    }


    private unsafe void FillCodeBg(Ent e, float x, float y)
    {
        if (e.Code.Count == 0)
        {
            return;
        }
        if (e.KpLayouts != null)
        {
            // KP 长段落:每行用本行的行内码范围(行内坐标)
            for (int k = 0; k < e.KpLayouts.Count; k++)
            {
                if (e.KpCodes[k].Count > 0)
                {
                    FillRangesOn(e.KpLayouts[k], e.KpCodes[k], x, y + e.KpTop(k), BCode, 2f);
                }
            }
            return;
        }
        if (e.L != IntPtr.Zero)
        {
            FillRangesOn(e.L, e.Code, x, y, BCode, 2f);
        }
    }

    // ==高亮== 背景(半透明色块,需 ent.L;KP 长段落暂不支持)
    private unsafe void FillMarkBg(Ent e, float x, float y)
    {
        if (e.MarkRanges.Count == 0 || e.L == IntPtr.Zero)
        {
            return;
        }
        FillRangesOn(e.L, e.MarkRanges, x, y, BMark, 0f);
    }

    private unsafe void FillRangesOn(nint layout, List<(int s, int l)> ranges, float x, float y, nint brush, float pad)
    {
        foreach (var item3 in ranges)
        {
            FillRangeOn(layout, item3.s, item3.l, x, y, brush, pad);
        }
    }

    private unsafe void FillRangeOn(nint layout, int s, int l, float x, float y, nint brush, float pad)
    {
        if (layout == IntPtr.Zero || l <= 0) return;
        if (brush == BHit || brush == BMark) InkLinksIn(layout, s, l);   // E7
        delegate* unmanaged[Stdcall]<nint, uint, uint, float, float, DWRITE_HIT_TEST_METRICS*, uint, uint*, int> fnPtr2 = (delegate* unmanaged[Stdcall]<nint, uint, uint, float, float, DWRITE_HIT_TEST_METRICS*, uint, uint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)layout) + (nint)66 * (nint)sizeof(nint)));
        DWRITE_HIT_TEST_METRICS* ptr = stackalloc DWRITE_HIT_TEST_METRICS[64];
        uint num = 0u;
        if (fnPtr2(layout, (uint)s, (uint)l, x, y, ptr, 64u, &num) >= 0)
        {
            for (uint num2 = 0u; num2 < num && num2 < 64; num2++)
            {
                D2D1_RECT_F d2D1_RECT_F = default(D2D1_RECT_F);
                d2D1_RECT_F.left = ptr[num2].left - pad;
                d2D1_RECT_F.top = ptr[num2].top;
                d2D1_RECT_F.right = ptr[num2].left + ptr[num2].width + pad;
                d2D1_RECT_F.bottom = ptr[num2].top + ptr[num2].height;
                D2D1_RECT_F d2D1_RECT_F2 = d2D1_RECT_F;
                PFill(Rt, &d2D1_RECT_F2, brush);
            }
        }
    }

    private static void DrawDiagramEdge(float ax, float ay, float bx, float by, nint brush, float weight, bool dashed)
    {
        if (!dashed) { StrokeLine(ax, ay, bx, by, brush, weight); return; }
        float dx = bx - ax, dy = by - ay;
        int parts = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy) / 10f), 1, 256);
        for (int i = 0; i < parts; i++)
        {
            float start = (float)i / parts, end = (i + 0.6f) / parts;
            StrokeLine(ax + dx * start, ay + dy * start, ax + dx * end, ay + dy * end, brush, weight);
        }
    }

    // Mermaid 流程图子集绘制:节点形状 + 边 + 箭头 + 标签
    public unsafe static void DrawMermaidGraph(MermaidGraph g, float ox, float oy, Dictionary<string, (nint lay, float w, float h)> cache, Action<nint, float, float, string> underText = null)
    {
        // S93:淡墨只用于普通主题;高对比度下线与框回到文字色(BtnShadow 在系统对比度主题里不保证与底色有对比)
        nint lineBrush = RenderResources.HighContrast ? BInk : BMuted, frameBrush = RenderResources.HighContrast ? BInk : BControlBorder;
        foreach (var e in g.Edges)
        {
            nint eb = lineBrush;   // S88:结构线退后(原墨色;虚线原注释绿),虚线靠线型区分
            float weight = e.Thick ? 2.4f : 1.2f;
            var path = MermaidLite.EdgePath(g, e);
            for (int k = 0; k < path.Count; k++) path[k] = (path[k].x + ox, path[k].y + oy);
            // Arrowhead follows the last segment; stop the shaft short so it does not poke through the tip.
            var (tipX, tipY) = path[^1];
            var (px, py) = path[^2];
            float len = MathF.Sqrt((tipX - px) * (tipX - px) + (tipY - py) * (tipY - py));
            float ux = len > 0.01f ? (tipX - px) / len : 0f, uy = len > 0.01f ? (tipY - py) / len : 1f;
            if (e.Arrow && len > 8f) path[^1] = (tipX - ux * 8f, tipY - uy * 8f);
            for (int k = 0; k + 1 < path.Count; k++)
                DrawDiagramEdge(path[k].x, path[k].y, path[k + 1].x, path[k + 1].y, eb, weight, e.Dashed);
            if (e.Arrow)
            {
                float bx = tipX - ux * 8f, by = tipY - uy * 8f;
                StrokeLine(bx - uy * 4f, by + ux * 4f, tipX, tipY, eb, weight);
                StrokeLine(bx + uy * 4f, by - ux * 4f, tipX, tipY, eb, weight);
            }
            if (e.Label != null && e.Label.Length > 0)
            {
                string key = "e:" + e.Label;
                if (!cache.TryGetValue(key, out var cl))
                {
                    nint ll = Dw.MkLayout(e.Label, FmtUi, 10000f);
                    Dw.Bind(ll);
                    DWRITE_TEXT_METRICS lm = default(DWRITE_TEXT_METRICS);
                    Dw.GetMetrics(ll, &lm);
                    cl = (ll, lm.width, lm.height);
                    cache[key] = cl;
                }
                var (mx, my, beside) = MermaidLite.LabelAt(path);   // S82:竖线旁的标签放右侧,不压线
                var (lx, ly) = MermaidLite.LabelOrigin(mx, my, beside, MermaidLite.LabelOutside(g, e), cl.w, cl.h);   // S85:来回一对边的标签各放外侧
                var at = new D2D1_POINT_2F { x = lx, y = ly };
                underText?.Invoke(cl.lay, lx, ly, e.Label);   // S92:查找命中底色画在字下
                PDraw(Rt, at, cl.lay, BMuted, 0);   // S88:原注释绿,与配色体系无关(高对比度下 Muted 即 GrayText,属文字色)
            }
        }
        foreach (var nd in g.Nodes)
        {
            float nx = ox + nd.X, ny = oy + nd.Y, nw = nd.W, nh = nd.H;
            string nk = "n:" + nd.Text + ":" + (nw - 10f);
            if (!cache.TryGetValue(nk, out var cn))
            {
                nint el = Dw.MkLayout(nd.Shown ?? nd.Text, FmtUi, nw - 10f);
                Dw.Bind(el);
                Dw.SetTextAlignment(el, 2 /*CENTER*/);   // 折成两行的标签逐行居中(S79)
                DWRITE_TEXT_METRICS tm3 = default(DWRITE_TEXT_METRICS);
                Dw.GetMetrics(el, &tm3);
                cn = (el, tm3.width, tm3.height);
                cache[nk] = cn;
            }
            float tm3w = cn.w, tm3h = cn.h;
            if (nd.Shape == 2)
            {
                // S88 续:逐行横线填 Canvas,与方框一致;随后的描边盖住锯齿边(没有路径几何接口,不为一个菱形引入)
                float half = nh / 2f;
                for (float fy = 1f; fy < nh - 1f; fy += 1f)
                {
                    float hw = nw / 2f * (1f - MathF.Abs(fy - half) / half);
                    if (hw > 1f) StrokeLine(nx + nw / 2f - hw, ny + fy, nx + nw / 2f + hw, ny + fy, BCanvas, 1.2f);
                }
                StrokeLine(nx + nw / 2f, ny, nx + nw, ny + nh / 2f, frameBrush, 1f);
                StrokeLine(nx + nw, ny + nh / 2f, nx + nw / 2f, ny + nh, frameBrush, 1f);
                StrokeLine(nx + nw / 2f, ny + nh, nx, ny + nh / 2f, frameBrush, 1f);
                StrokeLine(nx, ny + nh / 2f, nx + nw / 2f, ny, frameBrush, 1f);
            }
            else if (nd.Shape == 1)
            {
                D2D1_ROUNDED_RECT rr = new D2D1_ROUNDED_RECT
                {
                    rect = new D2D1_RECT_F { left = nx, top = ny, right = nx + nw, bottom = ny + nh },
                    radiusX = 12f,
                    radiusY = nh / 2f
                };
                PFillRound(Rt, &rr, BCanvas);
                PStrokeRound(Rt, &rr, frameBrush, 1f, 0);
            }
            else
            {
                D2D1_ROUNDED_RECT rr = new D2D1_ROUNDED_RECT   // S88:4 DIP 圆角,描边 ControlBorder(原直角墨色)
                {
                    rect = new D2D1_RECT_F { left = nx, top = ny, right = nx + nw, bottom = ny + nh },
                    radiusX = 4f,
                    radiusY = 4f
                };
                PFillRound(Rt, &rr, BCanvas);
                PStrokeRound(Rt, &rr, frameBrush, 1f, 0);
            }
            underText?.Invoke(cn.lay, nx + 5f, ny + (nh - tm3h) / 2f, nd.Shown ?? nd.Text);
            PDraw(Rt, new D2D1_POINT_2F { x = nx + 5f, y = ny + (nh - tm3h) / 2f }, cn.lay, BInk, 0);
        }
    }

    // S92:流程图里的查找命中。块内命中的原文片段若出现在节点/边标签里,就在该标签文字下画 Hit 底色;
    // 只落在语法上的命中(如 "-->"、节点 id)没有可见文字,不画。
    private List<string> MermaidMarks(Ent e, int i)
    {
        if (SearchHits.Count == 0 || e.Text == null) return null;
        int b0 = _blockStart[i], len = e.Text.Length;
        List<string> marks = null;
        foreach (var (hs, hl) in SearchHits)
        {
            if (hl <= 0 || hs < b0 || hs + hl > b0 + len) continue;
            string s = e.Text.Substring(hs - b0, hl);
            if (!(marks ??= new()).Contains(s)) marks.Add(s);
        }
        return marks;
    }

    private unsafe void MarkMermaidLabel(nint lay, float x, float y, string text, List<string> marks)
    {
        foreach (var s in marks)
            for (int p = text.IndexOf(s, StringComparison.OrdinalIgnoreCase); p >= 0; p = text.IndexOf(s, p + s.Length, StringComparison.OrdinalIgnoreCase))
                FillRangeOn(lay, p, s.Length, x, y, BHit, 0f);
    }

    private unsafe void FillSelectionOn(nint layout, int start, int length, float x, float y)
    {
        if (!HasSel || layout == IntPtr.Zero) return;
        int a = Math.Clamp(Math.Min(_selStart, _selEnd) - start, 0, length);
        int b = Math.Clamp(Math.Max(_selStart, _selEnd) - start, 0, length);
        if (b > a) FillSelRectsAt(layout, x, y, a, b - a);
    }

    private unsafe void FillSearchOn(nint layout, int start, int length, float x, float y)
    {
        if (layout == IntPtr.Zero) return;
        foreach (var (hs, hl) in SearchHits)
        {
            int a = Math.Clamp(hs - start, 0, length), b = Math.Clamp(hs + hl - start, 0, length);
            if (b > a) FillRangeOn(layout, a, b - a, x, y, BHit, 0f);
        }
    }

    // 搜索全部命中背景(橙色);当前命中由选区蓝色呈现
    private unsafe void FillHitsBg(Ent e, int i, float x, float y)
    {
        if (SearchHits.Count == 0)
        {
            return;
        }
        int b0 = _blockStart[i];
        int blen = (e.Text ?? "").Length;
        int bEnd = b0 + blen;
        if (e.SubLayouts != null)
        {
            FillSearchOn(e.L, b0, ((ListItem)_blocks[i]).Inlines.Sum(r => r.Text.Length), x, y);
            return;
        }
        if (e.KpLayouts != null)
        {
            for (int k = 0; k < e.KpLayouts.Count; k++)
            {
                int ls = e.KpStarts[k];
                int ll = e.KpLines[k].Length;
                foreach (var (hs, hl2) in SearchHits)
                {
                    int a = Math.Max(hs - b0 - ls, 0), b = Math.Min(hs + hl2 - b0 - ls, ll);   // hs 是全局偏移,须先减块起点(与非 KP 分支同)
                    if (b > a) FillRangeOn(e.KpLayouts[k], a, b - a, x, y + e.KpTop(k), BHit, 0f);
                }
            }
            return;
        }
        if (_blocks[i] is HtmlDetails)
        {
            // details 两段命中背景:摘要段(+2 前缀)+ 展开正文段(x+14,域起点 sLen+1)
            string et = e.Text ?? "";
            int sLen = et.IndexOf('\n'); if (sLen < 0) sLen = et.Length;
            int baseOff = sLen + 1;
            foreach (var (hs, hl2) in SearchHits)
            {
                int a0 = hs - b0, a1 = hs + hl2 - b0;
                if (a0 >= et.Length) break;
                int ha = Math.Clamp(a0, 0, sLen), hb = Math.Clamp(a1, 0, sLen);
                if (hb > ha) FillRangeOn(e.L, ha + 2, hb - ha, x, y, BHit, 0f);
                if (e.DetailsBody != IntPtr.Zero)
                {
                    int ba = Math.Clamp(a0 - baseOff, 0, et.Length - baseOff);
                    int bb = Math.Clamp(a1 - baseOff, 0, et.Length - baseOff);
                    if (bb > ba) FillRangeOn(e.DetailsBody, ba, bb - ba, x + 14f, y + e.DetailsSummaryH, BHit, 0f);
                }
            }
            return;
        }
        if (e.L == IntPtr.Zero)
        {
            return;
        }
        foreach (var (hs, hl2) in SearchHits)
        {
            if (hs >= bEnd) break;
            int a = Math.Max(hs - b0, 0), b = Math.Min(hs + hl2 - b0, blen);
            if (b > a) FillRangeOn(e.L, a, b - a, x, y, BHit, 0f);
        }
    }
    public void SelBegin(float mx, float myDoc)
    {
        _selStart = (_selEnd = PosAt(mx, myDoc));
    }

    public void SelExtend(float mx, float myDoc)
    {
        _selEnd = PosAt(mx, myDoc);
    }

    public void SelAll()
    {
        _selStart = 0;
        _selEnd = _totalChars;
    }

    public void SelWord(float mx, float myDoc)
    {
        int num = PosAt(mx, myDoc);
        if (num < 0)
        {
            return;
        }
        int num2 = FindBlockByOffset(num);
        string text = GetEnt(num2).Text ?? "";
        int num3 = Math.Clamp(num - _blockStart[num2], 0, text.Length);
        if (num3 >= text.Length)
        {
            _selStart = (_selEnd = _blockStart[num2] + text.Length);
            return;
        }
        int num4 = num3;
        int i = num3;
        while (num4 > 0 && !char.IsWhiteSpace(text[num4 - 1]))
        {
            num4--;
        }
        for (; i < text.Length && !char.IsWhiteSpace(text[i]); i++)
        {
        }
        if (i == num4)
        {
            i = Math.Min(text.Length, num4 + 1);
        }
        _selStart = _blockStart[num2] + num4;
        _selEnd = _blockStart[num2] + i;
    }

    public void SelClear()
    {
        _selStart = -1;
        _selEnd = -1;
    }

    public (int block, int pos) PosAtText(float mx, float myDoc)
    {
        int num = PosAt(mx, myDoc);
        if (num < 0)
        {
            return (block: -1, pos: 0);
        }
        int num2 = FindBlockByOffset(num);
        return (block: num2, pos: num - _blockStart[num2]);
    }

    // 光标判定: 点是否落在文本块内(块间空隙/内容下方空白/文本列左侧 → false → 箭头)
    public bool HitBlock(float mx, float myDoc)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            Block block = _blocks[i];
            if (myDoc >= _ys[i] && myDoc <= _ys[i] + _heights[i])
            {
                float left = _colX + Indent(block) + ((block is ListItem) ? 26f : 0f);
                if (mx >= left)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public bool TryAnchorJump(string anchor, out float y)
    {
        y = 0f;
        if (anchor.Length == 0) return true;
        if (!_anchors.TryFind(anchor, out var target)) return false;
        y = _ys[target.Block];
        if (target.Sub >= 0)
        {
            Ent entry = GetEnt(target.Block);
            y += entry.SubYs[target.Sub];
        }
        return true;
    }

    public void ApproveImage(string source, string localPath)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _images.Approve(source, localPath);
        _images.Load(source);
        Relayout();
    }

    public string ImageMessage(string source) => _images.Message(source);

    public (float w, float h) GetImageDims(string src)
    {
        var image = _images.Get(src);
        return image.Bitmap != 0 ? (image.Width, image.Height) : (1f, 1f);
    }

    public bool TryImageHit(float mx, float myDoc, out string src)
    {
        src = "";
        foreach (var r in ImageRects)
        {
            if (mx >= r.x && mx <= r.x + r.w && myDoc >= r.y && myDoc <= r.y + r.h)
            {
                src = r.src;
                return true;
            }
        }
        return false;
    }

    // lightbox 用:返回已缓存位图句柄(未加载返回 Zero)
    public nint GetImageBmp(string src)
    {
        return _images.Get(src).Bitmap;
    }

    public bool TryRefBackY(string id, out float y)
    {
        y = 0f;
        // S90:引用显示为 "^" + 原文标签(大小写可能与折叠后的 id 不同),原先找 "[^id]" 永远落空。
        // 取第一处整词匹配(^1 不命中 ^12;边界只看 ASCII,引用后常紧跟汉字);脚注节在文末,第一处即正文引用。
        var m = System.Text.RegularExpressions.Regex.Match(SearchText(), @"\^" + System.Text.RegularExpressions.Regex.Escape(id) + @"(?![0-9A-Za-z_-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        y = YAtOffset(m.Index);
        return true;
    }

    public bool TryFootnoteY(string id, out float y)
    {
        y = 0f;
        string prefix = "^" + id + ".";
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is Para p && p.Inlines.Count > 0 && p.Inlines[0].Text.StartsWith(prefix))
            {
                y = _ys[i];
                return true;
            }
        }
        return false;
    }

    // 点击 <details> 摘要行切换展开/收起(高度变化需要重排)
    public void ToggleDetailsAt(float mx, float myDoc)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is HtmlDetails && myDoc >= _ys[i] - 2f && myDoc <= _ys[i] + DetailsBandH(i) && mx >= _colX && mx <= _colX + _colW)
            {
                if (!_openDetails.Add(i))
                {
                    _openDetails.Remove(i);
                }
                Relayout();
                return;
            }
        }
    }

    public bool HasOpenDetailsHit(float mx, float myDoc)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is HtmlDetails && myDoc >= _ys[i] - 2f && myDoc <= _ys[i] + DetailsBandH(i) && mx >= _colX && mx <= _colX + _colW)
            {
                return true;
            }
        }
        return false;
    }
    // details 摘要行命中带高度:实测摘要布局高(默认 34 兜底)
    private float DetailsBandH(int i)
    {
        Ent ent = GetEnt(i);
        return MathF.Max(34f, ent.DetailsSummaryH + 4f);
    }

    // 任务清单(28):命中复选框区域时返回该任务项在文档顺序中的序号,否则 -1。
    // 序号与 TaskListSource.Locate 的源码扫描一一对应;两侧总数由 TaskCount 校验。
    public int TaskOrdinalAt(float mx, float myDoc)
    {
        int ordinal = 0;
        float band = MathF.Max(24f, RenderResources.LineH(RenderResources.BodySize));
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i] is not ListItem li) continue;
            if (li.Task > 0)
            {
                float x0 = _colX + Indent(li);
                if (i < _ys.Length && myDoc >= _ys[i] && myDoc <= _ys[i] + band && mx >= x0 - 2f && mx <= x0 + 24f) return ordinal;
                ordinal++;
            }
            if (li.Sub != null) ordinal += CountTasks(li.Sub);
        }
        return -1;
    }

    public int TaskCount => CountTasks(_blocks);

    private static int CountTasks(List<Block> blocks)
    {
        int n = 0;
        foreach (Block b in blocks)
        {
            if (b is not ListItem li) continue;
            if (li.Task > 0) n++;
            if (li.Sub != null) n += CountTasks(li.Sub);
        }
        return n;
    }

    private unsafe static int HitInLayout(nint layout, int textLen, float relX, float relY)
    {
        if (layout == IntPtr.Zero || textLen == 0) return 0;
        var hitPoint = (delegate* unmanaged[Stdcall]<nint, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int>)(*(nint*)((nint)(*(nint*)layout) + 64 * sizeof(nint)));
        int trailing = 0, inside = 0;
        DWRITE_HIT_TEST_METRICS hit = default;
        if (hitPoint(layout, relX, relY, &trailing, &inside, &hit) < 0) return 0;
        return Math.Clamp((int)(hit.textPosition + (trailing != 0 ? hit.length : 0)), 0, textLen);
    }

    private int BlockAtY(float myDoc)
    {
        return HitTestingGeometry.BlockAtY(_ys, _heights, _blocks.Count, myDoc);
    }

    private int PosAt(float mx, float myDoc)
    {
        int num = BlockAtY(myDoc);
        if (num < 0)
        {
            return -1;
        }
        Ent ent = GetEnt(num);
        Block block = _blocks[num];
        float num2 = _colX + Indent(block) + ((block is ListItem) ? 26f : 0f);
        float num3 = _ys[num];
        float relY = Math.Clamp(myDoc - num3, 0f, MathF.Max(0f, _heights[num] - 1f));
        // A sideways-scrolled code block paints at (x - ScrollX), so the point the user
        // clicked maps to (relX + ScrollX) inside the layout. Without this, selecting in
        // a scrolled block silently grabs the wrong characters (30).
        float relX = Math.Clamp(mx - num2 + ent.ScrollX, 0f, 10000f);
        if (block is Table t && ent.Cells != null)
        {
            return PosAtTable(num, t, ent, mx, myDoc);
        }
        if (ent.KpLayouts != null)
        {
            // KP 长段落:先按行高定位行,再在行布局内精确命中
            int k = ent.KpLineAt(relY);
            float lineTop = ent.KpTop(k);
            int valK = HitInLayout(ent.KpLayouts[k], ent.KpLines[k].Length, relX, relY - lineTop);
            return _blockStart[num] + ent.KpStarts[k] + Math.Min(valK, ent.KpLines[k].Length);
        }
        if (ent.SubLayouts != null)
        {
            // 列表项子块:先在首段内命中,落入子块区间则逐块命中
            float headH = ent.SubTop;
            if (relY < headH)
            {
                int valH = HitInLayout(ent.L, (ent.Text ?? "").Length, relX, relY);
                return _blockStart[num] + Math.Min(valH, ent.Text.Length);
            }
            for (int sk = 0; sk < ent.SubLayouts.Count; sk++)
            {
                if (relY < ent.SubYs[sk] + ent.SubHeights[sk] + NestedPad(((ListItem)block).Sub[sk]) || sk == ent.SubLayouts.Count - 1)
                {
                    if (ent.SubRich[sk] is Ent rich && ((ListItem)block).Sub[sk] is Table childTable)
                        return PosAtTable(childTable, rich, mx, myDoc, num2 + ent.SubXs[sk], num3 + ent.SubYs[sk], _blockStart[num] + ent.SubStarts[sk]);
                    if (ent.SubLayouts[sk] == IntPtr.Zero) return _blockStart[num] + ent.SubStarts[sk];
                    // A nested code block paints at (x - SubScrollX), so add it back before
                    // hit testing, exactly as the top-level path does with ScrollX (30).
                    int valS = HitInLayout(ent.SubLayouts[sk], ent.SubTexts[sk].Length,
                        MathF.Max(0f, relX - ent.SubXs[sk] + NestedScrollX(ent, sk)), relY - ent.SubYs[sk]);
                    return _blockStart[num] + ent.SubStarts[sk] + Math.Min(valS, ent.SubTexts[sk].Length);
                }
            }
            return _blockStart[num] + ent.SubStarts[^1];
        }
        if (block is HtmlDetails)
        {
            // 两段命中:摘要段(域 [0,sLen],布局含 2 字符 ▸ 前缀)+ 展开正文段(域 [sLen+1,…],绘制 x+14)
            string et = ent.Text ?? "";
            int sLen = et.IndexOf('\n'); if (sLen < 0) sLen = et.Length;
            if (relY < ent.DetailsSummaryH || ent.DetailsBody == IntPtr.Zero)
            {
                int valH = HitInLayout(ent.L, sLen + 2, relX, Math.Min(relY, Math.Max(0f, ent.DetailsSummaryH - 1f)));
                return _blockStart[num] + Math.Clamp(valH - 2, 0, sLen);
            }
            int valB = HitInLayout(ent.DetailsBody, et.Length - sLen - 1, Math.Max(0f, relX - 14f), Math.Max(0f, relY - ent.DetailsSummaryH));
            return _blockStart[num] + sLen + 1 + Math.Min(valB, et.Length - sLen - 1);
        }
        if (ent.L == IntPtr.Zero)
        {
            return _blockStart[num];
        }
        int val = HitInLayout(ent.L, (ent.Text ?? "").Length, relX, relY);
        return _blockStart[num] + Math.Min(val, (ent.Text ?? "").Length);
    }

    private int FindBlockByOffset(int g)
    {
        // _blockStart 非递减:二分找首个 end>g 的块(与原线性扫描语义一致,含零长块跳过)
        int lo = 0, hi = _blocks.Count - 1, res = _blocks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (g < _blockStart[mid + 1]) { res = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return res;
    }

    public string SelDebugTitle()
    {
        if (!HasSel)
        {
            return "无选区 totalChars=" + _totalChars + " blocks=" + _blocks.Count;
        }
        int num = Math.Min(_selStart, _selEnd);
        int num2 = Math.Max(_selStart, _selEnd);
        int num3 = FindBlockByOffset(num);
        int num4 = FindBlockByOffset(Math.Max(num, num2 - 1));
        return string.Format("selS={0} selE={1} b1={2} b2={3} t1={4} t2={5}", _selStart, _selEnd, num3, num4, (GetEnt(num3).Text ?? "").Length, (GetEnt(num4).Text ?? "").Length);
    }

    // ---- 查找支持:全局文本与 _blockStart 完全同源(BlockTextOf + '\n' 连接),偏移可直接喂选区 ----
    public string SearchText()
    {
        var sb = new System.Text.StringBuilder(_totalChars + 16);
        for (int i = 0; i < _blocks.Count; i++) { if (i > 0 && !SharesChildRow(i - 1)) sb.Append('\n'); sb.Append(BlockTextOf(_blocks[i])); }
        return sb.ToString();
    }

    public void SelectRange(int start, int end) { _selStart = start; _selEnd = end; }

    // 选区对应的块序列(41):首尾块按选区偏移切片,中间块整体保留;偏移与 _blockStart/BlockTextOf 同源
    public List<Block> SelectionBlocks()
    {
        if (!HasSel) return null;
        int lo = Math.Min(_selStart, _selEnd), hi = Math.Max(_selStart, _selEnd);
        int b1 = FindBlockByOffset(lo), b2 = FindBlockByOffset(Math.Max(lo, hi - 1));
        var result = new List<Block>();
        for (int i = b1; i <= b2; i++)
        {
            string text = BlockTextOf(_blocks[i]);
            int s = Math.Max(lo - _blockStart[i], 0), e = Math.Min(hi - _blockStart[i], text.Length);
            if (e <= s && text.Length > 0) continue;
            result.Add(SliceBlock(_blocks[i], text, s, e));
        }
        return result;
    }

    public (string Text, string Html) CopyRich()
    {
        var blocks = SelectionBlocks();
        if (blocks == null || blocks.Count == 0) return default;
        return (CopyText(), HtmlExport.Fragment(blocks));
    }

    private static Block SliceBlock(Block b, string text, int s, int e)
    {
        if (s <= 0 && e >= text.Length) return b;
        Block sliced = b switch
        {
            Para p => new Para { Inlines = SliceRuns(p.Inlines, s, e - s) },
            Heading h => new Heading { Level = h.Level, Inlines = SliceRuns(h.Inlines, s, e - s) },
            Quote q => new Quote { Inlines = SliceRuns(q.Inlines, s, e - s) },
            ListItem li when li.Sub == null => new ListItem { Marker = li.Marker, Inlines = SliceRuns(li.Inlines, s, e - s), Task = li.Task, Loose = li.Loose, Nest = li.Nest, ListGroup = li.ListGroup, Continuation = li.Continuation },
            CodeBlock cb => new CodeBlock { Text = text.Substring(s, e - s), Lang = cb.Lang, FrontMatter = cb.FrontMatter },
            _ => b   // 表格/图片/details/子块列表项:任一字符命中即整体复制
        };
        if (!ReferenceEquals(sliced, b)) { sliced.XOff = b.XOff; sliced.QuoteDepth = b.QuoteDepth; sliced.QSeq = b.QSeq; sliced.Alert = b.Alert; sliced.AlertTitle = b.AlertTitle; }
        return sliced;
    }

    public int SelHead => (_selStart < 0 || _selEnd < 0) ? 0 : Math.Min(_selStart, _selEnd);
    public int SelTail => (_selStart < 0 || _selEnd < 0) ? 0 : Math.Max(_selStart, _selEnd);

    /// 命中偏移的视口纵坐标(DIP):块内按长度线性插值,滚动定位足够准
    public float YAtOffset(int g)
    {
        int i = FindBlockByOffset(g);
        int s = _blockStart[i];
        string t = BlockTextOf(_blocks[i]);
        float frac = t.Length == 0 ? 0f : (float)(g - s) / t.Length;
        return _ys[i] + frac * _heights[i];
    }

    /// Bring a text offset inside a sideways-scrollable code block into view horizontally.
    /// Vertical scrolling alone is not enough once code blocks scroll: a hit can sit past
    /// the right edge of its block and be highlighted where nobody can see it (30).
    /// No-op for every block that does not overflow, which is the overwhelming majority.
    public unsafe bool RevealOffsetX(int g)
    {
        int i = FindBlockByOffset(g);
        if (i < 0 || i >= _blocks.Count) return false;
        if (_blocks[i] is Table table) return RevealTableOffsetX(table, i, g);
        if (_blocks[i] is ListItem) return RevealNestedOffsetX(i, g);
        if (_blocks[i] is not CodeBlock) return false;
        var e = GetEnt(i);
        if (e == null || e.OverflowW <= 0.5f || e.L == IntPtr.Zero) return false;
        int length = (e.Text ?? "").Length;
        int local = Math.Clamp(g - _blockStart[i], 0, length);
        if (local >= length) return false;

        DWRITE_HIT_TEST_METRICS* metrics = stackalloc DWRITE_HIT_TEST_METRICS[4];
        uint produced = 0u;
        if (Dw.HitRange(e.L, (uint)local, 1u, 0f, 0f, metrics, 4u, &produced) < 0 || produced == 0) return false;
        float hitLeft = metrics[0].left, hitRight = metrics[0].left + metrics[0].width;

        float viewport = CodeViewportW(_colW - Indent(_blocks[i]));
        float before = e.ScrollX;
        // Keep a little context on whichever side the hit came from rather than parking it
        // flush against the edge.
        const float margin = 40f;
        if (hitLeft < e.ScrollX + margin) e.ScrollX = hitLeft - margin;
        else if (hitRight > e.ScrollX + viewport - margin) e.ScrollX = hitRight - viewport + margin;
        e.ScrollX = Math.Clamp(e.ScrollX, 0f, e.OverflowW);
        return MathF.Abs(e.ScrollX - before) > 0.01f;
    }

    // Same idea for a wide table, but a table has no single layout to hit-test against:
    // walk the per-cell character spans to find the column holding the offset, then bring
    // that whole column into view. A match inside the frozen column needs no scrolling
    // because that column is always on screen (30).
    private bool RevealTableOffsetX(Table table, int block, int g) =>
        RevealTableOffsetX(table, GetEnt(block), g - _blockStart[block]);

    private bool RevealTableOffsetX(Table table, Ent e, int local)
    {
        if (e == null || e.OverflowW <= 0.5f || e.CellTexts == null) return false;
        int columns = table.Header.Length;
        if (columns <= 0) return false;
        if (local < 0) return false;

        int scan = 0, hitCol = -1;
        for (int row = 0; row <= table.Rows.Length && hitCol < 0; row++)
        {
            if (row >= e.CellTexts.Length) break;
            for (int col = 0; col < columns; col++)
            {
                int length = e.CellTexts[row][col].Length;
                if (local >= scan && local <= scan + length) { hitCol = col; break; }
                scan += length + (col + 1 < columns ? 3 : 0);
            }
            scan++;
        }
        if (hitCol < 0) return false;
        if (e.FrozenW > 0f && hitCol == 0) return false;

        float columnLeft = 0f;
        for (int col = 0; col < hitCol; col++) columnLeft += e.ColWs[col];
        float columnRight = columnLeft + e.ColWs[hitCol];

        float viewport = TableViewportW(e);
        float before = e.ScrollX;
        const float margin = 40f;
        // The frozen column covers the left of the viewport, so "visible" starts after it.
        if (columnLeft < e.ScrollX + e.FrozenW + margin) e.ScrollX = columnLeft - e.FrozenW - margin;
        else if (columnRight > e.ScrollX + viewport - margin) e.ScrollX = columnRight - viewport + margin;
        e.ScrollX = Math.Clamp(e.ScrollX, 0f, e.OverflowW);
        return MathF.Abs(e.ScrollX - before) > 0.01f;
    }

    // Same reveal for a code block nested in a list item. Found by auditing which consumers
    // handle which alternate layout path: PosAt and FillSelForBlock had been taught about
    // SubScrollX, but the search reveal had not — so a hit past the right edge of a nested
    // block scrolled into view vertically and stayed invisible horizontally (30).
    private unsafe bool RevealNestedOffsetX(int block, int g)
    {
        var e = GetEnt(block);
        if (e?.SubLayouts == null || e.SubOverflowW == null) return false;
        int local = g - _blockStart[block];
        if (local < 0) return false;

        for (int sk = 0; sk < e.SubLayouts.Count; sk++)
        {
            int start = e.SubStarts[sk], length = e.SubTexts[sk].Length;
            if (local < start || local > start + length) continue;
            if (e.SubRich[sk] is Ent rich && ((ListItem)_blocks[block]).Sub[sk] is Table table)
                return RevealTableOffsetX(table, rich, local - start);
            if (e.SubLayouts[sk] == IntPtr.Zero || NestedOverflow(e, sk) <= 0.5f) return false;
            int inner = Math.Clamp(local - start, 0, Math.Max(0, length - 1));

            DWRITE_HIT_TEST_METRICS* metrics = stackalloc DWRITE_HIT_TEST_METRICS[4];
            uint produced = 0u;
            if (Dw.HitRange(e.SubLayouts[sk], (uint)inner, 1u, 0f, 0f, metrics, 4u, &produced) < 0 || produced == 0) return false;
            float hitLeft = metrics[0].left, hitRight = metrics[0].left + metrics[0].width;

            float viewport = NestedViewportW(MathF.Max(120f, _colW - Indent(_blocks[block]) - 46f));
            float overflow = NestedOverflow(e, sk);
            float before = e.SubScrollX[sk];
            const float margin = 40f;
            float next = before;
            if (hitLeft < next + margin) next = hitLeft - margin;
            else if (hitRight > next + viewport - margin) next = hitRight - viewport + margin;
            e.SubScrollX[sk] = Math.Clamp(next, 0f, overflow);
            return MathF.Abs(e.SubScrollX[sk] - before) > 0.01f;
        }
        return false;
    }

    public string CopyText()
    {
        if (!HasSel)
        {
            return null;
        }
        int num = Math.Min(_selStart, _selEnd);
        int num2 = Math.Max(_selStart, _selEnd);
        int num3 = FindBlockByOffset(num);
        int num4 = FindBlockByOffset(Math.Max(num, num2 - 1));
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = num3; i <= num4; i++)
        {
            string text = GetEnt(i).Text ?? BlockTextOf(_blocks[i]);
            int num5 = _blockStart[i];
            int num6 = Math.Max(num - num5, 0);
            int num7 = Math.Min(num2 - num5, text.Length);
            if (num7 > num6)
            {
                int num8 = num6;
                stringBuilder.Append(text.Substring(num8, num7 - num8));
            }
            if (i < num4 && !SharesChildRow(i))
            {
                stringBuilder.Append('\n');
            }
        }
        return stringBuilder.ToString();
    }

    // 单元格文本:优先 Ent 缓存(Build 时与 CellText 同源生成),旧 Ent 无缓存时回退实时算
    private static string CellTextCached(Ent e, Table t, int row, int col)
    {
        if (e.CellTexts != null && row < e.CellTexts.Length && col < e.CellTexts[row].Length) return e.CellTexts[row][col];
        return CellText(t, row, col);
    }

    private int PosAtTable(int bi, Table t, Ent e, float mx, float myDoc) =>
        PosAtTable(t, e, mx, myDoc, _colX + Indent(_blocks[bi]), _ys[bi], _blockStart[bi]);

    private int PosAtTable(Table t, Ent e, float mx, float myDoc, float left, float top, int baseChar)
    {
        int num = t.Header.Length;
        float num2 = left;
        // A scrolled table paints columns past the first at (x - ScrollX); the frozen
        // first column does not move. Undo the shift only outside it, otherwise clicking
        // in a scrolled table silently selects the wrong cell (30).
        float shift = e.OverflowW > 0.5f ? e.ScrollX : 0f;
        float frozen = shift > 0f ? e.FrozenW : 0f;
        float num3 = mx - num2;
        if (shift > 0f && num3 >= frozen) num3 += shift;
        int num4 = -1;
        float num5 = top;
        if (myDoc < top + e.HeadH)
        {
            num4 = 0;
        }
        else
        {
            float num6 = top + e.HeadH;
            for (int i = 0; i < t.Rows.Length; i++)
            {
                if (myDoc < num6 + e.RowHs[i])
                {
                    num4 = i + 1;
                    num5 = num6;
                    break;
                }
                num6 += e.RowHs[i];
            }
            if (num4 < 0)
            {
                num4 = t.Rows.Length;
                num5 = top + e.HeadH;
            }
        }
        int num7 = num - 1;
        float num8 = 0f;
        for (int j = 0; j < num; j++)
        {
            if (num3 < num8 + e.ColWs[j])
            {
                num7 = j;
                break;
            }
            num8 += e.ColWs[j];
        }
        int[] array = e.RowLens ?? TableRowLens(t);   // 旧 Ent 兜底:缓存缺失时按原路径重建
        int num9 = 0;
        for (int k = 0; k < num4; k++)
        {
            num9 += array[k] + 1;
        }
        int num10 = 0;
        for (int l = 0; l < num7; l++)
        {
            num10 += CellTextCached(e, t, num4, l).Length + 3;
        }
        string text = CellTextCached(e, t, num4, num7);
        nint num11 = e.Cells[num4 * num + num7];
        if (num11 == IntPtr.Zero)
        {
            return baseChar + num9 + num10;
        }
        float relY = Math.Clamp(myDoc - num5 - 7f, 0f, 10000f);
        float relX = Math.Clamp(num3 - num8 - 8f, 0f, 10000f);
        int val = HitInLayout(num11, text.Length, relX, relY);
        return baseChar + num9 + num10 + Math.Min(val, text.Length);
    }

    private unsafe void FillSelCell(int bi, Table t, Ent e, int row, int col, float x, float y)
    {
        if (!HasSel)
        {
            return;
        }
        string text = CellTextCached(e, t, row, col);
        if (text.Length == 0)
        {
            return;
        }
        int[] array = e.RowLens ?? TableRowLens(t);   // 旧 Ent 兜底
        int num = 0;
        for (int i = 0; i < row; i++)
        {
            num += array[i] + 1;
        }
        int num2 = 0;
        for (int j = 0; j < col; j++)
        {
            num2 += CellTextCached(e, t, row, j).Length + 3;
        }
        int num3 = _blockStart[bi] + num + num2;
        int num4 = Math.Min(_selStart, _selEnd);
        int num5 = Math.Max(_selStart, _selEnd);
        int num6 = Math.Clamp(num4 - num3, 0, text.Length);
        int num7 = Math.Clamp(num5 - num3, 0, text.Length);
        if (num7 <= num6)
        {
            return;
        }
        nint num8 = e.Cells[row * t.Header.Length + col];
        if (num8 == IntPtr.Zero)
        {
            return;
        }
        DWRITE_HIT_TEST_METRICS* ptr = stackalloc DWRITE_HIT_TEST_METRICS[64];
        uint num9 = 0u;
        if (Dw.HitRange(num8, (uint)num6, (uint)(num7 - num6), x, y, ptr, 64u, &num9) >= 0)
        {
            for (uint num10 = 0u; num10 < num9 && num10 < 64; num10++)
            {
                D2D1_RECT_F d2D1_RECT_F = default(D2D1_RECT_F);
                d2D1_RECT_F.left = ptr[num10].left;
                d2D1_RECT_F.top = ptr[num10].top;
                d2D1_RECT_F.right = ptr[num10].left + ptr[num10].width;
                d2D1_RECT_F.bottom = ptr[num10].top + ptr[num10].height;
                D2D1_RECT_F d2D1_RECT_F2 = d2D1_RECT_F;
                PFill(Rt, &d2D1_RECT_F2, BSel);
            }
        }
    }

    private void FillSelForBlock(int i, float x, float y)
    {
        if (!HasSel)
        {
            return;
        }
        Ent ent = GetEnt(i);
        if (ent.KpLayouts != null)
        {
            // KP 长段落:逐行裁剪选区并高亮
            int base0 = _blockStart[i];
            int s = Math.Max(Math.Min(_selStart, _selEnd) - base0, 0);
            int e = Math.Min(Math.Max(_selStart, _selEnd) - base0, (ent.Text ?? "").Length);
            if (e <= s)
            {
                return;
            }
            for (int k = 0; k < ent.KpLayouts.Count; k++)
            {
                int ls = ent.KpStarts[k];
                int ll = ent.KpLines[k].Length;
                int a = Math.Clamp(s - ls, 0, ll);
                int b = Math.Clamp(e - ls, 0, ll);
                if (b > a)
                {
                    FillSelRectsAt(ent.KpLayouts[k], x, y + ent.KpTop(k), a, b - a);
                }
            }
            return;
        }
        if (_blocks[i] is HtmlDetails)
        {
            // details 两段选区:摘要段(布局含 2 字符前缀)+ 展开正文段(绘制 x+14)
            int base0 = _blockStart[i];
            string et = ent.Text ?? "";
            int sLen = et.IndexOf('\n'); if (sLen < 0) sLen = et.Length;
            int s = Math.Max(Math.Min(_selStart, _selEnd) - base0, 0);
            int e = Math.Min(Math.Max(_selStart, _selEnd) - base0, et.Length);
            if (e <= s) return;
            int ha = Math.Clamp(s, 0, sLen), hb = Math.Clamp(e, 0, sLen);
            if (hb > ha) FillSelRectsAt(ent.L, x, y, ha + 2, hb - ha);
            if (ent.DetailsBody != IntPtr.Zero)
            {
                int baseOff = sLen + 1;
                int ba = Math.Clamp(s - baseOff, 0, et.Length - baseOff);
                int bb = Math.Clamp(e - baseOff, 0, et.Length - baseOff);
                if (bb > ba) FillSelRectsAt(ent.DetailsBody, x + 14f, y + ent.DetailsSummaryH, ba, bb - ba);
            }
            return;
        }
        if (ent.SubLayouts != null)
        {
            // 列表项子块:首段 + 各子块分别裁剪选区
            int baseS = _blockStart[i];
            int sS = Math.Min(_selStart, _selEnd) - baseS;
            int eS = Math.Max(_selStart, _selEnd) - baseS;
            string headT = ent.Text ?? "";
            if (ent.L != IntPtr.Zero)
            {
                int a0 = Math.Clamp(sS, 0, headT.Length);
                int b0 = Math.Clamp(eS, 0, headT.Length);
                if (b0 > a0) FillSelRectsAt(ent.L, x, y, a0, b0 - a0);
            }
            for (int k = 0; k < ent.SubLayouts.Count; k++)
            {
                if (ent.SubLayouts[k] != IntPtr.Zero)
                {
                    int ls = ent.SubStarts[k];
                    int ll = ent.SubTexts[k].Length;
                    int a = Math.Clamp(sS - ls, 0, ll);
                    int b = Math.Clamp(eS - ls, 0, ll);
                    // Twin of the glyph draw: a scrolled nested code block paints shifted, so
                    // its selection rectangles must move by the same amount or the highlight
                    // detaches from the text (30).
                    if (b > a) FillSelRectsAt(ent.SubLayouts[k], x + ent.SubXs[k] - NestedScrollX(ent, k), y + ent.SubYs[k], a, b - a);
                }
            }
            return;
        }
        if (ent.L != IntPtr.Zero)
        {
            int num = _blockStart[i];
            int length = (ent.Text ?? "").Length;
            int num2 = Math.Min(_selStart, _selEnd);
            int num3 = Math.Max(_selStart, _selEnd);
            int num4 = Math.Clamp(num2 - num, 0, length);
            int num5 = Math.Clamp(num3 - num, 0, length);
            if (num5 > num4)
            {
                FillSelRectsAt(ent.L, x, y, num4, num5 - num4);
            }
        }
    }

    // §4.3:当前命中 = 选区底色 + Accent 轮廓。每次绘制判断一次:选区恰好等于当前命中。
    private bool _selIsHit;
    private bool SelectionIsCurrentHit()
    {
        if (!HasSel || SearchHitIdx < 0 || SearchHitIdx >= SearchHits.Count) return false;
        var (s, l) = SearchHits[SearchHitIdx];
        return Math.Min(_selStart, _selEnd) == s && Math.Max(_selStart, _selEnd) == s + l;
    }

    private unsafe void FillSelRectsAt(nint layout, float x, float y, int from, int len)
    {
        if (layout == IntPtr.Zero || len <= 0) return;
        InkLinksIn(layout, from, Math.Min(len, 4096));   // E7
        DWRITE_HIT_TEST_METRICS* ptr = stackalloc DWRITE_HIT_TEST_METRICS[64];
        uint num = 0u;
        if (Dw.HitRange(layout, (uint)from, (uint)Math.Min(len, 4096), x, y, ptr, 64u, &num) >= 0)
        {
            for (uint num2 = 0u; num2 < num && num2 < 64; num2++)
            {
                D2D1_RECT_F d2D1_RECT_F = default(D2D1_RECT_F);
                d2D1_RECT_F.left = ptr[num2].left;
                d2D1_RECT_F.top = ptr[num2].top;
                d2D1_RECT_F.right = ptr[num2].left + ptr[num2].width;
                d2D1_RECT_F.bottom = ptr[num2].top + ptr[num2].height;
                D2D1_RECT_F d2D1_RECT_F2 = d2D1_RECT_F;
                // U7：desk 下当前一处用查找底色（演示 mark.hit.cur），外面的朱笔圈另画；classic 仍是选区色 + 轮廓。
                NoteSelectionRect(d2D1_RECT_F2);
                bool ink = _selIsHit && RenderResources.Reading.IsDesk;
                PFill(Rt, &d2D1_RECT_F2, ink && !HighContrast ? BHit : BSel);
                if (ink) { if (!_inkRectSet) { _inkRect = d2D1_RECT_F2; _inkRectSet = true; } }
                else if (_selIsHit) OutlineRect(d2D1_RECT_F2, BAccent);
            }
        }
    }

    // U7：本帧画出的选区外框（窗口 DIP），供选区气泡定位；只含视口里真正画到的部分。
    private bool _selBoundsSet;
    private D2D1_RECT_F _selBounds;
    private void NoteSelectionRect(D2D1_RECT_F r)
    {
        if (!_selBoundsSet) { _selBounds = r; _selBoundsSet = true; return; }
        _selBounds.left = MathF.Min(_selBounds.left, r.left); _selBounds.top = MathF.Min(_selBounds.top, r.top);
        _selBounds.right = MathF.Max(_selBounds.right, r.right); _selBounds.bottom = MathF.Max(_selBounds.bottom, r.bottom);
    }
    public bool TryGetSelectionBounds(out float left, out float top, out float right, out float bottom)
    {
        (left, top, right, bottom) = (_selBounds.left, _selBounds.top, _selBounds.right, _selBounds.bottom);
        return _selBoundsSet && HasSel;
    }

    // U7：朱笔圈。几何在 InkCircle（纯）；InkProgress 由宿主按动画时钟写入（0 未起笔，1 画完）。
    public float InkProgress = 1f;
    private bool _inkRectSet;
    private D2D1_RECT_F _inkRect;

    private unsafe void PaintInkCircle()
    {
        if (!_inkRectSet || InkProgress <= 0f || SearchHitIdx < 0 || SearchHitIdx >= SearchHits.Count) return;
        var f = InkCircle.Frame(_inkRect.left, _inkRect.top, _inkRect.right - _inkRect.left, _inkRect.bottom - _inkRect.top);
        var p = InkCircle.Points(f.W, f.H, InkCircle.Seed(SearchHits[SearchHitIdx].start));
        var (whole, part) = InkCircle.Progress(p, InkProgress);
        SetOpacity(BAccent, InkCircle.Opacity);
        try
        {
            for (int k = 1; k <= whole; k++) StrokeLine(f.X + p[k - 1].X, f.Y + p[k - 1].Y, f.X + p[k].X, f.Y + p[k].Y, BAccent, InkCircle.Stroke);
            float ex = f.X + p[whole].X, ey = f.Y + p[whole].Y;
            if (whole < p.Length - 1 && part > 0f)
            {
                float nx = f.X + p[whole + 1].X, ny = f.Y + p[whole + 1].Y;
                float mx = ex + (nx - ex) * part, my = ey + (ny - ey) * part;
                StrokeLine(ex, ey, mx, my, BAccent, InkCircle.Stroke);
                ex = mx; ey = my;
            }
            // 演示 stroke-linecap:round：两端补圆头（中间折点的缝在 1.8 DIP 线宽下看不出）。
            var head = new D2D1_ELLIPSE { point = new D2D1_POINT_2F { x = f.X + p[0].X, y = f.Y + p[0].Y }, radiusX = InkCircle.Stroke / 2f, radiusY = InkCircle.Stroke / 2f };
            PFillEllipse(Rt, &head, BAccent);
            head.point = new D2D1_POINT_2F { x = ex, y = ey };
            PFillEllipse(Rt, &head, BAccent);
        }
        finally { SetOpacity(BAccent, 1f); }
    }

    // 1 物理像素轮廓,落整像素并画在矩形内侧,不改变命中框的占位。
    private unsafe void OutlineRect(D2D1_RECT_F box, nint brush)
    {
        float s = Scale > 0f ? Scale : 1f, one = 1f / s;
        float l = MathF.Round(box.left * s) / s, t = MathF.Round(box.top * s) / s, r = MathF.Round(box.right * s) / s, b = MathF.Round(box.bottom * s) / s;
        if (r - l < 2f * one || b - t < 2f * one) return;
        var edge = new D2D1_RECT_F { left = l, top = t, right = r, bottom = t + one }; PFill(Rt, &edge, brush);
        edge = new D2D1_RECT_F { left = l, top = b - one, right = r, bottom = b }; PFill(Rt, &edge, brush);
        edge = new D2D1_RECT_F { left = l, top = t + one, right = l + one, bottom = b - one }; PFill(Rt, &edge, brush);
        edge = new D2D1_RECT_F { left = r - one, top = t + one, right = r, bottom = b - one }; PFill(Rt, &edge, brush);
    }

    public unsafe string LinkAt(float mx, float myDoc)
    {
        int num = BlockAtY(myDoc);
        if (num >= 0)
        {
            GetEnt(num);
        }
        for (int i = Math.Max(0, num - 1); i < _blocks.Count; i++)
        {
            Block block = _blocks[i];
            if (myDoc < _ys[i] || myDoc > _ys[i] + _heights[i])
            {
                continue;
            }
            Ent ent = GetEnt(i);
            if (block is Table rootTable) return RichTableLink(rootTable, ent, _colX + Indent(block), _ys[i], mx, myDoc);
            if (block is ListItem { Sub: not null } item && ent.SubRich != null)
            {
                for (int child = 0; child < ent.SubRich.Count; child++)
                {
                    float childY = _ys[i] + ent.SubYs[child];
                    if (myDoc >= childY && myDoc < childY + ent.SubHeights[child] && item.Sub[child] is Table childTable)
                        return RichTableLink(childTable, ent.SubRich[child], _colX + Indent(block) + 26 + ent.SubXs[child], childY, mx, myDoc);
                }
            }
            if (ent.Links.Count == 0)
            {
                return null;
            }
            float num2;
            float num3;
            if (block is ListItem)
            {
                num2 = _colX + Indent(block) + 26f;
                num3 = _ys[i];
            }
            else
            {
                num2 = _colX + Indent(block);
                num3 = _ys[i];
            }
            int hitPos;
            if (block is HtmlDetails)
            {
                // details 两段:hitPos 折算到统一块文本域后扫描 ent.Links
                string et = ent.Text ?? "";
                int sLen = et.IndexOf('\n'); if (sLen < 0) sLen = et.Length;
                if (myDoc - num3 < ent.DetailsSummaryH || ent.DetailsBody == IntPtr.Zero)
                {
                    hitPos = HitInLayout(ent.L, sLen + 2, mx - num2, Math.Min(myDoc - num3, Math.Max(0f, ent.DetailsSummaryH - 1f))) - 2;
                }
                else
                {
                    hitPos = sLen + 1 + HitInLayout(ent.DetailsBody, et.Length - sLen - 1, Math.Max(0f, mx - num2 - 14f), Math.Max(0f, myDoc - num3 - ent.DetailsSummaryH));
                }
            }
            else if (ent.SubLayouts != null || block is Table)
            {
                var hit = PosAtText(mx, myDoc);
                if (hit.block != i) return null;
                hitPos = hit.pos;
            }
            else if (ent.KpLayouts != null)
            {
                // KP 长段落:逐行命中后映射回段内偏移
                int k = ent.KpLineAt(myDoc - num3);
                float lineTop = ent.KpTop(k);
                hitPos = ent.KpStarts[k] + HitInLayout(ent.KpLayouts[k], ent.KpLines[k].Length, mx - num2, myDoc - num3 - lineTop);
            }
            else
            {
                if (ent.L == IntPtr.Zero)
                {
                    return null;
                }
                delegate* unmanaged[Stdcall]<nint, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int> fnPtr3 = (delegate* unmanaged[Stdcall]<nint, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int>)(*(IntPtr*)((nint)(*(IntPtr*)ent.L) + (nint)64 * (nint)sizeof(nint)));
                int num4 = 0;
                int num5 = 0;
                DWRITE_HIT_TEST_METRICS dWRITE_HIT_TEST_METRICS = default(DWRITE_HIT_TEST_METRICS);
                if (fnPtr3(ent.L, mx - num2, myDoc - num3, &num5, &num4, &dWRITE_HIT_TEST_METRICS) < 0 || num4 == 0)
                {
                    return null;
                }
                hitPos = (int)dWRITE_HIT_TEST_METRICS.textPosition;
            }
            foreach (var (num6, num7, result) in ent.Links)
            {
                if (hitPos >= num6 && hitPos < num6 + num7)
                {
                    return result;
                }
            }
            return null;
        }
        return null;
    }
}
