namespace DuplicateVideoCleaner;

// Evict one oldest entry at a time so a full cache does not invalidate an entire library.
internal sealed class BoundedMetadataCache(int capacity)
{
    private readonly Dictionary<string, MediaMetadata> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    internal bool TryGetValue(string key, out MediaMetadata metadata)
    {
        lock (_gate) return _entries.TryGetValue(key, out metadata!);
    }

    internal void Add(string key, MediaMetadata metadata)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                if (_entries.Count >= capacity) _entries.Remove(_order.Dequeue());
                _order.Enqueue(key);
            }
            _entries[key] = metadata;
        }
    }
}
