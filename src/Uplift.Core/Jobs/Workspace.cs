using Uplift.Formats;
using Uplift.Plugins;
using Uplift.Rules;
using Uplift.Server;

namespace Uplift.Jobs;

/// <summary>An opened server: its folder, layout, settings and what Uplift can do for it (built-ins and plugins).</summary>
public sealed class Workspace
{
	private Workspace(string root, UpliftConfig config, ServerLayout layout, UpliftRegistry registry)
	{
		Root = root;
		Config = config;
		Layout = layout;
		Registry = registry;
	}

	public string Root { get; }

	public UpliftConfig Config { get; }

	public ServerLayout Layout { get; }

	public UpliftRegistry Registry { get; }

	/// <summary>The GS1 commands and functions: the standard ones, the plugins' and the server's own (uplift.json).</summary>
	public Syntax.Gs1Catalog Catalog => _catalog ??= new Syntax.Gs1Catalog(
		Registry.Gs1Commands.Concat(Config.Gs1Commands ?? []), Registry.Gs1Functions.Concat(Config.Gs1Functions ?? []));

	private Syntax.Gs1Catalog? _catalog;

	/// <summary>The folder of the server's hand conversions, or null.</summary>
	public string? OverridesFolder => Config.Overrides is { Length: > 0 } o ? Path.GetFullPath(Path.Combine(Root, o)) : null;

	/// <summary>
	///     Opens a server folder: reads its uplift.json, loads the plugins of <paramref name="pluginFolders" /> and the ones the server
	///     names, and adds the server's own text rules.
	/// </summary>
	public static Workspace Open(string root, IEnumerable<string>? pluginFolders = null)
	{
		root = Path.GetFullPath(root);
		var config = UpliftConfig.Load(root);
		var registry = UpliftRegistry.CreateDefault();
		var plugins = (pluginFolders ?? []).SelectMany(UpliftRegistry.PluginFiles).ToList();
		plugins.AddRange((config.Plugins ?? []).Select(p => Path.IsPathRooted(p) ? p : Path.Combine(root, p)));
		registry.LoadPlugins(plugins);
		foreach (var rule in config.TextRules ?? [])
		{
			if (rule.Id.Length == 0 || rule.Pattern.Length == 0) continue;
			var sides = rule.Side?.ToLowerInvariant() switch
			{
				"server" => new HashSet<ScriptSide> { ScriptSide.Server },
				"client" => new HashSet<ScriptSide> { ScriptSide.Client },
				_ => null,
			};
			HashSet<ScriptKind>? kinds = null;
			if (rule.Kinds is { Count: > 0 })
			{
				kinds = [];
				foreach (var k in rule.Kinds)
					if (ScriptKinds.TryParse(k, out var kind)) kinds.Add(kind);
			}
			registry.AddTextFix(new RegexTextFix(rule.Id, rule.Title ?? rule.Id, rule.Description ?? $"{rule.Pattern} → {rule.Replacement}", rule.Pattern, rule.Replacement, rule.IgnoreCase, sides, kinds));
		}
		foreach (var (flag, function) in config.Events ?? []) registry.AddEvent(flag, function);
		return new Workspace(root, config, new ServerLayout(root, config), registry);
	}

	/// <summary>Whether a rule or text fix is on: the server's setting, else its default.</summary>
	public bool IsEnabledByDefault(string id, bool fallback) => Config.Rules != null && Config.Rules.TryGetValue(id, out var on) ? on : fallback;

	/// <summary>The ids of the rules and text fixes that are on by default for this server.</summary>
	public HashSet<string> DefaultSwitches() =>
		Registry.Rules.Where(r => IsEnabledByDefault(r.Id, r.EnabledByDefault)).Select(r => r.Id)
			.Concat(Registry.TextFixes.Where(f => IsEnabledByDefault(f.Id, f.EnabledByDefault)).Select(f => f.Id))
			.ToHashSet(StringComparer.Ordinal);

	/// <summary>Opens one file with the first format of its kind that claims it, or gives why it cannot.</summary>
	public (ScriptDocument? Document, string? Problem) OpenFile(ScriptKind kind, ServerFile file, FormatOptions options)
	{
		string text;
		try
		{
			text = ServerText.Read(file.FullPath);
		}
		catch (IOException e)
		{
			return (null, "cannot read: " + e.Message);
		}
		foreach (var format in Registry.Formats.Where(f => f.Kind == kind))
			if (format.Claims(file, text))
				return (format.Open(file, text, options), null);
		return (null, kind switch
		{
			ScriptKind.Weapon => "not a weapon file (no GRAWP001 first line)",
			ScriptKind.DbNpc => "not a database NPC file (no GRNPC001 first line)",
			ScriptKind.LevelNpc => "not a text level",
			_ => "no format reads it",
		});
	}

	/// <summary>Every file of the kinds, with its kind.</summary>
	public List<(ScriptKind Kind, ServerFile File)> Files(IEnumerable<ScriptKind> kinds) =>
		kinds.Distinct().SelectMany(k => Layout.Candidates(k).Select(f => (k, f))).ToList();

	/// <summary>The bare names the server's scripts assign (x = 1;): variables every script shares, which GS1 flags are not.</summary>
	public HashSet<string> SharedVariables(int parallelism, CancellationToken cancellation = default)
	{
		var found = new System.Collections.Concurrent.ConcurrentBag<string>();
		Workers.ForEach(Files(ScriptKinds.All), parallelism, f =>
		{
			var (document, _) = OpenFile(f.Kind, f.File, new FormatOptions());
			if (document == null) return;
			foreach (var slot in document.Slots)
				foreach (var (_, code) in ConversionEngine.Parts(f.Kind, slot.Source))
					foreach (var name in Conversion.Gs1Converter.AssignedNames(code, Catalog))
						found.Add(name);
		}, cancellation);
		return new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>The custom events of the server: the ones its scripts send by name (unless switched off) and the configured ones.</summary>
	public HashSet<string> CustomEvents(int parallelism, CancellationToken cancellation = default)
	{
		var names = new HashSet<string>(Config.CustomEvents ?? [], StringComparer.OrdinalIgnoreCase);
		if (Config.ScanCustomEvents == false) return names;
		var files = Files(ScriptKinds.All);
		var found = new System.Collections.Concurrent.ConcurrentBag<string>();
		Workers.ForEach(files, parallelism, f =>
		{
			try
			{
				foreach (var name in EventScanner.Find(ServerText.Read(f.File.FullPath))) found.Add(name);
			}
			catch (IOException)
			{
			}
		}, cancellation);
		names.UnionWith(found);
		return names;
	}
}
