// GS1 part → GS2 part. The part is read with the compiler's own GS1 front-end (the tree the compiler runs for the GS1 text),
// the tree is normalized (constant variable names to variables, strtofloat to float(), timeout = x to setTimer(x), no-op bare words
// kept as comments, then the enabled rules), and its main code is specialized per event: a GS1 script's main code runs once per
// event with that event's flag true, so the handler of event E is the main code with E's flag true and every other flag false,
// folded. Every output is proven: its printed text re-parses as GS2 to the same bytecode as the tree it was printed from, it has
// none of the GS1 signs (the compiler's own ScriptLanguage), and it compiles as GS2.
using System.Text.RegularExpressions;
using Alpaxis.Scripting.GS2Compiler;
using Alpaxis.Scripting.GSAst;
using Uplift.Rules;

namespace Uplift.Conversion;

/// <summary>A comment line the converter writes (never emitted).</summary>
public sealed record CommentStmt(string Text) : Stmt
{
	public override TResult Accept<TResult>(IAstVisitor<TResult> visitor) => throw new NotSupportedException();
}

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

/// <summary>How the converter works: the server's custom events and the rules.</summary>
public sealed class ConversionOptions
{
	/// <summary>Events the server's scripts send by name: a bare, never-assigned name of one tested outside functions is that event.</summary>
	public IReadOnlyCollection<string> CustomEvents { get; init; } = [];

	/// <summary>The enabled rules, in the order they run.</summary>
	public IReadOnlyList<IConversionRule> Rules { get; init; } = BuiltInRules.All.Where(r => r.EnabledByDefault).ToList();

	/// <summary>More GS1 event flags and their GS2 functions.</summary>
	public IReadOnlyDictionary<string, string> ExtraEvents { get; init; } = new Dictionary<string, string>();
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

	private readonly EventTable _events;
	private readonly IReadOnlyList<IConversionRule> _rules;

	public Gs1Converter(ConversionOptions? options = null)
	{
		options ??= new ConversionOptions();
		_rules = options.Rules;
		_events = new EventTable(options.CustomEvents, _rules.Any(r => r.Id == BuiltInRules.MisspeltEventsId), options.ExtraEvents);
	}

	public EventTable Events => _events;

	/// <summary>Converts one part (a server or client part, or a gani's script).</summary>
	public PartResult Convert(string code, ScriptKind kind, ScriptSide side)
	{
		var target = kind.CompileTarget();
		var client = side == ScriptSide.Client;
		try
		{
			var auto = Interface.CompileCode(code, target, "npc", withHeader: false, grammar: ScriptGrammar.Auto);
			if (auto.Success && auto.Grammar == ScriptGrammar.GS2 && !ScriptLanguage.HasLegacySyntax(code) && !ScriptLanguage.TestsGs1Event(code) && !ScriptLanguage.HasBareCommand(code))
				return new(PartStatus.AlreadyGs2, "", [], "");
			if (!auto.Success) return PartResult.Fail("does not compile as GS1 or GS2: " + FirstLine(auto.ErrMsg));
			// Read the way the compiler reads it: GS1, or GS2 (with GS1's main-code rules when the part looked like GS1 and GS1 could not read it).
			var asGs2 = auto.Grammar != ScriptGrammar.GS1;
			var gs1MainCode = asGs2 && ScriptLanguage.LooksLikeGs1(code);
			UpliftAccess.Lines = new(ReferenceEqualityComparer.Instance);
			UpliftAccess.Folds = new(ReferenceEqualityComparer.Instance);
			var (program, error) = asGs2 ? UpliftAccess.ParseGs2(code, gs1MainCode) : UpliftAccess.ParseGs1(code);
			var lines = UpliftAccess.Lines;
			var folds = UpliftAccess.Folds;
			UpliftAccess.Lines = null;
			UpliftAccess.Folds = null;
			if (program == null) return PartResult.Fail("parse: " + FirstLine(error));
			var comments = Comments.Collect(code, lines, asGs2);
			var converter = new PartConverter(comments, client, _events, _rules, kind, readAsGs1: !asGs2, timeoutIsEvent: !asGs2 || gs1MainCode);
			var gs2 = converter.Convert(program);
			if (converter.Zone) return new(PartStatus.Kept, "", [], "level flag word (sparringzone/nopkzone): left as written, the client reads it by its text");
			// Constant arithmetic is written as the author wrote it when GS2 folds it back to the same bytecode; else as the number.
			var text = converter.Print(gs2, folds);
			var (same, why) = Proof.Reparses(Strip(gs2), text);
			if (!same && folds.Count > 0)
			{
				converter.ResetComments();
				text = converter.Print(gs2, null);
				(same, why) = Proof.Reparses(Strip(gs2), text);
			}
			// Proof 1 (above): the text is exactly the tree.
			if (!same) return PartResult.Fail("printer: " + why);
			// Proof 2: GS2 in the compiler's eyes.
			var check = Interface.CompileCode(text, target, "npc", withHeader: false, grammar: ScriptGrammar.Auto);
			if (!check.Success || check.Grammar != ScriptGrammar.GS2) return PartResult.Fail("output not read as GS2: " + FirstLine(check.ErrMsg));
			if (ScriptLanguage.HasLegacySyntax(text) || ScriptLanguage.TestsGs1Event(text) || ScriptLanguage.HasBareCommand(text) || ScriptLanguage.Marker(text) == ScriptGrammar.GS1)
				return PartResult.Fail("output still has GS1 signs");
			return new(PartStatus.Converted, text, converter.Notes, "");
		}
		catch (UnconvertibleException u)
		{
			return PartResult.Fail(u.Message);
		}
		catch (Exception e)
		{
			return PartResult.Fail("exception " + e.GetType().Name + ": " + FirstLine(e.Message));
		}
		finally
		{
			UpliftAccess.Lines = null;
			UpliftAccess.Folds = null;
		}
	}

