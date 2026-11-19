namespace Uplift.Syntax;

/// <summary>
///     A GS1 command or function and the kind of each argument:
///     T text (message codes expanded; a command's last text argument is the rest of the statement, commas included),
///     N a numeric expression, V a variable's name, C a message code to set (setplayerprop #c,...), D a direction (up, left, down,
///     right, or a number), B a script block (putnpc2's {script}: the text between the braces). Arguments past the last kind take
///     the last kind.
/// </summary>
public sealed record Gs1Command(string Name, string Kinds)
{
	public char ArgumentKind(int index) => Kinds.Length == 0 ? 'T' : Kinds[Math.Min(index, Kinds.Length - 1)];

	/// <summary>Whether the argument at an index is the last text argument (which takes the rest of the statement).</summary>
	public bool IsRest(int index) => Kinds.Length > 0 && index >= Kinds.Length - 1 && Kinds[^1] == 'T';
}

/// <summary>
///     What GS1's commands, functions, message codes and predefined names are, and the GS2 that means the same. Built from GS1's
///     public documentation; a server adds its own commands in uplift.json ("gs1Commands") or a plugin.
/// </summary>
public sealed class Gs1Catalog
{
	// ------------------------------------------------------------ commands

	private static readonly Dictionary<string, string> StandardCommands = new(StringComparer.OrdinalIgnoreCase)
	{
		// NPC appearance and set-up
		["setimg"] = "T", ["setgif"] = "T", ["setimgpart"] = "TNNNN", ["setgifpart"] = "TNNNN", ["setshape"] = "NNN", ["setshape2"] = "NNN",
		["showcharacter"] = "", ["setcharprop"] = "CT", ["setcharani"] = "TT", ["setchargender"] = "T", ["setplayerprop"] = "CT",
		["dontblock"] = "", ["blockagain"] = "", ["dontblocklocal"] = "", ["blockagainlocal"] = "", ["drawunderplayer"] = "", ["drawoverplayer"] = "",
		["drawaslight"] = "", ["show"] = "", ["hide"] = "", ["showlocal"] = "", ["hidelocal"] = "", ["setcoloreffect"] = "NNNN",
		["setzoomeffect"] = "N", ["seteffect"] = "NNNN", ["seteffectmode"] = "N", ["canbecarried"] = "", ["cannotbecarried"] = "",
		["canbepushed"] = "", ["cannotbepushed"] = "", ["canbepulled"] = "", ["cannotbepulled"] = "", ["noplayerkilling"] = "",
		["timereverywhere"] = "", ["destroy"] = "", ["setbackpal"] = "T", ["setletters"] = "T", ["setmap"] = "TTNN", ["setminimap"] = "TTNN",
		["replaceani"] = "TT", ["setani"] = "TT", ["join"] = "T", ["followplayer"] = "", ["setcursor"] = "N",
		// the player
		["sethead"] = "T", ["setbody"] = "T", ["setsword"] = "TN", ["setshield"] = "TN", ["setgender"] = "T",
		["setskincolor"] = "T", ["setcoatcolor"] = "T", ["setsleevecolor"] = "T", ["setshoecolor"] = "T", ["setbeltcolor"] = "T",
		["setlevel"] = "T", ["setlevel2"] = "TNN", ["warpto"] = "TNN", ["addweapon"] = "T", ["removeweapon"] = "T", ["toweapons"] = "T",
		["enableweapons"] = "", ["disableweapons"] = "", ["enableselectweapons"] = "", ["disableselectweapons"] = "", ["enabledefmovement"] = "",
		["disabledefmovement"] = "", ["enablemap"] = "", ["disablemap"] = "", ["enablepause"] = "", ["disablepause"] = "", ["enablefeatures"] = "N",
		["freezeplayer"] = "N", ["unfreezeplayer"] = "", ["hurt"] = "N", ["setplayerdir"] = "D", ["hideplayer"] = "N", ["takeplayercarry"] = "",
		["takeplayerhorse"] = "", ["takehorse"] = "", ["throwcarry"] = "", ["carryobject"] = "T", ["attachplayertoobj"] = "NN", ["detachplayer"] = "",
		["setfocus"] = "NN", ["resetfocus"] = "", ["showstats"] = "N", ["loadmap"] = "T", ["toinventory"] = "T", ["take"] = "T", ["take2"] = "N", ["addguildmember"] = "TTT", ["removeguildmember"] = "TT",
		// talking and messages
		["say"] = "N", ["say2"] = "T", ["message"] = "T", ["sendpm"] = "T", ["sendtorc"] = "T", ["sendrpgmessage"] = "T", ["savelog"] = "T",
		["savelog2"] = "TT", ["openurl"] = "T", ["openurl2"] = "TNN",
		// images and text on the screen
		["showimg"] = "NTNN", ["showimg2"] = "NTNNN", ["hideimg"] = "N", ["hideimgs"] = "NN", ["changeimgvis"] = "NN", ["changeimgpart"] = "NNNNN",
		["changeimgcolors"] = "NNNNN", ["changeimgzoom"] = "NN", ["changeimgmode"] = "NN", ["showtext"] = "NNNTTT", ["showtext2"] = "NNNNTTT",
		["showpoly"] = "NN", ["showpoly2"] = "NN", ["showani"] = "NNNNT", ["showani2"] = "NNNNNT", ["wraptext"] = "NTT", ["wraptext2"] = "NNTT",
		// sound
		["play"] = "T", ["play2"] = "TNNN", ["playlooped"] = "T", ["stopsound"] = "T", ["stopmidi"] = "", ["setmusicvolume"] = "NN",
		// the level
		["putleaps"] = "NNN", ["putbomb"] = "NNN", ["putexplosion"] = "NNN", ["putexplosion2"] = "NNNN", ["removebomb"] = "N", ["removeexplo"] = "N",
		["removearrow"] = "N", ["lay"] = "T", ["lay2"] = "TNN", ["putnpc"] = "TTNN", ["putnpc2"] = "NNB", ["putcomp"] = "TNN", ["puthorse"] = "TNN",
		["putobject"] = "TNN", ["updateboard"] = "NNNN", ["updateboard2"] = "NNNN", ["updateterrain"] = "", ["addtiledef"] = "TTN",
		["addtiledef2"] = "TTNN", ["removetiledefs"] = "T", ["copylevel"] = "TT", ["hitplayer"] = "NNNN", ["hitcompu"] = "NNNN", ["hitnpc"] = "NNN",
		["hitobjects"] = "NNN", ["shoot"] = "NNNNNNTT", ["shootarrow"] = "D", ["shootball"] = "", ["shootfireball"] = "D", ["shootfireblast"] = "D",
		["shootnuke"] = "D", ["setshootparams"] = "T", ["move"] = "NNNN", ["sleep"] = "N",
		// variables, flags and texts
		["set"] = "V", ["unset"] = "V", ["setstring"] = "VT", ["setarray"] = "VN", ["tokenize"] = "T", ["tokenize2"] = "TT",
		["addstring"] = "VT", ["removestring"] = "VT", ["deletestring"] = "VN", ["insertstring"] = "VNT", ["replacestring"] = "VNT",
		// other scripts
		["callnpc"] = "NT", ["callweapon"] = "NT", ["triggeraction"] = "NNTT",
		// more of GS1's documented commands
		["cannotwarp"] = "", ["canwarp"] = "", ["canwarp2"] = "", ["copyflags"] = "TT", ["copystrings"] = "TT", ["deletelevel"] = "T", ["explodebomb"] = "N",
		["freezeplayer2"] = "", ["hidesword"] = "N", ["noplayeronwall"] = "", ["putnewcomp"] = "TNNTN", ["reflectarrow"] = "N", ["removecompus"] = "",
		["removeguild"] = "T", ["removehorse"] = "N", ["removeitem"] = "N", ["saveinfo"] = "TT", ["sendtonc"] = "T", ["serverwarp"] = "T", ["setbow"] = "T",
		["setcursor2"] = "T", ["setpm"] = "T", ["seturllevel"] = "T", ["setz"] = "NNNNNNNN", ["showfile"] = "T", ["spyfire"] = "NN", ["timershow"] = "",
		["setplayerx"] = "N", ["setplayery"] = "N", ["setx"] = "N", ["sety"] = "N", ["reducebombs"] = "N", ["reducedarts"] = "N", ["reducerupees"] = "N",
		["setbacktile"] = "N", ["setbacktile2"] = "NNNNN", ["setspritesimage"] = "T", ["setstatusimage"] = "T", ["drawovertrees"] = "",
	};

