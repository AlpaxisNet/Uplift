using Uplift.Formats;

namespace Uplift.Server;

/// <summary>Where a server keeps each kind of script.</summary>
public sealed class ServerLayout
{
	public static readonly IReadOnlyDictionary<ScriptKind, string> DefaultFolders = new Dictionary<ScriptKind, string>
	{
		[ScriptKind.DbNpc] = "npcs",
		[ScriptKind.Weapon] = "weapons",
		[ScriptKind.Class] = "scripts",
		[ScriptKind.LevelNpc] = "levels",
		[ScriptKind.Gani] = "levels",
	};

	private readonly Dictionary<ScriptKind, string> _folders = new(DefaultFolders);

	public ServerLayout(string root, UpliftConfig? config = null)
	{
		Root = Path.GetFullPath(root);
		if (config?.Folders != null)
			foreach (var (key, folder) in config.Folders)
				if (ScriptKinds.TryParse(key, out var kind))
				{
					_folders[kind] = folder.Trim('/', '\\');
					if (kind == ScriptKind.LevelNpc && !config.Folders.Keys.Any(k => ScriptKinds.TryParse(k, out var g) && g == ScriptKind.Gani)) _folders[ScriptKind.Gani] = _folders[kind];
				}
	}

	public string Root { get; }

	/// <summary>The kind's folder, relative to the server, as configured.</summary>
	public string Folder(ScriptKind kind) => _folders[kind];

	/// <summary>The kind's folder as it is on disk (matched without regard to case), or null when the server has none.</summary>
	public string? FindFolder(ScriptKind kind)
	{
		var path = Root;
		foreach (var piece in _folders[kind].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
		{
			var exact = Path.Combine(path, piece);
			if (Directory.Exists(exact))
			{
				path = exact;
				continue;
			}
			var match = Directory.Exists(path) ? Directory.EnumerateDirectories(path).FirstOrDefault(d => Path.GetFileName(d).Equals(piece, StringComparison.OrdinalIgnoreCase)) : null;
			if (match == null) return null;
			path = match;
		}
		return path;
	}

	/// <summary>Whether a folder looks like a server: it has at least one of the kinds' folders.</summary>
	public bool LooksLikeServer() => ScriptKinds.All.Any(k => FindFolder(k) != null);

	/// <summary>The files a kind's formats may claim: .txt files of the folder (levels: every level and gani file below it).</summary>
	public IEnumerable<ServerFile> Candidates(ScriptKind kind)
	{
		var folder = FindFolder(kind);
		if (folder == null) yield break;
		IEnumerable<string> files = kind switch
		{
			ScriptKind.LevelNpc => Below(folder, [".nw", ".graal", ".zelda"]),
			ScriptKind.Gani => Below(folder, [".gani"]),
			_ => Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
		};
		foreach (var f in files.Order(StringComparer.Ordinal)) yield return new ServerFile(Root, Path.GetRelativePath(Root, f).Replace('\\', '/'));
	}

	// Every file with one of the extensions below a folder, skipping hidden folders (.git, .uplift).
	private static IEnumerable<string> Below(string folder, string[] extensions)
	{
		var stack = new Stack<string>();
		stack.Push(folder);
		while (stack.Count > 0)
		{
			var dir = stack.Pop();
			IEnumerable<string> entries;
			try
			{
				entries = Directory.EnumerateFiles(dir).ToList();
			}
			catch (UnauthorizedAccessException)
			{
				continue;
			}
			foreach (var f in entries)
				if (extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))) yield return f;
			foreach (var d in Directory.EnumerateDirectories(dir))
				if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);
		}
	}
}
