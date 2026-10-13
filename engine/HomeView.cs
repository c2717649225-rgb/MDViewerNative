using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static MDViewerNative.RenderResources;
using static MDViewerNative.DeskShapes;

namespace MDViewerNative;

internal readonly record struct HomePaintState(float Top, float Scroll, HomeTarget Hover);

// U10：起始页的绘制与文字排版（几何在 Home）。文字布局在 Prepare 时建好，模型、宽度或衬线字体变了才重建。
internal static unsafe class HomeView
{
    const string Sans = "Segoe UI";
    static readonly List<nint> _layouts = new();
    static readonly Dictionary<(string, uint, float), nint> _formats = new();
    static HomeModel _model;
    static float _w, _top;
    static string _serif;
    static HomeMeasure _m;
    static nint _secCont, _secRecent, _title, _sub, _btn, _whereL, _whereR, _open, _drop;
    static nint[] _name = Array.Empty<nint>(), _ex = Array.Empty<nint>(), _dir = Array.Empty<nint>(), _when = Array.Empty<nint>();
    static nint[] _greet = Array.Empty<nint>(), _date = Array.Empty<nint>(), _stamp = Array.Empty<nint>();
    static float _dateColW;
    public static HomeFrame Frame { get; private set; }

    static string Serif => SerifFamily ?? "SimSun";

    static nint Fmt(string family, uint weight, float size)
    {
        var key = (family, weight, size);
        if (!_formats.TryGetValue(key, out nint f)) _formats[key] = f = Dw.MkFormat(family, weight, size);
        return f;
    }

    static nint Keep(nint layout) { if (layout != 0) _layouts.Add(layout); return layout; }

    static nint Text(string s, nint fmt, float spacing = 0f)
    {
        nint l = Keep(Dw.MkLayout(s ?? "", fmt, 10000f));
        if (spacing > 0f && !string.IsNullOrEmpty(s)) Dw.SetCharacterSpacing(l, 0f, spacing, 0, (uint)s.Length);
        return l;
    }

    static nint Trim(string s, nint fmt, float width) => Keep(Dw.TrimLayout(s ?? "", fmt, MathF.Max(8f, width)));

    static void Release()
    {
        foreach (nint l in _layouts) Marshal.Release(l);
        _layouts.Clear();
    }

    // 每个字单独一个布局，竖排时逐字往下排（演示 writing-mode:vertical-rl）。
    static nint[] Chars(string s, nint fmt)
    {
        var list = new List<nint>();
        foreach (var rune in s.EnumerateRunes()) list.Add(Keep(Dw.MkLayout(rune.ToString(), fmt, 10000f)));
        return list.ToArray();
    }

