using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace IISMonitor.Dashboard.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

/// <summary>Command for async work; disabled while running, errors go to the supplied handler.</summary>
public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        catch (Exception e)
        {
            onError(e);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Like <see cref="AsyncCommand"/>, but passes the command parameter on (for example the grid row a context menu was opened on).</summary>
public sealed class AsyncCommand<T>(Func<T?, Task> execute, Action<Exception> onError, Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter as T) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter as T);
        }
        catch (Exception e)
        {
            onError(e);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>
/// A collection whose contents can be replaced with a single Reset notification, so a grid with
/// thousands of rows (and its sort order) is rebuilt once instead of once per row.
/// </summary>
public sealed class RowCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
