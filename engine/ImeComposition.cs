namespace MDViewerNative;

// Tracks the native IME composition contract without inspecting or logging user text.
// Enter/Escape/Tab must reach the IME while it is composing, not application commands.
internal sealed class ImeComposition
{
    public bool Active { get; private set; }
    public bool Observe(uint message)
    {
        bool next = message switch
        {
            0x010D => true,  // WM_IME_STARTCOMPOSITION
            0x010E => false, // WM_IME_ENDCOMPOSITION
            0x0051 => false, // WM_INPUTLANGCHANGE finishes the previous input context
            0x0082 => false, // WM_NCDESTROY
            _ => Active
        };
        if (next == Active) return false;
        Active = next;
        return true;
    }
}
