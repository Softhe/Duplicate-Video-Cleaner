namespace DuplicateVideoCleaner;

internal sealed record MetadataProgress(string Phase, string? Path, long BytesCopied, long TotalBytes,
    int CompletedFiles, int TotalFiles, TimeSpan Elapsed);

// Staging and parsing totals are summed worker durations; total elapsed is wall time.
internal sealed record MetadataBatchMetrics(int Files, int CacheHits, long StagedBytes,
    TimeSpan Setup, TimeSpan Staging, TimeSpan Parsing, TimeSpan Elapsed);
