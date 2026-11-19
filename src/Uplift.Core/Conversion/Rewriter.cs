// A post-order rewrite of the syntax tree: children first, then the hooks on the rebuilt node. A node whose children did not change
// and that no hook replaced is returned as it is (so comments stay with it); a rebuilt node is passed to keep(original, made).
// With fold, constant conditions are folded: if (true) A else B is A; true && x is x; false && x is false (x must have no effect).
using Alpaxis.Scripting.GSAst;

namespace Uplift.Conversion;

public sealed class Rewriter(
	Func<Expr, Expr?>? expr = null,
	Func<Stmt, IEnumerable<Stmt>?>? stmt = null,
	Func<object, object, object>? keep = null,
	bool fold = false,
	Action<object, object>? inherit = null)
{
	private T K<T>(object original, T made) where T : class => ReferenceEquals(original, made) || keep == null ? made : (T)keep(original, made);

	public List<Stmt> Statements(List<Stmt> body)
	{
		var result = new List<Stmt>(body.Count);
		var changed = false;
		foreach (var s in body)
		{
			var list = Statement(s);
			if (list.Count != 1 || !ReferenceEquals(list[0], s)) changed = true;
			result.AddRange(list);
		}
		return changed ? result : body;
	}

	private List<Stmt> Statement(Stmt s)
	{
		var rebuilt = Rebuild(s);
		if (rebuilt.Count == 1 && stmt?.Invoke(rebuilt[0]) is { } replaced) return replaced.ToList();
		return rebuilt;
	}

	private List<Stmt> Rebuild(Stmt s)
	{
		switch (s)
		{
			case ExprStmt e:
			{
				var x = Expr(e.Expression);
				return [ReferenceEquals(x, e.Expression) ? s : K(s, e with { Expression = x })];
			}
			case InlineStmt i:
			{
				var inner = Statement(i.Statement);
				if (inner.Count == 1 && ReferenceEquals(inner[0], i.Statement)) return [s];
				return inner.Count == 1 ? [K(s, i with { Statement = inner[0] })] : [K(s, new BlockStmt(inner))];
			}
			case BlockStmt b:
			{
				var body = Statements(b.Body);
				return [ReferenceEquals(body, b.Body) ? s : K(s, b with { Body = body })];
			}
			case ReturnStmt r:
			{
				var x = Expr(r.Expression);
				return [ReferenceEquals(x, r.Expression) ? s : K(s, r with { Expression = x })];
			}
			case IfStmt i:
			{
				var c = Expr(i.Condition);
				if (fold && c is BoolExpr constant)
				{
					var branch = Statements(constant.Value ? i.ThenBody : i.ElseBody);
					if (branch.Count > 0) inherit?.Invoke(s, branch[0]);
					return branch;
				}
				var then = Statements(i.ThenBody);
				var otherwise = Statements(i.ElseBody);
				if (ReferenceEquals(c, i.Condition) && ReferenceEquals(then, i.ThenBody) && ReferenceEquals(otherwise, i.ElseBody)) return [s];
				return [K(s, i with { Condition = c, ThenBody = then, ElseBody = otherwise })];
			}
			case ForStmt f:
			{
				var init = f.Init == null ? null : Expr(f.Init);
				var c = Expr(f.Condition);
				var post = f.Post == null ? null : Expr(f.Post);
				var body = Statements(f.Body);
				if (ReferenceEquals(init, f.Init) && ReferenceEquals(c, f.Condition) && ReferenceEquals(post, f.Post) && ReferenceEquals(body, f.Body)) return [s];
				return [K(s, f with { Init = init, Condition = c, Post = post, Body = body })];
			}
			case ForEachStmt f:
			{
				var n = Expr(f.Name);
				var src = Expr(f.Source);
				var body = Statements(f.Body);
				if (ReferenceEquals(n, f.Name) && ReferenceEquals(src, f.Source) && ReferenceEquals(body, f.Body)) return [s];
				return [K(s, f with { Name = n, Source = src, Body = body })];
			}
			case WhileStmt w:
			{
				var c = Expr(w.Condition);
				var body = Statements(w.Body);
				if (ReferenceEquals(c, w.Condition) && ReferenceEquals(body, w.Body)) return [s];
				return [K(s, w with { Condition = c, Body = body })];
			}
			case DoWhileStmt d:
			{
				var c = Expr(d.Condition);
				var body = Statements(d.Body);
				if (ReferenceEquals(c, d.Condition) && ReferenceEquals(body, d.Body)) return [s];
				return [K(s, d with { Condition = c, Body = body })];
			}
			case WithStmt w:
			{
				var t = Expr(w.Target);
				var body = Statements(w.Body);
				if (ReferenceEquals(t, w.Target) && ReferenceEquals(body, w.Body)) return [s];
				return [K(s, w with { Target = t, Body = body })];
			}
			case SwitchStmt sw:
			{
				var x = Expr(sw.Expression);
				var changed = !ReferenceEquals(x, sw.Expression);
				var cases = sw.Cases.Select(c =>
				{
					var labels = c.Labels.Select(l => l == null ? null : Expr(l)).ToList();
					var body = Statements(c.Body);
					if (!ReferenceEquals(body, c.Body) || labels.Where((l, i) => !ReferenceEquals(l, c.Labels[i])).Any()) { changed = true; return new SwitchCase(labels, body); }
					return c;
				}).ToList();
				return [changed ? K(s, sw with { Expression = x, Cases = cases }) : s];
			}
			case NewStmt n:
			{
				var args = Exprs(n.Args);
				var body = Statements(n.Body);
				if (ReferenceEquals(args, n.Args) && ReferenceEquals(body, n.Body)) return [s];
				return [K(s, n with { Args = args, Body = body })];
			}
			case FunctionStmt f:
			{
				var body = Statements(f.Function.Body);
				return [ReferenceEquals(body, f.Function.Body) ? s : K(s, f with { Function = f.Function with { Body = body } })];
			}
			default:
				return [s];
		}
	}

	private List<Expr> Exprs(List<Expr> list)
	{
		List<Expr>? result = null;
		for (var i = 0; i < list.Count; i++)
		{
			var x = Expr(list[i]);
			if (!ReferenceEquals(x, list[i]) && result == null) result = list.Take(i).ToList();
			result?.Add(x);
		}
		return result ?? list;
	}

	public Expr Expr(Expr e)
	{
		var rebuilt = RebuildExpr(e);
		if (fold) rebuilt = Fold(rebuilt);
		var hooked = expr?.Invoke(rebuilt);
		if (hooked != null && fold) hooked = Fold(hooked);
		return hooked ?? rebuilt;
	}

	private Expr RebuildExpr(Expr e)
	{
		switch (e)
		{
			case BinaryExpr b:
			{
				var l = Expr(b.Left);
				var r = Expr(b.Right);
				return ReferenceEquals(l, b.Left) && ReferenceEquals(r, b.Right) ? e : K(e, b with { Left = l, Right = r });
			}
			case InExpr i:
			{
				var x = Expr(i.Expression);
				var lo = Expr(i.Lower);
				var hi = i.Upper == null ? null : Expr(i.Upper);
				return ReferenceEquals(x, i.Expression) && ReferenceEquals(lo, i.Lower) && ReferenceEquals(hi, i.Upper) ? e : K(e, i with { Expression = x, Lower = lo, Upper = hi });
			}
			case TernaryExpr t:
			{
				var c = Expr(t.Condition);
				var a = Expr(t.WhenTrue);
				var b = Expr(t.WhenFalse);
				return ReferenceEquals(c, t.Condition) && ReferenceEquals(a, t.WhenTrue) && ReferenceEquals(b, t.WhenFalse) ? e : K(e, t with { Condition = c, WhenTrue = a, WhenFalse = b });
			}
			case UnaryExpr u:
			{
				var x = Expr(u.Expression);
				return ReferenceEquals(x, u.Expression) ? e : K(e, u with { Expression = x });
			}
			case StringCastExpr c:
			{
				var x = Expr(c.Expression);
				return ReferenceEquals(x, c.Expression) ? e : K(e, c with { Expression = x });
			}
			case CastExpr c:
			{
				var x = Expr(c.Expression);
				return ReferenceEquals(x, c.Expression) ? e : K(e, c with { Expression = x });
			}
			case MemberExpr m:
			{
				var o = Expr(m.Object);
				return ReferenceEquals(o, m.Object) ? e : K(e, m with { Object = o });
			}
			case DynamicMemberExpr d:
			{
				var o = Expr(d.Object);
				var n = Expr(d.Name);
				return ReferenceEquals(o, d.Object) && ReferenceEquals(n, d.Name) ? e : K(e, d with { Object = o, Name = n });
			}
			case DynamicVarExpr v:
			{
				var n = Expr(v.Name);
				return ReferenceEquals(n, v.Name) ? e : K(e, v with { Name = n });
			}
			case ArrayIndexExpr a:
			{
				var t = Expr(a.Target);
				var i = Expr(a.Index);
				return ReferenceEquals(t, a.Target) && ReferenceEquals(i, a.Index) ? e : K(e, a with { Target = t, Index = i });
			}
			case MultiArrayIndexExpr m:
			{
				var t = Expr(m.Target);
				var i = Exprs(m.Indices);
				return ReferenceEquals(t, m.Target) && ReferenceEquals(i, m.Indices) ? e : K(e, m with { Target = t, Indices = i });
			}
			case CallExpr c:
			{
				var args = Exprs(c.Args);
				return ReferenceEquals(args, c.Args) ? e : K(e, c with { Args = args });
			}
			case ChainedCallExpr c:
			{
				var call = Expr(c.Call);
				var args = Exprs(c.Args);
				return ReferenceEquals(call, c.Call) && ReferenceEquals(args, c.Args) ? e : K(e, c with { Call = call, Args = args });
			}
			case MethodCallExpr m:
			{
				var o = Expr(m.Object);
				var args = Exprs(m.Args);
				return ReferenceEquals(o, m.Object) && ReferenceEquals(args, m.Args) ? e : K(e, m with { Object = o, Args = args });
			}
			case DynamicMethodCallExpr d:
			{
				var o = Expr(d.Object);
				var n = Expr(d.Name);
				var args = Exprs(d.Args);
				return ReferenceEquals(o, d.Object) && ReferenceEquals(n, d.Name) && ReferenceEquals(args, d.Args) ? e : K(e, d with { Object = o, Name = n, Args = args });
			}
			case NewObjectExpr n:
			{
				var args = Exprs(n.Args);
				return ReferenceEquals(args, n.Args) ? e : K(e, n with { Args = args });
			}
			case NewArrayExpr n:
			{
				var d = Exprs(n.Dimensions);
				return ReferenceEquals(d, n.Dimensions) ? e : K(e, n with { Dimensions = d });
			}
			case ArrayLiteralExpr a:
			{
				var v = Exprs(a.Values);
				return ReferenceEquals(v, a.Values) ? e : K(e, a with { Values = v });
			}
			case LambdaExpr l:
			{
				var body = Statements(l.Body);
				return ReferenceEquals(body, l.Body) ? e : K(e, l with { Body = body });
			}
			default:
				return e;
		}
	}

	private static Expr Fold(Expr e)
	{
		switch (e)
		{
			case UnaryExpr { Op: "!", Expression: BoolExpr b }:
				return new BoolExpr(!b.Value);
			case BinaryExpr { Op: "&&" } and:
				if (and.Left is BoolExpr { Value: false }) return Drop(and.Right, false);
				if (and.Left is BoolExpr { Value: true }) return and.Right;
				if (and.Right is BoolExpr { Value: true }) return and.Left;
				if (and.Right is BoolExpr { Value: false }) return Drop(and.Left, false);
				return e;
			case BinaryExpr { Op: "||" } or:
				if (or.Left is BoolExpr { Value: true }) return Drop(or.Right, true);
				if (or.Left is BoolExpr { Value: false }) return or.Right;
				if (or.Right is BoolExpr { Value: false }) return or.Left;
				if (or.Right is BoolExpr { Value: true }) return Drop(or.Left, true);
				return e;
			case TernaryExpr { Condition: BoolExpr c } t:
				return c.Value ? t.WhenTrue : t.WhenFalse;
			case BinaryExpr { Op: "==" or "!=", Left: BoolExpr l, Right: NumberExpr r } cmp when r.Text is "0" or "1":
				return new BoolExpr(l.Value == (r.Text == "1") == (cmp.Op == "=="));
			default:
				// An event flag anywhere else (a value, not a condition) stays a number: 1 for the event, 0 otherwise.
				return e;
		}
	}

	/// <summary>The constant an operand folds to, when the operand has no effect of its own (no call, no assignment).</summary>
	private static Expr Drop(Expr operand, bool value)
	{
		if (!IsPure(operand)) throw new UnconvertibleException("a condition with a call in it would lose the call when the event flag is folded");
		return new BoolExpr(value);
	}

	public static bool IsPure(Expr e) =>
		e switch
		{
			BoolExpr or NumberExpr or StringExpr or NullExpr or IdentifierExpr => true,
			BinaryExpr { Op: "=" or "+=" or "-=" or "*=" or "/=" or "^=" or "%=" or "@=" or "|=" or "&=" or "<<=" or ">>=" or ":=" } => false,
			BinaryExpr b => IsPure(b.Left) && IsPure(b.Right),
			UnaryExpr { Op: "++" or "--" } => false,
			UnaryExpr u => IsPure(u.Expression),
			StringCastExpr c => IsPure(c.Expression),
			CastExpr c => IsPure(c.Expression),
			MemberExpr m => IsPure(m.Object),
			ArrayIndexExpr a => IsPure(a.Target) && IsPure(a.Index),
			InExpr i => IsPure(i.Expression) && IsPure(i.Lower) && (i.Upper == null || IsPure(i.Upper)),
			TernaryExpr t => IsPure(t.Condition) && IsPure(t.WhenTrue) && IsPure(t.WhenFalse),
			// Text functions that read only.
			CallExpr { Name: "strequals" or "strlen" or "startswith" or "strcontains" or "indexof" or "strtofloat" or "abs" or "int" or "arraylen" or "sarraylen" or "keydown" or "keydown2" or "onwall" or "onwall2" or "onwater" or "hasweapon" or "findweapon" or "contains" or "lindexof" } c => c.Args.All(IsPure),
			MethodCallExpr { Name: "starts" or "pos" or "length" or "size" or "index" or "substring" or "trim" or "lower" or "upper" or "hasweapon" or "tokenize" } m => IsPure(m.Object) && m.Args.All(IsPure),
			_ => false,
		};
}
