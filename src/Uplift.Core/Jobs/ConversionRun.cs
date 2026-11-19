using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Uplift.Conversion;
using Uplift.Formats;
using Uplift.Verification;

namespace Uplift.Jobs;

/// <summary>Scans and conversions of whole servers.</summary>
public static class ConversionRun
{
	/// <summary>Scans the files of the kinds: which parts are GS2, GS1 or broken.</summary>
	public static ServerScan Scan(Workspace workspace, IEnumerable<ScriptKind> kinds, int parallelism, IProgress<JobProgress>? progress = null, CancellationToken cancellation = default)
	{
		var clock = Stopwatch.StartNew();
		var engine = new ConversionEngine(workspace, new ConvertOptions(), []);
		var files = workspace.Files(kinds);
		var results = new FileScan[files.Count];
		var done = 0;
		progress?.Report(new JobProgress("Scanning", 0, files.Count));
		var indexed = files.Select((f, i) => (f.Kind, f.File, i)).ToList();
		Workers.ForEach(indexed, parallelism, item =>
		{
			results[item.i] = engine.ScanFile(item.Kind, item.File);
			var n = Interlocked.Increment(ref done);
			if (n % 25 == 0 || n == files.Count) progress?.Report(new JobProgress("Scanning", n, files.Count, item.File.RelativePath));
		}, cancellation);
		return new ServerScan { Root = workspace.Root, Files = results.ToList(), Elapsed = clock.Elapsed };
	}

	/// <summary>Converts the server's files and writes them (unless a dry run), with a report.</summary>
	public static ConversionReport Run(Workspace workspace, ConvertOptions options, IProgress<JobProgress>? progress = null, CancellationToken cancellation = default)
	{
		var clock = Stopwatch.StartNew();
		if (!options.DryRun && options.Output == OutputMode.Folder && string.IsNullOrWhiteSpace(options.OutputFolder))
			throw new ArgumentException("an output folder is needed (or convert in place)");
		var outputFolder = options.Output == OutputMode.Folder && options.OutputFolder != null ? Path.GetFullPath(options.OutputFolder) : null;
		if (outputFolder != null && IsInside(outputFolder, workspace.Root) && !options.DryRun)
			throw new ArgumentException("the output folder must not be inside the server's folder");

		progress?.Report(new JobProgress("Finding custom events", 0, 1));
		var events = workspace.CustomEvents(options.Parallelism, cancellation);
		progress?.Report(new JobProgress("Finding shared names and flags", 0, 1));
		var names = workspace.Names(options.Parallelism, cancellation);
		var engine = new ConversionEngine(workspace, options, events, names);
		var files = workspace.Files(options.Kinds).Where(f => options.Only == null || options.Only.Contains(f.File.RelativePath)).ToList();

		var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
		var report = new ConversionReport { Root = workspace.Root, DryRun = options.DryRun, OutputFolder = outputFolder };
		report.Warnings.AddRange(workspace.Registry.PluginErrors.Select(e => "plugin: " + e));
		if (options.Output == OutputMode.InPlace && !options.DryRun) report.BackupFolder = Path.Combine(workspace.Root, ".uplift", stamp, "backup");
		report.ReportFolder = options.ReportFolder != null ? Path.GetFullPath(options.ReportFolder)
			: outputFolder != null ? outputFolder.TrimEnd(Path.DirectorySeparatorChar) + ".report"
			: Path.Combine(workspace.Root, ".uplift", stamp, "report");

		var outcomes = new FileOutcome[files.Count];
		var done = 0;
		progress?.Report(new JobProgress("Converting", 0, files.Count));
		var indexed = files.Select((f, i) => (f.Kind, f.File, i)).ToList();
		Workers.ForEach(indexed, options.Parallelism, item =>
		{
			var outcome = engine.ProcessFile(item.Kind, item.File);
			if (!options.DryRun) Write(outcome, options, outputFolder, report.BackupFolder);
			outcome.OriginalText = null;
			outcome.NewText = null;
			outcomes[item.i] = outcome;
			var n = Interlocked.Increment(ref done);
			if (n % 25 == 0 || n == files.Count) progress?.Report(new JobProgress("Converting", n, files.Count, item.File.RelativePath));
		}, cancellation);
		report.Files.AddRange(outcomes);
		report.Warnings.AddRange(JoinAnalysis.Warnings(report.Files));
		report.Elapsed = clock.Elapsed;
		progress?.Report(new JobProgress("Writing the report", 0, 1));
		WriteReport(report, events.Count);
		progress?.Report(new JobProgress("Done", files.Count, files.Count));
		return report;
	}

