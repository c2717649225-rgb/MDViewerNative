// Knuth-Plass optimal line-breaking — C# port from chenshuo/txt2png linebreak.h/cpp
// (originally extracted from TeX.web by D.E. Knuth).
// Reference: Knuth & Plass, "Breaking Paragraphs into Lines", SPE 11(11):1119-1184, 1981.
using System;
using System.Collections.Generic;

namespace MDViewerNative;

// ---------------------------------------------------------------------------
// Item types
// ---------------------------------------------------------------------------
internal readonly record struct KpBox(double Width, double Protrude = 0);
internal readonly record struct KpGlue(double Width, double Stretch, double Shrink);
internal readonly record struct KpPenalty(double Value, bool Flagged);

internal readonly struct KpItem
{
    public enum Kind : byte { Box, Glue, Penalty }
    public readonly Kind K;
    public readonly KpBox B;
    public readonly KpGlue G;
    public readonly KpPenalty P;

    public KpItem(KpBox b) { K = Kind.Box; B = b; G = default; P = default; }
    public KpItem(KpGlue g) { K = Kind.Glue; G = g; B = default; P = default; }
    public KpItem(KpPenalty p) { K = Kind.Penalty; P = p; B = default; G = default; }

    public static implicit operator KpItem(KpBox b) => new(b);
    public static implicit operator KpItem(KpGlue g) => new(g);
    public static implicit operator KpItem(KpPenalty p) => new(p);
}

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------
internal struct KpParams
{
    public double LinePenalty = 10.0;
    public double DoubleHyphenDemerits = 10_000.0;
    public double AdjDemerits = 10_000.0;
    public double Tolerance = 200.0;
    public double PreTolerance = 100.0;
    public double EmergencyStretch = 0.0;
    public KpParams() { }
}

// ---------------------------------------------------------------------------
// Line width spec (variable width support)
// ---------------------------------------------------------------------------
internal struct KpLineSpec
{
    public double[] Widths;
    public double Last;

    public static KpLineSpec Uniform(double w) => new() { Widths = Array.Empty<double>(), Last = w };

    public readonly double Width(int line) =>
        (line >= 1 && line <= Widths.Length) ? Widths[line - 1] : Last;
}

// ---------------------------------------------------------------------------
// Active node
// ---------------------------------------------------------------------------
internal sealed class KpActive
{
    public int Pos;
    public int Line;
    public int Fit;
    public bool Flagged;
    public double Demerits;
    public double Tw, Ts, Tk;
    public KpActive Prev;
}

// ---------------------------------------------------------------------------
// Core algorithm
// ---------------------------------------------------------------------------
internal static class KpBreak
{
    const double InfPenalty = 10_000.0;
    const double InfBad = 10_000.0;
    const double AwfulBad = 10_000_000.0;
    // S82:行首是宽串、或紧挨宽串之前断开的欠满行,劣度封顶到“很松”(渲染也不两端对齐)。
    // 仍比好行贵得多,只在另一边同样是极差行时才会胜出。
    const double WideRun = 0.5, WideRunBadness = 100.0;

    // TeX §108: badness(shortfall, total_stretch_or_shrink)
    static double Badness(double t, double s)
    {
        if (t == 0.0) return 0.0;
        if (s <= 0.0) return InfBad;
        double r = t / s;
        return Math.Min(InfBad, Math.Round(100.0 * r * r * r));
    }

