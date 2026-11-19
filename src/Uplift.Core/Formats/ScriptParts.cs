namespace Uplift.Formats;

/// <summary>
///     Splits a script into the part the server runs and the part the clients get, at the comment <c>//#CLIENTSIDE</c>, and joins
///     new parts back into the script.
/// </summary>
/// <remarks>
///     The marker is the first line comment of the code that begins with <c>//#CLIENTSIDE</c>: one inside a string, inside a
///     <c>/* */</c> comment or later in another line comment (<c>//----&gt;//#CLIENTSIDE</c>) does not count, and spaces before it do
///     not matter. Many scripts put the marker inside a string to give a new NPC a client part
///     (<c>putnpc2(x, y, "//#CLIENTSIDE\nfunction onCreated() ...")</c>) or comment out an old version of a script with its marker;
///     both work only with this rule.
/// </remarks>
public static class ScriptParts
{
	public const string ClientSideMarker = "//#CLIENTSIDE";

	/// <summary>Everything before the marker (the whole source without one), with line breaks as \n.</summary>
	public static string ServerPart(string source)
	{
		source = source.Replace("\r", string.Empty, StringComparison.Ordinal);
		var marker = FindClientSideMarker(source);
		return marker < 0 ? source : source[..marker].TrimEnd('\n');
	}

	/// <summary>Everything after the marker, or nothing without one, with line breaks as \n.</summary>
	public static string ClientPart(string source)
	{
		source = source.Replace("\r", string.Empty, StringComparison.Ordinal);
		var marker = FindClientSideMarker(source);
		return marker < 0 ? string.Empty : source[(marker + ClientSideMarker.Length)..].TrimStart('\n');
	}

	/// <summary>The part of a side.</summary>
	public static string Part(string source, ScriptSide side) => side == ScriptSide.Server ? ServerPart(source) : ClientPart(source);

	/// <summary>
	///     The script with new parts (null: the part as it is). Without a marker the server part is the whole script; a new client part
	///     for a script without one adds the marker.
	/// </summary>
	public static string Join(string source, string? newServer, string? newClient)
	{
		source = source.Replace("\r", string.Empty, StringComparison.Ordinal);
		if (newServer == null && newClient == null) return source;
		var marker = FindClientSideMarker(source);
		if (marker < 0)
		{
			var server = newServer ?? source;
			if (newClient == null) return Lines(server);
			return Lines(server) + ClientSideMarker + "\n" + Lines(newClient);
		}
		var serverText = newServer != null ? Lines(newServer) : source[..marker];
		var clientText = newClient != null ? "\n" + Lines(newClient) : source[(marker + ClientSideMarker.Length)..];
		return serverText + ClientSideMarker + clientText;
	}

	// Code as whole lines: without the line breaks at its end, then one \n; nothing for an empty part.
	private static string Lines(string code) => code.TrimEnd('\n').Length == 0 ? string.Empty : code.TrimEnd('\n') + "\n";

	/// <summary>
	///     Where the marker is in a source, or -1. A string (in " or ', with \ escaping the next character) ends at its closing quote or
	///     at the end of its line; a /* */ comment ends at its */; a line comment ends at the end of its line and is the marker when it
	///     begins with <see cref="ClientSideMarker" />.
	/// </summary>
	public static int FindClientSideMarker(string source)
	{
		var index = 0;
		while (index < source.Length)
		{
			var character = source[index];
			if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
			{
				if (string.CompareOrdinal(source, index, ClientSideMarker, 0, ClientSideMarker.Length) == 0) return index;
				var end = source.IndexOf('\n', index);
				index = end < 0 ? source.Length : end;
			}
			else if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
			{
				var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
				index = end < 0 ? source.Length : end + 2;
			}
			else if (character is '"' or '\'')
			{
				index = EndOfString(source, index, character);
			}
			else
			{
				index++;
			}
		}
		return -1;
	}

	private static int EndOfString(string source, int index, char quote)
	{
		for (index++; index < source.Length; index++)
		{
			var character = source[index];
			if (character == '\n') return index;
			if (character == '\\') index++;
			else if (character == quote) return index + 1;
		}
		return source.Length;
	}
}
