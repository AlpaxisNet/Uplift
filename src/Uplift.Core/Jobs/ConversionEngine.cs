using System.Collections.Concurrent;
using Uplift.Conversion;
using Uplift.Formats;
using Uplift.Rules;
using Uplift.Verification;

namespace Uplift.Jobs;

/// <summary>
///     Scans and converts the files of a workspace. Parts are cached by their hash (kind, side, code): a server's many copies of one
///     NPC are converted once. Thread-safe; every method needs a deep stack (call it from <see cref="Workers" />).
/// </summary>
public sealed class ConversionEngine
{
	private readonly ConcurrentDictionary<string, CheckResult> _checks = new();
	private readonly ConcurrentDictionary<string, PartResult> _conversions = new();
	private readonly Gs1Converter _converter;
	private readonly IReadOnlyList<ITextFix> _fixes;
	private readonly ConvertOptions _options;
	private readonly Dictionary<string, (bool Keep, string Text)> _overrides = new(StringComparer.OrdinalIgnoreCase);

	public ConversionEngine(Workspace workspace, ConvertOptions options, IReadOnlyCollection<string> customEvents)
	{
		Workspace = workspace;
		_options = options;
		var switches = options.Switches ?? workspace.DefaultSwitches();
		Rules = workspace.Registry.Rules.Where(r => switches.Contains(r.Id)).ToList();
		_fixes = workspace.Registry.TextFixes.Where(f => switches.Contains(f.Id)).ToList();
		Catalog = workspace.Catalog;
		_converter = new Gs1Converter(new ConversionOptions { CustomEvents = customEvents, Rules = Rules, ExtraEvents = workspace.Registry.Events, Catalog = Catalog });
		if (workspace.OverridesFolder is { } folder && Directory.Exists(folder))
			foreach (var f in Directory.EnumerateFiles(folder))
			{
				var ext = Path.GetExtension(f).ToLowerInvariant();
				if (ext == ".gs2") _overrides[Path.GetFileNameWithoutExtension(f)] = (false, ServerText.Read(f).Replace("\r", "", StringComparison.Ordinal));
				else if (ext == ".keep") _overrides[Path.GetFileNameWithoutExtension(f)] = (true, File.ReadLines(f, ServerText.Encoding).FirstOrDefault()?.Trim() ?? "kept by hand");
			}
	}

	public Workspace Workspace { get; }

	public IReadOnlyList<IConversionRule> Rules { get; }

	public Syntax.Gs1Catalog Catalog { get; }

	private FormatOptions FormatOptions => new() { Repair = _options.Repair };

	/// <summary>The hash of a part: the name of its override file.</summary>
	public static string PartHash(ScriptKind kind, ScriptSide side, string code) =>
		ServerText.Hash(kind.HashName() + "\n" + (side == ScriptSide.Server ? "server" : "client") + "\n" + code);

	/// <summary>The parts of a script: (side, code), only the non-empty ones; a gani's script is its client part.</summary>
	public static IEnumerable<(ScriptSide Side, string Code)> Parts(ScriptKind kind, string source)
	{
		if (kind.IsClientOnly())
		{
			if (source.Trim().Length > 0) yield return (ScriptSide.Client, source);
			yield break;
		}
		var server = ScriptParts.ServerPart(source);
		if (!string.IsNullOrWhiteSpace(server)) yield return (ScriptSide.Server, server);
		var client = ScriptParts.ClientPart(source);
		if (!string.IsNullOrWhiteSpace(client)) yield return (ScriptSide.Client, client);
	}

	private readonly ConcurrentDictionary<string, Syntax.Reading> _readings = new();

	public CheckResult Check(string code, ScriptKind kind) => _checks.GetOrAdd(code, c =>
	{
		var read = Syntax.Language.Read(c, Catalog);
		_readings.TryAdd(c, read.Reading);
		return StrictCheck.Check(read);
	});

	public PartState StateOf(string code, ScriptKind kind)
	{
		Check(code, kind);
		return _readings.GetValueOrDefault(code) switch
		{
			Syntax.Reading.Gs2 => PartState.Gs2,
			Syntax.Reading.Broken => PartState.Broken,
			_ => PartState.Gs1,
		};
	}

