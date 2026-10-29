# Changelog

## 1.1.2 - 2026-10-29

### Recording dates, protected clips, and release packages

- Protect clips without recording dates in their filenames, retain every protected clip, and suggest original-name copies only when duration, resolution, and exact byte size match.
- Discover potential copies of protected clips across different timestamps and validate an unchanged protected survivor through removal of a matching original.
- Prefer the earlier creation/modification time when a filename has no recording date. Name actual protected matches in Review and exports, summarize the weakest suggested removal's confidence, and clear protected-only groups after cleanup.
- Publish both a setup EXE and portable ZIP with checksums. Qualify each exact package in a hosted Windows VM, including the extracted portable app's sandboxed video-review and export flow.

### Review fixes - 2026-10-20

- Preserved timestamp-based review and validated cached metadata when batch sandbox initialization fails, without an unrestricted parser fallback.
- Required independent evidence that both installed versions rewrite their settings fixture during upgrade qualification.
- Kept detail pagination controls alive across page changes and preserved keyboard focus, including transfers at page boundaries.
- Added file/byte/elapsed metadata progress, per-phase measurements and a separate 30-minute staging deadline. Fully cached batches validate snapshots without initializing a sandbox.
- Added ownership-checked startup cleanup of abandoned staging after 24 hours, protecting live processes, leased markers, unowned directories and reparse points.
- Added confirmed archival of verified completed Recovery history older than 30 days, with opt-in archived-payload inspection and preservation of active/incomplete/unexpected entries. Archival deletes no media or metadata.
- Extracted completed-review selection, export and reconciliation into an indexed session model with incremental file/group totals. Background preference saves coalesce and flush asynchronously on close.
- Added repeatable synthetic performance measurements and responsiveness regressions for delayed diagnostics and settings writes.
- Bound scan fields to one read handle and metadata probes to the original scan snapshot, including cache hits.
- Replaced whole-cache invalidation with bounded incremental eviction and serialized media copies so concurrent probes do not independently budget the same temporary disk space.
- Preserved successful metadata when staging cleanup is delayed or fails, and surfaced cleanup warnings without blocking the UI during owner cleanup.
- Moved scan diagnostic logging and streaming report export off the UI thread. Export cancellation retains completed reports and removes incomplete temporary files.
- Isolated recovery discovery errors per child, added cancellation when closing the dialog, virtualized its rows, preserved refresh warnings, and recorded handled mutation failures.
- Corrected bulk-selection accessibility, restored windows on monitors with negative coordinates, and reused sidebar fonts.
- Listed every selected path in confirmation and warned when a group has no unselected survivor.
- Added a synthetic video fixture and required the exact installed application to return bundled sandboxed video metadata and complete a UI export during release qualification.
- Required previous-version settings round trips during upgrade acceptance, retained CI qualification evidence, enabled pull-request validation, and selected the .NET SDK through global.json.

### Candidate foundation

- Migrated the application, tests, and screenshot tooling to .NET 10 LTS.
- Replaced path-based Recycle Bin removal with explicit app Recovery. Durable original-location records support validated restoration without overwriting existing files and explicit permanent purge.
- Excluded Recovery directories from scans, isolated corrupt entries during listing, and hardened removal, restoration, purge, and scan audit logs against redirected paths.
- Isolated metadata parsing in a zero-capability Windows AppContainer with a one-process job, CPU and memory limits, private media staging, clean environment, and fail-closed startup.
- Paged review groups and expanded files, retained selections across all pages, and bounded Activity controls for large libraries.
- Added exact-installer upgrade, settings, installed-UI, uninstall, and VM qualification checks. Public release requires evidence matching the setup SHA256.
- Pinned GitHub Actions to reviewed commit IDs and verified checksums for release delivery.

- Bound permanent deletion to a validated Windows file handle and rejected active writers.
- Restricted metadata probes to the file protocol, bounded output and subprocess lifetime, and included file identity in cache keys.
- Neutralized spreadsheet formula prefixes in CSV escaping.
- Kept review actions and metadata within the window when scan context is long.
- Preserved total hours when displaying recordings longer than one day.
- Routed GitHub release builds through pinned dependency verification, validated tag versions, and qualified release assets before publication.

## 1.1.1 — 2026-10-15

- Serialized settings writes so concurrent saves retain a readable preferences file.
- Recomputed group keepers and suggestions after removal.

- Stopped removal when scanned file metadata or Windows file identity changes, snapshots are missing, or files are reparse points.
- Recorded audit intent and outcome per file, stopped batches on audit failure, and preserved accurate completed-removal results.
- Bound cleanup logs and dry-run reports to the original scan folder and rules.
- Reported skipped scan entries, discarded canceled scans, and stopped canceled metadata probes.
- Prevented repeated dry-run exports from overwriting earlier reports and surfaced invalid export destinations.

- Added a per-user Windows EXE installer with Start menu integration and uninstall support, plus a portable ZIP from the same verified payload.
- Included screenshot documentation in both packages and published download checksums.
- Pinned the FFmpeg dependency to a dated release and expected SHA256; corrupted downloads and cache entries fail before extraction.

- Reworked every workspace and the removal confirmation dialog with a dark navy palette, mint accents, brighter text, and larger Segoe UI typography.
- Added compact review summaries, selection markers, consistent action styling, and dark dropdowns.
- Corrected theme application to preserve semantic status colors and selected file backgrounds.
- Added UI checks for summary density, multiple groups, action bounds, and expansion state, with optional screenshot evidence.
- Fixed the main window and the confirmation dialog showing the default icon on the title bar and taskbar instead of the application icon.

## 1.1.0 — 2026-08-02

- Added persistent scan and window preferences.
- Added FFprobe duration/resolution metadata and advisory confidence.
- Added accessible metadata columns and exact size comparison.
- Added CSV and text dry-run exports.
- Added automated service, deletion-integration, and UIA3 workflow tests.
- Added a reproducible icon and documented portable release package.
- Replaced the wizard/table interface with a dark sidebar workspace, expandable duplicate-group cards, a persistent safety dock, and current-session activity view.

## 1.0.0

- Initial guided dark-mode C# replacement for `DuplicateVideoClear.ps1`.