    // Single pass of the KP algorithm with given tolerance and extra stretch
    static List<KpActive> LinebreakPass(
        IReadOnlyList<KpItem> items, KpLineSpec spec, KpParams param,
        double tolerance, double extraStretch)
    {
        var seed = new KpActive { Pos = -1, Line = 0, Fit = 2 /*DECENT*/, Flagged = false, Demerits = 0.0, Tw = 0, Ts = 0, Tk = 0 };
        var active = new List<KpActive> { seed };

        double cumW = 0, cumS = 0, cumK = 0;
        int n = items.Count;
        // 宽串 = 占行宽一半以上、中间不能断开的一串 Box(如长网址:单词与 : / ? 等符号各成一个 Box,彼此之间没有胶)。
        // 两种欠满行是躲不掉的,按参差行计价(劣度随空缺线性增长,留白越多越贵),不按“极差”重罚:
        //   ① 行内含宽串:可伸展的空格太少;② 下一个宽串放不进本行余下的空间,只能在此断行。
        // 只看“后面紧跟宽串”会误伤:“Long link:” 放得下网址却也被当作躲不掉,独占一行(S83 截图发现)。
        // 串内断点(Penalty,见 KpCjk.SplitOverlongRuns)不打断串:比整行还宽的网址折开后仍算同一串。
        // nextRun[k + 1] = 下标 > k 的第一个 Box 起、连续 Box 的总宽度(没有则为 0)。
        var nextRun = new double[n + 1];
        double run = 0.0, following = 0.0;
        for (int k = n - 1; k >= -1; k--)
        {
            nextRun[k + 1] = following;
            if (k < 0) break;
            run = items[k].K == KpItem.Kind.Box ? run + items[k].B.Width : items[k].K == KpItem.Kind.Penalty ? run : 0.0;
            if (items[k].K == KpItem.Kind.Box) following = run;
        }
        // runAt[s] = 以 s 起头的一串 Box 的总宽,runEnd[s] = 串尾;prevRun[s] = 上一串的起点;lastRun[k] = 起点 ≤ k 的最后一串(无则 -1)。
        var runEnd = new int[n];
        var runAt = new double[n];
        var prevRun = new int[n];
        var lastRun = new int[n];
        bool open = false;
        for (int k = 0, last = -1; k < n; k++)
        {
            var kind = items[k].K;
            if (kind == KpItem.Kind.Box)
            {
                if (!open) { prevRun[k] = last; last = k; open = true; }
                runAt[last] += items[k].B.Width;
                runEnd[last] = k;
            }
            else if (kind == KpItem.Kind.Glue) open = false;
            lastRun[k] = last;
        }

        for (int i = 0; i < n; ++i)
        {
            var item = items[i];
            bool isBreak = false;
            double pi = 0.0;
            bool isFlag = false;

            if (item.K == KpItem.Kind.Glue)
            {
                if (i > 0 && items[i - 1].K == KpItem.Kind.Box) isBreak = true;
            }
            else if (item.K == KpItem.Kind.Penalty)
            {
                if (item.P.Value < InfPenalty) { isBreak = true; pi = item.P.Value; isFlag = item.P.Flagged; }
            }

            if (isBreak)
            {
                double bw = cumW, bs = cumS, bk = cumK;
                // 行尾标点悬挂(microtype protrusion):断点前紧邻 Box 的 Protrude 让出宽度,
                // 该字形按自然宽度绘制时伸出右缘,视觉右缘更齐;绘制路径零改动
                double protrude = i > 0 && items[i - 1].K == KpItem.Kind.Box ? items[i - 1].B.Protrude : 0.0;
                if (pi <= -InfPenalty) pi = -InfPenalty;

                var survivors = new List<KpActive>();
                var newNodes = new List<KpActive>();
                double[] minDem = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
                KpActive[] bestPrev = new KpActive[4];

                foreach (var a in active)
                {
                    double lineW = bw - a.Tw - protrude;   // 紧急伸展只加进伸展量(TeX background[2]),不改自然宽度
                    double lineS = bs - a.Ts + extraStretch;
                    double lineK = bk - a.Tk;
                    int lnum = a.Line + 1;
                    double target = spec.Width(lnum);
                    double sfall = target - lineW;

                    double b; int fc;
                    if (sfall > 0.0)
                    {
                        b = Badness(sfall, lineS);
                        if (b > WideRunBadness)
                        {
                            double wide = WideRun * target;
                            bool holds = false;
                            for (int s = lastRun[i]; s >= 0 && runEnd[s] > a.Pos && !holds; s = prevRun[s]) holds = runAt[s] >= wide;   // 行内碰到宽串
                            bool forced = nextRun[i + 1] >= wide && nextRun[i + 1] <= target && lineW + nextRun[i + 1] > target;   // 下一宽串放得进一行、却放不进本行余下的空间
                            // 断在宽串内部(串内断点)的行还能继续排网址,留白并非躲不掉:按常规劣度,逼它排满;
                            // 否则参差计价(线性)经缺点值平方后会让折开的几行“平均地空”(S83 截图 w1100 发现)。
                            bool insideRun = items[i].K == KpItem.Kind.Penalty && lastRun[i] >= 0 && runEnd[lastRun[i]] > i;
                            if (!insideRun && (holds || forced)) b = Math.Min(b, WideRunBadness * sfall / wide);
                        }
                        fc = (b > 99.0) ? 0 : (b > 12.0) ? 1 : 2; // VERY_LOOSE / LOOSE / DECENT
                    }
                    else if (sfall < 0.0)
                    {
                        b = Badness(-sfall, lineK);
                        // 超出收缩能力的溢出按 100/DIP 计入劣度:紧急遍在多个超宽方案间选溢出最少者,
                        // 不再把多个不可断长串挤进同一行(Badness 本身封顶 InfBad,对溢出量不敏感)。
                        if (-sfall > lineK) b = InfBad + 100.0 * (-sfall - lineK);
                        fc = (b > 12.0) ? 3 : 2; // TIGHT / DECENT
                    }
                    else { b = 0; fc = 2; }

                    bool overfull = sfall < 0.0;
                    if (overfull && b > tolerance && pi != -InfPenalty) continue;
                    survivors.Add(a);
                    if (b > tolerance && !overfull && pi != -InfPenalty) continue;

                    // 缺点值照常计算,劣度封顶 InfBad:极差行(如一词一行)代价约 1e8,紧急遍也会避开它们。
                    // 原实现对 b ≥ InfBad 或强制断行给 0 缺点值,紧急遍因此偏爱一词一行(A5 发现)。
                    double bb = b;   // 欠满行已由 Badness 封顶 InfBad;超宽行保留溢出项
                    double d = (param.LinePenalty + bb) * (param.LinePenalty + bb);
                    if (pi > 0.0) d += pi * pi;
                    else if (pi > -InfPenalty) d -= pi * pi;
                    if (isFlag && a.Flagged) d += param.DoubleHyphenDemerits;
                    if (Math.Abs(fc - a.Fit) > 1) d += param.AdjDemerits;
                    d += a.Demerits;

                    if (d <= minDem[fc])
                    {
                        minDem[fc] = d;
                        bestPrev[fc] = a;
                    }
                }

                // 4 fit classes: keep the best candidate per class
                double globalMin = double.PositiveInfinity;
                for (int fc = 0; fc < 4; ++fc)
                    if (minDem[fc] < globalMin) globalMin = minDem[fc];

                if (globalMin < double.PositiveInfinity)
                {
                    for (int fc = 0; fc < 4; ++fc)
                    {
                        // 该 fit 桶无候选时 minDem==AwfulBad 而 bestPrev==null;globalMin 落在
                        // AwfulBad-AdjDemerits 窗口内时条件可能成立,必须排除 null(否则 NRE)
                        if (bestPrev[fc] != null && minDem[fc] <= globalMin + param.AdjDemerits)
                        {
                            newNodes.Add(new KpActive
                            {
                                Pos = i,
                                Line = bestPrev[fc].Line + 1,
                                Fit = fc,
                                Flagged = isFlag,
                                Demerits = minDem[fc],
                                Tw = bw,
                                Ts = bs,
                                Tk = bk,
                                Prev = bestPrev[fc]
                            });
                        }
                    }
                }

                survivors.AddRange(newNodes);
                active = survivors;
            }

            if (item.K == KpItem.Kind.Box) cumW += item.B.Width;
            else if (item.K == KpItem.Kind.Glue) { cumW += item.G.Width; cumS += item.G.Stretch; cumK += item.G.Shrink; }
        }

        int finalPos = n - 1;
        var finals = new List<KpActive>();
        foreach (var a in active)
            if (a.Pos == finalPos) finals.Add(a);
        if (finals.Count == 0) finals = active;
        return finals;
    }

