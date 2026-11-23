namespace DuplicateVideoCleaner;

internal sealed record ScanContext(string Root, int ThresholdSeconds, KeepMode KeepMode, bool IncludeSubfolders, bool ProtectUndated = false)
{
    internal string LogPath => Path.Combine(Root, "DuplicateCleanupLog.txt");
    internal string Description => $"{Root} · {ThresholdSeconds}s · Keep {KeepMode} · {(IncludeSubfolders ? "Including subfolders" : "Current folder only")}" +
        (ProtectUndated ? " · Protecting undated clips" : string.Empty);

    internal DryRunContext CreateDryRun(IReadOnlyList<DryRunRow> rows) =>
        new(Root, ThresholdSeconds, KeepMode, IncludeSubfolders, rows, ProtectUndated);
}