	// Commands that are another command's other name.
	private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase) { ["setgif"] = "setimg", ["setgifpart"] = "setimgpart" };

	private static readonly Dictionary<string, int> Directions = new(StringComparer.OrdinalIgnoreCase) { ["up"] = 0, ["left"] = 1, ["down"] = 2, ["right"] = 3 };

	// ------------------------------------------------------------ functions with text or variable arguments

	private static readonly Dictionary<string, string> StandardFunctions = new(StringComparer.OrdinalIgnoreCase)
	{
		["strequals"] = "TT", ["strcontains"] = "TT", ["startswith"] = "TT", ["strlen"] = "T", ["strtofloat"] = "T", ["lindexof"] = "TV",
		["sarraylen"] = "V", ["imgwidth"] = "T", ["imgheight"] = "T", ["hasweapon"] = "T", ["indexof"] = "TT", ["getnpc"] = "T", ["getplayer"] = "T", ["playersays"] = "T",
		["onmapx"] = "T", ["onmapy"] = "T", ["ascii"] = "T", ["keycode"] = "T", ["getflagkeys"] = "T", ["playersays2"] = "T", ["textwidth"] = "NTTT",
		["textheight"] = "NTTT", ["base64encode"] = "T", ["base64decode"] = "T", ["hasright"] = "TT", ["passwordmatches"] = "TT",
	};

	// ------------------------------------------------------------ message codes

	// The attribute each player code stands for (#c is the player's chat; with an index, another player's or, with -1, the NPC's).
	private static readonly Dictionary<string, string> Attributes = new(StringComparer.Ordinal)
	{
		["c"] = "chat", ["n"] = "nick", ["a"] = "account", ["g"] = "guild", ["m"] = "ani", ["1"] = "swordimg", ["2"] = "shieldimg", ["3"] = "headimg",
		["5"] = "horseimg", ["6"] = "carrynpc", ["7"] = "bowimg", ["8"] = "bodyimg",
	};

	// The codes that take arguments, and their kinds.
	private static readonly Dictionary<string, string> CodeArgumentKinds = new(StringComparer.Ordinal)
	{
		["v"] = "N", ["s"] = "V", ["p"] = "N", ["e"] = "NNT", ["I"] = "VN", ["t"] = "N", ["T"] = "T", ["K"] = "N", ["W"] = "N", ["w"] = "N", ["P"] = "N",
		["N"] = "N", ["L"] = "N",
	};

	// Codes that need no arguments and are not player attributes (#N, #L and #W also take an index).
	private static readonly HashSet<string> PlainCodes = ["L", "F", "f", "N", "w", "W", "D"];

	// Codes GS1 leaves as written: #K(n) and #7 are text (setplayerprop #7 is the bow).
	private static readonly HashSet<string> LiteralCodes = ["K", "7"];

	// ------------------------------------------------------------ predefined names

	private static readonly HashSet<string> PlayerVariables = new(StringComparer.OrdinalIgnoreCase)
	{
		"x", "y", "z", "dir", "hearts", "fullhearts", "rupees", "darts", "bombs", "glovepower", "swordpower", "shieldpower", "ap", "mp", "sprite", "id",
		"freezetime", "hurtpower",
	};

	private static readonly Dictionary<string, Func<Expr>> Counts = new(StringComparer.OrdinalIgnoreCase)
	{
		["playerscount"] = () => Length(new NameExpr("players")),
		["npcscount"] = () => Length(new NameExpr("npcs")),
		["compuscount"] = () => Length(new NameExpr("compus")),
		["bombscount"] = () => Length(new NameExpr("bombs")),
		["arrowscount"] = () => Length(new NameExpr("arrows")),
		["weaponscount"] = () => Length(new MemberExpr(new NameExpr("player"), "weapons")),
		["tokenscount"] = () => Length(new NameExpr("tokens")),
		["allplayerscount"] = () => Length(new NameExpr("allplayers")),
		["itemscount"] = () => Length(new NameExpr("items")),
		["exploscount"] = () => Length(new NameExpr("explos")),
		["horsescount"] = () => Length(new NameExpr("horses")),
		["signscount"] = () => Length(new NameExpr("signs")),
		["paramscount"] = () => Length(new NameExpr("params")),
	};

	private readonly Dictionary<string, string> _commands;
	private readonly Dictionary<string, string> _functions;

	public Gs1Catalog(IEnumerable<KeyValuePair<string, string>>? moreCommands = null, IEnumerable<KeyValuePair<string, string>>? moreFunctions = null)
	{
		_commands = new Dictionary<string, string>(StandardCommands, StringComparer.OrdinalIgnoreCase);
		_functions = new Dictionary<string, string>(StandardFunctions, StringComparer.OrdinalIgnoreCase);
		foreach (var (name, kinds) in moreCommands ?? []) _commands[name] = kinds.ToUpperInvariant();
		foreach (var (name, kinds) in moreFunctions ?? []) _functions[name] = kinds.ToUpperInvariant();
	}

	public static Gs1Catalog Default { get; } = new();

	public IReadOnlyDictionary<string, string> Commands => _commands;

	public bool TryGetCommand(string name, out Gs1Command command)
	{
		if (_commands.TryGetValue(name, out var kinds))
		{
			command = new Gs1Command(name.ToLowerInvariant(), kinds);
			return true;
		}
		command = null!;
		return false;
	}

	public bool TryGetFunction(string name, out Gs1Command function)
	{
		if (_functions.TryGetValue(name, out var kinds))
		{
			function = new Gs1Command(name.ToLowerInvariant(), kinds);
			return true;
		}
		function = null!;
		return false;
	}

	// ------------------------------------------------------------ codes

	/// <summary>Whether a known message code starts at a position (just after its #).</summary>
	public static bool IsCodeStart(string s, int i)
	{
		var j = i;
		return ReadCodeName(s, ref j) != null;
	}

	/// <summary>Reads a known code's name at a position (after #); null when no known code starts there.</summary>
	// target: the code a command sets (setcharprop #C5,...), any color index; in a text only #C0 .. #C4 are codes.
	public static string? ReadCodeName(string s, ref int i, bool target = false)
	{
		if (i >= s.Length) return null;
		var c = s[i];
		if (c == 'C' && i + 1 < s.Length && (target ? char.IsAsciiDigit(s[i + 1]) : s[i + 1] is >= '0' and <= '4'))
		{
			i += 2;
			return "C" + s[i - 1];
		}
		if (c == 'P' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
		{
			var j = i + 1;
			while (j < s.Length && j < i + 3 && char.IsAsciiDigit(s[j])) j++;
			var name = "P" + s[(i + 1)..j];
			i = j;
			return name;
		}
		var code = c.ToString();
		var takesArguments = CodeArgumentKinds.ContainsKey(code);
		var after = i + 1;
		if (NeedsArguments(code))
			while (after < s.Length && s[after] is ' ' or '\t' or '\r' or '\n') after++;
		var hasParen = after < s.Length && s[after] == '(';
		if (Attributes.ContainsKey(code) || PlainCodes.Contains(code) || takesArguments && hasParen)
		{
			// #s, #v, #p, #e, #I, #t, #T, #K, #W, #P need their arguments.
			if (takesArguments && !hasParen && !Attributes.ContainsKey(code) && !PlainCodes.Contains(code)) return null;
			i++;
			return code;
		}
		return null;
	}

	/// <summary>Whether GS1 leaves a code as its text (#K(32), #7).</summary>
	public static bool IsLiteralCode(string code) => LiteralCodes.Contains(code);

	/// <summary>Whether a code is nothing without its arguments (#v(...), #s(...) ...).</summary>
	public static bool NeedsArguments(string code) => CodeArgumentKinds.ContainsKey(code) && !Attributes.ContainsKey(code) && !PlainCodes.Contains(code);

	/// <summary>The kinds of a code's arguments ("" for an optional player index), or null when it takes none.</summary>
	public string? CodeArguments(string code) =>
		CodeArgumentKinds.TryGetValue(code, out var kinds) ? kinds
		: Attributes.ContainsKey(code) || code.StartsWith('C') || code.StartsWith('P') ? "N" : null;

	/// <summary>Whether a code stands for an attribute of a player or the NPC (its value is made text when it is assigned alone).</summary>
	public static bool IsAttributeCode(string code) => Attributes.ContainsKey(code) || code.Length > 1 && code[0] is 'C' or 'P';

	/// <summary>The GS2 a message code means.</summary>
	public Expr? LowerCode(string code, List<Expr> args)
	{
		switch (code)
		{
			case "L" or "F" when args.Count == 0:
				return new MemberExpr(new NameExpr("level"), "name");
			case "L" when args.Count == 1:
				return new MemberExpr(new MemberExpr(new IndexExpr(new NameExpr("players"), [args[0]]), "level"), "name");
			case "f":
				return new NameExpr("image");
			case "N" when args.Count == 0:
				return new NameExpr("name");
			case "N" when args.Count == 1:
				return new MemberExpr(new IndexExpr(new NameExpr("npcs"), [args[0]]), "name");
			case "D":
				return new NameExpr("downloadfile");
			case "W" when args.Count == 0:
				return new MemberExpr(new MemberExpr(new NameExpr("player"), "weapon"), "image");
			case "v" when args.Count == 1:
				return new CallExpr("float", [args[0]]);
			case "s" when args.Count == 1:
				return args[0] is CallExpr { Name: "makevar", Args: [StringExpr { Value: var path }] } && PathExpr(path) is { } p ? p : args[0] is CallExpr { Name: "makevar" } ? args[0] : Var(args[0]);
			case "p" when args.Count == 1:
				return new IndexExpr(new NameExpr("params"), [args[0]]);
			case "e" when args.Count == 3:
				return new MethodCallExpr(args[2], "substring", [args[0], args[1]]);
			case "I" when args.Count == 2:
				return new IndexExpr(Var(args[0]), [args[1]]);
			case "t" when args.Count == 1:
				return new IndexExpr(new NameExpr("tokens"), [args[0]]);
			case "T" when args.Count == 1:
				return new MethodCallExpr(args[0], "trim", []);
			case "K" when args.Count == 1:
				return new CallExpr("char", [args[0]]);
			case "W" when args.Count == 1:
				return new MemberExpr(new IndexExpr(new MemberExpr(new NameExpr("player"), "weapons"), [args[0]]), "image");
			case "w":
				return args.Count == 0
					? new MemberExpr(new MemberExpr(new NameExpr("player"), "weapon"), "name")
					: new MemberExpr(new IndexExpr(new MemberExpr(new NameExpr("player"), "weapons"), [args[0]]), "name");
			case "P" when args.Count == 1:
				return new IndexExpr(new MemberExpr(new NameExpr("player"), "attr"), [args[0]]);
			// #P(n,i): player i's attribute n; #P(n,-1): the NPC's own.
			case "P" when args.Count == 2 && IsSelf(args[1]):
				return new IndexExpr(new NameExpr("attr"), [args[0]]);
			case "P" when args.Count == 2:
				return new IndexExpr(new MemberExpr(new IndexExpr(new NameExpr("players"), [args[1]]), "attr"), [args[0]]);
		}
		if (args.Count > 1) return null;
		return Attribute(code, args.Count == 1 ? args[0] : null, forPlayer: true);
	}

	/// <summary>
	///     A player attribute code as GS2: the player's (no index), the NPC's own (index -1), or players[index]'s. With
	///     <paramref name="forPlayer" /> false (setcharprop), no index means the NPC's own.
	/// </summary>
	public static Expr? Attribute(string code, Expr? index, bool forPlayer)
	{
		Expr Member(Expr? owner, string name) => owner == null ? new NameExpr(name) : new MemberExpr(owner, name);
		Expr Indexed(Expr? owner, string name, int i) => new IndexExpr(Member(owner, name), [new NumberExpr(i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
		var self = index != null && IsSelf(index) || !forPlayer && index == null;
		Expr? owner = self ? null : index == null ? new NameExpr("player") : new IndexExpr(new NameExpr("players"), [index]);
		if (Attributes.TryGetValue(code, out var name))
		{
			// The NPC's own ani is this.ani; GS1's bow image is always the player's.
			if (code == "m" && self && index != null) return new MemberExpr(new NameExpr("this"), "ani");
			if (code == "7" && !forPlayer && index == null) return new MemberExpr(new NameExpr("player"), "bowimg");
			return Member(owner, name);
		}
		if (code.Length > 1 && code[0] == 'C') return Indexed(owner, "colors", code[1] - '0');
		if (code.Length > 1 && code[0] == 'P') return Indexed(owner, "attr", int.Parse(code[1..], System.Globalization.CultureInfo.InvariantCulture));
		return null;
	}

	private static bool IsSelf(Expr index) => index is UnaryExpr { Op: "-", Operand: NumberExpr { Text: "1" } } or NumberExpr { Text: "-1" };

	// ------------------------------------------------------------ texts and variables

	/// <summary>A text argument from its pieces (literal texts and codes): one value, joined with @.</summary>
	public Expr Text(List<Expr> pieces)
	{
		if (pieces.Count == 0) return new StringExpr("");
		// Literal pieces next to each other are one text.
		for (var i = pieces.Count - 1; i > 0; i--)
			if (pieces[i] is StringExpr b && pieces[i - 1] is StringExpr a)
			{
				pieces[i - 1] = new StringExpr(a.Value + b.Value);
				pieces.RemoveAt(i);
			}
		var e = pieces[0];
		for (var i = 1; i < pieces.Count; i++) e = new BinaryExpr(e, "@", pieces[i]);
		return e;
	}

	/// <summary>A variable named by a text argument: makevar of the name (the converter writes a plain name as the variable).</summary>
	public Expr VariableReference(List<Expr> pieces)
	{
		if (pieces.Count == 1 && pieces[0] is not StringExpr) return new CallExpr("makevar", [pieces[0]]);
		return new CallExpr("makevar", [Text(pieces)]);
	}

	private static Expr Var(Expr reference) => reference is CallExpr { Name: "makevar", Args: [StringExpr { Value: var path }] } && (PathExpr(path) ?? Element(path)) is { } p ? p : reference;

	// this.list[this.i]: the element the text names (a path with indices).
	private static Expr? Element(string text)
	{
		if (!text.Contains('[', StringComparison.Ordinal)) return null;
		var read = Parser.Parse(text + ";", Dialect.Gs2);
		return read.Script is { Items: [StatementItem { Statement: ExprStmt { Expression: IndexExpr { Target: MemberExpr or NameExpr } e } }] } && IsPlainPath(e) ? e : null;
	}

	private static bool IsPlainPath(Expr e) => e switch
	{
		NameExpr or NumberExpr => true,
		MemberExpr m => IsPlainPath(m.Target),
		IndexExpr i => IsPlainPath(i.Target) && i.Indices.All(IsPlainPath),
		_ => false,
	};

	/// <summary>this.a.b as a GS2 expression; null when the text is not such a name.</summary>
	public static Expr? PathExpr(string path)
	{
		var parts = path.Split('.');
		if (parts.Any(p => p.Length == 0 || !(IsIdentifier(p) || p.All(char.IsAsciiDigit)))) return null;
		if (!IsIdentifier(parts[0]) || parts.Length == 1) return null;
		Expr e = new NameExpr(parts[0]);
		foreach (var p in parts.Skip(1)) e = new MemberExpr(e, p);
		return e;
	}

	private static bool IsIdentifier(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

	// A value of a text argument that sets something: a lone code is made text with @.
	private static Expr AsText(Expr value) => value is StringExpr || value is BinaryExpr { Op: "@" } ? value : new UnaryExpr("@", value);

	private static Expr Length(Expr list) => new CallExpr("arraylen", [list]);

	// ------------------------------------------------------------ names

	/// <summary>The GS2 of a GS1 predefined name (playerx is player.x, playerscount the number of players), or null.</summary>
	public Expr? LowerName(string name, bool counts = true)
	{
		if (counts && Counts.TryGetValue(name, out var count)) return count();
		if (name.Length > 6 && name.StartsWith("player", StringComparison.OrdinalIgnoreCase) && PlayerVariables.Contains(name[6..]))
			return new MemberExpr(new NameExpr("player"), name[6..].ToLowerInvariant());
		return null;
	}

	// ------------------------------------------------------------ lowering

	/// <summary>The GS2 statement a GS1 command means.</summary>
	public Stmt LowerCommand(string name, List<Expr> args, bool inFunction)
	{
		name = name.ToLowerInvariant();
		Stmt Call(string function, List<Expr> a) => new ExprStmt(new CallExpr(function, a));
		Stmt Assign(Expr target, Expr value) => new ExprStmt(new BinaryExpr(target, "=", value));
		Stmt Method(Expr target, string method, List<Expr> a) => new ExprStmt(new MethodCallExpr(target, method, a));
		if (args.Count == 0 && _commands.TryGetValue(name, out var kinds) && kinds.StartsWith('T')) args = [new StringExpr("")];
		// setstring x; and setplayerprop #c; set the empty text.
		if (args.Count == 1 && name is "setstring" or "setplayerprop" or "setcharprop") args = [args[0], new StringExpr("")];
		switch (name)
		{
			case "setplayerx" or "setplayery" when args.Count == 1:
				return Assign(new MemberExpr(new NameExpr("player"), name[^1..]), args[0]);
			case "setx" or "sety" when args.Count == 1:
				return Assign(new MemberExpr(new NameExpr("this"), name[^1..]), args[0]);
			// Commands GS2 has as the player's members.
			case "setbody" when args.Count == 1:
				return Assign(new MemberExpr(new NameExpr("player"), "bodyimg"), AsText(args[0]));
			case "setplayerdir" when args.Count == 1:
				return Assign(new MemberExpr(new NameExpr("player"), "dir"), args[0]);
			case "setlevel" when args.Count == 1:
				return Method(new NameExpr("player"), "setlevel2", [args[0], new MemberExpr(new NameExpr("player"), "x"), new MemberExpr(new NameExpr("player"), "y")]);
			// GS2's setmap(levelnames.txt, image, x, y): GS1 scripts wrote the image first.
			case "setmap" or "setminimap" when args.Count >= 2 && !EndsWithTxt(args[0]):
				return Call(name, [args[1], args[0], .. args.Skip(2)]);
			// #P(n): the attribute n.
			case "setplayerprop" or "setcharprop" when args.Count == 2 && args[0] is Gs1CodeExpr { Code: "P", Args: [var number] }:
				return Assign(new IndexExpr(name == "setplayerprop" ? new MemberExpr(new NameExpr("player"), "attr") : new NameExpr("attr"), [number]), AsText(args[1]));
			case "setplayerprop" or "setcharprop" when args.Count == 2 && args[0] is Gs1CodeExpr code && Attribute(code.Code, code.Args.FirstOrDefault(), name == "setplayerprop") is { } target:
				return Assign(target, AsText(args[1]));
			case "setplayerprop" or "setcharprop" when args.Count == 2 && args[0] is Gs1CodeExpr code:
				return Call(name, [new StringExpr("#" + code.Code), args[1]]);
			case "setstring" when args.Count == 2:
				return Assign(Var(args[0]), AsText(args[1]));
			case "set" when args.Count == 1:
				return Assign(Var(args[0]), new NumberExpr("1"));
			case "unset" when args.Count == 1:
				return Assign(Var(args[0]), new StringExpr(""));
			case "toggle" when args.Count == 1:
				return Assign(Var(args[0]), new UnaryExpr("!", Var(args[0])));
			case "tokenize" when args.Count == 1:
				return Assign(new NameExpr("tokens"), new MethodCallExpr(args[0], "tokenize", []));
			case "tokenize2" when args.Count == 2:
				return Assign(new NameExpr("tokens"), new MethodCallExpr(args[1], "tokenize",
					[args[0] is StringExpr separators ? new StringExpr(" ," + separators.Value) : new BinaryExpr(new StringExpr(" ,"), "@", args[0])]));
			// An array's name is a variable of the script (not a player's flag).
			case "setarray" when args.Count == 2:
				return Call("setarray", [args[0] is CallExpr { Name: "makevar", Args: [StringExpr { Value: var array }] } && (PathExpr(array) ?? Element(array) ?? (IsIdentifier(array) ? new NameExpr(array) : null)) is { } plain ? plain : Var(args[0]), args[1]]);
			case "addstring" when args.Count == 2:
				return Method(Var(args[0]), "add", [AsText(args[1])]);
			case "removestring" when args.Count == 2:
				return Method(Var(args[0]), "remove", [AsText(args[1])]);
			case "deletestring" when args.Count == 2:
				return Method(Var(args[0]), "delete", [new CallExpr("float", [args[1]])]);
			case "insertstring" when args.Count == 3:
				return Method(Var(args[0]), "insert", [new CallExpr("float", [args[1]]), AsText(args[2])]);
			case "replacestring" when args.Count == 3:
				return Method(Var(args[0]), "replace", [new CallExpr("float", [args[1]]), AsText(args[2])]);
		}
		return Call(Renamed.GetValueOrDefault(name, name), args);
	}

	private static bool EndsWithTxt(Expr e) => e is StringExpr { Value: var v } && v.TrimEnd().EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

	/// <summary>The text of "quoted text" (a quoted string in a text argument), or null.</summary>
	public static string? Unquoted(string text)
	{
		var t = text.Trim();
		return t.Length >= 2 && t[0] == t[^1] && t[0] is '"' or '\'' && t.IndexOf(t[0], 1) == t.Length - 1 ? t[1..^1] : null;
	}

	/// <summary>A direction argument: up, left, down, right as 0-3; anything else as written.</summary>
	public static Expr? Direction(string word) => Directions.TryGetValue(word.Trim(), out var d) ? new NumberExpr(d.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;

	/// <summary>The GS2 a GS1 function with text arguments means.</summary>
	public Expr LowerFunction(string name, List<Expr> args)
	{
		switch (name.ToLowerInvariant())
		{
			// The player's chat is the text (case ignored): playersays("Buy") or playersays(buy).
			case "playersays" when args.Count == 1:
				return new CallExpr("strequals", [new MemberExpr(new NameExpr("player"), "chat"), args[0] is StringExpr q && Unquoted(q.Value) is { } quoted ? new StringExpr(quoted) : AsText(args[0])]);
			case "ascii" when args.Count == 1:
				return new CallExpr("getascii", args);
			case "keycode" when args.Count == 1:
				return new CallExpr("getkeycode", [AsText(args[0])]);
			case "getflagkeys" when args.Count == 1:
				return new CallExpr("getstringkeys", args);
			case "strequals" when args.Count == 2:
				return new CallExpr("strequals", [AsText(args[0]), AsText(args[1])]);
			case "strcontains" when args.Count == 2:
				return new CallExpr("contains", args);
			case "startswith" when args.Count == 2:
				return new MethodCallExpr(args[1], "starts", [args[0]]);
			case "strlen" when args.Count == 1:
				return new MethodCallExpr(args[0], "length", []);
			case "strtofloat" when args.Count == 1:
				return new CallExpr("float", args);
			case "lindexof" when args.Count == 2:
				return new MethodCallExpr(Var(args[1]), "index", [args[0]]);
			case "sarraylen" when args.Count == 1:
				return Length(Var(args[0]));
			case "imgwidth" when args.Count == 1:
				return new CallExpr("getimgwidth", args);
			case "imgheight" when args.Count == 1:
				return new CallExpr("getimgheight", args);
			// The weapon the player has (the converter writes player.findweapon on the server).
			case "hasweapon" when args.Count == 1:
				return new BinaryExpr(new CallExpr("findweapon", args), "!=", new NullExpr());
			case "indexof" when args.Count == 2:
				return new MethodCallExpr(args[1], "pos", [args[0]]);
			case "getnpc" when args.Count == 1:
				return new CallExpr("findnpc", args);
			case "getplayer" when args.Count == 1:
				return new CallExpr("findplayer", args);
			case "onmapx" or "onmapy" when args.Count == 1:
				return new CallExpr(name.ToLowerInvariant() == "onmapx" ? "getmapx" : "getmapy", args);
		}
		return new CallExpr(name, args);
	}
}
