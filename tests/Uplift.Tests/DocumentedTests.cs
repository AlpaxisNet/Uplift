using Uplift.Conversion;
using Uplift.Jobs;
using Uplift.Rules;
using Uplift.Syntax;
using Xunit;

namespace Uplift.Tests;

/// <summary>What GS1 and GS2 mean as their documentation describes it, and how Uplift writes it.</summary>
public sealed class DocumentedTests
{
	private static PartResult Run(string code, ScriptKind kind = ScriptKind.LevelNpc, ScriptSide side = ScriptSide.Server, ConversionOptions? options = null) =>
		Workers.Run(() => new Gs1Converter(options).Convert(code, kind, side)).GetAwaiter().GetResult();

	private static string Convert(string code, ScriptKind kind = ScriptKind.LevelNpc, ScriptSide side = ScriptSide.Server, ConversionOptions? options = null)
	{
		var result = Run(code, kind, side, options);
		Assert.True(result.Status == PartStatus.Converted, result.Status + ": " + result.Reason);
		return result.Text;
	}

	private static ConversionOptions Without(string ruleId) => new() { Rules = BuiltInRules.All.Where(r => r.EnabledByDefault && r.Id != ruleId).ToList() };

	[Theory]
	// GS1: % binds tighter than * and /; ^ is written with parentheses wherever its grouping could be read another way.
	[InlineData("this.x = 2 * 7 % 4;", "this.x = 2 * (7 % 4);")]
	[InlineData("this.x = (2 * 3) ^ 2;", "this.x = (2 * 3) ^ 2;")]
	[InlineData("this.x = 2 * 3 ^ 2;", "this.x = 2 * (3 ^ 2);")]
	// The first = of a condition compares; one after && whose left side is no variable compares too.
	[InlineData("if (this.a = 1 && this.b = 0) hide;", "if (this.a == ((1 && this.b) == 0)) {")]
	// An assignment's value assigns (GS1 and GS2 alike).
	[InlineData("this.a = this.b = 1;", "this.a = this.b = 1;")]
	// Message codes as the docs define them.
	[InlineData("message #K(65);", "message(\"#K(65)\");")]
	[InlineData("message a#7b;", "message(\"a#7b\");")]
	[InlineData("message #C5;", "message(\"#C5\");")]
	[InlineData("message 100##;", "message(\"100#\");")]
	[InlineData("setplayerprop #7,bow.png;", "player.bowimg = \"bow.png\";")]
	[InlineData("setcharprop #C5,black;", "colors[5] = \"black\";")]
	[InlineData("message #N(2);", "message(level.npcs[2].name);")]
	[InlineData("message #W;", "message(player.weapon.image);")]
	[InlineData("message #D;", "message(downloadfile);")]
	[InlineData("message #P(3,-1);", "message(attr[3]);")]
	[InlineData("message #P(3,this.i);", "message(level.players[this.i].attr[3]);")]
	[InlineData("message #U(x,#c);", "message(\"#U(x,#c)\");")]
	// Braced text keeps its braces; setstring x; sets the empty text.
	[InlineData("setstring this.s,{a,b};", "this.s = \"{a,b}\";")]
	[InlineData("setstring this.s;", "this.s = \"\";")]
	[InlineData("setplayerprop #c;", "player.chat = \"\";")]
	// Commands with GS2 forms of their own.
	[InlineData("setplayerx 5;", "player.x = 5;")]
	[InlineData("setx 5;", "this.x = 5;")]
	[InlineData("setbody body2.png;", "player.bodyimg = \"body2.png\";")]
	[InlineData("setlevel a.nw;", "player.setlevel2(\"a.nw\", player.x, player.y);")]
	[InlineData("setmap map.png,levels.txt,0,0;", "setmap(\"levels.txt\", \"map.png\", 0, 0);")]
	[InlineData("putbomb 1,30,30;", "level.putbomb(1, 30, 30);")]
	[InlineData("setbackpal pics1.png;", "// setbackpal \"pics1.png\"; (GS2 has no setbackpal)")]
	[InlineData("addweapon (\"Sword\");", "addweapon(\"Sword\");")]
	// Numbers: up to 8 hex digits are 32 bits.
	[InlineData("this.x = 0xFFFFFFFF;", "this.x = -1;")]
	[InlineData("this.x = 1e3;", "this.x = 1000;")]
	// GS1's exclusive range is written as comparisons.
	[InlineData("if (this.x in <1, 5|) hide;", "if (this.x > 1 && this.x <= 5) {")]
	// timeout outside a yes/no position is the timer's time left.
	[InlineData("this.t = timeout;", "this.t = timeout;")]
	// A tab in a text is TAB.
	[InlineData("message a\tb;", "message(\"a\" TAB \"b\");")]
	public void Gs1MeansWhatItsDocumentationSays(string statement, string expected) =>
		Assert.Contains(expected, Convert("if (created) {\n  " + statement + "\n}\n"));

