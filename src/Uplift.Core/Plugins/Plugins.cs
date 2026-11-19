using System.Reflection;
using Uplift.Formats;
using Uplift.Rules;

namespace Uplift.Plugins;

/// <summary>
///     A plugin: a .NET assembly with a public class that implements this interface (and has a public constructor without parameters).
///     Put it in the plugins folder beside Uplift, or name it in a server's uplift.json ("plugins").
/// </summary>
public interface IUpliftPlugin
{
	string Name { get; }

	void Register(UpliftRegistry registry);
}

/// <summary>Everything Uplift can do: the formats, rules, text fixes and events, built-in and from plugins.</summary>
public sealed class UpliftRegistry
{
	private readonly List<IScriptFormat> _formats = [];
	private readonly List<IConversionRule> _rules = [];
	private readonly List<ITextFix> _fixes = [];
	private readonly Dictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<IScriptFormat> Formats => _formats;

	public IReadOnlyList<IConversionRule> Rules => _rules;

	public IReadOnlyList<ITextFix> TextFixes => _fixes;

	public IReadOnlyDictionary<string, string> Events => _events;

	public Dictionary<string, string> Gs1Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, string> Gs1Functions { get; } = new(StringComparer.OrdinalIgnoreCase);

	public List<string> LoadedPlugins { get; } = [];

	public List<string> PluginErrors { get; } = [];

	public static UpliftRegistry CreateDefault()
	{
		var registry = new UpliftRegistry();
		registry._formats.AddRange(ScriptFormats.BuiltIn);
		registry._rules.AddRange(BuiltInRules.All);
		registry._fixes.AddRange(BuiltInTextFixes.All);
		return registry;
	}

	/// <summary>Adds a format. A plugin's formats are asked before the built-in ones, so a plugin can take over a kind of file.</summary>
	public void AddFormat(IScriptFormat format) => _formats.Insert(_formats.Count(f => !ScriptFormats.BuiltIn.Contains(f)), format);

	public void AddRule(IConversionRule rule)
	{
		_rules.RemoveAll(r => r.Id == rule.Id);
		_rules.Add(rule);
	}

	public void AddTextFix(ITextFix fix)
	{
		_fixes.RemoveAll(f => f.Id == fix.Id);
		_fixes.Add(fix);
	}

	/// <summary>Adds a GS1 command (or changes one) and its argument kinds: T text, N number, V variable, D direction.</summary>
	public void AddGs1Command(string name, string argumentKinds) => Gs1Commands[name] = argumentKinds;

	/// <summary>Adds a GS1 function with text or variable arguments.</summary>
	public void AddGs1Function(string name, string argumentKinds) => Gs1Functions[name] = argumentKinds;

	/// <summary>Adds a GS1 event flag and its GS2 function.</summary>
	public void AddEvent(string gs1Flag, string gs2Function) => _events[gs1Flag] = gs2Function;

	/// <summary>Loads plugin assemblies and lets each plugin register itself; failures go to <see cref="PluginErrors" />.</summary>
	public void LoadPlugins(IEnumerable<string> paths)
	{
		foreach (var path in paths)
		{
			try
			{
				var full = Path.GetFullPath(path);
				var assembly = Assembly.LoadFrom(full);
				var found = false;
				foreach (var type in assembly.GetExportedTypes().Where(t => typeof(IUpliftPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
				{
					var plugin = (IUpliftPlugin)Activator.CreateInstance(type)!;
					plugin.Register(this);
					LoadedPlugins.Add($"{plugin.Name} ({Path.GetFileName(full)})");
					found = true;
				}
				if (!found) PluginErrors.Add($"{path}: no public class implements IUpliftPlugin");
			}
			catch (Exception e)
			{
				PluginErrors.Add($"{path}: {e.GetType().Name}: {e.Message}");
			}
		}
	}

	/// <summary>The plugin assemblies of a folder (*.dll).</summary>
	public static IEnumerable<string> PluginFiles(string folder) =>
		Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.dll").Order(StringComparer.Ordinal) : [];
}
