using System.Text.RegularExpressions;
using Uplift.Conversion;
using Uplift.Rules;

namespace Uplift.Jobs;

/// <summary>
///     Classes converted from GS1 that other scripts join. GS1 ran only the joining script's main code (a joined class gave it its
///     functions, and its event tests made the script's code run): the class's own code outside functions never ran. Converted, that
///     code is the class's event functions, which GS2 runs; and a script that joins the class and defines the same event function on
///     the same side hides the class's. Each such pair is reported, for a person to decide which code should run.
/// </summary>
public static class JoinAnalysis
{
	private static readonly Regex Join = new(@"\bjoin\s*\(\s*""([^""]+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex Function = new(@"\bfunction\s+(on[A-Za-z0-9_]+)\s*\(", RegexOptions.Compiled);

	internal static void Collect(FileOutcome outcome, ScriptSide side, string code)
	{
		foreach (var (kind, text) in CodeTokens.Split(code))
		{
			if (kind != CodeTokens.Kind.Code) continue;
			foreach (Match m in Function.Matches(text)) outcome.EventFunctions.Add((side, m.Groups[1].Value));
		}
		// The class's name is a string: match on the whole code with comments removed.
		var withoutComments = string.Concat(CodeTokens.Split(code).Where(t => t.Kind != CodeTokens.Kind.Comment).Select(t => t.Text));
		foreach (Match m in Join.Matches(withoutComments)) outcome.Joins.Add((side, m.Groups[1].Value));
	}

	public static IEnumerable<string> Warnings(IEnumerable<FileOutcome> files)
	{
		var all = files.ToList();
		var classes = new Dictionary<string, FileOutcome>(StringComparer.OrdinalIgnoreCase);
		foreach (var f in all.Where(f => f.Kind == ScriptKind.Class && f.Parts.Any(p => p.Result == PartStatus.Converted)))
			classes[Path.GetFileNameWithoutExtension(f.File.RelativePath)] = f;
		if (classes.Count == 0) yield break;
		foreach (var f in all)
			foreach (var (side, name) in f.Joins.Distinct())
			{
				if (!classes.TryGetValue(name, out var cls) || ReferenceEquals(cls, f)) continue;
				if (!cls.Parts.Any(p => p.Side == side && p.Result == PartStatus.Converted)) continue;
				var shared = cls.EventFunctions.Where(e => e.Side == side).Select(e => e.Function)
					.Intersect(f.EventFunctions.Where(e => e.Side == side).Select(e => e.Function), StringComparer.OrdinalIgnoreCase).ToList();
				foreach (var fn in shared)
					yield return $"{f.File.RelativePath} ({side.ToString().ToLowerInvariant()}) joins class {name}, which was converted from GS1, and defines {fn} itself: " +
					             $"GS2 runs the script's own {fn}, not the class's. The class's {fn} is its GS1 code outside functions, which never ran in GS1 when the class was joined: check which code should run.";
			}
	}
}
