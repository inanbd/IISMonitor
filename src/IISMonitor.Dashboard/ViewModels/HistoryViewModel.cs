using System.Collections.ObjectModel;
using System.Windows.Input;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Protocol;
using IISMonitor.Dashboard.Controls;

namespace IISMonitor.Dashboard.ViewModels;

public sealed record KindOption(EntityKind Kind, string Label);

public sealed record RangeOption(TimeSpan Range, string Label);

public sealed class HistoryViewModel : ObservableObject
{
    private readonly Func<IMonitorBackend?> _backend;
    private readonly Action<Exception> _onError;
    private KindOption _selectedKind;
    private string? _selectedEntity;
    private RangeOption _selectedRange;
    private HistoryResult? _result;
    private string _statusText = "Pick an app pool, site or the server to see its history.";
    private int _loadVersion;

    public HistoryViewModel(Func<IMonitorBackend?> backend, Action<Exception> onError)
    {
        _backend = backend;
        _onError = onError;
        Kinds = [new(EntityKind.AppPool, "App pool"), new(EntityKind.Site, "Site"), new(EntityKind.Server, "Server")];
        Ranges =
        [
            new(TimeSpan.FromHours(1), "Last hour"), new(TimeSpan.FromHours(6), "Last 6 hours"),
            new(TimeSpan.FromHours(24), "Last 24 hours"), new(TimeSpan.FromDays(3), "Last 3 days"),
            new(TimeSpan.FromDays(7), "Last 7 days"),
        ];
        _selectedKind = Kinds[0];
        _selectedRange = Ranges[0];
        RefreshCommand = new AsyncCommand(LoadAsync, onError, () => SelectedEntity is not null);
    }

    /// <summary>Raised on the UI thread after a query finishes.</summary>
    public event EventHandler? ResultChanged;

    public IReadOnlyList<KindOption> Kinds { get; }
    public IReadOnlyList<RangeOption> Ranges { get; }
    public ObservableCollection<string> Entities { get; } = [];
    public ICommand RefreshCommand { get; }

    public HistoryResult? Result { get => _result; private set => Set(ref _result, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public KindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is not null && Set(ref _selectedKind, value))
                _ = ReloadEntitiesAsync();
        }
    }

    public string? SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            if (Set(ref _selectedEntity, value) && value is not null)
                _ = SafeLoadAsync();
        }
    }

    public RangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && Set(ref _selectedRange, value) && SelectedEntity is not null)
                _ = SafeLoadAsync();
        }
    }

    public IReadOnlyList<ChartSeries> Series(ChartDefinition chart)
    {
        if (Result is not { } result)
            return [];

        var times = result.Timestamps
            .Select(ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToOADate())
            .ToArray();
        var list = new List<ChartSeries>();
        foreach (var key in chart.MetricKeys)
        {
            if (!result.Series.TryGetValue(key, out var values) || MetricCatalog.Find(result.Kind, key) is not { } metric)
                continue;

            var xs = new List<double>(values.Count);
            var ys = new List<double>(values.Count);
            for (var i = 0; i < values.Count && i < times.Length; i++)
            {
                if (values[i] is { } v && !double.IsNaN(v))
                {
                    xs.Add(times[i]);
                    ys.Add(v);
                }
            }

            list.Add(new ChartSeries(metric.DisplayName, xs.ToArray(), ys.ToArray()));
        }

        return list;
    }

    public (double From, double To) Range()
    {
        var now = DateTime.Now;
        return (now.Subtract(SelectedRange.Range).ToOADate(), now.ToOADate());
    }

    public async Task ReloadEntitiesAsync()
    {
        if (_backend() is not { } backend)
            return;

        try
        {
            var previous = SelectedEntity;
            var names = await backend.ListHistoryEntitiesAsync(SelectedKind.Kind);
            Entities.Clear();
            foreach (var name in names)
                Entities.Add(name);

            if (names.Count == 0)
            {
                SelectedEntity = null;
                Result = null;
                StatusText = "No history recorded yet. Rows are written every history interval (10 seconds by default).";
                ResultChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Set the field directly so the chart reloads even when the selection stays the same.
            _selectedEntity = previous is not null && names.Contains(previous) ? previous : names[0];
            Raise(nameof(SelectedEntity));
            await SafeLoadAsync();
        }
        catch (Exception e)
        {
            _onError(e);
        }
    }

    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception e)
        {
            StatusText = "Loading history failed: " + e.Message;
        }
    }

    private async Task LoadAsync()
    {
        if (_backend() is not { } backend || SelectedEntity is not { } entity)
            return;

        var version = ++_loadVersion;
        StatusText = $"Loading {entity}…";
        var now = DateTime.UtcNow;
        var result = await backend.QueryHistoryAsync(new HistoryQuery
        {
            Kind = SelectedKind.Kind,
            Name = entity,
            FromUtc = now - SelectedRange.Range,
            ToUtc = now,
            MaxPoints = 1500,
        });

        if (version != _loadVersion)
            return;

        Result = result;
        StatusText = result.Timestamps.Count == 0
            ? $"No history for {entity} in this range."
            : $"{result.Timestamps.Count:N0} points, each covering {FormatSeconds(result.BucketSeconds)}.";
        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatSeconds(int seconds) => seconds switch
    {
        < 60 => $"{seconds} seconds",
        < 3600 => $"{seconds / 60.0:0.#} minutes",
        _ => $"{seconds / 3600.0:0.#} hours",
    };
}