    public static HomeFrame Prepare(HomeModel model, float viewW, float top)
    {
        if (ReferenceEquals(model, _model) && viewW == _w && top == _top && _serif == Serif && Frame != null) return Frame;
        bool rebuild = !ReferenceEquals(model, _model) || viewW != _w || _serif != Serif || Frame == null;
        _top = top;
        if (!rebuild) { Frame = Home.Layout(viewW, top, _m, model.Continue != null, model.Recent.Count); return Frame; }
        Release();
        _model = model; _w = viewW; _serif = Serif;
        // 竖排问候：字 50 粗宋、字距 .18em、列宽 1.2 行高；日期 15 宋、字距 .35em；「书桌」印 32 宽。
        _greet = Chars(model.Greeting, Fmt(Serif, 700, Home.GreetSize));
        _date = Chars(model.Date, Fmt(Serif, 400, Home.DateSize));
        _stamp = Chars(Home.StampText, Fmt(Serif, 700, Home.StampSize));
        _dateColW = _date.Length > 0 ? Dw.LayoutH(_date[0]) : Home.DateSize * 1.4f;
        float greetW = Home.GreetSize * Home.GreetLine + Home.GreetGap + _dateColW + Home.GreetGap + Home.StampW;

        _secCont = Text("继续阅读", Fmt(Sans, 400, Home.SecSize), Home.SecSpacing * Home.SecSize);
        _secRecent = Text("最近打开", Fmt(Sans, 400, Home.SecSize), Home.SecSpacing * Home.SecSize);
        _btn = Text("继续阅读", Fmt(Sans, 400, Home.ButtonSize));
        _open = Text("打开文件…", Fmt(Sans, 400, Home.ButtonSize));
        _drop = Text("或者把 Markdown 文件直接拖进窗口", Fmt(Sans, 400, Home.ActionsText));
        float bh = Home.ButtonPadY * 2f + MathF.Max(Dw.LayoutH(_btn), Home.ButtonIcon);
        float bw = Home.ButtonInsetX * 2f + Dw.LayoutW(_btn) + Home.ButtonIconGap + Home.ButtonIcon;
        float ow = Home.ButtonInsetX * 2f + Dw.LayoutW(_open) + Home.ButtonIconGap + Home.ButtonIcon;
        nint nameProbe = Text("字", Fmt(Serif, 700, Home.NameSize)), footProbe = Text("字", Fmt(Sans, 400, Home.FootSize));
        nint titleProbe = Text("字", Fmt(Serif, 700, Home.TitleSize)), subProbe = Text("字", Fmt(Sans, 400, Home.SubSize)), whereProbe = Text("字", Fmt(Sans, 400, Home.WhereSize));
        var m = new HomeMeasure(Dw.LayoutH(_secCont), Dw.LayoutH(titleProbe) + Home.SubGap + Dw.LayoutH(subProbe), Dw.LayoutH(whereProbe),
            bw, bh, Home.CardHeight(Dw.LayoutH(nameProbe), Dw.LayoutH(footProbe)), ow, greetW);
        _m = m;
        var f = Frame = Home.Layout(viewW, top, m, model.Continue != null, model.Recent.Count);

        if (model.Continue is HomeEntry c)
        {
            float textW = f.LeftW - Home.ContPadLeft - Home.ContPadRight - Home.ContGapX - bw;
            _title = Trim(c.Title, Fmt(Serif, 700, Home.TitleSize), textW);
            _sub = Trim(c.Name, Fmt(Sans, 400, Home.SubSize), textW);
            _whereR = Text("已读 " + Home.Percent(c.Progress ?? 0f), Fmt(Sans, 400, Home.WhereSize));
            _whereL = Trim(c.Folder, Fmt(Sans, 400, Home.WhereSize), f.LeftW - Home.ContPadLeft - Home.ContPadRight - Dw.LayoutW(_whereR) - 16f);
        }
        int n = model.Recent.Count;
        _name = new nint[n]; _ex = new nint[n]; _dir = new nint[n]; _when = new nint[n];
        float exLine = Home.ExcerptSize * Home.ExcerptLine;
        for (int i = 0; i < n; i++)
        {
            var e = model.Recent[i];
            float inner = f.Cards[i].W - 2f * Home.CardInsetX;
            _name[i] = Trim(e.Name, Fmt(Serif, 700, Home.NameSize), inner);
            _ex[i] = Keep(Dw.MkLayout(e.Excerpt ?? "", Fmt(Sans, 400, Home.ExcerptSize), MathF.Max(8f, inner)));
            Dw.Bind(_ex[i]);
            Dw.SetLineSpacing(_ex[i], 1, exLine, exLine * 0.8f);
            _when[i] = Text(e.When, Fmt(Sans, 400, Home.FootSize));
            _dir[i] = Trim(e.Folder, Fmt(Sans, 400, Home.FootSize), inner - Dw.LayoutW(_when[i]) - 12f);
        }
        return f;
    }

    static float Baseline(nint layout)
    {
        var lines = Dw.Lines(layout);
        return lines.Length > 0 ? lines[0].baseline : Dw.LayoutH(layout) * 0.8f;
    }

