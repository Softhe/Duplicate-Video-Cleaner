namespace DuplicateVideoCleaner;

internal sealed record MetadataProgress(string Phase, string? Path, int CompletedFiles, int TotalFiles, TimeSpan Elapsed);

// Parsing is the summed worker duration; total elapsed is wall time.
internal sealed record MetadataBatchMetrics(int Files, int CacheHits, int Parsed,
    TimeSpan Setup, TimeSpan Parsing, TimeSpan Elapsed);
