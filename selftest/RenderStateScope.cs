using System;
using MDViewerNative;

// 测试用：进入时记下 RenderResources 中测试会改动的全局状态，Dispose 时按依赖顺序还原：
// 字号 → 衬线（会重新探测 SerifBase）→ SerifBase → Scale → 主题。
// 用来替代各测试手写的保存与还原（E6）。Scale 与主题不参与字体格式重建，顺序只对衬线和 SerifBase 有要求。
internal sealed class RenderStateScope : IDisposable
{
    private readonly float _size = RenderResources.BodySize, _scale = RenderResources.Scale, _serifBase = RenderResources.SerifBase;
    private readonly bool _serif = RenderResources.SerifMode, _dark = RenderResources.DarkMode;
    private readonly string _family = RenderResources.SerifFamily;

    public void Dispose()
    {
        RenderResources.SetBodySize(_size);
        RenderResources.SetSerif(_serif, _family);
        RenderResources.SerifBase = _serifBase;
        RenderResources.Scale = _scale;
        RenderResources.ApplyTheme(_dark);
    }

    // 自检：六项状态全部改掉，离开作用域后逐项还原。
    internal static void Run(Action<string, bool, string> check)
    {
        float size = RenderResources.BodySize, scale = RenderResources.Scale, serifBase = RenderResources.SerifBase;
        bool serif = RenderResources.SerifMode, dark = RenderResources.DarkMode;
        string family = RenderResources.SerifFamily;
        using (new RenderStateScope())
        {
            RenderResources.SetBodySize(size + 4f);
            RenderResources.SetSerif(!serif, family ?? "Times New Roman");
            RenderResources.SerifBase = serifBase + 0.1f;
            RenderResources.Scale = scale + 1f;
            RenderResources.ApplyTheme(!dark);
        }
        bool ok = RenderResources.BodySize == size && RenderResources.Scale == scale && RenderResources.SerifBase == serifBase
            && RenderResources.SerifMode == serif && RenderResources.SerifFamily == family && RenderResources.DarkMode == dark;
        check("RenderState/scope restores size, serif, baseline, scale and theme", ok,
            $"size={RenderResources.BodySize} scale={RenderResources.Scale} base={RenderResources.SerifBase} serif={RenderResources.SerifMode} family={RenderResources.SerifFamily ?? "(null)"} dark={RenderResources.DarkMode}");
    }
}
