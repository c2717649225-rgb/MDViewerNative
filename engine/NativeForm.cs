using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MDViewerNative.NativeMenus;

namespace MDViewerNative;

internal enum FormFieldKind { Text, Check, Choice }
internal sealed record FormField(string Label, string Value = "", FormFieldKind Kind = FormFieldKind.Text, string[] Choices = null);
internal sealed record FormResult(int Action, string[] Values, int[] Selected);

// Small system-control dialogs for document operations, not a second UI framework.
internal sealed unsafe class NativeForm
{
    private static bool _registered;
    private static int _modalDepth;
    public static bool IsActive => _modalDepth > 0;
    public static event Action Dismissed;
    public static IDisposable EnterScope() => new ModalScope();
    private sealed class ModalScope : IDisposable
    {
        private bool _closed;
        public ModalScope() => _modalDepth++;
        public void Dispose() { if (_closed) return; _closed = true; if (--_modalDepth == 0) Dismissed?.Invoke(); }
    }
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll")] private static extern int ImmGetCompositionStringW(nint context, uint index, nint buffer, uint length);
    private static bool Composing(nint window)
    {
        nint context = ImmGetContext(window); if (context == 0) return false;
        try { return ImmGetCompositionStringW(context, 8, 0, 0) > 0; }
        finally { ImmReleaseContext(window, context); }
    }
    private nint _window, _owner, _font, _list;
    private readonly List<nint> _inputs = new();
    private FormField[] _fields;
    private FormResult _result;
    private bool _done;
    private GCHandle _self;
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsDialogMessageW(nint window, ref MSG message);
    [DllImport("user32.dll")] private static extern nint GetFocus();

    internal static string Input(nint owner, string title, string label, string initial = "") =>
        Show(owner, title, "", new[] { new FormField(label, initial) }, null, false, new[] { "确定", "取消" })?.Values[0];

    internal static FormResult Show(nint owner, string title, string description, FormField[] fields,
        string[] items = null, bool multiple = false, string[] buttons = null) =>
        new NativeForm().Run(owner, title, description, fields ?? Array.Empty<FormField>(), items, multiple, buttons ?? new[] { "确定", "取消" });

