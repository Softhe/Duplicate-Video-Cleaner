using System.Windows.Input;
using System.Windows.Media;
using DuplicateVideoCleaner.Mvvm;

namespace DuplicateVideoCleaner.ViewModels;

// One duplicate group in the Review list. File rows are created when the group is first shown.
internal sealed class GroupItemViewModel : ObservableObject
{
    private readonly ReviewViewModel _review;
    private readonly ReviewWorkspace _workspace;
    private IReadOnlyList<FileItemViewModel>? _files;

    internal GroupItemViewModel(ReviewViewModel review, ReviewWorkspace workspace, DuplicateGroup group)
    {
        _review = review;
        _workspace = workspace;
        Group = group;
        var reviewed = group.Files.Select(file => workspace.Reviewed[file.FullPath]).ToArray();
        Confidence = ConfidenceEvaluator.ForSuggestedRemovals(reviewed);
        var first = group.Files.Min(file => file.Timestamp);
        var last = group.Files.Max(file => file.Timestamp);
        Title = $"Group {group.Number} · {DisplayText.ShortDateTime(first)}";
        RangeText = first.Date == last.Date
            ? $"{DisplayText.LongDate(first)} · {DisplayText.Time(first)} – {DisplayText.Time(last)} · {DisplayText.Files(group.Files.Count)}"
            : $"{DisplayText.LongDate(first)} {DisplayText.Time(first)} – {DisplayText.LongDate(last)} {DisplayText.Time(last)} · {DisplayText.Files(group.Files.Count)}";
        HasProtected = group.Files.Any(file => file.IsProtected);
        SelectSuggestedCommand = new RelayCommand(() => SetSelection(true), () => !_review.IsBusy);
        ClearCommand = new RelayCommand(() => SetSelection(false), () => !_review.IsBusy);
    }

    internal DuplicateGroup Group { get; }
    public int Number => Group.Number;
    public string Title { get; }
    public string RangeText { get; }
    public bool HasProtected { get; }
    public DuplicateConfidence? Confidence { get; }
    public ICommand SelectSuggestedCommand { get; }
    public ICommand ClearCommand { get; }

    // Chip tag (High | Medium | Low | None) and text.
    public string ConfidenceKey => Confidence?.ToString() ?? "None";
    public string ConfidenceText => Confidence?.ToString() ?? "No removal suggested";
    public string ConfidenceTitle => Confidence is { } level ? $"{level} confidence" : "No removal suggested";

    public string ConfidenceExplanation => Confidence switch
    {
        DuplicateConfidence.High => "duration, resolution and exact size match",
        DuplicateConfidence.Medium => "two of duration, resolution and size match",
        DuplicateConfidence.Low => "one or none of duration, resolution and size match, or metadata is missing",
        _ => HasProtected ? "protected clips and unmatched files are kept" : "every file is kept"
    };

    public int SelectedCount => _workspace.GroupSelection(Group.Number).Count;
    public long SelectedBytes => _workspace.GroupSelection(Group.Number).Bytes;
    public bool AllSelected => SelectedCount == Group.Files.Count;
    public string SizeText => SelectedCount == 0 ? "—" : DisplayText.Size(SelectedBytes);

    public string SelectionText => AllSelected
        ? $"All {Group.Files.Count} files selected"
        : $"{DisplayText.Files(Group.Files.Count)} · {SelectedCount} selected";

    public string SelectionDetail => $"{SelectedCount} of {Group.Files.Count} selected · {(SelectedCount == 0 ? "nothing to remove" : DisplayText.Size(SelectedBytes))}";

    public string AutomationName =>
        $"Duplicate group {Group.Number}; {Group.Files.Count} files; {SelectedCount} selected; {ConfidenceTitle}; suggested keep {Group.SuggestedKeep.RelativePath}";

    public IReadOnlyList<FileItemViewModel> Files => _files ??= BuildFiles();

