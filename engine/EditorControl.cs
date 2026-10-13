using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

// Owns the native EDIT, its subclass callback, GDI font and gutter layouts.
// The host owns document policy and supplies the two actions that cross that boundary.
internal sealed unsafe partial class EditorControl : IDisposable
{
    public nint Handle { get; private set; }
    public float Scale { get; private set; }
    public float LineHeight { get; private set; } = 18f;
    // 行号栏宽度随位数与字号变化（EditorGutter），RefreshGutterWidth 变化时重新排版
    public float GutterWidth { get; private set; } = 44f;
    private float _digitWidth;
    private int _digitTypographyVersion = -1;
    private readonly nint _owner;
    private readonly Action _changed;
    private readonly Func<string> _pasteImage;
    private GCHandle _self;
    private nint _font, _previous;
    private bool _disposed;
    private readonly ImeComposition _ime = new();
    public bool IsComposing => _ime.Active;
    public event Action CompositionChanged;
    public Func<bool> UndoRequested, RedoRequested, HasUndo, HasRedo;
    private int _mutationDepth;
    private bool _mutationChanged;
    public bool IsMutating => _mutationDepth > 0;
    public void NoteMutation() => _mutationChanged = true;
    private float _top = ToolH;
    public float BottomInset { get; set; }   // U9a：底部让给宿主页脚的高度（DIP）
    public Action CaretMoved;                // U9a：光标可能换了行（与行号栏同时机），宿主据此刷新页脚
    public float FontSize { get; private set; } = BodySize;

    public EditorControl(nint owner, float scale, Action changed, Func<string> pasteImage)
    {
        _owner = owner; _changed = changed; _pasteImage = pasteImage;
        try
        {
            // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_NOHIDESEL.
            EnsureRichEdit();
            Handle = Externs.CreateWindowExW(0, "RICHEDIT50W", "", 0x54200144,
                0, 0, 0, 0, owner, 0, Externs.GetModuleHandleW(null), 0);
            if (Handle == 0) throw new System.ComponentModel.Win32Exception();
            Externs.SendMessageW(Handle, 0x00C5 /*EM_SETLIMITTEXT*/, (nint)0x7FFFFFFE, 0);
            _self = GCHandle.Alloc(this);
            Externs.SetWindowLongPtrW(Handle, -21, GCHandle.ToIntPtr(_self));
            _previous = Externs.GetWindowLongPtrW(Handle, -4);
            Externs.SetWindowLongPtrW(Handle, -4, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc);
            InitializeRichEdit();
            SetScale(scale);
            Resize();
            ApplyTheme();
        }
        catch { Dispose(); throw; }
    }