    private FormResult Run(nint owner, string title, string description, FormField[] fields, string[] items, bool multiple, string[] buttons)
    {
        if (fields.Length > 16 || buttons.Length is < 2 or > 5 || (items?.Length ?? 0) > 2000)
            throw new ArgumentException("Dialog content exceeds its bounded layout.");
        using var modal = EnterScope();
        _owner = owner; _fields = fields;
        float scale = Math.Max(.75f, Externs.GetDpiForWindow(owner) / 96f);
        bool enabled = IsWindowEnabled(owner);
        nint focus = GetFocus();
        bool quit = false; int quitCode = 0;
        try
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    hInstance = Externs.GetModuleHandleW(null),
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                    hCursor = Externs.LoadCursorW(0, (nint)32512),
                    hbrBackground = (nint)16,
                    lpszClassName = "MDVNativeForm"
                };
                if (Externs.RegisterClassExW(in wc) == 0) throw new System.ComponentModel.Win32Exception();
                _registered = true;
            }
            _self = GCHandle.Alloc(this);
            GetWindowRect(owner, out var bounds);
            int width = (int)(580 * scale), height = (int)((140 + fields.Length * 60 + (items != null ? 245 : 0)) * scale);
            _window = Externs.CreateWindowExW(1, "MDVNativeForm", title, 0x80C80000,
                Math.Max(0, bounds.L + (bounds.R - bounds.L - width) / 2), Math.Max(0, bounds.T + (bounds.B - bounds.T - height) / 2),
                width, height, owner, 0, Externs.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
            if (_window == 0) throw new System.ComponentModel.Win32Exception();
            _font = Externs.CreateFontW(-(int)Math.Round(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            nint Child(string cls, string text, uint style, int id, int x, int y, int w, int h)
            {
                nint child = Externs.CreateWindowExW(0, cls, text, 0x50000000 | style,
                    (int)(x * scale), (int)(y * scale), (int)(w * scale), (int)(h * scale), _window, (nint)id, Externs.GetModuleHandleW(null), 0);
                if (child == 0) throw new System.ComponentModel.Win32Exception();
                Externs.SendMessageW(child, 0x0030, _font, 0);
                return child;
            }
            Child("STATIC", description ?? "", 0, 1, 16, 12, 540, 40);
            int y = 54;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i]; nint input;
                if (field.Kind == FormFieldKind.Check)
                {
                    input = Child("BUTTON", field.Label, 0x10003, 1000 + i, 16, y, 540, 28);
                    Externs.SendMessageW(input, 0x00F1, field.Value == "true" ? 1 : 0, 0);
                }
                else
                {
                    Child("STATIC", field.Label, 0, 2000 + i, 16, y, 540, 20);
                    input = Child(field.Kind == FormFieldKind.Choice ? "COMBOBOX" : "EDIT", "",
                        field.Kind == FormFieldKind.Choice ? 0x210003u : 0x810080u, 1000 + i, 16, y + 22, 540, field.Kind == FormFieldKind.Choice ? 180 : 27);
                    if (field.Kind == FormFieldKind.Choice)
                    {
                        string[] choices = field.Choices ?? Array.Empty<string>();
                        foreach (string choice in choices) fixed (char* p = choice) Externs.SendMessageW(input, 0x0143, 0, (nint)p);
                        int selected = Array.IndexOf(choices, field.Value);
                        Externs.SendMessageW(input, 0x014E, (nint)Math.Max(0, selected), 0);
                    }
                    else
                    {
                        Externs.SendMessageW(input, 0x00C5, 1048576, 0);
                        Externs.SetWindowTextW(input, field.Value ?? "");
                    }
                }
                _inputs.Add(input); y += 60;
            }
            if (items != null)
            {
                _list = Child("LISTBOX", "", 0x00A10101u | (multiple ? 0x0800u : 0u), 3000, 16, y, 540, 226);
                foreach (string item in items) fixed (char* p = item) Externs.SendMessageW(_list, 0x0180, 0, (nint)p);
                if (items.Length != 0) Externs.SendMessageW(_list, multiple ? 0x0185u : 0x0186u, multiple ? 1 : 0, multiple ? -1 : 0);
                y += 242;
            }
            int buttonWidth = Math.Min(128, (540 - 8 * (buttons.Length - 1)) / buttons.Length);
            for (int i = 0; i < buttons.Length; i++)
                Child("BUTTON", buttons[i], 0x10000u | (i == buttons.Length - 1 ? 1u : 0u), 4000 + i,
                    556 - buttons.Length * (buttonWidth + 8) + i * (buttonWidth + 8), y + 8, buttonWidth, 30);
            if (enabled) EnableWindow(owner, false);
            Externs.ShowWindow(_window, 5);
            Externs.SetFocus(_inputs.Count > 0 ? _inputs[0] : _list != 0 ? _list : _window);
            while (!_done)
            {
                int value = (int)Externs.GetMessageW(out var message, 0, 0, 0);
                if (value <= 0) { quit = value == 0; quitCode = (int)message.wParam; break; }
                if (Composing(message.hwnd)) { Externs.TranslateMessage(in message); Externs.DispatchMessageW(in message); continue; }
                if (message.message == Externs.WM_KEYDOWN && message.wParam == 27) { _done = true; break; }
                if (!IsDialogMessageW(_window, ref message)) { Externs.TranslateMessage(in message); Externs.DispatchMessageW(in message); }
            }
            return _result;
        }
        finally
        {
            if (_window != 0 && Externs.IsWindow(_window)) Externs.DestroyWindow(_window);
            if (_font != 0) Externs.DeleteObject(_font);
            if (_self.IsAllocated) _self.Free();
            if (enabled && Externs.IsWindow(owner)) EnableWindow(owner, true);
            if (focus != 0 && Externs.IsWindow(focus)) Externs.SetFocus(focus);
            if (quit) Externs.PostQuitMessage(quitCode);
        }
    }

    private void Submit(int action)
    {
        var values = new string[_fields.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var field = _fields[i]; nint input = _inputs[i];
            values[i] = field.Kind == FormFieldKind.Check ? (Externs.SendMessageW(input, 0x00F0, 0, 0) != 0 ? "true" : "false")
                : field.Kind == FormFieldKind.Choice ? field.Choices[Math.Clamp((int)Externs.SendMessageW(input, 0x0147, 0, 0), 0, field.Choices.Length - 1)]
                : Externs.WindowText(input);
        }
        var selected = new List<int>();
        if (_list != 0)
        {
            int count = (int)Externs.SendMessageW(_list, 0x0190, 0, 0);
            if (count >= 0)
            {
                int[] indices = new int[Math.Min(count, 2000)];
                fixed (int* p = indices) count = (int)Externs.SendMessageW(_list, 0x0191, (nint)indices.Length, (nint)p);
                for (int i = 0; i < Math.Max(0, count); i++) selected.Add(indices[i]);
            }
            else
            {
                int index = (int)Externs.SendMessageW(_list, 0x0188, 0, 0);
                if (index >= 0) selected.Add(index);
            }
        }
        _result = new(action, values, selected.ToArray()); _done = true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WindowProc(nint h, uint message, nint w, nint l)
    {
        try
        {
            nint token = message == 0x0081 ? *(nint*)l : Externs.GetWindowLongPtrW(h, -21);
            NativeForm form = token == 0 ? null : (NativeForm)GCHandle.FromIntPtr(token).Target;
            if (message == 0x0081 && form != null) Externs.SetWindowLongPtrW(h, -21, token);
            if (form != null)
            {
                if (message == Externs.WM_COMMAND && ((long)w >> 16) == 0)
                {
                    int id = (int)((long)w & 0xffff);
                    if (id >= 4000 && id < 4005)
                    {
                        // The last action is the native cancel/default button.
                        int count = 0;
                        for (int i = 4000; i < 4005; i++) if (GetDlgItem(h, i) != 0) count++;
                        if (id == 4000 + count - 1) form._done = true;
                        else form.Submit(id - 4000);
                        return 0;
                    }
                }
                if (message == Externs.WM_CLOSE) { form._done = true; return 0; }
            }
        }
        catch (Exception ex) { AppLog.Error("native form", ex); }
        return Externs.DefWindowProcW(h, message, w, l);
    }
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
}