    internal bool FilesBuilt => _files is not null;

    private IReadOnlyList<FileItemViewModel> BuildFiles() =>
        Group.Files.Select(file => new FileItemViewModel(this, _workspace, Group, _workspace.Reviewed[file.FullPath])).ToArray();

    private void SetSelection(bool suggested)
    {
        _workspace.SelectGroup(Group, suggested);
        _review.OnSelectionChanged();
    }

    // One file toggled: only this group and the totals change.
    internal void OnFileSelectionChanged()
    {
        Refresh();
        _review.OnTotalsChanged();
    }

    internal void Refresh()
    {
        Raise(nameof(SelectedCount), nameof(SelectedBytes), nameof(AllSelected), nameof(SizeText), nameof(SelectionText),
            nameof(SelectionDetail), nameof(AutomationName));
        if (_files is null) return;
        foreach (var file in _files) file.Refresh();
    }

    internal bool IsBusy => _review.IsBusy;
    internal ImageSource? RequestThumbnail(VideoCandidate file, Action<ImageSource> loaded) => _review.Thumbnails?.Request(file, loaded);
}

// Supplies video thumbnails. Request returns a cached image or null, and calls loaded later
// (on the UI thread) when a frame becomes available. Rows only ask when they are on screen.
internal interface IThumbnailProvider
{
    ImageSource? Request(VideoCandidate file, Action<ImageSource> loaded);
    void CancelPending();
}

// One attribute on a file row (resolution, duration, size) and how it compares with the keeper.
internal sealed record AttributeViewModel(string Text, bool? Matches, string AutomationText)
{
    public bool IsMatch => Matches == true;
    public bool IsDifferent => Matches == false;
}

internal sealed class FileItemViewModel : ObservableObject
{
    private readonly GroupItemViewModel _group;
    private readonly ReviewWorkspace _workspace;
    private ImageSource? _thumbnail;

    internal FileItemViewModel(GroupItemViewModel group, ReviewWorkspace workspace, DuplicateGroup duplicateGroup, ReviewedVideo reviewed)
    {
        _group = group;
        _workspace = workspace;
        Reviewed = reviewed;
        var file = reviewed.Candidate;
        var reference = reviewed.ComparisonReference ?? duplicateGroup.SuggestedKeep;
        var referenceVideo = workspace.Reviewed[reference.FullPath];
        IsReference = ReferenceEquals(file, reference) || string.Equals(file.FullPath, reference.FullPath, StringComparison.OrdinalIgnoreCase);
        Role = file.IsProtected ? "Protected" : reviewed.SuggestRemoval ? "Remove" : "Keep";
        RoleText = Role.ToUpperInvariant();
        RoleGlyph = Role switch { "Protected" => "", "Remove" => "", _ => "" };
        if (IsReference)
            RoleNote = reviewed.SuggestedKeep ? $"{(workspace.Context?.KeepMode == KeepMode.Newest ? "newest" : "oldest")} in group · reference" : "reference";
        else
        {
            var offset = file.Timestamp - reference.Timestamp;
            var relation = string.Equals(reference.FullPath, duplicateGroup.SuggestedKeep.FullPath, StringComparison.OrdinalIgnoreCase) ? "keeper" : "its match";
            RoleNote = offset >= TimeSpan.Zero ? $"{DisplayText.Gap(offset)} after {relation}" : $"{DisplayText.Gap(offset.Duration())} before {relation}";
        }
        Name = Path.GetFileName(file.RelativePath);
        Folder = DisplayText.Folder(file.RelativePath);
        TimeText = (file.Timestamp.Date == reference.Timestamp.Date ? DisplayText.Time(file.Timestamp) : $"{DisplayText.ShortDate(file.Timestamp)} {DisplayText.Time(file.Timestamp)}") +
            $" · {DisplayText.TimestampSource(file.TimestampSource)}";
        Attributes = BuildAttributes(reviewed, file, referenceVideo, IsReference);
        DurationBadge = reviewed.Metadata.Duration is { } duration ? DisplayText.Duration(duration) : null;
        Note = file.IsProtected || reviewed.InProtectedGroup || reviewed.RequiresProtectedMatch ? reviewed.RetentionText : null;
        AutomationName = $"Group {duplicateGroup.Number}, select {file.RelativePath} for removal";
    }

