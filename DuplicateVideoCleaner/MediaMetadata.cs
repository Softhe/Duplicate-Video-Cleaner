namespace DuplicateVideoCleaner;

internal enum MetadataProbeStatus { Available, Unavailable, Failed, TimedOut }
internal enum DuplicateConfidence { High, Medium, Low }

internal sealed record MediaMetadata(
    TimeSpan? Duration,
    int? Width,
    int? Height,
    MetadataProbeStatus Status,
    string? Error = null)
{
    internal string DurationText => Duration is null ? "Unavailable" :
        FormattableString.Invariant($"{(long)Duration.Value.TotalHours:00}:{Duration.Value.Minutes:00}:{Duration.Value.Seconds:00}.{Duration.Value.Milliseconds:000}");
    internal string ResolutionText => Width is null || Height is null ? "Unavailable" : $"{Width}×{Height}";
}

internal sealed record ReviewedVideo(
    VideoCandidate Candidate,
    MediaMetadata Metadata,
    DuplicateConfidence Confidence,
    bool SuggestedKeep,
    bool RequiresProtectedMatch = false,
    IReadOnlyList<VideoCandidate>? ProtectedMatches = null)
{
    internal bool MatchesProtectedCopy => ProtectedMatches is { Count: > 0 };
    internal VideoCandidate? ProtectedReference => ProtectedMatches?.FirstOrDefault();
    internal bool CanRemove => !Candidate.IsProtected && (!RequiresProtectedMatch || MatchesProtectedCopy);
    internal bool SuggestRemoval => !SuggestedKeep && CanRemove;
    internal string RetentionText => Candidate.IsProtected ? "Protected renamed clip" :
        RequiresProtectedMatch ? MatchesProtectedCopy ? $"Metadata match to protected clip: {ProtectedReference!.RelativePath}; duration, resolution and size match" :
        "No metadata match to a protected clip; kept" : "Standard timestamp review";
}

internal static class ConfidenceEvaluator
{
    internal static DuplicateConfidence? ForSuggestedRemovals(IEnumerable<ReviewedVideo> videos) =>
        videos.Where(video => video.SuggestRemoval).Select(video => (DuplicateConfidence?)video.Confidence).Max();

    internal static DuplicateConfidence Evaluate(VideoCandidate candidate, MediaMetadata metadata, VideoCandidate reference, MediaMetadata referenceMetadata)
    {
        if (metadata.Status != MetadataProbeStatus.Available || referenceMetadata.Status != MetadataProbeStatus.Available ||
            metadata.Duration is null || referenceMetadata.Duration is null ||
            metadata.Width is null || metadata.Height is null || referenceMetadata.Width is null || referenceMetadata.Height is null)
            return DuplicateConfidence.Low;

        var matches = 0;
        if (candidate.Size == reference.Size) matches++;
        if (metadata.Width == referenceMetadata.Width && metadata.Height == referenceMetadata.Height) matches++;
        if (NormalizeMilliseconds(metadata.Duration.Value) == NormalizeMilliseconds(referenceMetadata.Duration.Value)) matches++;
        return matches switch { 3 => DuplicateConfidence.High, 2 => DuplicateConfidence.Medium, _ => DuplicateConfidence.Low };
    }

    internal static long NormalizeMilliseconds(TimeSpan duration) => (long)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
}
