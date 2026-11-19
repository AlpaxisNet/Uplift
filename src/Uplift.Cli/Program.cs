// uplift-cli: converts a Graal server's GS1 scripts to GS2 (database NPCs, weapons, classes, level NPCs, ganis).
using System.Text;
using Uplift;
using Uplift.Conversion;
using Uplift.Formats;
using Uplift.Jobs;
using Uplift.Server;
using Uplift.Verification;

namespace Uplift.Cli;

public static class Program
{
	private const string Usage = """
		Uplift: converts a Graal server's GS1 scripts to GS2.

		  uplift-cli scan <server> [--only KINDS] [--list]
		      What is GS1, GS2 or broken, per kind (--list: every file that needs work).
		  uplift-cli convert <server> (--out <folder> | --in-place) [options]
		      Converts and writes the files, with a report (parts.tsv, files.tsv, warnings.txt, summary.json).
		        --only KINDS          dbnpcs,weapons,classes,levels,ganis (default: all)
		        --sides SIDES         server,client (default: both)
		        --enable IDS          switch rules or text fixes on (see `uplift-cli rules`)
		        --disable IDS         switch rules or text fixes off
		        --files @LIST         only the files of a list (paths relative to the server, one per line)
		        --copy-unchanged      with --out: copy the files that need no change too (a complete copy)
		        --no-repair           leave missing NPCEND / GLEVNW01 / SCRIPTEND as they are
		        --no-fix-gs2          run text fixes on converted parts only (not on parts that were GS2)
		        --report <folder>     where the report goes (default: <out>.report, or <server>/.uplift/<time>/report)
		        --dry-run             convert and report, write no script file
		  uplift-cli check <server> [--only KINDS] [--quiet]
		      The strict GS2 check of every part (exit 1 when a part is not strict GS2).
		  uplift-cli part <file> [--kind KIND] [--side server|client]
		      Converts one part (the whole file) and prints it.
		  uplift-cli rules [<server>]      The rules and text fixes, and whether each is on.
		  uplift-cli events <server>       The custom events the server's scripts send by name.
		  uplift-cli init <server>         Writes a starter uplift.json into the server's folder.

		  Every command: --jobs N (worker threads), --plugins <folder> (more plugin folders; the plugins folder beside Uplift is always read).
		""";

	public static int Main(string[] args)
	{
		var code = 0;
		var thread = new Thread(() => code = Run(args), Workers.StackSize);
		thread.Start();
		thread.Join();
		return code;
	}

	private static int Run(string[] argv)
	{
		var args = new Args(argv);
		try
		{
			return args.Command switch
			{
				"scan" => Scan(args),
				"convert" => ConvertCommand(args),
				"check" => Check(args),
				"part" => Part(args),
				"rules" => Rules(args),
				"events" => Events(args),
				"init" => Init(args),
				"help" or "--help" or "-h" or null => Help(),
				_ => Fail("unknown command: " + args.Command),
			};
		}
		catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
		{
			return Fail(e.Message);
		}
	}

	private static int Help()
	{
		Console.WriteLine(Usage);
		return 0;
	}

	private static int Fail(string message)
	{
		Console.Error.WriteLine("uplift-cli: " + message);
		return 2;
	}

	// ------------------------------------------------------------ commands

	private static Workspace Open(Args args)
	{
		var root = args.Positional(0) ?? throw new ArgumentException("name the server's folder");
		if (!Directory.Exists(root)) throw new ArgumentException("no such folder: " + root);
		var workspace = Workspace.Open(root, PluginFolders(args));
		if (!workspace.Layout.LooksLikeServer())
			throw new ArgumentException($"{root} has none of the folders {string.Join(", ", ScriptKinds.All.Select(k => workspace.Layout.Folder(k) + "/").Distinct())} (set them in uplift.json)");
		foreach (var error in workspace.Registry.PluginErrors) Console.Error.WriteLine("plugin: " + error);
		return workspace;
	}

	// The plugins folder beside the program, and the ones given with --plugins.
	private static List<string> PluginFolders(Args args) => [Path.Combine(AppContext.BaseDirectory, "plugins"), .. args.Values("--plugins")];

