# Changelog

## 1.2.0 - Unreleased

### Redesigned interface

- Replaced the whole interface with a Windows 11 style design: a navigation pane, and Scan, Review and Activity & Recovery pages. Scanning, protection, removal and Recovery rules are unchanged.
- Dark theme by default, a light theme, and a **System** option that follows the Windows app mode. Settings files from earlier versions open in the dark theme.
- Scan: a drop area with a path box that remembers the last five folders, the grouping rules in one card, and a progress bar with the current file while metadata is read.
- Review: a group list beside the selected group's files. Each file shows its role (KEEP, REMOVE or PROTECTED), its timestamp and source, and its resolution, duration and size, with ✓ for a match and ≠ for a difference from the file it is compared with. Lists are virtualised instead of paged.
- Confirmation is an in-window dialog. Cancel is the focused default; Enter and Esc never confirm. It lists every selected file, names the Recovery folder and warns when every copy of a recording is selected.
- Recovery moved from a separate window into the Activity & Recovery page, next to the session activity. Permanent deletion asks first, and restores and deletions appear in the activity log.
- New monochrome app icon: viewfinder corners around a play mark with a faded duplicate. The title-bar icon follows the app theme and the taskbar icon follows the Windows light or dark mode, both live; the EXE, Start menu and installer use a white version on a dark tile that reads on any background.

### Video thumbnails

- Review shows one frame per file. ffmpeg runs in the same AppContainer sandbox as ffprobe, reads only the validated read-only handle, and returns a fixed-size raw frame, so the app parses no media or image format itself. Changed files, failed decodes and timeouts keep a placeholder.
- Thumbnails load only for files on screen, pause during scans and removals, and are cached in `%LOCALAPPDATA%\DuplicateVideoCleaner\Thumbnails` (up to 96 MB). Uninstall removes the cache. Settings can turn thumbnails off.
- Both packages now include `ffmpeg.exe` from the same pinned LGPL build (about 0.5 MB, as it shares ffprobe's libraries).

### Internal

- Moved scanning, review, removal, Recovery and settings into `DuplicateVideoCleaner.Core` with new workflow tests, and rebuilt the app on WPF without third-party UI libraries. The self-contained app is about 24 MB larger than 1.1.3 because it includes the WPF runtime.
- UI tests now cover the view models on a WPF dispatcher and drive the packaged app through UI Automation. Release qualification upgrades from the published 1.1.3 setup.

## 1.1.3 - 2026-11-23

### Fixes for regressions in 1.1.2

- Made protection of clips without a recording date in the name an opt-in scan setting, off by default, so camera, phone and recorder files are reviewed and removable again as in 1.1.1.
- Recognised more recording dates in file names: `2026-10-15_14-30-00`, `2026-10-15 14.30.00`, NVIDIA ShadowPlay `2026.10.15 - 14.30.00`, and phone/camera `VID_20261015_143000`, `20261015_143000` and `PXL_20261015_143000123`.
- With protection on, a protected clip no longer blocks originals it does not match. Originals in the same time window keep ordinary timestamp suggestions among themselves, and originals joined from other dates by a protected size match are never compared with each other. Without ffprobe, these originals still get timestamp review.
- Stopped copying every video to `%TEMP%` for metadata. The sandboxed ffprobe now reads the validated original through a read-only handle, which removes the copy time, the temporary disk space requirement and the 30-minute copy deadline. Raised the metadata cache to 65,536 entries and parser concurrency to up to four.
- Fixed Recovery restore and permanent deletion on FAT32 and exFAT drives, where file IDs change when a file moves between folders and timestamps shift by an hour after a daylight saving change. Recovery records the file's identity after the move and still rejects a replaced payload with the same size and timestamps.
- Allowed scanning and removal when the chosen folder or a folder above it is a junction, directory symbolic link or mounted volume. Both the link and the folders it resolves to stay pinned during each operation. Links inside the scanned folder are still skipped, and cloud-sync folders with data reparse points are scanned.
- Scanned and probed files that another program has open for writing instead of skipping them; removal still rejects active writers.
- Reported video paths that differ only by letter case instead of failing the whole scan. Dates in file names must use ASCII digits; other digits fall back to file times instead of failing the scan.
- Warned when every original of a time window is selected, even if an unrelated protected clip remains in the group.
- Listed, removed and logged selected files in review order.
- Replaced the misleading "renamed clip" wording for files that were never renamed.

### Other fixes

- Directory pins now actually prevent deletion, rename and link retargeting. They were opened with attribute-only access, which Windows excludes from sharing checks.
- Recovery can restore or permanently delete several selected entries at once, with **Select all**, a size column, the total size and one confirmation. Closing the dialog during restore or deletion now closes it after the current file instead of being ignored.
- Re-pinned FFmpeg to n8.1.3-39 because BtbN removed the pinned 2026-10-15 build, which broke fresh release builds, and added optional verified mirrors to `build/FFmpeg.json`.
- Corrected the FFmpeg licence statement to LGPL version 3 or later (the bundled build uses `--enable-version3`) and noted that both packages include ffprobe.

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
