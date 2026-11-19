namespace Uplift.Conversion;

/// <summary>
///     The GS1 event flags and the GS2 event function of each. A GS1 script's main code runs once for every event, with that event's
///     flag true; the converter writes the code of each event in its own function.
/// </summary>
public sealed class EventTable
{
	private static readonly Dictionary<string, string> Standard = new(StringComparer.OrdinalIgnoreCase)
	{
		["created"] = "onCreated", ["initialized"] = "onInitialized", ["istimeout"] = "onTimeout", ["timeout"] = "onTimeout",
		["playerenters"] = "onPlayerEnters", ["playerleaves"] = "onPlayerLeaves", ["playertouchsme"] = "onPlayerTouchsMe",
		["playertouchesme"] = "onPlayerTouchsMe", ["playertouchsother"] = "onPlayerTouchsOther", ["playertouchesother"] = "onPlayerTouchsOther",
		["playerchats"] = "onPlayerChats", ["playerhurt"] = "onPlayerHurt", ["playerhurted"] = "onPlayerHurt", ["playerdies"] = "onPlayerDies",
		["playerlaysitem"] = "onPlayerLaysItem", ["playerendsreading"] = "onPlayerEndsReading", ["compusdied"] = "onCompusDied",
		["emoticon"] = "onEmoticon", ["mousedown"] = "onMouseDown", ["mouseup"] = "onMouseUp", ["mousewheel"] = "onMouseWheel",
		["exploded"] = "onExploded", ["wasshot"] = "onWasShot", ["wasshooted"] = "onWasShot", ["waspelt"] = "onWasPelt",
		["keypressed"] = "onKeyPressed", ["washit"] = "onWasHit", ["shapetrigger"] = "onShapeTrigger", ["playanimation"] = "onPlayAnimation",
		["pkzonechanges"] = "onPkZoneChanges", ["weaponfired"] = "onWeaponFired", ["actionserverside"] = "onActionServerSide",
		["actionclientside"] = "onActionClientSide", ["wasthrown"] = "onWasThrown", ["movementfinished"] = "onMovementFinished",
	};

	// Misspellings of playerenters (never true in GS1): read as playerenters only with the misspelt-events rule.
	private static readonly Dictionary<string, string> Misspelt = new(StringComparer.OrdinalIgnoreCase)
	{
		["playerenterslevel"] = "playerenters", ["playereneterslevel"] = "playerenters", ["playereters"] = "playerenters",
	};

	private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["timeout"] = "istimeout", ["playertouchesme"] = "playertouchsme", ["playertouchesother"] = "playertouchsother",
		["playerhurted"] = "playerhurt", ["wasshooted"] = "wasshot",
	};

	private readonly Dictionary<string, string> _functions;
	private readonly Dictionary<string, string> _canon;

	/// <param name="customEvents">Events the server's scripts send by name (callnpc, callweapon, trigger, scheduleevent).</param>
	/// <param name="misspelt">Whether misspellings of playerenters are playerenters.</param>
	/// <param name="extra">More GS1 flags and their GS2 functions (plugins).</param>
	public EventTable(IEnumerable<string>? customEvents = null, bool misspelt = true, IEnumerable<KeyValuePair<string, string>>? extra = null)
	{
		_functions = new Dictionary<string, string>(Standard, StringComparer.OrdinalIgnoreCase);
		_canon = new Dictionary<string, string>(Aliases, StringComparer.OrdinalIgnoreCase);
		if (misspelt)
			foreach (var (word, meant) in Misspelt)
			{
				_functions[word] = Standard[meant];
				_canon[word] = meant;
			}
		if (extra != null)
			foreach (var (flag, function) in extra)
				_functions.TryAdd(flag, function);
		CustomEvents = new HashSet<string>(customEvents ?? [], StringComparer.OrdinalIgnoreCase);
	}

	public IReadOnlySet<string> CustomEvents { get; }

	/// <summary>Whether a word is a misspelling read as another event.</summary>
	public bool IsMisspelling(string word) => Misspelt.ContainsKey(word) && _canon.ContainsKey(word);

	public bool IsStandard(string name) => _functions.ContainsKey(name);

	/// <summary>Whether a word is a GS1 event flag: a known event, or action&lt;name&gt; (triggeraction).</summary>
	public bool IsEventFlag(string name) =>
		_functions.ContainsKey(name) || name.Length > 6 && name.StartsWith("action", StringComparison.OrdinalIgnoreCase) && Gs1Converter.Identifier.IsMatch(name);

	/// <summary>The one name of an event that has several (timeout and istimeout ...).</summary>
	public string Canon(string flag) => _canon.TryGetValue(flag, out var c) ? c : flag.ToLowerInvariant();

	public bool SameEvent(string a, string b) => Canon(a).Equals(Canon(b), StringComparison.OrdinalIgnoreCase);

	/// <summary>The GS2 function of an event: onCreated, onActionFoo, on&lt;Custom&gt;.</summary>
	public string Function(string flag)
	{
		if (_functions.TryGetValue(flag, out var f)) return f;
		if (flag.Length > 6 && flag.StartsWith("action", StringComparison.OrdinalIgnoreCase))
		{
			var rest = flag[6..];
			return "onAction" + char.ToUpperInvariant(rest[0]) + rest[1..];
		}
		return "on" + char.ToUpperInvariant(flag[0]) + flag[1..];
	}
}
