// GS2 source from the syntax tree. Precedence (low to high), the same as the parser's: assignment, ?:, ||, &&, & | << >>,
// == != in, < <= > >=, @ SPC NL TAB, + -, * / % (GS1: * /, then %), ^, prefix, postfix.
using System.Globalization;
using System.Text;
using Uplift.Syntax;

namespace Uplift.Conversion;

public sealed class Printer
{
	private readonly StringBuilder _out = new();
	private int _indent;

	// "} else " written at the start of the next line (an else-if chain).
	private string? _pendingPrefix;

	public Comments? Comments { get; init; }

	public string Indent { get; init; } = "  ";

	/// <summary>Comments written /* */ (a lambda's body, written on one line).</summary>
	public bool BlockComments { get; init; }

	public override string ToString() => _out.ToString();

	public void Line(string text)
	{
		for (var i = 0; i < _indent; i++) _out.Append(Indent);
		if (_pendingPrefix != null)
		{
			_out.Append(_pendingPrefix);
			_pendingPrefix = null;
		}
		_out.Append(text).Append('\n');
	}

	public void Blank()
	{
		if (_out.Length > 0 && !_out.ToString().EndsWith("\n\n", StringComparison.Ordinal)) _out.Append('\n');
	}

	// ---- scripts ----

	public void Item(Item item)
	{
		switch (item)
		{
			case FunctionDecl f:
				Function(f);
				break;
			case StatementItem s:
				Statement(s.Statement);
				break;
			case ConstDecl c:
				Comments?.Before(c, this);
				Line($"const {c.Name} = {Expr(c.Value)};");
				break;
			case EnumDecl e:
				Comments?.Before(e, this);
				Line($"enum {(e.Name == null ? "" : e.Name + " ")}{{ {string.Join(", ", e.Members.Select(m => m.Value == null ? m.Name : m.Name + " = " + Expr(m.Value)))} }}");
				break;
		}
	}

	public void Function(FunctionDecl f)
	{
		Comments?.Before(f, this);
		var name = f.Owner == null ? f.Name : f.Owner + "." + f.Name;
		Line($"{(f.Public ? "public " : "")}function {name}({string.Join(", ", f.Params)}) {{" + (Comments?.Trailing(f) is { Length: > 0 } t ? " " + t : ""));
		Block(f.Body);
		Line("}");
	}

	// ---- statements ----

	public void Statements(IEnumerable<Stmt> body)
	{
		foreach (var statement in body) Statement(statement);
	}

	public void Block(List<Stmt> body)
	{
		_indent++;
		Statements(body);
		_indent--;
	}

	public void Statement(Stmt statement)
	{
		Comments?.Before(statement, this);
		switch (statement)
		{
			case ExprStmt e:
				Line(Expr(e.Expression) + ";" + Trailing(statement));
				break;
			case BlockStmt block:
				Line("{" + Trailing(statement));
				Block(block.Body);
				Line("}");
				break;
			case ReturnStmt r:
				Line((r.Value == null ? "return" : "return " + Expr(r.Value)) + ";" + Trailing(statement));
				break;
			case IfStmt i:
				If(i);
				break;
			case ForStmt f:
				Line($"for ({(f.Init == null ? "" : Expr(f.Init))}; {(f.Condition == null ? "" : Expr(f.Condition))}; {(f.Step == null ? "" : Expr(f.Step))}) {{" + Trailing(statement));
				Block(f.Body);
				Line("}");
				break;
			case ForEachStmt f:
				Line($"for ({Expr(f.Variable)} : {Expr(f.Source)}) {{" + Trailing(statement));
				Block(f.Body);
				Line("}");
				break;
			case WhileStmt w:
				Line($"while ({Expr(w.Condition)}) {{" + Trailing(statement));
				Block(w.Body);
				Line("}");
				break;
			case DoWhileStmt d:
				Line("do {" + Trailing(statement));
				Block(d.Body);
				Line($"}} while ({Expr(d.Condition)});");
				break;
			case WithStmt w:
				Line($"with ({Expr(w.Target)}) {{" + Trailing(statement));
				Block(w.Body);
				Line("}");
				break;
			case SwitchStmt s:
				Line($"switch ({Expr(s.Value)}) {{" + Trailing(statement));
				_indent++;
				foreach (var c in s.Cases)
				{
					foreach (var label in c.Labels) Line(label == null ? "default:" : $"case {Expr(label)}:");
					Block(c.Body);
				}
				_indent--;
				Line("}");
				break;
			case BreakStmt b:
				Line((b.Label == null ? "break;" : $"break {b.Label};") + Trailing(statement));
				break;
			case ContinueStmt c:
				Line((c.Label == null ? "continue;" : $"continue {c.Label};") + Trailing(statement));
				break;
			case LabeledStmt l:
				Line(l.Label + ":");
				Statement(l.Body);
				break;
			case NewStmt n:
				Line($"new {(n.TypeExpr != null ? "(" + Expr(n.TypeExpr) + ")" : n.TypeName)}({Args(n.Args)}) {{" + Trailing(statement));
				Block(n.Body);
				Line("};");
				break;
			case CommentStmt c:
				Line(BlockComments && c.Text.StartsWith("//", StringComparison.Ordinal) ? "/* " + c.Text[2..].Trim().Replace("*/", "* /", StringComparison.Ordinal) + " */" : c.Text);
				break;
			default:
				throw new NotSupportedException("statement " + statement.GetType().Name);
		}
	}

