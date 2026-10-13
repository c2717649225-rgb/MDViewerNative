using System;
using System.Collections.Generic;

namespace MDViewerNative;

internal sealed partial class ParserContext
{
    private string[] _sourceLines;
    private int[] _keptSourceLines = Array.Empty<int>();
    private int[] _sourceStarts = Array.Empty<int>();
    private int _sourceLength;

    private void PrepareSource(string text)
    {
        _sourceLength = text.Length;
        var starts = new List<int> { text.StartsWith('\uFEFF') ? 1 : 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        _sourceStarts = starts.ToArray();
    }

    private void AssignSource(Block block, int firstLine, int afterLine, bool container = false)
    {
        if (block.Source != null || firstLine < 0 || firstLine >= _keptSourceLines.Length) return;
        int first = _keptSourceLines[firstLine];
        int last = _keptSourceLines[Math.Clamp(afterLine - 1, firstLine, _keptSourceLines.Length - 1)] + 1;
        int end = last < _sourceStarts.Length ? _sourceStarts[last] : _sourceLength;
        SetSource(block, new(_sourceStarts[first], end, container));
    }

    private static void SetSource(Block block, SourceRange range)
    {
        block.Source = range;
        if (block is ListItem { Sub: { } children })
            foreach (var child in children) SetSource(child, range with { Container = true });
    }

    // A root iteration may emit several flattened quote/list blocks. They share the
    // true outer-container range, explicitly marked coarse rather than guessed from
    // repeated rendered words. Synthetic footnotes have no source range.
    private ref struct BlockSourceScope
    {
        private readonly ParserContext _parser;
        private readonly List<Block> _blocks;
        private readonly int _firstBlock, _firstLine;
        private ref int _cursor;

        internal BlockSourceScope(ParserContext parser, string[] lines, List<Block> blocks, int line, ref int cursor)
        {
            _parser = ReferenceEquals(lines, parser._sourceLines) ? parser : null;
            _blocks = blocks; _firstBlock = blocks.Count; _firstLine = line; _cursor = ref cursor;
        }

        public void Dispose()
        {
            if (_parser == null) return;
            bool container = false;
            for (int b = _firstBlock; b < _blocks.Count; b++)
                if (_blocks[b].QuoteDepth > 0 || _blocks[b] is ListItem or Quote) container = true;
            for (int b = _firstBlock; b < _blocks.Count; b++)
                _parser.AssignSource(_blocks[b], _firstLine, _cursor, container);
        }
    }
}
