using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DuplicateVideoCleaner;

// Thumbnails already extracted, in the user profile. Each entry is one deflated BGRA frame,
// keyed by the file's scan snapshot (path, size, times and file ID), so an edited or replaced
// file never shows an old frame. The folder is trimmed to a size limit, oldest entries first.
internal sealed class ThumbnailCache
{
    private const uint Magic = 0x48545644; // "DVTH"
    private const int Version = 1;
    private const string Extension = ".thumb";
    private readonly string _directory;
    private readonly long _maxBytes;
    private int _writesSinceTrim;

    internal ThumbnailCache(string? directory = null, long maxBytes = 96L * 1024 * 1024)
    {
        _directory = directory ?? DefaultDirectory;
        _maxBytes = maxBytes;
    }

    internal static string DefaultDirectory => Environment.GetEnvironmentVariable("DVC_THUMBNAIL_CACHE") is { Length: > 0 } path
        ? path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuplicateVideoCleaner", "Thumbnails");

    internal string Directory => _directory;

    internal static string KeyFor(VideoCandidate file)
    {
        var identity = $"{file.FullPath.ToUpperInvariant()}|{file.Size}|{file.CreationTimeUtc?.Ticks}|{file.LastWriteTimeUtc?.Ticks}|{file.FileIdentity}|{ThumbnailExtractor.Width}x{ThumbnailExtractor.Height}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    // Entries need a full snapshot; files without one are never cached.
    internal static bool CanCache(VideoCandidate file) =>
        file.CreationTimeUtc is not null && file.LastWriteTimeUtc is not null && !string.IsNullOrEmpty(file.FileIdentity);

    internal ThumbnailFrame? TryGet(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version) return null;
            var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            if (width != ThumbnailExtractor.Width || height != ThumbnailExtractor.Height) return null;
            var pixels = new byte[ThumbnailExtractor.FrameBytes];
            using (var inflate = new DeflateStream(stream, CompressionMode.Decompress))
            {
                inflate.ReadExactly(pixels);
                if (inflate.ReadByte() != -1) return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return new ThumbnailFrame(width, height, pixels);
        }
        catch (Exception) { return null; }
    }

    internal void Store(string key, ThumbnailFrame frame)
    {
        if (frame.Pixels.Length != ThumbnailExtractor.FrameBytes) return;
        var temporary = Path.Combine(_directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                Span<byte> header = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], frame.Width);
                BinaryPrimitives.WriteInt32LittleEndian(header[12..], frame.Height);
                stream.Write(header);
                using var deflate = new DeflateStream(stream, CompressionLevel.Fastest);
                deflate.Write(frame.Pixels);
            }
            File.Move(temporary, PathFor(key), true);
            if (Interlocked.Increment(ref _writesSinceTrim) % 25 == 0) Trim();
        }
        catch (Exception) { }
        finally { try { File.Delete(temporary); } catch { } }
    }

    // Deletes the least recently used entries until the folder fits the size limit.
    internal void Trim()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;
            var entries = new DirectoryInfo(_directory).EnumerateFiles("*" + Extension)
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long total = 0;
            foreach (var entry in entries)
            {
                total += entry.Length;
                if (total > _maxBytes) { try { entry.Delete(); } catch { } }
            }
            foreach (var stale in new DirectoryInfo(_directory).EnumerateFiles("*.tmp").Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                try { stale.Delete(); } catch { }
        }
        catch (Exception) { }
    }

    private string PathFor(string key) => Path.Combine(_directory, key + Extension);
}
