using Uplift.Conversion;
using Uplift.Formats;
using Uplift.Verification;

namespace Uplift.Jobs;

/// <summary>What a part is before conversion.</summary>
public enum PartState
{
	/// <summary>Strict GS2.</summary>
	Gs2,

	/// <summary>GS1 (or GS2 with GS1 signs): to convert.</summary>
	Gs1,

	/// <summary>Compiles in neither language.</summary>
	Broken,
}

/// <summary>What a file is, from its parts.</summary>
public enum FileState
{
	NoScript,
	Gs2,
	NeedsConversion,
	HasBroken,
	Unreadable,
}

/// <summary>Progress of a job: a phase, how far it is, and the file it is on.</summary>
public sealed record JobProgress(string Phase, int Done, int Total, string? Current = null);

/// <summary>One part of a scanned file.</summary>
public sealed record PartScan(int Slot, string SlotLabel, int Line, ScriptSide Side, PartState State, string Detail);

/// <summary>A scanned file: its parts' states and its problems.</summary>
public sealed class FileScan
{
	public required ScriptKind Kind { get; init; }

	public required ServerFile File { get; init; }

	public List<PartScan> Parts { get; } = [];

	public List<string> Problems { get; } = [];

	/// <summary>Repairs the format would make (written only with Repair on).</summary>
	public List<string> Repairs { get; } = [];

	public bool Unreadable { get; init; }

	public int Gs1 => Parts.Count(p => p.State == PartState.Gs1);

	public int Gs2 => Parts.Count(p => p.State == PartState.Gs2);

	public int Broken => Parts.Count(p => p.State == PartState.Broken);

	public FileState State => Unreadable ? FileState.Unreadable : Broken > 0 ? FileState.HasBroken : Gs1 > 0 ? FileState.NeedsConversion : Parts.Count == 0 ? FileState.NoScript : FileState.Gs2;
}

/// <summary>A scan of a server.</summary>
public sealed class ServerScan
{
	public required string Root { get; init; }

	public required List<FileScan> Files { get; init; }

	public required TimeSpan Elapsed { get; init; }

	public IEnumerable<FileScan> OfKind(ScriptKind kind) => Files.Where(f => f.Kind == kind);
}

/// <summary>Where converted files go.</summary>
public enum OutputMode
{
	/// <summary>Into a new folder, mirroring the server's layout; the server is not touched.</summary>
	Folder,

	/// <summary>Over the server's own files, each first copied to a backup folder.</summary>
	InPlace,
}

/// <summary>How a conversion runs.</summary>
public sealed class ConvertOptions
{
	public IReadOnlySet<ScriptKind> Kinds { get; init; } = ScriptKinds.All.ToHashSet();

	public IReadOnlySet<ScriptSide> Sides { get; init; } = new HashSet<ScriptSide> { ScriptSide.Server, ScriptSide.Client };

	/// <summary>The ids of the rules and text fixes that are on (null: each one's default for the server).</summary>
	public IReadOnlySet<string>? Switches { get; init; }

	/// <summary>Runs the text fixes on parts that were GS2 already too (not only on converted ones).</summary>
	public bool FixAlreadyGs2 { get; init; } = true;

	/// <summary>Repairs the faults that keep the server from reading a file (missing NPCEND, GLEVNW01, SCRIPTEND).</summary>
	public bool Repair { get; init; } = true;

	public OutputMode Output { get; init; } = OutputMode.Folder;

	/// <summary>The new folder (OutputMode.Folder).</summary>
	public string? OutputFolder { get; init; }

	/// <summary>Also copies the files of the chosen kinds that need no change (a complete copy of the server's scripts).</summary>
	public bool CopyUnchanged { get; init; }

	/// <summary>Where the report goes (default: beside the output folder, or in the server's .uplift folder).</summary>
	public string? ReportFolder { get; init; }

	public int Parallelism { get; init; } = Workers.DefaultParallelism;

	/// <summary>Converts and reports, but writes no file but the report.</summary>
	public bool DryRun { get; init; }

	/// <summary>The files to convert (paths relative to the server); null: every file of the chosen kinds.</summary>
	public IReadOnlySet<string>? Only { get; init; }
}

/// <summary>What happened to one part.</summary>
public sealed record PartOutcome(
	int Slot,
	string SlotLabel,
	int Line,
	ScriptSide Side,
	string Hash,
	PartState Before,
	PartStatus Result,
	CheckStatus After,
	IReadOnlyList<string> Notes,
	string Reason)
{
	public bool Changed { get; init; }
}

/// <summary>What happened to one file.</summary>
public sealed class FileOutcome
{
	public required ScriptKind Kind { get; init; }

	public required ServerFile File { get; init; }

	/// <summary>Why the file could not be opened, or null.</summary>
	public string? Unreadable { get; init; }

	public List<string> Problems { get; } = [];

	public List<string> Repairs { get; } = [];

	public List<PartOutcome> Parts { get; } = [];

	/// <summary>The file as it was (kept for previews only).</summary>
	public string? OriginalText { get; set; }

	/// <summary>The file converted (kept for previews only).</summary>
	public string? NewText { get; set; }

	public bool Changed { get; set; }

	/// <summary>Set when the file was written (or copied) to this path.</summary>
	public string? WrittenTo { get; set; }

	/// <summary>The classes the file's final scripts join, by side.</summary>
	public List<(ScriptSide Side, string Class)> Joins { get; } = [];

	/// <summary>The event functions (on...) the file's final scripts define, by side.</summary>
	public List<(ScriptSide Side, string Function)> EventFunctions { get; } = [];
}

/// <summary>The result of a conversion.</summary>
public sealed class ConversionReport
{
	public required string Root { get; init; }

	public List<FileOutcome> Files { get; } = [];

	public List<string> Warnings { get; } = [];

	public string? OutputFolder { get; set; }

	public string? BackupFolder { get; set; }

	public string? ReportFolder { get; set; }

	public bool DryRun { get; set; }

	public TimeSpan Elapsed { get; set; }

	public int FilesChanged => Files.Count(f => f.Changed);

	public int Count(PartStatus status) => Files.Sum(f => f.Parts.Count(p => p.Result == status));

	public int CountAfter(CheckStatus status) => Files.Sum(f => f.Parts.Count(p => p.After == status));
}
