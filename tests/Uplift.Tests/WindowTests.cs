using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Uplift.Gui;
using Uplift.Gui.ViewModels;
using Uplift.Gui.Views;
using Xunit;
using Uplift;

[assembly: AvaloniaTestApplication(typeof(Uplift.Tests.TestApp))]

namespace Uplift.Tests;

public static class TestApp
{
	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests
{
	private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleServer");

	private static void WaitFor(Func<bool> done)
	{
		var until = DateTime.UtcNow.AddSeconds(60);
		while (!done() && DateTime.UtcNow < until)
		{
			Dispatcher.UIThread.RunJobs();
			Thread.Sleep(20);
		}
		Dispatcher.UIThread.RunJobs();
	}

	[AvaloniaTheory]
	[InlineData(1)]
	[InlineData(2)]
	public void WindowScansAndPreviews(int theme)
	{
		// UPLIFT_TEST_SERVER: scan another server instead of the sample (for a look at a big one).
		var other = Environment.GetEnvironmentVariable("UPLIFT_TEST_SERVER");
		var model = new MainViewModel(new Settings { Theme = theme }) { ServerFolder = other is { Length: > 0 } ? other : Fixture };
		Application.Current!.RequestedThemeVariant = theme == 2 ? ThemeVariant.Dark : ThemeVariant.Light;
		var window = new MainWindow { DataContext = model, Width = 1400, Height = 900 };
		window.Show();
		model.ScanCommand.Execute(null);
		WaitFor(() => !model.IsBusy && model.Files.Count > 0);
		if (other is { Length: > 0 })
		{
			model.StateFilter = 1;
			model.SelectedFile = model.VisibleFiles.FirstOrDefault(f => f.Scan.Kind == ScriptKind.LevelNpc && f.Scan.Gs1 is > 1 and < 6);
			WaitFor(() => model.PreviewParts.Count > 0);
		}
		else
		{
			Assert.Equal(7, model.Files.Count);
			model.SelectedFile = model.Files.First(f => f.Path.EndsWith("square.nw", StringComparison.Ordinal));
			WaitFor(() => model.PreviewParts.Count > 0);
			Assert.Contains("function onPlayerChats()", model.PreviewConverted);
			if (Environment.GetEnvironmentVariable("UPLIFT_SCREENSHOTS") is { Length: > 0 }) model.ServerFolder = "/srv/sample-server";
		}
		Thread.Sleep(200);
		Dispatcher.UIThread.RunJobs();
		if (Environment.GetEnvironmentVariable("UPLIFT_SCREENSHOTS") is { Length: > 0 } folder)
		{
			Directory.CreateDirectory(folder);
			window.CaptureRenderedFrame()?.Save(Path.Combine(folder, (other is { Length: > 0 } ? "server-" : "uplift-") + (theme == 2 ? "dark.png" : "light.png")));
		}
		window.Close();
	}
}
