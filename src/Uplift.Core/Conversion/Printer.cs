// GS2 source from the syntax tree. Precedence follows the GS2 grammar (low to high):
// assignment, ?:, ||, &&, & | xor << >>, == != in, < <= > >=, @, + -, * / % ^, prefix, postfix.
using System.Globalization;
using System.Text;
using Alpaxis.Scripting.GSAst;

namespace Uplift.Conversion;

public sealed class Printer
{
	private readonly StringBuilder _out = new();
	private int _indent;
	public Comments? Comments { get; init; }
	public string Indent { get; init; } = "  ";

	/// <summary>Numbers the reader folded from constant arithmetic, written as the arithmetic (pi / 2), when set.</summary>
	public Dictionary<object, Expr>? Folds { get; init; }

	private Expr Unfold(Expr e) => e is NumberExpr && Folds != null && Folds.TryGetValue(e, out var source) ? source : e;

	public override string ToString() => _out.ToString();

	public void Line(string text)
	{
		for (var i = 0; i < _indent; i++) _out.Append(Indent);
		if (_pendingPrefix != null) { _out.Append(_pendingPrefix); _pendingPrefix = null; }
		_out.Append(text).Append('\n');
	}

	public void Raw(string text) => _out.Append(text);

	public void Blank()
	{
		if (_out.Length > 0 && !_out.ToString().EndsWith("\n\n", StringComparison.Ordinal)) _out.Append('\n');
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
			case InlineStmt inline:
				Statement(inline.Statement);
				break;
			case BlockStmt block:
				Line("{" + Trailing(statement));
				Block(block.Body);
				Line("}");
				break;
			case ReturnStmt r:
				Line((r.Expression is NumberExpr { Text: "0" } ? "return" : "return " + Expr(r.Expression)) + ";" + Trailing(statement));
				break;
			case IfStmt i:
				If(i, "if");
				break;
			case ForStmt f:
				Line($"{Label(f.Label)}for ({(f.Init == null ? "" : Expr(f.Init))}; {Expr(f.Condition)}; {(f.Post == null ? "" : Expr(f.Post))}) {{" + Trailing(statement));
				Block(f.Body);
				Line("}");
				break;
			case ForEachStmt f:
				Line($"{Label(f.Label)}for ({Expr(f.Name)} : {Expr(f.Source)}) {{" + Trailing(statement));
				Block(f.Body);
				Line("}");
				break;
			case WhileStmt w:
				Line($"{Label(w.Label)}while ({Expr(w.Condition)}) {{" + Trailing(statement));
				Block(w.Body);
				Line("}");
				break;
			case DoWhileStmt d:
				Line($"{Label(d.Label)}do {{");
				Block(d.Body);
				Line($"}} while ({Expr(d.Condition)});");
				break;
			case WithStmt w:
				Line($"with ({Expr(w.Target)}) {{" + Trailing(statement));
				Block(w.Body);
				Line("}");
				break;
			case SwitchStmt s:
				Line($"{Label(s.Label)}switch ({Expr(s.Expression)}) {{");
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
				Line("break" + (b.Label == null ? "" : " " + b.Label) + ";" + Trailing(statement));
				break;
			case ContinueStmt c:
				Line("continue" + (c.Label == null ? "" : " " + c.Label) + ";" + Trailing(statement));
				break;
			case GotoStmt g:
				Line($"goto {g.Label};");
				break;
			case LabelStmt l:
				Line($"{l.Label}:");
				break;
			case NewStmt n:
				Line($"new {(n.TypeExpression != null ? "(" + Expr(n.TypeExpression) + ")" : n.TypeName)}({Args(n.Args)}) {{");
				Block(n.Body);
				Line("}");
				break;
			case FunctionStmt f:
				Function(f.Function);
				break;
			case CommentStmt c:
				Line(c.Text);
				break;
			default:
				throw new NotSupportedException("statement " + statement.GetType().Name);
		}
	}

	private static string Label(string? label) => label == null ? "" : label + ": ";

	private string Trailing(Stmt statement) => Comments?.Trailing(statement) is { Length: > 0 } t ? " " + t : "";

	private void If(IfStmt i, string keyword)
	{
		Line($"{keyword} ({Expr(i.Condition)}) {{" + Trailing(i));
		Block(i.ThenBody);
		if (i.ElseBody is [InlineStmt { Statement: IfStmt nested } inline])
		{
			// The else-if's own comments go just above it, at the end of the branch before.
			if (Comments != null) { _indent++; Comments.Before(inline, this); Comments.Before(nested, this); _indent--; }
			_pendingPrefix = "} else ";
			If(nested, "if");
			return;
		}
		if (i.HasElse || i.ElseBody.Count > 0)
		{
			Line("} else {");
			Block(i.ElseBody);
		}
		Line("}");
	}

	// "} else " written at the start of the next line (an else-if chain).
	private string? _pendingPrefix;

	public void Function(FunctionNode f)
	{
		Comments?.Before(f, this);
		var name = f.ObjectName == null ? f.Name : f.ObjectName + "." + f.Name;
		Line($"{(f.Public ? "public " : "")}function {name}({Args(f.Args)}) {{" + (Comments?.Trailing(f) is { Length: > 0 } t ? " " + t : ""));
		Block(f.Body);
		Line("}");
	}

	// ---- expressions ----

	private string Args(List<Expr> args) => string.Join(", ", args.Select(a => Expr(a)));

	private int Prec(Expr e) => PrecOf(Unfold(e));

	private static int PrecOf(Expr e) =>
		e switch
		{
			BinaryExpr b => OpPrec(b.Op),
			InExpr       => 6,
			TernaryExpr  => 2,
			UnaryExpr { Postfix: false } => 11,
			StringCastExpr => 11,
			_ => 12,
		};

	public static int OpPrec(string op) =>
		op switch
		{
			"=" or ":=" or "+=" or "-=" or "*=" or "/=" or "^=" or "%=" or "@=" or "|=" or "&=" or "<<=" or ">>=" => 1,
			"||" => 3,
			"&&" => 4,
			"&" or "|" or "xor" or "<<" or ">>" => 5,
			"==" or "!=" or "=<" or "=>" => 6,
			"<" or "<=" or ">" or ">=" => 7,
			"@" or " " or "\n" or "\t" => 8,
			"+" or "-" => 9,
			"*" or "/" or "%" or "^" => 10,
			_ => throw new NotSupportedException("operator " + op),
		};

	private string Wrap(Expr e, bool parens) => parens ? "(" + Expr(e) + ")" : Expr(e);

	public string Expr(Expr e)
	{
		e = Unfold(e);
		switch (e)
		{
			case BinaryExpr b:
			{
				var p = OpPrec(b.Op);
				var op = b.Op switch { " " => "SPC", "\n" => "NL", "\t" => "TAB", _ => b.Op };
				if (p == 1) return $"{Wrap(b.Left, Prec(b.Left) < 12)} {op} {Wrap(b.Right, Prec(b.Right) < 1)}";
				return $"{Wrap(b.Left, Prec(b.Left) < p)} {op} {Wrap(b.Right, Prec(b.Right) <= p)}";
			}
			case InExpr i:
			{
				var left = Wrap(i.Expression, Prec(i.Expression) < 6);
				if (i.Upper == null) return $"{left} in {Wrap(i.Lower, Prec(i.Lower) <= 7)}";
				return $"{left} in {(i.LowerExclusive ? "<" : "|")}{Expr(i.Lower)}, {Expr(i.Upper)}{(i.UpperExclusive ? ">" : "|")}";
			}
			case TernaryExpr t:
				return $"{Wrap(t.Condition, Prec(t.Condition) <= 2)} ? {Expr(t.WhenTrue)} : {Wrap(t.WhenFalse, Prec(t.WhenFalse) < 2)}";
			case UnaryExpr { Postfix: true } u:
				return Wrap(u.Expression, Prec(u.Expression) < 12) + u.Op;
			case UnaryExpr u:
			{
				var inner = Wrap(u.Expression, Prec(u.Expression) < 11);
				// "- -x" and "--x" differ; "!-x" is fine.
				if ((u.Op == "-" && inner.StartsWith('-')) || (u.Op == "+" && inner.StartsWith('+'))) inner = "(" + Expr(u.Expression) + ")";
				return u.Op + inner;
			}
			case StringCastExpr s:
				return "@" + Wrap(s.Expression, Prec(s.Expression) < 11);
			case CastExpr c:
				return $"{c.Type}({Expr(c.Expression)})";
			case MemberExpr m:
				return $"{Wrap(m.Object, Prec(m.Object) < 12 || m.Object is NumberExpr)}.{m.Name}";
			case DynamicMemberExpr d:
				return $"{Wrap(d.Object, Prec(d.Object) < 12)}.({Expr(d.Name)})";
			case DynamicVarExpr v:
				return $"makevar({Expr(v.Name)})";
			case ArrayIndexExpr a:
				return $"{Wrap(a.Target, Prec(a.Target) < 12)}[{Expr(a.Index)}]";
			case MultiArrayIndexExpr m:
				return $"{Wrap(m.Target, Prec(m.Target) < 12)}[{Args(m.Indices)}]";
			case IdentifierExpr id:
				return id.Name;
			case EnumExpr en:
				return $"{en.EnumName}.{en.MemberName}";
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
			case ChainedCallExpr c:
				return $"({Expr(c.Call)})({Args(c.Args)})";
			case MethodCallExpr m:
				return $"{Wrap(m.Object, Prec(m.Object) < 12 || m.Object is NumberExpr)}.{m.Name}({Args(m.Args)})";
			case DynamicMethodCallExpr d:
				return $"{Wrap(d.Object, Prec(d.Object) < 12)}.({Expr(d.Name)})({Args(d.Args)})";
			case NewObjectExpr n:
				return $"new {(n.TypeExpression != null ? "(" + Expr(n.TypeExpression) + ")" : n.TypeName)}({Args(n.Args)})";
			case NewArrayExpr n:
				return "new" + string.Concat(n.Dimensions.Select(d => "[" + Expr(d) + "]"));
			case ArrayLiteralExpr a:
				return "{" + Args(a.Values) + "}";
			case LambdaExpr l:
			{
				var inner = new Printer { Indent = Indent, Folds = Folds };
				inner.Statements(l.Body);
				return $"function({Args(l.Args)}) {{ {inner.ToString().Replace("\n", " ").Trim()} }}";
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
