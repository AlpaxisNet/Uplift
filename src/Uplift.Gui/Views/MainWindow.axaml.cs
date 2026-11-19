using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Uplift.Gui.ViewModels;

namespace Uplift.Gui.Views;

public sealed partial class MainWindow : Window
{
	private MainViewModel? _model;
	private readonly TextEditor _original;
	private readonly TextEditor _converted;

	public MainWindow()
	{
		AvaloniaXamlLoader.Load(this);
		_original = this.FindControl<TextEditor>("OriginalEditor")!;
		_converted = this.FindControl<TextEditor>("ConvertedEditor")!;
		ApplyHighlighting();
		ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
		Closing += (_, _) => _model?.SaveWindowSize(Width, Height);
	}

	private void ApplyHighlighting()
	{
		var definition = Highlighting.For(ActualThemeVariant);
		_original.SyntaxHighlighting = definition;
		_converted.SyntaxHighlighting = definition;
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_model != null) _model.PropertyChanged -= ModelChanged;
		_model = DataContext as MainViewModel;
		if (_model == null) return;
		_model.PropertyChanged += ModelChanged;
		_model.PickFolder = PickFolderAsync;
		_model.Confirm = (title, text) => ConfirmDialog.AskAsync(this, title, text);
		_model.OpenFolder = path =>
		{
			if (Directory.Exists(path)) _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
		};
	}

	private void ModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_model == null) return;
		if (e.PropertyName == nameof(MainViewModel.PreviewOriginal)) Show(_original, _model.PreviewOriginal);
		else if (e.PropertyName == nameof(MainViewModel.PreviewConverted)) Show(_converted, _model.PreviewConverted);
		else if (e.PropertyName == nameof(MainViewModel.PreviewFocusLine))
		{
			// A few lines of context above the first change.
			var line = Math.Max(1, _model.PreviewFocusLine - 3);
			ScrollToTop(_original, line);
			ScrollToTop(_converted, line);
		}
	}

	// Puts a line at the top of the editor (the plain ScrollTo centres it).
	private static void ScrollToTop(TextEditor editor, int line) =>
		editor.ScrollTo(Math.Clamp(line, 1, Math.Max(1, editor.Document.LineCount)), 0, VisualYPosition.LineTop, 0, 0);

	private static void Show(TextEditor editor, string text)
	{
		editor.Text = text.Replace("\r", "", StringComparison.Ordinal);
		editor.ScrollToHome();
	}

	private async Task<string?> PickFolderAsync(string title, string? start)
	{
		var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
		if (start is { Length: > 0 } && Directory.Exists(start)) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
		var folders = await StorageProvider.OpenFolderPickerAsync(options);
		return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
	}
}

/// <summary>A yes/no question in its own small window.</summary>
public static class ConfirmDialog
{
	public static async Task<bool> AskAsync(Window owner, string title, string text)
	{
		var dialog = new Window
		{
			Title = title,
			Width = 520,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false,
		};
		var yes = new Button { Content = "Continue", Classes = { "accent" }, IsDefault = true, MinWidth = 100, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
		var no = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
		yes.Click += (_, _) => dialog.Close(true);
		no.Click += (_, _) => dialog.Close(false);
		dialog.Content = new StackPanel
		{
			Margin = new Thickness(20),
			Spacing = 16,
			Children =
			{
				new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
				new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
				new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } },
			},
		};
		return await dialog.ShowDialog<bool>(owner);
	}
}
