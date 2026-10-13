using System;

namespace MDViewerNative;

// U7：选区气泡的宿主接线。松开鼠标时只记下“想要气泡”；位置在下一帧按 DocView 刚画出的选区外框算出，
// 这样拖选最后一下的变化也算在内。滚动、按键、点别处、切换标签/模式即收起（演示稿：滚动即隐藏）。
internal static unsafe partial class Program
{
    static bool _bubbleWanted;
    static string _bubbleText;
    static BubbleLayout? _bubble;
    static BubblePart _bubbleHover;
    static bool _bubblePressed;   // 这一击按在气泡上：随后的抬起不再当作选区结束

    static bool BubbleAllowed => State.Appearance.Desk && !_editing;

    static void RequestSelectionBubble()
    {
        string text = BubbleAllowed && Doc.HasSel ? SelectedPlainText() : null;
        if (!SelectionBubble.Shows(text)) { HideSelectionBubble(); return; }
        _bubbleWanted = true; _bubbleText = text; _bubbleHover = BubblePart.None;
        InvalidateContent(_hwnd);
    }

    static string SelectedPlainText()
    {
        string all = Doc.SearchText();
        int a = Math.Clamp(Doc.SelHead, 0, all.Length), b = Math.Clamp(Doc.SelTail, a, all.Length);
        return all.Substring(a, b - a);
    }

    static void HideSelectionBubble()
    {
        if (!_bubbleWanted) return;
        _bubbleWanted = false; _bubble = null; _bubbleText = null;
        InvalidateContent(_hwnd);
    }

    // OnPaint 在正文、页脚、查找框之后调用：气泡浮在最上层。
    static void PaintSelectionBubble(IntPtr hdc, RECT cr)
    {
        _bubble = null;
        if (!_bubbleWanted || !BubbleAllowed || !Doc.TryGetSelectionBounds(out float l, out float t, out float r, out float b)) return;
        var (copyW, findW, textH) = BubbleView.Measure();
        var layout = SelectionBubble.Place((l + r) / 2f, t, b, copyW, findW, textH, _viewW / _scale, ContentTop);
        _bubble = layout;
        BubbleView.Paint(hdc, cr, _scale, layout, _bubbleHover);
    }

    static bool OverBubble(float x, float y) => _bubble is { } b && SelectionBubble.Hit(b, x, y) != BubblePart.None;

    // 按下：点在气泡上执行按钮并吞掉这一击；点在别处先收起气泡，再照常处理。
    static bool BubblePointerDown(float x, float y)
    {
        if (!_bubbleWanted) return false;
        var part = _bubble is { } b ? SelectionBubble.Hit(b, x, y) : BubblePart.None;
        if (part == BubblePart.None) { HideSelectionBubble(); return false; }
        string text = _bubbleText;
        _bubblePressed = true;
        if (part == BubblePart.Box) return true;
        HideSelectionBubble();
        if (part == BubblePart.Copy) _ = Externs.SendMessageW(_hwnd, Externs.WM_COMMAND, (IntPtr)2001, IntPtr.Zero);
        else
        {
            // 光标收到选区起点再查：第一处命中就是刚选中的这段，朱笔圈画在原地。
            int start = Doc.SelHead;
            Doc.SelectRange(start, start);
            Find.OpenWith(SelectionBubble.Query(text));
        }
        return true;
    }

    static bool BubblePointerUp()
    {
        if (!_bubblePressed) return false;
        _bubblePressed = false;
        return true;
    }

    static void BubbleHover(float x, float y)
    {
        var part = _bubble is { } b ? SelectionBubble.Hit(b, x, y) : BubblePart.None;
        if (part == _bubbleHover) return;
        _bubbleHover = part;
        InvalidateContent(_hwnd);
    }
}
