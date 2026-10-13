using System;
using System.Linq;
using MDViewerNative;

// U2b：纸纹与灯下暖光像素的契约（规格 §1）。
internal static class PaperTextureTests
{
    internal static void Run(Action<string, bool, string> check)
    {
        void C(string name, bool ok, string detail = "paper texture") => check("PaperTexture/" + name, ok, detail);

        int n = PaperTexture.GrainSize;
        float[] noise = PaperTexture.Noise(n, 7u);
        C("noise stays in 0..1", noise.All(v => v >= 0f && v <= 1f), noise.Min().ToString("0.000") + ".." + noise.Max().ToString("0.000"));
        C("noise has texture", noise.Max() - noise.Min() > 0.3f);
        C("noise is deterministic", PaperTexture.Noise(n, 7u).SequenceEqual(noise) && !PaperTexture.Noise(n, 8u).SequenceEqual(noise));
        float seam = 0f, inner = 0f;
        for (int i = 0; i < n; i++)
        {
            seam = Math.Max(seam, Math.Max(Math.Abs(noise[i * n] - noise[i * n + n - 1]), Math.Abs(noise[i] - noise[(n - 1) * n + i])));
            for (int j = 1; j < n; j++) inner = Math.Max(inner, Math.Abs(noise[i * n + j] - noise[i * n + j - 1]));
        }
        C("tile edges join like any neighbouring pixels", seam <= inner + 1e-4f, seam.ToString("0.000") + " <= " + inner.ToString("0.000"));

        uint[] light = PaperTexture.Grain(false), dark = PaperTexture.Grain(true);
        byte MaxA(uint[] p) => (byte)p.Max(x => x >> 24);
        C("grain is faint", MaxA(light) <= 18 && MaxA(dark) <= 9 && MaxA(light) > 4, MaxA(light) + "/" + MaxA(dark));
        C("grain is premultiplied", light.All(p => ((p >> 16) & 0xFF) <= p >> 24 && ((p >> 8) & 0xFF) <= p >> 24 && (p & 0xFF) <= p >> 24));

        uint[] glow = PaperTexture.LampGlow();
        uint A(int x, int y) => glow[y * PaperTexture.GlowWidth + x] >> 24;
        int w = PaperTexture.GlowWidth, h = PaperTexture.GlowHeight;
        C("glow is clear in the middle and darker at the corners", A(w / 2, h / 2) == 0 && A(0, h - 1) > 30 && A(0, h - 1) <= 57, A(0, h - 1).ToString());
        C("glow is warm at the top", ((glow[w / 2] >> 16) & 0xFF) > (glow[w / 2] & 0xFF) && A(w / 2, 0) > 0);
        C("premultiply", PaperTexture.Premultiply(0xFFFFFF, 1f) == 0xFFFFFFFF && PaperTexture.Premultiply(0xFF8000, 0.5f) == 0x80804000 && PaperTexture.Premultiply(0x123456, 0f) == 0);
    }
}
