namespace MDViewerNative;

// v2.2「安静的书桌」色彩唯一真值(docs/mcp-design-direction.md §2.1–2.2,十六进制 sRGB)。
// RenderResources.InitThemes(D2D 浮点)、WindowChrome(DWM COLORREF/BGR)、ControlColors(GDI)
// 必须共用本表,禁止第二份漂移色表;高对比度不走本表(系统色,见 RenderResources.HighContrastTheme)。
// 对比度按 §2.3 口径复算 18 组色对 × 双主题全部达标(work/a1-b1-contrast.txt)。
internal static class ThemePalette
{
    public const uint CanvasL = 0xF9F8F5, CanvasD = 0x181716;
    public const uint SurfaceL = 0xEFECE5, SurfaceD = 0x242220;
    public const uint SurfaceAltL = 0xF4F2EB, SurfaceAltD = 0x201E1B;
    public const uint HairlineL = 0xE2DFD7, HairlineD = 0x3B3833;
    public const uint HairlineZoneL = 0xD4CFC5, HairlineZoneD = 0x49453F;
    public const uint ControlBorderL = 0x8A8479, ControlBorderD = 0x8A8378;
    public const uint InkL = 0x1E1C1A, InkD = 0xD6D3CB;
    public const uint MutedL = 0x6D685E, MutedD = 0x948E83;
    public const uint AccentL = 0x28557F, AccentD = 0x7CA4CC;
    public const uint SelectionL = 0xD8E5F2, SelectionD = 0x2A4463;
    public const uint WarnL = 0xB87318, WarnD = 0xDEA243;
    public const uint WarnTextL = 0x8A5310, WarnTextD = 0xDEA243;
    public const uint MarkL = 0xF5E5B8, MarkD = 0x494025;
    public const uint HitL = 0xFAD48E, HitD = 0x64491C;
    public const uint KeywordL = 0x28557F, KeywordD = 0x7CA4CC;
    public const uint StringL = 0x9C4532, StringD = 0xD2735F;
    public const uint CommentL = 0x536E4E, CommentD = 0x7E9F77;
    public const uint NumberL = 0x85611F, NumberD = 0xCE9F4E;

    public static float R(uint rgb) => ((rgb >> 16) & 0xFF) / 255f;

    public static float G(uint rgb) => ((rgb >> 8) & 0xFF) / 255f;

    public static float B(uint rgb) => (rgb & 0xFF) / 255f;

    // sRGB(0xRRGGBB) → Win32 COLORREF(0x00BBGGRR),字节序由 SelfTest 断言执法
    public static uint ToColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00u) | ((rgb >> 16) & 0xFF);
}

// ---------- v3「安静的书桌」纸张（docs/design/quiet-desk-spec.md §1；ADR-0003）----------
// 与上方 v2.2 色表并存：ChromeStyle.Desk 用本表，Classic 仍用上表（ADR-0003 第 1 条）。
// 半透明的设计色（选区、查找底色、提示块底色）在这里预先与纸面合成为不透明色，绘制端不做混色。
// 代码注释等少数颜色较演示加深到 ≥4.5:1（规格 §1 对比度红线），演示已同步。
internal enum Paper { Auto = 0, Xuan = 1, Celadon = 2, White = 3, Lamp = 4 }

