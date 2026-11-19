// The comments of a part, each given to the statement it belongs to by source line: a comment alone on its line(s) leads the next
// statement; a comment after code on its line trails the statement that starts there.
using Uplift.Syntax;

namespace Uplift.Conversion;

public sealed class Comment(int line, int endLine, string text, bool trailing)
{
	public int Line { get; } = line;

	public int EndLine { get; } = endLine;

	public string Text { get; } = text;

	public bool IsTrailing { get; } = trailing;

	public bool Printed { get; set; }
}

public sealed class Comments
{
	private readonly List<Comment> _all = [];
	private readonly Dictionary<object, List<Comment>> _leading = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<object, List<Comment>> _trailing = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<object, object> _alias = new(ReferenceEqualityComparer.Instance);

	public static Comments Collect(IEnumerable<SourceComment> source, Dictionary<object, (int Start, int Stop)> lines)
	{
		var result = new Comments();
		foreach (var c in source.OrderBy(c => c.Start))
		{
			// Language markers are dropped: the converted part is GS2.
			if (c.Text.StartsWith("//#GS1", StringComparison.OrdinalIgnoreCase) || c.Text.StartsWith("//#GS2", StringComparison.OrdinalIgnoreCase)) continue;
			result._all.Add(new Comment(c.Line, c.EndLine, c.Text, c.Trailing));
		}

		// Owners: nodes by start line; for the same start line the one that spans the most lines (the outer one).
		var nodes = lines.Select(pair => (Node: pair.Key, pair.Value.Start, pair.Value.Stop)).OrderBy(n => n.Start).ThenByDescending(n => n.Stop - n.Start).ToList();
		foreach (var comment in result._all)
		{
			if (comment.IsTrailing)
			{
				// The innermost node that starts on the comment's line, else the innermost that covers it.
				var owner = nodes.Where(n => n.Start == comment.Line).OrderBy(n => n.Stop - n.Start).Select(n => n.Node).FirstOrDefault()
				            ?? nodes.Where(n => n.Start <= comment.Line && comment.Line <= n.Stop).OrderBy(n => n.Stop - n.Start).Select(n => n.Node).FirstOrDefault();
				if (owner != null)
				{
					Add(result._trailing, owner, comment);
					continue;
				}
			}

			var next = nodes.FirstOrDefault(n => n.Start > comment.EndLine);
			if (next.Node != null) Add(result._leading, next.Node, comment);
		}

		return result;
	}

	private static void Add(Dictionary<object, List<Comment>> map, object node, Comment comment)
	{
		if (!map.TryGetValue(node, out var list)) map[node] = list = [];
		list.Add(comment);
	}

	/// <summary>Says that a node made from another (a statement with a changed condition) carries the other's comments.</summary>
	public void Alias(object made, object original)
	{
		if (!ReferenceEquals(made, original)) _alias[made] = _alias.GetValueOrDefault(original, original);
	}

	/// <summary>A statement folded away (an event test) gives its comments to the first statement of the branch that stays.</summary>
	public void Inherit(object from, object to)
	{
		from = Resolve(from);
		var moved = new List<Comment>();
		if (_leading.TryGetValue(from, out var lead)) moved.AddRange(lead);
		if (_trailing.TryGetValue(from, out var trail)) moved.AddRange(trail);
		if (moved.Count == 0) return;
		var target = Resolve(to);
		if (!_leading.TryGetValue(target, out var list)) _leading[target] = list = [];
		list.InsertRange(0, moved.Where(c => !list.Contains(c)));
	}

	private object Resolve(object node) => _alias.GetValueOrDefault(node, node);

	public void Before(object node, Printer printer)
	{
		if (!_leading.TryGetValue(Resolve(node), out var list)) return;
		foreach (var comment in list.Where(c => !c.Printed))
		{
			comment.Printed = true;
			foreach (var line in comment.Text.Split('\n')) printer.Line(line.TrimEnd());
		}
	}

	public string Trailing(object node)
	{
		if (!_trailing.TryGetValue(Resolve(node), out var list)) return "";
		var texts = list.Where(c => !c.Printed).ToList();
		foreach (var c in texts) c.Printed = true;
		// A block comment over several lines cannot trail: it is written as it is, on the same line it starts.
		return string.Join(" ", texts.Select(c => c.Text.Replace("\n", " ")));
	}

	/// <summary>Writes every comment no statement printed (its statement folded away, or after the last statement).</summary>
	public void Rest(Printer printer)
	{
		foreach (var comment in _all.Where(c => !c.Printed).OrderBy(c => c.Line))
		{
			comment.Printed = true;
			foreach (var line in comment.Text.Split('\n')) printer.Line(line.TrimEnd());
		}
	}

	public int Count => _all.Count;

	/// <summary>Marks every comment unprinted again (for a second printing).</summary>
	public void Reset()
	{
		foreach (var c in _all) c.Printed = false;
	}
}
