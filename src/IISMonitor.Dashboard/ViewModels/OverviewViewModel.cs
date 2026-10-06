using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Presentation;
using IISMonitor.Dashboard.Controls;

namespace IISMonitor.Dashboard.ViewModels;

public sealed record MemoryOption(string MetricKey, string Label, string ShortLabel);

/// <summary>One app pool in the Overview's pick list; its line key doubles as the charts' legend.</summary>
public sealed class PoolToggle : ObservableObject
{
    private readonly Action<PoolToggle> _changed;
    private bool _isChecked;
    private SeriesStyle _style = SeriesStyles.ForSlot(0);

    public PoolToggle(string name, bool isChecked, Action<PoolToggle> changed)
    {
        Name = name;
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                _changed(this);
        }
    }

    public SeriesStyle Style
    {
        get => _style;
        set
        {
            if (!Set(ref _style, value))
                return;
            Raise(nameof(Stroke));
            Raise(nameof(DashArray));
        }
    }

    public Brush Stroke => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Style.ColorHex));

    public DoubleCollection? DashArray => ChartPanel.DashArray(Style.Pattern);

    /// <summary>Sets the tick without raising the change callback (used for bulk changes).</summary>
    internal void SetSilently(bool value) => Set(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>
/// The Overview tab: four charts comparing app pools (CPU, RAM, requests/sec, network), with a
/// tick list choosing which pools are drawn. Choices and colors are remembered per user.
/// </summary>
public sealed class OverviewViewModel : ObservableObject
{
    public const string MemoryKey = "memory";

    public static readonly IReadOnlyList<ChartDefinition> Charts =
    [
        new("CPU", MetricUnit.Percent, ["cpu"]),
        new("RAM", MetricUnit.Bytes, [MemoryKey]),
        new("Requests/sec", MetricUnit.PerSecond, ["rps"]),
        new("Network", MetricUnit.BytesPerSecond, ["net_total"]),
    ];

    private readonly DashboardPreferencesStore _store;
    private readonly DashboardPreferences _preferences;
    private readonly SeriesSlotRegistry _slots;
    private MemoryOption _selectedMemory;

    public OverviewViewModel(DashboardPreferencesStore store)
    {
        _store = store;
        _preferences = store.Load();
        _slots = new SeriesSlotRegistry(_preferences.PoolStyleSlots);
        MemoryOptions =
        [
            new("working_set", "Working set (RAM in use)", "working set"),
            new("private_bytes", "Private bytes (committed)", "private bytes"),
        ];
        _selectedMemory = MemoryOptions.FirstOrDefault(o => o.MetricKey == _preferences.OverviewMemoryMetric) ?? MemoryOptions[0];

        SelectAllCommand = new RelayCommand(() => SetAll(true), () => Pools.Any(p => !p.IsChecked));
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => Pools.Any(p => p.IsChecked));
    }

    /// <summary>Raised when ticks or the memory metric change, so the charts redraw.</summary>
    public event EventHandler? SelectionChanged;

    public ObservableCollection<PoolToggle> Pools { get; } = [];

    public IReadOnlyList<MemoryOption> MemoryOptions { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand SelectNoneCommand { get; }

    public MemoryOption SelectedMemory
    {
        get => _selectedMemory;
        set
        {
            if (value is null || !Set(ref _selectedMemory, value))
                return;
            _preferences.OverviewMemoryMetric = value.MetricKey;
            Save();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectionSummary => Pools.Count == 0
        ? "No app pools found yet."
        : $"{Pools.Count(p => p.IsChecked)} of {Pools.Count} shown";

    /// <summary>Metric key behind a chart's placeholder key (the RAM chart follows the memory switch).</summary>
    public string ResolveKey(string key) => key == MemoryKey ? SelectedMemory.MetricKey : key;

    public string ChartTitle(ChartDefinition chart, MonitorSnapshot? latest)
    {
        var name = chart.MetricKeys[0] == MemoryKey ? $"RAM ({SelectedMemory.ShortLabel})" : chart.Title;
        var ticked = Pools.Where(p => p.IsChecked).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ticked.Count == 0)
            return $"{name}: no app pools ticked";
        if (latest is null)
            return name;

        var key = ResolveKey(chart.MetricKeys[0]);
        double? total = null;
        foreach (var pool in latest.AppPools.Where(p => ticked.Contains(p.Name)))
        {
            if (MetricCatalog.Extract(pool, latest.IntervalSeconds).GetValueOrDefault(key) is { } value)
                total = (total ?? 0) + value;
        }

        return $"{name}: {MetricFormatter.Format(chart.Unit, total)} total";
    }

    /// <summary>Adds new pools (ticked unless the user hid them before) and drops removed ones.</summary>
    public void Sync(IReadOnlyList<AppPoolMetrics> pools)
    {
        var names = pools.Select(p => p.Name).ToList();
        var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        for (var i = Pools.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(Pools[i].Name))
            {
                Pools.RemoveAt(i);
                changed = true;
            }
        }

        var hidden = new HashSet<string>(_preferences.OverviewHiddenPools, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Pools.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var toggle = new PoolToggle(name, !hidden.Contains(name), OnToggled);
            var index = 0;
            while (index < Pools.Count && string.Compare(Pools[index].Name, name, StringComparison.OrdinalIgnoreCase) < 0)
                index++;
            Pools.Insert(index, toggle);
            changed = true;
        }

        if (_slots.Assign(names))
        {
            _preferences.PoolStyleSlots = new Dictionary<string, int>(_slots.Slots, StringComparer.OrdinalIgnoreCase);
            Save();
        }

        foreach (var toggle in Pools)
            toggle.Style = SeriesStyles.ForSlot(_slots.SlotOf(toggle.Name));

        if (changed)
            Raise(nameof(SelectionSummary));
    }

    /// <summary>Chart lines for the ticked pools, in a stable order so colors and overlaps don't shuffle.</summary>
    public IReadOnlyList<ChartSeries> Series(ChartDefinition chart, Func<string, string, (double[] Xs, double[] Ys)> read)
    {
        var key = ResolveKey(chart.MetricKeys[0]);
        return Pools
            .Where(p => p.IsChecked)
            .OrderBy(p => _slots.SlotOf(p.Name))
            .Select(p =>
            {
                var (xs, ys) = read(p.Name, key);
                return new ChartSeries(p.Name, xs, ys, p.Style.ColorHex, p.Style.Pattern);
            })
            .ToList();
    }

    private void SetAll(bool value)
    {
        foreach (var toggle in Pools)
            toggle.SetSilently(value);
        PersistTicks();
    }

    private void OnToggled(PoolToggle toggle) => PersistTicks();

    private void PersistTicks()
    {
        // Keep hidden pools that aren't currently listed (e.g. a pool that is briefly missing).
        var listed = new HashSet<string>(Pools.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        _preferences.OverviewHiddenPools = _preferences.OverviewHiddenPools
            .Where(name => !listed.Contains(name))
            .Concat(Pools.Where(p => !p.IsChecked).Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save();
        Raise(nameof(SelectionSummary));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save() => _store.Save(_preferences);
}