	private string Trailing(Stmt statement) => Comments?.Trailing(statement) is { Length: > 0 } t ? " " + t : "";

	private void If(IfStmt i)
	{
		Line($"if ({Expr(i.Condition)}) {{" + Trailing(i));
		Block(i.Then);
		if (i.Else is [IfStmt nested])
		{
			// The else-if's own comments go just above it, at the end of the branch before.
			if (Comments != null)
			{
				_indent++;
				Comments.Before(nested, this);
				_indent--;
			}
			_pendingPrefix = "} else ";
			If(nested);
			return;
		}
		if (i.HasElse || i.Else.Count > 0)
		{
			Line("} else {");
			Block(i.Else);
		}
		Line("}");
	}

	// ---- expressions ----

	private string Args(List<Expr> args) => string.Join(", ", args.Select(Expr));

	private static int Prec(Expr e) =>
		e switch
		{
			BinaryExpr b => OpPrec(b.Op),
			SequenceExpr => 0,
			InExpr => 6,
			ConditionalExpr => 2,
			UnaryExpr { Postfix: false } => 12,
			_ => 13,
		};

	// ^ and % are written with parentheses wherever their tier could be read another way (GS1 binds % above * and /; ^ is
	// above * / % in GS2, its grouping with itself and with the prefix operators is not documented).
	private static bool Ambiguous(BinaryExpr parent, Expr operand) =>
		operand is BinaryExpr { Op: var op } && parent.Op is "*" or "/" or "%" && (op == "^" || op is "*" or "/" or "%" && (op == "%") != (parent.Op == "%"));

	public static int OpPrec(string op) =>
		op switch
		{
			"=" or ":=" or "+=" or "-=" or "*=" or "/=" or "^=" or "%=" or "@=" or "|=" or "&=" or "<<=" or ">>=" => 1,
			"||" => 3,
			"&&" => 4,
			"&" or "|" or "<<" or ">>" or "xor" => 5,
			"==" or "!=" => 6,
			"<" or "<=" or ">" or ">=" or "=<" or "=>" => 7,
			"@" or " " or "\n" or "\t" => 8,
			"+" or "-" => 9,
			"*" or "/" or "%" => 10,
			"^" => 11,
			_ => throw new NotSupportedException("operator " + op),
		};

	private string Wrap(Expr e, bool parens) => parens ? "(" + Expr(e) + ")" : Expr(e);

