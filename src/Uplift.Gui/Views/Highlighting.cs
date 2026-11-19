using System.Xml;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Uplift.Gui.Views;

/// <summary>GS2 highlighting (the Remote Control's Graal.xshd), coloured for the light or the dark theme.</summary>
public static class Highlighting
{
	private static readonly Dictionary<string, string> Dark = new()
	{
		["Comment"] = "#7F7A99", ["String"] = "#E6DB74", ["Decimal"] = "#BE84FF", ["Keyword"] = "#FF6188", ["Others"] = "#FF6188",
		["Others4"] = "#D6D3E8", ["Others5"] = "#FC9867", ["Operators"] = "#FF6188", ["ObjectProperties"] = "#78DCE8", ["Function"] = "#A9DC76",
	};

	private static readonly Dictionary<string, string> Light = new()
	{
		["Comment"] = "#6A737D", ["String"] = "#0A7A3E", ["Decimal"] = "#6F42C1", ["Keyword"] = "#C2185B", ["Others"] = "#C2185B",
		["Others4"] = "#24292E", ["Others5"] = "#B35900", ["Operators"] = "#C2185B", ["ObjectProperties"] = "#00708A", ["Function"] = "#005CC5",
	};

	private static readonly Dictionary<bool, IHighlightingDefinition> Cache = [];

	public static IHighlightingDefinition For(ThemeVariant variant)
	{
		var dark = variant == ThemeVariant.Dark;
		if (Cache.TryGetValue(dark, out var cached)) return cached;
		using var stream = typeof(Highlighting).Assembly.GetManifestResourceStream("Uplift.Gui.Graal.xshd")!;
		XshdSyntaxDefinition xshd;
		using (var reader = XmlReader.Create(stream)) xshd = HighlightingLoader.LoadXshd(reader);
		var palette = dark ? Dark : Light;
		foreach (var color in xshd.Elements.OfType<XshdColor>())
			if (color.Name != null && palette.TryGetValue(color.Name, out var hex))
				color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
		var definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
		Cache[dark] = definition;
		return definition;
	}
}
