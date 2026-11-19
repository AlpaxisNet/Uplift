// An example of each thing a plugin can add. Copy this project, rename it, and change what the rules do.
using Uplift;
using Uplift.Plugins;
using Uplift.Rules;
using Uplift.Syntax;

namespace Uplift.Plugins.Sample;

/// <summary>Uplift finds every public class that implements IUpliftPlugin and calls Register once.</summary>
public sealed class SamplePlugin : IUpliftPlugin
{
	public string Name => "Sample";

	public void Register(UpliftRegistry registry)
	{
		// A rewrite of the GS1 syntax tree (off until switched on in the window, on the command line or in uplift.json).
		registry.AddRule(new GiveGralatsRule());

		// A regular expression run on GS2 code (never inside strings or comments), server parts of weapons and classes only.
		registry.AddTextFix(new RegexTextFix(
			id: "sample-sendtorc",
			title: "sendtorc(...) → echo(...)",
			description: "Example: a server whose engine has no sendtorc writes its messages with echo.",
			pattern: @"\bsendtorc\s*\(",
			replacement: "echo(",
			ignoreCase: true,
			sides: new HashSet<ScriptSide> { ScriptSide.Server },
			kinds: new HashSet<ScriptKind> { ScriptKind.Weapon, ScriptKind.Class },
			enabledByDefault: false));

		// A GS1 event flag the server's own engine sends, and the GS2 function it becomes.
		registry.AddEvent("playerjoinsguild", "onPlayerJoinsGuild");

		// A GS1 command of the server's own, with bare arguments: givegralats 5; (N: a number).
		registry.AddGs1Command("givegralats", "N");
	}
}

/// <summary>Example: a server replaced its GS1 helper givegralats(n) with the player's method addGralats(n).</summary>
public sealed class GiveGralatsRule : IConversionRule
{
	public string Id => "sample-givegralats";

	public string Title => "givegralats(n) → player.addGralats(n)";

	public string Description => "Example rule: the server's old helper givegralats(n) is the player's addGralats(n) in its GS2 code.";

	public bool EnabledByDefault => false;

	public Expr? RewriteExpression(Expr expression, RuleContext context)
	{
		// The tree is rewritten children first: the arguments are already converted here.
		if (expression is not CallExpr { Name: var name } call || !name.Equals("givegralats", StringComparison.OrdinalIgnoreCase)) return null;
		// A script with its own givegralats function keeps calling it.
		if (context.FunctionNames.Contains("givegralats")) return null;
		if (context.Side == ScriptSide.Client) context.Refuse("givegralats in client code (the server's helper is server-side)");
		context.Note("givegralats(...) written player.addGralats(...)");
		return new MethodCallExpr(new NameExpr("player"), "addGralats", call.Args);
	}
}
