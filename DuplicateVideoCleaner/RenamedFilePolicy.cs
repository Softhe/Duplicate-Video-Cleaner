namespace DuplicateVideoCleaner;

// Applies only to groups that contain protected clips (the opt-in "protect undated
// clips" scan option). Protected clips are always kept. An original is removable when:
//  - its duration, resolution and exact byte size match a protected clip, which then
//    survives as its copy; or
//  - other unmatched originals share its time window, in which case ordinary timestamp
//    review applies among those originals only. A protected clip never stands in as the
//    survivor for an original it does not match.
// The only unmatched original in its time window is kept and cannot be selected.
internal static class RenamedFilePolicy
{
    internal static void Apply(DuplicateGroup group, IDictionary<string, ReviewedVideo> reviewed, KeepMode keepMode = KeepMode.Oldest)
    {
        var protectedFiles = group.Files.Where(file => file.IsProtected).ToArray();
        if (protectedFiles.Length == 0) return;
        // Index complete metadata so large groups do not require pairwise comparisons.
        var signatures = protectedFiles.Where(file => Signature(reviewed[file.FullPath]).HasValue)
            .GroupBy(file => Signature(reviewed[file.FullPath])!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VideoCandidate>)Array.AsReadOnly(group.ToArray()));
        var unmatched = new List<VideoCandidate>();
        foreach (var file in group.Files)
        {
            var video = reviewed[file.FullPath];
            if (file.IsProtected)
            {
                reviewed[file.FullPath] = video with
                {
                    SuggestedKeep = ReferenceEquals(file, group.SuggestedKeep), RequiresProtectedMatch = false,
                    ProtectedMatches = null, InProtectedGroup = true, OriginalKeeper = null
                };
                continue;
            }
            IReadOnlyList<VideoCandidate>? matches = null;
            if (Signature(video) is { } signature) signatures.TryGetValue(signature, out matches);
            if (matches is not { Count: > 0 })
            {
                unmatched.Add(file);
                continue;
            }
            reviewed[file.FullPath] = video with
            {
                SuggestedKeep = false, RequiresProtectedMatch = true, ProtectedMatches = matches, InProtectedGroup = true,
                OriginalKeeper = null,
                Confidence = ConfidenceEvaluator.Evaluate(file, video.Metadata, matches[0], reviewed[matches[0].FullPath].Metadata)
            };
        }
        foreach (var cluster in unmatched.GroupBy(file => file.TimeCluster))
        {
            var originals = cluster.ToArray();
            if (originals.Length == 1)
            {
                reviewed[originals[0].FullPath] = reviewed[originals[0].FullPath] with
                {
                    SuggestedKeep = false, RequiresProtectedMatch = true, ProtectedMatches = null, InProtectedGroup = true,
                    OriginalKeeper = null
                };
                continue;
            }
            var keep = DuplicateScanner.ChooseKeeper(originals, keepMode);
            var keepMetadata = reviewed[keep.FullPath].Metadata;
            foreach (var file in originals)
            {
                var video = reviewed[file.FullPath];
                reviewed[file.FullPath] = video with
                {
                    SuggestedKeep = ReferenceEquals(file, keep), RequiresProtectedMatch = false, ProtectedMatches = null,
                    InProtectedGroup = true, OriginalKeeper = keep,
                    Confidence = ConfidenceEvaluator.Evaluate(file, video.Metadata, keep, keepMetadata)
                };
            }
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
