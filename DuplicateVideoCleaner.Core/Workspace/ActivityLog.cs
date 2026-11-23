namespace DuplicateVideoCleaner;

internal sealed record ActivityEntry(DateTime Time, string Title, string Detail, bool Success);

// Session activity, newest first, capped so a noisy scan cannot grow the UI without bound.
// Not thread-safe: workflows add entries on the UI thread, as the views that observe it expect.
internal sealed class ActivityLog
{
    internal const int Capacity = 200;
    private readonly List<ActivityEntry> _entries = [];

    internal IReadOnlyList<ActivityEntry> Entries => _entries;

    // Raised after an entry is added. Evicted is the oldest entry dropped to stay within
    // Capacity, or null.
    internal event Action<ActivityEntry, ActivityEntry?>? Added;

    internal ActivityEntry Add(string title, string detail, bool success)
    {
        ActivityEntry? evicted = null;
        if (_entries.Count >= Capacity)
        {
            evicted = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
        }
        var entry = new ActivityEntry(DateTime.Now, title, detail, success);
        _entries.Insert(0, entry);
        Added?.Invoke(entry, evicted);
        return entry;
    }
}