    public void SetInputPreferences(int tabWidth, bool wrap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = CapturePosition();
        int tab = Math.Clamp(tabWidth, 1, 16) * 4;
        Externs.SendMessageW(Handle, 0x00CB, 1, (nint)(&tab));
        long style = Externs.GetWindowLongPtrW(Handle, -16).ToInt64();
        long wanted = wrap ? style & ~(0x00100000L | 0x80L) : style | 0x00100000L | 0x80L;
        if (wanted != style)
        {
            Externs.SetWindowLongPtrW(Handle, -16, (nint)wanted);
            Externs.SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x27);
        }
        Native(0x0448 /*EM_SETTARGETDEVICE*/, 0, wrap ? 0 : 1);
        RestorePosition(position);
    }

    public EditorPosition CapturePosition()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (start, end) = Externs.EditSelection(Handle);
        int line = (int)Externs.SendMessageW(Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, 0, 0);
        int first = (int)Externs.SendMessageW(Handle, 0x00BB /*EM_LINEINDEX*/, (nint)line, 0);
        return new(start, end, Math.Max(0, first));
    }

    public void RestorePosition(EditorPosition position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        position = position.Clamp(Externs.GetWindowTextLengthW(Handle));
        Externs.SendMessageW(Handle, 0x00B1 /*EM_SETSEL*/, (nint)position.Start, (nint)position.End);
        int line = (int)Externs.SendMessageW(Handle, 0x00C9 /*EM_LINEFROMCHAR*/, (nint)position.FirstCharacter, 0);
        int current = (int)Externs.SendMessageW(Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, 0, 0);
        Externs.SendMessageW(Handle, 0x00B6 /*EM_LINESCROLL*/, 0, (nint)(Math.Max(0, line) - current));
        InvalidateGutter();
    }

    public void SetScale(float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nint font = Externs.CreateFontW(-(int)MathF.Round(FontSize * scale), 0, 0, 0, 400, 0, 0, 0,
            1, 0, 0, 4, 0, "Microsoft YaHei UI");
        if (font == 0) throw new System.ComponentModel.Win32Exception();
        Externs.SendMessageW(Handle, 0x0030 /*WM_SETFONT*/, font, (nint)1);
        if (_font != 0) Externs.DeleteObject(_font);
        _font = font;
        Scale = scale;
        nint dc = GetDC(Handle), old = SelectObject(dc, font);
        try { if (GetTextMetricsW(dc, out var metrics)) LineHeight = metrics.tmHeight + metrics.tmExternalLeading; }
        finally { SelectObject(dc, old); ReleaseDC(Handle, dc); }
    }

    public void SetFontSize(float size)
    {
        if (FontSize == size) return;
        FontSize = size;
        SetScale(Scale);
        Resize(_top);
    }

    public void Resize(float top = -1f)
    {
        if (top < 0f) top = ToolH;
        _top = top;
        GutterWidth = MeasureGutterWidth();
        Externs.GetClientRect(_owner, out var rect);
        Externs.MoveWindow(Handle, (int)(GutterWidth * Scale), (int)(_top * Scale),
            Math.Max(0, rect.R - (int)(GutterWidth * Scale)), Math.Max(0, rect.B - (int)(_top * Scale) - (int)(BottomInset * Scale)), true);
        Externs.GetClientRect(Handle, out var textRect);
        int padding = (int)(8f * Scale);
        textRect.L += (int)(TextLeftInset * Scale); textRect.R -= padding; textRect.T += padding; textRect.B -= padding;   // U9b：信笺左侧留出双线页边
        Externs.SendMessageW(Handle, 0x00B3 /*EM_SETRECT*/, 0, (nint)(&textRect));
        InvalidateGutter();
    }

    public void ApplyTheme()
    {
        Externs.SetWindowTheme(Handle, WindowChrome.GetScrollbarTheme(DarkMode, HighContrast), null);
        Externs.SendMessageW(Handle, 0x0030 /*WM_SETFONT*/, _font, (nint)1);
        Resize(_top); // Native theme changes reset the EDIT formatting rectangle.
        Native(0x0443 /*EM_SETBKGNDCOLOR*/, 0, (nint)SourceTheme.Background);
        QueueHighlight();
    }

    public void InvalidateGutter()
    {
        Externs.GetClientRect(_owner, out var rect);
        rect.L = 0; rect.T = (int)(_top * Scale); rect.R = (int)(GutterWidth * Scale);
        Externs.InvalidateRectRect(_owner, ref rect, 0);
        CaretMoved?.Invoke();
    }

    // U9a：光标（选区起点）所在的源码行，从 1 起；RichEdit 折行后的显示行不算。
    public int CaretSourceLine
    {
        get
        {
            if (Handle == 0) return 1;
            int* range = stackalloc int[2];
            Native(0x0434 /*EM_EXGETSEL*/, 0, (nint)range);
            return Coordinates.LineOfNative(range[0]);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        EditorControl editor = null;
        try
        {
            nint token = Externs.GetWindowLongPtrW(h, -21);
            if (token != 0) editor = (EditorControl)GCHandle.FromIntPtr(token).Target;
            if (editor != null)
            {
                if (editor._ime.Observe(message))
                {
                    editor.CompositionChanged?.Invoke();
                    if (!editor._ime.Active) editor.QueueHighlight();
                }
                if (message == 0x0082 /*WM_NCDESTROY*/)
                {
                    nint result = Externs.CallWindowProcW(editor._previous, h, message, w, l);
                    editor.Handle = 0;
                    editor.Dispose();
                    return result;
                }
                bool mutation = message is 0x000C or 0x00C2 or 0x00C7 or 0x0100 or 0x0102 or 0x010F or 0x0300 or 0x0302 or 0x0303 or 0x0304 or 0x0454;
                if (mutation) { editor._mutationDepth++; editor._coordinates = null; }
                try { return editor.HandleMessage(h, message, w, l); }
                finally
                {
                    if (mutation)
                    {
                        editor._coordinates = null;
                        if (--editor._mutationDepth == 0)
                        {
                            if (editor._mutationChanged) { editor._mutationChanged = false; editor._changed(); }
                            editor.QueueHighlight();
                            editor.RefreshGutterWidth();
                        }
                    }
                }
            }
        }
        catch (Exception ex) { AppLog.Error("editor input callback", ex); }
        return editor != null ? Externs.CallWindowProcW(editor._previous, h, message, w, l) : Externs.DefWindowProcW(h, message, w, l);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseRichEdit();
        if (Handle != 0 && Externs.IsWindow(Handle))
        {
            if (_previous != 0) Externs.SetWindowLongPtrW(Handle, -4, _previous);
            Externs.SetWindowLongPtrW(Handle, -21, 0);
            Externs.DestroyWindow(Handle);
        }
        Handle = 0;
        if (_self.IsAllocated) _self.Free();
        if (_font != 0) { Externs.DeleteObject(_font); _font = 0; }
        ReleaseGutterLayouts();
    }

    private nint HandleMessage(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == 0x0113 /*WM_TIMER*/ && (nuint)w == RulerTimer) { PaintLetter(); return 0; }   // U9b：镇尺出现动画
        if (TryRichMessage(m, w, l, out nint translated)) return translated;
        if (!_ime.Active)
        {
            if (m == 0x00C6 && HasUndo != null) return HasUndo() ? 1 : 0;
            if (m == 0x0455 && HasRedo != null) return HasRedo() ? 1 : 0;
            if ((m == 0x00C7 || m == 0x0304) && UndoRequested != null) return UndoRequested() ? 1 : 0;
            if (m == 0x0454 && RedoRequested != null) return RedoRequested() ? 1 : 0;
            if (m == 0x0102 && w == 26 && UndoRequested != null)
                return (Externs.GetKeyState(0x10) & 0x8000) != 0 ? (RedoRequested?.Invoke() == true ? 1 : 0) : (UndoRequested() ? 1 : 0);
            if (m == 0x0102 && w == 25 && RedoRequested != null) return RedoRequested() ? 1 : 0;
        }

        if (m == 0x0302)
        {
            string path = _pasteImage?.Invoke();
            if (path != null)
            {
                string ins = "![](" + Path.GetFileName(path) + ")";
                fixed (char* pch = ins) _ = Externs.SendMessageW(h, 0x00C2, (IntPtr)1, (IntPtr)pch); _changed();

                return default;
            }
            return Native(0x0440 /*EM_PASTESPECIAL*/, 13 /*CF_UNICODETEXT*/, 0);
        }
        if (m == 0x007B /*WM_CONTEXTMENU*/)
        {
            // Route mouse and keyboard context menus to the host's editing commands.
            _ = Externs.SendMessageW(_owner, m, w, l);
            return default;
        }
        // TranslateMessage queues WM_CHAR before dispatching WM_KEYDOWN. Handle text
        // transformations here so the native EDIT cannot insert the same character again.
        if (m == 0x0102 /*WM_CHAR*/ && !_ime.Active)
        {
            int character = (int)w;
            bool shift = (Externs.GetKeyState(0x10) & 0x8000) != 0;
            if (character == 0x0D && !shift)
            {
                // 回车自动续列表
                var (cs, _) = Externs.EditSelection(h);
                string text = Externs.WindowText(h);
                int lineStart = EditorKit.LineStart(text, cs);
                string lt = text[lineStart..EditorKit.LineEnd(text, cs)];
                string cont = EditorKit.EnterContinuation(lt);
                if (cont != null)
                {
                    if (cont.Length == 0)
                    {
                        _ = Externs.SendMessageW(h, 0x00B1, (IntPtr)lineStart, (IntPtr)(lineStart + lt.Length));
                        fixed (char* pe2 = "") _ = Externs.SendMessageW(h, 0x00C2, (IntPtr)1, (IntPtr)pe2);
                    }
                    else
                    {
                        fixed (char* p = cont) _ = Externs.SendMessageW(h, 0x00C2, (IntPtr)1, (IntPtr)p);
                    }
                    _changed();

                    InvalidateGutter();
                    return default;
                }
            }
            if (character == 0x09)
            {
                TabIndent(h, shift);
                InvalidateGutter();
                return default;
            }
            if (character == 0x0D)
            {
                fixed (char* newline = "\r\n") Native(0x00C2, 1, (nint)newline);
                InvalidateGutter();
                return 0;
            }
        }
        if (m == 0x0101 /*WM_KEYUP*/)
        {
            InvalidateGutter();
        }
        nint result = Externs.CallWindowProcW(_previous, h, m, w, l);
        if (m is 0x0115 or 0x020A or 0x0202) InvalidateGutter();
        if (_letter != null && RepaintsText(m, w)) PaintLetter();   // U9b：RichEdit 画完再叠信笺
        return result;
    }
    private void TabIndent(IntPtr h, bool shift)
    {
        var (a, b) = Externs.EditSelection(h);
        string text = Externs.WindowText(h);
        int lastCh = b > a ? b - 1 : b;
        int spanStart = EditorKit.LineStart(text, a);
        int spanEnd = EditorKit.LineEnd(text, lastCh);
        string span = text[spanStart..spanEnd];
        string mod = shift ? EditorKit.DedentBlock(span, AppPreferences.Current.TabWidth) : EditorKit.IndentBlock(span, AppPreferences.Current.TabWidth, AppPreferences.Current.UseTabs);
        _ = Externs.SendMessageW(h, 0x00B1, (IntPtr)spanStart, (IntPtr)spanEnd);
        fixed (char* p = mod) _ = Externs.SendMessageW(h, 0x00C2, (IntPtr)1, (IntPtr)p);
        // Map each endpoint through the same line transformation as the replacement.
        int Map(int position)
        {
            int offset = Math.Clamp(position - spanStart, 0, span.Length);
            string prefix = span[..offset];
            return spanStart + (shift ? EditorKit.DedentBlock(prefix, AppPreferences.Current.TabWidth) : EditorKit.IndentBlock(prefix, AppPreferences.Current.TabWidth, AppPreferences.Current.UseTabs)).Length;
        }
        _ = Externs.SendMessageW(h, 0x00B1, (IntPtr)Map(a), (IntPtr)Map(b));
        _changed();

    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextMetricsW(IntPtr dc, out TEXTMETRICW tm);

    [StructLayout(LayoutKind.Sequential)]
    struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth, tmWeight;
        public int tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    /// 编辑模式行号沟槽(主窗 Paint 的编辑分支调用)
    private readonly Dictionary<int, nint> _gutterLayouts = new();   // 行号→布局缓存(用 FmtUi 构建,SetBodySize 时整体失效;条目有上限)
    private void ReleaseGutterLayouts()
    {
        foreach (var g in _gutterLayouts.Values) if (g != IntPtr.Zero) Marshal.Release(g);
        _gutterLayouts.Clear();
    }
    private int _gutterTypographyVersion = -1;

    // 行数位数或字号变了才重排（每次修改后调用，常态只比较一次）
    private void RefreshGutterWidth()
    {
        if (Handle == 0 || _disposed) return;
        if (MeasureGutterWidth() != GutterWidth) Resize(_top);
    }

    private float MeasureGutterWidth()
    {
        if (Handle == 0) return GutterWidth;
        if (_digitTypographyVersion != TypographyVersion || _digitWidth <= 0f)
        {
            nint layout = FmtUi != 0 ? Dw.MkLayout("0000000000", FmtUi, 10000f) : 0;
            if (layout != 0)
            {
                DWRITE_TEXT_METRICS m = default;
                Dw.GetMetrics(layout, &m);
                Marshal.Release(layout);
                _digitWidth = m.widthIncludingTrailingWhitespace / 10f;
                _digitTypographyVersion = TypographyVersion;
            }
            if (_digitWidth <= 0f) return GutterWidth;
        }
        // 信笺按源码行编号，经典编辑框按显示行
        int lines = _letter != null ? Coordinates.LineOfNative(int.MaxValue) : (int)Native(0x00BA /*EM_GETLINECOUNT*/);
        return EditorGutter.Width(lines, _digitWidth);
    }
    public unsafe void PaintGutter(RECT cr, nint hdc)
    {
        if (Handle == IntPtr.Zero || LineHeight <= 0f)
        {
            return;
        }
        if (_gutterTypographyVersion != TypographyVersion)
        {
            ReleaseGutterLayouts();
            _gutterTypographyVersion = TypographyVersion;
        }
        RECT r2 = cr;
        PBind(Rt, hdc, &r2);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(IntPtr*)((nint)(*(IntPtr*)Rt) + (nint)51 * (nint)sizeof(nint))))(Rt, 96f * Scale, 96f * Scale);
        PBegin(Rt);
        ulong ga = 0, gb = 0;
        try
        {
            // N8:RT 已 SetDpi(96*Scale),坐标一律 DIP——旧代码 44f*Scale/22f*Scale 双重缩放,150% 下行号画到 EDIT 控件底下
            float gx = GutterWidth;
            float clientBotDip = cr.B / Scale - BottomInset;   // 行号只画到编辑框底；底色铺满，页脚叠在上面
            D2D1_RECT_F bg = new D2D1_RECT_F { left = 0f, top = _top, right = cr.R / Scale, bottom = cr.B / Scale };
            PFill(Rt, &bg, BCanvas);
            if (_letter == null) FillLine(gx - 0.5f, _top, gx - 0.5f, clientBotDip, BHairZone, 1.5f);   // 信笺有自己的双线页边
            int first = (int)Externs.SendMessageW(Handle, 0x00CE /*EM_GETFIRSTVISIBLELINE*/, IntPtr.Zero, IntPtr.Zero);
            int caretLine = (int)Externs.SendMessageW(Handle, 0x00C9 /*EM_LINEFROMCHAR*/, (IntPtr)(-1), IntPtr.Zero);
            bool source = _letter != null;   // U9b：信笺按源码行编号（折行的续行不写号）；经典编辑框仍按显示行
            int caretSource = source ? CaretSourceLine : 0;
            float lineH = LineHeight / Scale;
            int lines = (int)Externs.SendMessageW(Handle, 0x00BA /*EM_GETLINECOUNT*/, 0, 0);
            int n = first;
            int maxVisible = (int)(clientBotDip / lineH) + 2;
            for (int k = 0; k < maxVisible && n < lines; k++, n++)
            {
                float y = _top + LineTop(n);
                if (y >= clientBotDip) break;
                if (y + lineH < _top) continue;
                if (_gutterLayouts.Count > 4096) ReleaseGutterLayouts();   // 超长文档防无界增长(重建成本可忽略)
                int number = n + 1;
                if (source)
                {
                    int index = (int)Native(0x00BB /*EM_LINEINDEX*/, n, 0);
                    if (!Coordinates.IsParagraphStart(index)) continue;
                    number = Coordinates.LineOfNative(index);
                }
                if (!_gutterLayouts.TryGetValue(number, out nint el))
                {
                    el = Dw.MkLayout(number.ToString(), FmtUi, 10000f);   // 显示 1 基行号(EM_GETFIRSTVISIBLELINE 是 0 基);缓存键仍按实际行索引
                    Dw.Bind(el);
                    // 不 SetTextAlignment(right):文本会在 10000px 布局盒内右对齐被推出窗口(行号"消失"的根因);
                    // 下方 x = gx-8-metrics.width 的手动定位本身就是右对齐
                    _gutterLayouts[number] = el;   // 缓存键＝显示的号码
                }
                Dw.Bind(el);   // Bind 是静态槽位赋值,DrawBtn 等同帧路径可能覆盖,绘制前重绑
                DWRITE_TEXT_METRICS em = default(DWRITE_TEXT_METRICS);
                Dw.GetMetrics(el, &em);
                PDraw(Rt, new D2D1_POINT_2F { x = EditorGutter.NumberRight(gx) - em.width, y = y + (lineH - em.height) / 2f }, el, (source ? number == caretSource : n == caretLine) ? BAccent : BMuted, 0);
            }
        }
        finally { PEnd(Rt, &ga, &gb); }
    }
}
