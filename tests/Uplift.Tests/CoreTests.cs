using Uplift.Conversion;
using Uplift.Formats;
using Uplift.Jobs;
using Uplift.Rules;
using Uplift.Server;
using Uplift.Syntax;
using Uplift.Verification;
using Xunit;

namespace Uplift.Tests;

public sealed class CoreTests
{
	private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleServer");

	private static PartResult Convert(string code, ScriptKind kind = ScriptKind.LevelNpc, ScriptSide side = ScriptSide.Server, ConversionOptions? options = null) =>
		Workers.Run(() => new Gs1Converter(options).Convert(code, kind, side)).GetAwaiter().GetResult();

	private static CheckResult Check(string code, ScriptKind kind = ScriptKind.LevelNpc) => Workers.Run(() => StrictCheck.Check(code)).GetAwaiter().GetResult();

	[Fact]
	public void MainCodeBecomesOneFunctionPerEvent()
	{
		var result = Convert("if (created) {\n  setimg door.png;\n}\nif (playertouchsme && this.locked == 0) {\n  hide;\n  timeout = 3;\n}\nif (timeout) {\n  show;\n}\n");
		Assert.Equal(PartStatus.Converted, result.Status);
		Assert.Contains("function onCreated() {", result.Text);
		Assert.Contains("function onPlayerTouchsMe() {", result.Text);
		Assert.Contains("setTimer(3);", result.Text);
		Assert.Contains("function onTimeout() {", result.Text);
		Assert.Equal(CheckStatus.Ok, Check(result.Text).Status);
	}

	[Fact]
	public void CommentsAreKept()
	{
		var result = Convert("// the door\nif (created) {\n  // its picture\n  setimg door.png; // closed\n}\n");
		Assert.Equal(PartStatus.Converted, result.Status);
		Assert.Contains("// the door", result.Text);
		Assert.Contains("// its picture", result.Text);
		Assert.Contains("// closed", result.Text);
	}

	[Fact]
	public void Gs2IsLeftAlone() => Assert.Equal(PartStatus.AlreadyGs2, Convert("function onCreated() {\n  this.x = 1;\n}\n").Status);

	[Fact]
	public void BrokenCodeIsNotConverted() => Assert.Equal(PartStatus.Failed, Convert("if (created) {\n  this.x = (;\n}\n").Status);

	[Fact]
	public void RulesCanBeSwitchedOff()
	{
		const string code = "if (playerenters) {\n  callnpc -1,opened;\n}\n";
		Assert.Contains("this.trigger(\"opened\")", Convert(code).Text);
		var without = Convert(code, options: new ConversionOptions { Rules = [] });
		Assert.Equal(PartStatus.Converted, without.Status);
		Assert.DoesNotContain("this.trigger", without.Text);
	}

	[Fact]
	public void PluginRulesRun()
	{
		var registry = Plugins.UpliftRegistry.CreateDefault();
		registry.AddRule(new RenameRule());
		var result = Convert("if (created) {\n  oldcommand(1);\n}\n", options: new ConversionOptions { Rules = registry.Rules });
		Assert.Contains("newcommand(1);", result.Text);
		Assert.Contains("renamed oldcommand", result.Notes);
	}

	private sealed class RenameRule : IConversionRule
	{
		public string Id => "test-rename";

		public string Title => "oldcommand → newcommand";

		public string Description => "test";

		public Expr? RewriteExpression(Expr expression, RuleContext context)
		{
			if (expression is not CallExpr { Name: "oldcommand" } call) return null;
			context.Note("renamed oldcommand");
			return call with { Name = "newcommand" };
		}
	}

	[Fact]
	public void TextFixesNeverTouchStringsOrComments()
	{
		var fix = new RegexTextFix("t", "t", "t", @"\bfoo\b", "bar");
		var context = new TextFixContext(ScriptKind.Weapon, ScriptSide.Server);
		var result = fix.Apply("foo(); // foo\nthis.x = \"foo\"; foo = 'foo';\n", context);
		Assert.Equal("bar(); // foo\nthis.x = \"foo\"; bar = 'foo';\n", result);
	}