	private static HashSet<ScriptKind> Kinds(Args args)
	{
		if (args.Value("--only") is not { } only) return ScriptKinds.All.ToHashSet();
		var kinds = new HashSet<ScriptKind>();
		foreach (var word in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			kinds.Add(ScriptKinds.TryParse(word, out var k) ? k : throw new ArgumentException("unknown kind: " + word + " (dbnpcs, weapons, classes, levels, ganis)"));
		return kinds;
	}

	private static int Jobs(Args args) => args.Value("--jobs") is { } j && int.TryParse(j, out var n) && n > 0 ? n : Workers.DefaultParallelism;

	private static int Scan(Args args)
	{
		var workspace = Open(args);
		var scan = ConversionRun.Scan(workspace, Kinds(args), Jobs(args), new ConsoleProgress());
		ConsoleProgress.Clear();
		Console.WriteLine($"{workspace.Root} (scanned in {scan.Elapsed.TotalSeconds:0.0} s)");
		Console.WriteLine($"{"",-15} {"files",7} {"GS1",7} {"GS2",7} {"broken",7}   parts: {"GS1",7} {"GS2",7} {"broken",7}");
		foreach (var kind in ScriptKinds.All)
		{
			var files = scan.OfKind(kind).ToList();
			if (files.Count == 0) continue;
			Console.WriteLine($"{kind.Title(),-15} {files.Count,7} {files.Count(f => f.State == FileState.NeedsConversion),7} {files.Count(f => f.State == FileState.Gs2),7} {files.Count(f => f.State is FileState.HasBroken or FileState.Unreadable),7}" +
			                  $"   parts: {files.Sum(f => f.Gs1),7} {files.Sum(f => f.Gs2),7} {files.Sum(f => f.Broken),7}");
		}
		if (args.Has("--list"))
			foreach (var f in scan.Files.Where(f => f.State is FileState.NeedsConversion or FileState.HasBroken or FileState.Unreadable))
			{
				Console.WriteLine($"{f.State,-16} {f.File.RelativePath}  (GS1 {f.Gs1}, GS2 {f.Gs2}, broken {f.Broken})");
				foreach (var p in f.Problems) Console.WriteLine("    problem: " + p);
				foreach (var p in f.Parts.Where(p => p.State == PartState.Broken)) Console.WriteLine($"    broken {p.Side.ToString().ToLowerInvariant()} part of {p.SlotLabel}: {p.Detail}");
			}
		return 0;
	}

	private static int ConvertCommand(Args args)
	{
		var workspace = Open(args);
		var inPlace = args.Has("--in-place");
		var output = args.Value("--out");
		var dryRun = args.Has("--dry-run");
		if (inPlace == (output != null) && !dryRun) throw new ArgumentException("give --out <folder> or --in-place");
		var switches = workspace.DefaultSwitches();
		var known = workspace.Registry.Rules.Select(r => r.Id).Concat(workspace.Registry.TextFixes.Select(f => f.Id)).ToHashSet();
		foreach (var id in args.List("--enable")) switches.Add(known.Contains(id) ? id : throw new ArgumentException("unknown rule or fix: " + id));
		foreach (var id in args.List("--disable")) switches.Remove(known.Contains(id) ? id : throw new ArgumentException("unknown rule or fix: " + id));
		var sides = args.Value("--sides") is { } s
			? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant() switch
			{
				"server" => ScriptSide.Server,
				"client" => ScriptSide.Client,
				_ => throw new ArgumentException("unknown side: " + w),
			}).ToHashSet()
			: [ScriptSide.Server, ScriptSide.Client];
		HashSet<string>? only = null;
		if (args.Value("--files") is { } list)
			only = File.ReadAllLines(list.TrimStart('@')).Select(l => l.Trim().Replace('\\', '/')).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
		var options = new ConvertOptions
		{
			Kinds = Kinds(args),
			Sides = sides,
			Switches = switches,
			FixAlreadyGs2 = !args.Has("--no-fix-gs2"),
			Repair = !args.Has("--no-repair"),
			Output = inPlace ? OutputMode.InPlace : OutputMode.Folder,
			OutputFolder = output,
			CopyUnchanged = args.Has("--copy-unchanged"),
			ReportFolder = args.Value("--report"),
			Parallelism = Jobs(args),
			DryRun = dryRun,
			Only = only,
		};
		var report = ConversionRun.Run(workspace, options, new ConsoleProgress());
		ConsoleProgress.Clear();
		PrintReport(report);
		return 0;
	}

	private static void PrintReport(ConversionReport report)
	{
		Console.WriteLine($"{report.Files.Count} files, {report.FilesChanged} changed{(report.DryRun ? " (dry run: nothing written)" : "")}, in {report.Elapsed.TotalSeconds:0.0} s");
		Console.WriteLine($"parts: {report.Count(PartStatus.Converted)} converted, {report.Count(PartStatus.AlreadyGs2)} already GS2, {report.Count(PartStatus.Kept)} kept, {report.Count(PartStatus.Failed)} not converted");
		Console.WriteLine($"after: {report.CountAfter(CheckStatus.Ok)} strict GS2, {report.CountAfter(CheckStatus.Gs1)} GS1, {report.CountAfter(CheckStatus.NotGs2)} not GS2");
		var unreadable = report.Files.Count(f => f.Unreadable != null);
		if (unreadable > 0) Console.WriteLine($"{unreadable} files could not be read as their kind (see files.tsv)");
		if (report.Warnings.Count > 0) Console.WriteLine($"{report.Warnings.Count} warnings (warnings.txt)");
		if (report.OutputFolder != null && !report.DryRun) Console.WriteLine("written to " + report.OutputFolder);
		if (report.BackupFolder != null) Console.WriteLine("originals backed up in " + report.BackupFolder);
		Console.WriteLine("report: " + report.ReportFolder);
	}

