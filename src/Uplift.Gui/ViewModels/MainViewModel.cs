using System.Collections.ObjectModel;
using Uplift.Conversion;
using Uplift.Jobs;
using Uplift.Verification;

namespace Uplift.Gui.ViewModels;

/// <summary>The main window: a server, what to convert, the scanned files, a preview, and the conversion.</summary>
public sealed class MainViewModel : ViewModelBase
{
	private readonly Settings _settings;
	private CancellationTokenSource? _cancel;
	private IReadOnlyCollection<string> _events = [];
	private string _serverFolder;
	private string _outputFolder;
	private bool _outputToFolder = true;
	private bool _copyUnchanged;
	private bool _convertServer = true;
	private bool _convertClient = true;
	private bool _repair = true;
	private bool _fixAlreadyGs2 = true;
	private bool _isBusy;
	private string _status = "Choose a server folder (the one with weapons/, scripts/, npcs/ and levels/) and scan it.";
	private double _progress;
	private bool _progressIndeterminate;
	private string _filter = "";
	private int _stateFilter;
	private IReadOnlyList<FileItem> _visibleFiles = [];
	private FileItem? _selectedFile;
	private string _previewOriginal = "";
	private string _previewConverted = "";
	private string _previewTitle = "Select a file to preview its conversion.";
	private IReadOnlyList<PartRow> _previewParts = [];
	private int _previewVersion;
	private string? _resultText;
	private string? _reportFolder;
	private string? _resultOutputFolder;
	private string _filesSummary = "";

