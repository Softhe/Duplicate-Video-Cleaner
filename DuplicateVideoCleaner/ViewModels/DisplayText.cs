using System.Globalization;

namespace DuplicateVideoCleaner.ViewModels;

// Short, readable text for the views. Exact values stay in exports and accessible names.
internal static class DisplayText
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    internal static string Size(long bytes) => SizeFormatter.Format(bytes);

    internal static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    internal static string ShortDate(DateTime time) => time.ToString("MMM d", Culture);

    internal static string ShortDateTime(DateTime time) => time.ToString("MMM d HH:mm", Culture);

    internal static string LongDate(DateTime time) => time.ToString("MMM d, yyyy", Culture);

    internal static string Time(DateTime time) => time.ToString("HH:mm:ss", Culture);

    // "Today, 10:42", "Oct 18, 22:47", or with the year when it is not this year.
    internal static string Moment(DateTime local, DateTime now) =>
        local.Date == now.Date ? $"Today, {local.ToString("HH:mm", Culture)}" :
        local.Year == now.Year ? local.ToString("MMM d, HH:mm", Culture) :
        local.ToString("MMM d yyyy, HH:mm", Culture);

    internal static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    // A gap between recordings: "4 s", "1:25", "2:03:00".
    internal static string Gap(TimeSpan gap) => gap.TotalSeconds < 60
        ? $"{(int)gap.TotalSeconds} s"
        : Duration(gap);

    internal static string TimestampSource(TimestampSource source) => source switch
    {
        DuplicateVideoCleaner.TimestampSource.FileName => "time from file name",
        DuplicateVideoCleaner.TimestampSource.LastWriteTime => "time from modified date",
        _ => "time from creation date"
    };

    internal static string Folder(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(directory) ? "Scanned folder" : directory + Path.DirectorySeparatorChar;
    }
}
