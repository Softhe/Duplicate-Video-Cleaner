namespace DuplicateVideoCleaner;

// Workers replace one snapshot; the UI polls it without posting one event per copy chunk.
internal sealed class LatestMetadataProgress : IProgress<MetadataProgress>
{
    private MetadataProgress? _latest;
    internal MetadataProgress? Latest => Volatile.Read(ref _latest);
    public void Report(MetadataProgress value) => Volatile.Write(ref _latest, value);
}