    static void Shadow(HomeRect r, float radius, float dy, float blur, float spread, float alpha) => DeskShapes.Shadow(r.X, r.Y, r.W, r.H, radius, dy, blur, spread, alpha);

    static void Rotate(float deg, float cx, float cy)
    {
        float rad = deg * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        var m = new D2D1_MATRIX_3X2_F { m11 = cos, m12 = sin, m21 = -sin, m22 = cos, dx = cx - cos * cx + sin * cy, dy = cy - sin * cx - cos * cy };
        SetTransform(&m);
    }

    static void Unrotate()
    {
        var identity = new D2D1_MATRIX_3X2_F { m11 = 1f, m22 = 1f };
        SetTransform(&identity);
    }

    public static void Paint(nint hdc, RECT client, float scale, HomePaintState s)
    {
        var f = Frame;
        if (f == null || _model == null) return;
        PBind(Rt, hdc, &client);
        ((delegate* unmanaged[Stdcall]<nint, float, float, void>)(*(nint**)Rt)[51])(Rt, 96f * scale, 96f * scale);
        PBegin(Rt);
        float viewH = (client.B - client.T) / scale;
        var area = new D2D1_RECT_F { left = 0f, top = s.Top, right = _w, bottom = viewH };
        PPushClip(Rt, &area, 0);
        ulong a = 0, b = 0;
        try
        {
            PFill(Rt, &area, BCanvas);
            PaintPaperGrain(area.left, area.top, area.right, area.bottom);
            float dy = -s.Scroll;
            if (_model.Continue != null) PaintContinue(f, dy, s.Hover.Kind == HomeTargetKind.Continue);
            if (_model.Recent.Count > 0)
            {
                PaintSection(_secRecent, f.RecentHead, dy);
                for (int i = 0; i < _model.Recent.Count; i++) PaintCard(i, f.Cards[i], dy, s.Hover.Kind == HomeTargetKind.Card && s.Hover.Index == i);
            }
            PaintActions(f, dy, s.Hover.Kind == HomeTargetKind.Open);
            if (f.ShowGreeting) PaintGreeting(f, dy);
            PaintLampGlow(0f, s.Top, _w, viewH);
        }
        finally
        {
            Unrotate();
            PPopClip(Rt);
            PEnd(Rt, &a, &b);
        }
    }

    // 小节标题：12 号、字距 .25em、淡墨，右边一条细线到头。
    static void PaintSection(nint label, HomeRect r, float dy)
    {
        float y = r.Y + dy;
        Draw(label, r.X, y, BMuted);
        float x0 = r.X + Dw.LayoutW(label) + Home.SecRuleGap, ly = MathF.Round(y + r.H / 2f);
        if (r.X + r.W > x0) { var line = new D2D1_RECT_F { left = x0, top = ly, right = r.X + r.W, bottom = ly + 1f }; PFill(Rt, &line, BHair); }
    }

