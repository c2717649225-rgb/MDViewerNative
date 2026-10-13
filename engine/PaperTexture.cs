using System;

namespace MDViewerNative;

// U2b：纸纹与灯下暖光的像素生成（规格 §1）。纯函数，输出预乘 BGRA，由 RenderResources 建成位图。
// 固定种子、可平铺，每次运行结果一致；不引入图片资源。
internal static class PaperTexture
{
    public const int GrainSize = 240;
    public const int GlowWidth = 64, GlowHeight = 40;   // 小图拉伸到视口，线性插值即成平滑渐变
    const float GrainAlpha = 0.07f;                     // 演示 feColorMatrix 的 0.07

    // 两层周期值噪声（对应演示 fractalNoise, baseFrequency .8, numOctaves 2 的观感），值域约 0..1。
    public static float[] Noise(int size, uint seed)
    {
        var result = new float[size * size];
        (int cells, float weight)[] octaves = { (size / 2, 0.6f), (size, 0.4f) };
        foreach (var (cells, weight) in octaves)
        {
            float step = (float)cells / size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fx = x * step, fy = y * step;
                    int x0 = (int)fx, y0 = (int)fy;
                    float tx = Smooth(fx - x0), ty = Smooth(fy - y0);
                    float a = Lattice(x0, y0, cells, seed), b = Lattice(x0 + 1, y0, cells, seed);
                    float c = Lattice(x0, y0 + 1, cells, seed), d = Lattice(x0 + 1, y0 + 1, cells, seed);
                    result[y * size + x] += weight * Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
                }
            seed = seed * 747796405u + 2891336453u;
        }
        return result;
    }

    // 纸纹瓦片：浅色纸张为暖褐色细点（近似演示的 multiply），灯下为暖白细点（近似 screen）。
    public static uint[] Grain(bool dark, uint seed = 0x5EED_1234u)
    {
        float[] noise = Noise(GrainSize, seed);
        uint rgb = dark ? 0xFFDCB4u : 0x5C4733u;
        float scale = dark ? 0.5f : 1f;
        var pixels = new uint[noise.Length];
        for (int i = 0; i < noise.Length; i++) pixels[i] = Premultiply(rgb, GrainAlpha * scale * Math.Clamp(noise[i], 0f, 1f));
        return pixels;
    }

    // 灯下：顶部暖光（椭圆 80%×60%，圆心 50%/−10%，rgba(255,196,130,.08) → 70% 处透明）
    // 叠加暗角（椭圆 130%×110%，圆心 50%/40%，62% 处透明 → 边缘 rgba(0,0,0,.22)）。
    public static uint[] LampGlow()
    {
        var pixels = new uint[GlowWidth * GlowHeight];
        for (int y = 0; y < GlowHeight; y++)
            for (int x = 0; x < GlowWidth; x++)
            {
                float u = (x + 0.5f) / GlowWidth, v = (y + 0.5f) / GlowHeight;
                float warm = Ellipse(u, v, 0.5f, -0.1f, 0.8f, 0.6f);
                float warmA = 0.08f * Math.Clamp(1f - warm / 0.7f, 0f, 1f);
                float edge = Ellipse(u, v, 0.5f, 0.4f, 1.3f, 1.1f);
                float darkA = 0.22f * Math.Clamp((edge - 0.62f) / (1f - 0.62f), 0f, 1f);
                // 先暖光后暗角，按 source-over 合成为一个像素。
                float a = warmA + darkA * (1f - warmA);
                float r = a <= 0f ? 0f : (255f * warmA * (1f - darkA)) / a;
                float g = a <= 0f ? 0f : (196f * warmA * (1f - darkA)) / a;
                float b = a <= 0f ? 0f : (130f * warmA * (1f - darkA)) / a;
                pixels[y * GlowWidth + x] = Premultiply(((uint)r << 16) | ((uint)g << 8) | (uint)b, a);
            }
        return pixels;
    }

    // CSS radial-gradient 的“椭圆距离”：到圆心的归一化距离，1 表示落在渐变终止椭圆上（半轴为宽高比例的一半）。
    static float Ellipse(float u, float v, float cx, float cy, float w, float h)
    {
        float dx = (u - cx) / (w / 2f), dy = (v - cy) / (h / 2f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // 0xAARRGGBB（内存为 BGRA），颜色按 alpha 预乘。
    public static uint Premultiply(uint rgb, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        uint A = (uint)MathF.Round(alpha * 255f);
        uint Ch(int shift) => (uint)MathF.Round(((rgb >> shift) & 0xFF) * alpha);
        return (A << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    static float Lattice(int x, int y, int cells, uint seed)
    {
        x = ((x % cells) + cells) % cells; y = ((y % cells) + cells) % cells;   // 周期边界：瓦片无缝
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + seed * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
