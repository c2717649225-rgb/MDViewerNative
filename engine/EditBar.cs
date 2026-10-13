using System;

namespace MDViewerNative;

// U9a：编辑条（演示稿 .editbar，规格 §8）。纯层：文字、按钮几何、窄窗取舍与命中；绘制在 EditBarView。
// 坐标为窗口 DIP；条带在顶栏之下、查找条之上，44 高，全窗宽，左右内边距 48（与正文边距一致）。
internal enum EditBarPart { None, Save, Done }

internal readonly record struct EditBarBox(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float x, float y) => W > 0f && x >= X && x < Right && y >= Y && y < Bottom;
}

// Measure：各段文字宽（由视图用 DWrite 量出）。
internal readonly record struct EditBarText(float Mode, float StateFull, float StateShort, float Save, float SaveKey, float Done, float DoneKey);

internal readonly record struct EditBarLayout(EditBarBox Bar, float IconX, float ModeX, float StateDotX, float StateX, EditBarState State, EditBarBox Save, EditBarBox Done, float SaveKeyX, float DoneKeyX);

internal enum EditBarState { Full, Short, Hidden }

internal static class EditBar
{
    public const float Height = 44f, InsetX = 48f, Gap = 10f;
    public const float Icon = 14f, IconGap = 6f, Dot = 6f, Ring = 7f, RingStroke = 1.3f, DotGap = 6f;
    public const float ButtonH = 28f, ButtonInsetX = 12f, ButtonRadius = 7f, KeyGap = 8f;
    public const float TextSize = 12.5f, KeySize = 10.5f, KeyInsetX = 0.45f * KeySize, KeyInsetY = 0.05f * KeySize, KeyRadius = 5f;
    public const float DisabledOpacity = 0.45f;
    public const string Mode = "编辑中", Saved = "已保存", Unsaved = "有改动未保存", Backed = " · 草稿已自动备份", Done = "完成";
    public const string SaveTip = "保存到文件", DoneTip = "回到阅读，改动会保留";

    public static string StateText(bool dirty, EditBarState fit) => fit == EditBarState.Hidden ? "" : !dirty ? Saved : fit == EditBarState.Full ? Unsaved + Backed : Unsaved;

    public static float KeyWidth(float textW) => textW + 2f * KeyInsetX + 2f;   // 左右 1 边框
    // keyW 为 0：不带键帽（试用反馈：按钮上不再写快捷键），按钮只包文字。
    public static float ButtonWidth(float textW, float keyW) => ButtonInsetX * 2f + textW + (keyW > 0f ? KeyGap + KeyWidth(keyW) : 0f);

    public static EditBarLayout Layout(float viewW, float top, EditBarText t)
    {
        var bar = new EditBarBox(0f, top, viewW, Height);
        float by = top + (Height - ButtonH) / 2f;
        float doneW = ButtonWidth(t.Done, t.DoneKey), saveW = ButtonWidth(t.Save, t.SaveKey);
        var done = new EditBarBox(viewW - InsetX - doneW, by, doneW, ButtonH);
        var save = new EditBarBox(done.X - Gap - saveW, by, saveW, ButtonH);
        float iconX = InsetX, modeX = iconX + Icon + IconGap, dotX = modeX + t.Mode + Gap, stateX = dotX + Ring + DotGap;
        float room = save.X - Gap - stateX;
        var fit = room >= t.StateFull ? EditBarState.Full : room >= t.StateShort ? EditBarState.Short : EditBarState.Hidden;
        return new(bar, iconX, modeX, dotX, stateX, fit, save, done,
            save.X + ButtonInsetX + t.Save + KeyGap, done.X + ButtonInsetX + t.Done + KeyGap);
    }

    public static EditBarPart Hit(EditBarLayout l, float x, float y) =>
        l.Save.Contains(x, y) ? EditBarPart.Save : l.Done.Contains(x, y) ? EditBarPart.Done : EditBarPart.None;
}
