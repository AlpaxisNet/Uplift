using System.Text;

namespace Uplift.Syntax;

/// <summary>Which language a part is read as.</summary>
public enum Dialect
{
	/// <summary>GS2 only.</summary>
	Gs2,

	/// <summary>
	///     GS1: GS2's syntax plus GS1's commands with bare arguments, message codes (#c, #v(...)), text arguments, the comparing = of
	///     conditions, =&lt; and =&gt;.
	/// </summary>
	Gs1,

	/// <summary>GS2 functions (a statement of one that is not GS2 is read as GS1), and main code (outside functions) read as GS1.</summary>
	Gs1MainCode,
}

/// <summary>The GS1 forms the parser met (reported for the language decision and the strict check).</summary>
public sealed class Gs1Signs
{
	public List<string> Found { get; } = [];

	public void Add(string sign)
	{
		if (!Found.Contains(sign) && Found.Count < 20) Found.Add(sign);
	}

	public void AddIf(bool condition, string sign)
	{
		if (condition) Add(sign);
	}
}

/// <summary>The result of a parse: the script, or the error and its line; the source lines of each node; the comments.</summary>
public sealed class ParseResult
{
	public Script? Script { get; init; }

	public string? Error { get; init; }

	public int ErrorLine { get; init; }

	public required Dictionary<object, (int Start, int End)> Lines { get; init; }

	public required IReadOnlyCollection<SourceComment> Comments { get; init; }

	public required Gs1Signs Signs { get; init; }

	/// <summary>The GS2-only syntax met (SPC, @, quoted text, method calls, ?:, in, parameters ...): GS1 has none of it.</summary>
	public required Gs1Signs Gs2Only { get; init; }

	public bool Success => Script != null;
}

public sealed class ParseException(string message, int position) : Exception(message)
{
	public int Position { get; } = position;
}

/// <summary>A GS1 message code as the parser read it (internal: the catalog turns it into GS2 before the tree is finished).</summary>
public sealed record Gs1CodeExpr(string Code, List<Expr> Args) : Expr;

/// <summary>Reads GS2 or GS1 into Uplift's syntax tree. The GS1 commands, functions and message codes come from a <see cref="Gs1Catalog" />.</summary>
public sealed class Parser
{
	private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
	{
		"if", "else", "elseif", "while", "for", "do", "switch", "case", "default", "break", "continue", "return", "function", "public", "new", "with",
		"in", "true", "false", "null", "const", "enum",
	};

