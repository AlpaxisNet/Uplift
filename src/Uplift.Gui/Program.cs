using Avalonia;

namespace Uplift.Gui;

public static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Settings.WriteCrashLog(e.ExceptionObject as Exception);
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			Settings.WriteCrashLog(e.Exception);
			e.SetObserved();
		};
		return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
