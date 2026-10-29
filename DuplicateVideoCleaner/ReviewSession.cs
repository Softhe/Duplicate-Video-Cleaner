namespace DuplicateVideoCleaner;

// A completed review's indexed selection state. Toggling one file never scans the library.
internal sealed class ReviewSession
{
    private readonly Dictionary<string, VideoCandidate> _files;
    private readonly HashSet<string> _selection;
    private readonly Dictionary<string, int> _groupForPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (int Count, long Bytes)> _groupTotals = new();
    internal IReadOnlyList<DuplicateGroup> Groups { get; }
    internal ScanContext? Context { get; }
    internal IReadOnlyDictionary<string, ReviewedVideo> Reviewed { get; }
    internal int FileCount => _files.Count;
    internal int SelectedCount => _selection.Count;
    internal long SelectedBytes { get; private set; }

    internal ReviewSession(IReadOnlyList<DuplicateGroup> groups, HashSet<string> selection, ScanContext? context, IReadOnlyDictionary<string, ReviewedVideo> reviewed)
    {
        Groups = groups;
        Context = context;
        Reviewed = reviewed;
        _selection = selection;
        _files = groups.SelectMany(group => group.Files).ToDictionary(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
        _selection.IntersectWith(_files.Keys);
        _selection.RemoveWhere(path => _files[path].IsProtected ||
            reviewed.TryGetValue(path, out var video) && !video.CanRemove);
        SelectedBytes = _selection.Sum(path => _files[path].Size);
        foreach (var group in groups)
        {
            var count = 0; long bytes = 0;
            foreach (var file in group.Files)
            {
                _groupForPath.Add(file.FullPath, group.Number);
                if (_selection.Contains(file.FullPath)) { count++; bytes += file.Size; }
            }
            _groupTotals.Add(group.Number, (count, bytes));
        }
    }

    internal void SetSelected(VideoCandidate file, bool selected)
    {
        if (!_files.TryGetValue(file.FullPath, out var canonical)) return;
        if (selected && (canonical.IsProtected || Reviewed.TryGetValue(file.FullPath, out var video) && !video.CanRemove)) return;
        if (selected ? _selection.Add(file.FullPath) : _selection.Remove(file.FullPath))
        {
            var delta = selected ? canonical.Size : -canonical.Size;
            SelectedBytes += delta;
            var number = _groupForPath[file.FullPath];
            var totals = _groupTotals[number];
            _groupTotals[number] = (totals.Count + (selected ? 1 : -1), totals.Bytes + delta);
        }
    }

    internal (int Count, long Bytes) GroupSelection(int number) => _groupTotals[number];

    internal ReviewSession Reconcile(RemovalResult result)
    {
        var context = Context ?? throw new InvalidOperationException("No completed scan is available.");
        var rebuilt = new List<DuplicateGroup>();
        var reviewed = new Dictionary<string, ReviewedVideo>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            var remaining = group.Files.Where(file => !result.RemovedPaths.Contains(file.FullPath)).ToArray();
            if (remaining.Length < 2) continue;
            var keep = remaining.Contains(group.SuggestedKeep) ? group.SuggestedKeep :
                DuplicateScanner.ChooseKeeper(remaining, context.KeepMode);
            var reference = Reviewed[keep.FullPath].Metadata;
            foreach (var file in remaining)
            {
                var video = Reviewed[file.FullPath];
                reviewed.Add(file.FullPath, video with
                {
                    SuggestedKeep = ReferenceEquals(file, keep),
                    Confidence = ConfidenceEvaluator.Evaluate(file, video.Metadata, keep, reference)
                });
            }
            var rebuiltGroup = new DuplicateGroup(rebuilt.Count + 1, remaining, keep);
            RenamedFilePolicy.Apply(rebuiltGroup, reviewed);
            rebuilt.Add(rebuiltGroup);
        }
        return new ReviewSession(rebuilt, _selection, context, reviewed);
    }

    internal DryRunContext CreateDryRun()
    {
        var context = Context ?? throw new InvalidOperationException("Complete a scan before exporting results.");
        return context.CreateDryRun(Groups.SelectMany(group => group.Files.Select(file =>
            new DryRunRow(group.Number, Reviewed[file.FullPath], _selection.Contains(file.FullPath)))).ToArray());
    }

    internal List<VideoCandidate> SelectedFiles() => _selection.Select(path => _files[path]).ToList();
}