	private static int Check(Args args)
	{
		var workspace = Open(args);
		var quiet = args.Has("--quiet");
		var engine = new ConversionEngine(workspace, new ConvertOptions(), []);
		var files = workspace.Files(Kinds(args));
		var lines = new List<string>[files.Count];
		int ok = 0, gs1 = 0, fail = 0;
		Workers.ForEach(files.Select((f, i) => (f, i)).ToList(), Jobs(args), item =>
		{
			var output = new List<string>();
			var (document, problem) = workspace.OpenFile(item.f.Kind, item.f.File, new FormatOptions { Repair = false });
			if (document == null)
			{
				output.Add($"SKIP\t{item.f.File.RelativePath}\t-\t{problem}");
				lines[item.i] = output;
				return;
			}
			foreach (var p in document.Problems) output.Add($"PROBLEM\t{item.f.File.RelativePath}\t-\t{p}");
			foreach (var slot in document.Slots)
				foreach (var (side, code) in ConversionEngine.Parts(item.f.Kind, slot.Source))
				{
					var result = engine.Check(code, item.f.Kind);
					var label = $"{slot.Label}\t{side.ToString().ToLowerInvariant()}";
					switch (result.Status)
					{
						case CheckStatus.Ok:
							Interlocked.Increment(ref ok);
							if (!quiet) output.Add($"OK\t{label}");
							break;
						case CheckStatus.Gs1:
							Interlocked.Increment(ref gs1);
							output.Add($"GS1\t{label}\t{result.Detail}");
							break;
						default:
							Interlocked.Increment(ref fail);
							output.Add($"FAIL\t{label}\t{result.Detail}");
							break;
					}
				}
			lines[item.i] = output;
		});
		foreach (var l in lines.SelectMany(l => l)) Console.WriteLine(l);
		Console.WriteLine($"# parts: {ok} ok, {fail} fail, {gs1} gs1");
		return fail + gs1 > 0 ? 1 : 0;
	}

	private static int Part(Args args)
	{
		var path = args.Positional(0) ?? throw new ArgumentException("name a file");
		var kind = args.Value("--kind") is { } k ? ScriptKinds.TryParse(k, out var parsed) ? parsed : throw new ArgumentException("unknown kind: " + k) : ScriptKind.LevelNpc;
		var side = args.Value("--side")?.ToLowerInvariant() == "client" || kind.IsClientOnly() ? ScriptSide.Client : ScriptSide.Server;
		var code = ServerText.Read(path).Replace("\r", "", StringComparison.Ordinal);
		var result = new Gs1Converter().Convert(code, kind, side);
		Console.Out.Flush();
		Console.Error.WriteLine($"== {result.Status}{(result.Reason.Length > 0 ? ": " + result.Reason : "")}");
		foreach (var note in result.Notes) Console.Error.WriteLine("   note: " + note);
		if (result.Status == PartStatus.Converted) Console.Write(result.Text);
		return result.Status == PartStatus.Failed ? 1 : 0;
	}

	private static int Rules(Args args)
	{
		Workspace? workspace = null;
		if (args.Positional(0) != null) workspace = Open(args);
		var registry = workspace?.Registry;
		if (registry == null)
		{
			registry = Plugins.UpliftRegistry.CreateDefault();
			registry.LoadPlugins(PluginFolders(args).SelectMany(Plugins.UpliftRegistry.PluginFiles));
			foreach (var error in registry.PluginErrors) Console.Error.WriteLine("plugin: " + error);
		}
		Console.WriteLine("Rules (rewrites of the GS1 tree):");
		foreach (var r in registry.Rules)
			Console.WriteLine($"  [{(workspace?.IsEnabledByDefault(r.Id, r.EnabledByDefault) ?? r.EnabledByDefault ? "on " : "off")}] {r.Id,-22} {r.Title}\n{Wrap(r.Description, 8)}");
		Console.WriteLine("Text fixes (regular expressions on GS2 code, never in strings or comments):");
		foreach (var f in registry.TextFixes)
			Console.WriteLine($"  [{(workspace?.IsEnabledByDefault(f.Id, f.EnabledByDefault) ?? f.EnabledByDefault ? "on " : "off")}] {f.Id,-22} {f.Title}\n{Wrap(f.Description, 8)}");
		if (registry.LoadedPlugins.Count > 0) Console.WriteLine("Plugins: " + string.Join(", ", registry.LoadedPlugins));
		return 0;
	}

