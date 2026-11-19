// A post-order rewrite of the syntax tree: children first, then the hooks on the rebuilt node. A node whose children did not change
// and that no hook replaced is returned as it is (so comments stay with it); a rebuilt node is passed to keep(original, made).
// With fold, constant conditions are folded: if (true) A else B is A; true && x is x; false && x is false (x must have no effect).
using Uplift.Syntax;

namespace Uplift.Conversion;

public sealed class Rewriter(
	Func<Expr, Expr?>? expr = null,
	Func<Stmt, IEnumerable<Stmt>?>? stmt = null,
	Func<object, object, object>? keep = null,
	bool fold = false,
	Action<object, object>? inherit = null)
{
	private T K<T>(object original, T made) where T : class => ReferenceEquals(original, made) || keep == null ? made : (T)keep(original, made);

	private static bool Same(object? a, object? b) => ReferenceEquals(a, b);

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

	private Expr? Optional(Expr? e) => e == null ? null : Expr(e);

	private List<Stmt> Rebuild(Stmt s)
	{
		switch (s)
		{
			case ExprStmt e:
			{
				var x = Expr(e.Expression);
				return [Same(x, e.Expression) ? s : K(s, e with { Expression = x })];
			}
			case BlockStmt b:
			{
				var body = Statements(b.Body);
				return [Same(body, b.Body) ? s : K(s, b with { Body = body })];
			}
			case ReturnStmt r:
			{
				var x = Optional(r.Value);
				return [Same(x, r.Value) ? s : K(s, r with { Value = x })];
			}
			case IfStmt i:
			{
				var c = Expr(i.Condition);
				if (fold && c is BoolExpr constant)
				{
					var branch = Statements(constant.Value ? i.Then : i.Else);
					if (branch.Count > 0) inherit?.Invoke(s, branch[0]);
					return branch;
				}
				var then = Statements(i.Then);
				var otherwise = Statements(i.Else);
				if (Same(c, i.Condition) && Same(then, i.Then) && Same(otherwise, i.Else)) return [s];
				return [K(s, i with { Condition = c, Then = then, Else = otherwise })];
			}
			case ForStmt f:
			{
				var init = Optional(f.Init);
				var c = Optional(f.Condition);
				var step = Optional(f.Step);
				var body = Statements(f.Body);
				if (Same(init, f.Init) && Same(c, f.Condition) && Same(step, f.Step) && Same(body, f.Body)) return [s];
				return [K(s, f with { Init = init, Condition = c, Step = step, Body = body })];
			}
			case ForEachStmt f:
			{
				var v = Expr(f.Variable);
				var src = Expr(f.Source);
				var body = Statements(f.Body);
				if (Same(v, f.Variable) && Same(src, f.Source) && Same(body, f.Body)) return [s];
				return [K(s, f with { Variable = v, Source = src, Body = body })];
			}
			case WhileStmt w:
			{
				var c = Expr(w.Condition);
				var body = Statements(w.Body);
				if (Same(c, w.Condition) && Same(body, w.Body)) return [s];
				return [K(s, w with { Condition = c, Body = body })];
			}
			case DoWhileStmt d:
			{
				var body = Statements(d.Body);
				var c = Expr(d.Condition);
				if (Same(c, d.Condition) && Same(body, d.Body)) return [s];
				return [K(s, d with { Condition = c, Body = body })];
			}
			case WithStmt w:
			{
				var t = Expr(w.Target);
				var body = Statements(w.Body);
				if (Same(t, w.Target) && Same(body, w.Body)) return [s];
				return [K(s, w with { Target = t, Body = body })];
			}
			case SwitchStmt sw:
			{
				var x = Expr(sw.Value);
				var changed = !Same(x, sw.Value);
				var cases = sw.Cases.Select(c =>
				{
					var labels = c.Labels.Select(l => l == null ? null : Expr(l)).ToList();
					var body = Statements(c.Body);
					if (!Same(body, c.Body) || labels.Where((l, i) => !ReferenceEquals(l, c.Labels[i])).Any())
					{
						changed = true;
						return new SwitchCase(labels, body);
					}
					return c;
				}).ToList();
				return [changed ? K(s, sw with { Value = x, Cases = cases }) : s];
			}
			case NewStmt n:
			{
				var args = Exprs(n.Args);
				var body = Statements(n.Body);
				var type = Optional(n.TypeExpr);
				if (Same(args, n.Args) && Same(body, n.Body) && Same(type, n.TypeExpr)) return [s];
				return [K(s, n with { Args = args, Body = body, TypeExpr = type })];
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
				return Same(l, b.Left) && Same(r, b.Right) ? e : K(e, b with { Left = l, Right = r });
			}
			case InExpr i:
			{
				var x = Expr(i.Value);
				var lo = Expr(i.Low);
				var hi = Optional(i.High);
				return Same(x, i.Value) && Same(lo, i.Low) && Same(hi, i.High) ? e : K(e, i with { Value = x, Low = lo, High = hi });
			}
			case ConditionalExpr t:
			{
				var c = Expr(t.Condition);
				var a = Expr(t.Then);
				var b = Expr(t.Else);
				return Same(c, t.Condition) && Same(a, t.Then) && Same(b, t.Else) ? e : K(e, t with { Condition = c, Then = a, Else = b });
			}
			case UnaryExpr u:
			{
				var x = Expr(u.Operand);
				return Same(x, u.Operand) ? e : K(e, u with { Operand = x });
			}
			case MemberExpr m:
			{
				var o = Expr(m.Target);
				return Same(o, m.Target) ? e : K(e, m with { Target = o });
			}
			case DynamicMemberExpr d:
			{
				var o = Expr(d.Target);
				var n = Expr(d.Name);
				return Same(o, d.Target) && Same(n, d.Name) ? e : K(e, d with { Target = o, Name = n });
			}
			case IndexExpr a:
			{
				var t = Expr(a.Target);
				var i = Exprs(a.Indices);
				return Same(t, a.Target) && Same(i, a.Indices) ? e : K(e, a with { Target = t, Indices = i });
			}
			case CallExpr c:
			{
				var args = Exprs(c.Args);
				return Same(args, c.Args) ? e : K(e, c with { Args = args });
			}
			case MethodCallExpr m:
			{
				var o = Expr(m.Target);
				var args = Exprs(m.Args);
				return Same(o, m.Target) && Same(args, m.Args) ? e : K(e, m with { Target = o, Args = args });
			}
			case DynamicCallExpr d:
			{
				var callee = Expr(d.Callee);
				var args = Exprs(d.Args);
				return Same(callee, d.Callee) && Same(args, d.Args) ? e : K(e, d with { Callee = callee, Args = args });
			}
			case NewExpr n:
			{
				var args = Exprs(n.Args);
				var type = Optional(n.TypeExpr);
				return Same(args, n.Args) && Same(type, n.TypeExpr) ? e : K(e, n with { Args = args, TypeExpr = type });
			}
			case SequenceExpr q:
			{
				var items = Exprs(q.Items);
				return Same(items, q.Items) ? e : K(e, q with { Items = items });
			}
			case NewArrayExpr n:
			{
				var d = Exprs(n.Sizes);
				return Same(d, n.Sizes) ? e : K(e, n with { Sizes = d });
			}
			case ArrayExpr a:
			{
				var v = Exprs(a.Items);
				return Same(v, a.Items) ? e : K(e, a with { Items = v });
			}
			case FunctionExpr l:
			{
				var body = Statements(l.Body);
				return Same(body, l.Body) ? e : K(e, l with { Body = body });
			}
			default:
				return e;
		}
	}

	private static Expr Fold(Expr e)
	{
		switch (e)
		{
			case UnaryExpr { Op: "!", Operand: BoolExpr b }:
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
			case ConditionalExpr { Condition: BoolExpr c } t:
				return c.Value ? t.Then : t.Else;
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
			BoolExpr or NumberExpr or StringExpr or NullExpr or NameExpr => true,
			BinaryExpr { Op: "=" or "+=" or "-=" or "*=" or "/=" or "^=" or "%=" or "@=" or "|=" or "&=" or "<<=" or ">>=" or ":=" } => false,
			BinaryExpr b => IsPure(b.Left) && IsPure(b.Right),
			UnaryExpr { Op: "++" or "--" } => false,
			UnaryExpr u => IsPure(u.Operand),
			MemberExpr m => IsPure(m.Target),
			IndexExpr a => IsPure(a.Target) && a.Indices.All(IsPure),
			InExpr i => IsPure(i.Value) && IsPure(i.Low) && (i.High == null || IsPure(i.High)),
			ConditionalExpr t => IsPure(t.Condition) && IsPure(t.Then) && IsPure(t.Else),
			// Functions that only read.
			CallExpr { Name: "strequals" or "strlen" or "startswith" or "strcontains" or "contains" or "indexof" or "strtofloat" or "abs" or "int" or "float" or "arraylen" or "sarraylen" or "keydown" or "keydown2" or "onwall" or "onwall2" or "onwater" or "hasweapon" or "findweapon" or "lindexof" } c => c.Args.All(IsPure),
			MethodCallExpr { Name: "starts" or "pos" or "length" or "size" or "index" or "substring" or "trim" or "lower" or "upper" or "hasweapon" or "tokenize" } m => IsPure(m.Target) && m.Args.All(IsPure),
			_ => false,
		};
}
