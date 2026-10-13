using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// One UI thread shares these resources. The application scope outlives every view and control.
internal sealed unsafe class RenderResources : IDisposable
{

    public static nint Rt;

    public static nint BInk;
    public static nint BCloseHover, BCloseGlyph;
    public static nint BQuietIdle, BQuietOn;   // U6c：读时退后的混合色（ink2→muted、ink→ink2）
    static D2D1_COLOR_F _mixInk, _mixInk2, _mixMuted;   // U6b：关闭按钮悬停红底白字（系统色，不随纸张）
    public static nint BDeskHover, BSurfaceHover, BAccentHover;   // 试用反馈：悬停底（顶栏上 / 纸面卡片上），暗纸加大反差；经典界面同 Code
    public static nint BDesk, BDesk2, BAccentSoft;   // U6a：顶栏桌面色、悬停底、选中工具底；经典界面依次同 Canvas / Code / Sel
    public static nint BFaint;   // U5b：未读刻度；classic 与高对比度下同 BMuted
    public static nint BEdge, BShadow;   // U10：卡片边线（desk 取纸张 Edge，否则同细线）、投影色（配合 SetOpacity）
    public static float ShadowAlpha;     // U10：投影浓度；经典界面与高对比度为 0（不画投影）
    public static nint BShade, BGlint;   // U5a：固定黑 / 白，配合 SetOpacity 做明暗与投影，不随主题变
    public static nint BInk2;   // U3b：次级墨色（desk 引用正文）；classic 与高对比度下同 BInk

    public static nint BCode;

    public static nint BHair;

    public static nint BQuote;

    /// 正文基准字号(DIP);标题 26/21/18/16、代码 13.5、UI 13 均按 16 基准派生。
    public static float BodySize { get; private set; } = 16f;
    public static int TypographyVersion { get; private set; }

    // I11:serif opt-in(PLAN-TYPO-9 §3,默认关=零回归)。SerifFamily 由 Program 注册表探测注入(null=未安装)。
    // palt/KpKern 挂 backlog:本机无思源宋体不可验证,不猜 COM 槽位(探针纪律)。
    public static bool SerifMode { get; private set; }
    public static string SerifFamily { get; private set; }
    public static float SerifBase = 0.75f;   // 基线占比探针值(仅 serif 档用;sans 恒 0.73 不动,零回归)
    private const string SansFamily = "Segoe UI";
    static string BodyFam() => SerifMode && SerifFamily != null ? SerifFamily : SansFamily;

    // v2.2 §3.1(B2):行高=1.75×字号(28 DIP @16,恒比缩放在 12/20/24 各档都不裁字;rhythm 段距与 SetLineSpacing 共用)
    // U3:行高倍数随版式(classic 1.75、desk 1.9)。
    public static float LineH(float bs) => Reading.LineRatio * bs;

    // U3:正文版式,构造时注入,Dispose 时回到 Classic(同一进程里的无窗口测试互不串味)。
    public static ReadingStyle Reading { get; private set; } = ReadingStyle.Classic;
    static string HeadingFam() => Reading.SerifHeadings && SerifFamily != null ? SerifFamily : SansFamily;
    public static float MonoSize(float bs) => 0.84375f * bs;             // v2.2 §3.1:代码字号(13.5 @16)
    public static float CodeLineH(float bs) => 1.6f * MonoSize(bs);      // v2.2 §3.1:代码行高约 1.6×mono(21.6 @16,刻意密于正文)
    public static float MonoBase = 0.79f;                                // 等宽基线占比(RebuildFormats 探针回填;回落≈Consolas 实测)

    internal static void SetBodySize(float size)
    {
        BodySize = size;
        RebuildFormats();
    }

    // U3:切换正文版式并重建文本格式(调用方负责 Relayout)。启动时由构造函数注入,这里供自检与 U12 使用。
    internal static void SetReading(ReadingStyle reading)
    {
        Reading = reading ?? ReadingStyle.Classic;
        RebuildFormats();
    }

    internal static void SetSerif(bool enabled, string family)
    {
        SerifMode = enabled;
        SerifFamily = family;
        RebuildFormats();
        if (enabled && family != null) SerifBase = ProbeBaseline("中", FmtBody, 0.75f);
    }

    private static void ReleaseFormats()
    {
        Release(ref FmtH1); Release(ref FmtH2); Release(ref FmtH3); Release(ref FmtH4); Release(ref FmtSeal); Release(ref FmtQuote);
        Release(ref FmtBody); Release(ref FmtMono); Release(ref FmtUi);
    }

    // A1-B5 中文回退探针(计划 §6 B5):先取证再决定落地方式 —— 不猜 COM 槽位、不加新接口,只用现有 CreateTextFormat。
    // 判据:(a) 混排下"逗号族名列表"若成立,西文应取第一族、中文取能覆盖的族;
    //       (b) 纯 "Segoe UI" 与 "Microsoft YaHei UI" 的中文宽度若相等,说明系统回退已落到同一字面;
    //       (c) 伪族名 "NoSuchFamily-A1B5" 作对照:逗号串若与伪族名同宽,说明整串被当成未知族(列表名不受支持)。
    // 生产正文族名(探针/自检读取;见 FontProbeReport 的 B5 结论)
    public static string BodyFamilyName => BodyFam();

