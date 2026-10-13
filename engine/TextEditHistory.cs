using System;
using System.Collections.Generic;

namespace MDViewerNative;

internal sealed record TextEdit(int Start, string Removed, string Inserted, EditorPosition Before, EditorPosition After);

// Document-owned plain-text history, independent of the lifetime of the shared EDIT.
// The budget counts retained delta characters, not unbounded full-document snapshots.
internal sealed class TextEditHistory
{
    private readonly List<TextEdit> _undo = new(), _redo = new();
    private long _characters;
    public string Current { get; private set; }
    public int Limit { get; set; } = 256;
    public int CharacterBudget { get; set; } = 4 * 1024 * 1024;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public EditorPosition Position { get; private set; }

    // Retained delta characters for this history, as counted by the global budget.
    internal long RetainedCharacters => _characters;

    // Drops the oldest undo step. Used by UndoBudget when the process-wide ceiling is hit.
    // Returns false when nothing can be dropped, so the caller stops instead of spinning.
    internal bool TrimOldest()
    {
        if (_undo.Count == 0) return false;
        long freed = _undo[0].Removed.Length + _undo[0].Inserted.Length;
        _undo.RemoveAt(0);
        _characters -= freed;
        if (_characters < 0) _characters = 0;
        UndoBudget.Adjust(-freed);
        return true;
    }

    public void Reset(string text, EditorPosition position = default)
    {
        Current = text ?? ""; Position = position.Clamp(Current.Length);
        _undo.Clear(); _redo.Clear();
        UndoBudget.Adjust(-_characters);
        _characters = 0;
        UndoBudget.Forget(this);
    }

    public void Record(string text, EditorPosition after)
    {
        text ??= "";
        if (Current == null) { Reset(text, after); return; }
        if (Current == text) { Position = after.Clamp(text.Length); return; }
        int prefix = 0, min = Math.Min(Current.Length, text.Length);
        while (prefix < min && Current[prefix] == text[prefix]) prefix++;
        // Never put a delta boundary inside a valid surrogate pair.
        if (prefix > 0 && prefix < min && char.IsHighSurrogate(Current[prefix - 1]) && char.IsLowSurrogate(Current[prefix])) prefix--;
        int oldEnd = Current.Length, newEnd = text.Length;
        while (oldEnd > prefix && newEnd > prefix && Current[oldEnd - 1] == text[newEnd - 1]) { oldEnd--; newEnd--; }
        if (oldEnd > prefix && oldEnd < Current.Length && char.IsHighSurrogate(Current[oldEnd - 1]) && char.IsLowSurrogate(Current[oldEnd])) { oldEnd++; newEnd++; }
        string removed = Current[prefix..oldEnd], inserted = text[prefix..newEnd];
        var before = new EditorPosition(prefix, oldEnd, Position.FirstCharacter).Clamp(Current.Length);
        var edit = new TextEdit(prefix, removed, inserted, before, after.Clamp(text.Length));
        long delta = 0;
        foreach (var abandoned in _redo) delta -= abandoned.Removed.Length + abandoned.Inserted.Length;
        _redo.Clear(); _undo.Add(edit);
        delta += removed.Length + inserted.Length;
        _characters += delta;
        UndoBudget.Adjust(delta);
        Current = text; Position = edit.After;
        while (_undo.Count > Math.Clamp(Limit, 1, 2000) || _characters > Math.Max(1024, CharacterBudget))
        {
            if (_undo.Count == 0) break;
            long freed = _undo[0].Removed.Length + _undo[0].Inserted.Length;
            _characters -= freed;
            _undo.RemoveAt(0);
            UndoBudget.Adjust(-freed);
        }
        // Register as most-recently-used, then let the global ceiling trim other documents.
        UndoBudget.Touch(this);
        UndoBudget.Enforce(this);
    }

    public bool Undo(out string text, out EditorPosition position)
    {
        text = Current ?? ""; position = Position;
        if (!CanUndo) return false;
        var edit = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        text = text[..edit.Start] + edit.Removed + text[(edit.Start + edit.Inserted.Length)..];
        Current = text; Position = position = edit.Before.Clamp(text.Length); _redo.Add(edit);
        return true;
    }
    public bool Redo(out string text, out EditorPosition position)
    {
        text = Current ?? ""; position = Position;
        if (!CanRedo) return false;
        var edit = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        text = text[..edit.Start] + edit.Inserted + text[(edit.Start + edit.Removed.Length)..];
        Current = text; Position = position = edit.After.Clamp(text.Length); _undo.Add(edit);
        return true;
    }
}
