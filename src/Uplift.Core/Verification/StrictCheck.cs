using System.Text.RegularExpressions;
using Alpaxis.Scripting.GS2Compiler;

namespace Uplift.Verification;

public enum CheckStatus
{
	/// <summary>GS2: compiles as GS2, the compiler's Auto rule reads it as GS2, no GS1 signs.</summary>
	Ok,

	/// <summary>Does not compile as GS2.</summary>
	NotGs2,

	/// <summary>Compiles as GS2 but has GS1 signs, or the Auto rule would read it as GS1.</summary>
	Gs1,
}

/// <param name="Status">The verdict.</param>
/// <param name="Detail">The compiler's error or the GS1 signs found.</param>
/// <param name="TopLevelCode">For a GS2 part: the first line of code outside functions other than join/enum/const, or null.</param>
public sealed record CheckResult(CheckStatus Status, string Detail, string? TopLevelCode);

/// <summary>
///     The strict GS2 check: a part passes when it compiles as GS2, the compiler's Auto rule also picks GS2 for it, and it has none
///     of the compiler's GS1 signs (a //#GS1 marker, #codes or an old command with bare arguments, a GS1 event tested outside
///     functions, an old command alone outside functions).
/// </summary>
public static class StrictCheck
{
	public static CheckResult Check(string code, ScriptKind kind, string name = "npc")
	{
		var target = kind.CompileTarget();
		var (bytes, error, _) = Compile(code, target, name, ScriptGrammar.GS2);
		if (bytes == null) return new(CheckStatus.NotGs2, error, null);
		var signs = new List<string>();
		if (ScriptLanguage.Marker(code) == ScriptGrammar.GS1) signs.Add("//#GS1 marker");
		if (ScriptLanguage.HasLegacySyntax(code)) signs.Add("legacy syntax (#codes or an old command with bare arguments): " + FirstLegacyLine(code));
		if (ScriptLanguage.TestsGs1Event(code)) signs.Add("tests a GS1 event outside functions");
		if (ScriptLanguage.HasBareCommand(code)) signs.Add("old command alone outside functions");
		var (autoBytes, _, autoGrammar) = Compile(code, target, name, ScriptGrammar.Auto);
		if (autoBytes != null && autoGrammar == ScriptGrammar.GS1) signs.Add("the compiler's Auto rule reads it as GS1");
		if (signs.Count > 0) return new(CheckStatus.Gs1, string.Join("; ", signs), null);
		return new(CheckStatus.Ok, "", ScriptLanguage.HasTopLevelCode(code) ? TopLevelBeyondJoin(code) : null);
	}

	/// <summary>Whether a part compiles at all, in either language (Auto).</summary>
	public static bool CompilesAtAll(string code, ScriptKind kind) => Compile(code, kind.CompileTarget(), "npc", ScriptGrammar.Auto).Bytes != null;

	private static (byte[]? Bytes, string Error, ScriptGrammar Chosen) Compile(string code, string target, string name, ScriptGrammar grammar)
	{
		try
		{
			var r = Interface.CompileCode(code, target, name, withHeader: false, grammar: grammar);
			return r.Success ? (r.ByteCode, "", r.Grammar) : (null, (r.ErrMsg ?? "?").Replace("\r", "", StringComparison.Ordinal).Split('\n')[0], r.Grammar);
		}
		catch (Exception e)
		{
			return (null, "exception " + e.GetType().Name + ": " + e.Message.Split('\n')[0], grammar);
		}
	}

	private static string FirstLegacyLine(string code)
	{
		var lines = code.Replace("\r", "", StringComparison.Ordinal).Split('\n');
		for (var i = 0; i < lines.Length; i++)
			if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && ScriptLanguage.HasLegacySyntax(lines[i]))
				return $"line {i + 1}: {lines[i].Trim()}";
		return "?";
	}

	private static readonly Regex AllowedTopLevel = new(@"^(public\s+|static\s+)*function\b|^\{|^\}|^(this\.)?join\s*\(|^enum\b|^const\b|^;$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Code outside functions that is more than join(...)/enum/const declarations: the first such line, or null.
	private static string? TopLevelBeyondJoin(string code)
	{
		var depth = 0;
		var lines = code.Replace("\r", "", StringComparison.Ordinal).Split('\n');
		var inBlock = false;
		for (var i = 0; i < lines.Length; i++)
		{
			var t = Regex.Replace(lines[i], "//.*$", "").Trim();
			if (inBlock)
			{
				if (t.Contains("*/", StringComparison.Ordinal)) inBlock = false;
				continue;
			}
			if (t.StartsWith("/*", StringComparison.Ordinal))
			{
				if (!t.Contains("*/", StringComparison.Ordinal)) inBlock = true;
				continue;
			}
			if (depth == 0 && t.Length > 0 && !AllowedTopLevel.IsMatch(t)) return $"line {i + 1}: {t}";
			depth += t.Count(c => c == '{') - t.Count(c => c == '}');
			if (depth < 0) depth = 0;
		}
		return null;
	}
}
