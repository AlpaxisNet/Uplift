// GS1 part → GS2 part. The part is read into Uplift's syntax tree (as GS1, or as GS2 with GS1 main code), the tree is normalized
// (comparing = to ==, timeout = x to setTimer(x), no-op bare words kept as comments, then the enabled rules), and its main code is
// specialized per event: a GS1 script's main code runs once per event with that event's flag true, so the handler of event E is
// the main code with E's flag true and every other flag false, folded. Every output is proven: its printed text parses as GS2 back
// to the same tree it was printed from, and it is strict GS2 (no GS1 signs).
using System.Text.RegularExpressions;
using Uplift.Rules;
using Uplift.Syntax;

namespace Uplift.Conversion;

/// <summary>A part the converter leaves as it is, and why.</summary>
public sealed class UnconvertibleException(string reason) : Exception(reason);

public enum PartStatus
{
	/// <summary>The part is GS2 already (left as it is).</summary>
	AlreadyGs2,

	/// <summary>The part was GS1 and is now GS2.</summary>
	Converted,

	/// <summary>Left as written on purpose (a level-flag word the client reads by its text).</summary>
	Kept,

	/// <summary>Not converted: the reason says why (the part stays as it is).</summary>
	Failed,
}

/// <param name="Status">What happened.</param>
/// <param name="Text">The GS2 text (Converted only).</param>
/// <param name="Notes">What the conversion changed in meaning or form, for a person to check.</param>
/// <param name="Reason">Why the part was kept or failed.</param>
public sealed record PartResult(PartStatus Status, string Text, IReadOnlyList<string> Notes, string Reason)
{
	public static PartResult Fail(string reason) => new(PartStatus.Failed, "", [], reason);
}

/// <summary>How the converter works: the server's custom events, the rules, the GS1 commands.</summary>
public sealed class ConversionOptions
{
	/// <summary>Events the server's scripts send by name: a bare, never-assigned name of one tested outside functions is that event.</summary>
	public IReadOnlyCollection<string> CustomEvents { get; init; } = [];

	/// <summary>The enabled rules, in the order they run.</summary>
	public IReadOnlyList<IConversionRule> Rules { get; init; } = BuiltInRules.All.Where(r => r.EnabledByDefault).ToList();

	/// <summary>More GS1 event flags and their GS2 functions.</summary>
	public IReadOnlyDictionary<string, string> ExtraEvents { get; init; } = new Dictionary<string, string>();

	/// <summary>The GS1 commands and functions (the standard ones, and the server's own).</summary>
	public Gs1Catalog Catalog { get; init; } = Gs1Catalog.Default;
}

/// <summary>Converts GS1 parts to GS2. Thread-safe; each call needs a deep stack (see <see cref="Jobs.Workers" />).</summary>
public sealed class Gs1Converter
{
	internal static readonly Regex Identifier = new(@"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.Compiled);

	private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
	{
		"const", "enum", "public", "function", "if", "elseif", "else", "for", "while", "do", "switch", "case", "default", "with", "new", "return",
		"break", "continue", "goto", "int", "float", "in", "true", "false", "null", "NULL", "xor", "NL", "SPC", "TAB", "_",
	};

	// Functions GS2 runs as built-ins: a script's own function of one of these names would never be called.
	private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
	{
		"sin", "cos", "arctan", "exp", "log", "abs", "random", "char", "int", "float", "min", "max", "pow", "getangle", "getdir", "vecx", "vecy",
	};

	private readonly EventTable _events;
	private readonly IReadOnlyList<IConversionRule> _rules;
	private readonly Gs1Catalog _catalog;

	public Gs1Converter(ConversionOptions? options = null)
	{
		options ??= new ConversionOptions();
		_rules = options.Rules;
		_catalog = options.Catalog;
		_events = new EventTable(options.CustomEvents, _rules.Any(r => r.Id == BuiltInRules.MisspeltEventsId), options.ExtraEvents);
	}

	public EventTable Events => _events;

	/// <summary>Converts one part (a server or client part, or a gani's script).</summary>
	public PartResult Convert(string code, ScriptKind kind, ScriptSide side)
	{
		try
		{
			var read = Language.Read(code, _catalog);
			switch (read.Reading)
			{
				case Reading.Gs2:
					return new(PartStatus.AlreadyGs2, "", [], "");
				case Reading.Broken:
					return PartResult.Fail("does not parse as GS1 or GS2: " + read.Error);
			}
			var script = read.Script!;
			var gs1MainCode = read.Reading == Reading.Gs2WithGs1MainCode;
			// GS1 main code in GS2 syntax: an old command alone is the command.
			if (gs1MainCode) script = OldCommandsAreCalls(script);
			var comments = Comments.Collect(read.Parse.Comments, read.Parse.Lines);
			var converter = new PartConverter(comments, side == ScriptSide.Client, _events, _rules, kind, readAsGs1: !gs1MainCode, timeoutIsEvent: true);
			var gs2 = converter.Convert(script);
			if (converter.Zone) return new(PartStatus.Kept, "", [], "level flag word (sparringzone/nopkzone): left as written, the client reads it by its text");
			var text = converter.Print(gs2);
			// Proof 1: the text is exactly the tree.
			var again = Parser.Parse(text, Dialect.Gs2, _catalog);
			if (!again.Success) return PartResult.Fail("printer: the printed text does not parse as GS2: line " + again.ErrorLine + ": " + again.Error);
			if (!SyntaxEquality.Equal(Strip(gs2), again.Script)) return PartResult.Fail("printer: the printed text does not read back as the same tree");
			// Proof 2: strict GS2.
			if (Language.Gs1SignsOf(again, _catalog) is { Count: > 0 } signs) return PartResult.Fail("output still has GS1 signs: " + signs[0]);
			return new(PartStatus.Converted, text, converter.Notes, "");
		}
		catch (UnconvertibleException u)
		{
			return PartResult.Fail(u.Message);
		}
		catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or InsufficientExecutionStackException)
		{
			return PartResult.Fail("exception " + e.GetType().Name + ": " + e.Message.Split('\n')[0]);
		}
	}