	private static string Wrap(string text, int indent)
	{
		var sb = new StringBuilder();
		var line = new StringBuilder();
		foreach (var word in text.Split(' '))
		{
			if (line.Length + word.Length > 110 - indent)
			{
				sb.Append(new string(' ', indent)).Append(line.ToString().TrimEnd()).Append('\n');
				line.Clear();
			}
			line.Append(word).Append(' ');
		}
		if (line.Length > 0) sb.Append(new string(' ', indent)).Append(line.ToString().TrimEnd());
		return sb.ToString();
	}

	private static int Events(Args args)
	{
		var workspace = Open(args);
		foreach (var name in workspace.CustomEvents(Jobs(args)).Order(StringComparer.Ordinal)) Console.WriteLine(name);
		return 0;
	}

	private static int Init(Args args)
	{
		var root = args.Positional(0) ?? throw new ArgumentException("name the server's folder");
		var path = Path.Combine(root, UpliftConfig.FileName);
		if (File.Exists(path)) throw new ArgumentException(path + " exists already");
		var registry = Plugins.UpliftRegistry.CreateDefault();
		var config = new UpliftConfig
		{
			Folders = new Dictionary<string, string> { ["dbnpcs"] = "npcs", ["weapons"] = "weapons", ["classes"] = "scripts", ["levels"] = "levels" },
			CustomEvents = [],
			ScanCustomEvents = true,
			Rules = registry.Rules.Select(r => (r.Id, r.EnabledByDefault)).Concat(registry.TextFixes.Select(f => (f.Id, f.EnabledByDefault))).ToDictionary(p => p.Id, p => p.EnabledByDefault),
			TextRules = [new TextRuleConfig { Id = "example-rename", Title = "Example: oldfunction( → newfunction(", Pattern = @"\boldfunction\s*\(", Replacement = "newfunction(", Side = "both" }],
			Plugins = [],
			Overrides = ".uplift/overrides",
		};
		File.WriteAllText(path, config.ToJson() + "\n");
		Console.WriteLine("wrote " + path);
		return 0;
	}
}

/// <summary>The command line: a command, positional words and --options (with or without a value).</summary>
internal sealed class Args
{
	private static readonly HashSet<string> Flags = ["--list", "--in-place", "--copy-unchanged", "--no-repair", "--no-fix-gs2", "--dry-run", "--quiet"];
	private readonly List<string> _positional = [];
	private readonly List<(string Name, string? Value)> _options = [];

	public Args(string[] args)
	{
		Command = args.Length > 0 ? args[0] : null;
		for (var i = 1; i < args.Length; i++)
		{
			var a = args[i];
			if (a.StartsWith("--", StringComparison.Ordinal))
			{
				if (a.Contains('=')) _options.Add((a[..a.IndexOf('=')], a[(a.IndexOf('=') + 1)..]));
				else if (Flags.Contains(a) || i + 1 >= args.Length) _options.Add((a, null));
				else _options.Add((a, args[++i]));
			}
			else _positional.Add(a);
		}
	}

	public string? Command { get; }

	public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;

	public bool Has(string name) => _options.Any(o => o.Name == name);

	public string? Value(string name) => _options.LastOrDefault(o => o.Name == name).Value;

	public IEnumerable<string> Values(string name) => _options.Where(o => o.Name == name && o.Value != null).Select(o => o.Value!);

	public IEnumerable<string> List(string name) => Values(name).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>Progress on one line of the error stream (when it is a terminal).</summary>
internal sealed class ConsoleProgress : IProgress<JobProgress>
{
	private static readonly bool Interactive = !Console.IsErrorRedirected;
	private string _phase = "";

	public void Report(JobProgress value)
	{
		if (!Interactive)
		{
			if (value.Phase != _phase) Console.Error.WriteLine(value.Phase + "...");
			_phase = value.Phase;
			return;
		}
		var text = value.Total > 1 ? $"{value.Phase} {value.Done}/{value.Total} {value.Current}" : value.Phase + "...";
		var width = Math.Max(20, Console.BufferWidth - 1);
		Console.Error.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
	}

	public static void Clear()
	{
		if (Interactive) Console.Error.Write("\r" + new string(' ', Math.Max(20, Console.BufferWidth - 1)) + "\r");
	}
}