    public static string FontProbeReport()
    {
        var sb = new System.Text.StringBuilder();
        if (Dw.Factory == IntPtr.Zero) Dw.Init();   // 探针独立入口:生产路径由启动时初始化,探针须自行初始化
        sb.AppendLine("font-probe body=" + BodySize.ToString("G", System.Globalization.CultureInfo.InvariantCulture)
            + " serif=" + (SerifMode ? "on" : "off") + " serifFamily=" + (SerifFamily ?? "(null)") + " production=" + BodyFam());
        string[] fams = { "Segoe UI", "Microsoft YaHei UI", "Segoe UI, Microsoft YaHei UI", "Microsoft YaHei UI, Segoe UI", "NoSuchFamily-A1B5", "SimSun", "SimSun, Segoe UI" };
        string[] texts = { "Hamburgefonstiv ABC 0123", "中文字体渲染测试", "混排 ABC 中文字体 0123 号" };
        foreach (string fam in fams)
        {
            nint fmt;
            try { fmt = Dw.MkFormat(fam, 400u, BodySize); }
            catch (Exception ex) { sb.AppendLine("FAM " + fam + " -> CreateTextFormat 失败: " + ex.Message); continue; }
            sb.Append("FAM " + fam + " ->");
            foreach (string t in texts)
            {
                nint lay = nint.Zero;
                string tag = t.Substring(0, Math.Min(4, t.Length));
                try { lay = Dw.MkLayout(t, fmt, 2000f); sb.Append(" [" + tag + " w=" + Dw.LayoutW(lay).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]"); }
                catch (Exception ex) { sb.Append(" [" + tag + " ERR " + ex.Message + "]"); }
                finally { if (lay != nint.Zero) { System.Runtime.InteropServices.Marshal.Release(lay); } }
            }
            sb.AppendLine();
            System.Runtime.InteropServices.Marshal.Release(fmt);
        }
        return sb.ToString();
    }

    private static void RebuildFormats()
    {
        ReleaseFormats();
        float bs = BodySize;
        // Serif opt-in affects the body only; headings/UI remain sans, code remains monospace.
        // v2.2 §3.1 字阶(B2):H1 26/700、H2 21/700、H3 18/700、H4–H6=bs/700;代码 0.84375×bs(13.5 @16)
        // S87:H2–H6 由 600 改 700 —— 600 时西文用 Segoe UI 半粗,雅黑没有半粗落到粗体,同一标题里中西文两种粗细。
        // U3 desk:标题用宋体(思源宋体优先),字阶见 ReadingStyle;H3 以下 600(演示)。classic 数值不变。
        var rs = Reading;
        FmtH1 = Dw.MkFormat(HeadingFam(), 700u, rs.HeadingScale(1) * bs);
        FmtH2 = Dw.MkFormat(HeadingFam(), 700u, rs.HeadingScale(2) * bs);
        FmtH3 = Dw.MkFormat(rs.IsDesk ? SansFamily : HeadingFam(), rs.IsDesk ? 600u : 700u, rs.HeadingScale(3) * bs);
        FmtH4 = Dw.MkFormat(rs.IsDesk ? SansFamily : HeadingFam(), rs.IsDesk ? 600u : 700u, rs.HeadingScale(4) * bs);
        if (rs.IsDesk) FmtSeal = Dw.MkFormat(HeadingFam(), 700u, 0.6f * rs.HeadingScale(2) * bs);   // H2 方印里的数字
        if (rs.IsDesk) FmtQuote = Dw.MkFormat(HeadingFam(), 400u, ReadingStyle.QuoteScale * bs);    // U3b：引用用宋体、略大
        FmtBody = Dw.MkFormat(BodyFam(), 400u, bs);
        FmtMono = Dw.MkFormat("Consolas", 400u, MonoSize(bs));
        FmtUi = Dw.MkFormat(SansFamily, 400u, MathF.Max(10f, 0.8125f * bs));
        MonoBase = ProbeBaseline("Ag", FmtMono, 0.79f);   // B2:等宽基线占比探针(回落 0.79≈Consolas 实测)
        TypographyVersion++;
        CharacterWidths.Clear();
    }

    // 字体自然基线占比探针(GetLineMetrics 实测,不猜 DesignMetrics 槽位):"中"单行布局 baseline/height
    private static unsafe float ProbeBaseline(string probe, nint fmt, float fallback)
    {
        try
        {
            nint l = Dw.MkLayout(probe, fmt, 10000f);
            Dw.Bind(l);
            var lm = default(DWRITE_LINE_METRICS);
            uint got = 0;
            int hr = Dw.GetLineMetrics(l, &lm, 1, &got);
            float f = hr >= 0 && got > 0 && lm.height > 0.01f ? lm.baseline / lm.height : fallback;
            Marshal.Release(l);
            return f < 0.6f || f > 0.9f ? fallback : f;
        }
        catch { return fallback; }
    }

    public static nint BCanvas;   // 画布背景刷(工具栏背景填充用, 随主题更新)

    public static float Scale = 1f;

    public unsafe static delegate* unmanaged[Stdcall]<nint, nint, RECT*, void> PBind;

    public unsafe static delegate* unmanaged[Stdcall]<nint, void> PBegin;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_COLOR_F*, void> PClear;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_RECT_F*, nint, void> PFill;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_POINT_2F, nint, nint, int, void> PDraw;

    public unsafe static delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int> PEnd;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_POINT_2F, D2D1_POINT_2F, nint, float, nint, void> PLine;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_ROUNDED_RECT*, nint, void> PFillRound;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_ROUNDED_RECT*, nint, float, nint, void> PStrokeRound;

    public unsafe static delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int> PCreateBmpWic;

    public unsafe static delegate* unmanaged[Stdcall]<nint, nint, D2D1_RECT_F*, float, int, nint, nint, void> PDrawBmp;

    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_RECT_F*, int, void> PPushClip;

    public static nint FmtUi;

    /// 工具栏专用固定字号(不随 Ctrl+滚轮缩放)
    public static nint FmtToolbar;

    public unsafe static delegate* unmanaged[Stdcall]<nint, void> PPopClip;
    // U6a：DrawEllipse(20) / FillEllipse(21)
    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_ELLIPSE*, nint, float, nint, void> PDrawEllipse;
    public unsafe static delegate* unmanaged[Stdcall]<nint, D2D1_ELLIPSE*, nint, void> PFillEllipse;

    internal static nint FmtH1;

    internal static nint FmtH2;

    internal static nint FmtH3;

    internal static nint FmtH4;

    internal static nint FmtSeal;   // U3:H2 方印数字(仅 desk)

    internal static nint FmtQuote;  // U3b:引用正文(仅 desk)

    internal static nint FmtFooter, FmtFooterSerif;   // U4:页脚
    internal static nint FmtJuanHeading, FmtJuanL1, FmtJuanL2, FmtJuanL3, FmtJuanNo, FmtJuanNoBold, FmtJuanTag, FmtJuanPage, FmtJuanPageBold;   // U8：目录卷目
    internal static nint FmtEditMode, FmtEditKey;   // U9a：编辑条「编辑中」半粗 12.5、键帽 10.5
    internal static nint FmtDeskUi, FmtTip, FmtTipKey;   // U6a2：提示 12、键帽 10   // U6a：desk 顶栏文字 12.5，不随缩放

    public static nint FmtBody;

    internal static nint FmtMono;

    // KP 断行的单字符宽度缓存:键=字符,值=当前 FmtBody 字号下的宽度(仅 plain 无样式测量,确定性)。
    // 字号变化(RebuildFormats/SetBodySize)时清空;串级宽度不缓存(字距微调会差异)。
    internal static readonly Dictionary<char, float> CharacterWidths = new();

    // 顶栏高：classic 36；desk 合并标题栏 44（U6b）。开关只在启动时读取，窗口创建前由宿主定下，运行中不变。
    public const float ClassicToolH = 36f;
    public static float ToolH { get; private set; } = ClassicToolH;
    public static void UseChrome(bool desk) { ToolH = desk ? DeskMetrics.TitleBarHeight : ClassicToolH; DeskChrome = desk; }
    public static bool DeskChrome { get; private set; }

    public static nint BSel = IntPtr.Zero;

    // ---------- 主题 ----------
    // 试用修复：分段按钮的选中底。演示里灯下的选中底（paper #1c1a18）与分段底（desk-2 #1b1a17）几乎同色，看不出选了哪个；
    // 深色纸张改用略亮的 Surface（= 代码块底，paper-2），浅色纸张仍是纸色。
    public static nint BSegOn => ActivePaper is PaperColors segPaper && segPaper.Dark && !HighContrast ? BCode : BCanvas;
    public static bool DarkMode { get; private set; }
    public static bool HighContrast { get; private set; }
    internal static SourceColorTheme SourceTheme => ActivePaper is PaperColors paper && !HighContrast
        ? MarkdownSourceColors.CreateTheme(paper)
        : MarkdownSourceColors.CreateTheme(DarkMode, HighContrast, HighContrast ? GetSysColor(5) : 0u, HighContrast ? GetSysColor(8) : 0u);

    // U2（ADR-0003）：desk 界面选定的纸张。null 表示 classic 界面。
    public static PaperColors? DeskPaper { get; private set; }
    // 实际生效的纸张：打印时临时换成 v2.2 浅色，此值为 null；高对比度时不使用纸张。
    public static PaperColors? ActivePaper { get; private set; }
    public static void SetPaper(PaperColors? paper) => DeskPaper = paper;

    // ID2D1RenderTarget::SetTransform(30)。U3 方印的微倾用它，画完必须还原为单位矩阵。
    public static unsafe void SetTransform(D2D1_MATRIX_3X2_F* matrix)
        => ((delegate* unmanaged[Stdcall]<nint, D2D1_MATRIX_3X2_F*, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)30 * (nint)sizeof(nint))))(Rt, matrix);

    // U2b：纸纹与灯下暖光（规格 §1）。位图首次使用时由 PaperTexture 生成，随渲染目标一起释放。
    static nint _grainLight, _grainDark, _lampGlow;

    static unsafe nint TextureBitmap(ref nint slot, Func<uint[]> pixels, int width, int height)
    {
        if (slot != 0 || Rt == IntPtr.Zero) return slot;
        uint[] data = pixels();
        fixed (uint* p = data) { if (CreateBitmapFromMemory((uint)width, (uint)height, (nint)p, (uint)(width * 4), out slot) < 0) slot = 0; }
        return slot;
    }

    // 纸纹铺在正文之下，固定在视口上（不随滚动）；高对比度与打印时不画。
    public static unsafe void PaintPaperGrain(float left, float top, float right, float bottom)
    {
        if (ActivePaper is not PaperColors p || HighContrast || p.Grain <= 0f) return;
        int n = PaperTexture.GrainSize;
        nint bmp = p.Dark ? TextureBitmap(ref _grainDark, () => PaperTexture.Grain(true), n, n) : TextureBitmap(ref _grainLight, () => PaperTexture.Grain(false), n, n);
        if (bmp == 0) return;
        for (float y = top; y < bottom; y += n)
            for (float x = left; x < right; x += n)
            {
                D2D1_RECT_F r = new D2D1_RECT_F { left = x, top = y, right = x + n, bottom = y + n };
                PDrawBmp(Rt, bmp, &r, p.Grain, 1, IntPtr.Zero, IntPtr.Zero);
            }
    }

    // 灯下的顶部暖光与暗角盖在正文之上（演示 z-index 25），小图拉伸到视口。
    public static unsafe void PaintLampGlow(float left, float top, float right, float bottom)
    {
        if (ActivePaper is not PaperColors p || HighContrast || !p.Dark) return;
        nint bmp = TextureBitmap(ref _lampGlow, PaperTexture.LampGlow, PaperTexture.GlowWidth, PaperTexture.GlowHeight);
        if (bmp == 0) return;
        D2D1_RECT_F r = new D2D1_RECT_F { left = left, top = top, right = right, bottom = bottom };
        PDrawBmp(Rt, bmp, &r, 1f, 1, IntPtr.Zero, IntPtr.Zero);
    }
    public static D2D1_COLOR_F CanvasColor;   // PClear 背景

    public struct ThemeColors
    {
        public D2D1_COLOR_F Canvas, Ink, Code, Hair, Quote, Sel;
        public D2D1_COLOR_F Zebra;                          // 表格偶数数据行隔行底色(surfaceAlt)
        public D2D1_COLOR_F Muted, HairZone, Accent, Warn;  // 弱化文字/区域级分隔/UI 强调/未保存点
        public D2D1_COLOR_F WarnText, ControlBorder;   // A1 建位(v2.2 §2.1):文字警示/必要控件边界,画笔接线归 A2/A3
        public D2D1_COLOR_F Keyword, Str, Comment, Num;   // 代码高亮 token 色
        public D2D1_COLOR_F Mark;                          // ==高亮== 背景
        public D2D1_COLOR_F Hit;                           // 搜索全部命中背景
    }

    public static ThemeColors LightTheme;
    public static ThemeColors DarkTheme;

    static void InitThemes()
    {
        // v2.2「安静的书桌」色表(唯一真值 ThemePalette):Quote 占位沿用 Hairline(提示块轨道走 Warn/Accent)。
        LightTheme = new ThemeColors
        {
            Canvas = C(ThemePalette.CanvasL),
            Ink = C(ThemePalette.InkL),
            Code = C(ThemePalette.SurfaceL),
            Hair = C(ThemePalette.HairlineL),
            Quote = C(ThemePalette.HairlineL),
            Sel = C(ThemePalette.SelectionL),
            Zebra = C(ThemePalette.SurfaceAltL),
            Muted = C(ThemePalette.MutedL),
            HairZone = C(ThemePalette.HairlineZoneL),
            Accent = C(ThemePalette.AccentL),
            Warn = C(ThemePalette.WarnL),
            WarnText = C(ThemePalette.WarnTextL),
            ControlBorder = C(ThemePalette.ControlBorderL),
            Keyword = C(ThemePalette.KeywordL),
            Str = C(ThemePalette.StringL),
            Comment = C(ThemePalette.CommentL),
            Num = C(ThemePalette.NumberL),
            Mark = C(ThemePalette.MarkL),
            Hit = C(ThemePalette.HitL)
        };
        DarkTheme = new ThemeColors
        {
            Canvas = C(ThemePalette.CanvasD),
            Ink = C(ThemePalette.InkD),
            Code = C(ThemePalette.SurfaceD),
            Hair = C(ThemePalette.HairlineD),
            Quote = C(ThemePalette.HairlineD),
            Sel = C(ThemePalette.SelectionD),
            Zebra = C(ThemePalette.SurfaceAltD),
            Muted = C(ThemePalette.MutedD),
            HairZone = C(ThemePalette.HairlineZoneD),
            Accent = C(ThemePalette.AccentD),
            Warn = C(ThemePalette.WarnD),
            WarnText = C(ThemePalette.WarnTextD),
            ControlBorder = C(ThemePalette.ControlBorderD),
            Keyword = C(ThemePalette.KeywordD),
            Str = C(ThemePalette.StringD),
            Comment = C(ThemePalette.CommentD),
            Num = C(ThemePalette.NumberD),
            Mark = C(ThemePalette.MarkD),
            Hit = C(ThemePalette.HitD)
        };
    }

    // v3 纸张到绘制色槽的映射（规格 §1.1）。引用条用 rule-2，其余语义与 v2.2 同名槽一致。
    internal static ThemeColors FromPaper(PaperColors p) => new()
    {
        Canvas = C(p.Canvas),
        Ink = C(p.Ink),
        Code = C(p.Surface),
        Hair = C(p.Hairline),
        Quote = C(p.HairlineZone),
        Sel = C(p.Selection),
        Zebra = C(p.SurfaceAlt),
        Muted = C(p.Muted),
        HairZone = C(p.HairlineZone),
        Accent = C(p.Accent),
        Warn = C(p.Warn),
        WarnText = C(p.WarnText),
        ControlBorder = C(p.ControlBorder),
        Keyword = C(p.Keyword),
        Str = C(p.String),
        Comment = C(p.Comment),
        Num = C(p.Number),
        Mark = C(p.Mark),
        Hit = C(p.Hit)
    };

    static D2D1_COLOR_F C(uint rgb) => C(ThemePalette.R(rgb), ThemePalette.G(rgb), ThemePalette.B(rgb));

    static D2D1_COLOR_F C(float r, float g, float b)
    {
        D2D1_COLOR_F c = default(D2D1_COLOR_F);
        c.r = r; c.g = g; c.b = b; c.a = 1f;
        return c;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIGHCONTRAST { public uint cbSize, dwFlags; public nint scheme; }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint flags);
    [DllImport("user32.dll")]
    static extern uint GetSysColor(int index);
    static bool IsHighContrastEnabled()
    {
        var value = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        return SystemParametersInfo(0x0042, value.cbSize, ref value, 0) && (value.dwFlags & 1) != 0;
    }
    static D2D1_COLOR_F SystemColor(int index)
    {
        uint c = GetSysColor(index);
        return C((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);
    }
    static ThemeColors HighContrastTheme()
    {
        var window = SystemColor(5);
        var text = SystemColor(8);
        var button = SystemColor(15);
        var shadow = SystemColor(16);
        var highlight = SystemColor(13);
        var hot = SystemColor(26);
        return new ThemeColors
        {
            Canvas = window,
            Ink = text,
            Code = button,
            Hair = shadow,
            Quote = shadow,
            Sel = highlight,
            Zebra = button,
            Muted = SystemColor(17),
            HairZone = shadow,
            Accent = hot,
            Warn = text,
            WarnText = text,
            ControlBorder = shadow,
            Keyword = text,
            Str = text,
            Comment = text,
            Num = text,
            Mark = highlight,
            Hit = highlight
        };
    }

    // SetColor on ID2D1SolidColorBrush (slot 8).
    internal static void SetBrushRgb(nint brush, uint rgb) => SetBrushColor(brush, C(rgb));   // U9c：临时换色（用完由调用方换回）
    static void SetBrushColor(nint brush, D2D1_COLOR_F color)
    {
        ((delegate* unmanaged[Stdcall]<nint, D2D1_COLOR_F*, void>)(*(IntPtr*)((nint)(*(IntPtr*)brush) + (nint)8 * (nint)sizeof(nint))))(brush, &color);
    }

    // desk 界面下调用方传入 DeskPaper.Dark，得到纸张配色；classic 下与原先一致。
    public static void ApplyTheme(bool dark) => Apply(dark, DeskPaper is PaperColors p && p.Dark == dark ? p : null);

    // 打印与导出用的 v2.2 配色，不跟随纸张（ADR-0003 第 8 条）。之后用 ApplyTheme(dark) 恢复。
    public static void ApplyClassicTheme(bool dark) => Apply(dark, null);

    static void Apply(bool dark, PaperColors? paper)
    {
        DarkMode = dark;
        HighContrast = IsHighContrastEnabled();
        ActivePaper = paper;
        var t = HighContrast ? HighContrastTheme() : paper is PaperColors p ? FromPaper(p) : (dark ? DarkTheme : LightTheme);
        SetBrushColor(BInk, t.Ink);
        SetBrushColor(BCode, t.Code);
        SetBrushColor(BHair, t.Hair);
        SetBrushColor(BQuote, t.Quote);
        SetBrushColor(BSel, t.Sel);
        SetBrushColor(BCanvas, t.Canvas);
        if (BKeyword != IntPtr.Zero)
        {
            SetBrushColor(BKeyword, t.Keyword);
            SetBrushColor(BString, t.Str);
            SetBrushColor(BComment, t.Comment);
            SetBrushColor(BNumber, t.Num);
            SetBrushColor(BMark, t.Mark);
            SetBrushColor(BHit, t.Hit);
            SetBrushColor(BAccent, t.Accent);
            SetBrushColor(BZebra, t.Zebra);
            SetBrushColor(BMuted, t.Muted);
            SetBrushColor(BHairZone, t.HairZone);
            SetBrushColor(BWarn, t.Warn);
            SetBrushColor(BControlBorder, t.ControlBorder);
        }
        if (BDesk != IntPtr.Zero)
        {
            bool deskPaper = paper is PaperColors && !HighContrast;
            var dp = paper.GetValueOrDefault();
            SetBrushColor(BDesk, deskPaper ? C(dp.Desk) : t.Canvas);
            SetBrushColor(BDesk2, deskPaper ? C(dp.Desk2) : t.Code);
            SetBrushColor(BAccentSoft, deskPaper ? C(dp.AccentSoft) : t.Sel);
            SetBrushColor(BDeskHover, deskPaper ? C(dp.DeskHover) : t.Code);
            SetBrushColor(BSurfaceHover, deskPaper ? C(dp.SurfaceHover) : t.Code);
            SetBrushColor(BAccentHover, deskPaper ? C(dp.AccentHover) : t.Sel);
        }
        if (BFaint != IntPtr.Zero) SetBrushColor(BFaint, paper is PaperColors faintPaper && !HighContrast ? C(faintPaper.Faint) : t.Muted);
        if (BInk2 != IntPtr.Zero) SetBrushColor(BInk2, paper is PaperColors inkPaper && !HighContrast ? C(inkPaper.Ink2) : t.Ink);
        if (BEdge != IntPtr.Zero) SetBrushColor(BEdge, paper is PaperColors edgePaper && !HighContrast ? C(edgePaper.Edge) : t.Hair);
        if (BShadow != IntPtr.Zero) SetBrushColor(BShadow, paper is PaperColors shadowPaper ? C(shadowPaper.Shadow) : C(0f, 0f, 0f));
        ShadowAlpha = paper is PaperColors alphaPaper && !HighContrast ? alphaPaper.ShadowAlpha : 0f;
        CanvasColor = t.Canvas;
        SurfaceRef = ColorRef(t.Code);
        MutedRef = ColorRef(t.Muted);
        _mixInk = t.Ink; _mixMuted = t.Muted;
        _mixInk2 = paper is PaperColors mixPaper && !HighContrast ? C(mixPaper.Ink2) : t.Ink;
    }

    // U6c：q = 0 为常态，1 为退后。
    public static void MixQuiet(float q)
    {
        if (BQuietIdle == IntPtr.Zero) return;
        q = Math.Clamp(q, 0f, 1f);
        SetBrushColor(BQuietIdle, Lerp(_mixInk2, _mixMuted, q));
        SetBrushColor(BQuietOn, Lerp(_mixInk, _mixInk2, q));
    }

    static D2D1_COLOR_F Lerp(D2D1_COLOR_F a, D2D1_COLOR_F b, float t) =>
        new() { r = a.r + (b.r - a.r) * t, g = a.g + (b.g - a.g) * t, b = a.b + (b.b - a.b) * t, a = a.a + (b.a - a.a) * t };

    public static nint BKeyword;
    public static nint BAccent;   // UI 强调色(进度线/引用条/TOC活动/行号当前行/下划线),与语法关键字刷分离
    public static nint BZebra;   // 表格隔行底色
    public static nint BMuted;     // 弱化文字(TOC未激活/行号/计数/DrawHint)
    public static nint BHairZone;  // 区域级分隔(TOC 右缘等,chrome §2)
    public static nint BWarn;      // 未保存圆点
    public static nint BControlBorder;   // 必要控件边界(A3 查找面板描边;高对比映射系统色)
    public static nint BString;
    public static nint BComment;
    public static nint BNumber;
    public static nint BMark;

    // 供 Lightbox 全屏窗复用 DCRT 绘制
    public unsafe static void PBindExternal(nint hdc, RECT* r)
    {
        PBind(Rt, hdc, r);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)51 * (nint)sizeof(nint))))(Rt, 96f, 96f);
    }

    public unsafe static void PFillExternal(float x1, float y1, float x2, float y2, nint brush)
    {
        D2D1_RECT_F r = new D2D1_RECT_F { left = x1, top = y1, right = x2, bottom = y2 };
        PFill(Rt, &r, brush);
    }

    public unsafe static void PDrawBmpExternal(nint bmp, float x, float y, float w, float h)
    {
        D2D1_RECT_F r = new D2D1_RECT_F { left = x, top = y, right = x + w, bottom = y + h };
        PDrawBmp(Rt, bmp, &r, 1f, 1, IntPtr.Zero, IntPtr.Zero);
    }

    // ID2D1RenderTarget::CreateBitmap(slot 4):预乘 BGRA 内存 → 位图;动画帧使用
    public unsafe static int CreateBitmapFromMemory(uint width, uint height, nint pixels, uint pitch, out nint bitmap)
    {
        bitmap = 0;
        if (Rt == IntPtr.Zero) return unchecked((int)0x8000FFFF);
        var size = new D2D1_SIZE_U { width = width, height = height };
        var props = new D2D1_BITMAP_PROPERTIES { pixelFormat = new D2D1_PIXEL_FORMAT { format = 87 /*B8G8R8A8_UNORM*/, alphaMode = 1 /*PREMULTIPLIED*/ }, dpiX = 96f, dpiY = 96f };
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, D2D1_SIZE_U, nint, uint, D2D1_BITMAP_PROPERTIES*, nint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)4 * (nint)sizeof(nint))))(Rt, size, pixels, pitch, &props, &result);
        bitmap = result;
        return hr;
    }
    public static nint BHit;

    // ID2D1Brush::SetOpacity(4)。用完请复位为 1。
    internal unsafe static void SetOpacity(nint brush, float opacity)
        => ((delegate* unmanaged[Stdcall]<nint, float, void>)(*(nint**)brush)[4])(brush, opacity);

    private unsafe static nint MkBrush(D2D1_COLOR_F c)
    {
        var props = default(D2D1_BRUSH_PROPERTIES);
        props.opacity = 1f;
        nint b = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<nint, D2D1_COLOR_F*, D2D1_BRUSH_PROPERTIES*, nint*, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)8 * (nint)sizeof(nint))))(Rt, &c, &props, &b);
        return b;
    }

    private static void Initialize(float scale)
    {
        Scale = scale;
        uint hr = 0u;
        int num = D2D.MD_CreateD2D(96f * scale, 96f * scale, out Rt, out BInk, out BCode, out BHair, out BQuote, out hr);
        if (num != 0)
        {
            throw new Exception($"MD_CreateD2D stage={num} hr=0x{hr:X8}");
        }
        PBind = (delegate* unmanaged[Stdcall]<nint, nint, RECT*, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)57 * (nint)sizeof(nint)));
        PBegin = (delegate* unmanaged[Stdcall]<nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)48 * (nint)sizeof(nint)));
        PClear = (delegate* unmanaged[Stdcall]<nint, D2D1_COLOR_F*, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)47 * (nint)sizeof(nint)));
        PFill = (delegate* unmanaged[Stdcall]<nint, D2D1_RECT_F*, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)17 * (nint)sizeof(nint)));
        PDraw = (delegate* unmanaged[Stdcall]<nint, D2D1_POINT_2F, nint, nint, int, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)28 * (nint)sizeof(nint)));
        PEnd = (delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)49 * (nint)sizeof(nint)));
        PLine = (delegate* unmanaged[Stdcall]<nint, D2D1_POINT_2F, D2D1_POINT_2F, nint, float, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)15 * (nint)sizeof(nint)));
        PFillRound = (delegate* unmanaged[Stdcall]<nint, D2D1_ROUNDED_RECT*, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)19 * (nint)sizeof(nint)));
        PStrokeRound = (delegate* unmanaged[Stdcall]<nint, D2D1_ROUNDED_RECT*, nint, float, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)18 * (nint)sizeof(nint)));
        PCreateBmpWic = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)5 * (nint)sizeof(nint)));
        PDrawBmp = (delegate* unmanaged[Stdcall]<nint, nint, D2D1_RECT_F*, float, int, nint, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)26 * (nint)sizeof(nint)));
        PPushClip = (delegate* unmanaged[Stdcall]<nint, D2D1_RECT_F*, int, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)45 * (nint)sizeof(nint)));
        PPopClip = (delegate* unmanaged[Stdcall]<nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)46 * (nint)sizeof(nint)));
        PDrawEllipse = (delegate* unmanaged[Stdcall]<nint, D2D1_ELLIPSE*, nint, float, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)20 * (nint)sizeof(nint)));
        PFillEllipse = (delegate* unmanaged[Stdcall]<nint, D2D1_ELLIPSE*, nint, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)21 * (nint)sizeof(nint)));
        Dw.Init();
        RebuildFormats();
        CharacterWidths.Clear();   // 全新图形环境:字符宽度缓存作废
        FmtToolbar = Dw.MkFormat("Segoe UI", 400u, 13f);   // 固定 13,不随缩放
        FmtFooter = Dw.MkFormat("Segoe UI", 400u, DeskMetrics.FooterTextSize);   // U4:页脚固定 12,不随缩放
        FmtFooterSerif = Dw.MkFormat(SerifFamily ?? "SimSun", 400u, DeskMetrics.FooterTextSize);
        FmtDeskUi = Dw.MkFormat("Segoe UI", 400u, DeskMetrics.ToolbarTextSize);
        FmtTip = Dw.MkFormat("Segoe UI", 400u, DeskTip.TextSize);
        FmtTipKey = Dw.MkFormat("Segoe UI", 400u, DeskTip.KeyTextSize);
        FmtEditMode = Dw.MkFormat("Segoe UI", 600u, EditBar.TextSize);
        FmtEditKey = Dw.MkFormat("Segoe UI", 400u, EditBar.KeySize);
        FmtJuanHeading = Dw.MkFormat(SerifFamily ?? "SimSun", 700u, Juan.TitleSize);
        FmtJuanL1 = Dw.MkFormat(SerifFamily ?? "SimSun", 700u, Juan.L1Size);
        FmtJuanL2 = Dw.MkFormat(SerifFamily ?? "SimSun", 400u, Juan.L2Size);
        FmtJuanL3 = Dw.MkFormat("Segoe UI", 400u, Juan.L3Size);
        FmtJuanNo = Dw.MkFormat(SerifFamily ?? "SimSun", 400u, Juan.NoSize);
        FmtJuanNoBold = Dw.MkFormat(SerifFamily ?? "SimSun", 700u, Juan.NoSize);
        FmtJuanTag = Dw.MkFormat("Segoe UI", 400u, Juan.TagSize);
        FmtJuanPage = Dw.MkFormat("Palatino Linotype", 400u, Juan.PageSize);
        FmtJuanPageBold = Dw.MkFormat("Palatino Linotype", 700u, Juan.PageSize);   // 页脚章节名用宋体
        InitThemes();
        BSel = MkBrush(LightTheme.Sel);
        BCanvas = MkBrush(LightTheme.Canvas);
        BKeyword = MkBrush(LightTheme.Keyword);
        BString = MkBrush(LightTheme.Str);
        BComment = MkBrush(LightTheme.Comment);
        BNumber = MkBrush(LightTheme.Num);
        BMark = MkBrush(LightTheme.Mark);
        BHit = MkBrush(LightTheme.Hit);
        BZebra = MkBrush(LightTheme.Zebra);
        BAccent = MkBrush(LightTheme.Accent);
        BMuted = MkBrush(LightTheme.Muted);
        BHairZone = MkBrush(LightTheme.HairZone);
        BWarn = MkBrush(LightTheme.Warn);
        BControlBorder = MkBrush(LightTheme.ControlBorder);
        BInk2 = MkBrush(LightTheme.Ink);
        BFaint = MkBrush(LightTheme.Muted);
        BDesk = MkBrush(LightTheme.Canvas);
        BDesk2 = MkBrush(LightTheme.Code);
        BAccentSoft = MkBrush(LightTheme.Sel);
        BDeskHover = MkBrush(LightTheme.Code);
        BSurfaceHover = MkBrush(LightTheme.Code);
        BAccentHover = MkBrush(LightTheme.Sel);
        BCloseHover = MkBrush(C(DeskFrame.CloseHover));
        BCloseGlyph = MkBrush(C(0xFFFFFF));
        BQuietIdle = MkBrush(LightTheme.Ink);
        BQuietOn = MkBrush(LightTheme.Ink);
        BShade = MkBrush(C(0f, 0f, 0f));
        BGlint = MkBrush(C(1f, 1f, 1f));
        BEdge = MkBrush(LightTheme.Hair);
        BShadow = MkBrush(C(0f, 0f, 0f));
        ApplyTheme(false);   // 全部笔刷按当前主题上色(历史字面量建刷区与 BSysClose 死代码一并清除)
    }

    // FillLine is a pixel-aligned rectangle helper. Actual diagonal strokes must
    // use DrawLine, otherwise roots, arrowheads and diamonds turn into solid boxes.
    public unsafe static void StrokeLine(float x1, float y1, float x2, float y2, nint brush, float width)
    {
        PLine(Rt, new D2D1_POINT_2F { x = x1, y = y1 }, new D2D1_POINT_2F { x = x2, y = y2 }, brush, width, 0);
    }

    public unsafe static void FillLine(float x1, float y1, float x2, float y2, nint brush, float w)
    {
        D2D1_RECT_F d2D1_RECT_F = default(D2D1_RECT_F);
        d2D1_RECT_F.left = MathF.Min(x1, x2) - ((x1 == x2) ? (w / 2f) : 0f);
        d2D1_RECT_F.top = MathF.Min(y1, y2) - ((y1 == y2) ? (w / 2f) : 0f);
        d2D1_RECT_F.right = MathF.Max(x1, x2) + ((x1 == x2) ? (w / 2f) : 0f);
        d2D1_RECT_F.bottom = MathF.Max(y1, y2) + ((y1 == y2) ? (w / 2f) : 0f);
        D2D1_RECT_F d2D1_RECT_F2 = d2D1_RECT_F;
        PFill(Rt, &d2D1_RECT_F2, brush);
    }
    private static bool _active;
    private bool _disposed;
    private static nint _controlLight, _controlDark;
    private static uint _controlLightColor, _controlDarkColor;

    public static uint SurfaceRef;      // 查找面板底色(GDI 控件用)
    public static uint MutedRef;        // U7：desk 查找框计数（GDI 控件用）

    // U7：desk 查找框计数用弱化墨色，底色同输入框。
    public static nint MutedControlColors(nint dc)
    {
        nint brush = ControlColors(dc);
        Externs.SetTextColor(dc, MutedRef);
        return brush;
    }
    static uint ColorRef(D2D1_COLOR_F c) => (uint)MathF.Round(c.r * 255f) | ((uint)MathF.Round(c.g * 255f) << 8) | ((uint)MathF.Round(c.b * 255f) << 16);
    private static nint _surfaceBrush;
    private static uint _surfaceBrushColor;

    // A3:浮动查找面板上的原生静态文字(计数)用 Surface 底,与面板一致
    public static nint SurfaceControlColors(nint dc)
    {
        Externs.SetTextColor(dc, SourceTheme.Text);
        Externs.SetBkColor(dc, SurfaceRef);
        Externs.SetBkMode(dc, 2);
        if (_surfaceBrush != 0 && _surfaceBrushColor != SurfaceRef) { Externs.DeleteObject(_surfaceBrush); _surfaceBrush = 0; }
        if (_surfaceBrush == 0) { _surfaceBrush = Externs.CreateSolidBrush(SurfaceRef); _surfaceBrushColor = SurfaceRef; }
        return _surfaceBrush;
    }

    public static nint ControlColors(nint dc) => ControlColors(dc, SourceTheme);

    internal static nint ControlColors(nint dc, SourceColorTheme theme)
    {
        Externs.SetTextColor(dc, theme.Text);
        Externs.SetBkColor(dc, theme.Background);
        Externs.SetBkMode(dc, 2);
        ref nint brush = ref (theme.Dark ? ref _controlDark : ref _controlLight);
        ref uint color = ref (theme.Dark ? ref _controlDarkColor : ref _controlLightColor);
        // System colors may change without changing DarkMode.
        if (brush != 0 && color != theme.Background) { Externs.DeleteObject(brush); brush = 0; }
        if (brush == 0) { brush = Externs.CreateSolidBrush(theme.Background); color = theme.Background; }
        return brush;
    }

    public RenderResources(float scale, float bodySize = 16f, string serifFamily = null, bool serif = false, ReadingStyle reading = null)
    {
        if (_active) throw new InvalidOperationException("Graphics already belong to an active UI scope.");
        _active = true;
        Reading = reading ?? ReadingStyle.Classic;
        BodySize = bodySize;
        SerifFamily = serifFamily;
        SerifMode = serif && serifFamily != null;
        try { Initialize(scale); }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_controlLight != 0) Externs.DeleteObject(_controlLight);
        if (_controlDark != 0) Externs.DeleteObject(_controlDark);
        _controlLight = _controlDark = 0;
        Release(ref BInk);
        Release(ref BCode);
        Release(ref BHair);
        Release(ref BQuote);
        Release(ref BCanvas);
        Release(ref FmtToolbar);
        Release(ref FmtFooter);
        Release(ref FmtFooterSerif);
        Release(ref FmtDeskUi);
        Release(ref FmtTip);
        Release(ref FmtTipKey);
        Release(ref FmtEditMode);
        Release(ref FmtEditKey);
        Release(ref FmtJuanHeading);
        Release(ref FmtJuanL1);
        Release(ref FmtJuanL2);
        Release(ref FmtJuanL3);
        Release(ref FmtJuanNo);
        Release(ref FmtJuanNoBold);
        Release(ref FmtJuanTag);
        Release(ref FmtJuanPage);
        Release(ref FmtJuanPageBold);
        ReleaseFormats();
        Release(ref BSel);
        Release(ref BKeyword);
        Release(ref BAccent);
        Release(ref BZebra);
        Release(ref BMuted);
        Release(ref BHairZone);
        Release(ref BWarn);
        Release(ref BControlBorder);
        Release(ref BString);
        Release(ref BComment);
        Release(ref BNumber);
        Release(ref BMark);
        Release(ref BHit);
        Release(ref BInk2);
        Release(ref BFaint);
        Release(ref BDesk);
        Release(ref BDesk2);
        Release(ref BAccentSoft);
        Release(ref BDeskHover);
        Release(ref BSurfaceHover);
        Release(ref BAccentHover);
        Release(ref BCloseHover);
        Release(ref BCloseGlyph);
        Release(ref BQuietIdle);
        Release(ref BQuietOn);
        Release(ref BShade);
        Release(ref BGlint);
        Release(ref BEdge);
        Release(ref BShadow);
        Release(ref _grainLight);
        Release(ref _grainDark);
        Release(ref _lampGlow);
        Release(ref Rt);
        Dw.Shutdown();
        CharacterWidths.Clear();
        Reading = ReadingStyle.Classic;
        _active = false;
    }

    private static void Release(ref nint resource)
    {
        if (resource == 0) return;
        Marshal.Release(resource);
        resource = 0;
    }
}