	public MainViewModel(Settings settings)
	{
		_settings = settings;
		_serverFolder = settings.ServerFolder ?? "";
		_outputFolder = settings.OutputFolder ?? "";
		foreach (var kind in ScriptKinds.All)
			Kinds.Add(new KindOption(kind, Server.ServerLayout.DefaultFolders[kind] + "/", ApplyFilter));
		ResetSwitches(null);
		BrowseServerCommand = new RelayCommand(BrowseServerAsync, () => !IsBusy);
		ScanCommand = new RelayCommand(ScanAsync, () => !IsBusy && ServerFolder.Trim().Length > 0);
		BrowseOutputCommand = new RelayCommand(BrowseOutputAsync, () => !IsBusy);
		ConvertCommand = new RelayCommand(ConvertAsync, () => !IsBusy && Workspace != null && Files.Count > 0);
		CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy);
		CheckAllCommand = new RelayCommand(() => SetChecks(_ => true));
		CheckNoneCommand = new RelayCommand(() => SetChecks(_ => false));
		CheckNeedingCommand = new RelayCommand(() => SetChecks(f => !f.IsGs2));
		OpenOutputCommand = new RelayCommand(() => OpenFolder?.Invoke(_resultOutputFolder ?? ""), () => _resultOutputFolder != null);
		OpenReportCommand = new RelayCommand(() => OpenFolder?.Invoke(_reportFolder ?? ""), () => _reportFolder != null);
		DismissResultCommand = new RelayCommand(() => ResultText = null);
	}

	// ------------------------------------------------------------ services the window gives

	/// <summary>Asks for a folder (title, start folder); null when cancelled.</summary>
	public Func<string, string?, Task<string?>>? PickFolder { get; set; }

	/// <summary>Asks a yes/no question (title, text).</summary>
	public Func<string, string, Task<bool>>? Confirm { get; set; }

	/// <summary>Opens a folder in the system's file manager.</summary>
	public Action<string>? OpenFolder { get; set; }

	// ------------------------------------------------------------ commands

	public RelayCommand BrowseServerCommand { get; }

	public RelayCommand ScanCommand { get; }

	public RelayCommand BrowseOutputCommand { get; }

	public RelayCommand ConvertCommand { get; }

	public RelayCommand CancelCommand { get; }

	public RelayCommand CheckAllCommand { get; }

	public RelayCommand CheckNoneCommand { get; }

	public RelayCommand CheckNeedingCommand { get; }

	public RelayCommand OpenOutputCommand { get; }

	public RelayCommand OpenReportCommand { get; }

	public RelayCommand DismissResultCommand { get; }

	// ------------------------------------------------------------ state

	public Workspace? Workspace { get; private set; }

	public ObservableCollection<KindOption> Kinds { get; } = [];

	public ObservableCollection<SwitchOption> Rules { get; } = [];

	public ObservableCollection<SwitchOption> Fixes { get; } = [];

	public List<FileItem> Files { get; private set; } = [];

	public string[] StateFilters { get; } = ["All files", "To convert (GS1)", "Broken or unreadable", "GS2 already"];

	public string[] Themes { get; } = ["System theme", "Light", "Dark"];

	public string Plugins => Workspace is { } w && w.Registry.LoadedPlugins.Count > 0 ? "Plugins: " + string.Join(", ", w.Registry.LoadedPlugins) : "";

	public int Theme
	{
		get => _settings.Theme;
		set
		{
			if (_settings.Theme == value) return;
			_settings.Theme = value;
			App.ApplyTheme(value);
			_settings.Save();
			Raise(nameof(Theme));
		}
	}

	public string ServerFolder
	{
		get => _serverFolder;
		set
		{
			if (Set(ref _serverFolder, value)) ScanCommand.Refresh();
		}
	}

	public string OutputFolder
	{
		get => _outputFolder;
		set => Set(ref _outputFolder, value);
	}

	public bool OutputToFolder
	{
		get => _outputToFolder;
		set
		{
			if (Set(ref _outputToFolder, value)) Raise(nameof(InPlace));
		}
	}

	public bool InPlace
	{
		get => !_outputToFolder;
		set => OutputToFolder = !value;
	}

	public bool CopyUnchanged
	{
		get => _copyUnchanged;
		set => Set(ref _copyUnchanged, value);
	}

	public bool ConvertServer
	{
		get => _convertServer;
		set => Set(ref _convertServer, value);
	}

	public bool ConvertClient
	{
		get => _convertClient;
		set => Set(ref _convertClient, value);
	}

	public bool Repair
	{
		get => _repair;
		set => Set(ref _repair, value);
	}

	public bool FixAlreadyGs2
	{
		get => _fixAlreadyGs2;
		set => Set(ref _fixAlreadyGs2, value);
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (!Set(ref _isBusy, value)) return;
			Raise(nameof(IsIdle));
			foreach (var c in new[] { BrowseServerCommand, ScanCommand, BrowseOutputCommand, ConvertCommand, CancelCommand }) c.Refresh();
		}
	}

	public bool IsIdle => !_isBusy;

	public string Status
	{
		get => _status;
		private set => Set(ref _status, value);
	}

	public double Progress
	{
		get => _progress;
		private set => Set(ref _progress, value);
	}

	public bool ProgressIndeterminate
	{
		get => _progressIndeterminate;
		private set => Set(ref _progressIndeterminate, value);
	}

	public string Filter
	{
		get => _filter;
		set
		{
			if (Set(ref _filter, value)) ApplyFilter();
		}
	}

	public int StateFilter
	{
		get => _stateFilter;
		set
		{
			if (Set(ref _stateFilter, value)) ApplyFilter();
		}
	}

	public IReadOnlyList<FileItem> VisibleFiles
	{
		get => _visibleFiles;
		private set => Set(ref _visibleFiles, value);
	}

	public string FilesSummary
	{
		get => _filesSummary;
		private set => Set(ref _filesSummary, value);
	}

	public FileItem? SelectedFile
	{
		get => _selectedFile;
		set
		{
			if (Set(ref _selectedFile, value)) _ = PreviewAsync(value);
		}
	}

	public string PreviewOriginal
	{
		get => _previewOriginal;
		private set => Set(ref _previewOriginal, value);
	}

	public string PreviewConverted
	{
		get => _previewConverted;
		private set => Set(ref _previewConverted, value);
	}

	public string PreviewTitle
	{
		get => _previewTitle;
		private set => Set(ref _previewTitle, value);
	}

	/// <summary>The first line (1-based) where the conversion differs from the original, for the editors to show.</summary>
	public int PreviewFocusLine
	{
		get => _previewFocusLine;
		private set
		{
			_previewFocusLine = value;
			Raise(nameof(PreviewFocusLine));
		}
	}

	private int _previewFocusLine = 1;

	private static int FirstDifference(string a, string b)
	{
		var x = a.Replace("\r", "", StringComparison.Ordinal).Split('\n');
		var y = b.Replace("\r", "", StringComparison.Ordinal).Split('\n');
		for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
			if (x[i] != y[i]) return i + 1;
		return 1;
	}

	public IReadOnlyList<PartRow> PreviewParts
	{
		get => _previewParts;
		private set => Set(ref _previewParts, value);
	}

	public string? ResultText
	{
		get => _resultText;
		private set
		{
			if (Set(ref _resultText, value)) Raise(nameof(HasResult));
		}
	}

	public bool HasResult => _resultText != null;

	// ------------------------------------------------------------ actions

	public void SaveWindowSize(double width, double height)
	{
		_settings.Width = width;
		_settings.Height = height;
		_settings.Save();
	}

	private async Task BrowseServerAsync()
	{
		if (PickFolder == null) return;
		var folder = await PickFolder("Choose the server's folder", ServerFolder);
		if (folder == null) return;
		ServerFolder = folder;
		await ScanAsync();
	}

	private async Task BrowseOutputAsync()
	{
		if (PickFolder == null) return;
		var folder = await PickFolder("Choose a folder for the converted scripts", OutputFolder.Length > 0 ? OutputFolder : null);
		if (folder != null) OutputFolder = folder;
	}

	private async Task ScanAsync()
	{
		var root = ServerFolder.Trim();
		if (!Directory.Exists(root))
		{
			Status = "No such folder: " + root;
			return;
		}
		IsBusy = true;
		_cancel = new CancellationTokenSource();
		var token = _cancel.Token;
		ResultText = null;
		try
		{
			Status = "Opening " + root;
			var progress = new Progress<JobProgress>(ShowProgress);
			var (workspace, scan, events) = await Task.Run(() =>
			{
				var w = Workspace.Open(root, [System.IO.Path.Combine(AppContext.BaseDirectory, "plugins")]);
				if (!w.Layout.LooksLikeServer())
					throw new InvalidDataException($"{root} has none of the folders npcs/, weapons/, scripts/ or levels/ (other names can be set in uplift.json)");
				var s = ConversionRun.Scan(w, ScriptKinds.All, Workers.DefaultParallelism, progress, token);
				((IProgress<JobProgress>)progress).Report(new JobProgress("Finding custom events", 0, 1));
				var e = w.CustomEvents(Workers.DefaultParallelism, token);
				return (w, s, e);
			}, token);
			Workspace = workspace;
			_events = events;
			_settings.ServerFolder = root;
			_settings.Save();
			if (OutputFolder.Length == 0) OutputFolder = root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + "-GS2";
			ResetSwitches(workspace);
			Files = scan.Files.Select(f => new FileItem(f)).ToList();
			foreach (var f in Files) f.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(FileItem.IsChecked) && !_bulk) UpdateSummary();
			};
			foreach (var kind in Kinds)
			{
				var files = scan.OfKind(kind.Kind).ToList();
				kind.ToConvert = files.Count(f => f.State == FileState.NeedsConversion);
				var folder = workspace.Layout.FindFolder(kind.Kind);
				kind.Summary = folder == null ? "no " + workspace.Layout.Folder(kind.Kind) + "/ folder"
					: Plural(files.Count, "file") + " · " + Plural(files.Sum(f => f.Gs1), "GS1 part") + (files.Sum(f => f.Broken) is var b && b > 0 ? $" · {b:N0} broken" : "");
			}
			ApplyFilter();
			Raise(nameof(Plugins));
			var gs1 = scan.Files.Sum(f => f.Gs1);
			Status = $"Scanned {Plural(scan.Files.Count, "file")} in {scan.Elapsed.TotalSeconds:0.0} s: {Plural(gs1, "GS1 part")} to convert, {scan.Files.Sum(f => f.Broken):N0} broken. {Plural(events.Count, "custom event")} found." +
			         (workspace.Registry.PluginErrors.Count > 0 ? " Plugin errors: " + string.Join("; ", workspace.Registry.PluginErrors) : "");
		}
		catch (OperationCanceledException)
		{
			Status = "Scan cancelled.";
		}
		catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or AggregateException)
		{
			Status = "Could not scan: " + (e is AggregateException a ? a.InnerException?.Message : e.Message);
		}
		finally
		{
			Progress = 0;
			ProgressIndeterminate = false;
			IsBusy = false;
		}
	}

	private static string Plural(int count, string word) => $"{count:N0} {word}{(count == 1 ? "" : "s")}";

	private void ResetSwitches(Workspace? workspace)
	{
		var registry = workspace?.Registry ?? Uplift.Plugins.UpliftRegistry.CreateDefault();
		Rules.Clear();
		foreach (var r in registry.Rules)
			Rules.Add(new SwitchOption(r.Id, r.Title, r.Description, workspace?.IsEnabledByDefault(r.Id, r.EnabledByDefault) ?? r.EnabledByDefault));
		Fixes.Clear();
		foreach (var f in registry.TextFixes)
			Fixes.Add(new SwitchOption(f.Id, f.Title, f.Description, workspace?.IsEnabledByDefault(f.Id, f.EnabledByDefault) ?? f.EnabledByDefault));
	}

	private void ShowProgress(JobProgress p)
	{
		ProgressIndeterminate = p.Total <= 1;
		Progress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
		Status = p.Total > 1 ? $"{p.Phase}: {p.Done:N0} of {p.Total:N0}  {p.Current}" : p.Phase + "...";
	}

	private bool _bulk;

	private void SetChecks(Func<FileItem, bool> check)
	{
		_bulk = true;
		foreach (var f in VisibleFiles) f.IsChecked = check(f);
		_bulk = false;
		UpdateSummary();
	}

	private void ApplyFilter()
	{
		var chosen = Kinds.Where(k => k.IsChecked).Select(k => k.Kind).ToHashSet();
		var text = _filter.Trim();
		VisibleFiles = Files.Where(f => chosen.Contains(f.Scan.Kind))
			.Where(f => text.Length == 0 || f.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
			.Where(f => _stateFilter switch
			{
				1 => f.IsGs1,
				2 => f.IsBroken,
				3 => f.IsGs2,
				_ => true,
			}).ToList();
		UpdateSummary();
	}

	private void UpdateSummary() =>
		FilesSummary = Files.Count == 0 ? "" : $"{VisibleFiles.Count:N0} shown · {Files.Count(f => f.IsChecked && Kinds.First(k => k.Kind == f.Scan.Kind).IsChecked):N0} chosen for conversion";

	private ConvertOptions Options(bool preview) => new()
	{
		Kinds = Kinds.Where(k => k.IsChecked).Select(k => k.Kind).ToHashSet(),
		Sides = new HashSet<ScriptSide>((ConvertServer ? [ScriptSide.Server] : Array.Empty<ScriptSide>()).Concat(ConvertClient ? [ScriptSide.Client] : [])),
		Switches = Rules.Concat(Fixes).Where(s => s.IsChecked).Select(s => s.Id).ToHashSet(StringComparer.Ordinal),
		FixAlreadyGs2 = FixAlreadyGs2,
		Repair = Repair,
		Output = OutputToFolder ? OutputMode.Folder : OutputMode.InPlace,
		OutputFolder = OutputToFolder ? OutputFolder.Trim() : null,
		CopyUnchanged = CopyUnchanged,
		Parallelism = Workers.DefaultParallelism,
		DryRun = preview,
		Only = preview ? null : ChosenFiles(),
	};

	// The checked files; null when every file of the chosen kinds is checked.
	private HashSet<string>? ChosenFiles()
	{
		var kinds = Kinds.Where(k => k.IsChecked).Select(k => k.Kind).ToHashSet();
		var inKinds = Files.Where(f => kinds.Contains(f.Scan.Kind)).ToList();
		return inKinds.All(f => f.IsChecked) ? null : inKinds.Where(f => f.IsChecked).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
	}

	private async Task PreviewAsync(FileItem? item)
	{
		var version = ++_previewVersion;
		if (item == null || Workspace == null)
		{
			PreviewTitle = "Select a file to preview its conversion.";
			PreviewOriginal = PreviewConverted = "";
			PreviewParts = [];
			return;
		}
		PreviewTitle = item.Path + " — converting...";
		try
		{
			var outcome = await ConversionRun.PreviewAsync(Workspace, Options(preview: true), _events, item.Scan.Kind, item.Scan.File);
			if (version != _previewVersion) return;
			PreviewOriginal = outcome.OriginalText ?? "";
			PreviewConverted = outcome.Unreadable != null ? "// " + outcome.Unreadable : outcome.NewText ?? outcome.OriginalText ?? "";
			PreviewFocusLine = FirstDifference(PreviewOriginal, PreviewConverted);
			PreviewParts = outcome.Parts.Select(p => new PartRow(p)).ToList();
			var converted = outcome.Parts.Count(p => p.Result == PartStatus.Converted);
			var failed = outcome.Parts.Count(p => p.Result == PartStatus.Failed);
			PreviewTitle = $"{item.Path} — " + (outcome.Unreadable ?? (outcome.Changed
				? $"{converted} part(s) converted" + (failed > 0 ? $", {failed} not converted" : "") + (outcome.Repairs.Count > 0 ? ", repaired" : "")
				: failed > 0 ? $"{failed} part(s) could not be converted" : "no change needed"));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			if (version == _previewVersion) PreviewTitle = item.Path + " — " + e.Message;
		}
	}

	private async Task ConvertAsync()
	{
		if (Workspace == null) return;
		var options = Options(preview: false);
		if (options.Kinds.Count == 0 || options.Sides.Count == 0)
		{
			Status = "Choose at least one kind of script and one side.";
			return;
		}
		if (options.Output == OutputMode.Folder)
		{
			if (options.OutputFolder is not { Length: > 0 } output)
			{
				Status = "Choose an output folder.";
				return;
			}
			if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && Confirm != null &&
			    !await Confirm("Output folder is not empty", $"{output} already has files in it. Converted files will be written over the ones with the same names. Continue?"))
				return;
		}
		else if (Confirm != null && !await Confirm("Convert in place?",
			         $"The converted scripts will be written over the server's own files in {Workspace.Root}. Every file that changes is first copied to {Workspace.Root}/.uplift/<time>/backup. Continue?"))
			return;
		IsBusy = true;
		_cancel = new CancellationTokenSource();
		var token = _cancel.Token;
		var workspace = Workspace;
		try
		{
			_settings.OutputFolder = OutputFolder;
			_settings.Save();
			var progress = new Progress<JobProgress>(ShowProgress);
			var report = await Task.Run(() => ConversionRun.Run(workspace, options, progress, token), token);
			_reportFolder = report.ReportFolder;
			_resultOutputFolder = report.OutputFolder ?? (options.Output == OutputMode.InPlace ? workspace.Root : null);
			OpenOutputCommand.Refresh();
			OpenReportCommand.Refresh();
			ResultText = $"Done in {report.Elapsed.TotalSeconds:0.0} s: {report.FilesChanged:N0} of {report.Files.Count:N0} files changed. " +
			             $"Parts: {report.Count(PartStatus.Converted):N0} converted, {report.Count(PartStatus.AlreadyGs2):N0} GS2 already, " +
			             $"{report.Count(PartStatus.Kept):N0} kept, {report.Count(PartStatus.Failed):N0} not converted. " +
			             $"Afterwards {report.CountAfter(CheckStatus.Ok):N0} parts are strict GS2." +
			             (report.Warnings.Count > 0 ? $" {report.Warnings.Count} warning(s) in the report." : "");
			Status = "Converted. Report: " + report.ReportFolder;
		}
		catch (OperationCanceledException)
		{
			Status = "Conversion cancelled (files already written stay written).";
		}
		catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or AggregateException)
		{
			Status = "Conversion failed: " + (e is AggregateException a ? a.InnerException?.Message : e.Message);
		}
		finally
		{
			Progress = 0;
			ProgressIndeterminate = false;
			IsBusy = false;
		}
	}
}
