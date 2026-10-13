using System;
using System.Linq;
using static MDViewerNative.RenderResources;

namespace MDViewerNative;

unsafe partial class DocView
{
    // Inline graphics retain text-flow alt labels; their image action uses the same
    // bounded loader/lightbox, rather than treating the image file as an external app.
    public bool IsInlineImage(string source)
    {
        bool Has(Block block)
        {
            var runs = block switch
            {
                Para p => p.Inlines,
                Heading h => h.Inlines,
                Quote q => q.Inlines,
                ListItem item => item.Inlines,
                HtmlDetails details => details.Summary,
                _ => null
            };
            if (runs != null && runs.Any(r => r.Img && r.Link == source)) return true;
            if (block is Table table && table.Header.Concat(table.Rows.SelectMany(row => row)).Any(cell => cell.Any(r => r.Img && r.Link == source))) return true;
            return block is ListItem { Sub: not null } list && list.Sub.Any(Has);
        }
        return _blocks.Any(Has);
    }

    public void LoadImageForViewing(string source) => _images.Load(source);

    // The same table geometry serves top-level and list-contained blocks.
    private void DrawRichTable(Table table, Ent entry, float left, float top, int baseChar)
    {
        int columns = table.Header.Length;
        int offset = baseChar;
        float y = top;
        // A table wider than the column keeps its natural widths and slides sideways instead
        // of being squeezed. The first column stays put so the row is always identifiable.
        // Clipping keys off "overflows", not "currently scrolled": at ScrollX 0 the far
        // columns already extend past the viewport and would paint into the margin (30).
        bool overflows = entry.OverflowW > 0.5f;
        float shift = overflows ? entry.ScrollX : 0f;
        float frozen = overflows ? entry.FrozenW : 0f;
        float viewportW = TableViewportW(entry);
        // Tables that fit keep their original full-width backing exactly as before.
        float visibleRight = left + (overflows ? viewportW : entry.RichWidth);
        for (int row = 0; row <= table.Rows.Length; row++)
        {
            float height = row == 0 ? entry.HeadH : entry.RowHs[row - 1];
            int rowStartOffset = offset;
            if (row > 1 && RenderResources.Reading.IsDesk)   // U3b desk:不铺斑马纹,体行之间一条细线(演示 td 下边线)
                TableRule(left, visibleRight, y, 1f, false, BHair);
            else if (row > 0 && (row & 1) == 0 && !RenderResources.Reading.IsDesk)   // E3:表头不铺底;偶数体行 SurfaceAlt,上下边落像素网格
            {
                var rect = new D2D1_RECT_F { left = left, top = SnapDip(y), right = visibleRight, bottom = SnapDip(y + height) };
                PFill(Rt, &rect, BZebra);
            }
            float x = left - shift;
            // One clip for the whole scrolling strip: cells that slid out of view must not
            // paint over the frozen column or past the right edge of the viewport.
            if (overflows)
            {
                var strip = new D2D1_RECT_F { left = left + frozen, top = y, right = visibleRight, bottom = y + height };
                PPushClip(Rt, &strip, 0);
            }
            for (int col = 0; col < columns; col++)
            {
                nint layout = entry.Cells[row * columns + col];
                int length = entry.CellTexts[row][col].Length;
                if (layout != 0)
                {
                    // The frozen column is drawn separately, unshifted, after the strip.
                    if (frozen <= 0f || col != 0)
                    {
                        float cellX = x + 8;
                        FillSearchOn(layout, offset, length, cellX, y + 7);
                        FillSelectionOn(layout, offset, length, cellX, y + 7);
                        PDraw(Rt, new D2D1_POINT_2F { x = cellX, y = y + 7 }, layout, BInk, 0);
                    }
                }
                offset += length + (col + 1 < columns ? 3 : 0);
                x += entry.ColWs[col];
            }
            if (overflows) PPopClip(Rt);
            // Frozen column: outside the clip, so it is never covered by scrolled cells.
            if (frozen > 0f && entry.Cells[row * columns] != 0)
            {
                int frozenLen = entry.CellTexts[row][0].Length;
                FillSearchOn(entry.Cells[row * columns], rowStartOffset, frozenLen, left + 8, y + 7);
                FillSelectionOn(entry.Cells[row * columns], rowStartOffset, frozenLen, left + 8, y + 7);
                PDraw(Rt, new D2D1_POINT_2F { x = left + 8, y = y + 7 }, entry.Cells[row * columns], BInk, 0);
                // Divider only once actually scrolled, so an untouched table looks unchanged.
                if (shift > 0f) Line(left + frozen, y, left + frozen, y + height);
            }
            offset++;
            y += height;
        }
        // E3 三线表:顶/底 1.5 DIP、表头下 1 DIP,HairlineZone,物理像素取整并吸附;画在行底之上。
        TableRule(left, visibleRight, top, 1.5f, false);
        TableRule(left, visibleRight, top + entry.HeadH, 1f, false);
        TableRule(left, visibleRight, y, 1.5f, true);
        // 溢出提示放在底部规则线之下 3 DIP,不与规则线挤成双线(表后外边距 ≥ 0.75 行,放得下)。
        if (overflows) DrawCodeScrollbar(entry, left, visibleRight, y, viewportW, y + 4f);
    }

