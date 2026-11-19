using Uplift.Conversion;
using Uplift.Jobs;
using Uplift.Verification;

namespace Uplift.Gui.ViewModels;

/// <summary>A kind of script, chosen or not, with what the scan found.</summary>
public sealed class KindOption(ScriptKind kind, string folder, Action changed) : ViewModelBase
{
	private bool _isChecked = true;
	private string _summary = "not scanned";
	private int _toConvert;

	public ScriptKind Kind { get; } = kind;

	public string Title { get; } = kind.Title();

	public string Folder { get; } = folder;

	public bool IsChecked
	{
		get => _isChecked;
		set
		{
			if (Set(ref _isChecked, value)) changed();
		}
	}

	public string Summary
	{
		get => _summary;
		set => Set(ref _summary, value);
	}

	public int ToConvert
	{
		get => _toConvert;
		set
		{
			if (Set(ref _toConvert, value)) Raise(nameof(ToConvertText));
		}
	}

	public string ToConvertText => _toConvert > 0 ? _toConvert.ToString("N0") : "";
}

/// <summary>A rule or text fix, on or off.</summary>
public sealed class SwitchOption(string id, string title, string description, bool isChecked) : ViewModelBase
{
	private bool _isChecked = isChecked;

	public string Id { get; } = id;

	public string Title { get; } = title;

	public string Description { get; } = description;

	public string Tip => $"{Id}\n\n{Description}";

	public bool IsChecked
	{
		get => _isChecked;
		set => Set(ref _isChecked, value);
	}
}

/// <summary>A scanned file in the list.</summary>
public sealed class FileItem(FileScan scan) : ViewModelBase
{
	private bool _isChecked = true;

	public FileScan Scan { get; } = scan;

	public string Path => Scan.File.RelativePath;

	public string KindTitle => Scan.Kind.Title();

	public string Gs1 => Scan.Gs1 > 0 ? Scan.Gs1.ToString("N0") : "";

	public string Gs2 => Scan.Gs2 > 0 ? Scan.Gs2.ToString("N0") : "";

	public string Broken => Scan.Broken > 0 ? Scan.Broken.ToString("N0") : Scan.Unreadable ? "!" : "";

	public string StateText => Scan.State switch
	{
		FileState.NeedsConversion => "GS1",
		FileState.HasBroken => "broken",
		FileState.Unreadable => "unreadable",
		FileState.NoScript => "no script",
		_ => "GS2",
	};

	public bool IsGs1 => Scan.State == FileState.NeedsConversion;

	public bool IsGs2 => Scan.State is FileState.Gs2 or FileState.NoScript;

	public bool IsBroken => Scan.State is FileState.HasBroken or FileState.Unreadable;

	public string Tip
	{
		get
		{
			var lines = new List<string> { Path, $"{Scan.Kind.Title()}: {Scan.Gs1} GS1, {Scan.Gs2} GS2, {Scan.Broken} broken part(s)" };
			lines.AddRange(Scan.Problems.Select(p => "problem: " + p));
			lines.AddRange(Scan.Repairs.Select(p => "repair: " + p));
			return string.Join("\n", lines);
		}
	}

	public bool IsChecked
	{
		get => _isChecked;
		set => Set(ref _isChecked, value);
	}
}

/// <summary>One part of the previewed file.</summary>
public sealed class PartRow(PartOutcome part)
{
	public string Where
	{
		get
		{
			var hash = part.SlotLabel.IndexOf('#');
			var where = hash < 0 ? "line " + part.Line : "NPC #" + part.SlotLabel[(hash + 1)..].Split('@')[0] + " (line " + part.Line + ")";
			return where + " · " + part.Side.ToString().ToLowerInvariant();
		}
	}

	public string Result => part.Result switch
	{
		PartStatus.AlreadyGs2 => part.Changed ? "GS2, fixed" : "GS2 already",
		PartStatus.Converted => "converted",
		PartStatus.Kept => "kept",
		_ => "not converted",
	};

	public bool IsConverted => part.Result == PartStatus.Converted || part.Changed;

	public bool IsUnchanged => part.Result == PartStatus.AlreadyGs2 && !part.Changed;

	public bool IsKept => part.Result == PartStatus.Kept;

	public bool IsFailed => part.Result == PartStatus.Failed;

	public string After => part.After == CheckStatus.Ok ? "strict GS2" : part.After == CheckStatus.Gs1 ? "still GS1" : "not GS2";

	public string Details => string.Join("\n", (part.Reason.Length > 0 ? [part.Reason] : Array.Empty<string>()).Concat(part.Notes));

	public bool HasDetails => Details.Length > 0;
}
