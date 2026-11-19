using System.Text.RegularExpressions;
using Uplift.Syntax;

namespace Uplift.Verification;

public enum CheckStatus
{
	/// <summary>GS2: parses as GS2 and has none of GS1's signs.</summary>
	Ok,

	/// <summary>Not GS2 syntax (GS1, or broken).</summary>
	NotGs2,

	/// <summary>GS2 syntax with GS1 signs (event flags tested outside functions, old commands alone there, a //#GS1 marker).</summary>
	Gs1,
}

/// <param name="Status">The verdict.</param>
/// <param name="Detail">The parse error or the GS1 signs found.</param>
/// <param name="TopLevelCode">For a GS2 part: the first line of code outside functions other than join/enum/const, or null.</param>
public sealed record CheckResult(CheckStatus Status, string Detail, string? TopLevelCode);

/// <summary>The strict GS2 check (see <see cref="Language" /> for what makes a part GS2).</summary>
public static class StrictCheck
{
	public static CheckResult Check(string code, Gs1Catalog? catalog = null) => Check(Language.Read(code, catalog));

	public static CheckResult Check(ReadPart read) => read.Reading switch
	{
		Reading.Gs2 => new CheckResult(CheckStatus.Ok, "", TopLevelBeyondJoin(read.Script!)),
		Reading.Gs2WithGs1MainCode => new CheckResult(CheckStatus.Gs1, string.Join("; ", read.Gs1Signs), null),
		Reading.Gs1 => new CheckResult(CheckStatus.NotGs2, "GS1: " + string.Join("; ", read.Gs1Signs.Take(3)), null),
		_ => new CheckResult(CheckStatus.NotGs2, read.Error, null),
	};

	private static readonly Regex Join = new(@"^(this\.)?join\s*\(", RegexOptions.Compiled);

	// Code outside functions that is more than join(...): the first such statement, or null.
	private static string? TopLevelBeyondJoin(Script script)
	{
		foreach (var item in script.Items.OfType<StatementItem>())
		{
			if (item.Statement is ExprStmt { Expression: CallExpr { Name: "join" } or MethodCallExpr { Target: NameExpr { Name: "this" }, Name: "join" } }) continue;
			var text = new Conversion.Printer().Expr(item.Statement is ExprStmt e ? e.Expression : new NameExpr(item.Statement.GetType().Name));
			if (!Join.IsMatch(text)) return text.Length > 80 ? text[..80] : text;
		}
		return null;
	}
}
