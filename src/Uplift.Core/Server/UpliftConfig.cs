using System.Text.Json;
using System.Text.Json.Serialization;

namespace Uplift.Server;

/// <summary>
///     A server's own settings: <c>uplift.json</c> in the server's folder (optional). Every setting has a default that fits a standard
///     Graal server layout.
/// </summary>
public sealed class UpliftConfig
{
	public const string FileName = "uplift.json";

	/// <summary>The folder of each kind, relative to the server (keys: dbnpcs, weapons, classes, levels).</summary>
	public Dictionary<string, string>? Folders { get; set; }

	/// <summary>Event names the scripts send by name, in addition to the ones found in the scripts.</summary>
	public List<string>? CustomEvents { get; set; }

	/// <summary>Whether to find custom event names in the scripts (callnpc, callweapon, trigger, scheduleevent). Default true.</summary>
	public bool? ScanCustomEvents { get; set; }

	/// <summary>Rules and text fixes switched on or off by id (the rest keep their default).</summary>
	public Dictionary<string, bool>? Rules { get; set; }

	/// <summary>The server's own regular-expression fixes of GS2 code.</summary>
	public List<TextRuleConfig>? TextRules { get; set; }

	/// <summary>Plugin assemblies (paths relative to the server's folder or absolute).</summary>
	public List<string>? Plugins { get; set; }

	/// <summary>A folder of hand conversions: &lt;hash&gt;.gs2 replaces the part of that hash, &lt;hash&gt;.keep leaves it as it is.</summary>
	public string? Overrides { get; set; }

	/// <summary>
	///     GS1 commands the standard set lacks, and the kind of each argument ("givegralats": "N"): T text, N number, V a variable's
	///     name, D a direction; a command's last text argument takes the rest of the statement.
	/// </summary>
	public Dictionary<string, string>? Gs1Commands { get; set; }

	/// <summary>GS1 functions with text or variable arguments the standard set lacks ("myfunc": "TN").</summary>
	public Dictionary<string, string>? Gs1Functions { get; set; }

	/// <summary>More GS1 event flags and their GS2 functions ("myevent": "onMyEvent").</summary>
	public Dictionary<string, string>? Events { get; set; }

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>The settings of a server folder (defaults when it has no uplift.json).</summary>
	public static UpliftConfig Load(string root)
	{
		var path = Path.Combine(root, FileName);
		if (!File.Exists(path)) return new UpliftConfig();
		try
		{
			return JsonSerializer.Deserialize<UpliftConfig>(File.ReadAllText(path), JsonOptions) ?? new UpliftConfig();
		}
		catch (JsonException e)
		{
			throw new InvalidDataException($"{path}: {e.Message}", e);
		}
	}

	public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>A regular-expression fix of GS2 code in uplift.json.</summary>
public sealed class TextRuleConfig
{
	public string Id { get; set; } = "";

	public string? Title { get; set; }

	public string? Description { get; set; }

	public string Pattern { get; set; } = "";

	public string Replacement { get; set; } = "";

	public bool IgnoreCase { get; set; }

	/// <summary>server, client or both (default).</summary>
	public string? Side { get; set; }

	/// <summary>The kinds it applies to (dbnpcs, weapons, classes, levels, ganis); all when empty.</summary>
	public List<string>? Kinds { get; set; }
}