internal readonly record struct PaperColors(
    bool Dark,
    uint Desk, uint Desk2, uint Canvas, uint Surface, uint SurfaceAlt,
    uint Hairline, uint HairlineZone, uint ControlBorder,
    uint Ink, uint Ink2, uint Muted, uint Faint,
    uint Accent, uint AccentSoft, uint Selection, uint Hit, uint Mark,
    uint Warn, uint WarnText,
    uint Keyword, uint Type, uint String, uint Number, uint Comment,
    float Grain)
{
    // U10：卡片的边与投影（演示 --edge、--shadow）。
    public uint Edge { get; init; }
    public uint Shadow { get; init; }
    public float ShadowAlpha { get; init; }

    // 试用反馈：悬停底。暗纸上 desk-2 / paper-2 与底色只差 7–9 级，悬停几乎看不出；暗纸改为向墨色混 10%（约 20 级），浅纸沿用演示值。
    public uint DeskHover => Dark ? PaperPalette.Over(Ink, HoverInk, Desk) : Desk2;
    public uint SurfaceHover => Dark ? PaperPalette.Over(Ink, HoverInk, Canvas) : Surface;
    public const float HoverInk = 0.10f;
    // 朱色悬停 / 选中行（目录悬停、命令面板当前行）：暗纸上 10% 朱色同样太淡，加到 18%。
    public uint AccentHover => Dark ? PaperPalette.Over(Accent, 0.18f, Canvas) : AccentSoft;
}

internal static class PaperPalette
{
    public static readonly PaperColors Xuan = new(false,
        Desk: 0xEBE4D6, Desk2: 0xE1D9C9, Canvas: 0xF6F1E7, Surface: 0xEDE6D8, SurfaceAlt: 0xF2ECE0,
        Hairline: 0xE2D8C6, HairlineZone: 0xCFC3AD, ControlBorder: 0x918778,
        Ink: 0x2A2521, Ink2: 0x544C43, Muted: 0x6C6358, Faint: 0xB3A894,
        Accent: 0xB0361F, AccentSoft: Over(0xB0361F, 0.08f, 0xF6F1E7), Selection: Over(0xB0361F, 0.16f, 0xF6F1E7),
        Hit: Over(0xD6783C, 0.22f, 0xF6F1E7), Mark: Over(0xD6783C, 0.30f, 0xF6F1E7),
        Warn: 0xA0662A, WarnText: 0x8E5B25,
        Keyword: 0xA3402C, Type: 0x3F5F7A, String: 0x556E35, Number: 0x8E5B25, Comment: 0x6B6458,
        Grain: 1f)
    { Edge = 0xDDD3C0, Shadow = 0x462D14, ShadowAlpha = 0.2f };

    public static readonly PaperColors Celadon = new(false,
        Desk: 0xBFD0BB, Desk2: 0xB4C7B0, Canvas: 0xCDDCC9, Surface: 0xC2D3BE, SurfaceAlt: 0xC8D8C4,
        Hairline: 0xB8CAB4, HairlineZone: 0xA3B89F, ControlBorder: 0x677C6B,
        Ink: 0x1B2A23, Ink2: 0x34463D, Muted: 0x44554C, Faint: 0x8FA592,
        Accent: 0x8F2F1D, AccentSoft: Over(0x8F2F1D, 0.09f, 0xCDDCC9), Selection: Over(0x285A46, 0.20f, 0xCDDCC9),
        Hit: Over(0xBE6E32, 0.24f, 0xCDDCC9), Mark: Over(0xBE6E32, 0.32f, 0xCDDCC9),
        Warn: 0x865A1F, WarnText: 0x754E1B,
        Keyword: 0x953928, Type: 0x244F63, String: 0x36603B, Number: 0x754E1B, Comment: 0x4B5A52,
        Grain: 0.8f)
    { Edge = 0xB3C6AF, Shadow = 0x143223, ShadowAlpha = 0.22f };

    public static readonly PaperColors White = new(false,
        Desk: 0xF0F0EF, Desk2: 0xE6E6E4, Canvas: 0xFFFFFF, Surface: 0xF4F4F2, SurfaceAlt: 0xF9F9F8,
        Hairline: 0xE6E6E6, HairlineZone: 0xCFCFCF, ControlBorder: 0x8C8C8C,
        Ink: 0x141414, Ink2: 0x3D3D3D, Muted: 0x666666, Faint: 0xB0B0B0,
        Accent: 0xB0301A, AccentSoft: Over(0xB0301A, 0.07f, 0xFFFFFF), Selection: Over(0xB0301A, 0.15f, 0xFFFFFF),
        Hit: Over(0xFFBE3C, 0.35f, 0xFFFFFF), Mark: Over(0xFFBE3C, 0.45f, 0xFFFFFF),
        Warn: 0x935A14, WarnText: 0x935A14,
        Keyword: 0xA3321F, Type: 0x1F4F7A, String: 0x3D6B1F, Number: 0x935A14, Comment: 0x6E6E6E,
        Grain: 0f)
    { Edge = 0xE0E0DE, Shadow = 0x000000, ShadowAlpha = 0.16f };

