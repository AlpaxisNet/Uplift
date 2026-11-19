using System.Text;

namespace Uplift.Syntax;

public enum TokenKind
{
	End,
	Identifier,
	Number,
	String,
	Operator,

	/// <summary>A GS1 message code's # (the parser reads the code itself).</summary>
	Hash,

	/// <summary>A character that starts no token.</summary>
	Invalid,
}

/// <summary>A token: its kind, its text as written, where it is, and (for a string) its value.</summary>
public readonly record struct Token(TokenKind Kind, string Text, int Start, int End, string? Value = null)
{
	public bool Is(string op) => (Kind == TokenKind.Operator || Kind == TokenKind.Identifier) && Text == op;
}

/// <summary>A comment of the source: its text, the lines it spans, and whether code comes before it on its first line.</summary>
public sealed record SourceComment(int Start, int End, int Line, int EndLine, string Text, bool Trailing);

/// <summary>
///     Reads GS1 and GS2 tokens from any position of a source (the parser reads GS1 text arguments itself, character by character,
///     and then goes on reading tokens after them). Comments are skipped and collected once each.
/// </summary>
public sealed class Lexer
{
	private static readonly string[] Operators =
	[
		"<<=", ">>=",
		"==", "!=", "<>", "<=", ">=", "=<", "=>", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "^=", "@=", "|=", "&=", ":=", "<<", ">>",
		"+", "-", "*", "/", "%", "^", "@", "=", "<", ">", "!", "~", "&", "|", "?", ":", ",", ";", ".", "(", ")", "[", "]", "{", "}",
	];

	private readonly int[] _lineStarts;
	private readonly SortedDictionary<int, SourceComment> _comments = [];

	public Lexer(string source)
	{
		Source = source;
		var starts = new List<int> { 0 };
		for (var i = 0; i < source.Length; i++)
			if (source[i] == '\n')
				starts.Add(i + 1);
		_lineStarts = starts.ToArray();
	}

	public string Source { get; }

	public IReadOnlyCollection<SourceComment> Comments => _comments.Values;

	/// <summary>The line (1-based) of a position.</summary>
	public int LineOf(int position)
	{
		var i = Array.BinarySearch(_lineStarts, position);
		return (i >= 0 ? i : ~i - 1) + 1;
	}

	/// <summary>Whether only spaces come before a position on its line.</summary>
	public bool StartsLine(int position)
	{
		for (var i = position - 1; i >= 0 && Source[i] != '\n'; i--)
			if (!char.IsWhiteSpace(Source[i]))
				return false;
		return true;
	}

	/// <summary>Skips spaces and comments from a position; returns the position of the next token.</summary>
	public int SkipBlank(int position)
	{
		var s = Source;
		while (position < s.Length)
		{
			var c = s[position];
			if (char.IsWhiteSpace(c))
			{
				position++;
				continue;
			}
			if (c == '/' && position + 1 < s.Length && s[position + 1] == '/')
			{
				var end = s.IndexOf('\n', position);
				end = end < 0 ? s.Length : end;
				AddComment(position, end);
				position = end;
				continue;
			}
			if (c == '/' && position + 1 < s.Length && s[position + 1] == '*')
			{
				var end = s.IndexOf("*/", position + 2, StringComparison.Ordinal);
				end = end < 0 ? s.Length : end + 2;
				AddComment(position, end);
				position = end;
				continue;
			}
			break;
		}
		return position;
	}

	public void AddComment(int start, int end)
	{
		if (_comments.ContainsKey(start)) return;
		var text = Source[start..end].TrimEnd();
		_comments[start] = new SourceComment(start, end, LineOf(start), LineOf(Math.Max(start, end - 1)), text, !StartsLine(start));
	}

	/// <summary>The token at a position (after spaces and comments).</summary>
	public Token Read(int position)
	{
		var s = Source;
		var start = SkipBlank(position);
		if (start >= s.Length) return new Token(TokenKind.End, "", start, start);
		var c = s[start];
		if (char.IsAsciiLetter(c) || c == '_' || c == '$')
		{
			var i = start + 1;
			while (i < s.Length)
			{
				if (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$') i++;
				// class::function, read as one name.
				else if (s[i] == ':' && i + 2 < s.Length && s[i + 1] == ':' && (char.IsAsciiLetter(s[i + 2]) || s[i + 2] == '_')) i += 2;
				else break;
			}
			return new Token(TokenKind.Identifier, s[start..i], start, i);
		}
		if (char.IsAsciiDigit(c) || c == '.' && start + 1 < s.Length && char.IsAsciiDigit(s[start + 1]))
		{
			var i = start;
			if (c == '0' && i + 1 < s.Length && (s[i + 1] is 'x' or 'X') && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 2]))
			{
				i += 2;
				while (i < s.Length && char.IsAsciiHexDigit(s[i])) i++;
			}
			else
			{
				while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
				if (i < s.Length && s[i] == '.' && (i + 1 >= s.Length || !char.IsAsciiLetter(s[i + 1]) || s[i + 1] is 'e' or 'E'))
				{
					i++;
					while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
				}
				if (i + 1 < s.Length && s[i] is 'e' or 'E' && (char.IsAsciiDigit(s[i + 1]) || s[i + 1] is '+' or '-' && i + 2 < s.Length && char.IsAsciiDigit(s[i + 2])))
				{
					i += 2;
					while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
				}
			}
			return new Token(TokenKind.Number, s[start..i], start, i);
		}
		if (c is '"' or '\'')
		{
			var (value, end, closed) = ReadString(start);
			return closed ? new Token(TokenKind.String, s[start..end], start, end, value) : new Token(TokenKind.Invalid, s[start..end], start, end);
		}
		if (c == '#') return new Token(TokenKind.Hash, "#", start, start + 1);
		foreach (var op in Operators)
			if (string.CompareOrdinal(s, start, op, 0, op.Length) == 0)
				return new Token(TokenKind.Operator, op, start, start + op.Length);
		return new Token(TokenKind.Invalid, c.ToString(), start, start + 1);
	}

	/// <summary>A quoted string from its opening quote: its value, the position after it, and whether it was closed (it may span lines).</summary>
	public (string Value, int End, bool Closed) ReadString(int start)
	{
		var s = Source;
		var quote = s[start];
		var sb = new StringBuilder();
		var i = start + 1;
		while (i < s.Length)
		{
			var c = s[i];
			if (c == quote) return (sb.ToString(), i + 1, true);
			if (c == '\r') {
				i++;
				continue;
			}
			if (c == '\\' && i + 1 < s.Length)
			{
				var n = s[i + 1];
				sb.Append(n switch
				{
					'n' => '\n',
					't' => '\t',
					'r' => '\r',
					_ => n,
				});
				i += 2;
				continue;
			}
			sb.Append(c);
			i++;
		}
		return (sb.ToString(), i, false);
	}
}