	public string Expr(Expr e)
	{
		switch (e)
		{
			case BinaryExpr b:
			{
				var p = OpPrec(b.Op);
				var op = b.Op switch { " " => "SPC", "\n" => "NL", "\t" => "TAB", _ => b.Op };
				if (p == 1) return $"{Wrap(b.Left, Prec(b.Left) < 13)} {op} {Wrap(b.Right, Prec(b.Right) < 1)}";
				if (b.Op == "^") return $"{Wrap(b.Left, Prec(b.Left) < 13)} ^ {Wrap(b.Right, Prec(b.Right) < 13)}";
				return $"{Wrap(b.Left, Prec(b.Left) < p || Ambiguous(b, b.Left))} {op} {Wrap(b.Right, Prec(b.Right) <= p || Ambiguous(b, b.Right))}";
			}
			case InExpr i:
			{
				var left = Wrap(i.Value, Prec(i.Value) < 6);
				if (i.High == null) return $"{left} in {Wrap(i.Low, Prec(i.Low) <= 7)}";
				return $"{left} in {(i.LowExclusive ? "<" : "|")}{Wrap(i.Low, Prec(i.Low) < 8)}, {Wrap(i.High, Prec(i.High) < 8)}{(i.HighExclusive ? ">" : "|")}";
			}
			case ConditionalExpr t:
				return $"{Wrap(t.Condition, Prec(t.Condition) <= 2)} ? {Expr(t.Then)} : {Wrap(t.Else, Prec(t.Else) < 2)}";
			case UnaryExpr { Postfix: true } u:
				return Wrap(u.Operand, Prec(u.Operand) < 13) + u.Op;
			case UnaryExpr u:
			{
				var inner = Wrap(u.Operand, Prec(u.Operand) < 12);
				// "- -x" and "--x" differ; "!-x" is fine.
				if (u.Op is "-" or "+" && inner.StartsWith(u.Op[0])) inner = "(" + Expr(u.Operand) + ")";
				return u.Op + inner;
			}
			case MemberExpr m:
				return $"{Wrap(m.Target, Prec(m.Target) < 13 || m.Target is NumberExpr)}.{m.Name}";
			case DynamicMemberExpr d:
				return $"{Wrap(d.Target, Prec(d.Target) < 13 || d.Target is NumberExpr)}.({Expr(d.Name)})";
			case IndexExpr a:
				return $"{Wrap(a.Target, Prec(a.Target) < 13)}[{Args(a.Indices)}]";
			case NameExpr id:
				return id.Name;
			case NumberExpr n:
				return n.Text;
			case StringExpr s:
				return Quote(s.Value);
			case BoolExpr b:
				return b.Value ? "true" : "false";
			case NullExpr:
				return "null";
			case CallExpr c:
				return $"{c.Name}({Args(c.Args)})";
			case MethodCallExpr m:
				return $"{Wrap(m.Target, Prec(m.Target) < 13 || m.Target is NumberExpr)}.{m.Name}({Args(m.Args)})";
			case DynamicCallExpr d:
				return d.Callee is DynamicMemberExpr ? $"{Expr(d.Callee)}({Args(d.Args)})" : $"({Expr(d.Callee)})({Args(d.Args)})";
			case NewExpr n:
				return $"new {(n.TypeExpr != null ? "(" + Expr(n.TypeExpr) + ")" : n.TypeName)}({Args(n.Args)})";
			case SequenceExpr q:
				return string.Join(", ", q.Items.Select(i => Wrap(i, Prec(i) < 1)));
			case NewArrayExpr n:
				return "new" + string.Concat(n.Sizes.Select(d => "[" + Expr(d) + "]"));
			case ArrayExpr a:
				return "{" + Args(a.Items) + "}";
			case FunctionExpr l:
			{
				// The body is written on one line: its comments as /* */.
				var inner = new Printer { Indent = Indent, BlockComments = true };
				inner.Statements(l.Body);
				return $"function({string.Join(", ", l.Params)}) {{ {inner.ToString().Replace("\n", " ").Trim()} }}";
			}
			default:
				throw new NotSupportedException("expression " + e.GetType().Name);
		}
	}

	public static string Quote(string value)
	{
		var sb = new StringBuilder("\"");
		foreach (var c in value)
			sb.Append(c switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\n' => "\\n",
				'\t' => "\\t",
				'\r' => "\\r",
				_ => c.ToString(CultureInfo.InvariantCulture),
			});
		return sb.Append('"').ToString();
	}
}