	/// <summary>The conversion of one file, in memory (for previews).</summary>
	public static Task<FileOutcome> PreviewAsync(Workspace workspace, ConvertOptions options, IReadOnlyCollection<string> customEvents, ScriptKind kind, ServerFile file,
		ServerNames? names = null) =>
		Workers.Run(() => new ConversionEngine(workspace, options, customEvents, names).ProcessFile(kind, file));

	private static void Write(FileOutcome outcome, ConvertOptions options, string? outputFolder, string? backupFolder)
	{
		var relative = outcome.File.RelativePath.Replace('/', Path.DirectorySeparatorChar);
		if (options.Output == OutputMode.Folder)
		{
			var target = Path.Combine(outputFolder!, relative);
			if (outcome.Changed && outcome.NewText != null)
			{
				ServerText.Write(target, outcome.NewText);
				outcome.WrittenTo = target;
			}
			else if (options.CopyUnchanged && outcome.Unreadable == null)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(outcome.File.FullPath, target, overwrite: true);
				outcome.WrittenTo = target;
			}
			return;
		}
		if (!outcome.Changed || outcome.NewText == null) return;
		var backup = Path.Combine(backupFolder!, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
		File.Copy(outcome.File.FullPath, backup, overwrite: true);
		ServerText.Write(outcome.File.FullPath, outcome.NewText);
		outcome.WrittenTo = outcome.File.FullPath;
	}

	private static bool IsInside(string path, string folder)
	{
		var f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(f, StringComparison.Ordinal);
	}

	private static string Clean(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "", StringComparison.Ordinal);

	/// <summary>Writes parts.tsv (every part), files.tsv (every file), warnings.txt and summary.json.</summary>
	public static void WriteReport(ConversionReport report, int customEvents)
	{
		var folder = report.ReportFolder!;
		Directory.CreateDirectory(folder);
		var parts = new StringBuilder("file\tscript\tline\tside\tbefore\tresult\tafter\thash\tnotes\treason\n");
		var filesTsv = new StringBuilder("file\tkind\tchanged\twritten\tproblems\trepairs\n");
		foreach (var f in report.Files)
		{
			filesTsv.Append($"{f.File.RelativePath}\t{f.Kind.Key()}\t{(f.Changed ? "yes" : "no")}\t{f.WrittenTo ?? ""}\t{Clean(string.Join("; ", f.Unreadable is { } u ? [u, .. f.Problems] : f.Problems))}\t{Clean(string.Join("; ", f.Repairs))}\n");
			foreach (var p in f.Parts)
				parts.Append($"{f.File.RelativePath}\t{p.SlotLabel}\t{p.Line}\t{p.Side.ToString().ToLowerInvariant()}\t{p.Before}\t{p.Result}\t{p.After}\t{p.Hash}\t{Clean(string.Join("; ", p.Notes))}\t{Clean(p.Reason)}\n");
		}
		ServerText.Write(Path.Combine(folder, "parts.tsv"), parts.ToString());
		ServerText.Write(Path.Combine(folder, "files.tsv"), filesTsv.ToString());
		ServerText.Write(Path.Combine(folder, "warnings.txt"), string.Join("\n", report.Warnings) + (report.Warnings.Count > 0 ? "\n" : ""));
		var summary = new
		{
			report.Root,
			report.OutputFolder,
			report.BackupFolder,
			report.DryRun,
			Seconds = Math.Round(report.Elapsed.TotalSeconds, 1),
			Files = report.Files.Count,
			report.FilesChanged,
			FilesWritten = report.Files.Count(f => f.WrittenTo != null),
			Unreadable = report.Files.Count(f => f.Unreadable != null),
			CustomEvents = customEvents,
			Parts = new
			{
				AlreadyGs2 = report.Count(PartStatus.AlreadyGs2),
				Converted = report.Count(PartStatus.Converted),
				Kept = report.Count(PartStatus.Kept),
				Failed = report.Count(PartStatus.Failed),
			},
			After = new
			{
				Gs2 = report.CountAfter(CheckStatus.Ok),
				Gs1 = report.CountAfter(CheckStatus.Gs1),
				NotGs2 = report.CountAfter(CheckStatus.NotGs2),
			},
			Warnings = report.Warnings.Count,
		};
		File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
	}
}