	[Fact]
	public void ClientPartStartsAtTheFirstRealMarker()
	{
		const string source = "putnpc2(1, 1, \"//#CLIENTSIDE\");\n/* //#CLIENTSIDE */\nthis.a = 1;\n//#CLIENTSIDE\nthis.b = 2;\n";
		Assert.Equal("this.b = 2;\n", ScriptParts.ClientPart(source));
		var joined = ScriptParts.Join(source, null, "this.c = 3;\n");
		Assert.Equal(ScriptParts.ServerPart(source), ScriptParts.ServerPart(joined));
		Assert.Equal("this.c = 3;\n", ScriptParts.ClientPart(joined));
	}

	[Fact]
	public void UnchangedFilesRenderByteForByte()
	{
		var workspace = Workspace.Open(Fixture);
		foreach (var (kind, file) in workspace.Files(ScriptKinds.All))
		{
			var (document, _) = workspace.OpenFile(kind, file, new FormatOptions { Repair = false });
			Assert.NotNull(document);
			Assert.Equal(ServerText.Read(file.FullPath), document!.Render(new Dictionary<int, string>()));
		}
	}

	[Fact]
	public void LineEndingsOfAReplacedScriptAreKept()
	{
		var text = "GRAWP001\r\nREALNAME x\r\nSCRIPT\r\nif (created) {\r\n  this.a = 1;\r\n}\r\nSCRIPTEND\r\n";
		var document = new WeaponFormat().Open(new ServerFile("/", "weapons/x.txt"), text, new FormatOptions());
		Assert.Equal("if (created) {\n  this.a = 1;\n}", document.Slots[0].Source);
		var rendered = document.Render(new Dictionary<int, string> { [0] = "function onCreated() {\n  this.a = 1;\n}\n" });
		Assert.Equal("GRAWP001\r\nREALNAME x\r\nSCRIPT\r\nfunction onCreated() {\r\n  this.a = 1;\r\n}\r\nSCRIPTEND\r\n", rendered);
	}

	[Fact]
	public void MissingNpcEndIsRepaired()
	{
		var text = "GLEVNW01\nNPC - 1 1\nthis.a = 1;\nNPC - 2 2\nthis.b = 2;\nNPCEND\n";
		var document = new NwLevelFormat().Open(new ServerFile("/", "levels/a.nw"), text, new FormatOptions());
		Assert.Equal(2, document.Slots.Count);
		Assert.Single(document.Repairs);
		Assert.Equal("GLEVNW01\nNPC - 1 1\nthis.a = 1;\nNPCEND\nNPC - 2 2\nthis.b = 2;\nNPCEND\n", document.Render(new Dictionary<int, string>()));
	}

	[Fact]
	public void EventScannerReadsGs1Forms()
	{
		var names = EventScanner.Find("callnpc testnpc(32,36),ForceUpdate;\ncallweapon(3, \"reload\");\nobj.trigger(\"Opened\", 1);\nscheduleevent(5, \"later\", 2);\n").ToHashSet();
		Assert.Equal(new HashSet<string> { "forceupdate", "reload", "opened", "later" }, names);
	}

	[Fact]
	public void SampleServerConverts()
	{
		var output = Path.Combine(Path.GetTempPath(), "uplift-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			var workspace = Workspace.Open(Fixture);
			var report = ConversionRun.Run(workspace, new ConvertOptions { OutputFolder = output, Parallelism = 2 });
			Assert.True(report.Count(PartStatus.Converted) >= 6, "converted " + report.Count(PartStatus.Converted));
			Assert.Equal(1, report.Count(PartStatus.Kept));
			Assert.Equal(1, report.Count(PartStatus.Failed));
			Assert.Equal(report.Files.Sum(f => f.Parts.Count) - 2, report.CountAfter(CheckStatus.Ok));
			Assert.True(File.Exists(Path.Combine(output, "weapons", "weapon-System%047Greeter.txt")));
			Assert.False(File.Exists(Path.Combine(output, "weapons", "weapon-System%047Clock.txt")));
			Assert.Contains("function onPlayerEnters()", File.ReadAllText(Path.Combine(output, "weapons", "weapon-System%047Greeter.txt")));
			Assert.True(File.Exists(Path.Combine(output + ".report", "parts.tsv")));
		}
		finally
		{
			if (Directory.Exists(output)) Directory.Delete(output, true);
			if (Directory.Exists(output + ".report")) Directory.Delete(output + ".report", true);
		}
	}
}
