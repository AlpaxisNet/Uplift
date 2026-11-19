using System.Diagnostics.CodeAnalysis;
using Uplift.Conversion;
using Uplift.Syntax;

namespace Uplift.Rules;

/// <summary>
///     A rewrite of a GS1 part's syntax tree, run while the converter normalizes the tree (before the main code is split into event
///     functions). The converter's own rewrites are what GS1 means; a rule is a choice: a bug fix, or a rewrite for an engine that
///     reads something differently. Every rule can be switched off; plugins add their own (UpliftRegistry.AddRule).
/// </summary>
/// <remarks>
///     The tree is rewritten children first: a hook sees a node whose children are already rewritten. Return null to leave the node.
///     The printed result is still proven against the rewritten tree, so a rule cannot make the printer lie; what the rule means is the
///     rule's own responsibility.
/// </remarks>
public interface IConversionRule
{
	/// <summary>A short, stable name (kebab-case), used in uplift.json and on the command line.</summary>
	string Id { get; }

	string Title { get; }

	string Description { get; }

	bool EnabledByDefault => true;

	Expr? RewriteExpression(Expr expression, RuleContext context) => null;

	IEnumerable<Stmt>? RewriteStatement(Stmt statement, RuleContext context) => null;
}

/// <summary>What a rule knows about the part it rewrites.</summary>
public sealed class RuleContext
{
	private readonly List<string> _notes;

	internal RuleContext(ScriptKind kind, ScriptSide side, bool readAsGs1, IReadOnlySet<string> functionNames, List<string> notes)
	{
		Kind = kind;
		Side = side;
		ReadAsGs1 = readAsGs1;
		FunctionNames = functionNames;
		_notes = notes;
	}

	public ScriptKind Kind { get; }

	public ScriptSide Side { get; }

	/// <summary>The part was read as GS1 (not GS2 with GS1-looking code).</summary>
	public bool ReadAsGs1 { get; }

	/// <summary>Whether the code being rewritten is GS1's: a part read as GS1, a mixed part's main code, or its functions when they are GS1 too.</summary>
	public bool Gs1Code { get; internal set; }

	/// <summary>A level NPC's server GS1 code: the NPC-Server gave it GS1's meanings of players, playerscount, setani ....</summary>
	public bool Gs1LevelServer => Kind == ScriptKind.LevelNpc && Side == ScriptSide.Server && Gs1Code;

	/// <summary>The functions the script defines itself (case-insensitive).</summary>
	public IReadOnlySet<string> FunctionNames { get; }

	/// <summary>Adds a note to the part's report (once).</summary>
	public void Note(string text)
	{
		if (!_notes.Contains(text)) _notes.Add(text);
	}

	/// <summary>Stops the conversion of the part: it is left as it is, with the reason in the report.</summary>
	[DoesNotReturn]
	public void Refuse(string reason) => throw new UnconvertibleException(reason);
}

/// <summary>A rule that is a switch the converter reads itself (no tree hooks).</summary>
public sealed class SwitchRule(string id, string title, string description, bool enabledByDefault = true) : IConversionRule
{
	public string Id => id;

	public string Title => title;

	public string Description => description;

	public bool EnabledByDefault => enabledByDefault;
}

/// <summary>The rules Uplift has.</summary>
public static class BuiltInRules
{
	public const string MisspeltEventsId = "misspelt-events";

	public const string Gs1FlagsId = "gs1-flags";