	[Fact]
	public void AnAssignmentInAConditionIsNotWritten()
	{
		// GS1: this.a == (this.b = 1); GS2 would compare at the inner = too.
		var result = Run("if (created && this.a = this.b = 1) hide;\n");
		Assert.Equal(PartStatus.Failed, result.Status);
		Assert.Contains("assignment inside a condition", result.Reason);
	}

	[Fact]
	public void PlayersaysIsTheChat()
	{
		var text = Convert("if (playerchats && playersays(hi)) hide;\n");
		Assert.Contains("strequals(player.chat, \"hi\")", text);
	}

	[Fact]
	public void HasweaponIsTheSidesFindweapon()
	{
		Assert.Contains("player.findweapon(\"Bow\") != null", Convert("if (playertouchsme && hasweapon(Bow)) hide;\n"));
		Assert.Contains("if (findweapon(\"Bow\") != null)", Convert("if (playertouchsme && hasweapon(Bow)) hide;\n", side: ScriptSide.Client));
	}

	[Fact]
	public void JoinedCommandsRunWithTheRuleAndAreCommentsWithout()
	{
		Assert.Contains("sleep(1);", Convert("if (created) { sleep1; }\n"));
		var off = Convert("if (created) { sleep1; hide; }\n", options: Without(CommandTyposRule.RuleId));
		Assert.Contains("// sleep1;", off);
		Assert.DoesNotContain("sleep(1)", off);
	}

	[Fact]
	public void FlagsAreThePlayersOnBothSides()
	{
		const string code = "if (playerchats) { set gotsword; }\nif (playertouchsme && gotsword && quest.step == 2) hide;\n";
		foreach (var side in new[] { ScriptSide.Server, ScriptSide.Client })
		{
			var text = Convert(code, side: side);
			Assert.Contains("player.gotsword = 1;", text);
			Assert.Contains("player.gotsword && player.quest.step == 2", text);
		}
		// A name a script assigns is that variable, and a with block's names are its target's.
		var shared = Convert("if (playertouchsme && gotsword) hide;\n", options: new ConversionOptions { SharedVariables = ["gotsword"] });
		Assert.Contains("if (gotsword)", shared);
		var with = Convert("if (playerenters) { with (findimg(1)) { emitter.delaymin = 1; } }\n");
		Assert.Contains("emitter.delaymin = 1;", with);
		Assert.DoesNotContain("player.emitter", with);
		// Without the rule, a bare read stays bare.
		Assert.Contains("if (gotsword)", Convert("if (playertouchsme && gotsword) hide;\n", options: Without(BuiltInRules.Gs1FlagsId)));
	}

