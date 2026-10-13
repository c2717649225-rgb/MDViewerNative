using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MDViewerNative;

// Native print/PDF path. Each page is rasterized through the existing document renderer
// into a bounded memory surface, then transferred to the selected printer DC.
internal static unsafe class DocumentPrinter
{
    [StructLayout(LayoutKind.Sequential)]
    struct PrintDialog
    {
        public uint Size; public nint Owner, DevMode, DevNames, DC; public uint Flags;
        public ushort From, To, Minimum, Maximum, Copies;
        public nint Instance, Custom, PrintHook, SetupHook, PrintName, SetupName, PrintTemplate, SetupTemplate;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DocInfo { public int Size; public nint Name, Output, DataType; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize;
        public int X, Y; public uint Used, Important, Color;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] static extern bool PrintDlgW(ref PrintDialog dialog);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern nint CreateDCW(string driver, string device, string output, nint mode);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int StartDocW(nint dc, ref DocInfo info);
    [DllImport("gdi32.dll")] static extern int StartPage(nint dc);
    [DllImport("gdi32.dll")] static extern int EndPage(nint dc);
    [DllImport("gdi32.dll")] static extern int EndDoc(nint dc);
    [DllImport("gdi32.dll")] static extern int AbortDoc(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern int StretchDIBits(nint dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, nint bits, ref BitmapInfo info, uint usage, uint operation);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint value);

