namespace Uplift.Formats;

/// <summary>One script inside a file: a weapon's, a class's, one NPC of a level, a gani's.</summary>
/// <param name="Index">Its position among the file's scripts (0 for a file with one script).</param>
/// <param name="Label">How it is named in reports: the file, and for a level the NPC's number and line.</param>
/// <param name="Line">The file's line (1-based) of the script's first line.</param>
/// <param name="Source">The script as the server reads it, with line breaks as \n.</param>
public sealed record ScriptSlot(int Index, string Label, int Line, string Source);

/// <summary>A server file opened by its format: its scripts, and the file rebuilt with new scripts.</summary>
public abstract class ScriptDocument
{
	protected ScriptDocument(ServerFile file, ScriptKind kind)
	{
		File = file;
		Kind = kind;
	}

	public ServerFile File { get; }

	public ScriptKind Kind { get; }

	public abstract IReadOnlyList<ScriptSlot> Slots { get; }

	/// <summary>What keeps the server from reading the file as it is (shown in reports; the file's scripts are still listed).</summary>
	public List<string> Problems { get; } = [];

	/// <summary>Repairs this format made when opening the file (written only when the file is written).</summary>
	public List<string> Repairs { get; } = [];

	/// <summary>The whole file with the scripts of <paramref name="newSources" /> (by slot index) replaced; every other byte as it was.</summary>
	public abstract string Render(IReadOnlyDictionary<int, string> newSources);
}

/// <summary>
///     A document whose scripts are ranges of whole lines. Lines keep their own line endings: a replaced range is written with \r\n
///     when every line it replaces had one (or, for an empty range, when the file's lines do), else with \n.
/// </summary>
public abstract class LineDocument : ScriptDocument
{
	private readonly List<(ScriptSlot Slot, int Start, int End)> _ranges = [];
	private string[] _lines;

	protected LineDocument(ServerFile file, ScriptKind kind, string text) : base(file, kind)
	{
		_lines = text.Split('\n');
	}

	public override IReadOnlyList<ScriptSlot> Slots => _ranges.Select(r => r.Slot).ToList();

	/// <summary>The lines without their \r.</summary>
	protected string[] Stripped => _lines.Select(l => l.TrimEnd('\r')).ToArray();

	protected string[] RawLines => _lines;

	protected void SetLines(string[] lines) => _lines = lines;

	/// <summary>Adds a script made of lines [start, end), its source given (each format joins the lines its own way).</summary>
	protected void AddRange(int start, int end, string label, string source) => _ranges.Add((new ScriptSlot(_ranges.Count, label, start + 1, source), start, end));

	/// <summary>How a new source becomes lines (the default: its lines without the line breaks at the end).</summary>
	protected virtual IEnumerable<string> SourceLines(string source) => source.TrimEnd('\n').Length == 0 ? [] : source.TrimEnd('\n').Split('\n');

	public override string Render(IReadOnlyDictionary<int, string> newSources)
	{
		var output = new List<string>(_lines.Length);
		var next = 0;
		var fileCrlf = _lines.Length > 1 && _lines[..^1].All(l => l.EndsWith('\r'));
		foreach (var (slot, start, end) in _ranges.OrderBy(r => r.Start))
		{
			if (!newSources.TryGetValue(slot.Index, out var source)) continue;
			output.AddRange(_lines[next..start]);
			var replaced = _lines[start..end];
			var crlf = replaced.Length > 0 ? replaced.All(l => l.EndsWith('\r')) : fileCrlf;
			output.AddRange(SourceLines(source.Replace("\r", "", StringComparison.Ordinal)).Select(l => crlf ? l + "\r" : l));
			next = end;
		}
		output.AddRange(_lines[next..]);
		return string.Join("\n", output);
	}
}
