using Uplift.Rules;
using Uplift.Syntax;
using static Uplift.Conversion.Gs1Converter;

namespace Uplift.Conversion;

/// <summary>The conversion of one part's tree (see <see cref="Gs1Converter" />).</summary>
// gs1Functions: whether the part's functions are GS1 code too (read as GS1, or a mixed part's with nothing of GS2's in them).
internal sealed class PartConverter(Comments comments, bool client, EventTable events, IReadOnlyList<IConversionRule> rules, ScriptKind kind, bool readAsGs1, bool gs1Functions,
	IReadOnlySet<string> shared)
{
	// GS1's own names: a bare name that is none of these, and that the script never assigns, is the player's flag of that name.
	private static readonly HashSet<string> Gs1Names = new(StringComparer.OrdinalIgnoreCase)
	{
		// objects and lists
		"this", "thiso", "temp", "local", "global", "player", "playero", "client", "clientr", "server", "serverr", "level", "params", "tokens", "players",
		"allplayers", "npcs",
		"weapons", "weapon", "compus", "bombs", "arrows", "items", "explos", "horses", "signs", "links", "baddies",
		// values
		"x", "y", "z", "dir", "id", "timevar", "timevar2", "nwtime", "levelorgx", "levelorgy", "board", "tiles", "pi", "mousex", "mousey", "mousescreenx",
		"mousescreeny", "mousebuttons", "screenwidth", "screenheight", "isweapon", "isleader", "isonmap", "issparringzone", "isnopkzone", "nopkzone",
		"sparringzone", "compsdead", "carrying", "canspin", "firedonhorse", "keypressed", "lighteffectsenabled", "weaponsenabled", "timeout", "istimeout",
		"focusx", "focusy",
		// the NPC's own attributes, as message codes and commands write them
		"chat", "nick", "account", "guild", "ani", "swordimg", "shieldimg", "headimg", "horseimg", "carrynpc", "bowimg", "bodyimg", "colors", "attr", "image",
		"name", "downloadfile", "ap", "hearts", "fullhearts", "rupees", "darts", "glovepower", "swordpower", "shieldpower", "sprite", "mp", "freezetime",
		"hurtpower", "width", "height", "visible", "blockagain", "dontblock",
	};

	// The flags GS1 reads bare, written as the player's (for the part's note).
	private readonly SortedSet<string> _playerFlags = new(StringComparer.OrdinalIgnoreCase);

	// Names the script assigns (bare: one variable of the server's, which every script shares) or makes an array of, or the objects it
	// makes (new GuiWindowCtrl("Name")).
	private readonly HashSet<string> _universe = new(StringComparer.OrdinalIgnoreCase);

	// Names inside with (...) { } and new T() { } blocks: the target's members, never the player's flags.
	private readonly HashSet<NameExpr> _scoped = new(ReferenceEqualityComparer.Instance);

	private readonly bool _gs1FlagsRule = rules.Any(r => r.Id == BuiltInRules.Gs1FlagsId);

	// Whether the code being normalized is GS1's (the main code, or GS1 functions).
	private bool _gs1Code;

	private bool Gs1Flags => _gs1FlagsRule && _gs1Code;

	// Events without parameters: one handler may call another's when their bodies are the same.
	private static readonly HashSet<string> ParameterlessEvents = new(StringComparer.OrdinalIgnoreCase)
	{
		"onCreated", "onPlayerEnters", "onPlayerLeaves", "onPlayerTouchsMe", "onPlayerChats", "onTimeout", "onPlayerEndsReading", "onPlayerDies",
		"onPlayerHurt", "onInitialized",
	};

	// Commands whose effect is the same however often they run with the same constant arguments (NPC set-up), and the commands
	// that undo them: unconditional set-up code goes to onCreated only when the script never calls a command of its group elsewhere.
	private static readonly Dictionary<string, string> SetupGroups = new(StringComparer.OrdinalIgnoreCase)
	{
		["setimg"] = "img", ["setimgpart"] = "img", ["setgif"] = "img", ["setgifpart"] = "img", ["showcharacter"] = "img",
		["setshape"] = "shape", ["setshape2"] = "shape",
		["dontblock"] = "block", ["blockagain"] = "block", ["dontblocklocal"] = "block", ["blockagainlocal"] = "block",
		["drawunderplayer"] = "draw", ["drawoverplayer"] = "draw", ["drawaslight"] = "light",
		["show"] = "vis", ["hide"] = "vis", ["showlocal"] = "vis", ["hidelocal"] = "vis",
		["setcoloreffect"] = "color", ["setzoomeffect"] = "zoom", ["seteffect"] = "effect", ["seteffectmode"] = "effectmode",
		["setcharprop"] = "char", ["setcharani"] = "ani", ["setchargender"] = "gender", ["canbecarried"] = "carry", ["cannotbecarried"] = "carry",
		["canbepushed"] = "push", ["cannotbepushed"] = "push", ["canbepulled"] = "pull", ["cannotbepulled"] = "pull",
		["noplayerkilling"] = "pk", ["timereverywhere"] = "timer", ["disablepause"] = "pause", ["enablepause"] = "pause",
		["setmap"] = "map", ["setminimap"] = "minimap",
	};

	private static readonly HashSet<string> NoPlayerEvents = new(StringComparer.OrdinalIgnoreCase) { "onCreated", "onTimeout", "onInitialized" };

	public bool Zone;
	public readonly List<string> Notes = [];
	private readonly Dictionary<FunctionDecl, List<object>> _origins = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<string, string> _renamed = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _assigned = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _customFlags = new(StringComparer.OrdinalIgnoreCase);
	private RuleContext? _context;

	private bool Gani => kind == ScriptKind.Gani;

	/// <summary>A level NPC's server part with GS1 code (players[0] is the event's player ...).</summary>
	private bool Gs1LevelServer => kind == ScriptKind.LevelNpc && !client && (readAsGs1 || gs1Functions);

	private void Note(string text)
	{
		if (!Notes.Contains(text)) Notes.Add(text);
	}

	private T Keep<T>(object original, T made) where T : class
	{
		comments.Alias(made, original);
		return made;
	}

	private object KeepAny(object original, object made)
	{
		comments.Alias(made, original);
		return made;
	}

	private static bool IsPlayerTarget(Expr target) => target is CallExpr { Name: "findplayer" or "getplayer" or "findplayer2" or "findplayerbycommunityname" }
		or IndexExpr { Target: NameExpr { Name: "allplayers" } } || target is IndexExpr { Target: var list } && IsPlayersList(list);

	/// <summary>
	///     GS1's with (players[i]) / with (getplayer(x)) changes the current player only: the player's commands, #a and the player's
	///     flags are that player's, and this. is still the NPC. GS2's with makes the target this and keeps the NPC as thiso: inside
	///     such a block player is written this and the NPC's this is written thiso.
	/// </summary>
	private List<Stmt> WithPlayer(List<Stmt> body)
	{
		var changed = false;
		var result = new List<Stmt>(body.Count);
		foreach (var s in body)
		{
			if (s is WithStmt w && IsPlayerTarget(w.Target))
			{
				CheckWithPlayerBody(w.Body, w.Target is IndexExpr { Target: var list } && IsPlayersList(list));
				var inner = new Rewriter(
					expr: e => e switch
					{
						NameExpr { Name: "player" } => new NameExpr("this"),
						NameExpr { Name: "this" } => new NameExpr("thiso"),
						NameExpr { Name: "client" or "clientr" } c => new MemberExpr(new NameExpr("this"), c.Name),
						NameExpr { Name: "thiso" or "playero" } => throw new UnconvertibleException("thiso/playero inside a GS1 with-player block"),
						_ => null,
					},
					stmt: st => st is WithStmt ? throw new UnconvertibleException("with inside a GS1 with-player block") : null,
					keep: KeepAny);
				var newBody = inner.Statements(w.Body);
				var made = ReferenceEquals(newBody, w.Body) ? w : w with { Body = newBody };
				if (!ReferenceEquals(made, w))
				{
					comments.Alias(made, w);
					changed = true;
					Note(WithNote);
				}
				result.Add(made);
				continue;
			}
			var rebuilt = new Rewriter(stmt: st => st is WithStmt ws && IsPlayerTarget(ws.Target) ? WithPlayer([st]) : null, keep: KeepAny).Statements([s]);
			if (rebuilt.Count != 1 || !ReferenceEquals(rebuilt[0], s)) changed = true;
			result.AddRange(rebuilt);
		}
		return changed ? result : body;
	}

	// Inside a GS2 with, a bare name is looked up on the target first: only names whose meaning is the same either way are allowed.
	private static readonly HashSet<string> WithRoots = new(StringComparer.Ordinal) { "player", "this", "client", "clientr", "server", "serverr", "params", "tokens", "pi" };

	private static readonly HashSet<string> WithCalls = new(StringComparer.OrdinalIgnoreCase)
	{
		// the player's commands, which a GS2 with gives to its target as GS1's with gave them to its current player
		"addweapon", "removeweapon", "setlevel2", "setlevel", "freezeplayer", "freezeplayer2", "unfreezeplayer", "disableweapons", "enableweapons",
		"say2", "say", "sethead", "setsword", "setshield", "setgender", "setskincolor", "setcoatcolor", "setsleevecolor", "setshoecolor", "setbeltcolor",
		// functions that read only their arguments
		"strequals", "strlen", "startswith", "strcontains", "contains", "indexof", "lindexof", "int", "float", "abs", "random", "format", "min", "max", "sin",
		"cos", "arctan", "getangle", "vecx", "vecy", "arraylen", "sarraylen", "char", "keyname", "findplayer", "findnpc", "makevar",
	};

	// level inside the block: the NPC's level or the player's; the same level when the target is one of the level's players.
	private static void CheckWithPlayerBody(List<Stmt> body, bool levelPlayer) =>
		new Rewriter(expr: e =>
		{
			switch (e)
			{
				case NameExpr { Name: "level" } when levelPlayer:
					break;
				case NameExpr { Name: var n } when !WithRoots.Contains(n):
					throw new UnconvertibleException($"with-player block uses '{n}' (GS1: the NPC's or the script's; GS2: looked up on the player first)");
				case CallExpr { Name: var c } when !WithCalls.Contains(c):
					throw new UnconvertibleException($"with-player block calls {c}() (GS1: the NPC's command; GS2: the player's first)");
			}
			return null;
		}).Statements(body);

	private const string WithNote = "with-player blocks written with GS1's meaning (player → this, the NPC's this → thiso)";

	public Script Convert(Script script)
	{
		var functions = script.Items.OfType<FunctionDecl>().ToList();
		var declarations = script.Items.Where(i => i is ConstDecl or EnumDecl).ToList();
		var main = script.Items.OfType<StatementItem>().SelectMany(s => s.Statement is BlockStmt b ? b.Body : [s.Statement]).ToList();
		// Names the script defines for itself that GS2 reserves are renamed.
		foreach (var f in functions)
			if (IsReserved(f.Name) || !Identifier.IsMatch(f.Name) || IsBuiltIn(f.Name))
			{
				var name = Identifier.IsMatch(f.Name) ? f.Name + "Fn" : throw new UnconvertibleException("function name " + f.Name);
				_renamed[f.Name] = name;
				Note($"function {f.Name} renamed {name} (in GS2 the name is a keyword or a built-in, which would be called instead)");
			}

		// GS1's timeout is the event only where a condition tests it: elsewhere it is the time left on the timer.
		var timeouts = TimeoutEvents();
		main = timeouts.Statements(main);
		functions = functions.Select(f => timeouts.Statements(f.Body) is var b && !ReferenceEquals(b, f.Body) ? Keep(f, f with { Body = b }) : f).ToList();

		Walk(main.Concat(functions.SelectMany(f => f.Body)).ToList(), e =>
		{
			if (e is BinaryExpr { Left: NameExpr { Name: var an }, Op: "=" or "+=" or "-=" or "@=" or "*=" or "/=" or "%=" or "^=" or ":=" or "|=" or "&=" or "<<=" or ">>=" })
			{
				_assigned.Add(an);
				_universe.Add(an);
			}
			// An array (x[0]), and a name also used as temp.x (GS2 binds a bare x to it): no flag.
			if (e is IndexExpr { Target: NameExpr { Name: var arrayName } }) _universe.Add(arrayName);
			if (e is MemberExpr { Target: NameExpr { Name: "temp" }, Name: var tempName }) _universe.Add(tempName);
			if (e is UnaryExpr { Op: "++" or "--", Operand: NameExpr { Name: var un } })
			{
				_assigned.Add(un);
				_universe.Add(un);
			}
			if (e is CallExpr { Name: "makevar", Args: [StringExpr { Value: var dv }] }) _assigned.Add(dv);
			if (e is CallExpr { Name: "setarray", Args: [NameExpr { Name: var array }, ..] }) _universe.Add(array);
			if (e is NewExpr { Args: [StringExpr { Value: var made }, ..] }) _universe.Add(made);
		});
		new Rewriter(stmt: st =>
		{
			switch (st)
			{
				// A bare word alone does nothing: kept as it is written (a comment).
				case ExprStmt { Expression: NameExpr alone }:
					_scoped.Add(alone);
					break;
				case WithStmt w:
					Walk(w.Body, x => { if (x is NameExpr n) _scoped.Add(n); });
					break;
				case NewStmt n:
					if (n.Args is [StringExpr { Value: var made }, ..]) _universe.Add(made);
					Walk(n.Body, x => { if (x is NameExpr name) _scoped.Add(name); });
					break;
			}
			return null;
		}).Statements(main.Concat(functions.SelectMany(f => f.Body)).ToList());
		FindCustomEvents(main, functions);
		_context = new RuleContext(kind, client ? ScriptSide.Client : ScriptSide.Server, readAsGs1, functions.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), Notes);
		var normalizer = Normalizer(functions);
		_gs1Code = _context.Gs1Code = true;
		main = normalizer.Statements(main);
		_gs1Code = _context.Gs1Code = readAsGs1 || gs1Functions;
		functions = functions.Select(f => Keep(f, f with { Name = _renamed.GetValueOrDefault(f.Name, f.Name), Body = normalizer.Statements(f.Body) })).ToList();
		_gs1Code = _context.Gs1Code = false;
		if (readAsGs1)
		{
			main = WithPlayer(main);
			functions = functions.Select(f => WithPlayer(f.Body) is var b && !ReferenceEquals(b, f.Body) ? Keep(f, f with { Body = b }) : f).ToList();
		}
		if (_playerFlags.Count > 0)
			Note($"GS1 flags read without a prefix are the player's: written player.{string.Join(", player.", _playerFlags.Take(6))}{(_playerFlags.Count > 6 ? " ..." : "")}");

		if (Uses(main, functions, e => e is NameExpr { Name: var n } && (n.Equals("sparringzone", StringComparison.OrdinalIgnoreCase) || n.Equals("nopkzone", StringComparison.OrdinalIgnoreCase)))) Zone = true;
		if (functions.Any(f => Uses(f.Body, [], e => e is NameExpr { Name: var n } && Flag(n))))
			throw new UnconvertibleException("an event flag is tested inside a function (true while the event runs)");

		// Joins outside any event stay at the top (GS2 runs them when the script starts).
		var joins = main.Where(s => s is ExprStmt { Expression: CallExpr { Name: "join" } }).ToList();
		var joinSet = new HashSet<Stmt>(joins, ReferenceEqualityComparer.Instance);
		main = main.Where(s => !joinSet.Contains(s)).ToList();

		// The events the script tests, in the order they first appear; created and timeout are always delivered.
		var tested = new List<string>();
		Walk(main, e =>
		{
			if (e is NameExpr { Name: var n } && Flag(n) && !tested.Contains(events.Canon(n), StringComparer.OrdinalIgnoreCase))
				tested.Add(events.IsStandard(n) || n.StartsWith("action", StringComparison.OrdinalIgnoreCase) ? events.Canon(n) : n);
		});
		foreach (var t in tested)
			if (events.CustomEvents.Contains(t) && !events.IsStandard(t)) Note($"'{t}' tested as an event (other scripts send it): on{char.ToUpperInvariant(t[0]) + t[1..]}()");
		Walk(main, e =>
		{
			if (e is NameExpr { Name: var tn } && events.IsMisspelling(tn)) Note($"bug fix: '{tn}' (never true in GS1: a misspelt event, or an event's name in another case) is the event {events.Canon(tn)}");
		});
		var eventList = new List<string>(tested);
		// GS1: an event other scripts send (callnpc i,doit;) runs the main code with its flag true, then the script's function of
		// that name. In GS2 the event is the function on<Event>: it gets the main code, then calls the function.
		var eventFunctions = readAsGs1 || gs1Functions
			? functions.Where(f => events.CustomEvents.Contains(f.Name) && !events.IsStandard(f.Name) && !f.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && f.Params.Count == 0).ToList()
			: [];
		foreach (var f in eventFunctions)
			if (!eventList.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
				eventList.Add(f.Name);
		var timerSet = Uses(main, functions, e => e is CallExpr { Name: var c } && c.Equals("setTimer", StringComparison.OrdinalIgnoreCase));
		if (!eventList.Contains("created", StringComparer.OrdinalIgnoreCase)) eventList.Insert(0, "created");
		if (timerSet && !eventList.Contains("istimeout", StringComparer.OrdinalIgnoreCase)) eventList.Add("istimeout");

		// Unconditional set-up statements run once, in onCreated.
		var unconditional = Specialize(main, "\0none");
		if (Gani && unconditional.Count > 0) throw new UnconvertibleException("gani script with code outside any event test (which events a gani gets is not established)");
		var setupOnly = new HashSet<Stmt>(ReferenceEqualityComparer.Instance);
		foreach (var s in unconditional)
			if (IsIdempotentSetup(s, main, functions)) setupOnly.Add(s);

		var handlers = new List<FunctionDecl>();
		foreach (var ev in eventList)
		{
			var body = Specialize(main, ev);
			if (!ev.Equals("created", StringComparison.OrdinalIgnoreCase)) body = body.Where(s => !setupOnly.Contains(s)).ToList();
			if (body.Count == 0) continue;
			var handler = new FunctionDecl(events.Function(ev), null, false, [], body);
			_origins[handler] = OriginsOf(main, ev);
			handlers.Add(handler);
		}

		// An event function of the script's own runs after the main code: the handler is the main code's statements, then the function's.
		foreach (var f in functions.ToList())
		{
			var at = handlers.FindIndex(h => h.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
			if (at < 0) continue;
			var generated = handlers[at];
			if (Returns(generated.Body)) throw new UnconvertibleException("main code that returns, and an event function of the same event: " + f.Name);
			if (f.Params.Count > 0) throw new UnconvertibleException("event function with parameters and main code of the same event: " + f.Name);
			var merged = generated with { Name = f.Name, Body = [.. generated.Body, .. f.Body] };
			_origins[merged] = _origins[generated];
			comments.Alias(merged, f);
			handlers[at] = merged;
			functions.RemoveAll(x => ReferenceEquals(x, f));
			Note($"main code of {f.Name} merged before the script's own {f.Name}");
		}

		foreach (var f in eventFunctions)
		{
			var handlerName = events.Function(f.Name);
			if (functions.Any(x => x.Name.Equals(handlerName, StringComparison.OrdinalIgnoreCase))) continue;
			var call = new ExprStmt(new CallExpr(f.Name, []));
			var at = handlers.FindIndex(h => h.Name.Equals(handlerName, StringComparison.OrdinalIgnoreCase));
			if (at >= 0)
			{
				if (Returns(handlers[at].Body)) throw new UnconvertibleException("main code that returns, and the function of the same event: " + f.Name);
				var withCall = handlers[at] with { Body = [.. handlers[at].Body, call] };
				_origins[withCall] = _origins[handlers[at]];
				comments.Alias(withCall, handlers[at]);
				handlers[at] = withCall;
			}
			else
			{
				var handler = new FunctionDecl(handlerName, null, false, [], [call]);
				_origins[handler] = [];
				handlers.Add(handler);
			}
			Note($"'{f.Name}' (an event other scripts send) is {handlerName}(), which calls the script's {f.Name}() after its main code, as in GS1");
		}

		// Same bodies: the later parameterless handler calls the earlier one.
		var printedBodies = new Dictionary<string, FunctionDecl>();
		for (var i = 0; i < handlers.Count; i++)
		{
			var h = handlers[i];
			var key = BodyKey(h.Body);
			if (ParameterlessEvents.Contains(h.Name) && printedBodies.TryGetValue(key, out var earlier) && ParameterlessEvents.Contains(earlier.Name) && h.Body.Count > 1)
			{
				var call = h with { Body = [new ExprStmt(new CallExpr(earlier.Name, []))] };
				_origins[call] = _origins[h];
				handlers[i] = call;
			}
			else printedBodies.TryAdd(key, h);
		}

		for (var i = 0; i < handlers.Count; i++)
		{
			var reordered = PlayerOrder(handlers[i], true);
			if (!ReferenceEquals(reordered, handlers[i]))
			{
				_origins[reordered] = _origins[handlers[i]];
				comments.Alias(reordered, handlers[i]);
				handlers[i] = reordered;
			}
		}
		functions = functions.Select(f =>
		{
			var r = PlayerOrder(f, false);
			if (!ReferenceEquals(r, f)) comments.Alias(r, f);
			return r;
		}).ToList();

		if (kind == ScriptKind.Class && readAsGs1 && handlers.Count > 0)
			Note("a GS1 class's code outside functions never ran when a script joined it (GS1 ran only the script's own main code): in GS2 its event functions run");
		var items = new List<Item>(declarations);
		items.AddRange(joins.Select(j => Keep(j, new StatementItem(j))));
		items.AddRange(handlers);
		items.AddRange(functions);
		if (handlers.Count == 0 && functions.Count == 0 && joins.Count == 0 && main.Count > 0) Note("every event body was empty: only the comments are left");
		return new Script(items);
	}

	private bool Flag(string name) => (events.IsEventFlag(name) || _customFlags.Contains(name)) && !name.Equals("timeout", StringComparison.OrdinalIgnoreCase);

	// timeout tested by an if, while, &&, ||, ! or ?: is the event (istimeout).
	private Rewriter TimeoutEvents()
	{
		Expr Event(Expr e) => e switch
		{
			NameExpr { Name: var n } when n.Equals("timeout", StringComparison.OrdinalIgnoreCase) && events.IsEventFlag(n) => new NameExpr("istimeout"),
			BinaryExpr { Op: "&&" or "||" } b when Event(b.Left) is var l && Event(b.Right) is var r && (!ReferenceEquals(l, b.Left) || !ReferenceEquals(r, b.Right)) => b with { Left = l, Right = r },
			UnaryExpr { Op: "!" } u when Event(u.Operand) is var x && !ReferenceEquals(x, u.Operand) => u with { Operand = x },
			_ => e,
		};
		Expr? Changed(Expr before, Expr after) => ReferenceEquals(before, after) ? null : after;
		return new Rewriter(
			expr: e => e switch
			{
				BinaryExpr { Op: "&&" or "||" } or UnaryExpr { Op: "!" } => Changed(e, Event(e)),
				ConditionalExpr t => Changed(t.Condition, Event(t.Condition)) is { } c ? t with { Condition = c } : null,
				_ => null,
			},
			stmt: s => s switch
			{
				IfStmt i when Changed(i.Condition, Event(i.Condition)) is { } c => [Keep(s, i with { Condition = c })],
				WhileStmt w when Changed(w.Condition, Event(w.Condition)) is { } c => [Keep(s, w with { Condition = c })],
				DoWhileStmt d when Changed(d.Condition, Event(d.Condition)) is { } c => [Keep(s, d with { Condition = c })],
				_ => null,
			},
			keep: KeepAny);
	}

	// A bare name GS1 reads as the player's flag: one of no GS1 meaning, never assigned, no event.
	private bool IsPlayerFlag(string name) =>
		Gs1Flags && Identifier.IsMatch(name) && !IsReserved(name) && !Gs1Names.Contains(name) && !BuiltInNames.Globals.Contains(name) && !_universe.Contains(name)
		&& !shared.Contains(name) && !Flag(name) && !events.IsEventFlag(name) && !name.Contains("::", StringComparison.Ordinal)
		&& !name.StartsWith("peltwith", StringComparison.OrdinalIgnoreCase)
		&& !name.StartsWith("player", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("carries", StringComparison.OrdinalIgnoreCase)
		&& !name.Equals("NULL", StringComparison.Ordinal);

	// The custom events of this script: names other scripts send that this one only ever tests (each use a condition, never set).
	private void FindCustomEvents(List<Stmt> main, List<FunctionDecl> functions)
	{
		var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var atoms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var all = main.Concat(functions.SelectMany(f => f.Body)).ToList();
		Walk(all, e =>
		{
			if (e is NameExpr { Name: var n }) total[n] = total.GetValueOrDefault(n) + 1;
		});
		void Atoms(Expr c)
		{
			switch (c)
			{
				case NameExpr { Name: var n }:
					atoms[n] = atoms.GetValueOrDefault(n) + 1;
					break;
				case BinaryExpr { Op: "&&" or "||" } b:
					Atoms(b.Left);
					Atoms(b.Right);
					break;
				case UnaryExpr { Op: "!" } u:
					Atoms(u.Operand);
					break;
			}
		}
		new Rewriter(stmt: st =>
		{
			if (st is IfStmt i) Atoms(i.Condition);
			return null;
		}).Statements(all);
		foreach (var (n, count) in atoms)
			if (events.CustomEvents.Contains(n) && !_assigned.Contains(n) && total.GetValueOrDefault(n) == count && !events.IsEventFlag(n)) _customFlags.Add(n);
	}

	private static bool Returns(List<Stmt> body)
	{
		var found = false;
		new Rewriter(stmt: st =>
		{
			if (st is ReturnStmt) found = true;
			return null;
		}).Statements(body);
		return found;
	}

	private static string BodyKey(List<Stmt> body)
	{
		var p = new Printer();
		p.Statements(body);
		return p.ToString();
	}

	private List<object> OriginsOf(List<Stmt> main, string ev)
	{
		var list = new List<object>();
		foreach (var s in main)
		{
			var uses = false;
			Walk([s], e =>
			{
				if (e is NameExpr { Name: var n } && Flag(n) && events.SameEvent(n, ev)) uses = true;
			});
			if (uses) list.Add(s);
		}
		return list;
	}

	// ------------------------------------------------ normalization

	private Rewriter Normalizer(List<FunctionDecl> functions)
	{
		var context = _context!;
		return new Rewriter(
			expr: e =>
			{
				foreach (var rule in rules)
					if (rule.RewriteExpression(e, context) is { } replaced)
						return replaced;
				return Core(e, functions);
			},
			stmt: s =>
			{
				foreach (var rule in rules)
					if (rule.RewriteStatement(s, context) is { } replaced)
						return replaced.Select((r, i) => i == 0 ? Keep(s, r) : r).ToList();
				return Core(s);
			},
			keep: KeepAny);
	}

	/// <summary>What a GS1 expression means, written in GS2.</summary>
	private Expr? Core(Expr e, List<FunctionDecl> functions) => e switch
	{
		// makevar("this.x") is the variable this.x; makevar("flag") (set/unset/setstring of a GS1 flag) is the player's flag.
		CallExpr { Name: "makevar", Args: [StringExpr { Value: var name }] } when VariablePath(name) is { } path => path,
		NameExpr { Name: "NULL" } => new NullExpr(),
		// GS1: a bare name nothing defines is the player's flag (set flag; and if (flag) are one variable).
		NameExpr { Name: var flag } bare when !_scoped.Contains(bare) && IsPlayerFlag(flag) && _playerFlags.Add(flag) | true => new MemberExpr(new NameExpr("player"), flag),
		// A member named by something that is not a GS2 name (this.2dstars, this.632, temp.default) is the member of that name.
		MemberExpr { Name: var memberName } badMember when !Identifier.IsMatch(memberName) || IsReserved(memberName) => new DynamicMemberExpr(badMember.Target, new StringExpr(memberName)),
		// 0x40 is written 64; up to 8 digits a hex number is 32 bits (0xFFFFFFFF is -1).
		NumberExpr { Text: var hex } when hex.Length > 2 && hex[0] == '0' && hex[1] is 'x' or 'X' && long.TryParse(hex[2..], System.Globalization.NumberStyles.HexNumber, null, out var value) =>
			Number(hex.Length <= 10 ? (int)(uint)value : value),
		// 1e5 (in no GS2 documentation) is written 100000.
		NumberExpr { Text: var exp } when exp.Contains('e', StringComparison.OrdinalIgnoreCase) && !exp.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(exp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) =>
			new NumberExpr(number.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture)),
		// GS2 documents no \t in texts: a tab is TAB.
		StringExpr { Value: var tabbed } when tabbed.Contains('\t') => tabbed.Split('\t').Select(Expr (part) => new StringExpr(part)).Aggregate((a, b) => new BinaryExpr(a, "\t", b)),
		// GS1's =< and => are <= and >=.
		BinaryExpr { Op: "=<" } le => le with { Op = "<=" },
		BinaryExpr { Op: "=>" } ge => ge with { Op = ">=" },
		// Old text functions in GS2 code read their argument's source text: keycode(m) is the key m, lindexof(0, this.list) the
		// position of "0" in the list. (Anything else is left as written.)
		CallExpr { Name: var kc, Args: [var key and (NameExpr or NumberExpr)] } when kc.Equals("keycode", StringComparison.OrdinalIgnoreCase) => new CallExpr("getkeycode", [new StringExpr(SourceText(key))]),
		CallExpr { Name: var li, Args: [var item and (NameExpr or NumberExpr), var list and (MemberExpr or NameExpr)] } when li.Equals("lindexof", StringComparison.OrdinalIgnoreCase) =>
			new MethodCallExpr(list, "index", [new StringExpr(SourceText(item))]),
		// GS1's x in <a, b> leaves out its bounds (only |a, b| is GS2's): written as comparisons.
		InExpr { High: { } high, LowExclusive: var lx, HighExclusive: var hx } range when (lx || hx) && _gs1Code && Rewriter.IsPure(range.Value) =>
			new BinaryExpr(new BinaryExpr(range.Value, lx ? ">" : ">=", range.Low), "&&", new BinaryExpr(range.Value, hx ? "<" : "<=", high)),
		// sarraylen(this.list) in GS2 code: the length of the list the old text function names.
		CallExpr { Name: var sl, Args: [var listPath and (MemberExpr or NameExpr)] } when sl.Equals("sarraylen", StringComparison.OrdinalIgnoreCase) => new CallExpr("arraylen", [listPath]),
		// strequals casts its arguments itself.
		CallExpr { Name: "strequals", Args: [var a, var b] } c => Uncast(a) is var ua && Uncast(b) is var ub && (!ReferenceEquals(ua, a) || !ReferenceEquals(ub, b)) ? c with { Args = [ua, ub] } : null,
		CallExpr c when _renamed.TryGetValue(c.Name, out var renamed) && (!IsBuiltIn(c.Name) || functions.Any(f => f.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && f.Params.Count == c.Args.Count)) => c with { Name = renamed },
		_ => null,
	};

	/// <summary>What a GS1 statement means, written in GS2.</summary>
	private IEnumerable<Stmt>? Core(Stmt s) => s switch
	{
		// a, b; is a; b;
		ExprStmt { Expression: SequenceExpr q } => q.Items.Select((x, i) => i == 0 ? Keep(s, new ExprStmt(x)) : new ExprStmt(x)).ToList(),
		// for (a, b; c; d) is a; b; for (; c; d).
		ForStmt { Init: SequenceExpr q } f => [.. q.Items.Select(x => new ExprStmt(x)), Keep(s, f with { Init = null })],
		// A read alone does nothing.
		ExprStmt { Expression: MemberExpr or NumberExpr or StringExpr or IndexExpr or BoolExpr } read => Dropped(s, new Printer().Expr(read.Expression)),
		// timeout = x; is setTimer(x); (the engine: "like assigning timeout").
		ExprStmt { Expression: BinaryExpr { Left: NameExpr { Name: "timeout" }, Op: "=", Right: var delay } } => [Keep(s, new ExprStmt(new CallExpr("setTimer", [delay])))],
		// A bare word does nothing in GS1.
		ExprStmt { Expression: NameExpr { Name: var word } } when !events.IsEventFlag(word) && !word.Equals("sparringzone", StringComparison.OrdinalIgnoreCase) && !word.Equals("nopkzone", StringComparison.OrdinalIgnoreCase) => Dropped(s, word),
		_ => null,
	};

	/// <summary>
	///     GS1's players[0] is the event's player (then the level's others by ID); GS2's players is the level's list by ID. In an event
	///     with a player, players[0] is player; without one (created, timeout) the two lists agree. Any other fixed position in the list
	///     depends on that order and is left for a person.
	/// </summary>
	private FunctionDecl PlayerOrder(FunctionDecl f, bool handler)
	{
		if (!Gs1LevelServer) return f;
		var withPlayer = handler && !NoPlayerEvents.Contains(f.Name);
		var rewriter = new Rewriter(expr: e => e switch
		{
			IndexExpr { Target: var l0, Indices: [NumberExpr { Text: "0" }] } when IsPlayersList(l0) && withPlayer => new NameExpr("player"),
			IndexExpr { Target: var l1, Indices: [NumberExpr { Text: "0" }] } when IsPlayersList(l1) && !handler => throw new UnconvertibleException("players[0] in a function (whose event decides who it is)"),
			IndexExpr { Target: var l2, Indices: [var index] } when IsPlayersList(l2) && index is not (NumberExpr { Text: "0" } or NameExpr or MemberExpr or CallExpr { Name: "float" or "int", Args: [NameExpr or MemberExpr] }) =>
				throw new UnconvertibleException("players[" + new Printer().Expr(index) + "]: GS1 lists the event's player first"),
			_ => null,
		}, keep: KeepAny);
		var body = rewriter.Statements(f.Body);
		return ReferenceEquals(body, f.Body) ? f : f with { Body = body };
	}

	private IEnumerable<Stmt> Dropped(Stmt s, string word)
	{
		Note($"no-op '{word}' (did nothing in GS1) kept as a comment");
		return [Keep(s, new CommentStmt($"// {word}; (this did nothing in the GS1 script)"))];
	}

	private static Expr Number(long value) =>
		value < 0 ? new UnaryExpr("-", new NumberExpr((-value).ToString(System.Globalization.CultureInfo.InvariantCulture))) : new NumberExpr(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static string SourceText(Expr e) => e is NameExpr n ? n.Name : ((NumberExpr)e).Text;

	private static Expr Uncast(Expr e) => e is UnaryExpr { Op: "@", Operand: MemberExpr or NameExpr or IndexExpr } c ? c.Operand : e;

	private Expr? VariablePath(string name)
	{
		var parts = name.Split('.');
		if (parts.Any(p => p.Length == 0 || !(Identifier.IsMatch(p) || p.All(char.IsAsciiDigit)) || IsReserved(p))) return null;
		if (parts.Length == 1)
		{
			// A GS1 flag without a prefix: the player's (the account's unprefixed flags are the player's members); with gs1-flags, on
			// either side, unless the script assigns the name (then it is that variable).
			if (!Identifier.IsMatch(parts[0]) || events.IsEventFlag(parts[0])) return null;
			if (Gs1Flags && _universe.Contains(parts[0])) return new NameExpr(parts[0]);
			if (client && !Gs1Flags) return null;
			return new MemberExpr(new NameExpr("player"), parts[0]);
		}
		if (!Identifier.IsMatch(parts[0])) return null;
		// quest.step: the player's flag quest.step, when quest is no GS1 object and no variable of the script's.
		Expr e = IsPlayerFlag(parts[0]) ? new MemberExpr(new NameExpr("player"), parts[0]) : new NameExpr(parts[0]);
		foreach (var p in parts.Skip(1)) e = new MemberExpr(e, p);
		return e;
	}

	// ------------------------------------------------ specialization

	/// <summary>The main code as it runs for one event: that event's flag true, every other flag false, folded.</summary>
	private List<Stmt> Specialize(List<Stmt> main, string ev)
	{
		var rewriter = new Rewriter(
			expr: e => e is NameExpr { Name: var n } && Flag(n) ? new BoolExpr(events.SameEvent(n, ev)) : null,
			stmt: null, keep: KeepAny, fold: true, inherit: comments.Inherit);
		return rewriter.Statements(main);
	}

	private static bool IsIdempotentSetup(Stmt s, List<Stmt> main, List<FunctionDecl> functions)
	{
		if (s is not ExprStmt { Expression: CallExpr call } || !SetupGroups.TryGetValue(call.Name, out var group)) return false;
		if (!call.Args.All(IsConstant)) return false;
		var count = 0;
		Walk(main.Concat(functions.SelectMany(f => f.Body)).ToList(), e =>
		{
			if (e is CallExpr c && SetupGroups.TryGetValue(c.Name, out var g) && g == group) count++;
		});
		return count == 1;
	}

	private static bool IsConstant(Expr e) => e is NumberExpr or StringExpr or BoolExpr or UnaryExpr { Op: "-", Operand: NumberExpr } || e is BinaryExpr { Op: "@" } b && IsConstant(b.Left) && IsConstant(b.Right);

	// ------------------------------------------------ printing

	public void ResetComments() => comments.Reset();

	public string Print(Script script)
	{
		var printer = new Printer { Comments = comments };
		var first = true;
		foreach (var item in script.Items)
		{
			if (item is not FunctionDecl f)
			{
				printer.Item(item);
				continue;
			}
			if (!first) printer.Blank();
			first = false;
			if (_origins.TryGetValue(f, out var origins))
				foreach (var o in origins) comments.Before(o, printer);
			printer.Function(f);
		}
		comments.Rest(printer);
		return printer.ToString();
	}
}