    private static float SnapDip(float v) { float s = Scale > 0f ? Scale : 1f; return MathF.Round(v * s) / s; }

    // 规则线粗 round(dip×scale) 物理像素(至少 1);above=true 时线落在边界之上(表格底线),否则在边界之下。
    private void TableRule(float left, float right, float edge, float dip, bool above, nint brush = 0)
    {
        float s = Scale > 0f ? Scale : 1f, t = MathF.Max(1f, MathF.Round(dip * s, MidpointRounding.AwayFromZero)) / s, e = SnapDip(edge);
        var rule = new D2D1_RECT_F { left = left, top = above ? e - t : e, right = right, bottom = above ? e : e + t };
        PFill(Rt, &rule, brush != 0 ? brush : BHairZone);
    }

    private string RichTableLink(Table table, Ent entry, float left, float top, float mx, float my)
    {
        // Painting clips every table to this viewport, including list-contained tables.
        // A hidden cell must not remain clickable in the blank margin beyond that clip.
        if (mx < left || mx >= left + TableViewportW(entry)) return null;
        int offset = 0, columns = table.Header.Length;
        float y = top;
        // Same correction as the hit test: links in a scrolled table would otherwise be
        // picked up from whatever cell happens to sit at the unshifted coordinate (30).
        float shift = entry.OverflowW > 0.5f ? entry.ScrollX : 0f;
        float frozen = shift > 0f ? entry.FrozenW : 0f;
        float probeX = shift > 0f && mx - left >= frozen ? mx + shift : mx;
        for (int row = 0; row <= table.Rows.Length; row++)
        {
            float height = row == 0 ? entry.HeadH : entry.RowHs[row - 1];
            float x = left;
            for (int col = 0; col < columns; col++)
            {
                int length = entry.CellTexts[row][col].Length;
                nint layout = entry.Cells[row * columns + col];
                if (layout != 0 && probeX >= x && probeX < x + entry.ColWs[col] && my >= y && my < y + height)
                {
                    var hit = (delegate* unmanaged[Stdcall]<nint, float, float, int*, int*, DWRITE_HIT_TEST_METRICS*, int>)(*(nint**)layout)[64];
                    int trailing = 0, inside = 0;
                    DWRITE_HIT_TEST_METRICS metrics = default;
                    if (hit(layout, probeX - x - 8, my - y - 7, &trailing, &inside, &metrics) < 0 || inside == 0) return null;
                    int position = offset + (int)metrics.textPosition;
                    foreach (var link in entry.Links) if (position >= link.s && position < link.s + link.l) return link.url;
                    return null;
                }
                offset += length + (col + 1 < columns ? 3 : 0);
                x += entry.ColWs[col];
            }
            offset++;
            y += height;
        }
        return null;
    }

    private void DrawRichImage(Image image, Ent entry, float left, float top, float documentY)
    {
        if (entry.Bmp != 0 && entry.ImgH > 0)
        {
            var rect = new D2D1_RECT_F { left = left, top = top + 3, right = left + entry.ImgW, bottom = top + 3 + entry.ImgH };
            ImageRects.Add((left, documentY + 3, entry.ImgW, entry.ImgH, image.Src));
            PDrawBmp(Rt, _images.CurrentFrame(image.Src), &rect, 1f, 1, IntPtr.Zero, IntPtr.Zero);   // 动画(34):按当前帧绘制,布局仍以首帧尺寸为准
            FillLine(rect.left, rect.top, rect.right, rect.top, BHair, 1f);
            FillLine(rect.left, rect.bottom, rect.right, rect.bottom, BHair, 1f);
            FillLine(rect.left, rect.top, rect.left, rect.bottom, BHair, 1f);
            FillLine(rect.right, rect.top, rect.right, rect.bottom, BHair, 1f);
        }
        else if (entry.L != 0)
        {
            PDraw(Rt, new D2D1_POINT_2F { x = left, y = top }, entry.L, BInk, 0);
            ImageRects.Add((left, documentY, MathF.Min(entry.RichWidth, Dw.LayoutW(entry.L)), entry.H, image.Src));
        }
    }
}