	[Fact]
	public void MixedPartsRewriteOnlyKnownFlags()
	{
		// GS2 syntax with GS1 main code may name GS2 objects: only names known as flags become the player's.
		const string code = "if (playerenters) { this.c = EventControl; this.f = gotsword; }\n";
		var text = Convert(code, options: new ConversionOptions { PlayerFlags = ["gotsword"] });
		Assert.Contains("this.c = EventControl;", text);
		Assert.Contains("this.f = player.gotsword;", text);
	}

	[Fact]
	public void ServerNamesAreFound()
	{
		var (shared, flags) = Gs1Converter.NamesOf("function onCreated() {\n  Bot.start();\n  new GuiWindowCtrl(\"Win\") { text = \"a\"; };\n  count = 1;\n  this.a = player.quest;\n}\n");
		Assert.Superset(new HashSet<string>(["Bot", "Win", "count"], StringComparer.OrdinalIgnoreCase), new HashSet<string>(shared, StringComparer.OrdinalIgnoreCase));
		Assert.Contains("quest", flags);
		Assert.Contains("gotsword", Gs1Converter.NamesOf("set gotsword;\n").Flags);
	}

	[Fact]
	public void LevelNpcServerCodeUsesTheLevelsLists()
	{
		var text = Convert("if (created) { this.n = playerscount; this.a = players[this.i].x; }\n");
		Assert.Contains("this.n = arraylen(level.players);", text);
		Assert.Contains("this.a = level.players[this.i].x;", text);
		Assert.Contains("this.a = players[this.i].x;", Convert("if (created) { this.a = players[this.i].x; }\n", side: ScriptSide.Client));
	}

	[Fact]
	public void FakeEventAliasesAreEventsOnlyWithTheRule()
	{
		Assert.Contains("function onPlayerTouchsMe()", Convert("if (playertouchesme) hide;\n"));
		var off = Run("if (playertouchesme) hide;\nif (created) show;\n", options: Without(BuiltInRules.MisspeltEventsId));
		Assert.DoesNotContain("onPlayerTouchsMe", off.Text);
	}

	[Fact]
	public void CustomEventsCallTheScriptsFunction()
	{
		var text = Convert("function doit() { hide; }\nif (created) show;\n", options: new ConversionOptions { CustomEvents = ["doit"] });
		Assert.Contains("function onDoit() {\n  doit();\n}", text);
	}

	[Fact]
	public void CommaStatementsAreSplit()
	{
		var text = Convert("function onCreated() {\n  this.a = 1, this.b = 2;\n}\nif (playerenters) show;\n");
		Assert.Contains("this.a = 1;\n  this.b = 2;", text);
	}

	[Theory]
	[InlineData("function onCreated() {\n  outer: for (temp.i = 0; temp.i < 3; temp.i++) {\n    while (true) {\n      break outer;\n    }\n  }\n}\n")]
	[InlineData("function onCreated() {\n  temp.a = this.f(1, 2,);\n  temp.b = 2 ^ 3 * 4 % 5;\n}\n")]
	public void Gs2ReadsBackTheSame(string code)
	{
		var read = Language.Read(code);
		Assert.Equal(Reading.Gs2, read.Reading);
		var printer = new Printer();
		foreach (var item in read.Script!.Items) printer.Item(item);
		var again = Parser.Parse(printer.ToString(), Dialect.Gs2);
		Assert.True(again.Success, again.Error);
		Assert.True(SyntaxEquality.Equal(read.Script, again.Script), printer.ToString());
	}

	[Fact]
	public void Gs2HasNoEmptyArguments() => Assert.False(Parser.Parse("function onCreated() { echo(1,,2); }\n", Dialect.Gs2).Success);

	[Fact]
	public void Gs2PowerBindsTighterThanProducts()
	{
		var read = Parser.Parse("function f() { temp.x = 2 * 3 ^ 2; }\n", Dialect.Gs2);
		var assign = (BinaryExpr)((ExprStmt)((FunctionDecl)read.Script!.Items[0]).Body[0]).Expression;
		Assert.Equal("*", ((BinaryExpr)assign.Right).Op);
	}
}
