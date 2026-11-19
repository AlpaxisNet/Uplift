using Uplift.Conversion;
using Uplift.Jobs;
using Uplift.Syntax;
using Xunit;

namespace Uplift.Tests;

/// <summary>Uplift's own GS1 and GS2 reading: what a part means, and that printed GS2 reads back as the same tree.</summary>
public sealed class ParserTests
{
	private static string Convert(string code, ScriptKind kind = ScriptKind.LevelNpc, ScriptSide side = ScriptSide.Server)
	{
		var result = Workers.Run(() => new Gs1Converter().Convert(code, kind, side)).GetAwaiter().GetResult();
		Assert.True(result.Status == PartStatus.Converted, result.Status + ": " + result.Reason);
		return result.Text;
	}

	[Theory]
	[InlineData("setimg door.png;", "setimg(\"door.png\");")]
	[InlineData("setimgpart a.png,0,0,32,32;", "setimgpart(\"a.png\", 0, 0, 32, 32);")]
	[InlineData("message Hello, #n!;", "message(\"Hello, \" @ player.nick @ \"!\");")]
	[InlineData("say2 \"quoted\";", "say2(\"\\\"quoted\\\"\");")]
	[InlineData("openurl http://example.com/a;", "openurl(\"http://example.com/a\");")]
	[InlineData("setplayerprop #c,Hi #a;", "player.chat = \"Hi \" @ player.account;")]
	[InlineData("setcharprop #3,head1.png;", "headimg = \"head1.png\";")]
	[InlineData("setcharprop #P2,hat.png;", "attr[2] = \"hat.png\";")]
	[InlineData("setstring client.x,a,b;", "client.x = \"a,b\";")]
	[InlineData("set client.done;", "client.done = 1;")]
	[InlineData("unset client.done;", "client.done = \"\";")]
	[InlineData("tokenize #c;", "tokens = player.chat.tokenize();")]
	[InlineData("setplayerdir down;", "player.dir = 2;")]
	[InlineData("sleep0.5;", "sleep(0.5);")]
	[InlineData("this.x = #v(this.y) + 1;", "this.x = float(this.y) + 1;")]
	[InlineData("this.x = strlen(#c);", "this.x = player.chat.length();")]
	[InlineData("this.x = playerx + playerscount;", "this.x = player.x + arraylen(level.players);")]
	[InlineData("putnpc2 1,2,{join x;};", "putnpc2(1, 2, \"join(\\\"x\\\");\\n\");")]
	[InlineData("setimgpart a.png,,0,32,32;", "setimgpart(\"a.png\", \"\", 0, 32, 32);")]
	public void Gs1CommandsMeanTheirGs2(string statement, string expected) =>
		Assert.Contains(expected, Convert("if (created) {\n  " + statement + "\n}\n"));

	[Fact]
	public void ComparingEqualsBindsAfterAndOr()
	{
		var text = Convert("if (playertouchsme && this.open = 0) hide;\n");
		// (playertouchsme && this.open) == 0: true in created, when playertouchsme is false.
		Assert.Contains("function onCreated() {\n  hide();", text);
	}

	[Fact]
	public void LessGreaterIsNotEqual() => Assert.Contains("if (this.a != 1)", Convert("if (created) { if (this.a <> 1) hide; }\n"));

	[Fact]
	public void GlobalFunctionsInBlocksAreTheScripts()
	{
		var text = Convert("if (created) {\n  function foo() { hide; }\n  foo();\n}\n");
		Assert.Contains("function foo() {\n  hide();\n}", text);
	}

	[Theory]
	[InlineData("function onCreated() {\n  this.a = 1, this.join(\"x\");\n}\n")]
	[InlineData("function onCreated() {\n  temp.v = x in |1, 3| ? \"a\" : 'b';\n  for (temp.i : this.list) echo(temp.i SPC NL @ TAB);\n}\n")]
	[InlineData("function onCreated() {\n  new GuiWindowCtrl(\"W\") {\n    profile = \"GuiBlueWindowProfile\";\n    new GuiButtonCtrl(\"B\") {\n      text = \"OK\";\n    };\n  };\n}\n")]
	[InlineData("enum { A, B = 2 }\nconst C = 3;\nfunction onCreated() {\n  switch (this.x) {\n    case A:\n    case \"b\":\n      break;\n    default:\n      this.y = -(-1);\n  }\n}\n")]
	[InlineData("function onCreated() {\n  this.f = function(a) { return a * 2; };\n  this.(\"x\" @ 1) = new[3];\n  do { this.i++; } while (this.i < 3 && !this.stop);\n}\n")]
	public void Gs2IsGs2(string code)
	{
		var read = Language.Read(code);
		Assert.Equal(Reading.Gs2, read.Reading);
		var printed = Printed(read.Script!);
		var again = Parser.Parse(printed, Dialect.Gs2);
		Assert.True(again.Success, again.Error);
		Assert.True(SyntaxEquality.Equal(read.Script, again.Script), printed);
	}

	private static string Printed(Script script)
	{
		var printer = new Printer();
		foreach (var item in script.Items) printer.Item(item);
		return printer.ToString();
	}

	[Theory]
	[InlineData("if (created) { this.x = 1; }\n", Reading.Gs2WithGs1MainCode)]
	[InlineData("dontblock;\n", Reading.Gs2WithGs1MainCode)]
	[InlineData("hide;\n", Reading.Gs2)]
	[InlineData("//#GS2\nif (created) { this.x = 1; }\n", Reading.Gs2)]
	[InlineData("// note\n//#GS1\n//#GS2\nfunction onCreated() { this.x = 1; }\n", Reading.Gs2WithGs1MainCode)]
	[InlineData("timeout = 0.05;\n", Reading.Gs2WithGs1MainCode)]
	[InlineData("function onCreated() { this.x = 1; }\ndontblock;\n", Reading.Gs2)]
	[InlineData("setimg door.png;\n", Reading.Gs1)]
	[InlineData("function onCreated() { echo(\"a\" SPC \"b\"); }\nif (playerenters) setimg a.png;\n", Reading.Gs2WithGs1MainCode)]
	[InlineData("function onCreated() {\n  this.x = (;\n}\n", Reading.Broken)]
	[InlineData("function onCreated() { this.x = 1; }\n", Reading.Gs2)]
	public void LanguageIsDecided(string code, Reading expected) => Assert.Equal(expected, Language.Read(code).Reading);

	[Fact]
	public void OneGs1LineInAGs2FunctionIsReadAsGs1Alone()
	{
		// The GS2 around it keeps GS2's meaning (strequals' arguments are values, not GS1 text).
		var text = Convert("function onPlayerChats() {\n  if (strequals(player.chat, \"hi\")) setimg a.png;\n}\nif (created) { show; }\n");
		Assert.Contains("strequals(player.chat, \"hi\")", text);
		Assert.Contains("setimg(\"a.png\");", text);
	}
}
