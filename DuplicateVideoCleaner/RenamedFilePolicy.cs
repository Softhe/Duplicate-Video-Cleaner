namespace DuplicateVideoCleaner;

internal static class RenamedFilePolicy
{
    internal static void Apply(DuplicateGroup group, IDictionary<string, ReviewedVideo> reviewed)
    {
        var protectedFiles = group.Files.Where(file => file.IsProtected).ToArray();
        // Index complete metadata so large groups do not require pairwise comparisons.
        var signatures = protectedFiles.Where(file => Signature(reviewed[file.FullPath]).HasValue)
            .GroupBy(file => Signature(reviewed[file.FullPath])!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VideoCandidate>)Array.AsReadOnly(group.ToArray()));
        foreach (var file in group.Files)
        {
            var video = reviewed[file.FullPath];
            IReadOnlyList<VideoCandidate>? matches = null;
            if (!file.IsProtected && Signature(video) is { } signature)
                signatures.TryGetValue(signature, out matches);
            reviewed[file.FullPath] = video with
            {
                RequiresProtectedMatch = protectedFiles.Length > 0,
                ProtectedMatches = matches,
                Confidence = matches is { Count: > 0 }
                    ? ConfidenceEvaluator.Evaluate(file, video.Metadata, matches[0], reviewed[matches[0].FullPath].Metadata)
                    : video.Confidence
            };
        }
    }

    private static (long Bytes, long Duration, int Width, int Height)? Signature(ReviewedVideo video)
    {
        var metadata = video.Metadata;
        if (metadata.Status != MetadataProbeStatus.Available || metadata.Duration is not { } duration ||
            duration <= TimeSpan.Zero || metadata.Width is not > 0 || metadata.Height is not > 0)
            return null;
        return (video.Candidate.Size, ConfidenceEvaluator.NormalizeMilliseconds(duration), metadata.Width.Value, metadata.Height.Value);
    }
}