    static void PaintContinue(HomeFrame f, float dy, bool hot)
    {
        PaintSection(_secCont, f.ContinueHead, dy);
        float lift = hot ? -Home.ContHoverLift : 0f;
        var r = new HomeRect(f.ContinueCard.X, f.ContinueCard.Y + dy + lift, f.ContinueCard.W, f.ContinueCard.H);
        if (hot) { Shadow(r, Home.ContRadius, 2f, 4f, 0f, 1f); Shadow(r, Home.ContRadius, 24f, 48f, -28f, 1f); }
        else { Shadow(r, Home.ContRadius, 1f, 2f, 0f, 1f); Shadow(r, Home.ContRadius, 16f, 36f, -26f, 1f); }
        FillRound(r.X, r.Y, r.W, r.H, Home.ContRadius, BCanvas);
        StrokeRound(r.X, r.Y, r.W, r.H, Home.ContRadius, BEdge, 1f);
        Ribbon(r.X - 1f, r.Y - 1f);
        float x = r.X + Home.ContPadLeft, y = r.Y + Home.ContPadTop;
        Draw(_title, x, y, BInk);
        Draw(_sub, x, y + Dw.LayoutH(_title) + Home.SubGap, BInk2);
        var btn = f.ContinueButton;
        PrimaryButton(btn.X, btn.Y + dy + lift, btn.W, btn.H, hot);
        float row = MathF.Max(_m.ContTextH, _m.ButtonH);
        float py = y + row + Home.ContGapY + Home.ProgGap;
        float pw = r.W - Home.ContPadLeft - Home.ContPadRight;
        var track = new D2D1_RECT_F { left = x, top = py, right = x + pw, bottom = py + Home.ProgH };
        PFill(Rt, &track, BHair);
        var done = new D2D1_RECT_F { left = x, top = py, right = x + pw * Math.Clamp(_model.Continue.Progress ?? 0f, 0f, 1f), bottom = py + Home.ProgH };
        PFill(Rt, &done, BAccent);
        float wy = py + Home.ProgH + Home.ContGapY + Home.WhereGap;
        Draw(_whereL, x, wy, BMuted);
        Draw(_whereR, x + pw - Dw.LayoutW(_whereR), wy, BMuted);
    }

    // 书签角：10×40，下端剪出燕尾（演示 .cont::before 的 clip-path）。
    static void Ribbon(float x, float y)
    {
        var top = new D2D1_RECT_F { left = x, top = y, right = x + Home.RibbonW, bottom = y + 32f };
        PFill(Rt, &top, BAccent);
        for (float t = 0f; t < 8f; t += 0.5f)
        {
            float w = Home.RibbonW / 2f * (1f - (t + 0.25f) / 8f);
            var l = new D2D1_RECT_F { left = x, top = y + 32f + t, right = x + w, bottom = y + 32.5f + t };
            var r = new D2D1_RECT_F { left = x + Home.RibbonW - w, top = y + 32f + t, right = x + Home.RibbonW, bottom = y + 32.5f + t };
            PFill(Rt, &l, BAccent); PFill(Rt, &r, BAccent);
        }
    }

    static void PrimaryButton(float x, float y, float w, float h, bool hot)
    {
        FillRound(x, y, w, h, Home.ButtonRadius, BAccent);
        if (hot && !HighContrast) { SetOpacity(BGlint, 0.08f); FillRound(x, y, w, h, Home.ButtonRadius, BGlint); SetOpacity(BGlint, 1f); }
        float tx = x + Home.ButtonInsetX;
        Draw(_btn, tx, y + (h - Dw.LayoutH(_btn)) / 2f, HighContrast ? BCanvas : BGlint);
        float ix = tx + Dw.LayoutW(_btn) + Home.ButtonIconGap, iy = y + (h - Home.ButtonIcon) / 2f, k = Home.ButtonIcon / 20f;
        nint ink = HighContrast ? BCanvas : BGlint;
        StrokeLine(ix + 4f * k, iy + 10f * k, ix + 15f * k, iy + 10f * k, ink, 1.7f);
        StrokeLine(ix + 11f * k, iy + 6f * k, ix + 15f * k, iy + 10f * k, ink, 1.7f);
        StrokeLine(ix + 15f * k, iy + 10f * k, ix + 11f * k, iy + 14f * k, ink, 1.7f);
    }

