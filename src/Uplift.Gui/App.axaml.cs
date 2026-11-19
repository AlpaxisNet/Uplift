using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Uplift.Gui.ViewModels;
using Uplift.Gui.Views;

namespace Uplift.Gui;

public sealed class App : Application
{
	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		var settings = Settings.Load();
		ApplyTheme(settings.Theme);
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			var model = new MainViewModel(settings);
			desktop.MainWindow = new MainWindow { DataContext = model, Width = settings.Width, Height = settings.Height };
			desktop.Exit += (_, _) => settings.Save();
		}
		base.OnFrameworkInitializationCompleted();
	}

	/// <summary>0: the system's theme, 1: light, 2: dark.</summary>
	public static void ApplyTheme(int theme)
	{
		if (Current == null) return;
		Current.RequestedThemeVariant = theme switch
		{
			1 => ThemeVariant.Light,
			2 => ThemeVariant.Dark,
			_ => ThemeVariant.Default,
		};
	}
}
