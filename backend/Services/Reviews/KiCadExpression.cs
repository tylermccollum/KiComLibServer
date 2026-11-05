using System.Text;

namespace KiComLibBackend.Services.Reviews;

/// <summary>Bounded S-expression reader; only direct children describe inheritance/models.</summary>
internal sealed record KiCadExpression(string? Atom, List<KiCadExpression> Children)
{
    public string? Head => Children.FirstOrDefault()?.Atom;
    public IEnumerable<KiCadExpression> Named(string name) => Children.Where(c => c.Head == name);
    public string? Argument(int index = 1) => Children.ElementAtOrDefault(index)?.Atom;

    public static KiCadExpression Parse(string text)
    {
        int offset = 0, count = 0;
        void Space()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset])) { offset++; continue; }
                if (text[offset] == ';') { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
        }
        KiCadExpression Read(int depth)
        {
            if (depth > 256 || ++count > 2_000_000) throw new FormatException("Geometry exceeds parser limits");
            Space();
            if (offset >= text.Length) throw new FormatException("Unexpected end of geometry");
            if (text[offset] == '(')
            {
                offset++; List<KiCadExpression> items = [];
                while (true)
                {
                    Space();
                    if (offset >= text.Length) throw new FormatException("Unclosed geometry expression");
                    if (text[offset] == ')') { offset++; return new(null, items); }
                    items.Add(Read(depth + 1));
                }
            }
            if (text[offset] == ')') throw new FormatException("Unexpected closing parenthesis");
            StringBuilder atom = new();
            if (text[offset] == '"')
            {
                offset++;
                while (offset < text.Length)
                {
                    char ch = text[offset++];
                    if (ch == '"') return new(atom.ToString(), []);
                    if (ch == '\\')
                    {
                        if (offset >= text.Length) throw new FormatException("Unfinished escape");
                        ch = text[offset++];
                        ch = ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => ch };
                    }
                    atom.Append(ch);
                }
                throw new FormatException("Unclosed string");
            }
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '(' and not ')') atom.Append(text[offset++]);
            return new(atom.ToString(), []);
        }
        var root = Read(0); Space();
        if (offset != text.Length) throw new FormatException("Extra geometry expressions");
        return root;
    }
}
