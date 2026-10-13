using System;
using System.Text;

namespace MDViewerNative;

// Shared, resource-free grammar for native formulas and inline Unicode output.
// Unsupported/malformed input is rejected as a whole, never partially consumed.
internal static class MathSyntax
{
    internal const int MaxLength = 32768;
    internal const int MaxAtoms = 4096;
    internal const int MaxDepth = 64;

    internal static bool TryParse(string source, out MRow row)
    {
        row = null;
        if (string.IsNullOrWhiteSpace(source) || source.Length > MaxLength) return false;
        try
        {
            row = new Reader(source).Read();
            return true;
        }
        catch (FormatException) { row = null; return false; }
    }

    private sealed class Reader
    {
        private readonly string _source;
        private int _position, _atoms;
        internal Reader(string source) => _source = source;
        internal MRow Read() => Row('\0', 0);
        private void Step(int depth)
        {
            if (depth > MaxDepth || ++_atoms > MaxAtoms) throw new FormatException("Math budget exceeded.");
        }
        private void Space()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++;
        }
        private MRow Row(char terminator, int depth)
        {
            Step(depth);
            var row = new MRow();
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length == 0) return;
                row.Items.Add(new MText(text.ToString(), 17f));
                text.Clear();
            }
            while (true)
            {
                if (_position == _source.Length)
                {
                    if (terminator != '\0') throw new FormatException("Unclosed math group.");
                    break;
                }
                if (terminator != '\0' && _source[_position] == terminator) { _position++; break; }
                if (char.IsWhiteSpace(_source[_position])) { text.Append(_source[_position++]); continue; }
                MAtom atom = Primary(depth + 1);
                MScript scripts = null;
                while (true)
                {
                    int beforeSpace = _position;
                    Space();
                    if (_position == _source.Length || _source[_position] is not ('^' or '_')) { _position = beforeSpace; break; }
                    bool upper = _source[_position++] == '^';
                    scripts ??= new MScript { BaseAtom = atom, SupFirst = upper };
                    if (upper ? scripts.Super != null : scripts.Sub != null) throw new FormatException("Duplicate math script.");
                    MAtom argument = Argument(depth + 1);
                    if (upper) scripts.Super = argument; else scripts.Sub = argument;
                }
                atom = scripts ?? atom;
                // 带正体/照印标志的文字(\mathrm d、\text{…})单独成项,合并会丢标志(S77 续)
                if (atom is MText { Upright: false, Verbatim: false } plain) text.Append(plain.Text);
                else { Flush(); row.Items.Add(atom); }
            }
            Flush();
            return row;
        }
        private MAtom Argument(int depth)
        {
            Space();
            return Primary(depth + 1);
        }
        private MAtom Primary(int depth)
        {
            Step(depth);
            if (_position >= _source.Length) throw new FormatException("Missing math argument.");
            char c = _source[_position++];
            if (c is '}' or '^' or '_') throw new FormatException("Unexpected math delimiter.");
            if (c == '{') return Row('}', depth + 1);
            if (c != '\\')
            {
                string value = char.IsHighSurrogate(c) && _position < _source.Length && char.IsLowSurrogate(_source[_position])
                    ? _source.Substring(_position++ - 1, 2) : c.ToString();
                return new MText(value, 17f);
            }
            if (_position == _source.Length) throw new FormatException("Incomplete math command.");
            int start = _position;
            while (_position < _source.Length && char.IsAsciiLetter(_source[_position])) _position++;
            string name = _source[start.._position];
            if (name.Length == 0)
            {
                char escaped = _source[_position++];
                // \| 双竖线;\, \; \: \! 间距(S75)
                string spacing = escaped switch { '|' => "‖", ',' => "\u2009", ';' => "\u2005", ':' => "\u205F", '!' => "", _ => null };
                if (spacing != null) return new MText(spacing, 17f);
                if (escaped is not ('{' or '}' or '_' or '^' or '%' or '$' or '#' or '&' or ' '))
                    throw new FormatException("Unsupported math escape.");
                return new MText(escaped.ToString(), 17f);
            }
            if (name is "frac" or "dfrac" or "tfrac")
                return new MFrac { Num = Argument(depth + 1), Den = Argument(depth + 1) };
            if (name == "sqrt")
            {
                Space();
                MAtom index = null;
                if (_position < _source.Length && _source[_position] == '[')
                {
                    _position++;
                    index = Row(']', depth + 1);
                }
                return new MSqrt { Index = index, Body = Argument(depth + 1) };
            }
            if (name == "text") return new MText(Literal(depth + 1), 17f) { Upright = true, Verbatim = true };
            // S75(自用走查 d2l):尺寸/样式命令不改变文字,忽略(\left. 不显示);正体命令即参数本身;
            // 字体命令映射到 Unicode 数学字母,重音转组合附加符号,都作用在参数的文字叶子上。
            if (name is "left" or "right" or "middle" or "big" or "Big" or "bigg" or "Bigg" or "bigl" or "bigr" or "Bigl" or "Bigr" or "biggl" or "biggr" or "Biggl" or "Biggr")
            {
                Space();
                if (_position < _source.Length && _source[_position] == '.') _position++;
                return new MText("", 17f);
            }
            if (name is "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits") return new MText("", 17f);
            if (name is "mathrm" or "operatorname" or "textrm" or "mathop" or "mathnormal")
            {
                if (name == "operatorname" && _position < _source.Length && _source[_position] == '*') _position++;
                MAtom upright = Argument(depth + 1);
                foreach (MText text in upright.Texts()) text.Upright = true;   // 行内原生公式不斜体(S77)
                return upright;
            }
            if (MathUnicode.IsAlphabet(name))
            {
                MAtom styled = Argument(depth + 1);
                MapText(styled, text => MathUnicode.Alphabet(name, text), true);
                return styled;
            }
            if (name is "vec" or "overrightarrow") return new MAccent { Body = Argument(depth + 1) };   // 自绘箭头(S77)
            if (MathUnicode.TryAccent(name, out char mark))
            {
                MAtom accented = Argument(depth + 1);
                MapText(accented, text => MathUnicode.Accent(text, mark), false);
                return accented;
            }
            if (MathUnicode.TrySymbol(name, out string symbol)) return new MText(symbol, 17f);
            throw new FormatException("Unsupported math command.");
        }
        // 改写原子树中的文字;scripts=false 时只改基底(重音不落到上下标)。
        private static void MapText(MAtom atom, Func<string, string> map, bool scripts)
        {
            switch (atom)
            {
                case MText text: text.Text = map(text.Text); break;
                case MRow row: foreach (MAtom item in row.Items) MapText(item, map, scripts); break;
                case MScript script:
                    MapText(script.BaseAtom, map, scripts);
                    if (scripts && script.Super != null) MapText(script.Super, map, scripts);
                    if (scripts && script.Sub != null) MapText(script.Sub, map, scripts);
                    break;
                case MFrac fraction: MapText(fraction.Num, map, scripts); MapText(fraction.Den, map, scripts); break;
                case MSqrt root: MapText(root.Body, map, scripts); if (root.Index != null) MapText(root.Index, map, scripts); break;
                case MAccent accent: MapText(accent.Body, map, scripts); break;
            }
        }
        private string Literal(int depth)
        {
            Space();
            if (_position == _source.Length || _source[_position++] != '{') throw new FormatException("Text requires a group.");
            var text = new StringBuilder();
            int nesting = 1;
            while (_position < _source.Length)
            {
                char c = _source[_position++];
                if (c == '\\' && _position < _source.Length && _source[_position] is '{' or '}' or '\\')
                {
                    text.Append(_source[_position++]);
                    continue;
                }
                if (c == '{' && ++nesting + depth > MaxDepth) throw new FormatException("Text nesting exceeds budget.");
                if (c == '}' && --nesting == 0) return text.ToString();
                text.Append(c);
            }
            throw new FormatException("Unclosed text group.");
        }
    }
}
