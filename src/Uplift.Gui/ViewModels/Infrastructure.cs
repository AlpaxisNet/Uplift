using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Uplift.Gui.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		Raise(name!);
		return true;
	}

	protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command that runs an action (or an asynchronous one), enabled while its condition holds.</summary>
public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
	public RelayCommand(Action execute, Func<bool>? canExecute = null) : this(() =>
	{
		execute();
		return Task.CompletedTask;
	}, canExecute)
	{
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

	public async void Execute(object? parameter)
	{
		try
		{
			await execute();
		}
		catch (Exception e)
		{
			Settings.WriteCrashLog(e);
		}
	}

	public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
