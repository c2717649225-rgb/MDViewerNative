using System;
using System.Collections.Generic;

namespace MDViewerNative;

// U4：页脚的宿主接线（规格 §5）。内容由 FooterModel 决定，绘制在 FooterView；这里只取状态、管计时与失效区域。
// 编辑模式的页脚（第 n 行、Markdown · UTF-8）随 U9a 编辑条一起做。
internal static unsafe partial class Program
{
    const int TIMER_FOOTER = 16;
    static readonly FooterStatus FooterNote = new();
    static FooterHover? _footerHover;
    static readonly List<(int Level, float Y)> _chapterHeads = new();

    static bool FooterShown => State.Appearance.Desk;   // U9a：编辑模式也显示（第 n 行 · Markdown · 编码）

    static FooterContent FooterNow()
    {
        int current = FooterModel.CurrentChapter(ChapterHeads(), _offY);
        string chapter = current < 0 ? null : FooterModel.ChapterLabel(Doc.SealOf(Doc.TocEntries[current].blockIdx), Doc.TocEntries[current].text);
        float zoom = _bodyFont / RenderResources.Reading.DefaultBodySize;
        return FooterModel.Reading(chapter, Doc.WordCount, FooterModel.ReadRatio(_offY, Doc.TotalH, ContentHeight), zoom, _footerHover,
            FooterNote.Current(Environment.TickCount64));
    }

    // 目录条目的（级别、文档 y），页脚当前章节与书签绳刻度共用（U5b）。
    static List<(int Level, float Y)> ChapterHeads()
    {
        _chapterHeads.Clear();
        foreach (var e in Doc.TocEntries) _chapterHeads.Add((e.level, e.y));
        return _chapterHeads;
    }

    // 右侧朱色临时状态（如「已保存」），3 秒后消失。
    static void ShowFooterNote(string text)
    {
        if (!State.Appearance.Desk) return;
        FooterNote.Show(text, Environment.TickCount64);
        _ = Externs.SetTimer(_hwnd, (IntPtr)TIMER_FOOTER, FooterStatus.DefaultMs, IntPtr.Zero);
        InvalidateFooter();
    }

    static void OnFooterTimer(IntPtr h, IntPtr w)
    {
        _ = Externs.KillTimer(h, w);
        int due = FooterNote.DueMs(Environment.TickCount64);
        if (due > 0) _ = Externs.SetTimer(h, w, (uint)due, IntPtr.Zero);
        InvalidateFooter();
    }

    // 悬停链接时左侧换成 ↗ 网址 / → 目标章节；移开恢复章节名。
    static void UpdateFooterHover(float x, float y, bool overBody)
    {
        if (!FooterShown || _editing) return;
        FooterHover? next = overBody ? HoverFor(Doc.LinkAt(x, y - ContentTop + _offY)) : null;
        if (next == _footerHover) return;
        _footerHover = next;
        InvalidateFooter();
    }

    static void ClearFooterHover()
    {
        if (_footerHover == null) return;
        _footerHover = null;
        InvalidateFooter();
    }

    static FooterHover? HoverFor(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("#fn:", StringComparison.Ordinal)) return new(false, "脚注");
        if (url.StartsWith("#ref:", StringComparison.Ordinal)) return new(false, "回到正文");
        if (!DocumentLinks.TryResolve(url, Doc.DocumentDirectory, out var target, out _)) return new(true, url);
        if (target.Kind != LinkKind.Anchor) return new(true, target.Location);
        if (Doc.TryAnchorJump(target.Fragment, out float y))
            foreach (var e in Doc.TocEntries)
                if (MathF.Abs(e.y - y) < 0.5f) return new(false, FooterModel.ChapterLabel(Doc.SealOf(e.blockIdx), e.text));
        return new(false, "#" + target.Fragment);
    }

    static void InvalidateFooter()
    {
        if (_hwnd == IntPtr.Zero || !State.Appearance.Desk) return;
        _ = Externs.GetClientRect(_hwnd, out var cr);
        var r = new RECT { L = 0, R = cr.R, B = cr.B, T = Math.Max(0, cr.B - (int)MathF.Ceiling((FooterView.Height + FooterView.FadeAbove) * _scale)) };
        _ = Externs.InvalidateRectRect(_hwnd, ref r, 0);
    }
}