    static void PaintCard(int i, HomeRect cell, float dy, bool hot)
    {
        var r = new HomeRect(cell.X, cell.Y + dy - (hot ? Home.CardHoverLift : 0f), cell.W, cell.H);
        float tilt = hot || HighContrast ? 0f : Home.CardTilt[i % Home.CardTilt.Length];
        if (tilt != 0f) Rotate(tilt, r.X + r.W / 2f, r.Y + r.H / 2f);
        if (hot) { Shadow(r, Home.CardRadius, 2f, 4f, 0f, 1f); Shadow(r, Home.CardRadius, 20f, 40f, -24f, 1f); }
        else { Shadow(r, Home.CardRadius, 1f, 2f, 0f, 1f); Shadow(r, Home.CardRadius, 10f, 24f, -20f, 1f); }
        FillRound(r.X, r.Y, r.W, r.H, Home.CardRadius, BCanvas);
        StrokeRound(r.X, r.Y, r.W, r.H, Home.CardRadius, BEdge, 1f);
        float x = r.X + Home.CardInsetX, y = r.Y + Home.CardPadTop, inner = r.W - 2f * Home.CardInsetX;
        Draw(_name[i], x, y, BInk);
        y += Dw.LayoutH(_name[i]) + Home.NameGap;
        var clip = new D2D1_RECT_F { left = x, top = y, right = x + inner, bottom = y + Home.ExcerptHeight };
        PPushClip(Rt, &clip, 0);
        Draw(_ex[i], x, y, BInk2);
        PPopClip(Rt);
        y += Home.ExcerptHeight + Home.ExcerptGap;
        for (float dx = 0f; dx < inner; dx += 6f)   // 1px 虚线（演示 border-top:1px dashed）
        {
            var dash = new D2D1_RECT_F { left = x + dx, top = y, right = x + MathF.Min(inner, dx + 3f), bottom = y + 1f };
            PFill(Rt, &dash, BHair);
        }
        y += 1f + Home.FootPad;
        Draw(_dir[i], x, y, BMuted);
        Draw(_when[i], x + inner - Dw.LayoutW(_when[i]), y, BMuted);
        if (tilt != 0f) Unrotate();
    }

    static void PaintActions(HomeFrame f, float dy, bool hot)
    {
        var r = f.Actions;
        DashedRound(r.X, r.Y + dy, r.W, r.H, Home.ActionsRadius, BHairZone, Home.ActionsStroke);
        var o = f.OpenButton;
        float y = o.Y + dy;
        FillRound(o.X, y, o.W, o.H, Home.ButtonRadius, BCanvas);
        StrokeRound(o.X, y, o.W, o.H, Home.ButtonRadius, hot ? BAccent : BEdge, 1f);
        nint ink = hot ? BAccent : BInk;
        float ix = o.X + Home.ButtonInsetX, iy = y + (o.H - Home.ButtonIcon) / 2f, k = Home.ButtonIcon / 20f;
        Folder(ix, iy, k, ink);
        Draw(_open, ix + Home.ButtonIcon + Home.ButtonIconGap, y + (o.H - Dw.LayoutH(_open)) / 2f, ink);
        Draw(_drop, o.X + o.W + Home.ActionsGap, y + (o.H - Dw.LayoutH(_drop)) / 2f, BMuted);
    }

    // 演示的文件夹图标（20 格），弧角用短折线近似。
    static void Folder(float x, float y, float k, nint ink)
    {
        (float, float)[] p = { (3f, 6.5f), (3f, 15f), (3.4f, 16f), (4.5f, 16.5f), (15.5f, 16.5f), (16.6f, 16f), (17f, 15f), (17f, 8f), (16.6f, 7f), (15.5f, 6.5f), (10f, 6.5f), (8.3f, 4.5f), (4.5f, 4.5f), (3.4f, 5f), (3f, 6f) };
        for (int i = 0; i + 1 < p.Length; i++) StrokeLine(x + p[i].Item1 * k, y + p[i].Item2 * k, x + p[i + 1].Item1 * k, y + p[i + 1].Item2 * k, ink, 1.5f);
    }