    public static readonly PaperColors Lamp = new(true,
        Desk: 0x141311, Desk2: 0x1B1A17, Canvas: 0x1C1A18, Surface: 0x252320, SurfaceAlt: 0x211F1C,
        Hairline: 0x2F2C28, HairlineZone: 0x433E38, ControlBorder: 0x7A7166,
        Ink: 0xE6DCCB, Ink2: 0xBDB2A1, Muted: 0x9A8F80, Faint: 0x5A534B,
        Accent: 0xE0805F, AccentSoft: Over(0xE0805F, 0.10f, 0x1C1A18), Selection: Over(0xE0805F, 0.26f, 0x1C1A18),
        Hit: Over(0xE0805F, 0.26f, 0x1C1A18), Mark: Over(0xE0805F, 0.34f, 0x1C1A18),
        Warn: 0xDDAE70, WarnText: 0xDDAE70,
        Keyword: 0xE8957A, Type: 0x98B8CF, String: 0xAEC78C, Number: 0xDDAE70, Comment: 0x918B80,
        Grain: 0.45f)
    { Edge = 0x302C28, Shadow = 0x000000, ShadowAlpha = 0.6f };

    // 「跟随系统」：浅色 → 宣纸，深色 → 灯下。
    public static Paper Resolve(Paper choice, bool systemDark) =>
        choice == Paper.Auto ? (systemDark ? Paper.Lamp : Paper.Xuan) : choice;

    public static PaperColors Get(Paper choice, bool systemDark) => Resolve(choice, systemDark) switch
    {
        Paper.Celadon => Celadon,
        Paper.White => White,
        Paper.Lamp => Lamp,
        _ => Xuan,
    };

    public static readonly Paper[] All = { Paper.Auto, Paper.Xuan, Paper.Celadon, Paper.White, Paper.Lamp };

    // config.txt 中的写法（paper=）。未知值返回 null，调用方保留原值。
    public static string Token(Paper p) => p switch
    {
        Paper.Xuan => "xuan",
        Paper.Celadon => "celadon",
        Paper.White => "white",
        Paper.Lamp => "lamp",
        _ => "auto",
    };

    public static Paper? Parse(string token) => token switch
    {
        "auto" => Paper.Auto,
        "xuan" => Paper.Xuan,
        "celadon" => Paper.Celadon,
        "white" => Paper.White,
        "lamp" => Paper.Lamp,
        _ => null,
    };

    public static string DisplayName(Paper p) => p switch
    {
        Paper.Xuan => "宣纸",
        Paper.Celadon => "青瓷",
        Paper.White => "素白",
        Paper.Lamp => "灯下",
        _ => "跟随系统",
    };

    // 与老版本互通（ADR-0003 第 4 条）：没有 paper= 时按 theme= 推导；写回时 theme= 取最接近的深浅。
    public static Paper FromLegacy(bool? themeDark) => themeDark switch { true => Paper.Lamp, false => Paper.Xuan, null => Paper.Auto };

    public static bool LegacyDark(Paper choice, bool systemDark) => Get(choice, systemDark).Dark;

    // 前景色 fg 以 alpha 叠在 bg 上的不透明结果（sRGB 空间线性插值，与浏览器合成一致）。
    public static uint Over(uint fg, float alpha, uint bg)
    {
        static uint Ch(uint c, int shift) => (c >> shift) & 0xFF;
        uint Mix(int shift) => (uint)System.MathF.Round(Ch(fg, shift) * alpha + Ch(bg, shift) * (1f - alpha));
        return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }
}
