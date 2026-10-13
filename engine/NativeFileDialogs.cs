using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MDViewerNative;

// Native resource ownership only: selection does not read, save, or replace a session.
internal static unsafe class NativeFileDialogs
{
    public static string Open(IntPtr owner) => Pick(owner, false, null)?.FirstOrDefault();
    public static string[] OpenMany(IntPtr owner) => Pick(owner, false, null, multiple: true);
    public static string OpenImage(IntPtr owner) => Pick(owner, false, null, label: "图片", filter: "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg")?.FirstOrDefault();
    public static string Save(IntPtr owner, string suggestedPath) => Pick(owner, true, suggestedPath)?.FirstOrDefault();
    public static string SaveHtml(IntPtr owner, string suggestedPath) => Pick(owner, true, suggestedPath, label: "HTML", filter: "*.html;*.htm", suffix: "html")?.FirstOrDefault();
    public static string SavePdf(IntPtr owner, string suggestedPath) => Pick(owner, true, suggestedPath, label: "PDF", filter: "*.pdf", suffix: "pdf")?.FirstOrDefault();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid iid, out IntPtr item);

    static string[] Pick(IntPtr owner, bool save, string suggestedPath, bool multiple = false, string label = "Markdown", string filter = null, string suffix = "md")
    {
        using var modal = NativeForm.EnterScope();
        Guid clsid = new(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
        Guid iid = new(save ? "84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB" : "D57C7288-D4AD-4768-BE02-9D969532D960");
        Marshal.ThrowExceptionForHR(Wic.CoCreateInstance(in clsid, IntPtr.Zero, 1, in iid, out IntPtr dialog));
        IntPtr name = IntPtr.Zero, pattern = IntPtr.Zero, allName = IntPtr.Zero, allPattern = IntPtr.Zero;
        IntPtr filename = IntPtr.Zero, extension = IntPtr.Zero;
        try
        {
            name = Marshal.StringToHGlobalUni(label);
            pattern = Marshal.StringToHGlobalUni(filter ?? (save ? "*.md;*.markdown" : "*.md;*.markdown;*.txt"));
            allName = Marshal.StringToHGlobalUni("所有文件");
            allPattern = Marshal.StringToHGlobalUni("*.*");
            IntPtr* filters = stackalloc IntPtr[] { name, pattern, allName, allPattern };
            var vt = *(IntPtr**)dialog;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vt[4])(dialog, 2, filters));
            uint options = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vt[10])(dialog, &options));
            // Overwrite confirmation is handled by the host after capturing the destination version.
            options = (options & ~2u) | 0x40u | 0x800u;
            if (!save) options |= 0x1000;
            if (multiple && !save) options |= 0x200;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[9])(dialog, options));
            if (save)
            {
                filename = Marshal.StringToHGlobalUni(Path.GetFileName(suggestedPath));
                extension = Marshal.StringToHGlobalUni(suffix);
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[15])(dialog, filename));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[22])(dialog, extension));
                string directory = Path.GetDirectoryName(suggestedPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Guid itemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, IntPtr.Zero, in itemIid, out IntPtr folder));
                    try { Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[12])(dialog, folder)); }
                    finally { Marshal.Release(folder); }
                }
            }
            int shown = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[3])(dialog, owner);
            if (shown == unchecked((int)0x800704C7)) return null; // User cancelled.
            Marshal.ThrowExceptionForHR(shown);
            if (multiple && !save)
            {
                nint array = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)vt[27])(dialog, &array));
                try
                {
                    var methods = *(nint**)array; uint count = 0;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)methods[7])(array, &count));
                    if (count > 500) throw new InvalidOperationException("一次最多打开 500 个文档。");
                    var paths = new List<string>();
                    for (uint index = 0; index < count; index++)
                    {
                        nint selected = 0, path = 0;
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)methods[8])(array, index, &selected));
                        try
                        {
                            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)selected)[5])(selected, 0x80058000, &path));
                            paths.Add(Marshal.PtrToStringUni(path));
                        }
                        finally { Marshal.FreeCoTaskMem(path); Marshal.Release(selected); }
                    }
                    return paths.ToArray();
                }
                finally { Marshal.Release(array); }
            }
            IntPtr item = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[20])(dialog, &item));
            IntPtr value = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)item)[5])(item, 0x80058000, &value));
                return new[] { Marshal.PtrToStringUni(value) };
            }
            finally { Marshal.FreeCoTaskMem(value); Marshal.Release(item); }
        }
        finally
        {
            Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(pattern);
            Marshal.FreeHGlobal(allName); Marshal.FreeHGlobal(allPattern);
            Marshal.FreeHGlobal(filename); Marshal.FreeHGlobal(extension);
            Marshal.Release(dialog);
        }
    }
}
