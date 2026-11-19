using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Uplift.Formats;

/// <summary>A file of a server: where the server is, and the file's path inside it (with / separators).</summary>
public sealed record ServerFile(string Root, string RelativePath)
{
	public string FullPath => Path.Combine(Root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>How a format opens files.</summary>
public sealed record FormatOptions
{
	/// <summary>Repairs the faults that keep the server from reading a file (a level's missing NPCEND or GLEVNW01 line, a gani's SCRIPTEND).</summary>
	public bool Repair { get; init; } = true;
}

/// <summary>A kind of server file that holds scripts. Plugins can add their own (UpliftRegistry.AddFormat).</summary>
public interface IScriptFormat
{
	/// <summary>A short name (weapon, dbnpc, class, nw, gani).</summary>
	string Id { get; }

	ScriptKind Kind { get; }

	/// <summary>Whether a file of the kind's folder is this format's (by its name and its text).</summary>
	bool Claims(ServerFile file, string text);

	ScriptDocument Open(ServerFile file, string text, FormatOptions options);
}

/// <summary>The formats Uplift knows.</summary>
public static class ScriptFormats
{
	public static IReadOnlyList<IScriptFormat> BuiltIn { get; } = [new WeaponFormat(), new DbNpcFormat(), new ClassFormat(), new NwLevelFormat(), new GaniFormat()];
}

// ------------------------------------------------------------------ weapons and database NPCs

/// <summary>
///     A file with a header line and the script between a start line and an end line: weapons (GRAWP001, SCRIPT ... SCRIPTEND) and
///     database NPCs (GRNPC001, NPCSCRIPT ... NPCSCRIPTEND). The script starts after the first line that is exactly the start word and
///     ends before the next line that starts with the end word (or at the end of the file), as the server reads them.
/// </summary>
public abstract class HeaderedFormat(string header, string start, string end) : IScriptFormat
{
	public abstract string Id { get; }

	public abstract ScriptKind Kind { get; }

	public bool Claims(ServerFile file, string text) =>
		file.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && text.StartsWith(header, StringComparison.Ordinal);

	public ScriptDocument Open(ServerFile file, string text, FormatOptions options) => new Document(file, Kind, text, start, end);

	private sealed class Document : LineDocument
	{
		public Document(ServerFile file, ScriptKind kind, string text, string start, string end) : base(file, kind, text)
		{
			var lines = Stripped;
			var first = Array.FindIndex(lines, 1, l => l == start);
			if (first < 0 || first == lines.Length - 1)
			{
				Problems.Add($"no {start} line: the file has no script");
				return;
			}
			var last = Array.FindIndex(lines, first + 1, l => l.StartsWith(end, StringComparison.Ordinal));
			if (last < 0) last = lines.Length;
			AddRange(first + 1, last, file.RelativePath, string.Join("\n", lines[(first + 1)..last]));
		}
	}
}

public sealed class WeaponFormat() : HeaderedFormat("GRAWP001", "SCRIPT", "SCRIPTEND")
{
	public override string Id => "weapon";

	public override ScriptKind Kind => ScriptKind.Weapon;
}

public sealed class DbNpcFormat() : HeaderedFormat("GRNPC001", "NPCSCRIPT", "NPCSCRIPTEND")
{
	public override string Id => "dbnpc";

	public override ScriptKind Kind => ScriptKind.DbNpc;
}

// ------------------------------------------------------------------ classes

/// <summary>A class: the whole .txt file is its script.</summary>
public sealed class ClassFormat : IScriptFormat
{
	public string Id => "class";

	public ScriptKind Kind => ScriptKind.Class;

	public bool Claims(ServerFile file, string text) => file.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

	public ScriptDocument Open(ServerFile file, string text, FormatOptions options) => new Document(file, text);

	private sealed class Document : ScriptDocument
	{
		private readonly bool _crlf;
		private readonly List<ScriptSlot> _slots;

		public Document(ServerFile file, string text) : base(file, ScriptKind.Class)
		{
			_crlf = ServerText.IsCrlf(text);
			_slots = [new ScriptSlot(0, file.RelativePath, 1, text.Replace("\r", "", StringComparison.Ordinal))];
		}

		public override IReadOnlyList<ScriptSlot> Slots => _slots;

		public override string Render(IReadOnlyDictionary<int, string> newSources)
		{
			var source = newSources.TryGetValue(0, out var s) ? s.Replace("\r", "", StringComparison.Ordinal) : _slots[0].Source;
			return _crlf ? source.Replace("\n", "\r\n", StringComparison.Ordinal) : source;
		}
	}
}

// ------------------------------------------------------------------ text levels

/// <summary>
///     A text level (GLEVNW01). Its NPCs are found the way the server reads them: sections by the first
///     word of the trimmed line; SIGN, BADDY and HEIGHTS blocks skipped to their end word; an NPC whose header lacks a numeric x and y
///     skipped; an NPC's script the lines up to the one that trims to NPCEND.
/// </summary>
public sealed class NwLevelFormat : IScriptFormat
{
	private static readonly HashSet<string> LevelWords = new(StringComparer.Ordinal) { "LINK", "BOARD", "NPC", "SIGN", "CHEST", "BADDY" };

	public string Id => "nw";

	public ScriptKind Kind => ScriptKind.LevelNpc;

	public bool Claims(ServerFile file, string text)
	{
		var path = file.RelativePath;
		if (path.EndsWith(".nw", StringComparison.OrdinalIgnoreCase)) return true;
		// A text level saved under another level name (.graal, .zelda): its first line says what it is.
		return (path.EndsWith(".graal", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zelda", StringComparison.OrdinalIgnoreCase)) &&
		       text.StartsWith("GLEVNW01", StringComparison.Ordinal);
	}

	public ScriptDocument Open(ServerFile file, string text, FormatOptions options) => new Document(file, text, options.Repair);

	/// <summary>The NPC blocks of a level's lines (without \r): (header line, first script line, end) as line indexes.</summary>
	public static List<(int Header, int Start, int End)> Npcs(string[] lines)
	{
		var result = new List<(int, int, int)>();
		if (lines.Length == 0 || lines[0].Trim() != "GLEVNW01") return result;
		for (var i = 1; i < lines.Length;)
		{
			var line = lines[i].Trim();
			i++;
			if (line.Length == 0) continue;
			var space = line.IndexOf(' ');
			var section = space < 0 ? line : line[..space];
			var terminator = section switch { "SIGN" => "SIGNEND", "BADDY" => "BADDYEND", "HEIGHTS" => "HEIGHTSEND", "NPC" => "NPCEND", _ => null };
			if (terminator == null) continue;
			var start = i;
			while (i < lines.Length && lines[i].Trim() != terminator) i++;
			var end = i;
			i++;
			if (section != "NPC") continue;
			var values = (space < 0 ? "" : line[(space + 1)..]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (values.Length >= 3 && IsNumber(values[^2]) && IsNumber(values[^1])) result.Add((start - 1, start, end));
		}
		return result;
	}

	private static bool IsNumber(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

	private static readonly Regex NpcHeader = new(@"^NPC \S.* -?[0-9.]+ -?[0-9.]+\s*$", RegexOptions.Compiled);

	private sealed class Document : LineDocument
	{
		public Document(ServerFile file, string text, bool repair) : base(file, ScriptKind.LevelNpc, text)
		{
			if (file.RelativePath.EndsWith(".graal", StringComparison.OrdinalIgnoreCase) || file.RelativePath.EndsWith(".zelda", StringComparison.OrdinalIgnoreCase))
				Problems.Add("a text level (GLEVNW01) saved as " + Path.GetExtension(file.RelativePath) + ": servers that pick the level reader by the file's extension read it as a binary level");
			var lines = Stripped;
			if (lines.Length == 0 || lines[0].Trim() != "GLEVNW01")
			{
				var first = lines.Length == 0 ? "" : lines[0].Trim();
				if (LevelWords.Contains(first.Split(' ')[0]))
				{
					if (repair)
					{
						var newline = RawLines[0].EndsWith('\r') ? "\r" : "";
						SetLines(["GLEVNW01" + newline, .. RawLines]);
						Repairs.Add("added the missing GLEVNW01 first line");
						lines = Stripped;
					}
					else
					{
						Problems.Add("no GLEVNW01 first line: the level does not load");
						return;
					}
				}
				else
				{
					Problems.Add(first.StartsWith("GR-V", StringComparison.Ordinal) || first.StartsWith("Z3-V", StringComparison.Ordinal) ? $"a binary level ({first[..Math.Min(8, first.Length)]}) named .nw: not a text level" : "not a text level (no GLEVNW01 first line)");
					return;
				}
			}

			// An NPC that runs into the next NPC's header (a missing NPCEND): the server reads both as one script.
			var blocks = Npcs(lines);
			var inner = blocks.SelectMany(b => Enumerable.Range(b.Start, b.End - b.Start)).Where(k => NpcHeader.IsMatch(lines[k])).ToList();
			if (inner.Count > 0)
			{
				if (repair)
				{
					var raw = RawLines.ToList();
					foreach (var k in inner.OrderByDescending(k => k)) raw.Insert(k, "NPCEND" + (raw[k].EndsWith('\r') ? "\r" : ""));
					SetLines(raw.ToArray());
					Repairs.Add("inserted " + inner.Count + " missing NPCEND line(s) (an NPC ran into the next one's header)");
					lines = Stripped;
					blocks = Npcs(lines);
				}
				else Problems.Add("an NPC runs into the next NPC's header (a missing NPCEND) at line(s) " + string.Join(", ", inner.Select(k => k + 1)));
			}

			var n = 0;
			foreach (var (header, start, end) in blocks)
			{
				var sb = new StringBuilder();
				for (var i = start; i < end; i++) sb.Append(lines[i]).Append('\n');
				AddRange(start, end, $"{file.RelativePath}#{n++}@{header + 1}", sb.ToString());
			}
		}
	}
}

// ------------------------------------------------------------------ ganis

/// <summary>
///     A gani's script, as the server reads it: the lines after the line whose first word is SCRIPT, up to a line
///     that starts with SCRIPTEND. It runs on the clients only (its whole script is the client part).
/// </summary>
public sealed class GaniFormat : IScriptFormat
{
	public string Id => "gani";

	public ScriptKind Kind => ScriptKind.Gani;

	public bool Claims(ServerFile file, string text) => file.RelativePath.EndsWith(".gani", StringComparison.OrdinalIgnoreCase);

	public ScriptDocument Open(ServerFile file, string text, FormatOptions options) => new Document(file, text, options.Repair);

	private sealed class Document : LineDocument
	{
		public Document(ServerFile file, string text, bool repair) : base(file, ScriptKind.Gani, text)
		{
			var lines = Stripped;
			for (var i = 0; i < lines.Length; i++)
			{
				if (Regex.Replace(lines[i].Trim(), @"\s+", " ").Split(' ')[0] != "SCRIPT") continue;
				var end = Array.FindIndex(lines, i + 1, l => l.StartsWith("SCRIPTEND", StringComparison.Ordinal));
				if (end < 0)
				{
					if (!repair)
					{
						Problems.Add("SCRIPT without SCRIPTEND: the gani does not load");
						return;
					}
					var raw = RawLines.ToList();
					var crlf = raw.Count > 1 && raw[0].EndsWith('\r');
					// The last line is what follows the file's last line break ("" when it ends with one).
					if (raw[^1].Length == 0) raw[^1] = "SCRIPTEND" + (crlf ? "\r" : "");
					else
					{
						if (crlf) raw[^1] += "\r";
						raw.Add("SCRIPTEND" + (crlf ? "\r" : ""));
					}
					raw.Add("");
					SetLines(raw.ToArray());
					Repairs.Add("added the missing SCRIPTEND line");
					lines = Stripped;
					end = Array.FindIndex(lines, i + 1, l => l.StartsWith("SCRIPTEND", StringComparison.Ordinal));
				}
				var sb = new StringBuilder();
				for (var j = i + 1; j < end; j++) sb.Append(lines[j]).Append('\n');
				if (sb.ToString().Trim().Length > 0) AddRange(i + 1, end, file.RelativePath, sb.ToString());
				return;
			}
		}
	}
}
