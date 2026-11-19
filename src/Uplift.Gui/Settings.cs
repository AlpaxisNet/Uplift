using System.Text.Json;

namespace Uplift.Gui;

/// <summary>What the window remembers between runs (settings.json in the user's application-data folder).</summary>
public sealed class Settings
{
	/// <summary>0: follow the system, 1: light, 2: dark.</summary>
	public int Theme { get; set; }

	public string? ServerFolder { get; set; }

	public string? OutputFolder { get; set; }

	public double Width { get; set; } = 1400;

	public double Height { get; set; } = 900;

	public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Uplift");

	private static string FilePath => Path.Combine(Folder, "settings.json");

	public static Settings Load()
	{
		try
		{
			return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings() : new Settings();
		}
		catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
		{
			return new Settings();
		}
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Folder);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static void WriteCrashLog(Exception? exception)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			File.AppendAllText(Path.Combine(Folder, "crash.log"), $"{DateTime.Now:O}\n{exception}\n\n");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}
}
