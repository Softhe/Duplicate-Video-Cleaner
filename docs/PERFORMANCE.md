# Performance measurements

The application records per-scan measurements in Activity and displays the metadata phase, processed files and elapsed time during processing. Parser totals sum worker durations; they can overlap and must not be added to calculate wall time.

## Reproduce a synthetic comparison

```powershell
./build/Measure-Performance.ps1 -FfprobePath ./release/DuplicateVideoCleaner-1.2.0-win-x64-portable/tools/ffprobe.exe -OutputPath ./validation/performance.json
```

This builds the existing test runner. It creates four copies of the checked-in one-second black video with valid 8 MiB MP4 free atoms, scans them, and uses the supplied native ffprobe inside the production sandbox. It compares cold and warm metadata batches. A second comparison performs the same 1,000 selection changes over 10,000 in-memory candidates using full traversal and indexed counters. Both methods must produce identical final totals.

With `DVC_TEST_FFPROBE` set to a native ffprobe, the core test runner also pads the checked-in video's media data to 300 MiB. Its index (`moov`) is after the media data, so the sandboxed parser must seek across the whole file through its read-only handle.

The generated fixtures are removed afterward. No user media is used. Native ffprobe, DLLs, Windows AppContainer support and the selected .NET SDK are required.

## Local observation, 2026-11-23

Windows x64, .NET 10.0.15, ffprobe n8.1.3-39. One descriptive run produced:

| Work | Result |
| --- | --- |
| Enumerating four padded synthetic files | 13.42 ms |
| Cold metadata, four files | 166.42 ms wall time; no media copied |
| Cold sandbox/tool setup | 64.21 ms |
| Cold parsing | 313.29 ms summed worker time (parallel) |
| Warm metadata, four validated cache hits | 0.66 ms wall time; no parser and no sandbox setup |
| One 300 MiB file with its index at the end | 176 ms wall time, including 73 ms setup |
| Full-traversal selection totals, 10,000 files and 1,000 changes | 577.28 ms |
| Indexed selection totals, identical changes and result | 0.34 ms |

For comparison, 1.1.2 copied every uncached input in full before parsing. Its 2026-10-20 run staged 33,557,988 bytes for the same four files, and copy time grows with total video size and source speed.

These figures exclude rendering from the selection comparison and exclude one-time index construction. They are not a representative large-recording throughput benchmark. Small valid video streams with large free atoms do not reproduce high-bitrate decoding, fragmented containers, network storage, cold operating-system caches or slow removable media. Timings vary by machine and load. Tests assert correct behavior and equal totals rather than a fixed speed ratio.

## Runtime tradeoffs

- ffprobe reads each original through a read-only handle that the broker opened and validated against the scan snapshot. Reading a file costs about what ffprobe needs to find its index and first video stream, not the full file size. No temporary disk space is used for media.
- Up to four parser processes run concurrently (half the processor count, at least two). Each parser has a 15-second deadline. A blocked synchronous filesystem call cannot necessarily be interrupted immediately.
- The metadata cache holds at most 65,536 entries with incremental eviction. A working set larger than that can still have many misses.
- Review renders 25 groups per page and 25 file rows per expanded page. Selection updates read indexed totals and update rendered controls. Export and bulk selection still process all relevant records.
- Recovery history archival is explicit. Normal discovery skips the archive; opting into archive inspection includes its traversal cost. No audit records are automatically deleted.
