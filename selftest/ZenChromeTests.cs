using System;
using System.Linq;
using MDViewerNative;

// 阶段 B:显式沉浸。状态判定与命令入口;工具栏隐藏绘制在 A2ToolbarTests 中验证。
internal static class ZenChromeTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        var zen = new ZenChrome();
        check("Zen/默认关闭时从不隐藏", !zen.Hides(false, false, false), "日常模式固定显示工具栏");
        zen.Enabled = true;
        check("Zen/开启且正文阅读时隐藏", zen.Hides(false, false, false), "无焦点、无查找、指针不在工具栏带");
        check("Zen/编辑态不隐藏", !zen.Hides(true, false, false), "正在输入时不自动隐藏");
        check("Zen/工具栏有焦点不隐藏", !zen.Hides(false, true, false), "F6 召回;焦点回正文后才隐退");
        check("Zen/查找打开不隐藏", !zen.Hides(false, false, true), "打开查找时不自动隐藏");
        check("Zen/指针进入工具栏带即召回", zen.SetPointer(true) && !zen.Hides(false, false, false), "返回值要求重绘工具栏带");
        check("Zen/指针离开后隐退", zen.SetPointer(false) && zen.Hides(false, false, false), "离开工具栏带即恢复隐藏");
        check("Zen/同一状态不重复重绘", !zen.SetPointer(false), "避免指针移动引发多余重绘");
        zen.Enabled = false;
        check("Zen/关闭时指针变化不重绘", zen.SetPointer(true) == false && !zen.Hides(false, false, false), "非沉浸时工具栏带本来就可见");

        var command = Commands.All.FirstOrDefault(c => c.Id == 2251);
        var state = new CmdState(false, false, false, false, false, 16f);
        check("Zen/命令入口:设置与维护 · F11 · 勾选反映开关",
            command != null && command.Accel == "F11" && command.Face == CmdFace.Preferences
            && command.IsEnabled(state) && !command.IsChecked(state) && command.IsChecked(state with { Zen = true }),
            "更多菜单已满 10 项上限,入口放在子菜单;无效开关不出现");

        // 顶缘 1 DIP 分隔线属于工具栏的下缘:工具栏隐藏时一并不画,正文像素其余不变。
        using (new RenderStateScope())
        {
            RenderResources.SetBodySize(16f);
            foreach (bool dark in new[] { false, true })
                foreach (float scale in new[] { 1f, 1.5f })
                {
                    RenderResources.ApplyTheme(dark);
                    RenderResources.Scale = scale;
                    using var doc = new DocView();
                    doc.Load("# 标题\n\n正文。\n");
                    using var frame = new OffscreenFrame((int)(1000 * scale), (int)(300 * scale));
                    doc.OnWidth(1000f);
                    int x = (int)(960 * scale), y = (int)MathF.Round(OffscreenFrame.Top * scale);
                    frame.Paint(doc, 0f);
                    uint rule = frame.At(x, y), canvas = frame.At(x, y + (int)(8 * scale));
                    frame.Paint(doc, 0f, null, zenQuiet: true);
                    uint quiet = frame.At(x, y);
                    check($"Zen/隐藏时顶缘分隔线不画 {(dark ? "dark" : "light")} x{scale}", rule != canvas && quiet == canvas,
                        $"rule={rule:X6} quiet={quiet:X6} canvas={canvas:X6}");
                }
        }
    }
}

