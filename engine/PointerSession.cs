namespace MDViewerNative;

internal enum PointerClickIntent
{
    None,
    JumpRefBack,
    JumpFootnote,
    OpenLink,
    ToggleTask,
    ToggleDetails
}

internal sealed class PointerSession
{
    public bool IsCapturing { get; private set; }
    public int LastMx { get; private set; }
    public int LastMy { get; private set; }
    public int AutoScrollDir { get; private set; }
    public int HoverBtn { get; set; } = -1;
    public int PressBtn { get; set; } = -1;
    public bool Tracking { get; set; }

    public void BeginSelection(int mx, int my)
    {
        IsCapturing = true;
        LastMx = mx;
        LastMy = my;
        AutoScrollDir = 0;
    }

    public bool OnMouseMove(int mx, int my, bool overBody, int desiredAutoScrollDir, out bool autoScrollChanged)
    {
        if (!IsCapturing)
        {
            autoScrollChanged = false;
            return false;
        }

        if (!overBody)
        {
            autoScrollChanged = AutoScrollDir != 0;
            AutoScrollDir = 0;
            return false;
        }

        LastMx = mx;
        LastMy = my;
        autoScrollChanged = AutoScrollDir != desiredAutoScrollDir;
        AutoScrollDir = desiredAutoScrollDir;
        return true;
    }

    public void ExtendSelection(int mx, int my)
    {
        LastMx = mx;
        LastMy = my;
    }

    public bool EndSelection()
    {
        bool was = IsCapturing;
        IsCapturing = false;
        AutoScrollDir = 0;
        return was;
    }

    public void CancelSelection()
    {
        IsCapturing = false;
        AutoScrollDir = 0;
    }

    public bool UpdateAutoScrollDir(int dir)
    {
        if (dir == AutoScrollDir) return false;
        AutoScrollDir = dir;
        return true;
    }

    public bool StopAutoScroll()
    {
        if (AutoScrollDir == 0) return false;
        AutoScrollDir = 0;
        return true;
    }

    public void PressButton(int btn) => PressBtn = btn;

    public bool ReleaseButton(int hitBtn, out bool actionTriggered, out int btn)
    {
        if (PressBtn >= 0)
        {
            btn = PressBtn;
            actionTriggered = hitBtn == PressBtn;
            PressBtn = -1;
            return true;
        }
        actionTriggered = false;
        btn = -1;
        return false;
    }

    public bool OnMouseMoveToolbar(int hitBtn)
    {
        if (hitBtn != HoverBtn)
        {
            HoverBtn = hitBtn;
            return true;
        }
        return false;
    }

    public bool OnMouseLeave()
    {
        Tracking = false;
        if (HoverBtn != -1)
        {
            HoverBtn = -1;
            return true;
        }
        return false;
    }

    public static PointerClickIntent ClassifyReleaseAction(
        bool wasDrag,
        bool overBody,
        bool editing,
        string url,
        bool hasDetailsHit,
        out string target)
    {
        target = null;
        if (editing || !wasDrag || !overBody)
            return PointerClickIntent.None;

        if (url != null)
        {
            if (url.StartsWith("#ref:"))
            {
                target = url[5..];
                return PointerClickIntent.JumpRefBack;
            }
            if (url.StartsWith("#fn:"))
            {
                target = url[4..];
                return PointerClickIntent.JumpFootnote;
            }
            target = url;
            return PointerClickIntent.OpenLink;
        }

        if (!wasDrag)
            return PointerClickIntent.ToggleTask;

        if (hasDetailsHit)
            return PointerClickIntent.ToggleDetails;

        return PointerClickIntent.None;
    }
}
