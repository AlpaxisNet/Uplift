using System.Text;
using System.Text.RegularExpressions;

namespace Uplift.Rules;

/// <summary>
///     A fix of GS2 text, run on the code of a part after its conversion (and on parts that were GS2 already). It sees only code: the
///     text inside strings and comments is never changed (see <see cref="CodeTokens" />). This is where regular expressions are safe;
///     a GS1 → GS2 conversion is not a text replacement (event code is split into functions, = compares inside conditions, comments
///     and strings must not change), which is why the converter works on a syntax tree.
/// </summary>
public interface ITextFix
{
	string Id { get; }

	string Title { get; }

	string Description { get; }

	bool EnabledByDefault => true;

	/// <summary>The part's text with the fix applied (the same string when nothing changed).</summary>
	string Apply(string code, TextFixContext context);
}

public sealed class TextFixContext(ScriptKind kind, ScriptSide side)
{
	public ScriptKind Kind { get; } = kind;

	public ScriptSide Side { get; } = side;

	public List<string> Notes { get; } = [];

	public void Note(string text)
	{
		if (!Notes.Contains(text)) Notes.Add(text);
	}
}

/// <summary>Splits GS2 text into code, comments and strings (a string ends at its quote or at the end of its line).</summary>
public static class CodeTokens
{
	public enum Kind
	{
		Code,
		Comment,
		String,
	}

	public static List<(Kind Kind, string Text)> Split(string source)
	{
		var output = new List<(Kind, string)>();
		var buffer = new StringBuilder();
		var i = 0;
		var n = source.Length;
		void Flush()
		{
			if (buffer.Length == 0) return;
			output.Add((Kind.Code, buffer.ToString()));
			buffer.Clear();
		}
		while (i < n)
		{
			var c = source[i];
			if (c == '/' && i + 1 < n && source[i + 1] == '/')
			{
				Flush();
				var j = source.IndexOf('\n', i);
				j = j < 0 ? n : j;
				output.Add((Kind.Comment, source[i..j]));
				i = j;
				continue;
			}
			if (c == '/' && i + 1 < n && source[i + 1] == '*')
			{
				Flush();
				var j = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
				j = j < 0 ? n : j + 2;
				output.Add((Kind.Comment, source[i..j]));
				i = j;
				continue;
			}
			if (c is '"' or '\'')
			{
				Flush();
				var j = i + 1;
				while (j < n && source[j] != c && source[j] != '\n') j += source[j] == '\\' ? 2 : 1;
				j = Math.Min(j + 1, n);
				output.Add((Kind.String, source[i..j]));
				i = j;
				continue;
			}
			buffer.Append(c);
			i++;
		}
		Flush();
		return output;
	}

	/// <summary>The text with a function applied to each piece of code (strings and comments as they are).</summary>
	public static string MapCode(string source, Func<string, string> map)
	{
		var sb = new StringBuilder(source.Length);
		foreach (var (kind, text) in Split(source)) sb.Append(kind == Kind.Code ? map(text) : text);
		return sb.ToString();
	}
}

/// <summary>A regular expression replaced in code only (uplift.json "textRules", or a plugin's).</summary>
public sealed class RegexTextFix : ITextFix
{
	private readonly Regex _pattern;
	private readonly string _replacement;
	private readonly IReadOnlySet<ScriptSide>? _sides;
	private readonly IReadOnlySet<ScriptKind>? _kinds;

	public RegexTextFix(string id, string title, string description, string pattern, string replacement, bool ignoreCase = false,
		IReadOnlySet<ScriptSide>? sides = null, IReadOnlySet<ScriptKind>? kinds = null, bool enabledByDefault = true)
	{
		Id = id;
		Title = title;
		Description = description;
		_pattern = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		_replacement = replacement;
		_sides = sides;
		_kinds = kinds;
		EnabledByDefault = enabledByDefault;
	}

	public string Id { get; }

	public string Title { get; }

	public string Description { get; }

	public bool EnabledByDefault { get; }

