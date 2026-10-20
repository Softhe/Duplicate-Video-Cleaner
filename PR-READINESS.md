# 1.1.2 implementation and PR readiness

Review date: 2026-10-20

## Decision

The completed candidate is suitable for one consolidated PR. Independent cross-review found no remaining blocking defect in the implemented changes. Public release requires the freshly built setup to pass disposable-Windows-VM installation, upgrade, installed-video, settings and uninstall checks in CI. CI results belong to their exact proposed commit and setup checksum.

The baseline is the 1.1.1 release commit, `2b92083`. Existing uncommitted 1.1.2 work was preserved and extended on `improve/review-recovery-performance` for a single PR. No installed application or user media was replaced. Machine-local historical acceptance records are retained locally and excluded from the source commit.

## Implemented review findings

| Finding | Resolution |
| --- | --- |
| Metadata mixed old scan fields with a newer file | Validate the original scan snapshot before cache lookup and staging; capture scan fields from one regular-file read handle |
| A failed recovery child hid healthy siblings | Catch errors per child and continue traversal |
| Diagnostic logging blocked the UI | Background secured append stream with bounded flush batches and cancellation |
| Export blocked the UI | Background streaming export from a captured context, cancellation and deferred window closing |
| Recovery discovery trapped the modal dialog | Cancel discovery on close and unwind before closing |
| Bulk selection exposed stale accessible text | Refresh accessible state from the shared selection setter |
| Acceptance did not exercise bundled sandboxed video | External-app test uses synthetic video, bundled ffprobe, expected metadata, review confidence and real UI export; release guard requires fresh matching evidence |
| Negative monitor coordinates were rejected | Restore only when the title bar is reachable, including negative coordinates |
| Staging cleanup faults discarded completed results | Retry per-file cleanup; dispose the owner off the UI thread and retain successful metadata with explicit cleanup warnings |

Other changes include incremental bounded cache eviction, serialized media copies with a free-space floor, virtual recovery rows, preserved refresh warnings, handled recovery failure journal records, complete keyboard-accessible confirmation paths, zero-survivor group warnings, and reusable sidebar fonts. Upgrade qualification now runs the previous application to save non-default preferences and verifies distinct installed versions. Pull requests invoke the qualification workflow without publishing; qualification evidence is retained separately. global.json selects the SDK and patch roll-forward policy.

## Verification

The final local verification results are recorded below and in generated test evidence. BuildRelease produced a fresh setup EXE and self-contained payload with the hash-verified pinned FFmpeg archive. The external-app workflow ran that payload without installing it on this workstation.

- Core/integration runner: 44/44 passed, including live AppContainer restrictions and snapshot/cache/recovery/archival/crash-cleanup regressions.
- UI tests with the fresh candidate path supplied: 29/29 passed in about 35 seconds. This explicitly ran the installed-video acceptance test instead of its ordinary source-only skip.
- Targeted export lifecycle tests exercise MainForm while a worker is blocked, require a UI timer heartbeat, cancel the active export, and verify deferred close and absence of partial reports.
- Self-contained win-x64 publication succeeded.
- The full local BuildRelease pipeline passed, including tests, pinned archive verification and setup compilation.
- Six README screenshots were regenerated from the final published payload and synthetic video; confirmation and expanded review were visually inspected.
- Build script parsing, setup assertions, pinned-dependency cache verification and corrupted-cache rejection passed.
- Acceptance-guard negative tests reject missing sandboxed-video and previous-version-settings evidence, as well as the earlier invalid records.
- All five embedded workflow PowerShell blocks parsed successfully.
- Managed dependency vulnerability query reported no known vulnerable packages from nuget.org, including transitive packages. This does not assess native FFmpeg.
- Independent review covered recovery/release changes and metadata/UI changes. It found and resolved the final owner-cleanup failure path before this decision.
- New targeted tests verify background preference coalescing, deferred close while settings writes are blocked, responsive cancellation during actual scan diagnostic persistence, and canonical indexed selection accounting.
- The native synthetic benchmark staged 33,557,988 bytes on a cold batch and none on its fully cached repeat. Warm setup, staging and parsing were zero; wall time was 1.57 ms in the descriptive run. See docs/PERFORMANCE.md for workload and limitations.

Generated local evidence is under `validation/final-video.json`, `validation/performance.json` and `DuplicateVideoCleaner.UiTests/TestResults/final-ui.trx`. Generated evidence and historical machine-local validation files remain outside the source commit. CI retains its own qualification artifacts separately from public setup delivery.

## Larger improvements completed

The additional follow-ups from the review are implemented:

- Metadata reports phase, file, byte and elapsed progress. Activity records enumeration, tool setup, summed staging/parser durations, cache hits and staged bytes. A repeatable native synthetic benchmark separates these costs without touching private recordings.
- Fully cached batches validate snapshots in the background and skip sandbox/tool setup entirely. Staging has its own 30-minute per-file deadline, including waiting for the serialized copy slot; parser timeout remains separate at 15 seconds.
- Startup cleanup requires an exact marked GUID root/profile pair, current-user SID, expired age, a dead original PID/start-time pair and an exclusive marker lease. Normal retention is 24 hours. Unowned, malformed, live, recent, contested and reparse-containing roots are preserved. Tests terminate a real subprocess and verify cleanup plus protected cases.
- Recovery history has an explicit confirmed archive policy for verified completed records older than 30 days. It moves metadata intact, never deletes media or metadata, preserves incomplete/active/unexpected entries, and checks for concurrent payload additions. Normal listing skips the archive; opt-in inspection makes unexpected archived payloads recoverable.
- ReviewSession owns captured scan context, reviewed records, indexed selection totals, dry-run snapshots and removal reconciliation. Per-group counters let visible cards refresh without traversing a large group. A 10,000-file/1,000-change synthetic comparison produced identical totals with 547.32 ms traversal versus 0.38 ms indexed updates, excluding rendering and index construction.
- Preference snapshots coalesce on a serialized background queue. Closing awaits the latest write without blocking the UI and reports failure. Tests cover contention and recovery after a failed write.
- Actual ScanAsync delayed-diagnostic persistence has a deterministic UI heartbeat and cancellation regression. Export and settings have comparable lifecycle checks.

## Boundaries

Real-library throughput on network/removable storage, physical power-loss durability and native parser fuzzing remain outside this local run. Synthetic benchmarks do not establish those properties. Individual synchronous filesystem calls may delay cancellation. Unmarked legacy temporary folders are intentionally left alone. The installer is unsigned; VM qualification is a CI requirement. The old setup and its old acceptance hashes do not qualify the updated source. Content hashing and changing the persisted permanent-removal preference remain separate product features rather than hidden changes to the review model.

## Suggested PR description

Title: Harden video review, recovery and metadata isolation for 1.1.2

The 1.1.2 candidate adds object-bound Recovery and sandboxed metadata processing. This change also fixes stale scan/probe identity handling, recovery discovery error isolation, and UI blocking during diagnostics and export. Users can cancel export and recovery discovery, review every selected path before removal, and receive accurate metadata-cleanup warnings without losing completed results.

Review and recovery rendering are bounded for large result sets. Tests cover substitution, active writers, audit failures, isolation restrictions, cancellation, accessibility, window placement and export lifecycle. Release qualification requires previous-version settings preservation and the exact installed app's bundled sandboxed video scan and export, in addition to payload hashes and uninstall. Public artifacts remain the setup EXE and checksum.

Validation should cite the final counts above. Required hosted installer/upgrade qualification remains pending until CI runs on the proposed commit.