	// GS1 main code in GS2 syntax: an old command alone (hide;, sleep1;) is the command, in the main code and in the functions; GS1's
	// player names (playerhearts) are the player's.
	private Script OldCommandsAreCalls(Script script)
	{
		var rewriter = new Rewriter(
			expr: e => e is NameExpr { Name: var name } && _catalog.LowerName(name, counts: false) is { } player ? player : null,
			stmt: s => s is ExprStmt { Expression: NameExpr { Name: var word } } ? Command(word) is { } call ? [new ExprStmt(call)] : null : null);
		var items = script.Items.Select(i => i switch
		{
			StatementItem s when rewriter.Statements([s.Statement]) is var r && (r.Count != 1 || !ReferenceEquals(r[0], s.Statement)) => new StatementItem(r.Count == 1 ? r[0] : new BlockStmt(r)),
			FunctionDecl f when rewriter.Statements(f.Body) is var body && !ReferenceEquals(body, f.Body) => f with { Body = body },
			_ => i,
		}).ToList();
		return new Script(items);
	}

	// A bare word that is an old command, or one written against its number (sleep1): the call.
	private Expr? Command(string word)
	{
		if (_catalog.TryGetCommand(word, out _)) return new CallExpr(word, []);
		for (var length = word.Length - 1; length >= 2; length--)
			if (char.IsAsciiDigit(word[length]) && word[length..].All(c => char.IsAsciiDigit(c) || c == '.') && _catalog.TryGetCommand(word[..length], out var c) && c.Kinds.Length > 0)
				return new CallExpr(word[..length], [new NumberExpr(word[length..])]);
		return null;
	}

	/// <summary>The tree without the converter's comment statements (what printing then parsing gives back).</summary>
	internal static Script Strip(Script script) =>
		script with { Items = script.Items.Select(i => i is FunctionDecl f ? f with { Body = StripBody(f.Body) } : i).ToList() };

	private static List<Stmt> StripBody(List<Stmt> body) => new Rewriter(stmt: s => s is CommentStmt ? [] : null).Statements(body);

	internal static bool IsReserved(string name) => Reserved.Contains(name);

	internal static bool IsBuiltIn(string name) => BuiltIns.Contains(name);

	/// <summary>The names a condition tests (through &amp;&amp;, || and !).</summary>
	public static IEnumerable<string> Atoms(Expr condition)
	{
		switch (condition)
		{
			case NameExpr { Name: var n }:
				yield return n;
				break;
			case BinaryExpr { Op: "&&" or "||" } b:
				foreach (var a in Atoms(b.Left)) yield return a;
				foreach (var a in Atoms(b.Right)) yield return a;
				break;
			case UnaryExpr { Op: "!" } u:
				foreach (var a in Atoms(u.Operand)) yield return a;
				break;
		}
	}

	public static void Walk(List<Stmt> body, Action<Expr> visit) => new Rewriter(expr: e =>
	{
		visit(e);
		return null;
	}).Statements(body);

	public static bool Uses(List<Stmt> main, List<FunctionDecl> functions, Func<Expr, bool> test)
	{
		var found = false;
		Walk(main.Concat(functions.SelectMany(f => f.Body)).ToList(), e =>
		{
			if (test(e)) found = true;
		});
		return found;
	}
}