	/// <summary>What a file's parts are, without converting anything.</summary>
	public FileScan ScanFile(ScriptKind kind, ServerFile file)
	{
		var (document, problem) = Workspace.OpenFile(kind, file, FormatOptions);
		if (document == null) return new FileScan { Kind = kind, File = file, Unreadable = true, Problems = { problem ?? "unreadable" } };
		var scan = new FileScan { Kind = kind, File = file };
		scan.Problems.AddRange(document.Problems);
		scan.Repairs.AddRange(document.Repairs);
		foreach (var slot in document.Slots)
			foreach (var (side, code) in Parts(kind, slot.Source))
			{
				var state = StateOf(code, kind);
				var detail = state == PartState.Gs2 ? "" : Check(code, kind).Detail;
				scan.Parts.Add(new PartScan(slot.Index, slot.Label, slot.Line, side, state, detail));
			}
		return scan;
	}

	/// <summary>Converts one file in memory: its parts' outcomes, and its new text when anything changed.</summary>
	public FileOutcome ProcessFile(ScriptKind kind, ServerFile file)
	{
		var (document, problem) = Workspace.OpenFile(kind, file, FormatOptions);
		if (document == null) return new FileOutcome { Kind = kind, File = file, Unreadable = problem ?? "unreadable" };
		var outcome = new FileOutcome { Kind = kind, File = file };
		outcome.Problems.AddRange(document.Problems);
		outcome.Repairs.AddRange(document.Repairs);
		var newSources = new Dictionary<int, string>();
		foreach (var slot in document.Slots)
		{
			string? newServer = null, newClient = null;
			foreach (var (side, code) in Parts(kind, slot.Source))
			{
				var part = ProcessPart(kind, side, code, slot, out var final);
				outcome.Parts.Add(part);
				JoinAnalysis.Collect(outcome, side, final);
				if (!part.Changed) continue;
				if (side == ScriptSide.Server) newServer = final;
				else newClient = final;
			}
			if (newServer == null && newClient == null) continue;
			newSources[slot.Index] = kind.IsClientOnly() ? newClient! : ScriptParts.Join(slot.Source, newServer, newClient);
		}
		var original = ServerText.Read(file.FullPath);
		outcome.OriginalText = original;
		if (newSources.Count > 0 || document.Repairs.Count > 0)
		{
			var text = document.Render(newSources);
			outcome.Changed = text != original;
			outcome.NewText = outcome.Changed ? text : null;
		}
		return outcome;
	}

	private PartOutcome ProcessPart(ScriptKind kind, ScriptSide side, string code, ScriptSlot slot, out string final)
	{
		var hash = PartHash(kind, side, code);
		var before = StateOf(code, kind);
		final = code;
		if (!_options.Sides.Contains(side))
			return new PartOutcome(slot.Index, slot.Label, slot.Line, side, hash, before, before == PartState.Gs2 ? PartStatus.AlreadyGs2 : PartStatus.Kept, Check(code, kind).Status, [], "side not chosen");

		PartResult result;
		if (_overrides.TryGetValue(hash, out var manual))
			result = manual.Keep
				? new PartResult(PartStatus.Kept, "", [], "kept by hand: " + manual.Text)
				: new PartResult(PartStatus.Converted, manual.Text, ["hand conversion (overrides/" + hash + ".gs2)"], "");
		else if (before == PartState.Gs2) result = new PartResult(PartStatus.AlreadyGs2, "", [], "");
		else if (before == PartState.Broken) result = PartResult.Fail("does not compile as GS1 or GS2: " + Check(code, kind).Detail);
		else result = _conversions.GetOrAdd(hash, _ => _converter.Convert(code, kind, side));

		var notes = new List<string>(result.Notes);
		var text = result.Status == PartStatus.Converted ? result.Text : code;
		if (result.Status == PartStatus.Converted || result.Status == PartStatus.AlreadyGs2 && _options.FixAlreadyGs2)
		{
			var context = new TextFixContext(kind, side);
			var fixedText = text;
			foreach (var fix in _fixes) fixedText = fix.Apply(fixedText, context);
			if (fixedText != text)
			{
				if (Check(fixedText, kind).Status == CheckStatus.Ok || Check(text, kind).Status != CheckStatus.Ok)
				{
					text = fixedText;
					notes.AddRange(context.Notes.Select(n => "text fix: " + n));
				}
				else notes.Add("text fixes left out: with them the part was not strict GS2 (" + string.Join("; ", context.Notes) + ")");
			}
		}
		final = text;
		var changed = !SameCode(text, code);
		if (!changed) final = code;
		var after = Check(final, kind).Status;
		return new PartOutcome(slot.Index, slot.Label, slot.Line, side, hash, before, result.Status, after, notes, result.Reason) { Changed = changed };
	}

	// The same code, but for the line breaks at its end (the part is written back as whole lines).
	private static bool SameCode(string a, string b) => a.TrimEnd('\n') == b.TrimEnd('\n');
}