	private static string FirstLine(string? text) => (text ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n')[0];

	/// <summary>The tree without the converter's comment statements (for the emitter).</summary>
	internal static ProgramNode Strip(ProgramNode program) =>
		program with { Items = program.Items.Select(i => i is FunctionItem f ? new FunctionItem(f.Function with { Body = StripBody(f.Function.Body) }) : i).ToList() };

	private static List<Stmt> StripBody(List<Stmt> body) => new Rewriter(stmt: s => s is CommentStmt ? [] : null).Statements(body);

	internal static bool IsReserved(string name) => Reserved.Contains(name);

	public static void Walk(List<Stmt> body, Action<Expr> visit) => new Rewriter(expr: e =>
	{
		visit(e);
		return null;
	}).Statements(body);

	public static bool Uses(List<Stmt> main, List<FunctionNode> functions, Func<Expr, bool> test)
	{
		var found = false;
		Walk(main.Concat(functions.SelectMany(f => f.Body)).ToList(), e =>
		{
			if (test(e)) found = true;
		});
		return found;
	}
}

/// <summary>The printer's proof: a printed text re-parses as GS2 to the bytecode of the tree it was printed from.</summary>
public static class Proof
{
	/// <summary>The bytecode of a tree emitted as GS2, or null when the emitter refuses it.</summary>
	public static byte[]? Gs2Bytes(ProgramNode program)
	{
		try
		{
			return UpliftAccess.Emit(program, ScriptGrammar.GS2);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static (bool Same, string Error) Reparses(ProgramNode program, string text)
	{
		var reference = Gs2Bytes(program);
		if (reference == null) return (false, "reference tree does not emit");
		var (again, error) = UpliftAccess.ParseGs2(text);
		if (again == null) return (false, "printed text does not parse as GS2: " + error?.Split('\n')[0]);
		var bytes = Gs2Bytes(again);
		if (bytes == null) return (false, "printed text does not emit");
		return bytes.AsSpan().SequenceEqual(reference) ? (true, "") : (false, "bytecode differs");
	}

	/// <summary>A tree printed as GS2 as it is (no specialization).</summary>
	public static string Flat(ProgramNode program)
	{
		var printer = new Printer();
		foreach (var item in program.Items)
			if (item is FunctionItem f) printer.Function(f.Function);
			else if (item is StatementItem { Statement: BlockStmt block }) printer.Statements(block.Body);
			else if (item is StatementItem s) printer.Statement(s.Statement);
		return printer.ToString();
	}
}
