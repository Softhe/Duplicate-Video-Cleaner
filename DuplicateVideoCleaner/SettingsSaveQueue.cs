namespace DuplicateVideoCleaner;

// Coalesces pending snapshots while serializing disk writes on a background worker.
internal sealed class SettingsSaveQueue(Action<AppSettings> save)
{
    private readonly object _gate = new();
    private AppSettings? _pending;
    private Task _worker = Task.CompletedTask;
    internal Exception? LastError { get; private set; }

    internal void Enqueue(AppSettings settings)
    {
        lock (_gate)
        {
            _pending = settings;
            if (_worker.IsCompleted) _worker = Task.Run(Drain);
        }
    }

    internal Task FlushAsync() { lock (_gate) return _worker; }

    private void Drain()
    {
        while (true)
        {
            AppSettings snapshot;
            lock (_gate)
            {
                if (_pending is null) { _worker = Task.CompletedTask; return; }
                snapshot = _pending;
                _pending = null;
            }
            try { save(snapshot); LastError = null; }
            catch (Exception ex) { LastError = ex; }
        }
    }
}
