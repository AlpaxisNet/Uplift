using System.Text.RegularExpressions;

namespace Uplift.Server;

/// <summary>
///     Finds the events a server's scripts send by name: callnpc(npc, "event"), callweapon(index, "event"), scheduleevent(delay,
///     "event"), trigger("event") (GS1's forms without parentheses or quotes too: callnpc testnpc(32,36),ForceUpdate2;). A GS1
///     script's main code ran for any such event with its name true, so a bare, never-assigned name of one, tested outside
///     functions, is that event.
/// </summary>
public static class EventScanner
{
	private static readonly Regex Sender = new(@"\b(callnpc|callweapon|scheduleevent)\b\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex Trigger = new(@"\btrigger\s*\(\s*""([A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex Name = new(@"\G\s*""?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

	private static readonly HashSet<string> NotEvents = new(StringComparer.OrdinalIgnoreCase) { "this", "player", "temp", "params", "null", "x", "y" };

	/// <summary>The event names one file's text sends (lower case).</summary>
	public static IEnumerable<string> Find(string text)
	{
		foreach (Match m in Trigger.Matches(text))
			if (Keep(m.Groups[1].Value) is { } name) yield return name;
		foreach (Match m in Sender.Matches(text))
		{
			// Skip the first argument (balanced brackets, strings), up to the comma after it, then read the event's name.
			var i = m.Index + m.Length;
			if (i < text.Length && text[i] == '(') i++;
			var depth = 0;
			for (; i < text.Length; i++)
			{
				var c = text[i];
				if (c is '"' or '\'')
				{
					var end = i + 1;
					while (end < text.Length && text[end] != c && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
					i = end;
					continue;
				}
				if (c is '(' or '[') depth++;
				else if (c is ')' or ']')
				{
					if (--depth < 0) break;
				}
				else if (c == ',' && depth == 0) break;
				else if (c is ';' or '\n' or '{' or '}') break;
			}
			if (i >= text.Length || text[i] != ',') continue;
			var name = Name.Match(text, i + 1);
			if (name.Success && Keep(name.Groups[1].Value) is { } n) yield return n;
		}
	}

	private static string? Keep(string name) => NotEvents.Contains(name) ? null : name.ToLowerInvariant();
}
