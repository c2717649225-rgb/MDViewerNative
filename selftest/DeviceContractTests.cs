using System;
using MDViewerNative;

internal static class DeviceContractTests
{
    // Per-window scale contracts, not a claim that the physical desktop was moved
    // between monitors or changed to 125/150/200 percent system scaling.
    public static unsafe void Run(Action<string, bool, string> check)
    {
        nint parent = Externs.CreateWindowExW(0, "STATIC", "scale-contract", 0, 0, 0, 600, 450, 0, 0, Externs.GetModuleHandleW(null), 0);
        if (parent == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var editor = new EditorControl(parent, 1f, () => { }, () => null);
            string text = "中文 😀 source\r\nsecond line\r\n";
            Externs.SetWindowTextW(editor.Handle, text);
            Externs.SendMessageW(editor.Handle, 0x00B1, 0, 0);
            fixed (char* p = "x") Externs.SendMessageW(editor.Handle, 0x00C2, 1, (nint)p);
            Externs.SendMessageW(editor.Handle, 0x00B1, 2, 7);
            var expected = Externs.EditSelection(editor.Handle);
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Externs.MoveWindow(parent, 0, 0, (int)(560 * scale), (int)(450 * scale), false);
                editor.SetScale(scale);
                editor.Resize(80f);
                var bounds = editor.CapturePosition();
                void C(string label, bool pass) => check("ScaleContract/" + scale + "/" + label, pass, "owned native EDIT; no desktop setting changed");
                C("scale and font metrics are finite", editor.Scale == scale && float.IsFinite(editor.LineHeight) && editor.LineHeight > 0);
                C("Unicode source survives resizing", Externs.WindowText(editor.Handle) == "x" + text);
                C("selection survives resizing", Externs.EditSelection(editor.Handle) == expected);
                C("undo survives resizing", Externs.SendMessageW(editor.Handle, 0x00C6, 0, 0) != 0);
                editor.RestorePosition(bounds);
                C("position restoration remains bounded", editor.CapturePosition().Start == bounds.Start && editor.CapturePosition().End == bounds.End);
            }
        }
        finally { Externs.DestroyWindow(parent); }
    }
}
