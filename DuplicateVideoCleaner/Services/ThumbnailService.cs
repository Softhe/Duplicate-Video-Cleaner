using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuplicateVideoCleaner.ViewModels;

namespace DuplicateVideoCleaner.Services;

// Loads thumbnails for the file rows on screen. Newest requests go first (the rows the user is
// looking at), at most two at a time, in the background. Work pauses while a scan or removal runs
// and is dropped when the shown group changes. Frames come from the disk cache or from sandboxed
// ffmpeg as raw pixels and are wrapped as frozen bitmaps; no image format is parsed.
internal sealed class ThumbnailService : IThumbnailProvider, IAsyncDisposable
{
    private const int MaxWorkers = 2;
    private const int MemoryEntries = 300;
    private static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(20);
    private readonly ThumbnailExtractor _extractor;
    private readonly ThumbnailCache? _cache;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();
    private readonly LinkedList<PendingThumbnail> _queue = new();
    private readonly Dictionary<string, PendingThumbnail> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.Ordinal);
    private readonly Queue<string> _memoryOrder = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _workers;
    private bool _paused;
    private bool _enabled = true;
    private CancellationTokenSource? _idle;

    private sealed class PendingThumbnail(string key, VideoCandidate file, TimeSpan? duration)
    {
        public string Key { get; } = key;
        public VideoCandidate File { get; } = file;
        public TimeSpan? Duration { get; } = duration;
        public List<Action<ImageSource>> Callbacks { get; } = [];
        public LinkedListNode<PendingThumbnail>? Node { get; set; }
    }

    internal ThumbnailService(ThumbnailExtractor extractor, ThumbnailCache? cache, SynchronizationContext? ui = null)
    {
        _extractor = extractor;
        _cache = cache;
        _ui = ui ?? SynchronizationContext.Current;
        _cache?.Trim();
    }

    internal bool IsAvailable => _extractor.IsAvailable;

    // Turning thumbnails off drops queued work; rows keep their placeholder.
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) CancelPending(); }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            lock (_gate) _paused = value;
            if (!value) StartWorkers();
        }
    }

    public ImageSource? Request(VideoCandidate file, TimeSpan? duration, Action<ImageSource> loaded)
    {
        if (!_enabled || !_extractor.IsAvailable || _shutdown.IsCancellationRequested) return null;
        var key = ThumbnailCache.KeyFor(file);
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var image)) return image;
            if (_pending.TryGetValue(key, out var existing))
            {
                existing.Callbacks.Add(loaded);
                if (existing.Node is { } node) { _queue.Remove(node); _queue.AddFirst(node); }
            }
            else
            {
                var request = new PendingThumbnail(key, file, duration);
                request.Callbacks.Add(loaded);
                request.Node = _queue.AddFirst(request);
                _pending[key] = request;
            }
            _idle?.Cancel();
        }
        StartWorkers();
        return null;
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            foreach (var request in _queue) _pending.Remove(request.Key);
            _queue.Clear();
        }
    }

    private void StartWorkers()
    {
        lock (_gate)
        {
            while (_workers < MaxWorkers && _queue.Count > 0 && !_paused && !_shutdown.IsCancellationRequested)
            {
                _workers++;
                _ = Task.Run(WorkAsync);
            }
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            PendingThumbnail request;
            lock (_gate)
            {
                if (_queue.Count == 0 || _paused || _shutdown.IsCancellationRequested)
                {
                    _workers--;
                    if (_workers == 0 && _queue.Count == 0) ScheduleIdleRelease();
                    return;
                }
                request = _queue.First!.Value;
                _queue.RemoveFirst();
                request.Node = null;
            }
            ImageSource? image = null;
            try { image = await LoadAsync(request).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            Action<ImageSource>[] callbacks;
            lock (_gate)
            {
                _pending.Remove(request.Key);
                callbacks = request.Callbacks.ToArray();
                if (image is not null) Remember(request.Key, image);
            }
            if (image is null || callbacks.Length == 0) continue;
            void Deliver() { foreach (var callback in callbacks) callback(image); }
            if (_ui is null) Deliver(); else _ui.Post(_ => Deliver(), null);
        }
    }

    private async Task<ImageSource?> LoadAsync(PendingThumbnail request)
    {
        var cacheable = _cache is not null && ThumbnailCache.CanCache(request.File);
        var frame = cacheable ? _cache!.TryGet(request.Key) : null;
        if (frame is null)
        {
            frame = await _extractor.ExtractAsync(request.File, request.Duration, _shutdown.Token).ConfigureAwait(false);
            if (frame is not null && cacheable) _cache!.Store(request.Key, frame);
        }
        return frame is null ? null : ToImage(frame);
    }

    // Raw BGRA from our own fixed-size buffer: no decoder involved.
    internal static BitmapSource ToImage(ThumbnailFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void Remember(string key, ImageSource image)
    {
        if (_memory.TryAdd(key, image)) _memoryOrder.Enqueue(key);
        while (_memoryOrder.Count > MemoryEntries) _memory.Remove(_memoryOrder.Dequeue());
    }

    // The sandbox (staged tools and an AppContainer profile) is released after a quiet period.
    private void ScheduleIdleRelease()
    {
        _idle?.Cancel();
        var idle = _idle = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleRelease, idle.Token).ConfigureAwait(false);
                await _extractor.ReleaseSandboxAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        CancelPending();
        _idle?.Cancel();
        await _extractor.DisposeAsync().ConfigureAwait(false);
    }
}