	private static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "^=", "@=", "|=", "&=", "<<=", ">>=", ":="];

	private readonly Gs1Catalog _catalog;
	private readonly Dialect _dialect;
	private readonly Dictionary<object, (int, int)> _lines = new(ReferenceEqualityComparer.Instance);
	private readonly Lexer _lexer;
	private readonly Gs1Signs _signs = new();
	private readonly Gs1Signs _gs2Only = new();
	private readonly List<FunctionDecl> _hoisted = [];
	private readonly List<Item> _hoistedDeclarations = [];
	private int _pos;
	private int _lastEnd;
	private bool _inCondition;
	private int _functionDepth;
	private Token _peeked;
	private int _peekedAt = -1;

	private Parser(string source, Dialect dialect, Gs1Catalog? catalog)
	{
		_lexer = new Lexer(source);
		_dialect = dialect;
		_catalog = catalog ?? Gs1Catalog.Default;
	}

	private bool Legacy => _dialect == Dialect.Gs1 || _dialect == Dialect.Gs1MainCode && (_functionDepth == 0 || _fallback);

	// In a GS2 function of a mixed part: reading one statement as GS1, because it is not GS2.
	private bool _fallback;

	/// <summary>Reads a whole part.</summary>
	public static ParseResult Parse(string source, Dialect dialect, Gs1Catalog? catalog = null)
	{
		var parser = new Parser(source, dialect, catalog);
		try
		{
			var script = parser.ScriptRule();
			return new ParseResult { Script = script, Lines = parser._lines, Comments = parser._lexer.Comments, Signs = parser._signs, Gs2Only = parser._gs2Only };
		}
		catch (ParseException e)
		{
			return new ParseResult { Error = e.Message, ErrorLine = parser._lexer.LineOf(Math.Min(e.Position, source.Length)), Lines = parser._lines, Comments = parser._lexer.Comments, Signs = parser._signs, Gs2Only = parser._gs2Only };
		}
		catch (InsufficientExecutionStackException)
		{
			return new ParseResult { Error = "nested too deeply", Lines = parser._lines, Comments = parser._lexer.Comments, Signs = parser._signs, Gs2Only = parser._gs2Only };
		}
	}

	// ------------------------------------------------------------ tokens

	private Token Peek()
	{
		if (_peekedAt != _pos)
		{
			_peeked = _lexer.Read(_pos);
			_peekedAt = _pos;
		}
		return _peeked;
	}

	private Token PeekAfter(Token t) => _lexer.Read(t.End);

	private Token Take()
	{
		var t = Peek();
		if (t.Kind == TokenKind.Invalid) throw new ParseException($"unexpected '{t.Text}'", t.Start);
		_pos = t.End;
		_lastEnd = t.End;
		return t;
	}

	private bool Is(string op) => Peek() is { Kind: TokenKind.Operator or TokenKind.Identifier } t && t.Text == op;

	private bool Accept(string op)
	{
		if (!Is(op)) return false;
		Take();
		return true;
	}

	private void Expect(string op)
	{
		if (!Accept(op)) throw Error($"expected '{op}'");
	}

	private ParseException Error(string message)
	{
		var t = Peek();
		return new ParseException(t.Kind == TokenKind.End ? message + " at the end" : $"{message}, found '{t.Text}'", t.Start);
	}

	private bool IsName(Token t) => t.Kind == TokenKind.Identifier && !Keywords.Contains(t.Text);

	private string Name()
	{
		var t = Peek();
		if (!IsName(t)) throw Error("expected a name");
		Take();
		return t.Text;
	}

	private T Mark<T>(T node, int start) where T : class
	{
		_lines[node] = (_lexer.LineOf(start), _lexer.LineOf(Math.Max(start, _lastEnd - 1)));
		return node;
	}

	// ------------------------------------------------------------ scripts

	private Script ScriptRule()
	{
		var items = new List<Item>();
		while (Peek().Kind != TokenKind.End)
		{
			var start = Peek().Start;
			if (Is("function") || Is("public") && PeekAfter(Peek()).Is("function"))
			{
				items.Add(Mark(Function(), start));
				continue;
			}
			if (Is("const") && !Legacy)
			{
				Take();
				var name = Name();
				Expect("=");
				var value = Expression();
				EndStatement();
				items.Add(Mark(new ConstDecl(name, value), start));
				continue;
			}
			if (Is("enum"))
			{
				items.Add(Mark(Enum(), start));
				continue;
			}
			var statement = Statement();
			if (statement != null) items.Add(Mark(new StatementItem(statement), start));
		}
		items.InsertRange(0, _hoistedDeclarations);
		items.AddRange(_hoisted);
		return new Script(items);
	}

	private FunctionDecl Function()
	{
		var isPublic = Accept("public");
		Expect("function");
		var name = Name();
		string? owner = null;
		if (Accept("."))
		{
			owner = name;
			name = Peek().Kind == TokenKind.Identifier ? Take().Text : throw Error("expected a function name");
		}
		Expect("(");
		var parameters = new List<string>();
		if (!Is(")"))
			do
			{
				// A parameter may be a dotted name (player.account).
				var parameter = Name();
				while (Is(".") && PeekAfter(Peek()).Kind == TokenKind.Identifier)
				{
					Take();
					parameter += "." + Take().Text;
				}
				parameters.Add(parameter);
			} while (Accept(","));
		Expect(")");
		if (parameters.Count > 0) _gs2Only.AddIf(!Legacy, "parameters");
		_functionDepth++;
		var body = Is("{") ? Block() : Body();
		_functionDepth--;
		return new FunctionDecl(name, owner, isPublic, parameters, body);
	}

	private EnumDecl Enum()
	{
		Expect("enum");
		string? name = IsName(Peek()) ? Take().Text : null;
		Expect("{");
		var members = new List<(string, Expr?)>();
		while (!Is("}"))
		{
			var member = Name();
			Expr? value = Accept("=") ? Expression() : null;
			members.Add((member, value));
			if (!Accept(",")) break;
		}
		Expect("}");
		Accept(";");
		return new EnumDecl(name, members);
	}

	// ------------------------------------------------------------ statements

	private List<Stmt> Block()
	{
		Expect("{");
		var body = new List<Stmt>();
		while (!Is("}"))
		{
			if (Peek().Kind == TokenKind.End) throw Error("expected '}'");
			var s = Statement();
			if (s != null) body.Add(s);
		}
		Expect("}");
		return body;
	}

	/// <summary>A statement's body: a block's statements, or the one statement.</summary>
	private List<Stmt> Body()
	{
		if (Is("{")) return Block();
		// GS1: if (x) } is an if with nothing to do.
		if (Legacy && Is("}")) return [];
		var s = Statement();
		return s == null ? [] : [s];
	}

	private void EndStatement()
	{
		if (Accept(";")) return;
		// A missing ; before } or at the end is forgiven; GS1 also forgives it at the end of a line.
		if (Is("}") || Peek().Kind == TokenKind.End) return;
		// GS1 forgives it before the next statement: at the end of a line, or before a name (ok so have fun).
		if (Legacy && (_lexer.LineOf(Peek().Start) > _lexer.LineOf(Math.Max(0, _lastEnd - 1)) || Peek().Kind == TokenKind.Identifier)) return;
		throw Error("expected ';'");
	}

	private Stmt? Statement()
	{
		if (_dialect != Dialect.Gs1MainCode || _functionDepth == 0 || _fallback) return StatementCore();
		var (pos, lastEnd, marks) = (_pos, _lastEnd, _gs2Only.Found.Count);
		try
		{
			return StatementCore();
		}
		catch (ParseException)
		{
			// Not GS2: this statement is read as GS1 (and what the failed GS2 reading saw does not count).
			(_pos, _lastEnd, _peekedAt) = (pos, lastEnd, -1);
			_gs2Only.Found.RemoveRange(marks, _gs2Only.Found.Count - marks);
			_fallback = true;
			try
			{
				var statement = StatementCore();
				_signs.Add("GS1 statement in a GS2 function");
				return statement;
			}
			finally
			{
				_fallback = false;
			}
		}
	}

	private Stmt? StatementCore()
	{
		RuntimeHelpers_EnsureStack();
		var t = Peek();
		var start = t.Start;
		if (t.Kind == TokenKind.End) throw Error("expected a statement");
		if (t.Is(";") || Legacy && t.Is("."))
		{
			// GS1 passes over a stray . (setimg a.png;.).
			Take();
			return null;
		}
		if (t.Is("{")) return Mark(new BlockStmt(Block()), start);
		if (t.Kind == TokenKind.Identifier)
			switch (t.Text)
			{
				case "if":
					return If(start);
				case "while":
				{
					Take();
					Expect("(");
					var c = Condition();
					Expect(")");
					return Mark(new WhileStmt(c, Body()), start);
				}
				case "do":
				{
					Take();
					var body = Body();
					Expect("while");
					Expect("(");
					var c = Condition();
					Expect(")");
					EndStatement();
					return Mark(new DoWhileStmt(body, c), start);
				}
				case "for":
					return For(start);
				case "switch":
					_gs2Only.AddIf(!Legacy, "switch");
					return Switch(start);
				case "with":
				{
					Take();
					Expect("(");
					var target = Expression();
					Expect(")");
					return Mark(new WithStmt(target, Body()), start);
				}
				case "return":
				{
					Take();
					Expr? value = Is(";") || Is("}") || Peek().Kind == TokenKind.End ? null : Expression();
					if (value != null) _gs2Only.AddIf(!Legacy, "return with a value");
					EndStatement();
					return Mark(new ReturnStmt(value), start);
				}
				case "break":
					Take();
					EndStatement();
					return Mark(new BreakStmt(), start);
				case "continue":
					Take();
					EndStatement();
					return Mark(new ContinueStmt(), start);
				case "const" when PeekAfter(t).Kind == TokenKind.Identifier:
				{
					Take();
					var name = Name();
					Expect("=");
					var value = Expression();
					EndStatement();
					_hoistedDeclarations.Add(Mark(new ConstDecl(name, value), start));
					return null;
				}
				case "enum":
					_hoistedDeclarations.Add(Mark(Enum(), start));
					return null;
				case "function" or "public" when Legacy:
				{
					// GS1 defines a function wherever it is written: it is the script's.
					_hoisted.Add(Mark(Function(), start));
					return null;
				}
				case "function" or "public":
					throw Error("a function inside a statement");
				case "else" or "elseif" or "case" or "default":
					throw Error($"'{t.Text}' without its statement");
				case "new" when IsNewStatement(t):
				{
					Take();
					Expr? typeExpr = null;
					var type = "";
					if (Accept("("))
					{
						typeExpr = Expression();
						Expect(")");
					}
					else type = Name();
					Expect("(");
					var args = Arguments(")");
					var body = Block();
					Accept(";");
					return Mark(new NewStmt(type, args, body, typeExpr), start);
				}
			}
		if (Legacy && t.Kind == TokenKind.Identifier && _catalog.TryGetCommand(t.Text, out var command) && IsCommandForm(t))
			return Mark(Command(t, command), start);
		// A command written against its first number (sleep0.5, putleaps2,1,2): GS1 reads the command, then the number.
		if (Legacy && t.Kind == TokenKind.Identifier && !_catalog.TryGetCommand(t.Text, out _) && GluedCommand(t) is { } glued)
		{
			var name = new Token(TokenKind.Identifier, t.Text[..glued.Name.Length], t.Start, t.Start + glued.Name.Length);
			return Mark(Command(name, glued), start);
		}
		var expression = Sequence();
		EndStatement();
		return Mark(new ExprStmt(expression), start);
	}

	private static void RuntimeHelpers_EnsureStack() => System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

	// new Type(args) { ... }: a statement that makes an object and sets its members.
	private bool IsNewStatement(Token newToken)
	{
		var type = PeekAfter(newToken);
		Token open;
		if (type.Is("("))
		{
			// new (expr)(args) { ...: skip the type's parentheses.
			var d = 0;
			var k = type;
			while (k.Kind != TokenKind.End)
			{
				if (k.Is("(")) d++;
				else if (k.Is(")") && --d == 0) break;
				k = _lexer.Read(k.End);
			}
			open = _lexer.Read(k.End);
		}
		else if (IsName(type)) open = _lexer.Read(type.End);
		else return false;
		if (!open.Is("(")) return false;
		// Skip the arguments to the matching ).
		var depth = 0;
		var t = open;
		while (t.Kind != TokenKind.End)
		{
			if (t.Is("(")) depth++;
			else if (t.Is(")") && --depth == 0) break;
			t = _lexer.Read(t.End);
		}
		return _lexer.Read(t.End).Is("{");
	}

	private Stmt If(int start)
	{
		Take();
		Expect("(");
		var condition = Condition();
		Expect(")");
		var then = Body();
		if (Is("else"))
		{
			Take();
			return Mark(new IfStmt(condition, then, Body(), true), start);
		}
		if (Is("elseif"))
		{
			var elseStart = Peek().Start;
			return Mark(new IfStmt(condition, then, [If(elseStart)], true), start);
		}
		return Mark(new IfStmt(condition, then, [], false), start);
	}

	private Stmt For(int start)
	{
		Take();
		Expect("(");
		Expr? init = null;
		if (!Is(";"))
		{
			init = Sequence();
			if (Accept(":"))
			{
				_gs2Only.AddIf(!Legacy, "for each");
				var source = Expression();
				Expect(")");
				return Mark(new ForEachStmt(init, source, Body()), start);
			}
		}
		Expect(";");
		var condition = Is(";") ? null : Condition();
		Expect(";");
		var step = Is(")") ? null : Sequence();
		Accept(";");
		Expect(")");
		return Mark(new ForStmt(init, condition, step, Body()), start);
	}

	private Stmt Switch(int start)
	{
		Take();
		Expect("(");
		var value = Expression();
		Expect(")");
		Expect("{");
		var cases = new List<SwitchCase>();
		while (!Is("}"))
		{
			var labels = new List<Expr?>();
			while (Is("case") || Is("default"))
			{
				if (Accept("default")) labels.Add(null);
				else
				{
					Take();
					labels.Add(Expression());
				}
				Expect(":");
			}
			if (labels.Count == 0) throw Error("expected 'case' or 'default'");
			var body = new List<Stmt>();
			while (!Is("case") && !Is("default") && !Is("}"))
			{
				if (Peek().Kind == TokenKind.End) throw Error("expected '}'");
				var s = Statement();
				if (s != null) body.Add(s);
			}
			cases.Add(new SwitchCase(labels, body));
		}
		Expect("}");
		return Mark(new SwitchStmt(value, cases), start);
	}

	// ------------------------------------------------------------ GS1 commands

	private Gs1Command? GluedCommand(Token word)
	{
		for (var length = word.Text.Length - 1; length >= 2; length--)
			if (char.IsAsciiDigit(word.Text[length]) && _catalog.TryGetCommand(word.Text[..length], out var command) && command.Kinds.Length > 0)
				return command;
		return null;
	}

	// A GS1 command takes bare arguments: its name is not followed by what makes it an expression (a call, an assignment, a member).
	// With a space between them, . ++ -- [ start its text (message ..Hmm..;).
	private bool IsCommandForm(Token name)
	{
		var next = PeekAfter(name);
		if (next.Kind == TokenKind.End) return true;
		var spaced = next.Start > name.End;
		if (next.Kind == TokenKind.Operator)
			return next.Text switch
			{
				";" or "}" => true,
				"(" or "." or "[" or "++" or "--" or ":" => spaced,
				"?" or "==" or "!=" or "&&" or "||" => false,
				_ when AssignOps.Contains(next.Text) => false,
				// -5, #c, "text", @x: arguments.
				_ => true,
			};
		return true;
	}

	private Stmt Command(Token name, Gs1Command command)
	{
		// The command's name (for a glued one, its first letters; the number after it is its first argument).
		_pos = name.End;
		_lastEnd = name.End;
		_peekedAt = -1;
		_signs.Add($"old command '{name.Text}'");
		var args = new List<Expr>();
		if (!(Is(";") || Is("}") || Peek().Kind == TokenKind.End))
		{
			for (var i = 0;; i++)
			{
				args.Add(Argument(command.ArgumentKind(i), false, command.IsRest(i)));
				if (!Accept(",")) break;
			}
		}
		EndStatement();
		return _catalog.LowerCommand(name.Text, args, _functionDepth > 0);
	}

	/// <summary>One argument of a GS1 command or function: text, a number, a variable's name, a message code to set, a direction.</summary>
	private Expr Argument(char kind, bool inCall, bool rest)
	{
		// An empty argument (setimgpart a.png,,0,0,32,32;) is the empty text, whatever its kind.
		if (kind is 'N' or 'D' or 'C' && Peek() is var next && (next.Is(",") || next.Is(";") || next.Is(")") || next.Is("}") || next.Kind == TokenKind.End))
			return new StringExpr("");
		switch (kind)
		{
			case 'N':
			{
				var (saved, savedComparing) = (_inCondition, _comparing);
				(_inCondition, _comparing) = (false, false);
				var e = Expression();
				(_inCondition, _comparing) = (saved, savedComparing);
				return e;
			}
			case 'D' when Peek() is { Kind: TokenKind.Identifier } word && Gs1Catalog.Direction(word.Text) is { } direction && PeekAfter(word) is var after && (after.Is(",") || after.Is(";") || after.Is(")") || after.Is("}") || after.Kind == TokenKind.End):
				Take();
				return direction;
			case 'D':
				return Argument('N', inCall, false);
			case 'C' when Peek().Kind == TokenKind.Hash:
				return Code(true, asTarget: true);
			default:
				return Text(inCall, kind == 'V', rest);
		}
	}

	/// <summary>
	///     A text argument, as GS1 reads it: the characters up to the next comma (a command's last text argument: up to the ; or } that
	///     ends the statement, commas included; in a function call: up to the ) that ends the call), with its message codes. Line breaks
	///     are dropped, spaces around the text are not part of it, quotes and // are text. {text} is the text between the braces.
	/// </summary>
	private Expr Text(bool inCall, bool variable, bool rest)
	{
		var s = _lexer.Source;
		var i = _lexer.SkipBlank(_pos);
		if (!variable && i < s.Length && s[i] == '{')
		{
			// {text}: to the matching brace.
			var depth = 0;
			var j = i;
			for (; j < s.Length; j++)
			{
				if (s[j] == '{') depth++;
				else if (s[j] == '}' && --depth == 0) break;
			}
			if (j < s.Length)
			{
				_pos = j + 1;
				_lastEnd = j + 1;
				_peekedAt = -1;
				// Line breaks are dropped, as in any text argument; the spaces before the first line are not part of it.
				return new StringExpr(s[(i + 1)..j].Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).TrimStart());
			}
		}
		var pieces = new List<Expr>();
		var literal = new StringBuilder();
		var parens = 0;
		while (i < s.Length)
		{
			var c = s[i];
			if (inCall)
			{
				if (parens == 0 && (c == ')' || c == ',' && !rest)) break;
			}
			else if (c is ';' or '}' || c == ',' && !rest) break;
			if (c == '#' && i + 1 < s.Length && Gs1Catalog.IsCodeStart(s, i + 1))
			{
				if (literal.Length > 0) pieces.Add(new StringExpr(literal.ToString()));
				literal.Clear();
				_pos = i;
				_peekedAt = -1;
				pieces.Add(Code(true, asTarget: false));
				i = _pos;
				continue;
			}
			if (inCall && c == '(') parens++;
			else if (inCall && c == ')') parens--;
			if (c is not ('\n' or '\r')) literal.Append(c);
			i++;
		}
		_pos = i;
		_lastEnd = i;
		_peekedAt = -1;
		if (literal.Length > 0) pieces.Add(new StringExpr(literal.ToString()));
		// Spaces before the text are not part of it; spaces after it are.
		if (pieces.Count > 0 && pieces[0] is StringExpr first) pieces[0] = first with { Value = first.Value.TrimStart() };
		pieces.RemoveAll(p => p is StringExpr { Value.Length: 0 });
		return variable ? _catalog.VariableReference(pieces) : _catalog.Text(pieces);
	}

	/// <summary>A message code at '#': #c, #C0, #P12, #v(expr), #s(name), #3(-1) ...; as the target of setplayerprop, the code itself.</summary>
	private Expr Code(bool inText, bool asTarget)
	{
		var s = _lexer.Source;
		var start = _lexer.SkipBlank(_pos);
		if (start >= s.Length || s[start] != '#') throw Error("expected a message code");
		var i = start + 1;
		var code = Gs1Catalog.ReadCodeName(s, ref i);
		if (code == null) throw new ParseException("unknown message code #" + (i < s.Length ? s[i] : ' '), start);
		_pos = i;
		_lastEnd = i;
		_peekedAt = -1;
		_signs.Add("message code #" + code);
		var args = new List<Expr>();
		// The arguments follow at once (#v(x), #3(-1)); a code that needs them may have spaces or a line break before them.
		if (Gs1Catalog.NeedsArguments(code))
			while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n') i++;
		if (i < s.Length && s[i] == '(' && _catalog.CodeArguments(code) is { } kinds)
		{
			_pos = i + 1;
			_peekedAt = -1;
			if (!Is(")"))
				for (var k = 0;; k++)
				{
					var kind = kinds.Length == 0 ? 'N' : kinds[Math.Min(k, kinds.Length - 1)];
					args.Add(Argument(kind, true, kind == 'T' && k >= kinds.Length - 1));
					if (!Accept(",")) break;
				}
			Expect(")");
		}
		if (asTarget) return new Gs1CodeExpr(code, args);
		var expr = _catalog.LowerCode(code, args) ?? throw new ParseException($"message code #{code} with these arguments", start);
		if (!inText && Gs1Catalog.IsAttributeCode(code)) _attributeValues.Add(expr);
		return expr;
	}

	// Attribute codes read as values (this.x = #c;): assigned alone, GS1 makes them text.
	private readonly HashSet<Expr> _attributeValues = new(ReferenceEqualityComparer.Instance);

	// ------------------------------------------------------------ expressions

	/// <summary>A condition: in GS1, = compares, and binds after &amp;&amp; and || (a = b = c is a = (b = c)).</summary>
	private Expr Condition()
	{
		var (savedCondition, savedComparing) = (_inCondition, _comparing);
		_inCondition = true;
		_comparing = Legacy;
		try
		{
			return Expression();
		}
		finally
		{
			(_inCondition, _comparing) = (savedCondition, savedComparing);
		}
	}

	// GS1: where = compares (a condition, the value of an assignment). In a condition it is kept as = (the converter writes ==
	// and notes a && b = c); elsewhere it is ==.
	private bool _comparing;

	// How many assignments' values are being read (a = b = c: 1 while reading b = c).
	private int _assignmentDepth;

	private Expr LegacyEquals()
	{
		var left = Or();
		if (!(Legacy && _comparing && Is("="))) return left;
		Take();
		_signs.Add("= comparing");
		return new BinaryExpr(left, _inCondition ? "=" : "==", LegacyEquals());
	}

	public Expr Expression() => Assignment();

	// a, b, c (a statement's expressions, each in turn): each one an assignment, a call or an increment, as GS2 has them.
	private Expr Sequence()
	{
		var first = Expression();
		if (!Is(",")) return first;
		if (!IsStatementExpression(first)) throw Error("expected ';'");
		var items = new List<Expr> { first };
		while (Accept(","))
		{
			var next = Expression();
			if (!IsStatementExpression(next)) throw Error("expected an assignment or a call after ','");
			items.Add(next);
		}
		_gs2Only.AddIf(!Legacy, "a, b");
		return new SequenceExpr(items);
	}

	private static bool IsStatementExpression(Expr e) =>
		e is CallExpr or MethodCallExpr or DynamicCallExpr or NewExpr or BinaryExpr { Op: "=" or ":=" or "+=" or "-=" or "*=" or "/=" or "%=" or "^=" or "@=" or "|=" or "&=" or "<<=" or ">>=" }
			or UnaryExpr { Op: "++" or "--" };

	private Expr Assignment()
	{
		RuntimeHelpers_EnsureStack();
		var left = Conditional();
		var t = Peek();
		if (t.Kind == TokenKind.Operator && AssignOps.Contains(t.Text) && !(Legacy && _comparing && t.Text == "="))
		{
			Take();
			// GS1 has no a = b = c: in the value of an assignment, = compares (in a part read as GS1; GS2's a = b = c assigns both).
			var savedComparing = _comparing;
			if (_dialect == Dialect.Gs1) _comparing = true;
			_assignmentDepth++;
			var right = Assignment();
			_assignmentDepth--;
			_comparing = savedComparing;
			if (t.Text == "=" && _attributeValues.Contains(right)) right = new BinaryExpr(new StringExpr(""), "@", right);
			// GS1: in r = c = 1 ? a : b, c = 1 is the condition of the ?: (it compares).
			if (Legacy && t.Text == "=" && _assignmentDepth > 0 && right is ConditionalExpr choice)
				return choice with { Condition = new BinaryExpr(left, "==", choice.Condition) };
			return new BinaryExpr(left, t.Text, right);
		}
		return left;
	}

	private Expr Conditional()
	{
		var condition = LegacyEquals();
		if (!Accept("?")) return condition;
		_gs2Only.AddIf(!Legacy, "?:");
		var then = Assignment();
		Expect(":");
		var otherwise = Assignment();
		return new ConditionalExpr(condition, then, otherwise);
	}

	private Expr Or()
	{
		var left = And();
		while (Accept("||")) left = new BinaryExpr(left, "||", And());
		return left;
	}

	private Expr And()
	{
		var left = Bits();
		while (Accept("&&")) left = new BinaryExpr(left, "&&", Bits());
		return left;
	}

	private Expr Bits()
	{
		var left = Equality();
		while (Peek() is { Kind: TokenKind.Operator, Text: "&" or "|" or "<<" or ">>" } or { Kind: TokenKind.Identifier, Text: "xor" })
		{
			var t = Take();
			left = new BinaryExpr(left, t.Text, Equality());
		}
		return left;
	}

	private Expr Equality()
	{
		var left = Relational();
		while (true)
		{
			if (Peek() is { Kind: TokenKind.Operator, Text: "==" or "!=" } t)
			{
				Take();
				left = new BinaryExpr(left, t.Text, Relational());
			}
			else if (Legacy && Is("<>"))
			{
				// GS1's <> is !=.
				Take();
				_signs.Add("operator <>");
				left = new BinaryExpr(left, "!=", Relational());
			}
			else if (Is("in"))
			{
				Take();
				left = In(left);
			}
			else return left;
		}
	}

	// value in <a, b> / |a, b| / <a, b| ...  or value in list
	private Expr In(Expr value)
	{
		if (Is("<") || Is("|"))
		{
			var lowExclusive = Take().Text == "<";
			var low = Concat();
			Expect(",");
			var high = Concat();
			bool highExclusive;
			if (Accept(">")) highExclusive = true;
			else if (Accept("|")) highExclusive = false;
			else throw Error("expected '>' or '|'");
			return new InExpr(value, low, high, lowExclusive, highExclusive);
		}
		return new InExpr(value, Relational(), null, false, false);
	}

	private Expr Relational()
	{
		var left = Concat();
		while (Peek() is { Kind: TokenKind.Operator, Text: "<" or "<=" or ">" or ">=" or "=<" or "=>" } t)
		{
			if (t.Text is "=<" or "=>" && Legacy) _signs.Add("operator " + t.Text);
			Take();
			left = new BinaryExpr(left, t.Text, Concat());
		}
		return left;
	}

	private Expr Concat()
	{
		var left = Additive();
		while (true)
		{
			var t = Peek();
			string? op = t.Kind == TokenKind.Operator && t.Text == "@" ? "@"
				: t.Kind == TokenKind.Identifier ? t.Text switch { "SPC" => " ", "NL" => "\n", "TAB" => "\t", _ => null } : null;
			if (op == null) return left;
			Take();
			_gs2Only.AddIf(!Legacy, "text joining (" + t.Text + ")");
			left = new BinaryExpr(left, op, Additive());
		}
	}

	private Expr Additive()
	{
		var left = Multiplicative();
		while (Peek() is { Kind: TokenKind.Operator, Text: "+" or "-" } t)
		{
			Take();
			left = new BinaryExpr(left, t.Text, Multiplicative());
		}
		return left;
	}

	private Expr Multiplicative()
	{
		var left = Unary();
		while (Peek() is { Kind: TokenKind.Operator, Text: "*" or "/" or "%" or "^" } t)
		{
			Take();
			left = new BinaryExpr(left, t.Text, Unary());
		}
		return left;
	}

	private Expr Unary()
	{
		RuntimeHelpers_EnsureStack();
		if (Peek() is { Kind: TokenKind.Operator, Text: "-" or "+" or "!" or "~" or "@" or "++" or "--" } t)
		{
			Take();
			if (t.Text == "@") _gs2Only.AddIf(!Legacy, "@");
			return new UnaryExpr(t.Text, Unary());
		}
		return Postfix(Primary());
	}

	private Expr Postfix(Expr e)
	{
		while (true)
		{
			var t = Peek();
			if (t.Is("."))
			{
				Take();
				if (Is("("))
				{
					Take();
					var name = Expression();
					Expect(")");
					e = Is("(") ? CallRest(new DynamicMemberExpr(e, name)) : new DynamicMemberExpr(e, name);
					continue;
				}
				var member = Peek();
				if (member.Kind is not (TokenKind.Identifier or TokenKind.Number)) throw Error("expected a member name");
				Take();
				if (Is("("))
				{
					Take();
					_gs2Only.AddIf(!Legacy, "method call");
					e = new MethodCallExpr(e, member.Text, Arguments(")"));
				}
				else e = new MemberExpr(e, member.Text);
			}
			else if (t.Is("["))
			{
				Take();
				e = new IndexExpr(e, Arguments("]"));
			}
			else if (t.Kind == TokenKind.Number && t.Text.StartsWith('.') && t.Start == _lastEnd && t.Text[1..].All(char.IsAsciiDigit))
			{
				// o.5: the member named 5 (the lexer read .5 as a number).
				Take();
				e = new MemberExpr(e, t.Text[1..]);
			}
			else if (t.Is("("))
			{
				e = CallRest(e);
			}
			else if (t.Kind == TokenKind.Operator && t.Text is "++" or "--" && _lexer.LineOf(t.Start) == _lexer.LineOf(Math.Max(0, _lastEnd - 1)))
			{
				Take();
				e = new UnaryExpr(t.Text, e, true);
			}
			else return e;
		}
	}

	private Expr CallRest(Expr callee)
	{
		Expect("(");
		var args = Arguments(")");
		return callee is NameExpr n ? new CallExpr(n.Name, args) : new DynamicCallExpr(callee, args);
	}

	private List<Expr> Arguments(string close)
	{
		var (saved, savedComparing) = (_inCondition, _comparing);
		(_inCondition, _comparing) = (false, false);
		var args = new List<Expr>();
		if (!Is(close))
			do
			{
				// An empty argument is the empty text.
				args.Add(Is(",") || Is(close) ? new StringExpr("") : Expression());
			} while (Accept(","));
		Expect(close);
		(_inCondition, _comparing) = (saved, savedComparing);
		return args;
	}

	private Expr Primary()
	{
		var t = Peek();
		switch (t.Kind)
		{
			case TokenKind.Number:
				Take();
				return new NumberExpr(t.Text);
			case TokenKind.String:
				Take();
				_gs2Only.AddIf(!Legacy, "quoted text");
				return new StringExpr(t.Value!);
			case TokenKind.Hash when Legacy:
				return Code(false, asTarget: false);
			case TokenKind.Operator when t.Text == "(":
			{
				Take();
				var inner = Expression();
				Expect(")");
				return inner;
			}
			case TokenKind.Operator when t.Text == "{":
			{
				Take();
				var items = new List<Expr>();
				var saved = _inCondition;
				_inCondition = false;
				while (!Is("}"))
				{
					items.Add(Expression());
					if (!Accept(",")) break;
				}
				_inCondition = saved;
				Expect("}");
				return new ArrayExpr(items);
			}
			case TokenKind.Identifier:
				switch (t.Text)
				{
					case "true":
						Take();
						return new BoolExpr(true);
					case "false":
						Take();
						return new BoolExpr(false);
					case "null":
						Take();
						return new NullExpr();
					case "new":
						return New();
					case "function":
					{
						Take();
						Expect("(");
						var parameters = new List<string>();
						if (!Is(")"))
							do
							{
								var parameter = Name();
								while (Is(".") && PeekAfter(Peek()).Kind == TokenKind.Identifier)
								{
									Take();
									parameter += "." + Take().Text;
								}
								parameters.Add(parameter);
							} while (Accept(","));
						Expect(")");
						return new FunctionExpr(parameters, Block());
					}
				}
				if (Keywords.Contains(t.Text)) throw Error("unexpected keyword");
				Take();
				// A GS1 function with text arguments: strequals(#c,hello).
				if (Legacy && Is("(") && _catalog.TryGetFunction(t.Text, out var function))
				{
					Take();
					var saved = _inCondition;
					_inCondition = false;
					var args = new List<Expr>();
					if (!Is(")"))
						for (var i = 0;; i++)
						{
							args.Add(Argument(function.ArgumentKind(i), true, function.IsRest(i)));
							if (!Accept(",")) break;
						}
					Expect(")");
					_inCondition = saved;
					return _catalog.LowerFunction(t.Text, args);
				}
				if (Legacy && _catalog.LowerName(t.Text) is { } lowered) return lowered;
				if (_dialect == Dialect.Gs1MainCode && _catalog.LowerName(t.Text, counts: false) is { } player) return player;
				return new NameExpr(t.Text);
			default:
				throw Error("expected a value");
		}
	}

	private Expr New()
	{
		Take();
		_gs2Only.AddIf(!Legacy, "new");
		if (Is("["))
		{
			var sizes = new List<Expr>();
			while (Accept("["))
			{
				sizes.Add(Expression());
				Expect("]");
			}
			return new NewArrayExpr(sizes);
		}
		if (Accept("("))
		{
			var typeExpr = Expression();
			Expect(")");
			return new NewExpr("", Accept("(") ? Arguments(")") : [], typeExpr);
		}
		var type = Name();
		var args = Accept("(") ? Arguments(")") : [];
		return new NewExpr(type, args);
	}
}
