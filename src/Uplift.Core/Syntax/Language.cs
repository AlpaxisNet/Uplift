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
///     Which language a part is in. A part is GS2 when it parses as GS2 and has none of GS1's signs: a //#GS1 marker, an event flag
///     (created, playerenters, timeout, actionX ...) tested outside functions, or an old command alone outside functions (hide;). A
///     //#GS2 marker makes it GS2. A part that is not GS2 syntax is read as GS1, unless it uses syntax GS1 does not have (SPC, @,
///     quoted text, method calls, ?:, in, function parameters ...): then its functions are read as GS2 and its main code as GS1. A
///     part that parses in none of these ways is broken.
/// </summary>
public static class Language
{
	public static ReadPart Read(string code, Gs1Catalog? catalog = null)
	{
		catalog ??= Gs1Catalog.Default;
		var gs2 = Parser.Parse(code, Dialect.Gs2, catalog);
		if (gs2.Success)
		{
			var signs = Gs1SignsOf(gs2, catalog);
			if (HasMarker(code, "//#GS2")) signs.Clear();
			if (signs.Count == 0) return new ReadPart(Reading.Gs2, gs2, signs);
			// GS1 main code: read with GS1's rules (hurt -1; is the command, not a subtraction).
			var main = Parser.Parse(code, Dialect.Gs1MainCode, catalog);
			return new ReadPart(Reading.Gs2WithGs1MainCode, main.Success ? main : gs2, signs);
		}
		// GS1 has none of GS2's syntax: a part whose functions use it is GS2 with GS1 main code (and GS1 statements where it is not
		// GS2); otherwise it is GS1.
		var mixed = Parser.Parse(code, Dialect.Gs1MainCode, catalog);
		ReadPart Mixed() => new(Reading.Gs2WithGs1MainCode, mixed, mixed.Signs.Found.Count > 0 ? mixed.Signs.Found : ["GS1 main code"]);
		if (mixed.Success && mixed.Gs2Only.Found.Count > 0) return Mixed();
		var gs1 = Parser.Parse(code, Dialect.Gs1, catalog);
		if (gs1.Success) return new ReadPart(Reading.Gs1, gs1, gs1.Signs.Found);
		return mixed.Success ? Mixed() : new ReadPart(Reading.Broken, gs1, []);
	}

	/// <summary>The GS1 signs of a part that parses as GS2.</summary>
	public static List<string> Gs1SignsOf(ParseResult gs2, Gs1Catalog catalog)
	{
		var signs = new List<string>();
		if (gs2.Comments.Any(c => c.Text.StartsWith("//#GS1", StringComparison.OrdinalIgnoreCase))) signs.Add("//#GS1 marker");
		var events = new EventTable();
		var main = gs2.Script!.Items.OfType<StatementItem>().Select(s => s.Statement).ToList();
		var testsEvent = false;
		new Rewriter(stmt: s =>
		{
			Expr? condition = s switch { IfStmt i => i.Condition, WhileStmt w => w.Condition, _ => null };
			if (condition != null && Gs1Converter.Atoms(condition).Any(events.IsEventFlag)) testsEvent = true;
			return null;
		}).Statements(main);
		if (testsEvent) signs.Add("tests a GS1 event outside functions");
		// An old command alone outside functions (hide;) is GS1's main code. (In a GS2 function it does nothing, and the part is GS2.)
		var bare = new List<string>();
		new Rewriter(stmt: s =>
		{
			if (s is ExprStmt { Expression: NameExpr { Name: var word } } && catalog.TryGetCommand(word, out _)) bare.Add(word);
			return null;
		}).Statements(main);
		if (bare.Count > 0) signs.Add("old command alone outside functions: " + bare[0]);
		return signs;
	}

	private static bool HasMarker(string code, string marker) =>
		code.Replace("\r", "", StringComparison.Ordinal).Split('\n').Any(l => l.TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase));
}
