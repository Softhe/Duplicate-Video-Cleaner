# Performance measurements

The application records per-scan measurements in Activity and displays metadata phase, processed files, copied bytes and elapsed time during processing. Copy and parser totals sum worker durations; they can overlap and must not be added to calculate wall time. Staging duration includes waiting for the serialized copy slot.

## Reproduce a synthetic comparison

```powershell
./build/Measure-Performance.ps1 -FfprobePath ./release/DuplicateVideoCleaner-1.1.2-win-x64-portable/tools/ffprobe.exe -OutputPath ./validation/performance.json
```

This builds the existing test runner. It creates four copies of the checked-in one-second black video with valid 8 MiB MP4 free atoms, scans them and uses the supplied native ffprobe inside the production sandbox. It compares cold and warm metadata batches. A second comparison performs the same 1,000 selection changes over 10,000 in-memory candidates using full traversal and indexed counters. Both methods must produce identical final totals.

The generated fixtures are removed afterward. No user media is used. Native ffprobe, DLLs, Windows AppContainer support and the selected .NET SDK are required.

## Local observation, 2026-10-20

Windows x64, .NET 10.0.13. One descriptive run produced:

| Work | Result |
| --- | --- |
| Enumerating four padded synthetic files | 13.75 ms |
| Cold metadata, four files | 220.13 ms wall time; 33,557,988 bytes staged |
| Cold sandbox/tool setup | 77.32 ms |
| Cold staging | 27.89 ms summed worker time, including copy-slot waiting |
| Cold parsing | 199.94 ms summed worker time |
| Warm metadata, four validated cache hits | 1.57 ms wall time; zero staged bytes and zero sandbox setup |
| Full-traversal selection totals, 10,000 files and 1,000 changes | 547.32 ms |
| Indexed selection totals, identical changes and result | 0.38 ms |

These figures exclude rendering from the selection comparison and exclude one-time index construction. They are not a representative large-recording throughput benchmark. Small valid video streams with large free atoms exercise staging bytes and cache behavior; they do not reproduce high-bitrate decoding, fragmented containers, network storage, cold operating-system caches or slow removable media. Timings vary by machine and load. Tests assert correct behavior and equal totals rather than a fixed speed ratio.

## Runtime tradeoffs

- Uncached metadata still makes a private complete copy of each input. This keeps parser access separate from original media ACLs. Source reads and temporary writes remain proportional to candidate bytes.
- Copies serialize to coordinate free-space use. Up to two parser processes can overlap. Other applications can consume free space, so write errors still fail safely.
- Each staging operation has a 30-minute deadline, including waiting for the copy slot. Parsing has a separate 15-second deadline. A blocked synchronous filesystem call cannot necessarily be interrupted immediately.
- The metadata cache holds at most 2,048 entries with incremental eviction. A working set larger than capacity can still have many misses.
- Review renders 25 groups per page and 25 file rows per expanded page. Selection updates read indexed totals and update rendered controls. Export and bulk selection still process all relevant records.
- Recovery history archival is explicit. Normal discovery skips the archive; opting into archive inspection includes its traversal cost. No audit records are automatically deleted.
