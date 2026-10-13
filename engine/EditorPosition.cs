using System;

namespace MDViewerNative;

// UTF-16 offsets in the native editor's CRLF text, not rendered pixels or AST spans.
internal readonly record struct EditorPosition(int Start, int End, int FirstCharacter)
{
    public EditorPosition Clamp(int length) => new(Math.Clamp(Start, 0, length),
        Math.Clamp(End, 0, length), Math.Clamp(FirstCharacter, 0, length));
}
