namespace IISMonitor.Core.Presentation;

/// <summary>
/// Gives every app pool a fixed style slot, so a pool keeps its color when others are ticked,
/// unticked, added or removed. A new pool takes the lowest slot no current pool is using; slots of
/// pools that no longer exist are recycled only when needed.
/// </summary>
public sealed class SeriesSlotRegistry
{
    private readonly Dictionary<string, int> _slots;

    public SeriesSlotRegistry(IReadOnlyDictionary<string, int>? saved = null)
    {
        _slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, slot) in saved ?? new Dictionary<string, int>())
        {
            if (slot >= 0)
                _slots[name] = slot;
        }
    }

    public IReadOnlyDictionary<string, int> Slots => _slots;

    /// <summary>Makes sure every current name has a slot. Returns true when an assignment changed.</summary>
    public bool Assign(IEnumerable<string> currentNames)
    {
        var current = currentNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var used = new HashSet<int>();
        foreach (var name in current)
        {
            if (_slots.TryGetValue(name, out var slot) && !used.Add(slot))
            {
                // Two current pools share a slot (e.g. hand-edited preferences): reassign the later one.
                _slots.Remove(name);
            }
        }

        // While every current pool could have a solid line, don't leave one dashed because of slots
        // held by pools that were deleted: move just those pools down into free solid slots.
        if (current.Count <= SeriesStyles.Palette.Count)
        {
            foreach (var name in current.Where(n => _slots.TryGetValue(n, out var slot) && slot >= SeriesStyles.Palette.Count).ToList())
            {
                used.Remove(_slots[name]);
                _slots.Remove(name);
            }
        }

        var changed = false;
        foreach (var name in current.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (_slots.ContainsKey(name))
                continue;

            var slot = 0;
            while (used.Contains(slot))
                slot++;

            // Recycle the slot from a pool that no longer exists.
            foreach (var stale in _slots.Where(kv => kv.Value == slot).Select(kv => kv.Key).ToList())
                _slots.Remove(stale);

            _slots[name] = slot;
            used.Add(slot);
            changed = true;
        }

        return changed;
    }

    public int SlotOf(string name) => _slots.TryGetValue(name, out var slot) ? slot : 0;
}