    static List<int> Traceback(KpActive node)
    {
        var breaks = new List<int>();
        for (var cur = node; cur != null; cur = cur.Prev)
            if (cur.Pos >= 0) breaks.Add(cur.Pos);
        breaks.Reverse();
        return breaks;
    }

    /// Public API: find optimal break positions. Returns sorted list of item indices to break after.
    public static List<int> Paragraph(IReadOnlyList<KpItem> items, KpLineSpec spec, KpParams param)
    {
        // Four passes with progressive relaxation;第 4 遍容忍 overfull:
        // 超长不可断 token(如超宽 Box)会让活跃点全部被弃 → 断行整体失败、整段不换行;
        // 紧急遍允许 overfull 行存在(渲染裁剪),保证后续文本恢复断行(TeX emergency pass 同款)
        double[] tols = { param.PreTolerance, param.Tolerance, param.Tolerance, double.MaxValue };
        double[] extras = { 0.0, 0.0, param.EmergencyStretch, param.EmergencyStretch };

        // 注:紧急遍活跃点不淘汰,最坏 O(n²)(万级断点 + 前三遍全败的病态输入 ~0.2-1s 停顿);常规段落毫秒级
        for (int pass = 0; pass < 4; ++pass)
        {
            var finals = LinebreakPass(items, spec, param, tols[pass], extras[pass]);
            if (finals.Count > 0)
            {
                KpActive best = null;
                foreach (var f in finals)
                    if (best == null || f.Demerits < best.Demerits) best = f;
                return Traceback(best);
            }
        }
        return new List<int>();
    }
}