	public static IReadOnlyList<IConversionRule> All { get; } =
	[
		new CallNpcSelfRule(),
		new Gs1LevelNamesRule(),
		new SelfMessageCodesRule(),
		new CommandTyposRule(),
		new SwitchRule(Gs1FlagsId, "GS1 flags are the player's",
			"In GS1 a bare name that the script never assigns and that GS1 does not define (if (gotsword), quest.step) is the player's flag of that name, the same variable set gotsword; writes. GS2 reads a bare name as something else: written player.gotsword and player.quest.step, on the server and the client. Names any script on the server assigns, database NPCs, objects scripts make or call, and built-in names are left alone. In a part read as GS1 every such name is a flag; in a mixed part's GS1 code only the names the server uses as flags (set x;, player.x)."),
		new SwitchRule(MisspeltEventsId, "Misspelt events are events",
			"playerenterslevel, playereneterslevel, playereters, playertouchesme, playertouchesother, playerhurted, wasshooted and event names in another case (PlayerEnters) were never true in GS1, so their code never ran. Read as the events they meant (playerenters, playertouchsme, playerhurt, wasshot ...): the code runs as its author meant."),
	];
}

/// <summary>callnpc(-1, "event", ...) is this.trigger("event", ...).</summary>
public sealed class CallNpcSelfRule : IConversionRule
{
	public string Id => "callnpc-self";

	public string Title => "callnpc(-1, ...) → this.trigger(...)";

	public string Description =>
		"In GS1, callnpc with the index -1 calls the current NPC (the NPC itself, or a with block's NPC). GS2 engines that take the index as a position in the level's NPC list call nobody. Written as this.trigger, with GS1's \"event,a,b\" text split into arguments.";

	public Expr? RewriteExpression(Expr expression, RuleContext context)
	{
		if (expression is not CallExpr { Name: var name, Args: [var index, .. var rest] } || !name.Equals("callnpc", StringComparison.OrdinalIgnoreCase)) return null;
		if (index is not (NumberExpr { Text: "-1" } or UnaryExpr { Op: "-", Operand: NumberExpr { Text: "1" } }) || rest.Count == 0) return null;
		if (TriggerArguments(rest) is not { } arguments) return null;
		context.Note("callnpc(-1, ...) (the current NPC) written this.trigger(...)");
		return new MethodCallExpr(new NameExpr("this"), "trigger", arguments);
	}

	/// <summary>
	///     The event and parameters: given apart (GS2), or as GS1 gave them, one text "event,a,b" that the receiver reads as a list. The
	///     text is split at the commas of its literal pieces; null when the event's name is not a literal word.
	/// </summary>
	internal static List<Expr>? TriggerArguments(List<Expr> rest)
	{
		if (rest.Count > 1) return rest;
		var pieces = new List<Expr>();
		void Flatten(Expr e)
		{
			if (e is BinaryExpr { Op: "@" } b)
			{
				Flatten(b.Left);
				Flatten(b.Right);
			}
			else pieces.Add(e);
		}
		Flatten(rest[0]);
		if (pieces.Count == 1 && pieces[0] is not StringExpr) return null;
		var args = new List<Expr>();
		Expr? current = null;
		void Append(Expr e) => current = current == null ? e : new BinaryExpr(current, "@", e);
		foreach (var piece in pieces)
		{
			if (piece is StringExpr { Value: var text })
			{
				var parts = text.Split(',');
				for (var i = 0; i < parts.Length; i++)
				{
					if (i > 0)
					{
						args.Add(current ?? new StringExpr(""));
						current = null;
					}
					if (parts[i].Length > 0) Append(new StringExpr(parts[i]));
				}
			}
			else Append(piece);
		}
		args.Add(current ?? new StringExpr(""));
		return args[0] is StringExpr { Value.Length: > 0 } ? args : null;
	}
}

/// <summary>The GS1 names of a level NPC's server part written in GS2.</summary>
public sealed class Gs1LevelNamesRule : IConversionRule
{
	public string Id => "gs1-level-names";

	public string Title => "GS1 level-NPC names → GS2 (players, npcs, playerscount, npcscount, setani, setbow)";

	public string Description =>
		"A level NPC's server code read as GS1 gets GS1's meanings of players, npcs, playerscount, npcscount, setani and setbow, which GS2 code does not get (on a GS2 server, a bare players is empty): written level.players, level.npcs, level.players.size(), level.npcs.size(), player.setani(...) and player.bowimg = ... (unless the script defines a function of that name).";