    internal ReviewedVideo Reviewed { get; }
    internal VideoCandidate Candidate => Reviewed.Candidate;
    public bool IsReference { get; }
    public string Role { get; }
    public string RoleText { get; }
    public string RoleGlyph { get; }
    public string RoleNote { get; }
    public string Name { get; }
    public string Folder { get; }
    public string RelativePath => Candidate.RelativePath;
    public string TimeText { get; }
    public IReadOnlyList<AttributeViewModel> Attributes { get; }
    public string? DurationBadge { get; }
    public string? Note { get; }
    public bool HasNote => Note is not null;
    public string AutomationName { get; }
    public bool CanSelect => Reviewed.CanRemove;
    public bool CannotSelect => !CanSelect;
    public string CannotSelectText => Candidate.IsProtected ? "Protected" : "Kept: no match";

    public bool IsSelected
    {
        get => _workspace.IsSelected(Candidate.FullPath);
        set
        {
            if (_group.IsBusy || value == IsSelected) return;
            _workspace.SetSelected(Candidate, value);
            Raise();
            _group.OnFileSelectionChanged();
        }
    }

    private bool _thumbnailRequested;

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _thumbnail = _group.RequestThumbnail(Candidate, image => Thumbnail = image);
            }
            return _thumbnail;
        }
        private set { if (Set(ref _thumbnail, value)) Raise(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => _thumbnail is not null;

    public string Details =>
        $"{Candidate.RelativePath}\n{Candidate.Timestamp:yyyy-MM-dd HH:mm:ss} · {Candidate.TimestampSource}\n" +
        $"Duration {Reviewed.Metadata.DurationText} · Resolution {Reviewed.Metadata.ResolutionText} · {Candidate.Size:N0} bytes\n{Reviewed.RetentionText}";

    internal void Refresh() => Raise(nameof(IsSelected));

    private static IReadOnlyList<AttributeViewModel> BuildAttributes(ReviewedVideo video, VideoCandidate file, ReviewedVideo reference, bool isReference)
    {
        var metadata = video.Metadata;
        var other = reference.Metadata;
        bool? Compare(bool available, bool equal) => isReference || !available ? null : equal;

        var resolutionKnown = metadata.Width is not null && metadata.Height is not null && other.Width is not null && other.Height is not null;
        var resolution = new AttributeViewModel(
            metadata.Width is null ? "resolution unavailable" : metadata.ResolutionText,
            Compare(resolutionKnown, metadata.Width == other.Width && metadata.Height == other.Height),
            $"Resolution {metadata.ResolutionText}");

        var durationKnown = metadata.Duration is not null && other.Duration is not null;
        var duration = new AttributeViewModel(
            metadata.Duration is { } value ? DisplayText.Duration(value) : "duration unavailable",
            Compare(durationKnown, durationKnown && ConfidenceEvaluator.NormalizeMilliseconds(metadata.Duration!.Value) == ConfidenceEvaluator.NormalizeMilliseconds(other.Duration!.Value)),
            $"Duration {metadata.DurationText}");

        var sameSize = file.Size == reference.Candidate.Size;
        var size = new AttributeViewModel(
            isReference ? DisplayText.Size(file.Size) : $"{DisplayText.Size(file.Size)} {(sameSize ? "exact size" : "different size")}",
            isReference ? null : sameSize,
            $"Size {file.Size} bytes{(isReference ? string.Empty : sameSize ? ", exact match" : ", different")}");
        return [resolution, duration, size];
    }
}
