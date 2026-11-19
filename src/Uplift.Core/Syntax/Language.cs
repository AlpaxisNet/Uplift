using Uplift.Conversion;

namespace Uplift.Syntax;

/// <summary>How a part was read.</summary>
public enum Reading
{
	/// <summary>GS2, with nothing of GS1 in it.</summary>
	Gs2,

	/// <summary>GS2 syntax with GS1 main code (event flags tested outside functions, old commands there).</summary>
	Gs2WithGs1MainCode,

	/// <summary>GS1 (it is not GS2: old commands with bare arguments, message codes ...).</summary>
	Gs1,

	/// <summary>Neither.</summary>
	Broken,
}

/// <summary>A part as read: how, the tree, and the parse that made it (lines, comments), or the error.</summary>
public sealed record ReadPart(Reading Reading, ParseResult Parse, IReadOnlyList<string> Gs1Signs)
{
	public Script? Script => Parse.Script;

	public string Error => Parse.Error is { } e ? $"line {Parse.ErrorLine}: {e}" : "";
}

/// <summary>
///     Which language a part is in. The first line comment that starts with //#GS1 or //#GS2 decides. Otherwise a part is GS2 when it
///     parses as GS2 and has none of GS1's signs: an event flag (created, playerenters, timeout, actionX ...) tested outside functions
///     or timeout = x; there, or an old command alone outside functions (hide;, show;, destroy; and enableweapons; are GS2's too); a
///     part with an event function (function onCreated()) is GS2 unless it tests an event outside functions. A part that is not GS2
///     syntax is read as GS1, unless it uses syntax GS1 does not have (SPC, @, method calls, function parameters ...) or has an event
///     function: then its functions are read as GS2 and its main code as GS1. A part that parses in none of these ways is broken.
/// </summary>
public static class Language
{
	// Old commands GS2 scripts also call bare outside functions.
	private static readonly HashSet<string> Gs2Commands = new(StringComparer.OrdinalIgnoreCase) { "hide", "show", "destroy", "enableweapons" };

	public static ReadPart Read(string code, Gs1Catalog? catalog = null)
	{
		catalog ??= Gs1Catalog.Default;
		var gs2 = Parser.Parse(code, Dialect.Gs2, catalog);
		if (gs2.Success)
		{
			var signs = Gs1SignsOf(gs2, catalog);
			if (signs.Count == 0) return new ReadPart(Reading.Gs2, gs2, signs);
			// GS1 main code: read with GS1's rules (hurt -1; is the command, not a subtraction).
			var main = Parser.Parse(code, Dialect.Gs1MainCode, catalog);
			return new ReadPart(Reading.Gs2WithGs1MainCode, main.Success ? main : gs2, signs);
		}
		// GS1 has none of GS2's syntax: a part whose functions use it, or that has an event function, is GS2 with GS1 main code (and
		// GS1 statements where it is not GS2); otherwise it is GS1.
		var mixed = Parser.Parse(code, Dialect.Gs1MainCode, catalog);
		ReadPart Mixed() => new(Reading.Gs2WithGs1MainCode, mixed, mixed.Signs.Found.Count > 0 ? mixed.Signs.Found : ["GS1 main code"]);
		var marker = Marker(mixed.Comments.Count > 0 ? mixed.Comments : gs2.Comments);
		if (mixed.Success && marker != "//#GS1" && (mixed.Gs2Only.Found.Count > 0 || HasEventFunction(mixed.Script!))) return Mixed();
		var gs1 = Parser.Parse(code, Dialect.Gs1, catalog);
		if (gs1.Success) return new ReadPart(Reading.Gs1, gs1, gs1.Signs.Found);
		return mixed.Success ? Mixed() : new ReadPart(Reading.Broken, gs1, []);
	}

	/// <summary>The GS1 signs of a part that parses as GS2.</summary>
	public static List<string> Gs1SignsOf(ParseResult gs2, Gs1Catalog catalog)
	{
		var signs = new List<string>();
		switch (Marker(gs2.Comments))
		{
			case "//#GS2":
				return signs;
			case "//#GS1":
				signs.Add("//#GS1 marker");
				return signs;
		}
		var events = new EventTable();
		var main = gs2.Script!.Items.OfType<StatementItem>().Select(s => s.Statement).ToList();
		var testsEvent = false;
		var bare = new List<string>();
		new Rewriter(stmt: s =>
		{
			Expr? condition = s switch { IfStmt i => i.Condition, WhileStmt w => w.Condition, _ => null };
			// An event's word anywhere in the condition (if (playertouchsme && this.open = 0)).
			if (condition != null) Gs1Converter.Walk([new ExprStmt(condition)], e => testsEvent |= e is NameExpr { Name: var n } && events.IsEventFlag(n));
			// timeout = 0.05; starts GS1's timer.
			if (s is ExprStmt { Expression: BinaryExpr { Left: NameExpr { Name: var t }, Op: "=" } } && t.Equals("timeout", StringComparison.OrdinalIgnoreCase)) testsEvent = true;
			// An old command alone outside functions (dontblock;) is GS1's main code. (In a GS2 function it does nothing, and the part is GS2.)
			if (s is ExprStmt { Expression: NameExpr { Name: var word } } && catalog.TryGetCommand(word, out _) && !Gs2Commands.Contains(word)) bare.Add(word);
			return null;
		}).Statements(main);
		if (testsEvent)
		{
			signs.Add("tests a GS1 event outside functions");
			return signs;
		}
		// A part with an event function is GS2.
		if (HasEventFunction(gs2.Script)) return signs;
		if (bare.Count > 0) signs.Add("old command alone outside functions: " + bare[0]);
		return signs;
	}

	/// <summary>The marker that decides a part's language: the first line comment that starts with //#GS1 or //#GS2 (any case), or null.</summary>
	public static string? Marker(IReadOnlyCollection<SourceComment> comments)
	{
		foreach (var c in comments.OrderBy(c => c.Start))
		{
			if (c.Text.StartsWith("//#GS1", StringComparison.OrdinalIgnoreCase)) return "//#GS1";
			if (c.Text.StartsWith("//#GS2", StringComparison.OrdinalIgnoreCase)) return "//#GS2";
		}
		return null;
	}

	/// <summary>Whether a part has a GS2 event function: function onCreated(), public function onActionServerSide().</summary>
	public static bool HasEventFunction(Script script) =>
		script.Items.OfType<FunctionDecl>().Any(f => f.Owner == null && f.Name.Length > 2 && f.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && char.IsAsciiLetterUpper(f.Name[2]));
}