	public string Apply(string code, TextFixContext context)
	{
		if (_sides != null && !_sides.Contains(context.Side)) return code;
		if (_kinds != null && !_kinds.Contains(context.Kind)) return code;
		var changed = false;
		var result = CodeTokens.MapCode(code, piece =>
		{
			var replaced = _pattern.Replace(piece, _replacement);
			if (!ReferenceEquals(replaced, piece) && replaced != piece) changed = true;
			return replaced;
		});
		if (!changed) return code;
		context.Note(Title);
		return result;
	}
}

/// <summary>The text fixes Uplift has.</summary>
public static class BuiltInTextFixes
{
	public static IReadOnlyList<ITextFix> All { get; } =
	[
		new RegexTextFix("drawoverplayer-typo", "drawoveplayer( → drawoverplayer(",
			"Misspelt calls of drawoverplayer (drawoveplayer, drawoverrplayer) that did nothing are the command.",
			@"\b(?:drawoveplayer|drawoverrplayer)\s*\(", "drawoverplayer(", ignoreCase: true),
		new CallNpcSelfTextFix(),
		new GsOneCountsTextFix(),
	];
}

/// <summary>callnpc(-1, "event", ...) in GS2 text: this.trigger("event", ...); with ("Name") callnpc(-1, ...): findnpc("Name").trigger(...).</summary>
public sealed class CallNpcSelfTextFix : ITextFix
{
	private static readonly Regex Call = new(@"(with\s*\(\s*""([^""]+)""\s*\)\s*)?\bcallnpc\s*\(\s*-\s*1\s*,\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public string Id => "callnpc-self-text";

	public string Title => "callnpc(-1, ...) in GS2 code → this.trigger(...)";

	public string Description =>
		"The callnpc-self rule for parts that were GS2 already: callnpc(-1, \"event\", ...) is the current NPC, which engines that read -1 as a list position call nobody.";

	public string Apply(string code, TextFixContext context)
	{
		var tokens = CodeTokens.Split(code);
		var sb = new StringBuilder(code.Length);
		var changed = false;
		for (var i = 0; i < tokens.Count; i++)
		{
			var (kind, text) = tokens[i];
			if (kind == CodeTokens.Kind.Code && i + 1 < tokens.Count && tokens[i + 1] is { Kind: CodeTokens.Kind.String, Text: var next } && !next.Contains(',') && Call.Match(text) is { Success: true } m)
			{
				var replacement = m.Groups[1].Success ? $"findnpc(\"{m.Groups[2].Value}\").trigger(" : "this.trigger(";
				text = text[..m.Index] + replacement;
				changed = true;
			}
			sb.Append(text);
		}
		if (!changed) return code;
		context.Note(Title);
		return sb.ToString();
	}
}

/// <summary>playerscount and npcscount (GS1 names) in a server part: players.size() and npcs.size().</summary>
public sealed class GsOneCountsTextFix : ITextFix
{
	private static readonly Regex Assigned = new(@"(?<![.\w])(playerscount|npcscount)\s*(?:=(?!=)|\+=|-=|\+\+|--)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex Name = new(@"(?<![.\w$])(playerscount|npcscount)\b(?!\s*\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public string Id => "gs1-counts-text";

	public string Title => "playerscount / npcscount in GS2 server code → players.size() / npcs.size()";

	public string Description =>
		"GS1's playerscount and npcscount are not GS2 names (an engine without them reads 0). In server parts that never assign them they are written players.size() and npcs.size(), which every GS2 engine has.";

	public string Apply(string code, TextFixContext context)
	{
		if (context.Side != ScriptSide.Server) return code;
		var allCode = string.Concat(CodeTokens.Split(code).Where(t => t.Kind == CodeTokens.Kind.Code).Select(t => t.Text));
		var assigned = Assigned.Matches(allCode).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
		var changed = false;
		var result = CodeTokens.MapCode(code, piece => Name.Replace(piece, m =>
		{
			var name = m.Groups[1].Value.ToLowerInvariant();
			if (assigned.Contains(name)) return m.Value;
			changed = true;
			return name == "playerscount" ? "players.size()" : "npcs.size()";
		}));
		if (!changed) return code;
		context.Note(Title);
		return result;
	}
}
