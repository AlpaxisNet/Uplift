// GS1 part → GS2 part. The part is read into Uplift's syntax tree (as GS1, or as GS2 with GS1 main code), the tree is normalized
// (comparing = to ==, timeout = x to setTimer(x), no-op bare words kept as comments, then the enabled rules), and its main code is
// specialized per event: a GS1 script's main code runs once per event with that event's flag true, so the handler of event E is
// the main code with E's flag true and every other flag false, folded. Every output is proven: its printed text parses as GS2 back
// to the same tree it was printed from, and it is strict GS2 (no GS1 signs).
using System.Text.RegularExpressions;
using Uplift.Formats;
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

	/// <summary>
	///     Bare names the server's scripts assign (x = 1;): one variable every script shares, so a bare read of one is that variable,
	///     not the player's flag (<see cref="Gs1Converter.AssignedNames" />).
	/// </summary>
	public IReadOnlyCollection<string> SharedVariables { get; init; } = [];
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
		"sin", "cos", "arctan", "exp", "log", "abs", "random", "char", "int", "float", "min", "max", "getangle", "getdir", "vecx", "vecy", "arraylen",
		"format", "makevar", "setarray", "sleep", "waitfor",
	};

	// GS1 commands that are the level's members in GS2's server code.
	private static readonly HashSet<string> LevelCommands = new(StringComparer.OrdinalIgnoreCase) { "putbomb", "puthorse", "noplayerkilling" };

	private readonly EventTable _events;
	private readonly IReadOnlyList<IConversionRule> _rules;
	private readonly Gs1Catalog _catalog;

	public Gs1Converter(ConversionOptions? options = null)
	{
		options ??= new ConversionOptions();
		_rules = options.Rules;
		_catalog = options.Catalog;
		_events = new EventTable(options.CustomEvents, _rules.Any(r => r.Id == BuiltInRules.MisspeltEventsId), options.ExtraEvents);
		_shared = new HashSet<string>(options.SharedVariables, StringComparer.OrdinalIgnoreCase);
	}

	private readonly HashSet<string> _shared;

	/// <summary>The bare names a part assigns (x = 1;, x++;, x[0] = 1;), as GS1 or GS2 reads it.</summary>
	public static IEnumerable<string> AssignedNames(string code, Gs1Catalog? catalog = null)
	{
		var read = Language.Read(code, catalog);
		if (read.Script == null) return [];
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var body = read.Script.Items.SelectMany(i => i switch
		{
			FunctionDecl f => f.Body,
			StatementItem s => [s.Statement],
			_ => [],
		}).ToList();
		static bool Assigns(string op) => op.EndsWith('=') && op is not ("==" or "!=" or "<=" or ">=");
		Walk(body, e =>
		{
			var name = e switch
			{
				BinaryExpr { Left: NameExpr { Name: var n }, Op: var op } when Assigns(op) => n,
				BinaryExpr { Left: IndexExpr { Target: NameExpr { Name: var n } }, Op: var op } when Assigns(op) => n,
				UnaryExpr { Op: "++" or "--", Operand: NameExpr { Name: var n } } => n,
				_ => null,
			};
			if (name != null) names.Add(name);
		});
		return names;
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
			var notes = new List<string>();
			script = Lowerings(script, read.Parse, side, notes);
			// GS1 main code in GS2 syntax: an old command alone is the command.
			if (gs1MainCode) script = OldCommandsAreCalls(script, notes);
			var comments = Comments.Collect(read.Parse.Comments, read.Parse.Lines);
			var gs1Functions = !gs1MainCode || read.Parse.Gs2Only.Found.Count == 0 && !Language.HasEventFunction(script);
			var converter = new PartConverter(comments, side == ScriptSide.Client, _events, _rules, kind, readAsGs1: !gs1MainCode, gs1Functions, _shared);
			var gs2 = converter.Convert(script);
			if (converter.Zone) return new(PartStatus.Kept, "", [], "level flag word (sparringzone/nopkzone): left as written, the client reads it by its text");
			CheckShape(gs2);
			foreach (var note in notes.Concat(read.Parse.Notes))
				if (!converter.Notes.Contains(note)) converter.Notes.Add(note);
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

	private bool CommandTypos => _rules.Any(r => r.Id == CommandTyposRule.RuleId);

	private static Script Apply(Script script, Rewriter rewriter)
	{
		var items = script.Items.Select(i => i switch
		{
			StatementItem s when rewriter.Statements([s.Statement]) is var r && (r.Count != 1 || !ReferenceEquals(r[0], s.Statement)) => new StatementItem(r.Count == 1 ? r[0] : new BlockStmt(r)),
			FunctionDecl f when rewriter.Statements(f.Body) is var body && !ReferenceEquals(body, f.Body) => f with { Body = body },
			_ => i,
		}).ToList();
		return new Script(items);
	}

	/// <summary>
	///     What GS1's commands and functions mean on the side the part runs on, and its commands written against their number (the
	///     original compiler dropped them: with the command-typos rule they run, otherwise they are kept as comments).
	/// </summary>
	private Script Lowerings(Script script, ParseResult parse, ScriptSide side, List<string> notes)
	{
		if (parse.Glued.Count > 0)
			script = Apply(script, new Rewriter(stmt: s => parse.Glued.TryGetValue(s, out var text) ? [Joined(s, text, notes)] : null));
		if (parse.Lowered.Count == 0) return script;
		var server = side == ScriptSide.Server;
		return Apply(script, new Rewriter(
			expr: e => e is CallExpr c && parse.Lowered.Contains(c) ? SideLowering(c, server, notes) : null,
			stmt: s =>
			{
				if (s is not ExprStmt { Expression: CallExpr { Name: var name } call } || !parse.Lowered.Contains(call) || !name.Equals("setbackpal", StringComparison.OrdinalIgnoreCase)) return null;
				notes.Add("setbackpal (no GS2 engine has it) kept as a comment");
				return [new CommentStmt("// setbackpal " + new Printer().Expr(call.Args.FirstOrDefault() ?? new StringExpr("")) + "; (GS2 has no setbackpal)")];
			}));
	}

	private Stmt Joined(Stmt command, string text, List<string> notes)
	{
		if (CommandTypos)
		{
			notes.Add($"bug fix: '{text}' (a command joined to its number: the original GS1 compiler dropped it) now runs");
			return command;
		}
		notes.Add($"'{text}' (a command joined to its number: the original GS1 compiler dropped it) kept as a comment");
		return new CommentStmt($"// {text} (the original GS1 compiler dropped this joined command)");
	}

	private Expr? SideLowering(CallExpr call, bool server, List<string> notes)
	{
		if (server && LevelCommands.Contains(call.Name)) return new MethodCallExpr(new NameExpr("level"), call.Name.ToLowerInvariant(), call.Args);
		// hasweapon(w): on the server, the player's weapon of that name.
		if (server && call.Name == "findweapon") return new MethodCallExpr(new NameExpr("player"), "findweapon", call.Args);
		if (call is { Name: "putnpc2", Args: [var x, var y, StringExpr { Value: var embedded }] }) return EmbeddedScript(x, y, embedded, notes);
		return null;
	}

	// putnpc2's script is GS1 too: converted (each part), or marked //#GS1 where it does not convert.
	private Expr? EmbeddedScript(Expr x, Expr y, string script, List<string> notes)
	{
		string? Part(string code, ScriptSide side)
		{
			if (code.Trim().Length == 0) return null;
			var result = Convert(code, ScriptKind.LevelNpc, side);
			return result.Status switch
			{
				PartStatus.Converted => result.Text,
				PartStatus.AlreadyGs2 => null,
				_ => "//#GS1\n" + code,
			};
		}
		var server = Part(ScriptParts.ServerPart(script), ScriptSide.Server);
		var client = Part(ScriptParts.ClientPart(script), ScriptSide.Client);
		if (server == null && client == null) return null;
		var joined = ScriptParts.Join(script, server, client);
		notes.Add(joined.Contains("//#GS1", StringComparison.Ordinal)
			? "putnpc2's GS1 script: marked //#GS1 where it does not convert"
			: "putnpc2's GS1 script converted to GS2 too");
		return new CallExpr("putnpc2", [x, y, new StringExpr(joined)]);
	}

	// GS1 main code in GS2 syntax: an old command alone (hide;, sleep1;) is the command, in the main code and in the functions; GS1's
	// player names (playerhearts) are the player's.
	private Script OldCommandsAreCalls(Script script, List<string> notes) =>
		Apply(script, new Rewriter(
			expr: e => e is NameExpr { Name: var name } && _catalog.LowerName(name, counts: false) is { } player ? player : null,
			stmt: s => s is ExprStmt { Expression: NameExpr { Name: var word } } ? Command(s, word, notes) is { } command ? [command] : null : null));

	// A bare word that is an old command, or one written against its number (sleep1): the call.
	private Stmt? Command(Stmt s, string word, List<string> notes)
	{
		if (_catalog.TryGetCommand(word, out _)) return new ExprStmt(new CallExpr(word, []));
		for (var length = word.Length - 1; length >= 2; length--)
			if (word[length..].All(char.IsAsciiDigit) && _catalog.TryGetCommand(word[..length], out var c) && c.Kinds.Length > 0)
				return Joined(new ExprStmt(new CallExpr(word[..length], [new NumberExpr(word[length..])])), word, notes);
		return null;
	}

	/// <summary>What GS2 engines read differently: an assignment inside a condition (GS2 compares there), a comma in a for loop.</summary>
	private static void CheckShape(Script script)
	{
		static bool Assigns(Expr e)
		{
			var found = false;
			Walk([new ExprStmt(e)], x =>
			{
				if (x is BinaryExpr { Op: "=" }) found = true;
			});
			return found;
		}
		var rewriter = new Rewriter(stmt: s =>
		{
			var condition = s switch { IfStmt i => i.Condition, WhileStmt w => w.Condition, DoWhileStmt d => d.Condition, ForStmt f => f.Condition, _ => null };
			if (condition != null && Assigns(condition)) throw new UnconvertibleException("an assignment inside a condition (GS2 would compare there)");
			if (s is ForStmt { Init: SequenceExpr } or ForStmt { Step: SequenceExpr }) throw new UnconvertibleException("a comma in a for loop's first or last part (not every GS2 engine reads it)");
			return null;
		});
		foreach (var item in script.Items)
			if (item is FunctionDecl f) rewriter.Statements(f.Body);
			else if (item is StatementItem st) rewriter.Statements([st.Statement]);
	}

	/// <summary>The tree without the converter's comment statements (what printing then parsing gives back).</summary>
	internal static Script Strip(Script script) =>
		script with { Items = script.Items.Select(i => i is FunctionDecl f ? f with { Body = StripBody(f.Body) } : i).ToList() };

	private static List<Stmt> StripBody(List<Stmt> body) => new Rewriter(stmt: s => s is CommentStmt ? [] : null).Statements(body);

	internal static bool IsReserved(string name) => Reserved.Contains(name);

	/// <summary>The level's players: players, or level.players as a level NPC's server part has it in GS2.</summary>
	public static bool IsPlayersList(Expr e) => e is NameExpr { Name: "players" } or MemberExpr { Target: NameExpr { Name: "level" }, Name: "players" };

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
