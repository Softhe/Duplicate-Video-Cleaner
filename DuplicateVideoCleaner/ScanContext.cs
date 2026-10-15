namespace DuplicateVideoCleaner;

internal sealed record ScanContext(string Root, int ThresholdSeconds, KeepMode KeepMode, bool IncludeSubfolders)
{
    internal string LogPath => Path.Combine(Root, "DuplicateCleanupLog.txt");
    internal string Description => $"{Root} · {ThresholdSeconds}s · Keep {KeepMode} · {(IncludeSubfolders ? "Including subfolders" : "Current folder only")}";

    internal DryRunContext CreateDryRun(IReadOnlyList<DryRunRow> rows) =>
        new(Root, ThresholdSeconds, KeepMode, IncludeSubfolders, rows);
}