    public static bool Print(nint owner, DocView document, string title, string pdfDestination = null)
    {
        using var operation = NativeForm.EnterScope();
        nint dc = 0, memoryDc = 0, bitmap = 0, oldBitmap = 0;
        var dialog = new PrintDialog
        {
            Size = (uint)Marshal.SizeOf<PrintDialog>(),
            Owner = owner,
            Flags = 0x100 | 0x4 | 0x40000,
            From = 1,
            To = 999,
            Minimum = 1,
            Maximum = 999,
            Copies = 1
        };
        string temporary = null;
        FileVersion expected = FileVersion.Missing;
        float oldWidth = document.LayoutWidth, oldScale = RenderResources.Scale;
        bool oldToc = document.ShowToc, dark = RenderResources.DarkMode, started = false;
        var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>() };
        try
        {
            if (pdfDestination != null)
            {
                pdfDestination = Path.GetFullPath(pdfDestination);
                expected = AtomicFile.Observe(pdfDestination);
                if (expected.Exists && !DocumentDialogs.ConfirmOverwrite(owner, pdfDestination)) return false;
                temporary = Path.Combine(Path.GetDirectoryName(pdfDestination), ".mdv-print-" + Guid.NewGuid().ToString("N") + ".pdf");
                dc = CreateDCW("WINSPOOL", "Microsoft Print to PDF", null, 0);
                if (dc == 0) throw new InvalidOperationException("Microsoft Print to PDF 不可用；未安装或更改任何打印机。");
                info.Output = Marshal.StringToHGlobalUni(temporary);
            }
            else
            {
                if (!PrintDlgW(ref dialog)) return false;
                dc = dialog.DC;
            }
            int width = GetDeviceCaps(dc, 8), height = GetDeviceCaps(dc, 10);
            int marginX = Math.Max(1, GetDeviceCaps(dc, 88) / 2), marginY = Math.Max(1, GetDeviceCaps(dc, 90) / 2);
            int printableWidth = width - 2 * marginX, printableHeight = height - 2 * marginY;
            if (printableWidth <= 0 || printableHeight <= 0) throw new InvalidOperationException("打印区域无效。");
            const float pageWidth = 800;
            float pageHeight = pageWidth * printableHeight / printableWidth;
            int pixelWidth = Math.Min(1600, printableWidth), pixelHeight = Math.Max(1, (int)(pixelWidth * (double)printableHeight / printableWidth));
            if ((long)pixelWidth * pixelHeight > 8 * 1024 * 1024) throw new InvalidOperationException("打印表面超过内存预算。");
            var pixels = new BitmapInfo { Size = 40, Width = pixelWidth, Height = -pixelHeight, Planes = 1, Bits = 32, ImageSize = (uint)(pixelWidth * pixelHeight * 4) };
            memoryDc = CreateCompatibleDC(dc);
            bitmap = CreateDIBSection(memoryDc, ref pixels, 0, out nint bits, 0, 0);
            if (memoryDc == 0 || bitmap == 0 || bits == 0) throw new System.ComponentModel.Win32Exception();
            oldBitmap = SelectObject(memoryDc, bitmap);
            // Paper cannot scroll sideways: wide code must wrap here or be lost past the
            // page edge. Set before OnWidth so the relayout it triggers measures the
            // wrapped heights, otherwise the page count below is computed from the wrong
            // total height. Restored in the finally block together with the width.
            document.WrapCodeForPaper = true;
            document.SetToc(false); document.OnWidth(pageWidth);
            document.Relayout();
            RenderResources.Scale = pixelWidth / pageWidth; RenderResources.ApplyClassicTheme(false);
            int pages = Math.Max(1, (int)Math.Ceiling(document.TotalH / pageHeight));
            if (pages > 999) throw new InvalidOperationException("文档超过 999 页，请缩小导出范围。");
            info.Name = Marshal.StringToHGlobalUni(title ?? "Markdown");
            if (StartDocW(dc, ref info) <= 0) throw new InvalidOperationException("无法开始打印任务。");
            started = true;
            int first = (dialog.Flags & 2) != 0 ? Math.Clamp(dialog.From, 1, pages) : 1;
            int last = (dialog.Flags & 2) != 0 ? Math.Clamp(dialog.To, first, pages) : pages;
            for (int page = first; page <= last; page++)
            {
                // Each page is a full re-render; a long job must stay interruptible. The
                // finally block already calls AbortDoc, so a cancelled job never leaves a
                // half-spooled document on the printer.
                DocumentWork.Poll(pixelWidth * pixelHeight / 1024);
                if (StartPage(dc) <= 0) throw new IOException("无法开始打印页。");
                var client = new RECT { R = pixelWidth, B = pixelHeight };
                document.Paint(client, memoryDc, (page - 1) * pageHeight, pageHeight, 0);
                if (StretchDIBits(dc, marginX, marginY, printableWidth, printableHeight, 0, 0, pixelWidth, pixelHeight, bits, ref pixels, 0, 0x00CC0020) == -1)
                    throw new IOException("无法传输打印页。");
                if (EndPage(dc) <= 0) throw new IOException("无法完成打印页。");
            }
            if (EndDoc(dc) <= 0) throw new IOException("打印任务失败。");
            started = false;
            if (temporary != null) AdoptSpooledPdf(temporary, pdfDestination, expected);
            return true;
        }
        finally
        {
            if (started && dc != 0) AbortDoc(dc);
            if (oldBitmap != 0 && memoryDc != 0) SelectObject(memoryDc, oldBitmap);
            if (bitmap != 0) Externs.DeleteObject(bitmap);
            if (memoryDc != 0) DeleteDC(memoryDc);
            if (dc != 0) DeleteDC(dc);
            if (dialog.DevMode != 0) GlobalFree(dialog.DevMode);
            if (dialog.DevNames != 0) GlobalFree(dialog.DevNames);
            Marshal.FreeHGlobal(info.Name); Marshal.FreeHGlobal(info.Output);
            RenderResources.Scale = oldScale; RenderResources.ApplyTheme(dark);
            RestoreReadingLayout(document, oldWidth, oldToc);
            if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    // File adoption is independent of printer HWND/DC. Keep the pre-print version
    // supplied by the caller; re-observing here would bypass overwrite confirmation.
    internal static void AdoptSpooledPdf(string temporary, string destination, FileVersion expected, Action wait = null)
    {
        byte[] bytes = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    using var file = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (file.Length > 0 && file.Length <= 256 * 1024 * 1024)
                    { bytes = new byte[file.Length]; file.ReadExactly(bytes); break; }
                }
            }
            catch (IOException) { }
            if (wait == null) Thread.Sleep(50); else wait();
        }
        // This is a signature gate, not a full PDF structural validator.
        if (bytes == null || bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new IOException("PDF 输出尚未完成；目标文件未被替换。");
        AtomicFile.Write(destination, bytes, expected);
    }

    // Shared with the windowless regression: this is the actual finally-path restore,
    // not a second implementation or a simulated print job.
    internal static void RestoreReadingLayout(DocView document, float width, bool toc)
    {
        // The original cancellation remains sticky for the caller, but must not interrupt
        // restoring the reading layout or prevent the finally block deleting its temp PDF.
        using var cleanup = DocumentWork.Suspend();
        document.WrapCodeForPaper = false;
        document.SetToc(toc);
        document.OnWidth(width);
        document.Relayout();
    }

}
