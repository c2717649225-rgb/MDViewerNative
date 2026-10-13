using System;
using MDViewerNative;

// U9a：编辑条几何（演示稿 .editbar）、编辑模式页脚与源码行号。
internal static class DeskEditBarTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "desk edit bar") => check("DeskEditBar/" + name, ok, detail);
        var t = new EditBarText(Mode: 40f, StateFull: 180f, StateShort: 80f, Save: 25f, SaveKey: 30f, Done: 25f, DoneKey: 30f);
        var l = EditBar.Layout(1200f, 44f, t);
        C("bar sits under the toolbar, 44 high, full width", l.Bar.Y == 44f && l.Bar.H == 44f && l.Bar.W == 1200f);
        C("no key: the button only wraps its label", EditBar.ButtonWidth(25f, 0f) == 12f * 2f + 25f);
        C("key cap is text plus .45em sides and borders", MathF.Abs(EditBar.KeyWidth(30f) - (30f + 2f * 0.45f * 10.5f + 2f)) < 0.01f);
        float bw = 12f * 2f + 25f + 8f + EditBar.KeyWidth(30f);
        C("done ends at the 48 inset", l.Done.Right == 1200f - 48f && MathF.Abs(l.Done.W - bw) < 0.01f, $"{l.Done}");
        C("save sits 10 left of done", MathF.Abs(l.Save.Right + 10f - l.Done.X) < 0.01f);
        C("buttons are 28 high and centered", l.Save.H == 28f && l.Save.Y == 44f + 8f);
        C("icon at the 48 inset, label after 14 + 6", l.IconX == 48f && l.ModeX == 48f + 14f + 6f);
        C("state follows the label with gap 10", l.StateDotX == l.ModeX + 40f + 10f && l.StateX == l.StateDotX + 7f + 6f);
        C("wide window keeps the backup note", l.State == EditBarState.Full);
        C("medium window drops the note", EditBar.Layout(560f, 44f, t).State == EditBarState.Short);
        C("narrow window hides the state", EditBar.Layout(420f, 44f, t).State == EditBarState.Hidden);
        C("key text starts after label and gap 8", MathF.Abs(l.SaveKeyX - (l.Save.X + 12f + 25f + 8f)) < 0.01f);
        C("hit save, done, none", EditBar.Hit(l, l.Save.X + 2f, l.Save.Y + 2f) == EditBarPart.Save && EditBar.Hit(l, l.Done.Right - 1f, l.Done.Bottom - 1f) == EditBarPart.Done
            && EditBar.Hit(l, l.Save.Right + 5f, l.Save.Y + 5f) == EditBarPart.None && EditBar.Hit(l, 300f, 60f) == EditBarPart.None);
        C("state text", EditBar.StateText(false, EditBarState.Full) == "已保存" && EditBar.StateText(true, EditBarState.Full) == "有改动未保存 · 草稿已自动备份"
            && EditBar.StateText(true, EditBarState.Short) == "有改动未保存" && EditBar.StateText(true, EditBarState.Hidden) == "");

        var f = FooterModel.Editing(12, "Markdown · UTF-8", 1f);
        C("edit footer: fish and line", f.Lead == FooterLead.Fish && f.Left == "第 12 行" && f.Right.Count == 1 && f.Right[0].Text == "Markdown · UTF-8");
        var g = FooterModel.Editing(0, "Markdown · GBK", 1.25f, "已保存");
        C("edit footer: status first, zoom last, line at least 1", g.Left == "第 1 行" && g.Right.Count == 3 && g.Right[0].Accent && g.Right[2].Text == "125%");

        var coords = new EditorCoordinates("ab\r\ncd\r\n\r\nef");   // RichEdit 原生：ab␍cd␍␍ef
        C("source line of native offsets", coords.LineOfNative(0) == 1 && coords.LineOfNative(2) == 1 && coords.LineOfNative(3) == 2 && coords.LineOfNative(6) == 3 && coords.LineOfNative(7) == 4);
    }
}