    // 虚线圆角框：沿周长走，每段 3 倍线宽实、3 倍线宽空（与浏览器 dashed 相近）。
    static void DashedRound(float x, float y, float w, float h, float r, nint brush, float width)
    {
        float i = width / 2f;
        x += i; y += i; w -= width; h -= width; r = MathF.Max(0f, r - i);
        float dash = 3f * width, straightW = w - 2f * r, straightH = h - 2f * r, arc = MathF.PI * r / 2f;
        float total = 2f * (straightW + straightH) + 4f * arc;
        (float X, float Y) At(float d)
        {
            d %= total;
            float[] seg = { straightW, arc, straightH, arc, straightW, arc, straightH, arc };
            for (int s = 0; s < 8; s++)
            {
                if (d > seg[s]) { d -= seg[s]; continue; }
                float t = seg[s] <= 0f ? 0f : d / seg[s];
                switch (s)
                {
                    case 0: return (x + r + t * straightW, y);
                    case 1: return Corner(x + w - r, y + r, -90f + 90f * t);
                    case 2: return (x + w, y + r + t * straightH);
                    case 3: return Corner(x + w - r, y + h - r, 90f * t);
                    case 4: return (x + w - r - t * straightW, y + h);
                    case 5: return Corner(x + r, y + h - r, 90f + 90f * t);
                    case 6: return (x, y + h - r - t * straightH);
                    default: return Corner(x + r, y + r, 180f + 90f * t);
                }
            }
            return (x + r, y);
        }
        (float, float) Corner(float cx, float cy, float deg) { float a = deg * MathF.PI / 180f; return (cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)); }
        int count = Math.Max(1, (int)MathF.Round(total / (2f * dash)));
        float period = total / count, on = period / 2f;
        for (int c = 0; c < count; c++)
        {
            float d0 = c * period;
            var prev = At(d0);
            for (float d = d0 + 1f; d <= d0 + on + 0.01f; d += 1f)
            {
                var next = At(MathF.Min(d, d0 + on));
                StrokeLine(prev.X, prev.Y, next.X, next.Y, brush, width);
                prev = next;
            }
        }
    }

    static void PaintGreeting(HomeFrame f, float dy)
    {
        float right = f.GreetX + _m.GreetW, top = f.GreetY + dy;
        float col = Home.GreetSize * Home.GreetLine, gx = right - col;
        float adv = Home.GreetSize * (1f + Home.GreetSpacing);
        for (int i = 0; i < _greet.Length; i++)
            Draw(_greet[i], gx + (col - Dw.LayoutW(_greet[i])) / 2f, top + i * adv + (Home.GreetSize - Dw.LayoutH(_greet[i])) / 2f, BInk);
        float dx = gx - Home.GreetGap - _dateColW, dTop = top + Home.DatePadTop, dAdv = Home.DateSize * (1f + Home.DateSpacing);
        for (int i = 0; i < _date.Length; i++)
            Draw(_date[i], dx + (_dateColW - Dw.LayoutW(_date[i])) / 2f, dTop + i * dAdv + (Home.DateSize - Dw.LayoutH(_date[i])) / 2f, BInk2);
        float sx = dx - Home.GreetGap - Home.StampW, sy = top + Home.StampTop;
        float sAdv = Home.StampSize * (1f + Home.StampSpacing), sh = 2f * Home.StampPadY + _stamp.Length * sAdv;
        Rotate(Home.StampTilt, sx + Home.StampW / 2f, sy + sh / 2f);
        FillRound(sx, sy, Home.StampW, sh, Home.StampRadius, BAccent);
        if (!HighContrast) { SetOpacity(BGlint, 0.5f); StrokeRound(sx, sy, Home.StampW, sh, Home.StampRadius, BGlint, 1.5f); SetOpacity(BGlint, 1f); }
        for (int i = 0; i < _stamp.Length; i++)
            Draw(_stamp[i], sx + (Home.StampW - Dw.LayoutW(_stamp[i])) / 2f, sy + Home.StampPadY + i * sAdv + (Home.StampSize - Dw.LayoutH(_stamp[i])) / 2f, HighContrast ? BCanvas : BGlint);
        Unrotate();
    }
}
