using System.Security.Cryptography;
using System.Text;

namespace Uplift.Formats;

/// <summary>
///     Reading and writing server files. Graal scripts are 8-bit text in no declared encoding: every file is read and written as
///     Latin-1, so each byte comes back exactly as it was, whatever it meant.
/// </summary>
public static class ServerText
{
	public static readonly Encoding Encoding = Encoding.Latin1;

	public static string Read(string path) => File.ReadAllText(path, Encoding);

	public static void Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		File.WriteAllText(path, text, Encoding);
	}

	/// <summary>A short stable hash of a part (kind, side and code), the name of its override file.</summary>
	public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.GetBytes(text)))[..20];

	/// <summary>Whether every line break of a text is \r\n (and it has at least one).</summary>
	public static bool IsCrlf(string text)
	{
		var lf = 0;
		var crlf = 0;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != '\n') continue;
			lf++;
			if (i > 0 && text[i - 1] == '\r') crlf++;
		}
		return lf > 0 && lf == crlf;
	}
}