	public Expr? RewriteExpression(Expr expression, RuleContext context)
	{
		if (!context.Gs1LevelServer) return null;
		switch (expression)
		{
			case NameExpr { Name: "players" or "npcs" } list:
				return new MemberExpr(new NameExpr("level"), list.Name);
			case NameExpr { Name: var n } when n.Equals("playerscount", StringComparison.OrdinalIgnoreCase):
				return new MethodCallExpr(new MemberExpr(new NameExpr("level"), "players"), "size", []);
			case NameExpr { Name: var n } when n.Equals("npcscount", StringComparison.OrdinalIgnoreCase):
				return new MethodCallExpr(new MemberExpr(new NameExpr("level"), "npcs"), "size", []);
			case CallExpr { Name: var n } call when n.Equals("setani", StringComparison.OrdinalIgnoreCase) && !context.FunctionNames.Contains("setani"):
				return new MethodCallExpr(new NameExpr("player"), "setani", call.Args);
			default:
				return null;
		}
	}

	public IEnumerable<Stmt>? RewriteStatement(Stmt statement, RuleContext context)
	{
		if (!context.Gs1LevelServer) return null;
		if (statement is ExprStmt { Expression: CallExpr { Name: var n, Args: [var bow] } } && n.Equals("setbow", StringComparison.OrdinalIgnoreCase) && !context.FunctionNames.Contains("setbow"))
			return [new ExprStmt(new BinaryExpr(new MemberExpr(new NameExpr("player"), "bowimg"), "=", bow))];
		return null;
	}
}

/// <summary>#3(-1), #C0(-1) ...: the NPC itself.</summary>
public sealed class SelfMessageCodesRule : IConversionRule
{
	public string Id => "self-message-codes";

	public string Title => "Message codes with index -1 → this";

	public string Description =>
		"In GS1, #3(-1), #C0(-1) and the other message codes with the index -1 are the NPC's own character. A reader that makes them players[-1] (nobody) loses that: written as this.";

	public Expr? RewriteExpression(Expr expression, RuleContext context)
	{
		if (expression is not IndexExpr { Target: var list, Indices: [NumberExpr { Text: "-1" } or UnaryExpr { Op: "-", Operand: NumberExpr { Text: "1" } }] } || !Gs1Converter.IsPlayersList(list)) return null;
		context.Note("players[-1] (the NPC itself in GS1) written as this");
		return new NameExpr("this");
	}
}

/// <summary>Typos of commands, which GS1 read as a bare word that did nothing.</summary>
public sealed class CommandTyposRule : IConversionRule
{
	private static readonly Dictionary<string, string> Typos = new(StringComparer.OrdinalIgnoreCase)
	{
		["drawundeprlayer"] = "drawunderplayer", ["drawoveplayer"] = "drawoverplayer", ["drawoverrplayer"] = "drawoverplayer",
		["donnotblock"] = "dontblock", ["dontblok"] = "dontblock", ["drawunderplayr"] = "drawunderplayer", ["drawaslights"] = "drawaslight",
	};

	public const string RuleId = "command-typos";

	public string Id => RuleId;

	public string Title => "Misspelt or joined commands are the commands";

	public string Description =>
		"drawoveplayer, dontblok, drawaslights ...: GS1 read a misspelt command as a bare word that did nothing, and the original compiler dropped a command written against its number (sleep1, freezeplayer.05). Written as the command its author meant (a bug fix: the command now runs). Off: misspelt commands and joined ones are kept as comments.";

	public IEnumerable<Stmt>? RewriteStatement(Stmt statement, RuleContext context)
	{
		if (statement is not ExprStmt { Expression: NameExpr { Name: var word } } || !Typos.TryGetValue(word, out var command)) return null;
		context.Note($"bug fix: '{word}' (a misspelt command, did nothing) is now {command}()");
		return [new ExprStmt(new CallExpr(command, []))];
	}
}
