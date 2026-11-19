namespace Uplift;

/// <summary>The kinds of script a Graal server keeps, each in its own folder and file format.</summary>
public enum ScriptKind
{
	/// <summary>Database NPCs: <c>npcs/npc*.txt</c>, <c>GRNPC001</c> files.</summary>
	DbNpc,

	/// <summary>Weapons: <c>weapons/weapon*.txt</c>, <c>GRAWP001</c> files.</summary>
	Weapon,

	/// <summary>Classes: <c>scripts/*.txt</c>, the whole file is the script.</summary>
	Class,

	/// <summary>The NPCs of text levels: <c>levels/**/*.nw</c>, <c>GLEVNW01</c> files, one script per <c>NPC ... NPCEND</c> block.</summary>
	LevelNpc,

	/// <summary>Gani scripts: <c>levels/**/*.gani</c>, the <c>SCRIPT ... SCRIPTEND</c> block, run by the clients only.</summary>
	Gani,
}

/// <summary>The two parts of a script, split at the first <c>//#CLIENTSIDE</c> comment.</summary>
public enum ScriptSide
{
	Server,
	Client,
}

public static class ScriptKinds
{
	public static IReadOnlyList<ScriptKind> All { get; } = [ScriptKind.DbNpc, ScriptKind.Weapon, ScriptKind.Class, ScriptKind.LevelNpc, ScriptKind.Gani];

	/// <summary>The name shown to people.</summary>
	public static string Title(this ScriptKind kind) => kind switch
	{
		ScriptKind.DbNpc    => "Database NPCs",
		ScriptKind.Weapon   => "Weapons",
		ScriptKind.Class    => "Classes",
		ScriptKind.LevelNpc => "Level NPCs",
		ScriptKind.Gani     => "Gani scripts",
		_                   => kind.ToString(),
	};

	/// <summary>The word used on the command line and in uplift.json.</summary>
	public static string Key(this ScriptKind kind) => kind switch
	{
		ScriptKind.DbNpc    => "dbnpcs",
		ScriptKind.Weapon   => "weapons",
		ScriptKind.Class    => "classes",
		ScriptKind.LevelNpc => "levels",
		ScriptKind.Gani     => "ganis",
		_                   => kind.ToString().ToLowerInvariant(),
	};

	public static bool TryParse(string text, out ScriptKind kind)
	{
		switch (text.Trim().ToLowerInvariant())
		{
			case "dbnpcs" or "dbnpc" or "npcs" or "db": kind = ScriptKind.DbNpc; return true;
			case "weapons" or "weapon": kind = ScriptKind.Weapon; return true;
			case "classes" or "class" or "scripts": kind = ScriptKind.Class; return true;
			case "levels" or "level" or "levelnpcs" or "nw": kind = ScriptKind.LevelNpc; return true;
			case "ganis" or "gani": kind = ScriptKind.Gani; return true;
			default: kind = default; return false;
		}
	}

	/// <summary>The script type the compiler is given (it only changes the bytecode's header).</summary>
	public static string CompileTarget(this ScriptKind kind) => kind switch
	{
		ScriptKind.Weapon => "weapon",
		ScriptKind.Class  => "class",
		ScriptKind.Gani   => "gani",
		_                 => "npc",
	};

	/// <summary>Whether the kind has only a client part (ganis).</summary>
	public static bool IsClientOnly(this ScriptKind kind) => kind == ScriptKind.Gani;
}
